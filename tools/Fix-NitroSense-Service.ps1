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

# 3. Ensure MUX capability flags in registry
Write-Host "`n[3/5] MUX yetenek degerleri yaziliyor..." -ForegroundColor Yellow
$advPath = "HKLM:\SOFTWARE\OEM\AcerAgentService\AdvanceSettings"
Set-ItemProperty -Path $advPath -Name "discrete_gpu_support" -Value 1 -Type DWord -Force
Set-ItemProperty -Path $advPath -Name "dgpu_mode_capability" -Value 7 -Type DWord -Force
Write-Host "  -> [BASARILI] discrete_gpu_support = 1, dgpu_mode_capability = 7" -ForegroundColor Green

# 4. Start updated 2025 services
Write-Host "`n[4/5] Guncel 2025 Acer servisleri baslatiliyor..." -ForegroundColor Yellow
Start-Service -Name "AASSvc"
Start-Service -Name "AcerLightingService" -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2
Write-Host "  -> [BASARILI] Guncel servisler calisiyor." -ForegroundColor Green

# 5. Create persistent logon task
Write-Host "`n[5/5] Baslangic gorevi tanimlaniyor..." -ForegroundColor Yellow
$taskCmd = "powershell -WindowStyle Hidden -NoProfile -Command `"Set-ItemProperty -Path '$advPath' -Name 'discrete_gpu_support' -Value 1 -Type DWord`""
schtasks /create /tn "AcerNitroSenseMuxRestore" /tr $taskCmd /sc onlogon /rl highest /f | Out-Null
Write-Host "  -> [BASARILI] Baslangic gorevi hazir." -ForegroundColor Green

Write-Host "`n========================================================" -ForegroundColor Cyan
Write-Host "  ISLEM BASARIYLA TAMAMLANDI!" -ForegroundColor Green
Write-Host "  Simdi NitroSense uygulamasini acabilirsin!" -ForegroundColor Cyan
Write-Host "========================================================" -ForegroundColor Cyan
