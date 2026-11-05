# Check full battery history for today
$ErrorActionPreference = "Continue"

$html = Get-Content ".\battery-report.html" -Raw
# Search for all occurrences of 2026-11-03 entries in battery report
$matches = [regex]::Matches($html, "2026-11-03.*?</tr>", [System.Text.RegularExpressions.RegexOptions]::Singleline)
Write-Host "Found $($matches.Count) matches for 2026-11-03:"
foreach ($m in $matches) {
    $cleaned = ($m.Value -replace "<.*?>", " ") -replace "\s+", " "
    Write-Host $cleaned.Trim()
}
