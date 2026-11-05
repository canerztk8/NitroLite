# Check settings.json usage and battery status
$ErrorActionPreference = "Continue"

Write-Host "Files in C:\ProgramData\Acer\CC:"
Get-ChildItem -Path "C:\ProgramData\Acer\CC" -Recurse | Select-Object FullName, Length

Write-Host "`nSearch for settings.json across all Acer ProgramData:"
Get-ChildItem -Path "C:\ProgramData\Acer" -Recurse -Filter "*.json" | ForEach-Object {
    Write-Host "`n--- $($_.FullName) ---"
    Get-Content $_.FullName
}

Write-Host "`nChecking current battery status via Windows CIM:"
Get-CimInstance Win32_Battery | Select-Object Name, EstimatedChargeRemaining, BatteryStatus, Status, DesignCapacity, FullChargeCapacity
