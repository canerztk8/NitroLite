using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using NitroLite.Core;
using NitroLite.Core.Hardware;
using WpfButton = System.Windows.Controls.Button;
using WpfContextMenu = System.Windows.Controls.ContextMenu;
using WpfMenuItem = System.Windows.Controls.MenuItem;

namespace NitroLite;

public partial class MainWindow
{
    /// <summary>
    /// The 60 Hz option only exists inside Eco: the battery branch of <see cref="PowerAutomationService"/> applies it
    /// there and nowhere else. Leaving it enabled under "Dengeli Mod" made it look like a setting that did nothing.
    /// </summary>
    private void UpdateDcRefreshOptionState()
    {
        if (ChkAutoRefreshRate == null) return;
        bool eco = _appSettings.DcPowerMode == DcPowerProfilePreference.Eco;
        ChkAutoRefreshRate.IsEnabled = eco;
        ChkAutoRefreshRate.Opacity = eco ? 1.0 : 0.45;
    }

    private void OnSystemPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend)
        {
            // Windows can cut power to the process at any point after this event, so anything still only in memory
            // is lost. Settings are normally written on every change, but the window rectangle is not: a user who
            // moves the window and closes the lid would come back to the old position.
            try
            {
                SaveWindowPosition();
            }
            catch (Exception ex)
            {
                AppLog.Warn("Suspend: could not persist window position", ex.Message);
            }
            return;
        }

        if (e.Mode == PowerModes.Resume)
        {
            _ = ReassertKeyboardLightingAsync();
            _ = ReassertOperatingModeAsync(isBoot: false);
            // The adapter may have been plugged or unplugged while suspended, and Windows does not always raise
            // StatusChange on resume: re-evaluate the power state explicitly.
            _ = _powerAutomationService?.EvaluatePowerStateAsync();
            // The charge limit lives in the EC and is reset by some BIOS revisions across a suspend cycle. The
            // care-centre read-back is the only place that notices, so it has to run on resume too.
            _ = SyncChargeLimitFromCareCenterAsync();
            // A laptop that sleeps for days would otherwise skip the wear history entirely: the reading is only
            // taken at launch and when the battery page is opened. The history decides whether this one is new.
            _ = RefreshBatteryHealthAsync();
        }
    }

    private void OnSystemSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionUnlock || e.Reason == SessionSwitchReason.SessionLogon)
        {
            _ = ReassertKeyboardLightingAsync();
            _ = ReassertOperatingModeAsync(isBoot: false);
        }
    }

    public void CancelActiveOperatingModeReassertion()
    {
        try
        {
            _modeReassertCts?.Cancel();
            _modeReassertCts?.Dispose();
            _modeReassertCts = null;
        }
        catch { }
    }

    internal async Task RestoreStartupOperatingModeAsync()
    {
        OperatingMode? modeToApply = null;
        OperatingMode? preferredMode = null;
        bool ecoBaselinePending = _powerAutomationService is { } pendingAutomation &&
                                  (pendingAutomation.IsDcEcoActive || pendingAutomation.HasPendingAcRestore);

        if (_appSettings.LastOperatingMode.HasValue)
        {
            var preferred = _appSettings.LastOperatingMode.Value;
            preferredMode = preferred;
            var (onAc, percent) = PowerStateProvider();
            var targetMode = ModePolicy.ClampForPower(preferred, onAc, percent);

            // Inform PowerAutomationService of the preferred AC baseline. Skip it while a recovered battery Eco
            // baseline is still pending: UpdateManualOperatingMode clears the pending restore flags and would
            // drop the desktop settings the Eco pass has to put back.
            if (!ecoBaselinePending)
            {
                _powerAutomationService?.UpdateManualOperatingMode(preferred);
            }

            bool restored = false;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                var cur = await _agentClient.TryGetOperatingModeAsync();
                if (cur.HasValue && cur.Value == targetMode)
                {
                    restored = true;
                    modeToApply = targetMode;
                    break;
                }

                if (await _agentClient.SetOperatingModeAsync(targetMode))
                {
                    restored = true;
                    modeToApply = targetMode;
                    break;
                }

                await Task.Delay(250);
            }

            if (!restored)
            {
                modeToApply = await _agentClient.TryGetOperatingModeAsync();
            }
        }
        else
        {
            modeToApply = await _agentClient.TryGetOperatingModeAsync();
            if (modeToApply.HasValue)
            {
                _appSettings.LastOperatingMode = modeToApply.Value;
                SaveSettings();
            }
        }

        if (modeToApply.HasValue)
        {
            UpdateModeUi(modeToApply.Value);
            // The desktop baseline is the user's preferred mode, not the value clamped for the current power
            // source: on battery Turbo is applied as Default, but plugging in must restore Turbo. Skip it while
            // a recovered Eco baseline is pending so the pending restore flags survive.
            if (!ecoBaselinePending)
            {
                UpdateDesktopBaselineForManualChange(mode: preferredMode ?? modeToApply.Value);
            }
        }
        else
        {
            ShowUnknownModeUi();
        }
    }

    public async Task ReassertOperatingModeAsync(bool isBoot = false)
    {
        if (!_appSettings.LastOperatingMode.HasValue) return;
        if (_debloatService.IsNitroSenseRunning()) return;

        // School mode owns the machine while it is on. Reasserting the saved mode here wrote Turbo on the adapter
        // (or the battery-clamped Default) over the quiet mode the switch promises, on every resume, unlock and
        // boot: the fans spun up in the middle of a lecture with the switch still showing on.
        if (_appSettings.SchoolModeEnabled) return;

        CancelActiveOperatingModeReassertion();
        var cts = new CancellationTokenSource();
        _modeReassertCts = cts;
        var token = cts.Token;

        int[] delays = isBoot
            ? new[] { 1000, 3000, 7000, 15000 }
            : new[] { 500, 1500 };

        try
        {
            foreach (var delay in delays)
            {
                await Task.Delay(delay, token);
                if (token.IsCancellationRequested || _isExiting || _isGameSessionActive) return;
                // The switch can be turned on between two attempts; the remaining ones must not undo it.
                if (_appSettings.SchoolModeEnabled) return;

                var (onAc, percent) = PowerStateProvider();
                var targetMode = ModePolicy.ClampForPower(_appSettings.LastOperatingMode.Value, onAc, percent);

                // The same lock PowerAutomationService takes. Without it this reassert and an Eco pass started by
                // the same resume event both wrote the operating mode, and the hardware kept whichever landed last
                // while the two components each believed their own value had won.
                if (!await _gameProfileTransitionLock.WaitAsync(TimeSpan.FromSeconds(5), token)) continue;
                try
                {
                    var currentMode = await _agentClient.TryGetOperatingModeAsync();
                    if (currentMode.HasValue && currentMode.Value != targetMode)
                    {
                        if (await _agentClient.SetOperatingModeAsync(targetMode))
                        {
                            if (!_isExiting && !Dispatcher.HasShutdownStarted)
                            {
                                // InvokeAsync, not Invoke: this runs while _gameProfileTransitionLock is held, and a
                                // synchronous hop to the UI thread is a deadlock waiting for the first UI-thread
                                // caller that waits on the same lock.
                                await Dispatcher.InvokeAsync(() =>
                                {
                                    UpdateModeUi(targetMode);
                                    UpdateDesktopBaselineForManualChange(mode: targetMode);
                                });
                            }
                        }
                    }
                }
                finally
                {
                    _gameProfileTransitionLock.Release();
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            // Trace only reaches a debugger; a mode that silently failed to reassert is exactly what a bug report
            // needs to show.
            AppLog.Warn("Çalışma modu yeniden uygulanamadı", ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_modeReassertCts, cts))
            {
                _modeReassertCts = null;
            }
        }
    }

    // ==========================================
    // CPU Boost (Module B)
    // ==========================================

    private void UpdateBoostUi(CpuBoostMode mode)
    {
        _lastShownBoost = mode;

        // Values NitroLite does not set itself (Windows default 1, or 3/5/6) are named as such instead of being
        // shown as "Aggressive".
        string name = ModePolicy.BoostDisplayName(mode);
        // A boost level is a setting, not an alarm: it is set in ink like every other readout.
        var brush = TryFindResource("InkSoft") as SolidColorBrush ?? System.Windows.Media.Brushes.Gray;

        if (TxtSideCpuBoost != null)
        {
            TxtSideCpuBoost.Text = (int)mode is 0 or 1 or 2 or 4 ? name : Loc.T("Diğer");
            TxtSideCpuBoost.Foreground = brush;
        }

        // Highlight the matching boost button (none for values without a button).
        SetSegActive(BtnBoostAggressive, mode == CpuBoostMode.Aggressive);
        SetSegActive(BtnBoostEfficient, mode == CpuBoostMode.Efficient);
        SetSegActive(BtnBoostDisabled, mode == CpuBoostMode.Disabled);
    }

    private void UpdateDesktopBaselineForManualChange(
        OperatingMode? mode = null,
        CpuBoostMode? boost = null,
        FanMode? fan = null,
        bool persist = true)
    {
        if (_isGameSessionActive) return;

        if (mode.HasValue)
        {
            _savedModeBeforeGame = mode.Value;
            _pendingModeRestore = false;
            _powerAutomationService?.UpdateManualOperatingMode(mode.Value);
            if (!_isInitializingUi && _settingsLoaded && persist)
            {
                _appSettings.LastOperatingMode = mode.Value;
                SaveSettings();
            }
        }

        if (boost.HasValue)
        {
            _savedCpuBoostBeforeGame = boost.Value;
            _pendingCpuBoostRestore = false;
            _powerAutomationService?.UpdateManualCpuBoost(boost.Value);
        }

        if (fan.HasValue)
        {
            _savedFanModeBeforeGame = fan.Value;
            _pendingFanRestore = false;
            _powerAutomationService?.UpdateManualFanMode(fan.Value);
        }

        // Recomputed from the individual steps, not from HasPendingDesktopRestore: that property includes
        // _hasPendingDesktopRestore itself, so assigning it here could never clear the flag after a failed restore.
        _hasPendingDesktopRestore = _interruptedGameRecovery != null ||
                                    _pendingModeRestore || _pendingCpuBoostRestore || _pendingFanRestore ||
                                    _pendingMouseRestore || (_powerAutomationService?.HasPendingAcRestore == true);
        // A manual change can satisfy the last outstanding step. The crash record has to go with it, or the next
        // start would restore the pre-game values over the choice the user just made.
        if (!HasPendingDesktopRestore)
        {
            _hasGameBaselineCaptured = false;
            _activeGameRecovery = null;
            ClearGameSessionRecoveryState();
        }
        UpdateTrayRetryMenuItemState();
    }

    internal async Task<bool> SetManualCpuBoostAsync(CpuBoostMode mode)
    {
        await _gameProfileTransitionLock.WaitAsync();
        try
        {
            bool ok = await ApplyCpuBoostModeAsync(mode);
            if (ok)
            {
                UpdateDesktopBaselineForManualChange(boost: mode);
            }
            return ok;
        }
        finally
        {
            _gameProfileTransitionLock.Release();
        }
    }

    private async void BtnBoost_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is WpfButton btn && btn.Tag is string tag)
            {
                if (Enum.TryParse<CpuBoostMode>(tag, out var mode))
                {
                    await SetManualCpuBoostAsync(mode);
                }
            }
        }
        catch (Exception ex)
        {
            ShowToast(Loc.T($"CPU modu uygulanamadı: {ex.Message}"), false);
        }
    }

    /// <summary>
    /// Restores CPU boost after a game. On battery Eco the game's value is replaced by the desktop baseline on the
    /// AC power-plan value and by Disabled on the DC value only, so the adapter never inherits the Eco limit.
    /// </summary>
    private async Task<bool> ApplyRestoreBoostAsync(CpuBoostMode target, bool isBatteryEco)
    {
        if (!isBatteryEco)
        {
            if (_activeGameRecovery is { } baseline)
            {
                if (baseline.SavedAcBoost is { } common && baseline.SavedDcBoost == common)
                    return await ApplyCpuBoostModeAsync(common);
                bool restoreAc = baseline.SavedAcBoost is not { } savedAc ||
                    await _cpuPowerService.SetBoostModeAsync(savedAc, targetAc: true, targetDc: false);
                bool restoreDc = baseline.SavedDcBoost is not { } savedDc ||
                    await _cpuPowerService.SetBoostModeAsync(savedDc, targetAc: false, targetDc: true);
                if (restoreAc && restoreDc) UpdateBoostUi(_cpuPowerService.CurrentMode);
                return restoreAc && restoreDc;
            }
            return await ApplyCpuBoostModeAsync(target);
        }

        bool acOk = await _cpuPowerService.SetBoostModeAsync(_savedCpuBoostBeforeGame, targetAc: true, targetDc: false);
        bool dcOk = await _cpuPowerService.SetBoostModeAsync(CpuBoostMode.Disabled, targetAc: false, targetDc: true);
        if (acOk && dcOk)
        {
            UpdateBoostUi(_cpuPowerService.CurrentMode);
            return true;
        }
        ShowToast(Loc.T("CPU boost modu geri yüklenemedi."), false);
        return false;
    }

    private async Task<bool> ApplyCpuBoostModeAsync(CpuBoostMode mode)
    {
        var ok = await _cpuPowerService.SetBoostModeAsync(mode);
        if (ok)
        {
            UpdateBoostUi(mode);
            ShowToast(Loc.T($"CPU boost modu: {ModePolicy.BoostDisplayName(mode)}"));
            return true;
        }
        else
        {
            ShowToast(Loc.T($"CPU boost modu uygulanamadı: {ModePolicy.BoostDisplayName(mode)}"), false);
            return false;
        }
    }

    private async Task<bool> ApplyGameBoostModeAsync(CpuBoostMode mode)
    {
        if (_activeGameRecovery is not { } baseline) return await ApplyCpuBoostModeAsync(mode);
        bool ac = baseline.SavedAcBoost.HasValue;
        bool dc = baseline.SavedDcBoost.HasValue;
        if (!ac && !dc) return false;
        bool applied = await _cpuPowerService.SetBoostModeAsync(mode, targetAc: ac, targetDc: dc);
        if (applied) UpdateBoostUi(_cpuPowerService.CurrentMode);
        return applied;
    }

    /// <summary>
    /// True when the mode is set in a capability bitmask. The bit position is the enum value. The implementation
    /// lives in <see cref="AcerRegistrySnapshot.IsGpuModeSupported"/> so the registry probe and the picker cannot
    /// drift apart; this stays as the call site the window already uses.
    /// </summary>
    public static bool IsGpuModeSupported(int capabilityBitmask, GpuOperatingMode mode) =>
        AcerRegistrySnapshot.IsGpuModeSupported(capabilityBitmask, mode);

    /// <summary>
    /// Mirrors NitroSense (personal-settings updateGPU_Mode): the GPU Operating card is shown whenever AASSvc reports
    /// a non-zero SUPPORT_GPU_MODE_CAPABILITY bitmask, and the options are the modes in that bitmask.
    /// NitroSense reads SUPPORT_GPU_MODE but never uses it to hide or lock the setting, so neither does NitroLite.
    /// An unknown capability (service not answering) never allows a switch.
    /// </summary>
    public static bool CanSwitchGpuMode(int? capabilityBitmask, GpuOperatingMode target)
    {
        return capabilityBitmask.HasValue
            && capabilityBitmask.Value > 0
            && IsGpuModeSupported(capabilityBitmask.Value, target);
    }

    private void SetGpuModeControlsEnabled(bool enabled, string? note)
    {
        if (CmbGpuMode != null) CmbGpuMode.IsEnabled = enabled;
        if (BtnApplyGpuMode != null) BtnApplyGpuMode.IsEnabled = enabled;
        if (TxtGpuModeNote != null)
        {
            TxtGpuModeNote.Text = note ?? string.Empty;
            TxtGpuModeNote.Visibility = string.IsNullOrEmpty(note) ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    public async Task RefreshGpuModeAsync()
    {
        try
        {
            var cap = await _agentClient.GetGpuModeCapabilityAsync();
            _supportedGpuCapability = cap;

            if (CmbGpuMode != null)
            {
                CmbGpuMode.Items.Clear();
            }

            if (cap.HasValue && cap.Value > 0 && CmbGpuMode != null)
            {

                // Bit 2 (4): Dynamic Display Switch (DDS / Advanced Optimus)
                if (IsGpuModeSupported(cap.Value, GpuOperatingMode.Automatic))
                {
                    CmbGpuMode.Items.Add(new ComboBoxItem
                    {
                        Content = Loc.T("Otomatik Seçim (DDS / Advanced Optimus)"),
                        Tag = "Automatic"
                    });
                }

                // Bit 0 (1): Optimus (Hybrid iGPU + dGPU)
                if (IsGpuModeSupported(cap.Value, GpuOperatingMode.Optimus))
                {
                    CmbGpuMode.Items.Add(new ComboBoxItem
                    {
                        Content = Loc.T("Optimus (Hibrit iGPU + dGPU)"),
                        Tag = "Optimus"
                    });
                }

                // Bit 1 (2): Discrete Only (dGPU)
                if (IsGpuModeSupported(cap.Value, GpuOperatingMode.DiscreteGpu))
                {
                    CmbGpuMode.Items.Add(new ComboBoxItem
                    {
                        Content = Loc.T("Yalnızca Harici GPU (NVIDIA dGPU)"),
                        Tag = "DiscreteGpu"
                    });
                }
            }

            var mode = await _agentClient.GetGpuModeAsync();
            if (mode.HasValue)
            {
                UpdateGpuModeUi(mode.Value);
            }
            else
            {
                _currentGpuMode = null;
            }

            if (CmbGpuMode != null && CmbGpuMode.SelectedIndex == -1 && CmbGpuMode.Items.Count > 0)
            {
                CmbGpuMode.SelectedIndex = 0;
            }

            if (cap.HasValue && cap.Value > 0 && CmbGpuMode?.Items.Count > 0)
            {
                // No note: the (?) badge already says the change needs a restart, and a line that repeats the
                // tooltip is the kind of description this page is being cleared of. The notes below stay, because
                // they explain a control that is greyed out and nothing else would.
                SetGpuModeControlsEnabled(true, null);
            }
            else if (cap.HasValue)
            {
                SetGpuModeControlsEnabled(false, Loc.T("Acer servisi bu cihaz için seçilebilir bir GPU modu bildirmiyor."));
            }
            else
            {
                SetGpuModeControlsEnabled(false,
                    Loc.T("GPU modu desteği Acer servisinden okunamadı; güvenlik için mod değiştirme kapalı."));
            }
        }
        catch (Exception ex)
        {
            _currentGpuMode = null;
            SetGpuModeControlsEnabled(false, Loc.T("GPU modu desteği Acer servisinden doğrulanamadı; güvenlik için mod değiştirme kapalı."));
            // Trace only reaches a debugger. A GPU mode query that failed is exactly what a bug report needs to
            // show, so it goes to the same log the user can open from the settings card.
            AppLog.Warn("GPU modu okunamadı", ex.Message);
        }
    }

    /// <summary>
    /// Points the combo box at the mode the service reported. The current mode is shown by that selection alone:
    /// a second readout beside the title only repeated it.
    /// </summary>
    private void UpdateGpuModeUi(GpuOperatingMode mode)
    {
        _currentGpuMode = mode;
        switch (mode)
        {
            case GpuOperatingMode.DiscreteGpu:
                SelectGpuModeCombo("DiscreteGpu");
                break;
            case GpuOperatingMode.Optimus:
                SelectGpuModeCombo("Optimus");
                break;
            case GpuOperatingMode.Automatic:
            default:
                SelectGpuModeCombo("Automatic");
                break;
        }
    }

    private static string GpuModeShortName(GpuOperatingMode? mode) => mode switch
    {
        GpuOperatingMode.DiscreteGpu => Loc.T("Yalnızca NVIDIA"),
        GpuOperatingMode.Optimus => Loc.T("Optimus"),
        GpuOperatingMode.Automatic => Loc.T("Otomatik"),
        _ => Loc.T("Bilinmiyor")
    };

    /// <summary>
    /// After a mode is applied, says which one is waiting for a restart. This used to be a badge beside the title;
    /// it now shares the note under it, so the pending state is not lost with the badge.
    /// </summary>
    private void ShowPendingGpuModeBadge()
    {
        if (TxtGpuModeNote == null || !_pendingGpuMode.HasValue) return;
        TxtGpuModeNote.Text = Loc.T($"{GpuModeShortName(_currentGpuMode)} → {GpuModeShortName(_pendingGpuMode)} (yeniden başlatma bekleniyor)");
        TxtGpuModeNote.Visibility = Visibility.Visible;
    }

    private void SelectGpuModeCombo(string tag)
    {
        if (CmbGpuMode == null) return;
        for (int i = 0; i < CmbGpuMode.Items.Count; i++)
        {
            if (CmbGpuMode.Items[i] is ComboBoxItem item && string.Equals(item.Tag as string, tag, StringComparison.OrdinalIgnoreCase))
            {
                CmbGpuMode.SelectedIndex = i;
                break;
            }
        }
    }

    private async void BtnApplyGpuMode_Click(object sender, RoutedEventArgs e)
    {
        if (CmbGpuMode?.SelectedItem is not ComboBoxItem selectedItem || selectedItem.Tag is not string tagStr)
        {
            ShowToast(Loc.T("Lütfen bir GPU çalışma modu seçin."), false);
            return;
        }

        if (!Enum.TryParse<GpuOperatingMode>(tagStr, out var targetMode))
        {
            ShowToast(Loc.T("Geçersiz GPU modu."), false);
            return;
        }

        if (!CanSwitchGpuMode(_supportedGpuCapability, targetMode))
        {
            ShowToast(Loc.T("Seçilen GPU modu Acer servisinin bildirdiği modlar arasında değil."), false);
            return;
        }

        if (_pendingGpuMode.HasValue && targetMode == _pendingGpuMode.Value)
        {
            ShowToast(Loc.T("Bu GPU modu zaten ayarlandı; yeniden başlatınca geçerli olacak."));
            return;
        }

        // Re-selecting the running mode is allowed only to cancel a pending, not yet applied change.
        if (!_pendingGpuMode.HasValue && _currentGpuMode.HasValue && targetMode == _currentGpuMode.Value)
        {
            ShowToast(Loc.T("Seçilen GPU modu zaten aktif."));
            return;
        }

        string modeDisplayName = targetMode switch
        {
            GpuOperatingMode.DiscreteGpu => Loc.T("Yalnızca Harici GPU (NVIDIA dGPU)"),
            GpuOperatingMode.Optimus => Loc.T("Optimus (Hibrit iGPU + dGPU)"),
            _ => Loc.T("Otomatik Seçim (DDS / Advanced Optimus)")
        };

        var confirm = System.Windows.MessageBox.Show(
            this,
            Loc.T($"GPU Çalışma Modunu '{modeDisplayName}' olarak ayarlamak üzeresiniz.\n\n" +
                  "NitroSense'te olduğu gibi yeni mod, bilgisayar yeniden başlatıldıktan sonra geçerli olur.\n\n" +
                  "Devam etmek istiyor musunuz?"),
            Loc.T("GPU Çalışma Modu (MUX Switch)"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes) return;

        BtnApplyGpuMode.IsEnabled = false;
        try
        {
            var success = await _agentClient.SetGpuModeAsync(targetMode);
            if (success)
            {
                // The running mode does not change until restart: keep showing it and mark the pending one.
                if (_currentGpuMode.HasValue && targetMode == _currentGpuMode.Value)
                {
                    _pendingGpuMode = null;
                    UpdateGpuModeUi(targetMode);
                }
                else
                {
                    _pendingGpuMode = targetMode;
                    ShowPendingGpuModeBadge();
                }
                ShowToast(Loc.T($"GPU modu '{modeDisplayName}' olarak ayarlandı; yeniden başlatınca geçerli olacak."));

                var restart = System.Windows.MessageBox.Show(
                    this,
                    Loc.T("GPU modu kaydedildi. Değişikliğin geçerli olması için bilgisayarın yeniden başlatılması gerekiyor.\n\n" +
                    "Açık çalışmalarınızı kaydettiyseniz şimdi yeniden başlatılsın mı?"),
                    Loc.T("Yeniden Başlat ve Uygula"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                if (restart == MessageBoxResult.Yes)
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = AppPaths.SystemTool("shutdown.exe"),
                        Arguments = "/r /t 5 /c \"NitroLite: GPU modu degisikligi\"",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    })?.Dispose();
                }
            }
            else
            {
                ShowToast(Loc.T("GPU modu uygulanamadı. Acer Agent Service yanıt vermedi."), false);
            }
        }
        catch (Exception ex)
        {
            ShowToast(Loc.T($"Hata: {ex.Message}"), false);
        }
        finally
        {
            BtnApplyGpuMode.IsEnabled = true;
        }
    }

    private void UpdateModeUi(OperatingMode mode)
    {
        bool modeChanged = _currentMode != mode;
        _currentMode = mode;
        if (modeChanged)
        {
            // The recording strip keeps a tick for every change inside its window, so a switch stays
            // visible on the paper instead of vanishing with the toast.
            var at = DateTime.Now;
            _modeMarks.Add((at, mode));
            var cutoff = at - ChartWindow;
            _modeMarks.RemoveAll(m => m.At < cutoff);
        }
        if (TxtModeHeader != null)
        {
            TxtModeHeader.Text = mode switch
            {
                OperatingMode.Quiet => Loc.T("SESSİZ"),
                OperatingMode.Default => Loc.T("DENGELİ"),
                OperatingMode.Extreme => Loc.T("PERFORMANS"),
                OperatingMode.Turbo => Loc.T("TURBO"),
                OperatingMode.Eco => Loc.T("ECO"),
                _ => ModePolicy.DisplayName(mode).ToUpperInvariant()
            };
        }

        // Reset all mode badges and card buttons
        ResetAllModeCards();

        // One signal colour for the whole board: the active row is marked by position and fill,
        // not by a per-mode hue, so the eye never has to relearn what a colour means.
        var signal = TryFindResource("Vermilion") as SolidColorBrush ?? System.Windows.Media.Brushes.Goldenrod;
        switch (mode)
        {
            case OperatingMode.Quiet:
                SetModeCardActive(BtnModeQuiet, IndQuiet, TxtIndQuiet, signal, LedQuiet);
                break;
            case OperatingMode.Default:
                SetModeCardActive(BtnModeDefault, IndDefault, TxtIndDefault, signal, LedDefault);
                break;
            case OperatingMode.Extreme:
                SetModeCardActive(BtnModeExtreme, IndExtreme, TxtIndExtreme, signal, LedExtreme);
                break;
            case OperatingMode.Turbo:
                SetModeCardActive(BtnModeTurbo, IndTurbo, TxtIndTurbo, signal, LedTurbo);
                break;
            case OperatingMode.Eco:
                SetModeCardActive(BtnModeEco, IndEco, TxtIndEco, signal, LedEco);
                break;
        }

        UpdateHighPowerAvailabilityUi();
    }

    private void ResetAllModeCards()
    {
        ResetModeCard(BtnModeQuiet, IndQuiet, TxtIndQuiet, LedQuiet);
        ResetModeCard(BtnModeDefault, IndDefault, TxtIndDefault, LedDefault);
        ResetModeCard(BtnModeExtreme, IndExtreme, TxtIndExtreme, LedExtreme);
        ResetModeCard(BtnModeTurbo, IndTurbo, TxtIndTurbo, LedTurbo);
        ResetModeCard(BtnModeEco, IndEco, TxtIndEco, LedEco);
    }

    private void ShowUnknownModeUi()
    {
        if (TxtModeHeader != null) TxtModeHeader.Text = Loc.T("BİLİNMİYOR");
        ResetAllModeCards();
        UpdateHighPowerAvailabilityUi();
    }

    /// <summary>
    /// Asks the keyboard what it can do. The Nitro line ships four-zone RGB, single-zone RGB, single-colour
    /// backlight and no lighting at all; the four-zone picker only makes sense on the first. A null answer leaves
    /// the UI exactly as it was, because a failed query is not evidence that the keyboard is single-zone.
    /// </summary>
    public async Task RefreshKeyboardCapabilityAsync()
    {
        try
        {
            _keyboardCapability = await _agentClient.GetKeyboardCapabilityAsync();
        }
        catch
        {
            _keyboardCapability = null;
        }

        ApplyKeyboardCapabilityUi();
    }

    /// <summary>
    /// Hides the per-zone picker on a keyboard that cannot take a colour per zone. Only a positive answer from the
    /// device hides it: an unknown capability keeps the card, so a service hiccup never costs the user a control.
    /// </summary>
    private void ApplyKeyboardCapabilityUi()
    {
        if (CardZonePreview == null) return;

        var capability = _keyboardCapability;
        bool showZones = capability == null || capability.HasZoneColors;
        CardZonePreview.Visibility = showZones ? Visibility.Visible : Visibility.Collapsed;

        // The dropdown follows the same report: offering an effect the keyboard does not have only produces a
        // rejected command and a toast.
        RebuildEffectList();

        if (TxtKeyboardCapabilityNote == null) return;
        if (showZones)
        {
            TxtKeyboardCapabilityNote.Visibility = Visibility.Collapsed;
            return;
        }

        // The note sits outside the card on purpose: it explains the missing picker, so hiding the card must not
        // hide the explanation with it. It is a separate element from TxtZonePreviewNote, which carries the
        // per-effect notes and would otherwise overwrite this one on the next refresh.
        TxtKeyboardCapabilityNote.Text = capability!.HasLighting
            ? Loc.T("Bu klavye tek bölgeli; tüm tuşlar aynı rengi alır.")
            : Loc.T("Bu modelde klavye aydınlatması bildirilmedi.");
        TxtKeyboardCapabilityNote.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Asks the service which operating modes this model has. Nitro models differ: a Nitro V 15 without Turbo must
    /// not offer a Turbo button that the firmware rejects. A null answer leaves <see cref="_supportedModes"/> null,
    /// which <see cref="ModePolicy.IsModeSupported"/> reads as "unknown, show everything".
    /// </summary>
    public async Task RefreshModeCapabilityAsync()
    {
        try
        {
            _supportedModes = await _agentClient.GetOperatingModesAsync();
        }
        catch
        {
            _supportedModes = null;
        }

        // The cached availability tuple would short-circuit the redraw, so drop it before asking for one.
        _lastModeAvailability = null;
        UpdateHighPowerAvailabilityUi();
    }

    /// <summary>
    /// Shows each mode group only where it can be used: Turbo/Performance need the AC adapter (NitroSense also
    /// wants 40 % battery), Eco is a battery mode, and a mode the model does not offer is hidden outright. The row
    /// that is active stays visible even when its group is hidden, so the list always marks the mode the machine is
    /// actually in. Called on mode changes, battery status updates and the periodic state poll.
    /// </summary>
    private void UpdateHighPowerAvailabilityUi()
    {
        var (onAc, percent) = PowerStateProvider();
        bool allowed = ModePolicy.IsHighPowerAllowed(onAc, percent);

        var state = (allowed, onAc, _currentMode);
        if (_lastModeAvailability == state) return;
        _lastModeAvailability = state;

        var (showHighPower, showEco) = ModePolicy.VisibleModeGroups(onAc, _currentMode);

        // Per-mode, not per-group: a model can offer Performance and not Turbo.
        bool showExtreme = showHighPower && ModePolicy.IsModeSupported(_supportedModes, OperatingMode.Extreme);
        bool showTurbo = showHighPower && ModePolicy.IsModeSupported(_supportedModes, OperatingMode.Turbo);
        bool showQuiet = ModePolicy.IsModeSupported(_supportedModes, OperatingMode.Quiet);
        bool showDefault = ModePolicy.IsModeSupported(_supportedModes, OperatingMode.Default);
        bool showEcoMode = showEco && ModePolicy.IsModeSupported(_supportedModes, OperatingMode.Eco);

        foreach (var (card, visible) in new[]
                 {
                     (BtnModeQuiet, showQuiet),
                     (BtnModeDefault, showDefault),
                     (BtnModeExtreme, showExtreme),
                     (BtnModeTurbo, showTurbo)
                 })
        {
            if (card == null) continue;
            card.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }

        // Only the high-power pair is gated on the adapter; Quiet and Balanced work on battery too.
        foreach (var card in new[] { BtnModeExtreme, BtnModeTurbo })
        {
            if (card == null) continue;
            card.IsEnabled = allowed;
            card.Opacity = allowed ? 1.0 : 0.45;
            card.ToolTip = allowed ? null : ModePolicy.HighPowerBlockedMessage;
        }

        if (BtnModeEco != null)
            BtnModeEco.Visibility = showEcoMode ? Visibility.Visible : Visibility.Collapsed;

        if (TxtModeLockNote != null)
        {
            TxtModeLockNote.Text = allowed ? string.Empty : ModePolicy.HighPowerBlockedMessage;
            TxtModeLockNote.Visibility = allowed ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private void ResetModeCard(WpfButton? btn, Border? ind, TextBlock? txt, System.Windows.Shapes.Ellipse? led = null)
    {
        if (ind != null)
        {
            // The chip is not a "SEÇ" prompt any more: an inactive card shows nothing, so the row of tiles reads as
            // four choices rather than four buttons asking to be pressed.
            ind.Visibility = Visibility.Collapsed;
            ind.Background = System.Windows.Media.Brushes.Transparent;
            ind.BorderBrush = TryFindResource("Rule") as SolidColorBrush ?? System.Windows.Media.Brushes.Gray;
        }
        if (led != null) led.Visibility = Visibility.Collapsed;
        if (txt != null)
        {
            txt.Text = Loc.T("AKTİF");
            txt.Foreground = TryFindResource("TextMuted") as SolidColorBrush ?? System.Windows.Media.Brushes.Gray;
        }
        if (btn != null)
        {
            // The 3x18 mark in the row's left margin is driven by BorderBrush; transparent means "no mark".
            btn.Background = System.Windows.Media.Brushes.Transparent;
            btn.BorderBrush = System.Windows.Media.Brushes.Transparent;
            btn.Foreground = TryFindResource("TextSecondary") as SolidColorBrush ?? System.Windows.Media.Brushes.Black;
        }
    }

    /// <summary>
    /// The active mode is marked by a 3px vermilion time mark above its row, full ink type and a
    /// vermilion chip; the other rows drop back to soft ink. State by de-emphasis, never by a fill,
    /// so the eye never has to relearn what a colour means.
    /// </summary>
    private void SetModeCardActive(WpfButton? btn, Border? ind, TextBlock? txt, SolidColorBrush accent,
        System.Windows.Shapes.Ellipse? led = null)
    {
        if (ind != null)
        {
            ind.Visibility = Visibility.Visible;
            ind.Background = System.Windows.Media.Brushes.Transparent;
            ind.BorderBrush = accent;
        }
        if (led != null)
        {
            led.Fill = accent;
            led.Visibility = Visibility.Visible;
        }
        if (txt != null)
        {
            txt.Text = Loc.T("AKTİF");
            txt.Foreground = accent;
        }
        if (btn != null)
        {
            btn.Background = System.Windows.Media.Brushes.Transparent;
            btn.BorderBrush = accent;
            btn.Foreground = TryFindResource("TextPrimary") as SolidColorBrush ?? System.Windows.Media.Brushes.Black;
        }
    }

    /// <param name="persist">
    /// False for a change the app made on the user's behalf — the fan-stall protection dropping to Eco. The machine
    /// really is in that mode, so the automation is told about it, but the saved preference is left alone: a safety
    /// action must not decide what the laptop boots into next time.
    /// </param>
    /// <summary>
    /// The compact mode menu behind the title bar's MOD figure. It is the tray menu's list, not a second one: the same
    /// capability filter hides a mode the model does not offer, the same battery rule greys out the high-power pair,
    /// and every item goes through <see cref="SetManualModeAsync"/> so school mode and the power clamp still apply.
    /// </summary>
    internal WpfContextMenu BuildModeMenu()
    {
        var (onAc, percent) = PowerStateProvider();
        var menu = new WpfContextMenu();
        foreach (var mode in new[]
                 {
                     OperatingMode.Quiet, OperatingMode.Default, OperatingMode.Extreme,
                     OperatingMode.Turbo, OperatingMode.Eco
                 })
        {
            // The active mode stays visible even when its group is hidden, so the menu always marks the mode the
            // machine is actually in.
            if (!ModePolicy.IsModeSupported(_supportedModes, mode) && mode != _currentMode) continue;

            bool allowed = ModePolicy.IsModeAllowed(mode, onAc, percent);
            var item = new WpfMenuItem
            {
                Header = ModePolicy.DisplayName(mode),
                IsEnabled = allowed,
                IsChecked = mode == _currentMode,
                Tag = mode,
                ToolTip = allowed ? null : ModePolicy.HighPowerBlockedMessage
            };
            var target = mode;
            item.Click += async (_, _) =>
            {
                try { await SetManualModeAsync(target); }
                catch (Exception ex) { ShowToast(Loc.T($"Çalışma modu uygulanamadı: {ex.Message}"), false); }
            };
            menu.Items.Add(item);
        }
        return menu;
    }

    /// <summary>
    /// The compact CPU boost menu behind the left rail's BOOST figure. The same three levels as the segmented buttons
    /// on the modes page, and the same call: <see cref="SetManualCpuBoostAsync"/>, so the desktop baseline and the
    /// game-session lock behave exactly as they do from the page.
    /// </summary>
    internal WpfContextMenu BuildBoostMenu()
    {
        var menu = new WpfContextMenu();
        foreach (var mode in new[] { CpuBoostMode.Aggressive, CpuBoostMode.Efficient, CpuBoostMode.Disabled })
        {
            var item = new WpfMenuItem
            {
                Header = ModePolicy.BoostDisplayName(mode),
                IsChecked = mode == _cpuPowerService.CurrentMode,
                Tag = mode
            };
            var target = mode;
            item.Click += async (_, _) =>
            {
                try { await SetManualCpuBoostAsync(target); }
                catch (Exception ex) { ShowToast(Loc.T($"CPU modu uygulanamadı: {ex.Message}"), false); }
            };
            menu.Items.Add(item);
        }
        return menu;
    }

    /// <summary>Opens <paramref name="menu"/> under the control that was clicked.</summary>
    private void OpenContextMenu(FrameworkElement anchor, WpfContextMenu menu)
    {
        if (menu.Items.Count == 0) return;
        menu.PlacementTarget = anchor;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void BadgeMode_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement anchor) return;
        try
        {
            OpenContextMenu(anchor, BuildModeMenu());
        }
        catch (Exception ex)
        {
            AppLog.Warn("Mod menüsü açılamadı", ex.ToString());
        }
    }

    internal async Task<bool> SetManualModeAsync(OperatingMode mode, bool persist = true)
    {
        // School mode owns the machine while it is on. The mode cards, the tray menu and the mode key all land here,
        // and without this guard any of them took the machine to Turbo with the switch still showing on: the fans
        // spun up in the middle of a lecture and the "turning this off puts everything back" promise was already
        // broken. Going quieter stays allowed, which is what the fan-stall protection asks for when it drops to Eco.
        if (_appSettings.SchoolModeEnabled && mode != OperatingMode.Eco && mode != OperatingMode.Quiet)
        {
            ShowToast(Loc.T("Okul modu açıkken çalışma modu değiştirilemez; okul modunu kapatınca geri gelir."), false);
            return false;
        }

        var (onAc, percent) = PowerStateProvider();
        if (!ModePolicy.IsModeAllowed(mode, onAc, percent))
        {
            ShowToast(ModePolicy.HighPowerBlockedMessage, false);
            return false;
        }

        CancelActiveOperatingModeReassertion();

        await _gameProfileTransitionLock.WaitAsync();
        try
        {
            bool ok = await SwitchModeAsync(mode);
            if (ok)
            {
                _currentMode = mode;
                UpdateDesktopBaselineForManualChange(mode: mode, persist: persist);
                if (persist)
                {
                    _appSettings.LastOperatingMode = mode;
                    SaveSettings();
                }
            }
            return ok;
        }
        finally
        {
            _gameProfileTransitionLock.Release();
        }
    }

    private async void BtnMode_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is WpfButton btn && btn.Tag is string tagStr)
            {
                if (Enum.TryParse<OperatingMode>(tagStr, out var mode))
                {
                    await SetManualModeAsync(mode);
                }
            }
        }
        catch (Exception ex)
        {
            ShowToast(Loc.T($"Çalışma modu uygulanamadı: {ex.Message}"), false);
        }
    }

    private async Task<bool> SwitchModeAsync(OperatingMode mode)
    {
        try
        {
            var ok = await _agentClient.SetOperatingModeAsync(mode);
            if (ok)
            {
                UpdateModeUi(mode);
                ShowToast(Loc.T($"Çalışma modu: {ModePolicy.DisplayName(mode)}"), isSuccess: true, trayBalloon: false);
                return true;
            }
            else
            {
                ShowToast(Loc.T($"Çalışma modu değiştirilemedi: {ModePolicy.DisplayName(mode)}"), false);
                return false;
            }
        }
        catch (Exception ex)
        {
            ShowToast(Loc.T($"Çalışma modu hatası: {ex.Message}"), false);
            return false;
        }
    }

    private async void CmbDcPowerMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _isInitializingUi || CmbDcPowerMode?.SelectedItem is not ComboBoxItem item || item.Tag is not string tag) return;
        if (Enum.TryParse<DcPowerProfilePreference>(tag, out var pref))
        {
            _appSettings.DcPowerMode = pref;
            UpdateDcRefreshOptionState();
            SaveSettings();
            ShowToast(pref == DcPowerProfilePreference.Eco ? Loc.T("Pilde güç tercihi: Eco (sessiz mod, pilde boost kapalı)") : Loc.T("Pilde güç tercihi: Dengeli (mevcut ayarlar korunur)"));
            try
            {
                if (_powerAutomationService != null) await _powerAutomationService.EvaluatePowerStateAsync();
            }
            catch (Exception ex)
            {
                AppLog.Warn("Pil güç tercihi uygulanamadı", ex.ToString());
            }
        }
    }

    private async void ChkAutoRefreshRate_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _isInitializingUi || ChkAutoRefreshRate == null) return;
        _appSettings.AutoRefreshRateSwitching = ChkAutoRefreshRate.IsChecked == true;
        try
        {
            if (_powerAutomationService != null) await _powerAutomationService.EvaluatePowerStateAsync();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Ekran yenileme hızı geçişi uygulanamadı", ex.ToString());
        }
        SaveSettings();
        ShowToast(_appSettings.AutoRefreshRateSwitching
            ? Loc.T("Otomatik tazeleme etkin: pilde Eco laptop ekranını 60 Hz'e alır, adaptörde önceki hıza döner")
            : Loc.T("Otomatik ekran tazeleme hızı geçişi devre dışı"));
    }
}
