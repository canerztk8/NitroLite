using System.IO;
using NitroLite.Core;
using NitroLite.Core.Hardware;
using NitroLite.Desktop.ViewModels;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// The second half of the unified-UI migration: the two bugs the live-hardware test caught, the battery refresh-rate
/// switch that replaced the last WPF-only tool, and the retirement of the WPF front end from the Task Scheduler.
/// </summary>
/// <remarks>
/// <para>
/// The banner and the extras badge share one root cause, so they share one fixture: a backend whose transport is not
/// open yet is not a machine without hardware, and a service socket that cannot answer a capability is not a machine
/// without that capability. <see cref="IPlatformSnapshot.NegativeAnswersAreConclusive"/> is the flag that separates
/// the two, and every test below pins one side of it.
/// </para>
/// <para>
/// Nothing here touches real hardware, a real scheduled task or a real UAC prompt.
/// </para>
/// </remarks>
public class RetireLegacyWpfTests
{
    // =============================================================================================================
    // Fixtures.
    // =============================================================================================================

    /// <summary>A backend that answers from fields the test sets and records nothing else.</summary>
    private sealed class FakeController : IHardwareController
    {
        public string BackendName => "Test";
        public bool IsAvailable { get; set; }
        public HardwareCapabilities Capabilities { get; set; } = HardwareCapabilities.Unknown;

        public Task<bool> EnsureConnectedAsync() => Task.FromResult(IsAvailable);
        public Task<HardwareCapabilities> GetCapabilitiesAsync() => Task.FromResult(Capabilities);
        public Task<MonitorData?> GetMonitorDataAsync() => Task.FromResult<MonitorData?>(null);
        public Task<OperatingMode?> GetOperatingModeAsync() => Task.FromResult<OperatingMode?>(null);
        public Task<bool> SetOperatingModeAsync(OperatingMode mode) => Task.FromResult(false);
        public Task<GpuOperatingMode?> GetGpuModeAsync() => Task.FromResult<GpuOperatingMode?>(null);
        public Task<bool> SetGpuModeAsync(GpuOperatingMode mode) => Task.FromResult(false);
        public Task<int?> GetGpuModeCapabilityAsync() => Task.FromResult<int?>(null);
        public Task<FanMode?> GetFanModeAsync() => Task.FromResult<FanMode?>(null);
        public Task<bool> SetFanModeAsync(FanMode mode, int cpuPercent = -1, int gpuPercent = -1) => Task.FromResult(false);
        public Task<LightingConfig?> GetLightingConfigAsync() => Task.FromResult<LightingConfig?>(null);
        public Task<bool> SetLightingAsync(string effect, int brightness, int speed, int direction, string primaryColor, string[]? zoneColors) => Task.FromResult(false);
        public Task<bool> TurnOffLightingAsync() => Task.FromResult(false);
        public Task<bool?> GetBacklightTimeoutAsync() => Task.FromResult<bool?>(null);
        public Task<bool> SetBacklightTimeoutAsync(bool enable30s) => Task.FromResult(false);
        public Task<BatteryChargeMode?> GetBatteryChargeModeAsync() => Task.FromResult<BatteryChargeMode?>(null);
        public Task<bool> SetBatteryChargeModeAsync(BatteryChargeMode mode) => Task.FromResult(false);
        public Task<bool?> GetCoolBoostAsync() => Task.FromResult<bool?>(null);
        public Task<bool> SetCoolBoostAsync(bool on) => Task.FromResult(false);
        public Task<bool?> GetLcdOverdriveAsync() => Task.FromResult<bool?>(null);
        public Task<bool> SetLcdOverdriveAsync(bool on) => Task.FromResult(false);
        public Task<bool> SetWinKeyLockAsync(bool enabled) => Task.FromResult(false);
        public void Dispose() { }
    }

    /// <summary>A probe snapshot with only the confidence flag under test.</summary>
    private sealed class FakeSnapshot : IPlatformSnapshot
    {
        public DeviceIdentity Identity { get; init; } = new() { Manufacturer = "Acer", Model = "Nitro AN16-41" };
        public string Tier { get; init; } = "wmi";
        public bool NeedsUdevRule { get; init; }
        public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
        public bool NegativeAnswersAreConclusive { get; init; }
        public string BuildHardwareReport() => "";
    }

    /// <summary>A panel whose rate the test can read back, and whose driver can be told to refuse.</summary>
    private sealed class FakeDisplay : IDisplayRefreshRateService
    {
        public bool IsSupported { get; init; } = true;
        public int CurrentHz { get; set; } = 165;
        public bool Refuse { get; set; }
        public List<int> Writes { get; } = new();

        public int GetCurrentRefreshRate() => CurrentHz;
        public int GetMaxRefreshRate() => 165;

        public bool SetRefreshRate(int hz)
        {
            Writes.Add(hz);
            if (Refuse) return false;
            CurrentHz = hz;
            return true;
        }
    }

    /// <summary>An <see cref="IProcessRunner"/> that answers from a queue and records every command.</summary>
    private sealed class FakeRunner : IProcessRunner
    {
        private readonly Func<string, string, ProcessRunResult> _answer;

        public FakeRunner(Func<string, string, ProcessRunResult> answer) => _answer = answer;

        public List<(string FileName, string Arguments, bool Elevate)> Calls { get; } = new();

        public Task<ProcessRunResult> RunAsync(string fileName, string arguments, bool elevate, CancellationToken cancellationToken = default)
        {
            Calls.Add((fileName, arguments, elevate));
            return Task.FromResult(_answer(fileName, arguments));
        }
    }

    private static ProcessRunResult Ok(string stdout = "") => new(0, stdout, "", true);
    private static ProcessRunResult Failed(int code = 1) => new(code, "", "failed", true);

    private static string TempSettingsPath() =>
        Path.Combine(Path.GetTempPath(), $"nitrolite_retire_{Guid.NewGuid():N}.json");

    private static MainViewModel CreateViewModel(
        IHardwareController? hardware,
        IPlatformSnapshot? platform,
        AppSettings? settings = null,
        RefreshRateAutomation? refreshRate = null,
        Func<(bool OnAc, int? BatteryPercent)>? powerState = null)
    {
        return new MainViewModel(
            settings ?? new AppSettings(),
            hardware,
            platform,
            TempSettingsPath(),
            refreshRate: refreshRate,
            powerState: powerState);
    }

    // =============================================================================================================
    // 1. The "no hardware backend" banner.
    // =============================================================================================================

    [Fact]
    public void ShowNoBackendBanner_IsFalseWhenTheWindowsTransportIsNotOpenYet()
    {
        // The live-hardware bug: the WMI worker is only started when the user elevates and the Acer service may not be
        // up yet, so IsAvailable is false for the first seconds of every launch. That is not a machine without
        // hardware, and the banner must not say it is.
        var hardware = new FakeController { IsAvailable = false };
        var platform = new FakeSnapshot { NegativeAnswersAreConclusive = false };

        var vm = CreateViewModel(hardware, platform);

        Assert.False(vm.HasBackend);
        Assert.True(vm.HasNoBackend);
        Assert.False(vm.ShowNoBackendBanner);
    }

    [Fact]
    public void ShowNoBackendBanner_IsTrueWhenNoControllerWasWiredAtAll()
    {
        var vm = CreateViewModel(null, new FakeSnapshot { NegativeAnswersAreConclusive = false });

        Assert.True(vm.ShowNoBackendBanner);
    }

    [Fact]
    public void ShowNoBackendBanner_IsTrueWhenLinuxFoundNoAcerNode()
    {
        // Linux enumerates its sysfs nodes once at startup, so a false answer there is a real answer.
        var hardware = new FakeController { IsAvailable = false };
        var platform = new FakeSnapshot { NegativeAnswersAreConclusive = true };

        var vm = CreateViewModel(hardware, platform);

        Assert.True(vm.ShowNoBackendBanner);
    }

    [Fact]
    public void ShowNoBackendBanner_IsFalseWhenTheBackendIsAvailable()
    {
        var hardware = new FakeController { IsAvailable = true };
        var platform = new FakeSnapshot { NegativeAnswersAreConclusive = true };

        var vm = CreateViewModel(hardware, platform);

        Assert.True(vm.HasBackend);
        Assert.False(vm.ShowNoBackendBanner);
    }

    [Fact]
    public void ShowNoBackendBanner_IsTrueWhenNoPlatformSnapshotWasWired()
    {
        // No probe means no evidence either way, and the banner is the honest default.
        var vm = CreateViewModel(new FakeController { IsAvailable = false }, null);

        Assert.True(vm.ShowNoBackendBanner);
    }

    // =============================================================================================================
    // 2. The "not on this device" extras badge.
    // =============================================================================================================

    [Fact]
    public async Task Extras_OnWindowsWithNoAnswer_ShowNeitherTheBadgeNorAnEmptyCard()
    {
        // The second live-hardware bug: the service socket has no CoolBoost or overdrive command, so both read as
        // false on a Nitro 16 that has them. Claiming "not on this device" from that is a lie about the hardware.
        var hardware = new FakeController
        {
            IsAvailable = true,
            Capabilities = new HardwareCapabilities { Backend = "Test", ServiceAvailable = true }
        };
        var platform = new FakeSnapshot { NegativeAnswersAreConclusive = false };

        var vm = CreateViewModel(hardware, platform);
        await vm.InitializeAsync();

        Assert.False(vm.HasAnyExtra);
        Assert.False(vm.NoExtrasAvailable);
        Assert.False(vm.ShowExtrasCard);
    }

    [Fact]
    public async Task Extras_OnLinuxWithNoNode_ShowTheBadge()
    {
        var hardware = new FakeController
        {
            IsAvailable = true,
            Capabilities = new HardwareCapabilities { Backend = "Test", ServiceAvailable = true }
        };
        var platform = new FakeSnapshot { NegativeAnswersAreConclusive = true };

        var vm = CreateViewModel(hardware, platform);
        await vm.InitializeAsync();

        Assert.False(vm.HasAnyExtra);
        Assert.True(vm.NoExtrasAvailable);
        Assert.True(vm.ShowExtrasCard);
    }

    [Fact]
    public async Task Extras_WhenOneSwitchIsSupported_ShowTheCardAndHideTheBadge()
    {
        // The badge is about the whole card, not about one switch: a machine with a backlight timeout has extras.
        var hardware = new FakeController
        {
            IsAvailable = true,
            Capabilities = new HardwareCapabilities
            {
                Backend = "Test",
                ServiceAvailable = true,
                BacklightTimeout = true
            }
        };
        var platform = new FakeSnapshot { NegativeAnswersAreConclusive = false };

        var vm = CreateViewModel(hardware, platform);
        await vm.InitializeAsync();

        Assert.True(vm.HasAnyExtra);
        Assert.False(vm.NoExtrasAvailable);
        Assert.True(vm.ShowExtrasCard);
    }

    [Fact]
    public async Task Extras_WhenCoolBoostIsSupported_ShowTheCardAndHideTheBadge()
    {
        var hardware = new FakeController
        {
            IsAvailable = true,
            Capabilities = new HardwareCapabilities { Backend = "Test", ServiceAvailable = true, CoolBoost = true }
        };
        var platform = new FakeSnapshot { NegativeAnswersAreConclusive = true };

        var vm = CreateViewModel(hardware, platform);
        await vm.InitializeAsync();

        Assert.True(vm.HasAnyExtra);
        Assert.False(vm.NoExtrasAvailable);
        Assert.True(vm.ShowExtrasCard);
    }

    // =============================================================================================================
    // 3. The battery refresh-rate switch.
    // =============================================================================================================

    private static (RefreshRateAutomation Automation, FakeDisplay Display, AppSettings Settings) Automation(
        int currentHz = 165,
        bool supported = true,
        bool enabled = true)
    {
        var display = new FakeDisplay { CurrentHz = currentHz, IsSupported = supported };
        var settings = new AppSettings { AutoRefreshRateSwitching = enabled };
        return (new RefreshRateAutomation(display, settings), display, settings);
    }

    [Fact]
    public void RefreshRate_LowersThePanelOnBatteryAndRemembersWhatItReplaced()
    {
        var (automation, display, _) = Automation();

        var result = automation.Apply(onAc: false);

        Assert.Equal(RefreshRateAction.LoweredToBattery, result.Action);
        Assert.Equal(165, result.From);
        Assert.Equal(60, result.To);
        Assert.Equal(60, display.CurrentHz);
        Assert.Equal(165, automation.SavedRate);
    }

    [Fact]
    public void RefreshRate_RestoresTheRememberedRateOnMains()
    {
        var (automation, display, _) = Automation();
        automation.Apply(onAc: false);

        var result = automation.Apply(onAc: true);

        Assert.Equal(RefreshRateAction.RestoredToMaximum, result.Action);
        Assert.Equal(165, display.CurrentHz);
        Assert.Null(automation.SavedRate);
    }

    [Fact]
    public void RefreshRate_RemembersTheUsersOwnRateRatherThanTheMaximum()
    {
        // A user who deliberately runs at 120 Hz to save power must not be promoted to 165 Hz on every replug.
        var (automation, display, _) = Automation(currentHz: 120);

        automation.Apply(onAc: false);
        Assert.Equal(60, display.CurrentHz);

        automation.Apply(onAc: true);
        Assert.Equal(120, display.CurrentHz);
    }

    [Fact]
    public void RefreshRate_LeavesAPanelAlreadyAtSixtyAlone()
    {
        // Nothing was replaced, so nothing has to come back: restoring here would count as a change that never was.
        var (automation, display, _) = Automation(currentHz: 60);

        var result = automation.Apply(onAc: false);

        Assert.Equal(RefreshRateAction.None, result.Action);
        Assert.Null(automation.SavedRate);
        Assert.Empty(display.Writes);
    }

    [Fact]
    public void RefreshRate_DoesNothingWhenThePowerStateHasNotChanged()
    {
        var (automation, display, _) = Automation();
        automation.Apply(onAc: true);

        var result = automation.Apply(onAc: true);

        Assert.Equal(RefreshRateAction.None, result.Action);
        Assert.Empty(display.Writes);
    }

    [Fact]
    public void RefreshRate_DoesNotOverwriteTheSavedRateOnASecondBatteryTick()
    {
        // The telemetry tick runs once a second. A second drop would save 60 over the user's 165 and lose it.
        var (automation, display, _) = Automation();
        automation.Apply(onAc: false);

        var result = automation.Apply(onAc: false, force: true);

        Assert.Equal(RefreshRateAction.None, result.Action);
        Assert.Equal(165, automation.SavedRate);
        Assert.Single(display.Writes);
    }

    [Fact]
    public void RefreshRate_ForceReEvaluatesTheSamePowerState()
    {
        // Turning the switch on while already on battery has to take effect now, not at the next replug.
        var (automation, display, _) = Automation(enabled: false);
        automation.Apply(onAc: false);
        Assert.Equal(165, display.CurrentHz);

        automation.Apply(onAc: false, force: true);
        Assert.Equal(165, display.CurrentHz);

        // The setting is read on every evaluation, so flipping it and forcing is what the switch does.
        var settings = new AppSettings { AutoRefreshRateSwitching = true };
        var forced = new RefreshRateAutomation(display, settings);
        forced.Apply(onAc: false, force: true);

        Assert.Equal(60, display.CurrentHz);
    }

    [Fact]
    public void RefreshRate_RestoresWhenTheSettingIsTurnedOff()
    {
        // Switched off while the panel was already lowered: put it back rather than leaving the user at 60 Hz with
        // nothing left to raise it until the adapter returns.
        var (automation, display, settings) = Automation();
        automation.Apply(onAc: false);
        Assert.Equal(60, display.CurrentHz);

        settings.AutoRefreshRateSwitching = false;
        var result = automation.Apply(onAc: false, force: true);

        Assert.Equal(RefreshRateAction.RestoredToMaximum, result.Action);
        Assert.Equal(165, display.CurrentHz);
        Assert.Null(automation.SavedRate);
    }

    [Fact]
    public void RefreshRate_ReportsUnsupportedOnAPlatformThatCannotChangeTheRate()
    {
        var (automation, display, _) = Automation(supported: false);

        var result = automation.Apply(onAc: false);

        Assert.Equal(RefreshRateAction.Unsupported, result.Action);
        Assert.False(automation.IsSupported);
        Assert.Empty(display.Writes);
    }

    [Fact]
    public void RefreshRate_KeepsTheSavedRateWhenTheDriverRefusesTheDrop()
    {
        var (automation, display, _) = Automation();
        display.Refuse = true;

        var result = automation.Apply(onAc: false);

        Assert.Equal(RefreshRateAction.Failed, result.Action);
        Assert.Equal(165, result.From);
        Assert.Null(automation.SavedRate);
    }

    [Fact]
    public void RefreshRate_KeepsTheSavedRateWhenTheDriverRefusesTheRestore()
    {
        // The driver may refuse while the lid is closed; the next evaluation has to try again rather than forget what
        // the user had.
        var (automation, display, _) = Automation();
        automation.Apply(onAc: false);

        display.Refuse = true;
        var result = automation.Apply(onAc: true);

        Assert.Equal(RefreshRateAction.Failed, result.Action);
        Assert.Equal(165, automation.SavedRate);

        display.Refuse = false;
        Assert.Equal(RefreshRateAction.RestoredToMaximum, automation.Apply(onAc: true).Action);
        Assert.Equal(165, display.CurrentHz);
    }

    [Fact]
    public void RefreshRate_RestorePutsTheRateBackWithoutConsultingThePowerState()
    {
        // What a clean exit calls: the fans go back to the firmware and the panel goes back to the user's rate.
        var (automation, display, _) = Automation();
        automation.Apply(onAc: false);

        var result = automation.Restore();

        Assert.Equal(RefreshRateAction.RestoredToMaximum, result.Action);
        Assert.Equal(165, display.CurrentHz);
        Assert.Equal(RefreshRateAction.None, automation.Restore().Action);
    }

    [Fact]
    public void RefreshRate_ViewModelHidesTheCardOnAPlatformThatCannotChangeTheRate()
    {
        var (automation, _, _) = Automation(supported: false);
        var vm = CreateViewModel(
            new FakeController { IsAvailable = true },
            new FakeSnapshot { NegativeAnswersAreConclusive = true },
            refreshRate: automation,
            powerState: () => (false, 50));

        Assert.False(vm.AutoRefreshRateAvailable);
    }

    [Fact]
    public void RefreshRate_ViewModelShowsTheCardAndLowersThePanelOnBattery()
    {
        var (automation, display, settings) = Automation(enabled: false);
        var onAc = false;
        var vm = CreateViewModel(
            new FakeController { IsAvailable = true },
            new FakeSnapshot { NegativeAnswersAreConclusive = true },
            settings,
            refreshRate: automation,
            powerState: () => (onAc, 50));

        Assert.True(vm.AutoRefreshRateAvailable);
        Assert.False(vm.AutoRefreshRateOnBattery);

        // The switch forces an evaluation, which is what makes it work while already on battery.
        vm.AutoRefreshRateOnBattery = true;
        Assert.Equal(60, display.CurrentHz);
        Assert.True(vm.HasRefreshRateStatus);
        Assert.True(settings.AutoRefreshRateSwitching);
    }

    // =============================================================================================================
    // 4. The Task Scheduler target.
    // =============================================================================================================

    [Theory]
    [InlineData(@"C:\Program Files\NitroLite\NitroLite.exe", true)]
    [InlineData(@"C:\Program Files\NitroLite\nitrolite.EXE", true)]
    [InlineData("\"C:\\Program Files\\NitroLite\\NitroLite.exe\"", true)]
    [InlineData(@"C:\Program Files\NitroLite\NitroLite.Desktop.exe", true)]
    [InlineData("\"C:\\Program Files\\NitroLite\\NitroLite.Desktop.exe\"", true)]
    [InlineData(@"C:\Program Files\NitroLite\NitroLite.Core.dll", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsDesktopExecutable_AcceptsThePackagedAndDevelopmentNames(string? path, bool expected)
    {
        Assert.Equal(expected, ElevatedStartupService.IsDesktopExecutable(path));
    }

    [Fact]
    public void ExpectedExecutableName_MatchesTheFileTheUpdaterSwaps()
    {
        // The in-place updater replaces NitroLite.exe and nothing else, so the task's target and the package's
        // allow-list have to name the same file. If they drift, an update leaves the scheduled task pointing at a
        // binary that no longer exists.
        Assert.Equal("NitroLite.exe", ElevatedStartupService.ExpectedExecutableName);
        Assert.Contains("NitroLite.exe", UpdateService.PackageFiles, StringComparer.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("<Task><Actions><Exec><Command>\"C:\\Apps\\NitroLite.exe\"</Command></Exec></Actions></Task>",
        "\"C:\\Apps\\NitroLite.exe\"")]
    [InlineData("<Task><Actions><Exec><Command>C:\\Apps\\NitroLite.Desktop.exe</Command></Exec></Actions></Task>",
        "C:\\Apps\\NitroLite.Desktop.exe")]
    [InlineData("<Task><Actions><Exec><Arguments>--autostart</Arguments></Exec></Actions></Task>", "")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void ReadCommandFromTaskXml_ReadsTheCommandElement(string? xml, string expected)
    {
        Assert.Equal(expected, ElevatedStartupService.ReadCommandFromTaskXml(xml));
    }

    [Fact]
    public async Task Enable_RegistersThePackagedExecutableNameAndDeletesNothingFirst()
    {
        // The package ships the Avalonia front end as NitroLite.exe, so the task must name that file. There is no
        // legacy name left to purge: the retired WPF build used the same file name.
        string registered = "";

        var runner = new FakeRunner((_, arguments) =>
        {
            const string marker = "/XML \"";
            int start = arguments.IndexOf(marker, StringComparison.Ordinal);
            if (start >= 0)
            {
                int from = start + marker.Length;
                int to = arguments.IndexOf('"', from);
                registered = File.ReadAllText(arguments[from..to]);
            }

            return Ok();
        });

        var service = new ElevatedStartupService(
            runner,
            isElevated: () => true,
            executablePath: () => @"C:\Apps\NitroLite\NitroLite.exe",
            userId: () => "S-1-5-21-1",
            tempFilePath: () => Path.Combine(Path.GetTempPath(), $"nitrolite_task_{Guid.NewGuid():N}.xml"),
            registerTask: (_, _, _, _) => "the COM path is not exercised here");

        var result = await service.EnableAsync(startMinimized: false);

        Assert.True(result.Applied);
        Assert.Contains("NitroLite.exe", registered, StringComparison.Ordinal);
        Assert.DoesNotContain(runner.Calls, c => c.Arguments.Contains("/Delete", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildTaskXml_NamesThePackagedExecutableAndKeepsTrayFriendlyDefaults()
    {
        var xml = ElevatedStartupService.BuildTaskXml(
            @"C:\Apps\NitroLite\NitroLite.exe",
            ElevatedStartupService.BuildTaskArguments(startMinimized: true),
            "S-1-5-21-1");

        Assert.Contains("NitroLite.exe", xml, StringComparison.Ordinal);
        Assert.Contains("--autostart --minimized", xml, StringComparison.Ordinal);
        Assert.Contains("<RunLevel>HighestAvailable</RunLevel>", xml, StringComparison.Ordinal);
        Assert.Contains("<ExecutionTimeLimit>PT0S</ExecutionTimeLimit>", xml, StringComparison.Ordinal);
        Assert.Contains("<DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>", xml, StringComparison.Ordinal);
        Assert.Contains("<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>", xml, StringComparison.Ordinal);
    }
}
