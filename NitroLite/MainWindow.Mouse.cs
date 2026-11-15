using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using NitroLite.Core;
using WpfContextMenu = System.Windows.Controls.ContextMenu;
using WpfMenuItem = System.Windows.Controls.MenuItem;

namespace NitroLite;

public partial class MainWindow
{
    private Task<AtkMouseResult> ApplyMouseProfileOnWorkerAsync(int targetProfile)
    {
        var switcher = MouseProfileSwitcher;
        return Task.Run(() => switcher == null
            ? _atkMouseService.ApplyProfile(targetProfile)
            : switcher(targetProfile) ? AtkMouseResult.Confirmed : AtkMouseResult.NoResponse);
    }

    private async Task<bool> SwitchMouseProfileInternalAsync(int targetProfile) =>
        await ApplyMouseProfileOnWorkerAsync(targetProfile) == AtkMouseResult.Confirmed;

    private async void BtnTestMouseProfile_Click(object sender, RoutedEventArgs e)
    {
        if (CmbGameAtkMouseProfile == null) return;
        var selectedIdx = CmbGameAtkMouseProfile.SelectedIndex;
        if (selectedIdx <= 0)
        {
            ShowToast(Loc.T("Test için 1-4 arasında bir ATK fare profili seçin."), false);
            return;
        }

        if (BtnTestMouseProfile != null) BtnTestMouseProfile.IsEnabled = false;
        var previousRequest = _pendingDesktopMouseRequest;
        await _gameProfileTransitionLock.WaitAsync();
        try
        {
            if (!_appSettings.AtkMouseIntegrationEnabled) return;
            var result = await ApplyMouseProfileOnWorkerAsync(selectedIdx);
            if (result == AtkMouseResult.Confirmed)
            {
                if (ReferenceEquals(_pendingDesktopMouseRequest, previousRequest))
                    _pendingDesktopMouseRequest = null;
                if (!_isGameSessionActive)
                {
                    _savedMouseProfileBeforeGame = selectedIdx;
                }
                UpdateSideMouseProfileStatus(selectedIdx);
                ShowToast(Loc.T($"ATK A9 Fare donanımı Config {selectedIdx} profiline geçirildi."));
            }
            else
            {
                ShowToast(DescribeMouseFailure(result), false);
            }
        }
        finally
        {
            _gameProfileTransitionLock.Release();
            if (BtnTestMouseProfile != null) BtnTestMouseProfile.IsEnabled = true;
        }
    }

    /// <summary>
    /// The five choices the default-profile combo offers, in the order it offers them. The menu behind the left
    /// rail's FARE figure is built from this list, so the two can never disagree about what Config 3 means.
    /// </summary>
    internal static readonly (int Profile, string Label)[] MouseProfileChoices =
    {
        (1, "Config 1 (Önerilen)"),
        (2, "Config 2"),
        (3, "Config 3"),
        (4, "Config 4"),
        (0, "Son Kullanılanı Koru")
    };

    /// <summary>Where a profile number sits in the combo. 0 ("keep the last one") is the last row.</summary>
    internal static int DefaultMouseProfileIndex(int profile) => profile switch
    {
        1 => 0,
        2 => 1,
        3 => 2,
        4 => 3,
        _ => 4
    };

    /// <summary>
    /// The compact mouse profile menu behind the left rail's FARE figure. It goes through the same apply path as the
    /// page's combo and Uygula button, so the saved setting, the badge and the hardware stay in step.
    /// </summary>
    internal WpfContextMenu BuildMouseProfileMenu()
    {
        var menu = new WpfContextMenu();
        foreach (var (profile, label) in MouseProfileChoices)
        {
            var item = new WpfMenuItem
            {
                Header = Loc.T(label),
                IsChecked = profile == _appSettings.DefaultAtkMouseProfile,
                Tag = profile
            };
            var target = profile;
            item.Click += async (_, _) =>
            {
                try { await ApplyDefaultMouseProfileAsync(target); }
                catch (Exception ex) { ShowToast(Loc.T($"Fare profili uygulanamadı: {ex.Message}"), false); }
            };
            menu.Items.Add(item);
        }
        return menu;
    }

    private void InitializeDefaultMouseProfileUi()
    {
        if (CmbDefaultAtkMouseProfile == null) return;
        int profile = _appSettings.DefaultAtkMouseProfile; // 0..4, default is 1
        // The combo is filled from the stored setting, not by the user: the handler would save the settings file
        // again and overwrite the badge this method is about to set.
        SyncDefaultMouseProfileCombo(profile);

        if (!_appSettings.AtkMouseIntegrationEnabled)
        {
            UpdateDefaultMouseProfileBadge(profile);
            return;
        }

        // Nothing is shown as active until the mouse itself confirms it.
        UpdateSideMouseProfileStatus(0);
        _ = ApplyDesktopMouseProfileAsync(profile);
    }

    /// <summary>Points the page's combo at <paramref name="profile"/> without letting its handler save again.</summary>
    private void SyncDefaultMouseProfileCombo(int profile)
    {
        if (CmbDefaultAtkMouseProfile == null) return;
        int index = DefaultMouseProfileIndex(profile);
        if (CmbDefaultAtkMouseProfile.SelectedIndex == index) return;
        bool wasSyncing = _isSyncingDefaultMouseProfileUi;
        _isSyncingDefaultMouseProfileUi = true;
        try { CmbDefaultAtkMouseProfile.SelectedIndex = index; }
        finally { _isSyncingDefaultMouseProfileUi = wasSyncing; }
    }

    /// <summary>
    /// Wait before the next attempt at a mouse profile that has not been confirmed yet. A wireless mouse only
    /// answers vendor commands while it is awake, so the first attempts sit close together: the user usually
    /// touches the mouse a second or two after the profile fails, and that is the window worth catching.
    /// </summary>
    /// <remarks>
    /// After a minute of silence the mouse is more likely switched off than asleep, so the schedule drops to a
    /// slow poll. User input still bypasses it, so switching the mouse back on applies the profile at once.
    /// </remarks>
    internal static long MouseRetryDelayMs(int attempts) => attempts switch
    {
        <= 1 => 400,
        2 => 800,
        3 => 1200,
        4 => 1600,
        5 => 2000,
        6 => 2500,
        <= 30 => 3000,
        _ => 10000
    };

    internal Task<AtkMouseResult?> ApplyDesktopMouseProfileAsync(int profile)
    {
        var request = new DesktopMouseRequest(profile);
        _pendingDesktopMouseRequest = request;
        UpdateDefaultMouseProfileBadge(profile, confirmed: false);
        return TryApplyDesktopMouseProfileAsync(request);
    }

    internal async Task RetryPendingDesktopMouseProfileIfDueAsync(long now)
    {
        if (_pendingDesktopMouseRequest is not { } request || request.InFlight || _isGameSessionActive ||
            !_appSettings.AtkMouseIntegrationEnabled) return;

        // Two reasons to try again: the backoff says it is time, or the user just touched the keyboard or
        // mouse. The second one is what makes this work — a sleeping wireless mouse only answers while it is
        // awake, so the attempt has to land inside that window instead of on a fixed clock.
        bool userWokeIt = UserActivityProvider() > request.LastInputAtAttempt &&
                          now - request.LastAttemptAt >= MouseRetryMinGapMs;
        if (now < request.NextAttemptAt && !userWokeIt) return;

        // Only one attempt for this request may wait on the transition lock at a time.
        request.NextAttemptAt = long.MaxValue;
        request.InFlight = true;
        try
        {
            await TryApplyDesktopMouseProfileAsync(request);
        }
        finally
        {
            request.InFlight = false;
        }
    }

    private async Task<AtkMouseResult?> TryApplyDesktopMouseProfileAsync(DesktopMouseRequest request)
    {
        await _gameProfileTransitionLock.WaitAsync();
        try
        {
            // A newer selection can replace this request while we wait for a game transition.
            if (!ReferenceEquals(_pendingDesktopMouseRequest, request) || !_appSettings.AtkMouseIntegrationEnabled)
                return null;

            int profile = request.Profile;
            if (_isGameSessionActive)
            {
                request.NextAttemptAt = Environment.TickCount64;
                UpdateDefaultMouseProfileBadge(profile, confirmed: false, status: "Oyun bitince uygulanacak");
                // Deferred, not queried: the row must not keep claiming a query is running.
                if (_atkMouseService.HasConfirmedProfile && _atkMouseService.CurrentActiveProfile is >= 1 and <= 4)
                    UpdateSideMouseProfileStatus(_atkMouseService.CurrentActiveProfile);
                else
                    ShowSideMouseProfileDeferred();
                return null;
            }

            int? activeProfile = null;
            AtkMouseResult result;
            if (profile > 0)
            {
                result = await ApplyMouseProfileOnWorkerAsync(profile);
                if (result == AtkMouseResult.Confirmed)
                {
                    activeProfile = profile;
                    _savedMouseProfileBeforeGame = profile;
                }
            }
            else if (MouseProfileSwitcher == null)
            {
                (activeProfile, result) = await Task.Run(() =>
                {
                    int? current = _atkMouseService.QueryCurrentProfile(out var readResult);
                    return (current, readResult);
                });
            }
            else result = AtkMouseResult.Confirmed;

            if (!ReferenceEquals(_pendingDesktopMouseRequest, request) || !_appSettings.AtkMouseIntegrationEnabled)
                return null;

            request.LastAttemptAt = Environment.TickCount64;
            request.LastInputAtAttempt = UserActivityProvider();
            if (result is AtkMouseResult.Confirmed or AtkMouseResult.Rejected)
                _pendingDesktopMouseRequest = null;
            else
            {
                request.Attempts++;
                request.NextAttemptAt = request.LastAttemptAt + MouseRetryDelayMs(request.Attempts);
            }

            UpdateDefaultMouseProfileBadge(profile,
                confirmed: result == AtkMouseResult.Confirmed,
                status: result switch
                {
                    AtkMouseResult.Confirmed => null,
                    AtkMouseResult.Rejected => "Reddedildi",
                    _ => "Uygulama bekleniyor"
                },
                tooltip: result == AtkMouseResult.Confirmed ? null : DescribeMouseFailure(result));
            UpdateSideMouseProfileStatus(activeProfile ?? 0, failure: result,
                retryPending: _pendingDesktopMouseRequest != null);
            return result;
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_pendingDesktopMouseRequest, request))
            {
                request.Attempts++;
                request.LastAttemptAt = Environment.TickCount64;
                request.LastInputAtAttempt = UserActivityProvider();
                request.NextAttemptAt = request.LastAttemptAt + MouseRetryDelayMs(request.Attempts);
            }
            App.WriteLog("ATK fare profili", ex.Message);
            return AtkMouseResult.CommunicationError;
        }
        finally
        {
            _gameProfileTransitionLock.Release();
        }
    }

    private static string DescribeMouseFailure(AtkMouseResult result) => result switch
    {
        AtkMouseResult.ReceiverNotFound => Loc.T("ATK alıcısı bulunamadı"),
        AtkMouseResult.NoResponse => Loc.T("Fare yanıt vermedi"),
        AtkMouseResult.Rejected => Loc.T("Fare profil değişikliğini reddetti"),
        AtkMouseResult.ProfileNotConfirmed => Loc.T("Fare profili doğrulanamadı"),
        _ => Loc.T("ATK alıcısıyla iletişim kurulamadı")
    };

    /// <summary>
    /// Compact variant for the sidebar status card, where the value shares a ~154 px row with its label
    /// (about 95 px for the value itself). The full text is still used in toasts and tooltips.
    /// </summary>
    /// <param name="retryPending">
    /// True while the desktop profile is still being retried. A silent wireless mouse is retried every few
    /// seconds, so "no response" read as a dead end; the retrying state says what is actually happening.
    /// </param>
    internal static string DescribeMouseFailureShort(AtkMouseResult result, bool retryPending = false) => result switch
    {
        AtkMouseResult.ReceiverNotFound => Loc.T("Alıcı yok"),
        AtkMouseResult.NoResponse => retryPending ? Loc.T("Tıklayın") : Loc.T("Yanıt yok"),
        AtkMouseResult.Rejected => Loc.T("Reddedildi"),
        AtkMouseResult.ProfileNotConfirmed => Loc.T("Doğrulanamadı"),
        _ => Loc.T("İletişim hatası")
    };

    private async Task<AtkMouseResult> TryApplyGameMouseProfileAsync(int target, int generation)
    {
        // In keep-last mode, never change the mouse unless its desktop profile is known.
        if (_savedMouseProfileBeforeGame == 0)
        {
            if (MouseProfileSwitcher != null) return AtkMouseResult.ProfileNotConfirmed;
            var (current, result) = await Task.Run(() =>
            {
                int? profile = _atkMouseService.QueryCurrentProfile(out var status);
                return (profile, status);
            });
            if (!current.HasValue) return result;
            if (generation != _gameSessionGeneration || !_appSettings.AtkMouseIntegrationEnabled)
                return AtkMouseResult.Rejected;
            _savedMouseProfileBeforeGame = current.Value;
            if (_activeGameRecovery is { } recovery)
            {
                recovery.SavedMouseProfile = current.Value;
                SaveGameSessionRecoveryState(recovery);
            }
        }
        return await ApplyMouseProfileOnWorkerAsync(target);
    }

    private async Task RetryPendingGameMouseProfileAsync(long now)
    {
        if (_pendingGameMouseProfile is not { } request || now < request.Due) return;
        _pendingGameMouseProfile = null;
        await _gameProfileTransitionLock.WaitAsync();
        try
        {
            if (!_isGameSessionActive || !_gameService.AutoGameModeEnabled || !_appSettings.AtkMouseIntegrationEnabled ||
                request.Generation != _gameSessionGeneration) return;
            var result = await TryApplyGameMouseProfileAsync(request.Profile, request.Generation);
            if (result == AtkMouseResult.Confirmed)
            {
                _isMouseProfileSwitchedForGame = true;
                UpdateSideMouseProfileStatus(request.Profile);
            }
            else if (result != AtkMouseResult.Rejected && request.Remaining > 1)
                _pendingGameMouseProfile = (request.Generation, request.Profile, request.Remaining - 1,
                    Environment.TickCount64 + GameMouseRetryDelayMs);
        }
        catch (Exception ex) { App.WriteLog("Oyun fare profili", ex.Message); }
        finally { _gameProfileTransitionLock.Release(); }
    }

    private void UpdateSideMouseProfileStatus(int profile, AtkMouseResult failure = AtkMouseResult.Confirmed,
        bool retryPending = false)
    {
        if (TxtSideMouseProfile == null) return;
        if (profile is >= 1 and <= 4) _lastShownMouseProfile = profile;

        var (text, isConfig) = DescribeSideMouseProfile(profile, failure, retryPending, _lastShownMouseProfile);
        TxtSideMouseProfile.Text = text;
        TxtSideMouseProfile.Foreground = (SolidColorBrush)FindResource(isConfig ? "AccentGreen" : "TextSecondary");
        TxtSideMouseProfile.ToolTip = !isConfig && failure == AtkMouseResult.NoResponse
            ? Loc.T("Kablosuz fare uykuya geçmiş. Fareye bir kez tıklayın; profil tıklama anında uygulanır.")
            : null;
    }

    /// <summary>
    /// What the sidebar row says for a given state. Split out so the placeholder rules can be tested without a
    /// window: a caller with no profile number must not erase one the row already showed, and "Bilinmiyor" is
    /// only honest once a query has finished without an answer.
    /// </summary>
    /// <param name="lastShownProfile">The last Config the row showed, or 0 when it never showed one.</param>
    internal static (string Text, bool IsConfig) DescribeSideMouseProfile(int profile, AtkMouseResult failure,
        bool retryPending, int lastShownProfile)
    {
        if (profile is >= 1 and <= 4) return (Loc.T($"Config {profile}"), true);
        if (failure == AtkMouseResult.Confirmed && lastShownProfile is >= 1 and <= 4)
            return (Loc.T($"Config {lastShownProfile}"), true);
        return (failure == AtkMouseResult.Confirmed
            ? Loc.T("Sorgulanıyor…")
            : DescribeMouseFailureShort(failure, retryPending), false);
    }

    /// <summary>
    /// The row's state while a desktop profile change waits for the running game to end. It used to say
    /// "Bilinmiyor", which read as a dead end even though the change was queued and would be applied.
    /// </summary>
    private void ShowSideMouseProfileDeferred()
    {
        if (TxtSideMouseProfile == null) return;
        TxtSideMouseProfile.Text = Loc.T("Oyun bitince");
        TxtSideMouseProfile.Foreground = (SolidColorBrush)FindResource("TextSecondary");
        TxtSideMouseProfile.ToolTip = Loc.T("Oyun oturumu sırasında değiştirilmez. Oyun kapanınca uygulanacak.");
    }

    private void UpdateAtkMouseIntegrationVisibility(bool enabled)
    {
        if (CardDefaultMouseProfile != null)
        {
            CardDefaultMouseProfile.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        }

        if (PnlGameAtkMouseProfile != null)
        {
            PnlGameAtkMouseProfile.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        }

        if (GridGameSettingsRow != null)
        {
            GridGameSettingsRow.Columns = enabled ? 4 : 3;
        }

        if (RowSideMouseProfile != null)
        {
            RowSideMouseProfile.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        }

        if (enabled)
        {
            UpdateSideMouseProfileStatus(_atkMouseService.HasConfirmedProfile ? _atkMouseService.CurrentActiveProfile : 0);
        }
    }

    private async void ChkAtkMouseIntegration_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _isInitializingUi || ChkAtkMouseIntegration == null) return;
        bool isEnabled = ChkAtkMouseIntegration.IsChecked == true;
        try
        {
            await SetMouseIntegrationEnabledAsync(isEnabled);
        }
        catch (Exception ex)
        {
            // The setting is already saved by the time this can throw, so the checkbox stays where the user put it;
            // what failed is the mouse profile restore behind it. Say so instead of letting the exception reach the
            // dispatcher, where it would only produce a generic error dialog.
            AppLog.Warn("ATK fare entegrasyonu değiştirilemedi", ex.ToString());
            ShowToast(Loc.T($"ATK Fare Entegrasyonu değiştirilemedi: {ex.Message}"), false);
        }
    }

    internal async Task SetMouseIntegrationEnabledAsync(bool isEnabled)
    {
        _appSettings.AtkMouseIntegrationEnabled = isEnabled;
        SaveSettings();
        UpdateAtkMouseIntegrationVisibility(isEnabled);

        if (isEnabled)
        {
            InitializeDefaultMouseProfileUi();
            ShowToast(Loc.T("ATK Fare Entegrasyonu etkinleştirildi."));
        }
        else
        {
            _pendingDesktopMouseRequest = null;
            // Acquire the lock FIRST, then check flags.
            // If a mouse command is in-flight (running under the lock), it hasn't set
            // _isMouseProfileSwitchedForGame yet — reading flags before acquiring would see false
            // and skip the restore, leaving the mouse stuck in the game profile.
            await _gameProfileTransitionLock.WaitAsync();
            try
            {
                if (_isMouseProfileSwitchedForGame || _pendingMouseRestore)
                {
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
                            ShowToast(Loc.T($"ATK Fare Entegrasyonu devre dışı bırakıldı (Fare Config {targetMouseProfile} profiline geri döndürüldü)."));
                        }
                        else
                        {
                            _pendingMouseRestore = true;
                            ShowToast(Loc.T("ATK Fare Entegrasyonu devre dışı bırakıldı (Masaüstü fare profiline geri dönülemedi)."), false);
                        }
                    }
                }
                else
                {
                    ShowToast(Loc.T("ATK Fare Entegrasyonu devre dışı bırakıldı."));
                }
            }
            finally
            {
                _gameProfileTransitionLock.Release();
            }
        }
    }

    /// <summary>
    /// Shows the default-profile badge only when it has something to say: a pending apply, a rejection, or the
    /// reason a write failed. The combo box already shows which profile is chosen, so a permanent
    /// "Config 1 (Aktif)" beside the title was a second copy of the same fact.
    /// </summary>
    private void UpdateDefaultMouseProfileBadge(int profile, bool confirmed = true, string? status = null, string? tooltip = null)
    {
        if (TxtDefaultMouseProfileStatus == null || BadgeDefaultMouseProfile == null) return;

        // Confirmed with no explicit status is the steady state: nothing to report.
        if (confirmed && status == null)
        {
            BadgeDefaultMouseProfile.Visibility = Visibility.Collapsed;
            TxtDefaultMouseProfileStatus.ToolTip = null;
            return;
        }

        TxtDefaultMouseProfileStatus.ToolTip = tooltip;
        TxtDefaultMouseProfileStatus.Text = status ?? Loc.T("Uygulanıyor…");
        TxtDefaultMouseProfileStatus.Foreground = (SolidColorBrush)FindResource(confirmed ? "AccentGreen" : "TextSecondary");
        BadgeDefaultMouseProfile.Visibility = Visibility.Visible;
    }

    private void CmbDefaultAtkMouseProfile_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializingUi || _isSyncingDefaultMouseProfileUi || CmbDefaultAtkMouseProfile == null) return;
        if (CmbDefaultAtkMouseProfile.SelectedItem is ComboBoxItem item && item.Tag is string tagStr && int.TryParse(tagStr, out int val))
        {
            if (val != _appSettings.DefaultAtkMouseProfile) _pendingDesktopMouseRequest = null;
            _appSettings.DefaultAtkMouseProfile = val;
            SaveSettings();
            if (val >= 1 && val <= 4)
            {
                // Saved as the default, but not sent to the mouse until "Uygula" (or the next game exit).
                UpdateDefaultMouseProfileBadge(val, confirmed: false, status: Loc.T("Uygula'ya basın"));
            }
            else
            {
                UpdateDefaultMouseProfileBadge(val);
            }
        }
    }

    private async void BtnApplyDefaultMouseProfile_Click(object sender, RoutedEventArgs e)
    {
        if (CmbDefaultAtkMouseProfile == null) return;
        await RunWithButtonFeedbackAsync(BtnApplyDefaultMouseProfile, "Uygula", ApplyDefaultMouseProfileAsync);
    }

    private async Task<bool> ApplyDefaultMouseProfileAsync()
    {
        int val = 1;
        if (CmbDefaultAtkMouseProfile?.SelectedItem is ComboBoxItem item && item.Tag is string tagStr && int.TryParse(tagStr, out int parsed))
        {
            val = parsed;
        }
        return await ApplyDefaultMouseProfileAsync(val);
    }

    /// <summary>
    /// Saves <paramref name="val"/> as the default profile and, for a real config, sends it to the mouse. Shared by
    /// the page's Uygula button and the left rail's FARE menu so both report the same outcome.
    /// </summary>
    internal async Task<bool> ApplyDefaultMouseProfileAsync(int val)
    {
        // The menu is a second way to make the same choice, so the page has to show it too.
        SyncDefaultMouseProfileCombo(val);

        _appSettings.DefaultAtkMouseProfile = val;
        bool saved = SaveSettings();
        string saveStatus = saved ? $"Config {val} kaydedildi" : $"Config {val} diske kaydedilemedi";

        if (val >= 1 && val <= 4)
        {
            var result = await ApplyDesktopMouseProfileAsync(val);
            if (result == AtkMouseResult.Confirmed)
            {
                ShowToast(Loc.T($"{saveStatus}. Fareye uygulandı."), saved);
                return saved;
            }
            if (result is AtkMouseResult.Rejected)
            {
                ShowToast($"{saveStatus}. {DescribeMouseFailure(result.Value)}.", false);
                return false;
            }
            if (result.HasValue)
            {
                ShowToast(Loc.T($"{saveStatus}. {DescribeMouseFailure(result.Value)}. Tekrar denenecek."), false);
                return false;
            }
            if (_pendingDesktopMouseRequest?.Profile == val && _isGameSessionActive)
            {
                // Queued, not applied: the session owns the mouse until it ends.
                ShowToast(Loc.T($"{saveStatus}. Oyun bitince uygulanacak."), saved);
                return false;
            }
            return false;
        }

        _pendingDesktopMouseRequest = null;
        UpdateDefaultMouseProfileBadge(val);
        if (saved) ShowToast(Loc.T("Varsayılan fare profili otomatik koruma (pasif) olarak kaydedildi."));
        return saved;
    }
}
