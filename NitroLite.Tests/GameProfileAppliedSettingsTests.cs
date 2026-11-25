using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NitroLite.Core;
using NitroLite.Core.Hardware;
using NitroLite.Desktop.ViewModels;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// Every setting the game-profile editor offers reaches the machine: the CPU boost on start, on an edit during the game,
/// at the end and after a crash; the mode that was really written (clamped on battery, or refused); the boost a
/// hand-added game gets; and the slider speed of a fixed Custom fan rule.
/// </summary>
/// <remarks>
/// Every hardware call goes to <see cref="GameHardware"/> and every boost call to <see cref="FakeBoost"/>; nothing here
/// reaches WMI, the EC, the power scheme or sysfs.
/// </remarks>
public class GameProfileAppliedSettingsTests
{
    private const string Exe = "boostgame.exe";
    private const string Name = "Boost Game";

    // ---------------------------------------------------------------- a. the boost on start

    [Fact]
    public async Task StartWritesTheGamesBoostToTheAdapterAndTheBattery()
    {
        var (service, scanner, hardware, boost, _) = Rig(CpuBoostMode.Disabled);

        scanner.Processes.Add(Running());
        var result = await service.PollAsync();

        Assert.Equal(GameDetectionChange.Started, result.Change);
        Assert.Contains((CpuBoostMode.Disabled, true, true), boost.WritesSnapshot());
        Assert.Equal(true, result.Applied?.BoostWritten);
    }

    [Fact]
    public async Task WithSchoolModeOnTheGameWritesTheAdapterOnly()
    {
        // School mode holds the battery value at Disabled; a game that wrote both used to be able to undo it.
        var (service, scanner, _, boost, settings) = Rig(CpuBoostMode.Aggressive);
        settings.SchoolModeEnabled = true;

        scanner.Processes.Add(Running());
        await service.PollAsync();

        Assert.Equal(new[] { (CpuBoostMode.Aggressive, true, false) }, boost.WritesSnapshot());
        Assert.Equal(CpuBoostMode.Efficient, boost.Dc);
    }

    [Fact]
    public async Task UnsupportedBoostIsNeverWritten()
    {
        var (service, scanner, _, boost, _) = Rig(CpuBoostMode.Disabled);
        boost.Supported = false;

        scanner.Processes.Add(Running());
        var started = await service.PollAsync();
        await service.ReapplyActiveAsync(Exe);
        scanner.Processes.Clear();
        await service.PollAsync();

        Assert.Empty(boost.WritesSnapshot());
        Assert.Null(started.Applied?.BoostWritten);
        Assert.False(started.Applied?.IsPartial);
    }

    [Fact]
    public async Task NoBoostControlMeansNoBoostWrite()
    {
        var (service, scanner, _, _, _) = Rig(CpuBoostMode.Disabled);
        service.CpuBoost = null;

        scanner.Processes.Add(Running());
        var result = await service.PollAsync();

        Assert.Null(result.Applied?.BoostWritten);
        Assert.Null(result.Snapshot?.AcBoost);
    }

    // ---------------------------------------------------------------- a. the boost at the end

    [Fact]
    public async Task EndRestoresTheCapturedAdapterAndBatteryValuesSeparately()
    {
        var (service, scanner, _, boost, _) = Rig(CpuBoostMode.Disabled);

        scanner.Processes.Add(Running());
        await service.PollAsync();
        boost.ClearWrites();

        scanner.Processes.Clear();
        var ended = await service.PollAsync();

        Assert.Equal(GameDetectionChange.Ended, ended.Change);
        Assert.Equal(
            new[] { (CpuBoostMode.Aggressive, true, false), (CpuBoostMode.Efficient, false, true) },
            boost.WritesSnapshot());
        Assert.Equal(CpuBoostMode.Aggressive, boost.Ac);
        Assert.Equal(CpuBoostMode.Efficient, boost.Dc);
    }

    [Fact]
    public async Task AnUnknownCapturedValueIsNotRestored()
    {
        var (service, scanner, _, boost, _) = Rig(CpuBoostMode.Disabled);
        boost.Ac = null;

        scanner.Processes.Add(Running());
        await service.PollAsync();
        boost.ClearWrites();

        scanner.Processes.Clear();
        await service.PollAsync();

        Assert.Equal(new[] { (CpuBoostMode.Efficient, false, true) }, boost.WritesSnapshot());
    }

    [Fact]
    public async Task TheBatteryValueTheGameDidNotWriteIsNotRestored()
    {
        // School mode on for the whole session: only the adapter was written, so only the adapter comes back.
        var (service, scanner, _, boost, settings) = Rig(CpuBoostMode.Disabled);
        settings.SchoolModeEnabled = true;

        scanner.Processes.Add(Running());
        await service.PollAsync();
        boost.ClearWrites();

        scanner.Processes.Clear();
        await service.PollAsync();

        Assert.Equal(new[] { (CpuBoostMode.Aggressive, true, false) }, boost.WritesSnapshot());
    }

    [Fact]
    public async Task SchoolModeSwitchedOnMidGameKeepsItsBatteryValueAtTheEnd()
    {
        var (service, scanner, _, boost, settings) = Rig(CpuBoostMode.Aggressive);

        scanner.Processes.Add(Running());
        await service.PollAsync();

        // School mode takes the battery value over while the game runs.
        settings.SchoolModeEnabled = true;
        boost.Dc = CpuBoostMode.Disabled;
        boost.ClearWrites();

        scanner.Processes.Clear();
        await service.PollAsync();

        Assert.DoesNotContain(boost.WritesSnapshot(), w => w.Dc);
        Assert.Equal(CpuBoostMode.Disabled, boost.Dc);
    }

    [Fact]
    public async Task ASingleSwitchIsRestoredWithOneWrite()
    {
        // Linux: cpufreq/boost has no adapter and battery halves. Two writes of one value is noise; two different
        // values would flip it.
        var (service, scanner, _, boost, _) = Rig(CpuBoostMode.Disabled);
        boost.SingleSwitch = true;
        boost.Ac = CpuBoostMode.Enabled;
        service.CpuBoostPerPowerSource = false;

        scanner.Processes.Add(Running());
        await service.PollAsync();
        boost.ClearWrites();

        scanner.Processes.Clear();
        await service.PollAsync();

        Assert.Equal(new[] { (CpuBoostMode.Enabled, true, true) }, boost.WritesSnapshot());
    }

    [Fact]
    public async Task ASingleSwitchHeldBySchoolModeIsNotTouched()
    {
        var (service, scanner, _, boost, settings) = Rig(CpuBoostMode.Aggressive);
        boost.SingleSwitch = true;
        boost.Ac = CpuBoostMode.Disabled;
        service.CpuBoostPerPowerSource = false;
        settings.SchoolModeEnabled = true;

        scanner.Processes.Add(Running());
        var started = await service.PollAsync();
        scanner.Processes.Clear();
        await service.PollAsync();

        Assert.Empty(boost.WritesSnapshot());
        Assert.Null(started.Applied?.BoostWritten);
    }

    [Fact]
    public async Task NoBoostIsWrittenOnceWritesAreStopped()
    {
        var (service, scanner, _, boost, _) = Rig(CpuBoostMode.Disabled);

        scanner.Processes.Add(Running());
        await service.PollAsync();
        boost.ClearWrites();

        service.StopWriting();
        await service.StopAsync();

        Assert.Empty(boost.WritesSnapshot());
    }

    // ---------------------------------------------------------------- a. an edit during the game

    [Fact]
    public async Task ReapplyWritesTheEditedBoost()
    {
        var (service, scanner, _, boost, settings) = Rig(CpuBoostMode.Disabled);

        scanner.Processes.Add(Running());
        await service.PollAsync();
        boost.ClearWrites();

        settings.GameProfiles[0].TargetCpuBoost = CpuBoostMode.Efficient;
        Assert.True(await service.ReapplyActiveAsync(Exe));

        Assert.Equal(new[] { (CpuBoostMode.Efficient, true, true) }, boost.WritesSnapshot());
    }

    [Fact]
    public async Task EditingTheBoostOfTheRunningGameInTheEditorWritesItNow()
    {
        // The editor saved the choice and nothing else, so it waited for the next launch.
        var (service, scanner, hardware, boost, settings) = Rig(CpuBoostMode.Disabled);
        using var vm = new MainViewModel(settings, hardware, null, TempSettingsPath(), gameProfiles: service,
            cpuBoost: boost);
        await vm.InitializeAsync();

        scanner.Processes.Add(Running());
        await vm.PollGameAsync();
        Assert.True(vm.GameActive);
        Assert.Equal(Exe, vm.SelectedGameProfile?.ExeName);
        boost.ClearWrites();

        vm.SelectedGameCpuBoost = vm.CpuBoostChoices.First(c => c.Mode == CpuBoostMode.Efficient);

        await WaitUntilAsync(() => boost.WritesSnapshot().Contains((CpuBoostMode.Efficient, true, true)));
        Assert.Equal(CpuBoostMode.Efficient, settings.GameProfiles[0].TargetCpuBoost);
    }

    [Fact]
    public async Task TheDashboardBoostPillsFollowTheSession()
    {
        var (service, scanner, hardware, boost, settings) = Rig(CpuBoostMode.Disabled);
        using var vm = new MainViewModel(settings, hardware, null, TempSettingsPath(), gameProfiles: service,
            cpuBoost: boost);
        await vm.InitializeAsync();
        Assert.Equal(CpuBoostMode.Aggressive, vm.SelectedCpuBoost?.Mode);

        scanner.Processes.Add(Running());
        await vm.PollGameAsync();
        Assert.Equal(CpuBoostMode.Disabled, vm.SelectedCpuBoost?.Mode);

        boost.ClearWrites();
        scanner.Processes.Clear();
        await vm.PollGameAsync();

        Assert.False(vm.GameActive);
        Assert.Equal(CpuBoostMode.Aggressive, vm.SelectedCpuBoost?.Mode);

        // The pills were re-read, not re-written: only the service's two restore writes reached the scheme.
        Assert.Equal(
            new[] { (CpuBoostMode.Aggressive, true, false), (CpuBoostMode.Efficient, false, true) },
            boost.WritesSnapshot());
    }

    // ---------------------------------------------------------------- a. the crash-recovery record

    [Fact]
    public async Task TheCrashRecordCarriesTheCapturedBoost()
    {
        var (service, scanner, hardware, boost, settings) = Rig(CpuBoostMode.Disabled);
        using var vm = new MainViewModel(settings, hardware, null, TempSettingsPath(), gameProfiles: service,
            cpuBoost: boost);
        await vm.InitializeAsync();

        scanner.Processes.Add(Running());
        await vm.PollGameAsync();

        var record = settings.GameSessionRecovery;
        Assert.NotNull(record);
        Assert.Equal(CpuBoostMode.Aggressive, record!.SavedAcBoost);
        Assert.Equal(CpuBoostMode.Efficient, record.SavedDcBoost);
    }

    [Fact]
    public async Task TheNextLaunchAfterACrashPutsTheBoostBack()
    {
        var hardware = new GameHardware();
        var boost = new FakeBoost { Ac = CpuBoostMode.Disabled, Dc = CpuBoostMode.Disabled };
        var settings = new AppSettings
        {
            GameSessionRecovery = new GameSessionRecoveryState
            {
                IsActive = true,
                SavedMode = OperatingMode.Default,
                SavedFan = FanMode.Auto,
                SavedAcBoost = CpuBoostMode.Aggressive,
                SavedDcBoost = CpuBoostMode.Efficient
            }
        };

        using var vm = new MainViewModel(settings, hardware, null, TempSettingsPath(), cpuBoost: boost);
        await vm.InitializeAsync();

        Assert.Equal(
            new[] { (CpuBoostMode.Aggressive, true, false), (CpuBoostMode.Efficient, false, true) },
            boost.WritesSnapshot());
        Assert.Null(settings.GameSessionRecovery);
        Assert.Equal(CpuBoostMode.Aggressive, vm.SelectedCpuBoost?.Mode);
    }

    [Fact]
    public async Task ARecordWithoutBoostWritesNoBoost()
    {
        // A record from 1.3.5 or earlier has no boost values; the next launch must not invent any.
        var boost = new FakeBoost();
        var settings = new AppSettings
        {
            GameSessionRecovery = new GameSessionRecoveryState { IsActive = true, SavedMode = OperatingMode.Default }
        };

        using var vm = new MainViewModel(settings, new GameHardware(), null, TempSettingsPath(), cpuBoost: boost);
        await vm.InitializeAsync();

        Assert.Empty(boost.WritesSnapshot());
    }

    // ---------------------------------------------------------------- d. what was really applied

    [Fact]
    public async Task OnBatteryTheReportedModeIsTheClampedOne()
    {
        var (service, scanner, hardware, boost, settings) = Rig(CpuBoostMode.Disabled, OperatingMode.Turbo);
        service.PowerState = () => (false, 80);
        settings.LastOperatingMode = OperatingMode.Quiet;
        hardware.CurrentMode = OperatingMode.Quiet;

        using var vm = new MainViewModel(settings, hardware, null, TempSettingsPath(), gameProfiles: service,
            powerState: () => (false, 80), cpuBoost: boost);
        await vm.InitializeAsync();

        scanner.Processes.Add(Running());
        await vm.PollGameAsync();

        Assert.Equal(OperatingMode.Default, service.LastApply?.Mode);
        Assert.Contains(OperatingMode.Default, hardware.ModeWritesSnapshot());
        Assert.DoesNotContain(OperatingMode.Turbo, hardware.ModeWritesSnapshot());
        Assert.Equal(OperatingMode.Default, vm.SelectedMode?.Mode);
    }

    [Fact]
    public async Task ARefusedModeWriteIsReported()
    {
        var (service, scanner, hardware, boost, settings) = Rig(CpuBoostMode.Disabled, OperatingMode.Turbo);
        hardware.ModeWriteResult = false;

        scanner.Processes.Add(Running());
        var result = await service.PollAsync();

        Assert.NotNull(result.Applied);
        Assert.False(result.Applied!.ModeWritten);
        Assert.True(result.Applied.FanWritten);
        Assert.True(result.Applied.IsPartial);

        // The fan and the boost still went out: a refused mode no longer takes the rest down with it.
        Assert.NotEmpty(hardware.FanWritesSnapshot());
        Assert.NotEmpty(boost.WritesSnapshot());
    }

    [Fact]
    public async Task ARefusedModeWriteShowsAPartialStatusAndKeepsTheCardOnTheRealMode()
    {
        var (service, scanner, hardware, boost, settings) = Rig(CpuBoostMode.Disabled, OperatingMode.Turbo);
        hardware.ModeWriteResult = false;
        hardware.CurrentMode = OperatingMode.Quiet;
        settings.LastOperatingMode = OperatingMode.Quiet;

        using var vm = new MainViewModel(settings, hardware, null, TempSettingsPath(), gameProfiles: service,
            cpuBoost: boost);
        await vm.InitializeAsync();

        scanner.Processes.Add(Running());
        await vm.PollGameAsync();

        Assert.Equal(Loc.T($"Oyun profili kısmen uygulandı: {Name}"), vm.Status);
        Assert.Equal(OperatingMode.Quiet, vm.SelectedMode?.Mode);
    }

    [Fact]
    public async Task ARefusedBoostWriteIsReported()
    {
        var (service, scanner, _, boost, _) = Rig(CpuBoostMode.Disabled);
        boost.WriteResult = false;

        scanner.Processes.Add(Running());
        var result = await service.PollAsync();

        Assert.Equal(false, result.Applied?.BoostWritten);
        Assert.True(result.Applied?.IsPartial);
    }

    // ---------------------------------------------------------------- b. a hand-added game

    [Fact]
    public void AHandAddedGameGetsTheLibrarysBoost()
    {
        // The model default is Disabled, which is what a hand-added game used to get.
        var settings = new AppSettings { GameProfiles = new List<GameProfile>() };
        using var vm = new MainViewModel(settings, new GameHardware(), null, TempSettingsPath());

        vm.NewGameExe = "valorant";
        vm.AddGameProfileCommand.Execute(null);

        var added = Assert.Single(settings.GameProfiles);
        Assert.Equal(GameLibraryRules.DetermineOptimalCpuBoost("valorant"), added.TargetCpuBoost);
        Assert.Equal(CpuBoostMode.Aggressive, added.TargetCpuBoost);
    }

    // ---------------------------------------------------------------- c. a fixed Custom fan rule

    [Fact]
    public async Task AFixedCustomFanRuleWritesTheSliders()
    {
        var (service, scanner, hardware, _, settings) = Rig(CpuBoostMode.Disabled);
        settings.CustomFanCpuPercent = 35;
        settings.CustomFanGpuPercent = 45;
        settings.GameProfiles[0].TargetFanMode = FanMode.Custom;
        settings.GameProfiles[0].TargetFanCurveEnabled = false;

        scanner.Processes.Add(Running());
        await service.PollAsync();

        Assert.Contains((FanMode.Custom, 35, 45), hardware.FanWritesSnapshot());
        Assert.DoesNotContain(hardware.FanWritesSnapshot(), w => w.Mode == FanMode.Custom && w.Cpu < 0);
    }

    // ---------------------------------------------------------------- review round 1: school mode switched on mid-game

    [Fact]
    public async Task SchoolModeSwitchedOnMidGameBacksUpTheUsersBatteryValueNotTheGames()
    {
        // School mode backs up the battery value it finds. It used to find the game's, and switching school mode off
        // after the game then put the game's boost into the power plan for good.
        var (service, scanner, hardware, boost, settings) = Rig(CpuBoostMode.Disabled);
        var backupPath = TempSettingsPath();
        try
        {
            using var vm = SchoolVm(service, hardware, boost, settings, backupPath);
            await vm.InitializeAsync();

            scanner.Processes.Add(Running());
            await vm.PollGameAsync();
            Assert.Equal(CpuBoostMode.Disabled, boost.Dc);

            vm.SchoolModeOn = true;
            await WaitUntilAsync(() => settings.SchoolModeEnabled && boost.Dc == CpuBoostMode.Disabled);
            Assert.Equal(CpuBoostMode.Disabled, boost.Dc);

            scanner.Processes.Clear();
            await vm.PollGameAsync();
            Assert.False(vm.GameActive);
            Assert.Equal(CpuBoostMode.Disabled, boost.Dc);

            vm.SchoolModeOn = false;
            await WaitUntilAsync(() => !settings.SchoolModeEnabled);

            Assert.Equal(CpuBoostMode.Efficient, boost.Dc);
            Assert.Equal(CpuBoostMode.Aggressive, boost.Ac);
        }
        finally
        {
            File.Delete(backupPath);
        }
    }

    [Fact]
    public async Task SchoolModeSwitchedOnMidGameOnASingleSwitchEndsOnThePreGameValue()
    {
        var (service, scanner, hardware, boost, settings) = Rig(CpuBoostMode.Disabled);
        boost.SingleSwitch = true;
        boost.Ac = CpuBoostMode.Enabled;
        service.CpuBoostPerPowerSource = false;
        var backupPath = TempSettingsPath();
        try
        {
            using var vm = SchoolVm(service, hardware, boost, settings, backupPath);
            await vm.InitializeAsync();

            scanner.Processes.Add(Running());
            await vm.PollGameAsync();
            Assert.Equal(CpuBoostMode.Disabled, boost.Ac);

            vm.SchoolModeOn = true;
            await WaitUntilAsync(() => settings.SchoolModeEnabled && boost.Ac == CpuBoostMode.Disabled);

            scanner.Processes.Clear();
            await vm.PollGameAsync();
            Assert.Equal(CpuBoostMode.Disabled, boost.Ac);

            vm.SchoolModeOn = false;
            await WaitUntilAsync(() => !settings.SchoolModeEnabled);

            Assert.Equal(CpuBoostMode.Enabled, boost.Ac);
        }
        finally
        {
            File.Delete(backupPath);
        }
    }

    [Fact]
    public async Task ReleasingTheBatteryValueWritesThePreGameValueAndTheEndThenLeavesItAlone()
    {
        var (service, scanner, _, boost, _) = Rig(CpuBoostMode.Disabled);

        scanner.Processes.Add(Running());
        await service.PollAsync();
        boost.ClearWrites();

        Assert.True(await service.ReleaseBatteryBoostAsync());
        Assert.Equal(new[] { (CpuBoostMode.Efficient, false, true) }, boost.WritesSnapshot());
        Assert.Null(service.Snapshot?.RestorableDcBoost);

        boost.ClearWrites();
        scanner.Processes.Clear();
        await service.PollAsync();

        Assert.Equal(new[] { (CpuBoostMode.Aggressive, true, false) }, boost.WritesSnapshot());
    }

    [Fact]
    public async Task NothingIsReleasedWithoutASession()
    {
        var (service, _, _, boost, _) = Rig(CpuBoostMode.Disabled);

        Assert.False(await service.ReleaseBatteryBoostAsync());
        Assert.Empty(boost.WritesSnapshot());
    }

    // ---------------------------------------------------------------- review round 1: the crash record comes first

    [Fact]
    public async Task TheCrashRecordIsSavedBeforeTheFirstWrite()
    {
        var (service, scanner, hardware, boost, settings) = Rig(CpuBoostMode.Disabled);
        hardware.WriteProbe = () => settings.GameSessionRecovery is { IsActive: true };
        boost.WriteProbe = () => settings.GameSessionRecovery is { IsActive: true, SavedAcBoost: not null, SavedDcBoost: not null };

        using var vm = new MainViewModel(settings, hardware, null, TempSettingsPath(), gameProfiles: service,
            cpuBoost: boost);
        await vm.InitializeAsync();
        hardware.ProbeAtModeWrites.Clear();
        boost.ProbeAtWrites.Clear();

        scanner.Processes.Add(Running());
        await vm.PollGameAsync();

        Assert.True(vm.GameActive);
        Assert.NotEmpty(hardware.ProbeAtModeWrites);
        Assert.True(hardware.ProbeAtModeWrites[0], "the mode was written before the crash record existed");
        Assert.NotEmpty(boost.ProbeAtWrites);
        Assert.True(boost.ProbeAtWrites[0], "the boost was written before the crash record named it");
    }

    // ---------------------------------------------------------------- review round 1: recovery order

    [Fact]
    public async Task ARecoveryWhoseModeWriteThrowsStillPutsTheBoostBack()
    {
        var hardware = new GameHardware { ThrowOnModeWrite = true };
        var boost = new FakeBoost { Ac = CpuBoostMode.Disabled, Dc = CpuBoostMode.Disabled };
        var settings = new AppSettings
        {
            GameSessionRecovery = new GameSessionRecoveryState
            {
                IsActive = true,
                SavedMode = OperatingMode.Default,
                SavedAcBoost = CpuBoostMode.Aggressive,
                SavedDcBoost = CpuBoostMode.Efficient
            }
        };

        using var vm = new MainViewModel(settings, hardware, null, TempSettingsPath(), cpuBoost: boost);
        await vm.InitializeAsync();

        Assert.Equal(CpuBoostMode.Aggressive, boost.Ac);
        Assert.Equal(CpuBoostMode.Efficient, boost.Dc);

        // The mode write threw, so the record is kept for the next launch with only the mode still owed: the boost that
        // did land is struck off, and is not replayed.
        var kept = settings.GameSessionRecovery;
        Assert.NotNull(kept);
        Assert.True(kept!.RestoreMode);
        Assert.Null(kept.SavedAcBoost);
        Assert.Null(kept.SavedDcBoost);
    }

    // ---------------------------------------------------------------- review round 1: OEM boost values in the record

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(6)]
    public void AnOemBoostValueInTheCrashRecordSurvivesTheSanitizer(int value)
    {
        var settings = new AppSettings
        {
            GameSessionRecovery = new GameSessionRecoveryState
            {
                IsActive = true,
                SavedAcBoost = (CpuBoostMode)value,
                SavedDcBoost = (CpuBoostMode)value
            }
        };

        settings.Normalize();

        Assert.Equal((CpuBoostMode)value, settings.GameSessionRecovery!.SavedAcBoost);
        Assert.Equal((CpuBoostMode)value, settings.GameSessionRecovery.SavedDcBoost);
    }

    [Theory]
    [InlineData(7)]
    [InlineData(-1)]
    public void ABoostValueOutsidePerfBoostModeIsDropped(int value)
    {
        var settings = new AppSettings
        {
            GameSessionRecovery = new GameSessionRecoveryState { IsActive = true, SavedAcBoost = (CpuBoostMode)value }
        };

        settings.Normalize();

        Assert.Null(settings.GameSessionRecovery!.SavedAcBoost);
    }

    // ---------------------------------------------------------------- review round 1: school mode off mid-game

    [Fact]
    public async Task SchoolModeOffMidGameThenAReapplyRestoresTheUsersBatteryValue()
    {
        var (service, scanner, _, boost, settings) = Rig(CpuBoostMode.Aggressive);
        settings.SchoolModeEnabled = true;
        boost.Dc = CpuBoostMode.Disabled; // school mode's value

        scanner.Processes.Add(Running());
        await service.PollAsync();

        // School mode is switched off and puts the user's value back; the user then picks another one.
        settings.SchoolModeEnabled = false;
        boost.Dc = CpuBoostMode.Enabled;
        settings.GameProfiles[0].TargetCpuBoost = CpuBoostMode.Efficient;
        Assert.True(await service.ReapplyActiveAsync(Exe));
        Assert.Equal(CpuBoostMode.Efficient, boost.Dc);

        scanner.Processes.Clear();
        await service.PollAsync();

        Assert.Equal(CpuBoostMode.Enabled, boost.Dc);
        Assert.Equal(CpuBoostMode.Aggressive, boost.Ac);
    }

    [Fact]
    public async Task TheCrashRecordFollowsABatteryValueTheSessionTookOverMidGame()
    {
        var (service, scanner, hardware, boost, settings) = Rig(CpuBoostMode.Disabled);
        settings.SchoolModeEnabled = true;
        boost.Dc = CpuBoostMode.Disabled;

        using var vm = new MainViewModel(settings, hardware, null, TempSettingsPath(), gameProfiles: service,
            cpuBoost: boost);
        await vm.InitializeAsync();

        scanner.Processes.Add(Running());
        await vm.PollGameAsync();
        Assert.Null(settings.GameSessionRecovery?.SavedDcBoost);
        Assert.Equal(CpuBoostMode.Aggressive, settings.GameSessionRecovery?.SavedAcBoost);

        settings.SchoolModeEnabled = false;
        boost.Dc = CpuBoostMode.Enabled;
        vm.SelectedGameCpuBoost = vm.CpuBoostChoices.First(c => c.Mode == CpuBoostMode.Efficient);

        await WaitUntilAsync(() => settings.GameSessionRecovery?.SavedDcBoost == CpuBoostMode.Enabled);
        Assert.Equal(CpuBoostMode.Aggressive, settings.GameSessionRecovery?.SavedAcBoost);
    }

    // ---------------------------------------------------------------- review round 1: the one-time boost migration

    [Fact]
    public void AnOldSettingsFileGetsTheLibrarysBoostForAnUnchosenDisabledOnce()
    {
        var path = TempSettingsPath();
        try
        {
            File.WriteAllText(path, OldSettingsJson(
                new GameProfile { ExeName = "MyGame", DisplayName = "My Game", TargetCpuBoost = CpuBoostMode.Disabled },
                new GameProfile { ExeName = "Balatro", DisplayName = "Balatro", TargetCpuBoost = CpuBoostMode.Disabled },
                new GameProfile { ExeName = "cs2", DisplayName = "CS2", TargetCpuBoost = CpuBoostMode.Efficient }));

            var first = SettingsManager.LoadSettings(path).Settings;

            Assert.True(first.GameBoostDefaultsMigrated);
            Assert.Equal(GameLibraryRules.DetermineOptimalCpuBoost("MyGame"), Profile(first, "MyGame").TargetCpuBoost);
            Assert.NotEqual(CpuBoostMode.Disabled, Profile(first, "MyGame").TargetCpuBoost);
            Assert.Equal(CpuBoostMode.Disabled, Profile(first, "Balatro").TargetCpuBoost);
            Assert.Equal(CpuBoostMode.Efficient, Profile(first, "cs2").TargetCpuBoost);

            // Written back: the file itself now carries the flag and the new value.
            var text = File.ReadAllText(path);
            Assert.Contains("\"game_boost_defaults_migrated\": true", text);

            // A Disabled the user picks after the migration is theirs, and stays.
            Profile(first, "MyGame").TargetCpuBoost = CpuBoostMode.Disabled;
            SettingsManager.SaveSettingsAtomic(path, first);

            var second = SettingsManager.LoadSettings(path).Settings;
            Assert.Equal(CpuBoostMode.Disabled, Profile(second, "MyGame").TargetCpuBoost);
            Assert.Equal(CpuBoostMode.Disabled, Profile(second, "Balatro").TargetCpuBoost);
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".bak");
        }
    }

    [Fact]
    public void ASecondLoadOfAMigratedFileChangesNothing()
    {
        var path = TempSettingsPath();
        try
        {
            File.WriteAllText(path, OldSettingsJson(
                new GameProfile { ExeName = "MyGame", DisplayName = "My Game", TargetCpuBoost = CpuBoostMode.Disabled }));

            SettingsManager.LoadSettings(path);
            var afterFirst = File.ReadAllText(path);
            var second = SettingsManager.LoadSettings(path).Settings;

            Assert.Equal(afterFirst, File.ReadAllText(path));
            Assert.Equal(0, second.MigrateGameBoostDefaults());
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".bak");
        }
    }

    [Fact]
    public void AFreshInstallIsMarkedAsMigrated()
    {
        var path = TempSettingsPath();

        var fresh = SettingsManager.LoadSettings(path).Settings;

        Assert.True(fresh.GameBoostDefaultsMigrated);
        Assert.False(File.Exists(path));
    }

    // ---------------------------------------------------------------- helpers

    private static GameProfile Profile(AppSettings settings, string exe) =>
        settings.GameProfiles.Single(p => p.ExeName == exe);

    /// <summary>A settings file as 1.3.5 wrote it: the given rules and no migration flag.</summary>
    private static string OldSettingsJson(params GameProfile[] profiles)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(
            System.Text.Json.JsonSerializer.Serialize(new AppSettings { GameProfiles = profiles.ToList() }))!.AsObject();
        Assert.True(node.Remove("game_boost_defaults_migrated"));
        return node.ToJsonString();
    }

    private static MainViewModel SchoolVm(GameProfileService service, GameHardware hardware, FakeBoost boost,
        AppSettings settings, string backupPath)
    {
        // School mode writes through the same fake boost: on Windows its own CpuPowerService reaches the same power
        // plan, which is what makes the backup see the game's value.
        var school = new SchoolModeService(hardware, boost, new NoDisplay(), new FixedBrightness(), settings,
            new NoPowerScheme())
        {
            BackupPath = backupPath
        };

        return new MainViewModel(settings, hardware, null, TempSettingsPath(), gameProfiles: service,
            schoolMode: school, cpuBoost: boost);
    }

    private sealed class NoDisplay : IDisplayRefreshRateService
    {
        public bool IsSupported => false;
        public int GetCurrentRefreshRate() => 60;
        public int GetMaxRefreshRate() => 60;
        public bool SetRefreshRate(int hz) => true;
    }

    private sealed class FixedBrightness : IScreenBrightnessService
    {
        public int? GetBrightness() => 80;
        public bool SetBrightness(int percent) => true;
    }

    private sealed class NoPowerScheme : IPowerSchemeAccess
    {
        public bool IsSupported => false;

        public bool TryReadDc(Guid subGroup, Guid setting, out uint value)
        {
            value = 0;
            return false;
        }

        public bool TryWriteDc(Guid subGroup, Guid setting, uint value) => false;
    }

    private static (GameProfileService Service, FakeScanner Scanner, GameHardware Hardware, FakeBoost Boost,
        AppSettings Settings) Rig(CpuBoostMode target, OperatingMode mode = OperatingMode.Default)
    {
        var settings = new AppSettings
        {
            AutoGameModeEnabled = true,
            GameProfiles = new List<GameProfile>
            {
                new()
                {
                    ExeName = Exe,
                    DisplayName = Name,
                    TargetMode = mode,
                    TargetCpuBoost = target,
                    TargetFanMode = FanMode.Auto
                }
            }
        };

        var scanner = new FakeScanner();
        var hardware = new GameHardware();
        var boost = new FakeBoost();
        var service = new GameProfileService(scanner, settings, hardware) { CpuBoost = boost };
        return (service, scanner, hardware, boost, settings);
    }

    private static RunningProcess Running() => new(5151, Exe, @"C:\Games\" + Exe);

    private static string TempSettingsPath() =>
        Path.Combine(Path.GetTempPath(), "nitrolite-gpas-" + Guid.NewGuid().ToString("N") + ".json");

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

    private sealed class FakeScanner : IProcessScanner
    {
        public List<RunningProcess> Processes { get; } = new();

        public IReadOnlyList<RunningProcess> Scan() => Processes.ToList();
    }

    /// <summary>
    /// A boost control with an adapter and a battery value, or one switch for both (Linux). Records every write and
    /// touches no power scheme.
    /// </summary>
    private sealed class FakeBoost : ICpuBoostAccess
    {
        private readonly object _lock = new();
        private readonly List<(CpuBoostMode Mode, bool Ac, bool Dc)> _writes = new();

        public bool Supported { get; set; } = true;

        public bool IsSupported => Supported;

        /// <summary>The adapter value; with <see cref="SingleSwitch"/> the only value.</summary>
        public CpuBoostMode? Ac { get; set; } = CpuBoostMode.Aggressive;

        public CpuBoostMode? Dc { get; set; } = CpuBoostMode.Efficient;

        public bool SingleSwitch { get; set; }

        public bool WriteResult { get; set; } = true;

        public List<(CpuBoostMode Mode, bool Ac, bool Dc)> WritesSnapshot()
        {
            lock (_lock) return _writes.ToList();
        }

        public void ClearWrites()
        {
            lock (_lock) _writes.Clear();
        }

        public CpuBoostMode? QueryBoostModeExact(bool isAc) => isAc || SingleSwitch ? Ac : Dc;

        /// <summary>Asked at every write; its answers are kept in <see cref="ProbeAtWrites"/>.</summary>
        public Func<bool>? WriteProbe { get; set; }

        public List<bool> ProbeAtWrites { get; } = new();

        public Task<bool> SetBoostModeAsync(CpuBoostMode mode, bool targetAc = true, bool targetDc = true)
        {
            lock (_lock)
            {
                _writes.Add((mode, targetAc, targetDc));
                if (WriteProbe != null) ProbeAtWrites.Add(WriteProbe());
            }
            if (!WriteResult) return Task.FromResult(false);

            if (SingleSwitch)
            {
                Ac = mode;
            }
            else
            {
                if (targetAc) Ac = mode;
                if (targetDc) Dc = mode;
            }

            return Task.FromResult(true);
        }
    }

    /// <summary>A backend that records mode and fan writes, can refuse the mode, and touches no hardware.</summary>
    private sealed class GameHardware : IHardwareController
    {
        private readonly object _lock = new();
        private readonly List<OperatingMode> _modeWrites = new();
        private readonly List<(FanMode Mode, int Cpu, int Gpu)> _fanWrites = new();

        public string BackendName => "Test";

        public bool IsAvailable => true;

        public HardwareCapabilities Capabilities { get; } = new()
        {
            Backend = "Test",
            ServiceAvailable = true,
            FanControl = true,
            OperatingModeControl = true
        };

        public bool ModeWriteResult { get; set; } = true;

        public OperatingMode CurrentMode { get; set; } = OperatingMode.Default;

        public List<OperatingMode> ModeWritesSnapshot()
        {
            lock (_lock) return _modeWrites.ToList();
        }

        public List<(FanMode Mode, int Cpu, int Gpu)> FanWritesSnapshot()
        {
            lock (_lock) return _fanWrites.ToList();
        }

        public Task<bool> EnsureConnectedAsync() => Task.FromResult(true);

        public Task<HardwareCapabilities> GetCapabilitiesAsync() => Task.FromResult(Capabilities);

        public Task<MonitorData?> GetMonitorDataAsync() => Task.FromResult<MonitorData?>(new MonitorData
        {
            CpuTemperature = 55,
            GpuTemperature = 50,
            CpuFanSpeed = 2000,
            GpuFanSpeed = 2000,
            IsFanRpmMeasured = true,
            IsTemperatureMeasured = true
        });

        public Task<OperatingMode?> GetOperatingModeAsync() => Task.FromResult<OperatingMode?>(CurrentMode);

        /// <summary>A mode write throws, like a WMI call that failed at boot.</summary>
        public bool ThrowOnModeWrite { get; set; }

        /// <summary>Asked at every mode write; its answers are kept in <see cref="ProbeAtModeWrites"/>.</summary>
        public Func<bool>? WriteProbe { get; set; }

        public List<bool> ProbeAtModeWrites { get; } = new();

        public Task<bool> SetOperatingModeAsync(OperatingMode mode)
        {
            lock (_lock)
            {
                _modeWrites.Add(mode);
                if (WriteProbe != null) ProbeAtModeWrites.Add(WriteProbe());
            }

            if (ThrowOnModeWrite) throw new InvalidOperationException("the WMI call failed");
            if (ModeWriteResult) CurrentMode = mode;
            return Task.FromResult(ModeWriteResult);
        }

        public Task<GpuOperatingMode?> GetGpuModeAsync() => Task.FromResult<GpuOperatingMode?>(null);

        public Task<bool> SetGpuModeAsync(GpuOperatingMode mode) => Task.FromResult(true);

        public Task<int?> GetGpuModeCapabilityAsync() => Task.FromResult<int?>(null);

        public Task<FanMode?> GetFanModeAsync() => Task.FromResult<FanMode?>(FanMode.Auto);

        public Task<bool> SetFanModeAsync(FanMode mode, int cpuPercent = -1, int gpuPercent = -1)
        {
            lock (_lock) _fanWrites.Add((mode, cpuPercent, gpuPercent));
            return Task.FromResult(true);
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
