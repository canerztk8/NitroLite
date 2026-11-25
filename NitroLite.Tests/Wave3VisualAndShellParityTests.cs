using System.Globalization;
using System.IO;
using System.Text;
using NitroLite.Core;
using NitroLite.Desktop;
using NitroLite.Desktop.ViewModels;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// The third wave: the danger button class, the labels the audit found mis-worded, the battery and monitoring repairs,
/// and the tray and shell parity the WPF window had.
/// </summary>
/// <remarks>
/// <para>
/// The page tests read the markup rather than a rendered window. Every defect this wave closes was a wrong binding or a
/// missing class on a control that already existed, and the only way any of them can come back is someone editing the
/// markup — so the markup is what the test asserts on.
/// </para>
/// <para>
/// The tray tests are the exception: <see cref="TrayMenuPolicy"/> is a pure function of what the view model knows, so
/// the menu's order and its tick are asserted directly rather than through a <c>TrayIcon</c> that needs a desktop.
/// </para>
/// </remarks>
public class Wave3VisualAndShellParityTests
{
    // =============================================================================================================
    // 1. The danger class: a destructive action never looks like an apply.
    // =============================================================================================================

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "NitroLite.sln")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string Markup(string view) =>
        File.ReadAllText(Path.Combine(RepoRoot(), "NitroLite.Desktop", "Views", view), Encoding.UTF8);

    private static string AppMarkup() =>
        File.ReadAllText(Path.Combine(RepoRoot(), "NitroLite.Desktop", "App.axaml"), Encoding.UTF8);

    private static string ScriptMarkup() =>
        File.ReadAllText(Path.Combine(RepoRoot(), "scripts", "screenshot.ps1"), Encoding.UTF8);

    [Fact]
    public void DangerClass_IsDefinedWithItsOwnFrameAndHoverWash()
    {
        var markup = AppMarkup();

        // The class exists, and it is a frame rather than a red label on a flat button: a red label reads as a link,
        // and a link is not what a delete is.
        Assert.Contains("Selector=\"Button.danger\"", markup, StringComparison.Ordinal);
        Assert.Contains("Selector=\"Button.danger /template/ ContentPresenter\"", markup, StringComparison.Ordinal);
        Assert.Contains("Selector=\"Button.danger:pointerover /template/ ContentPresenter\"", markup,
            StringComparison.Ordinal);

        // The rule and the label are the danger colour, and the wash under the pointer is the danger at 14 %.
        var danger = markup.IndexOf("Selector=\"Button.danger\"", StringComparison.Ordinal);
        var block = markup.Substring(danger, 900);
        Assert.Contains("DangerBrush", block, StringComparison.Ordinal);
        Assert.Contains("DangerSoftBrush", block, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("DashboardView.axaml", "DeleteSelectedGameCommand")]
    [InlineData("LightingView.axaml", "DeleteRgbProfileCommand")]
    [InlineData("BatteryView.axaml", "ClearWearHistoryCommand")]
    [InlineData("SettingsView.axaml", "DisableServicesCommand")]
    [InlineData("SettingsView.axaml", "KillNitroSenseCommand")]
    [InlineData("SettingsView.axaml", "OptimizeCommand")]
    [InlineData("SettingsView.axaml", "RestoreOptimizeCommand")]
    public void DangerClass_IsOnEveryDestructiveAction(string view, string command)
    {
        var markup = Markup(view);

        var at = markup.IndexOf($"Command=\"{{Binding {command}}}\"", StringComparison.Ordinal);
        Assert.True(at > 0, $"{command} is not bound in {view}");

        // The class sits on the same element, so the 400 characters before the command are the button's own attributes.
        var start = Math.Max(0, at - 400);
        var element = markup.Substring(start, at - start);
        Assert.Contains("Classes=\"danger\"", element, StringComparison.Ordinal);

        // The old spelling — a primary button with a red label — is gone, because it read as a link.
        Assert.DoesNotContain("Classes=\"primary\" Content=\"{Binding Labels.DeleteProfile}\"", element,
            StringComparison.Ordinal);
    }

    // =============================================================================================================
    // 2. The labels: the WPF wording, the colon, and the two badges that answered the wrong question.
    // =============================================================================================================

    [Fact]
    public void MonitoringLabels_UseTheWpfWording()
    {
        using var vm = new MainViewModel(new AppSettings(), null, null, TempSettingsPath());
        var labels = vm.Labels;

        // The processor's figure is the package, the graphics card's is the TGP. One word for both was the defect.
        Assert.Equal("Paket Güç Tüketimi:", labels.PackagePower);
        Assert.Equal("TGP Güç Tüketimi:", labels.TgpPower);
        Assert.NotEqual(labels.PackagePower, labels.TgpPower);

        // The row labels carry their colon, as the WPF page wrote them.
        Assert.Equal("Sıcaklık:", labels.Temperature);
        Assert.Equal("İşlemci Kullanımı:", labels.CpuUsage);
        Assert.Equal("GPU Kullanımı:", labels.GpuUsage);
        Assert.Equal("VRAM Kullanımı:", labels.Vram);
        Assert.Equal("Sistem Belleği (RAM)", labels.SystemMemory);

        // The chart's own title and subtitle.
        Assert.Equal("Telemetri Geçmiş Grafiği", labels.TelemetryCharts);
        Assert.Equal("Seçili metriğin son 60 saniyesi", labels.LastMinute);
    }

    [Fact]
    public void MonitoringBadges_AnswerTheirOwnQuestion()
    {
        var markup = Markup("MonitoringView.axaml");

        // The system-temperature badge explains the sensor, not the thermal floors: the floors are a fan-curve
        // contract and have nothing to do with where SYS1 sits on the board.
        Assert.Contains("ToolTip.Tip=\"{Binding Labels.SysTemperatureHint}\"", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("ToolTip.Tip=\"{Binding Labels.ThermalFloorSummary}\"", markup, StringComparison.Ordinal);

        // The chart badge explains the gaps, not the empty state: the empty state is already printed inside the plot.
        Assert.Contains("ToolTip.Tip=\"{Binding Labels.ChartGapHint}\"", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("ToolTip.Tip=\"{Binding Labels.ChartEmptyHint}\"", markup, StringComparison.Ordinal);

        // The empty-state sentence is still drawn, in the plot, where it belongs.
        Assert.Contains("Text=\"{Binding Labels.ChartEmptyHint}\"", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void SystemTemperatureRow_HasNoBar()
    {
        var markup = Markup("MonitoringView.axaml");

        // A bar under an unpublished sensor implies a scale the number does not have. The reading is the whole answer.
        Assert.DoesNotContain("Value=\"{Binding SysTemperature}\"", markup, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding SysTemperatureText}\"", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void ZoneNames_CarryTheirPosition()
    {
        using var vm = new MainViewModel(new AppSettings(), null, null, TempSettingsPath());
        var labels = vm.Labels;

        // Four bars in a row: "Bölge 3" alone leaves the user counting from the left.
        Assert.Equal("Bölge 1 (Sol)", labels.Zone(1));
        Assert.Equal("Bölge 2 (Orta Sol)", labels.Zone(2));
        Assert.Equal("Bölge 3 (Orta Sağ)", labels.Zone(3));
        Assert.Equal("Bölge 4 (Sayısal)", labels.Zone(4));

        // The four properties the deck and the palette both read are the same four names.
        Assert.Equal(labels.Zone(1), labels.Zone1);
        Assert.Equal(labels.Zone(4), labels.Zone4);
    }

    // =============================================================================================================
    // 3. The brightness figure: "0 / 9 (Kapalı)" rather than a second "Parlaklık:".
    // =============================================================================================================

    [Fact]
    public void BrightnessLabel_AtZero_SaysOff()
    {
        using var vm = new MainViewModel(new AppSettings(), null, null, TempSettingsPath());

        vm.BrightnessLevel = 0;
        Assert.Equal("0 / 9 (Kapalı)", vm.BrightnessLabel);

        vm.BrightnessLevel = 5;
        Assert.Equal("5 / 9", vm.BrightnessLabel);

        // The word "Parlaklık" is printed to the left of the slider, so the figure never repeats it.
        Assert.DoesNotContain("Parlaklık", vm.BrightnessLabel, StringComparison.Ordinal);
    }

    [Fact]
    public void SpeedLabel_IsTheFigureAlone()
    {
        using var vm = new MainViewModel(new AppSettings(), null, null, TempSettingsPath());

        vm.SpeedLevel = 3;
        Assert.Equal("3 / 9", vm.SpeedLabel);
        Assert.DoesNotContain("Efekt Hızı", vm.SpeedLabel, StringComparison.Ordinal);
    }

    [Fact]
    public void LightingSliders_PrintTheFigureInPlainInk()
    {
        var markup = Markup("LightingView.axaml");

        // The figure is not painted in the accent: it is a value, not a call to action, and the accent is what the
        // page uses for the one live signal.
        var at = markup.IndexOf("Text=\"{Binding BrightnessLabel}\"", StringComparison.Ordinal);
        Assert.True(at > 0, "the brightness figure is not bound");

        var start = markup.LastIndexOf("<TextBlock", at, StringComparison.Ordinal);
        Assert.True(start > 0, "the brightness figure is not inside a TextBlock");

        var element = markup.Substring(start, at - start);
        Assert.DoesNotContain("AccentBrush", element, StringComparison.Ordinal);
    }

    // =============================================================================================================
    // 4. The battery page: a chart only when there is a curve, and a state beside the switch.
    // =============================================================================================================

    private static string TempSettingsPath() =>
        Path.Combine(Path.GetTempPath(), "nitrolite-wave3-" + Guid.NewGuid().ToString("N"), "settings.json");

    [Fact]
    public async Task WearTrend_WithOneReading_HidesTheChartAndExplainsWhy()
    {
        var battery = new Phase3Fakes.FakeBattery();
        battery.SeedWear(6.0);

        using var vm = new MainViewModel(
            new AppSettings(), null, null, TempSettingsPath(), batteryHealth: battery);

        await vm.InitializeAsync();

        // One seeded reading plus the one the first read records is two, which is a curve. Clearing leaves none, and
        // the chart goes with it rather than staying as an empty frame.
        Assert.True(vm.ShowWearTrendChart);

        vm.ClearWearHistoryCommand.Execute(null);
        await Phase3Fakes.WaitUntilAsync(() => battery.Clears == 1);

        Assert.False(vm.ShowWearTrendChart);
        Assert.Equal(vm.Labels.WearNoSamples, vm.WearTrendSummary);

        // The sentence the page prints instead of the plot names the interval, so the empty card is an answer.
        Assert.Contains("iki haftada bir", vm.WearTrendSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void WearTrend_OneSampleSentence_NamesTheMinimum()
    {
        using var vm = new MainViewModel(new AppSettings(), null, null, TempSettingsPath());

        // The one-sample sentence is the WPF page's own wording: it says how many readings a curve needs and how often
        // they arrive, which is the whole of what a user can do about it.
        Assert.Contains("en az iki ölçüm", vm.Labels.WearOneSample, StringComparison.Ordinal);
        Assert.Contains("iki haftada bir", vm.Labels.WearOneSample, StringComparison.Ordinal);
    }

    [Fact]
    public void WearTrendChart_IsBoundToTheVisibilityFlag()
    {
        var markup = Markup("BatteryView.axaml");

        // The plot is hidden by the flag rather than always drawn: a single sample drawn as a flat line would claim a
        // trend the data does not have.
        Assert.Contains("IsVisible=\"{Binding ShowWearTrendChart}\"", markup, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding WearTrendSummary}\"", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void SchoolMode_ShowsItsStateBesideTheSwitch()
    {
        using var vm = new MainViewModel(new AppSettings(), null, null, TempSettingsPath());

        Assert.Equal(vm.Labels.SchoolModeStateOff, vm.SchoolModeStateText);

        vm.SchoolModeOn = true;
        Assert.Equal(vm.Labels.SchoolModeStateOn, vm.SchoolModeStateText);

        vm.SchoolModeOn = false;
        Assert.Equal(vm.Labels.SchoolModeStateOff, vm.SchoolModeStateText);

        // The two words are the WPF page's own, and they read correctly in both positions.
        Assert.Equal("Etkin", vm.Labels.SchoolModeStateOn);
        Assert.Equal("Kapalı", vm.Labels.SchoolModeStateOff);

        Assert.Contains("Text=\"{Binding SchoolModeStateText}\"", Markup("BatteryView.axaml"), StringComparison.Ordinal);
    }

    // =============================================================================================================
    // 5. The memory figures: megabytes and the percentage in one string.
    // =============================================================================================================

    private static string F(double value, string format) => value.ToString(format, CultureInfo.CurrentCulture);

    /// <summary>
    /// A monitor that answers the two figures this wave reformatted: 4 GB of 8 GB of VRAM, and 12 GB of 32 GB of RAM.
    /// </summary>
    private sealed class FakeMonitor : IHardwareMonitor
    {
        public bool IsAvailable => true;

        public MonitorInfo Info { get; } = new()
        {
            CpuName = "AMD Ryzen 7 7840HS",
            GpuName = "NVIDIA GeForce RTX 4060 Laptop GPU",
            CpuCores = 8,
            CpuThreads = 16,
            CpuMaxClockMhz = 5100,
            RamTotalMb = 32768
        };

        public void Enrich(MonitorData data)
        {
            data.CpuName = Info.CpuName;
            data.GpuName = Info.GpuName;
            data.CpuCores = 16;
            data.VramUsedMB = 4096;
            data.VramTotalMB = 8192;
            data.RamUsage = 37.5;
            data.RamTotalMB = 32768;
        }

        public MonitorData CreateFallback() => new();
    }

    [Fact]
    public async Task RamAndVram_PrintMegabytesAndPercent()
    {
        using var vm = new MainViewModel(
            new AppSettings(), new Phase3Fakes.StubHardware(), null, TempSettingsPath(),
            monitor: new FakeMonitor());

        await vm.RefreshTelemetryAsync();

        // 4 GB of 8 GB, and 12 GB of 32 GB: the two shapes the page prints.
        Assert.Equal($"{F(4096, "F0")} MB / {F(8192, "F0")} MB (%{F(50, "F0")})", vm.VramText);
        Assert.Equal($"{F(12288, "F0")} MB / {F(32768, "F0")} MB (%{F(37.5, "F0")})", vm.RamDetailText);

        // Both carry the unit and the share, so neither needs a second label beside it.
        Assert.Contains("MB", vm.VramText, StringComparison.Ordinal);
        Assert.Contains("%", vm.VramText, StringComparison.Ordinal);
        Assert.Contains("MB", vm.RamDetailText, StringComparison.Ordinal);
        Assert.Contains("%", vm.RamDetailText, StringComparison.Ordinal);
    }

    [Fact]
    public void RamCard_HasNoSecondPercentLabel()
    {
        var markup = Markup("MonitoringView.axaml");

        // The percentage is inside RamDetailText now; a second label on the right printed the same number twice.
        Assert.DoesNotContain("RamPercentText", markup, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding RamDetailText}\"", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void MemoryCleanerCard_OffersTheDownloadPage()
    {
        var markup = Markup("MonitoringView.axaml");

        // The helper is a separate GPL-3.0 program, so the card offers its own download page rather than pretending
        // the clean button can work without it.
        Assert.Contains("Command=\"{Binding GetMemoryCleanerCommand}\"", markup, StringComparison.Ordinal);
        Assert.Contains("Labels.GetMemoryCleaner", markup, StringComparison.Ordinal);

        using var vm = new MainViewModel(new AppSettings(), null, null, TempSettingsPath());
        Assert.Equal("WinMemoryCleaner'ı indir (ücretsiz, GPL-3.0)", vm.Labels.GetMemoryCleaner);

        // The URL is the cross-platform constant, so the card and the launcher cannot disagree about it.
        Assert.StartsWith("https://github.com/IgorMundstein/WinMemoryCleaner/releases",
            MemoryCleanerTool.ReleasesUrl, StringComparison.Ordinal);
    }

    // =============================================================================================================
    // 6. The tray: five modes in a flat list, one tick, and a double click that shows the window.
    // =============================================================================================================

    private static readonly OperatingMode[] FiveModes =
    {
        OperatingMode.Quiet,
        OperatingMode.Default,
        OperatingMode.Extreme,
        OperatingMode.Turbo,
        OperatingMode.Eco
    };

    [Fact]
    public void TrayMenu_TicksOnlyTheActiveMode()
    {
        // One tick, on the mode the machine is actually in. A menu with no marked row would leave the user guessing.
        Assert.True(TrayMenuPolicy.IsModeChecked(OperatingMode.Turbo, OperatingMode.Turbo));
        Assert.False(TrayMenuPolicy.IsModeChecked(OperatingMode.Quiet, OperatingMode.Turbo));

        // No mode at all is no tick, rather than a tick on the first row.
        Assert.False(TrayMenuPolicy.IsModeChecked(OperatingMode.Quiet, null));

        Assert.Equal(1, FiveModes.Count(m => TrayMenuPolicy.IsModeChecked(m, OperatingMode.Eco)));
    }

    [Fact]
    public void TrayMenu_KeepsTheBatteryRuleAndTheRetryRow()
    {
        // A mode the battery rule forbids stays visible but greyed out, with the reason in its tooltip.
        Assert.False(TrayMenuPolicy.EnableMode(allowed: false));
        Assert.Equal(ModePolicy.HighPowerBlockedMessage, TrayMenuPolicy.ModeToolTip(allowed: false));
        Assert.Null(TrayMenuPolicy.ModeToolTip(allowed: true));

        // The retry row is drawn only while a restore is pending, and clickable only when no game owns the mode.
        Assert.False(TrayMenuPolicy.ShowRetry(pending: false));
        Assert.True(TrayMenuPolicy.ShowRetry(pending: true));
        Assert.False(TrayMenuPolicy.EnableRetry(pending: true, gameActive: true));
        Assert.True(TrayMenuPolicy.EnableRetry(pending: true, gameActive: false));
    }

    [Fact]
    public void TrayDoubleClick_IsDerivedFromTheInterval()
    {
        // Avalonia's tray icon reports one Clicked per press and has no double-click event, so the second press inside
        // the window is what counts as one.
        Assert.True(TrayMenuPolicy.IsDoubleClick(TimeSpan.FromMilliseconds(120)));
        Assert.True(TrayMenuPolicy.IsDoubleClick(TrayMenuPolicy.DoubleClickWindow));
        Assert.False(TrayMenuPolicy.IsDoubleClick(TrayMenuPolicy.DoubleClickWindow + TimeSpan.FromMilliseconds(1)));

        // A clock that moved backwards is not a double click.
        Assert.False(TrayMenuPolicy.IsDoubleClick(TimeSpan.FromMilliseconds(-5)));
    }

    [Fact]
    public void TrayHost_BuildsTheFlatListAndHandlesTheDoubleClick()
    {
        var source = File.ReadAllText(
            Path.Combine(RepoRoot(), "NitroLite.Desktop", "TrayIconHost.cs"), Encoding.UTF8);

        // The submenu is gone: the modes are added to the menu itself.
        Assert.DoesNotContain("_quickModeItem", source, StringComparison.Ordinal);
        Assert.Contains("_menu.Add(item);", source, StringComparison.Ordinal);

        // The tick is a checkmark, which is what the WPF tray drew.
        Assert.Contains("NativeMenuItemToggleType.CheckBox", source, StringComparison.Ordinal);

        // The double click is derived, and the interval rule lives in the policy so it can be asserted without a tray.
        Assert.Contains("TrayMenuPolicy.IsDoubleClick", source, StringComparison.Ordinal);
        Assert.Contains("Icon.Clicked +=", source, StringComparison.Ordinal);
    }

    // =============================================================================================================
    // 7. The shell: the rail's reset, and the update notice in the header.
    // =============================================================================================================

    [Fact]
    public void RailSplitter_ResetsToTheDefaultWidth()
    {
        using var vm = new MainViewModel(new AppSettings(), null, null, TempSettingsPath());

        // A drag that went too far has no visible handle to drag back, so the double click is the way out.
        vm.SidebarWidth = AppSettings.MaxSidebarWidth;
        Assert.Equal(AppSettings.MaxSidebarWidth, vm.SidebarWidth);

        vm.SidebarWidth = AppSettings.DefaultSidebarWidth;
        Assert.Equal(215, vm.SidebarWidth);
        Assert.Equal(AppSettings.DefaultSidebarWidth, vm.SidebarWidth);
    }

    [Fact]
    public void RailSplitter_IsWiredToTheResetAndAnnouncesIt()
    {
        var markup = Markup("MainWindow.axaml");
        var code = File.ReadAllText(
            Path.Combine(RepoRoot(), "NitroLite.Desktop", "Views", "MainWindow.axaml.cs"), Encoding.UTF8);

        // Four transparent pixels have no affordance of their own, so the tooltip is where the reset is announced.
        Assert.Contains("DoubleTapped=\"OnRailSplitterDoubleTapped\"", markup, StringComparison.Ordinal);
        Assert.Contains("ToolTip.Tip=\"{Binding Labels.RailSplitterHint}\"", markup, StringComparison.Ordinal);

        // The handler writes the default width, not a hard-coded number.
        Assert.Contains("OnRailSplitterDoubleTapped", code, StringComparison.Ordinal);
        Assert.Contains("AppSettings.DefaultSidebarWidth", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Header_ShowsTheUpdateNotice()
    {
        var markup = Markup("MainWindow.axaml");

        // The two buttons the WPF window put beside the version, under the names it gave them.
        Assert.Contains("x:Name=\"BtnUpdateNow\"", markup, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"BtnReleaseNotes\"", markup, StringComparison.Ordinal);

        // The update runs from the header, and the notes never appear without the update they describe.
        Assert.Contains("Command=\"{Binding UpdateNowCommand}\"", markup, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding ReleaseNotesCommand}\"", markup, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding HasPendingUpdate}\"", markup, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding UpdateAvailable}\"", markup, StringComparison.Ordinal);
    }

    // =============================================================================================================
    // 8. The capture script: one command line, either front end.
    // =============================================================================================================

    [Fact]
    public void ScreenshotScript_AcceptsBothFrontEnds()
    {
        var script = ScriptMarkup();

        // Avalonia's rail is a list of Buttons (InvokePattern); WPF's was a list of RadioButtons
        // (SelectionItemPattern). Both are tried, so one script drives either build.
        Assert.Contains("SelectionItemPattern", script, StringComparison.Ordinal);
        Assert.Contains("InvokePattern", script, StringComparison.Ordinal);

        // The two pages the WPF build named differently still resolve.
        Assert.Contains("'NavRgb'", script, StringComparison.Ordinal);
        Assert.Contains("'NavDebloat'", script, StringComparison.Ordinal);
        Assert.Contains("'NavLighting'", script, StringComparison.Ordinal);
        Assert.Contains("'NavTools'", script, StringComparison.Ordinal);

        // The game fan curve editor follows the fan picker, so the script opens it by selecting the picker's last
        // entry, and the picker carries the id it looks for.
        Assert.Contains("[switch]$GameFanCurve", script, StringComparison.Ordinal);
        Assert.Contains("CmbGameFanMode", script, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"CmbGameFanMode\"",
            Markup("DashboardView.axaml"), StringComparison.Ordinal);
    }

    [Fact]
    public void ScreenshotScript_StillNamesTheCurrentPages()
    {
        var script = ScriptMarkup();

        // -All captures the five pages the Avalonia build actually has, in rail order.
        Assert.Contains(
            "@('NavModes', 'NavMonitoring', 'NavLighting', 'NavBattery', 'NavTools')",
            script, StringComparison.Ordinal);
    }
}
