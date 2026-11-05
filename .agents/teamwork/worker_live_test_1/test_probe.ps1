$services = @('AcerServiceSvc', 'AcerDIAgentSvis', 'AcerDeviceInfoAgentService', 'AcerEZSvc', 'ASMSvc', 'AcerDeviceEnablingServiceV2', 'AcerCCAgentSvis')
foreach ($s in $services) {
    try {
        Stop-Service -Name $s -ErrorAction Stop
        Write-Host "Successfully stopped $s"
        Start-Service -Name $s -ErrorAction SilentlyContinue
    } catch {
        Write-Host "Failed to stop ${s}: $($_.Exception.Message)"
    }
}
