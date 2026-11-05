# Search for Acer Care Center logs and config files
$ErrorActionPreference = "Continue"

Write-Host "=== Searching for Care Center logs ===" -ForegroundColor Cyan
Get-ChildItem -Path "C:\ProgramData\Acer" -Recurse -Filter "*.log" | Select-Object FullName, Length, LastWriteTime

Get-ChildItem -Path "C:\Program Files\AcerCCAgent" -Recurse | Select-Object FullName, Length

Write-Host "`n=== Check Acer Care Center installation & config ===" -ForegroundColor Cyan
Get-ItemProperty -Path "HKLM:\SOFTWARE\Acer\Acer Care Center\*" -ErrorAction SilentlyContinue
Get-ItemProperty -Path "HKLM:\SOFTWARE\WOW6432Node\Acer\Acer Care Center\*" -ErrorAction SilentlyContinue
