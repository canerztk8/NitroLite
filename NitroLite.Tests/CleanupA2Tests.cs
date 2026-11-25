using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NitroLite.Core;
using NitroLite.Core.Hardware;
using NitroLite.Desktop.ViewModels;
using Xunit;
using static NitroLite.Tests.CleanupA1Tests;

namespace NitroLite.Tests;

/// <summary>
/// Regression tests for clean-up group A, round 2: the reviewer's findings on round 1 (the crash record, the observed
/// mode, the unconfirmed curve, the GPU sentinels) and the wiring other groups needed (capability refresh, the Linux
/// permission card, static-only lighting, the predator_v4 undo, the second user session).
/// </summary>
/// <remarks>
/// Hardware goes to <see cref="CleanupA1Tests.A1Hardware"/>; nothing here reaches WMI, the EC, sysfs or a service.
/// </remarks>
public class CleanupA2Tests
{
    // ---------------------------------------------------------------- R1. a capability read that throws

    [Fact]
    public async Task ACapabilityReadThatThrowsKeepsTheCrashRecord()
    {
        // The defaults of an unread capability set are all "no". They used to be taken for "this machine has no such
        // control", the parts were struck off and the record deleted, with the machine still in the game's Turbo.
        var hardware = new A1Hardware { CapabilitiesThrow = true };
        var settings = Crashed();

        using var vm = new MainViewModel(settings, hardware, null, TempSettingsPath());
        await vm.InitializeAsync();

        Assert.Equal(0, hardware.ModeWriteCount);
        Assert.NotNull(settings.GameSessionRecovery);
        Assert.True(settings.GameSessionRecovery!.RestoreMode);
        Assert.True(settings.GameSessionRecovery.RestoreFan);
    }

    // ---------------------------------------------------------------- R2. a kept record does not outlive the user's choice

    [Fact]
    public async Task AModeTheUserSetsStrikesTheKeptRecordsModeOff()
    {
        var hardware = new A1Hardware { ModeWriteResult = false };
        var settings = Crashed();
        using var vm = new MainViewModel(settings, hardware, null, TempSettingsPath());
        await vm.InitializeAsync();
        Assert.True(settings.GameSessionRecovery?.RestoreMode);

        // The service is up now and the user picks Turbo themselves.
        hardware.ModeWriteResult = true;
        vm.SelectedMode = vm.Modes.First(m => m.Mode == OperatingMode.Turbo);
        await WaitUntilAsync(() => settings.GameSessionRecovery == null, 3000);

        // Nothing is left to replay the record's Quiet over the user's choice.
        Assert.Equal(OperatingMode.Turbo, hardware.ReportedMode);
        for (int i = 0; i < 30; i++) await vm.RefreshTelemetryAsync();
        await Task.Delay(300);
        Assert.Equal(OperatingMode.Turbo, hardware.ReportedMode);
    }

    [Fact]
    public async Task AKeptRecordIsTriedAgainInTheSameSession()
    {
        var hardware = new A1Hardware { ModeWriteResult = false };
        var settings = Crashed();
        using var vm = new MainViewModel(settings, hardware, null, TempSettingsPath());
        await vm.InitializeAsync();
        Assert.NotNull(settings.GameSessionRecovery);

        // A minute later the service answers.
        hardware.ModeWriteResult = true;
        for (int i = 0; i < 30; i++) await vm.RefreshTelemetryAsync();

        await WaitUntilAsync(() => settings.GameSessionRecovery == null, 3000);
        Assert.Equal(OperatingMode.Quiet, hardware.ReportedMode);
        Assert.Equal(vm.Labels.GameSessionRecovered, vm.Status);
    }

    [Fact]
    public async Task AnUntriedRecordIsWrittenHoweverOldItIs()
    {
        // A laptop that died mid-game and is opened eight days later still has the game's Turbo to undo. An age rule
        // measured from the game's start used to drop this record without a single write.
        var hardware = new A1Hardware();
        var settings = Crashed();
        settings.GameSessionRecovery!.Timestamp = DateTime.UtcNow.AddDays(-8);

        using var vm = new MainViewModel(settings, hardware, null, TempSettingsPath());
        await vm.InitializeAsync();

        Assert.True(hardware.ModeWriteCount >= 1);
        Assert.Equal(OperatingMode.Quiet, hardware.ReportedMode);
        Assert.Null(settings.GameSessionRecovery);
    }

    // ---------------------------------------------------------------- R1 on a platform whose "no" is conclusive

    [Fact]
    public async Task AConclusivePlatformStillKeepsTheRecordWhileTheCapabilitiesAreUnread()
    {
        var hardware = new A1Hardware { CapabilitiesThrow = true };
        var settings = Crashed();

        using var vm = new MainViewModel(settings, hardware, new FakeSnapshot(), TempSettingsPath());
        await vm.InitializeAsync();

        Assert.Equal(0, hardware.ModeWriteCount);
        Assert.NotNull(settings.GameSessionRecovery);
        Assert.True(settings.GameSessionRecovery!.RestoreMode);
        Assert.True(settings.GameSessionRecovery.RestoreFan);
    }

    [Fact]
    public async Task AConclusiveNoAfterAReadDropsThoseParts()
    {
        // The capabilities were read and the platform says, conclusively, that neither control exists: there is
        // nothing a game session could have written there, so those parts are struck off without a write.
        var hardware = new A1Hardware
        {
            Capabilities = new HardwareCapabilities { Backend = "Test", ServiceAvailable = true }
        };
        var settings = Crashed();

        using var vm = new MainViewModel(settings, hardware, new FakeSnapshot(), TempSettingsPath());
        await vm.InitializeAsync();

        Assert.Equal(0, hardware.ModeWriteCount);
        Assert.Empty(hardware.FanWritesSnapshot());
        Assert.Null(settings.GameSessionRecovery);
    }

    [Fact]
    public async Task ARetryThatLandsMovesTheCardsToTheRestoredState()
    {
        // The retry restores the record's Quiet behind the cards; the cards are read back and follow the machine.
        var hardware = new A1Hardware { ModeWriteResult = false, Sample = Hot(60) };
        var settings = Crashed();
        using var vm = new MainViewModel(settings, hardware, null, TempSettingsPath());
        await vm.InitializeAsync();
        Assert.NotNull(settings.GameSessionRecovery);

        hardware.ModeWriteResult = true;
        for (int i = 0; i < 30; i++) await vm.RefreshTelemetryAsync();

        await WaitUntilAsync(() => settings.GameSessionRecovery == null, 3000);
        await WaitUntilAsync(() => vm.SelectedMode?.Mode == OperatingMode.Quiet, 3000);
        Assert.Equal(FanMode.Auto, vm.SelectedFanMode?.Mode);
    }

    // ---------------------------------------------------------------- R4. the curve on an unconfirmed layout

    [Fact]
    public async Task AnArmedCurveOnAnUnconfirmedLayoutStillGetsTheFloor()
    {
        // The step used to return before any floor, so a 30 % slider stayed at 30 % at 95 °C.
        var clock = new FakeClock();
        var cool = Hot(60);
        cool.IsTemperatureMeasured = false;
        var hardware = new A1Hardware { Sample = cool };
        using var vm = await ReadyAsync(hardware, clock);
        await vm.RefreshTelemetryAsync();

        vm.CustomCpuPercent = 30;
        vm.CustomGpuPercent = 30;
        vm.CurveEnabled = true;
        vm.SelectedFanMode = vm.FanModes.First(f => f.Mode == FanMode.Custom);
        await WaitUntilAsync(() => hardware.HasFanWrite(w => w.Mode == FanMode.Custom), 4000);

        // Only the loop writes after this: the die heats up under the armed curve.
        var hot = Hot(95);
        hot.IsTemperatureMeasured = false;
        hardware.Sample = hot;
        await vm.RefreshTelemetryAsync();

        await WaitUntilAsync(() => hardware.HasFanWrite(w => w.Mode == FanMode.Custom && w.Cpu == 100 && w.Gpu == 100), 6000);
    }

    [Fact]
    public async Task ApplyingTheCurveOnAnUnconfirmedLayoutWritesTheSlidersNotTheCurve()
    {
        // 42 % is the default curve at 70 °C; the sliders say 30. On a guessed layout the curve's answer is not written.
        var clock = new FakeClock();
        var sample = Hot(70);
        sample.IsTemperatureMeasured = false;
        var hardware = new A1Hardware { Sample = sample };
        using var vm = await ReadyAsync(hardware, clock);
        await vm.RefreshTelemetryAsync();

        vm.CustomCpuPercent = 30;
        vm.CustomGpuPercent = 30;
        vm.CurveEnabled = true;
        vm.ApplyCurveCommand.Execute(null);

        await WaitUntilAsync(() => hardware.HasFanWrite(w => w.Mode == FanMode.Custom && w.Cpu == 30 && w.Gpu == 30), 4000);
        await Task.Delay(1600);
        Assert.False(hardware.HasFanWrite(w => w.Mode == FanMode.Custom && w.Cpu == 42));
    }

    // ---------------------------------------------------------------- R5. GPU sentinels and unavailable readings

    [Theory]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(115)]
    [InlineData(200)]
    public async Task AnImplausibleGpuFigureIsIgnoredByTheFanDecision(int gpu)
    {
        var clock = new FakeClock();
        var sample = Hot(70);
        sample.GpuTemperature = gpu;
        var hardware = new A1Hardware { Sample = sample };
        using var vm = await ReadyAsync(hardware, clock);
        await vm.RefreshTelemetryAsync();

        vm.CurveEnabled = false;
        vm.CustomCpuPercent = 30;
        vm.CustomGpuPercent = 30;
        vm.SelectedFanMode = vm.FanModes.First(f => f.Mode == FanMode.Custom);

        await WaitUntilAsync(() => hardware.CountFanWrites(w => w.Mode == FanMode.Custom) >= 2, 6000);
        Assert.False(hardware.HasFanWrite(w => w.Mode == FanMode.Custom && (w.Cpu > 30 || w.Gpu > 30)));
    }

    [Fact]
    public async Task AGpuFigureMarkedUnavailableIsIgnoredByTheFanDecision()
    {
        var clock = new FakeClock();
        var sample = Hot(70);
        sample.GpuTemperature = 92;
        sample.Provenance["gpu-temp"] = "unavailable";
        var hardware = new A1Hardware { Sample = sample };
        using var vm = await ReadyAsync(hardware, clock);
        await vm.RefreshTelemetryAsync();

        vm.CurveEnabled = false;
        vm.CustomCpuPercent = 30;
        vm.CustomGpuPercent = 30;
        vm.SelectedFanMode = vm.FanModes.First(f => f.Mode == FanMode.Custom);

        await WaitUntilAsync(() => hardware.CountFanWrites(w => w.Mode == FanMode.Custom) >= 2, 6000);
        Assert.False(hardware.HasFanWrite(w => w.Mode == FanMode.Custom && (w.Cpu > 30 || w.Gpu > 30)));
    }

    // ---------------------------------------------------------------- R8. exit during a game

    [Fact]
    public async Task AnExitDuringAGameWithAMaxRuleHandsTheFansBack()
    {
        // Max is left alone on exit only when it is the user's own choice; a game's Max is NitroLite's to undo.
        var hardware = new A1Hardware();
        var settings = new AppSettings
        {
            AutoGameModeEnabled = true,
            GameProfiles = new List<GameProfile>
            {
                new() { ExeName = "a2max.exe", DisplayName = "A2 Max", TargetMode = OperatingMode.Default, TargetFanMode = FanMode.Max }
            }
        };

        var scanner = new FakeScanner();
        var service = new GameProfileService(scanner, settings, hardware);
        using var vm = new MainViewModel(settings, hardware, null, TempSettingsPath(), gameProfiles: service);
        await vm.InitializeAsync();

        scanner.Processes.Add(new RunningProcess(4301, "a2max.exe", @"C:\Games\a2max.exe"));
        await vm.PollGameAsync();
        Assert.True(vm.GameActive);

        await vm.CleanExitAsync();

        Assert.Equal(FanMode.Auto, hardware.FanWritesSnapshot()[^1].Mode);
    }

    // ---------------------------------------------------------------- W2/W3. capabilities that change after start-up

    [Fact]
    public async Task ARescanThatFindsTheBackendMakesTheWindowUsable()
    {
        // After "Grant access" the backend answers, but the "no Acer node" banner and the read-only state used to stay
        // until a restart.
        var hardware = new A1Hardware { Available = false };
        var rescan = new FakeRescan(() => hardware.Available = true);
        using var vm = new MainViewModel(new AppSettings(), hardware, null, TempSettingsPath(), rescan: rescan);
        await vm.InitializeAsync();
        Assert.False(vm.IsReady);

        vm.RescanCommand.Execute(null);
        await WaitUntilAsync(() => vm.IsReady, 3000);

        Assert.Equal(1, rescan.Resets);
        Assert.True(vm.HasBackend);
        Assert.NotEqual(vm.Labels.NoAcerNodes, vm.Status);
    }

    [Fact]
    public async Task ABackendCapabilityChangeRefreshesWithoutResettingTheCounters()
    {
        // The event is raised by the reset itself; resetting again from its handler would loop.
        var hardware = new A1Hardware { Available = false };
        var rescan = new FakeRescan(() => { });
        using var vm = new MainViewModel(new AppSettings(), hardware, null, TempSettingsPath(), rescan: rescan);
        await vm.InitializeAsync();

        hardware.Available = true;
        await vm.OnBackendCapabilitiesChangedAsync();

        Assert.True(vm.IsReady);
        Assert.Equal(0, rescan.Resets);
    }

    // ---------------------------------------------------------------- W4. the Linux permission card

    [Fact]
    public async Task AHelperRefusalShowsAReasonInsteadOfTheGrantButton()
    {
        var setup = new FakeSetup();
        var snapshot = new FakeSnapshot { NeedsUdevRule = true };
        var denial = PermissionDenialReason.HelperNotAuthorized;
        using var vm = new MainViewModel(new AppSettings(), new A1Hardware(), snapshot, TempSettingsPath(),
            platformSetup: setup)
        {
            PlatformExtras = new PlatformExtras { PermissionDenial = () => denial }
        };
        await vm.InitializeAsync();

        Assert.False(vm.ShowGrantAccess);
        Assert.Equal(vm.Labels.LinuxHelperNotAuthorized, vm.LinuxPermissionNote);

        // The udev rule is what is missing: the button is back and the note goes.
        denial = PermissionDenialReason.UdevRuleMissing;
        Assert.True(vm.ShowGrantAccess);
        Assert.False(vm.HasLinuxPermissionNote);
    }

    [Fact]
    public async Task ANitroKeyStillUnreadableAfterTheGrantGetsAnExplanation()
    {
        var setup = new FakeSetup { NeedsInputGroup = true };
        using var vm = new MainViewModel(new AppSettings(), new A1Hardware(), new FakeSnapshot(), TempSettingsPath(),
            platformSetup: setup)
        {
            PlatformExtras = new PlatformExtras { AccessGrantApplied = () => true }
        };
        await vm.InitializeAsync();

        Assert.False(vm.ShowGrantAccess);
        Assert.Equal(vm.Labels.LinuxNitroKeyAfterGrant, vm.LinuxPermissionNote);
    }

    // ---------------------------------------------------------------- W5. a lighting path without effects

    [Fact]
    public async Task APathWithoutEffectsOffersOnlyTheFixedColourOnes()
    {
        using var vm = new MainViewModel(new AppSettings(), new A1Hardware(), null, TempSettingsPath())
        {
            PlatformExtras = new PlatformExtras { SupportsLightingEffects = () => false }
        };
        await vm.InitializeAsync();

        Assert.Equal(new[] { "STATIC", "OFF" }, vm.Effects.Select(e => e.Id).ToArray());
    }

    // ---------------------------------------------------------------- W6. predator_v4, undone

    [Fact]
    public async Task ThePredatorV4UndoIsOfferedAndRuns()
    {
        int calls = 0;
        using var vm = new MainViewModel(new AppSettings(), new A1Hardware(), null, TempSettingsPath())
        {
            PlatformExtras = new PlatformExtras
            {
                PredatorV4Installed = () => calls == 0,
                DisablePredatorV4 = () =>
                {
                    calls++;
                    return Task.FromResult(new PlatformSetupResult(true, "off"));
                }
            }
        };
        await vm.InitializeAsync();
        Assert.True(vm.ShowPredatorV4Undo);

        vm.DisablePredatorV4Command.Execute(null);
        await WaitUntilAsync(() => calls == 1);
        await WaitUntilAsync(() => !vm.ShowPredatorV4Undo);
    }

    // ---------------------------------------------------------------- W8. a second user session

    [Fact]
    public async Task ACopyInASecondSessionWritesNothing()
    {
        var hardware = new A1Hardware { Sample = Hot(60) };
        using var vm = new MainViewModel(new AppSettings(), hardware, null, TempSettingsPath())
        {
            HardwareWritesAllowed = false
        };
        await vm.InitializeAsync();
        Assert.Equal(vm.Labels.SecondSessionReadOnly, vm.Status);
        await vm.RefreshTelemetryAsync();

        vm.SelectedFanMode = vm.FanModes.First(f => f.Mode == FanMode.Custom);
        vm.SelectedMode = vm.Modes.First(m => m.Mode == OperatingMode.Quiet);
        await Task.Delay(2000);

        Assert.Empty(hardware.FanWritesSnapshot());
        Assert.Equal(0, hardware.ModeWriteCount);
        Assert.False(vm.FanCurveRunning);

        // Nothing of this copy's to hand back on exit either.
        await vm.CleanExitAsync();
        Assert.Empty(hardware.FanWritesSnapshot());
    }

    // ---------------------------------------------------------------- helpers

    private static AppSettings Crashed() => new()
    {
        GameSessionRecovery = new GameSessionRecoveryState
        {
            IsActive = true,
            SavedMode = OperatingMode.Quiet,
            SavedFan = FanMode.Auto,
            Timestamp = DateTime.UtcNow
        }
    };

    private static async Task<MainViewModel> ReadyAsync(A1Hardware hardware, FakeClock clock)
    {
        var vm = new MainViewModel(new AppSettings(), hardware, null, TempSettingsPath()) { FanClock = clock.Now };
        await vm.InitializeAsync();
        return vm;
    }

    private sealed class FakeScanner : IProcessScanner
    {
        public List<RunningProcess> Processes { get; } = new();

        public IReadOnlyList<RunningProcess> Scan() => Processes.ToList();
    }

    private sealed class FakeSnapshot : IPlatformSnapshot
    {
        public DeviceIdentity Identity { get; } = new();

        public string Tier => "test";

        public bool NeedsUdevRule { get; init; }

        public IReadOnlyList<string> Notes { get; } = Array.Empty<string>();

        public bool NegativeAnswersAreConclusive => true;

        public string BuildHardwareReport(HardwareReportContext? context = null) => "";
    }

    private sealed class FakeRescan : IRescannableBackend
    {
        private readonly Action _onReset;

        public FakeRescan(Action onReset) => _onReset = onReset;

        public int Resets { get; private set; }

        public IPlatformSnapshot Snapshot { get; } = new FakeSnapshot();

        public bool PermissionHintRaised => false;

        public void ResetReadBackCounters()
        {
            Resets++;
            _onReset();
        }
    }

    private sealed class FakeSetup : IPlatformSetup
    {
        public bool CanElevate => true;

        public bool NeedsInputGroup { get; init; }

        public KernelSetupHint KernelHint => default;

        public string? ModelNote => null;

        public string CaptureSummary => "";

        public Task<PlatformSetupResult> GrantAccessAsync() => Task.FromResult(new PlatformSetupResult(true, "ok"));

        public Task<PlatformSetupResult> EnablePredatorV4Async() => Task.FromResult(new PlatformSetupResult(true, "ok"));

        public Task<string?> WriteCaptureAsync(string directory) => Task.FromResult<string?>(null);
    }
}
