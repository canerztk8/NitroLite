using System.Diagnostics;
using System.IO;
using NitroLite.Core;
using NitroLite.Core.Hardware.Wmi;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// PR 6: the optional Task Scheduler entry that lets a machine with no Acer services run the direct-WMI backend
/// without answering a UAC prompt at the start of every session.
/// <para>
/// Nothing here registers a real task, reads the real registry or raises a real prompt: the process runner, the
/// elevation check, the executable path and the user SID are all injected.
/// </para>
/// </summary>
public class HalStep6Tests
{
    /// <summary>Records every command and answers with a scripted result.</summary>
    private sealed class FakeProcessRunner : IProcessRunner
    {
        public List<(string FileName, string Arguments, bool Elevate)> Calls { get; } = new();

        /// <summary>Answers keyed on a substring of the arguments, so a test can script Create and Query separately.</summary>
        public Dictionary<string, ProcessRunResult> Answers { get; } = new();

        public ProcessRunResult Default { get; set; } = new(0, "", "", Started: true);

        public Task<ProcessRunResult> RunAsync(string fileName, string arguments, bool elevate, CancellationToken cancellationToken = default)
        {
            Calls.Add((fileName, arguments, elevate));

            foreach (var (needle, answer) in Answers)
            {
                if (arguments.Contains(needle, StringComparison.OrdinalIgnoreCase)) return Task.FromResult(answer);
            }

            return Task.FromResult(Default);
        }

        public bool Saw(string needle) =>
            Calls.Any(c => c.Arguments.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }

    private const string ExePath = @"C:\Program Files\NitroLite\NitroLite.exe";
    private const string UserSid = "S-1-5-21-1111111111-2222222222-3333333333-1001";

    private static ElevatedStartupService Build(
        FakeProcessRunner runner,
        bool elevated = false,
        string? tempPath = null) =>
        new(runner,
            isElevated: () => elevated,
            executablePath: () => ExePath,
            userId: () => UserSid,
            tempFilePath: () => tempPath ?? Path.Combine(Path.GetTempPath(), $"nitrolite-task-{Guid.NewGuid():N}.xml"));

    // ------------------------------------------------------------------ 1. the task definition

    [Fact]
    public void TaskXml_TurnsOffTheThreeWindowsDefaultsThatWouldBreakATrayApp()
    {
        string xml = ElevatedStartupService.BuildTaskXml(ExePath, "--autostart", UserSid);

        // Without PT0S Windows terminates the task after 72 hours of runtime.
        Assert.Contains("<ExecutionTimeLimit>PT0S</ExecutionTimeLimit>", xml);

        // A laptop that unplugs must not lose its fan control.
        Assert.Contains("<DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>", xml);
        Assert.Contains("<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>", xml);

        // HighestAvailable is what makes the helper inherit an elevated token, so no prompt appears.
        Assert.Contains("<RunLevel>HighestAvailable</RunLevel>", xml);
        Assert.Contains("<LogonType>InteractiveToken</LogonType>", xml);
        Assert.Contains($"<UserId>{UserSid}</UserId>", xml);
        Assert.Contains("<LogonTrigger>", xml);
    }

    [Fact]
    public void TaskXml_QuotesTheExecutableAndCarriesTheArguments()
    {
        string xml = ElevatedStartupService.BuildTaskXml(ExePath, "--autostart --minimized", UserSid);

        Assert.Contains($"<Command>\"{ExePath}\"</Command>", xml);
        Assert.Contains("<Arguments>--autostart --minimized</Arguments>", xml);
        Assert.Contains($"<URI>\\{ElevatedStartupService.TaskName}</URI>", xml);
    }

    [Fact]
    public void TaskXml_DoesNotDoubleQuoteAnAlreadyQuotedPath()
    {
        string xml = ElevatedStartupService.BuildTaskXml($"\"{ExePath}\"", "--autostart", UserSid);

        Assert.Contains($"<Command>\"{ExePath}\"</Command>", xml);
        Assert.DoesNotContain("\"\"", xml);
    }

    [Fact]
    public void TaskXml_EscapesXmlMetacharacters()
    {
        string xml = ElevatedStartupService.BuildTaskXml(@"C:\A & B\NitroLite.exe", "--autostart", UserSid);

        Assert.Contains("A &amp; B", xml);
        Assert.DoesNotContain("A & B", xml);
    }

    [Fact]
    public void TaskXml_RejectsAnEmptyPathOrSid()
    {
        Assert.Throws<ArgumentException>(() => ElevatedStartupService.BuildTaskXml("", "--autostart", UserSid));
        Assert.Throws<ArgumentException>(() => ElevatedStartupService.BuildTaskXml(ExePath, "--autostart", ""));
    }

    [Fact]
    public void TaskArguments_MatchTheStandardRunEntry()
    {
        // The two registrations must start the application the same way, or switching between them changes behaviour.
        Assert.Equal("--autostart", ElevatedStartupService.BuildTaskArguments(startMinimized: false));
        Assert.Equal("--autostart --minimized", ElevatedStartupService.BuildTaskArguments(startMinimized: true));

        Assert.Equal(
            DebloatService.BuildAutoStartCommand(ExePath, startMinimized: true),
            $"\"{ExePath}\" {ElevatedStartupService.BuildTaskArguments(startMinimized: true)}");
    }

    // ------------------------------------------------------------------ 2. registering and removing

    [Fact]
    public async Task IsEnabled_AsksWindowsNotASettingsFile()
    {
        var runner = new FakeProcessRunner();
        var service = Build(runner);

        runner.Default = new ProcessRunResult(0, "", "", Started: true);
        Assert.True(await service.IsEnabledAsync());

        runner.Default = new ProcessRunResult(1, "", "ERROR: The system cannot find the file specified.", Started: true);
        Assert.False(await service.IsEnabledAsync());

        Assert.True(runner.Saw("/Query"));
        Assert.True(runner.Saw(ElevatedStartupService.TaskName));
    }

    [Fact]
    public async Task Enable_CreatesTheTaskFromATemporaryXmlAndDeletesTheFile()
    {
        string tempPath = Path.Combine(Path.GetTempPath(), $"nitrolite-task-test-{Guid.NewGuid():N}.xml");
        var runner = new FakeProcessRunner();
        var service = Build(runner, tempPath: tempPath);

        var result = await service.EnableAsync(startMinimized: true);

        Assert.True(result.Applied);
        Assert.True(runner.Saw("/Create"));
        Assert.True(runner.Saw("/XML"));
        Assert.True(runner.Saw("/F"));

        // The XML is a temporary file, not a record: leaving it behind would litter the temp folder.
        Assert.False(File.Exists(tempPath));
    }

    [Fact]
    public async Task Enable_WithoutElevation_AsksForThePrompt()
    {
        var runner = new FakeProcessRunner();
        var service = Build(runner, elevated: false);

        await service.EnableAsync(startMinimized: false);

        var create = Assert.Single(runner.Calls, c => c.Arguments.Contains("/Create"));
        Assert.True(create.Elevate);
    }

    [Fact]
    public async Task Enable_WhenAlreadyElevated_DoesNotAskForThePrompt()
    {
        var runner = new FakeProcessRunner();
        var service = Build(runner, elevated: true);

        await service.EnableAsync(startMinimized: false);

        var create = Assert.Single(runner.Calls, c => c.Arguments.Contains("/Create"));
        Assert.False(create.Elevate);
    }

    [Fact]
    public async Task Disable_DeletesTheTask()
    {
        var runner = new FakeProcessRunner();
        var service = Build(runner);

        var result = await service.DisableAsync();

        Assert.True(result.Applied);
        Assert.True(runner.Saw("/Delete"));
        Assert.True(runner.Saw("/F"));
    }

    [Fact]
    public async Task Disable_WhenTheTaskIsAlreadyGone_IsASuccess()
    {
        // The caller asked for "not registered", and that is the state it is in.
        var runner = new FakeProcessRunner();
        runner.Answers["/Delete"] = new ProcessRunResult(1, "", "ERROR: The system cannot find the file specified.", Started: true);
        runner.Answers["/Query"] = new ProcessRunResult(1, "", "ERROR: The system cannot find the file specified.", Started: true);

        var result = await Build(runner).DisableAsync();

        Assert.True(result.Applied);
    }

    // ------------------------------------------------------------------ 3. UAC cancellation

    [Fact]
    public async Task Enable_WhenThePromptIsDeclined_ChangesNothingAndDoesNotThrow()
    {
        var runner = new FakeProcessRunner();
        runner.Default = new ProcessRunResult(ElevatedStartupService.ErrorCancelled, "", "cancelled", Started: false, Cancelled: true);

        bool standardRemoved = false;
        var service = Build(runner);
        service.RemoveStandardAutoStart = () => { standardRemoved = true; return true; };

        var result = await service.EnableAsync(startMinimized: false);

        Assert.Equal(ElevatedStartupOutcome.Cancelled, result.Outcome);
        Assert.False(result.Applied);

        // The Run entry must survive: the user said no, so nothing may have changed.
        Assert.False(standardRemoved);
    }

    [Fact]
    public async Task Disable_WhenThePromptIsDeclined_ReportsCancelled()
    {
        var runner = new FakeProcessRunner();
        runner.Default = new ProcessRunResult(ElevatedStartupService.ErrorCancelled, "", "cancelled", Started: false, Cancelled: true);

        var result = await Build(runner).DisableAsync();

        Assert.Equal(ElevatedStartupOutcome.Cancelled, result.Outcome);
    }

    [Fact]
    public async Task Enable_WhenSchtasksFails_ReportsFailedWithTheReason()
    {
        var runner = new FakeProcessRunner();
        runner.Answers["/Create"] = new ProcessRunResult(1, "", "ERROR: Access is denied.", Started: true);

        var result = await Build(runner).EnableAsync(startMinimized: false);

        Assert.Equal(ElevatedStartupOutcome.Failed, result.Outcome);
        Assert.Contains("Access is denied", result.Detail);
    }

    [Fact]
    public async Task Enable_WithoutAnExecutablePath_FailsInsteadOfRegisteringNothing()
    {
        var runner = new FakeProcessRunner();
        var service = new ElevatedStartupService(runner, isElevated: () => false, executablePath: () => null, userId: () => UserSid);

        var result = await service.EnableAsync(startMinimized: false);

        Assert.Equal(ElevatedStartupOutcome.Failed, result.Outcome);
        Assert.Empty(runner.Calls);
    }

    // ------------------------------------------------------------------ 4. mutual exclusion with HKCU Run

    [Fact]
    public async Task Enable_RemovesTheStandardRunEntry()
    {
        var runner = new FakeProcessRunner();
        int removals = 0;
        var service = Build(runner);
        service.RemoveStandardAutoStart = () => { removals++; return true; };

        var result = await service.EnableAsync(startMinimized: false);

        Assert.True(result.Applied);
        Assert.Equal(1, removals);
    }

    [Fact]
    public async Task EnableStandard_RemovesTheTaskFirst()
    {
        var runner = new FakeProcessRunner();
        runner.Answers["/Query"] = new ProcessRunResult(0, "", "", Started: true);

        var order = new List<string>();
        var service = Build(runner);
        service.SetStandardAutoStart = _ => { order.Add("run"); return true; };
        service.RemoveStandardAutoStart = () => { order.Add("remove-run"); return true; };

        var result = await service.EnableStandardAsync(startMinimized: false);

        Assert.True(result.Applied);
        Assert.True(runner.Saw("/Delete"));
        Assert.Equal(new[] { "run" }, order);
    }

    [Fact]
    public async Task EnableStandard_WhenTheTaskCannotBeRemoved_LeavesTheRunEntryAlone()
    {
        // Two registrations would start NitroLite twice at logon, so the standard entry is not written at all.
        var runner = new FakeProcessRunner();
        runner.Answers["/Query"] = new ProcessRunResult(0, "", "", Started: true);
        runner.Answers["/Delete"] = new ProcessRunResult(ElevatedStartupService.ErrorCancelled, "", "cancelled", Started: false, Cancelled: true);

        bool runWritten = false;
        var service = Build(runner);
        service.SetStandardAutoStart = _ => { runWritten = true; return true; };

        var result = await service.EnableStandardAsync(startMinimized: false);

        Assert.Equal(ElevatedStartupOutcome.Cancelled, result.Outcome);
        Assert.False(runWritten);
    }

    [Fact]
    public async Task EnableStandard_WhenNoTaskExists_WritesTheRunEntryDirectly()
    {
        var runner = new FakeProcessRunner();
        runner.Answers["/Query"] = new ProcessRunResult(1, "", "not found", Started: true);

        bool? minimized = null;
        var service = Build(runner);
        service.SetStandardAutoStart = value => { minimized = value; return true; };

        var result = await service.EnableStandardAsync(startMinimized: true);

        Assert.True(result.Applied);
        Assert.True(minimized);
        Assert.False(runner.Saw("/Delete"));
    }

    [Fact]
    public async Task IsAnyAutoStartEnabled_ReportsEitherRegistration()
    {
        var runner = new FakeProcessRunner();
        runner.Answers["/Query"] = new ProcessRunResult(1, "", "not found", Started: true);

        var service = Build(runner);
        service.IsStandardAutoStartEnabled = () => true;
        Assert.True(await service.IsAnyAutoStartEnabledAsync());

        service.IsStandardAutoStartEnabled = () => false;
        Assert.False(await service.IsAnyAutoStartEnabledAsync());

        runner.Answers["/Query"] = new ProcessRunResult(0, "", "", Started: true);
        Assert.True(await service.IsAnyAutoStartEnabledAsync());
    }

    // ------------------------------------------------------------------ 5. no prompt when already elevated

    [Fact]
    public async Task WorkerLauncher_WhenAlreadyElevated_StartsWithoutRunas()
    {
        ProcessStartInfo? captured = null;
        using var launcher = new WmiWorkerLauncher(
            executablePath: ExePath,
            start: psi => { captured = psi; return null; },
            isElevated: () => true);

        Assert.True(launcher.IsElevated);

        // Returns null because the fake start produced no process; the point is the start info it was given.
        Assert.Null(await launcher.StartAsync());

        Assert.NotNull(captured);
        Assert.False(captured!.UseShellExecute);
        Assert.True(string.IsNullOrEmpty(captured.Verb));
        Assert.True(captured.CreateNoWindow);
    }

    [Fact]
    public async Task WorkerLauncher_WhenNotElevated_AsksForRunas()
    {
        ProcessStartInfo? captured = null;
        using var launcher = new WmiWorkerLauncher(
            executablePath: ExePath,
            start: psi => { captured = psi; return null; },
            isElevated: () => false);

        Assert.False(launcher.IsElevated);
        Assert.Null(await launcher.StartAsync());

        Assert.NotNull(captured);
        Assert.True(captured!.UseShellExecute);
        Assert.Equal("runas", captured.Verb);
    }

    [Fact]
    public void WorkerLauncher_ArgumentsStillMatchTheWorkerParser()
    {
        // The elevation change must not have touched the command line the worker parses.
        string arguments = WmiWorkerLauncher.BuildArguments("nitrolite-wmi-test", 4242, new string('a', 64));

        Assert.True(StartupMode.Parse(arguments.Split(' ')).IsValidWorkerRequest);
    }

    // ------------------------------------------------------------------ 6. auto-starting WMI at launch

    [Fact]
    public async Task AutoStartDirectWmi_WhenNotElevated_DoesNothing()
    {
        var agent = new RecordingController();
        var hybrid = new HybridHardwareController(
            agent, wmiFactory: () => new RecordingController { BackendName = "WMI" },
            serviceHealthy: () => Task.FromResult(false))
        {
            IsElevated = () => false
        };

        Assert.False(await hybrid.AutoStartDirectWmiAsync());
        Assert.Equal("AASSvc", hybrid.ActiveBackendName);
    }

    [Fact]
    public async Task AutoStartDirectWmi_WhenTheServiceIsHealthy_DoesNothing()
    {
        var agent = new RecordingController();
        var hybrid = new HybridHardwareController(
            agent, wmiFactory: () => new RecordingController { BackendName = "WMI" },
            serviceHealthy: () => Task.FromResult(true))
        {
            IsElevated = () => true
        };

        Assert.False(await hybrid.AutoStartDirectWmiAsync());
        Assert.Equal("AASSvc", hybrid.ActiveBackendName);
    }

    [Fact]
    public async Task AutoStartDirectWmi_WhenPinnedToTheServicePath_DoesNothing()
    {
        var agent = new RecordingController();
        var hybrid = new HybridHardwareController(
            agent, wmiFactory: () => new RecordingController { BackendName = "WMI" },
            useDirectWmi: () => false, serviceHealthy: () => Task.FromResult(false))
        {
            IsElevated = () => true
        };

        Assert.False(await hybrid.AutoStartDirectWmiAsync());
    }

    // ------------------------------------------------------------------ 7. the backlight timeout falls back

    /// <summary>
    /// The 30-second timeout, when the WMI selector is registered but does not answer.
    /// </summary>
    /// <remarks>
    /// The bug these tests exist for: on the reference AN16-41 <c>APGeAction</c> is present, so the direct backend is
    /// used, but <c>GetFunction(0x80001)</c> returns a status this build does not recognise. The hybrid treated "the
    /// worker was reachable" as "the selector answered" and returned the empty result, discarding the Acer service's
    /// own reading — which is the one that works. The page then showed its default "off" over a firmware timeout that
    /// was on, and the keyboard went dark after 30 seconds with the switch saying it would not.
    /// </remarks>
    [Fact]
    public async Task BacklightTimeout_WhenTheSelectorDoesNotAnswer_UsesTheService()
    {
        var agent = new RecordingController { BackendName = "AASSvc", BacklightTimeout = true };
        var wmi = new RecordingController { BackendName = "WMI", BacklightTimeout = null };
        var hybrid = new HybridHardwareController(
            agent, wmiFactory: () => wmi, serviceHealthy: () => Task.FromResult(false))
        {
            IsElevated = () => true
        };

        Assert.True(await hybrid.GetBacklightTimeoutAsync());

        // Both were asked: the direct backend first, then the service when it had nothing to say.
        Assert.Equal(1, wmi.TimeoutReads);
        Assert.Equal(1, agent.TimeoutReads);
    }

    [Fact]
    public async Task BacklightTimeout_WhenTheSelectorAnswers_IsPreferred()
    {
        var agent = new RecordingController { BackendName = "AASSvc", BacklightTimeout = true };
        var wmi = new RecordingController { BackendName = "WMI", BacklightTimeout = false };
        var hybrid = new HybridHardwareController(
            agent, wmiFactory: () => wmi, serviceHealthy: () => Task.FromResult(false))
        {
            IsElevated = () => true
        };

        // The direct backend is the one that talks to the firmware, so its answer wins when it has one.
        Assert.False(await hybrid.GetBacklightTimeoutAsync());
        Assert.Equal(1, wmi.TimeoutReads);
        Assert.Equal(0, agent.TimeoutReads);
    }

    [Fact]
    public async Task BacklightTimeoutWrite_WhenTheSelectorRefuses_UsesTheService()
    {
        var agent = new RecordingController { BackendName = "AASSvc", AcceptsTimeoutWrites = true };
        var wmi = new RecordingController { BackendName = "WMI", AcceptsTimeoutWrites = false };
        var hybrid = new HybridHardwareController(
            agent, wmiFactory: () => wmi, serviceHealthy: () => Task.FromResult(false))
        {
            IsElevated = () => true
        };

        Assert.True(await hybrid.SetBacklightTimeoutAsync(false));
        Assert.Equal(1, wmi.TimeoutWrites);
        Assert.Equal(1, agent.TimeoutWrites);
    }

    [Fact]
    public async Task BacklightTimeoutWrite_WhenTheSelectorAccepts_DoesNotAskTheService()
    {
        var agent = new RecordingController { BackendName = "AASSvc" };
        var wmi = new RecordingController { BackendName = "WMI", AcceptsTimeoutWrites = true };
        var hybrid = new HybridHardwareController(
            agent, wmiFactory: () => wmi, serviceHealthy: () => Task.FromResult(false))
        {
            IsElevated = () => true
        };

        Assert.True(await hybrid.SetBacklightTimeoutAsync(false));
        Assert.Equal(1, wmi.TimeoutWrites);
        Assert.Equal(0, agent.TimeoutWrites);
    }

    /// <summary>A controller that answers without touching the hardware.</summary>
    private sealed class RecordingController : NitroLite.Core.Hardware.IHardwareController
    {
        public string BackendName { get; init; } = "AASSvc";
        public bool IsAvailable => true;

        public Task<bool> EnsureConnectedAsync() => Task.FromResult(true);
        public Task<NitroLite.Core.Hardware.HardwareCapabilities> GetCapabilitiesAsync() =>
            Task.FromResult(new NitroLite.Core.Hardware.HardwareCapabilities { Backend = BackendName });
        public Task<MonitorData?> GetMonitorDataAsync() => Task.FromResult<MonitorData?>(null);
        public Task<OperatingMode?> GetOperatingModeAsync() => Task.FromResult<OperatingMode?>(OperatingMode.Default);
        public Task<bool> SetOperatingModeAsync(OperatingMode mode) => Task.FromResult(true);
        public Task<GpuOperatingMode?> GetGpuModeAsync() => Task.FromResult<GpuOperatingMode?>(GpuOperatingMode.Optimus);
        public Task<bool> SetGpuModeAsync(GpuOperatingMode mode) => Task.FromResult(true);
        public Task<int?> GetGpuModeCapabilityAsync() => Task.FromResult<int?>(null);
        public Task<FanMode?> GetFanModeAsync() => Task.FromResult<FanMode?>(FanMode.Auto);
        public Task<bool> SetFanModeAsync(FanMode mode, int cpuPercent = -1, int gpuPercent = -1) => Task.FromResult(true);
        public Task<LightingConfig?> GetLightingConfigAsync() => Task.FromResult<LightingConfig?>(null);
        public Task<bool> SetLightingAsync(string effect, int brightness, int speed, int direction, string primaryColor, string[]? zoneColors) => Task.FromResult(true);
        public Task<bool> TurnOffLightingAsync() => Task.FromResult(true);
        /// <summary>Null is a backend that cannot answer, which is what the WMI selector does on the AN16-41.</summary>
        public bool? BacklightTimeout { get; init; } = false;

        /// <summary>False is a backend whose timeout write is refused.</summary>
        public bool AcceptsTimeoutWrites { get; init; } = true;

        public int TimeoutWrites { get; private set; }

        public int TimeoutReads { get; private set; }

        public Task<bool?> GetBacklightTimeoutAsync()
        {
            TimeoutReads++;
            return Task.FromResult(BacklightTimeout);
        }

        public Task<bool> SetBacklightTimeoutAsync(bool enable30s)
        {
            TimeoutWrites++;
            return Task.FromResult(AcceptsTimeoutWrites);
        }
        public Task<BatteryChargeMode?> GetBatteryChargeModeAsync() => Task.FromResult<BatteryChargeMode?>(BatteryChargeMode.Optimized80);
        public Task<bool> SetBatteryChargeModeAsync(BatteryChargeMode mode) => Task.FromResult(true);

        public Task<bool?> GetCoolBoostAsync() => Task.FromResult<bool?>(null);

        public Task<bool> SetCoolBoostAsync(bool on) => Task.FromResult(false);

        public Task<bool?> GetLcdOverdriveAsync() => Task.FromResult<bool?>(null);

        public Task<bool> SetLcdOverdriveAsync(bool on) => Task.FromResult(false);

        public Task<bool> SetWinKeyLockAsync(bool enabled) => Task.FromResult(false);
        public void Dispose() { }
    }
}
