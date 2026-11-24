using System.Diagnostics;
using NitroLite.Core;
using NitroLite.Core.Hardware;
using NitroLite.Core.Hardware.Lighting;
using NitroLite.Core.Hardware.Verification;
using NitroLite.Core.Hardware.Wmi;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// Audit group G2a: the Windows WMI stack. Nothing here touches the hardware — the launcher is driven through an
/// injected process starter, the WMI factory and the HID writer are fakes, and the read-back delay is a no-op.
/// </summary>
public class AuditG2aWmiStackTests
{
    private static Task NoDelay(TimeSpan _) => Task.CompletedTask;

    // ---------------------------------------------------------------- (a) a read never starts the elevated helper

    [Fact]
    public async Task Hybrid_BootReads_DoNotConnectANotYetStartedWmiBackend_WhenTheServiceIsHealthy()
    {
        // A backend object that exists but has never opened its transport — the state every Windows launch starts in.
        // Looking it up starts nothing; asking it to connect is what would start the helper and raise UAC.
        var wmi = new Phase3Fakes.StubHardware { IsAvailable = false };
        var agent = new Phase3Fakes.StubHardware();
        using var hybrid = new HybridHardwareController(
            agent,
            wmiFactory: () => wmi,
            serviceHealthy: () => Task.FromResult(true));

        // AllowElevationForLighting defaults to true; that must not let a read reach the helper.
        Assert.True(hybrid.AllowElevationForLighting);

        await hybrid.GetCoolBoostAsync();
        await hybrid.GetLcdOverdriveAsync();
        await hybrid.GetBacklightTimeoutAsync();

        Assert.Equal(0, wmi.ConnectCount);
    }

    [Fact]
    public async Task Hybrid_BootReads_DoNotStartTheWorker_EvenWithTheServiceDown()
    {
        int starts = 0;
        using var launcher = new WmiWorkerLauncher(
            executablePath: "NitroLite.exe",
            start: _ =>
            {
                starts++;
                throw new InvalidOperationException("a read must not start the worker");
            },
            isElevated: () => true,
            isTrustedLocation: _ => true);
        var agent = new Phase3Fakes.StubHardware();
        using var hybrid = new HybridHardwareController(
            agent,
            wmiFactory: () => new WmiHardwareController(launcher, new ReadBackPolicy(new AppSettings(), NoDelay)),
            serviceHealthy: () => Task.FromResult(false));

        await hybrid.GetCoolBoostAsync();
        await hybrid.GetLcdOverdriveAsync();
        await hybrid.GetBacklightTimeoutAsync();

        Assert.Equal(0, starts);
        Assert.Equal(0, launcher.Attempts);
    }

    [Fact]
    public async Task Hybrid_AWrite_StillMayCreateTheWmiBackend()
    {
        int factoryCalls = 0;
        var agent = new Phase3Fakes.StubHardware();
        using var hybrid = new HybridHardwareController(
            agent,
            wmiFactory: () =>
            {
                factoryCalls++;
                return null;
            },
            serviceHealthy: () => Task.FromResult(true));

        await hybrid.SetCoolBoostAsync(true);

        Assert.Equal(1, factoryCalls);
    }

    // ---------------------------------------------------------------- (b) a failed launch is not repeated

    [Fact]
    public async Task Launcher_AfterAStartFailure_DoesNotStartAgainUntilAnExplicitRetry()
    {
        int starts = 0;
        using var launcher = new WmiWorkerLauncher(
            executablePath: "NitroLite.exe",
            start: _ =>
            {
                starts++;
                throw new InvalidOperationException("the helper could not be started");
            },
            isElevated: () => true,
            isTrustedLocation: _ => true);

        Assert.Null(await launcher.StartAsync());
        Assert.Equal(WmiWorkerRefusal.StartFailed, launcher.Refusal);

        Assert.Null(await launcher.StartAsync());
        Assert.Equal(1, starts);
        Assert.Equal(1, launcher.Attempts);
        Assert.True(launcher.FailedThisSession);
        Assert.Equal(WmiWorkerRefusal.StartFailed, launcher.Refusal);
        Assert.False(launcher.UserDeclined);

        launcher.AllowRetry();
        Assert.Null(await launcher.StartAsync());
        Assert.Equal(2, starts);
    }

    [Fact]
    public async Task Launcher_AfterAHandshakeFailure_DoesNotStartAgain()
    {
        int starts = 0;
        int connects = 0;
        using var launcher = new WmiWorkerLauncher(
            executablePath: "NitroLite.exe",
            start: _ =>
            {
                starts++;
                return Process.Start(new ProcessStartInfo("cmd.exe", "/c exit")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false
                });
            },
            connect: (_, _, _, _, _) =>
            {
                connects++;
                throw new InvalidOperationException("the worker did not answer the handshake in time");
            },
            isElevated: () => true,
            isTrustedLocation: _ => true);

        Assert.Null(await launcher.StartAsync());
        Assert.Equal(WmiWorkerRefusal.HandshakeFailed, launcher.Refusal);

        Assert.Null(await launcher.StartAsync());
        Assert.Equal(1, starts);
        Assert.Equal(1, connects);
        Assert.Equal(WmiWorkerRefusal.HandshakeFailed, launcher.Refusal);
    }

    [Fact]
    public async Task Hybrid_OnlyTheExplicitStartPath_RetriesAFailedLaunch()
    {
        int starts = 0;
        using var launcher = new WmiWorkerLauncher(
            executablePath: "NitroLite.exe",
            start: _ =>
            {
                starts++;
                throw new InvalidOperationException("the helper could not be started");
            },
            isElevated: () => true,
            isTrustedLocation: _ => true);
        var agent = new Phase3Fakes.StubHardware();
        using var hybrid = new HybridHardwareController(
            agent,
            wmiFactory: () => new WmiHardwareController(launcher, new ReadBackPolicy(new AppSettings(), NoDelay)),
            serviceHealthy: () => Task.FromResult(false));

        Assert.False(await hybrid.StartDirectWmiAsync());
        Assert.Equal(1, starts);

        // Automatic paths: a write that may start the helper, a fan write, and a telemetry-style resolve.
        await hybrid.SetCoolBoostAsync(true);
        await hybrid.SetFanModeAsync(FanMode.Max);
        await hybrid.EnsureConnectedAsync();
        Assert.Equal(1, starts);

        // The user pressed the button again: that one is allowed to try.
        Assert.False(await hybrid.StartDirectWmiAsync());
        Assert.Equal(2, starts);
    }

    // ---------------------------------------------------------------- (c) not supported is not a failed session

    [Fact]
    public async Task ReadBackPolicy_NotSupported_IsNotCountedTowardThePermanentDisable()
    {
        var settings = new AppSettings();
        var policy = new ReadBackPolicy(settings, NoDelay, new SessionVerification());

        for (int session = 0; session < ReadBackPolicy.SessionsBeforePermanentDisable + 1; session++)
        {
            var result = await policy.ApplyAsync<GpuOperatingMode>(
                ReadBackPolicy.MuxMode,
                write: () => throw new WmiCapabilityNotSupportedException("this machine reports no MUX switch"),
                readBack: () => Task.FromResult<GpuOperatingMode?>(null),
                matches: _ => true,
                requested: nameof(GpuOperatingMode.DiscreteGpu));

            Assert.False(result.Applied);
            Assert.True(result.NotSupported);
            Assert.False(result.DisabledForSession);
            Assert.False(result.PermanentlyDisabled);
        }

        Assert.False(policy.IsDisabled(ReadBackPolicy.MuxMode));
        Assert.Equal(0, policy.ConsecutiveSessionFailures(ReadBackPolicy.MuxMode));
        Assert.DoesNotContain(ReadBackPolicy.MuxMode, settings.DisabledCapabilities);
    }

    // ---------------------------------------------------------------- (d) turning the lights off honours HID

    private sealed class RecordingHidWriter : IHidLightingWriter
    {
        public string Description => "fake HID keyboard";

        public string Protocol => "fake";

        public bool CanWrite => true;

        public string? Refusal => null;

        public List<KeyboardLightingFrame> Frames { get; } = new();

        public bool TryWrite(KeyboardLightingFrame frame, out string? error)
        {
            Frames.Add(frame);
            error = null;
            return true;
        }

        public void Dispose() { }
    }

    [Fact]
    public async Task Hybrid_TurnOff_OnAHidKeyboardProfile_WritesAnOffFrameThroughHid()
    {
        var writer = new RecordingHidWriter();
        var agent = new Phase3Fakes.StubHardware();
        using var hybrid = new HybridHardwareController(
            agent,
            serviceHealthy: () => Task.FromResult(true),
            hidLighting: () => writer)
        {
            PreferHidKeyboard = () => true
        };

        Assert.True(await hybrid.TurnOffLightingAsync());

        var frame = Assert.Single(writer.Frames);
        Assert.True(frame.IsOff);

        // The service's own turn-off goes through the interface these keys ignore; it must not have been the answer.
        Assert.Empty(agent.LightingWrites);
    }

    [Fact]
    public async Task Hybrid_TurnOff_WithoutAHidProfile_StillGoesToTheService()
    {
        var agent = new Phase3Fakes.StubHardware();
        using var hybrid = new HybridHardwareController(agent, serviceHealthy: () => Task.FromResult(true));

        Assert.True(await hybrid.TurnOffLightingAsync());

        var write = Assert.Single(agent.LightingWrites);
        Assert.StartsWith("OFF|0|", write, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- (e) dispose does not break in-flight callers

    [Fact]
    public async Task WmiController_AfterDispose_WritesReturnFalseWithoutThrowing()
    {
        using var launcher = new WmiWorkerLauncher(
            executablePath: "NitroLite.exe",
            start: _ => throw new InvalidOperationException("a disposed controller must not start the worker"),
            isElevated: () => true,
            isTrustedLocation: _ => true);
        var controller = new WmiHardwareController(launcher, new ReadBackPolicy(new AppSettings(), NoDelay));
        controller.Dispose();

        Assert.False(await controller.SetFanModeAsync(FanMode.Max));
        Assert.False(await controller.SetGpuModeAsync(GpuOperatingMode.DiscreteGpu));
        Assert.False(await controller.EnsureConnectedAsync());
        Assert.Equal(0, launcher.Attempts);
    }

    // ---------------------------------------------------------------- (f) a timed-out read does not relaunch

    /// <summary>Answers every command at once except <c>ReadTelemetry</c>, which hangs like a stuck ACPI method.</summary>
    private sealed class HangingTelemetryExecutor : IWmiExecutor
    {
        public ManualResetEventSlim Release { get; } = new(false);

        public bool IsAvailable => true;

        private static WmiOperationResult Ok() => WmiOperationResult.Success();

        public WmiOperationResult Ping() => Ok();
        public WmiOperationResult GetCapabilities() => Ok();

        public WmiOperationResult ReadTelemetry(SensorQuery query)
        {
            Release.Wait(TimeSpan.FromSeconds(10));
            return Ok();
        }

        public WmiOperationResult ReadDeviceState() => Ok();
        public WmiOperationResult SetFanBehavior(FanMode cpu, FanMode gpu) => Ok();
        public WmiOperationResult SetFanSpeed(int cpuPercent, int gpuPercent) => Ok();
        public WmiOperationResult SetOperatingMode(OperatingMode mode) => Ok();
        public WmiOperationResult SetMuxMode(GpuOperatingMode mode) => Ok();
        public WmiOperationResult SetBatteryLimit(BatteryChargeMode mode) => Ok();
        public WmiOperationResult GetKeyboardBacklight() => Ok();
        public WmiOperationResult SetKeyboardLighting(KeyboardLightingRequest request) => Ok();
        public WmiOperationResult GetCoolBoost() => Ok();
        public WmiOperationResult SetCoolBoost(bool on) => Ok();
        public WmiOperationResult GetLcdOverdrive() => Ok();
        public WmiOperationResult SetLcdOverdrive(bool on) => Ok();
        public WmiOperationResult SetWinKeyLock(bool enabled) => Ok();
        public WmiOperationResult GetBacklightTimeout() => Ok();
        public WmiOperationResult SetBacklightTimeout(bool enabled) => Ok();

        public void Dispose() => Release.Set();
    }

    [Fact]
    public async Task WmiController_AfterATimedOutRead_TheNextReadDoesNotStartANewWorker()
    {
        var pipeName = "nitrolite-g2a-test-" + Guid.NewGuid().ToString("N");
        using var executor = new HangingTelemetryExecutor();
        using var server = new WmiWorkerServer(pipeName, executor);
        var serverTask = Task.Run(() => server.RunAsync(TimeSpan.FromSeconds(15)));

        int starts = 0;
        using var launcher = new WmiWorkerLauncher(
            executablePath: "NitroLite.exe",
            start: _ =>
            {
                starts++;
                return Process.Start(new ProcessStartInfo("cmd.exe", "/c exit")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false
                });
            },
            // The launcher's own pipe name is ignored: the fake worker above is already listening on its own.
            connect: async (_, nonce, _, _, ct) =>
            {
                var client = await WmiWorkerClient.ConnectAsync(pipeName, nonce, TimeSpan.FromSeconds(5), null, ct);
                client.CommandTimeout = TimeSpan.FromMilliseconds(300);
                return client;
            },
            isElevated: () => true,
            isTrustedLocation: _ => true);
        using var controller = new WmiHardwareController(launcher, new ReadBackPolicy(new AppSettings(), NoDelay));

        // The sensor probe's ReadTelemetry hangs and times out; the sample read right after it must not relaunch.
        Assert.Null(await controller.GetMonitorDataAsync());
        Assert.Equal(1, starts);
        Assert.Equal(1, launcher.Attempts);
        Assert.False(controller.IsAvailable);

        // A later poll is answered from memory too.
        Assert.Null(await controller.GetMonitorDataAsync());
        Assert.Null(await controller.GetCoolBoostAsync());
        Assert.Equal(1, launcher.Attempts);

        executor.Release.Set();
        await serverTask.WaitAsync(TimeSpan.FromSeconds(10));
    }

    // ---------------------------------------------------------------- (g) a dropped transport is not a probe answer

    [Fact]
    public async Task WmiController_SensorProbe_IsNotMarkedRunWhenNoResponseArrived()
    {
        using var launcher = new WmiWorkerLauncher(
            executablePath: "NitroLite.exe",
            start: _ => throw new InvalidOperationException("no worker"),
            isElevated: () => true,
            isTrustedLocation: _ => true);
        using var controller = new WmiHardwareController(launcher, new ReadBackPolicy(new AppSettings(), NoDelay));

        Assert.Null(await controller.GetMonitorDataAsync());

        Assert.False(controller.SensorProbeRan);
        Assert.Null(controller.DiscoveredSensors);
    }
}
