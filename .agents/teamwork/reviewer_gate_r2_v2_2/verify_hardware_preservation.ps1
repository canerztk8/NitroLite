# Hardware and XAML Preservation Verification Script for reviewer_gate_r2_v2_2

$mainWindowXamlPath = "c:\Users\caner\Documents\antigravity\eager-shannon\NitroLite\MainWindow.xaml"
$mainWindowCsPath = "c:\Users\caner\Documents\antigravity\eager-shannon\NitroLite\MainWindow.xaml.cs"
$osdXamlPath = "c:\Users\caner\Documents\antigravity\eager-shannon\NitroLite\OsdOverlayWindow.xaml"
$osdCsPath = "c:\Users\caner\Documents\antigravity\eager-shannon\NitroLite\OsdOverlayWindow.xaml.cs"

Write-Host "=== 1. Checking MainWindow.xaml x:Name Fields ==="
$mainXaml = Get-Content -LiteralPath $mainWindowXamlPath -Raw -Encoding UTF8
$mainNameMatches = [regex]::Matches($mainXaml, 'x:Name="([^"]+)"')
$mainNames = $mainNameMatches | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique
Write-Host "Unique x:Name count in MainWindow.xaml: $($mainNames.Count)"

Write-Host "`n=== 2. Checking OsdOverlayWindow.xaml x:Name Fields ==="
$osdXaml = Get-Content -LiteralPath $osdXamlPath -Raw -Encoding UTF8
$osdNameMatches = [regex]::Matches($osdXaml, 'x:Name="([^"]+)"')
$osdNames = $osdNameMatches | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique
Write-Host "Unique x:Name count in OsdOverlayWindow.xaml: $($osdNames.Count)"
$osdNames | ForEach-Object { Write-Host " - $_" }

Write-Host "`n=== 3. Checking Hardware Tag Attributes in MainWindow.xaml ==="
$tagMatches = [regex]::Matches($mainXaml, 'Tag="([^"]+)"')
$tags = $tagMatches | ForEach-Object { $_.Groups[1].Value }
Write-Host "Total Tag attributes in MainWindow.xaml: $($tags.Count)"
Write-Host "Tag values:"
$tags | ForEach-Object { Write-Host " - $_" }

Write-Host "`n=== 4. Checking Event Bindings in MainWindow.xaml ==="
# Events like Click="...", MouseLeftButtonDown="...", SelectionChanged="...", Checked="...", Unchecked="..."
$eventMatches = [regex]::Matches($mainXaml, '(Click|MouseLeftButtonDown|SelectionChanged|Checked|Unchecked|ValueChanged|PreviewKeyDown|Loaded|Unloaded|Closing|Closed)="([^"]+)"')
Write-Host "Total event bindings in MainWindow.xaml: $($eventMatches.Count)"
$uniqueHandlers = $eventMatches | ForEach-Object { "$($_.Groups[1].Value) -> $($_.Groups[2].Value)" } | Sort-Object -Unique
Write-Host "Unique event handlers count: $($uniqueHandlers.Count)"
$uniqueHandlers | ForEach-Object { Write-Host " - $_" }

Write-Host "`n=== 5. Checking 5 Dynamic SolidColorBrush Keys ==="
$requiredBrushes = @("AccentGreen", "AccentCyan", "AccentRed", "TextSecondary", "TextMuted")
foreach ($brush in $requiredBrushes) {
    $found = ($mainXaml -match "x:Key=`"$brush`"") -or ($mainXaml -match "FindResource\(`"$brush`"\)")
    Write-Host "Brush $brush defined: $found"
}

Write-Host "`n=== 6. Checking CmbRgbEffect Uppercase Socket Effect Tokens ==="
$requiredTokens = @("STATIC", "WAVE", "BREATHING", "NEON", "SHIFTING", "ZOOM", "METEOR", "TWINKLING", "OFF")
foreach ($tok in $requiredTokens) {
    # Check if ComboBoxItem Content or Tag contains $tok
    $tokMatch = $mainXaml -match "Content=`"$tok" -or $mainXaml -match ">$tok<" -or $mainXaml -match "Content=`"$tok "
    Write-Host "RGB Token '$tok' found in MainWindow.xaml: $tokMatch"
}
