# Hardware Boundary & Adversarial Challenge Script
$ErrorActionPreference = "Continue"

Write-Host "=== 1. WMI PROVIDER & CLASS INVESTIGATION ===" -ForegroundColor Cyan
try {
    $agf = Get-CimClass -Namespace "root\wmi" -ClassName "AcerGamingFunction"
    Write-Host "AcerGamingFunction exists in root\wmi. Qualifiers:"
    $agf.CimClassQualifiers | ForEach-Object { "$($_.Name) = $($_.Value)" }
    
    $agm = Get-CimClass -Namespace "root\wmi" -ClassName "AcerGenericMethod"
    Write-Host "AcerGenericMethod exists in root\wmi. Qualifiers:"
    $agm.CimClassQualifiers | ForEach-Object { "$($_.Name) = $($_.Value)" }
} catch {
    Write-Host "Error querying root\wmi classes: $_"
}

Write-Host "`nChecking WMI providers in root\wmi:" -ForegroundColor Cyan
Get-CimInstance -Namespace "root\wmi" -ClassName "__Win32Provider" | Select-Object Name, CLSID | Format-Table -AutoSize

Write-Host "`nChecking Acer-related classes in root\cimv2 and root\wmi:" -ForegroundColor Cyan
Get-CimClass -Namespace "root\cimv2" | Where-Object { $_.CimClassName -like "*Acer*" } | Select-Object CimClassName
Get-CimClass -Namespace "root\wmi" | Where-Object { $_.CimClassName -like "*Acer*" } | Select-Object CimClassName

Write-Host "`n=== 2. BATTERY 80% LIMIT & SETTINGS.JSON INVESTIGATION ===" -ForegroundColor Cyan
$ccSettingsPath = "C:\ProgramData\Acer\CC\settings.json"
if (Test-Path $ccSettingsPath) {
    Write-Host "Found $ccSettingsPath. Content:"
    Get-Content $ccSettingsPath -Raw
} else {
    Write-Host "File $ccSettingsPath not found!"
    Get-ChildItem -Path "C:\ProgramData\Acer" -Recurse -Filter "*setting*" -ErrorAction SilentlyContinue | Select-Object FullName
}

Write-Host "`nSearching for BatteryHealthy references across ProgramData and registry:" -ForegroundColor Cyan
Get-ItemProperty -Path "HKLM:\SOFTWARE\Acer\*" -ErrorAction SilentlyContinue | Out-String | Select-String "Battery"
Get-ItemProperty -Path "HKLM:\SOFTWARE\OEM\*" -ErrorAction SilentlyContinue | Out-String | Select-String "Battery"

Write-Host "`n=== 3. ACER SERVICE EXECUTABLE STRINGS & CROSS-DEPENDENCIES ===" -ForegroundColor Cyan
$services = Get-CimInstance Win32_Service | Where-Object { $_.Name -like "*Acer*" -or $_.Name -in "AASSvc","ASMSvc" }

foreach ($svc in $services) {
    Write-Host "`nService: $($svc.Name) ($($svc.DisplayName))" -ForegroundColor Yellow
    Write-Host "  Path: $($svc.PathName)"
    Write-Host "  State: $($svc.State) | StartMode: $($svc.StartMode)"
    Write-Host "  ProcessId: $($svc.ProcessId)"
}

Write-Host "`n=== 4. CHECK ACERAPPLICATIONBASEDRIVER.SYS & ACPI DEVICE ===" -ForegroundColor Cyan
Get-PnpDevice | Where-Object { $_.InstanceId -like "*1025165F*" -or $_.FriendlyName -like "*Acer Application*" } | Select-Object InstanceId, FriendlyName, Status, Class

Write-Host "`n=== 5. CHECK MUX SWITCH / DDS IN REGISTRY / DRIVER ===" -ForegroundColor Cyan
Get-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\nvlddmkm*" -ErrorAction SilentlyContinue | Select-Object -Property * -ExcludeProperty *Binary* | Out-String | Select-String -Pattern "DDS|Display|Optimus|MUX"

Write-Host "`n=== COMPLETE ==="
