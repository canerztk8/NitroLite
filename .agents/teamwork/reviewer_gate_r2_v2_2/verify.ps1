# Independent Verification Script for reviewer_gate_r2_v2_2

$baseDirs = @(
    "c:\Users\caner\Documents\antigravity\eager-shannon\NitroLite",
    "c:\Users\caner\Documents\antigravity\eager-shannon\NitroLite.Tests"
)

Write-Host "=== 1. Scanning for Emojis (Surrogate pairs & pictographs) ==="
$files = Get-ChildItem -Path $baseDirs -Recurse -Include *.cs,*.xaml | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }
$emojiFound = 0

foreach ($f in $files) {
    $lines = Get-Content -LiteralPath $f.FullName -Encoding UTF8
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]
        # Match surrogate pairs
        $m1 = [regex]::Matches($line, "[\uD800-\uDBFF][\uDC00-\uDFFF]")
        if ($m1.Count -gt 0) {
            foreach ($match in $m1) {
                Write-Host "EMOJI FOUND in $($f.FullName):$($i+1) -> $($match.Value) in line: $line"
                $emojiFound++
            }
        }
        # Match other common emoji ranges: 2600-27BF except standard close ✕ (2715)
        $m2 = [regex]::Matches($line, "[\u2600-\u2714\u2716-\u27BF]")
        if ($m2.Count -gt 0) {
            foreach ($match in $m2) {
                Write-Host "SYMBOL FOUND in $($f.FullName):$($i+1) -> $($match.Value) in line: $line"
                $emojiFound++
            }
        }
    }
}
Write-Host "Total Emojis Found: $emojiFound"

Write-Host "`n=== 2. Scanning for Em-dashes (U+2014) ==="
$emDashFound = 0
foreach ($f in $files) {
    $lines = Get-Content -LiteralPath $f.FullName -Encoding UTF8
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]
        $m = [regex]::Matches($line, "\u2014")
        if ($m.Count -gt 0) {
            Write-Host "EM-DASH FOUND in $($f.FullName):$($i+1) -> $line"
            $emDashFound++
        }
    }
}
Write-Host "Total Em-dashes Found: $emDashFound"

Write-Host "`n=== 3. Scanning for AI Buzzwords ==="
$buzzPattern = 'Akıllı|Mükemmel|Harika|Stüdyo|RAM Yiyor|Tebrikler|Sihirli|Proaktif'
$buzzFound = 0
foreach ($f in $files) {
    $lines = Get-Content -LiteralPath $f.FullName -Encoding UTF8
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]
        if ($line -match $buzzPattern) {
            Write-Host "BUZZWORD FOUND in $($f.FullName):$($i+1) -> $line"
            $buzzFound++
        }
    }
}
Write-Host "Total Buzzwords Found: $buzzFound"
