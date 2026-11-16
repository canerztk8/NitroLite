using System.ComponentModel;
using System.Diagnostics;
using NitroLite.Core;
using NitroLite.Core.Hardware;
using NitroLite.Core.Hardware.Wmi;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// Pins the fourth step of the hardware-layer migration: the read-back rule that decides whether a direct-WMI write
/// actually happened, the controller that talks to the elevated worker, the hybrid that chooses between the Acer
/// service and WMI, and the handle-based parent watchdog.
/// <para>
/// Nothing here touches the hardware. The launcher is driven through an injected process starter, the pipe through an
/// injected connect delegate, and the read-back through an injected delay, so a test never raises a UAC prompt and
/// never waits 360 ms for a pause that exists only to give a real EC time to settle.
/// </para>
/// </summary>
public class WmiControllerTests
{
    // ---------------------------------------------------------------- read-back: success paths

    [Fact]
    public async Task ReadBack_FirstAttemptMatches_IsAppliedAndResetsTheCounter()
    {
        var settings = new AppSettings();
        settings.CapabilitySessionFailures[ReadBackPolicy.FanSpeed] = 2;
        var policy = new ReadBackPolicy(settings, NoDelay);

        var result = await policy.ApplyAsync(
            ReadBackPolicy.FanSpeed,
            write: () => Task.FromResult(true),
            readBack: () => Task.FromResult<int?>(60),
            matches: value => value == 60);

        Assert.True(result.Applied);
        Assert.Equal(1, result.Attempts);
        Assert.False(result.DisabledForSession);
        Assert.Equal(0, policy.ConsecutiveSessionFailures(ReadBackPolicy.FanSpeed));
    }

    [Fact]
    public async Task ReadBack_SecondAttemptMatches_IsApplied()
    {
        var settings = new AppSettings();
        var policy = new ReadBackPolicy(settings, NoDelay);
        var reads = 0;

        var result = await policy.ApplyAsync(
            ReadBackPolicy.OperatingMode,
            write: () => Task.FromResult(true),
            readBack: () => Task.FromResult<OperatingMode?>(++reads == 1 ? OperatingMode.Quiet : OperatingMode.Turbo),
            matches: value => value == OperatingMode.Turbo);

        Assert.True(result.Applied);
        Assert.Equal(2, result.Attempts);
        Assert.False(policy.IsDisabled(ReadBackPolicy.OperatingMode));
    }

    [Fact]
    public async Task ReadBack_ThirdAttemptMatches_IsApplied()
    {
        var settings = new AppSettings();
        var policy = new ReadBackPolicy(settings, NoDelay);
        var reads = 0;

        var result = await policy.ApplyAsync(
            ReadBackPolicy.MuxMode,
            write: () => Task.FromResult(true),
            readBack: () => Task.FromResult<GpuOperatingMode?>(++reads < 3 ? GpuOperatingMode.Optimus : GpuOperatingMode.DiscreteGpu),
            matches: value => value == GpuOperatingMode.DiscreteGpu);

        Assert.True(result.Applied);
        Assert.Equal(3, result.Attempts);
    }

    // ---------------------------------------------------------------- read-back: failure paths

    [Fact]
    public async Task ReadBack_ThreeMismatches_DisableTheCapabilityForTheSession()
    {
        var settings = new AppSettings();
        var policy = new ReadBackPolicy(settings, NoDelay);
        var writes = 0;

        var result = await policy.ApplyAsync(
            ReadBackPolicy.FanBehavior,
            write: () => { writes++; return Task.FromResult(true); },
            readBack: () => Task.FromResult<FanMode?>(FanMode.Auto),
            matches: value => value == FanMode.Max);

        Assert.False(result.Applied);
        Assert.Equal(ReadBackPolicy.MaxAttempts, result.Attempts);
        Assert.Equal(ReadBackPolicy.MaxAttempts, writes);
        Assert.True(result.DisabledForSession);
        Assert.False(result.PermanentlyDisabled);
        Assert.True(policy.IsDisabled(ReadBackPolicy.FanBehavior));
        Assert.Equal(1, policy.ConsecutiveSessionFailures(ReadBackPolicy.FanBehavior));
    }

    [Fact]
    public async Task ReadBack_DisabledCapability_IsSkippedWithoutWriting()
    {
        var settings = new AppSettings();
        var policy = new ReadBackPolicy(settings, NoDelay);
        var writes = 0;

        await policy.ApplyAsync(
            ReadBackPolicy.FanSpeed,
            write: () => { writes++; return Task.FromResult(true); },
            readBack: () => Task.FromResult<int?>(0),
            matches: value => value == 60);

        var second = await policy.ApplyAsync(
            ReadBackPolicy.FanSpeed,
            write: () => { writes++; return Task.FromResult(true); },
            readBack: () => Task.FromResult<int?>(0),
            matches: value => value == 60);

        Assert.True(second.Skipped);
        Assert.Equal(ReadBackPolicy.MaxAttempts, writes);
    }

    [Fact]
    public async Task ReadBack_TwoFailuresInOneSession_CountAsOneSession()
    {
        var settings = new AppSettings();
        var policy = new ReadBackPolicy(settings, NoDelay);

        // The first failure disables the capability, so the second call is skipped rather than counted again. The
        // point of the assertion is that a single bad afternoon cannot reach the permanent threshold.
        await policy.ApplyAsync(ReadBackPolicy.FanSpeed, () => Task.FromResult(true),
            () => Task.FromResult<int?>(0), value => value == 60);
        await policy.ApplyAsync(ReadBackPolicy.FanSpeed, () => Task.FromResult(true),
            () => Task.FromResult<int?>(0), value => value == 60);

        Assert.Equal(1, policy.ConsecutiveSessionFailures(ReadBackPolicy.FanSpeed));
        Assert.False(policy.IsPermanentlyDisabled(ReadBackPolicy.FanSpeed));
    }

    [Fact]
    public async Task ReadBack_ThreeConsecutiveSessions_DisableTheCapabilityPermanently()
    {
        var settings = new AppSettings();

        for (int session = 1; session <= ReadBackPolicy.SessionsBeforePermanentDisable; session++)
        {
            // A fresh policy per session is what a restart looks like: the session-scoped set is empty again, the
            // persisted counter is not.
            var policy = new ReadBackPolicy(settings, NoDelay);
            var result = await policy.ApplyAsync(
                ReadBackPolicy.OperatingMode,
                write: () => Task.FromResult(true),
                readBack: () => Task.FromResult<OperatingMode?>(OperatingMode.Quiet),
                matches: value => value == OperatingMode.Turbo);

            Assert.False(result.Applied);
            Assert.Equal(session, policy.ConsecutiveSessionFailures(ReadBackPolicy.OperatingMode));
            Assert.Equal(session >= ReadBackPolicy.SessionsBeforePermanentDisable, result.PermanentlyDisabled);
        }

        Assert.Contains(ReadBackPolicy.OperatingMode, settings.DisabledCapabilities);
    }

    [Fact]
    public async Task ReadBack_ASuccessBetweenSessions_ClearsThePermanentCountdown()
    {
        var settings = new AppSettings();

        for (int session = 0; session < 2; session++)
        {
            var failing = new ReadBackPolicy(settings, NoDelay);
            await failing.ApplyAsync(ReadBackPolicy.MuxMode, () => Task.FromResult(true),
                () => Task.FromResult<GpuOperatingMode?>(GpuOperatingMode.Optimus),
                value => value == GpuOperatingMode.DiscreteGpu);
        }

        Assert.Equal(2, new ReadBackPolicy(settings, NoDelay).ConsecutiveSessionFailures(ReadBackPolicy.MuxMode));

        var recovering = new ReadBackPolicy(settings, NoDelay);
        var ok = await recovering.ApplyAsync(ReadBackPolicy.MuxMode, () => Task.FromResult(true),
            () => Task.FromResult<GpuOperatingMode?>(GpuOperatingMode.DiscreteGpu),
            value => value == GpuOperatingMode.DiscreteGpu);

        Assert.True(ok.Applied);
        Assert.Equal(0, recovering.ConsecutiveSessionFailures(ReadBackPolicy.MuxMode));
        Assert.False(recovering.IsPermanentlyDisabled(ReadBackPolicy.MuxMode));
    }

    [Fact]
    public async Task ReadBack_Rescan_ClearsCountersAndPermanentDisables()
    {
        var settings = new AppSettings();
        settings.DisabledCapabilities.Add(ReadBackPolicy.FanSpeed);
        settings.CapabilitySessionFailures[ReadBackPolicy.FanSpeed] = 3;

        var policy = new ReadBackPolicy(settings, NoDelay);
        Assert.True(policy.IsPermanentlyDisabled(ReadBackPolicy.FanSpeed));

        policy.ResetAll();

        Assert.False(policy.IsPermanentlyDisabled(ReadBackPolicy.FanSpeed));
        Assert.Equal(0, policy.ConsecutiveSessionFailures(ReadBackPolicy.FanSpeed));
        Assert.Empty(settings.DisabledCapabilities);
        Assert.Empty(settings.CapabilitySessionFailures);
    }

    [Fact]
    public async Task ReadBack_AWriteThatThrows_IsTreatedAsAFailureNotACrash()
    {
        var settings = new AppSettings();
        var policy = new ReadBackPolicy(settings, NoDelay);

        var result = await policy.ApplyAsync(
            ReadBackPolicy.FanSpeed,
            write: () => throw new InvalidOperationException("the pipe is gone"),
            readBack: () => Task.FromResult<int?>(60),
            matches: value => value == 60);

        Assert.False(result.Applied);
        Assert.True(result.DisabledForSession);
        Assert.Contains("pipe is gone", result.Error);
    }

    // ---------------------------------------------------------------- launcher: UAC

    [Fact]
    public async Task Launcher_DeclinedPrompt_LocksTheSessionWithoutThrowing()
    {
        using var launcher = new WmiWorkerLauncher(
            executablePath: "NitroLite.exe",
            start: _ => throw new Win32Exception(WmiWorkerLauncher.ErrorCancelled));

        var first = await launcher.StartAsync();
        var second = await launcher.StartAsync();

        Assert.Null(first);
        Assert.Null(second);
        Assert.True(launcher.UserDeclined);
        Assert.Equal(1, launcher.Attempts);
        Assert.Contains("declined", launcher.LastError);
    }

    [Fact]
    public async Task Launcher_OtherStartFailure_DoesNotLockTheSession()
    {
        using var launcher = new WmiWorkerLauncher(
            executablePath: "NitroLite.exe",
            start: _ => throw new Win32Exception(5));

        var result = await launcher.StartAsync();

        Assert.Null(result);
        Assert.False(launcher.UserDeclined);
        Assert.Equal(1, launcher.Attempts);
    }

    [Fact]
    public void Launcher_Arguments_RoundTripThroughTheWorkerParser()
    {
        var nonce = WmiWorkerClient.GenerateNonce();
        var arguments = WmiWorkerLauncher.BuildArguments("nitrolite-wmi-abc", 4242, nonce);

        var mode = StartupMode.Parse(SplitArguments(arguments));

        Assert.True(mode.IsWmiWorker);
        Assert.True(mode.IsValidWorkerRequest);
        Assert.Equal("nitrolite-wmi-abc", mode.PipeName);
        Assert.Equal(4242, mode.ParentProcessId);
        Assert.Equal(nonce, mode.Nonce);
    }

    // ---------------------------------------------------------------- WmiHardwareController

    [Fact]
    public async Task WmiController_DeclinedElevation_StaysUsableAndDoesNotRetry()
    {
        using var launcher = new WmiWorkerLauncher(
            executablePath: "NitroLite.exe",
            start: _ => throw new Win32Exception(WmiWorkerLauncher.ErrorCancelled));
        using var controller = new WmiHardwareController(launcher, new ReadBackPolicy(new AppSettings(), NoDelay));

        Assert.False(await controller.EnsureConnectedAsync());
        Assert.False(await controller.EnsureConnectedAsync());

        Assert.True(controller.UserDeclinedElevation);
        Assert.Equal(1, launcher.Attempts);
        Assert.False(controller.IsAvailable);

        // A read on a backend that never connected reports "no answer" rather than throwing.
        Assert.Null(await controller.GetOperatingModeAsync());
        Assert.Null(await controller.GetMonitorDataAsync());
        Assert.False(await controller.SetFanModeAsync(FanMode.Max));
    }

    [Fact]
    public async Task WmiController_BatteryLimitKnownUnsupported_DoesNotAskTheWorker()
    {
        var snapshot = SnapshotWith(CapabilitySnapshot.WmiBatteryChargeLimit, HardwareProbeOutcome.Unsupported);
        using var launcher = new WmiWorkerLauncher(
            executablePath: "NitroLite.exe",
            start: _ => throw new Win32Exception(WmiWorkerLauncher.ErrorCancelled));
        using var controller = new WmiHardwareController(
            launcher, new ReadBackPolicy(new AppSettings(), NoDelay), () => snapshot);

        Assert.True(controller.BatteryLimitKnownUnsupported);
        Assert.False(await controller.SetBatteryChargeModeAsync(BatteryChargeMode.Optimized80));
        Assert.Null(await controller.GetBatteryChargeModeAsync());

        // The whole point: no elevation was requested for a capability the probe already ruled out.
        Assert.Equal(0, launcher.Attempts);
    }

    [Fact]
    public void WmiController_MuxRange_AcceptsOnlyOneBasedModeNumbers()
    {
        Assert.True(WmiHardwareController.IsMuxValueInRange(0x0102));
        Assert.True(WmiHardwareController.IsMuxValueInRange(0x0202));
        Assert.True(WmiHardwareController.IsMuxValueInRange(0x0302));

        // 0 is what a firmware that numbers the modes from zero would answer, and 4 is out of range entirely.
        Assert.False(WmiHardwareController.IsMuxValueInRange(0x0002));
        Assert.False(WmiHardwareController.IsMuxValueInRange(0x0402));
    }

    [Fact]
    public void WmiController_NormalisePercent_KeepsTheFirmwareRange()
    {
        Assert.Equal(30, WmiHardwareController.NormalisePercent(10));
        Assert.Equal(100, WmiHardwareController.NormalisePercent(140));
        Assert.Equal(60, WmiHardwareController.NormalisePercent(60));
        Assert.Equal(50, WmiHardwareController.NormalisePercent(-1));
    }

    [Fact]
    public void WmiController_DeriveRpm_UsesTheProfileCeiling()
    {
        Assert.Equal(2400, WmiHardwareController.DeriveRpm(50, 4800));
        Assert.Equal(0, WmiHardwareController.DeriveRpm(50, 0));
        Assert.Equal(4800, WmiHardwareController.DeriveRpm(100, 4800));
    }

    // ---------------------------------------------------------------- HybridHardwareController

    [Fact]
    public async Task Hybrid_HealthyService_UsesTheAgentAndNeverCreatesTheWmiBackend()
    {
        var agent = new RecordingController("AASSvc");
        var created = 0;
        using var hybrid = new HybridHardwareController(
            agent,
            wmiFactory: () => { created++; return new RecordingController("WMI"); },
            useDirectWmi: () => true,
            serviceHealthy: () => Task.FromResult(true));

        Assert.True(await hybrid.SetFanModeAsync(FanMode.Max));
        Assert.Equal(1, agent.SetFanModeCalls);
        Assert.Equal(0, created);
        Assert.Equal("AASSvc", hybrid.ActiveBackendName);
    }

    [Fact]
    public async Task Hybrid_UnhealthyService_FallsBackToWmi()
    {
        var agent = new RecordingController("AASSvc");
        var wmi = new RecordingController("WMI");
        using var hybrid = new HybridHardwareController(
            agent,
            wmiFactory: () => wmi,
            useDirectWmi: () => true,
            serviceHealthy: () => Task.FromResult(false));

        Assert.True(await hybrid.SetFanModeAsync(FanMode.Max));
        Assert.Equal(0, agent.SetFanModeCalls);
        Assert.Equal(1, wmi.SetFanModeCalls);
        Assert.Equal("WMI", hybrid.ActiveBackendName);
    }

    [Fact]
    public async Task Hybrid_RollbackSwitchOff_ForcesTheAgentEvenWhenTheServiceIsDown()
    {
        var agent = new RecordingController("AASSvc");
        var wmi = new RecordingController("WMI");
        using var hybrid = new HybridHardwareController(
            agent,
            wmiFactory: () => wmi,
            useDirectWmi: () => false,
            serviceHealthy: () => Task.FromResult(false));

        Assert.True(await hybrid.SetOperatingModeAsync(OperatingMode.Turbo));
        Assert.Equal(1, agent.SetOperatingModeCalls);
        Assert.Equal(0, wmi.SetOperatingModeCalls);
    }

    [Fact]
    public async Task Hybrid_KeyboardLighting_FollowsTheSameArbitrationAsTheRest()
    {
        var agent = new RecordingController("AASSvc");
        var wmi = new RecordingController("WMI");
        using var hybrid = new HybridHardwareController(
            agent,
            wmiFactory: () => wmi,
            useDirectWmi: () => true,
            serviceHealthy: () => Task.FromResult(false));

        await hybrid.SetLightingAsync("STATIC", 80, 50, 0, "#FF0000", null);
        await hybrid.TurnOffLightingAsync();
        await hybrid.SetBacklightTimeoutAsync(true);

        // The service is down, so colour writes go to the WMI backend, which can drive the zones directly.
        Assert.Equal(1, wmi.SetLightingCalls);
        Assert.Equal(1, wmi.TurnOffLightingCalls);
        Assert.Equal(0, agent.SetLightingCalls);

        // The 30-second backlight timeout has no WMI equivalent, so it stays on the agent.
        Assert.Equal(1, agent.SetBacklightTimeoutCalls);
    }

    [Fact]
    public async Task Hybrid_KeyboardLighting_UsesTheAgentWhenTheServiceIsHealthy()
    {
        var agent = new RecordingController("AASSvc");
        var wmi = new RecordingController("WMI");
        using var hybrid = new HybridHardwareController(
            agent,
            wmiFactory: () => wmi,
            useDirectWmi: () => true,
            serviceHealthy: () => Task.FromResult(true));

        await hybrid.SetLightingAsync("STATIC", 80, 50, 0, "#FF0000", null);

        Assert.Equal(1, agent.SetLightingCalls);
        Assert.Equal(0, wmi.SetLightingCalls);
    }

    [Fact]
    public async Task Hybrid_FourZoneColors_GoToWmiEvenWhenTheServiceIsHealthy()
    {
        var agent = new RecordingController("AASSvc");
        var wmi = new RecordingController("WMI");
        using var hybrid = new HybridHardwareController(
            agent,
            wmiFactory: () => wmi,
            useDirectWmi: () => true,
            serviceHealthy: () => Task.FromResult(true));

        var zones = new[] { "#FF0000", "#00FF00", "#0000FF", "#FFFFFF" };
        Assert.True(await hybrid.SetLightingAsync("STATIC", 5, 3, 1, "#FF0000", zones));

        // The service advertises DIRECT as its only four-colour mode and animates it, so the firmware gets the
        // colours. The service still carries brightness and the effect, and it is written first so the zone colours
        // are the last thing the keyboard hears.
        Assert.Equal(1, wmi.SetLightingCalls);
        Assert.Equal(zones, wmi.LastZoneColors);
        Assert.Equal(1, agent.SetLightingCalls);
        Assert.Null(agent.LastZoneColors);
        Assert.True(hybrid.LastZoneColorsApplied);
    }

    [Fact]
    public async Task Hybrid_OneZoneColor_StaysOnTheService()
    {
        var agent = new RecordingController("AASSvc");
        var wmi = new RecordingController("WMI");
        using var hybrid = new HybridHardwareController(
            agent,
            wmiFactory: () => wmi,
            useDirectWmi: () => true,
            serviceHealthy: () => Task.FromResult(true));

        var zones = new[] { "#FF0000", "#FF0000", "#FF0000", "#FF0000" };
        Assert.True(await hybrid.SetLightingAsync("STATIC", 5, 3, 1, "#FF0000", zones));

        Assert.Equal(0, wmi.SetLightingCalls);
        Assert.Equal(1, agent.SetLightingCalls);
        Assert.Null(hybrid.LastZoneColorsApplied);
    }

    [Fact]
    public async Task Hybrid_FourZoneColors_WithoutWmi_FallBackToTheServiceAndSaySo()
    {
        var agent = new RecordingController("AASSvc");
        using var hybrid = new HybridHardwareController(
            agent,
            wmiFactory: () => null,
            useDirectWmi: () => true,
            serviceHealthy: () => Task.FromResult(true));

        var zones = new[] { "#FF0000", "#00FF00", "#0000FF", "#FFFFFF" };
        Assert.True(await hybrid.SetLightingAsync("STATIC", 5, 3, 1, "#FF0000", zones));

        // The service answers success while lighting one colour, so the caller has to be told the zones were lost.
        Assert.Equal(1, agent.SetLightingCalls);
        Assert.Equal(zones, agent.LastZoneColors);
        Assert.False(hybrid.LastZoneColorsApplied);
    }

    [Fact]
    public async Task Hybrid_FourZoneColors_WhenTheUserPinnedTheService_StayOnTheService()
    {
        var agent = new RecordingController("AASSvc");
        var wmi = new RecordingController("WMI");
        using var hybrid = new HybridHardwareController(
            agent,
            wmiFactory: () => wmi,
            useDirectWmi: () => false,
            serviceHealthy: () => Task.FromResult(true));

        var zones = new[] { "#FF0000", "#00FF00", "#0000FF", "#FFFFFF" };
        Assert.True(await hybrid.SetLightingAsync("STATIC", 5, 3, 1, "#FF0000", zones));

        Assert.Equal(0, wmi.SetLightingCalls);
        Assert.Equal(1, agent.SetLightingCalls);
        Assert.Null(hybrid.LastZoneColorsApplied);
    }

    [Fact]
    public async Task Hybrid_FourZoneColors_DoNotStartTheHelperWhenElevationIsNotAllowed()
    {
        var agent = new RecordingController("AASSvc");
        var wmi = new RecordingController("WMI");
        int factoryCalls = 0;
        using var hybrid = new HybridHardwareController(
            agent,
            wmiFactory: () => { factoryCalls++; return wmi; },
            useDirectWmi: () => true,
            serviceHealthy: () => Task.FromResult(true));

        // The boot re-assertion runs with this cleared, so a saved four-zone configuration cannot raise a prompt
        // when the app opens.
        hybrid.AllowElevationForLighting = false;

        var zones = new[] { "#FF0000", "#00FF00", "#0000FF", "#FFFFFF" };
        Assert.True(await hybrid.SetLightingAsync("STATIC", 5, 3, 1, "#FF0000", zones));

        Assert.Equal(0, factoryCalls);
        Assert.Equal(0, wmi.SetLightingCalls);
        Assert.Equal(1, agent.SetLightingCalls);
        Assert.False(hybrid.LastZoneColorsApplied);
    }

    [Fact]
    public async Task Hybrid_FourZoneColors_UseAnAlreadyRunningHelperWithoutAsking()
    {
        var agent = new RecordingController("AASSvc");
        var wmi = new RecordingController("WMI");
        using var hybrid = new HybridHardwareController(
            agent,
            wmiFactory: () => wmi,
            useDirectWmi: () => true,
            serviceHealthy: () => Task.FromResult(true));

        Assert.True(await hybrid.StartDirectWmiAsync());
        hybrid.AllowElevationForLighting = false;

        var zones = new[] { "#FF0000", "#00FF00", "#0000FF", "#FFFFFF" };
        Assert.True(await hybrid.SetLightingAsync("STATIC", 5, 3, 1, "#FF0000", zones));

        Assert.Equal(1, wmi.SetLightingCalls);
        Assert.True(hybrid.LastZoneColorsApplied);
    }

    [Fact]
    public async Task Hybrid_FactoryThatThrows_LeavesTheAgentInPlace()
    {
        var agent = new RecordingController("AASSvc");
        using var hybrid = new HybridHardwareController(
            agent,
            wmiFactory: () => throw new InvalidOperationException("no worker"),
            useDirectWmi: () => true,
            serviceHealthy: () => Task.FromResult(false));

        Assert.True(await hybrid.SetFanModeAsync(FanMode.Auto));
        Assert.Equal(1, agent.SetFanModeCalls);
        Assert.Equal("AASSvc", hybrid.ActiveBackendName);
    }

    // ---------------------------------------------------------------- parent watchdog

    [Fact]
    public async Task ParentWatchdog_HandleMode_FiresWhenTheWatchedProcessExits()
    {
        using var child = Process.Start(new ProcessStartInfo("cmd.exe", "/c pause")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        });

        Assert.NotNull(child);
        Assert.True(ParentWatchdog.TryOpenParentHandle(child!.Id, out var handle));
        Assert.True(ParentWatchdog.IsHandleAlive(handle));

        var fired = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watchdog = ParentWatchdog.Start(
            child.Id,
            () => fired.TrySetResult(true),
            interval: TimeSpan.FromMilliseconds(20));

        child.Kill(entireProcessTree: true);
        child.WaitForExit(10_000);

        var completed = await Task.WhenAny(fired.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(fired.Task, completed);

        // The held handle is signalled now, which is what makes the pid-reuse case safe.
        Assert.False(ParentWatchdog.IsHandleAlive(handle));
        handle.Dispose();
    }

    [Fact]
    public void ParentWatchdog_HandleMode_ReportsAnImpossiblePidAsGone()
    {
        Assert.False(ParentWatchdog.TryOpenParentHandle(0, out var zero));
        zero.Dispose();

        Assert.False(ParentWatchdog.TryOpenParentHandle(int.MaxValue, out var missing));
        missing.Dispose();

        Assert.False(ParentWatchdog.IsHandleAlive(null));
    }

    // ---------------------------------------------------------------- helpers

    private static Task NoDelay(TimeSpan _) => Task.CompletedTask;

    /// <summary>
    /// Splits a command line the way the Windows loader does for the shapes this application produces: quoted
    /// values, unquoted values, no escapes.
    /// </summary>
    private static string[] SplitArguments(string commandLine)
    {
        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        bool inQuotes = false;

        foreach (var c in commandLine)
        {
            if (c == '"') { inQuotes = !inQuotes; continue; }
            if (c == ' ' && !inQuotes)
            {
                if (current.Length > 0) { parts.Add(current.ToString()); current.Clear(); }
                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0) parts.Add(current.ToString());
        return parts.ToArray();
    }

    private static CapabilitySnapshot SnapshotWith(string capability, HardwareProbeOutcome outcome) => new()
    {
        CacheKey = "AN16-41|INSYNC 1.27",
        ProfileId = "nitro-an16-41",
        Capabilities = new[]
        {
            new HardwareProbeResult { Capability = capability, Outcome = outcome, Detail = "test" }
        }
    };

    /// <summary>
    /// A backend that records what it was asked to do and answers success. Used to prove which side of the hybrid a
    /// call landed on without touching either real backend.
    /// </summary>
    private sealed class RecordingController : IHardwareController
    {
        public RecordingController(string name) => BackendName = name;

        public string BackendName { get; }
        public bool IsAvailable => true;

        public int SetFanModeCalls { get; private set; }
        public int SetOperatingModeCalls { get; private set; }
        public int SetLightingCalls { get; private set; }
        public string[]? LastZoneColors { get; private set; }
        public int TurnOffLightingCalls { get; private set; }
        public int SetBacklightTimeoutCalls { get; private set; }

        public Task<bool> EnsureConnectedAsync() => Task.FromResult(true);

        public Task<HardwareCapabilities> GetCapabilitiesAsync() =>
            Task.FromResult(new HardwareCapabilities { Backend = BackendName, ServiceAvailable = true });

        public Task<MonitorData?> GetMonitorDataAsync() => Task.FromResult<MonitorData?>(new MonitorData());

        public Task<OperatingMode?> GetOperatingModeAsync() => Task.FromResult<OperatingMode?>(OperatingMode.Default);

        public Task<bool> SetOperatingModeAsync(OperatingMode mode)
        {
            SetOperatingModeCalls++;
            return Task.FromResult(true);
        }

        public Task<GpuOperatingMode?> GetGpuModeAsync() => Task.FromResult<GpuOperatingMode?>(GpuOperatingMode.Optimus);

        public Task<bool> SetGpuModeAsync(GpuOperatingMode mode) => Task.FromResult(true);

        public Task<int?> GetGpuModeCapabilityAsync() => Task.FromResult<int?>(7);

        public Task<FanMode?> GetFanModeAsync() => Task.FromResult<FanMode?>(FanMode.Auto);

        public Task<bool> SetFanModeAsync(FanMode mode, int cpuPercent = -1, int gpuPercent = -1)
        {
            SetFanModeCalls++;
            return Task.FromResult(true);
        }

        public Task<LightingConfig?> GetLightingConfigAsync() => Task.FromResult<LightingConfig?>(null);

        public Task<bool> SetLightingAsync(string effect, int brightness, int speed, int direction, string primaryColor, string[]? zoneColors)
        {
            SetLightingCalls++;
            LastZoneColors = zoneColors;
            return Task.FromResult(true);
        }

        public Task<bool> TurnOffLightingAsync()
        {
            TurnOffLightingCalls++;
            return Task.FromResult(true);
        }

        public Task<bool?> GetBacklightTimeoutAsync() => Task.FromResult<bool?>(false);

        public Task<bool> SetBacklightTimeoutAsync(bool enable30s)
        {
            SetBacklightTimeoutCalls++;
            return Task.FromResult(true);
        }

        public Task<BatteryChargeMode?> GetBatteryChargeModeAsync() =>
            Task.FromResult<BatteryChargeMode?>(BatteryChargeMode.Optimized80);

        public Task<bool> SetBatteryChargeModeAsync(BatteryChargeMode mode) => Task.FromResult(true);

        public void Dispose() { }
    }
}
