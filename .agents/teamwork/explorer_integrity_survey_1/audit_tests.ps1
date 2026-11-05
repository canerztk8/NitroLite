$testsDir = "c:\Users\caner\Documents\antigravity\eager-shannon\NitroLite.Tests"
$testFiles = Get-ChildItem -Path $testsDir -Filter "*.cs"

Write-Output "=== NitroLite.Tests Analysis ==="
Write-Output "Total test files: $($testFiles.Count)"

foreach ($file in $testFiles) {
    $content = [System.IO.File]::ReadAllText($file.FullName)
    $testMethods = [System.Text.RegularExpressions.Regex]::Matches($content, '\[Fact\]\s+public\s+(?:async\s+Task|void)\s+(\w+)|\[Theory\]\s+public\s+(?:async\s+Task|void)\s+(\w+)')
    Write-Output "`nFile: $($file.Name) ($($testMethods.Count) tests)"
    
    # Check for references to MainWindow, OsdOverlayWindow, XAML, UI
    $hasMainWindow = $content -match "MainWindow"
    $hasOsdWindow = $content -match "OsdOverlayWindow"
    $hasTelemetryFormatter = $content -match "TelemetryFormatter"
    Write-Output "  References: MainWindow: $hasMainWindow | OsdOverlayWindow: $hasOsdWindow | TelemetryFormatter: $hasTelemetryFormatter"

    # List all [Fact] or [Theory] method names
    foreach ($m in $testMethods) {
        $name = if ($m.Groups[1].Value) { $m.Groups[1].Value } else { $m.Groups[2].Value }
        Write-Output "    - $name"
    }
}
