using System.Collections.ObjectModel;
using System.IO;
using NitroLite.Core;
using NitroLite.Core.Hardware;
using NitroLite.Desktop.Mvvm;
using NitroLite.Linux;

namespace NitroLite.Desktop.ViewModels;

/// <summary>One entry in the operating-mode selector.</summary>
public sealed record ModeChoice(OperatingMode Mode, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One entry in the fan-mode selector.</summary>
public sealed record FanModeChoice(FanMode Mode, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One entry in the effect selector. <see cref="Id"/> is what the backend expects.</summary>
public sealed record EffectChoice(string Id, string Label)
{
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
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly AppSettings _settings;
    private readonly IHardwareController? _hardware;
    private readonly LinuxCapabilities? _linux;
    private readonly string _settingsPath;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    private FanCurveEvaluator _curveEvaluator;
    private HardwareCapabilities _capabilities = HardwareCapabilities.Unknown;

    /// <summary>
    /// True while the view model is filling its controls from the hardware. It starts true because the constructor
    /// populates the mode and fan-mode lists, and a selection change there is not a user action: without this the
    /// window would write "Auto" over the fan mode the machine was already in, on every launch, before it had read
    /// anything. <see cref="InitializeAsync"/> clears it once the first read is done.
    /// </summary>
    private bool _suppressWrite = true;
    private bool _refreshing;
    private bool _disposed;

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

    private LocLanguage? _selectedLanguage;
    private string _udevCommand = "";
    private bool _needsUdevRule;

    public MainViewModel(AppSettings settings, IHardwareController? hardware, LinuxCapabilities? linux, string settingsPath)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _hardware = hardware;
        _linux = linux;
        _settingsPath = settingsPath;

        _curveEvaluator = new FanCurveEvaluator(settings.BuildFanCurve());

        BuildCurveAnchors();
        BuildChoices();

        _selectedLanguage = Loc.Languages.FirstOrDefault(l => l.Code == Loc.Language) ?? Loc.Languages[0];
        _needsUdevRule = linux?.NeedsUdevRule == true;
        if (_needsUdevRule)
        {
            var rulePath = UdevGuide.EnsureRuleFile();
            _udevCommand = rulePath == null ? "" : UdevGuide.InstallCommand(rulePath);
        }

        // The pill is short on purpose; the banner carries the sentence that explains it.
        _status = HasNoBackend ? _labels.NoBackend : _labels.Ready;

        ApplyModeCommand = new AsyncCommand(() => ApplyModeAsync(_selectedMode?.Mode));
        ApplyFanModeCommand = new AsyncCommand(ApplyFanModeAsync);
        ApplyCurveCommand = new AsyncCommand(ApplyCurveAsync);
        ApplyLightingCommand = new AsyncCommand(ApplyLightingAsync);
        TurnOffLightingCommand = new AsyncCommand(TurnOffLightingAsync);
        CopyUdevCommand = new RelayCommand(CopyUdevCommandText);
        RescanCommand = new AsyncCommand(RescanAsync);
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
            Raise(nameof(CurvePreview));
            Raise(nameof(CapabilitySummaryText));
        }
    }

    /// <summary>The last thing that happened, shown in the footer. Never an exception message on its own.</summary>
    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    /// <summary>True once the backend has answered. The window uses it to hide the "no backend" banner.</summary>
    public bool IsReady
    {
        get => _isReady;
        private set => Set(ref _isReady, value);
    }

    /// <summary>
    /// True when there is a backend and it found something to talk to. A Linux machine with no <c>acer-wmi</c> node
    /// still gets a controller object, so "the object exists" is not the same question as "the hardware is there".
    /// </summary>
    public bool HasBackend => _hardware != null && _hardware.IsAvailable;

    public bool HasNoBackend => !HasBackend;

    /// <summary>
    /// The second line of the banner. Two different machines reach this state — a Windows build with no backend at
    /// all, and a Linux machine whose kernel has no Acer module — and they need different sentences.
    /// </summary>
    public string NoBackendDetail => _hardware == null ? Labels.NoBackendDetail : Labels.NoAcerNodes;

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
        }
    }

    public int GpuTemperature
    {
        get => _gpuTemperature;
        private set
        {
            if (Set(ref _gpuTemperature, value)) Raise(nameof(GpuTemperatureText));
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
            if (Set(ref _cpuFanRpm, value)) Raise(nameof(CpuFanRpmText));
        }
    }

    public int GpuFanRpm
    {
        get => _gpuFanRpm;
        private set
        {
            if (Set(ref _gpuFanRpm, value)) Raise(nameof(GpuFanRpmText));
        }
    }

    // The unit is part of the text rather than a StringFormat in XAML: a format string in a compiled binding is one
    // more thing that can silently render nothing, and these five are read at a glance.
    public string CpuTemperatureText => $"{CpuTemperature} °C";
    public string GpuTemperatureText => $"{GpuTemperature} °C";
    public string SysTemperatureText => $"{SysTemperature} °C";
    public string CpuFanRpmText => $"{CpuFanRpm} RPM";
    public string GpuFanRpmText => $"{GpuFanRpm} RPM";

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

    // ---------------------------------------------------------------- operating mode

    public ObservableCollection<ModeChoice> Modes { get; } = new();

    public ModeChoice? SelectedMode
    {
        get => _selectedMode;
        set
        {
            if (!Set(ref _selectedMode, value)) return;
            if (!_suppressWrite) _ = ApplyModeAsync(value?.Mode);
        }
    }

    public bool ModeControlAvailable => _capabilities.OperatingModeControl;

    // ---------------------------------------------------------------- fan

    public ObservableCollection<FanModeChoice> FanModes { get; } = new();

    public FanModeChoice? SelectedFanMode
    {
        get => _selectedFanMode;
        set
        {
            if (!Set(ref _selectedFanMode, value)) return;
            Raise(nameof(CustomFanVisible));
            if (!_suppressWrite) _ = ApplyFanModeAsync();
        }
    }

    /// <summary>The custom-speed sliders only mean something in <see cref="FanMode.Custom"/>.</summary>
    public bool CustomFanVisible => SelectedFanMode?.Mode == FanMode.Custom;

    public int CustomCpuPercent
    {
        get => _customCpuPercent;
        set
        {
            if (Set(ref _customCpuPercent, Math.Clamp(value, FanCurve.MinPercent, FanCurve.MaxPercent)))
            {
                Raise(nameof(CustomCpuPercentText));
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
            }
        }
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
            // are already at the right speed and never write.
            _curveEvaluator.Reset();
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
        set => Set(ref _selectedEffect, value);
    }

    public int BrightnessLevel
    {
        get => _brightnessLevel;
        set => Set(ref _brightnessLevel, Math.Clamp(value, 0, LinuxKeyboardProtocol.MaxBrightnessLevel));
    }

    public int SpeedLevel
    {
        get => _speedLevel;
        set => Set(ref _speedLevel, Math.Clamp(value, 0, LinuxKeyboardProtocol.MaxSpeed));
    }

    public int DirectionLevel
    {
        get => _directionLevel;
        set => Set(ref _directionLevel, Math.Clamp(value, 1, LinuxKeyboardProtocol.MaxDirection));
    }

    public string PrimaryColor
    {
        get => _primaryColor;
        set => Set(ref _primaryColor, value);
    }

    public string Zone1 { get => _zone1; set => Set(ref _zone1, value); }
    public string Zone2 { get => _zone2; set => Set(ref _zone2, value); }
    public string Zone3 { get => _zone3; set => Set(ref _zone3, value); }
    public string Zone4 { get => _zone4; set => Set(ref _zone4, value); }

    public bool LightingAvailable => _capabilities.KeyboardLighting;

    // The slider ranges come from the protocol constants rather than from numbers typed into the XAML, so a change to
    // the sysfs contract cannot leave the UI offering a level the kernel will reject.
    public int MaxBrightness => LinuxKeyboardProtocol.MaxBrightnessLevel;
    public int MaxSpeed => LinuxKeyboardProtocol.MaxSpeed;
    public int MaxDirection => LinuxKeyboardProtocol.MaxDirection;

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
            if (!_suppressWrite) _ = RunWriteAsync(() => _hardware!.SetBacklightTimeoutAsync(value), Labels.BacklightTimeout);
        }
    }

    public bool BatteryLimitAvailable => _capabilities.BatteryChargeLimit;

    /// <summary>True when the probe found none of the four optional nodes, so the page can say so once.</summary>
    public bool NoExtrasAvailable =>
        !CoolBoostAvailable && !LcdOverdriveAvailable && !BacklightTimeoutAvailable && !BatteryLimitAvailable;

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
            Status = Labels.LanguageRestartHint;
        }
    }

    public string DeviceModel => _linux?.Identity.ModelCode is { Length: > 0 } code
        ? $"{_linux.Identity.Model} ({code})"
        : _linux?.Identity.Model ?? "";

    public string DeviceBios => _linux?.Identity.BiosVersion ?? "";

    public string TierText => _linux?.Tier ?? "none";

    public string DataFolder => AppPaths.DataDirectory;

    public IReadOnlyList<string> Notes => _linux?.Notes ?? Array.Empty<string>();

    /// <summary>The probe notes as one block, so the page needs no item template for a list of strings.</summary>
    public string NotesText => string.Join(Environment.NewLine, Notes);

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

            var parts = new List<string>
            {
                Row(Labels.CapFanControl, _capabilities.FanControl),
                Row(Labels.CapOperatingMode, _capabilities.OperatingModeControl),
                Row(Labels.CapMux, _capabilities.MuxSwitch),
                Row(Labels.CapBatteryLimit, _capabilities.BatteryChargeLimit),
                Row(Labels.CapKeyboard, _capabilities.KeyboardLighting),
                Row(Labels.CapBacklightTimeout, _capabilities.BacklightTimeout),
                Row(Labels.CapCoolBoost, _capabilities.CoolBoost),
                Row(Labels.CapLcdOverdrive, _capabilities.LcdOverdrive)
            };

            return string.Join(Environment.NewLine, parts);
        }
    }

    private static string Row(string label, bool supported) => $"{(supported ? "✓" : "✕")}  {label}";

    // ---------------------------------------------------------------- commands

    public AsyncCommand ApplyModeCommand { get; }
    public AsyncCommand ApplyFanModeCommand { get; }
    public AsyncCommand ApplyCurveCommand { get; }
    public AsyncCommand ApplyLightingCommand { get; }
    public AsyncCommand TurnOffLightingCommand { get; }
    public RelayCommand CopyUdevCommand { get; }
    public AsyncCommand RescanCommand { get; }

    // ---------------------------------------------------------------- lifecycle

    /// <summary>
    /// Reads the machine's current state. Called once when the window opens; every step is skipped rather than forced
    /// when the backend does not answer, so a machine with only tier 1 still gets a usable window.
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_hardware == null) return;

        try
        {
            await _hardware.EnsureConnectedAsync().ConfigureAwait(true);
            _capabilities = await _hardware.GetCapabilitiesAsync().ConfigureAwait(true);

            BuildModeChoices();
            Raise(nameof(ModeControlAvailable));
            Raise(nameof(FanControlAvailable));
            Raise(nameof(LightingAvailable));
            Raise(nameof(CoolBoostAvailable));
            Raise(nameof(LcdOverdriveAvailable));
            Raise(nameof(BacklightTimeoutAvailable));
            Raise(nameof(BatteryLimitAvailable));
            Raise(nameof(CapabilitySummaryText));

            await ReadStateAsync().ConfigureAwait(true);

            // A Linux machine whose kernel exposes no Acer node is not an error and not a crash: the window stays
            // usable, the banner explains it, and every control that needs hardware stays hidden.
            if (!_hardware.IsAvailable)
            {
                IsReady = false;
                Status = Labels.NoAcerNodes;
                Raise(nameof(HasBackend));
                Raise(nameof(HasNoBackend));
                Raise(nameof(NoBackendDetail));
                return;
            }

            IsReady = true;
            Status = Labels.Ready;
        }
        catch (Exception ex)
        {
            AppLog.Error("Linux arayüzü başlatılamadı", ex.ToString());
            Status = Labels.NoAcerNodes;
        }
        finally
        {
            // Whatever happened above, the controls must work from now on: a failed initial read is not a reason to
            // leave the window read-only for the rest of the session.
            _suppressWrite = false;
        }
    }

    /// <summary>
    /// One telemetry sample, plus the fan-curve step when the curve is armed. Called by the window's timer; the guard
    /// keeps a slow read from stacking up behind the next tick.
    /// </summary>
    public async Task RefreshTelemetryAsync()
    {
        if (_hardware == null || _refreshing || _disposed) return;

        _refreshing = true;
        try
        {
            var data = await _hardware.GetMonitorDataAsync().ConfigureAwait(true);
            if (data == null) return;

            CpuTemperature = data.CpuTemperature;
            GpuTemperature = data.GpuTemperature;
            SysTemperature = data.SysTemperature;
            CpuFanRpm = data.CpuFanSpeed;
            GpuFanRpm = data.GpuFanSpeed;
            FanRpmMeasured = data.IsFanRpmMeasured;
            Raise(nameof(CurvePreview));

            await StepCurveAsync(data.CpuTemperature).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Telemetri okunamadı", ex.Message);
        }
        finally
        {
            _refreshing = false;
        }
    }

    /// <summary>
    /// Applies the curve for the current temperature when it is armed and the fan mode is custom. The evaluator owns
    /// the hysteresis and the dwell time, and <see cref="FanCurve.PercentAt"/> owns the safety floor, so neither is
    /// re-implemented here.
    /// </summary>
    private async Task StepCurveAsync(int cpuTemperature)
    {
        if (!CurveEnabled || SelectedFanMode?.Mode != FanMode.Custom) return;

        var target = _curveEvaluator.Evaluate(cpuTemperature, DateTime.UtcNow);
        if (target is not int percent) return;

        await _writeGate.WaitAsync().ConfigureAwait(true);
        try
        {
            await _hardware!.SetFanModeAsync(FanMode.Custom, percent, percent).ConfigureAwait(true);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    // ---------------------------------------------------------------- reads

    private async Task ReadStateAsync()
    {
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

            var coolBoost = await _hardware.GetCoolBoostAsync().ConfigureAwait(true);
            if (coolBoost is bool cool) CoolBoostOn = cool;

            var overdrive = await _hardware.GetLcdOverdriveAsync().ConfigureAwait(true);
            if (overdrive is bool lcd) LcdOverdriveOn = lcd;

            var timeout = await _hardware.GetBacklightTimeoutAsync().ConfigureAwait(true);
            if (timeout is bool backlight) BacklightTimeoutOn = backlight;

            var charge = await _hardware.GetBatteryChargeModeAsync().ConfigureAwait(true);
            if (charge is BatteryChargeMode battery) BatteryLimitOn = battery == BatteryChargeMode.Optimized80;
        }
        finally
        {
            _suppressWrite = false;
        }
    }

    private void ApplyLightingState(LightingConfig lighting)
    {
        SelectedEffect = Effects.FirstOrDefault(e => e.Id == lighting.Effect.ToUpperInvariant()) ?? SelectedEffect;
        BrightnessLevel = lighting.Brightness;
        SpeedLevel = lighting.Speed;
        DirectionLevel = lighting.Direction;
        PrimaryColor = lighting.Color;

        if (lighting.LEDs.Count > 0) Zone1 = lighting.LEDs[0];
        if (lighting.LEDs.Count > 1) Zone2 = lighting.LEDs[1];
        if (lighting.LEDs.Count > 2) Zone3 = lighting.LEDs[2];
        if (lighting.LEDs.Count > 3) Zone4 = lighting.LEDs[3];
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

        await RunWriteAsync(
            () => _hardware.SetFanModeAsync(choice.Mode, CustomCpuPercent, CustomGpuPercent),
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
        await RunWriteAsync(
            () => _hardware.SetFanModeAsync(FanMode.Custom, CustomCpuPercent, CustomGpuPercent),
            Labels.FanCurve).ConfigureAwait(true);
    }

    private async Task ApplyLightingAsync()
    {
        if (_hardware == null || SelectedEffect is not EffectChoice effect) return;

        var zones = new[] { Zone1, Zone2, Zone3, Zone4 };
        await RunWriteAsync(
            () => _hardware.SetLightingAsync(effect.Id, BrightnessLevel, SpeedLevel, DirectionLevel, PrimaryColor, zones),
            Labels.KeyboardLighting).ConfigureAwait(true);
    }

    private async Task TurnOffLightingAsync()
    {
        if (_hardware == null) return;
        await RunWriteAsync(() => _hardware.TurnOffLightingAsync(), Labels.KeyboardLighting).ConfigureAwait(true);
    }

    /// <summary>
    /// Runs one write behind the gate and turns the result into a status line. A refusal is not an exception: the
    /// backend reports "the firmware said no" as false, and the user needs to see which control it was.
    /// </summary>
    private async Task RunWriteAsync(Func<Task<bool>> write, string what)
    {
        await _writeGate.WaitAsync().ConfigureAwait(true);
        try
        {
            bool ok = await write().ConfigureAwait(true);
            Status = ok ? $"{Labels.Applied}: {what}" : $"{Labels.WriteFailed}: {what}";
        }
        catch (Exception ex)
        {
            AppLog.Warn("Donanım yazımı başarısız", $"{what}: {ex.Message}");
            Status = $"{Labels.WriteFailed}: {what}";
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
        if (_hardware is not LinuxSysfsHardwareController linux) return;

        linux.ReadBack.ResetAll();
        _capabilities = await linux.GetCapabilitiesAsync().ConfigureAwait(true);
        NeedsUdevRule = linux.Capabilities.NeedsUdevRule || linux.PermissionHintRaised;

        Raise(nameof(CapabilitySummaryText));
        Raise(nameof(ModeControlAvailable));
        Raise(nameof(FanControlAvailable));
        Raise(nameof(LightingAvailable));
        Raise(nameof(CoolBoostAvailable));
        Raise(nameof(LcdOverdriveAvailable));
        Raise(nameof(BacklightTimeoutAvailable));
        Raise(nameof(BatteryLimitAvailable));

        await ReadStateAsync().ConfigureAwait(true);
        Status = Labels.Ready;
    }

    // ---------------------------------------------------------------- setup helpers

    private void BuildChoices()
    {
        Effects.Clear();
        Effects.Add(new EffectChoice("STATIC", Loc.T("Sabit")));
        Effects.Add(new EffectChoice("BREATHING", Loc.T("Nefes")));
        Effects.Add(new EffectChoice("NEON", Loc.T("Neon")));
        Effects.Add(new EffectChoice("WAVE", Loc.T("Dalga")));
        Effects.Add(new EffectChoice("SHIFTING", Loc.T("Kayma")));
        Effects.Add(new EffectChoice("ZOOM", Loc.T("Zoom")));
        SelectedEffect = Effects[0];

        FanModes.Clear();
        FanModes.Add(new FanModeChoice(FanMode.Auto, Labels.Auto));
        FanModes.Add(new FanModeChoice(FanMode.Max, Labels.Max));
        FanModes.Add(new FanModeChoice(FanMode.Custom, Labels.CustomCurve));
        SelectedFanMode = FanModes[0];

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

        var previous = SelectedMode?.Mode;
        Modes.Clear();
        foreach (var mode in wanted) Modes.Add(new ModeChoice(mode, ModeLabel(mode)));

        SelectedMode = Modes.FirstOrDefault(m => m.Mode == previous) ?? Modes.FirstOrDefault();
    }

    private static string ModeLabel(OperatingMode mode) => mode switch
    {
        OperatingMode.Quiet => Loc.T("Sessiz"),
        OperatingMode.Default => Loc.T("Dengeli"),
        OperatingMode.Extreme => Loc.T("Performans"),
        OperatingMode.Turbo => Loc.T("Turbo"),
        OperatingMode.Eco => Loc.T("Eco"),
        _ => mode.ToString()
    };

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
    }

    private FanCurve CurrentCurve() =>
        new(CurveAnchors.Select(a => new FanCurvePoint(a.TemperatureC, a.Percent)));

    private void SaveSettings()
    {
        try
        {
            SettingsManager.SaveSettingsAtomic(_settingsPath, _settings);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Ayarlar kaydedilemedi", ex.Message);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _writeGate.Dispose();
    }
}
