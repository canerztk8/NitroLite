using System.IO;
using Avalonia.Media;
using NitroLite.Core;
using NitroLite.Core.Hardware;
using NitroLite.Core.Hardware.OpenRgb;
using NitroLite.Desktop;
using NitroLite.Desktop.Themes;
using NitroLite.Desktop.ViewModels;
using NitroLite.Linux;
using NitroLite.Linux.Sysfs;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// The cross-platform view model, driven end to end against an in-memory <c>/sys</c> tree.
/// </summary>
/// <remarks>
/// <para>
/// The view model holds no Avalonia types, which is the whole reason these tests can exist: the window, the timer and
/// the clipboard are the only parts that need a display, and none of them are here. What is left is the part that
/// decides what the user sees — whether the RPM number is a measurement or a guess, which modes the firmware
/// advertises, whether the curve respects the thermal floors, and whether the udev banner appears.
/// </para>
/// <para>
/// The OpenRGB client is pointed at a closed port so a developer who happens to be running OpenRGB on 6742 cannot
/// change a test result.
/// </para>
/// </remarks>
public class DesktopViewModelTests
{
    // ---------------------------------------------------------------- fixtures

    /// <summary>A Nitro 16 AN16-41 with both tiers, a tachometer and four advertised platform profiles.</summary>
    private static InMemorySysfsFileSystem Nitro16()
    {
        var fs = new InMemorySysfsFileSystem();

        Dmi(fs, "Nitro AN16-41", "AN16-41", "V1.27");

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
        fs.WithDirectory(SenseRoot());
        fs.WithFile(Sense("fan_speed"), "60,50\n");
        fs.WithFile(Sense("backlight_timeout"), "0\n");
        fs.WithFile(Sense("battery_limiter"), "0\n");
        fs.WithFile(Sense("lcd_override"), "0\n");
        fs.WithFile(Sense("cool_boost"), "0\n");

        fs.WithDirectory(KeyboardRoot());
        fs.WithFile(Keyboard("per_zone_mode"), "00E5FF,00E5FF,00E5FF,00E5FF,55\n");
        fs.WithFile(Keyboard("four_zone_mode"), "3,3,55,1,0,229,255\n");

        return fs;
    }

    /// <summary>The same machine with no <c>fan*_input</c>: the kernel exposes control but no tachometer.</summary>
    private static InMemorySysfsFileSystem Nitro16WithoutTachometer()
    {
        var fs = Nitro16();
        fs.WithoutFile("/sys/class/hwmon/hwmon0/fan1_input");
        fs.WithoutFile("/sys/class/hwmon/hwmon0/fan2_input");
        return fs;
    }

    private static void Dmi(InMemorySysfsFileSystem fs, string product, string board, string bios)
    {
        fs.WithFile("/sys/class/dmi/id/product_name", product + "\n");
        fs.WithFile("/sys/class/dmi/id/board_name", board + "\n");
        fs.WithFile("/sys/class/dmi/id/sys_vendor", "Acer\n");
        fs.WithFile("/sys/class/dmi/id/bios_version", bios + "\n");
        fs.WithFile("/sys/class/dmi/id/bios_vendor", "Insyde\n");
    }

    private static string SenseRoot() =>
        LinuxPaths.Combine(LinuxPaths.LinuwuModuleRoot, LinuxPaths.NitroSense);

    private static string Sense(string name) => LinuxPaths.Combine(SenseRoot(), name);

    private static string KeyboardRoot() => LinuxPaths.Combine(SenseRoot(), LinuxPaths.FourZoneKeyboard);

    private static string Keyboard(string name) => LinuxPaths.Combine(KeyboardRoot(), name);

    /// <summary>The embedded model table only, so a developer's own profiles.user.json cannot change a result.</summary>
    private static ProfileCatalog Catalog() =>
        ProfileCatalog.FromJson(ProfileCatalog.ReadEmbeddedJson(), null, out _);

    /// <summary>
    /// A view model wired to a real <see cref="LinuxSysfsHardwareController"/> over <paramref name="fs"/>.
    /// </summary>
    /// <remarks>
    /// <paramref name="settingsPath"/> defaults to the real data folder, which is what the window uses. A test that
    /// asserts on what was written passes a temporary file instead, so it neither reads nor overwrites the settings of
    /// whoever is running the suite.
    /// </remarks>
    private static (MainViewModel Vm, LinuxSysfsHardwareController Controller) Create(
        InMemorySysfsFileSystem fs,
        AppSettings? settings = null,
        IThemeApplier? themeApplier = null,
        string? settingsPath = null,
        Phase3Fakes.UrlCapturer? urlCapturer = null)
    {
        var appSettings = settings ?? new AppSettings();

        // Port 1 is closed on every machine, so the OpenRGB probe answers "nothing listening" deterministically
        // instead of depending on whether the developer runs OpenRGB.
        var controller = LinuxSysfsHardwareController.Create(
            appSettings, fs, Catalog(), new OpenRgbClient(port: 1));

        var vm = new MainViewModel(
            appSettings,
            controller,
            controller.Capabilities,
            settingsPath ?? AppPaths.GetDataFile("settings.json"),
            themeApplier: themeApplier,
            urlOpener: urlCapturer?.Capture);
        return (vm, controller);
    }

    /// <summary>Records what the view model asked the window to paint, without needing a window.</summary>
    private sealed class RecordingThemeApplier : IThemeApplier
    {
        public List<AppTheme> Applied { get; } = new();

        public List<string> Fonts { get; } = new();

        public void Apply(AppTheme theme) => Applied.Add(theme);

        public void ApplyFont(string fontStack) => Fonts.Add(fontStack);
    }

    /// <summary>
    /// Waits for a condition the view model reaches on a fire-and-forget task. A card click cannot be awaited from a
    /// test, so the assertion polls instead of sleeping for a fixed time.
    /// </summary>
    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(10);
        }

        Assert.True(condition(), "the condition did not become true within " + timeoutMs + " ms");
    }

    // ---------------------------------------------------------------- 1. telemetry

    [Fact]
    public async Task Telemetry_WithATachometer_ReportsMeasuredRpm()
    {
        var (vm, controller) = Create(Nitro16());
        using var _ = vm;
        using var __ = controller;

        await vm.InitializeAsync();
        await vm.RefreshTelemetryAsync();

        Assert.True(vm.FanRpmMeasured);
        Assert.Equal(vm.Labels.Measured, vm.FanRpmSourceText);
        Assert.Equal(2436, vm.CpuFanRpm);
        Assert.Equal(2409, vm.GpuFanRpm);
        Assert.Equal("2436 RPM", vm.CpuFanRpmText);

        // The three temperatures come from three different hwmon devices, which is the point of the name lookup.
        Assert.Equal(63, vm.CpuTemperature);
        Assert.Equal(57, vm.GpuTemperature);
        Assert.Equal(55, vm.SysTemperature);
    }

    [Fact]
    public async Task Telemetry_WithoutATachometer_ReportsDerivedRpm()
    {
        var (vm, controller) = Create(Nitro16WithoutTachometer());
        using var _ = vm;
        using var __ = controller;

        await vm.InitializeAsync();
        await vm.RefreshTelemetryAsync();

        Assert.False(vm.FanRpmMeasured);
        Assert.Equal(vm.Labels.Derived, vm.FanRpmSourceText);

        // 60 % of the profile's 6200 RPM ceiling and 50 % of 6000, not a measurement.
        Assert.Equal(LinuxSysfsHardwareController.DeriveRpm(60, 6200), vm.CpuFanRpm);
        Assert.Equal(LinuxSysfsHardwareController.DeriveRpm(50, 6000), vm.GpuFanRpm);
        Assert.Equal(3720, vm.CpuFanRpm);
        Assert.Equal(3000, vm.GpuFanRpm);
    }

    // ---------------------------------------------------------------- 2. operating modes

    [Fact]
    public async Task Modes_ListOnlyWhatTheFirmwareAdvertises()
    {
        var (vm, controller) = Create(Nitro16());
        using var _ = vm;
        using var __ = controller;

        await vm.InitializeAsync();

        // The fixture advertises quiet, balanced, balanced-performance and performance — no low-power, so no Eco.
        Assert.Equal(
            new[] { OperatingMode.Quiet, OperatingMode.Default, OperatingMode.Extreme, OperatingMode.Turbo },
            vm.Modes.Select(m => m.Mode).ToArray());
        Assert.DoesNotContain(vm.Modes, m => m.Mode == OperatingMode.Eco);
        Assert.True(vm.ModeControlAvailable);
        Assert.NotNull(vm.SelectedMode);
    }

    [Fact]
    public async Task Modes_TheSavedActiveModeStaysACard_EvenWhenTheFirmwareListOmitsIt()
    {
        // The fixture's list has no Eco, yet the machine is running low-power and the user last chose Eco. The card for
        // the running mode must exist, or the read-back finds nothing to select and the selection stays on Quiet.
        var fs = Nitro16();
        fs.WithFile(LinuxPaths.PlatformProfile, "low-power\n");
        var settings = new AppSettings { LastOperatingMode = OperatingMode.Eco };
        var (vm, controller) = Create(fs, settings);
        using var _ = vm;
        using var __ = controller;

        await vm.InitializeAsync();

        Assert.True(vm.Modes.Any(m => m.Mode == OperatingMode.Eco),
            $"selected={vm.SelectedMode?.Mode} last={settings.LastOperatingMode} ac={vm.OnAcPower} observed={await controller.GetOperatingModeAsync()}");
        Assert.Equal(OperatingMode.Eco, vm.SelectedMode?.Mode);
        // The cards the firmware did list are still there, in their usual order.
        Assert.Equal(
            new[] { OperatingMode.Quiet, OperatingMode.Default, OperatingMode.Extreme, OperatingMode.Turbo, OperatingMode.Eco },
            vm.Modes.Select(m => m.Mode).ToArray());
    }

    [Fact]
    public async Task Modes_FallBackToTheFullSetWhenTheFirmwareDoesNotAnswer()
    {
        var fs = Nitro16();
        fs.WithoutFile(LinuxPaths.PlatformProfileChoices);

        var (vm, controller) = Create(fs);
        using var _ = vm;
        using var __ = controller;

        await vm.InitializeAsync();

        // An unreadable choices file is "not asked", never "no mode works": hiding every button would be a lie.
        Assert.Equal(5, vm.Modes.Count);
        Assert.Contains(vm.Modes, m => m.Mode == OperatingMode.Eco);
    }

    // ---------------------------------------------------------------- 3. the fan curve

    [Fact]
    public async Task Curve_KeepsTheThermalFloorsWhenTheUserDragsAnAnchorDown()
    {
        var (vm, controller) = Create(Nitro16());
        using var _ = vm;
        using var __ = controller;

        await vm.InitializeAsync();

        Assert.Equal(6, vm.CurveAnchors.Count);
        Assert.Equal(new[] { 40, 50, 60, 70, 80, 90 }, vm.CurveAnchors.Select(a => a.TemperatureC).ToArray());

        var anchor90 = vm.CurveAnchors.Single(a => a.TemperatureC == 90);
        anchor90.Percent = 30;

        // The slider keeps what the user set; the floor is what the fans get.
        Assert.Equal(30, anchor90.Percent);
        Assert.Equal(80, anchor90.EffectivePercent);
        Assert.Equal("%80", anchor90.EffectiveText);

        // And the curve the write path builds agrees, because it applies the same floor.
        var curve = new FanCurve(vm.CurvePoints);
        Assert.True(curve.PercentAt(90) >= 80);
        Assert.True(curve.PercentAt(95) >= 100);
        Assert.True(curve.PercentAt(85) >= 60);
    }

    [Fact]
    public async Task Curve_ClampsAnAnchorToThePwmRange()
    {
        var (vm, controller) = Create(Nitro16());
        using var _ = vm;
        using var __ = controller;

        await vm.InitializeAsync();

        var anchor = vm.CurveAnchors.Single(a => a.TemperatureC == 60);
        anchor.Percent = 5;
        Assert.Equal(FanCurve.MinPercent, anchor.Percent);

        anchor.Percent = 500;
        Assert.Equal(FanCurve.MaxPercent, anchor.Percent);
    }

    [Fact]
    public async Task Curve_EditingAnAnchorRaisesThePreviewAndTheDrawing()
    {
        var (vm, controller) = Create(Nitro16());
        using var _ = vm;
        using var __ = controller;

        await vm.InitializeAsync();

        var changed = new List<string>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? "");

        vm.CurveAnchors.Single(a => a.TemperatureC == 70).Percent = 55;

        Assert.Contains(nameof(MainViewModel.CurvePoints), changed);
        Assert.Contains(nameof(MainViewModel.CurvePreview), changed);
        Assert.Equal(55, vm.CurvePoints.Single(p => p.TemperatureC == 70).Percent);
    }

    // ---------------------------------------------------------------- 4. the udev banner

    [Fact]
    public async Task UdevBanner_AppearsWhenTheNodesExistButCannotBeWritten()
    {
        var fs = Nitro16();
        fs.DenyWrite(LinuxPaths.PlatformProfile);

        var (vm, controller) = Create(fs);
        using var _ = vm;
        using var __ = controller;

        await vm.InitializeAsync();

        Assert.True(vm.NeedsUdevRule);
        Assert.False(string.IsNullOrWhiteSpace(vm.UdevCommand));
        Assert.Contains("99-nitrolite.rules", vm.UdevCommand);
        Assert.Contains("udevadm", vm.UdevCommand);
    }

    [Fact]
    public async Task UdevBanner_StaysHiddenWhenTheNodesAreWritable()
    {
        var (vm, controller) = Create(Nitro16());
        using var _ = vm;
        using var __ = controller;

        await vm.InitializeAsync();

        Assert.False(vm.NeedsUdevRule);
        Assert.True(string.IsNullOrEmpty(vm.UdevCommand));
    }

    // ---------------------------------------------------------------- 5. no Acer nodes at all

    [Fact]
    public async Task NoAcerNodes_DoesNotThrow_AndSaysSoInsteadOfClaimingReady()
    {
        var (vm, controller) = Create(new InMemorySysfsFileSystem());
        using var _ = vm;
        using var __ = controller;

        await vm.InitializeAsync();

        Assert.True(vm.HasNoBackend);
        Assert.False(vm.HasBackend);
        Assert.False(vm.IsReady);
        Assert.Equal(vm.Labels.NoAcerNodes, vm.Status);
        Assert.Equal(vm.Labels.NoAcerNodes, vm.NoBackendDetail);

        // Every control that needs hardware is off, so the window offers nothing that cannot work.
        Assert.False(vm.FanControlAvailable);
        Assert.False(vm.ModeControlAvailable);
        Assert.False(vm.LightingAvailable);
        Assert.False(vm.CoolBoostAvailable);
        Assert.False(vm.LcdOverdriveAvailable);
        Assert.False(vm.BacklightTimeoutAvailable);
        Assert.False(vm.BatteryLimitAvailable);
    }

    [Fact]
    public async Task NoAcerNodes_TelemetryTickIsANoOpRatherThanAnException()
    {
        var (vm, controller) = Create(new InMemorySysfsFileSystem());
        using var _ = vm;
        using var __ = controller;

        await vm.InitializeAsync();
        await vm.RefreshTelemetryAsync();
        await vm.RefreshTelemetryAsync();

        Assert.Equal(0, vm.CpuTemperature);
        Assert.Equal(0, vm.CpuFanRpm);
    }

    [Fact]
    public void NoBackendAtAll_StillConstructsAndExplainsItself()
    {
        using var vm = new MainViewModel(new AppSettings(), null, null, AppPaths.GetDataFile("settings.json"));

        Assert.True(vm.HasNoBackend);
        Assert.Equal(vm.Labels.NoBackend, vm.Status);
        Assert.Equal(vm.Labels.NoBackendDetail, vm.NoBackendDetail);
        Assert.Equal(vm.Labels.NoBackend, vm.CapabilitySummaryText);
    }

    // ---------------------------------------------------------------- 6. the settings page

    [Fact]
    public async Task SettingsPage_NamesTheDeviceAndTheTier()
    {
        var (vm, controller) = Create(Nitro16());
        using var _ = vm;
        using var __ = controller;

        await vm.InitializeAsync();

        Assert.Contains("AN16-41", vm.DeviceModel);
        Assert.Equal("V1.27", vm.DeviceBios);
        Assert.Equal("both", vm.TierText);
        Assert.Contains(vm.Labels.CapFanControl, vm.CapabilitySummaryText);
        Assert.Contains("✓", vm.CapabilitySummaryText);
    }

    [Fact]
    public async Task SettingsPage_LanguageChangeRepaintsEveryLabel()
    {
        var (vm, controller) = Create(Nitro16());
        using var _ = vm;
        using var __ = controller;

        await vm.InitializeAsync();

        var before = vm.Labels.Dashboard;
        var english = Loc.Languages.Single(l => l.Code == "en");

        // Loc is process-wide, so the original language goes back in the finally: leaving it on English would make
        // every other test that reads a Turkish key fail for a reason that has nothing to do with it.
        var original = Loc.Language;
        try
        {
            vm.SelectedLanguage = english;

            Assert.Equal("en", Loc.Language);
            Assert.Equal("Dashboard", vm.Labels.Dashboard);
            Assert.NotEqual(before, vm.Labels.Dashboard);
        }
        finally
        {
            Loc.SetLanguage(original);
        }
    }

    // ---------------------------------------------------------------- 7. the theme engine

    [Fact]
    public void Theme_ConstructorPaintsTheStoredPaletteBeforeTheWindowExists()
    {
        var settings = new AppSettings { Theme = AppTheme.NordFrost };
        var applier = new RecordingThemeApplier();

        var (vm, controller) = Create(Nitro16(), settings, applier);
        using var _ = vm;
        using var __ = controller;

        // Exactly one call, at construction: the first frame is already the right colour instead of flashing the
        // default and repainting a moment later.
        Assert.Equal(new[] { AppTheme.NordFrost }, applier.Applied);
        Assert.Equal(AppTheme.NordFrost, vm.SelectedTheme?.Theme);
    }

    [Fact]
    public void Theme_PickerOffersEveryPaletteInTheCatalogue()
    {
        var (vm, controller) = Create(Nitro16());
        using var _ = vm;
        using var __ = controller;

        Assert.Equal(ThemeCatalog.All, vm.Themes.Select(t => t.Theme).ToArray());
        Assert.All(vm.Themes, t => Assert.False(string.IsNullOrWhiteSpace(t.Label)));
    }

    [Fact]
    public void Theme_PickingAPaletteAppliesItAndWritesItToTheSettingsFile()
    {
        var folder = Path.Combine(Path.GetTempPath(), "nitrolite-theme-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(folder, "settings.json");
        var settings = new AppSettings { Theme = AppTheme.Dark };
        var applier = new RecordingThemeApplier();

        var (vm, controller) = Create(Nitro16(), settings, applier, path);
        using var _ = vm;
        using var __ = controller;

        try
        {
            vm.SelectedTheme = vm.Themes.Single(t => t.Theme == AppTheme.AmberConsole);

            Assert.Equal(new[] { AppTheme.Dark, AppTheme.AmberConsole }, applier.Applied);
            Assert.Equal(AppTheme.AmberConsole, settings.Theme);
            Assert.Equal(vm.Labels.ThemeApplied, vm.Status);

            // The choice survives a restart because it reached the file, not only the object in memory.
            Assert.Contains("\"AmberConsole\"", File.ReadAllText(path));
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Theme_ChangingTheLanguageRenamesThePickerWithoutRepainting()
    {
        var folder = Path.Combine(Path.GetTempPath(), "nitrolite-theme-" + Guid.NewGuid().ToString("N"));
        var applier = new RecordingThemeApplier();

        var (vm, controller) = Create(Nitro16(), null, applier, Path.Combine(folder, "settings.json"));
        using var _ = vm;
        using var __ = controller;

        var before = vm.Themes.Single(t => t.Theme == AppTheme.Dark).Label;
        var original = Loc.Language;
        try
        {
            vm.SelectedLanguage = Loc.Languages.Single(l => l.Code == "en");

            Assert.Equal("Dark", vm.Themes.Single(t => t.Theme == AppTheme.Dark).Label);
            Assert.NotEqual(before, vm.Themes.Single(t => t.Theme == AppTheme.Dark).Label);

            // Rebuilding the list is not a user action: the palette is not re-applied and the file is not rewritten.
            Assert.Single(applier.Applied);
        }
        finally
        {
            Loc.SetLanguage(original);
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Theme_ResolveTurnsTheSystemPreferenceIntoAConcretePalette()
    {
        // A test host has no Application, so the desktop cannot answer and the fallback is the light palette. What
        // matters is that "System" never reaches the palette table: it is a preference, not a colour.
        var resolved = DesktopThemeManager.Resolve(AppTheme.System);

        Assert.NotEqual(AppTheme.System, resolved);
        Assert.Contains(resolved, new[] { AppTheme.WarmCream, AppTheme.Dark });
    }

    [Fact]
    public void Theme_EveryPaletteCarriesEveryBrushTheViewsAskFor()
    {
        // These keys are the contract between Themes/ThemePalette.cs and the DynamicResource references in the XAML.
        // A missing one is not a build error — DynamicResource resolves at runtime — so it is asserted here instead.
        var expected = new[]
        {
            "PageBrush", "CardBrush", "CardHoverBrush", "PanelBrush", "RailBrush",
            "CardBorderBrush", "CardBorderStrongBrush",
            "TextPrimaryBrush", "TextSecondaryBrush", "TextMutedBrush",
            "AccentBrush", "AccentHoverBrush", "AccentSoftBrush", "OnAccentBrush",
            "SuccessBrush", "SuccessSoftBrush", "WarningBrush", "WarningSoftBrush",
            "DangerBrush", "DangerSoftBrush", "ChartFillBrush",

            // Fluent's own slider and toggle-switch keys. Without them the controls fall back to the system accent,
            // which is how the fan-curve thumbs ended up red in an ice-blue window.
            "SliderThumbBackground", "SliderThumbBackgroundPointerOver", "SliderThumbBackgroundPressed",
            "SliderThumbBackgroundDisabled", "SliderTrackValueFill", "SliderTrackValueFillPointerOver",
            "SliderTrackValueFillPressed", "SliderTrackValueFillDisabled", "SliderTrackFill",
            "SliderTrackFillPointerOver", "SliderTrackFillPressed", "SliderTrackFillDisabled",
            "ToggleSwitchFillOn", "ToggleSwitchFillOnPointerOver", "ToggleSwitchFillOnPressed",
            "ToggleSwitchStrokeOn", "ToggleSwitchStrokeOnPointerOver", "ToggleSwitchStrokeOnPressed",
            "ToggleSwitchKnobFillOn", "ToggleSwitchKnobFillOnPointerOver", "ToggleSwitchKnobFillOnPressed"
        };

        foreach (var theme in ThemeCatalog.All)
        {
            var resources = DesktopThemeManager.BuildResources(theme);
            foreach (var key in expected)
            {
                Assert.True(resources.ContainsKey(key), $"{theme} is missing {key}");
            }
        }
    }

    [Fact]
    public void Theme_EveryPaletteHasItsOwnPageColour()
    {
        // A copy-paste slip in the palette table would leave two themes looking identical, which no build would catch.
        var pages = ThemeCatalog.All
            .Where(t => t != AppTheme.System)
            .Select(t => ((ISolidColorBrush)DesktopThemeManager.BuildResources(t)["PageBrush"]!).Color)
            .ToArray();

        Assert.Equal(pages.Length, pages.Distinct().Count());
    }

    // ---------------------------------------------------------------- 8. the sparkline history

    [Fact]
    public void Sparkline_KeepsTheMostRecentSamplesOldestFirst()
    {
        var history = new SparklineHistory(capacity: 3);

        history.Add(1);
        history.Add(2);
        history.Add(3);
        history.Add(4);
        history.Add(5);

        Assert.Equal(3, history.Capacity);
        Assert.Equal(3, history.Count);
        Assert.Equal(new double[] { 3, 4, 5 }, history.Snapshot());
    }

    [Fact]
    public void Sparkline_IgnoresASampleThatIsNotANumber()
    {
        var history = new SparklineHistory(capacity: 4);
        history.Add(10);

        history.Add(double.NaN);
        history.Add(double.PositiveInfinity);

        // A NaN would poison the chart's own range and flatten every real sample against it.
        Assert.Equal(1, history.Count);
        Assert.Equal(new double[] { 10 }, history.Snapshot());
    }

    [Fact]
    public void Sparkline_SnapshotIsANewArraySoTheChartSeesAChange()
    {
        var history = new SparklineHistory();
        history.Add(42);

        var first = history.Snapshot();
        var second = history.Snapshot();

        // Avalonia only re-renders when the property value changes, so handing back the same instance would leave the
        // chart drawing the first sixty samples forever.
        Assert.NotSame(first, second);
        Assert.Equal(first, second);
    }

    [Fact]
    public void Sparkline_EmptyHistoryIsAnEmptyArrayRatherThanNull()
    {
        Assert.Empty(new SparklineHistory().Snapshot());
    }

    [Fact]
    public void Sparkline_RefusesAWindowTooShortToDraw()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SparklineHistory(capacity: 1));
    }

    [Fact]
    public async Task Sparkline_DashboardHistoryGrowsWithEachTelemetryTick()
    {
        var (vm, controller) = Create(Nitro16());
        using var _ = vm;
        using var __ = controller;

        await vm.InitializeAsync();
        Assert.Empty(vm.CpuTemperatureHistory);

        await vm.RefreshTelemetryAsync();
        await vm.RefreshTelemetryAsync();
        await vm.RefreshTelemetryAsync();

        Assert.Equal(new double[] { 63, 63, 63 }, vm.CpuTemperatureHistory);
        Assert.Equal(new double[] { 57, 57, 57 }, vm.GpuTemperatureHistory);
        Assert.Equal(new double[] { 2436, 2436, 2436 }, vm.CpuFanHistory);
        Assert.Equal(new double[] { 2409, 2409, 2409 }, vm.GpuFanHistory);
    }

    [Fact]
    public async Task Sparkline_DashboardHistoryStopsAtSixtySamples()
    {
        var (vm, controller) = Create(Nitro16());
        using var _ = vm;
        using var __ = controller;

        await vm.InitializeAsync();
        for (int i = 0; i < MainViewModel.HistoryLength + 5; i++) await vm.RefreshTelemetryAsync();

        Assert.Equal(60, MainViewModel.HistoryLength);
        Assert.Equal(60, vm.CpuTemperatureHistory.Count);
        Assert.Equal(60, vm.CpuFanHistory.Count);
    }

    [Fact]
    public async Task Sparkline_ASilentSensorIsNotRecordedButAStoppedFanIs()
    {
        var fs = Nitro16();
        fs.WithoutFile("/sys/class/hwmon/hwmon0/temp1_input");
        fs.WithoutFile("/sys/class/hwmon/hwmon1/temp1_input");
        fs.WithoutFile("/sys/class/hwmon/hwmon2/temp1_input");

        var (vm, controller) = Create(fs);
        using var _ = vm;
        using var __ = controller;

        await vm.InitializeAsync();
        await vm.RefreshTelemetryAsync();

        // Zero degrees means the sensor did not answer, so there is nothing to plot. Zero RPM means the fans are
        // stopped, which is a real reading and belongs on the chart.
        Assert.Equal(0, vm.CpuTemperature);
        Assert.Empty(vm.CpuTemperatureHistory);
        Assert.Single(vm.CpuFanHistory);
    }

    // ---------------------------------------------------------------- 9. the single-click mode and fan cards

    [Fact]
    public async Task ModeCards_AClickAppliesTheModeWithoutAnApplyButton()
    {
        var fs = Nitro16();
        var settings = new AppSettings();

        var (vm, controller) = Create(fs, settings);
        using var _ = vm;
        using var __ = controller;

        await vm.InitializeAsync();

        // The card is the control: there is no separate Apply button left to press.
        vm.Modes.Single(m => m.Mode == OperatingMode.Turbo).SelectCommand.Execute(null);

        Assert.Equal(OperatingMode.Turbo, vm.SelectedMode?.Mode);

        // The settings field is written after the node, so waiting on it also guarantees the write has landed.
        await WaitForAsync(() => settings.LastOperatingMode == OperatingMode.Turbo);
        Assert.Equal("performance", fs.ReadAllText(LinuxPaths.PlatformProfile));
    }

    [Fact]
    public async Task ModeCards_ExactlyOneCardCarriesTheAccent()
    {
        var (vm, controller) = Create(Nitro16());
        using var _ = vm;
        using var __ = controller;

        await vm.InitializeAsync();

        Assert.Single(vm.Modes, m => m.IsSelected);

        vm.Modes.Single(m => m.Mode == OperatingMode.Quiet).SelectCommand.Execute(null);

        // The accent moves rather than spreading: two highlighted cards would leave the user guessing which is live.
        Assert.Single(vm.Modes, m => m.IsSelected);
        Assert.True(vm.Modes.Single(m => m.Mode == OperatingMode.Quiet).IsSelected);
    }

    [Fact]
    public async Task ModeCards_AreListedButDisabledWhenThereIsNoBackend()
    {
        var (vm, controller) = Create(new InMemorySysfsFileSystem());
        using var _ = vm;
        using var __ = controller;

        await vm.InitializeAsync();

        // The cards stay on screen so the page keeps its shape; the view binds IsEnabled to these two flags.
        Assert.False(vm.ModeControlAvailable);
        Assert.False(vm.FanControlAvailable);
        Assert.Equal(5, vm.Modes.Count);
        Assert.Equal(3, vm.FanModes.Count);
    }

    [Fact]
    public async Task FanCards_AClickAppliesTheFanModeWithoutAnApplyButton()
    {
        var fs = Nitro16();

        var (vm, controller) = Create(fs);
        using var _ = vm;
        using var __ = controller;

        await vm.InitializeAsync();

        vm.FanModes.Single(f => f.Mode == FanMode.Max).SelectCommand.Execute(null);

        Assert.Equal(FanMode.Max, vm.SelectedFanMode?.Mode);
        await WaitForAsync(() => fs.ReadAllText(Sense("fan_speed")) == "100,100");
    }

    [Fact]
    public async Task FanCards_ExactlyOneCardCarriesTheAccent()
    {
        var (vm, controller) = Create(Nitro16());
        using var _ = vm;
        using var __ = controller;

        await vm.InitializeAsync();

        Assert.Single(vm.FanModes, f => f.IsSelected);

        vm.FanModes.Single(f => f.Mode == FanMode.Custom).SelectCommand.Execute(null);

        Assert.Single(vm.FanModes, f => f.IsSelected);
        Assert.True(vm.CustomFanVisible);
    }

    // ---------------------------------------------------------------- 10. the containers that hide themselves

    [Fact]
    public async Task EmptyContainers_TheDevicePillHidesItselfWhenTheProbeFoundNoName()
    {
        var (named, namedController) = Create(Nitro16());
        using var _ = named;
        using var __ = namedController;

        await named.InitializeAsync();
        Assert.True(named.HasDeviceName);

        var (anonymous, anonymousController) = Create(new InMemorySysfsFileSystem());
        using var ___ = anonymous;
        using var ____ = anonymousController;

        await anonymous.InitializeAsync();

        // No DMI answer means no name, and an empty pill in the header is worse than no pill at all.
        Assert.True(string.IsNullOrWhiteSpace(anonymous.DeviceModel));
        Assert.False(anonymous.HasDeviceName);
    }

    [Fact]
    public async Task EmptyContainers_TheProbeReportsWhatItCouldNotRead()
    {
        var (healthy, healthyController) = Create(Nitro16());
        using var _ = healthy;
        using var __ = healthyController;

        await healthy.InitializeAsync();

        // Every node answered, so there is nothing to warn about.
        Assert.Empty(healthy.Notes);

        var (bare, bareController) = Create(new InMemorySysfsFileSystem());
        using var ___ = bare;
        using var ____ = bareController;

        await bare.InitializeAsync();

        // The notes are no longer drawn — the settings page ends with the support card — but the probe's answer is
        // still the view model's, and the hardware report quotes it.
        Assert.NotEmpty(bare.Notes);
    }

    // ---------------------------------------------------------------- 11. the capability list

    [Fact]
    public async Task CapabilityList_IsStructuredRowsRatherThanABlockOfText()
    {
        var (vm, controller) = Create(Nitro16());
        using var _ = vm;
        using var __ = controller;

        await vm.InitializeAsync();

        Assert.NotEmpty(vm.CapabilityRows);
        Assert.All(vm.CapabilityRows, row => Assert.False(string.IsNullOrWhiteSpace(row.Label)));
        Assert.All(vm.CapabilityRows, row => Assert.Contains(row.Glyph, new[] { "✓", "✕" }));

        var fan = vm.CapabilityRows.Single(r => r.Label == vm.Labels.CapFanControl);
        Assert.True(fan.Supported);
        Assert.Equal("✓", fan.Glyph);
        Assert.Equal(Loc.T("Destekleniyor"), fan.StateText);
    }

    [Fact]
    public async Task CapabilityList_DoesNotAdvertiseCoolBoostOrLcdOverdrive()
    {
        var (vm, controller) = Create(Nitro16());
        using var _ = vm;
        using var __ = controller;

        await vm.InitializeAsync();

        // Neither feature is part of the app, so the first-run walkthrough must not claim to check for them.
        Assert.DoesNotContain(vm.CapabilityRows, row => row.Label == Loc.T("CoolBoost"));
        Assert.DoesNotContain(vm.CapabilityRows, row => row.Label == Loc.T("LCD overdrive"));
    }

    [Fact]
    public async Task CapabilityList_TheTierIsTranslatedInsteadOfShowingTheRawDiagnostic()
    {
        var (vm, controller) = Create(Nitro16());
        using var _ = vm;
        using var __ = controller;

        await vm.InitializeAsync();

        // TierText stays the raw diagnostic the probe reported; the page binds TierDisplay.
        Assert.Equal("both", vm.TierText);
        Assert.Equal(vm.Labels.TierBoth, vm.TierDisplay);
        Assert.NotEqual(vm.TierText, vm.TierDisplay);

        var (bare, bareController) = Create(new InMemorySysfsFileSystem());
        using var ___ = bare;
        using var ____ = bareController;

        await bare.InitializeAsync();

        Assert.Equal("none", bare.TierText);
        Assert.Equal(bare.Labels.TierNone, bare.TierDisplay);
        Assert.DoesNotContain("none", bare.TierDisplay);
    }

    // ---------------------------------------------------------------- 12. the game profiles page

    [Fact]
    public async Task GameProfiles_AddingARuleStoresItAndListsIt()
    {
        var folder = Path.Combine(Path.GetTempPath(), "nitrolite-games-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(folder, "settings.json");
        var settings = new AppSettings { GameProfiles = new List<GameProfile>() };

        var (vm, controller) = Create(Nitro16(), settings, settingsPath: path);
        using var _ = vm;
        using var __ = controller;

        try
        {
            await vm.InitializeAsync();
            Assert.True(vm.HasNoGameProfiles);

            vm.NewGameExe = "cs2";
            vm.NewGameName = "Counter-Strike 2";
            vm.NewGameMode = vm.GameModeChoices.Single(m => m.Mode == OperatingMode.Turbo);
            vm.NewGameFanMode = vm.GameFanChoices.Single(f => f.Mode == FanMode.Max);
            vm.AddGameProfileCommand.Execute(null);

            var row = Assert.Single(vm.GameProfiles);
            Assert.Equal("cs2", row.ExeName);
            Assert.Equal("Counter-Strike 2", row.DisplayName);
            Assert.Equal(vm.Labels.Max, row.FanLabel);
            Assert.Equal(vm.Labels.DeleteProfile, row.DeleteLabel);
            Assert.True(vm.HasGameProfiles);
            Assert.False(vm.HasNoGameProfiles);
            Assert.Equal(vm.Labels.GameAdded("Counter-Strike 2"), vm.GameProfileStatus);
            Assert.True(vm.HasGameProfileStatus);

            // The service reads the settings object on every poll, so the rule has to be there, not only on screen.
            var stored = Assert.Single(settings.GameProfiles);
            Assert.Equal("cs2", stored.ExeName);
            Assert.Equal(OperatingMode.Turbo, stored.TargetMode);
            Assert.Equal(FanMode.Max, stored.TargetFanMode);
            Assert.True(stored.AutoApply);

            // And it survives a restart because it reached the file.
            Assert.Contains("cs2", File.ReadAllText(path));

            // The form is cleared so the next rule does not inherit the last one's name.
            Assert.Equal("", vm.NewGameExe);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task GameProfiles_RefusesAnEmptyNameAndADuplicate()
    {
        var folder = Path.Combine(Path.GetTempPath(), "nitrolite-games-" + Guid.NewGuid().ToString("N"));
        var settings = new AppSettings { GameProfiles = new List<GameProfile>() };

        var (vm, controller) = Create(Nitro16(), settings, settingsPath: Path.Combine(folder, "settings.json"));
        using var _ = vm;
        using var __ = controller;

        try
        {
            await vm.InitializeAsync();

            vm.AddGameProfileCommand.Execute(null);
            Assert.Equal(vm.Labels.GameExeRequired, vm.GameProfileStatus);
            Assert.Empty(vm.GameProfiles);

            vm.NewGameExe = "cs2";
            vm.AddGameProfileCommand.Execute(null);
            Assert.Single(vm.GameProfiles);

            // Case-insensitive: a user typing the same title twice should not end up with two rules for it.
            vm.NewGameExe = "CS2";
            vm.AddGameProfileCommand.Execute(null);

            Assert.Equal(vm.Labels.GameAlreadyDefined, vm.GameProfileStatus);
            Assert.Single(vm.GameProfiles);
            Assert.Single(settings.GameProfiles);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task GameProfiles_DeletingARowRemovesItFromTheSettingsFile()
    {
        var folder = Path.Combine(Path.GetTempPath(), "nitrolite-games-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(folder, "settings.json");
        var settings = new AppSettings
        {
            GameProfiles = new List<GameProfile>
            {
                new() { ExeName = "cs2", DisplayName = "Counter-Strike 2", TargetMode = OperatingMode.Turbo },
                new() { ExeName = "dota2", DisplayName = "Dota 2", TargetMode = OperatingMode.Default }
            }
        };

        var (vm, controller) = Create(Nitro16(), settings, settingsPath: path);
        using var _ = vm;
        using var __ = controller;

        try
        {
            await vm.InitializeAsync();
            Assert.Equal(2, vm.GameProfiles.Count);

            vm.GameProfiles.Single(r => r.ExeName == "cs2").DeleteCommand.Execute(null);

            var remaining = Assert.Single(vm.GameProfiles);
            Assert.Equal("dota2", remaining.ExeName);
            Assert.Equal(vm.Labels.GameDeleted("Counter-Strike 2"), vm.GameProfileStatus);

            // Gone from the object the service polls and from the file the next launch reads.
            Assert.Equal("dota2", Assert.Single(settings.GameProfiles).ExeName);
            Assert.DoesNotContain("cs2", File.ReadAllText(path));
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task Linux_NoKeyboardPath_CoversTheLightingPage_AndSendsNothing()
    {
        var fs = Nitro16();
        fs.WithoutFile(Keyboard("per_zone_mode"));
        fs.WithoutFile(Keyboard("four_zone_mode"));

        var (vm, controller) = Create(fs);
        using var _ = vm;
        using var __ = controller;
        await vm.InitializeAsync();

        // The Linux probe enumerates the real nodes, so its "no keyboard" is an answer: the page says why instead of
        // offering a deck of controls whose every press fails.
        Assert.True(vm.LightingUnsupported);
        Assert.False(vm.LightingControlsEnabled);

        var writesBefore = fs.Writes.Count;
        vm.ApplyLightingCommand.Execute(null);
        await Task.Delay(50);
        Assert.Equal(writesBefore, fs.Writes.Count);
    }

    [Fact]
    public async Task Linux_WithAKeyboardPath_KeepsTheLightingPage()
    {
        var (vm, controller) = Create(Nitro16());
        using var _ = vm;
        using var __ = controller;
        await vm.InitializeAsync();

        Assert.False(vm.LightingUnsupported);
        Assert.True(vm.LightingControlsEnabled);
    }
}
