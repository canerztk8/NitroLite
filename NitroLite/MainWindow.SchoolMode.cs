using System.Windows;
using NitroLite.Core;

namespace NitroLite;

public partial class MainWindow
{
    // ---------------------------------------------------------------------------------------------------------------
    // School mode: one switch for the most economical the machine can be on a charge.
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// School mode drives the same services the rest of the window uses, so the mode, fan and lighting pages stay in
    /// sync through the handlers that already exist. The lighting callbacks are only wired when the window has a
    /// visual tree: the test harness never calls InitializeComponent, so those controls are null there.
    /// </summary>
    private SchoolModeService CreateSchoolModeService()
    {
        var service = new SchoolModeService(_agentClient, _cpuPowerService, _displayService, _screenBrightnessService, _appSettings)
        {
            GetCurrentOperatingMode = () => _isGameSessionActive ? _savedModeBeforeGame : _currentMode,
            GetCurrentFanMode = () => _isGameSessionActive ? _savedFanModeBeforeGame : _currentFanMode,
            IsModeSupported = mode => ModePolicy.IsModeSupported(_supportedModes, mode)
        };
        if (SliderBrightness != null)
        {
            service.TurnLightingOffAsync = TurnLightingOffForSchoolModeAsync;
            service.RestoreLightingAsync = RestoreLightingAfterSchoolModeAsync;
        }
        return service;
    }

    /// <summary>
    /// While school mode is on it owns the machine: the automatic battery/AC pass would otherwise undo its quiet
    /// mode, fan and refresh rate the moment the adapter comes back.
    /// </summary>
    private void ApplySchoolModeAutomationState()
    {
        if (_powerAutomationService != null)
        {
            _powerAutomationService.AutomationSuspended = _appSettings.SchoolModeEnabled;
        }
    }

    private void UpdateSchoolModeUi()
    {
        if (ChkSchoolMode == null) return;
        _schoolModeUiSyncing = true;
        try
        {
            ChkSchoolMode.IsChecked = _appSettings.SchoolModeEnabled;
            if (TxtSchoolModeDetail != null)
            {
                TxtSchoolModeDetail.Text = Loc.T("Pilde en tasarruflu ayarlar: Eco mod, CPU boost kapalı, işlemci %60, pasif soğutma, pil tasarrufu açık, ekran 3 dk / uyku 10 dk, 60 Hz, %40 parlaklık, klavye ışığı kapalı. Kapatınca hepsi geri döner.");
            }
        }
        finally
        {
            _schoolModeUiSyncing = false;
        }
    }

    private async void ChkSchoolMode_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _isInitializingUi || _schoolModeUiSyncing || ChkSchoolMode == null || _schoolModeService == null) return;

        bool on = ChkSchoolMode.IsChecked == true;
        ChkSchoolMode.IsEnabled = false;
        try
        {
            var outcome = on ? await _schoolModeService.EnableAsync() : await _schoolModeService.DisableAsync();
            _appSettings.SchoolModeEnabled = on;
            SaveSettings();
            ApplySchoolModeAutomationState();

            if (!on && _powerAutomationService != null)
            {
                // Hand the machine back to the automatic pass, which re-applies Eco when it is on battery.
                await _powerAutomationService.EvaluatePowerStateAsync();
            }

            ShowSchoolModeOutcome(on, outcome);
        }
        catch (Exception ex)
        {
            AppLog.Error("Okul modu", ex.ToString());
            ShowToast(Loc.T($"Okul modu uygulanamadı: {ex.Message}"), false);
        }
        finally
        {
            ChkSchoolMode.IsEnabled = true;
            UpdateSchoolModeUi();
        }
    }

    private void ShowSchoolModeOutcome(bool on, SchoolModeOutcome outcome)
    {
        if (TxtSchoolModeStatus == null) return;
        TxtSchoolModeStatus.Visibility = Visibility.Visible;
        if (outcome.Success)
        {
            TxtSchoolModeStatus.Text = on
                ? Loc.T("Okul modu açık: pil tasarrufu ayarları uygulandı.")
                : Loc.T("Okul modu kapalı: önceki ayarlar geri yüklendi.");
            if (outcome.Warnings.Count > 0)
            {
                // Applied, but the undo record is missing: the user has to know before they rely on the switch.
                TxtSchoolModeStatus.Text += " " + string.Join(" ", outcome.Warnings);
                if (TryFindResource("Vermilion") is System.Windows.Media.Brush warn) TxtSchoolModeStatus.Foreground = warn;
            }
            else if (TryFindResource("AccentGreen") is System.Windows.Media.Brush ok)
            {
                TxtSchoolModeStatus.Foreground = ok;
            }
        }
        else
        {
            TxtSchoolModeStatus.Text = Loc.T($"Okul modu: uygulanamayan adımlar: {string.Join(", ", outcome.Failed)}");
            if (TryFindResource("Vermilion") is System.Windows.Media.Brush bad) TxtSchoolModeStatus.Foreground = bad;
        }
    }

    /// <summary>
    /// School mode: keyboard backlight off. True when it was on and is now off, which is what tells the service
    /// there is something to put back. A backlight that was already off returns false: claiming it was on made
    /// switching school mode off turn the keyboard on with a default colour the user never chose.
    /// </summary>
    internal async Task<bool> TurnLightingOffForSchoolModeAsync()
    {
        if (IsLightingOff(_lighting)) return false;
        _appSettings.LastActiveLighting = LightingPolicy.Clone(_lighting);
        _lighting.Brightness = 0;
        _lighting.Effect = "OFF";
        _lighting.Color = "#000000";
        _lighting.LEDs = new List<string> { "#000000", "#000000", "#000000", "#000000" };
        _appSettings.IsBacklightOff = true;
        if (!await _agentClient.TurnOffLightingAsync()) return false;
        UpdateLightingUi(_lighting);
        SaveSettings();
        return true;
    }

    /// <summary>School mode off: the keyboard comes back the way the user had it.</summary>
    private async Task RestoreLightingAfterSchoolModeAsync()
    {
        _lighting = LightingPolicy.ResumeFromOff(_appSettings.LastActiveLighting);
        _appSettings.IsBacklightOff = false;
        UpdateLightingUi(_lighting);
        await ApplyLightingToDeviceAsync(Loc.T("Klavye ışığı geri yüklenemedi"));
        SaveSettings();
    }
}
