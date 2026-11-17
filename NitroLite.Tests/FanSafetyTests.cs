using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using NitroLite.Core;
using NitroLite.Core.Hardware;
using NitroLite.Core.Hardware.Wmi;
using NitroLite.Desktop.ViewModels;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// The thermal floor on the manual sliders, the fan loop's own clock, and the shutdown handback.
/// </summary>
/// <remarks>
/// <para>
/// The bug this file exists for: the two custom-speed sliders wrote the user's number straight to the firmware. A
/// slider left at 30 % on a 95 °C machine asked the EC for a 30 % fan, and the curve's safety floor — which the curve
/// path already applied — was bypassed entirely because the manual path never went through it.
/// </para>
/// <para>
/// The second half is the shutdown. The handback used to save the settings first and write Auto second, so a refused
/// write left the file saying the curve was off while the fans were still pinned. And the session-end path called
/// <c>GetAwaiter().GetResult()</c> on the UI thread, which blocks the thread the continuation needs.
/// </para>
/// </remarks>
public class FanSafetyTests
{
    private static readonly DateTime T0 = new(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc);

    // ---------------------------------------------------------------- HW-01 the manual floor

    [Theory]
    [InlineData(30, 95, 100)]   // the 95 °C band is full speed, whatever the slider says
    [InlineData(30, 90, 80)]    // the 90 °C band
    [InlineData(30, 85, 60)]    // the 85 °C band
    [InlineData(30, 84, 30)]    // below the first band the slider is the only answer
    [InlineData(70, 95, 100)]   // the floor raises, it never lowers
    [InlineData(100, 40, 100)]  // a slider already at full speed is left alone
    public void TheManualSliderIsRaisedToTheThermalFloor(int target, int temperatureC, int expected)
    {
        var floor = new ManualFanFloor();

        Assert.Equal(expected, floor.EffectivePercent(target, temperatureC));
    }

    [Fact]
    public void TheFloorIsATemporaryOverrideAndTheUserTargetComesBack()
    {
        var floor = new ManualFanFloor();

        // The user set 30 % on a cool machine and the fans are there.
        Assert.Equal(30, floor.Evaluate(30, 60));
        floor.Commit(30, 60);

        // The die climbs to 95 °C: the fans go to full speed without the stored target changing.
        Assert.Equal(100, floor.Evaluate(30, 95));
        floor.Commit(100, 95);

        // Still hot, nothing to write.
        Assert.Null(floor.Evaluate(30, 96));

        // The die cools well below the band: the fans return to the number the user chose.
        Assert.Equal(30, floor.Evaluate(30, 70));
        floor.Commit(30, 70);
        Assert.Equal(30, floor.AppliedPercent);
    }

    [Fact]
    public void TheFloorIsHeldUntilTheTemperatureClearlyLeavesTheBand()
    {
        var floor = new ManualFanFloor();
        floor.Commit(60, 85);

        // 84 °C is inside the 3 °C release band below 85, so the fans stay at the floor rather than dropping to the
        // slider and climbing straight back: a sensor wobbling on the threshold must not make the fans hunt.
        Assert.Null(floor.Evaluate(30, 84));
        Assert.Null(floor.Evaluate(30, 83));

        // 81 °C is clearly out of the band.
        Assert.Equal(30, floor.Evaluate(30, 81));
    }

    [Fact]
    public void ARefusedManualWriteIsProposedAgainInsteadOfBeingRemembered()
    {
        var floor = new ManualFanFloor();

        // The write is refused, so nothing is committed.
        Assert.Equal(100, floor.Evaluate(30, 95));
        Assert.Null(floor.AppliedPercent);

        // The next step proposes the same speed again rather than deciding the fans are already there.
        Assert.Equal(100, floor.Evaluate(30, 95));

        floor.Commit(100, 95);
        Assert.Equal(100, floor.AppliedPercent);
        Assert.Null(floor.Evaluate(30, 95));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void AnImpossibleReadingNeverChangesTheManualSpeed(double reading)
    {
        var floor = new ManualFanFloor();
        floor.Commit(30, 60);

        Assert.Null(floor.Evaluate(30, reading));
        Assert.Equal(30, floor.AppliedPercent);
    }

    // ---------------------------------------------------------------- HW-09 the fan loop's clock

    [Fact]
    public void TheFanLoopRunsFasterThanTheTrayTelemetryRate()
    {
        // The tray polls every five seconds. The curve has to be quicker than that or hiding the window to play a
        // game — the one case the curve exists for — would slow it to a crawl.
        Assert.InRange(FanCurveLoop.DefaultInterval.TotalMilliseconds, 1500, 2000);
        Assert.True(FanCurveLoop.DefaultInterval < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task TheFanLoopKeepsSteppingAfterAStepThrows()
    {
        int steps = 0;
        using var loop = new FanCurveLoop(() =>
        {
            int n = ++steps;
            if (n <= 2) throw new InvalidOperationException("the firmware said no");
            return Task.CompletedTask;
        }, TimeSpan.FromMilliseconds(10));

        loop.Start();

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (steps < 5 && DateTime.UtcNow < deadline) await Task.Delay(10);

        Assert.True(steps >= 5, $"the loop stopped after {steps} steps; a throwing step must not end it");
    }

    [Fact]
    public async Task StartingTheLoopTwiceDoesNotRunTwoLoops()
    {
        int steps = 0;
        using var loop = new FanCurveLoop(() =>
        {
            Interlocked.Increment(ref steps);
            return Task.CompletedTask;
        }, TimeSpan.FromMilliseconds(50));

        loop.Start();
        loop.Start();
        loop.Start();

        await Task.Delay(400);
        loop.Stop();

        // One loop at 50 ms over 400 ms is about eight steps. Three loops would be about twenty-four.
        Assert.InRange(steps, 1, 14);
    }

    [Fact]
    public void TheLoopReportsWhetherItIsRunning()
    {
        using var loop = new FanCurveLoop(() => Task.CompletedTask, TimeSpan.FromMilliseconds(50));

        Assert.False(loop.IsRunning);
        loop.Start();
        Assert.True(loop.IsRunning);
        loop.Stop();
        Assert.False(loop.IsRunning);
    }

    [Fact]
    public void ALoopThatWasDisposedDoesNotStartAgain()
    {
        var loop = new FanCurveLoop(() => Task.CompletedTask, TimeSpan.FromMilliseconds(50));
        loop.Dispose();

        loop.Start();

        Assert.False(loop.IsRunning);
    }

    // ---------------------------------------------------------------- HW-07 / HW-08 the Auto handback

    [Theory]
    [InlineData(0, 0, FanSpeedRoute.ReleaseToFirmware)]
    [InlineData(0, 60, FanSpeedRoute.Invalid)]
    [InlineData(60, 0, FanSpeedRoute.Invalid)]
    [InlineData(30, 30, FanSpeedRoute.FixedSpeed)]
    [InlineData(100, 100, FanSpeedRoute.FixedSpeed)]
    public void AZeroSpeedIsAHandbackNotAStoppedFan(int cpu, int gpu, FanSpeedRoute expected)
    {
        Assert.Equal(expected, AcerWmiExecutor.RouteFanSpeed(cpu, gpu));
    }

    [Fact]
    public void TheAutoHandbackIsTheBehaviourCallNotAZeroSpeedWrite()
    {
        // What SetFanModeAsync(FanMode.Auto) sends: both fans on the firmware's own Auto behaviour.
        ulong behaviour = AcerWmiProtocol.EncodeFanBehavior(AcerWmiProtocol.FanBitmapBoth, FanMode.Auto, FanMode.Auto);

        // What a literal 0 % would have sent, which the EC reads as a stopped fan.
        ulong stopped = AcerWmiProtocol.EncodeFanSpeed(AcerWmiProtocol.FanIdCpu, 0);

        Assert.NotEqual(stopped, behaviour);
        Assert.Equal(AcerWmiProtocol.FanBehaviorAuto, AcerWmiProtocol.FanBehaviorValue(FanMode.Auto));
        Assert.Equal(FanMode.Auto, AcerWmiProtocol.DecodeFanBehavior(behaviour).Cpu);
        Assert.Equal(FanMode.Auto, AcerWmiProtocol.DecodeFanBehavior(behaviour).Gpu);
    }

    [Fact]
    public void ZeroIsAcceptedAsAReleaseAndRejectedAsASpeed()
    {
        // The worker's own check: 0 is a legal request, 15 is not. The floor is what stops a caller asking for a
        // speed the fans cannot run at.
        Assert.True(WmiWorkerValidation.IsValidFanPercent(0));
        Assert.True(WmiWorkerValidation.IsValidFanPercent(30));
        Assert.True(WmiWorkerValidation.IsValidFanPercent(100));
        Assert.False(WmiWorkerValidation.IsValidFanPercent(15));
        Assert.False(WmiWorkerValidation.IsValidFanPercent(101));
        Assert.False(WmiWorkerValidation.IsValidFanPercent(-1));
    }

    // ---------------------------------------------------------------- the view model

    /// <summary>
    /// A backend that records every fan write and can be told to refuse them. The refusal is the interesting half:
    /// a write the firmware said no to must not be remembered as applied.
    /// </summary>
    private sealed class StubHardware : IHardwareController
    {
        public string BackendName => "Test";

        public bool IsAvailable => true;

        public HardwareCapabilities Capabilities { get; set; } = new()
        {
            Backend = "Test",
            ServiceAvailable = true,
            FanControl = true,
            OperatingModeControl = true
        };

        /// <summary>False is a firmware that refuses every fan write.</summary>
        public bool AcceptsFanWrites { get; set; } = true;

        /// <summary>False makes the first read throw, which is what a backend that is not there yet looks like.</summary>
        public bool ReadsThrow { get; set; }

        public MonitorData Sample { get; set; } = new()
        {
            CpuTemperature = 60,
            GpuTemperature = 55,
            CpuFanSpeed = 2400,
            GpuFanSpeed = 2400,
            IsFanRpmMeasured = true
        };

        public List<(FanMode Mode, int Cpu, int Gpu)> FanWrites { get; } = new();

        public List<OperatingMode> ModeWrites { get; } = new();

        public Task<bool> EnsureConnectedAsync() => Task.FromResult(true);

        public Task<HardwareCapabilities> GetCapabilitiesAsync() => Task.FromResult(Capabilities);

        public Task<MonitorData?> GetMonitorDataAsync() => Task.FromResult<MonitorData?>(Sample);

        public Task<OperatingMode?> GetOperatingModeAsync() =>
            ReadsThrow
                ? throw new InvalidOperationException("the backend is not there yet")
                : Task.FromResult<OperatingMode?>(OperatingMode.Default);

        public Task<bool> SetOperatingModeAsync(OperatingMode mode)
        {
            ModeWrites.Add(mode);
            return Task.FromResult(true);
        }

        public Task<GpuOperatingMode?> GetGpuModeAsync() => Task.FromResult<GpuOperatingMode?>(null);

        public Task<bool> SetGpuModeAsync(GpuOperatingMode mode) => Task.FromResult(true);

        public Task<int?> GetGpuModeCapabilityAsync() => Task.FromResult<int?>(null);

        public Task<FanMode?> GetFanModeAsync() => Task.FromResult<FanMode?>(FanMode.Auto);

        public Task<bool> SetFanModeAsync(FanMode mode, int cpuPercent = -1, int gpuPercent = -1)
        {
            FanWrites.Add((mode, cpuPercent, gpuPercent));
            return Task.FromResult(AcceptsFanWrites);
        }

        public Task<LightingConfig?> GetLightingConfigAsync() => Task.FromResult<LightingConfig?>(null);

        public Task<bool> SetLightingAsync(string effect, int brightness, int speed, int direction,
            string primaryColor, string[]? zoneColors) => Task.FromResult(true);

        public Task<bool> TurnOffLightingAsync() => Task.FromResult(true);

        public Task<bool?> GetBacklightTimeoutAsync() => Task.FromResult<bool?>(false);

        public Task<bool> SetBacklightTimeoutAsync(bool enable30s) => Task.FromResult(true);

        public Task<BatteryChargeMode?> GetBatteryChargeModeAsync() => Task.FromResult<BatteryChargeMode?>(null);

        public Task<bool> SetBatteryChargeModeAsync(BatteryChargeMode mode) => Task.FromResult(true);

        public Task<bool?> GetCoolBoostAsync() => Task.FromResult<bool?>(false);

        public Task<bool> SetCoolBoostAsync(bool on) => Task.FromResult(true);

        public Task<bool?> GetLcdOverdriveAsync() => Task.FromResult<bool?>(false);

        public Task<bool> SetLcdOverdriveAsync(bool on) => Task.FromResult(true);

        public Task<bool> SetWinKeyLockAsync(bool enabled) => Task.FromResult(true);

        public void Dispose()
        {
        }
    }

    private static string TempSettingsPath() =>
        Path.Combine(Path.GetTempPath(), "nitrolite-fansafety-" + Guid.NewGuid().ToString("N") + ".json");

    private static async Task<MainViewModel> ReadyAsync(
        StubHardware hardware, AppSettings? settings = null, FanStallWatchdog? fanStall = null)
    {
        var vm = new MainViewModel(
            settings ?? new AppSettings(), hardware, null, TempSettingsPath(), fanStall: fanStall);
        await vm.InitializeAsync();
        return vm;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(20);
        }

        Assert.True(condition(), "the condition did not become true within " + timeoutMs + " ms");
    }

    [Fact]
    public async Task TheManualSliderWritesTheFloorAndKeepsTheStoredTarget()
    {
        var hardware = new StubHardware();
        var settings = new AppSettings { CustomFanCpuPercent = 30, CustomFanGpuPercent = 30 };
        using var vm = await ReadyAsync(hardware, settings);

        vm.SelectedFanMode = vm.FanModes.First(f => f.Mode == FanMode.Custom);
        await WaitUntilAsync(() => hardware.FanWrites.Count > 0);

        // The die is at 95 °C, so the write is full speed even though the slider says 30 %.
        hardware.Sample = new MonitorData
        {
            CpuTemperature = 95,
            GpuTemperature = 90,
            CpuFanSpeed = 5000,
            GpuFanSpeed = 5000,
            IsFanRpmMeasured = true
        };
        await vm.RefreshTelemetryAsync();

        vm.CustomCpuPercent = 30;
        await WaitUntilAsync(() => hardware.FanWrites.Exists(w => w.Cpu == 100));

        // The stored number is the user's, not the floor's: the override is temporary.
        Assert.Equal(30, settings.CustomFanCpuPercent);
        Assert.Equal(30, vm.CustomCpuPercent);
    }

    [Fact]
    public async Task ARefusedFanWriteIsNotRememberedAsApplied()
    {
        var hardware = new StubHardware { AcceptsFanWrites = false };
        using var vm = await ReadyAsync(hardware);

        // The loop needs a temperature before it has anything to propose.
        await vm.RefreshTelemetryAsync();

        vm.SelectedFanMode = vm.FanModes.First(f => f.Mode == FanMode.Custom);
        await WaitUntilAsync(() => hardware.FanWrites.Count > 0);

        int writes = hardware.FanWrites.Count;

        // The loop keeps proposing the same speed because nothing was committed. Before the split it recorded the
        // speed it had asked for and then decided the fans were already there, so a refused write was never retried.
        await WaitUntilAsync(() => hardware.FanWrites.Count > writes, 4000);
    }

    [Fact]
    public async Task AFailedInitialReadStillLeavesTheControlsWritable()
    {
        var hardware = new StubHardware { ReadsThrow = true };
        using var vm = await ReadyAsync(hardware);

        // The first read threw. The write guard must still be off, or every control on the page would be dead for
        // the rest of the session.
        vm.SelectedFanMode = vm.FanModes.First(f => f.Mode == FanMode.Custom);

        await WaitUntilAsync(() => hardware.FanWrites.Count > 0);
    }

    [Fact]
    public async Task CleanExitHandsTheFansBackBeforeItSavesTheSettings()
    {
        var hardware = new StubHardware();
        var settings = new AppSettings { FanCurveEnabled = true };
        using var vm = await ReadyAsync(hardware, settings);

        vm.SelectedFanMode = vm.FanModes.First(f => f.Mode == FanMode.Custom);
        await WaitUntilAsync(() => hardware.FanWrites.Count > 0);

        hardware.FanWrites.Clear();
        await vm.CleanExitAsync();

        // The handback is the first thing that happened, and it is the only fan write.
        Assert.Single(hardware.FanWrites);
        Assert.Equal(FanMode.Auto, hardware.FanWrites[0].Mode);

        // Only then is the curve disarmed on disk.
        Assert.False(settings.FanCurveEnabled);
        Assert.False(vm.CurveEnabled);
        Assert.Equal(vm.Labels.CleanExitDone, vm.Status);
    }

    [Fact]
    public async Task ARefusedHandbackLeavesTheCurveArmedOnDisk()
    {
        var hardware = new StubHardware();
        var settings = new AppSettings { FanCurveEnabled = true };
        using var vm = await ReadyAsync(hardware, settings);

        vm.SelectedFanMode = vm.FanModes.First(f => f.Mode == FanMode.Custom);
        await WaitUntilAsync(() => hardware.FanWrites.Count > 0);

        // The firmware refuses the Auto write. The file must not claim the curve is off: the fans are still pinned,
        // and the next launch has to know that.
        hardware.AcceptsFanWrites = false;
        await vm.CleanExitAsync();

        Assert.True(settings.FanCurveEnabled);
        Assert.Equal(vm.Labels.CleanExitPartial, vm.Status);
    }

    [Fact]
    public async Task CleanExitCompletesWithoutBlockingOnItsOwnContinuation()
    {
        var hardware = new StubHardware();
        using var vm = await ReadyAsync(hardware);

        vm.SelectedFanMode = vm.FanModes.First(f => f.Mode == FanMode.Custom);
        await WaitUntilAsync(() => hardware.FanWrites.Count > 0);

        // The old session-end path called GetAwaiter().GetResult() on the UI thread, which blocks the thread the
        // continuation needs. A bounded wait is the whole point: this must finish, not hang.
        var exit = vm.CleanExitAsync();
        var finished = await Task.WhenAny(exit, Task.Delay(TimeSpan.FromSeconds(5)));

        Assert.Same(exit, finished);
        await exit;
    }

    [Fact]
    public async Task TheBlockingHandbackReturnsInsteadOfHanging()
    {
        var hardware = new StubHardware();
        using var vm = await ReadyAsync(hardware);

        vm.SelectedFanMode = vm.FanModes.First(f => f.Mode == FanMode.Custom);
        await WaitUntilAsync(() => hardware.FanWrites.Count > 0);

        // This is what ProcessExit and UnhandledException call. It has to come back, whatever the hardware does.
        Assert.True(vm.RestoreFansToAutoBlocking(TimeSpan.FromSeconds(2)));
        Assert.Contains(hardware.FanWrites, w => w.Mode == FanMode.Auto);
    }

    [Fact]
    public async Task AStalledFanOnAHotDieDropsTheMachineIntoTheProtectiveMode()
    {
        var hardware = new StubHardware
        {
            Sample = new MonitorData
            {
                CpuTemperature = 95,
                GpuTemperature = 60,
                CpuFanSpeed = 0,
                GpuFanSpeed = 2400,
                IsFanRpmMeasured = true
            }
        };

        // The production watchdog needs the condition to hold for ten seconds. A test cannot wait that long, so the
        // window is shortened here; the logic under test is the wiring, not the timing.
        var watchdog = new FanStallWatchdog(sustainFor: TimeSpan.Zero);
        var settings = new AppSettings { FanFailureProtection = true };
        using var vm = await ReadyAsync(hardware, settings, watchdog);

        Assert.True(vm.FanProtectionOn);

        await vm.RefreshTelemetryAsync();

        // The switch is on, so the app drops the machine to the coolest mode the firmware offers. The status line
        // then reports that write, which is why the stall message is not what is left on screen.
        await WaitUntilAsync(() => hardware.ModeWrites.Count > 0);
        Assert.Equal(OperatingMode.Eco, hardware.ModeWrites[0]);
        Assert.Contains(vm.Labels.OperatingMode, vm.Status);
    }

    [Fact]
    public async Task AStalledFanIsReportedButNotActedOnWhenTheSwitchIsOff()
    {
        var hardware = new StubHardware
        {
            Sample = new MonitorData
            {
                CpuTemperature = 95,
                GpuTemperature = 60,
                CpuFanSpeed = 0,
                GpuFanSpeed = 2400,
                IsFanRpmMeasured = true
            }
        };

        var watchdog = new FanStallWatchdog(sustainFor: TimeSpan.Zero);
        using var vm = await ReadyAsync(hardware, new AppSettings { FanFailureProtection = false }, watchdog);

        Assert.False(vm.FanProtectionOn);

        await vm.RefreshTelemetryAsync();

        // The fault is still reported: a user reading the log after a thermal shutdown deserves to know the fan had
        // stopped. Nothing is written to the hardware, because the user left the switch alone.
        Assert.Equal(vm.Labels.FanStallDetected, vm.Status);
        Assert.Empty(hardware.ModeWrites);
    }

    [Fact]
    public async Task AnUnmeasuredFanReadingIsNotJudged()
    {
        var hardware = new StubHardware
        {
            Sample = new MonitorData
            {
                CpuTemperature = 95,
                GpuTemperature = 60,
                CpuFanSpeed = 0,
                GpuFanSpeed = 0,
                IsFanRpmMeasured = false
            }
        };

        using var vm = await ReadyAsync(hardware);
        await vm.RefreshTelemetryAsync();

        // The fallback path reports 0 RPM because nothing measured the fans. Judging that would fire on every
        // machine without the Acer service.
        Assert.False(vm.FanRpmMeasured);
        Assert.DoesNotContain(vm.Labels.FanStallDetected, vm.Status);
    }
}
