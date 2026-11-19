<#
.SYNOPSIS
    Drives the real NitroLite build with UI Automation and records each feature with ffmpeg gdigrab.

.DESCRIPTION
    Raw captures land in scripts\raw\*.mkv; scripts\encode-clips.ps1 turns them into public\clips\*.mp4.
    The app runs on a throwaway copy of %LOCALAPPDATA%\NitroLite, so your settings files are never written.
    Hardware state DOES change while recording (power mode, fan, keyboard lighting); the finally block puts it back
    to Balanced, automatic fan and the backlight off.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File trailer\scripts\record-clips.ps1
    powershell -ExecutionPolicy Bypass -File trailer\scripts\record-clips.ps1 -Only 03,05
#>
param([string]$Only = '', [string]$RestoreMode = '')
. "$PSScriptRoot\lib.ps1"
$Raw = Join-Path $PSScriptRoot 'raw'
New-Item -ItemType Directory -Force $Raw | Out-Null
$TS = [System.Windows.Automation.TreeScope]
$CT = [System.Windows.Automation.ControlType]

function Want([string]$n) { -not $Only -or (($Only -split ',') -contains $n) }
function Inv($el) { try { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() } catch {} }
function OfType($t, $root = $script:Win) { $root.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $t))) }
function Kids($el) { $el.FindAll($TS::Children, [System.Windows.Automation.Condition]::TrueCondition) }
function ModeCards { foreach ($l in (OfType $CT::List)) { $k = Kids $l; if ($k.Count -eq 4 -and $k[0].Current.BoundingRectangle.Width -gt 300) { return , $k } } }
function Swatches { foreach ($l in (OfType $CT::List)) { $k = Kids $l; if ($k.Count -eq 8 -and $k[0].Current.BoundingRectangle.Width -lt 40) { return , $k } } }
function Button-Below([string]$name, [double]$minY) {
    foreach ($b in (OfType $CT::Button)) { if ($b.Current.Name -eq $name -and $b.Current.BoundingRectangle.Y -gt $minY) { return $b } }
}
function Open-Items($combo, [int]$ms = 400) {
    Click-El $combo $ms; Start-Sleep -Milliseconds 600
    $items = $AE::RootElement.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::ListItem)))
    @($items | Where-Object { $_.Current.ProcessId -eq $script:Proc.Id })
}
function Pick-Item($combo, [string]$rx, [int]$ms = 400) {
    $pick = $null; foreach ($it in (Open-Items $combo $ms)) { if ($it.Current.Name -match $rx) { $pick = $it; break } }
    if ($pick) { Click-El $pick ([Math]::Max(220, $ms - 120)) } else { Write-Host "no item $rx"; [System.Windows.Forms.SendKeys]::SendWait('{ESC}') }
}
function Focus-Win { $r = Get-WinRect; Click-At ($r.Left + 900) ($r.Top + 20) 150; Start-Sleep -Milliseconds 300 }
function Park { $r = Get-WinRect; [W]::SetCursorPos($r.Left + 1500, $r.Top + 980) | Out-Null }
$ModeNames = @('Quiet', 'Balanced', 'Performance', 'Turbo')
# The mode the laptop is in before anything is touched, read from the header of the first instance.
function Read-Mode {
    $r = Get-WinRect
    foreach ($t in (OfType $CT::Text)) {
        $b = $t.Current.BoundingRectangle
        if ($b.Y -lt $r.Top + 60 -and $t.Current.Name -match '^(QUIET|BALANCED|PERFORMANCE|TURBO)$') { return (Get-Culture).TextInfo.ToTitleCase($t.Current.Name.ToLower()) }
    }
    'Balanced'
}
function Restore-Hardware {
    try {
        Inv (Find-ById 'NavModes'); Start-Sleep -Milliseconds 900
        $m = ModeCards; if ($m) { Click-El $m[[array]::IndexOf($ModeNames, $script:OrigMode)] 200; Start-Sleep -Milliseconds 1200 }
        Inv (Find-ByName 'Automatic'); Start-Sleep -Milliseconds 1000
        Inv (Find-ById 'NavLighting'); Start-Sleep -Milliseconds 1000
        Inv (Find-ByName 'Turn Backlight Off'); Start-Sleep -Milliseconds 800
        Write-Host "hardware restored: $script:OrigMode, automatic fan, backlight off"
    } catch { Write-Host "restore failed: $_" -ForegroundColor Yellow }
}

if ($RestoreMode) { $script:OrigMode = $RestoreMode } else {
    Start-NL; Start-Sleep -Milliseconds 1500; $script:OrigMode = Read-Mode; Stop-NL
}
Write-Host "mode before recording: $script:OrigMode"

try {
    # ------------------------------------------------------------------ 01 cold launch + power modes
    if (Want '01') {
        Start-NL -rec "$Raw\01-power-modes.mkv"
        Park; Start-Sleep -Milliseconds 800
        $m = ModeCards
        foreach ($i in 0, 1, 2, 3) { Click-El $m[$i] 520; Start-Sleep -Milliseconds 1900 }
        Start-Sleep -Milliseconds 800
        Stop-Rec
        Restore-Hardware; Stop-NL
    }

    # ------------------------------------------------------------------ 02 manual speed + fan curve
    if (Want '02') {
        Start-NL; Focus-Win; Park
        Start-Rec "$Raw\02-fan-curve.mkv"
        Start-Sleep -Milliseconds 500
        Click-El (Find-ByName 'Custom') 550; Start-Sleep -Milliseconds 1100
        $sl = OfType $CT::Slider
        if ($sl.Count -ge 1) {
            $b = $sl[0].Current.BoundingRectangle; $yy = $b.Y + $b.Height / 2
            Drag-From ($b.X + $b.Width * 0.45) $yy ($b.X + 6) $yy 650; Start-Sleep -Milliseconds 700
            Drag-From ($b.X + 6) $yy ($b.X + $b.Width * 0.7) $yy 650; Start-Sleep -Milliseconds 700
        }
        Click-El (Find-ByName 'Curve') 500; Start-Sleep -Milliseconds 1200
        $q = Button-Below 'Quiet' 560; if ($q) { Click-El $q 450; Start-Sleep -Milliseconds 900 }
        $g = Button-Below 'Gaming' 560; if (-not $g) { $g = Button-Below 'Game' 560 }; if ($g) { Click-El $g 400; Start-Sleep -Milliseconds 900 }
        $th = $script:Win.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, 'PART_Thumb')))
        if ($th.Count -ge 6) {
            $c = Center $th[$th.Count - 2]; Drag-From $c[0] $c[1] ($c[0] - 240) $c[1] 650; Start-Sleep -Milliseconds 500
            $c = Center $th[$th.Count - 1]; Drag-From $c[0] $c[1] ($c[0] - 320) $c[1] 650; Start-Sleep -Milliseconds 500
        }
        Start-Sleep -Milliseconds 1000
        Stop-Rec
        Restore-Hardware; Stop-NL
    }

    # ------------------------------------------------------------------ 03 lighting: zones, effects, speed
    if (Want '03') {
        Start-NL; Focus-Win
        # off camera: full brightness and a static effect, and learn the effect names
        Inv (Find-ById 'NavLighting'); Start-Sleep -Milliseconds 1200
        $sl = OfType $CT::Slider
        if ($sl.Count -ge 1) { $b = $sl[0].Current.BoundingRectangle; Drag-From ($b.X + 8) ($b.Y + $b.Height / 2) ($b.X + $b.Width - 2) ($b.Y + $b.Height / 2) 250 }
        $cb = OfType $CT::ComboBox
        $effects = @(Open-Items $cb[1] 200 | ForEach-Object { $_.Current.Name } | Where-Object { $_ -and $_ -notmatch '^(OFF|Off|None)$' })
        [System.Windows.Forms.SendKeys]::SendWait('{ESC}'); Start-Sleep -Milliseconds 400
        Write-Host "effects: $($effects -join ', ')"
        Pick-Item $cb[1] '^STATIC$|^Static$' 200; Start-Sleep -Milliseconds 700
        Inv (Find-ById 'NavModes'); Start-Sleep -Milliseconds 1000; Park

        Start-Rec "$Raw\03-rgb-effects.mkv"
        Start-Sleep -Milliseconds 400
        Go-Page 'NavLighting'; Start-Sleep -Milliseconds 300
        $sw = Swatches; $apply = Find-ByName 'Apply to All Zones'
        if ($sw) { foreach ($i in 0, 4) { Click-El $sw[$i] 340; Start-Sleep -Milliseconds 200; Click-El $apply 340; Start-Sleep -Milliseconds 900 } }
        $cb = OfType $CT::ComboBox
        foreach ($e in ($effects | Select-Object -First 8)) { Pick-Item $cb[1] ('^' + [regex]::Escape($e) + '$') 300; Start-Sleep -Milliseconds 900 }
        $sl = OfType $CT::Slider
        if ($sl.Count -ge 2) { $b = $sl[1].Current.BoundingRectangle; $yy = $b.Y + $b.Height / 2; Drag-From ($b.X + $b.Width * 0.3) $yy ($b.X + $b.Width - 4) $yy 600 }
        Start-Sleep -Milliseconds 1000
        Stop-Rec
        Restore-Hardware; Stop-NL
    }

    # ------------------------------------------------------------------ 04 per-game profiles + monitoring + OSD
    if (Want '04') {
        # window at the screen origin so the OSD (top-left corner of the monitor) lands inside the capture
        Start-NL -x 0 -y 0; Focus-Win; Park
        Start-Rec "$Raw\04-game-profiles-osd.mkv"
        Start-Sleep -Milliseconds 600
        $c = OfType $CT::ComboBox
        if ($c.Count -ge 4) {
            Pick-Item $c[2] 'Performance' 550; Start-Sleep -Milliseconds 800
            $c = OfType $CT::ComboBox; Pick-Item $c[3] 'Efficient' 450; Start-Sleep -Milliseconds 800
        }
        Pick-Item (Find-ById 'CmbGameFanMode') '.' 450; Start-Sleep -Milliseconds 1000
        Go-Page 'NavMonitoring'; Start-Sleep -Milliseconds 1200
        Click-El (Find-ByName 'On-Screen Display (OSD)') 500; Start-Sleep -Milliseconds 2600
        $r = Get-WinRect; Move-To ($r.Left + 1100) ($r.Top + 700) 400
        for ($i = 0; $i -lt 4; $i++) { Scroll-Wheel 1; Start-Sleep -Milliseconds 250 }
        Start-Sleep -Milliseconds 1800
        Stop-Rec
        Inv (Find-ByName 'On-Screen Display (OSD)'); Start-Sleep -Milliseconds 600
        Stop-NL
    }

    # ------------------------------------------------------------------ 05 battery health + MUX (look, never click)
    if (Want '05') {
        Start-NL; Focus-Win; Park
        Start-Rec "$Raw\05-battery-mux.mkv"
        Start-Sleep -Milliseconds 400
        Go-Page 'NavBattery'; Start-Sleep -Milliseconds 1500
        $r = Get-WinRect
        $tg = @(OfType $CT::Button | Where-Object { $_.Current.BoundingRectangle.Width -eq 43 } | Sort-Object { $_.Current.BoundingRectangle.Y })
        if ($tg.Count) { $c = Center $tg[0]; Move-To $c[0] $c[1] 600; Start-Sleep -Milliseconds 1300 }
        Move-To ($r.Left + 1100) ($r.Top + 650) 400
        for ($i = 0; $i -lt 6; $i++) { Scroll-Wheel 1; Start-Sleep -Milliseconds 260 }
        Start-Sleep -Milliseconds 1600
        Go-Page 'NavModes'; Start-Sleep -Milliseconds 900
        $c = OfType $CT::ComboBox
        if ($c.Count) { $p = Center $c[0]; Move-To $p[0] $p[1] 600 }
        Start-Sleep -Milliseconds 2200
        Stop-Rec
        Stop-NL
    }

    # ------------------------------------------------------------------ 06 six themes, then the other seven languages
    if (Want '06') {
        $shots = @(@('Dark', 'en'), @('MidnightOled', 'en'), @('SlateSteel', 'en'), @('AmberConsole', 'en'), @('NordFrost', 'en'), @('WarmCream', 'en'),
            @('Dark', 'tr'), @('Dark', 'de'), @('Dark', 'es'), @('Dark', 'hi'), @('Dark', 'id'), @('Dark', 'pt'), @('Dark', 'ru'))
        $n = 0
        foreach ($s in $shots) {
            Start-NL -theme $s[0] -lang $s[1]; Park; Start-Sleep -Milliseconds 1200
            Start-Rec ("$Raw\06-{0:00}-{1}-{2}.mkv" -f $n, $s[0], $s[1])
            Start-Sleep -Milliseconds 1500
            Stop-Rec; Stop-NL; $n++
        }
    }
}
finally {
    if ($script:Rec -and -not $script:Rec.HasExited) { Stop-Rec }
    if ($script:Proc -and -not $script:Proc.HasExited) { Restore-Hardware; Stop-NL }
}
