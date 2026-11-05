# Inspect NitroSense app files for ADES and other service connections
$ErrorActionPreference = "Continue"

$appDir = "C:\Program Files\WindowsApps\ULICTekInc.NitroSenseforNotebook_5.0.1327.0_x64__nt9dgb7efx6bt\app"
if (Test-Path $appDir) {
    Write-Host "Searching NitroSense files for ADES or port references:"
    Get-ChildItem -Path $appDir -Recurse -Filter "*.js" -ErrorAction SilentlyContinue | ForEach-Object {
        $content = Get-Content $_.FullName -Raw
        if ($content -match "PrivacyPolicyConsensus|ADES|46933|15152|19443|4449|4343|51779") {
            Write-Host "Match in $($_.FullName):"
            [regex]::Matches($content, ".{0,50}(PrivacyPolicyConsensus|ADES|46933|15152|19443|4449|4343|51779).{0,50}") | ForEach-Object { $_.Value } | Select-Object -First 10
        }
    }
} else {
    Write-Host "AppDir not directly accessible or path differs."
}
