$emojiRegex = [regex]'[\u2600-\u27BF\uD83C-\uDBFF\uDC00-\uDFFF\u25A0-\u25FF\u2300-\u23FF]'
$files = Get-ChildItem -Path NitroLite -Recurse -Include *.xaml,*.cs | Where-Object { $_.FullName -notmatch '\\obj\\' -and $_.FullName -notmatch '\\bin\\' }

foreach ($f in $files) {
    $lines = [System.IO.File]::ReadAllLines($f.FullName, [System.Text.Encoding]::UTF8)
    for ($i = 0; $i -lt $lines.Length; $i++) {
        $line = $lines[$i]
        if ($emojiRegex.IsMatch($line)) {
            Write-Output "$($f.FullName):$($i + 1): $($line.Trim())"
        }
    }
}
