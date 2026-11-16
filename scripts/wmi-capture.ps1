<#
.SYNOPSIS
    Read-only Acer WMI capture tool. Uses the running AASSvc as an oracle.

.DESCRIPTION
    Two independent halves:

      1. AASSvc oracle (loopback 127.0.0.1:46933). Reads live telemetry (CPU/GPU/SYS
         temperatures and fan RPM) and the current keyboard lighting config, and can
         apply a known lighting state so the WMI getters can be diffed against it.

      2. AcerGamingFunction getters (root\WMI). Sweeps every Get* method over a range of
         input indices and records the raw output. Requires an elevated session because
         the class is dynamic.

    HARDWARE SAFETY: this script never calls a Set* method on any Acer WMI class. The only
    writes it performs go through AASSvc's own lighting command, which is the same path
    NitroLite and NitroSense already use. AcerBiosConfigurationTool is never touched.

.PARAMETER OutDir
    Directory for the JSON captures and the text report. Defaults to
    %TEMP%\nitrolite-wmi-capture.

.PARAMETER SkipLightingProbe
    Only capture the baseline state. No lighting command is sent to AASSvc.

.PARAMETER IndexMax
    Highest input index swept on the indexed getters. Default 15 (0x0F).

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\wmi-capture.ps1

.NOTES
    Read-only with respect to firmware. Safe to run repeatedly.
#>
[CmdletBinding()]
param(
    [string]$OutDir,
    [switch]$SkipLightingProbe,
    [switch]$NoPause,
    [ValidateSet('basic', 'zones')]
    [string]$ProbeSet = 'basic',
    [int]$IndexMax = 15
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

if (-not $OutDir -or $OutDir.Trim().Length -eq 0) {
    $OutDir = Join-Path $env:TEMP 'nitrolite-wmi-capture'
}
if (-not (Test-Path -LiteralPath $OutDir)) {
    New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
}

$script:LogLines = New-Object System.Collections.Generic.List[string]
function Write-Log {
    param([string]$Message)
    $line = '[{0}] {1}' -f (Get-Date -Format 'HH:mm:ss.fff'), $Message
    $script:LogLines.Add($line)
    Write-Host $line
}

# ---------------------------------------------------------------------------------------
# AASSvc oracle
# ---------------------------------------------------------------------------------------

$AgentHost = '127.0.0.1'
$AgentPort = 46933
$PacketInitialization = 0
$PacketGetMonitorData = 10
$PacketGetUpdatedData = 20
$PacketSetDeviceData = 100

function Find-JsonObject {
    param([string]$Text)
    $depth = 0
    $inString = $false
    $escaped = $false
    $start = -1
    for ($i = 0; $i -lt $Text.Length; $i++) {
        $ch = $Text[$i]
        if ($inString) {
            if ($escaped) { $escaped = $false }
            elseif ($ch -eq '\') { $escaped = $true }
            elseif ($ch -eq '"') { $inString = $false }
            continue
        }
        if ($ch -eq '"') { $inString = $true; continue }
        if ($ch -eq '{') {
            if ($depth -eq 0) { $start = $i }
            $depth++
            continue
        }
        if ($ch -eq '}') {
            $depth--
            if ($depth -eq 0 -and $start -ge 0) {
                return @{ Json = $Text.Substring($start, $i - $start + 1); End = ($i + 1) }
            }
        }
    }
    return $null
}

function Connect-Agent {
    $client = New-Object System.Net.Sockets.TcpClient
    $client.Connect($AgentHost, $AgentPort)
    $stream = $client.GetStream()
    $stream.ReadTimeout = 4000
    $stream.WriteTimeout = 4000
    $conn = @{
        Client = $client
        Stream = $stream
        Buffer = New-Object System.Collections.Generic.List[byte]
    }

    $resp = Send-AgentRequest -Conn $conn -PacketId $PacketInitialization -Payload @{ Function = 'VERSION' } -Expected 'VERSION'
    if ($null -eq $resp -or [string]$resp.result -ne '0') {
        $client.Close()
        throw 'AASSvc VERSION handshake failed; the service is not answering.'
    }
    return $conn
}

function Read-AgentMessage {
    param($Conn, [int]$TimeoutMs = 2500)
    $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMs)
    $buf = New-Object byte[] 8192
    while ([DateTime]::UtcNow -lt $deadline) {
        $text = [System.Text.Encoding]::UTF8.GetString($Conn.Buffer.ToArray())
        $found = Find-JsonObject -Text $text
        if ($null -ne $found) {
            $consumed = [int]$found.End
            $rest = $Conn.Buffer.GetRange($consumed, $Conn.Buffer.Count - $consumed)
            $Conn.Buffer.Clear()
            $Conn.Buffer.AddRange($rest)
            return $found.Json
        }
        if (-not $Conn.Stream.DataAvailable) {
            Start-Sleep -Milliseconds 15
            continue
        }
        $n = $Conn.Stream.Read($buf, 0, $buf.Length)
        if ($n -le 0) { return $null }
        for ($i = 0; $i -lt $n; $i++) { $Conn.Buffer.Add($buf[$i]) }
    }
    return $null
}

function Send-AgentRequest {
    param($Conn, [uint32]$PacketId, $Payload, [string]$Expected)
    $json = $Payload | ConvertTo-Json -Compress -Depth 10
    $jsonBytes = [System.Text.Encoding]::UTF8.GetBytes($json)
    $packet = New-Object byte[] (8 + $jsonBytes.Length)
    $packet[0] = 0x41; $packet[1] = 0x43; $packet[2] = 0x45; $packet[3] = 0x52
    $idBytes = [BitConverter]::GetBytes($PacketId)
    [Array]::Copy($idBytes, 0, $packet, 4, 4)
    [Array]::Copy($jsonBytes, 0, $packet, 8, $jsonBytes.Length)
    $Conn.Stream.Write($packet, 0, $packet.Length)
    $Conn.Stream.Flush()

    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    while ([DateTime]::UtcNow -lt $deadline) {
        $msg = Read-AgentMessage -Conn $Conn -TimeoutMs 1500
        if ($null -eq $msg) { continue }
        $obj = $null
        try { $obj = $msg | ConvertFrom-Json } catch { continue }
        $names = $obj.PSObject.Properties.Name
        if ($names -notcontains 'request') { continue }
        if ($null -eq $obj.request) { continue }
        if (([string]$obj.request).ToLowerInvariant() -eq $Expected.ToLowerInvariant()) { return $obj }
    }
    return $null
}

function Get-AgentSnapshot {
    param($Conn)
    $snap = [ordered]@{ monitor = $null; lighting = $null }

    $mon = Send-AgentRequest -Conn $Conn -PacketId $PacketGetMonitorData -Payload @{} -Expected 'GET_MONITOR_DATA'
    if ($null -ne $mon -and [string]$mon.result -eq '0') { $snap.monitor = $mon.data }

    $lit = Send-AgentRequest -Conn $Conn -PacketId $PacketGetUpdatedData -Payload @{ Function = 'LIGHTING' } -Expected 'LIGHTING'
    if ($null -ne $lit -and [string]$lit.result -eq '0') { $snap.lighting = $lit.data }

    return $snap
}

function Set-AgentLighting {
    param($Conn, [string]$Effect, [int]$Brightness, [string[]]$Zones)

    $leds = @($Zones[0], $Zones[1], $Zones[2], $Zones[3])
    $primary = $Zones[0]

    $ok = $true
    foreach ($device in 0, 1) {
        $param = @{
            device     = $device
            effect     = $Effect
            brightness = $Brightness
            speed      = 3
            duration   = 5
            direction  = 1
            color      = $primary
            LEDs       = $leds
        }
        $resp = Send-AgentRequest -Conn $Conn -PacketId $PacketSetDeviceData -Payload @{ Function = 'LIGHTING'; Parameter = $param } -Expected 'LIGHTING'
        if ($null -eq $resp -or [string]$resp.result -ne '0') { $ok = $false }
    }
    return $ok
}

# ---------------------------------------------------------------------------------------
# AcerGamingFunction getters (read-only)
# ---------------------------------------------------------------------------------------

$script:AllowedGetters = @(
    'GetGamingSysInfo',
    'GetGamingLED',
    'GetGamingKBBacklight',
    'GetGamingLEDColor',
    'GetGamingLEDBehavior',
    'GetGamingRgbKb',
    'GetGamingFanSpeed',
    'GetGamingFanBehavior',
    'GetGamingMiscSetting',
    'GetGamingProfile',
    'GetGamingProfileSetting',
    'GetGamingFanTable'
)

function Invoke-Getter {
    param($Instance, [string]$Method, [hashtable]$Arguments)
    if ($script:AllowedGetters -notcontains $Method) {
        throw "Refusing to call '$Method': only whitelisted Get* methods may run."
    }
    try {
        return Invoke-CimMethod -InputObject $Instance -MethodName $Method -Arguments $Arguments -ErrorAction Stop
    }
    catch {
        return $null
    }
}

function Format-Hex64 {
    param([uint64]$Value)
    return ('0x{0:X16}' -f $Value)
}

function Format-Bytes64 {
    param([uint64]$Value)
    $b = [BitConverter]::GetBytes($Value)
    return (($b | ForEach-Object { '{0:X2}' -f $_ }) -join ' ')
}

function Format-ByteArray {
    param($Value)
    if ($null -eq $Value) { return '' }
    return ((@($Value) | ForEach-Object { '{0:X2}' -f [byte]$_ }) -join ' ')
}

function Capture-WmiState {
    param($Instance, [string]$Label)

    $state = [ordered]@{
        label   = $Label
        at      = (Get-Date).ToString('o')
        getters = [ordered]@{}
    }

    # Indexed UInt64 getters.
    foreach ($method in 'GetGamingSysInfo', 'GetGamingLEDColor', 'GetGamingLEDBehavior', 'GetGamingRgbKb') {
        $rows = [ordered]@{}
        for ($i = 0; $i -le $IndexMax; $i++) {
            $key = '0x{0:X2}' -f $i
            $r = Invoke-Getter -Instance $Instance -Method $method -Arguments @{ gmInput = [uint32]$i }
            if ($null -eq $r) { $rows[$key] = $null; continue }
            $v = [uint64]$r.gmOutput
            $rows[$key] = [ordered]@{ raw = (Format-Hex64 $v); bytes = (Format-Bytes64 $v) }
        }
        $state.getters[$method] = $rows
    }

    # Indexed getters that also return a byte array.
    foreach ($method in 'GetGamingLED', 'GetGamingKBBacklight') {
        $rows = [ordered]@{}
        for ($i = 0; $i -le $IndexMax; $i++) {
            $key = '0x{0:X2}' -f $i
            $r = Invoke-Getter -Instance $Instance -Method $method -Arguments @{ gmInput = [uint32]$i }
            if ($null -eq $r) { $rows[$key] = $null; continue }
            $rows[$key] = [ordered]@{
                bytes  = (Format-ByteArray $r.gmOutput)
                length = @($r.gmOutput).Count
                ret    = ('0x{0:X2}' -f [byte]$r.gmReturn)
            }
        }
        $state.getters[$method] = $rows
    }

    # Fixed-input getters.
    $fixed = [ordered]@{}
    foreach ($pair in @(
            @{ m = 'GetGamingFanSpeed'; i = 0x01; n = 'cpu' },
            @{ m = 'GetGamingFanSpeed'; i = 0x04; n = 'gpu' },
            @{ m = 'GetGamingFanBehavior'; i = 0x01; n = 'cpu' },
            @{ m = 'GetGamingFanBehavior'; i = 0x04; n = 'gpu' },
            @{ m = 'GetGamingMiscSetting'; i = 0x02; n = 'mux' },
            @{ m = 'GetGamingMiscSetting'; i = 0x0B; n = 'platformProfile' },
            @{ m = 'GetGamingProfile'; i = 0x00; n = 'profile0' },
            @{ m = 'GetGamingProfileSetting'; i = 0x00; n = 'setting0' }
        )) {
        $r = Invoke-Getter -Instance $Instance -Method $pair.m -Arguments @{ gmInput = [uint32]$pair.i }
        if ($null -eq $r) { $fixed[$pair.n] = $null; continue }
        $v = [uint64]$r.gmOutput
        $fixed[$pair.n] = [ordered]@{ method = $pair.m; input = ('0x{0:X2}' -f $pair.i); raw = (Format-Hex64 $v); bytes = (Format-Bytes64 $v) }
    }
    $state.getters['fixed'] = $fixed

    $ft = Invoke-Getter -Instance $Instance -Method 'GetGamingFanTable' -Arguments @{}
    if ($null -ne $ft) {
        $v = [uint64]$ft.gmOutput
        $state.getters['GetGamingFanTable'] = [ordered]@{ raw = (Format-Hex64 $v); bytes = (Format-Bytes64 $v) }
    }
    else {
        $state.getters['GetGamingFanTable'] = $null
    }

    return $state
}

# ---------------------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------------------

$report = [ordered]@{
    startedAt = (Get-Date).ToString('o')
    host      = $env:COMPUTERNAME
    user      = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
    elevated  = ([System.Security.Principal.WindowsPrincipal]::new([System.Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
    indexMax  = $IndexMax
    captures  = @()
    errors    = @()
}

Write-Log ('Elevated: {0}' -f $report.elevated)

$agent = $null
try {
    $agent = Connect-Agent
    Write-Log 'AASSvc connected (VERSION handshake OK).'
}
catch {
    $report.errors += ('AASSvc: ' + $_.Exception.Message)
    Write-Log ('AASSvc unavailable: ' + $_.Exception.Message)
}

$instance = $null
try {
    $instance = Get-CimInstance -Namespace 'root\WMI' -ClassName 'AcerGamingFunction' -ErrorAction Stop
    Write-Log 'AcerGamingFunction instance acquired.'
}
catch {
    $report.errors += ('AcerGamingFunction: ' + $_.Exception.Message)
    Write-Log ('AcerGamingFunction unavailable: ' + $_.Exception.Message)
}

$baselineAgent = $null
if ($null -ne $agent) { $baselineAgent = Get-AgentSnapshot -Conn $agent }

$baseline = [ordered]@{
    label   = 'baseline'
    at      = (Get-Date).ToString('o')
    agent   = $baselineAgent
    getters = $null
}
if ($null -ne $instance) {
    $baseline['getters'] = (Capture-WmiState -Instance $instance -Label 'baseline').getters
}
$report.captures += $baseline
Write-Log 'Baseline captured.'

if ($null -ne $instance) {
    if (-not $SkipLightingProbe -and $null -ne $agent) {
        $original = $null
        if ($null -ne $baselineAgent) { $original = $baselineAgent.lighting }

        $probes = @(
            @{ name = 'static-red'; effect = 'STATIC'; brightness = 9; zones = @('#FF0000', '#FF0000', '#FF0000', '#FF0000') },
            @{ name = 'static-green'; effect = 'STATIC'; brightness = 9; zones = @('#00FF00', '#00FF00', '#00FF00', '#00FF00') },
            @{ name = 'static-blue'; effect = 'STATIC'; brightness = 9; zones = @('#0000FF', '#0000FF', '#0000FF', '#0000FF') },
            @{ name = 'static-red-dim'; effect = 'STATIC'; brightness = 3; zones = @('#FF0000', '#FF0000', '#FF0000', '#FF0000') },
            @{ name = 'direct-4zones'; effect = 'DIRECT'; brightness = 9; zones = @('#FF0000', '#00FF00', '#0000FF', '#FFFFFF') }
        )

        if ($ProbeSet -eq 'zones') {
            $probes = @(
                @{ name = 'z1-red'; effect = 'DIRECT'; brightness = 9; zones = @('#FF0000', '#000000', '#000000', '#000000') },
                @{ name = 'z2-green'; effect = 'DIRECT'; brightness = 9; zones = @('#000000', '#00FF00', '#000000', '#000000') },
                @{ name = 'z3-blue'; effect = 'DIRECT'; brightness = 9; zones = @('#000000', '#000000', '#0000FF', '#000000') },
                @{ name = 'z4-white'; effect = 'DIRECT'; brightness = 9; zones = @('#000000', '#000000', '#000000', '#FFFFFF') },
                @{ name = 'all-red'; effect = 'DIRECT'; brightness = 9; zones = @('#FF0000', '#FF0000', '#FF0000', '#FF0000') },
                @{ name = 'static-br1'; effect = 'STATIC'; brightness = 1; zones = @('#FF0000', '#FF0000', '#FF0000', '#FF0000') },
                @{ name = 'static-br9'; effect = 'STATIC'; brightness = 9; zones = @('#FF0000', '#FF0000', '#FF0000', '#FF0000') },
                @{ name = 'off'; effect = 'OFF'; brightness = 0; zones = @('#000000', '#000000', '#000000', '#000000') }
            )
        }

        foreach ($probe in $probes) {
            $applied = Set-AgentLighting -Conn $agent -Effect $probe.effect -Brightness $probe.brightness -Zones $probe.zones
            Start-Sleep -Milliseconds 900
            $snap = Get-AgentSnapshot -Conn $agent
            $cap = Capture-WmiState -Instance $instance -Label $probe.name
            $cap['agent'] = $snap
            $cap['applied'] = $applied
            $cap['requested'] = [ordered]@{ effect = $probe.effect; brightness = $probe.brightness; zones = $probe.zones }
            $report.captures += $cap
            Write-Log ('Probe {0}: applied={1}' -f $probe.name, $applied)
        }

        if ($null -ne $original) {
            $restoreEffect = [string]$original.effect
            $restoreBrightness = [int]$original.brightness
            $restoreZones = @('#00E5FF', '#00E5FF', '#00E5FF', '#00E5FF')
            if ($null -ne $original.LEDs -and @($original.LEDs).Count -ge 4) {
                $restoreZones = @($original.LEDs[0], $original.LEDs[1], $original.LEDs[2], $original.LEDs[3])
            }
            if ($restoreEffect -and $restoreEffect.Trim().Length -gt 0) {
                $restored = Set-AgentLighting -Conn $agent -Effect $restoreEffect -Brightness $restoreBrightness -Zones $restoreZones
                Write-Log ('Original lighting restored: {0} (ok={1})' -f $restoreEffect, $restored)
            }
        }
    }
}

if ($null -ne $agent) { $agent.Client.Close() }

$report.finishedAt = (Get-Date).ToString('o')

$jsonPath = Join-Path $OutDir 'capture.json'
$report | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $jsonPath -Encoding UTF8
$script:LogLines | Set-Content -LiteralPath (Join-Path $OutDir 'capture.log') -Encoding UTF8
Set-Content -LiteralPath (Join-Path $OutDir 'done.flag') -Value $report.finishedAt -Encoding UTF8

Write-Log ('Wrote ' + $jsonPath)
if (-not $NoPause) {
    Write-Host ''
    Write-Host 'Done. Press Enter to close.'
}
