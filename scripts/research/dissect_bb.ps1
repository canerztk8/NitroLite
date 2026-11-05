# Deeper analysis of AcerCCAgent.exe battery routines
$ErrorActionPreference = "Continue"

$ccExe = "C:\Windows\system32\AcerCCAgent.exe"
$bytes = [System.IO.File]::ReadAllBytes($ccExe)

# Search for "CallBIOSSetFunction" offset
$pattern = [System.Text.Encoding]::ASCII.GetBytes("CallBIOSSetFunction")
$pos = 0
$foundOffsets = @()
for ($i = 0; $i -lt ($bytes.Length - $pattern.Length); $i++) {
    $match = $true
    for ($j = 0; $j -lt $pattern.Length; $j++) {
        if ($bytes[$i + $j] -ne $pattern[$j]) {
            $match = $false
            break
        }
    }
    if ($match) {
        $foundOffsets += $i
    }
}

Write-Host "Found 'CallBIOSSetFunction' at offsets: $($foundOffsets -join ', ')"

# Search for "BatteryBoundary"
$patternBB = [System.Text.Encoding]::ASCII.GetBytes("BatteryBoundary")
$foundBB = @()
for ($i = 0; $i -lt ($bytes.Length - $patternBB.Length); $i++) {
    $match = $true
    for ($j = 0; $j -lt $patternBB.Length; $j++) {
        if ($bytes[$i + $j] -ne $patternBB[$j]) {
            $match = $false
            break
        }
    }
    if ($match) {
        $foundBB += $i
    }
}
Write-Host "Found 'BatteryBoundary' at offsets: $($foundBB -join ', ')"

# Look at context around first BatteryBoundary
if ($foundBB.Count -gt 0) {
    $start = [Math]::Max(0, $foundBB[0] - 200)
    $len = [Math]::Min($bytes.Length - $start, 500)
    $slice = [System.Text.Encoding]::ASCII.GetString($bytes, $start, $len)
    Write-Host "`nContext around BatteryBoundary offset $($foundBB[0]):"
    Write-Host $slice
}
