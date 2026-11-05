# Fix Keyboard Lighting Service (AcerLightingService)
$ErrorActionPreference = "Stop"

Write-Host "========================================================" -ForegroundColor Cyan
Write-Host "  Klavye Aydinlatma Servisi Onarimi (AcerLightingService)" -ForegroundColor Cyan
Write-Host "========================================================" -ForegroundColor Cyan

$lightingExe = "C:\Windows\System32\DriverStore\FileRepository\predatorservice.inf_amd64_c634eb8e856fb962\AcerLightingService.exe"
if (!(Test-Path -LiteralPath $lightingExe -PathType Leaf)) {
    throw "AcerLightingService.exe surucu deposunda bulunamadi: $lightingExe"
}

# 1. Stop services
Write-Host "`n[1/4] Ilgili servisler durduruluyor..." -ForegroundColor Yellow
Stop-Service -Name "AASSvc" -Force -ErrorAction SilentlyContinue
Stop-Service -Name "AcerLightingService" -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 1

# 2. Configure AcerLightingService registry & startup type
Write-Host "`n[2/4] AcerLightingService kayit defteri ve baslangic turu duzeltiliyor..." -ForegroundColor Yellow
Set-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\AcerLightingService" -Name "ImagePath" -Value $lightingExe -Force
Set-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\AcerLightingService" -Name "Start" -Value 2 -Force
Set-Service -Name "AcerLightingService" -StartupType Automatic
Write-Host "  -> [BASARILI] AcerLightingService StartType = Automatic olarak ayarlandi." -ForegroundColor Green

# 3. Start AcerLightingService then AASSvc
Write-Host "`n[3/4] AcerLightingService ve AASSvc baslatiliyor..." -ForegroundColor Yellow
Start-Service -Name "AcerLightingService"
Start-Sleep -Seconds 1
Start-Service -Name "AASSvc"
Start-Sleep -Seconds 2

# 4. Verification
Write-Host "`n[4/4] Servis durumlari kontrol ediliyor..." -ForegroundColor Yellow
$svcLighting = Get-Service -Name "AcerLightingService"
$svcAas = Get-Service -Name "AASSvc"

Write-Host "  AcerLightingService: $($svcLighting.Status) ($($svcLighting.StartType))" -ForegroundColor Green
Write-Host "  AASSvc: $($svcAas.Status) ($($svcAas.StartType))" -ForegroundColor Green

Set-Content -Path "$PSScriptRoot\lighting_fixed.log" -Value "AcerLightingService: $($svcLighting.Status) ($($svcLighting.StartType))`r`nAASSvc: $($svcAas.Status) ($($svcAas.StartType))`r`nTime: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')" -Encoding utf8

Write-Host "`n========================================================" -ForegroundColor Cyan
Write-Host "  ISLEM BASARIYLA TAMAMLANDI! Klavye aydinlatmasi aktif edildi." -ForegroundColor Green
Write-Host "========================================================" -ForegroundColor Cyan
