using System;
using System.Threading.Tasks;
using System.Windows;
using NitroLite.Core;
using NitroLite.Core.Hardware;

namespace NitroLite;

/// <summary>
/// The three switches that only some firmwares have: CoolBoost, LCD overdrive and the Windows-key lock.
/// <para>
/// All three live behind <c>APGeAction</c> or <c>GetGamingProfile</c>, which a Nitro V 15 does not register at all.
/// The card therefore starts collapsed and only appears once the backend has positively answered that the feature
/// exists — the same rule the capability page follows, where only a real "no" hides anything.
/// </para>
/// </summary>
public partial class MainWindow
{
    /// <summary>Guards the three toggles against their own <c>Changed</c> handlers while they are being filled in.</summary>
    private bool _isSyncingExtraSwitches;

    /// <summary>Set once the backend has been asked, so a page switch does not re-probe on every visit.</summary>
    private bool _extraSwitchesLoaded;

    /// <summary>
    /// Asks the backend which of the three switches exist, fills them in and shows the card when at least one does.
    /// Safe to call repeatedly: the answer is cached for the session and a rescan clears it.
    /// </summary>
    internal async Task RefreshExtraSwitchesAsync(bool force = false)
    {
        if (CardExtraSwitches == null) return;
        if (_extraSwitchesLoaded && !force) return;

        // Reading the capability list is a hardware call, and on the direct-WMI backend the first hardware call is
        // what raises the elevation prompt. Opening the window must never do that, so the card stays collapsed until
        // a backend is already up — the Acer service answering, or the worker the user started from the settings page.
        if (!_hardware.IsAvailable) return;

        HardwareCapabilities caps;
        try
        {
            caps = await _hardware.GetCapabilitiesAsync();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Ek donanım anahtarları okunamadı", ex.Message);
            return;
        }

        _extraSwitchesLoaded = true;

        bool any = caps.CoolBoost || caps.LcdOverdrive || caps.WinKeyLock;
        CardExtraSwitches.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        if (!any) return;

        SetExtraSwitchVisibility(ChkCoolBoost, caps.CoolBoost);
        SetExtraSwitchVisibility(ChkLcdOverdrive, caps.LcdOverdrive);
        SetExtraSwitchVisibility(ChkWinKeyLock, caps.WinKeyLock);

        // The two that have a getter are mirrored from the device; the Windows-key lock has none, so it starts off and
        // only ever reflects what the user asked for in this session.
        if (caps.CoolBoost)
        {
            try { SetExtraSwitchSilently(ChkCoolBoost, await _hardware.GetCoolBoostAsync() == true); }
            catch (Exception ex) { AppLog.Warn("CoolBoost durumu okunamadı", ex.Message); }
        }

        if (caps.LcdOverdrive)
        {
            try { SetExtraSwitchSilently(ChkLcdOverdrive, await _hardware.GetLcdOverdriveAsync() == true); }
            catch (Exception ex) { AppLog.Warn("LCD Overdrive durumu okunamadı", ex.Message); }
        }

        if (TxtExtraSwitchesNote != null)
        {
            TxtExtraSwitchesNote.Text = caps.WinKeyLock
                ? Loc.T("Windows tuşu kilidinin durumu okunamıyor; anahtar yalnızca bu oturumda geçerli.")
                : string.Empty;
            TxtExtraSwitchesNote.Visibility = caps.WinKeyLock ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private static void SetExtraSwitchVisibility(System.Windows.Controls.CheckBox? box, bool visible)
    {
        if (box == null) return;
        box.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetExtraSwitchSilently(System.Windows.Controls.CheckBox? box, bool value)
    {
        if (box == null) return;
        _isSyncingExtraSwitches = true;
        try { box.IsChecked = value; }
        finally { _isSyncingExtraSwitches = false; }
    }

    private async void ChkCoolBoost_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _isInitializingUi || _isSyncingExtraSwitches) return;
        bool enable = ChkCoolBoost.IsChecked == true;
        bool ok;
        try
        {
            ok = await _hardware.SetCoolBoostAsync(enable);
        }
        catch (Exception ex)
        {
            AppLog.Warn("CoolBoost değiştirilemedi", ex.Message);
            ok = false;
        }

        if (!ok)
        {
            SetExtraSwitchSilently(ChkCoolBoost, !enable);
            ShowToast(Loc.T("CoolBoost değiştirilemedi: firmware komutu reddetti."), false);
        }
    }

    private async void ChkLcdOverdrive_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _isInitializingUi || _isSyncingExtraSwitches) return;
        bool enable = ChkLcdOverdrive.IsChecked == true;
        bool ok;
        try
        {
            ok = await _hardware.SetLcdOverdriveAsync(enable);
        }
        catch (Exception ex)
        {
            AppLog.Warn("LCD Overdrive değiştirilemedi", ex.Message);
            ok = false;
        }

        if (!ok)
        {
            SetExtraSwitchSilently(ChkLcdOverdrive, !enable);
            ShowToast(Loc.T("LCD Overdrive değiştirilemedi: panel bu ayarı desteklemiyor."), false);
        }
    }

    private async void ChkWinKeyLock_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _isInitializingUi || _isSyncingExtraSwitches) return;
        bool enable = ChkWinKeyLock.IsChecked == true;
        bool ok;
        try
        {
            ok = await _hardware.SetWinKeyLockAsync(enable);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Windows tuşu kilidi değiştirilemedi", ex.Message);
            ok = false;
        }

        if (!ok)
        {
            SetExtraSwitchSilently(ChkWinKeyLock, !enable);
            ShowToast(Loc.T("Windows tuşu kilidi değiştirilemedi: firmware komutu reddetti."), false);
        }
    }
}
