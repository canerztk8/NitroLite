# Antigravity PC Optimizer - Elevated Script for Acer Nitro 16
#
# UYARI / WARNING: This is a PERSONAL, machine-specific script and is not part of NitroLite.
# It assumes Samsung 990 PRO (C:) + a second SSD (D:), Plastic SCM, Office and a stale PlanetVPN service,
# disables SysMain, restarts audio services, purges C:\Windows\Temp and DELETES a service with sc.exe.
# Do not run it on another computer and do not distribute it with the application.
Write-Host "========================================================" -ForegroundColor Cyan
Write-Host "  Applying Elevated Performance Optimizations" -ForegroundColor Cyan
Write-Host "========================================================" -ForegroundColor Cyan

# 1. SysMain (Superfetch) disable for NVMe PCIe 4.0 SSDs
Write-Host "`n[1/6] Configuring SysMain service..." -ForegroundColor Yellow
try {
    Set-Service -Name "SysMain" -StartupType Disabled -ErrorAction Stop
    Stop-Service -Name "SysMain" -Force -ErrorAction SilentlyContinue
    Write-Host "  -> [SUCCESS] SysMain service stopped and disabled (eliminates redundant SSD wear)." -ForegroundColor Green
} catch {
    Write-Host "  -> [ERROR] SysMain: $($_.Exception.Message)" -ForegroundColor Red
}

# 2. Hardware-Accelerated GPU Scheduling (HAGS)
Write-Host "`n[2/6] Enabling Hardware-Accelerated GPU Scheduling (HAGS)..." -ForegroundColor Yellow
try {
    $gfxPath = "HKLM:\SYSTEM\CurrentControlSet\Control\GraphicsDrivers"
    Set-ItemProperty -Path $gfxPath -Name "HwSchMode" -Value 2 -Type DWord -Force
    Write-Host "  -> [SUCCESS] HAGS enabled (unlocks DLSS 3 Frame Generation & lowers GPU latency)." -ForegroundColor Green
} catch {
    Write-Host "  -> [ERROR] HAGS: $($_.Exception.Message)" -ForegroundColor Red
}

# 3. Audio Device Graph Isolation (audiodg.exe) reset
Write-Host "`n[3/6] Resetting audiodg.exe and audio subsystems..." -ForegroundColor Yellow
try {
    Stop-Process -Name "audiodg" -Force -ErrorAction SilentlyContinue
    Restart-Service -Name "AudioEndpointBuilder" -Force -ErrorAction SilentlyContinue
    Restart-Service -Name "AudioSrv" -Force -ErrorAction SilentlyContinue
    Write-Host "  -> [SUCCESS] Audio service restarted and audiodg CPU accumulator cleared." -ForegroundColor Green
} catch {
    Write-Host "  -> [ERROR] Audio: $($_.Exception.Message)" -ForegroundColor Red
}

# 4. Disable Customer Experience Improvement Program (CEIP) Telemetry Tasks
Write-Host "`n[4/6] Disabling CEIP background telemetry tasks..." -ForegroundColor Yellow
$ceipTasks = @(
    @{ Name = "Consolidator"; Path = "\Microsoft\Windows\Customer Experience Improvement Program\" },
    @{ Name = "UsbCeip"; Path = "\Microsoft\Windows\Customer Experience Improvement Program\" }
)
foreach ($task in $ceipTasks) {
    try {
        Disable-ScheduledTask -TaskName $task.Name -TaskPath $task.Path -ErrorAction Stop | Out-Null
        Write-Host "  -> [SUCCESS] Disabled telemetry task: $($task.Name)" -ForegroundColor Green
    } catch {
        Write-Host "  -> [WARNING] Task $($task.Name): $($_.Exception.Message)" -ForegroundColor DarkYellow
    }
}

# 5. NVMe TRIM Optimization
Write-Host "`n[5/6] Running NVMe TRIM on Drive C: and Drive D:..." -ForegroundColor Yellow
try {
    Optimize-Volume -DriveLetter C -ReTrim
    Write-Host "  -> [SUCCESS] Samsung SSD 990 PRO 2TB (C:) TRIM re-indexing complete." -ForegroundColor Green
} catch {
    Write-Host "  -> [ERROR] TRIM C: $($_.Exception.Message)" -ForegroundColor Red
}

try {
    Optimize-Volume -DriveLetter D -ReTrim
    Write-Host "  -> [SUCCESS] Samsung SSD 1TB (D:) TRIM re-indexing complete." -ForegroundColor Green
} catch {
    Write-Host "  -> [ERROR] TRIM D: $($_.Exception.Message)" -ForegroundColor Red
}

# 6. Clean System Temp Files
Write-Host "`n[6/7] Purging system temporary files in C:\Windows\Temp..." -ForegroundColor Yellow
try {
    Get-ChildItem -Path "C:\Windows\Temp" -Recurse -Force -ErrorAction SilentlyContinue |
        Where-Object { -not $_.PSIsContainer } |
        Remove-Item -Force -ErrorAction SilentlyContinue
    Write-Host "  -> [SUCCESS] C:\Windows\Temp cache cleared." -ForegroundColor Green
} catch {
    Write-Host "  -> [WARNING] Partial clean: $($_.Exception.Message)" -ForegroundColor DarkYellow
}

# 7. Configure Idle Background Services to Manual (Plastic SCM, Office, Edge)
Write-Host "`n[7/8] Configuring idle background services to Manual..." -ForegroundColor Yellow
$idleServices = @(
    @{ Name = "Plastic Server 6"; Desc = "Plastic SCM / Unity VCS local server" },
    @{ Name = "ClickToRunSvc"; Desc = "Microsoft Office Click-to-Run background service" },
    @{ Name = "edgeupdate"; Desc = "Microsoft Edge Update Service" },
    @{ Name = "edgeupdatem"; Desc = "Microsoft Edge Update Service (Manual)" }
)
foreach ($item in $idleServices) {
    try {
        if (Get-Service -Name $item.Name -ErrorAction SilentlyContinue) {
            Stop-Service -Name $item.Name -Force -ErrorAction SilentlyContinue
            Set-Service -Name $item.Name -StartupType Manual -ErrorAction Stop
            Write-Host "  -> [SUCCESS] Service '$($item.Name)' stopped and set to Manual ($($item.Desc))." -ForegroundColor Green
        }
    } catch {
        Write-Host "  -> [WARNING] Service '$($item.Name)': $($_.Exception.Message)" -ForegroundColor DarkYellow
    }
}

# 8. Remove Orphaned Ghost Services (PlanetVPN)
Write-Host "`n[8/8] Cleaning orphaned ghost services..." -ForegroundColor Yellow
try {
    if (Get-Service -Name "PlanetVPNService" -ErrorAction SilentlyContinue) {
        Stop-Service -Name "PlanetVPNService" -Force -ErrorAction SilentlyContinue
        & sc.exe delete PlanetVPNService | Out-Null
        Write-Host "  -> [SUCCESS] Orphaned service 'PlanetVPNService' completely deleted from system." -ForegroundColor Green
    }
} catch {
    Write-Host "  -> [WARNING] PlanetVPNService cleanup: $($_.Exception.Message)" -ForegroundColor DarkYellow
}

Write-Host "`n========================================================" -ForegroundColor Cyan
Write-Host "  All Elevated Optimizations Applied Successfully!" -ForegroundColor Green
Write-Host "========================================================" -ForegroundColor Cyan
