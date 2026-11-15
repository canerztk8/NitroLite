using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using NitroLite.Core;
using WpfPoint = System.Windows.Point;

namespace NitroLite;

public partial class MainWindow
{
    /// <summary>Fills the CPU/GPU/memory names from Windows instead of hard-coded labels.</summary>
    private async Task LoadHardwareInfoAsync()
    {
        try
        {
            var info = await Task.Run(HardwareInfoService.Get);
            _hardwareInfo = info;

            // The CPU power reading is an estimate built around the processor's rated clock. Feeding it the real
            // rating is what keeps the number honest on a Nitro that is not this one.
            if (info.CpuMaxClockMhz > 0) _hardwareService.CpuReferenceFrequencyMHz = info.CpuMaxClockMhz;

            if (Dispatcher.HasShutdownStarted) return;
            await Dispatcher.InvokeAsync(() =>
            {
                if (TxtMonCpuName != null && info.CpuName.Length > 0) TxtMonCpuName.Text = info.CpuName;
                if (TxtMonGpuName != null && info.DiscreteGpuName.Length > 0) TxtMonGpuName.Text = info.DiscreteGpuName;
                if (TxtMonRamInfo != null) TxtMonRamInfo.Text = info.FormatMemoryText();
            });
        }
        catch { }
    }

    /// <summary>Sets a bar value; if the value exceeds the bar's ceiling the ceiling grows instead of the bar pinning at full.</summary>
    private static void SetBarValueGrowing(System.Windows.Controls.ProgressBar? bar, double value, double step)
    {
        if (bar == null) return;
        if (value > bar.Maximum) bar.Maximum = Math.Ceiling(value / step) * step;
        bar.Value = Math.Max(0, value);
    }

    /// <summary>
    /// Temperature is the one metric where colour carries meaning rather than decoration: the bar and
    /// its reading turn red once the part runs hot, so the panel answers "is anything wrong" at a glance.
    /// </summary>
    private void ApplyTempSeverity(System.Windows.Controls.ProgressBar? bar, TextBlock? value, double celsius, bool available)
    {
        if (bar == null || value == null) return;
        bool hot = available && celsius >= 85;
        var hotBrush = TryFindResource("AccentRed") as SolidColorBrush ?? System.Windows.Media.Brushes.Red;
        var idleBrush = TryFindResource("AccentCyan") as SolidColorBrush ?? System.Windows.Media.Brushes.Goldenrod;
        bar.Foreground = hot ? hotBrush : idleBrush;
        value.Foreground = hot ? hotBrush : (TryFindResource("TextPrimary") as SolidColorBrush ?? System.Windows.Media.Brushes.White);
    }

    private void UpdateServiceTelemetryState(bool serviceAvailable)
    {
        // Ignore a single timeout/reconnect; report loss only after 3 consecutive failed polls.
        if (!serviceAvailable && ++_consecutiveTelemetryFailures < 3) return;
        if (serviceAvailable) _consecutiveTelemetryFailures = 0;

        var previous = _lastServiceTelemetryAvailable;
        _lastServiceTelemetryAvailable = serviceAvailable;
        UpdateServiceBanner(serviceAvailable);
        if (previous == serviceAvailable) return;

        if (!serviceAvailable)
        {
            ShowToast(Loc.T("Acer servisine (AcerAgentService) ulaşılamıyor; yalnızca Windows ve NVIDIA'dan okunabilen veriler gösteriliyor."), false);
        }
        else if (previous == false)
        {
            ShowToast(Loc.T("Acer servisi bağlantısı geri geldi; tüm sensörler okunuyor."));
        }
    }

    /// <summary>
    /// Keeps a standing note on screen while the Acer service is unreachable. The toast is gone after three
    /// seconds, and a window whose every hardware control silently does nothing reads as a broken app rather
    /// than as a machine that is missing the service NitroLite talks to.
    /// </summary>
    private void UpdateServiceBanner(bool serviceAvailable)
    {
        if (ServiceBanner == null) return;

        ServiceBanner.Visibility = serviceAvailable ? Visibility.Collapsed : Visibility.Visible;
        if (serviceAvailable) return;

        if (TxtServiceBannerTitle != null)
            TxtServiceBannerTitle.Text = Loc.T("ACER SERVİSİNE ULAŞILAMIYOR");
        if (TxtServiceBannerBody != null)
            TxtServiceBannerBody.Text = Loc.T("Modlar, fan, klavye aydınlatması ve şarj sınırı Acer servisi üzerinden çalışır. NitroSense ile gelen AcerAgentService kurulu ve çalışıyor olmalı. Windows ve NVIDIA'dan okunabilen veriler gösterilmeye devam eder.");
    }

    private void ApplyServiceUnavailableTelemetryText(MonitorData data)
    {
        TxtSideCpuTemp.Text = ServiceUnavailableText;
        TxtSideCpuFan.Text = ServiceUnavailableText;
        TxtMonCpuTemp.Text = ServiceUnavailableText;
        TxtCpuFanRpm.Text = ServiceUnavailableText;
        TxtGpuFanRpm.Text = ServiceUnavailableText;
        TxtMonSysTemp.Text = ServiceUnavailableText;
        TxtMonCpuFreqGhz.Text = ServiceUnavailableText;
        if (TxtSubMoboTemp != null)
        {
            // A sentence, not a reading: it stays visible where a "-- °C" placeholder would be hidden.
            TxtSubMoboTemp.Text = ServiceUnavailableText;
            TxtSubMoboTemp.Visibility = Visibility.Visible;
        }
        if (data.GpuTemperature <= 0)
        {
            // NVML did not report either; do not claim the GPU is asleep.
            TxtSideGpuTemp.Text = ServiceUnavailableText;
            TxtMonGpuTemp.Text = ServiceUnavailableText;
        }
        TxtMonGpuUsage.Text = ServiceUnavailableText;
    }

    private async Task PollTelemetryAsync()
    {
        if (_isPolling) return;
        _isPolling = true;

        try
        {
            var data = await _agentClient.GetMonitorDataAsync();

            // AASSvc unavailable: fall back to what Windows/NVML can measure (CPU load, RAM, GPU via NVML).
            // Sensors only the Acer service provides (CPU temperature, fans, board temperature) are shown as
            // unavailable, and the sample is kept out of thermal history, the chart and the OSD.
            // NVML is only touched on the adapter: with the service gone there is no GPU1_TEMPERATURE to say whether
            // the dGPU is asleep, and waking it every second on battery is exactly the trap this avoids.
            bool isFallback = data == null;
            if (isFallback)
            {
                data = _hardwareService.CreateFallbackMonitorData(allowNvml: _powerAutomationService?.LastAcStatus == true);
            }
            UpdateServiceTelemetryState(serviceAvailable: !isFallback);

        if (data != null)
        {
            if (!isFallback)
            {
                // Enrich with GPU Wattage, VRAM and CPU Package Power
                // GPU1_TEMPERATURE 0 from AASSvc means the dGPU is powered down; do not wake it via NVML.
                _hardwareService.EnrichMonitorData(data, gpuAsleepPerService: data.GpuTemperature <= 0);
            }
            _telemetrySampleIndex++;

            // The software fan curve follows the temperature this poll already fetched. The last reading is kept
            // even while the curve is off, so turning it on acts on the current temperature instead of waiting.
            // The GPU reading goes along: the curve follows the hotter die, so a stuck CPU sensor cannot leave the
            // fans at idle speed while the GPU cooks.
            int hottestReading = Math.Max(data.CpuTemperature, data.GpuTemperature);
            // A sentinel (0xFF) is not a temperature: storing it would make the next curve edit ask for full speed.
            if (hottestReading > 0 && hottestReading <= TelemetryFormatter.MaxPlausibleTemperature)
            {
                _fanCurveLastTemperatureC = hottestReading;
            }
            if (_fanCurveEnabled)
            {
                await ApplyFanCurveAsync(data.CpuTemperature, data.GpuTemperature);
                UpdateFanCurveStatusText();
            }

            // A fan that is not turning while its die is hot is a hardware fault, not a preference. Judged only on
            // a real AASSvc sample: the fallback path reports 0 RPM because nothing measured the fans.
            if (!isFallback) await CheckFanStallAsync(data);

            bool isWindowVisible = IsVisible && WindowState != WindowState.Minimized;
            bool isGpuSleeping = data.GpuTemperature <= 0;

            if (isWindowVisible)
            {
                // Titlebar / Sidebar
                TxtSideCpuTemp.Text = TelemetryFormatter.FormatCpuTemperature(data.CpuTemperature);
                TxtSideGpuTemp.Text = TelemetryFormatter.FormatGpuTemperature(data.GpuTemperature, isGpuSleeping, data.GpuMetricsAvailable);
                TxtSideCpuFan.Text = TelemetryFormatter.FormatFanSpeedRpm(data.CpuFanSpeed);

                // Sub-Component Thermal Matrix: Motherboard / VRM (SYS1_TEMPERATURE)
                if (TxtSubMoboTemp != null)
                    ShowTemperature(TxtSubMoboTemp, TelemetryFormatter.FormatTemperature(data.SysTemperature > 0 ? data.SysTemperature : null));

                // Tab 1: Fans
                TxtCpuFanRpm.Text = TelemetryFormatter.FormatFanSpeedRpm(data.CpuFanSpeed);
                TxtGpuFanRpm.Text = TelemetryFormatter.FormatFanSpeedRpm(data.GpuFanSpeed);
                BarCpuFan.Value = data.CpuFanSpeed;
                BarGpuFan.Value = data.GpuFanSpeed;

                // Tab 2: Monitoring - CPU
                var coreText = (_hardwareInfo?.FormatCoreText(data.CpuCores)) ?? (data.CpuCores > 0 ? Loc.T($"{data.CpuCores} iş parçacığı") : "");
                TxtCpuCoreCount.Text = string.IsNullOrEmpty(coreText) ? "" : $" ({coreText})";
                TxtMonCpuTemp.Text = TelemetryFormatter.FormatCpuTemperature(data.CpuTemperature);
                BarMonCpuTemp.Value = data.CpuTemperature;
                ApplyTempSeverity(BarMonCpuTemp, TxtMonCpuTemp, data.CpuTemperature, data.CpuTemperature > 0);

                TxtMonCpuPower.Text = TelemetryFormatter.FormatCpuPower(data.CpuPowerWatts) + Loc.T(" (tahmini)");
                SetBarValueGrowing(BarMonCpuPower, data.CpuPowerWatts, 10);

                TxtMonCpuUsage.Text = FormattableString.Invariant($"{data.CpuUsage:F1} %");
                BarMonCpuUsage.Value = data.CpuUsage;

                TxtMonCpuFreqGhz.Text = TelemetryFormatter.FormatCpuFrequency(data.CpuFrequencyMHz);
                SetBarValueGrowing(BarMonCpuFreq, data.CpuFrequencyMHz, 100);

                // Tab 2: Monitoring - GPU
                TxtMonGpuTemp.Text = TelemetryFormatter.FormatGpuTemperature(data.GpuTemperature, isGpuSleeping, data.GpuMetricsAvailable);
                BarMonGpuTemp.Value = data.GpuTemperature;
                ApplyTempSeverity(BarMonGpuTemp, TxtMonGpuTemp, data.GpuTemperature, data.GpuMetricsAvailable && !isGpuSleeping);

                TxtMonGpuPower.Text = TelemetryFormatter.FormatGpuPower(data.GpuPowerWatts, isGpuSleeping, data.GpuMetricsAvailable);
                SetBarValueGrowing(BarMonGpuPower, data.GpuPowerWatts, 10);

                TxtMonGpuUsage.Text = FormattableString.Invariant($"{data.GpuUsage:F1} %");
                BarMonGpuUsage.Value = data.GpuUsage;

                BarMonVram.Maximum = data.VramTotalMB > 0 ? data.VramTotalMB : 8192;
                BarMonVram.Value = Math.Min(data.VramUsedMB, BarMonVram.Maximum);
                TxtMonVram.Text = TelemetryFormatter.FormatVramUsage(data.VramUsedMB, data.VramTotalMB, isGpuSleeping, data.GpuMetricsAvailable);

                // RAM
                if (data.RamTotalMB > 0)
                {
                    var ramUsedGB = (data.RamTotalMB * (data.RamUsage / 100.0)) / 1024.0;
                    var ramTotalGB = data.RamTotalMB / 1024.0;
                    TxtMonRamDetail.Text = FormattableString.Invariant($"{ramUsedGB:F1} GB / {ramTotalGB:F1} GB");
                    TxtMonRamPercent.Text = FormattableString.Invariant($"{data.RamUsage:F1} %");
                    BarMonRam.Value = data.RamUsage;
                }
                else
                {
                    TxtMonRamDetail.Text = Loc.T("-- GB / -- GB");
                    TxtMonRamPercent.Text = "-- %";
                    BarMonRam.Value = 0;
                }

                TxtMonSysTemp.Text = TelemetryFormatter.FormatTemperature(data.SysTemperature > 0 ? data.SysTemperature : null);

                if (isFallback)
                {
                    ApplyServiceUnavailableTelemetryText(data);
                }
            }

            // Update tray tooltip regardless of window visibility
            if (_notifyIcon != null)
            {
                var tip = isFallback
                    ? Loc.T($"NitroLite: Acer servisi yanıt vermiyor | CPU: %{data.CpuUsage:F0}")
                    : Loc.T($"NitroLite: {_currentMode} | CPU: {data.CpuTemperature}°C | GPU: {data.GpuTemperature}°C");
                _notifyIcon.Text = tip.Length > 63 ? tip[..63] : tip;
            }

            _ = RetryPendingDesktopMouseProfileIfDueAsync(Environment.TickCount64);
            _ = RetryPendingGameMouseProfileAsync(Environment.TickCount64);
            // Retry a desktop restore that failed or was interrupted, even without a crash record: the pending
            // flags are the source of truth. The AC restore has its own retry below.
            bool pendingGameRestore = _interruptedGameRecovery != null || _hasPendingDesktopRestore ||
                                      _pendingModeRestore || _pendingCpuBoostRestore || _pendingFanRestore || _pendingMouseRestore;
            if (!_isInitializingUi && pendingGameRestore && Environment.TickCount64 >= _nextGameRecoveryAttempt)
            {
                _nextGameRecoveryAttempt = Environment.TickCount64 + 10000;
                _ = RetryPendingDesktopRestoreAsync();
            }
            // A failed Eco step (mode/boost/fan) or a lowered panel rate is retried here: nothing else calls
            // EvaluatePowerStateAsync until the next power event.
            if (!_isInitializingUi && _powerAutomationService is { } automation &&
                (automation.HasPendingPanelRestore || automation.HasPendingDcApply) &&
                Environment.TickCount64 >= _nextPanelRestoreAttempt)
            {
                _nextPanelRestoreAttempt = Environment.TickCount64 + 15000;
                _ = automation.EvaluatePowerStateAsync();
            }

            if (!isFallback)
            {
                // Update OSD if open
                if (_isOsdOpen && _osdWindow != null && _osdWindow.IsVisible)
                {
                    _osdWindow.UpdateData(data);
                }

                // Keep the last 60 seconds by time (not by count: polling is 3 s while hidden, 1 s while shown).
                data.Timestamp = DateTime.Now;
                _telemetryHistory.Add(data);
                var historyCutoff = data.Timestamp - ChartWindow;
                while (_telemetryHistory.Count > 0 && (_telemetryHistory[0].Timestamp < historyCutoff || _telemetryHistory.Count > 240))
                {
                    _telemetryHistory.RemoveAt(0);
                }

                RenderTelemetryChart();
                RenderModeTrace();

                // Live standby/free sizes in the RAM card while it is on screen (one cheap syscall).
                if (IsVisible && TabMonitoring?.Visibility == Visibility.Visible && !_ramCleanBusy) UpdateMemoryListText();
            }

            // Auto-sync game dropdown to running active game if tab is visible
            var activeGame = _gameService.ActiveGameProfile;
            if (activeGame != null && !_isUpdatingGameUi && CmbConfiguredGames != null && TabModes?.Visibility == Visibility.Visible)
            {
                var selectedExe = GetSelectedGameExeName();
                bool needsSync = selectedExe == null ||
                                 !selectedExe.Equals(activeGame.ExeName, StringComparison.OrdinalIgnoreCase);

                if (needsSync)
                {
                    if (_configuredGameExeNames.Any(e => e.Equals(activeGame.ExeName, StringComparison.OrdinalIgnoreCase)))
                    {
                        _isUpdatingGameUi = true;
                        try { SelectConfiguredGame(activeGame.ExeName); }
                        finally { _isUpdatingGameUi = false; }
                        UpdateSelectedGameDetails();
                    }
                    else if (_gameService.GetProfile(activeGame.ExeName, activeGame.InstallPath) != null)
                    {
                        // The active game is not in the displayed snapshot (list changed underneath); rebuild it.
                        PopulateConfiguredGames(activeGame.ExeName);
                    }
                }
            }

            // Re-read operating mode, fan mode and CPU boost so changes made elsewhere (NitroSense, the mode key,
            // Windows power settings) are reflected instead of showing the last value NitroLite set.
            if (!isFallback
                && (_lastDeviceStateSyncAt == TimeSpan.MinValue
                    || (_deviceStateClock.Elapsed - _lastDeviceStateSyncAt) >= DeviceStateSyncInterval))
            {
                _lastDeviceStateSyncAt = _deviceStateClock.Elapsed;
                await SyncDeviceStateAsync();
            }

        }
        }
        catch (Exception ex)
        {
            // The tick handler is async void, so an exception here used to reach DispatcherUnhandledException and be
            // swallowed five times before the app was allowed to crash. One bad sample must cost one sample, not the
            // whole session: the next tick starts clean because _isPolling is reset in finally.
            AppLog.Warn("Telemetri turu başarısız", ex.ToString());
        }
        finally
        {
            _isPolling = false;
        }
    }

    private void CmbChartMetric_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _isInitializingUi || CmbChartMetric.SelectedItem is not ComboBoxItem item || item.Tag is not string tag) return;
        _selectedChartMetric = tag;
        _appSettings.SelectedChartMetric = tag;
        SaveSettings();
        RenderTelemetryChart();
    }

    /// <summary>
    /// Value of <paramref name="metric"/> in sample <paramref name="d"/>, or null when the sample has no real reading
    /// for it (sensor 0, or the dGPU asleep for GPU metrics), so gaps do not drag min/average down.
    /// </summary>
    internal static double? GetChartValue(string metric, MonitorData d)
    {
        bool gpuAwake = d.GpuTemperature > 0;
        double? v = metric switch
        {
            "CPU_TEMP" => d.CpuTemperature > 0 ? d.CpuTemperature : null,
            "GPU_TEMP" => gpuAwake ? d.GpuTemperature : null,
            "CPU_WATT" => d.CpuPowerWatts > 0 ? d.CpuPowerWatts : null,
            "GPU_WATT" => gpuAwake ? d.GpuPowerWatts : null,
            "CPU_GHZ" => d.CpuFrequencyMHz > 0 ? d.CpuFrequencyMHz / 1000.0 : null,
            "CPU_USAGE" => d.CpuUsage,
            "GPU_USAGE" => gpuAwake ? d.GpuUsage : null,
            "RAM_USAGE" => d.RamTotalMB > 0 ? d.RamUsage : null,
            "VRAM_GB" => gpuAwake && d.VramTotalMB > 0 ? d.VramUsedMB / 1024.0 : null,
            _ => d.CpuTemperature > 0 ? d.CpuTemperature : null
        };
        return v.HasValue && !double.IsNaN(v.Value) && !double.IsInfinity(v.Value) ? v : null;
    }

    private void ClearTelemetryChart(string unit)
    {
        string dash = string.IsNullOrEmpty(unit) ? "--" : $"-- {unit}";
        TxtChartCurrentVal.Text = dash;
        TxtChartMinVal.Text = dash;
        TxtChartMaxVal.Text = dash;
        TxtChartAvgVal.Text = dash;
        ChartLine.Points = new PointCollection();
        ChartArea.Points = new PointCollection();
        if (ChartDot != null) ChartDot.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Draws the recording strip on the modes screen: the last 60 seconds of CPU temperature as a
    /// continuous ink trace, a vermilion "now" edge, and a tick wherever the mode changed. The strip
    /// is the surface's signature move, so it reads the same history the chart uses instead of
    /// running a second sampler.
    /// </summary>
    private void BtnToggleTrace_Click(object sender, RoutedEventArgs e)
    {
        SetModeTraceCollapsed(!_appSettings.ModeTraceCollapsed, persist: true);
    }

    /// <summary>
    /// Folds the live temperature strip away. The header row stays, so the way back is always on screen; only the
    /// drawing area goes, which is the height the game profiles below it need.
    /// </summary>
    internal void SetModeTraceCollapsed(bool collapsed, bool persist)
    {
        _appSettings.ModeTraceCollapsed = collapsed;
        if (TraceChartBorder != null)
            TraceChartBorder.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        if (TxtToggleTrace != null)
            TxtToggleTrace.Text = collapsed ? Loc.T("GÖSTER") : Loc.T("GİZLE");
        if (BtnToggleTrace != null)
            BtnToggleTrace.ToolTip = collapsed ? Loc.T("Grafiği göster") : Loc.T("Grafiği gizle");
        if (persist) SaveSettings();
    }

    private void RenderModeTrace()
    {
        if (CanvasModeTrace == null || TraceLine == null) return;
        if (!IsVisible || WindowState == WindowState.Minimized) return;
        if (TabModes == null || TabModes.Visibility != Visibility.Visible) return;

        double width = CanvasModeTrace.ActualWidth;
        double height = CanvasModeTrace.ActualHeight;
        if (width <= 0 || height <= 0) return;

        var now = DateTime.Now;
        // The strip shows everything recorded so far, up to the 60 s window: a fresh recording fills
        // the paper instead of hugging the right edge, and the span is written out so the scale is
        // never guessed.
        var span = ChartWindow;
        if (_telemetryHistory.Count > 0)
        {
            var recorded = now - _telemetryHistory[0].Timestamp;
            if (recorded < span) span = recorded;
        }
        if (span < TimeSpan.FromSeconds(5)) span = TimeSpan.FromSeconds(5);
        var windowStart = now - span;
        var samples = new List<(double X, double Value)>();
        foreach (var d in _telemetryHistory)
        {
            if (d.Timestamp < windowStart) continue;
            var v = GetChartValue("CPU_TEMP", d);
            if (!v.HasValue) continue;
            double x = (d.Timestamp - windowStart).TotalSeconds / span.TotalSeconds * width;
            samples.Add((x, v.Value));
        }

        if (TxtTraceSpan != null) TxtTraceSpan.Text = Loc.T($"KAYIT · {span.TotalSeconds:0} SANİYE");

        if (samples.Count < 2)
        {
            TraceLine.Points = new PointCollection();
            if (TxtTraceValue != null) TxtTraceValue.Text = "-- °C";
            if (TxtTraceRange != null) TxtTraceRange.Text = "--";
            if (TxtTraceEmpty != null) TxtTraceEmpty.Visibility = Visibility.Visible;
            HideTraceMarks(0);
            return;
        }

        if (TxtTraceEmpty != null) TxtTraceEmpty.Visibility = Visibility.Collapsed;

        double low = samples[0].Value, high = samples[0].Value;
        foreach (var s in samples)
        {
            if (s.Value < low) low = s.Value;
            if (s.Value > high) high = s.Value;
        }
        // A flat trace must not sit on the floor: hold a 4 °C band around the value.
        if (high - low < 4)
        {
            double mid = (high + low) / 2;
            low = mid - 2;
            high = mid + 2;
        }

        const double pad = 7;
        double usable = Math.Max(1, height - pad * 2);
        var points = new PointCollection(samples.Count);
        foreach (var s in samples)
        {
            double y = pad + (1 - (s.Value - low) / (high - low)) * usable;
            points.Add(new WpfPoint(s.X, y));
        }
        TraceLine.Points = points;

        if (TraceNow != null)
        {
            TraceNow.X1 = width - 0.5;
            TraceNow.X2 = width - 0.5;
            TraceNow.Y1 = 0;
            TraceNow.Y2 = height;
        }

        if (TxtTraceValue != null) TxtTraceValue.Text = $"{samples[samples.Count - 1].Value:0} °C";
        if (TxtTraceRange != null) TxtTraceRange.Text = $"{low:0}–{high:0} °C";

        DrawTraceMarks(windowStart, span, width, height);
    }

    private void DrawTraceMarks(DateTime windowStart, TimeSpan span, double width, double height)
    {
        if (TraceMarks == null) return;

        var ink = TryFindResource("Vermilion") as SolidColorBrush ?? System.Windows.Media.Brushes.Red;
        while (_traceMarkLines.Count < MaxTraceMarks)
        {
            var line = new System.Windows.Shapes.Line
            {
                Stroke = ink,
                StrokeThickness = 1,
                Visibility = Visibility.Collapsed,
                SnapsToDevicePixels = true
            };
            TraceMarks.Children.Add(line);
            _traceMarkLines.Add(line);
        }

        int used = 0;
        foreach (var mark in _modeMarks)
        {
            if (mark.At < windowStart || used >= MaxTraceMarks) continue;
            double x = (mark.At - windowStart).TotalSeconds / span.TotalSeconds * width;
            var line = _traceMarkLines[used++];
            line.X1 = x;
            line.X2 = x;
            line.Y1 = 0;
            line.Y2 = height;
            line.Visibility = Visibility.Visible;
        }
        HideTraceMarks(used);
    }

    private void HideTraceMarks(int from)
    {
        for (int i = from; i < _traceMarkLines.Count; i++)
        {
            _traceMarkLines[i].Visibility = Visibility.Collapsed;
        }
    }

    private void RenderTelemetryChart()
    {
        if (CanvasHistoryChart == null) return;
        if (!IsVisible || WindowState == WindowState.Minimized || TabMonitoring?.Visibility != Visibility.Visible) return;

        double width = CanvasHistoryChart.ActualWidth;
        double height = CanvasHistoryChart.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            width = 720;
            height = 140;
        }

        // Time-based window: x follows the sample time, so the chart really spans the last 60 seconds whatever the
        // polling rate was (3 s while hidden, 1 s while shown).
        var now = DateTime.Now;
        var windowStart = now - ChartWindow;
        var samples = new List<(DateTime Time, double Value)>();
        foreach (var d in _telemetryHistory)
        {
            if (d.Timestamp < windowStart) continue;
            var v = GetChartValue(_selectedChartMetric, d);
            if (v.HasValue) samples.Add((d.Timestamp, v.Value));
        }
        List<double> values = samples.Select(s => s.Value).ToList();

        string unit = _selectedChartMetric switch
        {
            "CPU_TEMP" or "GPU_TEMP" => "°C",
            "CPU_WATT" or "GPU_WATT" => "W",
            "CPU_GHZ" => "GHz",
            "CPU_USAGE" or "GPU_USAGE" or "RAM_USAGE" => "%",
            "VRAM_GB" => "GB",
            _ => ""
        };

        if (values.Count < 2)
        {
            ClearTelemetryChart(unit);
            return;
        }

        double current = values.Last();
        double min = values.Min();
        double max = values.Max();
        double avg = values.Average();

        bool isFine = _selectedChartMetric is "CPU_GHZ" or "VRAM_GB";
        TxtChartCurrentVal.Text = isFine ? FormattableString.Invariant($"{current:F2} {unit}") : FormattableString.Invariant($"{current:F1} {unit}");
        TxtChartMinVal.Text = isFine ? FormattableString.Invariant($"{min:F2} {unit}") : FormattableString.Invariant($"{min:F1} {unit}");
        TxtChartMaxVal.Text = isFine ? FormattableString.Invariant($"{max:F2} {unit}") : FormattableString.Invariant($"{max:F1} {unit}");
        TxtChartAvgVal.Text = isFine ? FormattableString.Invariant($"{avg:F2} {unit}") : FormattableString.Invariant($"{avg:F1} {unit}");

        double rangeMin;
        double rangeMax;

        switch (_selectedChartMetric)
        {
            case "CPU_GHZ":
                // Dynamic adaptive scaling for CPU clock: auto-scale so 100 MHz variations show visible peaks & valleys
                double ghzMin = Math.Floor(min * 2.0) / 2.0 - 0.25;
                double ghzMax = Math.Ceiling(max * 2.0) / 2.0 + 0.25;
                if (ghzMax - ghzMin < 0.8) { ghzMin = Math.Max(0.5, ghzMin - 0.4); ghzMax = ghzMin + 0.8; }
                rangeMin = Math.Max(0.0, ghzMin);
                rangeMax = ghzMax;
                break;

            case "CPU_TEMP":
            case "GPU_TEMP":
                // Adaptive scale for temperature: 5°C padding so thermal curves are prominent
                double tempMin = Math.Floor(min / 5.0) * 5.0 - 5.0;
                double tempMax = Math.Ceiling(max / 5.0) * 5.0 + 5.0;
                if (tempMax - tempMin < 15.0) { tempMin = Math.Max(25.0, tempMin - 5.0); tempMax = tempMin + 15.0; }
                rangeMin = Math.Max(20.0, tempMin);
                rangeMax = Math.Min(115.0, tempMax);
                break;

            case "CPU_WATT":
            case "GPU_WATT":
                double wattMin = Math.Max(0.0, Math.Floor(min / 10.0) * 10.0 - 5.0);
                double wattMax = Math.Ceiling(max / 10.0) * 10.0 + 10.0;
                if (wattMax - wattMin < 25.0) wattMax = wattMin + 25.0;
                rangeMin = wattMin;
                rangeMax = wattMax;
                break;

            case "CPU_USAGE":
            case "GPU_USAGE":
            case "RAM_USAGE":
                if (max < 40.0)
                {
                    rangeMin = 0.0;
                    rangeMax = 50.0;
                }
                else
                {
                    rangeMin = 0.0;
                    rangeMax = 100.0;
                }
                break;

            case "VRAM_GB":
                rangeMin = 0.0;
                rangeMax = Math.Max(8.0, Math.Ceiling(max + 1.0));
                break;

            default:
                rangeMin = Math.Max(0.0, min - 5.0);
                rangeMax = max + 5.0;
                break;
        }

        // Update grid reference labels
        if (TxtChartGridTop != null)
            TxtChartGridTop.Text = isFine ? FormattableString.Invariant($"{rangeMax:F1} {unit}") : $"{(int)rangeMax} {unit}";
        if (TxtChartGridMid != null)
            TxtChartGridMid.Text = isFine ? FormattableString.Invariant($"{((rangeMin + rangeMax) / 2.0):F1} {unit}") : $"{(int)((rangeMin + rangeMax) / 2.0)} {unit}";
        if (TxtChartGridBottom != null)
            TxtChartGridBottom.Text = isFine ? FormattableString.Invariant($"{rangeMin:F1} {unit}") : $"{(int)rangeMin} {unit}";

        double range = rangeMax - rangeMin;
        if (range <= 0) range = 1;

        int count = samples.Count;
        double windowSeconds = ChartWindow.TotalSeconds;

        var linePoints = new PointCollection();
        var areaPoints = new PointCollection();

        double firstX = Math.Clamp((samples[0].Time - windowStart).TotalSeconds / windowSeconds, 0.0, 1.0) * width;
        areaPoints.Add(new WpfPoint(firstX, height));

        double lastX = 0;
        double lastY = height / 2;

        for (int i = 0; i < count; i++)
        {
            double x = Math.Clamp((samples[i].Time - windowStart).TotalSeconds / windowSeconds, 0.0, 1.0) * width;
            double normalized = Math.Clamp((samples[i].Value - rangeMin) / range, 0.0, 1.0);
            double y = height - (normalized * (height - 24)) - 12;

            var pt = new WpfPoint(x, y);
            linePoints.Add(pt);
            areaPoints.Add(pt);

            lastX = x;
            lastY = y;
        }

        areaPoints.Add(new WpfPoint(lastX, height));

        ChartLine.Points = linePoints;
        ChartArea.Points = areaPoints;

        if (ChartDot != null)
        {
            Canvas.SetLeft(ChartDot, lastX - 3.5);
            Canvas.SetTop(ChartDot, lastY - 3.5);
            ChartDot.Visibility = Visibility.Visible;
        }
    }

    // ==========================================
    // On-Screen Display (OSD Overlay)
    // ==========================================

    private void BtnToggleOsd_Click(object sender, RoutedEventArgs e)
    {
        ToggleOsd(!_isOsdOpen);
    }

    private void BtnCycleOsdCorner_Click(object sender, RoutedEventArgs e)
    {
        if (_osdWindow == null)
        {
            _osdWindow = new OsdOverlayWindow();
            _osdWindow.SetCorner(_appSettings.OsdCorner);
            _osdWindow.SetMonitor(_appSettings.OsdMonitor);
        }
        _osdWindow.CycleCorner();
        _appSettings.OsdCorner = _osdWindow.CurrentCorner;
        _appSettings.OsdMonitor = _osdWindow.CurrentMonitor;
        SaveSettings();

        string cornerDesc = _osdWindow.CurrentCorner switch
        {
            0 => Loc.T("Sol Üst"),
            1 => Loc.T("Sağ Üst"),
            2 => Loc.T("Sağ Alt"),
            3 => Loc.T("Sol Alt"),
            _ => Loc.T("Sol Üst")
        };
        if (OsdOverlayWindow.MonitorCount > 1)
        {
            cornerDesc += Loc.T($", monitör {_osdWindow.CurrentMonitor + 1}/{OsdOverlayWindow.MonitorCount}");
        }
        ShowToast(_isOsdOpen
            ? Loc.T($"OSD konumu: {cornerDesc}")
            : Loc.T($"OSD kapalı; konum kaydedildi ({cornerDesc}). Açınca orada görünecek."));
    }

    private void ToggleOsd(bool enable, bool showToast = true)
    {
        _isOsdOpen = enable;
        _appSettings.OsdEnabled = enable;
        if (!_isInitializingUi)
        {
            SaveSettings();
        }

        if (_isOsdOpen)
        {
            if (_osdWindow == null)
            {
                _osdWindow = new OsdOverlayWindow();
            }
            _osdWindow.SetCorner(_appSettings.OsdCorner);
            _osdWindow.SetMonitor(_appSettings.OsdMonitor);
            _osdWindow.Show();
            UpdateTelemetryPollingRate();
            BtnToggleOsd.Background = (SolidColorBrush)FindResource("AccentCyan");
            BtnToggleOsd.Foreground = TryFindResource("OnAccent") as SolidColorBrush ?? System.Windows.Media.Brushes.Black;
            BtnToggleOsd.Content = Loc.T("OSD Katmanı: Açık (Kapat)");

            if (showToast) ShowToast(Loc.T("Ekran üstü gösterge etkinleştirildi."));
        }
        else
        {
            _osdWindow?.Hide();
            UpdateTelemetryPollingRate();
            BtnToggleOsd.Background = System.Windows.Media.Brushes.Transparent;
            BtnToggleOsd.Foreground = (SolidColorBrush)FindResource("AccentCyan");
            BtnToggleOsd.Content = Loc.T("Ekran Üstü Gösterge (OSD)");
            if (showToast) ShowToast(Loc.T("Ekran üstü gösterge kapatıldı."));
        }
    }

    private async Task RefreshStorageHealthUi()
    {
        var drives = await Task.Run(() => _storageService.GetDrives());
        if (Dispatcher.HasShutdownStarted) return;
        Dispatcher.Invoke(() =>
        {
            if (Dispatcher.HasShutdownStarted) return;

            var d0 = drives.FirstOrDefault(d => d.DiskNumber == 0) ?? (drives.Count > 0 ? drives[0] : null);
            if (d0 != null)
            {
                if (CardDrive0 != null) CardDrive0.Visibility = Visibility.Visible;
                if (TxtDrive0Letter != null)
                {
                    string letter = !string.IsNullOrEmpty(d0.DriveLetter) ? d0.DriveLetter : "C";
                    TxtDrive0Letter.Text = letter.Contains(',') ? $"[{letter}] " : $"[{letter}:] ";
                }
                if (TxtDrive0Name != null) TxtDrive0Name.Text = $"{d0.Model}";
                if (TxtDrive0Bus != null)
                {
                    TxtDrive0Bus.Text = string.IsNullOrWhiteSpace(d0.Firmware)
                        ? $" ({d0.BusType})"
                        : $" ({d0.BusType} - {d0.Firmware})";
                }
                if (TxtDrive0Detail != null) TxtDrive0Detail.Text = TelemetryFormatter.FormatDiskCapacity(d0.UsedCapacityGB, d0.TotalCapacityGB);
                if (TxtDrive0Free != null) TxtDrive0Free.Text = Loc.TInv($"{d0.FreeCapacityGB:F1} GB Boş Alan");
                if (BarDrive0 != null)
                {
                    BarDrive0.Value = d0.UsedPercent;
                    BarDrive0.Foreground = (SolidColorBrush)FindResource(d0.UsedPercent > 85 ? "AccentRed" : "Ink");
                }
                if (TxtDrive0HealthBadge != null)
                {
                    TxtDrive0HealthBadge.Text = d0.EstHealthPercent >= 0
                        ? Loc.T($"Sağlık: %{d0.EstHealthPercent}")
                        : (string.IsNullOrWhiteSpace(d0.HealthDesc) ? Loc.T("Aşınma ölçülemiyor (yönetici izni gerekli)") : d0.HealthDesc);
                }
                if (TxtDrive0Temp != null)
                {
                    ShowTemperature(TxtDrive0Temp, TelemetryFormatter.FormatTemperature(d0.Temperature));
                }
                if (TxtSubSsd1Temp != null)
                {
                    ShowTemperature(TxtSubSsd1Temp, TelemetryFormatter.FormatTemperature(d0.Temperature));
                    if (TxtSubSsd1Label != null) TxtSubSsd1Label.Text = string.IsNullOrEmpty(d0.DriveLetter) ? Loc.T("Disk 1") : Loc.T($"Disk 1 ({d0.DriveLetter}:)");
                    if (TxtSubSsd1Name != null) TxtSubSsd1Name.Text = d0.Model;
                }
            }
            else
            {
                if (CardDrive0 != null) CardDrive0.Visibility = Visibility.Collapsed;
            }

            var d1 = drives.FirstOrDefault(d => d.DiskNumber == 1 && d != d0) ?? (drives.Count > 1 ? drives.FirstOrDefault(d => d != d0) : null);
            if (d1 != null)
            {
                if (CardDrive1 != null) CardDrive1.Visibility = Visibility.Visible;
                if (TxtDrive1Letter != null)
                {
                    string letter = !string.IsNullOrEmpty(d1.DriveLetter) ? d1.DriveLetter : "D";
                    TxtDrive1Letter.Text = letter.Contains(',') ? $"[{letter}] " : $"[{letter}:] ";
                }
                if (TxtDrive1Name != null) TxtDrive1Name.Text = $"{d1.Model}";
                if (TxtDrive1Bus != null)
                {
                    TxtDrive1Bus.Text = string.IsNullOrWhiteSpace(d1.Firmware)
                        ? $" ({d1.BusType})"
                        : $" ({d1.BusType} - {d1.Firmware})";
                }
                if (TxtDrive1Detail != null) TxtDrive1Detail.Text = TelemetryFormatter.FormatDiskCapacity(d1.UsedCapacityGB, d1.TotalCapacityGB);
                if (TxtDrive1Free != null) TxtDrive1Free.Text = Loc.TInv($"{d1.FreeCapacityGB:F1} GB Boş Alan");
                if (BarDrive1 != null)
                {
                    BarDrive1.Value = d1.UsedPercent;
                    BarDrive1.Foreground = (SolidColorBrush)FindResource(d1.UsedPercent > 85 ? "AccentRed" : "Ink");
                }
                if (TxtDrive1HealthBadge != null)
                {
                    TxtDrive1HealthBadge.Text = d1.EstHealthPercent >= 0
                        ? Loc.T($"Sağlık: %{d1.EstHealthPercent}")
                        : (string.IsNullOrWhiteSpace(d1.HealthDesc) ? Loc.T("Aşınma ölçülemiyor (yönetici izni gerekli)") : d1.HealthDesc);
                }
                if (TxtDrive1Temp != null)
                {
                    ShowTemperature(TxtDrive1Temp, TelemetryFormatter.FormatTemperature(d1.Temperature));
                }
                if (TxtSubSsd2Temp != null)
                {
                    ShowTemperature(TxtSubSsd2Temp, TelemetryFormatter.FormatTemperature(d1.Temperature));
                    if (TxtSubSsd2Label != null) TxtSubSsd2Label.Text = string.IsNullOrEmpty(d1.DriveLetter) ? Loc.T("Disk 2") : Loc.T($"Disk 2 ({d1.DriveLetter}:)");
                    if (TxtSubSsd2Name != null) TxtSubSsd2Name.Text = d1.Model;
                }
            }
            else
            {
                if (CardDrive1 != null) CardDrive1.Visibility = Visibility.Collapsed;
            }

            if (TxtStorageBadge != null)
            {
                bool anyProblem = drives.Any(d => d.HealthStatus == 1 || d.HealthStatus == 2);
                bool allHealthy = drives.Count > 0 && drives.All(d => d.HealthStatus == 0);
                if (anyProblem)
                {
                    TxtStorageBadge.Text = Loc.T("Sürücü Uyarısı Mevcut");
                    TxtStorageBadge.Foreground = (SolidColorBrush)FindResource("AccentRed");
                }
                else if (allHealthy)
                {
                    TxtStorageBadge.Text = Loc.T("Tüm Sürücüler Sağlıklı");
                    TxtStorageBadge.Foreground = (SolidColorBrush)FindResource("AccentGreen");
                }
                else
                {
                    TxtStorageBadge.Text = Loc.T("Sürücü Sağlığı Bilinmiyor");
                    TxtStorageBadge.Foreground = (SolidColorBrush)FindResource("TextSecondary");
                }
            }
        });
    }

    /// <summary>
    /// Writes a temperature into a card, or removes the line when there is no reading. A "-- °C" placeholder in a
    /// card whose sensor never answers reads as a broken value; the model name underneath already says what the
    /// component is, so the line is dropped instead of standing there empty.
    /// </summary>
    private static void ShowTemperature(TextBlock? block, string text)
    {
        if (block == null) return;
        bool hasReading = !string.IsNullOrWhiteSpace(text) && !text.Contains("--");
        block.Text = hasReading ? text : string.Empty;
        block.Visibility = hasReading ? Visibility.Visible : Visibility.Collapsed;
    }
}
