# Check app package structure
$ErrorActionPreference = "Continue"

$appDir = "C:\Program Files\WindowsApps\ULICTekInc.NitroSenseforNotebook_5.0.1327.0_x64__nt9dgb7efx6bt"
Get-ChildItem -Path $appDir -Recurse | Where-Object { $_.Name -like "*asar*" -or $_.Name -like "*bundle*" -or $_.Extension -in ".node",".dll",".json" } | Select-Object FullName, Length | Format-Table -AutoSize
