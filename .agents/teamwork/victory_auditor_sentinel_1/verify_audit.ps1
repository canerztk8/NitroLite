$ErrorActionPreference = "Stop"

Write-Host "========================================================"
Write-Host "       NITROLITE COMPREHENSIVE VICTORY AUDIT SUITE       "
Write-Host "========================================================"

$mainXamlPath = "c:\Users\caner\Documents\antigravity\eager-shannon\NitroLite\MainWindow.xaml"
$mainCsPath = "c:\Users\caner\Documents\antigravity\eager-shannon\NitroLite\MainWindow.xaml.cs"
$osdXamlPath = "c:\Users\caner\Documents\antigravity\eager-shannon\NitroLite\OsdOverlayWindow.xaml"
$osdCsPath = "c:\Users\caner\Documents\antigravity\eager-shannon\NitroLite\OsdOverlayWindow.xaml.cs"

$mainXaml = Get-Content $mainXamlPath -Raw -Encoding utf8
$mainCs = Get-Content $mainCsPath -Raw -Encoding utf8
$osdXaml = Get-Content $osdXamlPath -Raw -Encoding utf8
$osdCs = Get-Content $osdCsPath -Raw -Encoding utf8

Write-Host "`n--- CHECK 1: GRADIENTS & GLASSMORPHISM AUDIT ---"
$gradientMatches = [regex]::Matches($mainXaml + $osdXaml, "(LinearGradientBrush|RadialGradientBrush|GradientStop)")
Write-Host "Gradient instances in XAML: $($gradientMatches.Count)"
if ($gradientMatches.Count -gt 0) {
    Write-Host "FAIL: Gradients found!"
} else {
    Write-Host "PASS: 0 gradients found in MainWindow.xaml & OsdOverlayWindow.xaml."
}

$blurEffects = [regex]::Matches($mainXaml + $osdXaml, "BlurEffect")
Write-Host "BlurEffect instances in XAML: $($blurEffects.Count)"

$shadowBlurs = [regex]::Matches($mainXaml + $osdXaml, 'BlurRadius="([^"]+)"') | ForEach-Object { [double]$_.Groups[1].Value }
$maxBlur = ($shadowBlurs | Measure-Object -Maximum).Maximum
Write-Host "Max DropShadowEffect BlurRadius: $maxBlur (Target <= 8)"
if ($maxBlur -gt 8) {
    Write-Host "FAIL: Heavy blur drop shadow detected ($maxBlur > 8)!"
} else {
    Write-Host "PASS: All DropShadowEffect blur radii are subtle and <= 8."
}

Write-Host "`n--- CHECK 2: BORDER & SURFACE PALETTE AUDIT ---"
$borderBrushes = [regex]::Matches($mainXaml, 'BorderBrush="([^"]+)"') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique
Write-Host "BorderBrush values: $($borderBrushes -join ', ')"
# Ensure cards do not have saturated neon borders
$neonCardBorders = [regex]::Matches($mainXaml, '<Border[^>]+Style="\{StaticResource CardBorder\}"[^>]+BorderBrush="(?!\{StaticResource BorderDark\}|Transparent)[^"]+"')
Write-Host "Neon CardBorder instances: $($neonCardBorders.Count)"

Write-Host "`n--- CHECK 3: EMOJI PURGE AUDIT ---"
$allFiles = Get-ChildItem -Path "c:\Users\caner\Documents\antigravity\eager-shannon\NitroLite", "c:\Users\caner\Documents\antigravity\eager-shannon\NitroLite.Tests" -Recurse -Include *.cs, *.xaml
$emojiPattern = '[\u2600-\u2704\u2706-\u2714\u2716-\u27BF\u2B50\u2B55\uD83C-\uDBFF\uDC00-\uDFFF]'
$emojiFound = 0
foreach ($f in $allFiles) {
    $c = Get-Content $f.FullName -Raw -Encoding utf8
    $m = [regex]::Matches($c, $emojiPattern)
    if ($m.Count -gt 0) {
        Write-Host "Emoji in $($f.Name): $($m.Count) instances"
        $emojiFound += $m.Count
    }
}
Write-Host "Total emojis across all source files: $emojiFound"
if ($emojiFound -gt 0) {
    Write-Host "FAIL: Emojis detected!"
} else {
    Write-Host "PASS: 0 emojis detected across entire codebase."
}

Write-Host "`n--- CHECK 4: TEXT DE-SLOP & BUZZWORD AUDIT ---"
$buzzwordPattern = '\b(Akıllı|Mükemmel|Harika|Stüdyo|RAM Yiyor|Proaktif|Tebrikler|Sihirli)\b'
$buzzwordFound = 0
foreach ($f in $allFiles) {
    $c = Get-Content $f.FullName -Raw -Encoding utf8
    $m = [regex]::Matches($c, $buzzwordPattern, [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
    if ($m.Count -gt 0) {
        Write-Host "Buzzword in $($f.Name): $($m.Count) instances"
        $buzzwordFound += $m.Count
    }
}
Write-Host "Total buzzwords across all source files: $buzzwordFound"
if ($buzzwordFound -gt 0) {
    Write-Host "FAIL: Buzzwords detected!"
} else {
    Write-Host "PASS: 0 buzzwords detected."
}

$emDashFound = 0
foreach ($f in $allFiles) {
    $c = Get-Content $f.FullName -Raw -Encoding utf8
    $m = [regex]::Matches($c, '[\u2014]')
    if ($m.Count -gt 0) {
        Write-Host "Em-dash in $($f.Name): $($m.Count) instances"
        $emDashFound += $m.Count
    }
}
Write-Host "Total em-dashes across all source files: $emDashFound"
if ($emDashFound -gt 0) {
    Write-Host "FAIL: Em-dashes detected!"
} else {
    Write-Host "PASS: 0 em-dashes detected."
}

Write-Host "`n--- CHECK 5: ZERO-REGRESSION CONTROL & WIRING AUDIT ---"
$names136 = @(
    "ToastBanner", "TxtToastIcon", "TxtToastMessage",
    "TxtBatteryHeader", "TxtModeHeader",
    "NavModes", "NavMonitoring", "NavRgb", "NavBattery", "NavDebloat",
    "TxtSideCpuTemp", "TxtSideGpuTemp", "TxtSideCpuFan",
    "TabModes", "TabMonitoring", "TabRgb", "TabBattery", "TabDebloat",
    "TxtIndQuiet", "TxtIndDefault", "TxtIndExtreme", "TxtIndTurbo",
    "TxtCpuFanRpm", "BarCpuFan", "TxtGpuFanRpm", "BarGpuFan",
    "TxtBoostStatusBadge",
    "TxtGameStatus", "ChkAutoGameMode", "CmbConfiguredGames",
    "TxtSelectedGameNameBadge", "TxtSelectedGameExePath",
    "CmbGameTargetMode", "CmbGameTargetCpuBoost", "CmbGameTargetFan",
    "BtnToggleOsd",
    "TxtCpuCoreCount", "TxtMonCpuTemp", "BarMonCpuTemp", "TxtMonCpuPower", "BarMonCpuPower",
    "TxtMonCpuUsage", "BarMonCpuUsage", "TxtMonCpuFreqGhz", "TxtMonGpuTemp", "BarMonGpuTemp",
    "TxtMonGpuPower", "BarMonGpuPower", "TxtMonGpuUsage", "BarMonGpuUsage", "TxtMonVram",
    "BarMonVram", "TxtMonRamDetail", "TxtMonRamPercent", "BarMonRam", "TxtMonSysTemp",
    "TxtRamCleanStatus", "ChkAutoRamClean",
    "CmbChartMetric", "CanvasHistoryChart", "ChartArea", "ChartLine",
    "TxtChartCurrentVal", "TxtChartMinVal", "TxtChartMaxVal", "TxtChartAvgVal",
    "PreviewZone1", "PreviewZone2", "PreviewZone3", "PreviewZone4",
    "CmbRgbPresets", "TxtCustomPresetName", "CmbRgbEffect", "ChkBacklightTimeout",
    "TxtBrightnessVal", "SliderBrightness", "TxtSpeedVal", "SliderSpeed",
    "TxtBatLevelBig", "TxtBatStateDesc", "BarBatBig", "TxtBatDischargeRate", "TxtBatRemainingTime",
    "ChkWatchdog", "TxtDischargeStatus", "BtnToggleDischarge", "TxtBatHealthPercent",
    "TxtBatWearPercent", "TxtBatFullChargeCap", "TxtBatDesignCap", "TxtBatVoltage",
    "BannerOvercharge", "TxtOverchargeMsg",
    "TxtThermalBadge", "TxtPasteHealthPercent", "TxtThermalResistance", "TxtActiveFanHours",
    "TxtAvgFanRpm", "TxtPassiveFanHours", "TxtFanBearingHealth", "TxtStabilityDaysSubtitle",
    "RadioTrend14Days", "RadioTrend30Days", "TxtStabilityScore", "TxtStabilityStatus",
    "TxtStabilitySlope", "TxtStabilityBadge", "BarStabilityScore", "TxtThermalRecommendation",
    "TxtSubMoboTemp", "TxtSubSsd1Temp", "TxtSubSsd2Temp", "TxtSubBatteryTemp",
    "TxtStorageBadge", "CardDrive0", "TxtDrive0Letter", "TxtDrive0Name", "TxtDrive0Bus",
    "TxtDrive0Temp", "TxtDrive0HealthBadge", "TxtDrive0Detail", "TxtDrive0Free", "BarDrive0",
    "CardDrive1", "TxtDrive1Letter", "TxtDrive1Name", "TxtDrive1Bus", "TxtDrive1Temp",
    "TxtDrive1HealthBadge", "TxtDrive1Detail", "TxtDrive1Free", "BarDrive1",
    "ListServiceStatuses", "TxtNitroSenseRunningBadge", "ChkAutoStart", "ChkStartMinimized",
    "LedAas", "TxtAasStatus"
)
$missing136 = @()
foreach ($n in $names136) {
    if ($mainXaml -notmatch "x:Name=`"$n`"") {
        $missing136 += $n
    }
}
Write-Host "Authoritative 136 control inventory count: $($names136.Count)"
Write-Host "Missing from MainWindow.xaml: $($missing136.Count)"
if ($missing136.Count -gt 0) {
    Write-Host "FAIL: Missing controls: $($missing136 -join ', ')"
} else {
    Write-Host "PASS: 100% of the 136 authoritative controls exist in MainWindow.xaml."
}

$osd11Names = @(
    "TxtOsdCpuTemp", "TxtOsdCpuPower", "TxtOsdCpuFreq", "TxtOsdCpuUsage",
    "TxtOsdGpuTemp", "TxtOsdGpuPower", "TxtOsdGpuFreq", "TxtOsdVram",
    "TxtOsdMode", "TxtOsdRam", "TxtOsdFan"
)
$missingOsd = @()
foreach ($n in $osd11Names) {
    if ($osdXaml -notmatch "x:Name=`"$n`"") {
        $missingOsd += $n
    }
}
Write-Host "OsdOverlayWindow control inventory count: $($osd11Names.Count)"
Write-Host "Missing from OsdOverlayWindow.xaml: $($missingOsd.Count)"
if ($missingOsd.Count -gt 0) {
    Write-Host "FAIL: Missing OSD controls: $($missingOsd -join ', ')"
} else {
    Write-Host "PASS: 100% of the 11 OSD controls exist in OsdOverlayWindow.xaml."
}

$eventMatches = [regex]::Matches($mainXaml, '\b(Click|MouseLeftButtonDown|SelectionChanged|Checked|Unchecked|ValueChanged|PreviewKeyDown|Loaded|Unloaded|Closing|Closed)="([^"]+)"')
$handlers = [System.Collections.Generic.HashSet[string]]::new()
foreach ($m in $eventMatches) {
    $val = $m.Groups[2].Value
    if ($val -notmatch '^{Binding' -and $val -ne 'True' -and $val -ne 'False') {
        $handlers.Add($val) | Out-Null
    }
}
$missingHandlers = @()
foreach ($h in $handlers) {
    if ($mainCs -notmatch "(void|async\s+void|Task|async\s+Task)\s+$h\s*\(") {
        $missingHandlers += $h
    }
}
Write-Host "Unique event handlers in MainWindow.xaml: $($handlers.Count)"
Write-Host "Missing handlers in code-behind: $($missingHandlers.Count)"
if ($missingHandlers.Count -gt 0) {
    Write-Host "FAIL: Missing event handlers: $($missingHandlers -join ', ')"
} else {
    Write-Host "PASS: 100% of event handlers are defined and wired in MainWindow.xaml.cs."
}

$requiredTags = @(
    'Quiet', 'Default', 'Extreme', 'Turbo',
    'Aggressive', 'Efficient', 'Disabled',
    'CPU_TEMP', 'GPU_TEMP', 'CPU_WATT', 'GPU_WATT', 'CPU_GHZ', 'CPU_USAGE', 'GPU_USAGE', 'RAM_USAGE', 'VRAM_GB',
    '#00E5FF', '#FF2A55', '#00FFA3', '#BD00FF', '#FFAA00', '#FFFFFF', '#000000',
    '14', '30',
    'Auto', 'Max'
)
$missingTags = @()
foreach ($t in $requiredTags) {
    if (-not $mainXaml.Contains("Tag=""$t""")) {
        $missingTags += $t
    }
}
Write-Host "Required hardware tags: $($requiredTags.Count)"
Write-Host "Missing hardware tags: $($missingTags.Count)"
if ($missingTags.Count -gt 0) {
    Write-Host "FAIL: Missing hardware tags: $($missingTags -join ', ')"
} else {
    Write-Host "PASS: 100% of required hardware tags exist in MainWindow.xaml."
}

Write-Host "========================================================"
Write-Host "           VICTORY AUDIT SUITE EXECUTION DONE           "
Write-Host "========================================================"
