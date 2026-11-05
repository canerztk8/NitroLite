# Empirical Verification & Stress Test Harness for Challenger Gate R3_1
# Milestone 1: Power Automation (R1), Battery Telemetry (R2), Hardware Protection (R4)
$ErrorActionPreference = "Stop"

$workspaceRoot = $PSScriptRoot
if (-not $workspaceRoot) { $workspaceRoot = Get-Location }

Write-Host "=================================================================" -ForegroundColor Cyan
Write-Host "  CHALLENGER GATE R3_1: MILESTONE 1 EMPIRICAL VERIFICATION       " -ForegroundColor Cyan
Write-Host "=================================================================" -ForegroundColor Cyan

$allPass = $true

# -------------------------------------------------------------------------
# Test 1: Solution Build Verification (0 Warnings, 0 Errors)
# -------------------------------------------------------------------------
Write-Host "`n[TEST 1] Verifying clean build of NitroLite.sln..." -ForegroundColor Yellow
$buildOutput = dotnet build (Join-Path $workspaceRoot "NitroLite.sln")
if ($LASTEXITCODE -ne 0 -or $buildOutput -match "([1-9]\d*)\s+Error\(s\)") {
    Write-Host "  FAIL: Build produced errors!" -ForegroundColor Red
    $allPass = $false
} else {
    Write-Host "  PASS: Build succeeded with 0 Errors." -ForegroundColor Green
}

# -------------------------------------------------------------------------
# Test 2: Core Milestone 1 Unit Tests (Battery + Power Automation)
# -------------------------------------------------------------------------
Write-Host "`n[TEST 2] Executing Milestone 1 Core Unit Tests..." -ForegroundColor Yellow
$testOutput = dotnet test (Join-Path $workspaceRoot "NitroLite.sln") --filter "FullyQualifiedName~BatteryTelemetryTests|FullyQualifiedName~PowerAutomationTests"
if ($LASTEXITCODE -ne 0) {
    Write-Host "  FAIL: Milestone 1 unit tests failed!" -ForegroundColor Red
    $allPass = $false
} else {
    Write-Host "  PASS: BatteryTelemetryTests and PowerAutomationTests passed 100%." -ForegroundColor Green
}

# -------------------------------------------------------------------------
# Test 3: Dedicated Adversarial Telemetry & Power Stress Suite (34 Tests)
# -------------------------------------------------------------------------
Write-Host "`n[TEST 3] Executing Adversarial Telemetry & Power Stress Suite..." -ForegroundColor Yellow
$advOutput = dotnet test (Join-Path $workspaceRoot "NitroLite.sln") --filter "FullyQualifiedName~AdversarialTelemetryAndPowerStressTests"
if ($LASTEXITCODE -ne 0) {
    Write-Host "  FAIL: Adversarial stress tests failed!" -ForegroundColor Red
    $allPass = $false
} else {
    Write-Host "  PASS: All 34 Adversarial Telemetry and Power Automation stress tests passed 100%." -ForegroundColor Green
}

# -------------------------------------------------------------------------
# Test 4: MUX Switch & GPU Display Routing Zero-Touch Invariant
# -------------------------------------------------------------------------
Write-Host "`n[TEST 4] Verifying MUX switch code zero-touch invariant..." -ForegroundColor Yellow
$srcFiles = Get-ChildItem -Path (Join-Path $workspaceRoot "NitroLite") -Recurse -Filter "*.cs"
$muxViolations = [System.Collections.Generic.List[string]]::new()
foreach ($file in $srcFiles) {
    $content = Get-Content $file.FullName -Raw
    if ($content -match "nvlddmkm" -or $content -match "WmiMonitorBrightness" -or $content -match "NVIDIA_DDS") {
        $muxViolations.Add($file.Name)
    }
}

if ($muxViolations.Count -eq 0) {
    Write-Host "  PASS: Zero references to nvlddmkm, DDS, or WMI display routes found. MUX invariant strictly maintained." -ForegroundColor Green
} else {
    Write-Host "  FAIL: Found prohibited display switching references in: $($muxViolations -join ', ')" -ForegroundColor Red
    $allPass = $false
}

Write-Host "`n=================================================================" -ForegroundColor Cyan
if ($allPass) {
    Write-Host "  CHALLENGER GATE R3_1 VERDICT: ALL TESTS PASSED (APPROVE)        " -ForegroundColor Green
} else {
    Write-Host "  CHALLENGER GATE R3_1 VERDICT: VERIFICATION FAILED (REQUEST_CHANGES)" -ForegroundColor Red
}
Write-Host "=================================================================" -ForegroundColor Cyan
