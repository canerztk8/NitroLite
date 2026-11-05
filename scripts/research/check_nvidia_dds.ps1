# Investigate NVIDIA display driver and DDS dependencies
$ErrorActionPreference = "Continue"

Write-Host "=== 1. NVIDIA SERVICES & DEPENDENCIES ===" -ForegroundColor Cyan
Get-Service -Name "NVDisplay*" | Select-Object Name, DisplayName, Status, StartType, ServicesDependedOn, DependentServices | Format-Table -AutoSize

Write-Host "`n=== 2. NVIDIA PROCESSES & THREADS/HANDLES ===" -ForegroundColor Cyan
$nvProcs = Get-Process | Where-Object { $_.ProcessName -match "NVDisplay|nvcontainer" }
$nvProcs | Select-Object Id, ProcessName, Path, WorkingSet64

Write-Host "`n=== 3. NVIDIA REGISTRY CONFIGURATION (DDS / Advanced Optimus) ===" -ForegroundColor Cyan
Get-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\nvlddmkm" -ErrorAction SilentlyContinue | Format-List *
Get-ChildItem -Path "HKLM:\SYSTEM\CurrentControlSet\Services\nvlddmkm" | Select-Object Name

Write-Host "`n=== 4. CHECK DWM AND DISPLAY SWITCHING APIS ===" -ForegroundColor Cyan
# In Windows 11, Dynamic Display Switching (DDS) is mediated by OS DWM / DXGI and NVIDIA driver:
# DXGI Output duplication and D3DKMTSetDisplayMode / NVAPI DDS
Get-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Control\GraphicsDrivers" -ErrorAction SilentlyContinue | Format-List HwSchMode, Dds*
