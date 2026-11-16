using System.Linq;
using NitroLite.Core;
using NitroLite.Core.Hardware;
using NitroLite.Core.Hardware.OpenRgb;
using NitroLite.Desktop;
using NitroLite.Desktop.ViewModels;
using NitroLite.Linux;
using NitroLite.Linux.Sysfs;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// The shell the warm-cream WPF window used to draw: a five-tab rail, a live <c>SİSTEM</c> box pinned to its foot, a
/// <c>60 SANİYE</c> band over the mode page, an <c>AKTİF</c> stamp on the chosen mode card and a click-to-paint colour
/// palette on the lighting page.
/// </summary>
/// <remarks>
/// <para>
/// These are the parts of the parity pass that are decisions rather than pixels: which tab is showing, what the rail
/// prints, whether the band is open, which target a swatch writes to. The pixels themselves are checked by looking at
/// the window; a test that asserted on a margin would fail the next time somebody nudged it and would not catch a
/// single real regression.
/// </para>
/// <para>
/// The OpenRGB client is pointed at a closed port so a developer running OpenRGB on 6742 cannot change a result.
/// </para>
/// </remarks>
public class WpfParityUiTests
{
    // ---------------------------------------------------------------- fixtures

    /// <summary>A Nitro 16 AN16-41 with both tiers, a tachometer and four advertised platform profiles.</summary>
    private static InMemorySysfsFileSystem Nitro16()
    {
        var fs = new InMemorySysfsFileSystem();

        fs.WithFile("/sys/class/dmi/id/product_name", "Nitro AN16-41\n");
        fs.WithFile("/sys/class/dmi/id/board_name", "AN16-41\n");
        fs.WithFile("/sys/class/dmi/id/sys_vendor", "Acer\n");
        fs.WithFile("/sys/class/dmi/id/bios_version", "V1.27\n");
        fs.WithFile("/sys/class/dmi/id/bios_vendor", "Insyde\n");

        fs.WithFile(LinuxPaths.PlatformProfile, "balanced\n");
        fs.WithFile(LinuxPaths.PlatformProfileChoices, "quiet balanced balanced-performance performance\n");

        fs.WithFile("/sys/class/hwmon/hwmon0/name", "acer\n");
        fs.WithFile("/sys/class/hwmon/hwmon0/temp1_input", "55000\n");
        fs.WithFile("/sys/class/hwmon/hwmon0/fan1_input", "2436\n");
        fs.WithFile("/sys/class/hwmon/hwmon0/fan2_input", "2409\n");
        fs.WithFile("/sys/class/hwmon/hwmon1/name", "k10temp\n");
        fs.WithFile("/sys/class/hwmon/hwmon1/temp1_input", "63000\n");
        fs.WithFile("/sys/class/hwmon/hwmon2/name", "amdgpu\n");
        fs.WithFile("/sys/class/hwmon/hwmon2/temp1_input", "57000\n");

        fs.WithFile("/sys/class/power_supply/BAT1/charge_control_end_threshold", "100\n");

        fs.WithDirectory(LinuxPaths.AcpiPlatformRoot);

        var sense = LinuxPaths.Combine(LinuxPaths.LinuwuModuleRoot, LinuxPaths.NitroSense);
        fs.WithDirectory(sense);
        fs.WithFile(LinuxPaths.Combine(sense, "fan_speed"), "60,50\n");
        fs.WithFile(LinuxPaths.Combine(sense, "backlight_timeout"), "0\n");
        fs.WithFile(LinuxPaths.Combine(sense, "battery_limiter"), "0\n");
        fs.WithFile(LinuxPaths.Combine(sense, "lcd_override"), "0\n");
        fs.WithFile(LinuxPaths.Combine(sense, "cool_boost"), "0\n");

        var keyboard = LinuxPaths.Combine(sense, LinuxPaths.FourZoneKeyboard);
        fs.WithDirectory(keyboard);
        fs.WithFile(LinuxPaths.Combine(keyboard, "per_zone_mode"), "00E5FF,00E5FF,00E5FF,00E5FF,55\n");
        fs.WithFile(LinuxPaths.Combine(keyboard, "four_zone_mode"), "3,3,55,1,0,229,255\n");

        return fs;
    }

    /// <summary>The embedded model table only, so a developer's own profiles.user.json cannot change a result.</summary>
    private static ProfileCatalog Catalog() =>
        ProfileCatalog.FromJson(ProfileCatalog.ReadEmbeddedJson(), null, out _);

    private static (MainViewModel Vm, LinuxSysfsHardwareController Controller) Create(
        InMemorySysfsFileSystem fs,
        AppSettings? settings = null,
        Func<(bool OnAc, int? BatteryPercent)>? powerState = null)
    {
        var appSettings = settings ?? new AppSettings();

        var controller = LinuxSysfsHardwareController.Create(
            appSettings, fs, Catalog(), new OpenRgbClient(port: 1));

        var vm = new MainViewModel(
            appSettings,
            controller,
            controller.Capabilities,
            AppPaths.GetDataFile("settings.json"),
            powerState: powerState);
        return (vm, controller);
    }

    // ---------------------------------------------------------------- 1. the five-tab rail

    [Fact]
    public void Rail_HasTheFiveWpfTabsInOrder()
    {
        var (vm, controller) = Create(Nitro16());
        using var _ = vm;
        using var __ = controller;

        Assert.Equal(5, vm.NavTabs.Count);
        Assert.Equal(
            new[]
            {
                MainViewModel.NavModesKey,
                MainViewModel.NavMonitoringKey,
                MainViewModel.NavLightingKey,
                MainViewModel.NavBatteryKey,
                MainViewModel.NavToolsKey
            },
            vm.NavTabs.Select(t => t.Key));

        // Every tab carries a label and a vector icon; a tab with an empty path would render as a blank rail row.
        Assert.All(vm.NavTabs, tab =>
        {
            Assert.False(string.IsNullOrWhiteSpace(tab.Label));
            Assert.False(string.IsNullOrWhiteSpace(tab.Icon));
        });
    }

    [Fact]
    public void Rail_StartsOnTheModePage()
    {
        var (vm, controller) = Create(Nitro16());
        using var _ = vm;
        using var __ = controller;

        Assert.Equal(MainViewModel.NavModesKey, vm.SelectedTab?.Key);
        Assert.True(vm.IsModesPage);
        Assert.True(vm.NavTabs[0].IsSelected);
        Assert.False(vm.NavTabs[1].IsSelected);
    }

    [Theory]
    [InlineData(MainViewModel.NavModesKey)]
    [InlineData(MainViewModel.NavMonitoringKey)]
    [InlineData(MainViewModel.NavLightingKey)]
    [InlineData(MainViewModel.NavBatteryKey)]
    [InlineData(MainViewModel.NavToolsKey)]
    public void SelectTab_ShowsExactlyOnePage(string key)
    {
        var (vm, controller) = Create(Nitro16());
        using var _ = vm;
        using var __ = controller;

        vm.SelectTab(key);

        var flags = new[]
        {
            vm.IsModesPage, vm.IsMonitoringPage, vm.IsLightingPage, vm.IsBatteryPage, vm.IsToolsPage
        };

        Assert.Single(flags, f => f);
        Assert.Equal(key, vm.SelectedTab?.Key);
        Assert.Single(vm.NavTabs, t => t.IsSelected);
        Assert.Equal(key, vm.NavTabs.Single(t => t.IsSelected).Key);
    }

    [Fact]
    public void NavTabCommand_SelectsItsOwnTab()
    {
        var (vm, controller) = Create(Nitro16());
        using var _ = vm;
        using var __ = controller;

        vm.NavTabs[3].SelectCommand.Execute(null);

        Assert.True(vm.IsBatteryPage);
        Assert.Equal(MainViewModel.NavBatteryKey, vm.SelectedTab?.Key);
    }

    [Fact]
    public void SelectTab_IgnoresAnUnknownKey()
    {
        var (vm, controller) = Create(Nitro16());
        using var _ = vm;
        using var __ = controller;

        vm.SelectTab(MainViewModel.NavLightingKey);
        vm.SelectTab("nope");

        Assert.True(vm.IsLightingPage);
    }

    // ---------------------------------------------------------------- 2. the pinned SİSTEM box

    [Fact]
    public async Task SystemBox_PrintsTheSameFiguresAsTheCards()
    {
        var (vm, controller) = Create(Nitro16());
        using var _ = vm;
        using var __ = controller;

        await vm.InitializeAsync();
        await vm.RefreshTelemetryAsync();

        Assert.Equal(vm.CpuTemperatureText, vm.SystemCpuText);
        Assert.Equal(vm.GpuTemperatureText, vm.SystemGpuText);
        Assert.Equal(vm.CpuFanRpmText, vm.SystemFanText);

        // The rail row is one line wide, so it carries the number and its unit rather than a sentence.
        Assert.Contains("°C", vm.SystemCpuText);
        Assert.Contains("RPM", vm.SystemFanText);
    }

    [Fact]
    public void SystemBox_ShowsAnEmDashWhenThereIsNoBoostOrGame()
    {
        var (vm, controller) = Create(Nitro16());
        using var _ = vm;
        using var __ = controller;

        // No ICpuBoostAccess was composed, and no game has been detected yet.
        Assert.Equal("—", vm.SystemBoostText);
        Assert.Equal("—", vm.SystemGameText);
    }

    [Fact]
    public void VersionBadge_IsAVersionString()
    {
        var (vm, controller) = Create(Nitro16());
        using var _ = vm;
        using var __ = controller;

        Assert.StartsWith("v", vm.VersionText);
        Assert.Matches(@"^v\d+\.\d+\.\d+", vm.VersionText);
    }

    // ---------------------------------------------------------------- 3. the AKTİF mode stamp

    [Fact]
    public void ModeCards_CarryTheActiveStampAndExactlyOneIsSelected()
    {
        var (vm, controller) = Create(Nitro16());
        using var _ = vm;
        using var __ = controller;

        Assert.NotEmpty(vm.Modes);
        Assert.All(vm.Modes, card => Assert.Equal(vm.Labels.ActiveBadge, card.ActiveLabel));
        Assert.Single(vm.Modes, card => card.IsSelected);
        Assert.Equal(vm.SelectedMode?.Mode, vm.Modes.Single(card => card.IsSelected).Mode);
    }

    [Fact]
    public void ModeCards_MoveTheStampWhenTheModeChanges()
    {
        var (vm, controller) = Create(Nitro16());
        using var _ = vm;
        using var __ = controller;

        var target = vm.Modes.First(card => !card.IsSelected);
        vm.SelectedMode = target;

        Assert.True(target.IsSelected);
        Assert.Single(vm.Modes, card => card.IsSelected);
        Assert.Equal(target.Label.ToUpperInvariant(), vm.ModeBadgeText);
    }

    [Fact]
    public void ModeCards_EachCarryAShortHint()
    {
        var (vm, controller) = Create(Nitro16());
        using var _ = vm;
        using var __ = controller;

        Assert.All(vm.Modes, card => Assert.False(string.IsNullOrWhiteSpace(card.ShortHint)));
    }

    // ---------------------------------------------------------------- 4. the 60 SANİYE band

    [Fact]
    public void SparklineBand_StartsOpenAndTogglesItsCaption()
    {
        var (vm, controller) = Create(Nitro16());
        using var _ = vm;
        using var __ = controller;

        Assert.True(vm.SparklineBandVisible);
        Assert.Equal(vm.Labels.HideCaption, vm.SparklineBandToggleText);

        vm.ToggleSparklineBandCommand.Execute(null);

        Assert.False(vm.SparklineBandVisible);
        Assert.Equal(vm.Labels.ShowCaption, vm.SparklineBandToggleText);

        vm.ToggleSparklineBandCommand.Execute(null);

        Assert.True(vm.SparklineBandVisible);
    }

    [Fact]
    public void SparklineBand_ReportsAnEmDashRangeBeforeAnySample()
    {
        var (vm, controller) = Create(Nitro16());
        using var _ = vm;
        using var __ = controller;

        Assert.Equal($"{vm.Labels.RangeCaption} —", vm.SparklineRangeText);
        Assert.StartsWith(vm.Labels.NowCaption, vm.SparklineNowText);
    }

    [Fact]
    public async Task SparklineBand_ReportsTheRangeOnceSamplesArrive()
    {
        var (vm, controller) = Create(Nitro16());
        using var _ = vm;
        using var __ = controller;

        await vm.InitializeAsync();
        await vm.RefreshTelemetryAsync();

        Assert.Contains("–", vm.SparklineRangeText);
        Assert.EndsWith("°C", vm.SparklineRangeText);
        Assert.Contains("°C", vm.SparklineNowText);
    }

    // ---------------------------------------------------------------- 5. the colour palette

    [Fact]
    public void Palette_HasEightSwatchesWithDistinctColours()
    {
        var (vm, controller) = Create(Nitro16());
        using var _ = vm;
        using var __ = controller;

        Assert.Equal(8, vm.ColorPalette.Count);
        Assert.Equal(8, vm.ColorPalette.Select(s => s.Hex).Distinct().Count());
        Assert.All(vm.ColorPalette, swatch =>
        {
            Assert.StartsWith("#", swatch.Hex);
            Assert.Equal(7, swatch.Hex.Length);
            Assert.False(string.IsNullOrWhiteSpace(swatch.Name));
        });
    }

    [Fact]
    public void Palette_StartsOnThePrimaryColour()
    {
        var (vm, controller) = Create(Nitro16());
        using var _ = vm;
        using var __ = controller;

        Assert.Equal(0, vm.ActiveZone);
        Assert.True(vm.IsPrimaryZoneActive);
        Assert.False(vm.IsZone1Active);
        Assert.Equal(vm.Labels.PrimaryColor, vm.ActiveZoneText);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void ZonePills_ReportWhichTargetIsActive(int zone)
    {
        var (vm, controller) = Create(Nitro16());
        using var _ = vm;
        using var __ = controller;

        vm.ActiveZone = zone;

        var flags = new[]
        {
            vm.IsPrimaryZoneActive, vm.IsZone1Active, vm.IsZone2Active, vm.IsZone3Active, vm.IsZone4Active
        };

        Assert.Single(flags, f => f);
        Assert.Equal(zone, System.Array.IndexOf(flags, true));
        Assert.Equal(vm.Labels.Zone(zone), vm.ActiveZoneText);
    }

    [Fact]
    public void ZoneCommands_MoveTheActiveTarget()
    {
        var (vm, controller) = Create(Nitro16());
        using var _ = vm;
        using var __ = controller;

        vm.SelectZone3Command.Execute(null);
        Assert.Equal(3, vm.ActiveZone);

        vm.SelectPrimaryZoneCommand.Execute(null);
        Assert.Equal(0, vm.ActiveZone);
    }

    [Fact]
    public void SwatchClick_WritesIntoTheActiveZone()
    {
        var (vm, controller) = Create(Nitro16());
        using var _ = vm;
        using var __ = controller;

        vm.ActiveZone = 2;
        var swatch = vm.ColorPalette[4];
        swatch.SelectCommand.Execute(null);

        Assert.Equal(swatch.Hex, vm.Zone2);
        Assert.Same(swatch, vm.SelectedSwatch);
        Assert.True(swatch.IsSelected);

        // The other zones are untouched: a swatch paints one target, not the whole keyboard.
        Assert.NotEqual(swatch.Hex, vm.Zone1);
        Assert.NotEqual(swatch.Hex, vm.Zone3);
    }

    [Fact]
    public void SwatchClick_OnThePrimaryTargetWritesThePrimaryColour()
    {
        var (vm, controller) = Create(Nitro16());
        using var _ = vm;
        using var __ = controller;

        var swatch = vm.ColorPalette[1];
        swatch.SelectCommand.Execute(null);

        Assert.Equal(swatch.Hex, vm.PrimaryColor);
        Assert.True(swatch.IsSelected);
    }

    [Fact]
    public void SwatchSelection_FollowsTheZoneItIsPointedAt()
    {
        var (vm, controller) = Create(Nitro16());
        using var _ = vm;
        using var __ = controller;

        // Paint zone 1 with the sixth swatch, then look at the primary colour: the tick has to move with the target.
        vm.ActiveZone = 1;
        var swatch = vm.ColorPalette[5];
        swatch.SelectCommand.Execute(null);
        Assert.True(swatch.IsSelected);

        vm.ActiveZone = 0;
        Assert.False(swatch.IsSelected);
    }

    // ---------------------------------------------------------------- 6. the fan segmented control

    [Fact]
    public void FanSegments_AreTheFourWpfPills()
    {
        var (vm, controller) = Create(Nitro16());
        using var _ = vm;
        using var __ = controller;

        Assert.Equal(4, vm.FanSegments.Count);
        Assert.Equal(
            new[]
            {
                FanSegmentKind.Auto, FanSegmentKind.Max, FanSegmentKind.Custom, FanSegmentKind.Curve
            },
            vm.FanSegments.Select(s => s.Kind));
        Assert.Single(vm.FanSegments, s => s.IsSelected);
    }

    [Fact]
    public void FanSegments_FollowTheSelectedFanMode()
    {
        var (vm, controller) = Create(Nitro16());
        using var _ = vm;
        using var __ = controller;

        var max = vm.FanModes.First(f => f.Mode == FanMode.Max);
        vm.SelectedFanMode = max;

        Assert.Equal(FanSegmentKind.Max, vm.SelectedFanSegment?.Kind);
        Assert.False(vm.CurveEditorVisible);
    }

    [Fact]
    public void FanSegments_CurveOpensTheEditorAndLeavesTheModeAlone()
    {
        var (vm, controller) = Create(Nitro16());
        using var _ = vm;
        using var __ = controller;

        var before = vm.SelectedFanMode?.Mode;

        var curve = vm.FanSegments.First(s => s.Kind == FanSegmentKind.Curve);
        curve.SelectCommand.Execute(null);

        Assert.True(vm.CurveEnabled);
        Assert.True(vm.CurveEditorVisible);
        Assert.Equal(FanSegmentKind.Curve, vm.SelectedFanSegment?.Kind);

        // The curve is not a firmware mode, so choosing it must not move the fan card.
        Assert.Equal(before, vm.SelectedFanMode?.Mode);
    }

    [Fact]
    public void FanSegments_LeavingTheCurveTurnsItOff()
    {
        var (vm, controller) = Create(Nitro16());
        using var _ = vm;
        using var __ = controller;

        vm.FanSegments.First(s => s.Kind == FanSegmentKind.Curve).SelectCommand.Execute(null);
        Assert.True(vm.CurveEnabled);

        vm.FanSegments.First(s => s.Kind == FanSegmentKind.Auto).SelectCommand.Execute(null);

        Assert.False(vm.CurveEnabled);
        Assert.False(vm.CurveEditorVisible);
        Assert.Equal(FanMode.Auto, vm.SelectedFanMode?.Mode);
    }

    [Fact]
    public async Task FanBars_AreScaledAgainstTheDisplayCeiling()
    {
        var (vm, controller) = Create(Nitro16());
        using var _ = vm;
        using var __ = controller;

        await vm.InitializeAsync();
        await vm.RefreshTelemetryAsync();

        Assert.Equal(MainViewModel.FanBarCeilingRpm, 6000);
        Assert.InRange(vm.CpuFanBarPercent, 0, 100);
        Assert.InRange(vm.GpuFanBarPercent, 0, 100);
        Assert.Equal(vm.CpuFanRpm * 100.0 / MainViewModel.FanBarCeilingRpm, vm.CpuFanBarPercent, 3);
    }

    // ---------------------------------------------------------------- 7. the header's battery pill

    [Fact]
    public async Task BatteryPill_PrintsThePercentageAndTheChargerState()
    {
        var (vm, controller) = Create(Nitro16(), powerState: () => (true, 80));
        using var _ = vm;
        using var __ = controller;

        await vm.InitializeAsync();
        await vm.RefreshTelemetryAsync();

        Assert.Equal("%80", vm.BatteryPercentText);
        Assert.Equal(80d, vm.BatteryBarPercent);
        Assert.Equal(vm.Labels.PluggedIn, vm.BatteryStateText);
    }

    [Fact]
    public async Task BatteryPill_ReadsOnBatteryWhenTheChargerIsOut()
    {
        var (vm, controller) = Create(Nitro16(), powerState: () => (false, 42));
        using var _ = vm;
        using var __ = controller;

        await vm.InitializeAsync();
        await vm.RefreshTelemetryAsync();

        Assert.Equal("%42", vm.BatteryPercentText);
        Assert.Equal(42d, vm.BatteryBarPercent);
        Assert.Equal(vm.Labels.OnBattery, vm.BatteryStateText);
    }

    [Fact]
    public void BatteryPill_IsAnEmDashWithoutAPowerReader()
    {
        var (vm, controller) = Create(Nitro16());
        using var _ = vm;
        using var __ = controller;

        Assert.Equal("%—", vm.BatteryPercentText);
        Assert.Equal(0d, vm.BatteryBarPercent);
        Assert.Equal(vm.Labels.Unknown, vm.BatteryStateText);
    }

    [Fact]
    public async Task BatteryBar_ClampsAnOutOfRangeReading()
    {
        var (vm, controller) = Create(Nitro16(), powerState: () => (false, 140));
        using var _ = vm;
        using var __ = controller;

        await vm.InitializeAsync();
        await vm.RefreshTelemetryAsync();

        Assert.Equal(100d, vm.BatteryBarPercent);
    }
}
