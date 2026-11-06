# Restore NitroSense MUX Switch setting without altering active GPU mode (Keeps NVIDIA Only)
$ErrorActionPreference = "Stop"

Write-Host "========================================================" -ForegroundColor Cyan
Write-Host "  NitroSense MUX Switch Secenegini Aktiflestirme" -ForegroundColor Cyan
Write-Host "========================================================" -ForegroundColor Cyan

$regPath = "HKLM:\SOFTWARE\OEM\AcerAgentService\AdvanceSettings"

# 1. Update discrete_gpu_support to 1 (Do NOT restart AASSvc, as hardware service resets it on startup!)
Write-Host "`n[1/3] Registry MUX destek bayragi aktif ediliyor (discrete_gpu_support = 1)..." -ForegroundColor Yellow
Set-ItemProperty -Path $regPath -Name "discrete_gpu_support" -Value 1 -Type DWord -Force
$val = (Get-ItemProperty -Path $regPath).discrete_gpu_support
Write-Host "  -> [BASARILI] discrete_gpu_support degeri: $val" -ForegroundColor Green

# 2. Kalicilik gorevi ARTIK OLUSTURULMUYOR.
#    AcerHardwareService bu degeri acilista GetGamingMiscSetting(9) kontrolu basarisiz olursa 0'a ceker.
#    Her oturum acilisinda en yuksek yetkiyle zorla 1 yazmak, firmware'in reddettigi bir durumu gizler.
#    Eski surumden kalan gorevi kaldirmak icin: Remove-Mux-Logon-Task.ps1
Write-Host "`n[2/3] Tek seferlik degisiklik; kalici gorev olusturulmadi." -ForegroundColor Yellow

# 3. Quick Access servisini baslat (MUI_undefined sorununu cozer)
Start-Service -Name "AcerQAAgentSvis" -ErrorAction SilentlyContinue

# 4. Acik NitroSense uygulamasini kapat
Write-Host "`n[3/3] Acik NitroSense arayuzu kapatiliyor..." -ForegroundColor Yellow
Stop-Process -Name "NitroSense" -Force -ErrorAction SilentlyContinue
Write-Host "  -> [BASARILI] NitroSense kapatildi." -ForegroundColor Green

Write-Host "`n========================================================" -ForegroundColor Cyan
Write-Host "  TAMAMLANDI! Ekran karti modun (NVIDIA Only) KORUNDU." -ForegroundColor Green
Write-Host "  Simdi NitroSense'i acip kontrol edebilirsin!" -ForegroundColor Cyan
Write-Host "========================================================" -ForegroundColor Cyan
