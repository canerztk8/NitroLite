$mainCs = "c:\Users\caner\Documents\antigravity\eager-shannon\NitroLite\MainWindow.xaml.cs"
$lines = [System.IO.File]::ReadAllLines($mainCs)

Write-Output "=== Checks on .Content or .Text in MainWindow.xaml.cs ==="
for ($i = 0; $i -lt $lines.Length; $i++) {
    $line = $lines[$i]
    if ($line -match '\.(Content|Text)\s*(==|!=|\.Equals|\.StartsWith|\.Contains)') {
        Write-Output "Line $($i+1): $($line.Trim())"
    }
}
