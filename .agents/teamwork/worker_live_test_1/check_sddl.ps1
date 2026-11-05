$services = @('AcerServiceSvc', 'AcerDIAgentSvis', 'AcerDeviceInfoAgentService', 'AcerEZSvc', 'ASMSvc', 'AcerDeviceEnablingServiceV2', 'AcerCCAgentSvis', 'AcerLightingService', 'AASSvc')
foreach ($s in $services) {
    $sd = (sc.exe sdshow $s | Out-String).Trim()
    Write-Host "$s : $sd"
}
