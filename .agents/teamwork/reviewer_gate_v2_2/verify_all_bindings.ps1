$xaml = [System.IO.File]::ReadAllText('NitroLite\MainWindow.xaml', [System.Text.Encoding]::UTF8)
$cs = [System.IO.File]::ReadAllText('NitroLite\MainWindow.xaml.cs', [System.Text.Encoding]::UTF8)

# Match only genuine event attributes (Click, Checked, Unchecked, SelectionChanged, ValueChanged, MouseLeftButtonDown)
# and ensure it's not a property like IsChecked
$eventRegex = '(?<![A-Za-z0-9_])(Click|Checked|Unchecked|SelectionChanged|ValueChanged|MouseLeftButtonDown)\s*=\s*"([A-Za-z0-9_]+)"'
$matches = [regex]::Matches($xaml, $eventRegex)

$bindingSummary = @{}
$missingHandlers = @()
$totalEventBindings = 0

foreach ($m in $matches) {
    $eventName = $m.Groups[1].Value
    $handlerName = $m.Groups[2].Value
    $totalEventBindings++
    
    if (-not $bindingSummary.ContainsKey($handlerName)) {
        $bindingSummary[$handlerName] = @{ Event = $eventName; Count = 0 }
    }
    $bindingSummary[$handlerName].Count++
    
    # Check if handler exists in C#
    if (-not ($cs -match "\b$handlerName\b")) {
        if ($missingHandlers -notcontains $handlerName) {
            $missingHandlers += $handlerName
        }
    }
}

Write-Host "Total genuine event binding attributes in MainWindow.xaml: $totalEventBindings"
Write-Host "Unique handler methods bound in XAML: $($bindingSummary.Keys.Count)"
Write-Host "Missing/Unimplemented handlers in MainWindow.xaml.cs: $(if ($missingHandlers.Count -eq 0) { 'NONE [100% Implemented]' } else { $missingHandlers -join ', ' })"

Write-Host "`nBreakdown of handlers and their binding counts:"
foreach ($key in ($bindingSummary.Keys | Sort-Object)) {
    Write-Host ("  {0,-35} ({1,-16}) : {2} bindings" -f $key, $bindingSummary[$key].Event, $bindingSummary[$key].Count)
}
