<#
.SYNOPSIS
    Builds the downloadable NitroLite package: artifacts\NitroLite-v<version>-win-x64.zip

.DESCRIPTION
    - NitroLite.exe: self-contained single file (no .NET install needed on the target PC).
    - Pinned to a patched .NET runtime ($RuntimeVersion). 10.0.14 fixes CVE-2026-111497 (Brotli decoder,
      GHSA-p3jp-9v83-76w9); the SDK's bundled 10.0.12 is affected, so never ship a package built without the pin.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\publish-release.ps1
#>
param(
    [string]$RuntimeVersion = '10.0.14',
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

[xml]$proj = Get-Content (Join-Path $root 'NitroLite\NitroLite.csproj')
$version = ($proj.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1)
if (-not $version) { throw 'Version not found in NitroLite.csproj' }

$artifacts = Join-Path $root 'artifacts'
$stage     = Join-Path $artifacts 'NitroLite'
$zip       = Join-Path $artifacts "NitroLite-v$version-win-x64.zip"
$work      = Join-Path $artifacts 'work'

# The AOT linker step looks up vswhere.exe on PATH.
$vswhereDir = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
if ((Test-Path $vswhereDir) -and ($env:PATH -notlike "*$vswhereDir*")) { $env:PATH += ";$vswhereDir" }

function Invoke-Step([string]$title, [scriptblock]$block) {
    Write-Host "==> $title" -ForegroundColor Cyan
    & $block
    if ($LASTEXITCODE -ne 0) { throw "$title failed (exit $LASTEXITCODE)" }
}

if (Test-Path $artifacts) { Remove-Item $artifacts -Recurse -Force }
# Always publish from a clean win-x64 build: an incremental build once kept a stale g.resources without app.ico,
# and the packaged window then failed to open. (The normal Release build folder is left alone.)
foreach ($p in 'NitroLite\obj\Release\net10.0-windows\win-x64', 'NitroLite\bin\Release\net10.0-windows\win-x64') {
    $full = Join-Path $root $p
    if (Test-Path $full) { Remove-Item $full -Recurse -Force }
}
New-Item -ItemType Directory -Path $stage | Out-Null

if (-not $SkipTests) {
    Invoke-Step 'Tests' { dotnet test NitroLite.Tests/NitroLite.Tests.csproj -c Release --nologo -v q }
}

Invoke-Step "NitroLite (self-contained single file, runtime $RuntimeVersion)" {
    dotnet publish NitroLite/NitroLite.csproj -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
        -p:RuntimeFrameworkVersion=$RuntimeVersion -p:DebugType=none `
        -o $stage --nologo
}

# Ship only what the app needs.
Get-ChildItem $stage -Recurse -File | Where-Object { $_.Extension -in '.pdb', '.xml' } | Remove-Item -Force
$unexpected = Get-ChildItem $stage -Recurse -File | Where-Object {
    $_.Name -notin 'NitroLite.exe'
}
if ($unexpected) { throw "Unexpected files in the package: $($unexpected.FullName -join ', ')" }

# Guard against the vulnerable runtime: the single-file host carries the runtime version string.
$exeBytes = [IO.File]::ReadAllBytes((Join-Path $stage 'NitroLite.exe'))
if (-not ([Text.Encoding]::ASCII.GetString($exeBytes).Contains($RuntimeVersion))) {
    throw "NitroLite.exe does not reference runtime $RuntimeVersion"
}

Copy-Item (Join-Path $root 'LICENSE') $stage
Compress-Archive -Path $stage -DestinationPath $zip -CompressionLevel Optimal
Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue

$hash = (Get-FileHash $zip -Algorithm SHA256).Hash
Write-Host ''
Write-Host "Package : $zip" -ForegroundColor Green
Write-Host ("Size    : {0:N1} MB" -f ((Get-Item $zip).Length / 1MB))
Write-Host "SHA-256 : $hash"
Get-ChildItem $stage -Recurse -File | ForEach-Object { "  {0,-45} {1,8:N0} KB" -f $_.FullName.Substring($stage.Length + 1), ($_.Length / 1KB) }
