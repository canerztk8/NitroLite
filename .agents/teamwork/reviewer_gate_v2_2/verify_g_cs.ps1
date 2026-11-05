$gContent = [System.IO.File]::ReadAllText('NitroLite\obj\Debug\net10.0-windows\MainWindow.g.cs', [System.Text.Encoding]::UTF8)
$fieldMatches = [regex]::Matches($gContent, 'internal\s+[\w\.<>]+\s+(\w+);')
$fields = @($fieldMatches | ForEach-Object { $_.Groups[1].Value })
Write-Host "Generated fields in MainWindow.g.cs: $($fields.Count)"

$survey = [System.IO.File]::ReadAllText('.agents\teamwork\explorer_integrity_survey_1\survey_integrity_report.md', [System.Text.Encoding]::UTF8)
$nameMatches = [regex]::Matches($survey, '\|\s*`([A-Za-z0-9_]+)`\s*\|\s*`?(?:Border|TextBlock|RadioButton|Grid|ProgressBar|CheckBox|ComboBox|Button|Canvas|Polygon|Polyline|TextBox|Slider)`?')
$surveyNames = @($nameMatches | ForEach-Object { $_.Groups[1].Value } | Where-Object { $_ -ne 'ValueChanged' } | Sort-Object -Unique)
Write-Host "Survey unique required names: $($surveyNames.Count)"

$missing = @()
foreach ($n in $surveyNames) {
    if ($fields -notcontains $n) {
        $missing += $n
    }
}
Write-Host "Missing from generated MainWindow.g.cs: $(if ($missing.Count -eq 0) { 'NONE [100% Intact]' } else { $missing -join ', ' })"

$osdGContent = [System.IO.File]::ReadAllText('NitroLite\obj\Debug\net10.0-windows\OsdOverlayWindow.g.cs', [System.Text.Encoding]::UTF8)
$osdFieldMatches = [regex]::Matches($osdGContent, 'internal\s+[\w\.<>]+\s+(\w+);')
$osdFields = @($osdFieldMatches | ForEach-Object { $_.Groups[1].Value })
Write-Host "Generated fields in OsdOverlayWindow.g.cs: $($osdFields.Count)"

$osdRequired = @('TxtOsdCpuTemp', 'TxtOsdCpuPower', 'TxtOsdCpuFreq', 'TxtOsdCpuUsage', 'TxtOsdGpuTemp', 'TxtOsdGpuPower', 'TxtOsdGpuFreq', 'TxtOsdVram', 'TxtOsdMode', 'TxtOsdRam', 'TxtOsdFan')
$missingOsd = @()
foreach ($n in $osdRequired) {
    if ($osdFields -notcontains $n) {
        $missingOsd += $n
    }
}
Write-Host "Missing from generated OsdOverlayWindow.g.cs: $(if ($missingOsd.Count -eq 0) { 'NONE [100% Intact]' } else { $missingOsd -join ', ' })"
