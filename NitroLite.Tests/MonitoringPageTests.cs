using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NitroLite;
using NitroLite.Core;
using NitroLite.Desktop.ViewModels;
using Xunit;

namespace NitroLite.Tests;

public class HardwareInfoTests
{
    [Fact]
    public void CoreText_ShowsCoresAndThreads_NotThreadsAsCores()
    {
        var info = new HardwareInfo { CpuCores = 8, CpuThreads = 16 };
        Assert.Equal("8 çekirdek / 16 iş parçacığı", info.FormatCoreText(16));
        Assert.Equal("16 iş parçacığı", new HardwareInfo().FormatCoreText(16));
    }

    [Fact]
    public void MemoryText_UsesDetectedValues_AndModulesNotChannels()
    {
        var info = new HardwareInfo { MemoryType = "DDR5", MemorySpeedMts = 5600, MemoryModules = 2, MemoryTotalGb = 32 };
        Assert.Equal("DDR5 5600 MT/s · 2 modül · 32 GB", info.FormatMemoryText());
        Assert.Equal("Bellek bilgisi okunamadı", new HardwareInfo().FormatMemoryText());
    }

    [Theory]
    [InlineData(34, "DDR5")]
    [InlineData(26, "DDR4")]
    [InlineData(35, "LPDDR5")]
    [InlineData(0, "")]
    public void MemoryTypeCodes_MapToNames(int code, string expected)
    {
        Assert.Equal(expected, HardwareInfoService.MemoryTypeName(code));
    }

    [Fact]
    public void PickDiscreteGpu_PrefersNvidiaOverIntegratedAndBasicDisplay()
    {
        Assert.Equal("NVIDIA GeForce RTX 4070 Laptop GPU",
            HardwareInfoService.PickDiscreteGpu(new[] { "AMD Radeon(TM) 780M", "NVIDIA GeForce RTX 4070 Laptop GPU" }));
        Assert.Equal("AMD Radeon(TM) 780M",
            HardwareInfoService.PickDiscreteGpu(new[] { "Microsoft Basic Display Adapter", "AMD Radeon(TM) 780M" }));
        Assert.Equal("", HardwareInfoService.PickDiscreteGpu(System.Array.Empty<string>()));
    }

    [Fact]
    public void LiveRead_ReturnsSomethingForThisMachine()
    {
        // Read-only WMI query.
        var info = HardwareInfoService.Get();
        Assert.False(string.IsNullOrWhiteSpace(info.CpuName));
        Assert.True(info.CpuThreads >= info.CpuCores);
    }
}

/// <summary>
/// Page 2: the monitoring cards, the nine-series chart, the mode trace, the overlay and the standby-list cleaner.
/// </summary>
/// <remarks>
/// The view model holds no Avalonia types, so everything here runs without a display. What is asserted is the part
/// that decides what the user sees: which fields reach the page, whether a series switch redraws from the history
/// already held, whether a mode change is marked, and whether the overlay's corner and visibility survive a restart.
/// </remarks>
public class MonitoringPageTests
{
    // ---------------------------------------------------------------- fixtures

    /// <summary>A monitor that answers every field, so a missing row is a wiring bug rather than a quiet platform.</summary>
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
            RamTotalMb = 32768,
            MemoryText = "DDR5 5600 MT/s · 2 modül · 32 GB"
        };

        public void Enrich(MonitorData data)
        {
            data.CpuName = Info.CpuName;
            data.GpuName = Info.GpuName;
            data.CpuCores = 16;
            data.CpuUsage = 42.5;
            data.CpuPowerWatts = 54.2;
            data.CpuFrequencyMHz = 4200;
            data.GpuUsage = 61.0;
            data.GpuPowerWatts = 88.0;
            data.GpuFrequencyMHz = 2100;
            data.VramUsedMB = 4096;
            data.VramTotalMB = 8192;
            data.RamUsage = 37.5;
            data.RamTotalMB = 32768;
        }

        public MonitorData CreateFallback() => new();
    }

    /// <summary>A cleaner that reports a fixed result, so the card's wording can be asserted without a real tool.</summary>
    private sealed class FakeCleaner : IMemoryCleaner
    {
        public bool IsSupported { get; init; } = true;

        public bool NeedsExternalTool { get; init; } = true;

        public MemoryCleanResult Result { get; init; } = new()
        {
            Cleaned = true,
            FreedMegabytes = 2048,
            AvailableBeforeGigabytes = 9.4,
            AvailableGigabytes = 11.4
        };

        public Task<MemoryCleanResult> CleanStandbyMemoryAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Result);
    }

    /// <summary>A hardware controller that answers one fixed sample and accepts every write.</summary>
    private sealed class StubHardware : Core.Hardware.IHardwareController
    {
        public MonitorData Sample { get; set; } = new()
        {
            CpuTemperature = 62,
            GpuTemperature = 55,
            SysTemperature = 48,
            CpuFanSpeed = 2400,
            GpuFanSpeed = 2350,
            IsFanRpmMeasured = true
        };

        public string BackendName => "Test";

        public bool IsAvailable => true;

        public Task<bool> EnsureConnectedAsync() => Task.FromResult(true);

        public Task<Core.Hardware.HardwareCapabilities> GetCapabilitiesAsync() =>
            Task.FromResult(new Core.Hardware.HardwareCapabilities
            {
                Backend = "Test",
                ServiceAvailable = true,
                FanControl = true,
                OperatingModeControl = true,
                KeyboardLighting = true,
                BacklightTimeout = true,
                CoolBoost = true,
                LcdOverdrive = true,
                WinKeyLock = true,
                BatteryChargeLimit = true
            });

        public Task<MonitorData?> GetMonitorDataAsync() => Task.FromResult<MonitorData?>(Sample);

        public Task<OperatingMode?> GetOperatingModeAsync() => Task.FromResult<OperatingMode?>(OperatingMode.Default);

        public Task<bool> SetOperatingModeAsync(OperatingMode mode) => Task.FromResult(true);

        public Task<GpuOperatingMode?> GetGpuModeAsync() => Task.FromResult<GpuOperatingMode?>(null);

        public Task<bool> SetGpuModeAsync(GpuOperatingMode mode) => Task.FromResult(true);

        public Task<int?> GetGpuModeCapabilityAsync() => Task.FromResult<int?>(null);

        public Task<FanMode?> GetFanModeAsync() => Task.FromResult<FanMode?>(FanMode.Auto);

        public Task<bool> SetFanModeAsync(FanMode mode, int cpuPercent = -1, int gpuPercent = -1) => Task.FromResult(true);

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

        public void Dispose() { }
    }

    /// <summary>Formats a number the way the page does, so the assertion does not depend on the machine's locale.</summary>
    private static string F(double value, string format) => value.ToString(format, CultureInfo.CurrentCulture);

    private static string TempSettingsPath() =>
        Path.Combine(Path.GetTempPath(), "nitrolite-monitor-" + Guid.NewGuid().ToString("N") + ".json");

    private static MainViewModel Create(
        AppSettings? settings = null,
        IHardwareMonitor? monitor = null,
        IMemoryCleaner? cleaner = null,
        string? path = null,
        Core.Hardware.IHardwareController? hardware = null) =>
        new(settings ?? new AppSettings(),
            hardware,
            null,
            path ?? TempSettingsPath(),
            monitor: monitor,
            memoryCleaner: cleaner);

    /// <summary>The same view model, standing on the page that owns the chart.</summary>
    /// <remarks>
    /// The chart is rebuilt on every tick while the monitoring page is the page on screen, and once when it opens: a
    /// chart on a page nobody is looking at is two hundred points of geometry and five formatted numbers per second for
    /// a control that is not in the tree. The series keeps filling in either way, so arriving on the page redraws from
    /// the history already held, which is what these tests are about.
    /// </remarks>
    private static MainViewModel OnChartPage(MainViewModel vm)
    {
        vm.SelectedTab = vm.NavTabs.First(tab => tab.Key == MainViewModel.NavMonitoringKey);
        return vm;
    }

    // ---------------------------------------------------------------- 1. the cards

    [Fact]
    public async Task MonitorData_AllFieldsExposed()
    {
        var hardware = new StubHardware();
        using var vm = Create(monitor: new FakeMonitor(), hardware: hardware);

        await vm.RefreshTelemetryAsync();

        // The model strings come from the platform monitor, not from the firmware.
        Assert.Equal("AMD Ryzen 7 7840HS", vm.CpuNameText);
        Assert.Equal("NVIDIA GeForce RTX 4060 Laptop GPU", vm.GpuNameText);
        Assert.Equal(" (8 çekirdek / 16 iş parçacığı)", vm.CpuCoreCountText);
        Assert.Equal("DDR5 5600 MT/s · 2 modül · 32 GB", vm.MemoryInfoText);
        Assert.True(vm.HasMemoryInfo);

        // Every numeric row the page prints.
        Assert.Equal(42.5, vm.CpuUsage);
        Assert.Equal($"{F(42.5, "F1")} %", vm.CpuUsageText);
        Assert.Equal(54.2, vm.CpuPowerWatts);
        Assert.Contains($"{F(54.2, "F1")} W", vm.CpuPowerText);
        Assert.Equal($"{F(4.2, "F2")} GHz", vm.CpuFrequencyText);
        Assert.Equal($"{F(61.0, "F1")} %", vm.GpuUsageText);
        Assert.Equal($"{F(88.0, "F1")} W", vm.GpuPowerText);
        Assert.Equal($"{F(2100, "F0")} MHz", vm.GpuFrequencyText);
        // Megabytes and the percentage in one figure: a laptop GPU has 4, 6 or 8 GB, so the gigabyte form rounded
        // away most of the movement the row exists to show.
        Assert.Equal($"{F(4096, "F0")} MB / {F(8192, "F0")} MB (%{F(50, "F0")})", vm.VramText);
        Assert.Equal(50.0, vm.VramBarPercent, 3);
        Assert.Equal($"{F(12288, "F0")} MB / {F(32768, "F0")} MB (%{F(37.5, "F0")})", vm.RamDetailText);

        // The overlay's three rows are composed from the same numbers.
        Assert.Contains("62 °C", vm.OsdCpuLine);
        Assert.Contains("55 °C", vm.OsdGpuLine);
        Assert.Contains("2400 RPM", vm.OsdRamLine);
    }

    [Fact]
    public async Task MonitorData_WithoutAMonitor_LeavesPlaceholders()
    {
        using var vm = Create(hardware: new StubHardware());

        await vm.RefreshTelemetryAsync();

        Assert.False(vm.MonitorAvailable);
        Assert.Equal(vm.Labels.Cpu, vm.CpuNameText);
        Assert.Equal(vm.Labels.Gpu, vm.GpuNameText);
        Assert.Equal("-- W", vm.CpuPowerText);
        Assert.Equal("-- GHz", vm.CpuFrequencyText);
        Assert.Equal("-- MB / -- MB", vm.VramText);
        Assert.Equal("-- MB / -- MB", vm.RamDetailText);
        Assert.False(vm.HasMemoryInfo);
    }

    // ---------------------------------------------------------------- 2. the chart

    [Fact]
    public async Task ChartMetric_SwitchesSeries()
    {
        var hardware = new StubHardware();
        using var vm = OnChartPage(Create(monitor: new FakeMonitor(), hardware: hardware));

        Assert.Equal(9, vm.ChartMetrics.Count);
        Assert.Equal(TelemetryMetrics.CpuTemp, vm.SelectedChartMetric?.Id);

        await vm.RefreshTelemetryAsync();
        await vm.RefreshTelemetryAsync();

        // Two samples of the same series: the chart has a range and a current reading.
        Assert.Equal(2, vm.ChartValues.Count);
        Assert.Equal(62, vm.ChartValues[^1]);
        Assert.False(vm.ChartEmpty);
        Assert.Contains("62", vm.ChartCurrentText);

        // Switching series redraws from the history already held rather than waiting for the next tick.
        vm.SelectedChartMetric = vm.ChartMetrics.First(m => m.Id == TelemetryMetrics.CpuUsage);
        Assert.Equal(2, vm.ChartValues.Count);
        Assert.Equal(42.5, vm.ChartValues[^1]);
        Assert.Contains("42.5", vm.ChartCurrentText);

        // The choice is persisted, so the page comes back on the series the user left it on.
        Assert.Equal(TelemetryMetrics.CpuUsage, vm.SelectedChartMetric?.Id);
    }

    [Fact]
    public async Task ChartMetric_WithOneSample_ReportsEmpty()
    {
        using var vm = OnChartPage(Create(monitor: new FakeMonitor(), hardware: new StubHardware()));

        await vm.RefreshTelemetryAsync();

        Assert.True(vm.ChartEmpty);
        Assert.Equal("-- °C", vm.ChartCurrentText);
        Assert.Equal("-- °C", vm.ChartMinText);
    }

    // ---------------------------------------------------------------- 3. the mode-change marks

    [Fact]
    public async Task ModeChangeMarks_RecordsModeChanges()
    {
        using var vm = OnChartPage(Create(monitor: new FakeMonitor(), hardware: new StubHardware()));

        await vm.RefreshTelemetryAsync();

        // The mode the app launched in is the first mark: the chart shows where the session began.
        Assert.Single(vm.ModeChangeMarks);

        // A mode change is marked on the chart.
        var performance = vm.Modes.First(m => m.Mode == OperatingMode.Extreme);
        vm.SelectedMode = performance;

        Assert.Equal(2, vm.ModeChangeMarks.Count);
        Assert.InRange(vm.ModeChangeMarks[^1], 0.0, 1.0);

        // Selecting the same mode again is not a change, so it does not add a third rule.
        vm.SelectedMode = performance;
        Assert.Equal(2, vm.ModeChangeMarks.Count);

        var quiet = vm.Modes.First(m => m.Mode == OperatingMode.Quiet);
        vm.SelectedMode = quiet;
        Assert.Equal(3, vm.ModeChangeMarks.Count);
    }

    // ---------------------------------------------------------------- 4. the overlay

    [Fact]
    public void Osd_TogglePersists()
    {
        var path = TempSettingsPath();
        using (var vm = Create(path: path))
        {
            Assert.False(vm.OsdEnabled);
            Assert.Equal(vm.Labels.ShowCaption, vm.OsdToggleText);

            vm.ToggleOsdCommand.Execute(null);
            Assert.True(vm.OsdEnabled);
            Assert.Equal(vm.Labels.HideCaption, vm.OsdToggleText);
        }

        // An overlay someone turned on for a game session is a preference, so it is written to the file.
        Assert.True(SettingsManager.LoadSettingsWithFallback(path).OsdEnabled);
    }

    [Fact]
    public void Osd_CornerCycles()
    {
        var path = TempSettingsPath();
        using var vm = Create(path: path);
        vm.OsdMonitorCount = 2;

        Assert.Equal(0, vm.OsdCorner);
        Assert.Equal(vm.Labels.OsdCornerTopLeft, vm.OsdCornerName);

        vm.CycleOsdCornerCommand.Execute(null);
        Assert.Equal(1, vm.OsdCorner);
        Assert.Equal(vm.Labels.OsdCornerTopRight, vm.OsdCornerName);

        vm.CycleOsdCornerCommand.Execute(null);
        Assert.Equal(2, vm.OsdCorner);
        Assert.Equal(vm.Labels.OsdCornerBottomLeft, vm.OsdCornerName);

        vm.CycleOsdCornerCommand.Execute(null);
        Assert.Equal(3, vm.OsdCorner);
        Assert.Equal(vm.Labels.OsdCornerBottomRight, vm.OsdCornerName);

        // The fifth click wraps to the first corner and moves to the next display, which is the only way to reach a
        // second screen without a monitor picker.
        vm.CycleOsdCornerCommand.Execute(null);
        Assert.Equal(0, vm.OsdCorner);
        Assert.Equal(1, vm.OsdMonitor);
        Assert.Equal(vm.Labels.OsdPosition(vm.Labels.OsdCornerTopLeft, 2), vm.OsdPositionText);

        // And the position survives a restart.
        var reloaded = SettingsManager.LoadSettingsWithFallback(path);
        Assert.Equal(0, reloaded.OsdCorner);
        Assert.Equal(1, reloaded.OsdMonitor);
    }

    // ---------------------------------------------------------------- 5. the standby-list cleaner

    [Fact]
    public async Task RamClean_ReportsResult()
    {
        using var vm = Create(cleaner: new FakeCleaner());

        Assert.True(vm.MemoryCleanAvailable);
        Assert.False(vm.HasMemoryCleanStatus);
        Assert.Equal(vm.Labels.CleanStandby, vm.MemoryCleanButtonText);

        await vm.CleanMemoryAsync();

        Assert.True(vm.MemoryCleanSucceeded);
        Assert.True(vm.HasMemoryCleanStatus);
        Assert.Contains($"{F(2.0, "F1")} GB", vm.MemoryCleanStatus);
        Assert.Contains(F(9.4, "F1"), vm.MemoryCleanStatus);
        Assert.Contains(F(11.4, "F1"), vm.MemoryCleanStatus);
        Assert.False(vm.MemoryCleanRunning);
    }

    [Fact]
    public async Task RamClean_WithoutACleaner_HidesTheCard()
    {
        using var vm = Create();

        Assert.False(vm.MemoryCleanAvailable);

        // The command is a no-op rather than a crash when the platform has no standby list.
        await vm.CleanMemoryAsync();
        Assert.False(vm.HasMemoryCleanStatus);
    }

    [Fact]
    public async Task RamClean_WhenTheToolIsMissing_SaysSo()
    {
        using var vm = Create(cleaner: new FakeCleaner
        {
            Result = new MemoryCleanResult { Cleaned = false, ToolMissing = true }
        });

        await vm.CleanMemoryAsync();

        Assert.False(vm.MemoryCleanSucceeded);
        Assert.Equal(vm.Labels.MemoryCleanToolMissing, vm.MemoryCleanStatus);
    }

    [Fact]
    public void RamClean_OffersTheDownloadOnlyWhenTheCleanNeedsAHelper()
    {
        // Windows shells out to WinMemoryCleaner, so the card offers its page. Linux drops the page cache through the
        // kernel, so there is nothing to download and the row is hidden rather than pointing at a Windows tool.
        using var windows = Create(cleaner: new FakeCleaner { NeedsExternalTool = true });
        Assert.True(windows.MemoryCleanToolDownloadVisible);

        using var linux = Create(cleaner: new FakeCleaner { NeedsExternalTool = false });
        Assert.True(linux.MemoryCleanAvailable);
        Assert.False(linux.MemoryCleanToolDownloadVisible);
    }

    // ---------------------------------------------------------------- 5. the overlay toggle's state

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

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine(parts.Prepend(RepoRoot()).ToArray()));

    [Fact]
    public void OsdToggle_ShowsWhetherTheOverlayIsOn()
    {
        var markup = Read("NitroLite.Desktop", "Views", "MonitoringView.axaml");

        var start = markup.IndexOf("ToggleOsdCommand", StringComparison.Ordinal);
        Assert.True(start > 0, "the monitoring page has no OSD toggle");

        // The button is the switch, so its own state has to be on it. Before this it looked identical on and off and
        // the only way to tell was to look at the corner of the screen.
        // The element is self-closing, so the end is the first "/>" after the command rather than a closing tag.
        var button = markup[markup.LastIndexOf("<Button", start, StringComparison.Ordinal)..];
        button = button[..(button.IndexOf("/>", StringComparison.Ordinal) + 2)];

        Assert.Contains("Classes.on=\"{Binding OsdEnabled}\"", button, StringComparison.Ordinal);
        Assert.Contains("Labels.Osd", button, StringComparison.Ordinal);

        // The fill is the whole signal. A dot beside the label was tried and taken out again: at 11px it read as a
        // bullet in the text rather than as a state, and the accent fill already says it.
        Assert.DoesNotContain("statedot", button, StringComparison.Ordinal);
    }

    [Fact]
    public void OsdToggle_OnStateComesFromThePalette()
    {
        var markup = Read("NitroLite.Desktop", "App.axaml");

        // Both colours come from the palette, so the on state is legible in all eight themes instead of being a
        // literal that only works on the cream page.
        Assert.Contains("Selector=\"Button.primary.on\"", markup, StringComparison.Ordinal);

        // There is no second signal any more: the dot beside the label was taken out, so the fill is the state.
        Assert.DoesNotContain("statedot", markup, StringComparison.Ordinal);

        var start = markup.IndexOf("Selector=\"Button.primary.on\"", StringComparison.Ordinal);
        var end = markup.IndexOf("</Style>", start, StringComparison.Ordinal);
        var block = markup[start..end];

        Assert.Contains("AccentBrush", block, StringComparison.Ordinal);
        Assert.Contains("OnAccentBrush", block, StringComparison.Ordinal);
    }

    [Fact]
    public void OsdToggle_HoverKeepsTheFillAndTheType()
    {
        var markup = Read("NitroLite.Desktop", "App.axaml");

        // AccentHover is the accent lifted towards white. On a bright accent that leaves dark type on a washed-out
        // ground, which is the one state where the label stopped being readable, so the hovered switch keeps its fill
        // and draws an outline in the contrast colour instead.
        var start = markup.IndexOf("Selector=\"Button.primary.on:pointerover\"", StringComparison.Ordinal);
        Assert.True(start > 0, "the on state has no hover rule");

        var end = markup.IndexOf("</Style>", start, StringComparison.Ordinal);
        var block = markup[start..end];

        Assert.Contains("OnAccentBrush", block, StringComparison.Ordinal);
        Assert.DoesNotContain("AccentHoverBrush", block, StringComparison.Ordinal);
    }
}
