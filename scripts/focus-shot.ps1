param(
    [string]$Out = 'C:\temp\focus',
    [int]$Tabs = 3,
    [string]$Language = 'en',
    [switch]$KeepOpen
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $root) { $root = 'C:\Users\caner\Documents\antigravity\eager-shannon' }

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public class FocusShot {
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr v);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
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
[FocusShot]::SetProcessDpiAwarenessContext([IntPtr](-4)) | Out-Null

$exe = Join-Path $root 'NitroLite\bin\Release\net10.0-windows\NitroLite.exe'
if (-not (Test-Path $exe)) { throw "Not found: $exe" }

Get-Process NitroLite -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 1200

$dataDir = Join-Path ([IO.Path]::GetTempPath()) ('nl-focus-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $dataDir | Out-Null
$json = "{`r`n  `"language`": `"$Language`",`r`n  `"theme`": 0`r`n}`r`n"
[IO.File]::WriteAllText((Join-Path $dataDir 'settings.json'), $json, (New-Object Text.UTF8Encoding($false)))

New-Item -ItemType Directory -Path $Out -Force | Out-Null

$env:NITROLITE_DATA_DIR = $dataDir
$proc = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds 6

$hwnd = [IntPtr]::Zero
for ($i = 0; $i -lt 40 -and $hwnd -eq [IntPtr]::Zero; $i++) {
    $handles = [FocusShot]::ForPid([uint32]$proc.Id)
    if ($handles.Count -gt 0) { $hwnd = $handles[0] }
    else { Start-Sleep -Milliseconds 250 }
}
if ($hwnd -eq [IntPtr]::Zero) { throw 'window not found' }

[FocusShot]::SetForegroundWindow($hwnd) | Out-Null
Start-Sleep -Milliseconds 700

# The first Tab moves focus into the window; the rest walk the focusable controls.
for ($i = 0; $i -lt $Tabs; $i++) {
    [System.Windows.Forms.SendKeys]::SendWait('{TAB}')
    Start-Sleep -Milliseconds 350
}
Start-Sleep -Milliseconds 500

$rect = New-Object FocusShot+RECT
[FocusShot]::GetWindowRect($hwnd, [ref]$rect) | Out-Null
$w = $rect.Right - $rect.Left
$h = $rect.Bottom - $rect.Top
$bmp = New-Object System.Drawing.Bitmap($w, $h)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($rect.Left, $rect.Top, 0, 0, (New-Object System.Drawing.Size($w, $h)))
$file = Join-Path $Out ("focus-tab$Tabs.png")
$bmp.Save($file, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()

Write-Host "Saved $file"
if (-not $KeepOpen) { $proc | Stop-Process -Force }
