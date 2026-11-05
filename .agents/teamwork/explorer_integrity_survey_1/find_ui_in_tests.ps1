$testsDir = "c:\Users\caner\Documents\antigravity\eager-shannon\NitroLite.Tests"
$testFiles = Get-ChildItem -Path $testsDir -Filter "*.cs"

Write-Output "=== Matching UI elements / Window / Control in NitroLite.Tests ==="
foreach ($file in $testFiles) {
    $lines = [System.IO.File]::ReadAllLines($file.FullName)
    for ($i = 0; $i -lt $lines.Length; $i++) {
        $line = $lines[$i]
        if ($line -match 'Window|Control|Button|TextBox|Slider|ComboBox|ProgressBar|Border|Canvas') {
            Write-Output "$($file.Name):$($i+1): $($line.Trim())"
        }
    }
}
