# Inspect battery report and WMI battery status
$ErrorActionPreference = "Continue"

& powercfg /batteryreport /output ".\battery-report.html"
if (Test-Path ".\battery-report.html") {
    $report = Get-Content ".\battery-report.html" -Raw
    # Extract capacity numbers
    [regex]::Matches($report, "DESIGN CAPACITY.*?</span", [System.Text.RegularExpressions.RegexOptions]::Singleline) | ForEach-Object { $_.Value -replace "<.*?>", " " }
    [regex]::Matches($report, "FULL CHARGE CAPACITY.*?</span", [System.Text.RegularExpressions.RegexOptions]::Singleline) | ForEach-Object { $_.Value -replace "<.*?>", " " }
    [regex]::Matches($report, "CYCLE COUNT.*?</span", [System.Text.RegularExpressions.RegexOptions]::Singleline) | ForEach-Object { $_.Value -replace "<.*?>", " " }
    
    # Extract recent battery usage
    Write-Host "`nRecent battery levels:"
    [regex]::Matches($report, "(2026-\d\d-\d\d \d\d:\d\d:\d\d)\s*</td><td.*?>\s*([0-9,]+ mWh|\d+ %)\s*</td><td.*?>\s*([0-9,]+ mWh|\d+ %)") | ForEach-Object { $_.Value -replace "<.*?>", " " } | Select-Object -Last 10
}
