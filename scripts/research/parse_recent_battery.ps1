# Check recent battery readings in battery report
$ErrorActionPreference = "Continue"

$html = Get-Content ".\battery-report.html" -Raw
$tables = [regex]::Matches($html, "<table>.*?</table>", [System.Text.RegularExpressions.RegexOptions]::Singleline)
Write-Host "Total tables: $($tables.Count)"

foreach ($t in $tables) {
    if ($t.Value -match "START TIME|STATE|CAPACITY REMAINING") {
        Write-Host "Found Recent Usage Table!"
        # Print last 10 rows
        $rows = [regex]::Matches($t.Value, "<tr.*?>.*?</tr>", [System.Text.RegularExpressions.RegexOptions]::Singleline)
        for ($i = [Math]::Max(0, $rows.Count - 15); $i -lt $rows.Count; $i++) {
            $cleaned = ($rows[$i].Value -replace "<.*?>", " ") -replace "\s+", " "
            Write-Host $cleaned.Trim()
        }
    }
}
