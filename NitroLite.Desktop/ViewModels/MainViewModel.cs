using System.Buffers;
using System.Collections.ObjectModel;
using System.IO;
using NitroLite.Core;
using NitroLite.Core.Hardware;
using NitroLite.Desktop.Mvvm;
using NitroLite.Desktop.Themes;

namespace NitroLite.Desktop.ViewModels;

/// <summary>
/// One card in the operating-mode picker.
/// </summary>
/// <remarks>
/// A card rather than a combo-box row: clicking it applies the mode, so there is no separate Apply button and no state
/// in which the list shows one mode while the machine runs another. <see cref="IsSelected"/> is what the view paints
/// with the accent colour, and it is owned here rather than derived from the parent so a card can repaint on its own.
/// </remarks>
public sealed class ModeChoice : ObservableObject
{
    private bool _isSelected;

    public ModeChoice(OperatingMode mode, string label, string description, string icon, Action<ModeChoice>? select = null)
    {
        Mode = mode;
        Label = label;
        Description = description;
        Icon = icon;
        SelectCommand = new RelayCommand(() => select?.Invoke(this));
    }

    public OperatingMode Mode { get; }

    public string Label { get; }

    /// <summary>One line under the title: what this mode does to the machine.</summary>
    public string Description { get; }

    /// <summary>
    /// The two-word label the compact tile carries. The tall card had room for the full sentence; a quarter-width tile
    /// does not, and a clipped sentence reads worse than a short one.
    /// </summary>
    public string ShortHint { get; init; } = "";

    /// <summary>
    /// The Segoe Fluent codepoint the tile draws. A string rather than a <c>Geometry</c> so this file stays free of
    /// Avalonia types; the view binds it to a <c>TextBlock</c> in the icon font.
    /// </summary>
    public string Icon { get; }

    public RelayCommand SelectCommand { get; }

    /// <summary>
    /// The word the AKTİF chip carries. It lives on the card rather than being reached for through the parent's
    /// labels, because a compiled binding inside an item template cannot see the page's view model without a
    /// parent-relative path, and that path is the kind of thing that silently renders nothing.
    /// </summary>
    public string ActiveLabel { get; init; } = "";

    public bool IsSelected
    {
        get => _isSelected;
        internal set => Set(ref _isSelected, value);
    }

    public override string ToString() => Label;
}

/// <summary>One card in the fan-mode picker. Same contract as <see cref="ModeChoice"/>.</summary>
public sealed class FanModeChoice : ObservableObject
{
    private bool _isSelected;

    public FanModeChoice(FanMode mode, string label, string description, string icon, Action<FanModeChoice>? select = null)
    {
        Mode = mode;
        Label = label;
        Description = description;
        Icon = icon;
        SelectCommand = new RelayCommand(() => select?.Invoke(this));
    }

    public FanMode Mode { get; }

    public string Label { get; }

    public string Description { get; }

    public string Icon { get; }

    public RelayCommand SelectCommand { get; }

    public bool IsSelected
    {
        get => _isSelected;
        internal set => Set(ref _isSelected, value);
    }

    public override string ToString() => Label;
}

/// <summary>
/// One entry in the mouse-profile picker: "leave it alone", then the device's own four configurations.
/// </summary>
/// <remarks>
/// A class with its own command for the same reason <see cref="ModeChoice"/> is one: the FARE row's quick menu is an
/// item template, and a compiled binding inside a template cannot reach the page's view model.
/// </remarks>
public sealed class MouseProfileOption : ObservableObject
{
    private bool _isSelected;

    public MouseProfileOption(int index, string label, Action<MouseProfileOption>? select = null)
    {
        Index = index;
        Label = label;
        SelectCommand = new RelayCommand(() => select?.Invoke(this));
    }

    /// <summary>0 for "leave it alone", 1 to 4 for the mouse's own configurations.</summary>
    public int Index { get; }

    public string Label { get; }

    public RelayCommand SelectCommand { get; }

    public bool IsSelected
    {
        get => _isSelected;
        internal set => Set(ref _isSelected, value);
    }

    public override string ToString() => Label;
}

/// <summary>
/// One entry in the left rail. The rail is a list of these rather than a <c>TabControl</c> so the selected marker, the
/// icon and the label are one control the view can paint, and so the live <c>SİSTEM</c> card can sit under the list
/// without being a tab.
/// </summary>
public sealed class NavTab : ObservableObject
{
    private bool _isSelected;

    public NavTab(string key, string label, string icon, Action<NavTab>? select = null)
    {
        Key = key;
        Label = label;
        Icon = icon;
        SelectCommand = new RelayCommand(() => select?.Invoke(this));
    }

    /// <summary>A stable identifier the view model switches on; never shown.</summary>
    public string Key { get; }

    public string Label { get; }

    /// <summary>The Segoe Fluent codepoint the rail draws, in the icon font.</summary>
    public string Icon { get; }

    /// <summary>
    /// The identifier a UI-automation client addresses this tab by, e.g. <c>NavModes</c>.
    /// </summary>
    /// <remarks>
    /// Derived from <see cref="Key"/> rather than from the label, so a capture script keeps working in every language.
    /// </remarks>
    public string AutomationId => "Nav" + char.ToUpperInvariant(Key[0]) + Key[1..];

    public RelayCommand SelectCommand { get; }

    public bool IsSelected
    {
        get => _isSelected;
        internal set => Set(ref _isSelected, value);
    }

    public override string ToString() => Label;
}

/// <summary>Which of the four fan choices a segment stands for.</summary>
public enum FanSegmentKind
{
    Auto = 0,
    Max = 1,
    Custom = 2,
    Curve = 3
}

/// <summary>
/// One pill in the fan-mode segmented control. <see cref="FanSegmentKind.Curve"/> is not a <see cref="FanMode"/>: the
/// firmware has no curve mode, the app draws the curve and writes the resulting speed, so it is a separate choice that
/// turns <c>CurveEnabled</c> on instead of writing a mode.
/// </summary>
public sealed class FanSegmentChoice : ObservableObject
{
    private bool _isSelected;

    public FanSegmentChoice(FanSegmentKind kind, string label, Action<FanSegmentChoice>? select = null)
    {
        Kind = kind;
        Label = label;
        SelectCommand = new RelayCommand(() => select?.Invoke(this));
    }

    public FanSegmentKind Kind { get; }

    public string Label { get; }

    public RelayCommand SelectCommand { get; }

    public bool IsSelected
    {
        get => _isSelected;
        internal set => Set(ref _isSelected, value);
    }

    public override string ToString() => Label;
}

/// <summary>One pill in the CPU-boost segmented control.</summary>
public sealed class CpuBoostChoice : ObservableObject
{
    private bool _isSelected;

    public CpuBoostChoice(CpuBoostMode mode, string label, Action<CpuBoostChoice>? select = null)
    {
        Mode = mode;
        Label = label;
        SelectCommand = new RelayCommand(() => select?.Invoke(this));
    }

    public CpuBoostMode Mode { get; }

    public string Label { get; }

    public RelayCommand SelectCommand { get; }

    public bool IsSelected
    {
        get => _isSelected;
        internal set => Set(ref _isSelected, value);
    }

    public override string ToString() => Label;
}

/// <summary>
/// One swatch in the ready-made colour palette. Clicking it writes the hex into the zone or the primary colour, so a
/// user never has to type <c>#112233</c> to pick a colour.
/// </summary>
public sealed class ColorSwatch : ObservableObject
{
    private bool _isSelected;

    public ColorSwatch(string hex, string name, Action<ColorSwatch>? select = null)
    {
        Hex = hex;
        Name = name;
        SelectCommand = new RelayCommand(() => select?.Invoke(this));
    }

    /// <summary>The <c>#RRGGBB</c> the backend takes.</summary>
    public string Hex { get; }

    /// <summary>The swatch's accessible name, so the palette is not eight unlabelled squares.</summary>
    public string Name { get; }

    public RelayCommand SelectCommand { get; }

    public bool IsSelected
    {
        get => _isSelected;
        internal set => Set(ref _isSelected, value);
    }

    public override string ToString() => Name;
}

/// <summary>One row of the capability list: a name and whether this machine has it.</summary>
public sealed record CapabilityRow(string Label, bool Supported){
    /// <summary>The tick or cross the row is drawn with.</summary>
    public string Glyph => Supported ? "✓" : "✕";

    /// <summary>"Destekleniyor" / "Desteklenmiyor", so the row says it in words as well as in a symbol.</summary>
    public string StateText => Supported ? Loc.T("Destekleniyor") : Loc.T("Desteklenmiyor");
}

/// <summary>One configured game rule, as the Game Profiles page lists it.</summary>
public sealed class GameProfileRow : ObservableObject
{
    public GameProfileRow(
        GameProfile profile, string modeLabel, string fanLabel, string deleteLabel, Action<GameProfileRow> delete)
    {
        ExeName = profile.ExeName;
        DisplayName = string.IsNullOrWhiteSpace(profile.DisplayName) ? profile.ExeName : profile.DisplayName;
        ExePath = profile.InstallPath;
        ModeLabel = modeLabel;
        FanLabel = fanLabel;
        DeleteLabel = deleteLabel;
        DeleteCommand = new RelayCommand(() => delete(this));
    }

    public string ExeName { get; }

    public string DisplayName { get; }

    /// <summary>
    /// Where the executable was found, or empty for a rule added by hand. Shown under the picker so a user can see
    /// which copy of a game the rule points at when two installs share an executable name.
    /// </summary>
    public string ExePath { get; }

    public bool HasExePath => !string.IsNullOrWhiteSpace(ExePath);

    /// <summary>The operating mode this game switches to, already translated.</summary>
    public string ModeLabel { get; }

    /// <summary>The fan mode this game switches to, already translated.</summary>
    public string FanLabel { get; }

    /// <summary>The delete button's caption, carried on the row so the template needs no parent lookup.</summary>
    public string DeleteLabel { get; }

    public RelayCommand DeleteCommand { get; }
}

/// <summary>One entry in the effect selector. <see cref="Id"/> is what the backend expects.</summary>
public sealed record EffectChoice(string Id, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One entry in the GPU (MUX) selector. <see cref="Mode"/> is what the backend expects.</summary>
public sealed record GpuModeChoice(GpuOperatingMode Mode, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// One entry in the theme picker. <see cref="Theme"/> is what the settings file stores; <see cref="Label"/> is the
/// translated name, so the list is rebuilt when the language changes.
/// </summary>
public sealed record ThemeChoice(AppTheme Theme, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// One preview card in the theme picker: a screenshot of the palette, its five colours and its name.
/// </summary>
/// <remarks>
/// <para>
/// A card rather than a combo-box row, for the same reason <see cref="ModeChoice"/> is one: the choice is made by
/// looking, and a list of names asks the user to imagine what "Kuzey Buzulu" looks like. Clicking the card applies the
/// palette immediately, so there is no separate Apply button and no state in which the picker shows one theme while
/// the window runs another.
/// </para>
/// <para>
/// The swatches are hex strings and the screenshot is fetched by a converter, because the view model holds no Avalonia
/// types. <see cref="IsSelected"/> is owned here rather than derived from the parent so a card can repaint on its own
/// when the selection moves.
/// </para>
/// </remarks>
public sealed class ThemeCardViewModel : ObservableObject
{
    private bool _isSelected;

    public ThemeCardViewModel(
        AppTheme theme,
        string label,
        IReadOnlyList<string> swatches,
        Action<ThemeCardViewModel>? select = null)
    {
        Theme = theme;
        Label = label;
        Swatches = swatches;
        SelectCommand = new RelayCommand(() => select?.Invoke(this));
    }

    public AppTheme Theme { get; }

    /// <summary>The translated palette name, rebuilt when the language changes.</summary>
    public string Label { get; }

    /// <summary>Page, card, rule, ink and accent, as <c>#RRGGBB</c>.</summary>
    public IReadOnlyList<string> Swatches { get; }

    public RelayCommand SelectCommand { get; }

    /// <summary>True when this is the stored preference, which is what paints the card's accent frame.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        internal set => Set(ref _isSelected, value);
    }

    public override string ToString() => Label;
}

/// <summary>
/// One ready-made fan curve, as the ŞABLON row offers it.
/// </summary>
/// <remarks>
/// A class with its own command rather than a record, for the same reason <see cref="ModeChoice"/> is: a compiled
/// binding inside an item template cannot reach the page's view model, so the click has to be carried on the item.
/// </remarks>
public sealed class CurvePresetChoice : ObservableObject
{
    private bool _isSelected;

    public CurvePresetChoice(string id, string label, IReadOnlyList<FanCurvePoint> points, Action<CurvePresetChoice>? select = null)
    {
        Id = id;
        Label = label;
        Points = points;
        SelectCommand = new RelayCommand(() => select?.Invoke(this));
    }

    public string Id { get; }

    public string Label { get; }

    public IReadOnlyList<FanCurvePoint> Points { get; }

    public RelayCommand SelectCommand { get; }

    /// <summary>True when the anchors currently match this shape, which is what paints the button as chosen.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        internal set => Set(ref _isSelected, value);
    }

    public override string ToString() => Label;
}

/// <summary>
/// One anchor of the fan curve. The slider edits <see cref="Percent"/>; <see cref="EffectivePercent"/> is what the
/// fans will actually get, because the safety floor is applied on top and cannot be edited away.
/// </summary>
public sealed class CurveAnchorViewModel : ObservableObject
{
    private readonly Action _changed;
    private int _percent;

    public CurveAnchorViewModel(int temperatureC, int percent, Action changed)
    {
        TemperatureC = temperatureC;
        _percent = Math.Clamp(percent, FanCurve.MinPercent, FanCurve.MaxPercent);
        _changed = changed;
    }

    public int TemperatureC { get; }

    public string Label => $"{TemperatureC} °C";

    /// <summary>The floor this anchor sits on, or 0 below the first band.</summary>
    public int SafetyFloor => FanCurve.SafetyFloorAt(TemperatureC);

    public string SafetyText => SafetyFloor > 0 ? Labels.SafetyFloor(SafetyFloor) : "";

    /// <summary>Replaced with the active language's labels when the language changes.</summary>
    public ShellLabels Labels { get; set; } = new();

    public int Percent
    {
        get => _percent;
        set
        {
            if (!Set(ref _percent, Math.Clamp(value, FanCurve.MinPercent, FanCurve.MaxPercent))) return;
            Raise(nameof(EffectivePercent));
            Raise(nameof(EffectiveText));
            _changed();
        }
    }

    /// <summary>What the fans get: the slider value raised to the safety floor for this temperature.</summary>
    public int EffectivePercent => Math.Max(_percent, SafetyFloor);

    /// <summary>The effective value with its unit, so the view needs no format string.</summary>
    public string EffectiveText => $"%{EffectivePercent}";
}

/// <summary>
/// One row of the lighting page's direction list. A class rather than an int so the combo box can show a translated
/// name while the backend keeps the protocol's own number.
/// </summary>
public sealed class DirectionChoice
{
    public DirectionChoice(int direction, string label)
    {
        Direction = direction;
        Label = label;
    }

    /// <summary>The protocol's direction number: 1 right, 2 left, 3 up, 4 down.</summary>
    public int Direction { get; }

    public string Label { get; }

    public override string ToString() => Label;
}

/// <summary>
/// One row of the chart's series selector. A class rather than a bare string so the combo box can show a translated
/// label while the settings file keeps the stable identifier.
/// </summary>
public sealed class ChartMetricChoice
{
    public ChartMetricChoice(string id, string label)
    {
        Id = id;
        Label = label;
    }

    /// <summary>The stable identifier written to the settings file, e.g. <c>CPU_TEMP</c>.</summary>
    public string Id { get; }

    public string Label { get; }

    public override string ToString() => Label;
}

/// <summary>
/// The four statistics and the three grid labels the chart's strip shows, already formatted. A record because the seven
/// strings are always replaced together: a half-updated strip would show a maximum from one series beside a minimum
/// from another.
/// </summary>
public sealed record ChartStats(
    string Current,
    string Min,
    string Max,
    string Average,
    string GridTop,
    string GridMid,
    string GridBottom)
{
    /// <summary>The strip before the first pair of samples: every cell a dash, so the layout does not jump.</summary>
    public static ChartStats Empty { get; } = new("--", "--", "--", "--", "--", "--", "--");
}

/// <summary>
/// Everything the window binds to: telemetry, the mode and fan controls, the lighting page, the extras and the
/// settings page.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately free of Avalonia types. The telemetry timer lives in the window and calls
/// <see cref="RefreshTelemetryAsync"/>, so the whole view model can be exercised without a UI thread, a display or a
/// running message loop.
/// </para>
/// <para>
/// Every write goes through <see cref="RunWriteAsync"/>, which serialises the calls and turns a refusal into a status
/// line instead of an exception. The backend already gates its own writes; this gate exists so two clicks cannot
/// queue two writes and leave the status line describing the wrong one.
/// </para>
/// </remarks>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly AppSettings _settings;
    private readonly IHardwareController? _hardware;
    /// <summary>
    /// What the platform probe concluded, or null when there is no probe. An interface rather than the Linux record
    /// so the same window shows a Windows machine's identity, tier and report.
    /// </summary>
    private readonly IPlatformSnapshot? _platform;

    /// <summary>The backend that can be asked again, or null when this platform cannot re-probe.</summary>
    private readonly IRescannableBackend? _rescan;

    /// <summary>
    /// The optional sign-in launch, or null when this platform has no such mechanism. Windows supplies one so the app
    /// can start elevated without a UAC prompt; the settings card hides itself everywhere else.
    /// </summary>
    private readonly IAutoStartService? _autoStart;

    /// <summary>
    /// The battery refresh-rate policy, or null when the caller did not wire it. Null hides the settings switch, which
    /// is what a platform that cannot change the refresh rate should do.
    /// </summary>
    private readonly RefreshRateAutomation? _refreshRate;

    /// <summary>Where the AC/battery state comes from, or null when the caller did not supply one.</summary>
    private readonly Func<(bool OnAc, int? BatteryPercent)>? _powerState;

    /// <summary>
    /// The processor's boost control, or null when the caller did not wire one. Null disables the CPU BOOST pills
    /// rather than offering three buttons that write nowhere.
    /// </summary>
    private readonly ICpuBoostAccess? _cpuBoost;

    private readonly string _settingsPath;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    /// <summary>
    /// How long the shutdown path waits for the write gate before giving up. A step stuck in a WMI call must not hold
    /// the exit open: the machine is going down either way, and a bounded failure is reported instead of a hang.
    /// </summary>
    private static readonly TimeSpan WriteGateTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The three sliders that write to the machine. A drag raises a change per pixel, and each change would otherwise
    /// be a settings file write and a firmware call; the pause collapses the burst into one of each.
    /// </summary>
    private readonly Debouncer _lightingDebounce = new(TimeSpan.FromMilliseconds(350));
    private readonly Debouncer _customFanDebounce = new(TimeSpan.FromMilliseconds(400));
    private readonly Debouncer _curveDebounce = new(TimeSpan.FromMilliseconds(400));

    /// <summary>Automatic game switching, or null when the caller did not wire it (a test that does not need it).</summary>
    private readonly GameProfileService? _gameProfiles;

    /// <summary>School mode, or null when the caller did not wire it.</summary>
    private readonly SchoolModeService? _schoolMode;

    private readonly RgbProfileService _rgbProfiles;

    /// <summary>
    /// The installed-game library, or null when the caller did not wire one. Null disables the scan button rather than
    /// offering a click that finds nothing.
    /// </summary>
    private readonly IGameLibraryScanner? _gameLibrary;

    /// <summary>
    /// The mouse's onboard profiles, or null when the caller did not wire one. Null hides the FARE row and the mouse
    /// card, which is what a platform with no such device should do.
    /// </summary>
    private readonly IMouseProfileService? _mouseProfiles;

    /// <summary>
    /// The file picker and the confirmation, or null when the caller did not wire them (a test that does not need
    /// them). Null makes the two commands that need a dialog report that they cannot run.
    /// </summary>
    private readonly IDialogService? _dialogs;

    /// <summary>
    /// The operating system's half of the monitoring page: model strings, load, package watts, VRAM and RAM. Null
    /// leaves those rows at zero, which the page reads as "no reading" rather than as a measurement.
    /// </summary>
    private readonly IHardwareMonitor? _monitor;

    /// <summary>
    /// The standby-list cleaner, or null when the caller did not wire one. Null hides the card, which is what a
    /// platform with no standby list should do.
    /// </summary>
    private readonly IMemoryCleaner? _memoryCleaner;

    /// <summary>The palette applier, or null when the caller did not wire one (a test that does not need it).</summary>
    private readonly IThemeApplier? _themeApplier;

    /// <summary>
    /// The backend's OpenRGB status, or null when the platform does not speak the SDK. Null hides the lighting page's
    /// status line rather than claiming the server is absent.
    /// </summary>
    private readonly IOpenRgbStatusProvider? _openRgb;

    /// <summary>
    /// The last minute of telemetry, one ring buffer per series. Kept here rather than in the chart so the samples
    /// survive a view being re-created by a tab switch, and so a test can assert the history without a display.
    /// </summary>
    private readonly SparklineHistory _cpuTemperatureHistory = new();
    private readonly SparklineHistory _gpuTemperatureHistory = new();
    private readonly SparklineHistory _sysTemperatureHistory = new();
    private readonly SparklineHistory _cpuFanHistory = new();
    private readonly SparklineHistory _gpuFanHistory = new();

    /// <summary>
    /// Every sample inside the chart's window, kept whole rather than pre-reduced to one series: the selector can switch
    /// to any of the nine metrics and redraw from what is already held, instead of starting from an empty chart.
    /// </summary>
    private readonly List<MonitorData> _telemetryHistory = new();

    /// <summary>The window the chart and the mode trace both draw: one minute, matching the sparkline history.</summary>
    private static readonly TimeSpan ChartWindow = TimeSpan.FromSeconds(60);

    /// <summary>
    /// What ends the version part of a tag: <c>1.2.5-f</c> and <c>1.2.5+abc123</c> both mean 1.2.5 to a user.
    /// </summary>
    private static readonly SearchValues<char> VersionSeparators = SearchValues.Create("-+");

    // The monitoring page's live readings, filled from the sample the platform monitor enriched.
    private int _cpuCores;
    private double _cpuUsage;
    private double _cpuPowerWatts;
    private double _cpuFrequencyMhz;
    private double _gpuUsage;
    private double _gpuPowerWatts;
    private double _gpuFrequencyMhz;
    private double _vramUsedMb;
    private double _vramTotalMb;
    private double _ramUsage;
    private double _ramTotalMb;

    // The chart's series, its samples and its statistics, recomputed from the history on every tick.
    private ChartMetricChoice? _selectedChartMetric;
    private IReadOnlyList<double> _chartValues = Array.Empty<double>();
    private (double Low, double High) _chartRange = (0, 1);
    private ChartStats _chartStats = ChartStats.Empty;

    // The mode changes inside the chart window, as fractions of its width.
    private readonly List<(DateTime At, OperatingMode Mode)> _modeMarks = new();
    private IReadOnlyList<double> _modeChangeMarks = Array.Empty<double>();

    // The lighting page's direction list, the zone the deck pointed the palette at, and the extra switch.
    private DirectionChoice? _selectedDirection;
    private bool _winKeyLockOn;

    // The on-screen display: whether it is drawn, which corner, and which display.
    private bool _osdEnabled;
    private int _osdCorner;
    private int _osdMonitor;

    // The standby-list cleaner's last result.
    private bool _memoryCleanRunning;
    private string _memoryCleanStatus = "";
    private bool _memoryCleanSucceeded;

    /// <summary>
    /// Set by the composition root when the settings file had to be discarded, and raised once the window is
    /// listening. Null when the file loaded normally.
    /// </summary>
    private SettingsLoadSource? _settingsRecovery;

    /// <summary>
    /// Guards <see cref="InitializeAsync"/>, which is the first read of every service and the one place a crash
    /// record is replayed.
    /// </summary>
    /// <remarks>
    /// The window guards its own setup, but the view model is reachable without a window — the shell calls it for a
    /// launch that starts in the tray — so the guard belongs here too. A second pass would replay a game-session
    /// record written by a game that started after the first pass, and put the pre-game mode and fan back over a game
    /// that is still running.
    /// </remarks>
    private readonly OneShotGate _initialized = new();

    /// <summary>False while the window is hidden or minimised; see <see cref="ChartVisible"/>.</summary>
    private bool _chartVisible = true;

    private FanCurveEvaluator _curveEvaluator;
    private HardwareCapabilities _capabilities = HardwareCapabilities.Unknown;

    /// <summary>
    /// The thermal floor for the manual custom-speed sliders. Separate from <see cref="_curveEvaluator"/> because the
    /// two answer different questions: the curve decides what the temperature asks for, this decides how far the
    /// user's own number may be lowered while the die is hot.
    /// </summary>
    private readonly ManualFanFloor _manualFanFloor = new();

    /// <summary>
    /// The fan curve's own clock. Runs whether or not the window is on screen, so hiding the window to play a game
    /// does not slow the curve down to the tray's five-second telemetry rate.
    /// </summary>
    private readonly FanCurveLoop _curveLoop;

    /// <summary>
    /// Catches a fan that has stopped while its die is hot. Fed from the telemetry tick, and only with readings the
    /// Acer service actually produced: the fallback path reports 0 RPM because nothing measured the fans.
    /// </summary>
    private readonly FanStallWatchdog _fanStall;

    /// <summary>Guards the stall watchdog's alert so a confirmed fault is reported once, not on every tick.</summary>
    private bool _fanStallReported;

    /// <summary>
    /// True while the view model is filling its controls from the hardware. It starts true because the constructor
    /// populates the mode and fan-mode lists, and a selection change there is not a user action: without this the
    /// window would write "Auto" over the fan mode the machine was already in, on every launch, before it had read
    /// anything. <see cref="InitializeAsync"/> clears it once the first read is done.
    /// </summary>
    private bool _suppressWrite = true;

    /// <summary>
    /// The capability probe the composition root started on the thread pool. Null when the caller built the view model
    /// without one, which is every test that does not go through <see cref="AppHost"/>.
    /// </summary>
    private readonly Task? _probeTask;

    /// <summary>
    /// True once the probe has finished, so the fan loop and the hardware controls can be trusted with a write.
    /// </summary>
    private bool _hardwareReady;
    private bool _refreshing;
    private bool _disposed;

    /// <summary>Telemetry ticks since the window opened. Game detection runs on every other one.</summary>
    private int _tick;

    private ShellLabels _labels = new();
    private string _status = "";
    private bool _isReady;

    private int _cpuTemperature;
    private int _gpuTemperature;
    private int _sysTemperature;
    private int _cpuFanRpm;
    private int _gpuFanRpm;
    private bool _fanRpmMeasured;

    private ModeChoice? _selectedMode;
    private FanModeChoice? _selectedFanMode;
    private int _customCpuPercent = 60;
    private int _customGpuPercent = 60;
    private bool _curveEnabled;

    private EffectChoice? _selectedEffect;
    private int _brightnessLevel = 5;
    private int _speedLevel = 3;
    private int _directionLevel = 1;
    private string _primaryColor = "#00E5FF";
    private string _zone1 = "#00E5FF";
    private string _zone2 = "#00E5FF";
    private string _zone3 = "#00E5FF";
    private string _zone4 = "#00E5FF";

    private bool _coolBoostOn;
    private bool _lcdOverdriveOn;
    private bool _backlightTimeoutOn;
    private bool _batteryLimitOn;
    private bool _autoRefreshRateOnBattery;
    private string _refreshRateStatus = "";
    private bool _autoStartOn;
    private string _autoStartStatus = "";

    private LocLanguage? _selectedLanguage;
    private ThemeChoice? _selectedTheme;
    private UiFontChoice? _selectedUiFont;

    /// <summary>The machine's display language, read on first use; see <see cref="SystemLanguage"/>.</summary>
    private string? _systemLanguage;
    private string _udevCommand = "";
    private bool _needsUdevRule;

    private GpuModeChoice? _selectedGpuMode;
    private bool _schoolModeOn;
    private string _schoolModeDetail = "";
    private RgbPreset? _selectedRgbProfile;
    private bool _suppressProfileApply;
    private string _newProfileName = "";
    private string _rgbProfileStatus = "";
    private bool _gameActive;
    private string _gameStatus = "";
    private string _activeGameName = "";
    private GameProfileRow? _selectedGameProfile;
    private string _newGameExe = "";
    private string _newGameName = "";
    private ModeChoice? _newGameMode;
    private FanModeChoice? _newGameFanMode;
    private string _gameProfileStatus = "";

    // The Game Profiles page: the library scan, the per-game targets, and the mouse.
    private string _gameScanStatus = "";
    private bool _gameScanRunning;
    private ModeChoice? _selectedGameMode;
    private CpuBoostChoice? _selectedGameCpuBoost;
    private FanModeChoice? _selectedGameFanMode;
    private MouseProfileOption? _selectedGameMouseProfile;
    private bool _gameAutoApply = true;
    private MouseProfileOption? _selectedMouseProfile;
    private string _mouseProfileStatus = "";

    // The fan-curve presets, the pending MUX mode and the sentence under the MUX row.
    private CurvePresetChoice? _selectedCurvePreset;
    private GpuOperatingMode? _pendingGpuMode;
    private string _gpuModeNote = "";

    // The rail width and the toast's optional action.
    private double _sidebarWidth;
    private string _toastActionLabel = "";

    // The shell: which of the five pages is showing, and whether the 60-second band is drawn.
    private NavTab? _selectedTab;
    private bool _sparklineBandVisible = true;

    // The fan segmented control and the CPU-boost segmented control.
    private FanSegmentChoice? _selectedFanSegment;
    private CpuBoostChoice? _selectedCpuBoost;
    private bool _fanProtectionOn;

    // The colour palette writes into whichever target the lighting page has focused.
    private ColorSwatch? _selectedSwatch;
    private int _activeZone;

    // The header's battery figure, refreshed on the telemetry tick.
    private int? _batteryPercent;
    private bool? _onAcPower;

    /// <summary>
    /// <paramref name="gameProfiles"/>, <paramref name="schoolMode"/>, <paramref name="rgbProfiles"/> and
    /// <paramref name="themeApplier"/> are optional so the window can be built without them — a test that only checks
    /// telemetry does not need a process scanner — and so the composition root stays the only place that decides which
    /// platform services exist.
    /// </summary>
    public MainViewModel(
        AppSettings settings,
        IHardwareController? hardware,
        IPlatformSnapshot? platform,
        string settingsPath,
        GameProfileService? gameProfiles = null,
        SchoolModeService? schoolMode = null,
        RgbProfileService? rgbProfiles = null,
        IThemeApplier? themeApplier = null,
        IRescannableBackend? rescan = null,
        IAutoStartService? autoStart = null,
        RefreshRateAutomation? refreshRate = null,
        Func<(bool OnAc, int? BatteryPercent)>? powerState = null,
        ICpuBoostAccess? cpuBoost = null,
        IGameLibraryScanner? gameLibrary = null,
        IMouseProfileService? mouseProfiles = null,
        IHardwareMonitor? monitor = null,
        IMemoryCleaner? memoryCleaner = null,
        IDialogService? dialogs = null,
        IOpenRgbStatusProvider? openRgb = null,
        IBatteryHealthService? batteryHealth = null,
        IStorageHealthService? storageHealth = null,
        IDebloatService? debloat = null,
        ISystemToolsService? systemTools = null,
        IUpdateService? updates = null,
        IDiagnosticsService? diagnostics = null,
        IDirectWmiControl? directWmi = null,
        IRunAtStartupService? runAtStartup = null,
        INitroKeyListener? nitroKey = null,
        INitroSenseWatcher? nitroSense = null,
        FanStallWatchdog? fanStall = null,
        Action<string>? urlOpener = null,
        Task? probeTask = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _hardware = hardware;
        _platform = platform;
        _rescan = rescan;
        _autoStart = autoStart;
        _refreshRate = refreshRate;
        _powerState = powerState;
        _autoRefreshRateOnBattery = settings.AutoRefreshRateSwitching;
        _settingsPath = settingsPath;
        _gameProfiles = gameProfiles;
        _schoolMode = schoolMode;
        _rgbProfiles = rgbProfiles ?? new RgbProfileService();
        _themeApplier = themeApplier;
        _cpuBoost = cpuBoost;
        _gameLibrary = gameLibrary;
        _mouseProfiles = mouseProfiles;
        _monitor = monitor;
        _memoryCleaner = memoryCleaner;
        _dialogs = dialogs;
        _openRgb = openRgb;
        _batteryHealth = batteryHealth;
        _storageHealth = storageHealth;
        _debloat = debloat;
        _systemTools = systemTools;
        _updates = updates;
        _diagnostics = diagnostics;
        _directWmi = directWmi;
        _runAtStartup = runAtStartup;
        _nitroKey = nitroKey;
        _nitroSense = nitroSense;
        _urlOpener = urlOpener;
        _probeTask = probeTask;

        // The watchdog is a dependency rather than a field initialiser so a test can shorten its ten-second sustain
        // window. Production always gets the default one.
        _fanStall = fanStall ?? new FanStallWatchdog();

        _schoolModeOn = settings.SchoolModeEnabled;
        _fanProtectionOn = settings.FanFailureProtection;
        _sidebarWidth = AppSettings.ClampSidebarWidth(settings.SidebarWidth);
        _selectedMouseProfile = null;

        // The two custom speeds are the user's own numbers, so they come back as they were left. Without this the
        // sliders opened at their defaults and the first drag wrote those defaults over the stored choice.
        _customCpuPercent = AppSettings.ClampCustomFanPercent(settings.CustomFanCpuPercent);
        _customGpuPercent = AppSettings.ClampCustomFanPercent(settings.CustomFanGpuPercent);

        _curveEvaluator = new FanCurveEvaluator(settings.BuildFanCurve());

        // The curve's own clock, started by InitializeAsync once the first read is done and stopped by Dispose. It is
        // built here rather than lazily so a test can assert the cadence without a window.
        _curveLoop = new FanCurveLoop(StepFanAsync);

        // A settings file written by an older build can carry a profile for a helper or a duplicate of a title that is
        // already listed. Cleaning it here is the one place it is safe: the scan filters its own candidates, so running
        // this afterwards would delete a profile the user added by hand.
        if (GameLibraryRules.SanitizeProfiles(settings.GameProfiles) > 0)
        {
            AppLog.Info("Oyun profilleri temizlendi", "Yardımcı veya yinelenen kayıtlar kaldırıldı.");
        }

        BuildCurveAnchors();
        BuildChoices();
        BuildThemeChoices();
        BuildGameProfileRows();
        BuildCurvePresets();
        BuildMouseProfileChoices();
        BuildGameTargetChoices();
        RefreshRgbProfiles();
        BuildNavTabs();
        BuildFanSegments();
        BuildCpuBoostChoices();
        BuildColorPalette();
        BuildChartMetrics();
        BuildDirectionChoices();
        _osdEnabled = settings.OsdEnabled;
        _osdCorner = Math.Clamp(settings.OsdCorner, 0, 3);
        _osdMonitor = Math.Max(0, settings.OsdMonitor);

        // The stored palette goes on before the window exists, so the first frame is already the right colour rather
        // than flashing the default and repainting.
        _themeApplier?.Apply(_selectedTheme?.Theme ?? settings.Theme);

        // Same for the typeface: the first frame is already in the chosen font.
        _selectedUiFont = UiFontCatalog.Resolve(settings.UiFont);
        _themeApplier?.ApplyFont(_selectedUiFont.Stack);

        _selectedLanguage = Loc.Languages.FirstOrDefault(l => l.Code == Loc.Language) ?? Loc.Languages[0];
        _needsUdevRule = platform?.NeedsUdevRule == true;
        if (_needsUdevRule)
        {
            var rulePath = UdevGuide.EnsureRuleFile();
            _udevCommand = rulePath == null ? "" : UdevGuide.InstallCommand(rulePath);
        }

        // The pill is short on purpose; the banner carries the sentence that explains it.
        // The footer pill makes the same claim as the banner, so it uses the same rule: a backend that has not opened
        // its transport yet is not a machine without hardware. A machine that did find its hardware says nothing at
        // startup: the WPF window only raised the banner for something that had just happened.
        _status = ShowNoBackendBanner ? _labels.NoBackend : "";

        ApplyFanModeCommand = new AsyncCommand(ApplyFanModeAsync);
        AddGameProfileCommand = new RelayCommand(AddGameProfile);
        ApplyCurveCommand = new AsyncCommand(ApplyCurveAsync);
        ApplyLightingCommand = new AsyncCommand(ApplyLightingAsync);
        TurnOffLightingCommand = new AsyncCommand(TurnOffLightingAsync);
        CopyUdevCommand = new RelayCommand(CopyUdevCommandText);
        RescanCommand = new AsyncCommand(RescanAsync);
        ApplyGpuModeCommand = new AsyncCommand(ApplyGpuModeAsync);
        ApplyRgbProfileCommand = new AsyncCommand(ApplyRgbProfileAsync);
        SaveRgbProfileCommand = new RelayCommand(SaveRgbProfile);
        DeleteRgbProfileCommand = new RelayCommand(DeleteRgbProfile);
        CopyHardwareReportCommand = new RelayCommand(CopyHardwareReport);
        ScanGamesCommand = new AsyncCommand(ScanGamesAsync);
        AddGameFromFileCommand = new AsyncCommand(AddGameFromFileAsync);
        DeleteSelectedGameCommand = new AsyncCommand(DeleteSelectedGameAsync);
        CopyDesktopCurveCommand = new RelayCommand(CopyDesktopCurveToGame);
        ToastActionCommand = new RelayCommand(RunToastAction);
        CleanMemoryCommand = new RelayCommand(() => _ = CleanMemoryAsync());
        GetMemoryCleanerCommand = new RelayCommand(() => OpenUrl(MemoryCleanerTool.ReleasesUrl));
        CycleOsdCornerCommand = new RelayCommand(CycleOsdCorner);
        ToggleOsdCommand = new RelayCommand(() => OsdEnabled = !OsdEnabled);
        ApplyAllZonesCommand = new RelayCommand(ApplyAllZones);
        BuildPhase3Commands();
    }

    // ---------------------------------------------------------------- labels and status

    /// <summary>Every fixed text. Replaced wholesale when the language changes, which repaints the window.</summary>
    public ShellLabels Labels
    {
        get => _labels;
        private set
        {
            if (!Set(ref _labels, value)) return;
            foreach (var anchor in CurveAnchors) anchor.Labels = value;
            foreach (var anchor in GameCurveAnchors) anchor.Labels = value;
            Raise(nameof(CurvePreview));
            Raise(nameof(CapabilitySummaryText));
            Raise(nameof(TierDisplay));
            // The rows carry translated names, so they are rebuilt rather than re-raised.
            BuildCapabilityRows();
            BuildGameProfileRows();
            BuildNavTabs();
            BuildFanSegments();
            BuildCpuBoostChoices();
            BuildColorPalette();
            Raise(nameof(SparklineBandToggleText));
            Raise(nameof(SparklineRangeText));
            Raise(nameof(SparklineNowText));
            Raise(nameof(SparklineEmpty));
            Raise(nameof(SystemGameText));
            Raise(nameof(ShowSystemGameRow));
            Raise(nameof(ActiveZoneText));
            Raise(nameof(ModeBadgeText));
            // The launcher line is a sentence built from these labels, so it is re-raised rather than rebuilt.
            Raise(nameof(NitroSenseLauncherStateText));
            Raise(nameof(SchoolModeStateText));
        }
    }

    /// <summary>The last thing that happened, printed at the foot of the page. Never an exception message on its own.</summary>
    public string Status
    {
        get => _status;
        private set
        {
            if (!Set(ref _status, value)) return;
            Raise(nameof(HasStatus));
        }
    }

    /// <summary>True while there is something to report, which is what shows the toast.</summary>
    public bool HasStatus => !string.IsNullOrWhiteSpace(_status);

    /// <summary>True once the backend has answered. The window uses it to hide the "no backend" banner.</summary>
    public bool IsReady
    {
        get => _isReady;
        private set => Set(ref _isReady, value);
    }

    /// <summary>
    /// True while the capability probe is still running. The scanning banner is shown, so a control cannot be clicked
    /// against a capability list that has not been read yet.
    /// </summary>
    public bool IsHardwareBusy => !_hardwareReady;

    /// <summary>
    /// The same fact the other way round, because the page binds its <c>IsEnabled</c> to it. Two properties rather than
    /// a negated binding: a binding that silently fails to resolve leaves the page enabled, which is the one outcome
    /// this pair exists to prevent.
    /// </summary>
    public bool IsHardwareReady => _hardwareReady;

    /// <summary>
    /// True when there is a backend and it found something to talk to. A Linux machine with no <c>acer-wmi</c> node
    /// still gets a controller object, so "the object exists" is not the same question as "the hardware is there".
    /// </summary>
    public bool HasBackend => _hardware != null && _hardware.IsAvailable;

    public bool HasNoBackend => !HasBackend;

    /// <summary>
    /// True only when the app genuinely has nothing to talk to, which is what the banner and the settings row claim.
    /// </summary>
    /// <remarks>
    /// A backend that exists but has not opened its transport yet is not the same thing. On Windows the transport
    /// opens on demand, so <see cref="HasBackend"/> is false for the first seconds of every launch — long enough to
    /// paint "no hardware backend" across the top of a working Nitro 16. The banner therefore needs a backend object
    /// to be missing, or a platform whose negative answer is conclusive.
    /// </remarks>
    public bool ShowNoBackendBanner =>
        _hardware == null || (NegativeAnswersAreConclusive && !HasBackend);

    /// <summary>
    /// The second line of the banner. Two different machines reach this state — a Windows build with no backend at
    /// all, and a Linux machine whose kernel has no Acer module — and they need different sentences.
    /// </summary>
    public string NoBackendDetail => _hardware == null ? Labels.NoBackendDetail : Labels.NoAcerNodes;

    // ---------------------------------------------------------------- the shell

    /// <summary>The five pages of the rail, in the order the WPF window listed them.</summary>
    public ObservableCollection<NavTab> NavTabs { get; } = new();

    /// <summary>
    /// The page on show. Setting it moves the rail marker and swaps the body; nothing is written to the settings file,
    /// because which page a user was reading is not a preference worth persisting.
    /// </summary>
    public NavTab? SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (!Set(ref _selectedTab, value)) return;
            foreach (var tab in NavTabs) tab.IsSelected = ReferenceEquals(tab, value);
            Raise(nameof(IsModesPage));
            Raise(nameof(IsMonitoringPage));
            Raise(nameof(IsLightingPage));
            Raise(nameof(IsBatteryPage));
            Raise(nameof(IsToolsPage));

            // The 30-second timeout is read again here rather than only at startup: the elevated helper is not
            // connected yet when the window opens, so the boot read can come back empty and leave the switch on a
            // default the keyboard does not share.
            if (IsLightingPage) _ = RefreshBacklightTimeoutAsync();
        }
    }

    public bool IsModesPage => SelectedTab?.Key == NavModesKey;
    public bool IsMonitoringPage => SelectedTab?.Key == NavMonitoringKey;
    public bool IsLightingPage => SelectedTab?.Key == NavLightingKey;
    public bool IsBatteryPage => SelectedTab?.Key == NavBatteryKey;
    public bool IsToolsPage => SelectedTab?.Key == NavToolsKey;

    public const string NavModesKey = "modes";
    public const string NavMonitoringKey = "monitoring";
    public const string NavLightingKey = "lighting";
    public const string NavBatteryKey = "battery";
    public const string NavToolsKey = "tools";

    /// <summary>Moves the rail to <paramref name="key"/>. An unknown key is ignored rather than clearing the page.</summary>
    public void SelectTab(string key)
    {
        var tab = NavTabs.FirstOrDefault(t => t.Key == key);
        if (tab != null) SelectedTab = tab;
    }

    private void BuildNavTabs()
    {
        var previous = SelectedTab?.Key ?? NavModesKey;
        NavTabs.Clear();
        NavTabs.Add(new NavTab(NavModesKey, Labels.NavModes, NavIconModes, SelectNavTab));
        NavTabs.Add(new NavTab(NavMonitoringKey, Labels.NavMonitoring, NavIconMonitoring, SelectNavTab));
        NavTabs.Add(new NavTab(NavLightingKey, Labels.NavLighting, NavIconLighting, SelectNavTab));
        NavTabs.Add(new NavTab(NavBatteryKey, Labels.NavBattery, NavIconBattery, SelectNavTab));
        NavTabs.Add(new NavTab(NavToolsKey, Labels.NavTools, NavIconTools, SelectNavTab));
        SelectedTab = NavTabs.FirstOrDefault(t => t.Key == previous) ?? NavTabs[0];
    }

    private void SelectNavTab(NavTab tab) => SelectedTab = tab;

    // The rail glyphs are the WPF ones: Segoe Fluent codepoints, drawn by the icon font rather than by hand-built path
    // data. Hand-built paths never quite matched the weight of the rest of the window, and the font is already present
    // on every Windows install this app targets.
    private const string NavIconModes = "\uE9D9";

    private const string NavIconMonitoring = "\uE9D2";

    private const string NavIconLighting = "\uE765";

    private const string NavIconBattery = "\uE83F";

    private const string NavIconTools = "\uE90F";

    /// <summary>
    /// The 60-second band at the top of the modes page. Hiding it is a view preference, not a setting: it is one click
    /// to bring back and it does not survive a restart, which is what a "get this out of my way" control should do.
    /// </summary>
    public bool SparklineBandVisible
    {
        get => _sparklineBandVisible;
        set
        {
            if (!Set(ref _sparklineBandVisible, value)) return;
            Raise(nameof(SparklineBandToggleText));
        }
    }

    public string SparklineBandToggleText => SparklineBandVisible ? Labels.HideCaption : Labels.ShowCaption;

    public RelayCommand ToggleSparklineBandCommand { get; private set; } = null!;

    /// <summary>"ARALIK 41–78 °C" over the last minute of CPU temperature.</summary>
    public string SparklineRangeText
    {
        get
        {
            var values = _cpuTemperatureHistory.Snapshot();
            if (values.Count == 0) return $"{Labels.RangeCaption} —";
            return $"{Labels.RangeCaption} {values.Min():F0}–{values.Max():F0} °C";
        }
    }

    /// <summary>"ŞİMDİ 62 °C".</summary>
    public string SparklineNowText => $"{Labels.NowCaption} {CpuTemperature} °C";

    /// <summary>
    /// True until the first sample lands. The band draws a placeholder rather than an empty frame, because a blank
    /// rectangle at the top of the page reads as a broken chart.
    /// </summary>
    public bool SparklineEmpty => _cpuTemperatureHistory.Snapshot().Count == 0;

    public string SparklineEmptyText => Labels.WaitingForRecording;

    // ---------------------------------------------------------------- the live system box

    /// <summary>CPU temperature as the rail prints it.</summary>
    public string SystemCpuText => CpuTemperatureText;

    public string SystemGpuText => GpuTemperatureText;

    public string SystemFanText => CpuFanRpmText;

    /// <summary>The boost mode in words, or an em dash when this platform has no boost control.</summary>
    /// <remarks>
    /// The pills always hold a selection so the segmented control has something to paint, which means the label alone
    /// cannot tell a real reading from the default. The rail asks the capability instead, or a machine with no boost
    /// node would claim to be running aggressive boost.
    /// </remarks>
    public string SystemBoostText => CpuBoostAvailable ? _selectedCpuBoost?.Label ?? "—" : "—";

    /// <summary>
    /// The game the profile service switched to, or an em dash. The rail row is one line wide, so it carries the name
    /// rather than the sentence the toast and the game card use.
    /// </summary>
    public string SystemGameText => string.IsNullOrWhiteSpace(_activeGameName) ? "—" : _activeGameName;

    /// <summary>
    /// True when the rail draws the OYUN row at all. With no game running there is nothing to put there, and a row
    /// reading "—" is a line the user has to read to learn nothing. The row is the last one in the box, so it appears
    /// at the bottom the moment a game starts.
    /// </summary>
    public bool ShowSystemGameRow => !string.IsNullOrWhiteSpace(_activeGameName);

    /// <summary>The version badge beside the wordmark, read from the assembly rather than typed twice.</summary>
    public string VersionText { get; } = ReadVersion();

    private static string ReadVersion()
    {
        var raw = typeof(MainViewModel).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion;

        if (string.IsNullOrWhiteSpace(raw)) return "v0.0.0";

        // "1.2.5-f" and "1.2.5+abc123" both mean 1.2.5 to a user.
        var cut = raw.AsSpan().IndexOfAny(VersionSeparators);
        return "v" + (cut > 0 ? raw[..cut] : raw);
    }

    /// <summary>The battery percentage in the header, or null when this platform cannot report one.</summary>
    public int? BatteryPercent
    {
        get => _batteryPercent;
        private set
        {
            if (!Set(ref _batteryPercent, value)) return;
            Raise(nameof(BatteryPercentText));
            Raise(nameof(BatteryBarPercent));
        }
    }

    public string BatteryPercentText => BatteryPercent is { } percent ? $"%{percent}" : "%—";

    /// <summary>True on the charger, false on battery, null when this platform cannot say.</summary>
    public bool? OnAcPower
    {
        get => _onAcPower;
        private set
        {
            if (!Set(ref _onAcPower, value)) return;
            Raise(nameof(BatteryStateText));

            // Eco is a battery mode and Turbo/Performance are charger modes, so plugging the charger in changes which
            // cards exist. Rebuilding here is what makes the rule live rather than a decision taken at launch.
            RefreshModeVisibility();
        }
    }

    /// <summary>
    /// Reads the charger state and the battery figure from the platform. Called before the mode list is built and on
    /// every telemetry tick, so the header and the mode cards can never disagree about whether the charger is in.
    /// </summary>
    private void ReadPowerState()
    {
        if (_powerState == null) return;

        try
        {
            var (onAc, percent) = _powerState();
            OnAcPower = onAc;
            BatteryPercent = percent;
        }
        catch (Exception ex)
        {
            AppLog.Warn(Loc.T("Pil yüzdesi okunamadı"), ex.Message);
        }
    }

    /// <summary>
    /// Re-filters the mode cards without writing the mode back. The machine is already in the mode it is in, and a
    /// rebuild that applied it would fight the firmware every time the charger was plugged in.
    /// </summary>
    private void RefreshModeVisibility()
    {
        bool previous = _suppressWrite;
        _suppressWrite = true;
        try
        {
            BuildModeChoices();
        }
        finally
        {
            _suppressWrite = previous;
        }
    }

    /// <summary>The battery figure as a 0–100 fill, for the bar under it.</summary>
    public double BatteryBarPercent => Math.Clamp(BatteryPercent ?? 0, 0, 100);

    /// <summary>The one-line power state under the battery figure on the health page.</summary>
    public string BatteryStateText => OnAcPower switch
    {
        true => Labels.PluggedIn,
        false => Labels.OnBattery,
        _ => Labels.Unknown
    };

    /// <summary>The active mode in the header, uppercased so it reads as a printed stamp.</summary>
    public string ModeBadgeText => (SelectedMode?.Label ?? "—").ToUpperInvariant();

    // ---------------------------------------------------------------- fan segments

    /// <summary>The four pills of the fan segmented control.</summary>
    public ObservableCollection<FanSegmentChoice> FanSegments { get; } = new();

    /// <summary>
    /// The chosen fan pill. <see cref="FanSegmentKind.Curve"/> is not a firmware mode: it turns the drawn curve on and
    /// leaves the mode alone, which is why it is a segment of its own rather than a fourth <see cref="FanMode"/>.
    /// </summary>
    public FanSegmentChoice? SelectedFanSegment
    {
        get => _selectedFanSegment;
        set
        {
            if (!Set(ref _selectedFanSegment, value) || value == null) return;
            foreach (var segment in FanSegments) segment.IsSelected = ReferenceEquals(segment, value);
            ApplyFanSegment(value.Kind);
        }
    }

    private void BuildFanSegments()
    {
        FanSegments.Clear();
        FanSegments.Add(new FanSegmentChoice(FanSegmentKind.Auto, Labels.Auto, SelectFanSegment));
        FanSegments.Add(new FanSegmentChoice(FanSegmentKind.Max, Labels.Max, SelectFanSegment));
        FanSegments.Add(new FanSegmentChoice(FanSegmentKind.Custom, Labels.FanSegmentCustom, SelectFanSegment));
        FanSegments.Add(new FanSegmentChoice(FanSegmentKind.Curve, Labels.FanSegmentCurve, SelectFanSegment));
        SyncFanSegment();
    }

    private void SelectFanSegment(FanSegmentChoice segment) => SelectedFanSegment = segment;

    /// <summary>Paints the pill that matches the state the machine is actually in.</summary>
    private void SyncFanSegment()
    {
        var kind = CurveEnabled
            ? FanSegmentKind.Curve
            : SelectedFanMode?.Mode switch
            {
                FanMode.Max => FanSegmentKind.Max,
                FanMode.Custom => FanSegmentKind.Custom,
                _ => FanSegmentKind.Auto
            };

        var match = FanSegments.FirstOrDefault(s => s.Kind == kind);
        if (match == null) return;

        _selectedFanSegment = match;
        foreach (var segment in FanSegments) segment.IsSelected = ReferenceEquals(segment, match);
        Raise(nameof(SelectedFanSegment));
        Raise(nameof(CurveEditorVisible));
    }

    private void ApplyFanSegment(FanSegmentKind kind)
    {
        if (kind == FanSegmentKind.Curve)
        {
            CurveEnabled = true;
            return;
        }

        // Leaving the curve has to turn it off, or the curve keeps writing over the mode that was just chosen.
        if (CurveEnabled) CurveEnabled = false;

        var mode = kind switch
        {
            FanSegmentKind.Max => FanMode.Max,
            FanSegmentKind.Custom => FanMode.Custom,
            _ => FanMode.Auto
        };

        var card = FanModes.FirstOrDefault(f => f.Mode == mode);
        if (card != null) SelectedFanMode = card;
    }

    /// <summary>True when the Eğri pill is the chosen one, which is what opens the curve editor under the card.</summary>
    public bool CurveEditorVisible => SelectedFanSegment?.Kind == FanSegmentKind.Curve;

    /// <summary>
    /// Drop to Eco when a fan stops reporting. Persisted, because a machine whose fan has failed is a machine that
    /// needs the protection on the next boot too.
    /// </summary>
    public bool FanProtectionOn
    {
        get => _fanProtectionOn;
        set
        {
            if (!Set(ref _fanProtectionOn, value)) return;
            _settings.FanFailureProtection = value;
            SaveSettings();
        }
    }

    /// <summary>
    /// The fan bars. A tachometer reading has no natural ceiling, so the bar is scaled against a display ceiling rather
    /// than pretending to be a percentage of the fan's maximum: it is a shape, not a measurement.
    /// </summary>
    public double CpuFanBarPercent => Math.Clamp(CpuFanRpm * 100.0 / FanBarCeilingRpm, 0, 100);

    public double GpuFanBarPercent => Math.Clamp(GpuFanRpm * 100.0 / FanBarCeilingRpm, 0, 100);

    /// <summary>The top of the fan bar's scale. Above the highest reading a Nitro 16 reports.</summary>
    public const int FanBarCeilingRpm = 6000;

    // ---------------------------------------------------------------- CPU boost

    /// <summary>The three boost pills: aggressive, efficient, off.</summary>
    public ObservableCollection<CpuBoostChoice> CpuBoostChoices { get; } = new();

    public CpuBoostChoice? SelectedCpuBoost
    {
        get => _selectedCpuBoost;
        set
        {
            if (!Set(ref _selectedCpuBoost, value) || value == null) return;
            foreach (var choice in CpuBoostChoices) choice.IsSelected = ReferenceEquals(choice, value);
            Raise(nameof(SystemBoostText));
            _ = WriteCpuBoostAsync(value.Mode);
        }
    }

    /// <summary>False when the caller wired no boost control, which disables the pills instead of hiding them.</summary>
    public bool CpuBoostAvailable => _cpuBoost?.IsSupported == true;

    private void BuildCpuBoostChoices()
    {
        CpuBoostChoices.Clear();
        CpuBoostChoices.Add(new CpuBoostChoice(CpuBoostMode.Aggressive, Labels.BoostAggressive, SelectCpuBoost));
        CpuBoostChoices.Add(new CpuBoostChoice(CpuBoostMode.Efficient, Labels.BoostEfficient, SelectCpuBoost));
        CpuBoostChoices.Add(new CpuBoostChoice(CpuBoostMode.Disabled, Labels.BoostDisabled, SelectCpuBoost));

        var current = ReadCpuBoost();
        var match = CpuBoostChoices.FirstOrDefault(c => c.Mode == current) ?? CpuBoostChoices[0];
        _selectedCpuBoost = match;
        foreach (var choice in CpuBoostChoices) choice.IsSelected = ReferenceEquals(choice, match);

        // The rebuild replaced every instance, so the game form's target now points at an item the combo no longer
        // holds and would render blank. Re-resolve it against the new list.
        SyncGameTargets();
    }

    private void SelectCpuBoost(CpuBoostChoice choice) => SelectedCpuBoost = choice;

    private CpuBoostMode ReadCpuBoost()
    {
        if (_cpuBoost == null || !_cpuBoost.IsSupported) return CpuBoostMode.Aggressive;
        try
        {
            var onAc = _powerState?.Invoke().OnAc ?? true;
            return _cpuBoost.QueryBoostModeExact(onAc) ?? CpuBoostMode.Aggressive;
        }
        catch (Exception ex)
        {
            AppLog.Warn(Loc.T("CPU boost modu okunamadı"), ex.Message);
            return CpuBoostMode.Aggressive;
        }
    }

    private async Task WriteCpuBoostAsync(CpuBoostMode mode)
    {
        if (_cpuBoost == null || !_cpuBoost.IsSupported) return;
        try
        {
            bool ok = await _cpuBoost.SetBoostModeAsync(mode).ConfigureAwait(true);
            Status = ok ? Labels.Applied : Labels.WriteFailed;
        }
        catch (Exception ex)
        {
            AppLog.Warn(Loc.T("CPU boost modu yazılamadı"), ex.Message);
            Status = Labels.WriteFailed;
        }
    }

    // ---------------------------------------------------------------- the colour palette

    /// <summary>The ready-made swatches, so a colour is a click rather than a hex string.</summary>
    public ObservableCollection<ColorSwatch> ColorPalette { get; } = new();

    /// <summary>
    /// Which target the next swatch click writes to: 0 is the primary colour, 1–4 are the keyboard zones. The lighting
    /// page sets it when a zone row is focused.
    /// </summary>
    public int ActiveZone
    {
        get => _activeZone;
        set
        {
            if (!Set(ref _activeZone, value)) return;
            Raise(nameof(ActiveZoneText));
            Raise(nameof(IsPrimaryZoneActive));
            Raise(nameof(IsZone1Active));
            Raise(nameof(IsZone2Active));
            Raise(nameof(IsZone3Active));
            Raise(nameof(IsZone4Active));
            SyncSwatchSelection();
        }
    }

    public string ActiveZoneText => ActiveZone == 0 ? Labels.PrimaryColor : Labels.Zone(ActiveZone);

    // One flag per target, so a caller can ask which one the palette writes to without comparing numbers.
    public bool IsPrimaryZoneActive => ActiveZone == 0;
    public bool IsZone1Active => ActiveZone == 1;
    public bool IsZone2Active => ActiveZone == 2;
    public bool IsZone3Active => ActiveZone == 3;
    public bool IsZone4Active => ActiveZone == 4;

    public ColorSwatch? SelectedSwatch
    {
        get => _selectedSwatch;
        private set => Set(ref _selectedSwatch, value);
    }

    private void BuildColorPalette()
    {
        ColorPalette.Clear();
        // Eight swatches, chosen to be distinguishable on both the cream and the dark palettes and to cover the effects
        // the firmware ships: a red, an amber, a green, a cyan, a blue, a violet, a magenta and white.
        //
        // The red is pure #FF0000 on purpose. A "coral" red — the #D93829 this used to be — is the same colour on
        // screen, but the keyboard's diffuser mixes the three LEDs, so the green and blue it carries light the keys up
        // and the red arrives as salmon. On the deck the swatch has to be the colour the keys show.
        AddSwatch("#FF0000", Loc.T("Kırmızı"));
        AddSwatch("#F59E0B", Loc.T("Kehribar"));
        AddSwatch("#5F6B3E", Loc.T("Zeytin yeşili"));
        AddSwatch("#22D3EE", Loc.T("Buz mavisi"));
        AddSwatch("#3B82F6", Loc.T("Gök mavisi"));
        AddSwatch("#8B5CF6", Loc.T("Menekşe"));
        AddSwatch("#EC4899", Loc.T("Macenta"));
        AddSwatch("#F8FAFC", Loc.T("Beyaz"));

        ToggleSparklineBandCommand = new RelayCommand(() => SparklineBandVisible = !SparklineBandVisible);
        SyncSwatchSelection();
    }

    private void AddSwatch(string hex, string name) =>
        ColorPalette.Add(new ColorSwatch(hex, name, SelectSwatch));

    private void SelectSwatch(ColorSwatch swatch)
    {
        SelectedSwatch = swatch;
        switch (ActiveZone)
        {
            case 0: PrimaryColor = swatch.Hex; break;
            case 1: Zone1 = swatch.Hex; break;
            case 2: Zone2 = swatch.Hex; break;
            case 3: Zone3 = swatch.Hex; break;
            case 4: Zone4 = swatch.Hex; break;
        }
        SyncSwatchSelection();
    }

    /// <summary>Marks the swatch that matches the colour the active target already holds.</summary>
    private void SyncSwatchSelection()
    {
        var current = ActiveColor;

        foreach (var swatch in ColorPalette)
        {
            swatch.IsSelected = string.Equals(swatch.Hex, current, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ---------------------------------------------------------------- telemetry

    public int CpuTemperature
    {
        get => _cpuTemperature;
        private set
        {
            if (!Set(ref _cpuTemperature, value)) return;
            Raise(nameof(CpuTemperatureText));
            Raise(nameof(CurrentTemperature));
            Raise(nameof(CurvePreview));
            Raise(nameof(SystemCpuText));
            Raise(nameof(SparklineNowText));
        }
    }

    public int GpuTemperature
    {
        get => _gpuTemperature;
        private set
        {
            if (!Set(ref _gpuTemperature, value)) return;
            Raise(nameof(GpuTemperatureText));
            Raise(nameof(SystemGpuText));
        }
    }

    public int SysTemperature
    {
        get => _sysTemperature;
        private set
        {
            if (Set(ref _sysTemperature, value)) Raise(nameof(SysTemperatureText));
        }
    }

    public int CpuFanRpm
    {
        get => _cpuFanRpm;
        private set
        {
            if (!Set(ref _cpuFanRpm, value)) return;
            Raise(nameof(CpuFanRpmText));
            Raise(nameof(SystemFanText));
            Raise(nameof(CpuFanBarPercent));
        }
    }

    public int GpuFanRpm
    {
        get => _gpuFanRpm;
        private set
        {
            if (!Set(ref _gpuFanRpm, value)) return;
            Raise(nameof(GpuFanRpmText));
            Raise(nameof(GpuFanBarPercent));
        }
    }

    // The unit is part of the text rather than a StringFormat in XAML: a format string in a compiled binding is one
    // more thing that can silently render nothing, and these five are read at a glance.
    //
    // Every one of them goes through TelemetryFormatter, which is what makes "no reading" say so. A sensor that did
    // not answer reports 0, and 0 °C / 0 RPM / 0.0 % are not readings: they are the absence of one, and printing them
    // as figures claims the machine is at freezing point with its fans stopped.
    public string CpuTemperatureText => TelemetryFormatter.FormatCpuTemperature(CpuTemperature);
    public string GpuTemperatureText => TelemetryFormatter.FormatGpuTemperature(GpuTemperature);
    public string SysTemperatureText => TelemetryFormatter.FormatTemperature(SysTemperature);
    public string CpuFanRpmText => TelemetryFormatter.FormatFanRpm(CpuFanRpm);
    public string GpuFanRpmText => TelemetryFormatter.FormatFanRpm(GpuFanRpm);

    /// <summary>
    /// True when the RPM numbers are tachometer readings. False means they were derived from a fan percentage and the
    /// model's ceiling, which is a restatement of what the app asked for rather than a measurement — the window says
    /// so next to the number instead of presenting a guess as a reading.
    /// </summary>
    public bool FanRpmMeasured
    {
        get => _fanRpmMeasured;
        private set
        {
            if (!Set(ref _fanRpmMeasured, value)) return;
            Raise(nameof(FanRpmSourceText));
        }
    }

    public string FanRpmSourceText => FanRpmMeasured ? Labels.Measured : Labels.Derived;

    // ---------------------------------------------------------------- sparkline history

    /// <summary>How many samples each chart holds: one minute at the telemetry loop's one-second tick.</summary>
    public static int HistoryLength => SparklineHistory.DefaultCapacity;

    /// <summary>The last minute of CPU temperature, oldest first. A fresh array each tick, so the chart repaints.</summary>
    public IReadOnlyList<double> CpuTemperatureHistory => _cpuTemperatureHistory.Snapshot();

    public IReadOnlyList<double> GpuTemperatureHistory => _gpuTemperatureHistory.Snapshot();

    public IReadOnlyList<double> SysTemperatureHistory => _sysTemperatureHistory.Snapshot();

    public IReadOnlyList<double> CpuFanHistory => _cpuFanHistory.Snapshot();

    public IReadOnlyList<double> GpuFanHistory => _gpuFanHistory.Snapshot();

    /// <summary>
    /// Appends one sample per series and tells the charts. A zero temperature is "the sensor did not answer", not
    /// 0 °C, so it is not recorded: plotting it would draw a cliff to the floor that never happened. A zero fan speed
    /// is a real reading — the fans can stop — so it is kept.
    /// </summary>
    private void RecordHistory(MonitorData data)
    {
        if (data.CpuTemperature > 0) _cpuTemperatureHistory.Add(data.CpuTemperature);
        if (data.GpuTemperature > 0) _gpuTemperatureHistory.Add(data.GpuTemperature);
        if (data.SysTemperature > 0) _sysTemperatureHistory.Add(data.SysTemperature);
        _cpuFanHistory.Add(data.CpuFanSpeed);
        _gpuFanHistory.Add(data.GpuFanSpeed);

        // The chart keeps whole samples rather than one series, so the selector can redraw without waiting a minute for
        // a new history. Trimmed to the window on every tick, which is also what keeps the list from growing forever.
        _telemetryHistory.Add(data);
        var windowStart = DateTime.Now - ChartWindow;
        _telemetryHistory.RemoveAll(sample => sample.Timestamp < windowStart);

        RefreshChart();
        RefreshModeMarks();

        Raise(nameof(CpuTemperatureHistory));
        Raise(nameof(GpuTemperatureHistory));
        Raise(nameof(SysTemperatureHistory));
        Raise(nameof(CpuFanHistory));
        Raise(nameof(GpuFanHistory));
        Raise(nameof(SparklineRangeText));
        Raise(nameof(SparklineEmpty));
    }

    // ---------------------------------------------------------------- monitoring page

    /// <summary>True when the operating system answered at least one of the rows the Acer backend cannot.</summary>
    public bool MonitorAvailable => _monitor?.IsAvailable == true;

    /// <summary>The processor's marketing name, or the page's own label when nothing could read it.</summary>
    public string CpuNameText
    {
        get
        {
            var name = _monitor?.Info.CpuName ?? "";
            return name.Length > 0 ? name : Labels.Cpu;
        }
    }

    /// <summary>" (8 çekirdek / 16 iş parçacığı)", or empty when the platform did not say.</summary>
    public string CpuCoreCountText
    {
        get
        {
            var info = _monitor?.Info;
            if (info == null) return "";
            var text = info.FormatCoreText(CpuCores);
            return text.Length > 0 ? $" ({text})" : "";
        }
    }

    /// <summary>The discrete adapter's name, or the page's own label when nothing could read it.</summary>
    public string GpuNameText
    {
        get
        {
            var name = _monitor?.Info.GpuName ?? "";
            return name.Length > 0 ? name : Labels.Gpu;
        }
    }

    /// <summary>"DDR5 5600 MT/s · 2 modül · 32 GB", or empty when the platform did not say.</summary>
    public string MemoryInfoText => _monitor?.Info.MemoryText ?? "";

    public bool HasMemoryInfo => MemoryInfoText.Length > 0;

    /// <summary>Logical processors, from the sample or from the platform's own description.</summary>
    public int CpuCores
    {
        get => _cpuCores;
        private set
        {
            if (!Set(ref _cpuCores, value)) return;
            Raise(nameof(CpuCoreCountText));
        }
    }

    public double CpuUsage
    {
        get => _cpuUsage;
        private set
        {
            if (!Set(ref _cpuUsage, value)) return;
            Raise(nameof(CpuUsageText));
        }
    }

    public string CpuUsageText => TelemetryFormatter.FormatUsagePercent(CpuUsage);

    public double CpuPowerWatts
    {
        get => _cpuPowerWatts;
        private set
        {
            if (!Set(ref _cpuPowerWatts, value)) return;
            Raise(nameof(CpuPowerText));
        }
    }

    /// <summary>
    /// The package power with its unit. Marked as an estimate because it is one: no sensor reports it, the figure is
    /// built from load and clock, and presenting it as a measurement would be a lie about the hardware.
    /// </summary>
    public string CpuPowerText => CpuPowerWatts > 0
        ? $"{CpuPowerWatts:F1} W{TelemetryFormatter.EstimatedSuffix}"
        : "-- W";

    public double CpuFrequencyMhz
    {
        get => _cpuFrequencyMhz;
        private set
        {
            if (!Set(ref _cpuFrequencyMhz, value)) return;
            Raise(nameof(CpuFrequencyText));
        }
    }

    public string CpuFrequencyText => CpuFrequencyMhz > 0 ? $"{CpuFrequencyMhz / 1000.0:F2} GHz" : "-- GHz";

    public double GpuUsage
    {
        get => _gpuUsage;
        private set
        {
            if (!Set(ref _gpuUsage, value)) return;
            Raise(nameof(GpuUsageText));
        }
    }

    public string GpuUsageText => TelemetryFormatter.FormatUsagePercent(GpuUsage);

    public double GpuPowerWatts
    {
        get => _gpuPowerWatts;
        private set
        {
            if (!Set(ref _gpuPowerWatts, value)) return;
            Raise(nameof(GpuPowerText));
        }
    }

    public string GpuPowerText => GpuPowerWatts > 0 ? $"{GpuPowerWatts:F1} W" : "-- W";

    public double GpuFrequencyMhz
    {
        get => _gpuFrequencyMhz;
        private set
        {
            if (!Set(ref _gpuFrequencyMhz, value)) return;
            Raise(nameof(GpuFrequencyText));
        }
    }

    public string GpuFrequencyText => GpuFrequencyMhz > 0 ? $"{GpuFrequencyMhz:F0} MHz" : "-- MHz";

    public double VramUsedMb
    {
        get => _vramUsedMb;
        private set
        {
            if (!Set(ref _vramUsedMb, value)) return;
            Raise(nameof(VramText));
            Raise(nameof(VramBarPercent));
        }
    }

    public double VramTotalMb
    {
        get => _vramTotalMb;
        private set
        {
            if (!Set(ref _vramTotalMb, value)) return;
            Raise(nameof(VramText));
            Raise(nameof(VramBarPercent));
        }
    }

    /// <summary>
    /// "2331 MB / 8188 MB (%28)", or the placeholder when NVML did not answer.
    /// </summary>
    /// <remarks>
    /// Megabytes rather than gigabytes, and the percentage in the same string: a laptop GPU has 4, 6 or 8 GB, so the
    /// gigabyte figure rounds away most of the movement a user is watching for, and the bar alone does not say how
    /// full it is. The WPF page printed gigabytes; the audit found the figure unreadable at this size.
    /// </remarks>
    public string VramText => VramTotalMb > 0
        ? $"{VramUsedMb:F0} MB / {VramTotalMb:F0} MB (%{VramBarPercent:F0})"
        : "-- MB / -- MB";

    public double VramBarPercent => VramTotalMb > 0 ? Math.Clamp(VramUsedMb / VramTotalMb * 100.0, 0, 100) : 0;

    public double RamUsage
    {
        get => _ramUsage;
        private set
        {
            if (!Set(ref _ramUsage, value)) return;
            Raise(nameof(RamBarPercent));
            Raise(nameof(RamDetailText));
        }
    }

    public double RamTotalMb
    {
        get => _ramTotalMb;
        private set
        {
            if (!Set(ref _ramTotalMb, value)) return;
            Raise(nameof(RamDetailText));
        }
    }

    /// <summary>
    /// "12288 MB / 32768 MB (%38)", or the placeholder when the platform did not report the total.
    /// </summary>
    /// <remarks>
    /// The percentage is part of the figure rather than a second label on the right: the two numbers answer one
    /// question, and printing them apart made the card read as two unrelated readings.
    /// </remarks>
    public string RamDetailText => RamTotalMb > 0
        ? $"{RamTotalMb * (RamUsage / 100.0):F0} MB / {RamTotalMb:F0} MB (%{RamUsage:F0})"
        : "-- MB / -- MB";

    public double RamBarPercent => RamTotalMb > 0 ? Math.Clamp(RamUsage, 0, 100) : 0;

    /// <summary>
    /// Temperature is the one metric where colour carries meaning rather than decoration: the bar and its reading turn
    /// red once the part runs hot, so the panel answers "is anything wrong" at a glance. The threshold is the first
    /// thermal safety floor, not a number invented for the page.
    /// </summary>
    public bool CpuTempHot => CpuTemperature >= FanCurve.SafetyTemperatureC;

    public bool GpuTempHot => GpuTemperature >= FanCurve.SafetyTemperatureC;

    // ---------------------------------------------------------------- the telemetry chart

    /// <summary>The nine series the chart can draw.</summary>
    public ObservableCollection<ChartMetricChoice> ChartMetrics { get; } = new();

    /// <summary>
    /// The series the chart is drawing. Assigning it persists the choice and redraws from the samples already held, so
    /// switching series never waits for the next tick.
    /// </summary>
    public ChartMetricChoice? SelectedChartMetric
    {
        get => _selectedChartMetric;
        set
        {
            if (!Set(ref _selectedChartMetric, value)) return;
            _settings.SelectedChartMetric = value?.Id ?? TelemetryMetrics.CpuTemp;
            SaveSettings();
            RefreshChart();
        }
    }

    /// <summary>The measured samples of the selected series inside the window, oldest first.</summary>
    public IReadOnlyList<double> ChartValues => _chartValues;

    public double ChartMinimum => _chartRange.Low;

    public double ChartMaximum => _chartRange.High;

    public string ChartCurrentText => _chartStats.Current;
    public string ChartMinText => _chartStats.Min;
    public string ChartMaxText => _chartStats.Max;
    public string ChartAverageText => _chartStats.Average;
    public string ChartGridTopText => _chartStats.GridTop;
    public string ChartGridMidText => _chartStats.GridMid;
    public string ChartGridBottomText => _chartStats.GridBottom;

    /// <summary>True while the chart has fewer than two measured samples, so the card can say why it is empty.</summary>
    public bool ChartEmpty => _chartValues.Count < 2;

    // ---------------------------------------------------------------- the mode changes

    /// <summary>
    /// The operating-mode changes recorded inside the window, as fractions of the chart's width. The chart draws a
    /// vertical rule at each one, so a mode switch is visible against the series it happened at.
    /// </summary>
    public IReadOnlyList<double> ModeChangeMarks => _modeChangeMarks;

    /// <summary>
    /// Records a mode change for the chart. Called from the mode setter and from the game-profile switch, so a change
    /// made by a game is marked exactly like one made by a click.
    /// </summary>
    private void RecordModeMark(OperatingMode mode)
    {
        if (_modeMarks.Count > 0 && _modeMarks[^1].Mode == mode) return;
        _modeMarks.Add((DateTime.Now, mode));
        if (_modeMarks.Count > 64) _modeMarks.RemoveAt(0);

        // Redrawn here rather than only on the next telemetry tick: a click that moves the mode should move the rule
        // under the cursor, not a second later.
        RefreshModeMarks();
    }

    /// <summary>Recomputes the chart's mode-change rules from the changes already held.</summary>
    private void RefreshModeMarks()
    {
        var windowStart = DateTime.Now - ChartWindow;

        var marks = new List<double>();
        foreach (var (at, _) in _modeMarks)
        {
            if (at < windowStart) continue;
            marks.Add(Math.Clamp((at - windowStart).TotalSeconds / ChartWindow.TotalSeconds, 0, 1));
        }

        _modeChangeMarks = marks;
        Raise(nameof(ModeChangeMarks));
    }

    /// <summary>
    /// False while the window is hidden or minimised, which is when the chart cannot be seen.
    /// </summary>
    /// <remarks>
    /// The chart is the most expensive thing a tick does: it walks the whole history, formats nine strings and raises
    /// nine properties. None of that can be read from a window that is not on screen, so the work is skipped and the
    /// history keeps accumulating. Setting this back to true rebuilds the chart immediately, so the window never comes
    /// back to a stale one.
    /// </remarks>
    public bool ChartVisible
    {
        get => _chartVisible;
        set
        {
            if (_chartVisible == value) return;
            _chartVisible = value;
            if (value) RefreshChart();
        }
    }

    /// <summary>
    /// Recomputes the chart's samples, its range and its four statistics from the history already held. Called on every
    /// tick and whenever the series changes, so the two can never disagree about what is on screen.
    /// </summary>
    private void RefreshChart()
    {
        // Nothing on screen can show the result, and the next visible tick rebuilds it from the history that kept
        // growing while the window was away.
        if (!_chartVisible) return;

        var metric = SelectedChartMetric?.Id ?? TelemetryMetrics.CpuTemp;
        var windowStart = DateTime.Now - ChartWindow;

        var values = new List<double>();
        foreach (var sample in _telemetryHistory)
        {
            if (sample.Timestamp < windowStart) continue;
            var value = TelemetryMetrics.Value(metric, sample);
            if (value.HasValue) values.Add(value.Value);
        }

        _chartValues = values;
        _chartRange = TelemetryMetrics.Range(metric, values);

        if (values.Count < 2)
        {
            string unit = TelemetryMetrics.Unit(metric);
            string dash = unit.Length > 0 ? $"-- {unit}" : "--";
            _chartStats = new ChartStats(dash, dash, dash, dash, dash, dash, dash);
        }
        else
        {
            double current = values[^1];
            double min = values[0];
            double max = values[0];
            double sum = 0;
            foreach (var value in values)
            {
                if (value < min) min = value;
                if (value > max) max = value;
                sum += value;
            }

            _chartStats = new ChartStats(
                TelemetryMetrics.Format(metric, current),
                TelemetryMetrics.Format(metric, min),
                TelemetryMetrics.Format(metric, max),
                TelemetryMetrics.Format(metric, sum / values.Count),
                TelemetryMetrics.FormatGrid(metric, _chartRange.High),
                TelemetryMetrics.FormatGrid(metric, (_chartRange.Low + _chartRange.High) / 2.0),
                TelemetryMetrics.FormatGrid(metric, _chartRange.Low));
        }

        Raise(nameof(ChartValues));
        Raise(nameof(ChartMinimum));
        Raise(nameof(ChartMaximum));
        Raise(nameof(ChartCurrentText));
        Raise(nameof(ChartMinText));
        Raise(nameof(ChartMaxText));
        Raise(nameof(ChartAverageText));
        Raise(nameof(ChartGridTopText));
        Raise(nameof(ChartGridMidText));
        Raise(nameof(ChartGridBottomText));
        Raise(nameof(ChartEmpty));
    }

    private void BuildChartMetrics()
    {
        ChartMetrics.Clear();
        foreach (var metric in TelemetryMetrics.All)
        {
            ChartMetrics.Add(new ChartMetricChoice(metric, TelemetryMetrics.Label(metric)));
        }

        var stored = _settings.SelectedChartMetric;
        SelectedChartMetric = ChartMetrics.FirstOrDefault(m => m.Id == stored) ?? ChartMetrics[0];
    }

    // ---------------------------------------------------------------- the standby-list cleaner

    /// <summary>True when this platform has a standby list to release. False hides the whole card.</summary>
    public bool MemoryCleanAvailable => _memoryCleaner?.IsSupported == true;

    /// <summary>
    /// True when the clean needs a separate program. Windows does — WinMemoryCleaner — so the card offers its download
    /// page; Linux drops the page cache through the kernel, so the row is hidden rather than pointing at a Windows tool.
    /// </summary>
    public bool MemoryCleanToolDownloadVisible => _memoryCleaner?.NeedsExternalTool == true;

    public bool MemoryCleanRunning
    {
        get => _memoryCleanRunning;
        private set
        {
            if (!Set(ref _memoryCleanRunning, value)) return;
            Raise(nameof(MemoryCleanButtonText));
        }
    }

    public string MemoryCleanButtonText => MemoryCleanRunning ? Labels.Cleaning : Labels.CleanStandby;

    /// <summary>The last result, in the card's own words. Empty until the button has been pressed once.</summary>
    public string MemoryCleanStatus
    {
        get => _memoryCleanStatus;
        private set
        {
            if (!Set(ref _memoryCleanStatus, value)) return;
            Raise(nameof(HasMemoryCleanStatus));
        }
    }

    public bool HasMemoryCleanStatus => MemoryCleanStatus.Length > 0;

    /// <summary>True when the last clean succeeded, so the status line can be painted green rather than red.</summary>
    public bool MemoryCleanSucceeded
    {
        get => _memoryCleanSucceeded;
        private set => Set(ref _memoryCleanSucceeded, value);
    }

    public RelayCommand CleanMemoryCommand { get; private set; } = null!;

    /// <summary>
    /// Opens the helper's download page. Offered beside the clean button rather than only in the failure message: a
    /// user who has just been told the tool is missing should not have to press the button again to find out where to
    /// get it.
    /// </summary>
    public RelayCommand GetMemoryCleanerCommand { get; private set; } = null!;

    /// <summary>
    /// Releases the standby list and reports what happened. The result is described in the card rather than only in a
    /// toast: a toast is gone after three seconds, and "how much did that free" is the whole point of the button.
    /// </summary>
    public async Task CleanMemoryAsync()
    {
        if (_memoryCleaner == null || !_memoryCleaner.IsSupported || MemoryCleanRunning) return;

        MemoryCleanRunning = true;
        MemoryCleanStatus = Labels.Cleaning;
        MemoryCleanSucceeded = false;
        try
        {
            var result = await _memoryCleaner.CleanStandbyMemoryAsync().ConfigureAwait(true);

            if (result.ToolMissing)
            {
                MemoryCleanStatus = Labels.MemoryCleanToolMissing;
                return;
            }

            if (result.ElevationRefused)
            {
                MemoryCleanStatus = Labels.MemoryCleanElevationRefused;
                return;
            }

            if (!result.Cleaned)
            {
                MemoryCleanStatus = Labels.MemoryCleanFailed;
                return;
            }

            MemoryCleanSucceeded = true;
            MemoryCleanStatus = result.FreedMegabytes <= 0
                ? Labels.MemoryCleanAlreadyClean
                : Labels.MemoryCleanFreed(FormatMegabytes(result.FreedMegabytes),
                    result.AvailableBeforeGigabytes, result.AvailableGigabytes);
        }
        catch (Exception ex)
        {
            AppLog.Warn(Loc.T("Bekleme belleği boşaltılamadı"), ex.ToString());
            MemoryCleanStatus = Labels.MemoryCleanFailed;
        }
        finally
        {
            MemoryCleanRunning = false;
        }
    }

    /// <summary>Megabytes below a gigabyte, gigabytes above it, because "2048 MB" is a number nobody converts in place.</summary>
    private static string FormatMegabytes(double megabytes) => megabytes >= 1024
        ? $"{megabytes / 1024.0:F1} GB"
        : $"{megabytes:F0} MB";

    // ---------------------------------------------------------------- operating mode

    public ObservableCollection<ModeChoice> Modes { get; } = new();

    /// <summary>
    /// The mode in effect. Assigning it applies the mode: the cards are the control, so a click is the whole gesture.
    /// </summary>
    public ModeChoice? SelectedMode
    {
        get => _selectedMode;
        set
        {
            if (!Set(ref _selectedMode, value)) return;
            SyncModeSelection();
            Raise(nameof(ModeBadgeText));
            if (value != null) RecordModeMark(value.Mode);
            if (!_suppressWrite) _ = ApplyModeAsync(value?.Mode);
        }
    }

    /// <summary>Paints the accent on exactly one card. Called whenever the selection moves, including from a game.</summary>
    private void SyncModeSelection()
    {
        foreach (var card in Modes) card.IsSelected = card.Mode == _selectedMode?.Mode;
    }

    public bool ModeControlAvailable => _capabilities.OperatingModeControl;

    /// <summary>
    /// True when the page has to explain why the mode tiles are dead. The note is the difference between "this page
    /// is broken" and "this machine has no backend": the rest of the page keeps working either way.
    /// </summary>
    public bool HasModeLockNote => !ModeControlAvailable;

    // ---------------------------------------------------------------- fan

    public ObservableCollection<FanModeChoice> FanModes { get; } = new();

    public FanModeChoice? SelectedFanMode
    {
        get => _selectedFanMode;
        set
        {
            if (!Set(ref _selectedFanMode, value)) return;
            SyncFanSelection();
            SyncFanSegment();
            Raise(nameof(CustomFanVisible));

            // A new mode means a new set of speeds to write, so both trackers forget what they had applied. Without
            // this the loop would compare against a speed the previous mode left behind and skip the first write.
            _curveEvaluator.Reset();
            _manualFanFloor.Reset();
            SyncFanLoop();

            if (!_suppressWrite) _ = ApplyFanModeAsync();
        }
    }

    /// <summary>
    /// Starts or stops the fan loop to match the current mode. The loop is the only thing that writes a fixed fan
    /// speed, so it runs exactly when there is a fixed speed to write and sleeps the rest of the time.
    /// </summary>
    private void SyncFanLoop()
    {
        if (_disposed) return;

        // Nothing is written before the probe has answered. The loop is started again from InitializeAsync once it
        // has, so a machine that boots in custom mode still gets its curve without the user touching a card.
        if (!_hardwareReady) return;

        if (_hardware != null && SelectedFanMode?.Mode == FanMode.Custom) _curveLoop.Start();
        else _curveLoop.Stop();
    }

    private void SyncFanSelection()
    {
        foreach (var card in FanModes) card.IsSelected = card.Mode == _selectedFanMode?.Mode;
    }

    /// <summary>The custom-speed sliders only mean something in <see cref="FanMode.Custom"/>.</summary>
    public bool CustomFanVisible => SelectedFanMode?.Mode == FanMode.Custom;

    /// <summary>
    /// True while the fan curve loop is running. Exposed so the guarantee that it does not start before the capability
    /// probe has answered can be asserted without a window.
    /// </summary>
    public bool FanCurveRunning => _curveLoop.IsRunning;

    public int CustomCpuPercent
    {
        get => _customCpuPercent;
        set
        {
            if (Set(ref _customCpuPercent, Math.Clamp(value, FanCurve.MinPercent, FanCurve.MaxPercent)))
            {
                Raise(nameof(CustomCpuPercentText));
                if (!_suppressWrite) _customFanDebounce.Run(ApplyCustomFanAsync);
            }
        }
    }

    public int CustomGpuPercent
    {
        get => _customGpuPercent;
        set
        {
            if (Set(ref _customGpuPercent, Math.Clamp(value, FanCurve.MinPercent, FanCurve.MaxPercent)))
            {
                Raise(nameof(CustomGpuPercentText));
                if (!_suppressWrite) _customFanDebounce.Run(ApplyCustomFanAsync);
            }
        }
    }

    /// <summary>
    /// Stores the two custom speeds and, when the fans are already in custom mode, sends them. The WPF window did the
    /// same from SliderCustomFan_ValueChanged: the numbers are the user's choice whether or not the mode is active, so
    /// they are always kept, and only the write waits for the mode.
    /// </summary>
    /// <remarks>
    /// What is written is not always what the slider says. <see cref="ManualFanFloor"/> raises the speed to the
    /// thermal floor for the current temperature, so a slider left at 30 % on a 95 °C machine writes 100 %. The
    /// stored number is untouched: the floor is a temporary override, and the fan returns to the user's own value as
    /// soon as the die is cool again.
    /// </remarks>
    private async Task ApplyCustomFanAsync()
    {
        _settings.CustomFanCpuPercent = CustomCpuPercent;
        _settings.CustomFanGpuPercent = CustomGpuPercent;
        SaveSettings();

        // A game's own fan rule owns the fans while it runs; writing here would fight it.
        if (_hardware == null || GameActive) return;
        if (SelectedFanMode?.Mode != FanMode.Custom) return;

        int temperature = CpuTemperature;
        int effective = _manualFanFloor.EffectivePercent(CustomCpuPercent, temperature);

        bool ok = await RunWriteAsync(
            () => _hardware.SetFanModeAsync(FanMode.Custom, effective, effective),
            Labels.FanMode).ConfigureAwait(true);

        // The loop must not immediately rewrite what this call just wrote, and a refused write must not be recorded
        // as applied, so the tracker is updated only on success.
        if (ok) _manualFanFloor.Commit(effective, temperature);
    }

    /// <summary>
    /// The speeds to write for the current custom selection: the user's numbers, raised to the thermal floor. Every
    /// path that writes a fixed speed goes through here, so no caller can bypass the floor.
    /// </summary>
    private (int Cpu, int Gpu) EffectiveCustomPercents()
    {
        int temperature = CpuTemperature;
        return (_manualFanFloor.EffectivePercent(CustomCpuPercent, temperature),
                _manualFanFloor.EffectivePercent(CustomGpuPercent, temperature));
    }

    public string CustomCpuPercentText => $"%{CustomCpuPercent}";
    public string CustomGpuPercentText => $"%{CustomGpuPercent}";

    public bool FanControlAvailable => _capabilities.FanControl;

    // ---------------------------------------------------------------- fan curve

    public ObservableCollection<CurveAnchorViewModel> CurveAnchors { get; } = new();

    /// <summary>
    /// True when the curve drives the fans. The loop runs on the telemetry tick, so it stops when the window is
    /// closed and never competes with a mode change for the write gate.
    /// </summary>
    public bool CurveEnabled
    {
        get => _curveEnabled;
        set
        {
            if (!Set(ref _curveEnabled, value)) return;
            _settings.FanCurveEnabled = value;
            SaveSettings();

            // Re-arming has to forget what was applied, or the first evaluation after a re-arm would decide the fans
            // are already at the right speed and never write. The manual floor forgets too: disarming the curve hands
            // the fans back to the sliders, and the first manual write must not be skipped as "already applied".
            _curveEvaluator.Reset();
            _manualFanFloor.Reset();
            SyncFanSegment();
            SyncFanLoop();
            Status = value ? Labels.CurveEnabled : Labels.CurveDisabled;
            if (!value) _ = ApplyFanModeAsync();
        }
    }

    /// <summary>"Sıcaklık 70 °C → fan %55" for the anchor nearest the current CPU temperature.</summary>
    public string CurvePreview
    {
        get
        {
            var curve = CurrentCurve();
            int temperature = CpuTemperature > 0 ? CpuTemperature : FanCurve.AnchorTemperatures[0];
            return Labels.CurvePoint(temperature, curve.PercentAt(temperature));
        }
    }

    /// <summary>The anchors as the drawing control wants them: the same six numbers the sliders hold.</summary>
    public IReadOnlyList<FanCurvePoint> CurvePoints => CurrentCurve().Points;

    /// <summary>Where to draw the "you are here" marker, or 0 before the first telemetry tick.</summary>
    public double CurrentTemperature => CpuTemperature;

    /// <summary>The safety bands, as one line. Read from the constants so the text cannot drift from the code.</summary>
    public string ThermalFloorSummary => Labels.ThermalFloorSummary;

    // ---------------------------------------------------------------- lighting

    public ObservableCollection<EffectChoice> Effects { get; } = new();

    public EffectChoice? SelectedEffect
    {
        get => _selectedEffect;
        set
        {
            if (!Set(ref _selectedEffect, value)) return;
            BuildDirectionChoices();
            Raise(nameof(IsLightingOff));
            Raise(nameof(ZoneRainbow));
            Raise(nameof(ZoneSolid));
            Raise(nameof(ZonePreviewNote));
            Raise(nameof(HasZonePreviewNote));

            // The page has no Apply button, so picking an effect is the write. Without this the list changed the
            // view model and nothing else, and the keyboard stayed in whatever effect it was already in.
            if (_suppressWrite) return;

            // OFF is the user asking for dark, not for a different look, so it leaves the profile where it was.
            if (value != null && !string.Equals(value.Id, "OFF", StringComparison.OrdinalIgnoreCase))
            {
                MarkProfileCustom();
            }

            // Picking an effect while the lights are off is the user asking for light: the last lit configuration
            // comes back with this effect. OFF is the exception, because that is the user asking for dark.
            if (value != null && !string.Equals(value.Id, "OFF", StringComparison.OrdinalIgnoreCase))
            {
                ResumeFromOffForEdit(cfg => cfg.Effect = value.Id);
            }

            _lightingDebounce.Run(ApplyLightingAsync);
        }
    }

    public int BrightnessLevel
    {
        get => _brightnessLevel;
        set
        {
            if (!Set(ref _brightnessLevel, Math.Clamp(value, 0, LightingLimits.MaxBrightnessLevel))) return;
            Raise(nameof(BrightnessLabel));
            Raise(nameof(IsLightingOff));
            Raise(nameof(ZoneRainbow));
            Raise(nameof(ZoneSolid));

            // The page has no Apply button, so the slider is the write. The WPF window did the same from
            // SliderBrightness_ValueChanged, through the same kind of pause.
            if (_suppressWrite) return;

            MarkProfileCustom();

            // Raising the brightness while the lights are off brings the last lit configuration back at this level.
            // Writing "OFF" with a non-zero brightness is what the firmware reads as "stay off".
            if (_brightnessLevel > 0) ResumeFromOffForEdit(cfg => cfg.Brightness = _brightnessLevel);

            _lightingDebounce.Run(ApplyLightingAsync);
        }
    }

    public int SpeedLevel
    {
        get => _speedLevel;
        set
        {
            // The floor is 1, not 0: the protocol has no "speed 0", and a stored 0 from an older settings file would
            // otherwise be written to the keyboard as a level the firmware rejects.
            if (!Set(ref _speedLevel, Math.Clamp(value, LightingLimits.MinSpeed, LightingLimits.MaxSpeed))) return;
            Raise(nameof(SpeedLabel));

            if (_suppressWrite) return;
            MarkProfileCustom();
            _lightingDebounce.Run(ApplyLightingAsync);
        }
    }

    public int DirectionLevel
    {
        get => _directionLevel;
        set
        {
            if (!Set(ref _directionLevel, Math.Clamp(value, 1, LightingLimits.MaxDirection))) return;
            if (_suppressWrite) return;
            MarkProfileCustom();
            _lightingDebounce.Run(ApplyLightingAsync);
        }
    }

    public string PrimaryColor
    {
        get => _primaryColor;
        set
        {
            if (!Set(ref _primaryColor, value)) return;
            Raise(nameof(ActiveColor));
            if (_suppressWrite) return;

            MarkProfileCustom();

            // A colour picked while the lights are off brings the last lit configuration back with it.
            ResumeFromOffForEdit(cfg =>
            {
                cfg.Color = value;
                cfg.LEDs = new List<string> { value, value, value, value };
            });

            _lightingDebounce.Run(ApplyLightingAsync);
        }
    }

    public string Zone1
    {
        get => _zone1;
        set
        {
            if (!Set(ref _zone1, value)) return;
            Raise(nameof(ActiveColor));
            if (_suppressWrite) return;

            MarkProfileCustom();

            // Zone colours only exist in STATIC, so a zone picked while the lights are off comes back as one.
            ResumeFromOffForEdit(cfg =>
            {
                cfg.Effect = "STATIC";
                cfg.LEDs[0] = value;
            });
            _lightingDebounce.Run(ApplyLightingAsync);
        }
    }

    public string Zone2
    {
        get => _zone2;
        set
        {
            if (!Set(ref _zone2, value)) return;
            Raise(nameof(ActiveColor));
            if (_suppressWrite) return;

            MarkProfileCustom();

            // Zone colours only exist in STATIC, so a zone picked while the lights are off comes back as one.
            ResumeFromOffForEdit(cfg =>
            {
                cfg.Effect = "STATIC";
                cfg.LEDs[1] = value;
            });
            _lightingDebounce.Run(ApplyLightingAsync);
        }
    }

    public string Zone3
    {
        get => _zone3;
        set
        {
            if (!Set(ref _zone3, value)) return;
            Raise(nameof(ActiveColor));
            if (_suppressWrite) return;

            MarkProfileCustom();

            // Zone colours only exist in STATIC, so a zone picked while the lights are off comes back as one.
            ResumeFromOffForEdit(cfg =>
            {
                cfg.Effect = "STATIC";
                cfg.LEDs[2] = value;
            });
            _lightingDebounce.Run(ApplyLightingAsync);
        }
    }

    public string Zone4
    {
        get => _zone4;
        set
        {
            if (!Set(ref _zone4, value)) return;
            Raise(nameof(ActiveColor));
            if (_suppressWrite) return;

            MarkProfileCustom();

            // Zone colours only exist in STATIC, so a zone picked while the lights are off comes back as one.
            ResumeFromOffForEdit(cfg =>
            {
                cfg.Effect = "STATIC";
                cfg.LEDs[3] = value;
            });
            _lightingDebounce.Run(ApplyLightingAsync);
        }
    }

    public bool LightingAvailable => _capabilities.KeyboardLighting;

    // The slider ranges come from the protocol constants rather than from numbers typed into the XAML, so a change to
    // the sysfs contract cannot leave the UI offering a level the kernel will reject.
    public int MaxBrightness => LightingLimits.MaxBrightnessLevel;
    public int MaxSpeed => LightingLimits.MaxSpeed;
    public int MaxDirection => LightingLimits.MaxDirection;

    /// <summary>
    /// True when the page is describing a keyboard that is dark. Read from the same policy the write uses, so the deck
    /// and the firmware cannot disagree about what "off" means.
    /// </summary>
    public bool IsLightingOff => LightingPolicy.IsOff(CurrentLightingConfig());

    /// <summary>
    /// True when the selected effect paints the keyboard's own colours, so the four zone bars show a rainbow instead of
    /// a colour the effect will ignore. Read from the policy rather than from a list here, so the two cannot drift.
    /// </summary>
    /// <remarks>
    /// OFF is excluded even though it takes no colour: the rainbow is a statement that the effect will paint over the
    /// zones, and a keyboard that is off paints nothing. Leaving it in is what kept the deck showing four coloured bars
    /// over a keyboard the page had just turned black.
    /// </remarks>
    public bool ZoneRainbow =>
        !IsLightingOff && SelectedEffect != null && !LightingPolicy.EffectUsesColor(SelectedEffect.Id);

    /// <summary>The inverse of <see cref="ZoneRainbow"/>, so the deck can show one bar or the other without a converter.</summary>
    public bool ZoneSolid => !ZoneRainbow;

    /// <summary>The line under the deck that explains the rainbow, or empty when the zones show real colours.</summary>
    public string ZonePreviewNote => ZoneRainbow ? Labels.ZonePreviewNote : "";

    public bool HasZonePreviewNote => ZonePreviewNote.Length > 0;

    /// <summary>
    /// Brightness as "5 / 9", or "0 / 9 (Kapalı)" at zero. The word "Parlaklık" is already printed to the left of the
    /// slider, so repeating it here would print it twice on one line; what the right-hand side adds is the number.
    /// </summary>
    public string BrightnessLabel => Labels.BrightnessValue(BrightnessLevel, MaxBrightness);

    public string SpeedLabel => Labels.SpeedValue(SpeedLevel, MaxSpeed);

    /// <summary>The directions the selected effect accepts. Empty for the seven effects that take none.</summary>
    public ObservableCollection<DirectionChoice> DirectionChoices { get; } = new();

    public DirectionChoice? SelectedDirection
    {
        get => _selectedDirection;
        set
        {
            if (!Set(ref _selectedDirection, value)) return;
            if (value != null) DirectionLevel = value.Direction;
        }
    }

    /// <summary>True when the effect has a direction to choose, which is what shows the row.</summary>
    public bool ShowDirectionPanel => DirectionChoices.Count > 0;

    /// <summary>
    /// True once a click on the deck has pointed the palette at a zone. Avalonia has no modal colour dialog, so the
    /// click selects the zone and the palette beside it becomes the picker; the flag is what the view uses to bring
    /// that card into view.
    /// </summary>
    public bool ZonePickerOpen { get; private set; }

    /// <summary>
    /// Selects a zone from a click on the deck. Zone 0 is the primary colour, 1..4 the four bars, which is the same
    /// numbering the palette's own buttons use.
    /// </summary>
    public void SelectZoneForPicker(int zone)
    {
        ActiveZone = Math.Clamp(zone, 0, LightingLimits.ZoneCount);
        ZonePickerOpen = true;
        Raise(nameof(ZonePickerOpen));
    }

    public RelayCommand ApplyAllZonesCommand { get; private set; } = null!;

    /// <summary>
    /// The colour the active target holds. The palette, the hex box and the picker all read and write this one, so a
    /// click on the deck moves all three to the same zone instead of leaving the hex box on the primary colour.
    /// </summary>
    public string ActiveColor
    {
        get => ActiveZone switch
        {
            1 => Zone1,
            2 => Zone2,
            3 => Zone3,
            4 => Zone4,
            _ => PrimaryColor
        };
        set
        {
            switch (ActiveZone)
            {
                case 1: Zone1 = value; break;
                case 2: Zone2 = value; break;
                case 3: Zone3 = value; break;
                case 4: Zone4 = value; break;
                default: PrimaryColor = value; break;
            }
        }
    }

    /// <summary>
    /// Spreads the active target's colour over all four zones. The per-zone picker cannot do this in one click, and
    /// one colour for the whole keyboard is the common case.
    /// </summary>
    /// <remarks>
    /// The primary colour goes with them. The firmware's STATIC mode takes one colour and the payload carries both
    /// fields, so leaving the primary on its old value is what made "apply to all zones" land as a colour the page was
    /// not showing: the deck said one thing and the keyboard did another.
    /// </remarks>
    private void ApplyAllZones()
    {
        var color = LightingPolicy.NormalizeHex(ActiveColor, LightingPolicy.DefaultColor);
        PrimaryColor = color;
        Zone1 = color;
        Zone2 = color;
        Zone3 = color;
        Zone4 = color;
    }

    /// <summary>
    /// Writes a colour chosen in the picker window into whichever target the deck has focused. The window itself is
    /// the view's business — this is the one line of it the view model needs, so the hex validation stays here.
    /// </summary>
    public void ApplyPickedColor(string hex)
    {
        ActiveColor = LightingPolicy.NormalizeHex(hex, LightingPolicy.DefaultColor);
        SyncSwatchSelection();
    }

    /// <summary>
    /// Rebuilds the direction list for the selected effect. WAVE takes four directions, SHIFTING two, and the rest
    /// none, so the row disappears rather than offering a choice the firmware would ignore.
    /// </summary>
    private void BuildDirectionChoices()
    {
        var effect = SelectedEffect?.Id ?? "STATIC";
        var allowed = LightingPolicy.AllowedDirections(effect);

        DirectionChoices.Clear();
        foreach (var direction in allowed)
        {
            DirectionChoices.Add(new DirectionChoice(direction, LightingPolicy.DirectionName(direction)));
        }

        SelectedDirection = DirectionChoices.FirstOrDefault(d => d.Direction == DirectionLevel)
                            ?? DirectionChoices.FirstOrDefault();

        Raise(nameof(ShowDirectionPanel));
    }

    // ---------------------------------------------------------------- the on-screen display

    /// <summary>
    /// Whether the overlay is drawn. Persisted, because an OSD someone turned on for a game session is a preference,
    /// not a per-session one.
    /// </summary>
    public bool OsdEnabled
    {
        get => _osdEnabled;
        set
        {
            if (!Set(ref _osdEnabled, value)) return;
            _settings.OsdEnabled = value;
            SaveSettings();
            Raise(nameof(OsdToggleText));
            OsdChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The toggle's own caption: GİZLE while the overlay is drawn, GÖSTER while it is not.</summary>
    public string OsdToggleText => OsdEnabled ? Labels.HideCaption : Labels.ShowCaption;

    /// <summary>Which corner the overlay sits in: 0 top-left, 1 top-right, 2 bottom-left, 3 bottom-right.</summary>
    public int OsdCorner
    {
        get => _osdCorner;
        set
        {
            if (!Set(ref _osdCorner, Math.Clamp(value, 0, 3))) return;
            _settings.OsdCorner = _osdCorner;
            SaveSettings();
            Raise(nameof(OsdCornerName));
            Raise(nameof(OsdPositionText));
            OsdChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Which display the overlay is on, as an index into the screens the window can see.</summary>
    public int OsdMonitor
    {
        get => _osdMonitor;
        set
        {
            if (!Set(ref _osdMonitor, Math.Max(0, value))) return;
            _settings.OsdMonitor = _osdMonitor;
            SaveSettings();
            Raise(nameof(OsdPositionText));
            OsdChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// How many displays the overlay may move between. Set by the window, which is the only thing that can ask the
    /// platform; a view model that guessed would cycle onto a screen that is not there.
    /// </summary>
    public int OsdMonitorCount { get; set; } = 1;

    public string OsdCornerName => OsdCorner switch
    {
        1 => Labels.OsdCornerTopRight,
        2 => Labels.OsdCornerBottomLeft,
        3 => Labels.OsdCornerBottomRight,
        _ => Labels.OsdCornerTopLeft
    };

    public string OsdPositionText => Labels.OsdPosition(OsdCornerName, OsdMonitor + 1);

    public RelayCommand CycleOsdCornerCommand { get; private set; } = null!;

    /// <summary>Shows or hides the overlay. The button carries the caption, so one command serves both states.</summary>
    public RelayCommand ToggleOsdCommand { get; private set; } = null!;

    /// <summary>
    /// The overlay's three rows, composed here rather than in the window so the separators and the units are decided
    /// once. The overlay is a second window with no view model of its own: it binds to this one, which is what keeps
    /// the OSD and the page showing the same numbers on the same tick.
    /// </summary>
    public string OsdCpuLine =>
        $"{Labels.Cpu}: {CpuTemperatureText} | {CpuPowerText} | {CpuFrequencyText} | {CpuUsageText}";

    public string OsdGpuLine =>
        $"{Labels.Gpu}: {GpuTemperatureText} | {GpuPowerText} | {GpuFrequencyText} | {VramText}";

    public string OsdRamLine =>
        $"{Labels.SystemMemory}: {RamDetailText} | {Labels.FanRpm}: {CpuFanRpmText}";

    /// <summary>Raised when the overlay's visibility or position changed, so the window can move or hide it.</summary>
    public event EventHandler? OsdChanged;

    /// <summary>
    /// Steps the overlay through the four corners, then onto the next display. One button rather than two pickers: the
    /// overlay is a small box, and the corner it is in is easier to see than to describe.
    /// </summary>
    private void CycleOsdCorner()
    {
        if (OsdCorner < 3)
        {
            OsdCorner++;
            return;
        }

        OsdCorner = 0;
        OsdMonitor = (OsdMonitor + 1) % Math.Max(1, OsdMonitorCount);
    }

    // ---------------------------------------------------------------- extras

    public bool CoolBoostAvailable => _capabilities.CoolBoost;

    public bool CoolBoostOn
    {
        get => _coolBoostOn;
        set
        {
            if (!Set(ref _coolBoostOn, value)) return;
            if (!_suppressWrite) _ = RunWriteAsync(() => _hardware!.SetCoolBoostAsync(value), Labels.CoolBoost);
        }
    }

    public bool LcdOverdriveAvailable => _capabilities.LcdOverdrive;

    public bool LcdOverdriveOn
    {
        get => _lcdOverdriveOn;
        set
        {
            if (!Set(ref _lcdOverdriveOn, value)) return;
            if (!_suppressWrite) _ = RunWriteAsync(() => _hardware!.SetLcdOverdriveAsync(value), Labels.LcdOverdrive);
        }
    }

    public bool BacklightTimeoutAvailable => _capabilities.BacklightTimeout;

    public bool BacklightTimeoutOn
    {
        get => _backlightTimeoutOn;
        set
        {
            if (!Set(ref _backlightTimeoutOn, value)) return;
            if (_suppressWrite) return;
            _ = WriteBacklightTimeoutAsync(value, quiet: false);
        }
    }

    /// <summary>
    /// Writes the 30-second timeout and then reads it back.
    /// </summary>
    /// <remarks>
    /// The firmware answers "accepted" for a write it may not have applied, and a switch that keeps a state the
    /// keyboard never took is worse than one that snaps back: the page would promise the light stays on while the
    /// keyboard goes dark 30 seconds later. The read-back is what turns that promise into a fact, and a refusal is
    /// reported in the status line instead of being swallowed.
    /// <para>
    /// The opposite failure exists too, and it is the one the reference AN16-41 shows: the service applies the write
    /// to the EC and never sends a reply, so the write reports a timeout over a keyboard that did change. The
    /// read-back is what tells the two apart, which is why it runs whether the write said yes or no.
    /// </para>
    /// <para>
    /// <paramref name="revertOnFailure"/> is false for the write the page makes on its own behalf when the hardware
    /// cannot be asked. Nobody touched the switch in that case, so snapping it to the opposite state would be the page
    /// arguing with itself; the switch is left showing the choice it just tried to make true.
    /// </para>
    /// </remarks>
    private async Task<bool> WriteBacklightTimeoutAsync(bool enable, bool quiet, bool revertOnFailure = true)
    {
        if (_hardware == null) return false;

        bool ok = quiet
            ? await RunQuietWriteAsync(() => _hardware.SetBacklightTimeoutAsync(enable), Labels.BacklightTimeout)
                .ConfigureAwait(true)
            : await RunWriteAsync(() => _hardware.SetBacklightTimeoutAsync(enable), Labels.BacklightTimeout)
                .ConfigureAwait(true);

        // The read-back decides, not the write's own answer. On the reference AN16-41 the service applies
        // BACKLIGHT_30S to the EC and never sends a reply, so the write times out over a keyboard that did change.
        // Reverting the switch over that timeout is what made the user toggle again — and every toggle is another
        // EC write, which is the one-second stall they feel. A backend that cannot answer the read at all falls back
        // to the write's own answer, which is the behaviour every other switch on the page has.
        var state = await _hardware.GetBacklightTimeoutAsync().ConfigureAwait(true);
        bool applied = state is bool actual ? actual == enable : ok;

        if (!applied)
        {
            if (revertOnFailure) RevertBacklightTimeout(enable);
            if (!quiet) Status = $"{Labels.WriteFailed}: {Labels.BacklightTimeout}";
            return false;
        }

        // The write reported a failure the read-back contradicted, so the banner RunWriteAsync raised is wrong.
        if (!ok && !quiet) Status = $"{Labels.Applied}: {Labels.BacklightTimeout}";

        // The switch follows what was written, not what was asked for. The two differ on the re-assert path, where
        // the stored choice is put back on the keyboard without the user having touched anything.
        SyncControls(() => BacklightTimeoutOn = enable);

        _settings.BacklightTimeoutEnabled = enable;
        SaveSettings();
        return true;
    }

    /// <summary>Puts the switch back where it was, without the write the setter would otherwise start.</summary>
    private void RevertBacklightTimeout(bool attempted) => SyncControls(() => BacklightTimeoutOn = !attempted);

    /// <summary>
    /// Reads the 30-second timeout and puts the switch on what the keyboard actually holds.
    /// </summary>
    /// <remarks>
    /// The startup read races the elevated helper: the window opens before the worker has connected, the read falls
    /// through to the Acer service, and a machine that runs on direct WMI has no service to answer. The switch then
    /// kept its default "off" while the firmware's timeout was still on, and the keyboard went dark after 30 seconds
    /// with the page saying it would not. Reading again when the page is opened is what closes that gap.
    /// <para>
    /// When nothing answers — which is the case on the reference AN16-41, where <c>APGeAction</c> is registered but the
    /// backlight selector returns a status this build does not recognise — the switch cannot be filled from the
    /// hardware at all. The choice it shows is then the only statement of intent there is, and the only way to make
    /// that choice true is to write it. Leaving it unwritten is precisely the reported bug: a switch reading "off"
    /// over a firmware timeout that is still on.
    /// </para>
    /// </remarks>
    public async Task RefreshBacklightTimeoutAsync()
    {
        if (_hardware == null || !BacklightTimeoutAvailable) return;

        var state = await _hardware.GetBacklightTimeoutAsync().ConfigureAwait(true);
        if (state is bool on)
        {
            SyncControls(() => BacklightTimeoutOn = on);

            _settings.BacklightTimeoutEnabled = on;
            SaveSettings();
            return;
        }

        // Never touched means the switch is showing its default, and that default is what the page is claiming.
        var choice = _settings.BacklightTimeoutEnabled ?? _backlightTimeoutOn;
        await WriteBacklightTimeoutAsync(choice, quiet: true, revertOnFailure: false).ConfigureAwait(true);
    }

    public bool WinKeyLockAvailable => _capabilities.WinKeyLock;

    /// <summary>
    /// The Windows-key lock. There is no getter in the contract, so this is the state the user asked for rather than a
    /// state read back from the firmware; the write's own answer is what the status line reports.
    /// </summary>
    public bool WinKeyLockOn
    {
        get => _winKeyLockOn;
        set
        {
            if (!Set(ref _winKeyLockOn, value)) return;
            if (!_suppressWrite) _ = RunWriteAsync(() => _hardware!.SetWinKeyLockAsync(value), Labels.WinKeyLock);
        }
    }

    /// <summary>The OpenRGB SDK's state, or empty on a platform that does not speak it. Empty hides the line.</summary>
    public string OpenRgbStatus => _openRgb?.OpenRgbStatus ?? "";

    public bool HasOpenRgbStatus => OpenRgbStatus.Length > 0;

    public bool BatteryLimitAvailable => _capabilities.BatteryChargeLimit;

    /// <summary>True when the backend reports at least one of the optional switches this page carries.</summary>
    public bool HasAnyExtra =>
        CoolBoostAvailable || LcdOverdriveAvailable || WinKeyLockAvailable || BacklightTimeoutAvailable;

    /// <summary>
    /// True when the probe found none of the optional nodes this page carries, so the page can say so once. The battery
    /// limit is not in that list: it has its own card on the dashboard, where a user looks for it.
    /// </summary>
    /// <remarks>
    /// A negative answer is only allowed to become a claim when the platform can actually answer. The Windows service
    /// socket has no CoolBoost or overdrive command, so both read as false on a machine that has them; saying "not on
    /// this device" from that would be a lie about the user's hardware.
    /// </remarks>
    public bool NoExtrasAvailable => NegativeAnswersAreConclusive && !HasAnyExtra;

    /// <summary>
    /// True when the extras card has something to show: a switch, or the one-line "nothing here" badge. A platform
    /// that cannot answer hides the card instead of leaving a titled empty box.
    /// </summary>
    public bool ShowExtrasCard => HasAnyExtra || HasOpenRgbStatus || NegativeAnswersAreConclusive;

    /// <summary>
    /// True when a "not supported" answer from this platform is a real answer rather than "could not ask". False on
    /// Windows, where the transport opens on demand and the service socket cannot answer every capability.
    /// </summary>
    private bool NegativeAnswersAreConclusive => _platform?.NegativeAnswersAreConclusive ?? true;

    public bool BatteryLimitOn
    {
        get => _batteryLimitOn;
        set
        {
            if (!Set(ref _batteryLimitOn, value)) return;
            if (!_suppressWrite)
            {
                _ = RunWriteAsync(
                    () => _hardware!.SetBatteryChargeModeAsync(value ? BatteryChargeMode.Optimized80 : BatteryChargeMode.Full),
                    Labels.BatteryLimit);
            }
        }
    }

    // ---------------------------------------------------------------- sign-in launch

    /// <summary>True when the composition root supplied a sign-in mechanism. The card hides itself otherwise.</summary>
    public bool AutoStartAvailable => _autoStart != null;

    /// <summary>
    /// The Task Scheduler entry that starts the app elevated without a UAC prompt. The setter registers or removes it
    /// and then re-reads the real state, because a cancelled prompt must not leave the switch showing "on".
    /// </summary>
    public bool AutoStartOn
    {
        get => _autoStartOn;
        set
        {
            if (!Set(ref _autoStartOn, value)) return;

            // Turning the elevated entry on also puts the app in the tray. The entry exists so the window does not
            // appear at sign-in, and a user who has to find a second switch to get that has been given half a feature.
            // The flag lives in the registered command line, so it is set before the entry is written.
            if (value && !_settings.StartMinimized) StartMinimizedOn = true;

            if (!_suppressWrite) _ = ApplyAutoStartAsync(value);
        }
    }

    /// <summary>What the last attempt did, or empty. The row is hidden rather than left blank.</summary>
    public string AutoStartStatus
    {
        get => _autoStartStatus;
        private set
        {
            if (!Set(ref _autoStartStatus, value)) return;
            Raise(nameof(HasAutoStartStatus));
        }
    }

    public bool HasAutoStartStatus => !string.IsNullOrWhiteSpace(AutoStartStatus);

    // ---------------------------------------------------------------- battery refresh rate

    /// <summary>
    /// True when this platform can change the panel's refresh rate. The card hides itself otherwise, which is what
    /// Linux does: the rate is not reachable from sysfs and a switch that cannot work is worse than no switch.
    /// </summary>
    public bool AutoRefreshRateAvailable => _refreshRate?.IsSupported == true;

    /// <summary>
    /// Drops the built-in panel to 60 Hz on battery and puts the user's own rate back on mains.
    /// </summary>
    /// <remarks>
    /// The setter forces an evaluation: turning the option on while the machine is already on battery has to take
    /// effect now, not at the next replug. The rate that was replaced is remembered by the automation, so a user who
    /// deliberately runs at 120 Hz is not promoted to 165 Hz on every replug.
    /// </remarks>
    public bool AutoRefreshRateOnBattery
    {
        get => _autoRefreshRateOnBattery;
        set
        {
            if (!Set(ref _autoRefreshRateOnBattery, value)) return;
            _settings.AutoRefreshRateSwitching = value;
            SaveSettings();
            ApplyRefreshRate(force: true);
        }
    }

    /// <summary>What the last transition did, or empty. The row is hidden rather than left blank.</summary>
    public string RefreshRateStatus
    {
        get => _refreshRateStatus;
        private set
        {
            if (!Set(ref _refreshRateStatus, value)) return;
            Raise(nameof(HasRefreshRateStatus));
        }
    }

    public bool HasRefreshRateStatus => !string.IsNullOrWhiteSpace(RefreshRateStatus);

    // ---------------------------------------------------------------- MUX switch

    /// <summary>
    /// The GPU modes the switch offers. Automatic is deliberately absent: it is a firmware decision (Advanced
    /// Optimus), not something the user can select, and the backend refuses to write it.
    /// </summary>
    public ObservableCollection<GpuModeChoice> GpuModes { get; } = new();

    public GpuModeChoice? SelectedGpuMode
    {
        get => _selectedGpuMode;
        set => Set(ref _selectedGpuMode, value);
    }

    /// <summary>True when the module exposes <c>dgpu_mode</c>. The card hides itself otherwise.</summary>
    public bool MuxAvailable => _capabilities.MuxSwitch;

    /// <summary>
    /// How many columns the school-mode card takes. Two when the MUX card is hidden, so the row does not keep an empty
    /// half on the machines that have no switch — which is most of them.
    /// </summary>
    public int SchoolModeColumnSpan => MuxAvailable ? 1 : 2;

    // ---------------------------------------------------------------- school mode

    /// <summary>True when the composition root wired a school-mode state machine.</summary>
    public bool SchoolModeAvailable => _schoolMode != null;

    /// <summary>
    /// The switch. Turning it on applies the school-mode values and writes the undo record first; turning it off puts
    /// every recorded value back. The setter is guarded by <see cref="_suppressWrite"/> so reading the persisted state
    /// at startup does not re-apply it.
    /// </summary>
    public bool SchoolModeOn
    {
        get => _schoolModeOn;
        set
        {
            if (!Set(ref _schoolModeOn, value)) return;
            Raise(nameof(SchoolModeStateText));
            if (_suppressWrite) return;
            _ = SetSchoolModeAsync(value);
        }
    }

    /// <summary>What the last school-mode change managed to do, or why it refused.</summary>
    public string SchoolModeDetail
    {
        get => _schoolModeDetail;
        private set
        {
            if (!Set(ref _schoolModeDetail, value)) return;
            Raise(nameof(HasSchoolModeDetail));
        }
    }

    /// <summary>False until the switch has been used once, so the page does not carry an empty status line.</summary>
    public bool HasSchoolModeDetail => !string.IsNullOrWhiteSpace(SchoolModeDetail);

    /// <summary>
    /// "Etkin" or "Kapalı" beside the switch. A switch shows what pressing it would do; this says what is true now,
    /// which is the question a user asks when they come back to the page after a reboot.
    /// </summary>
    public string SchoolModeStateText => SchoolModeOn ? Labels.SchoolModeStateOn : Labels.SchoolModeStateOff;

    // ---------------------------------------------------------------- RGB profiles

    /// <summary>The name the profile list shows when the editor holds a hand-set colour rather than a saved preset.</summary>
    private const string CustomProfileName = "Özel";

    public ObservableCollection<RgbPreset> RgbProfiles { get; } = new();

    /// <summary>
    /// The entry the profile list shows when the editor holds a colour the user set by hand.
    /// </summary>
    /// <remarks>
    /// The list is a statement about where the current colours came from. Once a colour, an effect or a slider is
    /// edited by hand they no longer came from the preset that was selected, and leaving that preset selected is what
    /// made the page look like the profile was fighting the picker. Selecting this entry writes nothing, and it is
    /// never saved to disk: it is a label, not a preset.
    /// </remarks>
    public RgbPreset CustomProfile { get; } = new() { Name = CustomProfileName, IsBuiltIn = true };

    public RgbPreset? SelectedRgbProfile
    {
        get => _selectedRgbProfile;
        set
        {
            if (!Set(ref _selectedRgbProfile, value)) return;

            // The card has no Apply button: picking a profile is the write, the same way picking an effect or a
            // colour is. The guard covers the reloads that set this property themselves.
            if (_suppressProfileApply || value == null) return;
            _ = ApplyRgbProfileAsync();
        }
    }

    /// <summary>The name typed into the save box. Empty means "nothing to save yet".</summary>
    public string NewProfileName
    {
        get => _newProfileName;
        set => Set(ref _newProfileName, value);
    }

    /// <summary>The result of the last profile action, shown under the list.</summary>
    public string RgbProfileStatus
    {
        get => _rgbProfileStatus;
        private set
        {
            if (!Set(ref _rgbProfileStatus, value)) return;
            Raise(nameof(HasRgbProfileStatus));
        }
    }

    /// <summary>False until something has happened, so the page does not draw an empty status line.</summary>
    public bool HasRgbProfileStatus => !string.IsNullOrWhiteSpace(_rgbProfileStatus);

    /// <summary>Set when the presets file existed but could not be read, so the page can say so.</summary>
    public string? RgbProfileLoadError => _rgbProfiles.LoadError;

    public bool HasRgbProfileLoadError => !string.IsNullOrWhiteSpace(RgbProfileLoadError);

    // ---------------------------------------------------------------- game detection

    /// <summary>True while a configured game is running and its profile is in effect.</summary>
    public bool GameActive
    {
        get => _gameActive;
        private set
        {
            if (!Set(ref _gameActive, value)) return;
            Raise(nameof(GameStatusText));
        }
    }

    /// <summary>"Oyun profili uygulandı: …" or the idle line. Never empty, so the card always says something.</summary>
    public string GameStatusText => string.IsNullOrEmpty(_gameStatus) ? Labels.GameIdle : _gameStatus;

    /// <summary>True when automatic switching is wired and enabled in the settings.</summary>
    public bool AutoGameModeAvailable => _gameProfiles != null;

    public bool AutoGameModeOn
    {
        get => _settings.AutoGameModeEnabled;
        set
        {
            if (_settings.AutoGameModeEnabled == value) return;
            _settings.AutoGameModeEnabled = value;
            SaveSettings();
            Raise(nameof(AutoGameModeOn));
            // Turning it off while a game is running has to end the session, or the override would stay in place and
            // the switch would look broken.
            if (!value) _ = PollGameAsync();
        }
    }

    // ---------------------------------------------------------------- game profiles page

    /// <summary>The configured game rules, as the Game Profiles page lists them.</summary>
    public ObservableCollection<GameProfileRow> GameProfiles { get; } = new();

    /// <summary>The modes a new rule can target. Same list the dashboard offers, so a rule cannot name a mode the
    /// firmware refuses.</summary>
    public ObservableCollection<ModeChoice> GameModeChoices { get; } = new();

    /// <summary>The fan modes a new rule can target.</summary>
    public ObservableCollection<FanModeChoice> GameFanChoices { get; } = new();

    /// <summary>The game the quick selector on the modes page has highlighted.</summary>
    public GameProfileRow? SelectedGameProfile
    {
        get => _selectedGameProfile;
        set
        {
            if (!Set(ref _selectedGameProfile, value)) return;
            SyncGameTargets();
        }
    }

    public string NewGameExe
    {
        get => _newGameExe;
        set => Set(ref _newGameExe, value);
    }

    public string NewGameName
    {
        get => _newGameName;
        set => Set(ref _newGameName, value);
    }

    public ModeChoice? NewGameMode
    {
        get => _newGameMode;
        set => Set(ref _newGameMode, value);
    }

    public FanModeChoice? NewGameFanMode
    {
        get => _newGameFanMode;
        set => Set(ref _newGameFanMode, value);
    }

    /// <summary>The last thing the page did: added, deleted, or why it refused.</summary>
    public string GameProfileStatus
    {
        get => _gameProfileStatus;
        private set
        {
            if (!Set(ref _gameProfileStatus, value)) return;
            Raise(nameof(HasGameProfileStatus));
        }
    }

    /// <summary>False until the page has done something, so it does not draw an empty status line.</summary>
    public bool HasGameProfileStatus => !string.IsNullOrWhiteSpace(_gameProfileStatus);

    /// <summary>True when there is at least one rule, so the empty-state line and the list never both show.</summary>
    public bool HasGameProfiles => GameProfiles.Count > 0;

    public bool HasNoGameProfiles => !HasGameProfiles;

    public RelayCommand AddGameProfileCommand { get; }

    /// <summary>
    /// Rebuilds the list from the settings file. The service reads <c>AppSettings.GameProfiles</c> on every poll, so
    /// adding or removing a rule here is enough for detection to pick it up on the next tick.
    /// </summary>
    private void BuildGameProfileRows()
    {
        GameProfiles.Clear();
        foreach (var profile in _settings.GameProfiles)
        {
            if (profile == null || string.IsNullOrWhiteSpace(profile.ExeName)) continue;
            GameProfiles.Add(new GameProfileRow(
                profile,
                ModeLabel(profile.TargetMode),
                profile.TargetFanCurveEnabled ? Labels.CustomCurve : FanModeLabel(profile.TargetFanMode),
                Labels.DeleteProfile,
                DeleteGameProfile));
        }

        // The quick picker on the modes page opens on the first rule rather than on nothing, so the delete button
        // beside it is live from the first frame.
        if (SelectedGameProfile == null || !GameProfiles.Contains(SelectedGameProfile))
            SelectedGameProfile = GameProfiles.FirstOrDefault();

        // The per-game controls follow the selection, so the card never shows one game's mode beside another's name.
        SyncGameTargets();

        Raise(nameof(HasGameProfiles));
        Raise(nameof(HasNoGameProfiles));
    }

    /// <summary>
    /// The per-game target lists. They are copies of the mode, boost and fan lists rather than the same objects: a
    /// card in the game form picks a target, it does not apply one, so it must not share the selection state the
    /// machine's own controls paint.
    /// </summary>
    private void BuildGameTargetChoices()
    {
        GameModeChoices.Clear();
        foreach (var card in Modes)
        {
            GameModeChoices.Add(new ModeChoice(card.Mode, card.Label, card.Description, card.Icon));
        }

        GameFanChoices.Clear();
        foreach (var card in FanModes)
        {
            GameFanChoices.Add(new FanModeChoice(card.Mode, card.Label, card.Description, card.Icon));
        }

        SyncGameTargets();
    }

    private void AddGameProfile()
    {
        var exe = (NewGameExe ?? "").Trim();
        if (exe.Length == 0)
        {
            GameProfileStatus = Labels.GameExeRequired;
            return;
        }

        if (_settings.GameProfiles.Any(p => p != null && p.ExeName.Equals(exe, StringComparison.OrdinalIgnoreCase)))
        {
            GameProfileStatus = Labels.GameAlreadyDefined;
            return;
        }

        var profile = new GameProfile
        {
            ExeName = exe,
            DisplayName = string.IsNullOrWhiteSpace(NewGameName) ? exe : NewGameName.Trim(),
            TargetMode = NewGameMode?.Mode ?? OperatingMode.Default,
            TargetFanMode = NewGameFanMode?.Mode ?? FanMode.Auto,
            AutoApply = true
        };

        _settings.GameProfiles.Add(profile);
        SaveSettings();
        BuildGameProfileRows();

        NewGameExe = "";
        NewGameName = "";
        GameProfileStatus = Labels.GameAdded(profile.DisplayName);
    }

    private void DeleteGameProfile(GameProfileRow row)
    {
        var profile = _settings.GameProfiles
            .FirstOrDefault(p => p != null && p.ExeName.Equals(row.ExeName, StringComparison.OrdinalIgnoreCase));
        if (profile == null) return;

        _settings.GameProfiles.Remove(profile);
        SaveSettings();
        BuildGameProfileRows();
        GameProfileStatus = Labels.GameDeleted(row.DisplayName);
    }

    private static string FanModeLabel(FanMode mode) => mode switch
    {
        FanMode.Auto => Loc.T("Otomatik"),
        FanMode.Max => Loc.T("Maksimum"),
        FanMode.Custom => Loc.T("Özel eğri"),
        _ => mode.ToString()
    };

    // ---------------------------------------------------------------- game library

    /// <summary>True when this platform can scan for installed games.</summary>
    public bool GameScanAvailable => _gameLibrary != null;

    /// <summary>True while a scan is running, which disables the button and swaps its caption.</summary>
    public bool GameScanRunning
    {
        get => _gameScanRunning;
        private set
        {
            if (!Set(ref _gameScanRunning, value)) return;
            Raise(nameof(GameScanButtonText));
        }
    }

    /// <summary>The scan button's caption: "Yüklü Oyunları Tara", or "Taranıyor…" while it runs.</summary>
    public string GameScanButtonText => GameScanRunning ? Labels.ScanningGames : Labels.ScanGames;

    /// <summary>What the last scan found, or why it found nothing.</summary>
    public string GameScanStatus
    {
        get => _gameScanStatus;
        private set
        {
            if (!Set(ref _gameScanStatus, value)) return;
            Raise(nameof(HasGameScanStatus));
        }
    }

    public bool HasGameScanStatus => !string.IsNullOrWhiteSpace(_gameScanStatus);

    public AsyncCommand ScanGamesCommand { get; }

    /// <summary>Opens the file picker and adds the chosen executable as a game rule.</summary>
    public AsyncCommand AddGameFromFileCommand { get; }

    /// <summary>Deletes the selected rule after a confirmation.</summary>
    public AsyncCommand DeleteSelectedGameCommand { get; }

    /// <summary>
    /// Scans the installed libraries and adds a rule for every game that does not have one yet.
    /// </summary>
    /// <remarks>
    /// The scan does not run <see cref="GameLibraryRules.SanitizeProfiles"/> afterwards: the candidates are already
    /// filtered, and a sanitise pass would delete a rule the user added by hand for a game whose name happens to
    /// contain "server" or "console".
    /// </remarks>
    public async Task ScanGamesAsync()
    {
        if (_gameLibrary == null || GameScanRunning) return;

        GameScanRunning = true;
        GameScanStatus = "";

        try
        {
            var found = await Task.Run(() => _gameLibrary.ScanInstalledGames()).ConfigureAwait(true);

            int added = 0;
            foreach (var game in found)
            {
                if (string.IsNullOrWhiteSpace(game.ExeName)) continue;

                bool exists = _settings.GameProfiles.Any(p => p != null &&
                    (p.ExeName.Equals(game.ExeName, StringComparison.OrdinalIgnoreCase) ||
                     p.DisplayName.Equals(game.DisplayName, StringComparison.OrdinalIgnoreCase)));
                if (exists) continue;

                _settings.GameProfiles.Add(new GameProfile
                {
                    ExeName = game.ExeName,
                    DisplayName = game.DisplayName,
                    TargetMode = OperatingMode.Turbo,
                    TargetCpuBoost = _gameLibrary.DetermineOptimalCpuBoost(game.ExeName),
                    TargetFanMode = FanMode.Auto,
                    InstallPath = game.InstallPath,
                    AutoApply = true
                });
                added++;
            }

            if (added > 0)
            {
                SaveSettings();
                BuildGameProfileRows();
            }

            GameScanStatus = added > 0 ? Labels.GamesScanned(added) : Labels.NoNewGames;
            Status = GameScanStatus;
        }
        catch (Exception ex)
        {
            AppLog.Warn(Loc.T("Oyun kitaplığı taranamadı"), ex.ToString());
            GameScanStatus = Labels.GameScanFailed;
        }
        finally
        {
            GameScanRunning = false;
        }
    }

    /// <summary>
    /// Asks for an executable and adds it as a rule. The file is validated first, so a system binary or a launcher
    /// stub is refused with the reason rather than becoming a rule that pins the machine to Turbo.
    /// </summary>
    public async Task AddGameFromFileAsync()
    {
        if (_gameLibrary == null || _dialogs == null)
        {
            GameProfileStatus = Labels.GameScanUnavailable;
            return;
        }

        var path = await _dialogs.PickExecutableAsync(DefaultGamePickerDirectory()).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(path)) return;

        var error = _gameLibrary.ValidateCustomExecutable(path);
        if (error != null)
        {
            GameProfileStatus = error;
            Status = error;
            return;
        }

        var exeName = Path.GetFileNameWithoutExtension(path);
        var existing = _settings.GameProfiles.FirstOrDefault(p =>
            p != null && p.ExeName.Equals(exeName, StringComparison.OrdinalIgnoreCase));

        if (existing != null)
        {
            // The rule is kept as the user configured it; only the path is refreshed, because the game may have moved.
            existing.InstallPath = path;
            SaveSettings();
            BuildGameProfileRows();
            GameProfileStatus = Labels.GameAlreadyDefined;
            return;
        }

        var profile = new GameProfile
        {
            ExeName = exeName,
            DisplayName = exeName.Replace('_', ' ').Replace('-', ' '),
            TargetMode = OperatingMode.Turbo,
            TargetCpuBoost = _gameLibrary.DetermineOptimalCpuBoost(exeName),
            TargetFanMode = FanMode.Auto,
            InstallPath = path,
            AutoApply = true
        };

        _settings.GameProfiles.Add(profile);
        SaveSettings();
        BuildGameProfileRows();
        SelectedGameProfile = GameProfiles.FirstOrDefault(r =>
            r.ExeName.Equals(exeName, StringComparison.OrdinalIgnoreCase)) ?? SelectedGameProfile;

        GameProfileStatus = Labels.GameAdded(profile.DisplayName);
        Status = GameProfileStatus;
    }

    /// <summary>
    /// Deletes the selected rule. A confirmation is required because the rule carries the mode, the boost and the fan
    /// curve the user configured for that game, and there is no undo.
    /// </summary>
    public async Task DeleteSelectedGameAsync()
    {
        if (SelectedGameProfile is not GameProfileRow row) return;

        if (_dialogs != null)
        {
            bool confirmed = await _dialogs
                .ConfirmAsync(Labels.DeleteProfileTitle, Labels.DeleteProfileConfirm(row.DisplayName))
                .ConfigureAwait(true);
            if (!confirmed) return;
        }

        DeleteGameProfile(row);
    }

    /// <summary>Where the picker opens: the Steam library when there is one, so the common case is one click.</summary>
    private static string? DefaultGamePickerDirectory()
    {
        foreach (var candidate in new[]
                 {
                     @"C:\Program Files (x86)\Steam\steamapps\common",
                     @"C:\Program Files\Steam\steamapps\common"
                 })
        {
            if (Directory.Exists(candidate)) return candidate;
        }

        return null;
    }

    // ---------------------------------------------------------------- per-game targets

    /// <summary>The mode the selected game switches to.</summary>
    public ModeChoice? SelectedGameMode
    {
        get => _selectedGameMode;
        set
        {
            // The combo pushes null while it initialises; that must not clear the target the user configured.
            if (value == null) return;
            if (!Set(ref _selectedGameMode, value)) return;
            if (SelectedGameProfile is not GameProfileRow row) return;
            if (FindProfile(row) is not GameProfile profile) return;

            profile.TargetMode = value.Mode;
            SaveSettings();
            BuildGameProfileRows();
        }
    }

    /// <summary>The boost behaviour the selected game switches to.</summary>
    public CpuBoostChoice? SelectedGameCpuBoost
    {
        get => _selectedGameCpuBoost;
        set
        {
            if (value == null) return;
            if (!Set(ref _selectedGameCpuBoost, value)) return;
            if (SelectedGameProfile is not GameProfileRow row) return;
            if (FindProfile(row) is not GameProfile profile) return;

            profile.TargetCpuBoost = value.Mode;
            SaveSettings();
        }
    }

    /// <summary>
    /// The fan behaviour the selected game switches to. Choosing Özel Eğri is also what turns the game's own curve on,
    /// so the two cannot disagree: the editor below the picker appears exactly when the curve is the target.
    /// </summary>
    public FanModeChoice? SelectedGameFanMode
    {
        get => _selectedGameFanMode;
        set
        {
            if (value == null) return;
            if (!Set(ref _selectedGameFanMode, value)) return;
            Raise(nameof(ShowGameFanCurveEditor));
            if (SelectedGameProfile is not GameProfileRow row) return;
            if (FindProfile(row) is not GameProfile profile) return;

            profile.TargetFanMode = value.Mode;
            profile.TargetFanCurveEnabled = value.Mode == FanMode.Custom;
            SaveSettings();
            BuildGameProfileRows();
        }
    }

    /// <summary>The mouse profile the selected game switches to. 0 means "leave it alone".</summary>
    public MouseProfileOption? SelectedGameMouseProfile
    {
        get => _selectedGameMouseProfile;
        set
        {
            if (value == null) return;
            if (!Set(ref _selectedGameMouseProfile, value)) return;
            if (SelectedGameProfile is not GameProfileRow row) return;
            if (FindProfile(row) is not GameProfile profile) return;

            profile.TargetAtkMouseProfile = value.Index;
            SaveSettings();
        }
    }

    /// <summary>
    /// True when the selected game runs its own fan curve for the length of its session. The editor follows the fan
    /// picker rather than a switch of its own: choosing Özel Eğri is what turns the curve on, so there is no second
    /// control that can disagree with the picker.
    /// </summary>
    public bool ShowGameFanCurveEditor => SelectedGameFanMode?.Mode == FanMode.Custom;

    /// <summary>Copies the desktop curve onto the selected game, so a game curve starts from a shape the user knows.</summary>
    public RelayCommand CopyDesktopCurveCommand { get; }

    private void CopyDesktopCurveToGame()
    {
        if (SelectedGameProfile is not GameProfileRow row) return;
        if (FindProfile(row) is not GameProfile profile) return;

        profile.TargetFanCurvePoints = FanCurve.Sanitize(_settings.FanCurvePoints);
        profile.TargetFanCurveEnabled = true;
        SaveSettings();
        BuildGameProfileRows();
        GameProfileStatus = Labels.GameCurveCopied(row.DisplayName);
    }

    /// <summary>
    /// True when the selected game's rule is applied on its own when the game starts.
    /// </summary>
    /// <remarks>
    /// The service reads this on every poll, so turning it off stops the automatic switch without deleting the rule:
    /// the mode, the boost and the curve stay configured and can still be applied by hand. A rule that is never
    /// applied automatically is a saved preset, which is a thing users ask for.
    /// </remarks>
    public bool GameAutoApply
    {
        get => _gameAutoApply;
        set
        {
            if (!Set(ref _gameAutoApply, value)) return;
            if (_suppressWrite) return;
            if (SelectedGameProfile is not GameProfileRow row) return;
            if (FindProfile(row) is not GameProfile profile) return;

            profile.AutoApply = value;
            SaveSettings();
        }
    }

    /// <summary>
    /// The six anchors of the selected game's own curve. Rebuilt whenever the selection moves, so the sliders always
    /// edit the rule the picker names.
    /// </summary>
    public ObservableCollection<CurveAnchorViewModel> GameCurveAnchors { get; } = new();

    /// <summary>
    /// Fills <see cref="GameCurveAnchors"/> from the selected rule, falling back to the desktop curve when the rule
    /// has no shape of its own yet.
    /// </summary>
    /// <remarks>
    /// The anchors are always the six the editor offers, even when the stored list is shorter: a curve with three
    /// points would otherwise give the user three sliders and no way to reach the other three temperatures.
    /// </remarks>
    private void BuildGameCurveAnchors()
    {
        var profile = SelectedGameProfile is GameProfileRow row ? FindProfile(row) : null;
        var source = FanCurve.Sanitize(profile?.TargetFanCurvePoints ?? _settings.FanCurvePoints);

        GameCurveAnchors.Clear();
        foreach (var temperature in FanCurve.AnchorTemperatures)
        {
            int percent = source.FirstOrDefault(p => p.TemperatureC == temperature)?.Percent ?? FanCurve.MinPercent;
            GameCurveAnchors.Add(new CurveAnchorViewModel(temperature, percent, OnGameCurveChanged) { Labels = Labels });
        }
    }

    /// <summary>Writes a moved game anchor back into the rule. The shape is the state, so there is nothing else to save.</summary>
    private void OnGameCurveChanged()
    {
        if (_suppressWrite) return;
        if (SelectedGameProfile is not GameProfileRow row) return;
        if (FindProfile(row) is not GameProfile profile) return;

        profile.TargetFanCurvePoints = GameCurveAnchors
            .Select(a => new FanCurvePoint(a.TemperatureC, a.Percent))
            .ToList();

        // The rule is the state, so it is written as it is dragged; only the file write waits for the drag to stop.
        _curveDebounce.Run(() => SaveSettings());
    }

    /// <summary>The rule behind a row, matched on the executable name the row carries.</summary>
    private GameProfile? FindProfile(GameProfileRow row) =>
        _settings.GameProfiles.FirstOrDefault(p =>
            p != null && p.ExeName.Equals(row.ExeName, StringComparison.OrdinalIgnoreCase));

    /// <summary>Fills the per-game controls from the selected rule.</summary>
    private void SyncGameTargets()
    {
        var profile = SelectedGameProfile is GameProfileRow row ? FindProfile(row) : null;

        bool previous = _suppressWrite;
        _suppressWrite = true;
        try
        {
            _selectedGameMode = profile == null
                ? null
                : GameModeChoices.FirstOrDefault(m => m.Mode == profile.TargetMode);
            _selectedGameCpuBoost = profile == null
                ? null
                : CpuBoostChoices.FirstOrDefault(c => c.Mode == profile.TargetCpuBoost);
            _newGameFanMode = profile == null
                ? _newGameFanMode
                : GameFanChoices.FirstOrDefault(f => f.Mode == profile.TargetFanMode) ?? _newGameFanMode;
            _selectedGameFanMode = profile == null
                ? null
                : GameFanChoices.FirstOrDefault(f => f.Mode == profile.TargetFanMode);
            _selectedGameMouseProfile = profile == null
                ? null
                : MouseProfileOptions.FirstOrDefault(m => m.Index == profile.TargetAtkMouseProfile);
            _gameAutoApply = profile?.AutoApply ?? true;
        }
        finally
        {
            _suppressWrite = previous;
        }

        Raise(nameof(SelectedGameMode));
        Raise(nameof(SelectedGameCpuBoost));
        Raise(nameof(SelectedGameFanMode));
        Raise(nameof(SelectedGameMouseProfile));
        Raise(nameof(ShowGameFanCurveEditor));
        Raise(nameof(GameAutoApply));

        // The six sliders follow the selection, so the card never edits one game's curve while naming another.
        BuildGameCurveAnchors();
    }

    // ---------------------------------------------------------------- mouse profiles

    /// <summary>The five mouse choices: leave it alone, then the device's own four configurations.</summary>
    public ObservableCollection<MouseProfileOption> MouseProfileOptions { get; } = new();

    /// <summary>True when a supported mouse answered, which is what shows the FARE row and the mouse card.</summary>
    public bool MouseProfileAvailable => _mouseProfiles?.IsSupported == true;

    /// <summary>
    /// True when the mouse controls are drawn at all. The integration switch is part of the answer, exactly as it was
    /// in the WPF window: a user who turned the ATK integration off asked for the mouse controls to go away, not just
    /// for the writes to stop. Every mouse control on the modes page is gated on this, including the per-game picker.
    /// </summary>
    public bool ShowMouseProfileUi => MouseProfileAvailable && _settings.AtkMouseIntegrationEnabled;

    /// <summary>The profile the mouse is in, or 0 when it has not said.</summary>
    public int CurrentMouseProfile => _mouseProfiles?.CurrentProfile ?? 0;

    /// <summary>
    /// The default profile applied at launch. Picking one applies it in the same step: the picker is the action, so a
    /// separate Uygula button would only be a second click for the same write.
    /// </summary>
    public MouseProfileOption? SelectedMouseProfile
    {
        get => _selectedMouseProfile;
        set
        {
            if (!Set(ref _selectedMouseProfile, value) || value == null) return;
            foreach (var option in MouseProfileOptions) option.IsSelected = ReferenceEquals(option, value);
            _settings.DefaultAtkMouseProfile = value.Index;
            SaveSettings();
            _ = ApplyDefaultMouseProfileAsync();
        }
    }

    /// <summary>What the last mouse write did, or why it could not.</summary>
    public string MouseProfileStatus
    {
        get => _mouseProfileStatus;
        private set
        {
            if (!Set(ref _mouseProfileStatus, value)) return;
            Raise(nameof(HasMouseProfileStatus));
        }
    }

    public bool HasMouseProfileStatus => !string.IsNullOrWhiteSpace(_mouseProfileStatus);

    /// <summary>The FARE row's figure: the active configuration, or an em dash when the mouse has not answered.</summary>
    public string SystemMouseText
    {
        get
        {
            if (!MouseProfileAvailable) return "—";
            var current = CurrentMouseProfile;
            return current <= 0 ? "—" : $"Config {current}";
        }
    }

    /// <summary>
    /// True when the rail draws the FARE row at all. The integration switch is part of the answer, exactly as it was in
    /// the WPF window: a user who turned the ATK integration off asked for the row to go away, not just for the writes
    /// to stop.
    /// </summary>
    public bool ShowSystemMouseRow => ShowMouseProfileUi;

    private void BuildMouseProfileChoices()
    {
        MouseProfileOptions.Clear();
        MouseProfileOptions.Add(new MouseProfileOption(0, Labels.MousePassive, SelectMouseProfile));
        for (int i = 1; i <= 4; i++) MouseProfileOptions.Add(new MouseProfileOption(i, $"Config {i}", SelectMouseProfile));

        _selectedMouseProfile = MouseProfileOptions.FirstOrDefault(m => m.Index == _settings.DefaultAtkMouseProfile)
                                ?? MouseProfileOptions[0];
        foreach (var option in MouseProfileOptions) option.IsSelected = ReferenceEquals(option, _selectedMouseProfile);
    }

    /// <summary>A click in the FARE quick menu. The setter stores and applies in one step, so there is nothing to add.</summary>
    private void SelectMouseProfile(MouseProfileOption option) => SelectedMouseProfile = option;

    /// <summary>Applies the chosen default profile to the mouse and reports what happened.</summary>
    private async Task ApplyDefaultMouseProfileAsync()
    {
        if (_mouseProfiles == null || SelectedMouseProfile is not MouseProfileOption choice) return;

        if (choice.Index <= 0)
        {
            MouseProfileStatus = Labels.MousePassiveApplied;
            return;
        }

        bool ok = await _mouseProfiles.ApplyProfileAsync(choice.Index).ConfigureAwait(true);
        MouseProfileStatus = ok ? Labels.MouseProfileApplied(choice.Index) : Labels.MouseProfileFailed;
        Status = MouseProfileStatus;
        Raise(nameof(SystemMouseText));
        Raise(nameof(CurrentMouseProfile));
    }

    /// <summary>Asks the mouse which profile it is in, so the rail shows the real one rather than the stored default.</summary>
    private async Task RefreshMouseProfileAsync()
    {
        if (_mouseProfiles == null) return;

        try
        {
            var current = await _mouseProfiles.QueryCurrentProfileAsync().ConfigureAwait(true);
            if (current is int profile && profile > 0)
            {
                MouseProfileStatus = Labels.MouseProfileApplied(profile);
            }

            Raise(nameof(MouseProfileAvailable));
            Raise(nameof(ShowMouseProfileUi));
            Raise(nameof(ShowSystemMouseRow));
            Raise(nameof(SystemMouseText));
            Raise(nameof(CurrentMouseProfile));
        }
        catch (Exception ex)
        {
            AppLog.Warn(Loc.T("Fare profili okunamadı"), ex.Message);
        }
    }

    // ---------------------------------------------------------------- fan curve presets

    /// <summary>The three ready-made shapes offered above the curve editor.</summary>
    public ObservableCollection<CurvePresetChoice> CurvePresets { get; } = new();

    /// <summary>
    /// The preset the current anchors are, or null when the curve is the user's own. Setting it writes the shape into
    /// the anchors; the preset id is never stored, because the shape is the state.
    /// </summary>
    public CurvePresetChoice? SelectedCurvePreset
    {
        get => _selectedCurvePreset;
        set
        {
            if (!Set(ref _selectedCurvePreset, value) || value == null) return;
            ApplyPresetPoints(value);
        }
    }

    private void BuildCurvePresets()
    {
        CurvePresets.Clear();
        foreach (var preset in FanCurvePresets.All)
        {
            CurvePresets.Add(new CurvePresetChoice(
                preset.Id, Loc.T(preset.NameKey), preset.ToPoints(), ApplyCurvePreset));
        }

        SyncCurvePreset();
    }

    /// <summary>Paints the preset the anchors currently match, or clears the selection when they are the user's own.</summary>
    private void SyncCurvePreset()
    {
        var match = FanCurvePresets.Match(CurveAnchors.Select(a => new FanCurvePoint(a.TemperatureC, a.Percent)));
        var choice = match == null ? null : CurvePresets.FirstOrDefault(p => p.Id == match.Id);

        _selectedCurvePreset = choice;
        foreach (var preset in CurvePresets) preset.IsSelected = ReferenceEquals(preset, choice);
        Raise(nameof(SelectedCurvePreset));
    }

    private void ApplyCurvePreset(CurvePresetChoice preset) => ApplyPresetPoints(preset);

    /// <summary>Writes a preset's six values into the anchors, which is exactly what moving the sliders does.</summary>
    private void ApplyPresetPoints(CurvePresetChoice preset)
    {
        foreach (var point in preset.Points)
        {
            var anchor = CurveAnchors.FirstOrDefault(a => a.TemperatureC == point.TemperatureC);
            if (anchor != null) anchor.Percent = point.Percent;
        }

        _selectedCurvePreset = preset;
        foreach (var choice in CurvePresets) choice.IsSelected = ReferenceEquals(choice, preset);
        Raise(nameof(SelectedCurvePreset));
        Raise(nameof(CurvePreview));
        Raise(nameof(CurvePoints));
    }

    // ---------------------------------------------------------------- pending MUX mode

    /// <summary>
    /// The GPU mode that was written but needs a restart to take effect, or null when nothing is pending.
    /// </summary>
    public GpuOperatingMode? PendingGpuMode
    {
        get => _pendingGpuMode;
        private set
        {
            if (!Set(ref _pendingGpuMode, value)) return;
            Raise(nameof(ShowPendingGpuModeBadge));
        }
    }

    /// <summary>True when the card shows the "restart to apply" badge.</summary>
    public bool ShowPendingGpuModeBadge => _pendingGpuMode != null;

    /// <summary>
    /// The sentence under the MUX row: the restart warning for a staged switch, the reason for a refused one, or
    /// nothing. It is stored rather than derived from <see cref="PendingGpuMode"/> because a refusal has no pending
    /// mode to derive from and still has to say something.
    /// </summary>
    public string GpuModeNote
    {
        get => _gpuModeNote;
        private set
        {
            if (!Set(ref _gpuModeNote, value)) return;
            Raise(nameof(HasGpuModeNote));
        }
    }

    public bool HasGpuModeNote => !string.IsNullOrWhiteSpace(_gpuModeNote);

    // ---------------------------------------------------------------- desktop restore

    /// <summary>
    /// True when the app wrote a mode the firmware refused and the user can ask it to try again. The tray offers the
    /// same retry, so a machine that came back from a suspend in the wrong mode can be fixed without the window.
    /// </summary>
    public bool HasPendingDesktopRestore => _pendingDesktopRestore;

    private bool _pendingDesktopRestore;

    /// <summary>Retries the write that failed. Clears the flag when it succeeds.</summary>
    public async Task RetryDesktopRestoreAsync()
    {
        if (_hardware == null || SelectedMode is not ModeChoice card) return;

        bool ok = await _hardware.SetOperatingModeAsync(card.Mode).ConfigureAwait(true);
        _pendingDesktopRestore = !ok;
        Raise(nameof(HasPendingDesktopRestore));

        Status = ok ? Labels.DesktopRestoreDone : Labels.DesktopRestoreFailed;
    }

    // ---------------------------------------------------------------- rail width

    /// <summary>
    /// The rail's width in device-independent pixels. Persisted because it is a layout choice: someone who widened the
    /// rail to read a long translated label wants it wide next time too.
    /// </summary>
    public double SidebarWidth
    {
        get => _sidebarWidth;
        set
        {
            var clamped = AppSettings.ClampSidebarWidth(value);
            if (!Set(ref _sidebarWidth, clamped)) return;

            _settings.SidebarWidth = clamped;
            SaveSettings();
        }
    }

    // ---------------------------------------------------------------- window placement

    /// <summary>The stored window placement, so the window can put itself back where it was.</summary>
    public (double? Left, double? Top, double? Width, double? Height, bool Maximized) ReadWindowPlacement() =>
        (_settings.WindowLeft, _settings.WindowTop, _settings.WindowWidth, _settings.WindowHeight,
            _settings.WindowMaximized);

    /// <summary>Writes the window placement to the settings file. Called when the window hides or closes.</summary>
    public void SaveWindowPlacement(double left, double top, double width, double height, bool maximized)
    {
        _settings.WindowLeft = left;
        _settings.WindowTop = top;
        _settings.WindowWidth = width;
        _settings.WindowHeight = height;
        _settings.WindowMaximized = maximized;
        SaveSettings();
    }

    // ---------------------------------------------------------------- toast action

    /// <summary>The optional button on the toast, or empty when the message needs no action.</summary>
    public string ToastActionLabel
    {
        get => _toastActionLabel;
        private set
        {
            if (!Set(ref _toastActionLabel, value)) return;
            Raise(nameof(HasToastAction));
        }
    }

    public bool HasToastAction => !string.IsNullOrWhiteSpace(_toastActionLabel);

    public RelayCommand ToastActionCommand { get; }

    private Action? _toastAction;

    /// <summary>Attaches an action to the message that is about to be shown.</summary>
    private void SetToastAction(string label, Action action)
    {
        _toastAction = action;
        ToastActionLabel = label;
    }

    private void RunToastAction()
    {
        var action = _toastAction;
        _toastAction = null;
        ToastActionLabel = "";
        action?.Invoke();
    }

    /// <summary>
    /// Puts back the state a crashed game session overrode.
    /// </summary>
    /// <remarks>
    /// The record is written before the game's profile is applied and cleared when the session ends, so a record that
    /// is still there on the next launch is the only evidence that the process died mid-session. Restoring it is what
    /// stops a machine from booting into Turbo with no game running and no explanation.
    /// </remarks>
    private async Task RecoverGameSessionAsync()
    {
        var recovery = _settings.GameSessionRecovery;
        if (recovery is not { IsActive: true }) return;

        try
        {
            if (_hardware != null)
            {
                if (recovery.RestoreMode) await _hardware.SetOperatingModeAsync(recovery.SavedMode).ConfigureAwait(true);
                if (recovery.RestoreFan) await _hardware.SetFanModeAsync(recovery.SavedFan).ConfigureAwait(true);
            }

            if (recovery.RestoreMode) _settings.LastOperatingMode = recovery.SavedMode;

            if (recovery.SavedMouseProfile > 0 && _mouseProfiles != null)
            {
                await _mouseProfiles.ApplyProfileAsync(recovery.SavedMouseProfile).ConfigureAwait(true);
            }

            Status = Labels.GameSessionRecovered;
            AppLog.Info(Loc.T("Yarım kalan oyun oturumu geri alındı"), $"{recovery.SavedMode} · {recovery.SavedFan}");
        }
        catch (Exception ex)
        {
            AppLog.Warn(Loc.T("Oyun oturumu geri alınamadı"), ex.Message);
        }
        finally
        {
            _settings.GameSessionRecovery = null;
            SaveSettings();
        }
    }

    /// <summary>
    /// Records the state a game session is about to override, so a crash mid-session can be undone on the next launch.
    /// </summary>
    private void BeginGameSessionRecovery(GameProfile profile)
    {
        _settings.GameSessionRecovery = new GameSessionRecoveryState
        {
            IsActive = true,
            RestoreMode = true,
            RestoreFan = true,
            SavedMode = _settings.LastOperatingMode ?? OperatingMode.Default,
            SavedFan = SelectedFanMode?.Mode ?? FanMode.Auto,
            SavedMouseProfile = _mouseProfiles?.CurrentProfile ?? 0,
            Timestamp = DateTime.UtcNow
        };

        // The record is the only thing that puts the machine back if the process dies mid-game, so a write that did
        // not land is reported rather than assumed. The session still starts: refusing to launch a game because a
        // settings file could not be written would be the worse trade.
        if (SaveSettings()) return;

        Status = Labels.GameSessionRecoveryNotSaved;
        AppLog.Warn(Loc.T("Oyun oturumu kaydı yazılamadı"), "Çökme durumunda mod ve fan geri alınamayabilir.");
    }

    /// <summary>Clears the recovery record once the session ended cleanly.</summary>
    private void EndGameSessionRecovery()
    {
        if (_settings.GameSessionRecovery == null) return;

        _settings.GameSessionRecovery = null;
        SaveSettings();
    }

    // ---------------------------------------------------------------- hardware report

    /// <summary>
    /// The privacy-safe Markdown report. Built on demand rather than cached: it is a few hundred bytes of string work
    /// and the probe state can change after a rescan.
    /// </summary>
    public string HardwareReportText => _platform?.BuildHardwareReport() ?? "";

    public bool HardwareReportAvailable => _platform != null;

    // ---------------------------------------------------------------- settings page

    public IReadOnlyList<LocLanguage> Languages => Loc.Languages;

    public LocLanguage? SelectedLanguage
    {
        get => _selectedLanguage;
        set
        {
            if (!Set(ref _selectedLanguage, value) || value == null) return;
            Loc.SetLanguage(value.Code);
            _settings.Language = value.Code;
            SaveSettings();
            Labels = new ShellLabels();
            // The theme names are catalogue keys like every other label, so the picker is rebuilt with the window.
            BuildThemeChoices();
            // Moving into or out of Hindi adds or removes the Devanagari face from the typeface picker.
            Raise(nameof(UiFonts));
            Status = Labels.LanguageRestarting;
            RestartTask = RestartForLanguageAsync();
        }
    }

    /// <summary>
    /// Restarts the process so the new language reaches the parts of the shell that are built once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The catalogues are read through a static, and the tray menu, the window title and the OSD are constructed at
    /// startup. Swapping the labels in place therefore leaves half the shell in the old language, which is worse than
    /// a short restart: the user sees a window that is half translated and has no way to finish the job.
    /// </para>
    /// <para>
    /// The request is a delegate rather than a <c>Process.Start</c> here, so the view model stays free of process
    /// handling and a test can assert that the request was made without spawning anything. It is null in a test and
    /// in any host that cannot restart, and the status line still tells the user what happened.
    /// </para>
    /// </remarks>
    private async Task RestartForLanguageAsync()
    {
        if (RestartRequest == null) return;

        try
        {
            await RestartRequest().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AppLog.Warn(Loc.T("Yeniden başlatma isteği gönderilemedi"), ex.Message);
        }
    }

    /// <summary>
    /// Restarts the process. Set by the shell once the window exists; null in a test, where a restart would be a
    /// process the suite cannot see.
    /// </summary>
    public Func<Task>? RestartRequest { get; set; }

    /// <summary>
    /// The restart the last language change asked for.
    /// </summary>
    /// <remarks>
    /// The setter cannot await, so the handover runs in the background. Keeping the task here means a test can await
    /// it instead of sleeping, and it is the one place that proves a failing restart is swallowed rather than left to
    /// fault an unobserved task.
    /// </remarks>
    public Task RestartTask { get; private set; } = Task.CompletedTask;

    // ---------------------------------------------------------------- theme

    /// <summary>Every palette <see cref="ThemeCatalog"/> ships, in the order the picker offers them.</summary>
    public ObservableCollection<ThemeChoice> Themes { get; } = new();

    /// <summary>
    /// The chosen palette. Applying it is immediate — the window repaints from the swapped dictionary — and the choice
    /// is written to the settings file so the next launch starts in it.
    /// </summary>
    public ThemeChoice? SelectedTheme
    {
        get => _selectedTheme;
        set
        {
            if (!Set(ref _selectedTheme, value) || value == null) return;
            _themeApplier?.Apply(value.Theme);
            _settings.Theme = value.Theme;
            SaveSettings();
            Status = Labels.ThemeApplied;
            RefreshThemeCards();
        }
    }

    /// <summary>
    /// The preview cards, one per palette <see cref="ThemeCatalog"/> ships. Built from the catalogue rather than
    /// written into the XAML, so adding a palette is still one enum member and one dictionary.
    /// </summary>
    public ObservableCollection<ThemeCardViewModel> ThemeCards { get; } = new();

    /// <summary>
    /// Applies a palette from its card. Routed through <see cref="SelectedTheme"/> rather than duplicating its work:
    /// that is the property the rest of the code reads, and its setter is what saves the choice and repaints.
    /// </summary>
    private void SelectThemeCard(ThemeCardViewModel card)
    {
        var match = Themes.FirstOrDefault(t => t.Theme == card.Theme);
        if (match == null) return;

        // Already chosen: nothing to apply, nothing to write. Clicking the current card is not an action.
        if (_selectedTheme?.Theme == card.Theme) return;

        SelectedTheme = match;
    }

    /// <summary>
    /// Rebuilds the cards in the current language, keeping the selection. The stored preference is what paints a card
    /// as chosen, not the resolved palette: with "System" the window is showing one of the concrete themes, but the
    /// card that has to look chosen is the System one.
    /// </summary>
    private void BuildThemeCards()
    {
        var previous = _selectedTheme?.Theme ?? _settings.Theme;

        ThemeCards.Clear();
        foreach (var theme in ThemeCatalog.All)
        {
            ThemeCards.Add(new ThemeCardViewModel(
                theme,
                Loc.T(ThemeCatalog.DisplayNameKey(theme)),
                ThemePalette.SwatchHexes(theme),
                SelectThemeCard));
        }

        RefreshThemeCards();
    }

    /// <summary>Moves the accent frame to the stored preference. Cheap enough to run on every selection change.</summary>
    private void RefreshThemeCards()
    {
        var chosen = _selectedTheme?.Theme ?? _settings.Theme;

        foreach (var card in ThemeCards)
        {
            card.IsSelected = card.Theme == chosen;
        }
    }

    /// <summary>
    /// Rebuilds the picker in the current language, keeping the selection. The field is assigned directly rather than
    /// through the setter: rebuilding the list is not a user action, and going through the setter would write the
    /// settings file and re-apply the palette on every language change.
    /// </summary>
    private void BuildThemeChoices()
    {
        var previous = _selectedTheme?.Theme ?? _settings.Theme;

        Themes.Clear();
        foreach (var theme in ThemeCatalog.All)
        {
            Themes.Add(new ThemeChoice(theme, Loc.T(ThemeCatalog.DisplayNameKey(theme))));
        }

        _selectedTheme = Themes.FirstOrDefault(t => t.Theme == previous) ?? Themes.FirstOrDefault();
        Raise(nameof(SelectedTheme));

        // The cards carry the same names, so they are rebuilt with the picker rather than left in the old language.
        BuildThemeCards();
    }

    /// <summary>
    /// The typefaces the settings page offers. Built from <see cref="UiFontCatalog"/> rather than written into the
    /// XAML, so adding a face is one entry in the catalogue.
    /// </summary>
    /// <remarks>
    /// A face drawn for one script is offered only to a reader of that script, and the machine's own display language
    /// counts as well as the interface language: a Hindi Windows with the interface in English is still a Hindi
    /// reader. The face already in effect is always in the list, because a picker whose selected item is missing from
    /// its items draws blank.
    /// </remarks>
    public IReadOnlyList<UiFontChoice> UiFonts =>
        UiFontCatalog.VisibleFor(Loc.Language, SystemLanguage, _selectedUiFont?.Id);

    /// <summary>
    /// The machine's display language, read once. <see cref="Loc.DetectSystemLanguage"/> asks the current UI culture,
    /// which does not change while the process runs, and the font list is rebuilt on every language change.
    /// </summary>
    private string SystemLanguage => _systemLanguage ??= Loc.DetectSystemLanguage();

    /// <summary>
    /// The chosen typeface. Applying it is immediate — the window repaints from the swapped resource — and the choice
    /// is written to the settings file so the next launch starts in it.
    /// </summary>
    public UiFontChoice? SelectedUiFont
    {
        get => _selectedUiFont;
        set
        {
            if (!Set(ref _selectedUiFont, value) || value == null) return;
            _themeApplier?.ApplyFont(value.Stack);
            _settings.UiFont = value.Id;
            SaveSettings();
            Status = Labels.FontApplied;
        }
    }

    public string DeviceModel => _platform?.Identity.ModelCode is { Length: > 0 } code
        ? $"{_platform.Identity.Model} ({code})"
        : _platform?.Identity.Model ?? "";

    /// <summary>
    /// False when the probe could not name the machine. The header hides the badge rather than showing an empty pill,
    /// which is what a machine with no DMI answer would otherwise get.
    /// </summary>
    public bool HasDeviceName => !string.IsNullOrWhiteSpace(DeviceModel);

    public string DeviceBios => _platform?.Identity.BiosVersion ?? "";

    /// <summary>False when the probe could not read the BIOS version, so the row is dropped instead of left blank.</summary>
    public bool HasDeviceBios => !string.IsNullOrWhiteSpace(DeviceBios);

    /// <summary>The raw tier the probe reported: "acer-wmi", "linuwu-sense", "both" or "none".</summary>
    public string TierText => _platform?.Tier ?? "none";

    /// <summary>
    /// The same value in the user's language. "none" is a diagnostic string, not something to show a user, so the
    /// settings page binds this instead.
    /// </summary>
    public string TierDisplay => TierText switch
    {
        "acer-wmi" => Labels.TierAcerWmi,
        "linuwu-sense" => Labels.TierLinuwuSense,
        "both" => Labels.TierBoth,
        "wmi" => Labels.TierWmi,
        "aassvc" => Labels.TierAassvc,
        _ => Labels.TierNone
    };

    public string DataFolder => AppPaths.DataDirectory;

    /// <summary>
    /// What the probe could not read, in its own words.
    /// </summary>
    /// <remarks>
    /// Kept on the view model because it is the probe's answer and the hardware report quotes it, but no longer drawn:
    /// the settings page used to end with a "Notes" card that printed this, which was a developer's diagnostic in a
    /// user's window. The support card took that slot.
    /// </remarks>
    public IReadOnlyList<string> Notes => _platform?.Notes ?? Array.Empty<string>();

    public bool NeedsUdevRule
    {
        get => _needsUdevRule;
        private set => Set(ref _needsUdevRule, value);
    }

    public string UdevCommand
    {
        get => _udevCommand;
        private set => Set(ref _udevCommand, value);
    }

    /// <summary>One line per capability, with a tick or a cross. Built from the backend's own answer.</summary>
    public string CapabilitySummaryText
    {
        get
        {
            if (_hardware == null) return Labels.NoBackend;

            return string.Join(Environment.NewLine, CapabilityRows.Select(r => $"{r.Glyph}  {r.Label}"));
        }
    }

    /// <summary>
    /// The same list as <see cref="CapabilitySummaryText"/>, one row per capability, so the settings page can draw a
    /// tick or a cross in the theme's own colours instead of parsing a block of text.
    /// </summary>
    public ObservableCollection<CapabilityRow> CapabilityRows { get; } = new();

    private void BuildCapabilityRows()
    {
        CapabilityRows.Clear();
        if (_hardware == null) return;

        CapabilityRows.Add(new CapabilityRow(Labels.CapFanControl, _capabilities.FanControl));
        CapabilityRows.Add(new CapabilityRow(Labels.CapOperatingMode, _capabilities.OperatingModeControl));
        CapabilityRows.Add(new CapabilityRow(Labels.CapMux, _capabilities.MuxSwitch));
        CapabilityRows.Add(new CapabilityRow(Labels.CapBatteryLimit, _capabilities.BatteryChargeLimit));
        CapabilityRows.Add(new CapabilityRow(Labels.CapKeyboard, _capabilities.KeyboardLighting));
        CapabilityRows.Add(new CapabilityRow(Labels.CapBacklightTimeout, _capabilities.BacklightTimeout));
    }

    // ---------------------------------------------------------------- commands

    public AsyncCommand ApplyFanModeCommand { get; }
    public AsyncCommand ApplyCurveCommand { get; }
    public AsyncCommand ApplyLightingCommand { get; }
    public AsyncCommand TurnOffLightingCommand { get; }
    public RelayCommand CopyUdevCommand { get; }
    public AsyncCommand RescanCommand { get; }
    public AsyncCommand ApplyGpuModeCommand { get; }
    public AsyncCommand ApplyRgbProfileCommand { get; }
    public RelayCommand SaveRgbProfileCommand { get; }
    public RelayCommand DeleteRgbProfileCommand { get; }
    public RelayCommand CopyHardwareReportCommand { get; }

    // ---------------------------------------------------------------- lifecycle

    /// <summary>
    /// Reads the machine's current state. Called once when the window opens; every step is skipped rather than forced
    /// when the backend does not answer, so a machine with only tier 1 still gets a usable window.
    /// </summary>
    /// <summary>
    /// Tells the user that the settings file could not be read and the app started from the backup or from defaults.
    /// </summary>
    /// <remarks>
    /// Called by the composition root, which is the only place that sees the load result. It is a method rather than
    /// a constructor parameter because the constructor already carries every platform service, and this is a
    /// one-shot message rather than a dependency. The message is held until <see cref="InitializeAsync"/> because the
    /// window subscribes to <see cref="Status"/> when it opens, and a banner raised before that is never shown.
    /// </remarks>
    public void ReportSettingsRecovered(SettingsLoadSource source) => _settingsRecovery = source;

    public async Task InitializeAsync()
    {
        if (!_initialized.TryEnter()) return;

        // Raised first, so the banner is the first thing the window has to say: the app is running on the backup or
        // on defaults, and every preference the user set may be gone.
        if (_settingsRecovery is SettingsLoadSource recovered)
        {
            _settingsRecovery = null;
            Status = recovered == SettingsLoadSource.Defaults
                ? Labels.SettingsResetToDefaults
                : Labels.SettingsRestoredFromBackup;
        }

        // The probe runs on the thread pool from the composition root, so the window is already open by the time this
        // is reached. Awaiting it here is what keeps the first hardware read off the UI thread while it is still
        // going: a machine with a damaged WMI repository answers the probe in tens of seconds, and the old code paid
        // that on the UI thread. A failed probe is not a reason to leave the window read-only, so the exception is
        // logged and the controls are released anyway.
        if (_probeTask != null)
        {
            try
            {
                await _probeTask.ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                AppLog.Warn(Loc.T("Donanım yetenekleri okunamadı"), ex.Message);
            }
        }

        // The window can be closed while the probe is still running: a fast open and close on a machine whose WMI
        // repository is damaged is exactly that case, and the probe is the one thing that can take tens of seconds.
        // Everything below touches services the composition root disposes with the window, so the continuation stops
        // here rather than running against them.
        if (_disposed) return;

        _hardwareReady = true;
        Raise(nameof(IsHardwareBusy));
        Raise(nameof(IsHardwareReady));

        // The mode cards are filtered by the power source, so the source has to be read before the list is built: a
        // window opened on battery would otherwise offer Turbo and hide Eco until the first telemetry tick.
        ReadPowerState();
        RefreshModeVisibility();

        // School mode's undo record is checked before anything else: a run that died between writing the record and
        // saving the setting leaves the machine at 60 % processor and a dim screen with nothing in the UI to say so.
        if (_schoolMode != null)
        {
            try
            {
                if (await _schoolMode.RecoverOrphanedBackupAsync().ConfigureAwait(true))
                {
                    _schoolModeOn = false;
                    Raise(nameof(SchoolModeOn));
                    SchoolModeDetail = Labels.SchoolModeRecovered;
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn(Loc.T("Okul modu kurtarma başarısız"), ex.Message);
            }
        }

        // Read before the hardware guard: the sign-in entry is a Windows task, not a hardware capability, and a
        // machine with no Acer nodes still has one.
        await RefreshAutoStartAsync().ConfigureAwait(true);

        // A game session that is still marked active means the process died while a game was running. The mode and
        // fan the game overrode have to be put back, or the machine boots into Turbo with nothing on screen to say why.
        await RecoverGameSessionAsync().ConfigureAwait(true);

        // The mouse answers on its own schedule, so the rail's FARE row is filled in after the first read rather than
        // blocking the window on a USB control transfer.
        await RefreshMouseProfileAsync().ConfigureAwait(true);

        if (_hardware == null)
        {
            // No backend is not a reason to leave the battery, the services and the walkthrough unread: a machine whose
            // Acer nodes are missing still has a battery, and a first run still deserves the walkthrough.
            await InitializePhase3Async().ConfigureAwait(true);
            _suppressWrite = false;
            return;
        }

        try
        {
            await _hardware.EnsureConnectedAsync().ConfigureAwait(true);
            _capabilities = await _hardware.GetCapabilitiesAsync().ConfigureAwait(true);

            BuildModeChoices();
            Raise(nameof(ModeControlAvailable));
            Raise(nameof(HasModeLockNote));
            Raise(nameof(FanControlAvailable));
            Raise(nameof(LightingAvailable));
            Raise(nameof(CoolBoostAvailable));
            Raise(nameof(LcdOverdriveAvailable));
            Raise(nameof(BacklightTimeoutAvailable));
            Raise(nameof(WinKeyLockAvailable));
            Raise(nameof(HasAnyExtra));
            Raise(nameof(NoExtrasAvailable));
            Raise(nameof(ShowExtrasCard));
            Raise(nameof(BatteryLimitAvailable));
            Raise(nameof(MuxAvailable));
            Raise(nameof(SchoolModeColumnSpan));
            Raise(nameof(CapabilitySummaryText));
            Raise(nameof(TierDisplay));
            BuildCapabilityRows();

            await ReadStateAsync().ConfigureAwait(true);

            // The user turned the lights off before the last exit. The keyboard's own firmware may have brought the
            // last profile back on boot, so the stored off state is put back here rather than left to the next time
            // the window comes forward.
            if (_settings.IsBacklightOff) await ReassertKeyboardLightingAsync().ConfigureAwait(true);

            // A Linux machine whose kernel exposes no Acer node is not an error and not a crash: the window stays
            // usable, the banner explains it, and every control that needs hardware stays hidden.
            if (!_hardware.IsAvailable)
            {
                IsReady = false;
                Status = Labels.NoAcerNodes;
                Raise(nameof(HasBackend));
                Raise(nameof(HasNoBackend));
                Raise(nameof(ShowNoBackendBanner));
                Raise(nameof(NoBackendDetail));
                return;
            }

            IsReady = true;
        }
        catch (Exception ex)
        {
            AppLog.Error(Loc.T("Linux arayüzü başlatılamadı"), ex.ToString());
            Status = Labels.NoAcerNodes;
        }
        finally
        {
            // The battery, the services and the walkthrough are read after the probe, because the walkthrough's first
            // step lists what the probe found. This sits in the finally rather than at the end of the try block so it
            // also runs on the two early returns above: a machine with no Acer nodes still has a battery and still
            // deserves the walkthrough.
            //
            // Skipped when the window was closed while the probe was still running: the services this reads are
            // disposed with the window, and a continuation that outlived it has nothing left to initialise.
            if (!_disposed)
            {
                await InitializePhase3Async().ConfigureAwait(true);

                // Whatever happened above, the controls must work from now on: a failed initial read is not a reason to
                // leave the window read-only for the rest of the session.
                _suppressWrite = false;

                // The machine may already be in custom mode when the window opens, in which case the fan loop has to be
                // running before the first telemetry sample rather than after the user touches a card.
                SyncFanLoop();
            }
        }
    }

    /// <summary>
    /// One telemetry sample. The fan curve is not stepped here: it has its own clock, so hiding the window to play a
    /// game does not slow it to the tray's five-second rate. The guard keeps a slow read from stacking up behind the
    /// next tick.
    /// </summary>
    public async Task RefreshTelemetryAsync()
    {
        if (_refreshing || _disposed) return;

        _refreshing = true;
        try
        {
            if (_hardware == null)
            {
                // No backend is not a machine with nothing to show: the power source and the battery are read from the
                // platform, and the header's figures come from them. Returning here would leave the battery card frozen
                // at whatever the first read found.
                ReadPowerState();
                await TickPhase3Async().ConfigureAwait(true);
                return;
            }

            var data = await _hardware.GetMonitorDataAsync().ConfigureAwait(true);

            // A backend that did not answer is not a machine with nothing to show. The platform monitor can still
            // report load, clocks and memory, which is what keeps the page useful on a machine whose Acer service is
            // missing; the temperatures stay at zero, and zero is what the page reads as "no reading".
            data ??= _monitor?.CreateFallback();
            if (data == null) return;

            // The backend answers sensors; the model strings, load, package watts, VRAM and RAM come from the platform
            // monitor. Enriching here rather than in the backend keeps the Acer transport free of OS-specific reads.
            _monitor?.Enrich(data);

            CpuTemperature = data.CpuTemperature;
            GpuTemperature = data.GpuTemperature;
            SysTemperature = data.SysTemperature;
            CpuFanRpm = data.CpuFanSpeed;
            GpuFanRpm = data.GpuFanSpeed;
            FanRpmMeasured = data.IsFanRpmMeasured;
            Raise(nameof(CurvePreview));

            CpuCores = data.CpuCores > 0 ? data.CpuCores : _monitor?.Info.CpuThreads ?? 0;
            CpuUsage = data.CpuUsage;
            CpuPowerWatts = data.CpuPowerWatts;
            CpuFrequencyMhz = data.CpuFrequencyMHz;
            GpuUsage = data.GpuUsage;
            GpuPowerWatts = data.GpuPowerWatts;
            GpuFrequencyMhz = data.GpuFrequencyMHz;
            VramUsedMb = data.VramUsedMB;
            VramTotalMb = data.VramTotalMB;
            RamUsage = data.RamUsage;
            RamTotalMb = data.RamTotalMB;

            // The overlay is a second window bound to this view model, so its rows are raised on the same tick the
            // page's figures are. A separate timer would let the two disagree by a second.
            Raise(nameof(OsdCpuLine));
            Raise(nameof(OsdGpuLine));
            Raise(nameof(OsdRamLine));

            RecordHistory(data);

            // The fan curve is not stepped here. It has its own clock (see FanCurveLoop) so that hiding the window to
            // play a game does not slow it to the tray's five-second telemetry rate. What runs here is the stall
            // watchdog, which needs the same sample the page is showing.
            ObserveFanStall(data);

            // Game detection runs on the same tick as telemetry, every other second: a /proc scan is cheap but not
            // free, and a game that starts is not going to be missed by a one-second delay.
            if (++_tick % 2 == 0) await PollGameAsync().ConfigureAwait(true);

            // The refresh-rate policy is transition-driven, so after the first call this is a boolean comparison. It
            // runs on every tick because a replug is not something to notice two seconds late.
            ApplyRefreshRate(force: false);

            // The header's battery figure comes from the same reader the policy uses, so the two can never disagree
            // about whether the charger is plugged in.
            ReadPowerState();

            // The battery page's slow half: the capacities come from WMI, which is far too expensive for a two-second
            // tick, so it runs once a minute and whenever the charger state changes.
            await TickPhase3Async().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AppLog.Warn(Loc.T("Telemetri okunamadı"), ex.Message);
        }
        finally
        {
            _refreshing = false;
        }
    }

    /// <summary>
    /// Feeds the stall watchdog one sample and acts on a confirmed fault.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only a reading the Acer service produced is fed in. The fallback path reports 0 RPM because nothing measured
    /// the fans, and judging that would fire on every machine without the service.
    /// </para>
    /// <para>
    /// The switch is what decides whether the app acts. With it off the fault is still logged, because a user reading
    /// the log after a thermal shutdown deserves to know the fan had stopped, but nothing is written to the hardware:
    /// dropping the machine into Eco is a visible change and it is not one to make on a setting the user left alone.
    /// </para>
    /// </remarks>
    private void ObserveFanStall(MonitorData data)
    {
        if (!data.IsFanRpmMeasured)
        {
            // Nothing measured the fans, so there is nothing to judge. The watchdog is reset rather than left holding
            // a timer that a later real sample would complete against a gap it never saw.
            _fanStall.Reset();
            _fanStallReported = false;
            return;
        }

        var verdict = _fanStall.Observe(
            data.CpuTemperature, data.GpuTemperature, data.CpuFanSpeed, data.GpuFanSpeed, DateTime.UtcNow);

        if (verdict == null) return;

        AppLog.Error(Loc.T("Fan durmuş görünüyor"),
            $"{verdict.Target} · {verdict.TemperatureC} °C · {verdict.FanRpm} RPM · {verdict.Sustained.TotalSeconds:F0} sn");

        if (_fanStallReported) return;
        _fanStallReported = true;

        Status = Labels.FanStallDetected;

        if (!FanProtectionOn) return;

        // The protective mode is the coolest one the firmware offers. Eco is the battery choice and is not always
        // advertised, so Quiet is the fallback: both drop the package power, which is the only lever left when a fan
        // has stopped.
        var protective = Modes.FirstOrDefault(m => m.Mode == OperatingMode.Eco)
                         ?? Modes.FirstOrDefault(m => m.Mode == OperatingMode.Quiet);

        if (protective == null) return;

        SyncControls(() => SelectedMode = protective);
        _ = ApplyModeAsync(protective.Mode);
    }

    /// <summary>
    /// One step of the fan loop. The curve drives the fans when it is armed; otherwise the manual sliders do, raised
    /// to the thermal floor for the current temperature. Both write through the same gate and both record only what
    /// the hardware accepted, so a refused write is proposed again on the next step instead of being remembered.
    /// </summary>
    /// <remarks>
    /// Runs on the fan loop's background thread, which is why nothing here touches a control: the properties it reads
    /// are plain fields, and the only thing it raises is a log line.
    /// </remarks>
    private async Task StepFanAsync()
    {
        if (_hardware == null || _disposed) return;
        if (SelectedFanMode?.Mode != FanMode.Custom) return;

        // A game's own fan rule owns the fans while it runs; writing here would fight it.
        if (GameActive) return;

        int temperature = CpuTemperature;
        if (temperature <= 0) return;

        var now = DateTime.UtcNow;

        if (CurveEnabled)
        {
            // Captured once: the UI thread can replace the evaluator when the curve is edited, and a step that read
            // the field twice could evaluate against one curve and commit to another.
            var evaluator = _curveEvaluator;
            if (evaluator.Evaluate(temperature, now) is not int percent) return;

            await WriteFanPercentAsync(percent, () => evaluator.Commit(percent, temperature, now)).ConfigureAwait(false);
            return;
        }

        var floor = _manualFanFloor;
        if (floor.Evaluate(CustomCpuPercent, temperature) is not int manual) return;

        await WriteFanPercentAsync(manual, () => floor.Commit(manual, temperature)).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes one fixed speed to both fans and records it only when the firmware accepted it. The commit is a
    /// callback rather than a value because the two callers track different state.
    /// </summary>
    private async Task WriteFanPercentAsync(int percent, Action commit)
    {
        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            bool ok = await _hardware!.SetFanModeAsync(FanMode.Custom, percent, percent).ConfigureAwait(false);
            if (ok)
            {
                commit();
                return;
            }

            // Not committed: the next step proposes the same speed again, which is what makes a transient refusal
            // recover instead of leaving the fans at the previous value until the temperature happens to move.
            AppLog.Warn(Loc.T("Fan hızı uygulanmadı"), $"%{percent}");
        }
        catch (Exception ex)
        {
            AppLog.Warn(Loc.T("Fan hızı yazılamadı"), ex.Message);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    // ---------------------------------------------------------------- reads

    private async Task ReadStateAsync()
    {
        // The previous value is restored rather than cleared: this runs while the window is still filling itself in,
        // and a read that finished after the first one would otherwise switch writes back on underneath it.
        bool previous = _suppressWrite;
        _suppressWrite = true;
        try
        {
            var mode = await _hardware!.GetOperatingModeAsync().ConfigureAwait(true);
            if (mode is OperatingMode current)
                SelectedMode = Modes.FirstOrDefault(m => m.Mode == current) ?? SelectedMode;

            var fanMode = await _hardware.GetFanModeAsync().ConfigureAwait(true);
            if (fanMode is FanMode fan)
                SelectedFanMode = FanModes.FirstOrDefault(f => f.Mode == fan) ?? SelectedFanMode;

            var lighting = await _hardware.GetLightingConfigAsync().ConfigureAwait(true);
            if (lighting != null) ApplyLightingState(lighting);

            // The stored off state wins over the keyboard's own reading. The firmware restores the last profile on
            // some machines, and a page that showed it would be describing a state the user turned off.
            if (_settings.IsBacklightOff) ApplyLightingState(OffLightingConfig());

            var coolBoost = await _hardware.GetCoolBoostAsync().ConfigureAwait(true);
            if (coolBoost is bool cool) CoolBoostOn = cool;

            var overdrive = await _hardware.GetLcdOverdriveAsync().ConfigureAwait(true);
            if (overdrive is bool lcd) LcdOverdriveOn = lcd;

            var timeout = await _hardware.GetBacklightTimeoutAsync().ConfigureAwait(true);
            if (timeout is bool backlight) BacklightTimeoutOn = backlight;

            var charge = await _hardware.GetBatteryChargeModeAsync().ConfigureAwait(true);
            if (charge is BatteryChargeMode battery) BatteryLimitOn = battery == BatteryChargeMode.Optimized80;

            var gpu = await _hardware.GetGpuModeAsync().ConfigureAwait(true);
            if (gpu is GpuOperatingMode gpuMode)
                SelectedGpuMode = GpuModes.FirstOrDefault(m => m.Mode == gpuMode) ?? SelectedGpuMode;
        }
        finally
        {
            _suppressWrite = previous;
        }
    }

    private void ApplyLightingState(LightingConfig lighting)
    {
        SelectedEffect = Effects.FirstOrDefault(e => string.Equals(e.Id, lighting.Effect, StringComparison.OrdinalIgnoreCase)) ?? SelectedEffect;
        BrightnessLevel = lighting.Brightness;
        SpeedLevel = lighting.Speed;
        DirectionLevel = lighting.Direction;
        PrimaryColor = lighting.Color;

        if (lighting.LEDs.Count > 0) Zone1 = lighting.LEDs[0];
        if (lighting.LEDs.Count > 1) Zone2 = lighting.LEDs[1];
        if (lighting.LEDs.Count > 2) Zone3 = lighting.LEDs[2];
        if (lighting.LEDs.Count > 3) Zone4 = lighting.LEDs[3];
    }

    // ---------------------------------------------------------------- sign-in launch

    /// <summary>
    /// Registers or removes the sign-in entry, then re-reads the real state. The re-read is the point: a UAC prompt the
    /// user cancelled leaves the entry absent, and a switch that stayed on would be lying about the machine.
    /// </summary>
    private async Task ApplyAutoStartAsync(bool enable)
    {
        if (_autoStart == null) return;

        try
        {
            bool ok = enable
                ? await _autoStart.EnableAsync().ConfigureAwait(true)
                : await _autoStart.DisableAsync().ConfigureAwait(true);

            AutoStartStatus = ok
                ? (enable ? Labels.AutoStartEnabled : Labels.AutoStartDisabled)
                : Labels.AutoStartFailed;

            await RefreshAutoStartAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AppLog.Warn(Loc.T("Otomatik başlatma değiştirilemedi"), ex.ToString());
            AutoStartStatus = Labels.AutoStartFailed;
        }
    }

    /// <summary>
    /// Runs the battery refresh-rate policy for the current power state. Silent when the caller wired no automation,
    /// no power state, or the platform cannot change the rate.
    /// </summary>
    private void ApplyRefreshRate(bool force)
    {
        if (_refreshRate == null || _powerState == null) return;

        try
        {
            var (onAc, _) = _powerState();
            var result = _refreshRate.Apply(onAc, force);

            RefreshRateStatus = result.Action switch
            {
                RefreshRateAction.LoweredToBattery => Labels.RefreshRateLowered,
                RefreshRateAction.RestoredToMaximum => Labels.RefreshRateRestored,
                RefreshRateAction.Failed => Labels.RefreshRateFailed,
                _ => RefreshRateStatus
            };

            if (result.Changed) AppLog.Info(Loc.T("Ekran yenileme hızı değişti"), $"{result.From} Hz -> {result.To} Hz");
        }
        catch (Exception ex)
        {
            AppLog.Warn(Loc.T("Ekran yenileme hızı değiştirilemedi"), ex.Message);
        }
    }

    /// <summary>Reads the entry's real state without writing anything.</summary>
    private async Task RefreshAutoStartAsync()
    {
        if (_autoStart == null) return;

        try
        {
            bool enabled = await _autoStart.IsEnabledAsync().ConfigureAwait(true);

            SyncControls(() => AutoStartOn = enabled);
        }
        catch (Exception ex)
        {
            AppLog.Warn(Loc.T("Otomatik başlatma durumu okunamadı"), ex.Message);
        }
    }

    // ---------------------------------------------------------------- writes

    private async Task ApplyModeAsync(OperatingMode? mode)
    {
        if (_hardware == null || mode is not OperatingMode target) return;

        await RunWriteAsync(async () =>
        {
            bool ok = await _hardware.SetOperatingModeAsync(target).ConfigureAwait(true);
            if (ok) _settings.LastOperatingMode = target;
            return ok;
        }, Labels.OperatingMode).ConfigureAwait(true);
    }

    private async Task ApplyFanModeAsync()
    {
        if (_hardware == null || SelectedFanMode is not FanModeChoice choice) return;

        // A fixed speed goes through the thermal floor even when the user picked it from the mode card rather than
        // from the slider: the floor is a property of the temperature, not of which control asked for the write.
        var (cpu, gpu) = choice.Mode == FanMode.Custom ? EffectiveCustomPercents() : (CustomCpuPercent, CustomGpuPercent);

        await RunWriteAsync(
            () => _hardware.SetFanModeAsync(choice.Mode, cpu, gpu),
            Labels.FanMode).ConfigureAwait(true);
    }

    /// <summary>Sanitises the sliders into a curve, stores it and arms it.</summary>
    private async Task ApplyCurveAsync()
    {
        var points = FanCurve.Sanitize(CurveAnchors.Select(a => new FanCurvePoint(a.TemperatureC, a.Percent)));
        _settings.FanCurvePoints = points;
        SaveSettings();

        _curveEvaluator = new FanCurveEvaluator(_settings.BuildFanCurve());
        Raise(nameof(CurvePreview));

        if (_hardware == null) return;

        // The curve only drives anything in custom mode, so applying it also selects that mode.
        SelectedFanMode = FanModes.FirstOrDefault(f => f.Mode == FanMode.Custom) ?? SelectedFanMode;

        // The curve is what the user just edited, so the speed written is the curve's own answer at the current
        // temperature. Writing the manual slider here put the fans at a speed the curve never asked for and left them
        // there until the next step, which is the mismatch this method used to have.
        int temperature = CpuTemperature;
        var evaluator = _curveEvaluator;
        var (cpu, gpu) = CurveEnabled && temperature > 0
            ? (evaluator.Curve.PercentAt(temperature), evaluator.Curve.PercentAt(temperature))
            : EffectiveCustomPercents();

        var now = DateTime.UtcNow;
        bool ok = await RunWriteAsync(
            () => _hardware.SetFanModeAsync(FanMode.Custom, cpu, gpu),
            Labels.FanCurve).ConfigureAwait(true);

        // Committed only when the firmware accepted it, so the loop does not immediately write the same speed again
        // and a refused write is proposed again on the next step.
        if (ok && CurveEnabled && temperature > 0) evaluator.Commit(cpu, temperature, now);
    }

    private async Task ApplyLightingAsync()
    {
        if (_hardware == null || SelectedEffect == null) return;

        var config = CurrentLightingConfig();
        var zones = config.LEDs!.ToArray();

        await _writeGate.WaitAsync().ConfigureAwait(true);
        try
        {
            bool ok = await _hardware.SetLightingAsync(config.Effect, config.Brightness, config.Speed,
                config.Direction, config.Color, zones).ConfigureAwait(true);

            // No banner on success: the keyboard is the confirmation, and a toast on every slider step would cover
            // the page the user is working on. A refusal is still reported, because a silent one looks like a bug.
            if (!ok)
            {
                AppLog.Warn(Loc.T("Klavye aydınlatması"), "Donanım yazımı uygulanmadı.");
                Status = $"{Labels.WriteFailed}: {Labels.KeyboardLighting}";
                return;
            }

            // The recovery copy is written only after the firmware accepted the write, so a resume puts back the
            // lighting the keyboard actually had rather than the one the user last clicked.
            ActiveRgbStateStore.Save(_settingsPath, config);
            _settings.KeyboardLighting = config;

            // A brightness of zero is the same state as the "turn off" button, so it has to be stored the same way:
            // otherwise the next launch reads a lit configuration and puts the lights back on.
            _settings.IsBacklightOff = LightingPolicy.IsOff(config);

            // The last lit configuration is what raising the brightness after "off" restores, so an off write must
            // not overwrite it with black.
            if (!_settings.IsBacklightOff) _settings.LastActiveLighting = config;
            SaveSettings();
        }
        catch (Exception ex)
        {
            AppLog.Warn(Loc.T("Donanım yazımı başarısız"), $"{Labels.KeyboardLighting}: {ex.Message}");
            Status = $"{Labels.WriteFailed}: {Labels.KeyboardLighting}";
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task TurnOffLightingAsync()
    {
        if (_hardware == null) return;

        // Keep what was lit so raising the brightness or picking an effect can bring it back later.
        var current = CurrentLightingConfig();
        if (!LightingPolicy.IsOff(current)) _settings.LastActiveLighting = current;

        var off = OffLightingConfig();

        // Same rule as the rest of the page: turning the lights off is visible on the keyboard, so it does not need
        // a banner. A refusal still reaches the log.
        bool ok = await RunQuietWriteAsync(() => _hardware.TurnOffLightingAsync(), Labels.KeyboardLighting).ConfigureAwait(true);
        if (!ok) return;

        // The page shows the state the keyboard is in, so the controls go to "off" as well. Without this the effect
        // list and the brightness slider kept describing a configuration the keyboard was not running.
        SyncControls(() => ApplyLightingState(off));

        // "Off" is a preference, not a moment. It is written to the settings file so the next launch knows the
        // keyboard is meant to stay dark, and the recovery copy is overwritten with the same state: leaving the last
        // lit configuration there is what made the re-assert after a resume turn the lights back on.
        _settings.IsBacklightOff = true;
        _settings.KeyboardLighting = off;
        ActiveRgbStateStore.Save(_settingsPath, off);
        SaveSettings();
    }

    /// <summary>The lighting the page's controls describe, in the shape the backend takes.</summary>
    private LightingConfig CurrentLightingConfig()
    {
        var zones = new[] { Zone1, Zone2, Zone3, Zone4 };
        return new LightingConfig
        {
            Effect = SelectedEffect?.Id ?? "STATIC",
            Brightness = BrightnessLevel,
            Speed = SpeedLevel,
            Direction = DirectionLevel,
            Color = LightingPolicy.NormalizeHex(PrimaryColor, LightingPolicy.DefaultColor),
            LEDs = zones.Select(z => LightingPolicy.NormalizeHex(z, LightingPolicy.DefaultColor)).ToList()
        };
    }

    /// <summary>
    /// Brings the last lit configuration back when a control is changed while the lights are off, so the change is
    /// visible instead of being written with brightness zero. <paramref name="edit"/> is applied to the resumed
    /// configuration, which is then pushed back into the controls.
    /// </summary>
    /// <remarks>
    /// The page shows "off" after a restart with the lights off, and the firmware reads an OFF effect as "stay off"
    /// whatever the brightness byte says. Without this, raising the brightness or picking an effect from that state
    /// wrote a command the keyboard ignored, and the lights could only be brought back by picking a different effect
    /// first.
    /// </remarks>
    private void ResumeFromOffForEdit(Action<LightingConfig> edit)
    {
        if (!LightingPolicy.IsOff(CurrentLightingConfig())) return;

        var resumed = LightingPolicy.ResumeFromOff(_settings.LastActiveLighting);
        edit(resumed);
        SyncControls(() => ApplyLightingState(resumed));
    }

    /// <summary>
    /// The configuration that means "the lights are off": the effect the firmware reads as off, brightness zero and
    /// black zones. Built in one place so the button, the brightness slider and the startup re-assert cannot disagree
    /// about what "off" is.
    /// </summary>
    private static LightingConfig OffLightingConfig() => new()
    {
        Effect = "OFF",
        Brightness = 0,
        Speed = LightingLimits.MinSpeed,
        Direction = 1,
        Color = "#000000",
        LEDs = new List<string> { "#000000", "#000000", "#000000", "#000000" }
    };

    // ---------------------------------------------------------------- MUX, school mode, profiles, report

    private async Task ApplyGpuModeAsync()
    {
        if (_hardware == null || SelectedGpuMode is not GpuModeChoice choice) return;

        bool ok = await RunWriteAsync(() => _hardware.SetGpuModeAsync(choice.Mode), Labels.GpuMode).ConfigureAwait(true);

        // A MUX write is staged, never applied: the firmware reads the setting on the next boot. The note is what
        // makes the status line true, and a refusal has to clear the badge rather than leave a restart warning for a
        // switch that was never written.
        if (!ok)
        {
            PendingGpuMode = null;
            GpuModeNote = choice.Mode == GpuOperatingMode.Automatic ? Labels.GpuAutomaticNotWritable : "";
            return;
        }

        PendingGpuMode = choice.Mode;
        GpuModeNote = choice.Mode switch
        {
            GpuOperatingMode.DiscreteGpu => Labels.GpuPendingDiscrete,
            GpuOperatingMode.Optimus => Labels.GpuPendingHybrid,
            GpuOperatingMode.Automatic => Labels.GpuPendingAutomatic,
            _ => ""
        };

        // The write landed, but the firmware only reads it at POST. "Uygulandı" would be a claim the machine has not
        // made yet, and it is the one the user is left to reconcile with NitroSense asking for a restart.
        Status = Labels.GpuModeStaged;
    }

    /// <summary>
    /// Applies or undoes school mode. The state machine writes its undo record before the first change, so a refusal
    /// here means nothing was touched and the switch has to go back off.
    /// </summary>
    private async Task SetSchoolModeAsync(bool enable)
    {
        if (_schoolMode == null) return;

        await _writeGate.WaitAsync().ConfigureAwait(true);
        try
        {
            var outcome = enable
                ? await _schoolMode.EnableAsync().ConfigureAwait(true)
                : await _schoolMode.DisableAsync().ConfigureAwait(true);

            SchoolModeDetail = DescribeOutcome(outcome);

            if (outcome.Refused)
            {
                // Nothing was changed, so the switch must not stay on: it would claim a state the machine is not in.
                _schoolModeOn = false;
                Raise(nameof(SchoolModeOn));
                Status = Labels.SchoolModeRefused;
                return;
            }

            Status = enable ? Labels.SchoolModeApplied : Labels.SchoolModeDisabled;

            // The state machine changed the mode and the fan behind the view model's back, so the controls are read
            // back instead of being assumed.
            await ReadStateAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AppLog.Warn(Loc.T("Okul modu değiştirilemedi"), ex.Message);
            Status = Labels.SchoolModeRefused;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>One line describing what school mode managed to change.</summary>
    private string DescribeOutcome(SchoolModeOutcome outcome)
    {
        if (outcome.Refused) return Labels.SchoolModeRefused;
        if (outcome.Failed.Count == 0) return Labels.SchoolModeApplied;
        return $"{Labels.SchoolModePartial}: {string.Join(", ", outcome.Failed)}";
    }

    /// <summary>Reloads the preset list, keeping the selection when the preset still exists.</summary>
    /// <remarks>
    /// The whole rebuild runs with the apply guard on, not just the final selection. A <c>ComboBox</c> selects the
    /// first item as its list is filled, and that selection is not the user picking a profile — without the guard it
    /// applied a preset over whatever the keyboard was running every time the list was reloaded.
    /// </remarks>
    private void RefreshRgbProfiles()
    {
        var previous = SelectedRgbProfile?.Name ?? _settings.SelectedRgbPreset;

        _suppressProfileApply = true;
        try
        {
            RgbProfiles.Clear();
            foreach (var preset in _rgbProfiles.GetPresets()) RgbProfiles.Add(preset);
            RgbProfiles.Add(CustomProfile);

            // Nothing matched means the editor is holding a colour no preset describes, which is what the custom
            // entry is for. Falling back to the first preset would claim a provenance the colours do not have.
            SelectedRgbProfile = RgbProfiles.FirstOrDefault(p => p.Name == previous) ?? CustomProfile;
        }
        finally
        {
            _suppressProfileApply = false;
        }

        Raise(nameof(RgbProfileLoadError));
    }

    /// <summary>
    /// Moves the selection without writing it. A reload is not the user picking a profile, and the card applies on
    /// selection now, so the two have to be told apart.
    /// </summary>
    private void SelectRgbProfileSilently(RgbPreset? preset)
    {
        _suppressProfileApply = true;
        try { SelectedRgbProfile = preset; }
        finally { _suppressProfileApply = false; }
    }

    /// <summary>
    /// Moves the profile list to the custom entry, without writing anything.
    /// </summary>
    /// <remarks>
    /// Called from every control that edits the lighting by hand. The guard is the same one the writes use: applying a
    /// preset fills those controls itself, and that is not the user editing anything.
    /// </remarks>
    private void MarkProfileCustom()
    {
        if (_suppressWrite || _suppressProfileApply) return;
        if (SelectedRgbProfile?.Name == CustomProfileName) return;

        SelectRgbProfileSilently(CustomProfile);
    }

    /// <summary>
    /// Loads a preset into the editor and writes it. The editor is filled first so the user can see what was applied
    /// and adjust it afterwards, which is the whole point of a saved profile.
    /// </summary>
    private async Task ApplyRgbProfileAsync()
    {
        if (SelectedRgbProfile is not RgbPreset preset) return;

        // The custom entry is a label, not a preset: selecting it says "these colours came from the picker", and
        // there is nothing to write.
        if (preset.Name == CustomProfileName) return;

        // The editor is filled with the write guard on: each of these setters is a write of its own, and a preset is
        // one write, not five. The single call below is the one that reaches the keyboard.
        SyncControls(() =>
        {
            SelectedEffect = Effects.FirstOrDefault(e => string.Equals(e.Id, preset.Effect, StringComparison.OrdinalIgnoreCase)) ?? SelectedEffect;
            BrightnessLevel = preset.Brightness;
            SpeedLevel = preset.Speed;
            DirectionLevel = preset.Direction;
            PrimaryColor = preset.Color;

            if (preset.LEDs.Count > 0) Zone1 = preset.LEDs[0];
            if (preset.LEDs.Count > 1) Zone2 = preset.LEDs[1];
            if (preset.LEDs.Count > 2) Zone3 = preset.LEDs[2];
            if (preset.LEDs.Count > 3) Zone4 = preset.LEDs[3];
        });

        await ApplyLightingAsync().ConfigureAwait(true);

        _settings.SelectedRgbPreset = preset.Name;
        SaveSettings();
        RgbProfileStatus = $"{Labels.Applied}: {preset.DisplayName}";
    }

    /// <summary>Saves the editor's current state under the typed name.</summary>
    private void SaveRgbProfile()
    {
        var name = NewProfileName?.Trim() ?? "";
        if (name.Length == 0)
        {
            RgbProfileStatus = Labels.ProfileNameRequired;
            return;
        }

        try
        {
            _rgbProfiles.SaveCustomPreset(
                name,
                SelectedEffect?.Id ?? "STATIC",
                BrightnessLevel,
                SpeedLevel,
                DirectionLevel,
                PrimaryColor,
                new List<string> { Zone1, Zone2, Zone3, Zone4 });

            NewProfileName = "";
            RefreshRgbProfiles();
            SelectedRgbProfile = RgbProfiles.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                                 ?? SelectedRgbProfile;
            RgbProfileStatus = $"{Labels.Applied}: {name}";
        }
        catch (Exception ex)
        {
            // The service already localises its refusals (a built-in name, a disk that will not take the write).
            RgbProfileStatus = ex.Message;
        }
    }

    private void DeleteRgbProfile()
    {
        if (SelectedRgbProfile is not RgbPreset preset) return;

        if (preset.IsBuiltIn)
        {
            RgbProfileStatus = Labels.BuiltInProfileLocked;
            return;
        }

        bool deleted = _rgbProfiles.DeletePreset(preset.Name);
        RefreshRgbProfiles();
        RgbProfileStatus = deleted ? $"{Labels.Deleted}: {preset.DisplayName}" : Labels.DeleteFailed;
    }

    private void CopyHardwareReport()
    {
        var report = HardwareReportText;
        if (string.IsNullOrEmpty(report))
        {
            Status = Labels.NoBackend;
            return;
        }

        ClipboardService.SetText(report);
        Status = Labels.ReportCopied;
    }

    // ---------------------------------------------------------------- game detection

    /// <summary>
    /// One detection pass, and the control updates that follow it. Public so a test can drive it without waiting for
    /// the telemetry timer.
    /// </summary>
    public async Task PollGameAsync()
    {
        if (_gameProfiles == null) return;

        var result = await _gameProfiles.PollAsync().ConfigureAwait(true);
        if (!result.IsTransition) return;

        if (result.Change == GameDetectionChange.Started && result.Profile is GameProfile started)
        {
            GameActive = true;
            _gameStatus = Labels.GameStarted(started.DisplayName);
            _activeGameName = started.DisplayName;
            Raise(nameof(SystemGameText));
            Raise(nameof(ShowSystemGameRow));

            // The record is written before the controls follow, so a crash between here and the game's exit still
            // leaves enough behind to undo the override on the next launch.
            BeginGameSessionRecovery(started);

            // The service already wrote the mode and the fan; the controls follow so the window does not show the
            // user's old selection while the machine is in the game's.
            SyncControls(() =>
            {
                SelectedMode = Modes.FirstOrDefault(m => m.Mode == started.TargetMode) ?? SelectedMode;
                var fan = started.TargetFanCurveEnabled ? FanMode.Custom : started.TargetFanMode;
                SelectedFanMode = FanModes.FirstOrDefault(f => f.Mode == fan) ?? SelectedFanMode;
            });

            // A game that brought its own curve replaces the shape the loop follows; one that did not keeps the
            // user's curve, which is what "follow the curve set on the fan card" means.
            var points = _gameProfiles.ActiveCurvePoints;
            _curveEvaluator = new FanCurveEvaluator(points != null ? new FanCurve(points) : _settings.BuildFanCurve());
            _curveEvaluator.Reset();
        }
        else if (result.Change == GameDetectionChange.Ended && result.Previous is GameProfile ended)
        {
            GameActive = false;
            _gameStatus = Labels.GameEnded(ended.DisplayName);
            _activeGameName = "";
            Raise(nameof(SystemGameText));
            Raise(nameof(ShowSystemGameRow));

            // The session ended cleanly, so there is nothing left to recover on the next launch.
            EndGameSessionRecovery();

            var snapshot = result.Snapshot;
            if (snapshot != null)
            {
                SyncControls(() =>
                {
                    SelectedMode = Modes.FirstOrDefault(m => m.Mode == snapshot.Mode) ?? SelectedMode;
                    SelectedFanMode = FanModes.FirstOrDefault(f => f.Mode == snapshot.Fan) ?? SelectedFanMode;
                    CustomCpuPercent = snapshot.CustomCpuPercent;
                    CustomGpuPercent = snapshot.CustomGpuPercent;
                });

                // The curve flag is restored without going through its setter: that setter saves the settings file and
                // re-applies the fan mode, and the service has just written the fan mode that has to survive.
                _curveEnabled = snapshot.CurveEnabled;
                Raise(nameof(CurveEnabled));
                _curveEvaluator = new FanCurveEvaluator(
                    snapshot.CurvePoints is { Count: > 0 } points ? new FanCurve(points) : _settings.BuildFanCurve());
                _curveEvaluator.Reset();
            }
        }

        Raise(nameof(GameStatusText));
        Status = _gameStatus;
    }

    /// <summary>
    /// Runs a control update without letting the setters write to the hardware. Used when the backend has already
    /// been changed by something else and the controls only have to catch up.
    /// </summary>
    private void SyncControls(Action update)
    {
        bool previous = _suppressWrite;
        _suppressWrite = true;
        try
        {
            update();
        }
        finally
        {
            _suppressWrite = previous;
        }
    }

    // ---------------------------------------------------------------- tray

    /// <summary>
    /// The quick modes the tray menu offers, in the order NitroSense lists them. The menu is built from this list so
    /// the two can never disagree about which modes exist.
    /// </summary>
    public IReadOnlyList<OperatingMode> QuickModes { get; } = new[]
    {
        OperatingMode.Quiet,
        OperatingMode.Default,
        OperatingMode.Extreme,
        OperatingMode.Turbo,
        OperatingMode.Eco
    };

    /// <summary>The label for a quick mode, translated.</summary>
    public string QuickModeLabel(OperatingMode mode) => ModeLabel(mode);

    /// <summary>
    /// Applies a mode from the tray. The same path as the mode card, so the battery rule for Turbo and Performance
    /// applies here too instead of the menu offering a click that can only be refused.
    /// </summary>
    public async Task ApplyModeFromTrayAsync(OperatingMode mode)
    {
        if (_hardware == null) return;

        SyncControls(() => SelectedMode = Modes.FirstOrDefault(m => m.Mode == mode) ?? SelectedMode);
        await ApplyModeAsync(mode).ConfigureAwait(true);
    }

    /// <summary>
    /// Applies a boost mode from the tray. The same path the pills use, so the tray and the card can never disagree
    /// about which boost mode the machine is holding.
    /// </summary>
    public async Task ApplyCpuBoostFromTrayAsync(CpuBoostMode mode)
    {
        if (_cpuBoost == null || !_cpuBoost.IsSupported) return;

        SyncControls(() => SelectedCpuBoost = CpuBoostChoices.FirstOrDefault(c => c.Mode == mode) ?? SelectedCpuBoost);
        await WriteCpuBoostAsync(mode).ConfigureAwait(true);
    }

    /// <summary>
    /// Re-applies the stored keyboard lighting. Called when the window comes back from the tray, because the firmware
    /// drops the lighting on some machines after a suspend and a keyboard that stayed dark reads as a broken app.
    /// </summary>
    /// <remarks>
    /// The recovery copy wins over the settings file. <c>active_rgb_state.json</c> is written only after a lighting
    /// write succeeded, so it describes the keyboard; the settings file describes what the user last clicked, which
    /// may be a colour the firmware refused.
    /// <para>
    /// A stored "off" is re-asserted as off rather than skipped. The firmware drops the off state after a suspend on
    /// the same machines it drops a lit one, and re-applying the last lit configuration here is what used to turn the
    /// lights back on every time the window came forward.
    /// </para>
    /// </remarks>
    public async Task ReassertKeyboardLightingAsync()
    {
        if (_hardware == null || !LightingAvailable) return;

        if (_settings.IsBacklightOff)
        {
            try
            {
                await _hardware.TurnOffLightingAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                AppLog.Warn(Loc.T("Klavye aydınlatması kapatılamadı"), ex.Message);
            }
            return;
        }

        var last = ActiveRgbStateStore.Load(_settingsPath)
                   ?? _settings.LastActiveLighting
                   ?? _settings.KeyboardLighting;
        if (last == null || LightingPolicy.IsOff(last)) return;

        try
        {
            var zones = (last.LEDs ?? new List<string>()).Take(LightingLimits.ZoneCount).ToArray();
            await _hardware.SetLightingAsync(
                last.Effect,
                last.Brightness,
                last.Speed,
                last.Direction,
                last.Color,
                zones).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AppLog.Warn(Loc.T("Klavye aydınlatması yeniden uygulanamadı"), ex.Message);
        }
    }

    /// <summary>
    /// Hands the fans back to the firmware and disarms the curve. Called before the process exits so a machine is not
    /// left with a fixed fan speed written by a program that is no longer running.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The handback comes first and the bookkeeping second. The old order saved the settings and then wrote Auto, so
    /// a refused write left the file saying the curve was off while the fans were still running at a fixed speed —
    /// and the next launch believed the file.
    /// </para>
    /// <para>
    /// Everything here is <c>ConfigureAwait(false)</c> and the gate wait is bounded. This runs on the shutdown path,
    /// where the UI thread may be the one waiting for it: capturing the context would deadlock the exit, and an
    /// unbounded wait would hang it if a step were stuck in a WMI call.
    /// </para>
    /// </remarks>
    public async Task<bool> RestoreFansToAutoAsync()
    {
        if (_hardware == null) return true;

        // The loop has to stop before the handback, or its next step would write a fixed speed straight back over
        // the Auto write and the machine would exit with the fans pinned.
        _curveLoop.Stop();

        bool ok;
        try
        {
            if (!await _writeGate.WaitAsync(WriteGateTimeout).ConfigureAwait(false))
            {
                AppLog.Warn(Loc.T("Fanlar otomatiğe alınamadı"), "yazma kapısı zaman aşımına uğradı");
                return false;
            }

            try
            {
                ok = await _hardware.SetFanModeAsync(FanMode.Auto).ConfigureAwait(false);
            }
            finally
            {
                _writeGate.Release();
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn(Loc.T("Fanlar otomatiğe alınamadı"), ex.Message);
            return false;
        }

        if (!ok)
        {
            // The firmware refused. The settings file is left alone: the curve is still armed as far as the machine
            // is concerned, and a file that said otherwise would be a lie the next launch acts on.
            AppLog.Warn(Loc.T("Fanlar otomatiğe alınamadı"), "donanım yazımı reddedildi");
            return false;
        }

        _curveEnabled = false;
        _settings.FanCurveEnabled = false;
        _curveEvaluator.Reset();
        _manualFanFloor.Reset();
        SaveSettings();

        // The controls follow the handback, but a binding that cannot be updated from this thread must not turn a
        // successful handback into a failed exit.
        try
        {
            Raise(nameof(CurveEnabled));
            SyncControls(() => SelectedFanMode = FanModes.FirstOrDefault(f => f.Mode == FanMode.Auto) ?? SelectedFanMode);
        }
        catch (Exception ex)
        {
            AppLog.Warn(Loc.T("Fan kontrolleri güncellenemedi"), ex.Message);
        }

        return true;
    }

    /// <summary>
    /// Clean exit: end any game session, put the fans back under firmware control, and report whether that worked.
    /// The caller shuts the application down afterwards; nothing here closes a window.
    /// </summary>
    /// <remarks>
    /// The fan handback is the one thing that must happen, so it runs even when ending the game session throws: a
    /// machine left with a fixed fan speed is worse than a game profile that was not closed cleanly.
    /// </remarks>
    public async Task CleanExitAsync()
    {
        try
        {
            if (_gameProfiles != null) await _gameProfiles.StopAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Warn(Loc.T("Oyun oturumu kapatılamadı"), ex.Message);
        }

        bool ok = false;
        try
        {
            ok = await RestoreFansToAutoAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Warn(Loc.T("Temiz çıkış başarısız"), ex.Message);
        }

        try
        {
            Status = ok ? Labels.CleanExitDone : Labels.CleanExitPartial;
        }
        catch (Exception ex)
        {
            // A status line nobody is going to read is not worth failing the exit over.
            AppLog.Warn(Loc.T("Çıkış durumu yazılamadı"), ex.Message);
        }
    }

    /// <summary>
    /// The fan handback, synchronously, for the two paths that cannot await: <c>ProcessExit</c> and
    /// <c>UnhandledException</c>.
    /// </summary>
    /// <remarks>
    /// The work runs on a thread-pool thread and is waited for with a bound. Running it inline would deadlock the
    /// moment the caller is the UI thread, which is exactly where an unhandled exception is raised, and an unbounded
    /// wait would turn a crash into a hang.
    /// </remarks>
    public bool RestoreFansToAutoBlocking(TimeSpan timeout)
    {
        try
        {
            var task = Task.Run(RestoreFansToAutoAsync);

            if (!task.Wait(timeout))
            {
                AppLog.Warn(Loc.T("Fanlar otomatiğe alınamadı"), "zaman aşımına uğradı");
                return false;
            }

            return task.Result;
        }
        catch (Exception ex)
        {
            AppLog.Warn(Loc.T("Fanlar otomatiğe alınamadı"), ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Runs one write behind the gate and turns the result into a status line. A refusal is not an exception: the
    /// backend reports "the firmware said no" as false, and the user needs to see which control it was. The result is
    /// returned as well, because a caller that has state to update — the pending MUX mode, for one — must not do it
    /// for a write that never landed.
    /// </summary>
    private async Task<bool> RunWriteAsync(Func<Task<bool>> write, string what)
    {
        await _writeGate.WaitAsync().ConfigureAwait(true);
        try
        {
            bool ok = await write().ConfigureAwait(true);
            Status = ok ? $"{Labels.Applied}: {what}" : $"{Labels.WriteFailed}: {what}";
            return ok;
        }
        catch (Exception ex)
        {
            AppLog.Warn(Loc.T("Donanım yazımı başarısız"), $"{what}: {ex.Message}");
            Status = $"{Labels.WriteFailed}: {what}";
            return false;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// The same gate and the same logging as <see cref="RunWriteAsync"/>, without the banner. The lighting page uses
    /// it because the keyboard is its own confirmation: a toast on every slider step would cover the page the user is
    /// working on, and the WPF window never raised one either.
    /// </summary>
    private async Task<bool> RunQuietWriteAsync(Func<Task<bool>> write, string what)
    {
        await _writeGate.WaitAsync().ConfigureAwait(true);
        try
        {
            bool ok = await write().ConfigureAwait(true);
            if (!ok) AppLog.Warn(Loc.T("Donanım yazımı başarısız"), what);
            return ok;
        }
        catch (Exception ex)
        {
            AppLog.Warn(Loc.T("Donanım yazımı başarısız"), $"{what}: {ex.Message}");
            return false;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private void CopyUdevCommandText()
    {
        if (string.IsNullOrWhiteSpace(UdevCommand)) return;
        ClipboardService.SetText(UdevCommand);
        Status = Labels.Copied;
    }

    /// <summary>
    /// Re-probes the machine. The read-back counters go with it, because a rescan is the user saying "try again" and
    /// a capability switched off after three bad sessions would otherwise stay off for good.
    /// </summary>
    private async Task RescanAsync()
    {
        if (_hardware == null || _rescan == null) return;

        _rescan.ResetReadBackCounters();
        _capabilities = await _hardware.GetCapabilitiesAsync().ConfigureAwait(true);
        NeedsUdevRule = _rescan.Snapshot.NeedsUdevRule || _rescan.PermissionHintRaised;

        Raise(nameof(CapabilitySummaryText));
        Raise(nameof(ModeControlAvailable));
        Raise(nameof(HasModeLockNote));
        Raise(nameof(FanControlAvailable));
        Raise(nameof(LightingAvailable));
        Raise(nameof(CoolBoostAvailable));
        Raise(nameof(LcdOverdriveAvailable));
        Raise(nameof(BacklightTimeoutAvailable));
        Raise(nameof(WinKeyLockAvailable));
        Raise(nameof(HasAnyExtra));
        Raise(nameof(NoExtrasAvailable));
        Raise(nameof(ShowExtrasCard));
        Raise(nameof(BatteryLimitAvailable));
        Raise(nameof(MuxAvailable));
        Raise(nameof(SchoolModeColumnSpan));
        Raise(nameof(HardwareReportText));
        Raise(nameof(TierDisplay));
        BuildCapabilityRows();

        await ReadStateAsync().ConfigureAwait(true);
    }

    // ---------------------------------------------------------------- setup helpers

    private void BuildChoices()
    {
        // All nine, in the order the WPF page listed them, with the device's own names. The list is the policy's, so
        // an effect added there cannot be missing here.
        Effects.Clear();
        foreach (var effect in LightingPolicy.AllEffects)
        {
            Effects.Add(new EffectChoice(effect, LightingPolicy.EffectDisplayName(effect)));
        }
        SelectedEffect = Effects[0];

        FanModes.Clear();
        FanModes.Add(new FanModeChoice(FanMode.Auto, Labels.Auto, Labels.FanAutoHint, FanIconAuto, SelectFanMode));
        FanModes.Add(new FanModeChoice(FanMode.Max, Labels.Max, Labels.FanMaxHint, FanIconMax, SelectFanMode));
        FanModes.Add(new FanModeChoice(FanMode.Custom, Labels.CustomCurve, Labels.FanCustomHint, FanIconCustom, SelectFanMode));
        SelectedFanMode = FanModes[0];

        // The same three cards, without the click handler: the Game Profiles form picks a target, it does not apply one.
        GameFanChoices.Clear();
        foreach (var card in FanModes)
        {
            GameFanChoices.Add(new FanModeChoice(card.Mode, card.Label, card.Description, card.Icon));
        }

        NewGameFanMode = GameFanChoices[0];

        // The three modes the firmware knows, which is what NitroSense offers under "GPU Operating". The WMI protocol
        // encodes all three (Discrete=1, Optimus=2, Automatic=3), so offering two hid a mode the machine can switch to.
        GpuModes.Clear();
        GpuModes.Add(new GpuModeChoice(GpuOperatingMode.Optimus, Labels.GpuHybrid));
        GpuModes.Add(new GpuModeChoice(GpuOperatingMode.DiscreteGpu, Labels.GpuDiscrete));
        GpuModes.Add(new GpuModeChoice(GpuOperatingMode.Automatic, Labels.GpuAutomatic));
        SelectedGpuMode = GpuModes[0];

        BuildModeChoices();
    }

    /// <summary>
    /// The mode list, filtered to what the firmware advertises. An empty list means the firmware did not answer,
    /// which is not evidence that no mode works, so the full set is kept in that case.
    /// </summary>
    private void BuildModeChoices()
    {
        var supported = _capabilities.SupportedOperatingModes;
        var wanted = supported.Count > 0
            ? supported
            : new[] { OperatingMode.Quiet, OperatingMode.Default, OperatingMode.Extreme, OperatingMode.Turbo, OperatingMode.Eco };

        // Eco is a battery choice and Turbo/Performance are an AC choice, so each group appears only where it can be
        // used. The active mode always stays visible even when its group is hidden: a list with no marked card would
        // leave the user guessing which mode the machine is running.
        //
        // The rule only applies when the platform can say which source is in use. An unknown source is not evidence
        // that Eco is unavailable, and hiding a working card is worse than showing one that turns out to be a no-op.
        var active = SelectedMode?.Mode ?? _settings.LastOperatingMode ?? OperatingMode.Default;
        var (showHighPower, showEco) = OnAcPower is bool onAc
            ? ModePolicy.VisibleModeGroups(onAc, active)
            : (true, true);

        var previous = SelectedMode?.Mode;
        Modes.Clear();
        foreach (var mode in wanted)
        {
            if (mode == OperatingMode.Eco && !showEco && mode != active) continue;
            if (ModePolicy.IsHighPowerMode(mode) && !showHighPower && mode != active) continue;

            Modes.Add(new ModeChoice(mode, ModeLabel(mode), ModeHint(mode), ModeIcon(mode), SelectMode)
            {
                ShortHint = ModeShortHint(mode),
                ActiveLabel = Labels.ActiveBadge
            });
        }

        SelectedMode = Modes.FirstOrDefault(m => m.Mode == previous) ?? Modes.FirstOrDefault();
        SyncModeSelection();

        // The Game Profiles form offers the same modes. Its own selection is kept across a rescan.
        var previousTarget = NewGameMode?.Mode ?? OperatingMode.Turbo;
        GameModeChoices.Clear();
        foreach (var card in Modes)
        {
            GameModeChoices.Add(new ModeChoice(card.Mode, card.Label, card.Description, card.Icon));
        }

        NewGameMode = GameModeChoices.FirstOrDefault(m => m.Mode == previousTarget) ?? GameModeChoices.FirstOrDefault();

        // Same reason as the boost list: the game form's target must be re-resolved against the rebuilt instances.
        SyncGameTargets();
    }

    /// <summary>True when the firmware advertises <paramref name="mode"/>. An empty list means "not known", not "no".</summary>
    public bool IsModeSupported(OperatingMode mode) =>
        ModePolicy.IsModeSupported(_capabilities.SupportedOperatingModes, mode);

    /// <summary>True when the battery rule allows <paramref name="mode"/> right now.</summary>
    public bool IsModeAllowed(OperatingMode mode) =>
        ModePolicy.IsModeAllowed(mode, OnAcPower ?? true, BatteryPercent);

    /// <summary>A card click. Goes through the setter so the write, the settings file and the accent all follow.</summary>
    private void SelectMode(ModeChoice card) => SelectedMode = card;

    private void SelectFanMode(FanModeChoice card) => SelectedFanMode = card;

    private static string ModeLabel(OperatingMode mode) => mode switch
    {
        OperatingMode.Quiet => Loc.T("Sessiz"),
        OperatingMode.Default => Loc.T("Dengeli"),
        OperatingMode.Extreme => Loc.T("Performans"),
        OperatingMode.Turbo => Loc.T("Turbo"),
        OperatingMode.Eco => Loc.T("Eco"),
        _ => mode.ToString()
    };

    /// <summary>The two-word label the compact tile carries.</summary>
    private static string ModeShortHint(OperatingMode mode) => mode switch
    {
        OperatingMode.Quiet => Loc.T("Hafif kullanım"),
        OperatingMode.Default => Loc.T("Günlük önerilen"),
        OperatingMode.Extreme => Loc.T("Yüksek oyun gücü"),
        OperatingMode.Turbo => Loc.T("Maksimum performans"),
        OperatingMode.Eco => Loc.T("Pil ömrü odaklı"),
        _ => ""
    };

    /// <summary>The one line under a mode's title. Says what the mode costs, not what it is called.</summary>
    private static string ModeHint(OperatingMode mode) => mode switch
    {
        OperatingMode.Quiet => Loc.T("Fanlar en sessiz seviyede; sıcaklık biraz yükselir."),
        OperatingMode.Default => Loc.T("Sessizlik ile performans arasında dengeli çalışır."),
        OperatingMode.Extreme => Loc.T("Fanlar erken hızlanır; uzun oyunlarda daha serin kalır."),
        OperatingMode.Turbo => Loc.T("En yüksek güç ve fan hızı; en gürültülü seçenek."),
        OperatingMode.Eco => Loc.T("Güç sınırlanır; pil ömrü uzar, performans düşer."),
        _ => ""
    };

    // 24×24 path data. Bars for the power ladder, a moon for Quiet and a leaf for Eco: the shape says the same thing
    // as the hint. PathIcon stretches each geometry to fill its box, so a single short bar would have been blown up
    // into a solid square; the two ends of the ladder therefore get glyphs that are meant to fill the box.
    // The mode tiles carry the same glyphs the WPF window used: a moon for quiet, a monitor for balanced, a ladder for
    // performance, a gauge for turbo and a leaf for eco.
    private const string ModeIconQuiet = "\uE708";
    private const string ModeIconBalanced = "\uE9D2";
    private const string ModeIconPerformance = "\uE9D9";
    private const string ModeIconTurbo = "\uE945";
    private const string ModeIconEco = "\uE8AB";

    private static string ModeIcon(OperatingMode mode) => mode switch
    {
        OperatingMode.Quiet => ModeIconQuiet,
        OperatingMode.Default => ModeIconBalanced,
        OperatingMode.Extreme => ModeIconPerformance,
        OperatingMode.Turbo => ModeIconTurbo,
        OperatingMode.Eco => ModeIconEco,
        _ => ModeIconBalanced
    };

    // A ring for automatic, an arrow for maximum, two sliders for the custom curve.
    private const string FanIconAuto = "M12,2 A10,10 0 1,1 11.99,2 Z M12,7 A5,5 0 1,1 11.99,7 Z";
    private const string FanIconMax = "M12,2 L20,12 H15 V22 H9 V12 H4 Z";
    private const string FanIconCustom = "M2,5 H22 V8 H2 Z M2,16 H22 V19 H2 Z M7,3 H10 V10 H7 Z M14,14 H17 V21 H14 Z";

    private void BuildCurveAnchors()
    {
        var points = FanCurve.Sanitize(_settings.FanCurvePoints);
        CurveAnchors.Clear();
        foreach (var point in points)
        {
            CurveAnchors.Add(new CurveAnchorViewModel(point.TemperatureC, point.Percent, OnCurveChanged) { Labels = Labels });
        }
    }

    private void OnCurveChanged()
    {
        Raise(nameof(CurvePreview));
        Raise(nameof(CurvePoints));

        // Moving one anchor makes the curve the user's own, so the preset button stops claiming it. Without this the
        // ŞABLON row would keep a shape highlighted that the anchors no longer are.
        SyncCurvePreset();

        if (_suppressWrite) return;

        // The shape is the state, so it is stored as it is dragged and the evaluator is re-armed at once: the next
        // telemetry tick then follows the new shape. Only the file write waits for the drag to stop, because a drag
        // raises this once per pixel and each write is a whole settings file.
        _settings.FanCurvePoints = FanCurve.Sanitize(
            CurveAnchors.Select(a => new FanCurvePoint(a.TemperatureC, a.Percent)));
        _curveEvaluator = new FanCurveEvaluator(_settings.BuildFanCurve());
        _curveDebounce.Run(() => SaveSettings());
    }

    private FanCurve CurrentCurve() =>
        new(CurveAnchors.Select(a => new FanCurvePoint(a.TemperatureC, a.Percent)));

    /// <summary>
    /// Writes the settings file.
    /// </summary>
    /// <returns>
    /// False when the file could not be written. Most callers ignore it — a preference that failed to save is
    /// reported by the log and retried on the next change — but a caller recording something it cannot afford to
    /// lose, such as the game-session recovery record, has to be able to say so instead of assuming the write landed.
    /// </returns>
    private bool SaveSettings()
    {
        try
        {
            SettingsManager.SaveSettingsAtomic(_settingsPath, _settings);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Ayarlar kaydedilemedi", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Runs an action once the user has stopped moving a slider for a moment.
    /// </summary>
    /// <remarks>
    /// A slider raises a change per pixel of the drag. Every one of these actions writes the settings file and talks to
    /// the firmware, so without the pause a single drag is a hundred file writes and a hundred WMI calls — which is
    /// what made the sliders feel like they were not saving: the last write was still queued behind the others.
    /// The continuation resumes on the caller's context, so the properties it raises still reach the UI thread.
    /// </remarks>
    private sealed class Debouncer : IDisposable
    {
        private readonly TimeSpan _delay;
        private CancellationTokenSource? _pending;

        public Debouncer(TimeSpan delay) => _delay = delay;

        public void Run(Func<Task> action)
        {
            _pending?.Cancel();
            _pending?.Dispose();

            var cts = new CancellationTokenSource();
            _pending = cts;
            _ = RunAsync(action, cts);
        }

        /// <summary>The same pause for an action that does not await anything, such as a settings write.</summary>
        public void Run(Action action) => Run(() =>
        {
            action();
            return Task.CompletedTask;
        });

        private async Task RunAsync(Func<Task> action, CancellationTokenSource cts)
        {
            try
            {
                await Task.Delay(_delay, cts.Token).ConfigureAwait(true);
                if (cts.IsCancellationRequested) return;

                await action().ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                // A newer change replaced this one; that change does the work.
            }
            catch (Exception ex)
            {
                AppLog.Warn(Loc.T("Gecikmeli yazma başarısız"), ex.Message);
            }
        }

        public void Dispose()
        {
            _pending?.Cancel();
            _pending?.Dispose();
            _pending = null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // The loop is stopped before the gate is disposed: a step in flight is holding the gate, and disposing it
        // underneath that step would throw on a background thread nobody is watching.
        _curveLoop.Dispose();

        _lightingDebounce.Dispose();
        _customFanDebounce.Dispose();
        _curveDebounce.Dispose();
        _writeGate.Dispose();
    }
}
