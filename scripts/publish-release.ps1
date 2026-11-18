<#
.SYNOPSIS
    Builds the downloadable NitroLite package: artifacts\NitroLite-v<version>-win-x64.zip

.DESCRIPTION
    - NitroLite.exe: self-contained single file (no .NET install needed on the target PC). The Avalonia front end is
      the only one that ships; the WPF project it replaced is retired.
      The published binary is NitroLite.Desktop.exe and it is renamed here on purpose. The updater that ships inside
      every 1.2.x WPF build looks for an entry called NitroLite.exe and refuses the package without one, so a zip
      that carried NitroLite.Desktop.exe would leave every existing user unable to update in place. Keeping the name
      also means the Task Scheduler entry and the HKCU Run entry the old build wrote keep pointing at a file that
      exists, and that file is now this one.
    - Pinned to a patched .NET runtime ($RuntimeVersion). 10.0.14 fixes CVE-2026-111497 (Brotli decoder,
      GHSA-p3jp-9v83-76w9); the SDK's bundled 10.0.12 is affected, so never ship a package built without the pin.
    - tools\WinMemoryCleaner.exe: the unmodified upstream release, so the memory card works out of the box.
      GPL-3.0, distributed as an aggregate next to MIT-licensed NitroLite; see third-party\WinMemoryCleaner\NOTICE.md.
      Both the version and the SHA-256 are pinned, so a re-uploaded or tampered asset fails the build.
    - packaging\scoop\nitrolite.json: version, url and hash are rewritten from the package just built, so the
      manifest in main always describes the newest release. The WinGet manifest is submitted by hand and is only
      checked for drift.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\publish-release.ps1
#>
param(
    [string]$RuntimeVersion = '10.0.14',
    [string]$WinMemoryCleanerVersion = '3.0.8',
    [string]$WinMemoryCleanerSha256 = '8b68d56c6ee28740f76513f21b21f5a1018f0ef291467b3f3d39d43daf0c0f2f',
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

[xml]$proj = Get-Content (Join-Path $root 'NitroLite.Desktop\NitroLite.Desktop.csproj')
$version = ($proj.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1)
if (-not $version) { throw 'Version not found in NitroLite.Desktop.csproj' }

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

# Only the previous package is removed. artifacts\ also holds hand-written release notes
# (release-notes-v1.2.3.md) that a rebuild must not destroy, so the delete is narrowed to
# the files this script produces instead of taking the whole folder.
New-Item -ItemType Directory -Path $artifacts -Force | Out-Null
Get-ChildItem $artifacts -Force | Where-Object { $_.Name -like 'NitroLite*' } | Remove-Item -Recurse -Force
# Always publish from a clean win-x64 build: an incremental build once kept a stale g.resources without app.ico,
# and the packaged window then failed to open. (The normal Release build folder is left alone.)
foreach ($p in 'NitroLite.Desktop\obj\Release\net10.0-windows\win-x64', 'NitroLite.Desktop\bin\Release\net10.0-windows\win-x64') {
    $full = Join-Path $root $p
    if (Test-Path $full) { Remove-Item $full -Recurse -Force }
}
New-Item -ItemType Directory -Path $stage | Out-Null

if (-not $SkipTests) {
    Invoke-Step 'Tests' { dotnet test NitroLite.Tests/NitroLite.Tests.csproj -c Release --nologo -v q }
}

Invoke-Step "NitroLite (self-contained single file, runtime $RuntimeVersion)" {
    dotnet publish NitroLite.Desktop/NitroLite.Desktop.csproj -c Release -f net10.0-windows -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
        -p:PublishTrimmed=true `
        -p:RuntimeFrameworkVersion=$RuntimeVersion -p:DebugType=none `
        -o $stage --nologo
}

# Ship only what the app needs. The single-file publish bundles the runtime, so the three diagnostics libraries below
# are normally inside NitroLite.exe and this removes nothing; they are named because a publish that is ever switched
# back to loose files would otherwise ship a crash-dump reader and a symbol reader the app never loads.
$unused = @('mscordaccore.dll', 'mscordbi.dll', 'Microsoft.DiaSymReader.Native.amd64.dll')
Get-ChildItem $stage -Recurse -File |
    Where-Object { $_.Extension -in '.pdb', '.xml' -or $_.Name -in $unused } |
    Remove-Item -Force

# The one rename that makes in-place updates possible. See the .DESCRIPTION above: the 1.2.x updater, the scheduled
# task and the Run entry all name NitroLite.exe, and this package has to answer to that name.
$publishedExe = Join-Path $stage 'NitroLite.Desktop.exe'
if (-not (Test-Path $publishedExe)) { throw "The publish step did not produce $publishedExe" }
Move-Item $publishedExe (Join-Path $stage 'NitroLite.exe') -Force

# Guard against the vulnerable runtime: the single-file host carries the runtime version string.
$exeBytes = [IO.File]::ReadAllBytes((Join-Path $stage 'NitroLite.exe'))
if (-not ([Text.Encoding]::ASCII.GetString($exeBytes).Contains($RuntimeVersion))) {
    throw "NitroLite.exe does not reference runtime $RuntimeVersion"
}

Copy-Item (Join-Path $root 'LICENSE') $stage

# ---------------------------------------------------------------------------
# WinMemoryCleaner (GPL-3.0), bundled as a separate executable.
# NitroLite never links against it; it only starts it as a child process, so the two
# form an aggregate under GPL-3.0 section 5 and NitroLite stays MIT.
# ---------------------------------------------------------------------------
Write-Host "==> WinMemoryCleaner $WinMemoryCleanerVersion" -ForegroundColor Cyan
$wmcDir = Join-Path $stage 'tools'
New-Item -ItemType Directory -Path $wmcDir -Force | Out-Null

$wmcExe = Join-Path $wmcDir 'WinMemoryCleaner.exe'
Invoke-WebRequest -UseBasicParsing -OutFile $wmcExe `
    -Uri "https://github.com/IgorMundstein/WinMemoryCleaner/releases/download/$WinMemoryCleanerVersion/WinMemoryCleaner.exe"

$actual = (Get-FileHash $wmcExe -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actual -ne $WinMemoryCleanerSha256.ToLowerInvariant()) {
    throw "WinMemoryCleaner digest mismatch: expected $WinMemoryCleanerSha256, got $actual"
}

Copy-Item (Join-Path $root 'third-party\WinMemoryCleaner\LICENSE') (Join-Path $wmcDir 'WinMemoryCleaner-LICENSE.txt')
Copy-Item (Join-Path $root 'third-party\WinMemoryCleaner\NOTICE.md') (Join-Path $stage 'THIRD-PARTY-NOTICES.md')

# The six bundled typefaces are SIL OFL 1.1, which requires the licence to travel with each font. The package's
# allowlist has no room for six more files, so each font's notice and the shared licence text are appended to
# the notices instead of shipped beside them. The licence is read from the copy that sits next to the fonts,
# so there is one source of truth for it.
$notices = Join-Path $stage 'THIRD-PARTY-NOTICES.md'
foreach ($family in @('Quicksand', 'Exo2', 'Oxanium', 'Rubik', 'JetBrainsMono', 'Rajdhani'))
{
    Add-Content -Path $notices -Value "`r`n---`r`n" -Encoding UTF8
    Get-Content (Join-Path $root "third-party\$family\NOTICE.md") | Add-Content -Path $notices -Encoding UTF8
}
Add-Content -Path $notices -Value "`r`n---`r`n" -Encoding UTF8
Get-Content (Join-Path $root 'NitroLite.Desktop\Assets\Fonts\OFL.txt') | Add-Content -Path $notices -Encoding UTF8

# GPL-3.0 section 6(d) wants the Corresponding Source reachable from the same place as the
# object code, so the upstream source archive travels inside the package. GitHub generates
# these archives on demand and does not promise byte-stability, so only the shape is checked.
$srcZip = Join-Path $wmcDir 'WinMemoryCleaner-source.zip'
Invoke-WebRequest -UseBasicParsing -OutFile $srcZip `
    -Uri "https://github.com/IgorMundstein/WinMemoryCleaner/archive/refs/tags/$WinMemoryCleanerVersion.zip"
Add-Type -AssemblyName System.IO.Compression.FileSystem
$srcArchive = [IO.Compression.ZipFile]::OpenRead($srcZip)
try {
    if ($srcArchive.Entries.Count -eq 0) { throw 'The WinMemoryCleaner source archive is empty.' }
} finally { $srcArchive.Dispose() }

# Nothing else may reach the package: the updater refuses unknown entries, so a stray file
# here would break in-app updates for everyone.
$allowed = @(
    'NitroLite.exe',
    'LICENSE',
    'THIRD-PARTY-NOTICES.md',
    'tools\WinMemoryCleaner.exe',
    'tools\WinMemoryCleaner-LICENSE.txt',
    'tools\WinMemoryCleaner-source.zip'
)
$unexpected = Get-ChildItem $stage -Recurse -File | Where-Object {
    $_.FullName.Substring($stage.Length + 1) -notin $allowed
}
if ($unexpected) { throw "Unexpected files in the package: $($unexpected.FullName -join ', ')" }

# ---------------------------------------------------------------------------
# Optional Authenticode signature.
#
# NitroLite ships unsigned by default. A code-signing certificate costs money every year, and requiring one would
# break the portable and Scoop builds that are the point of the project, so signing is opt-in: set
# NITROLITE_SIGN_CERT (a path to a .pfx) and, if it has one, NITROLITE_SIGN_PASSWORD. Leave them unset and the
# package goes out unsigned, which is what every release so far has done.
#
# This runs before the archive is built, because signing changes the executable's bytes and therefore the digest of
# the package that contains it.
# ---------------------------------------------------------------------------
$signCert = $env:NITROLITE_SIGN_CERT
if ($signCert) {
    if (-not (Test-Path -LiteralPath $signCert)) { throw "NITROLITE_SIGN_CERT does not exist: $signCert" }
    $signtool = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if (-not $signtool) { throw 'NITROLITE_SIGN_CERT is set but signtool.exe is not on PATH.' }

    $signTarget = Join-Path $stage 'NitroLite.Desktop.exe'
    $signArgs = @('sign', '/fd', 'SHA256', '/f', $signCert, '/tr', 'http://timestamp.digicert.com', '/td', 'SHA256')
    if ($env:NITROLITE_SIGN_PASSWORD) { $signArgs += @('/p', $env:NITROLITE_SIGN_PASSWORD) }
    $signArgs += $signTarget

    Write-Host '==> Signing NitroLite.Desktop.exe' -ForegroundColor Cyan
    & $signtool.Source @signArgs
    if ($LASTEXITCODE -ne 0) { throw "signtool failed (exit $LASTEXITCODE)" }

    $signed = Get-AuthenticodeSignature -LiteralPath $signTarget
    if ($signed.Status -ne 'Valid') { throw "The signature did not verify: $($signed.Status)" }
    Write-Host "    signed by $($signed.SignerCertificate.Subject)" -ForegroundColor Green
} else {
    Write-Host '==> Unsigned build (set NITROLITE_SIGN_CERT to sign)' -ForegroundColor DarkYellow
}

# Compress-Archive stores each entry's timestamp, so two builds of the same source produced
# two different digests. Pinning the timestamps removes that one source of variance, which is
# what makes a rebuild comparable at all.
#
# It does not make the archive byte-identical across hosts. Compress-Archive emits a different
# archive from Windows PowerShell 5.1 than from pwsh 7, so a package built here and the one the
# release workflow builds from the same tag carry different digests. The published artifact is
# the one users download, so its digest is the one the manifests have to carry — never a local
# build's. The warning below fires when this run cannot match the workflow.
$epoch = [datetime]::new(2000, 1, 1, 0, 0, 0, [DateTimeKind]::Utc)
Get-ChildItem $stage -Recurse | ForEach-Object { $_.LastWriteTimeUtc = $epoch }

Compress-Archive -Path $stage -DestinationPath $zip -CompressionLevel Optimal
Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue

$hash = (Get-FileHash $zip -Algorithm SHA256).Hash

# The checksum file is always produced, signed or not. It is the only thing a user of the portable or Scoop build
# can verify the download against, and the in-app updater refuses a package whose digest GitHub does not publish —
# so a release without one is a release nobody can check.
$sumsPath = Join-Path $artifacts 'SHA256SUMS.txt'
"$($hash.ToLowerInvariant())  $(Split-Path $zip -Leaf)" |
    Set-Content -LiteralPath $sumsPath -Encoding ascii

Write-Host ''
Write-Host "Package : $zip" -ForegroundColor Green
Write-Host ("Size    : {0:N1} MB" -f ((Get-Item $zip).Length / 1MB))
Write-Host "SHA-256 : $hash"
Write-Host "Sums    : $sumsPath"
Get-ChildItem $stage -Recurse -File | ForEach-Object { "  {0,-45} {1,8:N0} KB" -f $_.FullName.Substring($stage.Length + 1), ($_.Length / 1KB) }

# The manifests below are rewritten with the digest of the package this run just built. That is
# right for a local test install and wrong for main: the release workflow builds its own package
# from the tag, and the two do not hash the same. Say so at the moment the digest is printed,
# because the mistake it prevents is committing this one.
if ($PSVersionTable.PSEdition -ne 'Core') {
    Write-Warning ("Running under Windows PowerShell $($PSVersionTable.PSVersion). The release workflow uses pwsh 7, and " +
        'Compress-Archive does not emit the same archive from the two hosts, so the digest above will NOT match the ' +
        'published package. After a release, point packaging\scoop\nitrolite.json and the WinGet manifest at the ' +
        'digest from the release page.')
}

# ---------------------------------------------------------------------------
# Package manifests. The digest is written where the package manager reads it, so a
# published release and its manifest cannot drift apart. Only the three values that
# change per release are replaced; the rest of the file keeps its hand-written shape,
# because ConvertTo-Json would reformat the whole thing.
# ---------------------------------------------------------------------------
$scoopManifest = Join-Path $root 'packaging\scoop\nitrolite.json'
if (-not (Test-Path $scoopManifest)) { throw "Scoop manifest not found: $scoopManifest" }

$releaseUrl = "https://github.com/canerztk8/NitroLite/releases/download/v$version/NitroLite-v$version-win-x64.zip"
$lowerHash  = $hash.ToLowerInvariant()
$text = Get-Content $scoopManifest -Raw
$text = [regex]::Replace($text, '(?m)^(\s*"version"\s*:\s*")[^"]*(")', "`${1}$version`${2}")
$text = [regex]::Replace($text, 'https://github\.com/canerztk8/NitroLite/releases/download/v[0-9.]+/NitroLite-v[0-9.]+-win-x64\.zip', $releaseUrl)
$text = [regex]::Replace($text, '(?m)^(\s*"hash"\s*:\s*")[0-9a-fA-F]{64}(")', "`${1}$lowerHash`${2}")
[IO.File]::WriteAllText($scoopManifest, $text, (New-Object Text.UTF8Encoding $false))

# Read it back. A manifest that no longer parses, or that names another digest, is worse
# than no manifest at all, and this is the only place that can catch it.
$scoop = Get-Content $scoopManifest -Raw | ConvertFrom-Json
if ($scoop.version -ne $version) { throw "Scoop manifest version is $($scoop.version), expected $version" }
if ($scoop.architecture.'64bit'.url -ne $releaseUrl) { throw "Scoop manifest url is $($scoop.architecture.'64bit'.url)" }
if ($scoop.architecture.'64bit'.hash -ne $lowerHash) { throw 'Scoop manifest hash does not match the package' }
Write-Host "==> packaging\scoop\nitrolite.json -> $version, $($lowerHash.Substring(0, 12))..." -ForegroundColor Cyan

# The WinGet manifest is submitted by hand and reviewed by a moderator, so it is not
# rewritten here. A stale one is still worth saying out loud.
$wingetManifest = Join-Path $root "packaging\winget\manifests\c\canerztk8\NitroLite\$version\canerztk8.NitroLite.installer.yaml"
if (-not (Test-Path $wingetManifest)) {
    Write-Warning "No WinGet manifest for $version. Copy packaging\winget\manifests\c\canerztk8\NitroLite\<version> and bump it before submitting to winget-pkgs."
} else {
    $wingetHash = Get-Content $wingetManifest |
        Where-Object { $_ -match '^\s*InstallerSha256:\s*(\S+)' } |
        ForEach-Object { $Matches[1] } |
        Select-Object -First 1
    if ($wingetHash -ne $hash) {
        Write-Warning "WinGet manifest InstallerSha256 is $wingetHash, the package is $hash. Update packaging\winget\...\$version before submitting."
    }
}
