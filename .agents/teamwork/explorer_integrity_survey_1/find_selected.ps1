$mainCs = "c:\Users\caner\Documents\antigravity\eager-shannon\NitroLite\MainWindow.xaml.cs"
$lines = [System.IO.File]::ReadAllLines($mainCs)

Write-Output "=== SelectedItem / SelectedValue / Content occurrences in MainWindow.xaml.cs ==="
for ($i = 0; $i -lt $lines.Length; $i++) {
    $line = $lines[$i]
    if ($line -match 'SelectedItem|SelectedValue|\.Content\b') {
        Write-Output "Line $($i+1): $($line.Trim())"
    }
}
