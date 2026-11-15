using System.Globalization;
using System.Media;
using System.Windows;
using System.Windows.Media;
using NitroLite.Core;
using WpfBrush = System.Windows.Media.Brush;
using WpfPoint = System.Windows.Point;

namespace NitroLite;

public partial class MainWindow
{
    // ==========================================
    // Battery Health Details & Power Flow (v1.3)
    // ==========================================

    private async Task RefreshBatteryHealthAsync()
    {
        var details = await _batteryService.GetBatteryHealthDetailsAsync();

        // One reading a day is enough for a wear curve; the history decides whether this one is new. It runs off the
        // UI thread because the first call reads the file.
        await Task.Run(() => _wearHistory.TryRecord(details));

        Dispatcher.Invoke(() =>
        {
            // Values computed from a fixed factory capacity, and the hard-coded fallbacks used when Windows
            // reports nothing, are shown as estimates.
            // Only values Windows reported are shown; anything missing is "--" (no model-specific constants).
            bool measured = details.DesignCapacityMWh > 0 && details.FullChargeCapacityMWh > 0;
            bool wearEstimated = details.IsDesignCapacityEstimated || details.IsFullChargeCapacityEstimated;

            if (TxtBatHealthPercent != null)
                TxtBatHealthPercent.Text = measured ? TelemetryFormatter.MarkEstimated(FormattableString.Invariant($"{details.HealthPercent:F1} %"), wearEstimated) : "--";
            if (TxtBatHealthNote != null)
                TxtBatHealthNote.Text = !measured ? Loc.T("Kapasite okunamadı")
                    : details.HealthPercent >= 80 ? Loc.T("İyi")
                    : details.HealthPercent >= 60 ? Loc.T("Yıpranmış")
                    : Loc.T("Değişim önerilir");

            if (TxtBatWearPercent != null)
                TxtBatWearPercent.Text = measured ? TelemetryFormatter.MarkEstimated(FormattableString.Invariant($"{details.WearLevelPercent:F1} %"), wearEstimated) : "--";
            if (TxtBatWearNote != null)
                TxtBatWearNote.Text = !measured ? "--"
                    : details.WearLevelPercent < 10 ? Loc.T("Düşük aşınma (< %10)")
                    : details.WearLevelPercent < 25 ? Loc.T("Orta aşınma (%10–25)")
                    : Loc.T("Yüksek aşınma (> %25)");

            if (TxtBatFullChargeCap != null)
                TxtBatFullChargeCap.Text = details.FullChargeCapacityMWh > 0
                    ? TelemetryFormatter.MarkEstimated(FormattableString.Invariant($"{details.FullChargeCapacityMWh:F0} mWh"), details.IsFullChargeCapacityEstimated)
                    : "--";

            if (TxtBatDesignCap != null)
                TxtBatDesignCap.Text = details.DesignCapacityMWh > 0
                    ? Loc.T("Fabrika: ") + TelemetryFormatter.MarkEstimated(FormattableString.Invariant($"{details.DesignCapacityMWh:F0} mWh"), details.IsDesignCapacityEstimated)
                    : Loc.T("Fabrika: --");

            // A row that says "the battery software does not report this" is a row about the software, not about the
            // battery. When there is no number, the line is simply not there.
            if (TxtBatCycleCount != null)
            {
                TxtBatCycleCount.Text = details.CycleCount.HasValue ? Loc.T($"Döngü: {details.CycleCount.Value}") : string.Empty;
                TxtBatCycleCount.Visibility = details.CycleCount.HasValue ? Visibility.Visible : Visibility.Collapsed;
            }

            if (TxtBatVoltage != null)
                TxtBatVoltage.Text = details.VoltageVolts > 0
                    ? TelemetryFormatter.MarkEstimated($"{details.VoltageVolts:F2} V", details.IsVoltageEstimated)
                    : "--";

            if (TxtBatChemistry != null)
            {
                // Li-ion cells are ~3.6-3.85 V nominal; the series cell count is inferred from the pack voltage.
                int cells = details.VoltageVolts > 0 ? (int)Math.Round(details.VoltageVolts / 3.85) : 0;
                var parts = new List<string>();
                if (details.Chemistry.Length > 0) parts.Add(details.Chemistry);
                if (cells is >= 1 and <= 8 && details.Chemistry.StartsWith("Lityum")) parts.Add(Loc.T($"~{cells} hücre (gerilimden)"));
                TxtBatChemistry.Text = parts.Count > 0 ? string.Join(" · ", parts) : "--";
            }

            var id = string.Join(" ", new[] { details.Manufacturer, details.ModelName }.Where(s => !string.IsNullOrWhiteSpace(s)));
            if (TxtSubBatteryName != null)
                TxtSubBatteryName.Text = id.Length > 0 ? id : Loc.T("Pil");

            RenderWearTrend();
        });
    }

    /// <summary>
    /// Draws the recorded wear readings as a line on the card's paper: x is the date, y is the wear percentage.
    /// Hidden until there are two readings, because one point is a number, not a trend.
    /// </summary>
    private void RenderWearTrend()
    {
        if (CanvasWearTrend == null || WearTrendLine == null || TxtWearTrendSummary == null) return;

        var trend = _wearHistory.GetTrend();
        var samples = trend.Samples;

        if (BtnClearWearHistory != null)
            BtnClearWearHistory.Visibility = samples.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        if (samples.Count == 0)
        {
            TxtWearTrendSummary.Text = Loc.T("Henüz ölçüm yok. NitroLite iki haftada bir ölçüm kaydeder; eğri birkaç ölçümden sonra görünür.");
            if (TxtWearTrendNote != null) TxtWearTrendNote.Visibility = Visibility.Collapsed;
            CanvasWearTrend.Visibility = Visibility.Collapsed;
            if (GridWearTrendAxis != null) GridWearTrendAxis.Visibility = Visibility.Collapsed;
            return;
        }

        var first = samples[0];
        var last = samples[^1];

        if (!trend.HasTrend)
        {
            TxtWearTrendSummary.Text = Loc.T("Tek ölçüm var. Eğri için en az iki ölçüm gerekir; NitroLite iki haftada bir ölçüm kaydeder.");
            if (TxtWearTrendNote != null)
            {
                TxtWearTrendNote.Text = Loc.T($"İlk ölçüm {first.TimestampUtc.ToLocalTime():d} · aşınma {FormattableString.Invariant($"{first.WearLevelPercent:F1}")} puan");
                TxtWearTrendNote.Visibility = Visibility.Visible;
            }
            CanvasWearTrend.Visibility = Visibility.Collapsed;
            if (GridWearTrendAxis != null) GridWearTrendAxis.Visibility = Visibility.Collapsed;
            return;
        }

        double days = trend.SpanDays ?? 0;
        double change = trend.WearChangePercent ?? 0;
        string changeText = change.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture);
        TxtWearTrendSummary.Text = Loc.T($"{Math.Max(1, Math.Round(days)):F0} günde aşınma {changeText} puan değişti.");

        if (TxtWearTrendNote != null)
        {
            if (trend.WearPerYearPercent is not double perYear)
            {
                TxtWearTrendNote.Text = Loc.T("Yıllık eğilim için en az bir haftalık kayıt gerekir.");
            }
            else if (change <= 0)
            {
                // A pack that reads healthier than before recalibrated; saying "it is healing" would be a lie.
                TxtWearTrendNote.Text = Loc.T("Aşınma artmadı; pil kendini yeniden kalibre etmiş olabilir.");
            }
            else
            {
                TxtWearTrendNote.Text = Loc.T($"Bu hızla yılda yaklaşık {FormattableString.Invariant($"{perYear:F1}")} puan aşınır.");
            }
            TxtWearTrendNote.Visibility = Visibility.Visible;
        }

        if (GridWearTrendAxis != null)
        {
            GridWearTrendAxis.Visibility = Visibility.Visible;
            if (TxtWearTrendFirstDate != null) TxtWearTrendFirstDate.Text = first.TimestampUtc.ToLocalTime().ToString("d");
            if (TxtWearTrendLastDate != null) TxtWearTrendLastDate.Text = last.TimestampUtc.ToLocalTime().ToString("d");
        }

        CanvasWearTrend.Visibility = Visibility.Visible;

        double width = CanvasWearTrend.ActualWidth;
        double height = CanvasWearTrend.ActualHeight;
        if (width <= 0 || height <= 0) { width = 600; height = 58; }

        double minWear = samples.Min(s => s.WearLevelPercent);
        double maxWear = samples.Max(s => s.WearLevelPercent);
        // A flat history is a real answer, so it is drawn as a flat line in the middle instead of dividing by zero.
        double pad = Math.Max(0.5, (maxWear - minWear) * 0.15);
        double lo = Math.Max(0, minWear - pad);
        double hi = maxWear + pad;
        if (hi - lo < 1.0) hi = lo + 1.0;

        var start = first.TimestampUtc;
        double spanSeconds = Math.Max(1.0, (last.TimestampUtc - start).TotalSeconds);

        var points = new PointCollection();
        foreach (var sample in samples)
        {
            double x = (sample.TimestampUtc - start).TotalSeconds / spanSeconds * (width - 2) + 1;
            double y = height - 1 - (sample.WearLevelPercent - lo) / (hi - lo) * (height - 2);
            points.Add(new WpfPoint(x, y));
        }
        WearTrendLine.Points = points;

        // The newest reading is the live one, so it gets the vermilion dot the other traces use.
        if (WearTrendMarks != null && points.Count > 0)
        {
            if (_wearTrendDot == null)
            {
                _wearTrendDot = new System.Windows.Shapes.Ellipse
                {
                    Width = 5,
                    Height = 5,
                    Fill = TryFindResource("Vermilion") as WpfBrush
                };
                WearTrendMarks.Children.Add(_wearTrendDot);
            }
            var tip = points[^1];
            System.Windows.Controls.Canvas.SetLeft(_wearTrendDot, tip.X - 2.5);
            System.Windows.Controls.Canvas.SetTop(_wearTrendDot, tip.Y - 2.5);
        }
    }

    private void CanvasWearTrend_SizeChanged(object sender, SizeChangedEventArgs e) => RenderWearTrend();

    private void BtnClearWearHistory_Click(object sender, RoutedEventArgs e)
    {
        var confirm = System.Windows.MessageBox.Show(this,
            Loc.T("Pil aşınma geçmişi silinsin mi? Bu, kaydedilmiş tüm ölçümleri siler ve eğri sıfırdan başlar."),
            Loc.T("Geçmişi sil"), MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        if (!_wearHistory.Clear())
        {
            ShowToast(Loc.T("Pil aşınma geçmişi silinemedi."), false);
            return;
        }
        RenderWearTrend();
        ShowToast(Loc.T("Pil aşınma geçmişi silindi."));
    }

    private void OnBatteryStatusUpdated(BatteryStatusInfo status)
    {
        if (Dispatcher.HasShutdownStarted) return;
        Dispatcher.Invoke(() =>
        {
            if (Dispatcher.HasShutdownStarted) return;

            UpdateHighPowerAvailabilityUi();

            // -1 means Windows could not report the level; show it as unknown instead of a flat battery.
            bool levelKnown = status.BatteryPercent >= 0;
            TxtBatteryHeader.Text = levelKnown ? $"{status.BatteryPercent}%" : "-- %";
            TxtBatLevelBig.Text = levelKnown ? $"{status.BatteryPercent}%" : "-- %";
            BarBatBig.Value = levelKnown ? status.BatteryPercent : 0;

            // Windows keeps its "charging" flag set while the 80 % limit holds the battery at 0 W, so decide by the
            // actual charge current when it was read.
            if (!status.IsPluggedIn)
                TxtBatStateDesc.Text = Loc.T("Pilde Çalışıyor (Deşarj)");
            else if (status.ChargeRateUnknown)
                TxtBatStateDesc.Text = Loc.T("Adaptöre Bağlı");
            else if (status.ChargeRateWatts > 0)
                TxtBatStateDesc.Text = Loc.T("Adaptöre Bağlı (Şarj Ediliyor)");
            else
                TxtBatStateDesc.Text = _batteryService.WatchdogEnabled && status.BatteryPercent >= 78
                    ? Loc.T("Adaptöre Bağlı (Şarj sınırında bekliyor)")
                    : Loc.T("Adaptöre Bağlı (Şarj olmuyor)");

            // Real-time power flow in Watts via TelemetryFormatter
            if (TxtBatDischargeRate != null)
            {
                TxtBatDischargeRate.Text = TelemetryFormatter.FormatBatteryPowerFlow(status);
                if (status.IsDischarging && status.DischargeRateWatts > 0)
                {
                    TxtBatDischargeRate.Foreground = (SolidColorBrush)FindResource("AccentCyan");
                }
                else if (status.IsCharging && status.ChargeRateWatts > 0)
                {
                    TxtBatDischargeRate.Foreground = (SolidColorBrush)FindResource("AccentGreen");
                }
                else
                {
                    TxtBatDischargeRate.Foreground = (SolidColorBrush)FindResource("TextSecondary");
                }
            }

            // On the adapter there is no "time left" to report: the machine is not running off the pack, and the
            // estimate the API returns in that state is a placeholder that read as "Hesaplanıyor..." forever.
            if (TxtBatRemainingTime != null)
            {
                TxtBatRemainingTime.Text = Loc.T($"Kalan Süre: {TelemetryFormatter.FormatRemainingTime(status.EstimatedRemainingMinutes, status.IsPluggedIn, status.IsCharging)}");
                TxtBatRemainingTime.Visibility = status.IsPluggedIn ? Visibility.Collapsed : Visibility.Visible;
            }

            if (status.IsOvercharged)
            {
                BannerOvercharge.Visibility = Visibility.Visible;
                TxtOverchargeMsg.Text = Loc.T($"Pil seviyesi %{status.BatteryPercent} seviyesine ulaştı. Adaptörü çıkararak deşarj işlemini başlatabilirsiniz.");
            }
            else
            {
                BannerOvercharge.Visibility = Visibility.Collapsed;
            }
        });
    }

    private void OnOverchargeAlert(int percent)
    {
        if (Dispatcher.HasShutdownStarted) return;
        Dispatcher.Invoke(() =>
        {
            if (Dispatcher.HasShutdownStarted) return;
            // School mode is a quiet mode: the banner and the balloon still say it, the beep does not.
            if (!_appSettings.SchoolModeEnabled) SystemSounds.Exclamation.Play();
            _notifyIcon?.ShowBalloonTip(3000, Loc.T("NitroLite"), Loc.T($"Pil %{percent} seviyesine ulaştı (%80 eşiği aşıldı)."), System.Windows.Forms.ToolTipIcon.Warning);
        });
    }

    private void SetWatchdogCheckboxSilently(bool isChecked)
    {
        if (ChkWatchdog == null) return;
        _isSyncingWatchdogUi = true;
        try { ChkWatchdog.IsChecked = isChecked; }
        finally { _isSyncingWatchdogUi = false; }
        _batteryService.WatchdogEnabled = isChecked;
    }

    /// <summary>
    /// Drops the stored Care Center certificate thumbprint and reads the limit again. Only reachable from the note
    /// that appears when the agent presented a different certificate, so trusting a new one is always a user action.
    /// </summary>
    private async void BtnRetrustCareCenter_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _careCenterClient.ForgetPinnedCertificate();
            await SyncChargeLimitFromCareCenterAsync();
        }
        catch (Exception ex)
        {
            // ForgetPinnedCertificate writes the pin file; a read-only data folder throws here, and the user would
            // otherwise see nothing at all after pressing the button.
            AppLog.Warn("Care Center sertifikası sıfırlanamadı", ex.ToString());
            ShowToast(Loc.T($"Sertifika sıfırlanamadı: {ex.Message}"), false);
        }
    }

    /// <summary>
    /// The 80 % limit is owned by Acer Care Center; show what it actually reports instead of assuming.
    /// Runs in the background so a missing AcerCCAgent cannot delay startup.
    /// </summary>
    private async Task SyncChargeLimitFromCareCenterAsync()
    {
        try
        {
            var mode = await _careCenterClient.GetBatteryChargeModeAsync();
            if (Dispatcher.HasShutdownStarted) return;

            var state = CareCenterClient.DescribeChargeLimit(mode);
            await Dispatcher.InvokeAsync(() =>
            {
                _chargeLimitAvailable = state.Available;
                if (ChkWatchdog != null) ChkWatchdog.IsEnabled = state.Available;
                bool certificateChanged = !state.Available && _careCenterClient.CertificateMismatch;
                if (TxtChargeLimitNote != null)
                {
                    // "Agent not found" was wrong when the agent answered with a different certificate: the user
                    // would conclude the laptop does not support the limit and never look again.
                    TxtChargeLimitNote.Text = state.Available
                        ? ""
                        : certificateChanged
                            ? Loc.T("Acer Care Center'in sertifikası değişmiş; bağlantı güvenlik için reddedildi. Acer güncellemesinden sonra bu normaldir.")
                            : Loc.T("Acer Care Center (AcerCCAgent) bulunamadı; şarj sınırı bu bilgisayarda ayarlanamıyor.");
                    TxtChargeLimitNote.Visibility = state.Available ? Visibility.Collapsed : Visibility.Visible;
                }
                if (BtnRetrustCareCenter != null)
                {
                    BtnRetrustCareCenter.Visibility = certificateChanged ? Visibility.Visible : Visibility.Collapsed;
                }

                // No agent, no limit: a ticked box would claim a charge limit the machine does not have, and the
                // watchdog would then raise an overcharge alert that nothing is preventing.
                SetWatchdogCheckboxSilently(state.Limited);
                if (state.Available && _appSettings.BatteryWatchdogEnabled != state.Limited)
                {
                    _appSettings.BatteryWatchdogEnabled = state.Limited;
                    SaveSettings();
                }
            });
        }
        catch (Exception ex)
        {
            // The checkbox then claims a charge limit the machine may not have.
            AppLog.Warn("Pil şarj sınırı Care Center'dan okunamadı", ex.Message);
        }
    }

    private async void ChkWatchdog_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || ChkWatchdog == null) return;
        bool enable = ChkWatchdog.IsChecked == true;
        _batteryService.WatchdogEnabled = enable;
        if (_isInitializingUi || _isSyncingWatchdogUi) return;

        ChkWatchdog.IsEnabled = false;
        try
        {
            bool ok = await _careCenterClient.SetBatteryChargeModeAsync(enable ? BatteryChargeMode.Optimized80 : BatteryChargeMode.Full);
            if (ok)
            {
                _appSettings.BatteryWatchdogEnabled = enable;
                SaveSettings();
                ShowToast(enable ? Loc.T("%80 şarj sınırı etkinleştirildi (Acer Care Center).") : Loc.T("Şarj sınırı kapatıldı; pil %100'e kadar şarj olacak."));
            }
            else
            {
                SetWatchdogCheckboxSilently(!enable);
                ShowToast(Loc.T($"Şarj sınırı değiştirilemedi: {_careCenterClient.LastError ?? "bilinmeyen hata"}"), false);
            }
        }
        catch (Exception ex)
        {
            SetWatchdogCheckboxSilently(!enable);
            ShowToast(Loc.T($"Şarj sınırı değiştirilemedi: {ex.Message}"), false);
        }
        finally
        {
            ChkWatchdog.IsEnabled = true;
        }
    }
}
