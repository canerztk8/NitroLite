# Elevate and fix NitroSense backend service mismatch
$ErrorActionPreference = "Stop"

Write-Host "========================================================" -ForegroundColor Cyan
Write-Host "  Acer Agent Service 2025 Surumune Guncelleme ve Onarma" -ForegroundColor Cyan
Write-Host "========================================================" -ForegroundColor Cyan

$newDir = "C:\Windows\System32\DriverStore\FileRepository\predatorservice.inf_amd64_c634eb8e856fb962"
$centralExe = "$newDir\AcerCentralService.exe"
$lightingExe = "$newDir\AcerLightingService.exe"
if (!(Test-Path -LiteralPath $centralExe -PathType Leaf) -or !(Test-Path -LiteralPath $lightingExe -PathType Leaf)) { throw "Expected Acer service binaries are missing; no changes applied." }

# 1. Stop existing services and processes
Write-Host "`n[1/5] Eski servisler ve NitroSense durduruluyor..." -ForegroundColor Yellow
Stop-Service -Name "AASSvc" -Force -ErrorAction SilentlyContinue
Stop-Service -Name "AcerLightingService" -Force -ErrorAction SilentlyContinue
Stop-Process -Name "AcerAgentService", "AcerHardwareService", "AcerCentralService", "NitroSense" -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 1

# 2. Update ImagePath in Registry to the 2025 driver version
Write-Host "`n[2/5] Servis dosya yollari ve baslangic turleri 2025 surumune guncelleniyor..." -ForegroundColor Yellow
Set-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\AASSvc" -Name "ImagePath" -Value $centralExe
Set-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\AASSvc" -Name "Start" -Value 2 -Force
Set-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\AcerLightingService" -Name "ImagePath" -Value $lightingExe
Set-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\AcerLightingService" -Name "Start" -Value 2 -Force
Set-Service -Name "AASSvc" -StartupType Automatic -ErrorAction SilentlyContinue
Set-Service -Name "AcerLightingService" -StartupType Automatic -ErrorAction SilentlyContinue
Write-Host "  -> [BASARILI] AASSvc ve AcerLightingService ImagePath ve Start (Automatic) guncellendi." -ForegroundColor Green

# 3. MUX capability flags are NOT written any more.
#    dgpu_mode_capability is reported by the hardware service itself; forcing it (and discrete_gpu_support)
#    makes NitroSense/NitroLite offer GPU modes the firmware may reject (black screen risk).
#    If you deliberately want the MUX option back, run Restore-Mux-Setting.ps1 once.
Write-Host "`n[3/5] MUX yetenek degerlerine dokunulmuyor (donanim servisi belirler)." -ForegroundColor Yellow

# 4. Start updated 2025 services
Write-Host "`n[4/5] Guncel 2025 Acer servisleri baslatiliyor..." -ForegroundColor Yellow
Start-Service -Name "AASSvc"
Start-Service -Name "AcerLightingService" -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2
Write-Host "  -> [BASARILI] Guncel servisler calisiyor." -ForegroundColor Green

# 5. No persistent logon task is created any more (it ran with highest privileges on every logon).
#    Remove a task left by older versions with Remove-Mux-Logon-Task.ps1.
Write-Host "`n[5/5] Kalici baslangic gorevi olusturulmuyor." -ForegroundColor Yellow

Write-Host "`n========================================================" -ForegroundColor Cyan
Write-Host "  ISLEM BASARIYLA TAMAMLANDI!" -ForegroundColor Green
Write-Host "  Simdi NitroSense uygulamasini acabilirsin!" -ForegroundColor Cyan
Write-Host "========================================================" -ForegroundColor Cyan
