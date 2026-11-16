<#
.SYNOPSIS
    Payload matrix probe for the Acer keyboard backlight WMI write path.

.DESCRIPTION
    The idempotent write-back of the OFF state was rejected with gmOutput=0x01, which may
    simply mean "nothing to do". This probe writes a real STATIC + colour state using every
    plausible encoding and reports which one the firmware accepts.

    Every attempt is followed by a read-back of both GetGamingKBBacklight(0x01) and
    GetGamingRgbKb(0x01). The original 15-byte state is restored in a finally block.

    Requires an elevated session (AcerGamingFunction is a dynamic class).

.PARAMETER OutDir
    Where the JSON result is written. Defaults to %TEMP%\nitrolite-wmi-write-verify.

.PARAMETER TestColor
    Test colour as RRGGBB. Default 00FF00 (green).

.PARAMETER NoPause
    Do not wait for Enter at the end.

.NOTES
    The only writes are to the keyboard backlight. No fan, power, MUX or BIOS setting is
    touched, and AcerBiosConfigurationTool is never referenced.
#>
[CmdletBinding()]
param(
    [string]$OutDir,
    [string]$TestColor = '00FF00',
    [switch]$NoPause
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

if (-not $OutDir -or $OutDir.Trim().Length -eq 0) {
    $OutDir = Join-Path $env:TEMP 'nitrolite-wmi-write-verify'
}
if (-not (Test-Path -LiteralPath $OutDir)) {
    New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
}

$script:Lines = New-Object System.Collections.Generic.List[string]
function Write-Log {
    param([string]$Message)
    $line = '[{0}] {1}' -f (Get-Date -Format 'HH:mm:ss.fff'), $Message
    $script:Lines.Add($line)
    Write-Host $line
}

function Format-Bytes {
    param($Value)
    if ($null -eq $Value) { return '(null)' }
    return ((@($Value) | ForEach-Object { '{0:X2}' -f [byte]$_ }) -join ' ')
}

function Format-Status32 {
    param($Value)
    return ('0x{0:X8}' -f [uint32]$Value)
}

function Get-Backlight {
    param($Instance)
    $r = Invoke-CimMethod -InputObject $Instance -MethodName 'GetGamingKBBacklight' -Arguments @{ gmInput = [uint32]1 } -ErrorAction Stop
    return @{ Status = $r.gmReturn; Bytes = [byte[]]@($r.gmOutput) }
}

function Get-RgbPacked {
    param($Instance)
    $r = Invoke-CimMethod -InputObject $Instance -MethodName 'GetGamingRgbKb' -Arguments @{ gmInput = [uint32]1 } -ErrorAction Stop
    return [uint64]$r.gmOutput
}

$result = [ordered]@{
    startedAt = (Get-Date).ToString('o')
    testColor = $TestColor
    attempts  = [ordered]@{}
    verdict   = 'unknown'
}

$instance = $null
$backup = $null

try {
    $instance = Get-CimInstance -Namespace 'root\WMI' -ClassName 'AcerGamingFunction' -ErrorAction Stop
    Write-Log 'AcerGamingFunction instance acquired.'

    $step1 = Get-Backlight -Instance $instance
    $backup = $step1.Bytes
    $backupPacked = Get-RgbPacked -Instance $instance
    Write-Log ('backup: gmReturn=0x{0:X2} bytes=[{1}] packed=0x{2:X16}' -f [byte]$step1.Status, (Format-Bytes $backup), $backupPacked)
    $result['backup'] = [ordered]@{
        gmReturn = ('0x{0:X2}' -f [byte]$step1.Status)
        bytes    = (Format-Bytes $backup)
        packed   = ('0x{0:X16}' -f $backupPacked)
    }

    if ($backup.Length -ne 15) {
        Write-Log ('ABORT: expected 15 bytes, got {0}.' -f $backup.Length)
        $result.verdict = 'unexpected-length'
        $backup = $null
        throw [System.InvalidOperationException]::new('__abort__')
    }

    $r = [Convert]::ToByte($TestColor.Substring(0, 2), 16)
    $g = [Convert]::ToByte($TestColor.Substring(2, 2), 16)
    $b = [Convert]::ToByte($TestColor.Substring(4, 2), 16)

    # STATIC state: mode bytes zeroed, brightness 100, test colour.
    $static = [byte[]]::new(16)
    $static[0] = 0x00
    $static[1] = 0x00
    $static[2] = 0x64
    $static[3] = 0x00
    $static[4] = 0x00
    $static[5] = $r
    $static[6] = $g
    $static[7] = $b
    $static[8] = 0x03
    $static[15] = 0x00

    $attempts = @(
        @{ name = 'kb16_static_tail00'; kind = 'kb'; bytes = $static },
        @{ name = 'kb16_static_tail01'; kind = 'kb'; bytes = ([byte[]]($static[0..14] + [byte]0x01)) },
        @{ name = 'kb16_static_tailff'; kind = 'kb'; bytes = ([byte[]]($static[0..14] + [byte]0xFF)) },
        @{ name = 'rgb_low00'; kind = 'rgb'; packed = (([uint64]$b -shl 24) -bor ([uint64]$g -shl 16) -bor ([uint64]$r -shl 8)) },
        @{ name = 'rgb_low01'; kind = 'rgb'; packed = (([uint64]$b -shl 24) -bor ([uint64]$g -shl 16) -bor ([uint64]$r -shl 8) -bor 0x01) },
        @{ name = 'rgb_low0f'; kind = 'rgb'; packed = (([uint64]$b -shl 24) -bor ([uint64]$g -shl 16) -bor ([uint64]$r -shl 8) -bor 0x0F) }
    )

    foreach ($a in $attempts) {
        $entry = [ordered]@{ kind = $a.kind }
        try {
            if ($a.kind -eq 'kb') {
                $entry['written'] = (Format-Bytes $a.bytes)
                $w = Invoke-CimMethod -InputObject $instance -MethodName 'SetGamingKBBacklight' -Arguments @{ gmInput = $a.bytes } -ErrorAction Stop
            }
            else {
                $entry['written'] = ('0x{0:X16}' -f $a.packed)
                $w = Invoke-CimMethod -InputObject $instance -MethodName 'SetGamingRgbKb' -Arguments @{ gmInput = [uint64]$a.packed } -ErrorAction Stop
            }
            $entry['returnValue'] = [string]$w.ReturnValue
            $entry['gmOutput'] = (Format-Status32 $w.gmOutput)
            Start-Sleep -Milliseconds 600

            $rb = Get-Backlight -Instance $instance
            $rp = Get-RgbPacked -Instance $instance
            $entry['readBackBytes'] = (Format-Bytes $rb.Bytes)
            $entry['readBackPacked'] = ('0x{0:X16}' -f $rp)
            $entry['rgbApplied'] = ($rb.Bytes[5] -eq $r) -and ($rb.Bytes[6] -eq $g) -and ($rb.Bytes[7] -eq $b)
            $entry['packedApplied'] = ($rp -ne [uint64]$backupPacked)

            Write-Log ('{0,-22} ret={1} gmOutput={2} bytes=[{3}] packed=0x{4:X16} rgbApplied={5}' -f $a.name, $entry['returnValue'], $entry['gmOutput'], $entry['readBackBytes'], $rp, $entry['rgbApplied'])
        }
        catch {
            $entry['error'] = $_.Exception.Message
            Write-Log ('{0,-22} FAILED - {1}' -f $a.name, $_.Exception.Message)
        }
        $result.attempts[$a.name] = $entry
    }

    # ---------------------------------------------------------------- fan index sweep (read-only)
    $fan = [ordered]@{}
    foreach ($idx in 0x00, 0x01, 0x02, 0x03, 0x04) {
        $fr = Invoke-CimMethod -InputObject $instance -MethodName 'GetGamingFanSpeed' -Arguments @{ gmInput = [uint32]$idx } -ErrorAction SilentlyContinue
        if ($null -eq $fr) { $fan[('0x{0:X2}' -f $idx)] = $null; continue }
        $v = [uint64]$fr.gmOutput
        $fan[('0x{0:X2}' -f $idx)] = [ordered]@{
            raw = ('0x{0:X16}' -f $v); status = ('0x{0:X2}' -f [byte]($v -band 0xFF)); value = [int](($v -shr 8) -band 0xFF)
        }
        Write-Log ('fan GetGamingFanSpeed(0x{0:X2}) = 0x{1:X16}  status=0x{2:X2} value={3}' -f $idx, $v, [byte]($v -band 0xFF), [int](($v -shr 8) -band 0xFF))
    }
    $result['fanIndices'] = $fan

    $anyApplied = $false
    foreach ($k in $result.attempts.Keys) {
        if ($result.attempts[$k]['rgbApplied'] -eq $true) { $anyApplied = $true }
    }
    $result.verdict = 'ok'
    $result['anyPayloadApplied'] = $anyApplied
}
catch {
    if ($_.Exception.Message -ne '__abort__') {
        Write-Log ('ERROR: ' + $_.Exception.Message)
        $result.verdict = 'error'
        $result['error'] = $_.Exception.Message
    }
}
finally {
    if ($null -ne $backup -and $backup.Length -eq 15) {
        try {
            $restoreBytes = [byte[]]($backup + [byte]0x00)
            $w3 = Invoke-CimMethod -InputObject $instance -MethodName 'SetGamingKBBacklight' -Arguments @{ gmInput = $restoreBytes } -ErrorAction Stop
            Start-Sleep -Milliseconds 500
            $rb = Get-Backlight -Instance $instance
            $restored = ((Format-Bytes $rb.Bytes) -eq (Format-Bytes $backup))
            Write-Log ('RESTORE: ret={0} gmOutput={1} readBack=[{2}] restored={3}' -f $w3.ReturnValue, (Format-Status32 $w3.gmOutput), (Format-Bytes $rb.Bytes), $restored)
            $result['restore'] = [ordered]@{
                returnValue = [string]$w3.ReturnValue; gmOutput = (Format-Status32 $w3.gmOutput)
                readBack = (Format-Bytes $rb.Bytes); restored = $restored
            }
        }
        catch {
            Write-Log ('RESTORE FAILED: ' + $_.Exception.Message)
            $result['restore'] = [ordered]@{ error = $_.Exception.Message }
        }
    }

    $result.finishedAt = (Get-Date).ToString('o')
    $result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $OutDir 'verify.json') -Encoding UTF8
    $script:Lines | Set-Content -LiteralPath (Join-Path $OutDir 'verify.log') -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $OutDir 'done.flag') -Value $result.verdict -Encoding UTF8
    Write-Log ('Verdict: ' + $result.verdict)
}

if (-not $NoPause) { Read-Host 'Press Enter to close' | Out-Null }
