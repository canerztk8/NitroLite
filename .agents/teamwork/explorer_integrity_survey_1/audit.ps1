$root = "c:\Users\caner\Documents\antigravity\eager-shannon"
$mainXaml = Join-Path $root "NitroLite\MainWindow.xaml"
$mainCs = Join-Path $root "NitroLite\MainWindow.xaml.cs"
$osdXaml = Join-Path $root "NitroLite\OsdOverlayWindow.xaml"
$osdCs = Join-Path $root "NitroLite\OsdOverlayWindow.xaml.cs"

Write-Output "=== 1. MainWindow.xaml x:Name Audit ==="
$mainXamlContent = [System.IO.File]::ReadAllText($mainXaml)
$mainCsContent = [System.IO.File]::ReadAllText($mainCs)

$xNameMatches = [System.Text.RegularExpressions.Regex]::Matches($mainXamlContent, 'x:Name="([^"]+)"')
$xNames = @()
foreach ($m in $xNameMatches) {
    $name = $m.Groups[1].Value
    if ($xNames -notcontains $name) {
        $xNames += $name
    }
}

Write-Output "Total unique x:Name in MainWindow.xaml: $($xNames.Count)"
$usedNames = @()
$unusedNames = @()
foreach ($name in $xNames) {
    if ($mainCsContent -match "\b$name\b") {
        $usedNames += $name
    } else {
        $unusedNames += $name
    }
}
Write-Output "Referenced in MainWindow.xaml.cs: $($usedNames.Count)"
Write-Output "NOT referenced in MainWindow.xaml.cs: $($unusedNames.Count)"
Write-Output "Referenced x:Names:"
$usedNames | Sort-Object | ForEach-Object { Write-Output "  - $_" }
Write-Output "Unreferenced x:Names (purely XAML/template internal):"
$unusedNames | Sort-Object | ForEach-Object { Write-Output "  - $_" }

Write-Output "`n=== 2. MainWindow.xaml Event Handlers ==="
$handlerPattern = '\b(Click|Checked|Unchecked|ValueChanged|SelectionChanged|MouseLeftButtonDown|MouseRightButtonDown|KeyDown|KeyUp|Loaded|Closing|Closed)="([^"]+)"'
$handlerMatches = [System.Text.RegularExpressions.Regex]::Matches($mainXamlContent, $handlerPattern)
$handlers = @{}
foreach ($m in $handlerMatches) {
    $evt = $m.Groups[1].Value
    $fn = $m.Groups[2].Value
    $key = "$evt -> $fn"
    if (-not $handlers.ContainsKey($key)) {
        $handlers[$key] = @{ Event = $evt; Handler = $fn; ExistsInCs = ($mainCsContent -match "\b$fn\b") }
    }
}
Write-Output "Total Event Handler bindings: $($handlers.Count)"
foreach ($h in $handlers.Values | Sort-Object { $_.Event + $_.Handler }) {
    Write-Output "  $($h.Event): $($h.Handler) (Found in CS: $($h.ExistsInCs))"
}

Write-Output "`n=== 3. OsdOverlayWindow.xaml Audit ==="
$osdXamlContent = [System.IO.File]::ReadAllText($osdXaml)
$osdCsContent = [System.IO.File]::ReadAllText($osdCs)

$osdXNames = [System.Text.RegularExpressions.Regex]::Matches($osdXamlContent, 'x:Name="([^"]+)"')
$osdNames = @()
foreach ($m in $osdXNames) {
    $name = $m.Groups[1].Value
    if ($osdNames -notcontains $name) {
        $osdNames += $name
    }
}
Write-Output "Total unique x:Name in OsdOverlayWindow.xaml: $($osdNames.Count)"
foreach ($name in $osdNames | Sort-Object) {
    $inCs = $osdCsContent -match "\b$name\b"
    Write-Output "  - $name (Used in CS: $inCs)"
}

$osdHandlers = [System.Text.RegularExpressions.Regex]::Matches($osdXamlContent, $handlerPattern)
Write-Output "Event handlers in OsdOverlayWindow.xaml:"
foreach ($m in $osdHandlers) {
    Write-Output "  $($m.Groups[1].Value): $($m.Groups[2].Value) (Found in CS: $($osdCsContent -match '\b' + $m.Groups[2].Value + '\b'))"
}
