using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NitroLite.Core;
using NitroLite.Core.Hardware;
using NitroLite.Desktop.ViewModels;
using Xunit;
using static NitroLite.Tests.CleanupA3LightingTests;

namespace NitroLite.Tests;

/// <summary>
/// Regression tests for clean-up round 3, power, school mode and UI gating (P1-P8, U1-U3).
/// </summary>
/// <remarks>
/// Hardware goes to <see cref="CleanupA3LightingTests.A3Hardware"/>, the power scheme to <see cref="SchemeFake"/>, the
/// panel to <see cref="PanelFake"/>; nothing here reaches WMI, powrprof or the display driver.
/// </remarks>
public class CleanupA3PowerTests
{
    // ---------------------------------------------------------------- P1. the switch reaches the settings file

    [Fact]
    public async Task SchoolModeSwitchedOn_IsOnDiskAtOnce_AndSoIsSwitchingItOff()
    {
        var settings = new AppSettings();
        var hardware = new A3Hardware();
        var path = TempSettingsPath();
        using var vm = new MainViewModel(settings, hardware, null, path, schoolMode: School(hardware, settings),
            powerState: () => (false, 60));
        await vm.InitializeAsync();

        vm.SchoolModeOn = true;
        await WaitUntilAsync(() => File.Exists(path) && SettingsManager.LoadSettings(path).Settings.SchoolModeEnabled);

        vm.SchoolModeOn = false;
        await WaitUntilAsync(() => !SettingsManager.LoadSettings(path).Settings.SchoolModeEnabled);
    }

    [Fact]
    public async Task SwitchingOffWithAnUnreadableRecord_ReportsTheFailure()
    {
        var settings = new AppSettings { SchoolModeEnabled = true };
        var school = School(new A3Hardware(), settings);
        File.WriteAllText(school.BackupPath!, "{ this is not json");
        try
        {
            var outcome = await school.DisableAsync();

            Assert.False(outcome.Success);
            Assert.NotEmpty(outcome.Failed);

            // The record is kept for a repair by hand, and the switch is off.
            Assert.True(File.Exists(school.BackupPath));
            Assert.False(settings.SchoolModeEnabled);
        }
        finally
        {
            File.Delete(school.BackupPath!);
        }
    }

    // ---------------------------------------------------------------- P2. the battery refresh rate survives the app

    [Fact]
    public void TheReplacedRate_IsPersisted_AndANewRunRestoresItOnTheAdapter()
    {
        var settings = new AppSettings { AutoRefreshRateSwitching = true };
        var panel = new PanelFake { Rate = 165 };

        var first = new RefreshRateAutomation(panel, settings);
        Assert.Equal(RefreshRateAction.LoweredToBattery, first.Apply(onAc: false).Action);
        Assert.Equal(165, settings.RefreshRateBeforeBattery);

        // The app ended on battery. The next run starts on the adapter.
        var next = new RefreshRateAutomation(panel, settings);
        var result = next.Apply(onAc: true);

        Assert.Equal(RefreshRateAction.RestoredToMaximum, result.Action);
        Assert.Equal(165, panel.Rate);
        Assert.Null(settings.RefreshRateBeforeBattery);
    }

    [Fact]
    public async Task TheCleanExitOnBattery_LeavesThePanelLowered_AndOnTheAdapterPutsItBack()
    {
        var settings = new AppSettings { AutoRefreshRateSwitching = true };
        var panel = new PanelFake { Rate = 165 };
        var path = TempSettingsPath();
        bool onAdapter = false;
        using var vm = new MainViewModel(settings, new A3Hardware(), null, path,
            refreshRate: new RefreshRateAutomation(panel, settings), powerState: () => (onAdapter, 60));
        await vm.InitializeAsync();

        await vm.RefreshTelemetryAsync();
        Assert.Equal(60, panel.Rate);
        Assert.Equal(165, SettingsManager.LoadSettings(path).Settings.RefreshRateBeforeBattery);

        // On battery (a restart for a language change, say) the 60 Hz is still wanted and the rate stays on disk.
        await vm.CleanExitAsync();
        Assert.Equal(60, panel.Rate);
        Assert.Equal(165, SettingsManager.LoadSettings(path).Settings.RefreshRateBeforeBattery);

        // On the adapter the exit puts it back, since nothing else would once the app is gone.
        onAdapter = true;
        await vm.CleanExitAsync();
        Assert.Equal(165, panel.Rate);
        Assert.Null(settings.RefreshRateBeforeBattery);
        Assert.Null(SettingsManager.LoadSettings(path).Settings.RefreshRateBeforeBattery);
    }

    [Fact]
    public void AStaleSavedRate_IsDroppedWithoutAWrite_WhenTheUserChoseAnotherRate()
    {
        // The app was killed on battery; with it closed, the user set 120 Hz in Windows. 165 must not be forced over it.
        var settings = new AppSettings { AutoRefreshRateSwitching = true, RefreshRateBeforeBattery = 165 };
        var panel = new PanelFake { Rate = 120 };

        var result = new RefreshRateAutomation(panel, settings).Apply(onAc: true);

        Assert.Equal(RefreshRateAction.None, result.Action);
        Assert.Equal(0, panel.Writes);
        Assert.Equal(120, panel.Rate);
        Assert.Null(settings.RefreshRateBeforeBattery);
    }

    [Fact]
    public void NoInternalPanel_IsNotAFailure_AndIsNotRetriedEverySecond()
    {
        var settings = new AppSettings { AutoRefreshRateSwitching = true };
        var panel = new PanelFake { Rate = 0 };
        var automation = new RefreshRateAutomation(panel, settings);

        for (int i = 0; i < 120; i++)
        {
            Assert.NotEqual(RefreshRateAction.Failed, automation.Apply(onAc: false).Action);
        }

        // Two minutes of ticks: the back-off keeps it to a handful of looks rather than one per tick.
        Assert.InRange(panel.Reads, 1, 12);
        Assert.Equal(0, panel.Writes);

        // A real change of power source is looked at at once.
        int before = panel.Reads;
        automation.Apply(onAc: true);
        automation.Apply(onAc: false);
        Assert.True(panel.Reads > before);
    }

    // ---------------------------------------------------------------- P3. school mode records the pre-battery rate

    [Fact]
    public async Task SchoolModeOnBattery_RecordsTheAutomationsSavedRate_NotItsSixtyHertz()
    {
        var settings = new AppSettings { AutoRefreshRateSwitching = true };
        var panel = new PanelFake { Rate = 165 };
        var automation = new RefreshRateAutomation(panel, settings);
        automation.Apply(onAc: false);
        Assert.Equal(60, panel.Rate);

        var school = new SchoolModeService(new A3Hardware(), new NoBoost(), panel, new NoBrightness(), settings,
            new NoScheme())
        { BackupPath = Path.Combine(Path.GetTempPath(), "nitrolite-a3-rate-" + Guid.NewGuid().ToString("N") + ".json") };

        try
        {
            await school.EnableAsync();

            // The adapter comes back: the automation restores its rate and lets go of it.
            automation.Apply(onAc: true);
            Assert.Equal(165, panel.Rate);
            Assert.Null(automation.SavedRate);

            // School mode switched off on the adapter used to write its recorded 60 Hz here, for good.
            int writes = panel.Writes;
            await school.DisableAsync();
            Assert.True(panel.Writes > writes);
            Assert.Equal(165, panel.Rate);
        }
        finally
        {
            File.Delete(school.BackupPath!);
        }
    }

    [Fact]
    public async Task SchoolModeSwitchedOffOnBattery_LeavesThePanelToTheAutomation()
    {
        var settings = new AppSettings { AutoRefreshRateSwitching = true };
        var panel = new PanelFake { Rate = 165 };
        var automation = new RefreshRateAutomation(panel, settings);
        automation.Apply(onAc: false);

        var school = new SchoolModeService(new A3Hardware(), new NoBoost(), panel, new NoBrightness(), settings,
            new NoScheme())
        { BackupPath = Path.Combine(Path.GetTempPath(), "nitrolite-a3-rate-" + Guid.NewGuid().ToString("N") + ".json") };

        try
        {
            await school.EnableAsync();

            // Still on battery: the panel stays at 60 Hz, as it did in 1.3.5 ...
            await school.DisableAsync();
            Assert.Equal(60, panel.Rate);
            Assert.False(automation.Apply(onAc: false).Changed);
            Assert.Equal(60, panel.Rate);

            // ... and the adapter brings the user's rate back.
            Assert.Equal(RefreshRateAction.RestoredToMaximum, automation.Apply(onAc: true).Action);
            Assert.Equal(165, panel.Rate);
        }
        finally
        {
            File.Delete(school.BackupPath!);
        }
    }

    [Fact]
    public async Task SchoolModeOnTheAdapter_ThenBattery_ThenOff_LowersThePanelAgain()
    {
        // The other order: school mode is switched on while plugged in, so it lowers the panel itself and the automation
        // never saves a rate (the panel is already at the battery rate when the plug is pulled). Switched off on
        // battery, the restore puts the full rate back; without one forced pass nothing would lower it again until
        // the next replug.
        var settings = new AppSettings { AutoRefreshRateSwitching = true };
        var hardware = new A3Hardware();
        var panel = new PanelFake { Rate = 165 };
        var path = TempSettingsPath();
        bool onAdapter = true;
        var school = new SchoolModeService(hardware, new NoBoost(), panel, new NoBrightness(), settings, new NoScheme())
        {
            BackupPath = Path.Combine(Path.GetTempPath(), "nitrolite-a3-order-" + Guid.NewGuid().ToString("N") + ".json"),
            GetCurrentOperatingMode = () => hardware.ReportedMode,
            GetCurrentFanMode = () => hardware.ReportedFan
        };

        try
        {
            using var vm = new MainViewModel(settings, hardware, null, path, schoolMode: school,
                refreshRate: new RefreshRateAutomation(panel, settings), powerState: () => (onAdapter, 60));
            await vm.InitializeAsync();

            vm.SchoolModeOn = true;
            await WaitUntilAsync(() => settings.SchoolModeEnabled && panel.Rate == 60);

            onAdapter = false;
            await vm.RefreshTelemetryAsync();
            Assert.Null(settings.RefreshRateBeforeBattery);

            vm.SchoolModeOn = false;
            await WaitUntilAsync(() => !settings.SchoolModeEnabled && settings.RefreshRateBeforeBattery == 165);
            Assert.Equal(60, panel.Rate);

            // The adapter then brings the user's rate back, as it does in every other order.
            onAdapter = true;
            await vm.RefreshTelemetryAsync();
            Assert.Equal(165, panel.Rate);
        }
        finally
        {
            File.Delete(school.BackupPath!);
        }
    }

    // ---------------------------------------------------------------- P4. school mode during a game

    [Fact]
    public async Task SchoolModeDuringAGame_BacksUpThePreGameState_AndTheGamesEndDoesNotUndoIt()
    {
        var settings = new AppSettings
        {
            AutoGameModeEnabled = true,
            GameProfiles = new List<GameProfile>
            {
                new()
                {
                    ExeName = "a3game.exe", DisplayName = "A3 Game", TargetMode = OperatingMode.Turbo,
                    TargetFanMode = FanMode.Max, TargetCpuBoost = CpuBoostMode.Aggressive, AutoApply = true
                }
            }
        };
        var hardware = new A3Hardware { ReportedMode = OperatingMode.Default, ReportedFan = FanMode.Auto };
        var scanner = new ScannerFake();
        var games = new GameProfileService(scanner, settings, hardware);
        var school = School(hardware, settings);

        try
        {
            using var vm = new MainViewModel(settings, hardware, null, TempSettingsPath(), gameProfiles: games,
                schoolMode: school, powerState: () => (true, 80));
            await vm.InitializeAsync();

            scanner.Processes.Add(new RunningProcess(4242, "a3game.exe", @"C:\Games\a3game.exe"));
            await vm.PollGameAsync();
            Assert.Equal(OperatingMode.Turbo, hardware.ReportedMode);
            Assert.Equal(FanMode.Max, hardware.ReportedFan);

            vm.SchoolModeOn = true;
            await WaitUntilAsync(() => settings.SchoolModeEnabled && hardware.ReportedMode == OperatingMode.Eco);

            // The game ends: its restore must leave school mode's Eco and Auto in place.
            scanner.Processes.Clear();
            await vm.PollGameAsync();
            Assert.False(vm.GameActive);
            Assert.Equal(OperatingMode.Eco, hardware.ReportedMode);
            Assert.Equal(FanMode.Auto, hardware.ReportedFan);

            // Switching school mode off puts back the pre-game state, not the game's Turbo and Max.
            vm.SchoolModeOn = false;
            await WaitUntilAsync(() => !settings.SchoolModeEnabled && hardware.ReportedMode == OperatingMode.Default);
            Assert.Equal(FanMode.Auto, hardware.ReportedFan);
        }
        finally
        {
            File.Delete(school.BackupPath!);
        }
    }

    [Fact]
    public async Task AGameThatClosesWhileSchoolModeIsStillSwitchingOn_DoesNotUndoSchoolModesEco()
    {
        // The window the CI run fell into: school mode has written Eco but is still switching on (its lighting step
        // comes after the mode), and the game closes right then. The game service ends the session on its own gate,
        // so its restore lands in the middle of the switch; it has to write school mode's values, not the pre-game
        // Balanced, or the machine ends up out of Eco with the switch on.
        var settings = new AppSettings
        {
            AutoGameModeEnabled = true,
            GameProfiles = new List<GameProfile>
            {
                new()
                {
                    ExeName = "a3race.exe", DisplayName = "A3 Race", TargetMode = OperatingMode.Turbo,
                    TargetFanMode = FanMode.Max, TargetCpuBoost = CpuBoostMode.Aggressive, AutoApply = true
                }
            }
        };
        var hardware = new A3Hardware { ReportedMode = OperatingMode.Default, ReportedFan = FanMode.Auto };
        var scanner = new ScannerFake();
        var games = new GameProfileService(scanner, settings, hardware);
        var school = School(hardware, settings);

        // The game ends from inside school mode's lighting step, which runs after school mode's mode write.
        var modeWhenTheGameEnded = OperatingMode.Default;
        var gameEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        school.TurnLightingOffAsync = async () =>
        {
            modeWhenTheGameEnded = hardware.ReportedMode;
            scanner.Processes.Clear();
            await games.PollAsync();
            gameEnded.TrySetResult();
            return false;
        };

        try
        {
            using var vm = new MainViewModel(settings, hardware, null, TempSettingsPath(), gameProfiles: games,
                schoolMode: school, powerState: () => (true, 80));
            await vm.InitializeAsync();

            scanner.Processes.Add(new RunningProcess(4343, "a3race.exe", @"C:\Games\a3race.exe"));
            await vm.PollGameAsync();
            Assert.Equal(OperatingMode.Turbo, hardware.ReportedMode);

            vm.SchoolModeOn = true;
            await WaitUntilAsync(() => settings.SchoolModeEnabled && gameEnded.Task.IsCompleted);
            Assert.False(games.IsGameActive);

            // School mode had already written Eco when the game closed, and the game's restore left it there.
            Assert.Equal(OperatingMode.Eco, modeWhenTheGameEnded);
            Assert.Equal(OperatingMode.Eco, hardware.ReportedMode);
            Assert.Equal(FanMode.Auto, hardware.ReportedFan);
        }
        finally
        {
            File.Delete(school.BackupPath!);
        }
    }

    // ---------------------------------------------------------------- P5. the scheme school mode changed

    [Fact]
    public async Task TheUndoGoesBackIntoTheSchemeSchoolModeChanged()
    {
        var balanced = Guid.NewGuid();
        var other = Guid.NewGuid();
        var scheme = new SchemeFake(balanced, other) { Active = balanced };
        scheme.Seed(balanced, 100);
        scheme.Seed(other, 77);

        var settings = new AppSettings();
        var school = new SchoolModeService(new A3Hardware(), new NoBoost(), new NoDisplay(), new NoBrightness(), settings,
            scheme)
        { BackupPath = Path.Combine(Path.GetTempPath(), "nitrolite-a3-scheme-" + Guid.NewGuid().ToString("N") + ".json") };

        try
        {
            await school.EnableAsync();
            Assert.Contains(scheme.Writes, w => w.Scheme == balanced);

            // An OEM tool switches the plan while school mode is on.
            scheme.Active = other;
            scheme.Writes.Clear();

            var outcome = await school.DisableAsync();

            Assert.True(outcome.Success);
            Assert.NotEmpty(scheme.Writes);
            Assert.All(scheme.Writes, w => Assert.Equal(balanced, w.Scheme));
            Assert.All(scheme.Values(balanced), v => Assert.Equal(100u, v));
            Assert.All(scheme.Values(other), v => Assert.Equal(77u, v));
        }
        finally
        {
            File.Delete(school.BackupPath!);
        }
    }

    [Fact]
    public async Task ASchemeThatNoLongerExists_IsReported_NotWrittenAround()
    {
        var balanced = Guid.NewGuid();
        var other = Guid.NewGuid();
        var scheme = new SchemeFake(balanced, other) { Active = balanced };
        scheme.Seed(balanced, 100);
        scheme.Seed(other, 77);

        var settings = new AppSettings();
        var school = new SchoolModeService(new A3Hardware(), new NoBoost(), new NoDisplay(), new NoBrightness(), settings,
            scheme)
        { BackupPath = Path.Combine(Path.GetTempPath(), "nitrolite-a3-scheme-" + Guid.NewGuid().ToString("N") + ".json") };

        try
        {
            await school.EnableAsync();
            scheme.Active = other;
            scheme.Delete(balanced);
            scheme.Writes.Clear();

            var outcome = await school.DisableAsync();

            Assert.False(outcome.Success);
            Assert.Empty(scheme.Writes);

            // One line saying the plan is gone, not eight "this setting failed" lines for writes never tried.
            var failure = Assert.Single(outcome.Failed);
            Assert.Equal(Loc.T("Güç planı ayarları (okul modunun değiştirdiği plan artık yok)"), failure);
            Assert.All(scheme.Values(other), v => Assert.Equal(77u, v));
        }
        finally
        {
            File.Delete(school.BackupPath!);
        }
    }

    [Fact]
    public async Task AnOldRecordWithoutAScheme_KeepsTodaysBehaviour()
    {
        var active = Guid.NewGuid();
        var scheme = new SchemeFake(active) { Active = active };
        scheme.Seed(active, 100);

        var settings = new AppSettings { SchoolModeEnabled = true };
        var path = Path.Combine(Path.GetTempPath(), "nitrolite-a3-scheme-" + Guid.NewGuid().ToString("N") + ".json");
        var old = new SchoolModeBackup
        {
            PowerSettings = SchoolModeService.PowerSettings.Select(s => new SchoolModePowerBackup
            {
                SubGroup = s.SubGroup.ToString(), Setting = s.Setting.ToString(), PreviousDc = 55
            }).ToList()
        };
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(old));
        var school = new SchoolModeService(new A3Hardware(), new NoBoost(), new NoDisplay(), new NoBrightness(), settings,
            scheme)
        { BackupPath = path };

        try
        {
            var outcome = await school.DisableAsync();

            Assert.True(outcome.Success);
            Assert.All(scheme.Values(active), v => Assert.Equal(55u, v));
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---------------------------------------------------------------- P6. the battery rule on every path

    [Fact]
    public async Task TurboFromTheCards_IsRefusedOnTheAdapterBelowTheThreshold()
    {
        var hardware = new A3Hardware();
        using var vm = new MainViewModel(new AppSettings(), hardware, null, TempSettingsPath(),
            powerState: () => (true, 30));
        await vm.InitializeAsync();

        vm.SelectedMode = vm.Modes.First(m => m.Mode == OperatingMode.Turbo);
        await WaitUntilAsync(() => vm.SelectedMode?.Mode == OperatingMode.Default);

        Assert.DoesNotContain(OperatingMode.Turbo, hardware.ModeWrites());
        Assert.Equal(ModePolicy.HighPowerBlockedMessage, vm.Status);
    }

    [Fact]
    public async Task TurboFromTheTray_IsRefusedOnBattery_AndAllowedOnTheAdapter()
    {
        bool onAdapter = false;
        var hardware = new A3Hardware();
        using var vm = new MainViewModel(new AppSettings(), hardware, null, TempSettingsPath(),
            powerState: () => (onAdapter, 80));
        await vm.InitializeAsync();

        await vm.ApplyModeFromTrayAsync(OperatingMode.Turbo);
        Assert.DoesNotContain(OperatingMode.Turbo, hardware.ModeWrites());
        Assert.NotEqual(OperatingMode.Turbo, vm.SelectedMode?.Mode);
        Assert.Equal(ModePolicy.HighPowerBlockedMessage, vm.Status);

        onAdapter = true;
        await vm.RefreshTelemetryAsync();
        await vm.ApplyModeFromTrayAsync(OperatingMode.Turbo);
        Assert.Contains(OperatingMode.Turbo, hardware.ModeWrites());
    }

    [Fact]
    public async Task ARefusedTurbo_OnAMachineThatDoesNotReportItsMode_GoesBackToThePreviousCard()
    {
        var hardware = new A3Hardware();
        using var vm = new MainViewModel(new AppSettings(), hardware, null, TempSettingsPath(),
            powerState: () => (true, 30));
        await vm.InitializeAsync();
        vm.SelectedMode = vm.Modes.First(m => m.Mode == OperatingMode.Quiet);
        await WaitUntilAsync(() => hardware.ModeWrites().Contains(OperatingMode.Quiet));

        hardware.ReportsMode = false;
        vm.SelectedMode = vm.Modes.First(m => m.Mode == OperatingMode.Turbo);
        await WaitUntilAsync(() => vm.SelectedMode?.Mode == OperatingMode.Quiet);

        Assert.DoesNotContain(OperatingMode.Turbo, hardware.ModeWrites());
        Assert.Equal(ModePolicy.HighPowerBlockedMessage, vm.Status);
    }

    // ---------------------------------------------------------------- P7. the boost pills while school mode is on

    [Fact]
    public async Task ABoostPillDuringSchoolMode_WritesTheAdapterValueOnly()
    {
        var settings = new AppSettings { SchoolModeEnabled = true };
        var boost = new BoostFake();
        using var vm = new MainViewModel(settings, new A3Hardware(), null, TempSettingsPath(), cpuBoost: boost,
            powerState: () => (true, 80));
        await vm.InitializeAsync();
        boost.Writes.Clear();

        vm.SelectedCpuBoost = vm.CpuBoostChoices.First(c => c.Mode == CpuBoostMode.Efficient);
        await WaitUntilAsync(() => boost.Writes.Count > 0);

        var write = Assert.Single(boost.Writes);
        Assert.Equal((CpuBoostMode.Efficient, true, false), write);
        Assert.Equal(CpuBoostMode.Disabled, boost.Dc);
        Assert.Equal(vm.Labels.BoostAdapterOnlySchoolMode, vm.Status);
    }

    [Fact]
    public async Task ABoostPillDuringSchoolMode_OnASingleSwitch_WritesNothing()
    {
        var settings = new AppSettings { SchoolModeEnabled = true };
        var boost = new BoostFake();
        var hardware = new A3Hardware();
        var games = new GameProfileService(new ScannerFake(), settings, hardware) { CpuBoostPerPowerSource = false };
        using var vm = new MainViewModel(settings, hardware, null, TempSettingsPath(), gameProfiles: games,
            cpuBoost: boost, powerState: () => (true, 80));
        await vm.InitializeAsync();
        boost.Writes.Clear();

        vm.SelectedCpuBoost = vm.CpuBoostChoices.First(c => c.Mode == CpuBoostMode.Efficient);
        await WaitUntilAsync(() => vm.Status == vm.Labels.BoostHeldBySchoolMode);

        Assert.Empty(boost.Writes);
    }

    // ---------------------------------------------------------------- P8. the battery power mode picker

    [Fact]
    public void TheDcProfilePicker_IsGoneWhileNothingActsOnIt()
    {
        // Nothing in this build acts on AppSettings.DcPowerMode, so the battery page offers no control for it.
        var markup = View("BatteryView.axaml");
        Assert.DoesNotContain("DcProfile", markup, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- U1-U3. gating and accessibility in the markup

    [Fact]
    public void TheTopBarModeMenu_IsGatedLikeTheCards()
    {
        var markup = View("MainWindow.axaml");
        int menu = markup.IndexOf("ToolTip.Tip=\"{Binding Labels.ModeCaption}\"", StringComparison.Ordinal);
        Assert.True(menu > 0);
        var button = markup.Substring(menu, 600);

        Assert.Contains("BoolConverters.And", button, StringComparison.Ordinal);
        Assert.Contains("ModeControlAvailable", button, StringComparison.Ordinal);
        Assert.Contains("IsHardwareReady", button, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("SettingsView.axaml")]
    [InlineData("BatteryView.axaml")]
    [InlineData("DashboardView.axaml")]
    [InlineData("OnboardingOverlay.axaml")]
    public void EveryRowSwitch_HasAnAccessibleName(string view)
    {
        var markup = View(view);
        int at = 0;
        int count = 0;
        while ((at = markup.IndexOf("Classes=\"toggle\"", at, StringComparison.Ordinal)) >= 0)
        {
            int end = markup.IndexOf("/>", at, StringComparison.Ordinal);
            var element = markup[at..end];
            Assert.Contains("AutomationProperties.Name=", element, StringComparison.Ordinal);
            at = end;
            count++;
        }

        Assert.True(count > 0);
    }

    [Fact]
    public void TheDialogs_AnswerEnterAndEscape()
    {
        var confirm = View("ConfirmDialog.axaml");
        Assert.Contains("IsCancel=\"True\"", confirm, StringComparison.Ordinal);

        // A destructive confirmation puts Enter on Cancel; only a harmless one lets Enter confirm.
        Assert.DoesNotContain("IsDefault=\"True\"", confirm, StringComparison.Ordinal);
        Assert.False(NitroLite.Desktop.Views.ConfirmDialog.EnterConfirms(destructive: true));
        Assert.True(NitroLite.Desktop.Views.ConfirmDialog.EnterConfirms(destructive: false));
        var confirmCode = File.ReadAllText(ViewPath("ConfirmDialog.axaml.cs"), Encoding.UTF8);
        Assert.Contains("CancelButton.IsDefault = !enterConfirms", confirmCode, StringComparison.Ordinal);

        var onboarding = File.ReadAllText(ViewPath("OnboardingOverlay.axaml.cs"), Encoding.UTF8);
        Assert.Contains("Key.Escape", onboarding, StringComparison.Ordinal);
        Assert.Contains("Key.Enter", onboarding, StringComparison.Ordinal);
        Assert.Contains("KeyboardNavigation.TabNavigation=\"Cycle\"", View("OnboardingOverlay.axaml"),
            StringComparison.Ordinal);

        var window = File.ReadAllText(ViewPath("MainWindow.axaml.cs"), Encoding.UTF8);
        Assert.Contains("KeyboardNavigationMode.None", window, StringComparison.Ordinal);
    }

    [Fact]
    public void TheOverlayTitle_IsTranslated()
    {
        Assert.DoesNotContain("Title=\"NitroLite OSD\"", View("OsdOverlayWindow.axaml"), StringComparison.Ordinal);
        Assert.Contains("Labels.OsdWindowTitle", View("OsdOverlayWindow.axaml"), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- helpers

    private static string View(string name) => File.ReadAllText(ViewPath(name), Encoding.UTF8);

    private static string ViewPath(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "NitroLite.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "NitroLite.Desktop", "Views", name);
    }

    private sealed class ScannerFake : IProcessScanner
    {
        public List<RunningProcess> Processes { get; } = new();
        public IReadOnlyList<RunningProcess> Scan() => Processes.ToList();
    }

    private sealed class PanelFake : IDisplayRefreshRateService
    {
        public int Rate { get; set; }
        public int Reads { get; private set; }
        public int Writes { get; private set; }

        public int GetCurrentRefreshRate()
        {
            Reads++;
            return Rate;
        }

        public int GetMaxRefreshRate() => 165;

        public bool SetRefreshRate(int hz)
        {
            Writes++;
            if (Rate <= 0) return false;
            Rate = hz;
            return true;
        }
    }

    private sealed class BoostFake : ICpuBoostAccess
    {
        public CpuBoostMode Ac { get; set; } = CpuBoostMode.Aggressive;
        public CpuBoostMode Dc { get; set; } = CpuBoostMode.Disabled;
        public List<(CpuBoostMode Mode, bool Ac, bool Dc)> Writes { get; } = new();

        public CpuBoostMode? QueryBoostModeExact(bool isAc) => isAc ? Ac : Dc;

        public Task<bool> SetBoostModeAsync(CpuBoostMode mode, bool targetAc = true, bool targetDc = true)
        {
            Writes.Add((mode, targetAc, targetDc));
            if (targetAc) Ac = mode;
            if (targetDc) Dc = mode;
            return Task.FromResult(true);
        }
    }

    /// <summary>Power schemes in memory, each with its own battery values.</summary>
    private sealed class SchemeFake : IPowerSchemeAccess
    {
        private readonly Dictionary<Guid, Dictionary<(Guid, Guid), uint>> _schemes = new();

        public SchemeFake(params Guid[] schemes)
        {
            foreach (var s in schemes) _schemes[s] = new();
        }

        public Guid Active { get; set; }
        public List<(Guid Scheme, Guid Setting, uint Value)> Writes { get; } = new();

        public Guid? ActiveScheme => Active;

        public void Seed(Guid scheme, uint value)
        {
            foreach (var s in SchoolModeService.PowerSettings) _schemes[scheme][(s.SubGroup, s.Setting)] = value;
        }

        public void Delete(Guid scheme) => _schemes.Remove(scheme);

        public IEnumerable<uint> Values(Guid scheme) => _schemes[scheme].Values;

        public bool SchemeExists(Guid scheme) => _schemes.ContainsKey(scheme);

        public bool TryReadDc(Guid subGroup, Guid setting, out uint value)
        {
            value = 0;
            return _schemes.TryGetValue(Active, out var values) && values.TryGetValue((subGroup, setting), out value);
        }

        public bool TryWriteDc(Guid subGroup, Guid setting, uint value) => TryWriteDc(Active, subGroup, setting, value);

        public bool TryWriteDc(Guid scheme, Guid subGroup, Guid setting, uint value)
        {
            if (!_schemes.TryGetValue(scheme, out var values)) return false;
            values[(subGroup, setting)] = value;
            Writes.Add((scheme, setting, value));
            return true;
        }
    }
}
