# Battery Verification Script

Write-Host "=== Inspecting C:\ProgramData\Acer\CC\settings.json ==="
if (Test-Path "C:\ProgramData\Acer\CC\settings.json") {
    Get-Content "C:\ProgramData\Acer\CC\settings.json"
} else {
    Write-Host "settings.json not found!"
}

Write-Host "`n=== Querying Win32_Battery ==="
Get-CimInstance Win32_Battery | Select-Object Name, EstimatedChargeRemaining, BatteryStatus, Status | Format-List

Write-Host "`n=== Checking Battery Report for Today's History ==="
$reportPath = "$PSScriptRoot\battery_report.html"
powercfg /batteryreport /output $reportPath
if (Test-Path $reportPath) {
    $html = Get-Content $reportPath -Raw
    Write-Host "Battery report generated ($($html.Length) bytes)."
    if ($html -match "83[.,]637") {
        Write-Host "Found 83.637 mWh in battery report!"
    } else {
        Write-Host "Pattern 83.637 not matched directly in battery report."
    }
    # Look for recent entries around 13:22 or 100%
    $matches = [regex]::Matches($html, '2026-11-03.*?100.*?mWh', [System.Text.RegularExpressions.RegexOptions]::Singleline)
    Write-Host "Found $($matches.Count) matching entries for 2026-11-03 with 100%."
}
