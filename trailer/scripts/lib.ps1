$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
if (-not ('W' -as [type])) {
Add-Type @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public class W {
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr v);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, int d, IntPtr e);
    public delegate bool EnumProc(IntPtr h, IntPtr p);
    public struct RECT { public int Left, Top, Right, Bottom; }
    public struct POINT { public int X, Y; }
    public static List<IntPtr> ForPid(uint target) {
        var list = new List<IntPtr>();
        EnumWindows((h, p) => { uint pid; GetWindowThreadProcessId(h, out pid);
            if (pid == target && IsWindowVisible(h) && GetWindowTextLength(h) > 0) list.Add(h); return true; }, IntPtr.Zero);
        return list;
    }
}
"@
}
[W]::SetProcessDpiAwarenessContext([IntPtr](-4)) | Out-Null
$AE = [System.Windows.Automation.AutomationElement]
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
# NITROLITE_EXE picks another build, e.g. the published single-file artifacts\NitroLite\NitroLite.exe
$Exe = if ($env:NITROLITE_EXE) { $env:NITROLITE_EXE } else { Join-Path $RepoRoot 'NitroLite.Desktop\bin\Release\net10.0-windows\NitroLite.Desktop.exe' }
$UserData = Join-Path $env:LOCALAPPDATA 'NitroLite'

# The recorded instance runs on a throwaway COPY of the user's data folder (game profiles, battery history, lighting),
# so the real %LOCALAPPDATA%\NitroLite is only read, never written. Only our own process is ever stopped.
function Start-NL([string]$theme = 'Dark', [string]$lang = 'en', [string]$onb = 'true', [int]$w = 1920, [int]$h = 1080, [int]$x = 320, [int]$y = 180, [string]$rec = '') {
    if ($script:Proc -and -not $script:Proc.HasExited) { Stop-NL }
    $script:DataDir = Join-Path $env:TEMP ('nl-video-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory $script:DataDir | Out-Null
    foreach ($f in 'game_profiles.json', 'battery_wear_history.json', 'thermal_health.json', 'settings.json') {
        $src = Join-Path $UserData $f; if (Test-Path $src) { Copy-Item $src $script:DataDir } }
    $sp = Join-Path $script:DataDir 'settings.json'
    $o = if (Test-Path $sp) { Get-Content $sp -Raw | ConvertFrom-Json } else { New-Object psobject }
    $set = @{ language = $lang; theme = $theme; onboarding_completed = ($onb -eq 'true'); StartMinimized = $false; OsdEnabled = $false; WindowMaximized = $false; auto_game_mode_enabled = $false }
    foreach ($k in $set.Keys) { $o | Add-Member -NotePropertyName $k -NotePropertyValue $set[$k] -Force }
    [IO.File]::WriteAllText($sp, ($o | ConvertTo-Json -Depth 20), (New-Object Text.UTF8Encoding($false)))
    $env:NITROLITE_DATA_DIR = $script:DataDir
    $env:NITROLITE_ALLOW_SECOND_INSTANCE = '1'
    $script:Proc = Start-Process -FilePath $Exe -PassThru
    $script:Hwnd = [IntPtr]::Zero
    for ($i = 0; $i -lt 60 -and $script:Hwnd -eq [IntPtr]::Zero; $i++) {
        Start-Sleep -Milliseconds 500
        $hs = [W]::ForPid([uint32]$script:Proc.Id); $best = 0
        foreach ($hh in $hs) { $r = New-Object W+RECT; [W]::GetWindowRect($hh, [ref]$r) | Out-Null
            $a = ($r.Right - $r.Left) * ($r.Bottom - $r.Top); if ($a -gt $best) { $best = $a; $script:Hwnd = $hh } }
    }
    if ($script:Hwnd -eq [IntPtr]::Zero) { throw 'no window' }
    # HWND_TOPMOST, physical pixels (this process is per-monitor DPI aware)
    [W]::SetWindowPos($script:Hwnd, [IntPtr](-1), $x, $y, $w, $h, 0x0040) | Out-Null
    [W]::SetForegroundWindow($script:Hwnd) | Out-Null
    if ($rec) { Start-Rec $rec }   # cold launch: record while the window is still populating
    Start-Sleep -Milliseconds 2500
    # the app restores its saved window size once it has loaded; pin the capture size again
    [W]::SetWindowPos($script:Hwnd, [IntPtr](-1), $x, $y, $w, $h, 0x0040) | Out-Null
    Start-Sleep -Milliseconds 300
    $cond = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $script:Proc.Id)
    $script:Win = $AE::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
}

function Stop-NL {
    try { if (-not $script:Proc.HasExited) { $script:Proc.CloseMainWindow() | Out-Null; if (-not $script:Proc.WaitForExit(4000)) { $script:Proc.Kill() } } } catch {}
    Start-Sleep -Milliseconds 800
    Copy-Item (Join-Path $script:DataDir 'nitrolite.log') (Join-Path $PSScriptRoot 'last.log') -ErrorAction SilentlyContinue
    Remove-Item $script:DataDir -Recurse -Force -ErrorAction SilentlyContinue
}

function Get-WinRect { $r = New-Object W+RECT; [W]::GetWindowRect($script:Hwnd, [ref]$r) | Out-Null; $r }

function Find-ById([string]$id) {
    $c = New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, $id)
    $script:Win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
}
function Find-ByName([string]$name, [int]$index = 0) {
    $c = New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $name)
    $all = $script:Win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $c)
    if ($all.Count -gt $index) { $all[$index] } else { $null }
}

function Dump-Tree([string]$path) {
    $all = $script:Win.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    $lines = foreach ($e in $all) { $c = $e.Current; $b = $c.BoundingRectangle
        if ($b.Width -gt 0) { '{0,-12} {1,-28} {2,-40} {3},{4} {5}x{6}' -f $c.ControlType.ProgrammaticName.Replace('ControlType.',''), $c.AutomationId, ($c.Name -replace '\s+',' '), [int]$b.X, [int]$b.Y, [int]$b.Width, [int]$b.Height } }
    $lines | Set-Content -Encoding utf8 $path
}

# --- cursor choreography -------------------------------------------------------------------------
function Ease([double]$t) { if ($t -lt 0.5) { 4 * $t * $t * $t } else { 1 - [Math]::Pow(-2 * $t + 2, 3) / 2 } }
function Move-To([double]$x, [double]$y, [int]$ms = 500) {
    $p = New-Object W+POINT; [W]::GetCursorPos([ref]$p) | Out-Null
    $steps = [Math]::Max(1, [int]($ms / 8))
    for ($i = 1; $i -le $steps; $i++) { $t = Ease ($i / $steps)
        [W]::SetCursorPos([int]($p.X + ($x - $p.X) * $t), [int]($p.Y + ($y - $p.Y) * $t)) | Out-Null
        Start-Sleep -Milliseconds 8 }
}
function Click-At([double]$x, [double]$y, [int]$ms = 500) {
    Move-To $x $y $ms; Start-Sleep -Milliseconds 90
    [W]::mouse_event(0x2, 0, 0, 0, [IntPtr]::Zero); Start-Sleep -Milliseconds 70
    [W]::mouse_event(0x4, 0, 0, 0, [IntPtr]::Zero)
}
function Center($el) { $b = $el.Current.BoundingRectangle; @(($b.X + $b.Width / 2), ($b.Y + $b.Height / 2)) }
function Click-El($el, [int]$ms = 500, [double]$dx = 0, [double]$dy = 0) {
    if (-not $el) { Write-Host 'element missing' -ForegroundColor Yellow; return }
    $c = Center $el; Click-At ($c[0] + $dx) ($c[1] + $dy) $ms
}
function Drag-From([double]$x1, [double]$y1, [double]$x2, [double]$y2, [int]$ms = 700) {
    Move-To $x1 $y1 400; Start-Sleep -Milliseconds 80
    [W]::mouse_event(0x2, 0, 0, 0, [IntPtr]::Zero); Start-Sleep -Milliseconds 80
    Move-To $x2 $y2 $ms; Start-Sleep -Milliseconds 80
    [W]::mouse_event(0x4, 0, 0, 0, [IntPtr]::Zero)
}
function Scroll-Wheel([int]$notches) { for ($i = 0; $i -lt [Math]::Abs($notches); $i++) {
    [W]::mouse_event(0x800, 0, 0, [int](-120 * [Math]::Sign($notches)), [IntPtr]::Zero); Start-Sleep -Milliseconds 60 } }
function Go-Page([string]$id) { Click-El (Find-ById $id) 600; Start-Sleep -Milliseconds 900 }

# --- recording -----------------------------------------------------------------------------------
function Start-Rec([string]$out) {
    $r = Get-WinRect
    $w = ($r.Right - $r.Left) - (($r.Right - $r.Left) % 2); $h = ($r.Bottom - $r.Top) - (($r.Bottom - $r.Top) % 2)
    $args = "-y -hide_banner -loglevel error -f gdigrab -framerate 60 -draw_mouse 1 -offset_x $($r.Left) -offset_y $($r.Top) -video_size ${w}x${h} -i desktop -c:v libx264 -preset ultrafast -crf 12 -pix_fmt yuv420p `"$out`""
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = (Get-Command ffmpeg).Source
    $psi.Arguments = $args
    $psi.UseShellExecute = $false
    $psi.RedirectStandardInput = $true
    $psi.CreateNoWindow = $true
    $script:Rec = [System.Diagnostics.Process]::Start($psi)
    Start-Sleep -Milliseconds 700
}
function Stop-Rec {
    Start-Sleep -Milliseconds 300
    try { $script:Rec.StandardInput.Write('q'); $script:Rec.StandardInput.Flush() } catch {}
    if (-not $script:Rec.WaitForExit(8000)) { try { $script:Rec.Kill() } catch {} }
    Start-Sleep -Milliseconds 300
}
