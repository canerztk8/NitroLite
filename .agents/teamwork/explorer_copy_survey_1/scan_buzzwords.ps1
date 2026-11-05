$terms = @("mükemmel", "harika", "akıllı", "ferah", "tertemiz", "tebrikler", "stüdyo", "proaktif", "asistan", "zengin", "afterburner", "ram yiyor", "tek tıkla", "serin eco", "son hız", "gayet serin")
$files = Get-ChildItem -Path NitroLite -Recurse -Include *.xaml,*.cs | Where-Object { $_.FullName -notmatch '\\obj\\' -and $_.FullName -notmatch '\\bin\\' }

foreach ($f in $files) {
    $lines = [System.IO.File]::ReadAllLines($f.FullName, [System.Text.Encoding]::UTF8)
    for ($i = 0; $i -lt $lines.Length; $i++) {
        $line = $lines[$i]
        foreach ($t in $terms) {
            if ($line.ToLowerInvariant().Contains($t)) {
                Write-Output "$($f.FullName):$($i + 1): [$t] $($line.Trim())"
                break
            }
        }
    }
}
