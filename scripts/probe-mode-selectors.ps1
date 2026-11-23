<#
.SYNOPSIS
    Read-only Acer WMI selector sweep: names the selector a machine's operating mode really lives in.

.DESCRIPTION
    A model can advertise every operating mode and still ignore the write. AN16-61 answers
    GetGamingMiscSetting(0x0A) with 0x7300 (Quiet, Balanced, Extreme, Turbo, Eco all offered) and answers status 0 to
    every SetGamingMiscSetting(0x0B) write, then sits on Balanced regardless. The question is therefore not "did the
    write fail" but "which selector holds the mode", and only the machine can answer it.

    This script asks every selector the interface has and prints the ones that answered something other than zero:

        AcerGamingFunction.GetGamingMiscSetting   0x00 .. 0x1F
        AcerGamingFunction.GetGamingProfile       0x00 .. 0x1F
        APGeAction.GetFunction                    0x01 .. 0x0F and 0x200 .. 0x22F

    Run it twice, once with the machine in one mode and once in another, and it prints exactly which selectors moved.
    That is the evidence a fix needs.

    HARDWARE SAFETY: no Set method is ever called. Only GetGamingMiscSetting, GetGamingProfile and
    APGeAction.GetFunction are invoked, which are readers. Nothing on the machine is changed, no setting is written,
    and the script is safe to run as often as you like. It does have to run elevated: AcerGamingFunction is a dynamic
    WMI class whose instances need an administrator token.

.PARAMETER Label
    A name for this capture, used in the file name and in the comparison output, e.g. "balanced" or "turbo".

.PARAMETER OutDir
    Where the captures are written. Defaults to %TEMP%\nitrolite-mode-probe.

.PARAMETER NoPause
    Do not wait for a key press before closing.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File probe-mode-selectors.ps1 -Label balanced

    Then switch the mode (in NitroSense or with the keyboard hotkey) and run:

    powershell -NoProfile -ExecutionPolicy Bypass -File probe-mode-selectors.ps1 -Label turbo

.NOTES
    Read-only with respect to firmware. Nothing is written to the machine.
#>
[CmdletBinding()]
param(
    [string]$Label = 'capture',
    [string]$OutDir,
    [switch]$NoPause
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Wait-AtEnd {
    if ($NoPause) { return }
    Write-Host ''
    Write-Host 'Press any key to close...' -ForegroundColor DarkGray
    try { $null = $Host.UI.RawUI.ReadKey('NoEcho,IncludeKeyDown') } catch { Start-Sleep -Seconds 5 }
}

function Exit-With([string]$Message) {
    Write-Host ''
    Write-Host $Message -ForegroundColor Yellow
    Wait-AtEnd
    exit 1
}

# ---------------------------------------------------------------- elevation

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Exit-With @'
This has to run in an elevated PowerShell: right-click Start, choose "Terminal (Admin)" or
"Windows PowerShell (Admin)", then run the command again in that window.
The Acer WMI classes are dynamic and their instances need an administrator token.
'@
}

if (-not $OutDir -or $OutDir.Trim().Length -eq 0) {
    $OutDir = Join-Path $env:TEMP 'nitrolite-mode-probe'
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

# ---------------------------------------------------------------- readers

# Every reader below reports $null instead of throwing: the point of the sweep is that most selectors do not exist,
# and a selector that answers WBEM_E_INVALID_METHOD_PARAMETERS is an answer too.

function Read-GamingWord {
    param($Instance, [string]$Method, [uint32]$Index)

    try {
        $result = Invoke-CimMethod -InputObject $Instance -MethodName $Method -Arguments @{ gmInput = $Index } -ErrorAction Stop
        if ($null -eq $result) { return $null }
        if ($null -eq $result.gmOutput) { return $null }
        return [uint64]$result.gmOutput
    }
    catch {
        return $null
    }
}

function Read-ApgeWord {
    param($Instance, [uint64]$Selector)

    try {
        $result = Invoke-CimMethod -InputObject $Instance -MethodName 'GetFunction' -Arguments @{ uiInput = $Selector } -ErrorAction Stop
        if ($null -eq $result) { return $null }
        if ($null -eq $result.uiOutput) { return $null }
        return [uint64]$result.uiOutput
    }
    catch {
        return $null
    }
}

# ---------------------------------------------------------------- the sweep

Write-Host ''
Write-Host "NitroLite mode-selector probe  (label: $Label)" -ForegroundColor Cyan
Write-Host 'Read-only: no setting on this machine is written.' -ForegroundColor DarkGray

$gaming = $null
$apge = $null

try {
    $gaming = Get-CimInstance -Namespace 'root/WMI' -ClassName 'AcerGamingFunction' -ErrorAction Stop |
        Select-Object -First 1
}
catch {
    Write-Host "  AcerGamingFunction: not readable ($($_.Exception.Message))" -ForegroundColor Yellow
}

try {
    $apge = Get-CimInstance -Namespace 'root/WMI' -ClassName 'APGeAction' -ErrorAction Stop |
        Select-Object -First 1
}
catch {
    Write-Host "  APGeAction: not readable ($($_.Exception.Message))" -ForegroundColor Yellow
}

if ($null -eq $gaming -and $null -eq $apge) {
    Exit-With 'Neither AcerGamingFunction nor APGeAction answered. Is this an Acer laptop with the Acer WMI provider installed?'
}

$answers = [ordered]@{}

if ($null -ne $gaming) {
    foreach ($index in 0x00..0x1F) {
        $word = Read-GamingWord -Instance $gaming -Method 'GetGamingMiscSetting' -Index ([uint32]$index)
        if ($null -ne $word -and $word -ne 0) {
            $key = 'misc:0x{0:X2}' -f $index
            $answers[$key] = ('0x{0:X16}' -f $word)
        }
    }

    foreach ($index in 0x00..0x1F) {
        $word = Read-GamingWord -Instance $gaming -Method 'GetGamingProfile' -Index ([uint32]$index)
        if ($null -ne $word -and $word -ne 0) {
            $key = 'profile:0x{0:X2}' -f $index
            $answers[$key] = ('0x{0:X16}' -f $word)
        }
    }
}

if ($null -ne $apge) {
    foreach ($selector in @(0x01..0x0F) + @(0x200..0x22F)) {
        $word = Read-ApgeWord -Instance $apge -Selector ([uint64]$selector)
        if ($null -ne $word -and $word -ne 0) {
            $key = 'apge:0x{0:X3}' -f $selector
            $answers[$key] = ('0x{0:X16}' -f $word)
        }
    }
}

# ---------------------------------------------------------------- output

Write-Host ''
Write-Host 'Selectors that answered something (zero answers are left out):' -ForegroundColor Cyan

if ($answers.Count -eq 0) {
    Write-Host '  none - every selector answered zero, which means nothing on this machine stores a readable setting.' -ForegroundColor Yellow
}
else {
    foreach ($key in $answers.Keys) {
        Write-Host ("  {0,-14} {1}" -f $key, $answers[$key])
    }
}

$capturePath = Join-Path $OutDir ("capture-{0}.json" -f ($Label -replace '[^\w\-]', '_'))
$answers | ConvertTo-Json | Set-Content -LiteralPath $capturePath -Encoding UTF8
Write-Host ''
Write-Host "Saved: $capturePath" -ForegroundColor DarkGray

# ---------------------------------------------------------------- comparison

$previous = Get-ChildItem -LiteralPath $OutDir -Filter 'capture-*.json' |
    Where-Object { $_.FullName -ne $capturePath } |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

if ($null -eq $previous) {
    Write-Host ''
    Write-Host 'Only one capture so far. Now change the operating mode (NitroSense or the keyboard hotkey), then run this' -ForegroundColor Yellow
    Write-Host "again with a different value for -Label, e.g.  -Label turbo" -ForegroundColor Yellow
    Write-Host 'The second run prints exactly which selectors moved, and that names the selector the mode lives in.' -ForegroundColor Yellow
    Wait-AtEnd
    return
}

$before = @{}
$rawBefore = Get-Content -LiteralPath $previous.FullName -Raw
if ($rawBefore.Trim().Length -gt 0) {
    (ConvertFrom-Json -InputObject $rawBefore).PSObject.Properties | ForEach-Object { $before[$_.Name] = $_.Value }
}

$keys = @($before.Keys) + @($answers.Keys) | Sort-Object -Unique

Write-Host ''
Write-Host ("Compared with: {0}" -f $previous.Name) -ForegroundColor Cyan
Write-Host ''
Write-Host ("  {0,-14} {1,-20} {2,-20}" -f 'selector', 'before', 'after')
Write-Host ("  {0,-14} {1,-20} {2,-20}" -f ('-' * 14), ('-' * 20), ('-' * 20))

$changed = @()
foreach ($key in $keys) {
    $leftValue = $before[$key]
    $rightValue = $answers[$key]
    $marker = ' '
    if ("$leftValue" -ne "$rightValue") {
        $marker = '*'
        $changed += $key
    }

    Write-Host ("{0} {1,-14} {2,-20} {3,-20}" -f $marker, $key, "$leftValue", "$rightValue")
}

Write-Host ''
if ($changed.Count -eq 0) {
    Write-Host 'Nothing moved. Either the mode did not change between the two captures, or this firmware does not' -ForegroundColor Yellow
    Write-Host 'expose the mode through any of these selectors - paste the output of both runs anyway.' -ForegroundColor Yellow
}
else {
    Write-Host ("Moved ({0}): {1}" -f $changed.Count, ($changed -join ', ')) -ForegroundColor Green
    Write-Host 'Paste this table into the issue. The selector marked with * is the one the mode lives in.' -ForegroundColor Green
}

Wait-AtEnd
