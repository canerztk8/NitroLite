# Empirical Verification & Stress Test Harness for NitroLite Gate V2_2
# Tests: x:Name bindings, event handlers, Tag attributes, Dynamic Brushes, RGB tokens, and Unit Test Suite.
$ErrorActionPreference = "Stop"

$workspaceRoot = $PSScriptRoot
if (-not $workspaceRoot) { $workspaceRoot = Get-Location }

$mainXamlPath = Join-Path $workspaceRoot "NitroLite\MainWindow.xaml"
$mainCsPath = Join-Path $workspaceRoot "NitroLite\MainWindow.xaml.cs"
$osdXamlPath = Join-Path $workspaceRoot "NitroLite\OsdOverlayWindow.xaml"
$osdCsPath = Join-Path $workspaceRoot "NitroLite\OsdOverlayWindow.xaml.cs"
$surveyPath = Join-Path $workspaceRoot ".agents\teamwork\explorer_integrity_survey_1\survey_integrity_report.md"
$testProjPath = Join-Path $workspaceRoot "NitroLite.Tests\NitroLite.Tests.csproj"

Write-Host "=================================================================" -ForegroundColor Cyan
Write-Host "  EMPIRICAL CHALLENGER GATE V2_2: ZERO-REGRESSION INTEGRITY TEST  " -ForegroundColor Cyan
Write-Host "=================================================================" -ForegroundColor Cyan

$allPass = $true

# -------------------------------------------------------------------------
# Test 1: Validate x:Name in MainWindow.xaml (136/138) and OsdOverlayWindow.xaml (11)
# -------------------------------------------------------------------------
Write-Host "`n[TEST 1] Validating x:Name fields..." -ForegroundColor Yellow

$surveyLines = Get-Content $surveyPath
$mainControls = [System.Collections.Generic.List[string]]::new()
for ($i = 27; $i -le 165; $i++) {
    $line = $surveyLines[$i]
    $parts = $line.Split('|')
    if ($parts.Length -ge 4) {
        $name = $parts[2].Trim().Trim('`')
        if ($name -match '^[A-Za-z0-9_]+$') {
            $mainControls.Add($name)
        }
    }
}

$mainXaml = Get-Content $mainXamlPath -Raw
$missingMain = [System.Collections.Generic.List[string]]::new()
foreach ($ctrl in $mainControls) {
    if (-not $mainXaml.Contains("x:Name=""$ctrl""")) {
        $missingMain.Add($ctrl)
    }
}

$osdExpected = @('TxtOsdCpuTemp', 'TxtOsdCpuPower', 'TxtOsdCpuFreq', 'TxtOsdCpuUsage', 'TxtOsdGpuTemp', 'TxtOsdGpuPower', 'TxtOsdGpuFreq', 'TxtOsdVram', 'TxtOsdMode', 'TxtOsdRam', 'TxtOsdFan')
$osdXaml = Get-Content $osdXamlPath -Raw
$missingOsd = [System.Collections.Generic.List[string]]::new()
foreach ($ctrl in $osdExpected) {
    if (-not $osdXaml.Contains("x:Name=""$ctrl""")) {
        $missingOsd.Add($ctrl)
    }
}

if ($missingMain.Count -eq 0 -and $missingOsd.Count -eq 0) {
    Write-Host "  PASS: MainWindow.xaml contains all $($mainControls.Count) controls from survey (target >= 136)." -ForegroundColor Green
    Write-Host "  PASS: OsdOverlayWindow.xaml contains all $($osdExpected.Count) telemetry controls." -ForegroundColor Green
} else {
    $allPass = $false
    Write-Host "  FAIL: Missing in MainWindow: $($missingMain -join ', ')" -ForegroundColor Red
    Write-Host "  FAIL: Missing in OsdOverlayWindow: $($missingOsd -join ', ')" -ForegroundColor Red
}

# -------------------------------------------------------------------------
# Test 2: Validate Event Handlers in MainWindow.xaml mapped to MainWindow.xaml.cs
# -------------------------------------------------------------------------
Write-Host "`n[TEST 2] Validating Event Handlers..." -ForegroundColor Yellow

$mainCs = Get-Content $mainCsPath -Raw
$eventRegex = '\b(?:Click|Checked|Unchecked|SelectionChanged|ValueChanged|MouseLeftButtonDown|Loaded|Closing|Closed)="([^"]+)"'
$eventMatches = [regex]::Matches($mainXaml, $eventRegex)

$uniqueHandlers = [System.Collections.Generic.HashSet[string]]::new()
$missingMethods = [System.Collections.Generic.List[string]]::new()

foreach ($m in $eventMatches) {
    $handler = $m.Groups[1].Value
    $uniqueHandlers.Add($handler) | Out-Null
}

foreach ($handler in $uniqueHandlers) {
    # Method pattern in C#
    if (-not ($mainCs -match '\b' + [regex]::Escape($handler) + '\s*\(')) {
        $missingMethods.Add($handler)
    }
}

Write-Host "  Found $($eventMatches.Count) event handler bindings referencing $($uniqueHandlers.Count) unique methods."
if ($missingMethods.Count -eq 0) {
    Write-Host "  PASS: All $($uniqueHandlers.Count) event handler methods map directly to existing methods in MainWindow.xaml.cs." -ForegroundColor Green
} else {
    $allPass = $false
    Write-Host "  FAIL: Missing methods in code-behind: $($missingMethods -join ', ')" -ForegroundColor Red
}

# -------------------------------------------------------------------------
# Test 3: Validate all 27 required Tag attributes
# -------------------------------------------------------------------------
Write-Host "`n[TEST 3] Validating 27 required Tag attributes..." -ForegroundColor Yellow

$requiredTags = @(
    'Quiet', 'Default', 'Extreme', 'Turbo',
    'Aggressive', 'Efficient', 'Disabled',
    'Auto', 'Max',
    'CPU_TEMP', 'GPU_TEMP', 'CPU_WATT', 'GPU_WATT', 'CPU_GHZ', 'CPU_USAGE', 'GPU_USAGE', 'RAM_USAGE', 'VRAM_GB',
    '#00E5FF', '#FF2A55', '#00FFA3', '#BD00FF', '#FFAA00', '#FFFFFF', '#000000',
    '14', '30'
)

$missingTags = [System.Collections.Generic.List[string]]::new()
foreach ($tag in $requiredTags) {
    if (-not $mainXaml.Contains("Tag=""$tag""")) {
        $missingTags.Add($tag)
    }
}

if ($missingTags.Count -eq 0) {
    Write-Host "  PASS: All 27 required Tag attributes are present in MainWindow.xaml." -ForegroundColor Green
} else {
    $allPass = $false
    Write-Host "  FAIL: Missing Tag attributes: $($missingTags -join ', ')" -ForegroundColor Red
}

# -------------------------------------------------------------------------
# Test 4: Validate all 5 dynamic SolidColorBrush keys in Window.Resources
# -------------------------------------------------------------------------
Write-Host "`n[TEST 4] Validating 5 Dynamic SolidColorBrush keys in Window.Resources..." -ForegroundColor Yellow

$requiredBrushes = @('AccentGreen', 'AccentCyan', 'AccentRed', 'TextSecondary', 'TextMuted')
$resStart = $mainXaml.IndexOf('<Window.Resources>')
$resEnd = $mainXaml.IndexOf('</Window.Resources>')
$resBlock = $mainXaml.Substring($resStart, $resEnd - $resStart + '</Window.Resources>'.Length)

$missingBrushes = [System.Collections.Generic.List[string]]::new()
foreach ($brush in $requiredBrushes) {
    $pat = '<SolidColorBrush\s+x:Key="' + $brush + '"'
    if (-not ($resBlock -match $pat)) {
        $missingBrushes.Add($brush)
    }
}

if ($missingBrushes.Count -eq 0) {
    Write-Host "  PASS: All 5 dynamic SolidColorBrush keys (AccentGreen, AccentCyan, AccentRed, TextSecondary, TextMuted) exist in Window.Resources." -ForegroundColor Green
} else {
    $allPass = $false
    Write-Host "  FAIL: Missing brushes: $($missingBrushes -join ', ')" -ForegroundColor Red
}

# -------------------------------------------------------------------------
# Test 5: Validate all 9 uppercase prefix tokens in CmbRgbEffect
# -------------------------------------------------------------------------
Write-Host "`n[TEST 5] Validating 9 uppercase prefix tokens in CmbRgbEffect..." -ForegroundColor Yellow

$cmbStart = $mainXaml.IndexOf('x:Name="CmbRgbEffect"')
$cmbEnd = $mainXaml.IndexOf('</ComboBox>', $cmbStart)
$cmbBlock = $mainXaml.Substring($cmbStart, $cmbEnd - $cmbStart + '</ComboBox>'.Length)

$itemMatches = [regex]::Matches($cmbBlock, '<ComboBoxItem[^>]*Content="([^"]+)"')
$foundTokens = [System.Collections.Generic.List[string]]::new()
foreach ($im in $itemMatches) {
    $tok = $im.Groups[1].Value.Trim().Split(' ')[0]
    $foundTokens.Add($tok)
}

$requiredTokens = @('STATIC', 'WAVE', 'BREATHING', 'NEON', 'SHIFTING', 'ZOOM', 'METEOR', 'TWINKLING', 'OFF')
$missingTokens = [System.Collections.Generic.List[string]]::new()
foreach ($rt in $requiredTokens) {
    if (-not $foundTokens.Contains($rt)) {
        $missingTokens.Add($rt)
    }
}

if ($missingTokens.Count -eq 0) {
    Write-Host "  PASS: All 9 uppercase prefix tokens (STATIC, WAVE, BREATHING, NEON, SHIFTING, ZOOM, METEOR, TWINKLING, OFF) are present in CmbRgbEffect." -ForegroundColor Green
} else {
    $allPass = $false
    Write-Host "  FAIL: Missing tokens: $($missingTokens -join ', ')" -ForegroundColor Red
}

# -------------------------------------------------------------------------
# Test 6: Run dotnet test and verify 201 tests pass cleanly
# -------------------------------------------------------------------------
Write-Host "`n[TEST 6] Running dotnet test suite..." -ForegroundColor Yellow

$testOutput = dotnet test $testProjPath --nologo 2>&1
$testOutputStr = $testOutput -join "`n"
Write-Host $testOutputStr

if ($LASTEXITCODE -eq 0 -and $testOutputStr -match 'Total:\s*201' -and $testOutputStr -match 'Passed:\s*201' -and $testOutputStr -match 'Failed:\s*0') {
    Write-Host "  PASS: All 201 automated unit tests passed cleanly with 0 failures." -ForegroundColor Green
} else {
    $allPass = $false
    Write-Host "  FAIL: dotnet test failed or did not report 201 passed tests!" -ForegroundColor Red
}

# -------------------------------------------------------------------------
# Overall Verdict
# -------------------------------------------------------------------------
Write-Host "`n=================================================================" -ForegroundColor Cyan
if ($allPass) {
    Write-Host "  FINAL VERDICT: APPROVE (Zero Regressions Confirmed)           " -ForegroundColor Green
    Write-Host "=================================================================" -ForegroundColor Cyan
    exit 0
} else {
    Write-Host "  FINAL VERDICT: REQUEST_CHANGES (Regressions Detected)         " -ForegroundColor Red
    Write-Host "=================================================================" -ForegroundColor Cyan
    exit 1
}
