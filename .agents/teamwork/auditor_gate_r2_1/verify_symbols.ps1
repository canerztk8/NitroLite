# Forensic Auditor Symbol Verification Script

$exe = "C:\Windows\System32\AcerCCAgent.exe"
$bytes = [System.IO.File]::ReadAllBytes($exe)
$ascii = [System.Text.Encoding]::ASCII.GetString($bytes)
$unicode = [System.Text.Encoding]::Unicode.GetString($bytes)

$symbols = @(
    "CallBIOSSetFunction",
    "GetBatteryBoundary",
    "SetTargetSystemUsageMode",
    "GetCurrentBatteryHealthyConfig"
)

Write-Host "=== Inspecting AcerCCAgent.exe ==="
foreach ($s in $symbols) {
    $foundAscii = $ascii.Contains($s)
    $foundUnicode = $unicode.Contains($s)
    Write-Host "Symbol '$s': ASCII=$foundAscii, Unicode=$foundUnicode"
}

Write-Host "`n=== Inspecting ADESv2Svc.exe ==="
$adesSvc = "C:\Windows\System32\DriverStore\FileRepository\acerdeviceenablingservicecomponent.inf_amd64_0a9a6f50fc73b215\ADESv2Svc.exe"
if (Test-Path $adesSvc) {
    $adesBytes = [System.IO.File]::ReadAllBytes($adesSvc)
    $adesAscii = [System.Text.Encoding]::ASCII.GetString($adesBytes)
    $adesUnicode = [System.Text.Encoding]::Unicode.GetString($adesBytes)
    
    $adesSymbols = @(
        "X-Sense_PrivacyPolicyConsensus_ADESV2",
        "ADESv2_Service_",
        "51779"
    )
    foreach ($as in $adesSymbols) {
        $fA = $adesAscii.Contains($as)
        $fU = $adesUnicode.Contains($as)
        Write-Host "ADES Symbol '$as': ASCII=$fA, Unicode=$fU"
    }
} else {
    Write-Host "ADESv2Svc.exe not found at expected path: $adesSvc"
}
