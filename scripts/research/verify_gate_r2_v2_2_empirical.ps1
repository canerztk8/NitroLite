# Empirical Verification Script for Challenger Gate R2 V2.2
# Author: challenger_gate_r2_v2_2
# Date: 2026-11-04

$ErrorActionPreference = "Stop"
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent

Write-Host "=================================================================" -ForegroundColor Cyan
Write-Host "  CHALLENGER GATE R2 V2.2 EMPIRICAL VERIFICATION HARNESS          " -ForegroundColor Cyan
Write-Host "=================================================================" -ForegroundColor Cyan

$mainXamlPath = Join-Path $root "NitroLite\MainWindow.xaml"
$mainCsPath = Join-Path $root "NitroLite\MainWindow.xaml.cs"
$osdXamlPath = Join-Path $root "NitroLite\OsdOverlayWindow.xaml"
$osdCsPath = Join-Path $root "NitroLite\OsdOverlayWindow.xaml.cs"
$surveyPath = Join-Path $root ".agents\teamwork\explorer_integrity_survey_1\survey_integrity_report.md"
$testProjPath = Join-Path $root "NitroLite.Tests\NitroLite.Tests.csproj"

$mainXaml = Get-Content $mainXamlPath -Raw
$mainCs = Get-Content $mainCsPath -Raw
$osdXaml = Get-Content $osdXamlPath -Raw
$osdCs = Get-Content $osdCsPath -Raw

$allPassed = $true

# -------------------------------------------------------------
# 1. Verification of x:Name controls in MainWindow.xaml and OsdOverlayWindow.xaml
# -------------------------------------------------------------
Write-Host "`n[STEP 1] Validating x:Name controls against survey inventory..." -ForegroundColor Yellow

$surveyLines = Get-Content $surveyPath
$surveyControls = [System.Collections.Generic.List[string]]::new()
for ($i = 27; $i -le 165; $i++) {
    $line = $surveyLines[$i]
    $parts = $line.Split('|')
    if ($parts.Length -ge 4) {
        $name = $parts[2].Trim().Trim('`')
        if ($name -match '^[A-Za-z0-9_]+$') {
            $surveyControls.Add($name)
        }
    }
}

$missingMain = [System.Collections.Generic.List[string]]::new()
foreach ($ctrl in $surveyControls) {
    if (-not $mainXaml.Contains("x:Name=""$ctrl""")) {
        $missingMain.Add($ctrl)
    }
}

$osdExpected = @(
    'TxtOsdCpuTemp', 'TxtOsdCpuPower', 'TxtOsdCpuFreq', 'TxtOsdCpuUsage',
    'TxtOsdGpuTemp', 'TxtOsdGpuPower', 'TxtOsdGpuFreq', 'TxtOsdVram',
    'TxtOsdMode', 'TxtOsdRam', 'TxtOsdFan'
)
$missingOsd = [System.Collections.Generic.List[string]]::new()
foreach ($ctrl in $osdExpected) {
    if (-not $osdXaml.Contains("x:Name=""$ctrl""")) {
        $missingOsd.Add($ctrl)
    }
}

# Also count total x:Name declarations in MainWindow.xaml
$allMainNames = [regex]::Matches($mainXaml, 'x:Name="([^"]+)"') | ForEach-Object { $_.Groups[1].Value }
$uniqueMainNames = $allMainNames | Select-Object -Unique
$dupGroups = $allMainNames | Group-Object | Where-Object { $_.Count -gt 1 }
foreach ($dg in $dupGroups) {
    Write-Host "  Note: '$($dg.Name)' appears $($dg.Count) times (template-scoped)."
}

Write-Host "  Survey controls in scope: $($surveyControls.Count)"
Write-Host "  Total x:Name attributes in MainWindow.xaml: $($allMainNames.Count)"
Write-Host "  Unique x:Name attributes in MainWindow.xaml: $($uniqueMainNames.Count)"
Write-Host "  Missing MainWindow controls: $($missingMain.Count)"
Write-Host "  Missing OsdOverlayWindow controls: $($missingOsd.Count)"

if ($missingMain.Count -eq 0 -and $missingOsd.Count -eq 0) {
    Write-Host "  -> STEP 1 PASS: All $($surveyControls.Count) MainWindow controls and $($osdExpected.Count) OSD controls are present." -ForegroundColor Green
} else {
    $allPassed = $false
    Write-Host "  -> STEP 1 FAIL: Missing controls detected!" -ForegroundColor Red
}

# -------------------------------------------------------------
# 2. Event Handlers verification
# -------------------------------------------------------------
Write-Host "`n[STEP 2] Validating Event Handlers in MainWindow.xaml..." -ForegroundColor Yellow

$eventPattern = '\b(?:Click|Checked|Unchecked|SelectionChanged|ValueChanged|MouseLeftButtonDown|Loaded|Closing|Closed)="([^"]+)"'
$eventBindings = [regex]::Matches($mainXaml, $eventPattern)
$uniqueHandlers = [System.Collections.Generic.HashSet[string]]::new()
foreach ($eb in $eventBindings) {
    $uniqueHandlers.Add($eb.Groups[1].Value) | Out-Null
}

$missingHandlers = [System.Collections.Generic.List[string]]::new()
foreach ($h in $uniqueHandlers) {
    if (-not ($mainCs -match '\b' + [regex]::Escape($h) + '\s*\(')) {
        $missingHandlers.Add($h)
    }
}

Write-Host "  Found $($eventBindings.Count) event bindings across $($uniqueHandlers.Count) unique methods."
if ($missingHandlers.Count -eq 0) {
    Write-Host "  -> STEP 2 PASS: All $($uniqueHandlers.Count) event handler methods exist in MainWindow.xaml.cs." -ForegroundColor Green
} else {
    $allPassed = $false
    Write-Host "  -> STEP 2 FAIL: Missing event handlers: $($missingHandlers -join ', ')" -ForegroundColor Red
}

# -------------------------------------------------------------
# 3. Hardware Tag properties verification
# -------------------------------------------------------------
Write-Host "`n[STEP 3] Validating 27 Hardware Tag Properties..." -ForegroundColor Yellow

$tagsToCheck = @(
    'Quiet', 'Default', 'Extreme', 'Turbo',
    'Aggressive', 'Efficient', 'Disabled',
    'Auto', 'Max',
    'CPU_TEMP', 'GPU_TEMP', 'CPU_WATT', 'GPU_WATT', 'CPU_GHZ', 'CPU_USAGE', 'GPU_USAGE', 'RAM_USAGE', 'VRAM_GB',
    '#00E5FF', '#FF2A55', '#00FFA3', '#BD00FF', '#FFAA00', '#FFFFFF', '#000000',
    '14', '30'
)

$missingTags = [System.Collections.Generic.List[string]]::new()
foreach ($tag in $tagsToCheck) {
    if (-not $mainXaml.Contains("Tag=""$tag""")) {
        $missingTags.Add($tag)
    }
}

if ($missingTags.Count -eq 0) {
    Write-Host "  -> STEP 3 PASS: All 27 hardware Tag attributes are present in MainWindow.xaml." -ForegroundColor Green
} else {
    $allPassed = $false
    Write-Host "  -> STEP 3 FAIL: Missing Tags: $($missingTags -join ', ')" -ForegroundColor Red
}

# -------------------------------------------------------------
# 4. Dynamic SolidColorBrush keys in Window.Resources
# -------------------------------------------------------------
Write-Host "`n[STEP 4] Validating 5 Dynamic SolidColorBrush keys..." -ForegroundColor Yellow

$reqBrushes = @('AccentGreen', 'AccentCyan', 'AccentRed', 'TextSecondary', 'TextMuted')
$resStart = $mainXaml.IndexOf('<Window.Resources>')
$resEnd = $mainXaml.IndexOf('</Window.Resources>')
$resBlock = $mainXaml.Substring($resStart, $resEnd - $resStart + '</Window.Resources>'.Length)

$missingBrushes = [System.Collections.Generic.List[string]]::new()
foreach ($b in $reqBrushes) {
    if (-not ($resBlock -match '<SolidColorBrush\s+x:Key="' + $b + '"')) {
        $missingBrushes.Add($b)
    }
}

# Also verify that every FindResource call in MainWindow.xaml.cs resolves to an existing resource key
$allCsLines = Get-Content $mainCsPath
$missingCsKeys = [System.Collections.Generic.List[string]]::new()
foreach ($line in $allCsLines) {
    if ($line -match 'FindResource\(') {
        $sm = [regex]::Matches($line, '"([A-Za-z0-9_]+)"')
        foreach ($m in $sm) {
            $key = $m.Groups[1].Value
            if (-not ($resBlock -match 'x:Key="' + $key + '"')) {
                $missingCsKeys.Add($key)
            }
        }
    }
}

if ($missingBrushes.Count -eq 0 -and $missingCsKeys.Count -eq 0) {
    Write-Host "  -> STEP 4 PASS: All 5 dynamic SolidColorBrush keys exist and 100% of FindResource calls map cleanly to Window.Resources." -ForegroundColor Green
} else {
    $allPassed = $false
    Write-Host "  -> STEP 4 FAIL: Missing brushes: $($missingBrushes -join ', ') | Missing code-behind keys: $($missingCsKeys -join ', ')" -ForegroundColor Red
}

# -------------------------------------------------------------
# 5. Uppercase prefix tokens in CmbRgbEffect
# -------------------------------------------------------------
Write-Host "`n[STEP 5] Validating 9 Uppercase Prefix Tokens in CmbRgbEffect..." -ForegroundColor Yellow

$cmbStart = $mainXaml.IndexOf('x:Name="CmbRgbEffect"')
$cmbEnd = $mainXaml.IndexOf('</ComboBox>', $cmbStart)
$cmbBlock = $mainXaml.Substring($cmbStart, $cmbEnd - $cmbStart + '</ComboBox>'.Length)

$itemMatches = [regex]::Matches($cmbBlock, '<ComboBoxItem[^>]*Content="([^"]+)"')
$foundTokens = [System.Collections.Generic.List[string]]::new()
foreach ($im in $itemMatches) {
    $tok = $im.Groups[1].Value.Trim().Split(' ')[0]
    $foundTokens.Add($tok)
}

$reqTokens = @('STATIC', 'WAVE', 'BREATHING', 'NEON', 'SHIFTING', 'ZOOM', 'METEOR', 'TWINKLING', 'OFF')
$missingTokens = [System.Collections.Generic.List[string]]::new()
foreach ($rt in $reqTokens) {
    if (-not $foundTokens.Contains($rt)) {
        $missingTokens.Add($rt)
    }
}

Write-Host "  Found tokens in CmbRgbEffect: $($foundTokens -join ', ')"
if ($missingTokens.Count -eq 0) {
    Write-Host "  -> STEP 5 PASS: All 9 uppercase prefix tokens are present and intact." -ForegroundColor Green
} else {
    $allPassed = $false
    Write-Host "  -> STEP 5 FAIL: Missing tokens: $($missingTokens -join ', ')" -ForegroundColor Red
}

# -------------------------------------------------------------
# 6. Automated Unit Tests (xUnit)
# -------------------------------------------------------------
Write-Host "`n[STEP 6] Running dotnet test suite..." -ForegroundColor Yellow

$testOut = dotnet test $testProjPath --nologo 2>&1
$testStr = $testOut -join "`n"
Write-Host $testStr

if ($LASTEXITCODE -eq 0 -and $testStr -match 'Total:\s*201' -and $testStr -match 'Passed:\s*201' -and $testStr -match 'Failed:\s*0') {
    Write-Host "  -> STEP 6 PASS: All 201 tests passed cleanly." -ForegroundColor Green
} else {
    $allPassed = $false
    Write-Host "  -> STEP 6 FAIL: dotnet test failed or did not report 201 passed tests!" -ForegroundColor Red
}

# -------------------------------------------------------------
# 7. Standalone Console Runner Test Execution
# -------------------------------------------------------------
Write-Host "`n[STEP 7] Running dotnet run (Console Test Runner)..." -ForegroundColor Yellow

$runOut = dotnet run --project $testProjPath --nologo 2>&1
$runStr = $runOut -join "`n"
Write-Host $runStr

if ($LASTEXITCODE -eq 0 -and $runStr -match 'ALL TESTS PASSED SUCCESSFULLY! \(0 failures\)') {
    Write-Host "  -> STEP 7 PASS: Standalone console runner passed with 0 failures." -ForegroundColor Green
} else {
    $allPassed = $false
    Write-Host "  -> STEP 7 FAIL: Console runner failed!" -ForegroundColor Red
}

# -------------------------------------------------------------
# 8. Slop & Anti-Pattern Deep Check
# -------------------------------------------------------------
Write-Host "`n[STEP 8] Validating absence of vibecoding patterns and buzzwords..." -ForegroundColor Yellow

$buzzwords = 'Akıllı|Mükemmel|Harika|Stüdyo|RAM Yiyor|Proaktif|Tebrikler|Sihirli'
$buzzMatches = Get-ChildItem -Path (Join-Path $root "NitroLite"), (Join-Path $root "NitroLite.Tests") -Recurse -Include *.cs,*.xaml | Select-String -Pattern $buzzwords

$emojis = [regex]::Matches($mainXaml + $osdXaml, '[\u2600-\u270F\u2716-\u27BF\uD83C-\uDBFF\uDC00-\uDFFF]')
$gradients = [regex]::Matches($mainXaml + $osdXaml, 'LinearGradientBrush|RadialGradientBrush|BlurEffect')

Write-Host "  Buzzword matches count: $($buzzMatches.Count)"
Write-Host "  Emoji matches count: $($emojis.Count)"
Write-Host "  Gradient/Blur matches count: $($gradients.Count)"

if ($buzzMatches.Count -eq 0 -and $emojis.Count -eq 0 -and $gradients.Count -eq 0) {
    Write-Host "  -> STEP 8 PASS: Zero buzzwords, zero emojis, zero gradients found." -ForegroundColor Green
} else {
    $allPassed = $false
    Write-Host "  -> STEP 8 FAIL: Anti-patterns detected!" -ForegroundColor Red
}

# -------------------------------------------------------------
# Final Verdict
# -------------------------------------------------------------
Write-Host "`n=================================================================" -ForegroundColor Cyan
if ($allPassed) {
    Write-Host "  EMPIRICAL VERDICT: APPROVE (100% Zero-Regression Confirmed)   " -ForegroundColor Green
    Write-Host "=================================================================" -ForegroundColor Cyan
    exit 0
} else {
    Write-Host "  EMPIRICAL VERDICT: REQUEST_CHANGES (Regressions Found)        " -ForegroundColor Red
    Write-Host "=================================================================" -ForegroundColor Cyan
    exit 1
}
