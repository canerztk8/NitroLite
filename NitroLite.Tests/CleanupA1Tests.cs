using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NitroLite.Core;
using NitroLite.Core.Hardware;
using NitroLite.Desktop.ViewModels;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// Regression tests for clean-up group A, round 1: the fan loop's stale, missing and GPU readings, a fixed speed the
/// firmware keeps refusing, the crash recovery's honesty, the exit that only hands back what NitroLite set, the start-up
/// order, the resume, and the mode read-back.
/// </summary>
/// <remarks>
/// Every hardware call goes to <see cref="A1Hardware"/>; nothing here reaches WMI, the EC or a service. Time-based
/// checks run on <see cref="FakeClock"/> through <see cref="MainViewModel.FanClock"/>, advanced in small steps so the
/// loop never mistakes the test's fast-forward for a sleep.
/// </remarks>
public class CleanupA1Tests
{
    // ---------------------------------------------------------------- 1. a reading that stops updating

    [Fact]
    public async Task AReadingThatStopsUpdatingHandsTheFansBack()
    {
        // Telemetry stops after one 50 °C sample. The loop used to keep that value for good, so the fixed speed stayed
        // with no floor while the die could be at 95 °C.
        var clock = new FakeClock();
        var hardware = new A1Hardware { Sample = Hot(50) };
        using var vm = await ReadyAsync(hardware, clock);
        await vm.RefreshTelemetryAsync();

        vm.CurveEnabled = false;
        vm.CustomCpuPercent = 35;
        vm.CustomGpuPercent = 35;
        vm.SelectedFanMode = vm.FanModes.First(f => f.Mode == FanMode.Custom);
        await WaitUntilAsync(() => hardware.HasFanWrite(w => w.Mode == FanMode.Custom), 4000);
        Assert.False(hardware.HasFanWrite(w => w.Mode == FanMode.Auto));

        await AdvanceUntilAsync(clock, () => hardware.HasFanWrite(w => w.Mode == FanMode.Auto), 25000);
        await WaitUntilAsync(() => vm.SelectedFanMode?.Mode == FanMode.Auto, 2000);
        Assert.Equal(vm.Labels.FanTemperatureLostHandedBack, vm.Status);
    }

    [Fact]
    public async Task AReadingThatKeepsUpdatingKeepsTheFixedSpeed()
    {
        // The other half: the same time passes with telemetry still ticking, and nothing is handed back.
        var clock = new FakeClock();
        var hardware = new A1Hardware { Sample = Hot(50) };
        using var vm = await ReadyAsync(hardware, clock);
        await vm.RefreshTelemetryAsync();

        vm.CurveEnabled = false;
        vm.SelectedFanMode = vm.FanModes.First(f => f.Mode == FanMode.Custom);
        await WaitUntilAsync(() => hardware.HasFanWrite(w => w.Mode == FanMode.Custom), 4000);

        for (int i = 0; i < 20; i++)
        {
            clock.Advance(2000);
            await vm.RefreshTelemetryAsync();
            await Task.Delay(200);
        }

        Assert.False(hardware.HasFanWrite(w => w.Mode == FanMode.Auto));
        Assert.Equal(FanMode.Custom, vm.SelectedFanMode?.Mode);
    }

    // ---------------------------------------------------------------- 2. the GPU temperature

    [Fact]
    public async Task AHotGpuRaisesBothFansToTheFloor()
    {
        // A GPU-bound game: CPU 70 °C, GPU 92 °C, a fixed 30 %. The floor used to read the CPU alone and never fired.
        var clock = new FakeClock();
        var sample = Hot(70);
        sample.GpuTemperature = 92;
        var hardware = new A1Hardware { Sample = sample };
        using var vm = await ReadyAsync(hardware, clock);
        await vm.RefreshTelemetryAsync();

        vm.CurveEnabled = false;
        vm.CustomCpuPercent = 30;
        vm.CustomGpuPercent = 30;
        vm.SelectedFanMode = vm.FanModes.First(f => f.Mode == FanMode.Custom);

        await WaitUntilAsync(() => hardware.HasFanWrite(w => w.Mode == FanMode.Custom && w.Cpu >= 80 && w.Gpu >= 80), 5000);

        // The page still shows each die's own figure.
        Assert.Equal(70, vm.CpuTemperature);
        Assert.Equal(92, vm.GpuTemperature);
    }

    [Fact]
    public async Task ASleepingGpuThatReadsZeroLeavesTheCpuDecision()
    {
        var clock = new FakeClock();
        var sample = Hot(70);
        sample.GpuTemperature = 0;
        var hardware = new A1Hardware { Sample = sample };
        using var vm = await ReadyAsync(hardware, clock);
        await vm.RefreshTelemetryAsync();

        vm.CurveEnabled = false;
        vm.CustomCpuPercent = 30;
        vm.CustomGpuPercent = 30;
        vm.SelectedFanMode = vm.FanModes.First(f => f.Mode == FanMode.Custom);

        await WaitUntilAsync(() => hardware.CountFanWrites(w => w.Mode == FanMode.Custom) >= 2, 6000);
        await Task.Delay(1600);

        Assert.True(hardware.HasFanWrite(w => w.Mode == FanMode.Custom && w.Cpu == 30 && w.Gpu == 30));
        Assert.False(hardware.HasFanWrite(w => w.Mode == FanMode.Custom && (w.Cpu > 30 || w.Gpu > 30)));
        Assert.False(hardware.HasFanWrite(w => w.Mode == FanMode.Auto));
    }

    // ---------------------------------------------------------------- 3. no reading at all

    [Fact]
    public async Task CustomWithNoReadingEverIsHandedBackAfterTheGrace()
    {
        // A machine that answers 0 with no fallback flag: no reading was ever seen, so the hand-back never fired and a
        // fixed speed ran with no floor for the whole session.
        var clock = new FakeClock();
        var hardware = new A1Hardware
        {
            Sample = new MonitorData { CpuTemperature = 0, GpuTemperature = 0, IsFanRpmMeasured = false }
        };
        using var vm = await ReadyAsync(hardware, clock);
        await vm.RefreshTelemetryAsync();

        vm.SelectedFanMode = vm.FanModes.First(f => f.Mode == FanMode.Custom);
        await WaitUntilAsync(() => hardware.HasFanWrite(w => w.Mode == FanMode.Custom), 4000);

        // Start-up is not a lost reading: with the clock standing still the grace never runs out.
        await Task.Delay(2000);
        Assert.False(hardware.HasFanWrite(w => w.Mode == FanMode.Auto));

        await AdvanceUntilAsync(clock, () => hardware.HasFanWrite(w => w.Mode == FanMode.Auto), 25000);
        await WaitUntilAsync(() => vm.SelectedFanMode?.Mode == FanMode.Auto, 2000);
    }

    // ---------------------------------------------------------------- 4. a fixed speed the firmware keeps refusing

    [Fact]
    public async Task AFixedSpeedRefusedThreeTimesMovesTheCardToAuto()
    {
        var clock = new FakeClock();
        var hardware = new A1Hardware { Sample = Hot(60), AcceptCustom = false };
        using var vm = await ReadyAsync(hardware, clock);
        await vm.RefreshTelemetryAsync();

        vm.CurveEnabled = false;
        vm.SelectedFanMode = vm.FanModes.First(f => f.Mode == FanMode.Custom);

        await WaitUntilAsync(() => hardware.HasFanWrite(w => w.Mode == FanMode.Auto), 12000);
        await WaitUntilAsync(() => vm.SelectedFanMode?.Mode == FanMode.Auto, 2000);
        Assert.Equal(vm.Labels.FanSpeedRefusedHandedBack, vm.Status);

        // The loop stopped with the card: no more refused writes pile up.
        int custom = hardware.CountFanWrites(w => w.Mode == FanMode.Custom);
        await Task.Delay(2000);
        Assert.Equal(custom, hardware.CountFanWrites(w => w.Mode == FanMode.Custom));
        Assert.False(vm.FanCurveRunning);
    }

    // ---------------------------------------------------------------- 5. the crash recovery

    [Fact]
    public async Task ARefusedRecoveryModeWriteKeepsTheRecord()
    {
        var hardware = new A1Hardware { ModeWriteResult = false };
        var settings = new AppSettings
        {
            GameSessionRecovery = new GameSessionRecoveryState
            {
                IsActive = true,
                SavedMode = OperatingMode.Default,
                SavedFan = FanMode.Auto
            }
        };

        using var vm = new MainViewModel(settings, hardware, null, TempSettingsPath());
        await vm.InitializeAsync();

        // The mode write was sent, after the backend was connected, and refused: the record stays for the next launch
        // with the fan struck off, because that write did land.
        Assert.True(hardware.ModeWriteCount >= 1);
        Assert.Equal(0, hardware.ModeWritesBeforeConnect);
        var kept = settings.GameSessionRecovery;
        Assert.NotNull(kept);
        Assert.True(kept!.IsActive);
        Assert.True(kept.RestoreMode);
        Assert.False(kept.RestoreFan);
    }

    [Fact]
    public async Task ARecoveryWithNoBackendUpYetKeepsTheRecord()
    {
        var hardware = new A1Hardware { Available = false };
        var settings = new AppSettings
        {
            GameSessionRecovery = new GameSessionRecoveryState { IsActive = true, SavedMode = OperatingMode.Default }
        };

        using var vm = new MainViewModel(settings, hardware, null, TempSettingsPath());
        await vm.InitializeAsync();

        Assert.Equal(0, hardware.ModeWriteCount);
        Assert.NotNull(settings.GameSessionRecovery);
        Assert.True(settings.GameSessionRecovery!.RestoreMode);
        Assert.True(settings.GameSessionRecovery.RestoreFan);
    }

    [Fact]
    public async Task ARecoveryThatLandsDeletesTheRecordAndSaysSo()
    {
        var hardware = new A1Hardware();
        var settings = new AppSettings
        {
            GameSessionRecovery = new GameSessionRecoveryState
            {
                IsActive = true,
                SavedMode = OperatingMode.Quiet,
                SavedFan = FanMode.Auto
            }
        };

        using var vm = new MainViewModel(settings, hardware, null, TempSettingsPath());
        await vm.InitializeAsync();

        Assert.Null(settings.GameSessionRecovery);
        Assert.Equal(OperatingMode.Quiet, settings.LastOperatingMode);
        Assert.Equal(0, hardware.ModeWritesBeforeConnect);
    }

    // ---------------------------------------------------------------- 6. the exit hands back only what NitroLite set

    [Fact]
    public async Task AnExitWithTheFansOnMaxLeavesThemThere()
    {
        var hardware = new A1Hardware();
        var settings = new AppSettings();
        using var vm = new MainViewModel(settings, hardware, null, TempSettingsPath());
        await vm.InitializeAsync();

        vm.SelectedFanMode = vm.FanModes.First(f => f.Mode == FanMode.Max);
        await WaitUntilAsync(() => hardware.HasFanWrite(w => w.Mode == FanMode.Max));

        await vm.CleanExitAsync();

        Assert.False(hardware.HasFanWrite(w => w.Mode == FanMode.Auto));
        Assert.Equal(FanMode.Max, vm.SelectedFanMode?.Mode);
        Assert.Equal(vm.Labels.CleanExitDone, vm.Status);
    }

    [Fact]
    public async Task AnExitWithAFixedSpeedStillHandsTheFansBack()
    {
        var hardware = new A1Hardware();
        using var vm = new MainViewModel(new AppSettings(), hardware, null, TempSettingsPath());
        await vm.InitializeAsync();

        vm.SelectedFanMode = vm.FanModes.First(f => f.Mode == FanMode.Custom);
        await WaitUntilAsync(() => hardware.HasFanWrite(w => w.Mode == FanMode.Custom));

        await vm.CleanExitAsync();

        Assert.Equal(FanMode.Auto, hardware.FanWritesSnapshot()[^1].Mode);
    }

    // ---------------------------------------------------------------- 7. start-up order

    [Fact]
    public async Task TheControlsAndTheFanLoopAreLiveBeforeTheSlowFirstReadsFinish()
    {
        // The storage read never answers. It used to hold the write guard on, the fan loop off and (in the window)
        // the telemetry timer unstarted for as long as it took.
        var storage = new BlockingStorage();
        var hardware = new A1Hardware { Sample = Hot(60) };
        using var vm = new MainViewModel(new AppSettings(), hardware, null, TempSettingsPath(), storageHealth: storage);

        var initialize = vm.InitializeAsync();
        try
        {
            await vm.ControlsReady.WaitAsync(TimeSpan.FromSeconds(5));
            await storage.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(initialize.IsCompleted);

            vm.SelectedFanMode = vm.FanModes.First(f => f.Mode == FanMode.Custom);
            await WaitUntilAsync(() => hardware.HasFanWrite(w => w.Mode == FanMode.Custom));
            Assert.True(vm.FanCurveRunning);
        }
        finally
        {
            storage.Release.Set();
        }

        await initialize.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // ---------------------------------------------------------------- 9. resume

    [Fact]
    public async Task AResumeMakesTheNextStepWriteTheSameSpeedAgain()
    {
        // The firmware may reset the speed during sleep, and the loop only writes when its target changes.
        var clock = new FakeClock();
        var hardware = new A1Hardware { Sample = Hot(60) };
        using var vm = await ReadyAsync(hardware, clock);
        await vm.RefreshTelemetryAsync();

        vm.CurveEnabled = false;
        vm.CustomCpuPercent = 50;
        vm.CustomGpuPercent = 50;
        vm.SelectedFanMode = vm.FanModes.First(f => f.Mode == FanMode.Custom);
        await WaitUntilAsync(() => hardware.CountFanWrites(w => w.Mode == FanMode.Custom) >= 2, 6000);

        // Settled: the loop has committed the speed and proposes nothing more.
        await Task.Delay(1800);
        int settled = hardware.CountFanWrites(w => w.Mode == FanMode.Custom);
        await Task.Delay(1800);
        Assert.Equal(settled, hardware.CountFanWrites(w => w.Mode == FanMode.Custom));

        int settledFifty = hardware.CountFanWrites(w => w.Mode == FanMode.Custom && w.Cpu == 50 && w.Gpu == 50);
        _ = vm.OnSystemResumedAsync();

        await WaitUntilAsync(
            () => hardware.CountFanWrites(w => w.Mode == FanMode.Custom && w.Cpu == 50 && w.Gpu == 50) > settledFifty, 4000);
    }

    [Fact]
    public async Task ASleepDoesNotHandTheFansBackOverAnOldReading()
    {
        // Every reading is old after a sleep because nothing ran. The loop sees the gap and restarts the age count;
        // the reading still goes stale if telemetry does not come back.
        var clock = new FakeClock();
        var hardware = new A1Hardware { Sample = Hot(60) };
        using var vm = await ReadyAsync(hardware, clock);
        await vm.RefreshTelemetryAsync();

        vm.CurveEnabled = false;
        vm.SelectedFanMode = vm.FanModes.First(f => f.Mode == FanMode.Custom);
        await WaitUntilAsync(() => hardware.HasFanWrite(w => w.Mode == FanMode.Custom), 4000);

        clock.Advance(60_000);
        await Task.Delay(3200);
        Assert.False(hardware.HasFanWrite(w => w.Mode == FanMode.Auto));
        Assert.Equal(FanMode.Custom, vm.SelectedFanMode?.Mode);

        await AdvanceUntilAsync(clock, () => hardware.HasFanWrite(w => w.Mode == FanMode.Auto), 25000);
    }

    // ---------------------------------------------------------------- 10. the mode read-back

    [Fact]
    public async Task AModeChangedOutsideTheAppIsReflectedWithoutAWrite()
    {
        var hardware = new A1Hardware { ReportedMode = OperatingMode.Quiet };
        var settings = new AppSettings();
        using var vm = new MainViewModel(settings, hardware, null, TempSettingsPath());
        await vm.InitializeAsync();
        Assert.Equal(OperatingMode.Quiet, vm.SelectedMode?.Mode);

        // NitroSense or the mode key moved the machine to Balanced.
        hardware.ReportedMode = OperatingMode.Default;
        for (int i = 0; i < 10; i++) await vm.RefreshTelemetryAsync();

        await WaitUntilAsync(() => vm.SelectedMode?.Mode == OperatingMode.Default, 3000);
        Assert.Equal(0, hardware.ModeWriteCount);

        // Shown, not stored: LastOperatingMode is the user's choice, and an observation is not one.
        Assert.NotEqual(OperatingMode.Default, settings.LastOperatingMode);
    }

    // ---------------------------------------------------------------- helpers

    internal static MonitorData Hot(int cpuC) => new()
    {
        CpuTemperature = cpuC,
        GpuTemperature = 60,
        CpuFanSpeed = 2400,
        GpuFanSpeed = 2400,
        IsFanRpmMeasured = true,
        IsTemperatureMeasured = true
    };

    internal static string TempSettingsPath() =>
        Path.Combine(Path.GetTempPath(), "nitrolite-a1-" + Guid.NewGuid().ToString("N") + ".json");

    private static async Task<MainViewModel> ReadyAsync(A1Hardware hardware, FakeClock clock)
    {
        var vm = new MainViewModel(new AppSettings(), hardware, null, TempSettingsPath()) { FanClock = clock.Now };
        await vm.InitializeAsync();
        return vm;
    }

    // 10 s rather than 3 s: a shared CI runner under the full parallel suite has missed 3 s on a condition that holds
    // in under a second locally. The wait returns as soon as the condition holds, so only a real failure pays for it.
    internal static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 10000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(20);
        }

        Assert.True(condition(), "the condition did not become true within " + timeoutMs + " ms");
    }

    /// <summary>
    /// Lets fake time pass with the fan steps until the condition holds: every read of the clock (one per fan step)
    /// moves it six seconds. Tied to the steps rather than to real time, so a slow, loaded run can never put more than
    /// six fake seconds between two steps and be mistaken for a sleep (ten seconds), however late a step runs.
    /// </summary>
    internal static async Task AdvanceUntilAsync(FakeClock clock, Func<bool> condition, int timeoutMs)
    {
        clock.AutoStepMs = 6000;
        try
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                if (condition()) return;
                await Task.Delay(100);
            }

            Assert.True(condition(), "the condition did not become true within " + timeoutMs + " ms");
        }
        finally
        {
            clock.AutoStepMs = 0;
        }
    }

    internal sealed class FakeClock
    {
        private long _ms = 1_000_000;
        private long _autoStepMs;

        /// <summary>When above zero, every read moves the clock this far first.</summary>
        public long AutoStepMs
        {
            get => Interlocked.Read(ref _autoStepMs);
            set => Interlocked.Exchange(ref _autoStepMs, value);
        }

        public long Now()
        {
            long step = AutoStepMs;
            return step > 0 ? Interlocked.Add(ref _ms, step) : Interlocked.Read(ref _ms);
        }

        public void Advance(long ms) => Interlocked.Add(ref _ms, ms);
    }

    /// <summary>A storage read that does not answer until the test lets it.</summary>
    private sealed class BlockingStorage : IStorageHealthService
    {
        public ManualResetEventSlim Release { get; } = new(false);

        public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsSupported => true;

        public IReadOnlyList<StorageDrive> GetDrives()
        {
            Entered.TrySetResult(true);
            Release.Wait(TimeSpan.FromSeconds(20));
            return Array.Empty<StorageDrive>();
        }
    }

    /// <summary>A backend that records every fan and mode write and touches no hardware.</summary>
    internal sealed class A1Hardware : IHardwareController
    {
        private readonly object _lock = new();
        private readonly List<(FanMode Mode, int Cpu, int Gpu)> _fanWrites = new();
        private int _modeWrites;
        private int _modeWritesBeforeConnect;
        private volatile bool _connected;

        public string BackendName => "Test";

        public bool Available { get; set; } = true;

        public bool IsAvailable => Available;

        public HardwareCapabilities Capabilities { get; init; } = new()
        {
            Backend = "Test",
            ServiceAvailable = true,
            FanControl = true,
            OperatingModeControl = true
        };

        public MonitorData Sample { get; set; } = Hot(60);

        /// <summary>False: every Custom write is refused, like a fan capability switched off for the session.</summary>
        public bool AcceptCustom { get; set; } = true;

        public bool ModeWriteResult { get; set; } = true;

        public OperatingMode ReportedMode { get; set; } = OperatingMode.Default;

        public int ModeWriteCount
        {
            get { lock (_lock) return _modeWrites; }
        }

        /// <summary>Mode writes that arrived before <see cref="EnsureConnectedAsync"/> was called.</summary>
        public int ModeWritesBeforeConnect
        {
            get { lock (_lock) return _modeWritesBeforeConnect; }
        }

        public List<(FanMode Mode, int Cpu, int Gpu)> FanWritesSnapshot()
        {
            lock (_lock) return _fanWrites.ToList();
        }

        public bool HasFanWrite(Func<(FanMode Mode, int Cpu, int Gpu), bool> match)
        {
            lock (_lock) return _fanWrites.Any(match);
        }

        public int CountFanWrites(Func<(FanMode Mode, int Cpu, int Gpu), bool> match)
        {
            lock (_lock) return _fanWrites.Count(match);
        }

        public Task<bool> EnsureConnectedAsync()
        {
            _connected = true;
            return Task.FromResult(Available);
        }

        /// <summary>True: the capability read throws, like a backend that connected and then failed its first query.</summary>
        public bool CapabilitiesThrow { get; set; }

        public Task<HardwareCapabilities> GetCapabilitiesAsync() => CapabilitiesThrow
            ? Task.FromException<HardwareCapabilities>(new InvalidOperationException("the capability query failed"))
            : Task.FromResult(Capabilities);

        public Task<MonitorData?> GetMonitorDataAsync() => Task.FromResult<MonitorData?>(Sample);

        public Task<OperatingMode?> GetOperatingModeAsync() => Task.FromResult<OperatingMode?>(ReportedMode);

        public Task<bool> SetOperatingModeAsync(OperatingMode mode)
        {
            lock (_lock)
            {
                _modeWrites++;
                if (!_connected) _modeWritesBeforeConnect++;
            }

            if (ModeWriteResult) ReportedMode = mode;
            return Task.FromResult(ModeWriteResult);
        }

        public Task<GpuOperatingMode?> GetGpuModeAsync() => Task.FromResult<GpuOperatingMode?>(null);

        public Task<bool> SetGpuModeAsync(GpuOperatingMode mode) => Task.FromResult(true);

        public Task<int?> GetGpuModeCapabilityAsync() => Task.FromResult<int?>(null);

        public Task<FanMode?> GetFanModeAsync() => Task.FromResult<FanMode?>(FanMode.Auto);

        public Task<bool> SetFanModeAsync(FanMode mode, int cpuPercent = -1, int gpuPercent = -1)
        {
            lock (_lock) _fanWrites.Add((mode, cpuPercent, gpuPercent));
            return Task.FromResult(mode != FanMode.Custom || AcceptCustom);
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

        public Task<bool> SetWinKeyLockAsync(bool enabled) => Task.FromResult(true);

        public void Dispose()
        {
        }
    }
}
