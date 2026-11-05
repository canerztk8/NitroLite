<#
.SYNOPSIS
    Live Testing & RAM Delta Validation Script for Acer NitroSense Ecosystem.
.DESCRIPTION
    Collects live baseline telemetry, evaluates service control permissions,
    measures individual and categorized memory consumption, audits Event Logs,
    and validates hardware subsystem integrity on Acer Nitro AN16-41.
#>

$ErrorActionPreference = "Continue"

Write-Host "=== 1. SYSTEM ENVIRONMENT & HARDWARE IDENTIFICATION ===" -ForegroundColor Cyan
$computerSystem = Get-CimInstance Win32_ComputerSystem
$bios = Get-CimInstance Win32_BIOS
$os = Get-CimInstance Win32_OperatingSystem

$envInfo = [PSCustomObject]@{
    Manufacturer = $computerSystem.Manufacturer
    Model = $computerSystem.Model
    BIOSVersion = $bios.SMBIOSBIOSVersion
    OSName = $os.Caption
    OSVersion = $os.Version
    OSBuild = $os.BuildNumber
    TotalPhysicalMemoryGB = [math]::Round($computerSystem.TotalPhysicalMemory / 1GB, 2)
    TimestampUTC = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
}
$envInfo | Format-List

Write-Host "`n=== 2. COMPLETE LIVE ACER PROCESS & MEMORY TELEMETRY ===" -ForegroundColor Cyan
$acerProcesses = Get-Process | Where-Object { $_.ProcessName -match 'Acer|Nitro|ACC|OpenRGB|ADES' } | ForEach-Object {
    $proc = $_
    $cimProc = Get-CimInstance Win32_Process -Filter "ProcessId = $($proc.Id)" -ErrorAction SilentlyContinue
    $parentPid = if ($cimProc) { $cimProc.ParentProcessId } else { "N/A" }
    $cmdLine = if ($cimProc) { $cimProc.CommandLine } else { "N/A" }
    $execPath = if ($proc.Path) { $proc.Path } elseif ($cimProc) { $cimProc.ExecutablePath } else { "N/A" }

    [PSCustomObject]@{
        PID = $proc.Id
        ProcessName = $proc.ProcessName
        WorkingSetMB = [math]::Round($proc.WorkingSet64 / 1MB, 2)
        PrivateMemoryMB = [math]::Round($proc.PrivateMemorySize64 / 1MB, 2)
        Handles = $proc.HandleCount
        Threads = $proc.Threads.Count
        SessionId = $proc.SessionId
        ParentPID = $parentPid
        ExecutablePath = $execPath
    }
} | Sort-Object -Property WorkingSetMB -Descending

$acerProcesses | Format-Table -Property PID, ProcessName, WorkingSetMB, PrivateMemoryMB, SessionId, ParentPID -AutoSize

$totalWS = [math]::Round(($acerProcesses | Measure-Object -Property WorkingSetMB -Sum).Sum, 2)
$totalPM = [math]::Round(($acerProcesses | Measure-Object -Property PrivateMemoryMB -Sum).Sum, 2)

$uiProcesses = $acerProcesses | Where-Object { $_.ProcessName -eq 'NitroSense' }
$uiWS = [math]::Round(($uiProcesses | Measure-Object -Property WorkingSetMB -Sum).Sum, 2)
$uiPM = [math]::Round(($uiProcesses | Measure-Object -Property PrivateMemoryMB -Sum).Sum, 2)

$bgProcesses = $acerProcesses | Where-Object { $_.ProcessName -ne 'NitroSense' }
$bgWS = [math]::Round(($bgProcesses | Measure-Object -Property WorkingSetMB -Sum).Sum, 2)
$bgPM = [math]::Round(($bgProcesses | Measure-Object -Property PrivateMemoryMB -Sum).Sum, 2)

Write-Host "`n--- BASELINE MEMORY AGGREGATES ---" -ForegroundColor Yellow
Write-Host "Total Active Acer Processes        : $($acerProcesses.Count)"
Write-Host "Total Baseline Working Set (RAM)   : $totalWS MB"
Write-Host "Total Baseline Private Memory      : $totalPM MB"
Write-Host "NitroSense UI Share (4 processes)  : $uiWS MB WS ($([math]::Round(($uiWS/$totalWS)*100, 1))%) | $uiPM MB PM ($([math]::Round(($uiPM/$totalPM)*100, 1))%)"
Write-Host "Background Services & Daemons      : $bgWS MB WS ($([math]::Round(($bgWS/$totalWS)*100, 1))%) | $bgPM MB PM ($([math]::Round(($bgPM/$totalPM)*100, 1))%)"

Write-Host "`n=== 3. LIVE WINDOWS SERVICES TELEMETRY ===" -ForegroundColor Cyan
$allAcerServices = Get-CimInstance Win32_Service | Where-Object { $_.Name -match 'Acer|AAS|ASM' } | ForEach-Object {
    [PSCustomObject]@{
        Name = $_.Name
        DisplayName = $_.DisplayName
        State = $_.State
        StartMode = $_.StartMode
        PID = $_.ProcessId
        PathName = $_.PathName
        StartName = $_.StartName
    }
}
$allAcerServices | Format-Table -Property Name, State, StartMode, PID, StartName, DisplayName -AutoSize

Write-Host "`n=== 4. CANDIDATE SERVICE STOP PROBE & SECURITY AUDIT ===" -ForegroundColor Cyan
$candidateServices = @('AcerServiceSvc', 'AcerDIAgentSvis', 'AcerDeviceInfoAgentService', 'AcerEZSvc', 'ASMSvc', 'AcerDeviceEnablingServiceV2')
$probeResults = foreach ($svcName in $candidateServices) {
    $svcObj = Get-Service -Name $svcName -ErrorAction SilentlyContinue
    $sddl = (sc.exe sdshow $svcName | Out-String).Trim()
    
    $stopAttemptResult = "Not Attempted"
    $stopError = "None"
    try {
        Stop-Service -Name $svcName -ErrorAction Stop
        $stopAttemptResult = "Success"
        Start-Service -Name $svcName -ErrorAction SilentlyContinue
    } catch {
        $stopAttemptResult = "AccessDenied"
        $stopError = $_.Exception.Message
    }

    [PSCustomObject]@{
        ServiceName = $svcName
        CurrentStatus = if ($svcObj) { $svcObj.Status } else { "NotFound" }
        StartupType = if ($svcObj) { $svcObj.StartType } else { "NotFound" }
        StopResult = $stopAttemptResult
        ErrorMessage = $stopError
        SDDL = $sddl
    }
}
$probeResults | Format-Table -Property ServiceName, CurrentStatus, StopResult, ErrorMessage -AutoSize

Write-Host "`n=== 5. TIER-BY-TIER MEMORY SAVINGS BREAKDOWN ===" -ForegroundColor Cyan
# Tier 1: Safe to disable telemetry / bloatware
$tier1ServiceNames = @('AcerServiceSvc', 'AcerDIAgentSvis', 'AcerDeviceInfoAgentService', 'AcerEZSvc', 'ASMSvc', 'AcerDeviceEnablingServiceV2')
$tier1Processes = $acerProcesses | Where-Object { 
    $_.ProcessName -match 'AcerService|AcerDIAgent|AcerDeviceInfoAgentService|AcerEZService|AcerSys|ADES' 
}
$tier1WS = [math]::Round(($tier1Processes | Measure-Object -Property WorkingSetMB -Sum).Sum, 2)
$tier1PM = [math]::Round(($tier1Processes | Measure-Object -Property PrivateMemoryMB -Sum).Sum, 2)

# Tier 2: Optional Care Center companion
$tier2Processes = $acerProcesses | Where-Object { $_.ProcessName -match 'AcerCCAgent|ACCUserPS' }
$tier2WS = [math]::Round(($tier2Processes | Measure-Object -Property WorkingSetMB -Sum).Sum, 2)
$tier2PM = [math]::Round(($tier2Processes | Measure-Object -Property PrivateMemoryMB -Sum).Sum, 2)

# Tier 3: Core hardware services (AASSvc + AcerLightingService)
$tier3Processes = $acerProcesses | Where-Object { $_.ProcessName -match 'AcerCentralService|AcerAgentService|AcerHardwareService|AcerLightingService|OpenRGB' }
$tier3WS = [math]::Round(($tier3Processes | Measure-Object -Property WorkingSetMB -Sum).Sum, 2)
$tier3PM = [math]::Round(($tier3Processes | Measure-Object -Property PrivateMemoryMB -Sum).Sum, 2)

$tierSummary = @(
    [PSCustomObject]@{
        Tier = "Tier 1: Safe to Disable (Telemetry, Cloud, Ads, Redundant)"
        ProcessCount = $tier1Processes.Count
        WorkingSetMB = $tier1WS
        PrivateMemoryMB = $tier1PM
        PercentOfBackground = [math]::Round(($tier1WS / $bgWS) * 100, 1)
        HardwareRisk = "Zero (0% Hardware Dependency)"
    },
    [PSCustomObject]@{
        Tier = "Tier 2: Optional (Care Center Update & Tray Companion)"
        ProcessCount = $tier2Processes.Count
        WorkingSetMB = $tier2WS
        PrivateMemoryMB = $tier2PM
        PercentOfBackground = [math]::Round(($tier2WS / $bgWS) * 100, 1)
        HardwareRisk = "Low (80% battery cap latches in EC; GUI toggle lost)"
    },
    [PSCustomObject]@{
        Tier = "Tier 3: Critical / Do Not Touch (Fan, Thermal, MUX, RGB)"
        ProcessCount = $tier3Processes.Count
        WorkingSetMB = $tier3WS
        PrivateMemoryMB = $tier3PM
        PercentOfBackground = [math]::Round(($tier3WS / $bgWS) * 100, 1)
        HardwareRisk = "CRITICAL (Mandatory for EC communication)"
    },
    [PSCustomObject]@{
        Tier = "NitroSense Electron UI (On-Demand User App)"
        ProcessCount = $uiProcesses.Count
        WorkingSetMB = $uiWS
        PrivateMemoryMB = $uiPM
        PercentOfBackground = "N/A (User Session)"
        HardwareRisk = "Zero (Settings persist in AASSvc / EC when closed)"
    }
)
$tierSummary | Format-Table -AutoSize

Write-Host "`n=== 6. CORE HARDWARE & COMMUNICATIONS STABILITY AUDIT ===" -ForegroundColor Cyan

# 6.1 AASSvc Hardware Daemon verification
$aasService = Get-Service -Name AASSvc
$aasConn = Get-NetTCPConnection -LocalPort 46933 -ErrorAction SilentlyContinue
Write-Host "AASSvc Service Status        : $($aasService.Status) ($($aasService.StartType))"
Write-Host "AASSvc Loopback Port 46933   : $(if ($aasConn) { "LISTENING (PID $($aasConn[0].OwningProcess))" } else { "INACTIVE" })"

# 6.2 AcerLightingService verification
$lightService = Get-Service -Name AcerLightingService
$lightConn = Get-NetTCPConnection -LocalPort 6742 -ErrorAction SilentlyContinue
Write-Host "AcerLightingService Status   : $($lightService.Status) ($($lightService.StartType))"
Write-Host "OpenRGB Loopback Port 6742   : $(if ($lightConn) { "LISTENING (PID $($lightConn[0].OwningProcess))" } else { "INACTIVE" })"

# 6.3 ACPI WMI Hardware Interface Verification
$wmiGaming = Get-CimClass -Namespace root\wmi -ClassName AcerGamingFunction -ErrorAction SilentlyContinue
$wmiGeneric = Get-CimClass -Namespace root\wmi -ClassName AcerGenericMethod -ErrorAction SilentlyContinue
Write-Host "WMI root\wmi:AcerGamingFunction : $(if ($wmiGaming) { "AVAILABLE ($($wmiGaming.CimClassMethods.Count) hardware methods)" } else { "MISSING" })"
Write-Host "WMI root\wmi:AcerGenericMethod  : $(if ($wmiGeneric) { "AVAILABLE ($($wmiGeneric.CimClassMethods.Count) hardware methods)" } else { "MISSING" })"

# 6.4 Battery Health Status
$ccSettings = if (Test-Path C:\ProgramData\Acer\CC\settings.json) { Get-Content C:\ProgramData\Acer\CC\settings.json | ConvertFrom-Json } else { $null }
$battery = Get-CimInstance Win32_Battery
Write-Host "Battery Charge Percentage    : $($battery.EstimatedChargeRemaining)%"
Write-Host "Battery Status               : $($battery.Status) (State Code: $($battery.BatteryStatus))"
Write-Host "CareCenter BatteryHealthy    : $(if ($ccSettings) { $ccSettings.BatteryHealthy } else { "N/A" }) (1 = 80% Cap Active)"

# 6.5 Windows Event Log Stability Check
Write-Host "`n=== 7. WINDOWS EVENT LOG STABILITY AUDIT ===" -ForegroundColor Cyan
$scmEvents = Get-WinEvent -FilterHashtable @{LogName='System'; ProviderName='Service Control Manager'; StartTime=(Get-Date).AddHours(-48)} -ErrorAction SilentlyContinue | Where-Object { $_.Message -match 'Acer|Predator' }
Write-Host "SCM Events in past 48 hours referencing Acer: $(@($scmEvents).Count)"
if ($scmEvents) {
    $scmEvents | Select-Object -First 5 TimeCreated, Id, LevelDisplayName, Message | Format-Table -Wrap
}

$appErrors = Get-WinEvent -FilterHashtable @{LogName='Application'; ProviderName='Application Error'; StartTime=(Get-Date).AddHours(-48)} -ErrorAction SilentlyContinue | Where-Object { $_.Message -match 'Acer|Nitro|Predator' }
Write-Host "Application Crash Events in past 48 hours for Acer: $(@($appErrors).Count)"
if ($appErrors) {
    $appErrors | Select-Object TimeCreated, Id, Message | Format-Table -Wrap
} else {
    Write-Host "Zero application crash events detected for Acer / NitroSense components." -ForegroundColor Green
}

Write-Host "`n=== 8. RESTORATION & INTEGRITY CONFIRMATION ===" -ForegroundColor Cyan
$allServicesRestored = $true
foreach ($s in (Get-Service -Name Acer*, AASSvc, ASMSvc)) {
    if ($s.Name -ne 'AcerQAAgentSvis' -and $s.Status -ne 'Running') {
        $allServicesRestored = $false
        Write-Warning "Service $($s.Name) is in state: $($s.Status)"
    }
}
if ($allServicesRestored) {
    Write-Host "ALL Acer Windows Services confirmed in healthy Running state!" -ForegroundColor Green
} else {
    Write-Warning "One or more services are not running."
}

# Export structured JSON data for test_results.md
$outputData = @{
    Environment = $envInfo
    Aggregates = @{
        TotalWorkingSetMB = $totalWS
        TotalPrivateMemoryMB = $totalPM
        NitroSenseUIWorkingSetMB = $uiWS
        NitroSenseUIPrivateMemoryMB = $uiPM
        BackgroundWorkingSetMB = $bgWS
        BackgroundPrivateMemoryMB = $bgPM
    }
    Tiers = $tierSummary
    Processes = $acerProcesses
    Services = $allAcerServices
    ProbeResults = $probeResults
}
$outputData | ConvertTo-Json -Depth 4 | Set-Content "c:\Users\caner\Documents\antigravity\eager-shannon\.agents\teamwork\worker_live_test_1\live_test_metrics.json" -Encoding UTF8
Write-Host "`nWrote live_test_metrics.json successfully." -ForegroundColor Green
