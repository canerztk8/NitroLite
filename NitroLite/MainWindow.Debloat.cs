using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using NitroLite.Core;

namespace NitroLite;

public partial class MainWindow
{
    // Service registry reads, the SCM queries and the schtasks call run off the UI thread.
    private async Task RefreshServiceListAsync()
    {
        var (statuses, nsRunning, launcherEnabled) = await Task.Run(() =>
            (_debloatService.GetServiceStatuses(), _debloatService.IsNitroSenseRunning(), _debloatService.IsNitroSenseLauncherEnabled()));

        if (ListServiceStatuses != null) ListServiceStatuses.ItemsSource = statuses;

        if (TxtNitroSenseRunningBadge != null)
        {
            TxtNitroSenseRunningBadge.Text = nsRunning ? Loc.T("Açık") : Loc.T("Kapalı");
            TxtNitroSenseRunningBadge.Foreground = nsRunning
                ? (SolidColorBrush)FindResource("AccentRed")
                : (SolidColorBrush)FindResource("TextMuted");
        }
        if (TxtNitroSenseLauncherState != null)
        {
            TxtNitroSenseLauncherState.Text = launcherEnabled switch
            {
                true => Loc.T("Windows açılışında NitroSense'i başlatan görev (NitroSenseLauncher): açık"),
                false => Loc.T("Windows açılışında NitroSense'i başlatan görev (NitroSenseLauncher): kapalı"),
                null => Loc.T("NitroSense'i açılışta başlatan görev bulunamadı")
            };
        }
        // Dimmed rather than disabled: the button template renders a disabled button as a blank light box.
        if (BtnKillNitroSense != null) BtnKillNitroSense.Opacity = nsRunning ? 1.0 : 0.45;
        UpdateServiceButtons();
    }

    private void UpdateServiceButtons()
    {
        // Dimmed, not disabled (see above); the click handlers ignore clicks while an operation runs.
        foreach (var b in new[] { BtnApplyDebloat, BtnRestoreServices })
        {
            if (b != null) b.Opacity = _serviceOperationBusy ? 0.45 : 1.0;
        }
    }

    private async void BtnKillNitroSense_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!_debloatService.IsNitroSenseRunning())
            {
                await RefreshServiceListAsync();
                ShowToast(Loc.T("NitroSense zaten kapalı."));
                return;
            }

            var confirm = System.Windows.MessageBox.Show(this,
                Loc.T("NitroSense ve yardımcı işlemleri kapatılacak. Kaydedilmemiş bir NitroSense ayarı varsa kaybolur.\n\nDevam edilsin mi?"),
                Loc.T("NitroSense'i Kapat"), MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;

            bool closed = await Task.Run(() => _debloatService.KillNitroSenseProcesses());
            await RefreshServiceListAsync();
            ShowToast(closed ? Loc.T("NitroSense kapatıldı.") : Loc.T("NitroSense kapatılamadı (yönetici olarak çalışıyor olabilir)."), closed);
        }
        catch (Exception ex)
        {
            AppLog.Warn("NitroSense kapatılamadı", ex.ToString());
            ShowToast(Loc.T($"NitroSense kapatılamadı: {ex.Message}"), false);
        }
    }

    private async void BtnApplyDebloat_Click(object sender, RoutedEventArgs e)
    {
        if (_serviceOperationBusy) return;
        try
        {
            if (await Task.Run(() => _debloatService.AreAllInstalledServicesDisabled()) && !_debloatService.HasRestorableBackup())
            {
                ShowToast(DebloatService.AlreadyDisabledMessage);
                return;
            }

            var confirm = System.Windows.MessageBox.Show(this,
                Loc.T("Listelenen Acer arka plan servisleri durdurulup devre dışı bırakılacak ve NitroSense'in açılışta başlatılması kapatılacak. " +
                "Mevcut ayarlar önce yedeklenir; \"Servisleri Geri Yükle\" ile geri dönebilirsiniz.\n\n" +
                "NitroLite'ın kullandığı Acer Agent, Care Center ve Lighting servislerine dokunulmaz. Yönetici izni istenecek.\n\nDevam edilsin mi?"),
                Loc.T("Acer Servisleri"), MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;

            _serviceOperationBusy = true;
            UpdateServiceButtons();
            var ok = await _debloatService.ApplyTier1DebloatAsync();
            _serviceOperationBusy = false;
            await RefreshServiceListAsync();
            if (ok)
            {
                ShowToast(Loc.T("Acer arka plan servisleri devre dışı bırakıldı."));
            }
            else
            {
                var msg = !string.IsNullOrEmpty(_debloatService.LastError)
                    ? Loc.T($"Servisler devre dışı bırakılamadı: {_debloatService.LastError}")
                    : Loc.T("Servisler devre dışı bırakılamadı (yönetici izni verilmemiş olabilir).");
                ShowToast(msg, false);
            }
        }
        catch (Exception ex)
        {
            ShowToast(Loc.T($"Servisler yapılandırılamadı: {ex.Message}"), false);
        }
        finally
        {
            _serviceOperationBusy = false;
            UpdateServiceButtons();
        }
    }

    private async void BtnRestoreServices_Click(object sender, RoutedEventArgs e)
    {
        if (_serviceOperationBusy) return;
        try
        {
            bool hasBackup = _debloatService.HasRestorableBackup();
            var question = hasBackup
                ? Loc.T("Servisler, devre dışı bırakılmadan önce kaydedilen haline döndürülecek. Yönetici izni istenecek.\n\nDevam edilsin mi?")
                : Loc.T("Servislerin önceki hali için kayıtlı bir yedek yok.\n\nServisler Acer'in fabrika ayarına (Otomatik başlat) döndürülüp başlatılacak. " +
                  "NitroSense'in açılışta başlatma ayarına dokunulmaz. Yönetici izni istenecek.\n\nDevam edilsin mi?");
            var confirm = System.Windows.MessageBox.Show(this, question, Loc.T("Servisleri Geri Yükle"), MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;

            _serviceOperationBusy = true;
            UpdateServiceButtons();
            var ok = hasBackup
                ? await _debloatService.RestoreTier1ServicesAsync()
                : await _debloatService.RestoreFactoryDefaultsAsync();
            _serviceOperationBusy = false;
            await RefreshServiceListAsync();
            if (ok)
            {
                ShowToast(hasBackup ? Loc.T("Önceki servis yapılandırması geri yüklendi.") : Loc.T("Servisler Acer fabrika ayarına döndürüldü."));
            }
            else
            {
                var msg = !string.IsNullOrEmpty(_debloatService.LastError)
                    ? Loc.T($"Servisler geri yüklenemedi: {_debloatService.LastError}")
                    : Loc.T("Servisler geri yüklenemedi (yönetici izni verilmemiş olabilir).");
                ShowToast(msg, false);
            }
        }
        catch (Exception ex)
        {
            ShowToast(Loc.T($"Servisler geri yüklenemedi: {ex.Message}"), false);
        }
        finally
        {
            _serviceOperationBusy = false;
            UpdateServiceButtons();
        }
    }

    private async void BtnFixLighting_Click(object sender, RoutedEventArgs e)
    {
        if (_serviceOperationBusy) return;
        var confirm = System.Windows.MessageBox.Show(this,
            Loc.T("AcerLightingService kayıt defteri yolu ve başlangıç türü sürücü deposuyla eşitlenecek, ardından klavye servisi yeniden başlatılacak. Yönetici izni istenecek.\n\nDevam edilsin mi?"),
            Loc.T("Klavye RGB Servisini Onar"), MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        SetRepairToolBusy(true, Loc.T("Klavye aydınlatma servisi onarılıyor..."));
        try
        {
            var res = await SystemToolsService.FixKeyboardLightingAsync();
            SetRepairToolStatus(res.Success, res.Message);
            ShowToast(res.Message, res.Success);
            await RefreshServiceListAsync();
        }
        catch (Exception ex)
        {
            SetRepairToolStatus(false, Loc.T($"Hata: {ex.Message}"));
            ShowToast(Loc.T($"Onarım hatası: {ex.Message}"), false);
        }
        finally
        {
            SetRepairToolBusy(false);
        }
    }

    private async void BtnFixAcerServices_Click(object sender, RoutedEventArgs e)
    {
        if (_serviceOperationBusy) return;
        var confirm = System.Windows.MessageBox.Show(this,
            Loc.T("AcerCentralService ve AcerLightingService en güncel sürücü paketine bağlanacak, çakışan eski servis süreçleri kapatılıp servisler yeniden başlatılacak. Yönetici izni istenecek.\n\nDevam edilsin mi?"),
            Loc.T("Acer & NitroSense Servislerini Onar"), MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        SetRepairToolBusy(true, Loc.T("Acer arka plan servisleri güncelleniyor ve onarılıyor..."));
        try
        {
            var res = await SystemToolsService.FixAcerServicesAsync();
            SetRepairToolStatus(res.Success, res.Message);
            ShowToast(res.Message, res.Success);
            await RefreshServiceListAsync();
        }
        catch (Exception ex)
        {
            SetRepairToolStatus(false, Loc.T($"Hata: {ex.Message}"));
            ShowToast(Loc.T($"Onarım hatası: {ex.Message}"), false);
        }
        finally
        {
            SetRepairToolBusy(false);
        }
    }

    private async void BtnRestoreMux_Click(object sender, RoutedEventArgs e)
    {
        if (_serviceOperationBusy) return;
        var confirm = System.Windows.MessageBox.Show(this,
            Loc.T("Kayıt defterinde discrete_gpu_support (1) değeri doğrulanacak, eski geçici logon görevleri temizlenecek ve Quick Access servisi başlatılacak. NitroSense açıksa kapatılacak. Yönetici izni istenecek.\n\nDevam edilsin mi?"),
            Loc.T("MUX Switch Kaydını Onar"), MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        SetRepairToolBusy(true, Loc.T("MUX switch kaydı ve servisleri düzenleniyor..."));
        try
        {
            var res = await SystemToolsService.RestoreMuxSwitchAsync();
            SetRepairToolStatus(res.Success, res.Message);
            ShowToast(res.Message, res.Success);
        }
        catch (Exception ex)
        {
            SetRepairToolStatus(false, Loc.T($"Hata: {ex.Message}"));
            ShowToast(Loc.T($"MUX onarım hatası: {ex.Message}"), false);
        }
        finally
        {
            SetRepairToolBusy(false);
        }
    }

    private async void BtnOptimizePc_Click(object sender, RoutedEventArgs e)
    {
        if (_serviceOperationBusy) return;
        var confirm = System.Windows.MessageBox.Show(this,
            Loc.T("Gereksiz müşteri deneyimi telemetri görevleri kapatılacak, HAGS (GPU donanım hızlandırmalı zamanlama) etkinleştirilecek, NVMe TRIM çalıştırılacak ve boşta kalan arka plan servisleri manuel moda alınacak. Yönetici izni istenecek.\n\nDevam edilsin mi?"),
            Loc.T("Windows & Sistem Optimizasyonu"), MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        SetRepairToolBusy(true, Loc.T("Sistem ve arka plan optimizasyonları uygulanıyor..."));
        try
        {
            var res = await SystemToolsService.OptimizePcAsync();
            SetRepairToolStatus(res.Success, res.Message);
            ShowToast(res.Message, res.Success);
        }
        catch (Exception ex)
        {
            SetRepairToolStatus(false, Loc.T($"Hata: {ex.Message}"));
            ShowToast(Loc.T($"Optimizasyon hatası: {ex.Message}"), false);
        }
        finally
        {
            SetRepairToolBusy(false);
        }
    }

    private async void BtnRestoreOptimize_Click(object sender, RoutedEventArgs e)
    {
        if (_serviceOperationBusy) return;
        var confirm = System.Windows.MessageBox.Show(this,
            Loc.T("Optimizasyonun değiştirdiği servis başlangıç türleri, HAGS ayarı ve telemetri görevleri yedekten geri yüklenecek. Yönetici izni istenecek.\n\nDevam edilsin mi?"),
            Loc.T("Optimizasyonu Geri Al"), MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        SetRepairToolBusy(true, Loc.T("Optimizasyon öncesi ayarlar geri yükleniyor..."));
        try
        {
            var res = await SystemToolsService.RestoreOptimizePcAsync();
            SetRepairToolStatus(res.Success, res.Message);
            ShowToast(res.Message, res.Success);
        }
        catch (Exception ex)
        {
            SetRepairToolStatus(false, Loc.T($"Hata: {ex.Message}"));
            ShowToast(Loc.T($"Geri alma hatası: {ex.Message}"), false);
        }
        finally
        {
            SetRepairToolBusy(false);
        }
    }

    private void SetRepairToolBusy(bool busy, string? status = null)
    {
        _serviceOperationBusy = busy;
        UpdateServiceButtons();
        if (BtnFixLighting != null) BtnFixLighting.IsEnabled = !busy;
        if (BtnFixAcerServices != null) BtnFixAcerServices.IsEnabled = !busy;
        if (BtnRestoreMux != null) BtnRestoreMux.IsEnabled = !busy;
        if (BtnOptimizePc != null) BtnOptimizePc.IsEnabled = !busy;
        if (BtnRestoreOptimize != null) BtnRestoreOptimize.IsEnabled = !busy;

        if (TxtRepairToolsStatus != null)
        {
            if (!string.IsNullOrWhiteSpace(status))
            {
                TxtRepairToolsStatus.Text = status;
                TxtRepairToolsStatus.Foreground = (System.Windows.Media.Brush)FindResource("AccentCyan");
                TxtRepairToolsStatus.Visibility = Visibility.Visible;
            }
        }
    }

    private void SetRepairToolStatus(bool success, string message)
    {
        if (TxtRepairToolsStatus != null)
        {
            TxtRepairToolsStatus.Text = (success ? "● " : "▲ ") + message;
            TxtRepairToolsStatus.Foreground = (System.Windows.Media.Brush)FindResource(success ? "AccentGreen" : "AccentRed");
            TxtRepairToolsStatus.Visibility = Visibility.Visible;
        }
    }
}
