$mainXaml = Get-Content -LiteralPath "c:\Users\caner\Documents\antigravity\eager-shannon\NitroLite\MainWindow.xaml" -Raw -Encoding UTF8
$mainCs = Get-Content -LiteralPath "c:\Users\caner\Documents\antigravity\eager-shannon\NitroLite\MainWindow.xaml.cs" -Raw -Encoding UTF8

$eventMatches = [regex]::Matches($mainXaml, '(Click|MouseLeftButtonDown|SelectionChanged|Checked|Unchecked|ValueChanged|PreviewKeyDown|Loaded|Unloaded|Closing|Closed)="([^"]+)"')

$handlers = [System.Collections.Generic.HashSet[string]]::new()
foreach ($m in $eventMatches) {
    $val = $m.Groups[2].Value
    # Skip bindings like {Binding ...} or literals like True/False
    if ($val -notmatch '^{Binding' -and $val -ne 'True' -and $val -ne 'False') {
        $handlers.Add($val) | Out-Null
    }
}

Write-Host "Unique custom event handlers in MainWindow.xaml: $($handlers.Count)"
$missingHandlers = @()
foreach ($h in $handlers) {
    # Check if method exists in MainWindow.xaml.cs
    if ($mainCs -notmatch "(void|async\s+void|Task|async\s+Task)\s+$h\s*\(") {
        $missingHandlers += $h
    }
}

if ($missingHandlers.Count -eq 0) {
    Write-Host "ALL $($handlers.Count) event handler methods exist in MainWindow.xaml.cs!"
} else {
    Write-Host "Missing handlers: $($missingHandlers -join ', ')"
}
