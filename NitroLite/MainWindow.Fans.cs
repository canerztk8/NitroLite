using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Media;
using NitroLite.Core;
using WpfBrush = System.Windows.Media.Brush;
using WpfButton = System.Windows.Controls.Button;
using WpfLine = System.Windows.Shapes.Line;
using WpfPoint = System.Windows.Point;

namespace NitroLite;

public partial class MainWindow
{
    /// <summary>Highlights the active fan mode button (Auto / Max / Custom / Curve).</summary>
    private void UpdateFanButtonsUi(FanMode mode, bool known = true)
    {
        if (BtnFanAuto == null || BtnFanMax == null) return;
        SetSegActive(BtnFanAuto, known && mode == FanMode.Auto);
        SetSegActive(BtnFanMax, known && mode == FanMode.Max);
        BtnFanAuto.ToolTip = known && mode == FanMode.Auto ? Loc.T("Etkin") : null;
        BtnFanMax.ToolTip = known && mode == FanMode.Max ? Loc.T("Etkin") : null;

        // The curve drives the fans through the service's custom mode, so "Özel" and "Eğri" share one device
        // state. Which of the two is lit is decided here, not by the device.
        bool curveOwnsFans = _fanCurveEnabled;
        if (BtnFanCustom != null)
        {
            bool custom = known && mode == FanMode.Custom && !curveOwnsFans;
            SetSegActive(BtnFanCustom, custom);
            BtnFanCustom.ToolTip = custom ? Loc.T("Etkin") : Loc.T("CPU ve GPU fanını sabit bir hızda (%30–100) çalıştırır.");
        }
        if (BtnFanCurve != null)
        {
            bool curve = known && mode == FanMode.Custom && curveOwnsFans;
            SetSegActive(BtnFanCurve, curve);
            // A running game profile owns the curve for the length of its session; the card cannot take it back.
            bool gameOwnsCurve = _fanCurveOwner == FanCurveOwner.Game;
            BtnFanCurve.IsEnabled = !gameOwnsCurve;
            BtnFanCurve.ToolTip = gameOwnsCurve
                ? Loc.T("Fan eğrisi bu oyun oturumu için oyun profilinden geliyor.")
                : curve ? Loc.T("Etkin") : Loc.T("Fan hızını CPU sıcaklığına göre kendisi ayarlar.");
        }

        // The sliders stay open while the user is choosing "Özel" (before the service confirms it).
        if (PnlCustomFan != null)
            PnlCustomFan.Visibility = ((known && mode == FanMode.Custom && !curveOwnsFans) || _customFanPanelPinned)
                ? Visibility.Visible
                : Visibility.Collapsed;
        if (PnlFanCurve != null)
            PnlFanCurve.Visibility = curveOwnsFans ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Loads the saved custom speeds into the sliders and the Acer client (all restore paths use them).</summary>
    private void LoadCustomFanSettings()
    {
        int cpu = AppSettings.ClampCustomFanPercent(_appSettings.CustomFanCpuPercent);
        int gpu = AppSettings.ClampCustomFanPercent(_appSettings.CustomFanGpuPercent);
        _agentClient.CustomFanCpuPercent = cpu;
        _agentClient.CustomFanGpuPercent = gpu;
        if (SliderFanCpu != null) SliderFanCpu.Value = cpu;
        if (SliderFanGpu != null) SliderFanGpu.Value = gpu;
        UpdateCustomFanLabels();
    }

    private void UpdateCustomFanLabels()
    {
        if (TxtFanCpuPercent != null && SliderFanCpu != null) TxtFanCpuPercent.Text = $"%{(int)SliderFanCpu.Value}";
        if (TxtFanGpuPercent != null && SliderFanGpu != null) TxtFanGpuPercent.Text = $"%{(int)SliderFanGpu.Value}";
    }

    private async void BtnFanCustom_Click(object sender, RoutedEventArgs e)
    {
        _customFanPanelPinned = true;
        try
        {
            await SetManualFanModeAsync(FanMode.Custom);
        }
        finally
        {
            _customFanPanelPinned = false;
            UpdateFanButtonsUi(_currentFanMode);
        }
    }

    private void SliderCustomFan_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateCustomFanLabels();
        if (!IsLoaded || _isInitializingUi || SliderFanCpu == null || SliderFanGpu == null) return;

        _appSettings.CustomFanCpuPercent = AppSettings.ClampCustomFanPercent((int)SliderFanCpu.Value);
        _appSettings.CustomFanGpuPercent = AppSettings.ClampCustomFanPercent((int)SliderFanGpu.Value);
        _agentClient.CustomFanCpuPercent = _appSettings.CustomFanCpuPercent;
        _agentClient.CustomFanGpuPercent = _appSettings.CustomFanGpuPercent;

        // Apply and save once dragging pauses, not on every tick of the slider.
        _customFanApplyTimer ??= CreateCustomFanApplyTimer();
        _customFanApplyTimer.Stop();
        _customFanApplyTimer.Start();
    }

    private DispatcherTimer CreateCustomFanApplyTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        timer.Tick += async (s, e) =>
        {
            timer.Stop();
            SaveSettings();
            if (_currentFanMode != FanMode.Custom || _isGameSessionActive) return;
            if (!await _agentClient.SetFanModeAsync(FanMode.Custom))
                ShowToast(Loc.T("Fan hızı uygulanamadı."), false);
        };
        return timer;
    }

    /// <summary>
    /// Fills the curve editor from the saved settings. Arming the curve is the startup path's job (or the
    /// button's), not this one: a settings reload must never silently take the fans.
    /// </summary>
    private void LoadFanCurveSettings()
    {
        _fanCurveLastPercent = _appSettings.CustomFanCpuPercent;
        InitializeCurvePresetButtons();
        ApplyFanCurveToEditor(_appSettings.BuildFanCurve());
        UpdateFanCurveStatusText();
    }

    /// <summary>
    /// Labels the template buttons from the preset list, so a preset is named in one place. The text is set here
    /// rather than in the XAML because the catalogue key lives on the preset record.
    /// </summary>
    private void InitializeCurvePresetButtons()
    {
        foreach (var button in CurvePresetButtons)
        {
            var preset = FanCurvePresets.ById(button.Tag as string);
            if (preset == null) continue;
            button.Content = Loc.T(preset.NameKey);
            button.ToolTip = Loc.T(preset.DescriptionKey);
        }
    }

    /// <summary>
    /// Marks the template the editor currently shows, and clears the mark once a slider has moved off it. The
    /// curve is the state, so nothing has to be remembered: the shape either is a preset or it is not.
    /// </summary>
    private void UpdateFanCurvePresetButtons()
    {
        var buttons = CurvePresetButtons.ToList();
        if (buttons.Count == 0) return;

        var current = FanCurvePresets.Match(ReadFanCurveFromEditor());
        foreach (var button in buttons)
        {
            SetSegActive(button, current != null && current.Id == button.Tag as string);
        }
    }

    private void ApplyFanCurveToEditor(FanCurve curve)
    {
        _isUpdatingFanCurveUi = true;
        try
        {
            var sliders = FanCurveSliders;
            for (int i = 0; i < sliders.Length && i < FanCurve.AnchorTemperatures.Count; i++)
            {
                if (sliders[i] == null) continue;
                sliders[i]!.Value = curve.PercentAt(FanCurve.AnchorTemperatures[i]);
            }
        }
        finally
        {
            _isUpdatingFanCurveUi = false;
        }
        UpdateFanCurveLabels();
        RenderFanCurve();
        UpdateFanCurvePresetButtons();
    }

    /// <summary>The curve the editor currently shows, as the six anchors the sliders stand for.</summary>
    private List<FanCurvePoint> ReadFanCurveFromEditor()
    {
        var points = new List<FanCurvePoint>();
        var sliders = FanCurveSliders;
        for (int i = 0; i < sliders.Length && i < FanCurve.AnchorTemperatures.Count; i++)
        {
            int percent = sliders[i] == null
                ? FanCurve.MinPercent
                : AppSettings.ClampCustomFanPercent((int)sliders[i]!.Value);
            points.Add(new FanCurvePoint(FanCurve.AnchorTemperatures[i], percent));
        }
        return points;
    }

    private void UpdateFanCurveLabels()
    {
        var sliders = FanCurveSliders;
        var labels = FanCurveLabels;
        for (int i = 0; i < sliders.Length && i < labels.Length; i++)
        {
            if (sliders[i] == null || labels[i] == null) continue;
            labels[i]!.Text = $"%{(int)sliders[i]!.Value}";
        }
    }

    private void UpdateFanCurveStatusText()
    {
        if (TxtFanCurveStatus == null) return;
        if (!_fanCurveEnabled)
        {
            TxtFanCurveStatus.Text = Loc.T("Kapalı");
            return;
        }
        if (_fanCurveSensorDead)
        {
            TxtFanCurveStatus.Text = Loc.T($"Sensör yanıt vermiyor · fan %{_fanCurveLastPercent}");
            return;
        }
        TxtFanCurveStatus.Text = _fanCurveLastTemperatureC > 0
            ? Loc.T($"CPU {_fanCurveLastTemperatureC} °C · fan %{_fanCurveLastPercent}")
            : Loc.T("Sıcaklık bekleniyor…");
    }

    private void SliderFanCurve_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateFanCurveLabels();
        RenderFanCurve();
        UpdateFanCurvePresetButtons();
        if (!IsLoaded || _isInitializingUi || _isUpdatingFanCurveUi) return;

        CommitFanCurveFromEditor();
    }

    /// <summary>
    /// Saves the shape the editor shows and re-arms the evaluator, then lets the debounce write it to the fans.
    /// Shared by the sliders and the templates so both take exactly the same path.
    /// </summary>
    private void CommitFanCurveFromEditor()
    {
        _appSettings.FanCurvePoints = ReadFanCurveFromEditor();
        // A new shape means the next evaluation must write again, even if the target happens to match.
        _fanCurveEvaluator = _fanCurveEnabled ? new FanCurveEvaluator(_appSettings.BuildFanCurve()) : null;

        _fanCurveApplyTimer ??= CreateFanCurveApplyTimer();
        _fanCurveApplyTimer.Stop();
        _fanCurveApplyTimer.Start();
    }

    /// <summary>
    /// Fills the editor from a template. The curve is only a shape here: whether the fans follow it is the
    /// "Eğri" button's business, and this panel is only reachable while it is on.
    /// </summary>
    private void BtnCurvePreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton button) return;
        var preset = FanCurvePresets.ById(button.Tag as string);
        if (preset == null) return;

        ApplyFanCurveToEditor(new FanCurve(
            preset.ToPoints(),
            _appSettings.FanCurveHysteresisC,
            _appSettings.FanCurveMinDwellSeconds));

        if (!IsLoaded || _isInitializingUi) return;
        CommitFanCurveFromEditor();
    }

    private DispatcherTimer CreateFanCurveApplyTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        timer.Tick += async (s, e) =>
        {
            timer.Stop();
            SaveSettings();
            if (_fanCurveOwner != FanCurveOwner.Desktop) return;
            await ApplyFanCurveAsync(_fanCurveSensorDead ? 0 : _fanCurveLastTemperatureC);
        };
        return timer;
    }

    /// <summary>
    /// One step of the curve: takes the temperature the poll already fetched and writes a new custom fan speed
    /// only when the evaluator says it is worth it. A game profile's curve runs only while that game is up, and
    /// the desktop curve only outside a session, so the two can never fight over the fans. One live sensor is
    /// enough; when neither answers the curve holds full speed rather than going quiet.
    /// </summary>
    internal async Task ApplyFanCurveAsync(int cpuTemperatureC, int gpuTemperatureC = 0)
    {
        if (!_fanCurveEnabled || _isInitializingUi) return;
        if (_isGameSessionActive != (_fanCurveOwner == FanCurveOwner.Game)) return;
        if (_fanCurveEvaluator == null) return;

        // The curve follows the hotter of the two dies. A CPU sensor that sticks at 45 °C while the GPU climbs to
        // 90 °C used to leave the fans at the idle speed: the curve only ever looked at the CPU. Taking the maximum
        // costs nothing when both readings are good and is the difference between cooling and not when one is not.
        // The guard used to sit on the CPU reading alone, so a dead CPU sensor returned before the maximum was
        // ever taken and a 92 °C GPU could not raise the fans.
        int temperatureC = Math.Max(cpuTemperatureC, gpuTemperatureC);

        // Neither sensor answered. The curve asks for full speed instead of leaving the fans wherever the last
        // write put them: a curve the user turned on must not go quiet because a sensor died.
        _fanCurveSensorDead = temperatureC <= 0;
        if (_fanCurveSensorDead)
        {
            await ApplyFanCurveFullSpeedAsync();
            return;
        }

        _fanCurveLastTemperatureC = temperatureC;

        // While the curve is on it owns the fans. Anything else holding a different mode — NitroSense, the mode
        // key, a restore that ran while the curve was suspended — is taken back here, at the speed this
        // temperature asks for. Turning the curve off is the one way to hand the fans back.
        if (_currentFanMode != FanMode.Custom)
        {
            int percent = _fanCurveEvaluator.Curve.PercentAt(temperatureC);
            _agentClient.CustomFanCpuPercent = percent;
            _agentClient.CustomFanGpuPercent = percent;
            if (!await _agentClient.SetFanModeAsync(FanMode.Custom)) return;

            _currentFanMode = FanMode.Custom;
            _fanCurveLastPercent = percent;
            UpdateFanButtonsUi(_currentFanMode);
            UpdateFanCurveStatusText();
            return;
        }

        int? target = _fanCurveEvaluator.Evaluate(temperatureC, DateTime.UtcNow);
        if (target is null) return;

        _agentClient.CustomFanCpuPercent = target.Value;
        _agentClient.CustomFanGpuPercent = target.Value;
        if (!await _agentClient.SetFanModeAsync(FanMode.Custom))
        {
            // Do not believe a write that did not land: the next poll tries again.
            _fanCurveEvaluator.Reset();
            return;
        }

        _fanCurveLastPercent = target.Value;
        UpdateFanCurveStatusText();
    }

    /// <summary>
    /// Neither temperature sensor is answering. The curve holds full speed rather than the speed the last good
    /// reading asked for, because a fan curve the user turned on must not go quiet when the sensor dies.
    /// </summary>
    /// <remarks>
    /// The write happens once per arming: the evaluator is reset so the next good reading takes over cleanly, and
    /// a machine whose sensor never reports does not hammer the EC with the same value on every poll.
    /// </remarks>
    private async Task ApplyFanCurveFullSpeedAsync()
    {
        if (_currentFanMode == FanMode.Custom && _fanCurveLastPercent == FanCurve.MaxPercent) return;

        _agentClient.CustomFanCpuPercent = FanCurve.MaxPercent;
        _agentClient.CustomFanGpuPercent = FanCurve.MaxPercent;
        if (!await _agentClient.SetFanModeAsync(FanMode.Custom)) return;

        _currentFanMode = FanMode.Custom;
        _fanCurveLastPercent = FanCurve.MaxPercent;
        _fanCurveEvaluator?.Reset();
        UpdateFanButtonsUi(_currentFanMode);
        UpdateFanCurveStatusText();
    }

    /// <summary>
    /// Turns the curve on or off. On, it takes the fans at the speed the current temperature asks for; off, it
    /// hands them back to the firmware instead of leaving them pinned at the last curve speed.
    /// </summary>
    internal async Task<bool> SetFanCurveEnabledAsync(bool enabled, bool quiet = false)
    {
        if (enabled == _fanCurveEnabled)
        {
            UpdateFanButtonsUi(_currentFanMode);
            return true;
        }

        if (enabled)
        {
            _fanCurveOwner = FanCurveOwner.Desktop;
            _appSettings.FanCurveEnabled = true;
            _fanCurveEvaluator = new FanCurveEvaluator(_appSettings.BuildFanCurve());
            SaveSettings();
            UpdateFanButtonsUi(_currentFanMode);

            int percent = _fanCurveLastTemperatureC > 0
                ? _fanCurveEvaluator.Curve.PercentAt(_fanCurveLastTemperatureC)
                : _appSettings.CustomFanCpuPercent;
            _agentClient.CustomFanCpuPercent = percent;
            _agentClient.CustomFanGpuPercent = percent;
            if (!await _agentClient.SetFanModeAsync(FanMode.Custom))
            {
                DisableFanCurveState();
                UpdateFanButtonsUi(_currentFanMode);
                if (!quiet) ShowToast(Loc.T("Fan eğrisi başlatılamadı."), false);
                return false;
            }

            _currentFanMode = FanMode.Custom;
            _fanCurveLastPercent = percent;
            // The power automation restores this fan mode on an adapter change, so it has to know the curve owns it.
            _powerAutomationService?.UpdateManualFanMode(FanMode.Custom);
            UpdateFanButtonsUi(_currentFanMode);
            UpdateFanCurveStatusText();
            if (!quiet) ShowToast(Loc.T("Fan eğrisi etkin: fan hızı CPU sıcaklığını izliyor."));
            return true;
        }

        DisableFanCurveState();

        bool ok = await _agentClient.SetFanModeAsync(FanMode.Auto);
        if (ok)
        {
            _currentFanMode = FanMode.Auto;
            _powerAutomationService?.UpdateManualFanMode(FanMode.Auto);
        }
        LoadCustomFanSettings();
        UpdateFanButtonsUi(_currentFanMode);
        if (!quiet) ShowToast(ok ? Loc.T("Fan eğrisi kapandı; fanlar otomatik moda döndü.") : Loc.T("Fan eğrisi kapandı."), ok);
        return ok;
    }

    /// <summary>
    /// Forgets the curve without touching the fans. The caller decides what mode they should be in. Only a curve
    /// the fan card armed clears the saved preference: a game profile handing back must leave the desktop setting
    /// exactly as the user left it.
    /// </summary>
    private void DisableFanCurveState()
    {
        if (_fanCurveOwner == FanCurveOwner.Desktop) _appSettings.FanCurveEnabled = false;
        _fanCurveOwner = FanCurveOwner.None;
        _fanCurveEvaluator = null;
        SaveSettings();
        UpdateFanCurveStatusText();
    }

    private async void BtnFanCurve_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await SetFanCurveEnabledAsync(!_fanCurveEnabled);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Fan eğrisi açılamadı", ex.ToString());
            ShowToast(Loc.T($"Fan eğrisi değiştirilemedi: {ex.Message}"), false);
        }
    }

    /// <summary>
    /// Arms the curve a game profile asked for. The shape is the profile's own anchors when it has them and the
    /// desktop curve otherwise, so a profile that only wants "my curve, but for this game" needs no second editor.
    /// </summary>
    internal async Task<bool> ArmGameFanCurveAsync(GameProfile profile)
    {
        var curve = new FanCurve(
            profile.TargetFanCurvePoints ?? _appSettings.FanCurvePoints,
            _appSettings.FanCurveHysteresisC,
            _appSettings.FanCurveMinDwellSeconds);

        _fanCurveOwner = FanCurveOwner.Game;
        _fanCurveEvaluator = new FanCurveEvaluator(curve);

        int percent = _fanCurveLastTemperatureC > 0
            ? curve.PercentAt(_fanCurveLastTemperatureC)
            : _appSettings.CustomFanCpuPercent;
        _agentClient.CustomFanCpuPercent = percent;
        _agentClient.CustomFanGpuPercent = percent;
        if (!await _agentClient.SetFanModeAsync(FanMode.Custom))
        {
            _fanCurveOwner = FanCurveOwner.None;
            _fanCurveEvaluator = null;
            return false;
        }

        _currentFanMode = FanMode.Custom;
        _fanCurveLastPercent = percent;
        UpdateFanButtonsUi(_currentFanMode);
        UpdateFanCurveStatusText();
        return true;
    }

    /// <summary>
    /// Hands the fans back after a game profile's curve ran. When the user had the desktop curve on, it is re-armed
    /// here rather than left to the next poll, so a session never ends on a fixed speed the user did not choose.
    /// </summary>
    internal async Task ReleaseGameFanCurveAsync()
    {
        if (_fanCurveOwner != FanCurveOwner.Game) return;
        _fanCurveOwner = FanCurveOwner.None;
        _fanCurveEvaluator = null;
        UpdateFanCurveStatusText();
        if (_appSettings.FanCurveEnabled) await SetFanCurveEnabledAsync(true, quiet: true);
    }

    /// <summary>
    /// Fills the per-game curve editor from a profile. A profile without its own anchors shows the desktop curve,
    /// which is exactly what it would run, so the sliders never claim a shape that is not in effect.
    /// </summary>
    private void LoadGameFanCurveEditor(GameProfile profile)
    {
        var curve = new FanCurve(
            profile.TargetFanCurvePoints ?? _appSettings.FanCurvePoints,
            _appSettings.FanCurveHysteresisC,
            _appSettings.FanCurveMinDwellSeconds);

        var sliders = GameFanCurveSliders;
        var labels = GameFanCurveLabels;
        _isUpdatingFanCurveUi = true;
        try
        {
            // Read through the curve rather than off its anchor list: a profile saved with fewer than six anchors
            // still fills every slider with the speed that anchor temperature would actually produce.
            for (int i = 0; i < sliders.Length && i < FanCurve.AnchorCount; i++)
            {
                int percent = curve.PercentAt(FanCurve.AnchorTemperatures[i]);
                if (sliders[i] != null) sliders[i]!.Value = percent;
                if (labels[i] != null) labels[i]!.Text = $"%{percent}";
            }
        }
        finally
        {
            _isUpdatingFanCurveUi = false;
        }

        if (ChkGameFanCurve != null) ChkGameFanCurve.IsChecked = profile.TargetFanCurveEnabled;
        if (PnlGameFanCurve != null)
            PnlGameFanCurve.Visibility = profile.TargetFanCurveEnabled ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The six anchors the per-game editor is showing, or null before the window is built.</summary>
    private List<FanCurvePoint>? ReadGameFanCurveFromEditor()
    {
        var sliders = GameFanCurveSliders;
        if (sliders.Any(s => s == null)) return null;

        var points = new List<FanCurvePoint>(FanCurve.AnchorCount);
        for (int i = 0; i < FanCurve.AnchorCount; i++)
            points.Add(new FanCurvePoint(FanCurve.AnchorTemperatures[i], (int)sliders[i]!.Value));
        return points;
    }

    private void UpdateGameFanCurveLabels()
    {
        var sliders = GameFanCurveSliders;
        var labels = GameFanCurveLabels;
        for (int i = 0; i < sliders.Length && i < labels.Length; i++)
        {
            if (sliders[i] == null || labels[i] == null) continue;
            labels[i]!.Text = $"%{(int)sliders[i]!.Value}";
        }
    }

    private void SliderGameFanCurve_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) =>
        UpdateGameFanCurveLabels();

    private void ChkGameFanCurve_Changed(object sender, RoutedEventArgs e)
    {
        if (PnlGameFanCurve == null) return;
        PnlGameFanCurve.Visibility = ChkGameFanCurve?.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Copies the fan card's curve into the selected game's profile. The editor already shows the desktop shape
    /// when the profile has none, so this is for the case where the user changed the desktop curve after the
    /// profile was saved and wants the game to follow the new one.
    /// </summary>
    private void BtnGameCurveCopyDesktop_Click(object sender, RoutedEventArgs e)
    {
        var desktop = _appSettings.BuildFanCurve();
        var sliders = GameFanCurveSliders;
        var labels = GameFanCurveLabels;
        _isUpdatingFanCurveUi = true;
        try
        {
            for (int i = 0; i < sliders.Length && i < FanCurve.AnchorCount; i++)
            {
                int percent = desktop.PercentAt(FanCurve.AnchorTemperatures[i]);
                if (sliders[i] != null) sliders[i]!.Value = percent;
                if (labels[i] != null) labels[i]!.Text = $"%{percent}";
            }
        }
        finally
        {
            _isUpdatingFanCurveUi = false;
        }
        ShowToast(Loc.T("Masaüstü fan eğrisi bu oyuna kopyalandı; kaydetmeyi unutmayın."));
    }

    private void CanvasFanCurve_SizeChanged(object sender, SizeChangedEventArgs e) => RenderFanCurve();

    /// <summary>
    /// Draws the curve on the recording strip: temperature across (30–100 °C), fan speed up (30–100 %), one ink
    /// trace through the anchors and a vermilion mark where the machine is right now.
    /// </summary>
    private void RenderFanCurve()
    {
        if (CanvasFanCurve == null || CanvasFanCurveGrid == null || FanCurveLine == null) return;

        double width = CanvasFanCurve.ActualWidth;
        double height = CanvasFanCurve.ActualHeight;
        if (width < 40 || height < 30) return;

        var rule = TryFindResource("Rule") as WpfBrush ?? System.Windows.Media.Brushes.Gray;
        var ink = TryFindResource("Ink") as WpfBrush ?? System.Windows.Media.Brushes.Black;

        const double padX = 6, padY = 6;
        double plotW = width - padX * 2;
        double plotH = height - padY * 2;

        double X(double temperatureC) =>
            padX + (Math.Clamp(temperatureC, FanCurve.MinTemperatureC, FanCurve.MaxTemperatureC) - FanCurve.MinTemperatureC)
            / (double)(FanCurve.MaxTemperatureC - FanCurve.MinTemperatureC) * plotW;
        double Y(double percent) =>
            padY + (1 - (Math.Clamp(percent, FanCurve.MinPercent, FanCurve.MaxPercent) - FanCurve.MinPercent)
            / (double)(FanCurve.MaxPercent - FanCurve.MinPercent)) * plotH;

        // Grid: one vertical rule per anchor temperature, three horizontal rules for the speed bands.
        CanvasFanCurveGrid.Children.Clear();
        foreach (int temperature in FanCurve.AnchorTemperatures)
        {
            double x = X(temperature);
            CanvasFanCurveGrid.Children.Add(new WpfLine
            {
                X1 = x, X2 = x, Y1 = padY, Y2 = padY + plotH,
                Stroke = rule, StrokeThickness = 1, SnapsToDevicePixels = true
            });
        }
        foreach (int percent in new[] { FanCurve.MinPercent, 65, FanCurve.MaxPercent })
        {
            double y = Y(percent);
            CanvasFanCurveGrid.Children.Add(new WpfLine
            {
                X1 = padX, X2 = padX + plotW, Y1 = y, Y2 = y,
                Stroke = rule, StrokeThickness = 1, SnapsToDevicePixels = true
            });
        }

        // The trace is sampled from the curve instead of drawn through the anchors, so the safety floor above
        // 85 °C shows up as the step the fans will actually make.
        var curve = new FanCurve(ReadFanCurveFromEditor());
        var points = new PointCollection();
        for (int temperature = FanCurve.MinTemperatureC; temperature <= FanCurve.MaxTemperatureC; temperature++)
            points.Add(new WpfPoint(X(temperature), Y(curve.PercentAt(temperature))));
        FanCurveLine.Points = points;
        FanCurveLine.Stroke = ink;

        // Where the machine is right now.
        if (FanCurveMarker != null)
        {
            if (_fanCurveEnabled && _fanCurveLastTemperatureC > 0)
            {
                FanCurveMarker.Visibility = Visibility.Visible;
                Canvas.SetLeft(FanCurveMarker, X(_fanCurveLastTemperatureC) - FanCurveMarker.Width / 2);
                Canvas.SetTop(FanCurveMarker, Y(_fanCurveLastPercent) - FanCurveMarker.Height / 2);
            }
            else
            {
                FanCurveMarker.Visibility = Visibility.Collapsed;
            }
        }
    }

    internal async Task<bool> SetManualFanModeAsync(FanMode mode)
    {
        // Choosing a fan mode by hand is a statement that the curve should stop: otherwise the next poll would
        // take the fans straight back and the button would look broken.
        if (_fanCurveEnabled) DisableFanCurveState();

        await _gameProfileTransitionLock.WaitAsync();
        try
        {
            bool ok = await _agentClient.SetFanModeAsync(mode);
            if (ok)
            {
                _currentFanMode = mode;
                UpdateDesktopBaselineForManualChange(fan: mode);
                // The curve panel has to close the moment the user picks another mode, not on the next sync.
                UpdateFanButtonsUi(mode);
                ShowToast(mode switch
                {
                    FanMode.Auto => Loc.T("Otomatik fan eğrisi etkinleştirildi."),
                    FanMode.Max => Loc.T("Maksimum fan devri etkinleştirildi."),
                    _ => Loc.T($"Özel fan hızı: CPU %{_agentClient.CustomFanCpuPercent}, GPU %{_agentClient.CustomFanGpuPercent}.")
                });
            }
            else
            {
                ShowToast(Loc.T("Fan modu uygulanamadı."), false);
            }
            return ok;
        }
        catch (Exception ex)
        {
            ShowToast(Loc.T($"Fan modu uygulanamadı: {ex.Message}"), false);
            return false;
        }
        finally
        {
            _gameProfileTransitionLock.Release();
        }
    }

    private async void BtnFanAuto_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await SetManualFanModeAsync(FanMode.Auto);
        }
        catch (Exception ex)
        {
            // SetManualFanModeAsync handles its own failures; this covers the step before it, where turning the
            // curve off saves the settings file and a full or read-only disk can throw.
            AppLog.Warn("Fan modu düğmesi başarısız", ex.ToString());
            ShowToast(Loc.T($"Fan modu uygulanamadı: {ex.Message}"), false);
        }
    }

    private async void BtnFanMax_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await SetManualFanModeAsync(FanMode.Max);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Fan modu düğmesi başarısız", ex.ToString());
            ShowToast(Loc.T($"Fan modu uygulanamadı: {ex.Message}"), false);
        }
    }
}
