$report = Get-Content '.agents\teamwork\explorer_integrity_survey_1\survey_integrity_report.md' -Raw
$xaml = Get-Content 'NitroLite\MainWindow.xaml' -Raw

$pattern = '\|\s*`(?:Click|Checked|SelectionChanged|ValueChanged|Loaded)`\s*\|\s*`([A-Za-z0-9_]+)`\s*\|'
$matches = [regex]::Matches($report, $pattern)
$handlers = @($matches | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)

$missingHandlers = @()
foreach ($h in $handlers) {
    if (-not $xaml.Contains("=""$h""")) {
        $missingHandlers += $h
    }
}

Write-Output "Event handlers count in survey: $($handlers.Count)"
Write-Output "Missing handlers in MainWindow.xaml: $(if ($missingHandlers.Count -eq 0) { 'NONE (100% Intact)' } else { $missingHandlers -join ', ' })"
