using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using NitroLite.Core;
using NitroLite.Core.Hardware.OpenRgb;
using NitroLite.Core.Hardware.Wmi;
using WpfBrush = System.Windows.Media.Brush;
using WpfButton = System.Windows.Controls.Button;
using WpfPoint = System.Windows.Point;

namespace NitroLite;

public partial class MainWindow
{
    /// <summary>
    /// The colour the user last chose, from a preset swatch, the picker or a single zone. "Apply to all zones" spreads
    /// this one rather than whatever sits in the first slot, which after a per-zone edit is a different colour.
    /// </summary>
    private string? _lastPickedColor;

    private void SaveActiveRgbState()
    {
        try
        {
            var json = JsonSerializer.Serialize(_lighting, new JsonSerializerOptions { WriteIndented = true });
            AtomicFile.WriteAllText(ActiveRgbConfigFile, json);
        }
        catch (Exception ex)
        {
            // The keyboard keeps the last applied lighting, but the next start would not know what it was.
            AppLog.Warn("Etkin aydınlatma durumu kaydedilemedi", $"{ActiveRgbConfigFile}: {ex.Message}");
        }
    }

    public void CancelActiveLightingReassertion()
    {
        try
        {
            _lightingReassertCts?.Cancel();
            _lightingReassertCts?.Dispose();
            _lightingReassertCts = null;
        }
        catch { }
    }

    public async Task ReassertKeyboardLightingAsync(bool isBoot = false)
    {
        // NitroSense also restores the keyboard lighting; re-applying ours on resume/unlock would fight it.
        if (_debloatService.IsNitroSenseRunning()) return;

        CancelActiveLightingReassertion();
        var cts = new CancellationTokenSource();
        _lightingReassertCts = cts;
        var token = cts.Token;

        // Staged re-assertion pulses with socket reconnect retry
        // to override Acer EC firmware default resets, AcerLightingService boot triggers, and sleep wakeups.
        int[] delays = isBoot
            ? new[] { 500, 2000, 5000, 10000, 18000 }
            : new[] { 250, 1500 };

        // Nothing here was asked for by the user, so it must not raise an elevation prompt: a saved four-zone
        // configuration is re-applied through the helper only when one is already running.
        var hybrid = _hardware as HybridHardwareController;
        bool previousElevation = hybrid?.AllowElevationForLighting ?? false;
        if (hybrid != null) hybrid.AllowElevationForLighting = false;

        try
        {
            foreach (var delay in delays)
            {
                await Task.Delay(delay, token);
                if (token.IsCancellationRequested) return;

                for (int retry = 0; retry < 3; retry++)
                {
                    if (token.IsCancellationRequested) return;
                    try
                    {
                        bool shouldBeOff = _appSettings.IsBacklightOff || 
                                           _lighting.Brightness == 0 || 
                                           _lighting.Effect.Equals("OFF", StringComparison.OrdinalIgnoreCase) ||
                                           SelectedPresetTurnsLightingOff();

                        bool success = shouldBeOff
                            ? await _hardware.TurnOffLightingAsync()
                            : await _hardware.SetLightingAsync(_lighting.Effect, _lighting.Brightness, _lighting.Speed, _lighting.Direction, _lighting.Color, _lighting.LEDs.ToArray());

                        if (success)
                        {
                            break;
                        }

                        await Task.Delay(200, token);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch
                    {
                        await Task.Delay(200, token);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Gracefully canceled when user changed lighting or window reopened
        }
        finally
        {
            if (hybrid != null) hybrid.AllowElevationForLighting = previousElevation;
        }
    }

    // ==========================================
    // RGB Preset Manager (Module 4) & Persistence
    // ==========================================

    private void PopulateRgbPresets(string? selectPresetName = null)
    {
        if (CmbRgbPresets == null) return;

        var presets = _rgbProfileService.GetPresets();
        // The combo shows DisplayName; the stored identity stays RgbPreset.Name so a language change cannot
        // orphan the user's saved selection.
        CmbRgbPresets.ItemsSource = presets.Select(p => p.DisplayName).ToList();

        string? target = selectPresetName;
        if (string.IsNullOrEmpty(target) && !string.IsNullOrEmpty(_appSettings.SelectedRgbPreset))
        {
            target = _appSettings.SelectedRgbPreset;
        }

        if (!string.IsNullOrEmpty(target) && presets.Any(p => p.Name.Equals(target, StringComparison.OrdinalIgnoreCase)))
        {
            var matched = presets.First(p => p.Name.Equals(target, StringComparison.OrdinalIgnoreCase)).DisplayName;
            CmbRgbPresets.SelectedItem = matched;
        }
        else if (_appSettings.IsBacklightOff)
        {
            var offPreset = presets.FirstOrDefault(p => p.Effect.Equals("OFF", StringComparison.OrdinalIgnoreCase) || p.Brightness == 0);
            if (offPreset != null)
            {
                CmbRgbPresets.SelectedItem = offPreset.DisplayName;
            }
            else
            {
                CmbRgbPresets.SelectedItem = null;
            }
        }
        else
        {
            CmbRgbPresets.SelectedItem = null;
        }
    }

    /// <summary>The preset the combo is showing, or null when nothing is selected.</summary>
    private RgbPreset? SelectedRgbPreset()
    {
        if (CmbRgbPresets?.SelectedItem is not string shownName) return null;
        return _rgbProfileService.GetPresets().FirstOrDefault(p => p.DisplayName == shownName);
    }

    /// <summary>
    /// True when the stored preset name points at a preset that turns the backlight off. The name is the stable
    /// identity, so this keeps working when the UI language changes.
    /// </summary>
    private bool SelectedPresetTurnsLightingOff()
    {
        var name = _appSettings.SelectedRgbPreset;
        if (string.IsNullOrEmpty(name)) return false;
        var preset = _rgbProfileService.GetPresets()
            .FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        return preset != null &&
               (preset.Effect.Equals("OFF", StringComparison.OrdinalIgnoreCase) || preset.Brightness == 0);
    }

    private async void CmbRgbPresets_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _isInitializingUi || _isApplyingPreset || CmbRgbPresets.SelectedItem is not string selectedName) return;

        var preset = _rgbProfileService.GetPresets().FirstOrDefault(p => p.DisplayName == selectedName);
        if (preset == null) return;

        _isApplyingPreset = true;
        CancelActiveLightingReassertion();
        try
        {
            _lighting.Effect = preset.Effect;
            _lighting.Brightness = preset.Brightness;
            _lighting.Speed = preset.Speed;
            _lighting.Direction = preset.Direction;
            _lighting.Color = preset.Color;
            _lighting.LEDs = new List<string>(preset.LEDs);

            UpdateLightingUi(_lighting);
            UpdateEffectComboBox(preset.Effect);

            bool isOff = IsLightingOff(_lighting);
            if (!await ApplyLightingToDeviceAsync(Loc.T($"'{selectedName}' profili uygulanamadı"))) return;

            _appSettings.SelectedRgbPreset = preset.Name;
            _appSettings.IsBacklightOff = isOff;
            _appSettings.KeyboardLighting = _lighting;
            PersistLightingState();
        }
        catch (Exception ex)
        {
            ShowToast(Loc.T($"Aydınlatma profili uygulanamadı: {ex.Message}"), false);
        }
        finally
        {
            _isApplyingPreset = false;
        }
    }

    private void UpdateEffectComboBox(string effect)
    {
        if (CmbRgbEffect == null) return;
        for (int i = 0; i < CmbRgbEffect.Items.Count; i++)
        {
            if (CmbRgbEffect.Items[i] is not ComboBoxItem item) continue;
            var name = item.Tag as string ?? (item.Content as string)?.Split(' ')[0];
            if (string.Equals(name, effect, StringComparison.OrdinalIgnoreCase))
            {
                CmbRgbEffect.SelectedIndex = i;
                break;
            }
        }
    }

    /// <summary>
    /// The effect name behind the current selection: from Tag when the list was rebuilt from the device report, and
    /// from the label prefix when it is still the list the XAML carries.
    /// </summary>
    private string? SelectedEffectName()
    {
        if (CmbRgbEffect?.SelectedItem is not ComboBoxItem item) return null;
        if (item.Tag is string tag && tag.Length > 0) return tag;
        return item.Content is string text ? text.Split(' ')[0] : null;
    }

    /// <summary>
    /// Replaces the effect dropdown with the effects this keyboard reports. An empty result leaves the XAML list in
    /// place: a probe that named almost nothing is not evidence that the keyboard has almost nothing.
    /// </summary>
    private void RebuildEffectList()
    {
        if (CmbRgbEffect == null) return;
        var offered = LightingPolicy.OfferedEffects(_keyboardCapability?.Effects.Select(e => e.Name));
        if (offered.Count == 0) return;

        var current = SelectedEffectName() ?? _lighting.Effect;
        _isRebuildingEffectList = true;
        try
        {
            CmbRgbEffect.Items.Clear();
            foreach (var name in offered)
            {
                CmbRgbEffect.Items.Add(new ComboBoxItem
                {
                    Content = LightingPolicy.EffectDisplayName(name),
                    Tag = name
                });
            }
            UpdateEffectComboBox(current);
        }
        finally
        {
            _isRebuildingEffectList = false;
        }
    }

    private void BtnSaveCustomPreset_Click(object sender, RoutedEventArgs e)
    {
        var name = TxtCustomPresetName.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            name = $"Profil {DateTime.Now:HH:mm}";
        }

        try
        {
            _rgbProfileService.SaveCustomPreset(name, _lighting.Effect, _lighting.Brightness, _lighting.Speed, _lighting.Direction, _lighting.Color, _lighting.LEDs);
        }
        catch (ArgumentException ex)
        {
            ShowToast(ex.Message, false);
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowToast(Loc.T($"Aydınlatma profili kaydedilemedi: {ex.Message}"), false);
            return;
        }

        _appSettings.SelectedRgbPreset = name;
        _appSettings.KeyboardLighting = _lighting;
        _appSettings.IsBacklightOff = _lighting.Brightness == 0 || _lighting.Effect.Equals("OFF", StringComparison.OrdinalIgnoreCase);
        SaveSettings();
        SaveActiveRgbState();

        PopulateRgbPresets(name);
        TxtCustomPresetName.Clear();

        ShowToast(Loc.T($"'{name}' aydınlatma profili kaydedildi."));
    }

    private void BtnDeleteCustomPreset_Click(object sender, RoutedEventArgs e)
    {
        var preset = SelectedRgbPreset();
        if (preset == null)
        {
            ShowToast(Loc.T("Silmek için listeden bir profil seçin."), false);
            return;
        }

        if (preset.IsBuiltIn)
        {
            ShowToast(Loc.T("Varsayılan fabrika profilleri silinemez."), false);
            return;
        }

        var confirm = System.Windows.MessageBox.Show(this,
            Loc.T($"'{preset.DisplayName}' aydınlatma profilini silmek istiyor musunuz?"),
            Loc.T("Profili Sil"), MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        if (!_rgbProfileService.DeletePreset(preset.Name))
        {
            ShowToast(Loc.T("Profil silinemedi."), false);
            return;
        }

        // Clear the combo first: SaveSettings() copies the combo's selection into SelectedRgbPreset,
        // which used to write the just-deleted name back.
        MarkLightingCustomized();
        SaveSettings();
        PopulateRgbPresets();
        ShowToast(Loc.T($"'{preset.DisplayName}' profili silindi."));
    }

    /// <summary>
    /// Shows a warning on the RGB tab when the Acer service explicitly reports that this model has no
    /// controllable keyboard lighting. A null answer (service absent, unknown reply) changes nothing.
    /// </summary>
    private async Task RefreshLightingCapabilityAsync()
    {
        bool? supported;
        try
        {
            supported = await _agentClient.GetLightingCapabilityAsync();
        }
        catch
        {
            return;
        }

        if (supported != false) return;

        Dispatcher.Invoke(() =>
        {
            if (Dispatcher.HasShutdownStarted) return;
            if (BannerLightingUnsupported != null) BannerLightingUnsupported.Visibility = Visibility.Visible;
            if (TxtLightingUnsupported != null)
                TxtLightingUnsupported.Text = Loc.T("Acer servisi bu modelde denetlenebilir klavye aydınlatması bildirmedi. "
                    + "Aşağıdaki denetimler etkisiz kalabilir; RGB servisini onarmayı deneyebilirsiniz.");
        });
    }

    /// <summary>
    /// Looks for an OpenRGB SDK server on <c>127.0.0.1:6742</c> and says so on the RGB tab when one answers.
    /// <para>
    /// OpenRGB is the third lighting path: it needs neither Acer's service nor a per-chassis HID packet layout, so on
    /// a machine where the service reports no controllable lighting it may still be the one that works. Nothing is
    /// installed and nothing is started — a machine without OpenRGB simply keeps the line hidden.
    /// </para>
    /// </summary>
    private async Task RefreshOpenRgbStatusAsync()
    {
        if (TxtOpenRgbStatus == null) return;

        string? text = null;
        try
        {
            using var client = new OpenRgbClient();
            if (await client.ConnectAsync(TimeSpan.FromMilliseconds(600)))
            {
                int? count = await client.GetControllerCountAsync();
                text = count is > 0
                    ? Loc.T($"OpenRGB SDK etkin (127.0.0.1:6742): {count} cihaz. Klavye renkleri OpenRGB üzerinden de yönetilebilir.")
                    : Loc.T("OpenRGB SDK etkin (127.0.0.1:6742) ancak cihaz bildirmiyor.");
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("OpenRGB durumu okunamadı", ex.Message);
        }

        if (Dispatcher.HasShutdownStarted) return;
        TxtOpenRgbStatus.Text = text ?? string.Empty;
        TxtOpenRgbStatus.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SetLightingSlidersSilently(Action update)
    {
        bool previous = _isSyncingLightingUi;
        _isSyncingLightingUi = true;
        try { update(); }
        finally { _isSyncingLightingUi = previous; }
    }

    private static bool IsLightingOff(LightingConfig cfg) =>
        cfg.Brightness == 0 || string.Equals(cfg.Effect, "OFF", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Sends the current <see cref="_lighting"/> to the hardware controller and reports a failure to the user.
    /// Cancels any pending debounced slider apply, since this call already carries the latest state.
    /// </summary>
    private async Task<bool> ApplyLightingToDeviceAsync(string failurePrefix)
    {
        _lightingApplyTimer?.Stop();
        var cfg = _lighting;
        bool ok = IsLightingOff(cfg)
            ? await _hardware.TurnOffLightingAsync()
            : await _hardware.SetLightingAsync(cfg.Effect, cfg.Brightness, cfg.Speed, cfg.Direction, cfg.Color, cfg.LEDs.ToArray());

        // Four different zone colours only reach the keyboard through the firmware. When that path is not available
        // the service lights a single colour and still answers success, so the refusal has to be named here or the
        // user is left looking at a keyboard that ignored three of their four choices.
        bool? zoneColorsApplied = (_hardware as HybridHardwareController)?.LastZoneColorsApplied;

        if (!ok && !_isExiting)
        {
            ShowToast(Loc.T($"{failurePrefix}: klavye komutu reddedildi veya Acer servisine ulaşılamadı. Ayar kaydedilmedi."), false);
        }
        else if (zoneColorsApplied == false && !_isExiting)
        {
            ShowToast(Loc.T("Bölge bölge renk için doğrudan WMI yolu gerekiyor; Acer servisi dört ayrı rengi tutamıyor. Ayarlar > Doğrudan WMI arka ucu > Kontrolü başlat."), false);
        }
        else if (ok && zoneColorsApplied == null && _agentClient.LastLightingPartialFailure && !_isExiting)
        {
            ShowToast(Loc.T("Işık komutunu iki hedeften (genel / klavye) yalnızca biri kabul etti; ayar kısmen uygulanmış olabilir."), false);
        }

        // Remember the last lit configuration so brightness/effect changes after "off" can bring it back.
        if (ok && !IsLightingOff(cfg))
        {
            _appSettings.LastActiveLighting = LightingPolicy.Clone(cfg);
        }
        return ok;
    }

    /// <summary>Any manual change means the lighting no longer matches the selected preset: clear the selection
    /// (so re-selecting the same preset applies it again) and stop saving its name.</summary>
    private void MarkLightingCustomized()
    {
        _appSettings.SelectedRgbPreset = null;
        if (CmbRgbPresets?.SelectedItem == null) return;
        bool wasApplying = _isApplyingPreset;
        _isApplyingPreset = true;
        try { CmbRgbPresets.SelectedItem = null; }
        finally { _isApplyingPreset = wasApplying; }
    }

    private static WpfBrush SolidOrFallback(string? hex)
    {
        try { return (new BrushConverter().ConvertFromString(hex ?? "#000000") as WpfBrush) ?? System.Windows.Media.Brushes.Black; }
        catch { return System.Windows.Media.Brushes.Black; }
    }

    /// <summary>Rainbow sample for effects that use the keyboard's own colours (each zone shifted).</summary>
    private static WpfBrush RainbowBrush(int zone)
    {
        var hues = new[] { "#FF2A55", "#FFAA00", "#00FFA3", "#00E5FF", "#BD00FF", "#FF2A55" };
        var brush = new LinearGradientBrush { StartPoint = new WpfPoint(0, 0.5), EndPoint = new WpfPoint(1, 0.5) };
        for (int i = 0; i < 3; i++)
        {
            var c = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hues[(zone + i) % hues.Length]);
            brush.GradientStops.Add(new GradientStop(c, i / 2.0));
        }
        return brush;
    }

    /// <summary>
    /// Shows what the keyboard will actually do: zone colours only for STATIC, one colour for the other
    /// colour effects, a rainbow sample for WAVE/NEON (which ignore the chosen colour), black when off.
    /// </summary>
    private void UpdatePreviewZones(LightingConfig cfg)
    {
        var zones = new[] { PreviewZone1, PreviewZone2, PreviewZone3, PreviewZone4 };
        string note;
        for (int i = 0; i < zones.Length; i++)
        {
            if (zones[i] == null) continue;
            // Off is an unlit slot, not a solid block: an ink fill read as a dead black rectangle in the light theme
            // and a white one in the dark, and neither said "this zone is a control you can click". CardBg rather
            // than PaperDeep, because the zones now sit on a PaperDeep deck and would otherwise vanish into it.
            if (IsLightingOff(cfg)) zones[i].Background = TryFindResource("CardBg") as SolidColorBrush ?? System.Windows.Media.Brushes.Transparent;
            else if (!LightingPolicy.EffectUsesColor(cfg.Effect)) zones[i].Background = RainbowBrush(i);
            else if (LightingPolicy.EffectUsesZoneColors(cfg.Effect)) zones[i].Background = SolidOrFallback(i < cfg.LEDs.Count ? cfg.LEDs[i] : cfg.Color);
            else zones[i].Background = SolidOrFallback(cfg.Color);

        }

        if (IsLightingOff(cfg))
            note = "";
        else if (!LightingPolicy.EffectUsesColor(cfg.Effect))
            note = Loc.T("Bu efekt klavyenin kendi renk döngüsünü kullanır; seçilen renk uygulanmaz.");
        else if (!LightingPolicy.EffectUsesZoneColors(cfg.Effect))
            note = Loc.T("Bu efekt tek renk kullanır; bölgelere ayrı renk yalnızca Sabit Renk (STATIC) efektinde verilebilir.");
        else
            note = "";

        if (TxtZonePreviewNote != null)
        {
            TxtZonePreviewNote.Text = note;
            TxtZonePreviewNote.Visibility = note.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>Shows the direction picker only for effects that support it, with only the allowed directions.</summary>
    private void UpdateLightingControlsForEffect(LightingConfig cfg)
    {
        var allowed = IsLightingOff(cfg) ? Array.Empty<int>() : LightingPolicy.AllowedDirections(cfg.Effect);
        if (PnlRgbDirection != null) PnlRgbDirection.Visibility = allowed.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (CmbRgbDirection != null)
        {
            _isSyncingDirectionUi = true;
            try
            {
                CmbRgbDirection.Items.Clear();
                foreach (var d in allowed)
                {
                    CmbRgbDirection.Items.Add(new ComboBoxItem { Content = LightingPolicy.DirectionName(d), Tag = d });
                }
                int idx = allowed.ToList().IndexOf(cfg.Direction);
                CmbRgbDirection.SelectedIndex = allowed.Count > 0 ? Math.Max(0, idx) : -1;
            }
            finally
            {
                _isSyncingDirectionUi = false;
            }
        }

        if (TxtRgbEffectNote != null)
        {
            string note = IsLightingOff(cfg) || LightingPolicy.EffectUsesColor(cfg.Effect)
                ? ""
                : Loc.T("Renk butonları bu efektte kullanılmaz; bir renk seçerseniz Sabit Renk'e geçilir.");
            TxtRgbEffectNote.Text = note;
            TxtRgbEffectNote.Visibility = note.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void PersistLightingState()
    {
        SaveSettings();
        SaveActiveRgbState();
    }

    /// <summary>Coalesces rapid slider changes into one lighting command + one settings write.</summary>
    private void ScheduleLightingApply()
    {
        if (_lightingApplyTimer == null)
        {
            _lightingApplyTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher) { Interval = LightingApplyDebounce };
            _lightingApplyTimer.Tick += OnLightingApplyTimerTick;
        }
        _lightingApplyTimer.Stop();
        _lightingApplyTimer.Start();
    }

    private async void OnLightingApplyTimerTick(object? sender, EventArgs e)
    {
        _lightingApplyTimer?.Stop();
        if (_isExiting || Dispatcher.HasShutdownStarted) return;
        try
        {
            if (await ApplyLightingToDeviceAsync(Loc.T("Aydınlatma ayarı uygulanamadı")))
            {
                PersistLightingState();
            }
        }
        catch (Exception ex)
        {
            ShowToast(Loc.T($"Aydınlatma ayarı uygulanamadı: {ex.Message}"), false);
        }
    }

    private void UpdateLightingUi(LightingConfig cfg)
    {
        SetLightingSlidersSilently(() =>
        {
            if (SliderBrightness != null) SliderBrightness.Value = cfg.Brightness;
            if (SliderSpeed != null) SliderSpeed.Value = cfg.Speed;
        });
        if (TxtBrightnessVal != null)
            TxtBrightnessVal.Text = IsLightingOff(cfg) ? Loc.T("0 / 9 (Kapalı)") : $"{cfg.Brightness} / 9";
        if (TxtSpeedVal != null) TxtSpeedVal.Text = $"{cfg.Speed} / 9";

        UpdatePreviewZones(cfg);
        UpdateLightingControlsForEffect(cfg);
    }

    private async void BtnPresetColor_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is WpfButton btn && btn.Tag is string hex)
            {
                if (hex == "#000000")
                {
                    CancelActiveLightingReassertion();
                    await TurnOffLightsAsync();
                    return;
                }
                await ApplyColorToAllZonesAsync(hex);
            }
        }
        catch (Exception ex)
        {
            ShowToast(Loc.T($"Aydınlatma rengi ayarlanamadı: {ex.Message}"), false);
        }
    }

    /// <summary>
    /// One colour for the whole keyboard. Effects that take a colour (STATIC, BREATHING, ZOOM…) keep running with
    /// the new colour; WAVE/NEON ignore colours, so the keyboard switches to STATIC. When the lights are off, the
    /// last lit settings come back with this colour.
    /// </summary>
    private async Task ApplyColorToAllZonesAsync(string hex)
    {
        CancelActiveLightingReassertion();
        _lastPickedColor = hex;
        if (IsLightingOff(_lighting))
        {
            _lighting = LightingPolicy.ResumeFromOff(_appSettings.LastActiveLighting);
        }
        if (!LightingPolicy.EffectUsesColor(_lighting.Effect))
        {
            _lighting.Effect = "STATIC";
        }
        _lighting.Color = hex;
        _lighting.LEDs = new List<string> { hex, hex, hex, hex };

        MarkLightingCustomized();
        UpdateLightingUi(_lighting);
        SuppressEffectComboEvents(() => UpdateEffectComboBox(_lighting.Effect));
        if (!await ApplyLightingToDeviceAsync(Loc.T("Aydınlatma rengi ayarlanamadı"))) return;
        _appSettings.IsBacklightOff = false;
        PersistLightingState();
    }

    /// <summary>Opens the Windows colour picker; returns "#RRGGBB" or null when cancelled.</summary>
    private static string? PickColor(string? initialHex)
    {
        using var dlg = new System.Windows.Forms.ColorDialog { FullOpen = true, AnyColor = true };
        try
        {
            var c = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(initialHex ?? LightingPolicy.DefaultColor);
            dlg.Color = System.Drawing.Color.FromArgb(c.R, c.G, c.B);
        }
        catch { }
        return dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK
            ? $"#{dlg.Color.R:X2}{dlg.Color.G:X2}{dlg.Color.B:X2}"
            : null;
    }

    private async void BtnCustomColor_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var hex = PickColor(IsLightingOff(_lighting) ? null : _lighting.Color);
            if (hex == null) return;
            if (hex == "#000000")
            {
                await TurnOffLightsAsync();
                return;
            }
            await ApplyColorToAllZonesAsync(hex);
        }
        catch (Exception ex)
        {
            ShowToast(Loc.T($"Aydınlatma rengi ayarlanamadı: {ex.Message}"), false);
        }
    }

    /// <summary>
    /// Spreads the colour the user last chose over all four zones. The per-zone picker can only ever change one, so
    /// after giving a single zone its own colour this is the one click that puts the keyboard back on one colour.
    /// </summary>
    private async void BtnApplyAllZones_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await ApplyLastPickedColorToAllZonesAsync();
        }
        catch (Exception ex)
        {
            ShowToast(Loc.T($"Aydınlatma rengi ayarlanamadı: {ex.Message}"), false);
        }
    }

    /// <summary>
    /// Spreads the colour the user last chose over all four zones. The per-zone picker can only ever change one, so
    /// after giving a single zone its own colour this is the one click that puts the keyboard back on one colour.
    /// </summary>
    internal async Task ApplyLastPickedColorToAllZonesAsync()
    {
        // The last colour the user picked, not the one sitting in the first slot: after a zone was given its own
        // colour, "apply all" must spread that colour rather than the one that happened to be in zone 1.
        var hex = _lastPickedColor
                  ?? (IsLightingOff(_lighting) ? _appSettings.LastActiveLighting?.Color : _lighting.Color)
                  ?? LightingPolicy.DefaultColor;
        await ApplyColorToAllZonesAsync(hex);
    }

    /// <summary>Click a zone to give it its own colour. Zone colours only exist in STATIC, so it switches to STATIC.</summary>
    private async void PreviewZone_Click(object sender, MouseButtonEventArgs e)
    {
        try
        {
            if (sender is not Border zone || !int.TryParse(zone.Tag?.ToString(), out int index) || index < 0 || index > 3) return;
            CancelActiveLightingReassertion();

            var baseCfg = IsLightingOff(_lighting) ? LightingPolicy.ResumeFromOff(_appSettings.LastActiveLighting) : LightingPolicy.Clone(_lighting);
            var leds = baseCfg.LEDs.Count >= 4 ? baseCfg.LEDs.Take(4).ToList() : Enumerable.Repeat(baseCfg.Color, 4).ToList();
            // Coming from a single-colour or rainbow effect, every zone starts from the visible colour.
            if (!LightingPolicy.EffectUsesZoneColors(baseCfg.Effect))
            {
                var start = LightingPolicy.EffectUsesColor(baseCfg.Effect) ? baseCfg.Color : LightingPolicy.DefaultColor;
                leds = Enumerable.Repeat(start, 4).ToList();
            }

            var hex = PickColor(leds[index]);
            if (hex == null) return;
            _lastPickedColor = hex;

            leds[index] = hex;
            baseCfg.Effect = "STATIC";
            baseCfg.LEDs = leds;
            baseCfg.Color = leds[0];
            _lighting = baseCfg;

            MarkLightingCustomized();
            UpdateLightingUi(_lighting);
            SuppressEffectComboEvents(() => UpdateEffectComboBox("STATIC"));
            if (!await ApplyLightingToDeviceAsync(Loc.T("Bölge rengi ayarlanamadı"))) return;
            _appSettings.IsBacklightOff = false;
            PersistLightingState();
        }
        catch (Exception ex)
        {
            ShowToast(Loc.T($"Bölge rengi ayarlanamadı: {ex.Message}"), false);
        }
    }

    private async void CmbRgbDirection_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        try
        {
            if (!IsLoaded || _isInitializingUi || _isApplyingPreset || _isSyncingDirectionUi) return;
            if (CmbRgbDirection.SelectedItem is not ComboBoxItem item || item.Tag is not int direction) return;
            if (_lighting.Direction == direction) return;
            CancelActiveLightingReassertion();
            _lighting.Direction = direction;
            MarkLightingCustomized();
            if (!await ApplyLightingToDeviceAsync(Loc.T("Efekt yönü ayarlanamadı"))) return;
            PersistLightingState();
        }
        catch (Exception ex)
        {
            ShowToast(Loc.T($"Efekt yönü ayarlanamadı: {ex.Message}"), false);
        }
    }

    private async void BtnTurnOffLights_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await TurnOffLightsAsync();
        }
        catch (Exception ex)
        {
            ShowToast(Loc.T($"Aydınlatma kapatılamadı: {ex.Message}"), false);
        }
    }

    private async Task TurnOffLightsAsync()
    {
        CancelActiveLightingReassertion();
        // Keep what was lit so brightness/effect/colour changes can bring it back later.
        if (!IsLightingOff(_lighting))
        {
            _appSettings.LastActiveLighting = LightingPolicy.Clone(_lighting);
        }
        _lighting.Brightness = 0;
        _lighting.Effect = "OFF";
        _lighting.Color = "#000000";
        _lighting.LEDs = new List<string> { "#000000", "#000000", "#000000", "#000000" };

        UpdateLightingUi(_lighting);
        SuppressEffectComboEvents(() => UpdateEffectComboBox("OFF"));

        if (!await ApplyLightingToDeviceAsync(Loc.T("Aydınlatma kapatılamadı"))) return;

        _appSettings.IsBacklightOff = true;
        _appSettings.KeyboardLighting = _lighting;

        var presets = _rgbProfileService.GetPresets();
        var offPreset = presets.FirstOrDefault(p => p.Effect.Equals("OFF", StringComparison.OrdinalIgnoreCase) || p.Brightness == 0);
        if (offPreset != null)
        {
            _appSettings.SelectedRgbPreset = offPreset.Name;
            if (CmbRgbPresets != null)
            {
                // Already applied above; do not let the preset combo send the same command again.
                bool wasApplying = _isApplyingPreset;
                _isApplyingPreset = true;
                try { CmbRgbPresets.SelectedItem = offPreset.DisplayName; }
                finally { _isApplyingPreset = wasApplying; }
            }
        }

        PersistLightingState();
    }

    private void SuppressEffectComboEvents(Action update)
    {
        bool wasApplying = _isApplyingPreset;
        _isApplyingPreset = true;
        try { update(); }
        finally { _isApplyingPreset = wasApplying; }
    }

    private async void CmbRgbEffect_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        try
        {
            if (!IsLoaded || _isInitializingUi || _isApplyingPreset || _isRebuildingEffectList) return;
            CancelActiveLightingReassertion();
            var effectName = SelectedEffectName();
            if (effectName != null)
            {
                if (effectName.Equals("OFF", StringComparison.OrdinalIgnoreCase))
                {
                    await TurnOffLightsAsync();
                    return;
                }

                // From "off" (black colours), bring the last lit settings back with the chosen effect;
                // otherwise just change the effect.
                if (IsLightingOff(_lighting))
                {
                    _lighting = LightingPolicy.ResumeFromOff(_appSettings.LastActiveLighting, effect: effectName);
                }
                else
                {
                    _lighting.Effect = effectName;
                    if (!LightingPolicy.AllowedDirections(effectName).Contains(_lighting.Direction)) _lighting.Direction = 1;
                }

                MarkLightingCustomized();
                UpdateLightingUi(_lighting);

                if (!await ApplyLightingToDeviceAsync(Loc.T("Aydınlatma efekti ayarlanamadı"))) return;
                _appSettings.IsBacklightOff = false;
                PersistLightingState();
            }
        }
        catch (Exception ex)
        {
            ShowToast(Loc.T($"Aydınlatma efekti ayarlanamadı: {ex.Message}"), false);
        }
    }

    // Sliders only update the preview and in-memory state immediately; the device command and the
    // settings write are debounced (ScheduleLightingApply) so a drag sends one command, not one per tick.
    private void SliderBrightness_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded || _isInitializingUi || _isApplyingPreset || _isSyncingLightingUi || TxtBrightnessVal == null) return;
        CancelActiveLightingReassertion();
        var val = (int)e.NewValue;

        if (val > 0 && IsLightingOff(_lighting))
        {
            // Raising brightness while off: bring back the last lit settings at this brightness
            // (before, the command stayed "OFF" and the keyboard never lit up).
            _lighting = LightingPolicy.ResumeFromOff(_appSettings.LastActiveLighting, brightness: val);
            SuppressEffectComboEvents(() => UpdateEffectComboBox(_lighting.Effect));
        }
        else
        {
            if (val == 0 && !IsLightingOff(_lighting))
            {
                _appSettings.LastActiveLighting = LightingPolicy.Clone(_lighting);
            }
            _lighting.Brightness = val;
        }

        _appSettings.IsBacklightOff = IsLightingOff(_lighting);
        _appSettings.KeyboardLighting = _lighting;
        MarkLightingCustomized();
        UpdateLightingUi(_lighting);
        ScheduleLightingApply();
    }

    private void SliderSpeed_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded || _isInitializingUi || _isApplyingPreset || _isSyncingLightingUi || TxtSpeedVal == null) return;
        CancelActiveLightingReassertion();
        var val = (int)e.NewValue;
        TxtSpeedVal.Text = $"{val} / 9";
        _lighting.Speed = val;
        MarkLightingCustomized();
        ScheduleLightingApply();
    }

    private void SetBacklightTimeoutCheckboxSilently(bool isChecked)
    {
        if (ChkBacklightTimeout == null) return;
        _isSyncingBacklightTimeoutUi = true;
        try { ChkBacklightTimeout.IsChecked = isChecked; }
        finally { _isSyncingBacklightTimeoutUi = false; }
    }

    /// <summary>The 30 s idle-off setting lives on the device (EC); mirror it instead of assuming "off".</summary>
    private async Task SyncBacklightTimeoutFromDeviceAsync()
    {
        try
        {
            var state = await _agentClient.GetBacklightTimeoutAsync();
            if (state.HasValue && !Dispatcher.HasShutdownStarted)
            {
                SetBacklightTimeoutCheckboxSilently(state.Value);
            }
        }
        catch (Exception ex)
        {
            // The checkbox then shows the default instead of what the device actually does.
            AppLog.Warn("Aydınlatma zaman aşımı aygıttan okunamadı", ex.Message);
        }
    }

    private async void ChkBacklightTimeout_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _isInitializingUi || _isSyncingBacklightTimeoutUi) return;
        var enable = ChkBacklightTimeout.IsChecked == true;
        bool ok;
        try
        {
            ok = await _agentClient.SetBacklightTimeoutAsync(enable);
        }
        catch
        {
            ok = false;
        }

        if (!ok)
        {
            SetBacklightTimeoutCheckboxSilently(!enable);
            ShowToast(Loc.T("30 sn aydınlatma zaman aşımı değiştirilemedi: klavye komutu reddedildi veya Acer servisine ulaşılamadı."), false);
        }
    }
}
