# Dissect AcerCCAgent.exe logic regarding BatteryHealthy and settings.json
$ErrorActionPreference = "Continue"

$ccExe = "C:\Windows\system32\AcerCCAgent.exe"
$bytes = [System.IO.File]::ReadAllBytes($ccExe)
$text = [System.Text.Encoding]::ASCII.GetString($bytes)

Write-Host "File size: $($bytes.Length) bytes"

Write-Host "`nOccurrences and context of 'BatteryHealthy':"
$matches = [regex]::Matches($text, ".{0,80}BatteryHealthy.{0,80}")
foreach ($m in $matches) {
    Write-Host "--> $($m.Value)"
}

Write-Host "`nOccurrences and context of 'settings.json':"
$matchesJson = [regex]::Matches($text, ".{0,80}settings\.json.{0,80}")
foreach ($m in $matchesJson) {
    Write-Host "--> $($m.Value)"
}

Write-Host "`nOccurrences of SetFunction or WMI calls in AcerCCAgent.exe:"
$matchesWmi = [regex]::Matches($text, ".{0,80}(SetFunction|AcerGenericMethod|root\\wmi).{0,80}")
foreach ($m in $matchesWmi) {
    Write-Host "--> $($m.Value)"
}
