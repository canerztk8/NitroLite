# audit_all.ps1 - Exhaustive Reviewer Gate v2.2 Audit Script

$ErrorActionPreference = "Stop"

$workspaceRoot = "c:\Users\caner\Documents\antigravity\eager-shannon"
$mwXamlPath = "$workspaceRoot\NitroLite\MainWindow.xaml"
$mwCsPath = "$workspaceRoot\NitroLite\MainWindow.xaml.cs"
$osdXamlPath = "$workspaceRoot\NitroLite\OsdOverlayWindow.xaml"
$osdCsPath = "$workspaceRoot\NitroLite\OsdOverlayWindow.xaml.cs"
$rgbCsPath = "$workspaceRoot\NitroLite\Core\RgbProfileService.cs"
$thermalCsPath = "$workspaceRoot\NitroLite\Core\ThermalHealthService.cs"
$storageCsPath = "$workspaceRoot\NitroLite\Core\StorageHealthService.cs"

$targetFiles = @($mwXamlPath, $mwCsPath, $osdXamlPath, $osdCsPath, $rgbCsPath, $thermalCsPath, $storageCsPath)

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "AUDIT 1: EMOJI PURGE VERIFICATION (UTF-8)" -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

# Unicode patterns covering emojis and symbols
$emojiRegex = '[\uD83C-\uDBFF][\uDC00-\uDFFF]|[\u2600-\u26FF]|[\u2700-\u270E\u2710-\u2714\u2716-\u27BF]'

$emojiFound = @()
foreach ($file in $targetFiles) {
    $lines = [System.IO.File]::ReadAllLines($file, [System.Text.Encoding]::UTF8)
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]
        $matches = [regex]::Matches($line, $emojiRegex)
        foreach ($m in $matches) {
            $emojiFound += [PSCustomObject]@{
                File = (Split-Path $file -Leaf)
                Line = $i + 1
                Char = $m.Value
                Codepoint = ("U+{0:X4}" -f [int][char]($m.Value[0]))
                Content = $line.Trim()
            }
        }
    }
}

Write-Host "Target files examined: $($targetFiles.Count)"
Write-Host "Total emojis found: $($emojiFound.Count)"
if ($emojiFound.Count -gt 0) {
    $emojiFound | Format-Table -AutoSize
} else {
    Write-Host "[PASS] Zero emojis found across all audited files (U+2715 '✕' close button excluded from emoji range)." -ForegroundColor Green
}

Write-Host "`n==========================================================" -ForegroundColor Cyan
Write-Host "AUDIT 2: EM-DASH AND SPACED HYPHEN AUDIT (UTF-8)" -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

$dashFindings = @()
foreach ($file in @($mwXamlPath, $osdXamlPath, $mwCsPath, $rgbCsPath)) {
    $lines = [System.IO.File]::ReadAllLines($file, [System.Text.Encoding]::UTF8)
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]
        if ($line -match '[\u2013\u2014]') {
            $dashFindings += [PSCustomObject]@{
                File = (Split-Path $file -Leaf)
                Line = $i + 1
                Type = "Unicode Dash (\u2013 / \u2014)"
                Content = $line.Trim()
            }
        }
        # Check spaced hyphen in XAML Text="..." or Content="..." or Title="..."
        if ($file.EndsWith(".xaml")) {
            if ($line -match '(?:Text|Content|Title|Header)\s*=\s*"[^"]*\s+-\s+[^"]*"') {
                $dashFindings += [PSCustomObject]@{
                    File = (Split-Path $file -Leaf)
                    Line = $i + 1
                    Type = "Spaced Hyphen in XAML attribute"
                    Content = $line.Trim()
                }
            }
        }
        if ($file.EndsWith(".cs")) {
            # In C#, check user-facing string literals with spaced hyphen
            if ($line -match '"[^"]*\s+-\s+[^"]*"') {
                if ($line -notmatch '^\s*//' -and $line -notmatch 'Regex|Format|CommandLine|\.BusType') {
                    $dashFindings += [PSCustomObject]@{
                        File = (Split-Path $file -Leaf)
                        Line = $i + 1
                        Type = "Spaced Hyphen in C# string"
                        Content = $line.Trim()
                    }
                }
            }
        }
    }
}

Write-Host "Total Dash/Hyphen findings: $($dashFindings.Count)"
if ($dashFindings.Count -gt 0) {
    $dashFindings | Format-Table -AutoSize
} else {
    Write-Host "[PASS] No em-dashes, en-dashes, or spaced hyphens found in user-facing copy." -ForegroundColor Green
}

Write-Host "`n==========================================================" -ForegroundColor Cyan
Write-Host "AUDIT 3: AI BUZZWORDS & MARKETING FLUFF AUDIT (UTF-8)" -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

$buzzwords = @(
    "Akıllı", "Mükemmel", "Harika", "Stüdyo", "RAM Yiyor", "Tebrikler",
    "Asistan", "Proaktif", "Zengin", "Tek Tıkla", "tertemiz", "ferah", "gayet serin"
)

$buzzFindings = @()
foreach ($file in $targetFiles) {
    $lines = [System.IO.File]::ReadAllLines($file, [System.Text.Encoding]::UTF8)
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]
        if ($line -match '^\s*//' -or $line -match '^\s*<!--') { continue }
        
        foreach ($bw in $buzzwords) {
            if ($line -match "(?i)\b$bw\b" -or $line.Contains($bw)) {
                $buzzFindings += [PSCustomObject]@{
                    File = (Split-Path $file -Leaf)
                    Line = $i + 1
                    Buzzword = $bw
                    Content = $line.Trim()
                }
            }
        }
    }
}

Write-Host "Total Buzzword findings: $($buzzFindings.Count)"
if ($buzzFindings.Count -gt 0) {
    $buzzFindings | Format-Table -AutoSize
} else {
    Write-Host "[PASS] Zero AI buzzwords detected across all target files." -ForegroundColor Green
}

Write-Host "`n==========================================================" -ForegroundColor Cyan
Write-Host "AUDIT 4: ZERO-REGRESSION HARDWARE PRESERVATION" -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

$mwXaml = [System.IO.File]::ReadAllText($mwXamlPath, [System.Text.Encoding]::UTF8)
$mwCs = [System.IO.File]::ReadAllText($mwCsPath, [System.Text.Encoding]::UTF8)
$osdXaml = [System.IO.File]::ReadAllText($osdXamlPath, [System.Text.Encoding]::UTF8)
$osdCs = [System.IO.File]::ReadAllText($osdCsPath, [System.Text.Encoding]::UTF8)

# 4.1 Check 136 x:Name in MainWindow.xaml from explorer survey
$surveyReport = [System.IO.File]::ReadAllText("$workspaceRoot\.agents\teamwork\explorer_integrity_survey_1\survey_integrity_report.md", [System.Text.Encoding]::UTF8)
$namePattern = '\|\s*`([A-Za-z0-9_]+)`\s*\|\s*`?(?:Border|TextBlock|RadioButton|Grid|ProgressBar|CheckBox|ComboBox|Button|Canvas|Polygon|Polyline|TextBox|Slider)`?'
$nameMatches = [regex]::Matches($surveyReport, $namePattern)
$surveyNames = @($nameMatches | ForEach-Object { $_.Groups[1].Value } | Where-Object { $_ -ne 'ValueChanged' } | Sort-Object -Unique)

$missingMwNames = @()
foreach ($name in $surveyNames) {
    if (-not $mwXaml.Contains("x:Name=""$name""")) {
        $missingMwNames += $name
    }
}
Write-Host "Survey MainWindow x:Name count: $($surveyNames.Count)"
Write-Host "Missing MainWindow x:Name: $(if ($missingMwNames.Count -eq 0) { 'NONE [PASS]' } else { $missingMwNames -join ', ' })"

# 4.2 Check 11 x:Name in OsdOverlayWindow.xaml
$osdNames = @('TxtOsdCpuTemp', 'TxtOsdCpuPower', 'TxtOsdCpuFreq', 'TxtOsdCpuUsage', 'TxtOsdGpuTemp', 'TxtOsdGpuPower', 'TxtOsdGpuFreq', 'TxtOsdVram', 'TxtOsdMode', 'TxtOsdRam', 'TxtOsdFan')
$missingOsdNames = @()
foreach ($name in $osdNames) {
    if (-not $osdXaml.Contains("x:Name=""$name""")) {
        $missingOsdNames += $name
    }
}
Write-Host "OSD Overlay x:Name count: $($osdNames.Count)"
Write-Host "Missing OSD x:Name: $(if ($missingOsdNames.Count -eq 0) { 'NONE [PASS]' } else { $missingOsdNames -join ', ' })"

# 4.3 Check event bindings in MainWindow.xaml
# Extract from survey report
$surveyHandlerPattern = '\|\s*`(?:Click|Checked|Unchecked|SelectionChanged|ValueChanged|MouseLeftButtonDown)`\s*\|\s*`([A-Za-z0-9_]+)`\s*\|'
$surveyHandlerMatches = [regex]::Matches($surveyReport, $surveyHandlerPattern)
$surveyHandlers = @($surveyHandlerMatches | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
Write-Host "`nSurvey unique event handler methods: $($surveyHandlers.Count)"

$missingInXaml = @()
$missingInCs = @()
foreach ($h in $surveyHandlers) {
    if (-not $mwXaml.Contains("""$h""")) {
        $missingInXaml += $h
    }
    if (-not $mwCs.Contains($h)) {
        $missingInCs += $h
    }
}
Write-Host "Survey handlers missing in MainWindow.xaml: $(if ($missingInXaml.Count -eq 0) { 'NONE [PASS]' } else { $missingInXaml -join ', ' })"
Write-Host "Survey handlers missing in MainWindow.xaml.cs: $(if ($missingInCs.Count -eq 0) { 'NONE [PASS]' } else { $missingInCs -join ', ' })"

# Count total event binding attributes in MainWindow.xaml
$eventRegex = '(?:Click|Checked|Unchecked|SelectionChanged|ValueChanged|MouseLeftButtonDown)\s*=\s*"([^"]+)"'
$bindingList = [regex]::Matches($mwXaml, $eventRegex)
Write-Host "Total event binding attributes in MainWindow.xaml: $($bindingList.Count)"

# 4.4 Check all 27 hardware Tag properties intact
$requiredTags = @(
    'Quiet', 'Default', 'Extreme', 'Turbo',
    'Aggressive', 'Efficient', 'Disabled',
    'CPU_TEMP', 'GPU_TEMP', 'CPU_WATT', 'GPU_WATT', 'CPU_GHZ', 'CPU_USAGE', 'GPU_USAGE', 'RAM_USAGE', 'VRAM_GB',
    '#00E5FF', '#FF2A55', '#00FFA3', '#BD00FF', '#FFAA00', '#FFFFFF', '#000000',
    '14', '30',
    'Auto', 'Max'
)
$missingTags = @()
foreach ($tag in $requiredTags) {
    if (-not $mwXaml.Contains("Tag=""$tag""")) {
        $missingTags += $tag
    }
}
Write-Host "`nRequired Tag count: $($requiredTags.Count)"
Write-Host "Missing Tags in MainWindow.xaml: $(if ($missingTags.Count -eq 0) { 'NONE [PASS]' } else { $missingTags -join ', ' })"

# 4.5 Check 5 dynamic SolidColorBrush keys in Window.Resources
$requiredBrushes = @("AccentGreen", "AccentCyan", "AccentRed", "TextSecondary", "TextMuted")
$missingBrushes = @()
foreach ($brush in $requiredBrushes) {
    if (-not ($mwXaml -match "x:Key\s*=\s*""$brush""")) {
        $missingBrushes += $brush
    }
}
Write-Host "`nRequired Dynamic Brush Keys: $($requiredBrushes.Count)"
Write-Host "Missing Brush Keys in Window.Resources: $(if ($missingBrushes.Count -eq 0) { 'NONE [PASS]' } else { $missingBrushes -join ', ' })"

# 4.6 Check CmbRgbEffect uppercase prefix tokens in MainWindow.xaml
$requiredEffectPrefixes = @("STATIC", "WAVE", "BREATHING", "NEON", "SHIFTING", "ZOOM", "METEOR", "TWINKLING", "OFF")
$missingPrefixes = @()
foreach ($prefix in $requiredEffectPrefixes) {
    if (-not ($mwXaml -match "(?i)<ComboBoxItem[^>]*>\s*$prefix\b|<ComboBoxItem[^>]*Content\s*=\s*""$prefix")) {
        if (-not ($mwXaml.Contains(">$prefix") -or $mwXaml.Contains("""$prefix"))) {
            $missingPrefixes += $prefix
        }
    }
}
Write-Host "`nRequired CmbRgbEffect uppercase prefix tokens: $($requiredEffectPrefixes.Count)"
Write-Host "Missing effect prefix tokens: $(if ($missingPrefixes.Count -eq 0) { 'NONE [PASS]' } else { $missingPrefixes -join ', ' })"
