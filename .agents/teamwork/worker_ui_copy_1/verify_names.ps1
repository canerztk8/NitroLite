$report = Get-Content '.agents\teamwork\explorer_integrity_survey_1\survey_integrity_report.md' -Raw
$xaml = Get-Content 'NitroLite\MainWindow.xaml' -Raw
$osdXaml = Get-Content 'NitroLite\OsdOverlayWindow.xaml' -Raw

$pattern = '\|\s*`([A-Za-z0-9_]+)`\s*\|\s*`?(?:Border|TextBlock|RadioButton|Grid|ProgressBar|CheckBox|ComboBox|Button|Canvas|Polygon|Polyline|TextBox|Slider)`?'
$matches = [regex]::Matches($report, $pattern)
$names = @($matches | ForEach-Object { $_.Groups[1].Value } | Where-Object { $_ -ne 'ValueChanged' } | Sort-Object -Unique)

$missing = @()
foreach ($n in $names) {
    if (-not $xaml.Contains("x:Name=""$n""")) {
        $missing += $n
    }
}

Write-Output "MainWindow survey controls count: $($names.Count)"
Write-Output "Missing in MainWindow.xaml: $(if ($missing.Count -eq 0) { 'NONE (100% Intact)' } else { $missing -join ', ' })"

$osdNames = @('TxtOsdCpuTemp', 'TxtOsdCpuPower', 'TxtOsdCpuFreq', 'TxtOsdCpuUsage', 'TxtOsdGpuTemp', 'TxtOsdGpuPower', 'TxtOsdGpuFreq', 'TxtOsdVram', 'TxtOsdMode', 'TxtOsdRam', 'TxtOsdFan')
$missingOsd = @()
foreach ($n in $osdNames) {
    if (-not $osdXaml.Contains("x:Name=""$n""")) {
        $missingOsd += $n
    }
}
Write-Output "OsdOverlayWindow controls count: $($osdNames.Count)"
Write-Output "Missing in OsdOverlayWindow.xaml: $(if ($missingOsd.Count -eq 0) { 'NONE (100% Intact)' } else { $missingOsd -join ', ' })"
