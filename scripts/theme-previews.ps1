<#
.SYNOPSIS
    Regenerates the theme preview thumbnails the settings page shows.

.DESCRIPTION
    The theme cards used to draw five colour swatches per palette. They now show a real screenshot of the
    app in that palette, taken from the English UI so one set of assets serves all eight languages.

    This script is the only supported way to produce those assets: it drives scripts/screenshot.ps1 once
    per palette, downscales each capture to -Width, and copies the result into NitroLite/Assets/themes
    under the palette's own name (light.jpg, dark.jpg, midnightoled.jpg, ...). The file name is what
    MainWindow.Theme.cs looks up, so renaming one breaks the card.

    Run it after any change that alters the layout of the Modes page, or the previews will show a window
    that no longer exists.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\theme-previews.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\theme-previews.ps1 -Width 600
#>
param(
    [string]$Out = '',
    [int]$Width = 400,
    [int]$Quality = 85,
    [string]$Language = 'en',
    [string]$Page = 'NavModes'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$shot = Join-Path $PSScriptRoot 'screenshot.ps1'
if (-not (Test-Path $shot)) { throw "Not found: $shot" }
if (-not $Out) { $Out = Join-Path $root 'NitroLite\Assets\themes' }

# The names match AppTheme.ToString().ToLowerInvariant(), which is how the card finds its file.
$themes = @('light', 'dark', 'midnightoled', 'slatesteel', 'amberconsole', 'nordfrost')

$tmp = Join-Path ([IO.Path]::GetTempPath()) ('nl-theme-previews-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmp -Force | Out-Null
New-Item -ItemType Directory -Path $Out -Force | Out-Null

try {
    foreach ($theme in $themes) {
        Write-Host "Capturing $theme ..." -ForegroundColor Cyan
        # A separate process per palette: screenshot.ps1 ends with exit 1 on a failed page, and that has to
        # stop this loop rather than take the whole run down silently.
        & powershell -NoProfile -ExecutionPolicy Bypass -File $shot `
            -Page $Page -Language $Language -Theme $theme `
            -MaxWidth $Width -Quality $Quality -Out $tmp -KeepOld
        if ($LASTEXITCODE -ne 0) { throw "screenshot.ps1 failed for '$theme' (exit $LASTEXITCODE)" }

        $src = Join-Path $tmp ("{0}-{1}-{2}.jpg" -f $Language, $theme, $Page)
        if (-not (Test-Path $src)) { throw "The capture is missing: $src" }
        Copy-Item $src (Join-Path $Out "$theme.jpg") -Force
    }
}
finally {
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ''
Get-ChildItem $Out -Filter *.jpg | Sort-Object Name |
    ForEach-Object { "  {0,-18} {1,7:N0} bytes" -f $_.Name, $_.Length }
Write-Host "Done: $($themes.Count) preview(s) in $Out" -ForegroundColor Green
