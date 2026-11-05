$bin = 'C:\Windows\system32\AcerCCAgent.exe'
if (Test-Path $bin) {
    $bytes = [System.IO.File]::ReadAllBytes($bin)
    $str = [System.Text.Encoding]::ASCII.GetString($bytes)
    $patterns = @('CallBIOSSetFunction', 'GetBatteryBoundary', 'SetTargetSystemUsageMode', 'GetCurrentBatteryHealthyConfig', 'BatteryHealthy')
    foreach ($p in $patterns) {
        Write-Host "Contains $p : $($str.Contains($p))"
    }
} else {
    Write-Host "File not found"
}
