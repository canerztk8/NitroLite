# Deep dive into ADESv2Svc.exe and its connection to NitroSense
$ErrorActionPreference = "Continue"

$adesPath = "C:\Windows\System32\DriverStore\FileRepository\acerdeviceenablingservicecomponent.inf_amd64_0a9a6f50fc73b215"
Write-Host "Files in ADES directory:"
Get-ChildItem -Path $adesPath | Format-Table Name, Length

$adesInf = "$adesPath\AcerDeviceEnablingServiceComponent.inf"
if (Test-Path $adesInf) {
    Write-Host "`nFull INF Content of ADES:"
    Get-Content $adesInf
}

Write-Host "`nDetailed string analysis of ADESv2Svc.exe:"
$adesExe = "$adesPath\ADESv2Svc.exe"
$bytes = [System.IO.File]::ReadAllBytes($adesExe)
# Look for strings with lengths >= 4
$chars = [System.Text.Encoding]::ASCII.GetChars($bytes)
$sb = [System.Text.StringBuilder]::new()
$strList = [System.Collections.Generic.List[string]]::new()
foreach ($c in $chars) {
    if ([int]$c -ge 32 -and [int]$c -le 126) {
        $sb.Append($c) | Out-Null
    } else {
        if ($sb.Length -ge 4) {
            $strList.Add($sb.ToString())
        }
        $sb.Clear() | Out-Null
    }
}

Write-Host "Total strings extracted: $($strList.Count)"
Write-Host "`nKeywords related to Nitro, Sense, Socket, Port, HTTP, JSON, RPC, Hardware, Device in ADESv2Svc:"
$strList | Where-Object { $_ -match "Nitro|Sense|Hardware|WMI|Port|Socket|http|json|pipe|Device|Enable|Plugin|Component|App" } | Select-Object -Unique | Select-Object -First 100

Write-Host "`nNow check ADESv2BW.exe:"
$adesBw = "$adesPath\ADESv2BW.exe"
if (Test-Path $adesBw) {
    $bytesBw = [System.IO.File]::ReadAllBytes($adesBw)
    $charsBw = [System.Text.Encoding]::ASCII.GetChars($bytesBw)
    $sbBw = [System.Text.StringBuilder]::new()
    $strListBw = [System.Collections.Generic.List[string]]::new()
    foreach ($c in $charsBw) {
        if ([int]$c -ge 32 -and [int]$c -le 126) {
            $sbBw.Append($c) | Out-Null
        } else {
            if ($sbBw.Length -ge 4) {
                $strListBw.Add($sbBw.ToString())
            }
            $sbBw.Clear() | Out-Null
        }
    }
    Write-Host "Keywords in ADESv2BW.exe:"
    $strListBw | Where-Object { $_ -match "Nitro|Sense|Hardware|WMI|Port|Socket|http|json|pipe|Device|Enable|Plugin|Component|App" } | Select-Object -Unique | Select-Object -First 60
}
