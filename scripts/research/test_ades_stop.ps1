# Controlled empirical test: Stop AcerDeviceEnablingServiceV2 and observe NitroSense
$ErrorActionPreference = "Continue"

Write-Host "=== Current NitroSense PID and connection state ===" -ForegroundColor Cyan
$nsProc = Get-Process -Name "NitroSense" -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 -or $_.WorkingSet64 -gt 100MB } | Select-Object -First 1
Write-Host "NitroSense main PID: $($nsProc.Id)"

$connBefore = Get-NetTCPConnection | Where-Object { $_.LocalPort -eq 51779 -or $_.RemotePort -eq 51779 }
Write-Host "Connection before stopping ADESv2:"
$connBefore | Format-Table -AutoSize

Write-Host "`nStopping AcerDeviceEnablingServiceV2..." -ForegroundColor Yellow
Stop-Service -Name "AcerDeviceEnablingServiceV2" -Force
Start-Sleep -Seconds 2

$connDuring = Get-NetTCPConnection | Where-Object { $_.LocalPort -eq 51779 -or $_.RemotePort -eq 51779 } -ErrorAction SilentlyContinue
Write-Host "Connection during ADESv2 stopped:"
$connDuring

$nsRunning = Get-Process -Name "NitroSense" -ErrorAction SilentlyContinue
Write-Host "NitroSense process count: $($nsRunning.Count)"

Write-Host "`nImmediately restarting AcerDeviceEnablingServiceV2..." -ForegroundColor Green
Start-Service -Name "AcerDeviceEnablingServiceV2"
Start-Sleep -Seconds 2

$connAfter = Get-NetTCPConnection | Where-Object { $_.LocalPort -eq 51779 -or $_.RemotePort -eq 51779 } -ErrorAction SilentlyContinue
Write-Host "Connection after restarting ADESv2:"
$connAfter | Format-Table -AutoSize

$svcStatus = Get-Service -Name "AcerDeviceEnablingServiceV2"
Write-Host "Service final status: $($svcStatus.Status)"
