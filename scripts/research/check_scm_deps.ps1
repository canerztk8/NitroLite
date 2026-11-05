# Test Service Control Manager dependencies and inspect ASMSvc / ADESv2
$ErrorActionPreference = "Continue"

Write-Host "=== SCM DEPENDENCY MAP ===" -ForegroundColor Cyan
$svcNames = @("AASSvc", "AcerLightingService", "AcerServiceSvc", "ASMSvc", "AcerDIAgentSvis", "AcerEZSvc", "AcerDeviceInfoAgentService", "AcerDeviceEnablingServiceV2", "AcerCCAgentSvis")

foreach ($name in $svcNames) {
    $s = Get-Service -Name $name -ErrorAction SilentlyContinue
    if ($s) {
        $depOn = ($s.ServicesDependedOn | ForEach-Object { $_.Name }) -join ", "
        $depBy = ($s.DependentServices | ForEach-Object { $_.Name }) -join ", "
        Write-Host "$name ($($s.DisplayName)):"
        Write-Host "  Depends on: $($depOn -replace '^$', 'NONE')"
        Write-Host "  Depended on by: $($depBy -replace '^$', 'NONE')"
    }
}

Write-Host "`n=== DETAILED SCM QUERY FOR DRIVER AND RPC DEPENDENCIES ===" -ForegroundColor Cyan
foreach ($name in $svcNames) {
    & sc.exe qc $name | Select-String "DEPENDENCIES|START_TYPE|BINARY_PATH_NAME"
}

Write-Host "`n=== CHECK RPC ENDPOINTS ON SYSTEM (Acer / ASMSvc / ADES) ===" -ForegroundColor Cyan
# Query RPC endpoints
& reg.exe query "HKLM\SYSTEM\CurrentControlSet\Services\AASSvc" /s
& reg.exe query "HKLM\SYSTEM\CurrentControlSet\Services\ASMSvc" /s
& reg.exe query "HKLM\SYSTEM\CurrentControlSet\Services\AcerDeviceEnablingServiceV2" /s
