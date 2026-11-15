using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using NitroLite.Core;

namespace NitroLite;

public partial class MainWindow
{
    internal void SaveGameSessionRecoveryState(GameSessionRecoveryState state)
    {
        try
        {
            var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
            AtomicFile.WriteAllText(GameSessionRecoveryFile, json);
        }
        catch (Exception ex)
        {
            // Without this file a crash mid-game leaves the game profile applied with nothing to undo it, so the
            // failure has to be in the log.
            AppLog.Warn("Oyun oturumu kurtarma durumu kaydedilemedi", $"{GameSessionRecoveryFile}: {ex.Message}");
        }
    }

    internal void ClearGameSessionRecoveryState()
    {
        try
        {
            if (File.Exists(GameSessionRecoveryFile))
            {
                File.Delete(GameSessionRecoveryFile);
            }
        }
        catch { }
    }

    internal GameSessionRecoveryState? LoadGameSessionRecoveryState()
    {
        try
        {
            if (File.Exists(GameSessionRecoveryFile))
            {
                var json = File.ReadAllText(GameSessionRecoveryFile);
                return JsonSerializer.Deserialize<GameSessionRecoveryState>(json);
            }
        }
        catch (Exception ex)
        {
            // A file that cannot be read means the app forgets it was in a game session; say so rather than
            // starting as if the last run ended cleanly.
            AppLog.Warn("Oyun oturumu kurtarma durumu okunamadı", $"{GameSessionRecoveryFile}: {ex.Message}");
        }
        return null;
    }

    internal async Task<bool> RestoreInterruptedGameAsync(GameSessionRecoveryState recovery)
    {
        if (recovery.RestoreMode && await _agentClient.SetOperatingModeAsync(recovery.SavedMode))
        {
            recovery.RestoreMode = false;
            UpdateModeUi(recovery.SavedMode);
        }
        if (recovery.SavedAcBoost is { } ac &&
            await _cpuPowerService.SetBoostModeAsync(ac, targetAc: true, targetDc: false))
            recovery.SavedAcBoost = null;
        if (recovery.SavedDcBoost is { } dc &&
            await _cpuPowerService.SetBoostModeAsync(dc, targetAc: false, targetDc: true))
            recovery.SavedDcBoost = null;
        if (recovery.RestoreFan && await _agentClient.SetFanModeAsync(recovery.SavedFan))
        {
            recovery.RestoreFan = false;
            _currentFanMode = recovery.SavedFan;
            UpdateFanButtonsUi(recovery.SavedFan);
        }
        if (!_appSettings.AtkMouseIntegrationEnabled) recovery.SavedMouseProfile = 0;
        if (recovery.SavedMouseProfile is >= 1 and <= 4)
        {
            int target = _appSettings.DefaultAtkMouseProfile > 0
                ? _appSettings.DefaultAtkMouseProfile : recovery.SavedMouseProfile;
            if (await SwitchMouseProfileInternalAsync(target))
            {
                UpdateSideMouseProfileStatus(target);
                recovery.SavedMouseProfile = 0;
            }
        }
        else recovery.SavedMouseProfile = 0;

        var pending = new List<string>();
        if (recovery.RestoreMode) pending.Add(Loc.T("güç modu"));
        if (recovery.SavedAcBoost.HasValue) pending.Add("priz CPU boost");
        if (recovery.SavedDcBoost.HasValue) pending.Add("pil CPU boost");
        if (recovery.RestoreFan) pending.Add("fan modu");
        if (recovery.SavedMouseProfile > 0) pending.Add("fare profili");
        if (pending.Count == 0)
        {
            _interruptedGameRecovery = null;
            ClearGameSessionRecoveryState();
            ShowToast(Loc.T("Önceki oyun oturumundan doğrulanabilir ayarlar geri yüklendi."));
        }
        else
        {
            _nextGameRecoveryAttempt = Environment.TickCount64 + 10000;
            SaveGameSessionRecoveryState(recovery);
            ShowToast(Loc.T($"Geri yükleme bekleniyor: {string.Join(", ", pending)}."), false);
        }
        UpdateTrayRetryMenuItemState();
        return pending.Count == 0;
    }

    private string? GetSelectedGameExeName()
    {
        if (CmbConfiguredGames == null) return null;
        int idx = CmbConfiguredGames.SelectedIndex;
        var snapshot = _configuredGameExeNames;
        return idx >= 0 && idx < snapshot.Count ? snapshot[idx] : null;
    }

    private GameProfile? GetSelectedGameProfile()
    {
        if (CmbConfiguredGames == null) return null;
        int idx = CmbConfiguredGames.SelectedIndex;
        var snapshot = _configuredGameProfiles;
        return idx >= 0 && idx < snapshot.Count ? snapshot[idx] : null;
    }

    private void SelectConfiguredGame(string exeName)
    {
        if (CmbConfiguredGames == null) return;
        var idx = _configuredGameExeNames.FindIndex(e => e.Equals(exeName, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0) CmbConfiguredGames.SelectedIndex = idx;
    }

    private void PopulateConfiguredGames(string? selectExeName = null)
    {
        if (CmbConfiguredGames == null) return;

        var profiles = _gameService.GetProfiles();
        var displayList = profiles.Select(p =>
        {
            var label = string.Equals(p.DisplayName, p.ExeName, StringComparison.OrdinalIgnoreCase)
                ? p.DisplayName
                : $"{p.DisplayName} ({p.ExeName})";
            // A profile whose exe is gone can never trigger; say so instead of silently doing nothing.
            return p.IsInstalled ? label : Loc.T($"{label} - kurulu değil");
        }).ToList();

        _isUpdatingGameUi = true;
        try
        {
            _configuredGameExeNames = profiles.Select(p => p.ExeName).ToList();
            _configuredGameProfiles = profiles;
            CmbConfiguredGames.ItemsSource = displayList;

            // Automatically prioritize currently running/active game if no specific selection was requested
            var targetExe = selectExeName ?? _gameService.ActiveGameProfile?.ExeName;

            if (!string.IsNullOrEmpty(targetExe))
            {
                var idx = profiles.FindIndex(p => p.ExeName.Equals(targetExe, StringComparison.OrdinalIgnoreCase));
                CmbConfiguredGames.SelectedIndex = idx >= 0 ? idx : 0;
            }
            else if (CmbConfiguredGames.Items.Count > 0)
            {
                CmbConfiguredGames.SelectedIndex = 0;
            }
        }
        finally
        {
            _isUpdatingGameUi = false;
        }

        UpdateSelectedGameDetails();
    }

    private void CmbConfiguredGames_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingGameUi) return;
        UpdateSelectedGameDetails();
    }

    private void UpdateSelectedGameDetails()
    {
        var profile = GetSelectedGameProfile();
        if (profile == null) return;

        var isCurrentlyActive = _gameService.ActiveGameProfile != null &&
            _gameService.ActiveGameProfile.ExeName.Equals(profile.ExeName, StringComparison.OrdinalIgnoreCase);

        TxtSelectedGameNameBadge.Text = isCurrentlyActive 
            ? Loc.T($" [{profile.DisplayName} - ŞU AN OYNANIYOR]") 
            : $" [{profile.DisplayName}]";
        TxtSelectedGameNameBadge.Foreground = (SolidColorBrush)FindResource(isCurrentlyActive ? "AccentGreen" : "TextSecondary");
        var pathText = string.IsNullOrEmpty(profile.InstallPath)
            ? $"{profile.ExeName}.exe"
            : (profile.InstallPath.Length > 48 ? "..." + profile.InstallPath[^45..] : profile.InstallPath);
        TxtSelectedGameExePath.Text = profile.IsInstalled ? pathText : pathText + Loc.T("  (dosya bulunamadı)");

        // 1. Target Operating Mode ComboBox
        SetComboBoxByTag(CmbGameTargetMode, profile.TargetMode.ToString());

        // 2. Target CPU Boost ComboBox
        SetComboBoxByTag(CmbGameTargetCpuBoost, profile.TargetCpuBoost.ToString());

        // 3. Target Fan Mode ComboBox
        SetComboBoxByTag(CmbGameTargetFan, profile.TargetFanMode.ToString());

        // 4. Auto-apply toggle
        if (ChkGameAutoApply != null) ChkGameAutoApply.IsChecked = profile.AutoApply;

        // 5. Target ATK Mouse Profile ComboBox
        if (CmbGameAtkMouseProfile != null)
        {
            var targetIdx = profile.TargetAtkMouseProfile;
            CmbGameAtkMouseProfile.SelectedIndex = (targetIdx >= 0 && targetIdx < CmbGameAtkMouseProfile.Items.Count) 
                ? targetIdx 
                : 0;
        }

        // 6. Per-game fan curve
        LoadGameFanCurveEditor(profile);
    }

    private async void BtnSaveGameProfile_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var stored = GetSelectedGameProfile();
            if (stored == null)
            {
                ShowToast(Loc.T("Seçili oyun profili bulunamadı; liste yenilendi."), false);
                PopulateConfiguredGames();
                return;
            }

            // Edit a copy so the shared instance held by GameDetectionService is only updated under its lock.
            var profile = new GameProfile
            {
                ExeName = stored.ExeName,
                DisplayName = stored.DisplayName,
                TargetMode = stored.TargetMode,
                TargetCpuBoost = stored.TargetCpuBoost,
                TargetFanMode = stored.TargetFanMode,
                InstallPath = stored.InstallPath,
                AutoApply = stored.AutoApply,
                TargetAtkMouseProfile = stored.TargetAtkMouseProfile,
                TargetFanCurveEnabled = stored.TargetFanCurveEnabled,
                TargetFanCurvePoints = stored.TargetFanCurvePoints
            };

            // 1. Operating Mode
            if (CmbGameTargetMode.SelectedItem is ComboBoxItem modeItem && modeItem.Tag is string modeTag)
            {
                if (Enum.TryParse<OperatingMode>(modeTag, out var m)) profile.TargetMode = m;
            }

            // 2. CPU Boost Mode
            if (CmbGameTargetCpuBoost.SelectedItem is ComboBoxItem cpuItem && cpuItem.Tag is string cpuTag)
            {
                if (Enum.TryParse<CpuBoostMode>(cpuTag, out var c)) profile.TargetCpuBoost = c;
            }

            // 3. Fan Mode
            if (CmbGameTargetFan.SelectedItem is ComboBoxItem fanItem && fanItem.Tag is string fanTag)
            {
                if (Enum.TryParse<FanMode>(fanTag, out var f)) profile.TargetFanMode = f;
            }

            // 4. Auto-apply toggle
            if (ChkGameAutoApply != null) profile.AutoApply = ChkGameAutoApply.IsChecked == true;

            // 5. ATK Mouse Profile
            if (CmbGameAtkMouseProfile != null && CmbGameAtkMouseProfile.SelectedIndex >= 0)
            {
                profile.TargetAtkMouseProfile = CmbGameAtkMouseProfile.SelectedIndex;
            }

            // 6. Per-game fan curve
            if (ChkGameFanCurve != null) profile.TargetFanCurveEnabled = ChkGameFanCurve.IsChecked == true;
            if (profile.TargetFanCurveEnabled)
            {
                var points = ReadGameFanCurveFromEditor();
                if (points != null) profile.TargetFanCurvePoints = points;
            }

            if (!_gameService.SaveProfile(profile))
            {
                ShowToast(Loc.T($"'{profile.DisplayName}' ayarları uygulandı ama diske kaydedilemedi; uygulama kapanınca kaybolabilir."), false);
            }

            // If this game is currently running right now, hot-apply settings immediately in real-time!
            bool isCurrentlyRunning = _gameService.ActiveGameProfile != null && 
                                      _gameService.ActiveGameProfile.ExeName.Equals(profile.ExeName, StringComparison.OrdinalIgnoreCase);

            if (isCurrentlyRunning)
            {
                await ApplyLiveGameProfileAsync(profile);
            }
            else
            {
                ShowToast(Loc.T($"'{profile.DisplayName}' oyun profili kaydedildi."));
            }
        }
        catch (Exception ex)
        {
            ShowToast(Loc.T($"Profil kaydedilemedi: {ex.Message}"), false);
        }
    }

    internal async Task<bool> ApplyLiveGameProfileAsync(GameProfile profile)
    {
        int saveGen = Interlocked.Increment(ref _gameSessionGeneration);
        await _gameProfileTransitionLock.WaitAsync();
        try
        {
            if (saveGen != _gameSessionGeneration || !_isGameSessionActive || _gameService.ActiveGameProfile == null ||
                !_gameService.ActiveGameProfile.ExeName.Equals(profile.ExeName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var (liveOnAc, liveBattery) = PowerStateProvider();
            bool modeOk = await SwitchModeAsync(ModePolicy.ClampForPower(profile.TargetMode, liveOnAc, liveBattery));
            if (saveGen != _gameSessionGeneration || !_isGameSessionActive) return false;

            bool boostOk = await ApplyGameBoostModeAsync(profile.TargetCpuBoost);
            if (saveGen != _gameSessionGeneration || !_isGameSessionActive) return false;

            bool fanOk = profile.TargetFanCurveEnabled
                ? await ArmGameFanCurveAsync(profile)
                : await _agentClient.SetFanModeAsync(profile.TargetFanMode);
            if (saveGen != _gameSessionGeneration || !_isGameSessionActive) return false;
            if (fanOk && !profile.TargetFanCurveEnabled)
            {
                _currentFanMode = profile.TargetFanMode;
            }

            bool mouseOk = true;
            if (_appSettings.AtkMouseIntegrationEnabled && profile.TargetAtkMouseProfile > 0)
            {
                var mouseResult = await TryApplyGameMouseProfileAsync(profile.TargetAtkMouseProfile, saveGen);
                mouseOk = mouseResult == AtkMouseResult.Confirmed;
                if (!mouseOk && mouseResult != AtkMouseResult.Rejected)
                    _pendingGameMouseProfile = (saveGen, profile.TargetAtkMouseProfile, GameMouseRetryAttempts, Environment.TickCount64 + GameMouseRetryDelayMs);
                if (mouseOk)
                {
                    _isMouseProfileSwitchedForGame = true;
                    UpdateSideMouseProfileStatus(profile.TargetAtkMouseProfile);
                }
                if (saveGen != _gameSessionGeneration || !_isGameSessionActive) return false;
            }
            else if (_isMouseProfileSwitchedForGame)
            {
                // Profile option was changed to "Don't use" (0) or disabled while mouse had been switched for the game.
                // Restore mouse to desktop/default profile immediately!
                int targetMouseProfile = _appSettings.DefaultAtkMouseProfile > 0 
                    ? _appSettings.DefaultAtkMouseProfile 
                    : _savedMouseProfileBeforeGame;

                if (targetMouseProfile > 0)
                {
                    bool restoredOk = await SwitchMouseProfileInternalAsync(targetMouseProfile);
                    if (restoredOk)
                    {
                        _isMouseProfileSwitchedForGame = false;
                        UpdateSideMouseProfileStatus(targetMouseProfile);
                    }
                    else
                    {
                        _isMouseProfileSwitchedForGame = true;
                        mouseOk = false;
                    }
                }
                if (saveGen != _gameSessionGeneration || !_isGameSessionActive) return false;
            }
            else
            {
                _isMouseProfileSwitchedForGame = false;
            }

            var boostName = ModePolicy.BoostDisplayName(profile.TargetCpuBoost);
            var mouseInfo = (_isMouseProfileSwitchedForGame && profile.TargetAtkMouseProfile > 0) ? $" / ATK Fare: Config {profile.TargetAtkMouseProfile}" : "";

            bool allCoreOk = modeOk && boostOk && fanOk && mouseOk;
            if (TxtGameStatus != null)
            {
                if (allCoreOk)
                {
                    TxtGameStatus.Text = Loc.T($"Aktif: {profile.DisplayName.ToUpperInvariant()} ({profile.TargetMode} / {boostName}{mouseInfo})");
                    if (TryFindResource("AccentGreen") is SolidColorBrush brush)
                    {
                        TxtGameStatus.Foreground = brush;
                    }
                }
                else
                {
                    TxtGameStatus.Text = Loc.T($"Kısmi Aktif: {profile.DisplayName.ToUpperInvariant()} (Bazı ayarlar uygulanamadı)");
                    if (TryFindResource("AccentRed") is SolidColorBrush brush)
                    {
                        TxtGameStatus.Foreground = brush;
                    }
                }
            }

            if (allCoreOk)
            {
                ShowToast(Loc.T($"'{profile.DisplayName}' profili kaydedildi ve uygulandı."));
            }
            else
            {
                ShowToast(Loc.T($"'{profile.DisplayName}' profili kaydedildi ancak bazı donanım ayarları uygulanamadı."), false);
            }

            return allCoreOk;
        }
        finally
        {
            _gameProfileTransitionLock.Release();
        }
    }

    private async void BtnScanGames_Click(object sender, RoutedEventArgs e)
    {
        if (BtnScanGames != null)
        {
            BtnScanGames.IsEnabled = false;
            BtnScanGames.Content = Loc.T("Taranıyor…");
        }
        try
        {
            var found = await Task.Run(() => _gameService.ScanAndAddInstalledGames());
            PopulateConfiguredGames();
            ShowToast(found > 0 ? Loc.T($"{found} yeni oyun profili eklendi.") : Loc.T("Yeni yüklü oyun tespit edilmedi."));
        }
        catch (Exception ex)
        {
            ShowToast(Loc.T($"Oyun taraması başarısız: {ex.Message}"), false);
        }
        finally
        {
            if (BtnScanGames != null)
            {
                BtnScanGames.Content = Loc.T("Yüklü Oyunları Tara");
                BtnScanGames.IsEnabled = true;
            }
        }
    }

    private void BtnAddCustomGame_Click(object sender, RoutedEventArgs e)
    {
        var steamCommon = @"C:\Program Files (x86)\Steam\steamapps\common";
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = Loc.T("Çalıştırılabilir Oyun Dosyasını Seçin"),
            Filter = Loc.T("Çalıştırılabilir Oyun Dosyaları (*.exe)|*.exe"),
            InitialDirectory = Directory.Exists(steamCommon) ? steamCommon : Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
        };

        if (dlg.ShowDialog(this) != true) return;

        var error = GameDetectionService.ValidateCustomGameExecutable(dlg.FileName);
        if (error != null)
        {
            ShowToast(error, false);
            return;
        }

        var exeName = Path.GetFileNameWithoutExtension(dlg.FileName);

        // Already configured: keep the user's settings instead of overwriting them with defaults.
        var existing = _gameService.GetProfile(exeName, dlg.FileName);
        if (existing != null)
        {
            PopulateConfiguredGames(existing.ExeName);
            ShowToast(Loc.T($"'{existing.DisplayName}' zaten listede; mevcut ayarları korundu."));
            return;
        }

        var cleanName = exeName.Replace("_", " ").Replace("-", " ");
        var profile = new GameProfile
        {
            ExeName = exeName,
            DisplayName = cleanName,
            InstallPath = dlg.FileName,
            TargetMode = OperatingMode.Turbo,
            // Same heuristic as the installed-games scan (Disabled only for light 2D games).
            TargetCpuBoost = GameDetectionService.DetermineOptimalCpuBoost(exeName),
            TargetFanMode = FanMode.Auto,
            AutoApply = true
        };

        bool saved = _gameService.SaveProfile(profile);
        PopulateConfiguredGames(exeName);
        ShowToast(saved
            ? Loc.T($"'{cleanName}' profili eklendi.")
            : Loc.T($"'{cleanName}' eklendi ama diske kaydedilemedi; uygulama kapanınca kaybolabilir."), saved);
    }

    private void BtnDeleteGame_Click(object sender, RoutedEventArgs e)
    {
        var profile = GetSelectedGameProfile();
        if (profile == null)
        {
            PopulateConfiguredGames();
            return;
        }

        var confirm = System.Windows.MessageBox.Show(this,
            Loc.T($"'{profile.DisplayName}' oyun profilini silmek istiyor musunuz?"), 
            Loc.T("Profili Sil"), MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (confirm == MessageBoxResult.Yes)
        {
            bool deleted = _gameService.DeleteProfile(profile.ExeName, profile.InstallPath);
            PopulateConfiguredGames();
            ShowToast(deleted ? Loc.T($"'{profile.DisplayName}' profili silindi.")
                : Loc.T($"'{profile.DisplayName}' profili silinemedi."), deleted);
        }
    }

    private async void OnGameStarted(GameProfile profile)
    {
        try
        {
            await HandleGameStartedAsync(profile);
        }
        catch (Exception ex)
        {
            // Trace only reaches a debugger. A game profile that failed to apply is exactly what a bug report needs
            // to show, and the user can open this log from the settings card.
            AppLog.Warn("Oyun profili uygulanamadı", ex.ToString());
        }
    }

    internal async Task HandleGameStartedAsync(GameProfile profile)
    {
        if (Dispatcher.HasShutdownStarted) return;
        int sessionGen = Interlocked.Increment(ref _gameSessionGeneration);
        try
        {
            await _gameProfileTransitionLock.WaitAsync();
            try
            {
                if (sessionGen != _gameSessionGeneration || !_gameService.AutoGameModeEnabled) return;
                CancelActiveOperatingModeReassertion();

                Func<Task> work = async () =>
                {
                    if (Dispatcher.HasShutdownStarted || sessionGen != _gameSessionGeneration || !_gameService.AutoGameModeEnabled) return;
                    try
                    {
                        if (_interruptedGameRecovery is { } interrupted && !await RestoreInterruptedGameAsync(interrupted)) return;
                        if (sessionGen != _gameSessionGeneration || !_gameService.AutoGameModeEnabled) return;

                        // Automatically sync dropdown to currently running game
                        if (CmbConfiguredGames != null)
                        {
                            PopulateConfiguredGames(profile.ExeName);
                        }

                        // Save desktop baseline reference before the first game transition.
                        // Use _hasGameBaselineCaptured (not HasPendingDesktopRestore) so that a pending AC restore
                        // failure does NOT prevent capturing the real pre-game desktop baseline.
                        if (!_hasGameBaselineCaptured)
                        {
                            if (_powerAutomationService?.IsDcEcoActive == true)
                            {
                                bool isAc = _powerAutomationService.LastAcStatus;
                                _savedModeBeforeGame = (!isAc || _powerAutomationService.PendingAcModeRestore)
                                    ? _powerAutomationService.SavedOperatingMode
                                    : _currentMode;

                                _savedCpuBoostBeforeGame = (!isAc || _powerAutomationService.PendingAcCpuRestore)
                                    ? _powerAutomationService.SavedCpuBoost
                                    : _cpuPowerService.CurrentMode;

                                _savedFanModeBeforeGame = (!isAc || _powerAutomationService.PendingAcFanRestore)
                                    ? _powerAutomationService.SavedFanMode
                                    : _currentFanMode;
                            }
                            else
                            {
                                _savedModeBeforeGame = _currentMode;
                                // CurrentMode keeps its initial value when the query failed; ask the power plan
                                // directly instead of restoring a boost value the user never had.
                                _savedCpuBoostBeforeGame = _cpuPowerService.IsModeVerified
                                    ? _cpuPowerService.CurrentMode
                                    : (await Task.Run(() => _cpuPowerService.QueryBoostModeExact(true)) ?? _cpuPowerService.CurrentMode);
                                _savedFanModeBeforeGame = _currentFanMode;
                            }
                            _savedMouseProfileBeforeGame = _appSettings.DefaultAtkMouseProfile > 0
                                ? _appSettings.DefaultAtkMouseProfile : 0;
                            var (savedAc, savedDc) = await Task.Run(() =>
                                (_cpuPowerService.QueryBoostModeExact(true), _cpuPowerService.QueryBoostModeExact(false)));
                            if (sessionGen != _gameSessionGeneration || !_gameService.AutoGameModeEnabled) return;

                            _activeGameRecovery = new GameSessionRecoveryState
                            {
                                IsActive = true,
                                SavedMode = _savedModeBeforeGame,
                                SavedBoost = _savedCpuBoostBeforeGame,
                                SavedAcBoost = savedAc,
                                SavedDcBoost = savedDc,
                                SavedFan = _savedFanModeBeforeGame,
                                SavedMouseProfile = _appSettings.AtkMouseIntegrationEnabled ? _savedMouseProfileBeforeGame : 0
                            };
                            _hasGameBaselineCaptured = true;
                        }

                        // Mark that this game session requires a desktop restore upon exit,
                        // separated from the one-time desktop baseline capture.
                        _hasPendingDesktopRestore = true;
                        _isGameSessionActive = true;
                        _batteryService.IsGameSessionActive = true;
                        UpdateTrayRetryMenuItemState();

                        if (_activeGameRecovery is { } baseline) SaveGameSessionRecoveryState(baseline);

                        var boostName = ModePolicy.BoostDisplayName(profile.TargetCpuBoost);

                        var mouseInfo = "";
                        bool mouseSwitchedThisTurn = false;
                        if (_appSettings.AtkMouseIntegrationEnabled && profile.TargetAtkMouseProfile > 0)
                        {
                            if (sessionGen != _gameSessionGeneration || !_gameService.AutoGameModeEnabled) return;

                            var mouseResult = await TryApplyGameMouseProfileAsync(profile.TargetAtkMouseProfile, sessionGen);
                            if (mouseResult == AtkMouseResult.Confirmed)
                            {
                                _isMouseProfileSwitchedForGame = true;
                                mouseSwitchedThisTurn = true;
                                mouseInfo = $" / Fare: Config {profile.TargetAtkMouseProfile}";
                                UpdateSideMouseProfileStatus(profile.TargetAtkMouseProfile);
                            }
                            else
                            {
                                if (mouseResult != AtkMouseResult.Rejected)
                                    _pendingGameMouseProfile = (sessionGen, profile.TargetAtkMouseProfile, GameMouseRetryAttempts, Environment.TickCount64 + GameMouseRetryDelayMs);
                                ShowToast(Loc.T($"Oyun fare profili: {DescribeMouseFailure(mouseResult)}."), false);
                            }

                            if (sessionGen != _gameSessionGeneration || !_gameService.AutoGameModeEnabled)
                            {
                                if (mouseSwitchedThisTurn)
                                {
                                    _isMouseProfileSwitchedForGame = true;
                                    _pendingMouseRestore = true;
                                }
                                return;
                            }
                        }

                        if (sessionGen != _gameSessionGeneration || !_gameService.AutoGameModeEnabled) return;

                        // Apply target settings with generation check at each stage under transition lock.
                        // Turbo/Performance are not allowed on battery or below 40 %: fall back to Balanced.
                        var (gameOnAc, gameBattery) = PowerStateProvider();
                        var gameMode = ModePolicy.ClampForPower(profile.TargetMode, gameOnAc, gameBattery);
                        if (gameMode != profile.TargetMode)
                        {
                            ShowToast(Loc.T($"'{profile.DisplayName}': {ModePolicy.DisplayName(profile.TargetMode)} pilde kullanılamaz, Dengeli uygulandı."), false);
                        }
                        bool modeOk = await SwitchModeAsync(gameMode);
                        if (sessionGen != _gameSessionGeneration || !_gameService.AutoGameModeEnabled) return;

                        bool boostOk = await ApplyGameBoostModeAsync(profile.TargetCpuBoost);
                        if (sessionGen != _gameSessionGeneration || !_gameService.AutoGameModeEnabled) return;

                        bool fanOk = profile.TargetFanCurveEnabled
                            ? await ArmGameFanCurveAsync(profile)
                            : await _agentClient.SetFanModeAsync(profile.TargetFanMode);
                        if (sessionGen != _gameSessionGeneration || !_gameService.AutoGameModeEnabled) return;
                        if (fanOk && !profile.TargetFanCurveEnabled)
                        {
                            _currentFanMode = profile.TargetFanMode;
                        }

                        bool allCoreOk = modeOk && boostOk && fanOk;
                        if (TxtGameStatus != null)
                        {
                            if (allCoreOk)
                            {
                                TxtGameStatus.Text = Loc.T($"Aktif: {profile.DisplayName.ToUpperInvariant()} ({profile.TargetMode} / {boostName}{mouseInfo})");
                                if (TryFindResource("AccentGreen") is SolidColorBrush brush)
                                {
                                    TxtGameStatus.Foreground = brush;
                                }
                            }
                            else
                            {
                                TxtGameStatus.Text = Loc.T($"Kısmi Aktif: {profile.DisplayName.ToUpperInvariant()} (Bazı ayarlar uygulanamadı)");
                                if (TryFindResource("AccentRed") is SolidColorBrush brush)
                                {
                                    TxtGameStatus.Foreground = brush;
                                }
                            }
                        }

                        if (!allCoreOk)
                        {
                            ShowToast(Loc.T($"'{profile.DisplayName}' profilinin bazı donanım ayarları uygulanamadı."), false, trayBalloon: false);
                        }
                    }
                    catch (Exception ex)
                    {
                        ShowToast(Loc.T($"Profil uygulanamadı: {ex.Message}"), false);
                    }
                };

                if (DispatcherRunner != null)
                {
                    await DispatcherRunner(work);
                }
                else
                {
                    var operation = Dispatcher.InvokeAsync(work);
                    await operation.Task.Unwrap();
                }
            }
            finally
            {
                _gameProfileTransitionLock.Release();
            }
        }
        catch (Exception ex)
        {
            // A game profile that fails to apply or undo leaves the machine in the wrong mode with no
            // other signal, so the reason has to reach the log.
            AppLog.Warn("Oyun profili uygulanamadı", ex.Message);
        }
    }

    private async void OnGameEnded(GameProfile profile)
    {
        try
        {
            await HandleGameEndedAsync(profile);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Oyun profili geri alınamadı", ex.ToString());
        }
    }

    internal (OperatingMode Mode, CpuBoostMode Boost, FanMode Fan, bool IsEco) GetEffectiveRestoreTargets()
    {
        bool isBatteryEco = (_powerAutomationService?.LastAcStatus == false) && (_appSettings.DcPowerMode == DcPowerProfilePreference.Eco);
        if (isBatteryEco)
        {
            return (OperatingMode.Quiet, CpuBoostMode.Disabled, FanMode.Auto, true);
        }
        return (_savedModeBeforeGame, _savedCpuBoostBeforeGame, _savedFanModeBeforeGame, false);
    }

    internal async Task HandleGameEndedAsync(GameProfile profile)
    {
        if (!_isGameSessionActive && _interruptedGameRecovery != null) return;
        if (Dispatcher.HasShutdownStarted) return;
        int endGen = Interlocked.Increment(ref _gameSessionGeneration);
        try
        {
            await _gameProfileTransitionLock.WaitAsync();
            try
            {
                if (endGen != _gameSessionGeneration) return;

                Func<Task> work = async () =>
                {
                    if (Dispatcher.HasShutdownStarted || endGen != _gameSessionGeneration) return;
                    try
                    {
                        // Immediately mark game session and battery monitoring inactive
                        _isGameSessionActive = false;
                        _batteryService.IsGameSessionActive = false;

                        if (HasPendingDesktopRestore)
                        {
                            // Pre-mark restore steps so that if interrupted at any step,
                            // unattempted steps remain pending and are not lost!
                            _pendingModeRestore = true;
                            _pendingCpuBoostRestore = true;
                            _pendingFanRestore = true;
                            if (_isMouseProfileSwitchedForGame || _pendingMouseRestore)
                            {
                                _pendingMouseRestore = true;
                            }
                            _hasPendingDesktopRestore = true;
                            UpdateTrayRetryMenuItemState();

                            var failedSteps = new List<string>();

                            var targets = GetEffectiveRestoreTargets();
                            OperatingMode targetRestoreMode = targets.Mode;
                            CpuBoostMode targetRestoreBoost = targets.Boost;
                            FanMode targetRestoreFan = targets.Fan;
                            bool isBatteryEco = targets.IsEco;

                            if (isBatteryEco && _powerAutomationService != null)
                            {
                                _powerAutomationService.SetSavedAcBaseline(_savedModeBeforeGame, _savedCpuBoostBeforeGame, _savedFanModeBeforeGame, force: false);
                                _powerAutomationService.SetDcEcoActive(true);
                            }

                            // 1. Operating mode
                            if (endGen != _gameSessionGeneration) return;
                            if (_pendingModeRestore)
                            {
                                bool modeOk = await SwitchModeAsync(targetRestoreMode);
                                if (endGen != _gameSessionGeneration) return;
                                if (modeOk)
                                {
                                    _pendingModeRestore = false;
                                }
                                else
                                {
                                    failedSteps.Add(Loc.T("Çalışma Modu"));
                                }
                            }

                            // 2. CPU Boost mode
                            if (endGen != _gameSessionGeneration) return;
                            if (_pendingCpuBoostRestore)
                            {
                                bool boostOk = await ApplyRestoreBoostAsync(targetRestoreBoost, isBatteryEco);
                                if (endGen != _gameSessionGeneration) return;
                                if (boostOk)
                                {
                                    _pendingCpuBoostRestore = false;
                                }
                                else
                                {
                                    failedSteps.Add("CPU Boost");
                                }
                            }

                            // 3. Fan mode (restore targetRestoreFan, NOT unconditionally Auto!)
                            if (endGen != _gameSessionGeneration) return;
                            if (_pendingFanRestore)
                            {
                                bool fanOk = await _agentClient.SetFanModeAsync(targetRestoreFan);
                                if (endGen != _gameSessionGeneration) return;
                                if (fanOk)
                                {
                                    _currentFanMode = targetRestoreFan;
                                    _pendingFanRestore = false;
                                }
                                else
                                {
                                    failedSteps.Add("Fan Modu");
                                }
                            }

                            // 4. Mouse profile
                            var restoredMouseInfo = "";
                            if (endGen != _gameSessionGeneration) return;
                            bool shouldRestoreMouse = _pendingMouseRestore || _isMouseProfileSwitchedForGame;
                            if (shouldRestoreMouse)
                            {
                                int targetMouseProfile = _appSettings.DefaultAtkMouseProfile > 0 
                                    ? _appSettings.DefaultAtkMouseProfile 
                                    : _savedMouseProfileBeforeGame;

                                if (targetMouseProfile > 0)
                                {
                                    if (endGen != _gameSessionGeneration) return;
                                    bool restoredOk = await SwitchMouseProfileInternalAsync(targetMouseProfile);
                                    if (endGen != _gameSessionGeneration) return;
                                    if (restoredOk)
                                    {
                                        restoredMouseInfo = $" / Fare: Config {targetMouseProfile}";
                                        UpdateSideMouseProfileStatus(targetMouseProfile);
                                        _isMouseProfileSwitchedForGame = false;
                                        _pendingMouseRestore = false;
                                    }
                                    else
                                    {
                                        _isMouseProfileSwitchedForGame = true;
                                        _pendingMouseRestore = true;
                                        failedSteps.Add("Fare Profili");
                                        ShowToast(Loc.T("Varsayılan fare profiline geri dönülemedi."), false);
                                    }
                                }
                            }

                            if (endGen != _gameSessionGeneration) return;

                            bool allSucceeded = !_pendingModeRestore && !_pendingCpuBoostRestore && !_pendingFanRestore && !_pendingMouseRestore;
                            _hasPendingDesktopRestore = !allSucceeded;
                            if (allSucceeded)
                            {
                                _hasGameBaselineCaptured = false; // Allow next game start to capture a fresh baseline
                                _activeGameRecovery = null;
                                ClearGameSessionRecoveryState();
                            }

                            if (allSucceeded)
                            {
                                if (TxtGameStatus != null)
                                {
                                    TxtGameStatus.Text = _gameService.AutoGameModeEnabled 
                                        ? Loc.T("Durum: Beklemede (Aktif oyun yok)") 
                                        : Loc.T("Durum: Devre Dışı (Manuel Kontrol)");
                                    if (TryFindResource("TextSecondary") is SolidColorBrush brush)
                                    {
                                        TxtGameStatus.Foreground = brush;
                                    }
                                }

                                string restoredSummary = isBatteryEco 
                                    ? Loc.T($"Pilde Eco profili (Quiet / Boost Devre Dışı{restoredMouseInfo})") 
                                    : Loc.T($"Varsayılan profil ({_savedModeBeforeGame}{restoredMouseInfo})");

                                // Game ended notification removed

                                ShowToast(isBatteryEco ? Loc.T("Pilde Eco profili uygulandı (Masaüstü ayarları adaptör için saklandı).") : Loc.T("Masaüstü ayarları başarıyla geri yüklendi."), trayBalloon: false);
                            }
                            else
                            {
                                if (TxtGameStatus != null)
                                {
                                    TxtGameStatus.Text = Loc.T("Durum: Masaüstü Geri Yükleme Bekliyor");
                                    if (TryFindResource("AccentCyan") is SolidColorBrush brush)
                                    {
                                        TxtGameStatus.Foreground = brush;
                                    }
                                }

                                ShowToast(Loc.T($"Geri yüklenemeyen ayarlar: {string.Join(", ", failedSteps)}"), false);
                            }
                        }
                        else if (_appSettings.AtkMouseIntegrationEnabled && _isMouseProfileSwitchedForGame)
                        {
                            if (endGen != _gameSessionGeneration) return;
                            int targetMouseProfile = _appSettings.DefaultAtkMouseProfile > 0 
                                ? _appSettings.DefaultAtkMouseProfile 
                                : _savedMouseProfileBeforeGame;
                            if (targetMouseProfile > 0)
                            {
                                bool restoredOk = await SwitchMouseProfileInternalAsync(targetMouseProfile);
                                if (endGen != _gameSessionGeneration) return;
                                if (restoredOk)
                                {
                                    UpdateSideMouseProfileStatus(targetMouseProfile);
                                    _isMouseProfileSwitchedForGame = false;
                                    _pendingMouseRestore = false;
                                }
                                else
                                {
                                    _isMouseProfileSwitchedForGame = true;
                                    _pendingMouseRestore = true;
                                    ShowToast(Loc.T("Varsayılan fare profiline geri dönülemedi."), false);
                                }
                            }
                        }
                        else
                        {
                            if (TxtGameStatus != null)
                            {
                                TxtGameStatus.Text = _gameService.AutoGameModeEnabled 
                                    ? Loc.T("Durum: Beklemede (Aktif oyun yok)") 
                                    : Loc.T("Durum: Devre Dışı (Manuel Kontrol)");
                                if (TryFindResource("TextSecondary") is SolidColorBrush brush)
                                {
                                    TxtGameStatus.Foreground = brush;
                                }
                            }
                        }

                        // A curve the game profile armed hands the fans back here, after every restore step above, so
                        // the machine ends up on the desktop curve rather than on the game's last speed.
                        if (endGen != _gameSessionGeneration) return;
                        await ReleaseGameFanCurveAsync();
                        if (endGen != _gameSessionGeneration) return;

                        UpdateTrayRetryMenuItemState();

                        // Refresh dropdown badge
                        if (endGen == _gameSessionGeneration && CmbConfiguredGames != null)
                        {
                            PopulateConfiguredGames();
                        }
                    }
                    catch (Exception ex)
                    {
                        ShowToast(Loc.T($"Profil geri yüklenemedi: {ex.Message}"), false);
                    }
                };

                if (DispatcherRunner != null)
                {
                    await DispatcherRunner(work);
                }
                else
                {
                    var operation = Dispatcher.InvokeAsync(work);
                    await operation.Task.Unwrap();
                }
            }
            finally
            {
                _gameProfileTransitionLock.Release();
            }
        }
        catch (Exception ex)
        {
            // A game profile that fails to apply or undo leaves the machine in the wrong mode with no
            // other signal, so the reason has to reach the log.
            AppLog.Warn("Oyun profili geri alınamadı", ex.Message);
        }
    }

    public async Task<bool> RetryPendingDesktopRestoreAsync()
    {
        if (!HasPendingDesktopRestore)
        {
            return true;
        }

        if (_isGameSessionActive) return false;

        await _gameProfileTransitionLock.WaitAsync();
        try
        {
            if (_isGameSessionActive || !HasPendingDesktopRestore)
            {
                return !HasPendingDesktopRestore;
            }

            int retryGen = _gameSessionGeneration;

            Func<Task<bool>> work = async () =>
            {
                if (Dispatcher.HasShutdownStarted || _isGameSessionActive || retryGen != _gameSessionGeneration)
                    return false;

                if (_interruptedGameRecovery is { } interrupted)
                    return await RestoreInterruptedGameAsync(interrupted);

                var failed = new List<string>();
                var targets = GetEffectiveRestoreTargets();

                // 1. Operating mode
                if (_pendingModeRestore)
                {
                    if (_isGameSessionActive || retryGen != _gameSessionGeneration) return false;
                    bool ok = await SwitchModeAsync(targets.Mode);
                    if (ok)
                    {
                        _pendingModeRestore = false;
                    }
                    else
                    {
                        failed.Add(Loc.T("Çalışma Modu"));
                    }
                }

                // 2. CPU Boost
                if (_pendingCpuBoostRestore)
                {
                    if (_isGameSessionActive || retryGen != _gameSessionGeneration) return false;
                    bool ok = await ApplyRestoreBoostAsync(targets.Boost, targets.IsEco);
                    if (ok)
                    {
                        _pendingCpuBoostRestore = false;
                    }
                    else
                    {
                        failed.Add("CPU Boost");
                    }
                }

                // 3. Fan
                if (_pendingFanRestore)
                {
                    if (_isGameSessionActive || retryGen != _gameSessionGeneration) return false;
                    bool ok = await _agentClient.SetFanModeAsync(targets.Fan);
                    if (ok)
                    {
                        _currentFanMode = targets.Fan;
                        _pendingFanRestore = false;
                    }
                    else
                    {
                        failed.Add("Fan Modu");
                    }
                }

                // 4. Mouse
                if (_pendingMouseRestore || _isMouseProfileSwitchedForGame)
                {
                    if (_isGameSessionActive || retryGen != _gameSessionGeneration) return false;
                    int targetMouseProfile = _appSettings.DefaultAtkMouseProfile > 0 
                        ? _appSettings.DefaultAtkMouseProfile 
                        : _savedMouseProfileBeforeGame;

                    if (targetMouseProfile > 0)
                    {
                        bool ok = await SwitchMouseProfileInternalAsync(targetMouseProfile);
                        if (ok)
                        {
                            UpdateSideMouseProfileStatus(targetMouseProfile);
                            _isMouseProfileSwitchedForGame = false;
                            _pendingMouseRestore = false;
                        }
                        else
                        {
                            _isMouseProfileSwitchedForGame = true;
                            _pendingMouseRestore = true;
                            failed.Add("Fare Profili");
                        }
                    }
                }

                // 5. Pending AC Restore
                if (_powerAutomationService?.HasPendingAcRestore == true)
                {
                    bool acOk = await _powerAutomationService.RetryPendingAcRestoreAsync(acquireLock: false);
                    if (!acOk)
                    {
                        failed.Add(Loc.T("Adaptör Güç Ayarları"));
                    }
                }

                if (_isGameSessionActive || retryGen != _gameSessionGeneration) return false;

                bool allSucceeded = !_pendingModeRestore && !_pendingCpuBoostRestore && !_pendingFanRestore && !_pendingMouseRestore && (_powerAutomationService?.HasPendingAcRestore != true);
                _hasPendingDesktopRestore = !allSucceeded;

                if (allSucceeded)
                {
                    _hasGameBaselineCaptured = false;
                    _activeGameRecovery = null;
                    ClearGameSessionRecoveryState();
                    if (TxtGameStatus != null)
                    {
                        TxtGameStatus.Text = _gameService.AutoGameModeEnabled 
                            ? Loc.T("Durum: Beklemede (Aktif oyun yok)") 
                            : Loc.T("Durum: Devre Dışı (Manuel Kontrol)");
                        if (TryFindResource("TextSecondary") is SolidColorBrush brush)
                        {
                            TxtGameStatus.Foreground = brush;
                        }
                    }
                    ShowToast(Loc.T("Bekleyen masaüstü ayarları başarıyla geri yüklendi."));
                }
                else
                {
                    if (TxtGameStatus != null)
                    {
                        TxtGameStatus.Text = Loc.T("Durum: Masaüstü Geri Yükleme Bekliyor");
                        if (TryFindResource("AccentCyan") is SolidColorBrush brush)
                        {
                            TxtGameStatus.Foreground = brush;
                        }
                    }
                    ShowToast(Loc.T($"Yeniden deneme tamamlanamadı: {string.Join(", ", failed)}"), false);
                }

                UpdateTrayRetryMenuItemState();
                return allSucceeded;
            };

            if (DispatcherRunner != null)
            {
                bool res = false;
                await DispatcherRunner(async () => { res = await work(); });
                return res;
            }
            else
            {
                var operation = Dispatcher.InvokeAsync(work);
                return await operation.Task.Unwrap();
            }
        }
        finally
        {
            _gameProfileTransitionLock.Release();
        }
    }

    private void ChkAutoGameMode_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || ChkAutoGameMode == null) return;
        Interlocked.Increment(ref _gameSessionGeneration);
        _gameService.AutoGameModeEnabled = ChkAutoGameMode.IsChecked == true;
        if (!_isInitializingUi)
        {
            _appSettings.AutoGameModeEnabled = _gameService.AutoGameModeEnabled;
            SaveSettings();
        }
        TxtGameStatus.Text = _gameService.AutoGameModeEnabled 
            ? Loc.T("Durum: Beklemede (Aktif oyun yok)") 
            : Loc.T("Durum: Devre Dışı (Manuel Kontrol)");
    }
}
