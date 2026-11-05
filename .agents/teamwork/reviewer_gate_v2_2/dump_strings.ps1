# dump_strings.ps1
$lines = [System.IO.File]::ReadAllLines("c:\Users\caner\Documents\antigravity\eager-shannon\NitroLite\MainWindow.xaml.cs", [System.Text.Encoding]::UTF8)

$userStrings = @()
for ($i = 0; $i -lt $lines.Count; $i++) {
    $line = $lines[$i]
    if ($line -match 'ShowToast\s*\(\s*"(.*?)"' -or 
        $line -match 'ShowToast\s*\(\s*\$"(.*?)"' -or 
        $line -match 'MessageBox\.Show\s*\(\s*"(.*?)"' -or 
        $line -match 'MessageBox\.Show\s*\(\s*\$"(.*?)"' -or 
        $line -match '\.(?:Text|Content|Header)\s*=\s*"(.*?)"' -or
        $line -match '\.(?:Text|Content|Header)\s*=\s*\$"(.*?)"' -or
        $line -match 'new\s+MenuItem\s*\{\s*Header\s*=\s*"(.*?)"') {
        
        $userStrings += [PSCustomObject]@{
            Line = $i + 1
            Content = $line.Trim()
        }
    }
}

Write-Host "Total user strings matched: $($userStrings.Count)"
$userStrings | Format-Table -Wrap -AutoSize
