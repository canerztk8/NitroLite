# verify_r2_audit.ps1
# Comprehensive empirical verification script for Challenger R2_1

$reportPath = "c:\Users\caner\Documents\antigravity\eager-shannon\nitro_sense_analysis.md"
$content = Get-Content -LiteralPath $reportPath -Raw
$lines = Get-Content -LiteralPath $reportPath

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "PART 1: ARITHMETIC VERIFICATION" -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

# 1. Parse Table 2.1 rows
$tableRows = $lines | Where-Object { $_ -match '^\|\s*\*\*\d+\*\*\s*\|' }
Write-Host "Found $($tableRows.Count) process rows in Table 2.1"

$procs = @()
$totalWS = 0.0
$totalPriv = 0.0
$totalHandles = 0
$totalThreads = 0

foreach ($r in $tableRows) {
    $cols = $r.Trim().Trim('|').Split('|') | ForEach-Object { $_.Trim() }
    $procId = [int]($cols[0] -replace '\*','')
    $name = $cols[1] -replace '`',''
    $ws = [double]($cols[2] -replace '\*','')
    $priv = [double]($cols[3] -replace '\*','')
    $handles = [int]($cols[4] -replace '\*','')
    $threads = [int]($cols[5] -replace '\*','')
    
    $totalWS += $ws
    $totalPriv += $priv
    $totalHandles += $handles
    $totalThreads += $threads
    
    $procs += [PSCustomObject]@{
        PID = $procId
        Name = $name
        WS = $ws
        Priv = $priv
        Handles = $handles
        Threads = $threads
        Cols = $cols
    }
}

Write-Host "Table 2.1 Calculated Sums:"
Write-Host "  Processes: $($procs.Count)"
Write-Host "  Working Set: $([math]::Round($totalWS, 2)) MB"
Write-Host "  Private Mem: $([math]::Round($totalPriv, 2)) MB"
Write-Host "  Handles:     $totalHandles"
Write-Host "  Threads:     $totalThreads"

# Compare with Table 2.1 Total Row
$totalRow = $lines | Where-Object { $_ -match '^\|\s*\*\*TOTAL\*\*\s*\|' }
Write-Host "Table 2.1 Reported Total Row:`n  $totalRow"

# Parse UI processes (14900, 16352, 5712, 2456)
$uiPids = @(14900, 16352, 5712, 2456)
$uiProcs = $procs | Where-Object { $uiPids -contains $_.PID }
$uiWS = ($uiProcs | Measure-Object -Property WS -Sum).Sum
$uiPriv = ($uiProcs | Measure-Object -Property Priv -Sum).Sum
Write-Host "`nUI Subtotals (PIDs 14900, 16352, 5712, 2456):"
Write-Host "  Count: $($uiProcs.Count)"
Write-Host "  WS:   $([math]::Round($uiWS, 2)) MB (Report claims: 485.55 MB)"
Write-Host "  Priv: $([math]::Round($uiPriv, 2)) MB (Report claims: 636.32 MB)"
Write-Host "  WS % of Total: $([math]::Round(($uiWS / $totalWS) * 100, 1))% (Report claims: 59.0%)"
Write-Host "  Priv % of Total: $([math]::Round(($uiPriv / $totalPriv) * 100, 1))% (Report claims: 82.9%)"

# Parse Background processes (17 processes)
$bgProcs = $procs | Where-Object { $uiPids -notcontains $_.PID }
$bgWS = ($bgProcs | Measure-Object -Property WS -Sum).Sum
$bgPriv = ($bgProcs | Measure-Object -Property Priv -Sum).Sum
Write-Host "`nBackground Subtotals (17 processes):"
Write-Host "  Count: $($bgProcs.Count)"
Write-Host "  WS:   $([math]::Round($bgWS, 2)) MB (Report claims: 337.98 MB)"
Write-Host "  Priv: $([math]::Round($bgPriv, 2)) MB (Report claims: 131.03 MB)"
Write-Host "  WS % of Total: $([math]::Round(($bgWS / $totalWS) * 100, 1))% (Report claims: 41.0%)"

# Tier 1 processes (10 processes: 6780, 4796, 11064, 8664, 4804, 4720, 4756, 19772, 11564, 4712)
$tier1Pids = @(6780, 4796, 11064, 8664, 4804, 4720, 4756, 19772, 11564, 4712)
$tier1Procs = $procs | Where-Object { $tier1Pids -contains $_.PID }
$tier1WS = ($tier1Procs | Measure-Object -Property WS -Sum).Sum
$tier1Priv = ($tier1Procs | Measure-Object -Property Priv -Sum).Sum
Write-Host "`nTier 1 Subtotals (10 processes):"
Write-Host "  Count: $($tier1Procs.Count)"
Write-Host "  WS:   $([math]::Round($tier1WS, 2)) MB (Report claims: 213.51 MB)"
Write-Host "  Priv: $([math]::Round($tier1Priv, 2)) MB (Report claims: 99.13 MB)"
Write-Host "  WS % of Background: $([math]::Round(($tier1WS / $bgWS) * 100, 1))% (Report claims: 63.2%)"
Write-Host "  WS % of Total:      $([math]::Round(($tier1WS / $totalWS) * 100, 1))% (Report claims: 25.9%)"

# Tier 2 processes (2 processes: 4732, 7556)
$tier2Pids = @(4732, 7556)
$tier2Procs = $procs | Where-Object { $tier2Pids -contains $_.PID }
$tier2WS = ($tier2Procs | Measure-Object -Property WS -Sum).Sum
$tier2Priv = ($tier2Procs | Measure-Object -Property Priv -Sum).Sum
Write-Host "`nTier 2 Subtotals (2 processes):"
Write-Host "  Count: $($tier2Procs.Count)"
Write-Host "  WS:   $([math]::Round($tier2WS, 2)) MB (Report claims: 41.13 MB)"
Write-Host "  Priv: $([math]::Round($tier2Priv, 2)) MB (Report claims: 9.79 MB)"
Write-Host "  WS % of Background: $([math]::Round(($tier2WS / $bgWS) * 100, 1))% (Report claims: 12.2%)"

# Tier 3 processes (5 processes: 10912, 5108, 4696, 12336, 4704)
$tier3Pids = @(10912, 5108, 4696, 12336, 4704)
$tier3Procs = $procs | Where-Object { $tier3Pids -contains $_.PID }
$tier3WS = ($tier3Procs | Measure-Object -Property WS -Sum).Sum
$tier3Priv = ($tier3Procs | Measure-Object -Property Priv -Sum).Sum
Write-Host "`nTier 3 Subtotals (5 processes):"
Write-Host "  Count: $($tier3Procs.Count)"
Write-Host "  WS:   $([math]::Round($tier3WS, 2)) MB (Report claims: 83.34 MB)"
Write-Host "  Priv: $([math]::Round($tier3Priv, 2)) MB (Report claims: 22.11 MB)"
Write-Host "  WS % of Background: $([math]::Round(($tier3WS / $bgWS) * 100, 1))% (Report claims: 24.6% or 24.7%)"

# Sum of tiers check
Write-Host "`nSum of Tiers Check:"
Write-Host "  Tier 1 + Tier 2 + Tier 3 WS = $([math]::Round($tier1WS + $tier2WS + $tier3WS, 2)) MB (Matches BG WS: $([math]::Round($bgWS, 2)))"
Write-Host "  Tier 1 + Tier 2 + Tier 3 Priv = $([math]::Round($tier1Priv + $tier2Priv + $tier3Priv, 2)) MB (Matches BG Priv: $([math]::Round($bgPriv, 2)))"

# Profile 1: Tier 1 Safe Debloat
$p1SavedWS = $tier1WS
$p1SavedPriv = $tier1Priv
$p1RemWS = $totalWS - $p1SavedWS
$p1RemPriv = $totalPriv - $p1SavedPriv
Write-Host "`nProfile 1 (Tier 1 Safe Debloat):"
Write-Host "  Saved WS:   $([math]::Round($p1SavedWS, 2)) MB (Report claims: 213.51 MB)"
Write-Host "  Saved Priv: $([math]::Round($p1SavedPriv, 2)) MB (Report claims: 99.13 MB)"
Write-Host "  Rem WS:     $([math]::Round($p1RemWS, 2)) MB (Report claims: 610.02 MB)"
Write-Host "  Rem Priv:   $([math]::Round($p1RemPriv, 2)) MB (Report claims: 668.22 MB)"
Write-Host "  Saved WS %: $([math]::Round(($p1SavedWS / $totalWS) * 100, 1))% (Report claims: 25.9%)"

# Profile 2: Recommended Gamer Profile (Tier 1 + On-Demand UI)
$p2SavedWS = $tier1WS + $uiWS
$p2SavedPriv = $tier1Priv + $uiPriv
$p2RemWS = $totalWS - $p2SavedWS
$p2RemPriv = $totalPriv - $p2SavedPriv
Write-Host "`nProfile 2 (Tier 1 + On-Demand UI):"
Write-Host "  Saved WS:   $([math]::Round($p2SavedWS, 2)) MB (Report claims: 699.06 MB)"
Write-Host "  Saved Priv: $([math]::Round($p2SavedPriv, 2)) MB (Report claims: 735.45 MB)"
Write-Host "  Rem WS:     $([math]::Round($p2RemWS, 2)) MB (Report claims: 124.47 MB)"
Write-Host "  Rem Priv:   $([math]::Round($p2RemPriv, 2)) MB (Report claims: 31.90 MB)"
Write-Host "  Saved WS %: $([math]::Round(($p2SavedWS / $totalWS) * 100, 1))% (Report claims: 84.9%)"

# Profile 3: Max Aggressive Debloat (Tier 1 + Tier 2 + On-Demand UI)
$p3SavedWS = $tier1WS + $tier2WS + $uiWS
$p3SavedPriv = $tier1Priv + $tier2Priv + $uiPriv
$p3RemWS = $totalWS - $p3SavedWS
$p3RemPriv = $totalPriv - $p3SavedPriv
Write-Host "`nProfile 3 (Tier 1 + Tier 2 + On-Demand UI):"
Write-Host "  Saved WS:   $([math]::Round($p3SavedWS, 2)) MB (Report claims: 740.19 MB)"
Write-Host "  Saved Priv: $([math]::Round($p3SavedPriv, 2)) MB (Report claims: 745.24 MB)"
Write-Host "  Rem WS:     $([math]::Round($p3RemWS, 2)) MB (Report claims: 83.34 MB)"
Write-Host "  Rem Priv:   $([math]::Round($p3RemPriv, 2)) MB (Report claims: 22.11 MB)"
Write-Host "  Saved WS %: $([math]::Round(($p3SavedWS / $totalWS) * 100, 1))% (Report claims: 89.9%)"
Write-Host "  Saved % BG: $([math]::Round((($tier1WS + $tier2WS) / $bgWS) * 100, 1))% (Report claims: 75.3%)"

Write-Host "`n==========================================================" -ForegroundColor Cyan
Write-Host "PART 2: POWERSHELL AST SYNTAX VALIDATION (SECTIONS 6 & 7)" -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

# Extract Code Blocks
# Section 6.2 Script:
$s62Start = $lines | Select-String -Pattern "### 6.2 Method 1: Automated PowerShell Debloat"
$s62Lines = $lines[($s62Start.LineNumber - 1)..($s62Start.LineNumber + 55)]
$inCode = $false
$s62Code = @()
foreach ($l in $s62Lines) {
    if ($l -match '^```powershell') { $inCode = $true; continue }
    if ($inCode -and $l -match '^```') { $inCode = $false; break }
    if ($inCode) { $s62Code += $l }
}
$s62Script = $s62Code -join "`n"

# Section 6.3 Script:
$s63Start = $lines | Select-String -Pattern "#### PowerShell Configuration:"
$s63Lines = $lines[($s63Start.LineNumber - 1)..($s63Start.LineNumber + 15)]
$inCode = $false
$s63Code = @()
foreach ($l in $s63Lines) {
    if ($l -match '^```powershell') { $inCode = $true; continue }
    if ($inCode -and $l -match '^```') { $inCode = $false; break }
    if ($inCode) { $s63Code += $l }
}
$s63Script = $s63Code -join "`n"

# Section 7.1 Script:
$s71Start = $lines | Select-String -Pattern "### 7.1 Method 1: One-Click PowerShell Factory Rollback"
$s71Lines = $lines[($s71Start.LineNumber - 1)..($s71Start.LineNumber + 55)]
$inCode = $false
$s71Code = @()
foreach ($l in $s71Lines) {
    if ($l -match '^```powershell') { $inCode = $true; continue }
    if ($inCode -and $l -match '^```') { $inCode = $false; break }
    if ($inCode) { $s71Code += $l }
}
$s71Script = $s71Code -join "`n"

# Section 7.3 Script:
$s73Start = $lines | Select-String -Pattern "### 7.3 Post-Restoration Verification Checklist"
$s73Lines = $lines[($s73Start.LineNumber - 1)..($s73Start.LineNumber + 15)]
$inCode = $false
$s73Code = @()
foreach ($l in $s73Lines) {
    if ($l -match '^```powershell') { $inCode = $true; continue }
    if ($inCode -and $l -match '^```') { $inCode = $false; break }
    if ($inCode) { $s73Code += $l }
}
$s73Script = $s73Code -join "`n"

function Test-PsAst($name, $codeText) {
    Write-Host "Validating AST for: $name"
    $tokens = $null
    $errors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseInput($codeText, [ref]$tokens, [ref]$errors)
    Write-Host "  Token Count: $($tokens.Count)"
    Write-Host "  Error Count: $($errors.Count)"
    if ($errors.Count -gt 0) {
        foreach ($e in $errors) {
            Write-Host "  [AST ERROR] Line $($e.Extent.StartLineNumber): $($e.Message)" -ForegroundColor Red
        }
    } else {
        Write-Host "  [AST OK] Clean syntax tree with 0 errors." -ForegroundColor Green
    }
}

Test-PsAst "Section 6.2 (Automated PowerShell Debloat)" $s62Script
Test-PsAst "Section 6.3 (Disable-ScheduledTask)" $s63Script
Test-PsAst "Section 7.1 (One-Click Factory Rollback)" $s71Script
Test-PsAst "Section 7.3 (Post-Restoration Verification)" $s73Script

Write-Host "`n==========================================================" -ForegroundColor Cyan
Write-Host "PART 3: LIVE GROUND TRUTH METRIC VERIFICATION" -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

# 1. Host OS & BIOS
$os = Get-CimInstance Win32_OperatingSystem
$bios = Get-CimInstance Win32_BIOS
$cs = Get-CimInstance Win32_ComputerSystem
Write-Host "Host System:"
Write-Host "  Model: $($cs.Model)"
Write-Host "  OS: $($os.Caption) (Build $($os.BuildNumber).$($os.UBR))"
Write-Host "  BIOS: $($bios.SMBIOSBIOSVersion) (Manufacturer: $($bios.Manufacturer), Date: $($bios.ReleaseDate))"

# 2. ACPI Device Node
$acpiNode = Get-PnpDevice -FriendlyName "*Acer Application Base*" -ErrorAction SilentlyContinue
Write-Host "`nACPI Node:"
if ($acpiNode) {
    Write-Host "  Name: $($acpiNode.FriendlyName)"
    Write-Host "  InstanceId: $($acpiNode.InstanceId)"
    Write-Host "  Status: $($acpiNode.Status)"
} else {
    Write-Host "  ACPI Node query returned nothing" -ForegroundColor Yellow
}

# 3. Executable and Driver File Paths
Write-Host "`nFile Path Existence Check (20 paths):"
$pathsToCheck = @(
    "C:\Program Files\WindowsApps\ULICTekInc.NitroSenseforNotebook_5.0.1327.0_x64__nt9dgb7efx6bt\app\NitroSense.exe",
    "C:\Windows\System32\DriverStore\FileRepository\acerservicecomponent.inf_amd64_c9ec0382fd426cdf\AcerService.exe",
    "C:\Windows\System32\AcerDIAgent.exe",
    "C:\Windows\System32\DriverStore\FileRepository\predatorservice.inf_amd64_269fd3cb3a95f72c\OpenRGB.exe",
    "C:\Windows\System32\AcerCCAgent.exe",
    "C:\Windows\System32\DriverStore\FileRepository\sysmonitorservice.inf_amd64_9a721f842c75068a\x64\AcerSysHardwareService.exe",
    "C:\Windows\System32\DriverStore\FileRepository\acerservicecomponent.inf_amd64_c9ec0382fd426cdf\AcerServiceWrapper.exe",
    "C:\Windows\System32\DriverStore\FileRepository\ezservicecomponent.inf_amd64_0956e84e8c72d1ab\AcerEZService.exe",
    "C:\Windows\System32\AcerDeviceInfo\AcerDeviceInfoAgentService.exe",
    "C:\Windows\System32\DriverStore\FileRepository\predatorservice.inf_amd64_269fd3cb3a95f72c\AcerHardwareService.exe",
    "C:\Windows\System32\DriverStore\FileRepository\predatorservice.inf_amd64_269fd3cb3a95f72c\AcerAgentService.exe",
    "C:\Windows\System32\DriverStore\FileRepository\sysmonitorservice.inf_amd64_9a721f842c75068a\x64\AcerSysMonitorService.exe",
    "C:\Program Files\AcerCCAgent\ACCUserPS.exe",
    "C:\Windows\System32\DriverStore\FileRepository\predatorservice.inf_amd64_269fd3cb3a95f72c\AcerLightingService.exe",
    "C:\Windows\System32\DriverStore\FileRepository\acerdeviceenablingservicecomponent.inf_amd64_0a9a6f50fc73b215\ADESv2BW.exe",
    "C:\Windows\System32\DriverStore\FileRepository\acerdeviceenablingservicecomponent.inf_amd64_0a9a6f50fc73b215\ADESv2Svc.exe",
    "C:\Windows\System32\DriverStore\FileRepository\sysmonitorservice.inf_amd64_9a721f842c75068a\x64\AcerSystemCentralService.exe",
    "C:\Windows\System32\DriverStore\FileRepository\predatorservice.inf_amd64_269fd3cb3a95f72c\AcerCentralService.exe",
    "C:\Program Files\NitroSense\Prerequisites\NitroSenseLauncher.exe",
    "C:\Windows\System32\DriverStore\FileRepository\acerapplicationbasedriver.inf_amd64_87fcd9ece24eed34\AcerApplicationBaseDriver.sys"
)

$pathPass = 0
foreach ($p in $pathsToCheck) {
    $exists = Test-Path -LiteralPath $p
    if ($exists) { $pathPass++ } else { Write-Host "  [MISSING] $p" -ForegroundColor Red }
}
Write-Host "  Paths verified: $pathPass / $($pathsToCheck.Count)"

# 4. Windows Services Status
Write-Host "`nWindows Services SCM Query:"
$svcNames = @("AASSvc","AcerLightingService","AcerDeviceEnablingServiceV2","AcerDIAgentSvis","AcerCCAgentSvis","AcerEZSvc","AcerServiceSvc","ASMSvc","AcerDeviceInfoAgentService","AcerQAAgentSvis")
$svcs = Get-Service -Name $svcNames -ErrorAction SilentlyContinue
foreach ($s in $svcs) {
    Write-Host "  $($s.Name) : Status=$($s.Status), StartType=$($s.StartType)"
}

# 5. WMI Classes & Methods
Write-Host "`nWMI Classes:"
$agf = Get-CimClass -Namespace "root\wmi" -ClassName "AcerGamingFunction" -ErrorAction SilentlyContinue
if ($agf) {
    Write-Host "  AcerGamingFunction methods count: $($agf.CimClassMethods.Count) (Report claims: 25)"
} else {
    Write-Host "  AcerGamingFunction class not found" -ForegroundColor Red
}

$agm = Get-CimClass -Namespace "root\wmi" -ClassName "AcerGenericMethod" -ErrorAction SilentlyContinue
if ($agm) {
    Write-Host "  AcerGenericMethod methods count: $($agm.CimClassMethods.Count) (Report claims: 3)"
} else {
    Write-Host "  AcerGenericMethod class not found" -ForegroundColor Red
}

# 6. Sockets & Named Pipes
Write-Host "`nActive Listeners Check:"
$ports = @(46933, 6742, 4343, 4449, 15152, 19443, 46753, 15150, 51779, 51780, 51781, 51782)
foreach ($pt in $ports) {
    $conn = Get-NetTCPConnection -LocalPort $pt -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($conn) {
        Write-Host "  Port $pt : LISTEN (PID $($conn.OwningProcess))"
    } else {
        Write-Host "  Port $pt : NOT LISTENING (might be inactive or service down)" -ForegroundColor Yellow
    }
}

# 7. Scheduled Task
Write-Host "`nScheduled Task Check:"
$st = Get-ScheduledTask -TaskName "NitroSenseLauncher" -ErrorAction SilentlyContinue
if ($st) {
    Write-Host "  Task: $($st.TaskName), State: $($st.State)"
} else {
    Write-Host "  Task NitroSenseLauncher not found" -ForegroundColor Red
}

# 8. Battery Telemetry & settings.json
Write-Host "`nBattery Telemetry Check:"
$batt = Get-CimInstance Win32_Battery -ErrorAction SilentlyContinue
if ($batt) {
    Write-Host "  EstimatedChargeRemaining: $($batt.EstimatedChargeRemaining)%"
    Write-Host "  BatteryStatus: $($batt.BatteryStatus)"
}
$ccSettingsPath = "C:\ProgramData\Acer\CC\settings.json"
if (Test-Path $ccSettingsPath) {
    Write-Host "  settings.json content: $(Get-Content $ccSettingsPath -Raw)"
}
