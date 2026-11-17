<#
.SYNOPSIS
    Captures NitroLite pages to small JPEGs without leaving the window on your desktop.

.DESCRIPTION
    Starts NitroLite against a throwaway data folder, parks the window out of the way, walks the
    requested pages, captures each one with PrintWindow (which renders even when the window is
    covered or off-screen), then closes the app and deletes the folder.

    Your real settings in %LOCALAPPDATA%\NitroLite are never read or written.

    Pages are addressed by AutomationId, not by their visible label, so the same command works in
    every language: NavModes, NavMonitoring, NavLighting, NavBattery, NavTools.

    The two pages the retired WPF build called NavRgb and NavDebloat are still accepted and mapped onto
    NavLighting and NavTools, so an old command line keeps working. The nav rows are clicked through
    whichever UI Automation pattern the running front end exposes: Avalonia's rail is a list of Buttons
    (InvokePattern) where WPF's was a list of RadioButtons (SelectionItemPattern).

    Every capture is downscaled to -MaxWidth (1280px by default) and written as a JPEG, because a
    full-size PNG of a 1325px window is several times larger than anything needed to check layout
    and colour. The output folder is cleared of the previous run's captures first, so a folder never
    accumulates images that then get read back into a session.

.PARAMETER Page
    AutomationId of the page to capture. Ignored when -All is given.

.PARAMETER All
    Capture all five pages.

.PARAMETER Theme
    warmcream (the default, aliased as light), dark, midnightoled, slatesteel, amberconsole or nordfrost.

.PARAMETER Language
    Language code: en, tr, de, es, ru, pt, hi, id.

.PARAMETER Out
    Output folder. One image per page, named <language>-<theme>-<page>.<ext>. Captures left there by
    an earlier run are deleted before the first shot.

.PARAMETER MaxWidth
    Downscale each capture to this width in pixels before saving. Default 1280. A window already
    narrower than this is saved at its own size; the aspect ratio is always preserved.

.PARAMETER Format
    jpeg (default) or png. JPEG at the default quality is roughly a tenth of the PNG size.

.PARAMETER Quality
    JPEG quality, 1-100. Default 78, which keeps text edges legible.

.PARAMETER KeepOld
    Leave the previous captures in the output folder instead of clearing them first.

.PARAMETER Position
    corner (default), bottom, center, or an explicit "X,Y".

    corner parks the window at 40,40. bottom does the same but also pushes it to the back of the
    z-order, so it stays hidden behind whatever you already have open. There is no fully invisible
    mode: WPF stops rendering a window that is completely off-screen, and PrintWindow then returns
    a stale frame.

.PARAMETER Size
    Window size as "WxH". Default 1325x913.

.PARAMETER Scrolls
    Large scroll increments before each capture.

.PARAMETER Exe
    Path to NitroLite.Desktop.exe. Defaults to the Release build, then the Debug build.

.PARAMETER KeepOpen
    Leave the app running and the data folder in place, for poking at it afterwards.

.PARAMETER MouseProfile
    Turns ATK mouse integration on with this default profile (0-4), so the sidebar's FARE row is
    captured. Omit it to leave the integration off, as a fresh install has it.

.PARAMETER FanCurve
    Turns the software fan curve on, so the curve editor is captured instead of the fixed-speed sliders.

.PARAMETER FanCurveFlat
    Turns the curve on with a flat shape at this percentage, so a fixed fan speed can be measured.

.PARAMETER GameFanCurve
    Turns the selected game's own fan curve on, so the six in-card anchors are captured instead of the
    "copy from desktop" row alone. Needs a game profile to be selected, which -Seed can supply.

.PARAMETER Settle
    Extra milliseconds to wait after the window is parked, before the first capture. Use it when a
    value is still being fetched at startup (for example the ATK mouse profile).

.PARAMETER Seed
    Folder whose files are copied into the throwaway data folder before launch, so a page that reads
    a data file (battery wear history, game profiles, RGB presets) can be captured with content in
    it. settings.json is written after the copy, so -Language and -Theme still win.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\screenshot.ps1 -All -Theme dark -Language de

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\screenshot.ps1 -Page NavBattery -Out C:\temp\pil
#>
param(
    [string]$Page = 'NavModes',
    [switch]$All,
    [ValidateSet('warmcream', 'light', 'dark', 'midnightoled', 'slatesteel', 'amberconsole', 'nordfrost')][string]$Theme = 'warmcream',
    [string]$Language = 'tr',
    [string]$Out = '',
    [int]$MaxWidth = 1280,
    [ValidateSet('jpeg', 'png')][string]$Format = 'jpeg',
    [ValidateRange(1, 100)][int]$Quality = 78,
    [switch]$KeepOld,
    [string]$Position = 'corner',
    [string]$Size = '1325x913',
    [int]$Scrolls = 0,
    [string]$Exe = '',
    [switch]$KeepOpen,
    [ValidateRange(0, 4)][int]$MouseProfile,
    [switch]$FanCurve,
    [ValidateRange(30, 100)][int]$FanCurveFlat,
    [switch]$GameFanCurve,
    [int]$Settle = 0,
    [string]$Seed = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public class Shot {
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr v);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
    public delegate bool EnumProc(IntPtr h, IntPtr p);
    public struct RECT { public int Left, Top, Right, Bottom; }
    public static List<IntPtr> ForPid(uint target) {
        var list = new List<IntPtr>();
        EnumWindows((h, p) => {
            uint pid; GetWindowThreadProcessId(h, out pid);
            if (pid == target && IsWindowVisible(h) && GetWindowTextLength(h) > 0) list.Add(h);
            return true;
        }, IntPtr.Zero);
        return list;
    }
}
"@
[Shot]::SetProcessDpiAwarenessContext([IntPtr](-4)) | Out-Null

# ---- resolve the executable -------------------------------------------------------------------
if (-not $Exe) {
    $Exe = @(
        (Join-Path $root 'NitroLite.Desktop\bin\Release\net10.0-windows\NitroLite.Desktop.exe'),
        (Join-Path $root 'NitroLite.Desktop\bin\Debug\net10.0-windows\NitroLite.Desktop.exe')
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $Exe) { throw 'NitroLite.Desktop.exe not found. Build it first, or pass -Exe.' }
}
if (-not (Test-Path $Exe)) { throw "Not found: $Exe" }

# ---- the single-instance mutex is machine-wide, so a running copy has to go --------------------
$running = Get-Process NitroLite.Desktop -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "Stopping the running NitroLite.Desktop (the single-instance mutex would swallow the test copy)." -ForegroundColor Yellow
    $running | Stop-Process -Force
    Start-Sleep -Milliseconds 1200
}

# ---- throwaway data folder --------------------------------------------------------------------
$dataDir = Join-Path ([IO.Path]::GetTempPath()) ('nl-shot-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $dataDir | Out-Null
if ($Seed) {
    if (-not (Test-Path $Seed)) { throw "Seed folder not found: $Seed" }
    Copy-Item -Path (Join-Path $Seed '*') -Destination $dataDir -Force
}
# The palette is stored by name, so the capture script names it rather than numbering it.
$themeValue = switch ($Theme) {
    'warmcream' { 'WarmCream' }
    'light' { 'WarmCream' }
    'dark' { 'Dark' }
    'midnightoled' { 'MidnightOled' }
    'slatesteel' { 'SlateSteel' }
    'amberconsole' { 'AmberConsole' }
    'nordfrost' { 'NordFrost' }
}
$json = "{`r`n  `"language`": `"$Language`",`r`n  `"theme`": `"$themeValue`""
if ($PSBoundParameters.ContainsKey('MouseProfile')) {
    $json += ",`r`n  `"atk_mouse_integration_enabled`": true,`r`n  `"default_atk_mouse_profile`": $MouseProfile"
}
if ($FanCurve) {
    $json += ",`r`n  `"fan_curve_enabled`": true"
}
if ($PSBoundParameters.ContainsKey('FanCurveFlat')) {
    $json += ",`r`n  `"fan_curve_enabled`": true,`r`n  `"fan_curve_points`": [`r`n    { `"TemperatureC`": 40, `"Percent`": $FanCurveFlat },`r`n    { `"TemperatureC`": 90, `"Percent`": $FanCurveFlat }`r`n  ]"
}
$json += "`r`n}`r`n"
[IO.File]::WriteAllText((Join-Path $dataDir 'settings.json'), $json, (New-Object Text.UTF8Encoding($false)))

if (-not $Out) { $Out = Join-Path ([IO.Path]::GetTempPath()) 'nl-shots' }
New-Item -ItemType Directory -Path $Out -Force | Out-Null

# ---- clear the previous run's captures ---------------------------------------------------------
# Only files this script names are touched, so pointing -Out at a folder that holds other images is
# safe. The downscale below is pointless if a stale folder keeps handing back full-size PNGs.
if (-not $KeepOld) {
    $stale = @(Get-ChildItem -Path $Out -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^[a-z]{2,3}-[a-z]+-Nav[A-Za-z]+\.(png|jpg|jpeg)$' })
    foreach ($f in $stale) { Remove-Item $f.FullName -Force -ErrorAction SilentlyContinue }
    if ($stale.Count -gt 0) { Write-Host "Cleared $($stale.Count) old capture(s) from $Out" }
}

$ext = if ($Format -eq 'png') { 'png' } else { 'jpg' }
$jpegCodec = [System.Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() |
    Where-Object { $_.MimeType -eq 'image/jpeg' } | Select-Object -First 1
if ($Format -ne 'png' -and -not $jpegCodec) { throw 'No JPEG encoder is available; pass -Format png.' }

$pages = if ($All) { @('NavModes', 'NavMonitoring', 'NavLighting', 'NavBattery', 'NavTools') } else { @($Page) }

# The WPF build named the last two pages NavRgb and NavDebloat. Both spellings resolve to the same page, so a
# command line written against the old build still captures the right tab instead of failing on a missing id.
$pageAliases = @{
    'NavRgb'     = 'NavLighting'
    'NavDebloat' = 'NavTools'
}
$pages = @($pages | ForEach-Object { if ($pageAliases.ContainsKey($_)) { $pageAliases[$_] } else { $_ } })

# ---- launch -----------------------------------------------------------------------------------
$env:NITROLITE_DATA_DIR = $dataDir
$proc = Start-Process -FilePath $Exe -PassThru
Write-Host "Started NitroLite (pid $($proc.Id)), data folder $dataDir"

$hwnd = [IntPtr]::Zero
for ($i = 0; $i -lt 60; $i++) {
    Start-Sleep -Milliseconds 500
    if ($proc.HasExited) { throw "NitroLite exited during startup (code $($proc.ExitCode))." }
    $handles = [Shot]::ForPid([uint32]$proc.Id)
    if ($handles.Count -gt 0) {
        $bestArea = 0
        foreach ($h in $handles) {
            $r = New-Object Shot+RECT
            [Shot]::GetWindowRect($h, [ref]$r) | Out-Null
            $area = ($r.Right - $r.Left) * ($r.Bottom - $r.Top)
            if ($area -gt $bestArea) { $bestArea = $area; $hwnd = $h }
        }
        break
    }
}
if ($hwnd -eq [IntPtr]::Zero) { throw 'The main window never appeared.' }

# ---- park the window --------------------------------------------------------------------------
$parts = $Size -split 'x'
$winW = [int]$parts[0]
$winH = [int]$parts[1]
$after = [IntPtr]::Zero   # SWP_NOZORDER: leave the z-order alone
switch ($Position) {
    'corner' { $x = 40; $y = 40 }
    'bottom' { $x = 40; $y = 40; $after = [IntPtr]1 }   # HWND_BOTTOM
    'center' {
        $screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
        $x = [int](($screen.Width - $winW) / 2)
        $y = [int](($screen.Height - $winH) / 2)
    }
    default {
        $xy = $Position -split ','
        if ($xy.Count -ne 2) { throw "Position must be corner, bottom, center or X,Y - got '$Position'." }
        $x = [int]$xy[0]; $y = [int]$xy[1]
    }
}
# SWP_NOACTIVATE: move and resize without stealing focus.
[Shot]::SetWindowPos($hwnd, $after, $x, $y, $winW, $winH, 0x0010) | Out-Null
Start-Sleep -Milliseconds (900 + $Settle)

# ---- capture ----------------------------------------------------------------------------------
$rootEl = [System.Windows.Automation.AutomationElement]::RootElement
$pidCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)
$win = $rootEl.FindFirst([System.Windows.Automation.TreeScope]::Children, $pidCond)
if (-not $win) { throw 'The window is not reachable through UI Automation.' }

function Save-Window([IntPtr]$handle, [string]$path) {
    $r = New-Object Shot+RECT
    [Shot]::GetWindowRect($handle, [ref]$r) | Out-Null
    $w = $r.Right - $r.Left
    $h = $r.Bottom - $r.Top
    if ($w -le 0 -or $h -le 0) { throw "The window has no size ($w x $h)." }
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc()
    $ok = [Shot]::PrintWindow($handle, $hdc, 2)
    $g.ReleaseHdc($hdc)
    $g.Dispose()

    # Downscale before saving. The extra pixels of a 1325px window buy nothing when the image is only
    # read to check layout and colour, and they are what pushes a session over its image budget.
    $target = $bmp
    if ($MaxWidth -gt 0 -and $w -gt $MaxWidth) {
        $sh = [int][Math]::Round($h * ($MaxWidth / $w))
        $target = New-Object System.Drawing.Bitmap $MaxWidth, $sh
        $tg = [System.Drawing.Graphics]::FromImage($target)
        $tg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $tg.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $tg.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $tg.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $tg.DrawImage($bmp, 0, 0, $MaxWidth, $sh)
        $tg.Dispose()
    }

    if ($Format -eq 'png') {
        $target.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    } else {
        $ep = New-Object System.Drawing.Imaging.EncoderParameters 1
        $ep.Param[0] = New-Object System.Drawing.Imaging.EncoderParameter(
            [System.Drawing.Imaging.Encoder]::Quality, [int]$Quality)
        $target.Save($path, $jpegCodec, $ep)
        $ep.Dispose()
    }

    if (-not [Object]::ReferenceEquals($target, $bmp)) { $target.Dispose() }
    $bmp.Dispose()
    return $ok
}

# The rail is a list of Buttons in Avalonia and was a list of RadioButtons in WPF, so the two front ends expose
# different patterns for the same click. Trying both is what lets one script drive either build; GetCurrentPattern
# throws when the pattern is unsupported, which is the only signal UI Automation gives.
function Select-Page([System.Windows.Automation.AutomationElement]$element) {
    try {
        $element.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        return $true
    } catch { }
    try {
        $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        return $true
    } catch { }
    return $false
}

# The same problem for a switch: Avalonia's ToggleButton and WPF's CheckBox both expose TogglePattern, but a
# ToggleButton that has been restyled can lose it, so the click is the fallback.
function Set-SwitchOn([System.Windows.Automation.AutomationElement]$element) {
    try {
        $tp = $element.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
        if ($tp.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::On) { $tp.Toggle() }
        return $true
    } catch { }
    try {
        $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        return $true
    } catch { }
    return $false
}

$failed = 0
foreach ($pageId in $pages) {
    $idCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $pageId)
    $nav = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $idCond)
    if (-not $nav) {
        Write-Host "  $pageId : nav button not found" -ForegroundColor Red
        $failed++
        continue
    }
    if (-not (Select-Page $nav)) {
        Write-Host "  $pageId : the nav row exposes neither InvokePattern nor SelectionItemPattern" -ForegroundColor Red
        $failed++
        continue
    }
    Start-Sleep -Milliseconds 1400

    # Toggled before scrolling: opening the panel makes the page taller, so the scroll has to see the final layout.
    if ($GameFanCurve) {
        $chkCond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'ChkGameFanCurve')
        $chk = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $chkCond)
        if ($chk) {
            Set-SwitchOn $chk | Out-Null
            Start-Sleep -Milliseconds 500
        } else {
            Write-Host "  ChkGameFanCurve not found" -ForegroundColor Yellow
        }
    }

    if ($Scrolls -ne 0) {
        $scrollCond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::IsScrollPatternAvailableProperty, $true)
        $scrollers = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $scrollCond)
        foreach ($s in $scrollers) {
            $sp = $s.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
            if (-not $sp.Current.VerticallyScrollable) { continue }
            # Scroll(LargeIncrement) is a no-op on WPF's ScrollViewer peer, so the step is computed here: one
            # viewport per scroll, which is what a large increment is meant to be.
            $step = [Math]::Max(1.0, $sp.Current.VerticalViewSize)
            for ($i = 0; $i -lt $Scrolls; $i++) {
                $next = [Math]::Min(100.0, $sp.Current.VerticalScrollPercent + $step)
                $sp.SetScrollPercent([System.Windows.Automation.ScrollPattern]::NoScroll, $next)
                Start-Sleep -Milliseconds 250
            }
            # The offset is applied on the UI thread and the poll timer can clamp it back while the page is
            # still settling, so the final position is asserted again right before the capture.
            Start-Sleep -Milliseconds 700
            $sp.SetScrollPercent([System.Windows.Automation.ScrollPattern]::NoScroll,
                                 [Math]::Min(100.0, $sp.Current.VerticalScrollPercent))
            break
        }
        Start-Sleep -Milliseconds 900
    }

    $file = Join-Path $Out ("{0}-{1}-{2}.{3}" -f $Language, $Theme, $pageId, $ext)
    $ok = Save-Window $hwnd $file
    if ($ok) {
        Write-Host ("  {0,-14} -> {1}" -f $pageId, $file) -ForegroundColor Green
    } else {
        Write-Host ("  {0,-14} -> {1} (PrintWindow returned false)" -f $pageId, $file) -ForegroundColor Yellow
        $failed++
    }
}

# ---- clean up ---------------------------------------------------------------------------------
if ($KeepOpen) {
    Write-Host "Left running (pid $($proc.Id)); data folder $dataDir" -ForegroundColor Yellow
} else {
    try { if (-not $proc.HasExited) { $proc.Kill() } } catch { }
    Start-Sleep -Milliseconds 800
    Remove-Item $dataDir -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ''
if ($failed -gt 0) { Write-Host "$failed page(s) failed." -ForegroundColor Red; exit 1 }
Write-Host "Done: $($pages.Count) capture(s) in $Out" -ForegroundColor Green
