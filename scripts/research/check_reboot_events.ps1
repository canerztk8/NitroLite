# Deep investigation of what occurred around 13:22 on 2026-11-03
$ErrorActionPreference = "Continue"

Write-Host "=== 1. SYSTEM EVENT LOGS AROUND 13:20-13:26 ===" -ForegroundColor Cyan
Get-WinEvent -FilterHashtable @{LogName='System'; StartTime=[datetime]'2026-11-03 13:20:00'; EndTime=[datetime]'2026-11-03 13:26:00'} | Select-Object TimeCreated, Id, ProviderName, Message | Format-List

Write-Host "`n=== 2. APPLICATION EVENT LOGS AROUND 13:20-13:26 ===" -ForegroundColor Cyan
Get-WinEvent -FilterHashtable @{LogName='Application'; StartTime=[datetime]'2026-11-03 13:20:00'; EndTime=[datetime]'2026-11-03 13:26:00'} | Select-Object TimeCreated, Id, ProviderName, Message | Format-List
