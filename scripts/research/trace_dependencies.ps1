# Deep binary & IPC dependency investigation
$ErrorActionPreference = "Continue"

Write-Host "=== 1. NAMED PIPES AUDIT ===" -ForegroundColor Cyan
[System.IO.Directory]::GetFiles("\\.\\pipe\\") | Where-Object { $_ -match "acer|predator|openrgb|ades|ez|cc|asm" }

Write-Host "`n=== 2. OPEN PORTS AND CONNECTIONS OF ACER PROCESSES ===" -ForegroundColor Cyan
$acerPids = (Get-Process | Where-Object { $_.ProcessName -match "Acer|Nitro|ACC|ADES|OpenRGB" }).Id
Get-NetTCPConnection | Where-Object { $_.OwningProcess -in $acerPids } | Select-Object LocalAddress, LocalPort, RemoteAddress, RemotePort, State, OwningProcess | Format-Table -AutoSize

Write-Host "`n=== 3. TRACING BINARIES OF AASSVC ===" -ForegroundColor Cyan
$aasPath = "C:\Windows\System32\DriverStore\FileRepository\predatorservice.inf_amd64_269fd3cb3a95f72c"
Get-ChildItem -Path $aasPath -File | Select-Object Name, Length, LastWriteTime

Write-Host "`nSearching strings in AcerHardwareService.exe and AcerAgentService.exe for references to other services:" -ForegroundColor Cyan
$hwService = "$aasPath\AcerHardwareService.exe"
$agentService = "$aasPath\AcerAgentService.exe"

$stringsHw = & strings.exe /accepteula $hwService 2>$null
if (-not $stringsHw) {
    # Use powershell regex on binary content
    $bytes = [System.IO.File]::ReadAllBytes($hwService)
    $text = [System.Text.Encoding]::ASCII.GetString($bytes)
    Write-Host "Extracted text length from AcerHardwareService.exe: $($text.Length)"
    
    $matchesSvc = [regex]::Matches($text, "(Acer[A-Za-z0-9_]+|ADES[A-Za-z0-9_]+|ASM[A-Za-z0-9_]+)") | ForEach-Object { $_.Value } | Select-Object -Unique
    Write-Host "Service-like identifiers in AcerHardwareService.exe:"
    $matchesSvc
    
    $pipes = [regex]::Matches($text, "pipe\\[A-Za-z0-9_]+") | ForEach-Object { $_.Value } | Select-Object -Unique
    Write-Host "Pipes in AcerHardwareService.exe:"
    $pipes
    
    $wmi = [regex]::Matches($text, "(Acer[A-Za-z0-9_]+Function|root\\[a-z0-9\\]+)") | ForEach-Object { $_.Value } | Select-Object -Unique
    Write-Host "WMI in AcerHardwareService.exe:"
    $wmi
}

Write-Host "`nSearching text in AcerAgentService.exe:" -ForegroundColor Cyan
$bytesAgent = [System.IO.File]::ReadAllBytes($agentService)
$textAgent = [System.Text.Encoding]::ASCII.GetString($bytesAgent)
$matchesAgent = [regex]::Matches($textAgent, "(Acer[A-Za-z0-9_]+|ADES[A-Za-z0-9_]+|ASM[A-Za-z0-9_]+)") | ForEach-Object { $_.Value } | Select-Object -Unique
Write-Host "Identifiers in AcerAgentService.exe:"
$matchesAgent

$pipesAgent = [regex]::Matches($textAgent, "pipe\\[A-Za-z0-9_]+") | ForEach-Object { $_.Value } | Select-Object -Unique
Write-Host "Pipes in AcerAgentService.exe:"
$pipesAgent

Write-Host "`n=== 4. INVESTIGATING ACERDEVICEENABLINGSERVICEV2 (ADES) ===" -ForegroundColor Cyan
$adesPath = "C:\Windows\System32\DriverStore\FileRepository\acerdeviceenablingservicecomponent.inf_amd64_0a9a6f50fc73b215"
Get-ChildItem -Path $adesPath -File | Select-Object Name, Length
$adesInf = Get-ChildItem -Path $adesPath -Filter "*.inf"
if ($adesInf) {
    Write-Host "Contents of $($adesInf.FullName):"
    Get-Content $adesInf.FullName | Select-String "Service|DisplayName|Description|Driver|Class"
}

Write-Host "`nChecking ADESv2Svc.exe text strings:" -ForegroundColor Cyan
$adesExe = "$adesPath\ADESv2Svc.exe"
if (Test-Path $adesExe) {
    $adesBytes = [System.IO.File]::ReadAllBytes($adesExe)
    $adesText = [System.Text.Encoding]::ASCII.GetString($adesBytes)
    [regex]::Matches($adesText, "(Acer[A-Za-z0-9_]+|ADES[A-Za-z0-9_]+|Install|Store|Device|WMI|pipe)") | ForEach-Object { $_.Value } | Select-Object -Unique | Select-Object -First 30
}

Write-Host "`n=== 5. INVESTIGATING ASMSVC (ACER SYSTEM MONITOR) ===" -ForegroundColor Cyan
$asmPath = "C:\Windows\System32\DriverStore\FileRepository\sysmonitorservice.inf_amd64_9a721f842c75068a"
Get-ChildItem -Path $asmPath -Recurse -File | Select-Object FullName, Length
$asmInf = Get-ChildItem -Path $asmPath -Filter "*.inf" -Recurse
if ($asmInf) {
    Write-Host "Contents of $($asmInf.FullName):"
    Get-Content $asmInf.FullName | Select-String "Service|DisplayName|Description|Class"
}

Write-Host "`n=== 6. INVESTIGATING ACERCCAGENT.EXE & BATTERY HOLD ===" -ForegroundColor Cyan
$ccExe = "C:\Windows\system32\AcerCCAgent.exe"
if (Test-Path $ccExe) {
    $ccBytes = [System.IO.File]::ReadAllBytes($ccExe)
    $ccText = [System.Text.Encoding]::ASCII.GetString($ccBytes)
    Write-Host "Battery references in AcerCCAgent.exe:"
    [regex]::Matches($ccText, "(Battery[A-Za-z0-9_]*|SetFunction|AcerGeneric[A-Za-z0-9_]*)") | ForEach-Object { $_.Value } | Select-Object -Unique
}

$accUserPs = "C:\Program Files\AcerCCAgent\ACCUserPS.exe"
if (Test-Path $accUserPs) {
    $accBytes = [System.IO.File]::ReadAllBytes($accUserPs)
    $accText = [System.Text.Encoding]::ASCII.GetString($accBytes)
    Write-Host "Battery references in ACCUserPS.exe:"
    [regex]::Matches($accText, "(Battery[A-Za-z0-9_]*|SetFunction|AcerGeneric[A-Za-z0-9_]*)") | ForEach-Object { $_.Value } | Select-Object -Unique
}
