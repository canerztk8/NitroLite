<#
.SYNOPSIS
    Turns the raw captures in scripts\raw into public\clips\*.mp4 (H.264, CRF 16, yuv420p, 60 fps) and checks them.
#>
$ErrorActionPreference = 'Stop'
$Raw = Join-Path $PSScriptRoot 'raw'
$Out = Join-Path $PSScriptRoot '..\public\clips'
New-Item -ItemType Directory -Force $Out | Out-Null
$enc = @('-c:v', 'libx264', '-preset', 'slow', '-crf', '16', '-pix_fmt', 'yuv420p', '-r', '60', '-an', '-movflags', '+faststart')

foreach ($n in '01-power-modes', '02-fan-curve', '03-rgb-effects', '04-game-profiles-osd', '05-battery-mux') {
    $src = Join-Path $Raw "$n.mkv"
    if (Test-Path $src) { & ffmpeg -y -hide_banner -loglevel error -i $src @enc (Join-Path $Out "$n.mp4") }
}

# 06: one 0.92 s cut per theme / language capture, joined in order
$parts = @(Get-ChildItem $Raw -Filter '06-*.mkv' | Sort-Object Name)
if ($parts.Count) {
    $args = @(); $chain = ''
    for ($i = 0; $i -lt $parts.Count; $i++) {
        $args += @('-ss', '0.4', '-t', '0.923', '-i', $parts[$i].FullName)
        $chain += "[${i}:v]fps=60,scale=1920:1080,setsar=1,format=yuv420p[v$i];"
    }
    $chain += (0..($parts.Count - 1) | ForEach-Object { "[v$_]" }) -join ''
    $chain += "concat=n=$($parts.Count):v=1:a=0[v]"
    & ffmpeg -y -hide_banner -loglevel error @args -filter_complex $chain -map '[v]' @enc (Join-Path $Out '06-themes-languages.mp4')
}

Get-ChildItem $Out -Filter *.mp4 | ForEach-Object {
    $info = & ffprobe -v error -count_frames -select_streams v:0 -show_entries 'stream=width,height,nb_read_frames:format=duration' -of csv=p=0 $_.FullName
    '{0,-28} {1}' -f $_.Name, (($info -join ' ') -replace '\s+', ' ')
}
