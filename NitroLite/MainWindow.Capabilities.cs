using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NitroLite.Core;
using NitroLite.Core.Hardware;

// System.Drawing comes in through the WinForms tray icon, so the media brush has to be named explicitly.
using Brush = System.Windows.Media.Brush;

namespace NitroLite;

/// <summary>
/// What the capability probe is allowed to change in the interface, and the first-run walkthrough that explains it.
/// <para>
/// The rule throughout is that only a positive "no" hides anything. <see cref="HardwareProbeOutcome.AccessDenied"/>
/// and <see cref="HardwareProbeOutcome.Unknown"/> mean the probe never got to ask — the unelevated
/// <c>AcerGamingFunction</c> call on an AN16-41 answers exactly that — and hiding a working page because the probe was
/// refused would be a worse bug than showing a page that turns out to be empty.
/// </para>
/// </summary>
public partial class MainWindow
{
    /// <summary>Number of steps in the first-run walkthrough.</summary>
    internal const int OnboardingStepCount = 3;

    /// <summary>Which step of the walkthrough is on screen. Zero-based.</summary>
    private int _onboardingStep;

    /// <summary>
    /// Applies the capability snapshot to the navigation rail and the two cards that can be genuinely absent.
    /// Safe to call before the probe has run: a null snapshot leaves everything visible.
    /// </summary>
    internal void ApplyCapabilityVisibility()
    {
        var snapshot = _capabilitySnapshot;
        if (snapshot == null) return;

        // Keyboard lighting: the whole page goes when the machine says there is no addressable keyboard. The page is
        // useless without it, and a rail entry that opens an empty page is worse than no entry.
        bool hideLighting = snapshot.IsUnsupported(CapabilitySnapshot.KeyboardZones);
        SetNavVisible(NavRgb, TabRgb, !hideLighting);

        // MUX switch: one row inside the modes page, so only the row and its rule go.
        bool hideMux = snapshot.IsUnsupported(CapabilitySnapshot.MuxSwitch);
        var muxVisibility = hideMux ? Visibility.Collapsed : Visibility.Visible;
        if (RowMuxSwitch != null) RowMuxSwitch.Visibility = muxVisibility;
        if (SepMuxSwitch != null) SepMuxSwitch.Visibility = muxVisibility;

        EnsureVisiblePageSelected();
    }

    /// <summary>Hides a rail entry and its page together, so the two can never disagree.</summary>
    private static void SetNavVisible(System.Windows.Controls.RadioButton? nav, FrameworkElement? page, bool visible)
    {
        if (nav != null) nav.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible && page != null) page.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// If the page that was selected is now hidden, selects the first one that is still there. Without this the window
    /// would show an empty content area with no rail entry highlighted, which reads as a crash.
    /// </summary>
    private void EnsureVisiblePageSelected()
    {
        var navs = new[] { NavModes, NavMonitoring, NavRgb, NavBattery, NavDebloat };
        bool selectedIsVisible = navs.Any(n => n != null && n.IsChecked == true && n.Visibility == Visibility.Visible);
        if (selectedIsVisible) return;

        var first = navs.FirstOrDefault(n => n != null && n.Visibility == Visibility.Visible);
        if (first != null) first.IsChecked = true;
    }

    // ---------------------------------------------------------------- first run

    /// <summary>
    /// Shows the walkthrough on a first run. Called once the window is loaded and the capability snapshot is in, so
    /// step one can list what the machine actually has instead of a generic paragraph.
    /// </summary>
    internal void MaybeShowOnboarding()
    {
        if (OnboardingOverlay == null) return;
        if (_appSettings.OnboardingCompleted) return;

        _onboardingStep = 0;
        RenderOnboardingStep();
        OnboardingOverlay.Visibility = Visibility.Visible;
    }

    /// <summary>Paints the current step: which panel is open, what the buttons say, and the capability list.</summary>
    internal void RenderOnboardingStep()
    {
        if (OnboardingOverlay == null) return;

        _onboardingStep = Math.Clamp(_onboardingStep, 0, OnboardingStepCount - 1);

        if (PanelOnboardingHardware != null)
            PanelOnboardingHardware.Visibility = _onboardingStep == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (PanelOnboardingProfile != null)
            PanelOnboardingProfile.Visibility = _onboardingStep == 1 ? Visibility.Visible : Visibility.Collapsed;
        if (PanelOnboardingStartup != null)
            PanelOnboardingStartup.Visibility = _onboardingStep == 2 ? Visibility.Visible : Visibility.Collapsed;

        if (TxtOnboardingStep != null)
            TxtOnboardingStep.Text = $"{_onboardingStep + 1} / {OnboardingStepCount}";

        if (TxtOnboardingTitle != null)
        {
            TxtOnboardingTitle.Text = _onboardingStep switch
            {
                0 => Loc.T("Donanımınız"),
                1 => Loc.T("Önerilen başlangıç"),
                _ => Loc.T("Otomatik başlatma")
            };
        }

        if (BtnOnboardingBack != null)
            BtnOnboardingBack.Visibility = _onboardingStep == 0 ? Visibility.Collapsed : Visibility.Visible;

        if (BtnOnboardingNext != null)
        {
            BtnOnboardingNext.Content = _onboardingStep switch
            {
                0 => Loc.T("İleri"),
                1 => Loc.T("Önerileni uygula"),
                _ => Loc.T("Bitir")
            };
        }

        if (_onboardingStep == 0) FillOnboardingCapabilities();
        if (_onboardingStep == 1) FillOnboardingProfileNote();
        if (_onboardingStep == 2) FillOnboardingStartupNote();
    }

    /// <summary>
    /// One line per capability the probe has an opinion about. An inconclusive answer is listed as "bilinmiyor"
    /// rather than as a missing feature, because that is what it is.
    /// </summary>
    private void FillOnboardingCapabilities()
    {
        if (PanelOnboardingCapabilities == null) return;
        PanelOnboardingCapabilities.Children.Clear();

        var snapshot = _capabilitySnapshot;
        if (snapshot == null || snapshot.Capabilities.Count == 0)
        {
            PanelOnboardingCapabilities.Children.Add(new TextBlock
            {
                Text = Loc.T("Donanım taraması henüz tamamlanmadı. Ayarlar sayfasından yeniden tarayabilirsiniz."),
                Foreground = TryFindResource("InkFaint") as Brush,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap
            });
            return;
        }

        foreach (var capability in snapshot.Capabilities)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var glyph = new TextBlock
            {
                Text = capability.Outcome switch
                {
                    HardwareProbeOutcome.Supported => "✓",
                    HardwareProbeOutcome.Unsupported => "✕",
                    _ => "?"
                },
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Top,
                Foreground = TryFindResource(capability.Outcome switch
                {
                    HardwareProbeOutcome.Supported => "AccentGreen",
                    HardwareProbeOutcome.Unsupported => "AccentRed",
                    _ => "InkFaint"
                }) as Brush
            };
            Grid.SetColumn(glyph, 0);

            var label = new TextBlock
            {
                Text = $"{Loc.T(CapabilityLabel(capability.Capability))} — {Loc.T(VerdictLabel(capability.Outcome))}",
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = TryFindResource("TextSecondary") as Brush
            };
            Grid.SetColumn(label, 1);

            row.Children.Add(glyph);
            row.Children.Add(label);
            PanelOnboardingCapabilities.Children.Add(row);
        }
    }

    /// <summary>Turkish name for a capability key, falling back to the key itself so a new one is never blank.</summary>
    internal static string CapabilityLabel(string capability) => capability switch
    {
        CapabilitySnapshot.WmiGamingFunction => "Acer WMI arayüzü",
        CapabilitySnapshot.WmiBatteryChargeLimit => "Pil şarj sınırı",
        CapabilitySnapshot.MuxSwitch => "MUX Switch",
        CapabilitySnapshot.KeyboardZones => "Klavye aydınlatma bölgeleri",
        CapabilitySnapshot.GpuOverclock => "GPU hız aşırtma",
        CapabilitySnapshot.PanelOverdrive => "Panel overdrive",
        CapabilitySnapshot.ProfileMatch => "Model profili",
        _ => capability
    };

    /// <summary>Turkish verdict for a probe outcome.</summary>
    internal static string VerdictLabel(HardwareProbeOutcome outcome) => outcome switch
    {
        HardwareProbeOutcome.Supported => "destekleniyor",
        HardwareProbeOutcome.Unsupported => "desteklenmiyor",
        HardwareProbeOutcome.AccessDenied => "yetki gerekiyor",
        HardwareProbeOutcome.TimedOut => "yanıt vermedi",
        HardwareProbeOutcome.Failed => "okunamadı",
        _ => "bilinmiyor"
    };

    private void FillOnboardingProfileNote()
    {
        if (TxtOnboardingProfileNote == null) return;
        bool limitSupported = _capabilitySnapshot?.IsUnsupported(CapabilitySnapshot.WmiBatteryChargeLimit) != true;
        TxtOnboardingProfileNote.Text = limitSupported
            ? Loc.T("Pil koruma sınırı bu cihazda kullanılabilir.")
            : Loc.T("Bu cihaz pil şarj sınırını bildirmiyor; o adım atlanacak.");
    }

    private void FillOnboardingStartupNote()
    {
        if (TxtOnboardingStartupNote == null) return;
        TxtOnboardingStartupNote.Text = Loc.T("Bu seçenek varsayılan olarak kapalıdır ve yalnızca açtığınızda bir kez yönetici onayı ister.");
        _ = SyncOnboardingStartupAsync();
    }

    private bool _isSyncingOnboardingStartup;

    /// <summary>
    /// Reads the real state of the scheduled task rather than a saved flag: a task deleted by hand must show as off,
    /// and the settings page owns the same task, so the two switches can never disagree.
    /// </summary>
    private async Task SyncOnboardingStartupAsync()
    {
        if (ChkOnboardingElevated == null) return;
        try
        {
            bool enabled = await _elevatedStartup.IsEnabledAsync();
            _isSyncingOnboardingStartup = true;
            try { ChkOnboardingElevated.IsChecked = enabled; }
            finally { _isSyncingOnboardingStartup = false; }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Görev Zamanlayıcı durumu okunamadı", ex.Message);
        }
    }

    /// <summary>
    /// The walkthrough's own copy of the elevated-autostart switch. It drives the same service as the settings page and
    /// refreshes that page's toggle afterwards, so turning it on here is not undone by the next settings refresh.
    /// </summary>
    private async void ChkOnboardingElevated_Changed(object sender, RoutedEventArgs e)
    {
        if (ChkOnboardingElevated == null || _isSyncingOnboardingStartup) return;
        bool enable = ChkOnboardingElevated.IsChecked == true;

        // A task pointing into a temp folder would start nothing once Windows cleans it up.
        if (enable && DebloatService.IsTemporaryLocation(Environment.ProcessPath))
        {
            SetOnboardingStartupToggle(false);
            if (TxtOnboardingStartupNote != null)
                TxtOnboardingStartupNote.Text = Loc.T("NitroLite geçici bir klasörden çalışıyor. Önce zip'i kalıcı bir klasöre çıkarın, sonra otomatik başlatmayı açın.");
            return;
        }

        ChkOnboardingElevated.IsEnabled = false;
        try
        {
            var result = enable
                ? await _elevatedStartup.EnableAsync(_appSettings.StartMinimized)
                : await _elevatedStartup.DisableAsync();

            if (result.Outcome != ElevatedStartupOutcome.Applied)
            {
                SetOnboardingStartupToggle(!enable);
                if (TxtOnboardingStartupNote != null)
                {
                    TxtOnboardingStartupNote.Text = result.Outcome == ElevatedStartupOutcome.Cancelled
                        ? Loc.T("Yönetici izni verilmedi; yönetici yetkisiyle otomatik başlatma değiştirilmedi")
                        : Loc.T("Görev Zamanlayıcı ayarı değiştirilemedi");
                }
            }
            else if (TxtOnboardingStartupNote != null)
            {
                TxtOnboardingStartupNote.Text = enable
                    ? Loc.T("Yönetici yetkisiyle otomatik başlatma etkin")
                    : Loc.T("Yönetici yetkisiyle otomatik başlatma kapatıldı");
            }

            // The settings page shows the same task; keep it in step.
            await RefreshElevatedAutoStartAsync();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Yönetici yetkisiyle otomatik başlatma değiştirilemedi", ex.ToString());
            SetOnboardingStartupToggle(!enable);
            if (TxtOnboardingStartupNote != null)
                TxtOnboardingStartupNote.Text = Loc.T("Görev Zamanlayıcı ayarı değiştirilemedi");
        }
        finally
        {
            ChkOnboardingElevated.IsEnabled = true;
        }
    }

    private void SetOnboardingStartupToggle(bool value)
    {
        if (ChkOnboardingElevated == null) return;
        _isSyncingOnboardingStartup = true;
        try { ChkOnboardingElevated.IsChecked = value; }
        finally { _isSyncingOnboardingStartup = false; }
    }

    private void BtnOnboardingNext_Click(object sender, RoutedEventArgs e)
    {
        if (_onboardingStep >= OnboardingStepCount - 1)
        {
            FinishOnboarding();
            return;
        }

        // Leaving step two is the moment the recommendation is applied: the button says so, and applying it on the
        // way out means the user sees the result on the page behind the overlay.
        if (_onboardingStep == 1) _ = ApplyRecommendedProfileAsync();

        _onboardingStep++;
        RenderOnboardingStep();
    }

    private void BtnOnboardingBack_Click(object sender, RoutedEventArgs e)
    {
        if (_onboardingStep == 0) return;
        _onboardingStep--;
        RenderOnboardingStep();
    }

    private void BtnOnboardingSkip_Click(object sender, RoutedEventArgs e) => FinishOnboarding();

    /// <summary>
    /// Closes the walkthrough and records that it is done. Written even on skip: a user who skipped has answered the
    /// question, and showing it again on the next launch would be nagging.
    /// </summary>
    internal void FinishOnboarding()
    {
        if (OnboardingOverlay != null) OnboardingOverlay.Visibility = Visibility.Collapsed;
        _appSettings.OnboardingCompleted = true;
        SaveSettings();
    }

    /// <summary>
    /// The recommended starting point: Balanced mode, automatic fans, and the 80 % charge limit when the machine has
    /// one. Deliberately conservative — nothing here is a performance setting, and every step is skipped rather than
    /// forced when the hardware does not answer.
    /// </summary>
    internal async Task ApplyRecommendedProfileAsync()
    {
        try
        {
            await SetManualModeAsync(OperatingMode.Default);

            if (_hardware != null)
            {
                try { await _hardware.SetFanModeAsync(FanMode.Auto); }
                catch (Exception ex) { AppLog.Warn("Önerilen profil: fan modu uygulanamadı", ex.Message); }
            }

            if (_capabilitySnapshot?.IsUnsupported(CapabilitySnapshot.WmiBatteryChargeLimit) != true)
            {
                _appSettings.BatteryWatchdogEnabled = true;
                if (ChkWatchdog != null) ChkWatchdog.IsChecked = true;
                SaveSettings();
            }

            ShowToast(Loc.T("Önerilen başlangıç ayarları uygulandı."));
        }
        catch (Exception ex)
        {
            AppLog.Warn("Önerilen profil uygulanamadı", ex.ToString());
            ShowToast(Loc.T($"Önerilen ayarlar uygulanamadı: {ex.Message}"), false);
        }
    }
}
