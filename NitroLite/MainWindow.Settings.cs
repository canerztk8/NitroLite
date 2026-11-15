using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NitroLite.Core;
using NitroLite.Core.Hardware;

namespace NitroLite;

public partial class MainWindow
{
    // ---------------------------------------------------------------------------------------------------------------
    // Settings card: language, theme, update checks, feedback. The update button in the title bar lives here too.
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Fills the settings card from the loaded settings and starts the update check. Called once the settings are in
    /// memory, so the language and theme lists show the values that are actually in use.
    /// </summary>
    private void SetupSettingsCard()
    {
        if (CmbLanguage == null || CmbTheme == null || ChkAutoUpdateCheck == null) return;

        _isInitializingUi = true;
        try
        {
            CmbLanguage.Items.Clear();
            foreach (var language in Loc.Languages)
                CmbLanguage.Items.Add(new ComboBoxItem { Content = language.NativeName, Tag = language.Code });
            CmbLanguage.SelectedItem = CmbLanguage.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => string.Equals(i.Tag as string, Loc.Language, StringComparison.OrdinalIgnoreCase))
                ?? CmbLanguage.Items[0];

            CmbTheme.Items.Clear();
            // Built from the catalogue, not from a hand-written list: the picker and the palettes cannot drift, and
            // the tag is the stable enum member rather than a row number.
            foreach (var theme in ThemeCatalog.All)
                CmbTheme.Items.Add(new ComboBoxItem { Content = Loc.T(ThemeCatalog.DisplayNameKey(theme)), Tag = theme });
            // Selected by the stored preference, not by the resolved palette: "System" has to stay selected even
            // though the window is currently showing one of the concrete themes.
            CmbTheme.SelectedItem = CmbTheme.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => i.Tag is AppTheme t && t == _appSettings.Theme) ?? CmbTheme.Items[0];

            ChkAutoUpdateCheck.IsChecked = _appSettings.AutoUpdateCheck;
            if (TxtSettingsVersion != null) TxtSettingsVersion.Text = UpdateService.CurrentVersionText;
        }
        finally
        {
            _isInitializingUi = false;
        }

        BuildThemeCards();
        _settingsCardReady = true;
        StartUpdateChecks();
    }

    private void CmbLanguage_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_settingsCardReady || _isInitializingUi) return;
        if (CmbLanguage?.SelectedItem is not ComboBoxItem item || item.Tag is not string code) return;
        if (string.Equals(code, Loc.Language, StringComparison.OrdinalIgnoreCase)) return;

        _appSettings.Language = code;
        SaveSettings();
        // A button still holding "✓ Tamamlandı" would come back in the old language after the restart; putting every
        // label back first means the new process starts from the catalogue, not from a stale capture.
        RestoreAllButtonFeedback();
        RestartForSettingChange(Loc.T("Dil değişikliği için NitroLite yeniden başlatılıyor…"));
    }

    private void CmbTheme_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_settingsCardReady || _isInitializingUi) return;
        if (CmbTheme?.SelectedItem is not ComboBoxItem item || item.Tag is not AppTheme theme) return;
        // Compare against the stored preference: with "System" the resolved palette can equal the new choice
        // while the preference itself still has to be saved.
        if (theme == _appSettings.Theme) return;

        // No restart: the brushes are DynamicResource, so the swap reaches the live window. The few surfaces that
        // paint themselves in code are re-run inside ApplyThemeLive.
        ApplyThemeLive(theme);
    }

    private void ChkAutoUpdateCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!_settingsCardReady || _isInitializingUi || ChkAutoUpdateCheck == null) return;
        _appSettings.AutoUpdateCheck = ChkAutoUpdateCheck.IsChecked == true;
        SaveSettings();
        if (_appSettings.AutoUpdateCheck) StartUpdateChecks();
        else StopUpdateChecks();
        ShowToast(_appSettings.AutoUpdateCheck
            ? Loc.T("Güncellemeleri otomatik denetle")
            : Loc.T("Güncelleme denetimi kapalı."));
    }

    /// <summary>
    /// Restarts NitroLite so a setting that is read once at startup takes effect. The single-instance mutex is
    /// released first: otherwise the replacement process would see a running instance and exit immediately.
    /// </summary>
    /// <param name="discardInMemorySettings">
    /// Set when the file on disk is newer than the settings this session holds (a restore). The shutdown path saves
    /// the in-memory copy, which would write the old settings straight back over the restored file.
    /// </param>
    private void RestartForSettingChange(string message, bool discardInMemorySettings = false)
    {
        if (_isGameSessionActive)
        {
            // Restarting mid-game would drop the game profile and the fan curve the session is running on.
            ShowToast(Loc.T("Ayar kaydedildi; oyun sırasında yeniden başlatılmaz, bir sonraki açılışta geçerli olur."), false);
            return;
        }

        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            ShowToast(Loc.T("NitroLite yeniden başlatılamadı; elle kapatıp açın."), false);
            return;
        }

        ShowToast(message);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        timer.Tick += (s, e) =>
        {
            timer.Stop();
            try
            {
                App.ReleaseSingleInstanceMutex();
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = exe,
                    UseShellExecute = true,
                    WorkingDirectory = AppContext.BaseDirectory
                })?.Dispose();
            }
            catch (Exception ex)
            {
                ShowToast(Loc.T($"NitroLite yeniden başlatılamadı: {ex.Message}"), false);
                return;
            }
            // Only once the replacement process is actually running: a failed start must leave this session able to
            // save its settings.
            if (discardInMemorySettings) _settingsLoaded = false;
            System.Windows.Application.Current?.Shutdown();
        };
        timer.Start();
    }

    private void StartUpdateChecks()
    {
        if (!_appSettings.AutoUpdateCheck) return;
        if (_firstUpdateTimer != null || _updateTimer != null) return;

        // Thirty seconds after start, then every six hours. A missing network is not something to report.
        _firstUpdateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _firstUpdateTimer.Tick += async (s, e) =>
        {
            _firstUpdateTimer?.Stop();
            _firstUpdateTimer = null;
            await CheckForUpdatesAsync(silent: true);
        };
        _firstUpdateTimer.Start();

        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };
        _updateTimer.Tick += async (s, e) => await CheckForUpdatesAsync(silent: true);
        _updateTimer.Start();
    }

    private void StopUpdateChecks()
    {
        _firstUpdateTimer?.Stop();
        _firstUpdateTimer = null;
        _updateTimer?.Stop();
        _updateTimer = null;
    }

    /// <summary>
    /// Asks GitHub for a newer release. Silent checks never report a failure; a manual check does.
    /// </summary>
    private async Task CheckForUpdatesAsync(bool silent)
    {
        if (_updateBusy) return;
        _updateBusy = true;
        try
        {
            if (!UpdateService.IsPackaged)
            {
                // A development build cannot replace itself; the button opens the release page instead.
                if (!silent) OpenUrl(UpdateService.ReleasePageUrl);
                return;
            }

            var info = await UpdateService.CheckAsync();
            if (info == null)
            {
                _pendingUpdate = null;
                UpdateNotice.Apply(BtnUpdateNow, BtnReleaseNotes, null);
                if (!silent) ShowToast(Loc.T("Güncel sürümü kullanıyorsunuz."));
                return;
            }

            _pendingUpdate = info;
            if (TxtUpdateNow != null) TxtUpdateNow.Text = Loc.T($"Güncelle {info.DisplayVersion}");
            UpdateNotice.Apply(BtnUpdateNow, BtnReleaseNotes, info);
            if (!silent) ShowToast(Loc.T($"Yeni sürüm var: {info.DisplayVersion}"));
        }
        catch (Exception ex)
        {
            if (!silent) ShowToast(Loc.T($"Güncelleme denetlenemedi: {ex.Message}"), false);
        }
        finally
        {
            _updateBusy = false;
        }
    }

    private async void BtnCheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (BtnCheckUpdates != null) BtnCheckUpdates.IsEnabled = false;
        try
        {
            await CheckForUpdatesAsync(silent: false);
        }
        finally
        {
            if (BtnCheckUpdates != null) BtnCheckUpdates.IsEnabled = true;
        }
    }

    /// <summary>
    /// One click: download, verify against the digest GitHub publishes, swap the executable, restart. Every failure
    /// leaves the current installation untouched.
    /// </summary>
    private async void BtnUpdateNow_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingUpdate == null || _updateBusy) return;
        if (_isGameSessionActive)
        {
            ShowToast(Loc.T("Oyun sırasında güncelleme yapılmaz."), false);
            return;
        }

        var info = _pendingUpdate;
        _updateBusy = true;
        if (BtnUpdateNow != null) BtnUpdateNow.IsEnabled = false;
        string? zip = null;
        string? staging = null;
        try
        {
            if (string.IsNullOrWhiteSpace(info.Sha256))
            {
                // Without a published digest there is nothing to verify the download against.
                ShowToast(Loc.T("Güncelleme doğrulanamadı: GitHub bu sürüm için bir özet yayınlamamış."), false);
                OpenUrl(UpdateService.ReleasePageUrl);
                return;
            }

            var progress = new Progress<double>(fraction =>
            {
                if (TxtUpdateNow != null) TxtUpdateNow.Text = Loc.T($"Güncelleme indiriliyor… %{fraction * 100:F0}");
            });
            zip = await UpdateService.DownloadAsync(info, progress);

            if (!UpdateService.VerifyDigest(zip, info.Sha256))
            {
                ShowToast(Loc.T("Güncelleme doğrulanamadı (özet uyuşmuyor)."), false);
                return;
            }

            staging = UpdateService.ExtractToStaging(zip);
            var exe = UpdateService.InstallFromStaging(staging, AppContext.BaseDirectory);

            // The release is installed, not pending, so the notice goes with it. Cleared before the restart: a slow
            // start must not leave a badge offering an update that is already on disk.
            _pendingUpdate = null;
            UpdateNotice.Apply(BtnUpdateNow, BtnReleaseNotes, null);

            ShowToast(Loc.T("Güncelleme hazır; NitroLite yeniden başlatılıyor."));
            App.ReleaseSingleInstanceMutex();
            UpdateService.StartInstalled(exe);
            System.Windows.Application.Current?.Shutdown();
        }
        catch (UnauthorizedAccessException)
        {
            ShowToast(Loc.T("Klasöre yazılamıyor; güncellemeyi elle indirin."), false);
            OpenUrl(UpdateService.ReleasePageUrl);
        }
        catch (Exception ex)
        {
            ShowToast(Loc.T($"Güncelleme kurulamadı: {ex.Message}"), false);
        }
        finally
        {
            UpdateService.CleanUp(staging, zip);
            _updateBusy = false;
            if (BtnUpdateNow != null)
            {
                BtnUpdateNow.IsEnabled = true;
                if (TxtUpdateNow != null && _pendingUpdate != null)
                    TxtUpdateNow.Text = Loc.T($"Güncelle {_pendingUpdate.DisplayVersion}");
            }
        }
    }

    /// <summary>
    /// Opens the pending release's page in the browser. The address comes out of the GitHub payload, so
    /// <see cref="UpdateService.ReleaseNotesUrl"/> checks it for an https scheme before the shell sees it; a payload
    /// with no usable page falls back to the repository's releases list.
    /// </summary>
    private void BtnReleaseNotes_Click(object sender, RoutedEventArgs e) =>
        OpenUrl(UpdateService.ReleaseNotesUrl(_pendingUpdate));

    /// <summary>
    /// Opens a prefilled GitHub issue. Only the version, the Windows build, the laptop model and the language go in;
    /// no user name, no file path, no log content.
    /// </summary>
    private void BtnReportBug_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var language = Loc.Languages.FirstOrDefault(l => l.Code == Loc.Language)?.NativeName ?? Loc.Language;
            var body = UpdateService.BuildIssueBody(
                UpdateService.CurrentVersionText,
                System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                UpdateService.GetMachineModel(),
                language);
            OpenUrl(UpdateService.BuildIssueUrl(Loc.T("NitroLite hata bildirimi"), body));
        }
        catch (Exception ex)
        {
            ShowToast(Loc.T($"Hata bildirimi açılamadı: {ex.Message}"), false);
        }
    }

    private void BtnOpenLogs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = AppPaths.DataDirectory,
                UseShellExecute = true
            })?.Dispose();
        }
        catch (Exception ex)
        {
            ShowToast(Loc.T($"Günlük klasörü açılamadı: {ex.Message}"), false);
        }
    }

    /// <summary>
    /// Writes the hardware report next to the settings file and opens the folder, because the file is useless until
    /// the user can attach it to an issue. The toast names the file so it can be found in a crowded folder.
    /// </summary>
    private void BtnHardwareReport_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // The report is the artefact a user attaches to an issue, so it carries every capability the service
            // reported, not just the keyboard the page happens to have probed.
            var capabilities = new DeviceCapabilities
            {
                Keyboard = _keyboardCapability,
                OperatingModes = _supportedModes,
                GpuModeCapability = _supportedGpuCapability,
                FanMode = _currentFanModeValue
            };
            var path = HardwareReport.WriteToDirectory(AppPaths.DataDirectory, capabilities);
            ShowToast(Loc.T($"Donanım raporu kaydedildi: {Path.GetFileName(path)}"), true);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = AppPaths.DataDirectory,
                UseShellExecute = true
            })?.Dispose();
        }
        catch (Exception ex)
        {
            ShowToast(Loc.T($"Donanım raporu yazılamadı: {ex.Message}"), false);
        }
    }

    private void LinkAuthor_Click(object sender, RoutedEventArgs e) => OpenUrl("https://github.com/canerztk8");

    /// <summary>
    /// Writes settings, game profiles and RGB presets into one file the user picks. The live settings object is used,
    /// so a change made a second ago is in the file even if it has not been flushed to disk yet.
    /// </summary>
    /// <remarks>
    /// The dialog stays on the UI thread; only the write moves to a worker, because the target can be a slow or a
    /// network drive and a frozen window during a save reads as a crash. Both backup buttons are held shut for the
    /// duration: a second click would start a second write over the same file.
    /// </remarks>
    private async void BtnCreateBackup_Click(object sender, RoutedEventArgs e)
    {
        await RunWithButtonFeedbackAsync(BtnCreateBackup, "Yedekle", CreateBackupAsync);
    }

    private async Task<bool> CreateBackupAsync()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = Loc.T("NitroLite yedeğini kaydet"),
            FileName = BackupService.SuggestedFileName(DateTime.Now),
            DefaultExt = BackupService.FileExtension,
            Filter = Loc.T("NitroLite yedeği (*.json)|*.json|Tüm dosyalar (*.*)|*.*"),
            AddExtension = true,
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != true) return false;

        // Taken on the UI thread: the window stays usable while the worker writes, and the lighting block is
        // replaced by reference on every change, so serialising the live object there could read a half-swapped one.
        var snapshot = SnapshotSettings();
        SetBackupButtonsEnabled(false);
        try
        {
            var result = await Task.Run(() => BackupService.Create(
                dialog.FileName,
                snapshot,
                GameProfilesFile,
                RgbPresetsFile,
                UpdateService.CurrentVersionText));

            if (!result.Success)
            {
                ShowToast(result.Error ?? Loc.T("Yedek yazılamadı."), false);
                return false;
            }

            ShowToast(Loc.T($"Yedek kaydedildi: {Path.GetFileName(dialog.FileName)}"));
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Yedek oluşturulamadı", ex.ToString());
            ShowToast(Loc.T($"Yedek yazılamadı: {ex.Message}"), false);
            return false;
        }
        finally
        {
            SetBackupButtonsEnabled(true);
        }
    }

    /// <summary>
    /// Puts a backup file back over the live settings and profiles, then restarts so the restored settings are the
    /// ones the app reads. Refused during a game session: the restart would drop the profile the session runs on.
    /// </summary>
    /// <remarks>
    /// The dialogs stay on the UI thread; the read of the chosen file and the three writes move to a worker, so a
    /// slow or a network drive does not freeze the window.
    /// </remarks>
    private async void BtnRestoreBackup_Click(object sender, RoutedEventArgs e)
    {
        if (_isGameSessionActive)
        {
            ShowToast(Loc.T("Oyun sırasında yedek geri yüklenmez; oyunu kapattıktan sonra deneyin."), false);
            return;
        }

        await RunWithButtonFeedbackAsync(BtnRestoreBackup, "Geri yükle", RestoreBackupAsync);
    }

    private async Task<bool> RestoreBackupAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = Loc.T("NitroLite yedeğini seç"),
            DefaultExt = BackupService.FileExtension,
            Filter = Loc.T("NitroLite yedeği (*.json)|*.json|Tüm dosyalar (*.*)|*.*"),
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return false;

        var confirm = System.Windows.MessageBox.Show(this,
            Loc.T("Bu yedekteki ayarlar, oyun profilleri ve RGB profilleri şu anki ayarlarınızın üzerine yazılacak. Devam edilsin mi?"),
            Loc.T("Yedeği geri yükle"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return false;

        SetBackupButtonsEnabled(false);
        BackupResult result;
        try
        {
            result = await Task.Run(() =>
                BackupService.Restore(dialog.FileName, AppSettingsConfigFile, GameProfilesFile, RgbPresetsFile));
        }
        catch (Exception ex)
        {
            AppLog.Warn("Yedek geri yüklenemedi", ex.ToString());
            ShowToast(Loc.T($"Yedek geri yüklenemedi: {ex.Message}"), false);
            return false;
        }
        finally
        {
            SetBackupButtonsEnabled(true);
        }

        if (!result.Success)
        {
            ShowToast(result.Error ?? Loc.T("Yedek geri yüklenemedi."), false);
            return false;
        }

        // An older build reads the legacy copy; leaving it stale would resurrect the old settings on a downgrade.
        try
        {
            if (File.Exists(AppSettingsConfigFile))
                File.Copy(AppSettingsConfigFile, LegacyConfigFile, overwrite: true);
        }
        catch (Exception ex) { AppLog.Warn("Eski ayar kopyası güncellenemedi", ex.Message); }

        // A backup written by an older build may not carry every part. Saying so is the difference between "the
        // restore worked" and "my game profiles are gone and nobody told me". The labels are catalogue keys, so
        // they are translated here: joining them raw put Turkish words inside an English sentence.
        var message = Loc.T("Yedek geri yüklendi; NitroLite yeniden başlatılıyor…");
        if (result.Skipped.Count > 0)
        {
            message += " " + Loc.T($"Yedekte olmayan bölümler atlandı: {string.Join(", ", result.Skipped.Select(label => Loc.T(label)))}");
        }
        RestartForSettingChange(message, discardInMemorySettings: true);
        return true;
    }

    /// <summary>
    /// Holds both backup buttons shut while a file operation runs, so a second click cannot start a second write
    /// over the same file. The buttons are null in the test harness, which builds no XAML.
    /// </summary>
    private void SetBackupButtonsEnabled(bool enabled)
    {
        if (BtnCreateBackup != null) BtnCreateBackup.IsEnabled = enabled;
        if (BtnRestoreBackup != null) BtnRestoreBackup.IsEnabled = enabled;
    }

    /// <summary>
    /// A copy of the live settings, taken on the UI thread before the backup moves to a worker.
    /// </summary>
    private AppSettings SnapshotSettings()
    {
        try
        {
            return JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(_appSettings)) ?? _appSettings;
        }
        catch (Exception ex)
        {
            // A snapshot that cannot be taken is not a reason to refuse the backup: the live object is what the
            // previous build wrote anyway.
            AppLog.Warn("Ayar anlık görüntüsü alınamadı", ex.Message);
            return _appSettings;
        }
    }

    private void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            })?.Dispose();
        }
        catch (Exception ex)
        {
            ShowToast(Loc.T($"Bağlantı açılamadı: {ex.Message}"), false);
        }
    }
}
