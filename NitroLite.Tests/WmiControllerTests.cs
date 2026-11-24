using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Nodes;
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
    public async Task ReadBack_AMissAfterAConfirmedWrite_IsReported_ButDoesNotSwitchTheControlOff()
    {
        // An AN17-51: thirty colours confirmed, then the one written as the charger went in did not read back, and
        // the session-wide disable that followed made every later colour fail without being tried.
        var settings = new AppSettings();
        var policy = new ReadBackPolicy(settings, NoDelay);
        var stored = 0x111111;

        var first = await policy.ApplyAsync(ReadBackPolicy.KeyboardLighting,
            write: () => { stored = 0x222222; return Task.FromResult(true); },
            readBack: () => Task.FromResult<int?>(stored), matches: v => v == 0x222222);
        var missed = await policy.ApplyAsync(ReadBackPolicy.KeyboardLighting,
            write: () => Task.FromResult(true),
            readBack: () => Task.FromResult<int?>(stored), matches: v => v == 0x333333);
        var next = await policy.ApplyAsync(ReadBackPolicy.KeyboardLighting,
            write: () => { stored = 0x444444; return Task.FromResult(true); },
            readBack: () => Task.FromResult<int?>(stored), matches: v => v == 0x444444);

        Assert.True(first.Applied);
        Assert.False(missed.Applied);
        Assert.False(missed.DisabledForSession);
        Assert.True(next.Applied);
        Assert.False(policy.IsDisabled(ReadBackPolicy.KeyboardLighting));
        Assert.Equal(0, policy.ConsecutiveSessionFailures(ReadBackPolicy.KeyboardLighting));
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
            start: _ => throw new Win32Exception(WmiWorkerLauncher.ErrorCancelled),
            // Directory trust has its own test. On a CI runner the build folder is world-writable, and letting
            // that decide these tests would make them assert the runner's ACLs instead of the UAC path.
            isTrustedLocation: _ => true);

        var first = await launcher.StartAsync();
        var second = await launcher.StartAsync();

        Assert.Null(first);
        Assert.Null(second);
        Assert.True(launcher.UserDeclined);
        Assert.Equal(1, launcher.Attempts);
        Assert.Contains("declined", launcher.LastError);
    }

    [Fact]
    public async Task Launcher_OtherStartFailure_IsNotRecordedAsADeclinedPrompt()
    {
        // Not a "no" from the user: UserDeclined stays false. The launch is still not repeated automatically after it
        // (AuditG2aWmiStackTests covers that); only the explicit start path may retry it.
        using var launcher = new WmiWorkerLauncher(
            executablePath: "NitroLite.exe",
            start: _ => throw new Win32Exception(5),
            // Directory trust has its own test. On a CI runner the build folder is world-writable, and letting
            // that decide these tests would make them assert the runner's ACLs instead of the UAC path.
            isTrustedLocation: _ => true);

        var result = await launcher.StartAsync();

        Assert.Null(result);
        Assert.False(launcher.UserDeclined);
        Assert.Equal(1, launcher.Attempts);
    }

    [Fact]
    public void Launcher_Arguments_RoundTripThroughTheWorkerParser()
    {
        var arguments = WmiWorkerLauncher.BuildArguments("nitrolite-wmi-abc", 4242);

        var mode = StartupMode.Parse(SplitArguments(arguments));

        Assert.True(mode.IsWmiWorker);
        Assert.True(mode.IsValidWorkerRequest);
        Assert.Equal("nitrolite-wmi-abc", mode.PipeName);
        Assert.Equal(4242, mode.ParentProcessId);
    }

    // ---------------------------------------------------------------- WmiHardwareController

    [Fact]
    public async Task An16_61_Bios153DoesNotSendTheModeWriteThatSelectsBalanced()
    {
        using var launcher = new WmiWorkerLauncher(executablePath: "NitroLite.exe",
            start: _ => throw new InvalidOperationException(), isTrustedLocation: _ => true);
        using var controller = new WmiHardwareController(launcher, new ReadBackPolicy(new AppSettings(), NoDelay),
            () => new CapabilitySnapshot { CacheKey = "AN16-61|1.53" });
        Assert.False(await controller.SetOperatingModeAsync(OperatingMode.Turbo));
        Assert.Equal(0, launcher.Attempts);
    }

    [Fact]
    public async Task WmiController_DeclinedElevation_StaysUsableAndDoesNotRetry()
    {
        using var launcher = new WmiWorkerLauncher(
            executablePath: "NitroLite.exe",
            start: _ => throw new Win32Exception(WmiWorkerLauncher.ErrorCancelled),
            // Directory trust has its own test. On a CI runner the build folder is world-writable, and letting
            // that decide these tests would make them assert the runner's ACLs instead of the UAC path.
            isTrustedLocation: _ => true);
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
    public async Task WmiController_WriteWithoutTransport_DoesNotCountASessionFailure()
    {
        // The worker never started (UAC declined, or the install folder is untrusted). That is "no backend",
        // not "the firmware refused the write": counting it would burn the three-session budget on a machine
        // where nothing was ever written and leave the capability permanently disabled for a phantom reason.
        var settings = new AppSettings();
        using var launcher = new WmiWorkerLauncher(
            executablePath: "NitroLite.exe",
            start: _ => throw new Win32Exception(WmiWorkerLauncher.ErrorCancelled),
            isTrustedLocation: _ => true);
        using var controller = new WmiHardwareController(launcher, new ReadBackPolicy(settings, NoDelay));

        Assert.False(await controller.SetFanModeAsync(FanMode.Max));
        Assert.False(await controller.SetOperatingModeAsync(OperatingMode.Turbo));

        Assert.Empty(settings.CapabilitySessionFailures);
        Assert.Empty(settings.DisabledCapabilities);
        Assert.False(new ReadBackPolicy(settings, NoDelay).IsDisabled(ReadBackPolicy.FanBehavior));
    }

    [Fact]
    public async Task WmiController_BatteryLimitKnownUnsupported_DoesNotAskTheWorker()
    {
        // The probe positively found no WMID_GUID5 class. The combined battery capability is a separate question: on
        // an AN16-41 it is supported through Acer Care Center, and this backend still must not write through a class
        // that is not there.
        var snapshot = SnapshotWith(CapabilitySnapshot.WmiBatteryChargeLimit, HardwareProbeOutcome.Supported)
            with { WmiBatteryChargeLimitPresent = false };
        using var launcher = new WmiWorkerLauncher(
            executablePath: "NitroLite.exe",
            start: _ => throw new Win32Exception(WmiWorkerLauncher.ErrorCancelled),
            // Directory trust has its own test. On a CI runner the build folder is world-writable, and letting
            // that decide these tests would make them assert the runner's ACLs instead of the UAC path.
            isTrustedLocation: _ => true);
        using var controller = new WmiHardwareController(
            launcher, new ReadBackPolicy(new AppSettings(), NoDelay), () => snapshot);

        Assert.True(controller.BatteryLimitKnownUnsupported);
        Assert.False(await controller.SetBatteryChargeModeAsync(BatteryChargeMode.Optimized80));
        Assert.Null(await controller.GetBatteryChargeModeAsync());

        // The whole point: no elevation was requested for a capability the probe already ruled out.
        Assert.Equal(0, launcher.Attempts);
    }

    [Fact]
    public async Task WmiController_BatteryLimitAnsweredByCareCenter_StillRefusesTheWmiWrite()
    {
        // The AN16-41 case: the capability is a yes because Acer Care Center owns the limit, and the WMI class the
        // worker would write through does not exist. A supported capability must not turn into a doomed round trip.
        var snapshot = SnapshotWith(CapabilitySnapshot.WmiBatteryChargeLimit, HardwareProbeOutcome.Supported)
            with { WmiBatteryChargeLimitPresent = false };
        using var launcher = new WmiWorkerLauncher(
            executablePath: "NitroLite.exe",
            start: _ => throw new Win32Exception(WmiWorkerLauncher.ErrorCancelled),
            // Directory trust has its own test. On a CI runner the build folder is world-writable, and letting
            // that decide these tests would make them assert the runner's ACLs instead of the UAC path.
            isTrustedLocation: _ => true);
        using var controller = new WmiHardwareController(
            launcher, new ReadBackPolicy(new AppSettings(), NoDelay), () => snapshot);

        Assert.True(snapshot.IsSupported(CapabilitySnapshot.WmiBatteryChargeLimit));
        Assert.True(controller.BatteryLimitKnownUnsupported);
        Assert.False(await controller.SetBatteryChargeModeAsync(BatteryChargeMode.Optimized80));
        Assert.Equal(0, launcher.Attempts);
    }

    [Fact]
    public async Task WmiController_BatteryLimitUnknown_DoesNotBlockTheWrite()
    {
        // A cache written by a build that did not record the field, or a schema enumeration that failed, is not
        // evidence of absence: the worker is asked and answers for itself.
        var snapshot = SnapshotWith(CapabilitySnapshot.WmiBatteryChargeLimit, HardwareProbeOutcome.Unknown);
        using var launcher = new WmiWorkerLauncher(
            executablePath: "NitroLite.exe",
            start: _ => throw new Win32Exception(WmiWorkerLauncher.ErrorCancelled),
            // Directory trust has its own test. On a CI runner the build folder is world-writable, and letting
            // that decide these tests would make them assert the runner's ACLs instead of the UAC path.
            isTrustedLocation: _ => true);
        using var controller = new WmiHardwareController(
            launcher, new ReadBackPolicy(new AppSettings(), NoDelay), () => snapshot);

        Assert.Null(snapshot.WmiBatteryChargeLimitPresent);
        Assert.False(controller.BatteryLimitKnownUnsupported);
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

    // ---------------------------------------------------------------- live sensor telemetry

    /// <summary>The five answers an AN16-41 gave, in the shape the worker reports them.</summary>
    private static JsonObject MeasuredTelemetry() => new()
    {
        ["sensors"] = new JsonObject
        {
            ["cpuTemp"] = Sensor(1, 63),
            ["cpuFanRpm"] = Sensor(2, 2436),
            ["sys1Temp"] = Sensor(3, 55),
            ["gpuFanRpm"] = Sensor(6, 2409),
            ["gpuTemp"] = Sensor(10, 57)
        },
        ["cpuFanPercent"] = 34,
        ["gpuFanPercent"] = 34
    };

    private static JsonObject Sensor(int id, int value) => new()
    {
        ["sensor"] = id,
        ["status"] = 0,
        ["value"] = value
    };

    private static SensorLayout MeasuredLayout() => new()
    {
        Supported = true,
        Verified = true
    };

    [Fact]
    public void WmiTelemetry_RealSensors_AreDecodedAndMarkedMeasured()
    {
        var data = WmiHardwareController.BuildMonitorData(MeasuredTelemetry(), MeasuredLayout(), 6200, 6000);

        Assert.Equal(63, data.CpuTemperature);
        Assert.Equal(57, data.GpuTemperature);
        Assert.Equal(55, data.SysTemperature);
        Assert.Equal(2436, data.CpuFanSpeed);
        Assert.Equal(2409, data.GpuFanSpeed);

        // Real tachometer readings, so the stall watchdog is allowed to judge this sample.
        Assert.True(data.IsFanRpmMeasured);
    }

    [Fact]
    public void WmiTelemetry_RealRpm_FeedsTheStallWatchdog()
    {
        // The whole point of the measured flag: a hot die with a stopped fan has to be caught on the WMI path too.
        var hot = MeasuredTelemetry();
        hot["sensors"]!["cpuTemp"] = Sensor(1, 92);
        hot["sensors"]!["gpuTemp"] = Sensor(10, 90);
        hot["sensors"]!["cpuFanRpm"] = Sensor(2, 0);
        hot["sensors"]!["gpuFanRpm"] = Sensor(6, 0);

        var data = WmiHardwareController.BuildMonitorData(hot, MeasuredLayout(), 6200, 6000);
        Assert.True(data.IsFanRpmMeasured);

        var watchdog = new FanStallWatchdog();
        var start = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        Assert.Null(watchdog.Observe(
            data.CpuTemperature, data.GpuTemperature, data.CpuFanSpeed, data.GpuFanSpeed, start));

        var verdict = watchdog.Observe(
            data.CpuTemperature, data.GpuTemperature, data.CpuFanSpeed, data.GpuFanSpeed, start.AddSeconds(11));

        Assert.NotNull(verdict);
        Assert.Equal(FanStallTarget.Both, verdict!.Target);
    }

    [Fact]
    public void WmiTelemetry_NonZeroStatus_IsDiscardedAndFallsBackToDerivedRpm()
    {
        // A firmware that does not know the id answers a status code and a zero. Publishing that zero would show a
        // fan at 0 RPM that is physically spinning, and would let the watchdog fire on a healthy machine.
        var answer = MeasuredTelemetry();
        answer["sensors"]!["cpuFanRpm"] = new JsonObject { ["sensor"] = 2, ["status"] = 1, ["value"] = 0 };
        answer["sensors"]!["gpuFanRpm"] = new JsonObject { ["sensor"] = 6, ["status"] = 1, ["value"] = 0 };

        var data = WmiHardwareController.BuildMonitorData(answer, MeasuredLayout(), 6200, 6000);

        Assert.False(data.IsFanRpmMeasured);
        Assert.Equal(WmiHardwareController.DeriveRpm(34, 6200), data.CpuFanSpeed);
        Assert.Equal(WmiHardwareController.DeriveRpm(34, 6000), data.GpuFanSpeed);
    }

    [Fact]
    public void WmiTelemetry_OneTachometerMissing_StaysInStandby()
    {
        // Half a measurement is not a measurement: the watchdog judges both fans together, so a real CPU reading
        // next to a derived GPU one would compare a measurement against a guess.
        var answer = MeasuredTelemetry();
        ((JsonObject)answer["sensors"]!).Remove("gpuFanRpm");

        var data = WmiHardwareController.BuildMonitorData(answer, MeasuredLayout(), 6200, 6000);

        Assert.False(data.IsFanRpmMeasured);
        Assert.Equal(WmiHardwareController.DeriveRpm(34, 6200), data.CpuFanSpeed);
    }

    [Fact]
    public void WmiTelemetry_UnsupportedLayout_ReportsNoTemperatures()
    {
        // A machine the table does not name has no confirmed sensor ids. Reporting 0 is the honest answer: the UI
        // renders it as "-- °C" instead of a number that is not a temperature.
        var data = WmiHardwareController.BuildMonitorData(MeasuredTelemetry(), SensorLayout.Default, 6200, 6000);

        Assert.Equal(0, data.CpuTemperature);
        Assert.Equal(0, data.GpuTemperature);
        Assert.Equal(0, data.SysTemperature);
        Assert.False(data.IsFanRpmMeasured);
    }

    [Fact]
    public void WmiTelemetry_NoSensorsObject_DoesNotThrow()
    {
        var data = WmiHardwareController.BuildMonitorData(new JsonObject(), MeasuredLayout(), 6200, 6000);

        Assert.Equal(0, data.CpuTemperature);
        Assert.False(data.IsFanRpmMeasured);
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

        // The service is down, so colour writes go to the WMI backend, which can drive the zones directly. Turning
        // the lights off travels the same chain as a colour write (so a HID-keyboard profile can catch it too), as
        // the OFF request both backends use for their own turn-off: a second lighting write with four black zones.
        Assert.Equal(2, wmi.SetLightingCalls);
        Assert.Equal(new[] { "#000000", "#000000", "#000000", "#000000" }, wmi.LastZoneColors);
        Assert.Equal(0, wmi.TurnOffLightingCalls);
        Assert.Equal(0, agent.SetLightingCalls);

        // The 30-second backlight timeout lives on APGeAction, which only the WMI backend can reach, so it goes
        // there rather than to a service that has no such command.
        Assert.Equal(1, wmi.SetBacklightTimeoutCalls);
        Assert.Equal(0, agent.SetBacklightTimeoutCalls);
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
        // colours — and the service is not asked for anything at all. It takes one colour and paints all four zones
        // with it, so that pre-write is a solid keyboard underneath the split: whichever of the two the firmware
        // finished second is what the user sees, and on the reference AN16-41 the solid one won and halves came out
        // entirely in the left half's colour. The effect parameters travel with the firmware write, so nothing is lost.
        Assert.Equal(1, wmi.SetLightingCalls);
        Assert.Equal(zones, wmi.LastZoneColors);
        Assert.Equal(0, agent.SetLightingCalls);
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

        await fired.Task.WaitAsync(TimeSpan.FromSeconds(10));

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

        public Task<bool?> GetCoolBoostAsync() => Task.FromResult<bool?>(null);

        public Task<bool> SetCoolBoostAsync(bool on) => Task.FromResult(false);

        public Task<bool?> GetLcdOverdriveAsync() => Task.FromResult<bool?>(null);

        public Task<bool> SetLcdOverdriveAsync(bool on) => Task.FromResult(false);

        public Task<bool> SetWinKeyLockAsync(bool enabled) => Task.FromResult(false);

        public void Dispose() { }
    }
}
