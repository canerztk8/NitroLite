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
/// Regression tests for the G1 audit round: the fan loop during a game, the lost-reading hand-back, the CLI's fixed
/// speed, the loop's own serialisation, the backup restore that the next save undid, and the thermal-zone fallback.
/// </summary>
/// <remarks>
/// Every hardware call goes to <see cref="RecordingHardware"/>; nothing here reaches WMI, the EC or a service.
/// </remarks>
public class AuditG1Tests
{
    // ---------------------------------------------------------------- a. a game's curve is driven by the loop

    [Fact]
    public async Task AGameWithACurveRuleGetsTheCurveAndTheFloorAtNinetyFive()
    {
        // The service puts the fans in Custom at the slider's speed and leaves the curve to the window's loop. The loop
        // used to return for every running game, so the fans stayed at 30 % for the whole session at 95 °C.
        var hardware = new RecordingHardware { Sample = Hot(95) };
        var settings = new AppSettings
        {
            AutoGameModeEnabled = true,
            CustomFanCpuPercent = 30,
            CustomFanGpuPercent = 30,
            GameProfiles = new List<GameProfile>
            {
                new()
                {
                    ExeName = "g1game.exe",
                    DisplayName = "G1 Game",
                    TargetMode = OperatingMode.Default,
                    TargetFanCurveEnabled = true
                }
            }
        };

        var scanner = new FakeScanner();
        var service = new GameProfileService(scanner, settings, hardware);
        using var vm = new MainViewModel(settings, hardware, null, TempSettingsPath(), gameProfiles: service);
        await vm.InitializeAsync();
        await vm.RefreshTelemetryAsync();

        scanner.Processes.Add(new RunningProcess(4242, "g1game.exe", @"C:\Games\g1game.exe"));
        await vm.PollGameAsync();

        Assert.True(vm.GameActive);
        Assert.Equal(FanMode.Custom, vm.SelectedFanMode?.Mode);

        // The service's own write is the slider's 30 %; only the loop can produce full speed.
        await WaitUntilAsync(() => hardware.HasFanWrite(w => w.Mode == FanMode.Custom && w.Cpu == 100 && w.Gpu == 100), 4000);
    }

    [Fact]
    public async Task AGameWithAFixedMaxRuleIsLeftToTheFirmware()
    {
        // The other half: a fixed Auto or Max rule still owns the fans, so the loop writes no Custom speed over it.
        var hardware = new RecordingHardware { Sample = Hot(95) };
        var settings = new AppSettings
        {
            AutoGameModeEnabled = true,
            GameProfiles = new List<GameProfile>
            {
                new() { ExeName = "g1max.exe", DisplayName = "G1 Max", TargetMode = OperatingMode.Default, TargetFanMode = FanMode.Max }
            }
        };

        var scanner = new FakeScanner();
        var service = new GameProfileService(scanner, settings, hardware);
        using var vm = new MainViewModel(settings, hardware, null, TempSettingsPath(), gameProfiles: service);
        await vm.InitializeAsync();
        await vm.RefreshTelemetryAsync();

        scanner.Processes.Add(new RunningProcess(4243, "g1max.exe", @"C:\Games\g1max.exe"));
        await vm.PollGameAsync();

        Assert.Equal(FanMode.Max, vm.SelectedFanMode?.Mode);
        await Task.Delay(1600);

        Assert.False(hardware.HasFanWrite(w => w.Mode == FanMode.Custom));
    }

    // ---------------------------------------------------------------- b. the lost-reading hand-back is retried

    [Fact]
    public async Task AHandBackWhoseGateTimedOutIsTriedAgainOnTheNextStep()
    {
        // The "done" flag used to be set before the write, so a gate that timed out left it true and the fans were
        // never handed back: they stayed at the curve's last speed with no reading left to change them.
        var hardware = new RecordingHardware { Sample = Hot(70) };
        using var vm = await ReadyAsync(hardware);
        await vm.RefreshTelemetryAsync();
        vm.CurveEnabled = true;
        vm.SelectedFanMode = vm.FanModes.First(f => f.Mode == FanMode.Custom);

        // 42 % is the default curve at 70 °C.
        await WaitUntilAsync(() => hardware.HasFanWrite(w => w.Cpu == 42), 4000);

        // A mode write that does not return holds the write gate for longer than the hand-back waits for it.
        var block = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        hardware.ModeWriteBlock = block.Task;
        vm.SelectedMode = vm.Modes.First(m => m.Mode != vm.SelectedMode?.Mode);
        await WaitUntilAsync(() => hardware.ModeWriteCount > 0);

        hardware.Sample = new MonitorData { CpuTemperature = 0, GpuTemperature = 0, IsFanRpmMeasured = false };
        await vm.RefreshTelemetryAsync();

        // Long enough for at least one hand-back to time out on the gate.
        await Task.Delay(1800);
        Assert.False(hardware.HasFanWrite(w => w.Mode == FanMode.Auto));

        block.SetResult(true);

        await WaitUntilAsync(() => hardware.HasFanWrite(w => w.Mode == FanMode.Auto), 4000);
        await WaitUntilAsync(() => vm.SelectedFanMode?.Mode == FanMode.Auto, 2000);
    }

    [Fact]
    public async Task AHandBackThatThrewIsTriedAgainOnTheNextStep()
    {
        var hardware = new RecordingHardware { Sample = Hot(70), ThrowOnNextAuto = true };
        using var vm = await ReadyAsync(hardware);
        await vm.RefreshTelemetryAsync();
        vm.CurveEnabled = true;
        vm.SelectedFanMode = vm.FanModes.First(f => f.Mode == FanMode.Custom);
        await WaitUntilAsync(() => hardware.HasFanWrite(w => w.Cpu == 42), 4000);

        hardware.Sample = new MonitorData { CpuTemperature = 0, GpuTemperature = 0, IsFanRpmMeasured = false };
        await vm.RefreshTelemetryAsync();

        // The first Auto write throws; the second one, on a later step, is the hand-back.
        await WaitUntilAsync(() => hardware.CountFanWrites(w => w.Mode == FanMode.Auto) >= 2, 5000);
        await WaitUntilAsync(() => vm.SelectedFanMode?.Mode == FanMode.Auto, 2000);
    }

    // ---------------------------------------------------------------- c. the CLI's fixed speed goes through the floor

    [Theory]
    [InlineData(20, 95, 100)]
    [InlineData(20, 90, 80)]
    [InlineData(20, 50, 20)]
    [InlineData(70, 40, 70)]
    public void TheCliFixedSpeedIsRaisedToTheThermalFloor(int target, int temperatureC, int expected)
    {
        var sample = new MonitorData { CpuTemperature = temperatureC };

        Assert.True(CliCommands.TryResolveFixedFanSpeed(target, target, sample, out var cpu, out var gpu, out var error));
        Assert.Equal(expected, cpu);
        Assert.Equal(expected, gpu);
        Assert.Equal(string.Empty, error);
    }

    [Fact]
    public void TheCliRefusesAFixedSpeedWithoutATemperature()
    {
        // No reading, no floor: the command exits straight after the write, so a blind fixed speed would stay there.
        Assert.False(CliCommands.TryResolveFixedFanSpeed(20, 20, null, out _, out _, out var missing));
        Assert.False(string.IsNullOrWhiteSpace(missing));

        Assert.False(CliCommands.TryResolveFixedFanSpeed(20, 20, new MonitorData { CpuTemperature = 0 }, out _, out _, out var zero));
        Assert.Contains(CliCommands.FanFlag, zero);

        // A thermal-zone stand-in is not the die, so it is no reading either.
        var zone = new MonitorData { CpuTemperature = 28, IsTemperatureMeasured = false };
        zone.Provenance["cpu-temp"] = "PDH thermal zone";
        Assert.False(CliCommands.TryResolveFixedFanSpeed(20, 20, zone, out _, out _, out _));
    }

    // ---------------------------------------------------------------- d. one step at a time across stop and start

    [Fact]
    public async Task AStopFollowedByAStartNeverRunsTwoStepsAtOnce()
    {
        int running = 0;
        int maxRunning = 0;
        int calls = 0;
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task Step()
        {
            int now = Interlocked.Increment(ref running);
            InterlockedMax(ref maxRunning, now);
            try
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    firstEntered.TrySetResult(true);
                    await release.Task;
                }
            }
            finally
            {
                Interlocked.Decrement(ref running);
            }
        }

        using var loop = new FanCurveLoop(Step, TimeSpan.FromMilliseconds(30));
        loop.Start();
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));

        // Stop does not wait, so the first step is still inside its "WMI call" when the new run starts.
        loop.Stop();
        loop.Start();

        await Task.Delay(400);
        Assert.Equal(1, Volatile.Read(ref calls));
        Assert.Equal(1, Volatile.Read(ref maxRunning));

        release.SetResult(true);
        await WaitUntilAsync(() => Volatile.Read(ref calls) >= 3, 3000);

        Assert.Equal(1, Volatile.Read(ref maxRunning));
    }

    // ---------------------------------------------------------------- l. a restored backup survives this session

    [Fact]
    public async Task AWindowPlacementSaveAfterARestoreDoesNotOverwriteTheRestoredFile()
    {
        // The restore writes the settings file, but the view model keeps the copy it loaded at startup, and closing
        // the window saved that copy straight back over the restore.
        var path = TempSettingsPath();
        var backupPath = Path.Combine(Path.GetTempPath(), "nitrolite-g1-backup-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var dialogs = new Phase3Fakes.FakeDialogs { SaveResult = backupPath, PickResult = backupPath, ConfirmResult = true };
            using var vm = new MainViewModel(new AppSettings(), null, null, path, dialogs: dialogs);
            await vm.InitializeAsync();

            vm.SaveWindowPlacement(11, 12, 900, 700, false);
            Assert.Equal(11d, SettingsManager.LoadSettingsWithFallback(path).WindowLeft);

            vm.CreateBackupCommand.Execute(null);
            await WaitUntilAsync(() => File.Exists(backupPath));

            vm.RestoreBackupCommand.Execute(null);
            await WaitUntilAsync(() => vm.SettingsSavesBlocked);

            // What MainWindow.OnClosing does on the way out.
            vm.SaveWindowPlacement(777, 778, 1000, 800, true);

            var onDisk = SettingsManager.LoadSettingsWithFallback(path);
            Assert.NotEqual(777d, onDisk.WindowLeft);
            Assert.False(onDisk.WindowMaximized);
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
            try { if (File.Exists(backupPath)) File.Delete(backupPath); } catch { }
        }
    }

    // ---------------------------------------------------------------- n. the thermal-zone fallback is a lost reading

    [Fact]
    public async Task AThermalZoneFallbackHandsTheFansBackToTheFirmware()
    {
        // A confirmed layout, the curve running on the firmware's reading, then the firmware's CPU figure goes away and
        // the Windows monitor substitutes the ACPI thermal zone: a static ~28 °C board value. Following it would hold
        // the fans at the curve's idle speed while the die heats up.
        var hardware = new RecordingHardware { Sample = Hot(70) };
        using var vm = await ReadyAsync(hardware);
        await vm.RefreshTelemetryAsync();
        vm.CurveEnabled = true;
        vm.SelectedFanMode = vm.FanModes.First(f => f.Mode == FanMode.Custom);
        await WaitUntilAsync(() => hardware.HasFanWrite(w => w.Cpu == 42), 4000);

        var fallback = new MonitorData
        {
            CpuTemperature = 28,
            GpuTemperature = 60,
            CpuFanSpeed = 2400,
            GpuFanSpeed = 2400,
            IsFanRpmMeasured = true,
            IsTemperatureMeasured = false
        };
        fallback.Provenance["cpu-temp"] = "PDH thermal zone";
        hardware.Sample = fallback;
        await vm.RefreshTelemetryAsync();

        // The page still shows the number; the fans are the firmware's again.
        Assert.Equal(28, vm.CpuTemperature);
        await WaitUntilAsync(() => hardware.HasFanWrite(w => w.Mode == FanMode.Auto), 4000);
        Assert.False(hardware.HasFanWrite(w => w.Mode == FanMode.Custom && w.Cpu < 42));
    }

    // ---------------------------------------------------------------- review round: races and the fallback

    [Fact]
    public async Task ACurveStepInFlightWhenTheGameEndsDoesNotOutliveTheRestoredMode()
    {
        // The service restores the pre-game fan mode without the view model's write gate. A curve step that was already
        // inside its write landed after that restore, and the machine stayed at the game's fixed speed with the card
        // saying Auto. The view model now writes the restored mode once more, after any such step.
        var block = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hardware = new RecordingHardware { Sample = Hot(95), BlockCustomPercent = 100, CustomBlock = block.Task };
        var settings = new AppSettings
        {
            AutoGameModeEnabled = true,
            CustomFanCpuPercent = 30,
            CustomFanGpuPercent = 30,
            GameProfiles = new List<GameProfile>
            {
                new()
                {
                    ExeName = "g1race.exe",
                    DisplayName = "G1 Race",
                    TargetMode = OperatingMode.Default,
                    TargetFanCurveEnabled = true
                }
            }
        };

        var scanner = new FakeScanner();
        var service = new GameProfileService(scanner, settings, hardware);
        using var vm = new MainViewModel(settings, hardware, null, TempSettingsPath(), gameProfiles: service);
        await vm.InitializeAsync();
        await vm.RefreshTelemetryAsync();

        scanner.Processes.Add(new RunningProcess(4244, "g1race.exe", @"C:\Games\g1race.exe"));
        await vm.PollGameAsync();

        // The loop's 100 % write is now inside the gate and has not reached the EC.
        await hardware.CustomBlockEntered.Task.WaitAsync(TimeSpan.FromSeconds(4));

        scanner.Processes.Clear();
        var ending = vm.PollGameAsync();

        // The service's restore has gone out; the held write is still to land.
        await WaitUntilAsync(() => hardware.LastCompletedFanMode == FanMode.Auto);
        block.SetResult(true);

        await ending.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(vm.GameActive);
        Assert.Equal(FanMode.Auto, vm.SelectedFanMode?.Mode);
        Assert.Equal(FanMode.Auto, hardware.LastCompletedFanMode);
    }

    [Fact]
    public async Task AMachineThatOnlyEverAnswersTheThermalZoneHandsTheFansBack()
    {
        // No firmware reading ever arrives, so "a reading was seen once" never became true and the hand-back never
        // fired: a Custom selection sat at a fixed speed with no floor for the whole session.
        var zone = new MonitorData
        {
            CpuTemperature = 28,
            GpuTemperature = 50,
            CpuFanSpeed = 2400,
            GpuFanSpeed = 2400,
            IsFanRpmMeasured = true,
            IsTemperatureMeasured = false
        };
        zone.Provenance["cpu-temp"] = "PDH thermal zone";

        var hardware = new RecordingHardware { Sample = zone };
        using var vm = await ReadyAsync(hardware);
        await vm.RefreshTelemetryAsync();

        vm.SelectedFanMode = vm.FanModes.First(f => f.Mode == FanMode.Custom);

        await WaitUntilAsync(() => hardware.HasFanWrite(w => w.Mode == FanMode.Auto), 4000);
        await WaitUntilAsync(() => vm.SelectedFanMode?.Mode == FanMode.Auto, 2000);
    }

    [Fact]
    public async Task NoFixedSpeedIsWrittenWhileTheHandBackWaitsForTheUiThread()
    {
        // The hand-back's control update is posted to the UI thread. Until it runs the card still says Custom, and a
        // reading that came back meanwhile used to let the loop write the curve over the Auto it had just written.
        var hardware = new RecordingHardware { Sample = Hot(70) };
        using var vm = await ReadyAsync(hardware);

        var queued = new Queue<Action>();
        vm.UiDispatch = action =>
        {
            lock (queued) queued.Enqueue(action);
        };

        await vm.RefreshTelemetryAsync();
        vm.CurveEnabled = true;
        vm.SelectedFanMode = vm.FanModes.First(f => f.Mode == FanMode.Custom);
        await WaitUntilAsync(() => hardware.HasFanWrite(w => w.Cpu == 42), 4000);

        hardware.Sample = new MonitorData { CpuTemperature = 0, GpuTemperature = 0, IsFanRpmMeasured = false };
        await vm.RefreshTelemetryAsync();
        await WaitUntilAsync(() => hardware.HasFanWrite(w => w.Mode == FanMode.Auto), 4000);

        // The reading comes back before the UI thread has run anything.
        hardware.Sample = Hot(70);
        await vm.RefreshTelemetryAsync();
        await Task.Delay(3500);

        var writes = hardware.FanWritesSnapshot();
        int auto = writes.FindIndex(w => w.Mode == FanMode.Auto);
        Assert.DoesNotContain(writes.Skip(auto + 1), w => w.Mode == FanMode.Custom);
        Assert.Equal(FanMode.Custom, vm.SelectedFanMode?.Mode);

        // The UI thread catches up and the card follows the hand-back.
        Action[] pending;
        lock (queued) pending = queued.ToArray();
        Assert.NotEmpty(pending);
        foreach (var action in pending) action();

        Assert.Equal(FanMode.Auto, vm.SelectedFanMode?.Mode);
    }

    [Fact]
    public async Task AUserStepInFlightWhenAMaxGameStartsDoesNotOutliveTheGamesMode()
    {
        // The start-side mirror: the user's Custom write was inside the gate while the service wrote the game's Max,
        // landed after it, and the loop then stopped with the card on Max and the fans at the user's speed.
        var block = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hardware = new RecordingHardware { Sample = Hot(60), BlockCustomPercent = 50, CustomBlock = block.Task };
        var settings = new AppSettings
        {
            AutoGameModeEnabled = true,
            CustomFanCpuPercent = 50,
            CustomFanGpuPercent = 50,
            GameProfiles = new List<GameProfile>
            {
                new() { ExeName = "g1start.exe", DisplayName = "G1 Start", TargetMode = OperatingMode.Default, TargetFanMode = FanMode.Max }
            }
        };

        var scanner = new FakeScanner();
        var service = new GameProfileService(scanner, settings, hardware);
        using var vm = new MainViewModel(settings, hardware, null, TempSettingsPath(), gameProfiles: service);
        await vm.InitializeAsync();
        await vm.RefreshTelemetryAsync();

        vm.SelectedFanMode = vm.FanModes.First(f => f.Mode == FanMode.Custom);
        await hardware.CustomBlockEntered.Task.WaitAsync(TimeSpan.FromSeconds(4));

        scanner.Processes.Add(new RunningProcess(4245, "g1start.exe", @"C:\Games\g1start.exe"));
        var starting = vm.PollGameAsync();

        await WaitUntilAsync(() => hardware.LastCompletedFanMode == FanMode.Max);
        block.SetResult(true);

        await starting.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(vm.GameActive);
        Assert.Equal(FanMode.Max, vm.SelectedFanMode?.Mode);
        await Task.Delay(300);
        Assert.Equal(FanMode.Max, hardware.LastCompletedFanMode);
    }

    [Fact]
    public async Task ASessionStillClosingWhenTheExitGivesUpWritesNothingAfterTheHandBack()
    {
        // The clean exit waits five seconds for the game session, then hands the fans back regardless. The session
        // used to carry on afterwards and write the pre-game fan mode over the hand-back.
        var hardware = new RecordingHardware { Sample = Hot(60) };
        var settings = new AppSettings
        {
            AutoGameModeEnabled = true,
            GameProfiles = new List<GameProfile>
            {
                new() { ExeName = "g1exit.exe", DisplayName = "G1 Exit", TargetMode = OperatingMode.Default, TargetFanMode = FanMode.Max }
            }
        };

        var scanner = new FakeScanner();
        var service = new GameProfileService(scanner, settings, hardware);
        using var vm = new MainViewModel(settings, hardware, null, TempSettingsPath(), gameProfiles: service);
        await vm.InitializeAsync();

        // The pre-game snapshot says Custom, so a restore that ran after the hand-back would be visible as one.
        hardware.ReportedFanMode = FanMode.Custom;

        scanner.Processes.Add(new RunningProcess(4246, "g1exit.exe", @"C:\Games\g1exit.exe"));
        await vm.PollGameAsync();
        Assert.True(vm.GameActive);

        // The session's restore gets stuck on its first write, the mode.
        var block = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        hardware.ModeWriteBlock = block.Task;

        await vm.CleanExitAsync().WaitAsync(TimeSpan.FromSeconds(12));

        Assert.True(service.WritesStopped);
        Assert.Equal(FanMode.Auto, hardware.FanWritesSnapshot()[^1].Mode);

        // The stuck write finishes; the session must not go on to write its fan mode.
        block.SetResult(true);
        await Task.Delay(500);

        Assert.Equal(FanMode.Auto, hardware.FanWritesSnapshot()[^1].Mode);
        Assert.Equal(FanMode.Auto, hardware.LastCompletedFanMode);
    }

    // ---------------------------------------------------------------- helpers

    private static MonitorData Hot(int cpuC) => new()
    {
        CpuTemperature = cpuC,
        GpuTemperature = 60,
        CpuFanSpeed = 2400,
        GpuFanSpeed = 2400,
        IsFanRpmMeasured = true,
        IsTemperatureMeasured = true
    };

    private static string TempSettingsPath() =>
        Path.Combine(Path.GetTempPath(), "nitrolite-g1-" + Guid.NewGuid().ToString("N") + ".json");

    private static async Task<MainViewModel> ReadyAsync(RecordingHardware hardware)
    {
        var vm = new MainViewModel(new AppSettings(), hardware, null, TempSettingsPath());
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

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value)
        {
            if (Interlocked.CompareExchange(ref target, value, current) == current) return;
        }
    }

    private sealed class FakeScanner : IProcessScanner
    {
        public List<RunningProcess> Processes { get; } = new();

        public IReadOnlyList<RunningProcess> Scan() => Processes.ToList();
    }

    /// <summary>A backend that records every fan and mode write and touches no hardware.</summary>
    private sealed class RecordingHardware : IHardwareController
    {
        private readonly object _lock = new();
        private readonly List<(FanMode Mode, int Cpu, int Gpu)> _fanWrites = new();
        private int _modeWrites;

        public string BackendName => "Test";

        public bool IsAvailable => true;

        public HardwareCapabilities Capabilities { get; } = new()
        {
            Backend = "Test",
            ServiceAvailable = true,
            FanControl = true,
            OperatingModeControl = true
        };

        public MonitorData Sample { get; set; } = Hot(60);

        /// <summary>When set, a mode write waits for it, holding the view model's write gate meanwhile.</summary>
        public Task? ModeWriteBlock { get; set; }

        /// <summary>The next Auto write throws once, like a WMI call that failed.</summary>
        public bool ThrowOnNextAuto { get; set; }

        /// <summary>A Custom write at this CPU percentage waits for <see cref="CustomBlock"/>, once.</summary>
        public int? BlockCustomPercent { get; set; }

        public Task? CustomBlock { get; set; }

        public TaskCompletionSource<bool> CustomBlockEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private FanMode? _lastCompletedFanMode;

        /// <summary>The mode of the fan write that finished last: what the EC is holding.</summary>
        public FanMode? LastCompletedFanMode
        {
            get { lock (_lock) return _lastCompletedFanMode; }
        }

        public List<(FanMode Mode, int Cpu, int Gpu)> FanWritesSnapshot()
        {
            lock (_lock) return _fanWrites.ToList();
        }

        public int ModeWriteCount
        {
            get { lock (_lock) return _modeWrites; }
        }

        public bool HasFanWrite(Func<(FanMode Mode, int Cpu, int Gpu), bool> match)
        {
            lock (_lock) return _fanWrites.Any(match);
        }

        public int CountFanWrites(Func<(FanMode Mode, int Cpu, int Gpu), bool> match)
        {
            lock (_lock) return _fanWrites.Count(match);
        }

        public Task<bool> EnsureConnectedAsync() => Task.FromResult(true);

        public Task<HardwareCapabilities> GetCapabilitiesAsync() => Task.FromResult(Capabilities);

        public Task<MonitorData?> GetMonitorDataAsync() => Task.FromResult<MonitorData?>(Sample);

        public Task<OperatingMode?> GetOperatingModeAsync() => Task.FromResult<OperatingMode?>(OperatingMode.Default);

        public async Task<bool> SetOperatingModeAsync(OperatingMode mode)
        {
            lock (_lock) _modeWrites++;
            if (ModeWriteBlock is Task block) await block.ConfigureAwait(false);
            return true;
        }

        public Task<GpuOperatingMode?> GetGpuModeAsync() => Task.FromResult<GpuOperatingMode?>(null);

        public Task<bool> SetGpuModeAsync(GpuOperatingMode mode) => Task.FromResult(true);

        public Task<int?> GetGpuModeCapabilityAsync() => Task.FromResult<int?>(null);

        /// <summary>What a fan-mode read answers; the game service snapshots it as the pre-game mode.</summary>
        public FanMode ReportedFanMode { get; set; } = FanMode.Auto;

        public Task<FanMode?> GetFanModeAsync() => Task.FromResult<FanMode?>(ReportedFanMode);

        public async Task<bool> SetFanModeAsync(FanMode mode, int cpuPercent = -1, int gpuPercent = -1)
        {
            lock (_lock) _fanWrites.Add((mode, cpuPercent, gpuPercent));

            if (mode == FanMode.Auto && ThrowOnNextAuto)
            {
                ThrowOnNextAuto = false;
                throw new InvalidOperationException("the WMI call failed");
            }

            if (mode == FanMode.Custom && BlockCustomPercent == cpuPercent && CustomBlock is Task block)
            {
                // Once: the write that is held is the one the test is about.
                BlockCustomPercent = null;
                CustomBlockEntered.TrySetResult(true);
                await block.ConfigureAwait(false);
            }

            lock (_lock) _lastCompletedFanMode = mode;
            return true;
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
}
