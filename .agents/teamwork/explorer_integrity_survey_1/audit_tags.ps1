$mainXaml = "c:\Users\caner\Documents\antigravity\eager-shannon\NitroLite\MainWindow.xaml"
$lines = [System.IO.File]::ReadAllLines($mainXaml)

Write-Output "=== Tag attributes in MainWindow.xaml ==="
for ($i = 0; $i -lt $lines.Length; $i++) {
    if ($lines[$i] -match 'Tag="([^"]+)"') {
        Write-Output "Line $($i+1): $($lines[$i].Trim())"
    }
}
