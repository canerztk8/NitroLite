using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NitroLite.Core;
using NitroLite.Desktop;
using NitroLite.Desktop.ViewModels;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// Phase 1 of the WPF parity pass: the shell, the tray menu and the Operating Modes page.
/// </summary>
/// <remarks>
/// <para>
/// These are the decisions the WPF window made that the Avalonia window had to learn: which mode cards exist on
/// battery, whether the rail draws a FARE row, whether a rail width survives a restart, whether a game scan adds
/// rules, whether a delete asks first, whether a curve preset writes the anchors, and what the tray menu hides.
/// </para>
/// <para>
/// The tray host itself needs a <c>TrayIcon</c>, which needs a display, so the menu's rules live in
/// <see cref="TrayMenuPolicy"/> and are asserted there. Everything else is driven through the view model, which is
/// where the behaviour actually lives.
/// </para>
/// </remarks>
public class WpfParityPhase1Tests
{
    // ---------------------------------------------------------------- fixtures

    /// <summary>A settings file of this test's own, so a parallel test cannot read or overwrite it.</summary>
    private static string SettingsPath() =>
        Path.Combine(Path.GetTempPath(), "NitroLitePhase1_" + Guid.NewGuid().ToString("N") + ".json");

    /// <summary>
    /// A view model with no hardware backend. Every test here is about a decision the shell makes, not about a write
    /// reaching a fan, so a null controller is the honest fixture: it also proves the controls stay usable when the
    /// backend is missing.
    /// </summary>
    private static MainViewModel Create(
        AppSettings? settings = null,
        Func<(bool OnAc, int? BatteryPercent)>? powerState = null,
        IGameLibraryScanner? gameLibrary = null,
        IMouseProfileService? mouseProfiles = null,
        IDialogService? dialogs = null,
        string? settingsPath = null) =>
        new(settings ?? new AppSettings(),
            null,
            null,
            settingsPath ?? SettingsPath(),
            powerState: powerState,
            gameLibrary: gameLibrary,
            mouseProfiles: mouseProfiles,
            dialogs: dialogs);

    /// <summary>A scanner that answers from a fixed list and validates against a fixed set of accepted paths.</summary>
    private sealed class FakeGameLibrary : IGameLibraryScanner
    {
        public List<ScannedGame> Games { get; } = new();

        public HashSet<string> Accepted { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string? ValidationError { get; set; }

        public int Scans { get; private set; }

        public IReadOnlyList<ScannedGame> ScanInstalledGames()
        {
            Scans++;
            return Games;
        }

        public string? ValidateCustomExecutable(string path) =>
            ValidationError ?? (Accepted.Contains(path) ? null : "Seçilen dosya çalıştırılabilir değil.");

        public CpuBoostMode DetermineOptimalCpuBoost(string exeName) => CpuBoostMode.Aggressive;
    }

    /// <summary>A mouse that is either present with a profile, or absent.</summary>
    private sealed class FakeMouse : IMouseProfileService
    {
        public bool IsSupported { get; set; } = true;

        public int CurrentProfile { get; set; } = 2;

        public List<int> Applied { get; } = new();

        public Task<bool> ApplyProfileAsync(int profile, CancellationToken cancellationToken = default)
        {
            Applied.Add(profile);
            CurrentProfile = profile;
            return Task.FromResult(true);
        }

        public Task<int?> QueryCurrentProfileAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<int?>(CurrentProfile);
    }

    /// <summary>A dialog that answers from a script rather than from a window.</summary>
    private sealed class FakeDialogs : IDialogService
    {
        public string? PickResult { get; set; }

        public bool ConfirmResult { get; set; }

        public int ConfirmCalls { get; private set; }

        public string? LastConfirmMessage { get; private set; }

        public Task<string?> PickExecutableAsync(string? startDirectory = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(PickResult);

        public Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken = default)
        {
            ConfirmCalls++;
            LastConfirmMessage = message;
            return Task.FromResult(ConfirmResult);
        }
    }

    // ---------------------------------------------------------------- 1. Eco is a battery mode

    [Fact]
    public async Task Eco_HiddenOnAc()
    {
        // On the charger: Eco is not offered, and the three charger modes are.
        using (var onAc = Create(powerState: () => (true, 80)))
        {
            await onAc.InitializeAsync();

            Assert.True(onAc.OnAcPower);
            Assert.DoesNotContain(onAc.Modes, m => m.Mode == OperatingMode.Eco);
            Assert.Contains(onAc.Modes, m => m.Mode == OperatingMode.Turbo);
            Assert.Contains(onAc.Modes, m => m.Mode == OperatingMode.Extreme);
        }

        // On battery: Eco appears and the high-power modes go, which is the rule the WPF window applied.
        using (var onBattery = Create(powerState: () => (false, 55)))
        {
            await onBattery.InitializeAsync();

            Assert.False(onBattery.OnAcPower);
            Assert.Contains(onBattery.Modes, m => m.Mode == OperatingMode.Eco);
            Assert.DoesNotContain(onBattery.Modes, m => m.Mode == OperatingMode.Turbo);
            Assert.DoesNotContain(onBattery.Modes, m => m.Mode == OperatingMode.Extreme);
        }
    }

    [Fact]
    public void Eco_StaysVisibleWhileItIsTheActiveMode()
    {
        // A list with no marked card would leave the user guessing which mode the machine is running, so the active
        // mode survives the filter even when its group is hidden.
        var (showHighPower, showEco) = ModePolicy.VisibleModeGroups(onAc: true, OperatingMode.Eco);

        Assert.True(showEco);
        Assert.True(showHighPower);

        // On the charger with any other mode active, Eco is gone: it is a battery choice.
        var (_, ecoHidden) = ModePolicy.VisibleModeGroups(onAc: true, OperatingMode.Default);
        Assert.False(ecoHidden);

        // On battery, Turbo and Performance are gone unless one of them is the mode the machine is in.
        var (highPowerHidden, ecoShown) = ModePolicy.VisibleModeGroups(onAc: false, OperatingMode.Default);
        Assert.False(highPowerHidden);
        Assert.True(ecoShown);

        var (highPowerKept, _) = ModePolicy.VisibleModeGroups(onAc: false, OperatingMode.Turbo);
        Assert.True(highPowerKept);
    }

    // ---------------------------------------------------------------- 2. the rail's FARE row

    [Fact]
    public async Task SystemMouseRow_ShowsProfile()
    {
        var mouse = new FakeMouse { IsSupported = true, CurrentProfile = 3 };
        using (var vm = Create(mouseProfiles: mouse))
        {
            await vm.InitializeAsync();

            Assert.True(vm.MouseProfileAvailable);
            Assert.True(vm.ShowSystemMouseRow);
            Assert.Equal("Config 3", vm.SystemMouseText);
        }

        // A laptop with no ATK mouse draws no row at all: four configurations that write nowhere would be worse than
        // no row.
        var absent = new FakeMouse { IsSupported = false };
        using (var vm = Create(mouseProfiles: absent))
        {
            await vm.InitializeAsync();

            Assert.False(vm.MouseProfileAvailable);
            Assert.False(vm.ShowSystemMouseRow);
            Assert.Equal("—", vm.SystemMouseText);
        }
    }

    // ---------------------------------------------------------------- 3. the rail's width

    [Fact]
    public void SidebarWidth_Persists()
    {
        var path = SettingsPath();
        var settings = new AppSettings();

        using (var vm = Create(settings, settingsPath: path))
        {
            Assert.Equal(AppSettings.DefaultSidebarWidth, vm.SidebarWidth);

            vm.SidebarWidth = 260;
            Assert.Equal(260, vm.SidebarWidth);
            Assert.Equal(260, settings.SidebarWidth);
        }

        // The number reached the file, not just the object: a width that only lived in memory would be forgotten the
        // next time the window opened.
        var reloaded = SettingsManager.LoadSettingsWithFallback(path);
        Assert.Equal(260, reloaded.SidebarWidth);

        // A hand-edited file cannot leave the rail wider than the page or narrower than its longest label.
        using (var vm = Create(settingsPath: SettingsPath()))
        {
            vm.SidebarWidth = 40;
            Assert.Equal(AppSettings.MinSidebarWidth, vm.SidebarWidth);

            vm.SidebarWidth = 4000;
            Assert.Equal(AppSettings.MaxSidebarWidth, vm.SidebarWidth);
        }
    }

    // ---------------------------------------------------------------- 4. the window's placement

    [Fact]
    public void WindowPosition_RoundTrips()
    {
        var path = SettingsPath();
        var settings = new AppSettings();

        using (var vm = Create(settings, settingsPath: path))
        {
            var empty = vm.ReadWindowPlacement();
            Assert.Null(empty.Left);
            Assert.Null(empty.Top);
            Assert.False(empty.Maximized);

            vm.SaveWindowPlacement(120, 80, 1060, 730, maximized: true);

            var read = vm.ReadWindowPlacement();
            Assert.Equal(120, read.Left);
            Assert.Equal(80, read.Top);
            Assert.Equal(1060, read.Width);
            Assert.Equal(730, read.Height);
            Assert.True(read.Maximized);
        }

        var reloaded = SettingsManager.LoadSettingsWithFallback(path);
        Assert.Equal(120, reloaded.WindowLeft);
        Assert.Equal(80, reloaded.WindowTop);
        Assert.Equal(1060, reloaded.WindowWidth);
        Assert.Equal(730, reloaded.WindowHeight);
        Assert.True(reloaded.WindowMaximized);
    }

    // ---------------------------------------------------------------- 5. scanning the libraries

    [Fact]
    public async Task ScanGames_AddsProfiles()
    {
        var library = new FakeGameLibrary();
        library.Games.Add(new ScannedGame("Cyberpunk2077", "Cyberpunk 2077", @"D:\Games\Cyberpunk 2077\bin\Cyberpunk2077.exe"));
        library.Games.Add(new ScannedGame("cs2", "Counter-Strike 2", @"D:\Games\cs2\cs2.exe"));

        var settings = new AppSettings { GameProfiles = new List<GameProfile>() };

        using var vm = Create(settings, gameLibrary: library);
        await vm.InitializeAsync();

        Assert.True(vm.GameScanAvailable);
        Assert.Equal(vm.Labels.ScanGames, vm.GameScanButtonText);

        await vm.ScanGamesAsync();

        Assert.Equal(1, library.Scans);
        Assert.Equal(2, settings.GameProfiles.Count);
        Assert.Contains(settings.GameProfiles, p => p.ExeName == "Cyberpunk2077");
        Assert.Contains(settings.GameProfiles, p => p.ExeName == "cs2");

        // The path is kept, so the rule can be checked against a game that was uninstalled or moved.
        Assert.Equal(@"D:\Games\cs2\cs2.exe", settings.GameProfiles.Single(p => p.ExeName == "cs2").InstallPath);

        // The rows follow the rules, and the status says what happened rather than leaving the button looking inert.
        Assert.Equal(2, vm.GameProfiles.Count);
        Assert.Equal(vm.Labels.GamesScanned(2), vm.GameScanStatus);
        Assert.True(vm.HasGameScanStatus);

        // A second scan finds nothing new and says so instead of duplicating the rules.
        await vm.ScanGamesAsync();
        Assert.Equal(2, settings.GameProfiles.Count);
        Assert.Equal(vm.Labels.NoNewGames, vm.GameScanStatus);
    }

    [Fact]
    public async Task ScanGames_WithoutALibrary_SaysSo()
    {
        // A platform with no library scanner draws no scan button, and the file picker says why rather than doing
        // nothing at all.
        using var vm = Create(dialogs: new FakeDialogs());
        await vm.InitializeAsync();

        Assert.False(vm.GameScanAvailable);

        await vm.AddGameFromFileAsync();
        Assert.Equal(vm.Labels.GameScanUnavailable, vm.GameProfileStatus);
    }

    // ---------------------------------------------------------------- 6. adding a game by hand

    [Fact]
    public async Task AddGameFromFile_Validates()
    {
        var library = new FakeGameLibrary();
        var dialogs = new FakeDialogs();
        var settings = new AppSettings { GameProfiles = new List<GameProfile>() };

        using var vm = Create(settings, gameLibrary: library, dialogs: dialogs);
        await vm.InitializeAsync();

        // A refused file adds nothing and reports the reason the scanner gave.
        dialogs.PickResult = @"C:\Windows\System32\svchost.exe";
        library.ValidationError = "Seçilen dosya çalıştırılabilir değil.";

        await vm.AddGameFromFileAsync();

        Assert.Empty(settings.GameProfiles);
        Assert.Equal("Seçilen dosya çalıştırılabilir değil.", vm.GameProfileStatus);

        // A cancelled picker is not an error: nothing is added and nothing is said.
        dialogs.PickResult = null;
        library.ValidationError = null;
        await vm.AddGameFromFileAsync();
        Assert.Empty(settings.GameProfiles);

        // An accepted file becomes a rule, with the boost the scanner recommended for it.
        const string path = @"D:\Games\Elden Ring\Game\eldenring.exe";
        dialogs.PickResult = path;
        library.Accepted.Add(path);

        await vm.AddGameFromFileAsync();

        var added = Assert.Single(settings.GameProfiles);
        Assert.Equal("eldenring", added.ExeName);
        Assert.Equal(path, added.InstallPath);
        Assert.Equal(CpuBoostMode.Aggressive, added.TargetCpuBoost);
        Assert.Equal(vm.Labels.GameAdded(added.DisplayName), vm.GameProfileStatus);

        // Adding the same executable again refreshes the path rather than creating a second rule.
        await vm.AddGameFromFileAsync();
        Assert.Single(settings.GameProfiles);
        Assert.Equal(vm.Labels.GameAlreadyDefined, vm.GameProfileStatus);
    }

    // ---------------------------------------------------------------- 7. deleting a rule

    [Fact]
    public async Task DeleteGame_RequiresConfirm()
    {
        var dialogs = new FakeDialogs();
        var settings = new AppSettings
        {
            GameProfiles = new List<GameProfile>
            {
                new() { ExeName = "cs2", DisplayName = "Counter-Strike 2" }
            }
        };

        using var vm = Create(settings, gameLibrary: new FakeGameLibrary(), dialogs: dialogs);
        await vm.InitializeAsync();

        vm.SelectedGameProfile = vm.GameProfiles.Single();

        // A refused confirmation keeps the rule: it carries the mode, the boost and the curve the user configured,
        // and there is no undo.
        dialogs.ConfirmResult = false;
        await vm.DeleteSelectedGameAsync();

        Assert.Equal(1, dialogs.ConfirmCalls);
        Assert.Single(settings.GameProfiles);
        Assert.Contains("Counter-Strike 2", dialogs.LastConfirmMessage);

        dialogs.ConfirmResult = true;
        await vm.DeleteSelectedGameAsync();

        Assert.Equal(2, dialogs.ConfirmCalls);
        Assert.Empty(settings.GameProfiles);
        Assert.Empty(vm.GameProfiles);
    }

    // ---------------------------------------------------------------- 8. the curve presets

    [Fact]
    public void CurvePresets_Apply()
    {
        using var vm = Create();

        Assert.Equal(FanCurvePresets.All.Count, vm.CurvePresets.Count);
        Assert.All(vm.CurvePresets, preset => Assert.False(string.IsNullOrWhiteSpace(preset.Label)));

        // The quiet shape is the one that differs most from the factory curve, so it is the one that proves the write
        // reached the anchors rather than only the button.
        var quiet = vm.CurvePresets.Single(p => p.Id == "quiet");
        vm.SelectedCurvePreset = quiet;

        Assert.True(quiet.IsSelected);
        Assert.All(vm.CurvePresets.Where(p => p.Id != "quiet"), p => Assert.False(p.IsSelected));

        foreach (var point in quiet.Points)
        {
            var anchor = vm.CurveAnchors.Single(a => a.TemperatureC == point.TemperatureC);
            Assert.Equal(point.Percent, anchor.Percent);
        }

        // The safety floor is not a preset: the top anchor is still 100 %, whatever shape was chosen.
        Assert.Equal(100, vm.CurveAnchors.Single(a => a.TemperatureC == 90).Percent);

        // Moving one anchor makes the curve the user's own, and the preset stops claiming it.
        vm.CurveAnchors.Single(a => a.TemperatureC == 60).Percent = 44;
        Assert.DoesNotContain(vm.CurvePresets, p => p.IsSelected);
    }

    // ---------------------------------------------------------------- 9. the tray menu

    [Fact]
    public void TrayMenu_HasBoostSubmenu()
    {
        // The submenu carries exactly the three levels the firmware understands, in the order the WPF menu listed
        // them, so the tray and the GPU & CPU card cannot drift apart.
        Assert.Equal(
            new[] { CpuBoostMode.Disabled, CpuBoostMode.Efficient, CpuBoostMode.Aggressive },
            TrayMenuPolicy.BoostModes);

        // The card offers the same three, so the two lists cannot drift apart.
        using var vm = Create();
        Assert.Equal(
            TrayMenuPolicy.BoostModes.OrderBy(m => m),
            vm.CpuBoostChoices.Select(c => c.Mode).OrderBy(m => m));

        // A machine that cannot write the boost mode gets no submenu at all rather than three dead rows.
        Assert.False(TrayMenuPolicy.ShowBoost(available: false));
        Assert.True(TrayMenuPolicy.ShowBoost(available: true));
    }

    [Fact]
    public void TrayMenu_FiltersUnsupportedModes()
    {
        // A mode the firmware does not advertise is hidden.
        Assert.False(TrayMenuPolicy.ShowMode(supported: false, OperatingMode.Turbo, active: null));

        // Unless the machine is already in it: a menu with no marked row would leave the user guessing.
        Assert.True(TrayMenuPolicy.ShowMode(supported: false, OperatingMode.Turbo, OperatingMode.Turbo));

        // A supported mode is always drawn.
        Assert.True(TrayMenuPolicy.ShowMode(supported: true, OperatingMode.Quiet, active: null));

        // A mode the battery rule forbids stays visible but greyed out, with the reason in its tooltip.
        Assert.False(TrayMenuPolicy.EnableMode(allowed: false));
        Assert.Equal(ModePolicy.HighPowerBlockedMessage, TrayMenuPolicy.ModeToolTip(allowed: false));

        Assert.True(TrayMenuPolicy.EnableMode(allowed: true));
        Assert.Null(TrayMenuPolicy.ModeToolTip(allowed: true));

        // The retry row only exists while a desktop restore is pending, and a running game owns the mode, so the row
        // is not clickable then.
        Assert.False(TrayMenuPolicy.ShowRetry(pending: false));
        Assert.True(TrayMenuPolicy.ShowRetry(pending: true));
        Assert.False(TrayMenuPolicy.EnableRetry(pending: true, gameActive: true));
        Assert.True(TrayMenuPolicy.EnableRetry(pending: true, gameActive: false));
    }
}
