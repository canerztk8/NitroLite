$files = @(
    "c:\Users\caner\Documents\antigravity\eager-shannon\NitroLite\MainWindow.xaml",
    "c:\Users\caner\Documents\antigravity\eager-shannon\NitroLite\MainWindow.xaml.cs"
)
foreach ($f in $files) {
    $lines = Get-Content $f
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match '[\u2014]') {
            Write-Host "$f : Line $($i + 1) : $($lines[$i])"
            # Print unicode code points of characters in this line
            foreach ($ch in $lines[$i].ToCharArray()) {
                $code = [int][char]$ch
                if ($code -ge 8200) {
                    Write-Host "Unicode char: $ch (U+$($code.ToString('X4')))"
                }
            }
        }
    }
}
