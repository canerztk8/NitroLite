Get-NetTCPConnection | Where-Object { $_.LocalPort -ge 51779 -and $_.LocalPort -le 51782 } | Select-Object LocalAddress, LocalPort, RemoteAddress, RemotePort, State, OwningProcess | Format-Table -AutoSize

# Check process names for those PIDs
Get-NetTCPConnection | Where-Object { $_.LocalPort -ge 51779 -and $_.LocalPort -le 51782 } | ForEach-Object {
    $proc = Get-Process -Id $_.OwningProcess -ErrorAction SilentlyContinue
    Write-Host "Port $($_.LocalPort) ($($_.State)) owned by PID $($_.OwningProcess) ($($proc.Name))"
}

# Check named pipes
[System.IO.Directory]::GetFiles('\\.\pipe\') | Where-Object { $_ -match 'ADESv2|predator|AcerLighting' }
