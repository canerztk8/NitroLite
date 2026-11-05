# Inspect unpacked modules in NitroSense
$ErrorActionPreference = "Continue"

$unpacked = "C:\Program Files\WindowsApps\ULICTekInc.NitroSenseforNotebook_5.0.1327.0_x64__nt9dgb7efx6bt\app\resources\app.asar.unpacked"
Get-ChildItem -Path $unpacked -Recurse | Select-Object FullName, Length | Format-Table -AutoSize
