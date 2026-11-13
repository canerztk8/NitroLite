<#
.SYNOPSIS
    Shows how many people downloaded NitroLite and how many looked at the repository.

.DESCRIPTION
    Two kinds of numbers, both from GitHub, neither from the app:

    - Downloads per release. Public: anyone can read these from the API.
    - Repository traffic for the last 14 days (views, unique visitors, clones). GitHub only returns this to the
      repository owner, so it is visible to you and nobody else.

    NitroLite itself contains no counter and sends nothing anywhere. This script only reads GitHub.

    Requires the GitHub CLI (https://cli.github.com) and a one-time `gh auth login` as the repository owner.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\stats.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\stats.ps1 -Repo someone/other-repo
#>
param(
    [string]$Repo = 'canerztk8/NitroLite'
)

$ErrorActionPreference = 'Stop'

function Write-Head([string]$text) {
    Write-Host ''
    Write-Host $text -ForegroundColor Cyan
    Write-Host ('-' * $text.Length) -ForegroundColor DarkGray
}

function Invoke-Gh([string[]]$arguments) {
    $output = & gh @arguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw ($output -join "`n") }
    return $output
}

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    Write-Host 'The GitHub CLI (gh) is not installed.' -ForegroundColor Red
    Write-Host 'Install it from https://cli.github.com, then run: gh auth login'
    exit 1
}

try {
    Invoke-Gh @('auth', 'status') | Out-Null
}
catch {
    Write-Host 'Not signed in to GitHub.' -ForegroundColor Red
    Write-Host 'Run: gh auth login'
    Write-Host ''
    Write-Host 'The traffic numbers need the repository owner account; downloads are public but this script'
    Write-Host 'reads them through the same login.'
    exit 1
}

# ---- repository -------------------------------------------------------------------------------------------

try {
    $repoLine = Invoke-Gh @('api', "repos/$Repo", '--jq',
        '[.full_name, .stargazers_count, .forks_count, .subscribers_count, .open_issues_count] | @tsv')
    $repoFields = ($repoLine -join '') -split "`t"
}
catch {
    Write-Host "Could not read $Repo." -ForegroundColor Red
    Write-Host $_
    exit 1
}

Write-Head "NitroLite - $($repoFields[0])"
Write-Host ("  Stars {0}   Forks {1}   Watchers {2}   Open issues {3}" -f `
    $repoFields[1], $repoFields[2], $repoFields[3], $repoFields[4])

# ---- downloads per release --------------------------------------------------------------------------------

Write-Head 'Downloads per release'

$releaseLines = Invoke-Gh @('api', "repos/$Repo/releases?per_page=100", '--paginate', '--jq',
    '.[] | [.tag_name, .published_at, (.assets | map(.download_count) | add // 0)] | @tsv')

$releases = @()
foreach ($line in $releaseLines) {
    if ([string]::IsNullOrWhiteSpace($line)) { continue }
    $parts = $line -split "`t"
    $releases += [pscustomobject]@{
        Tag       = $parts[0]
        Published = if ($parts[1]) { [datetime]::Parse($parts[1], [cultureinfo]::InvariantCulture).ToLocalTime() } else { $null }
        Downloads = [int]$parts[2]
    }
}

if ($releases.Count -eq 0) {
    Write-Host '  No releases yet.'
}
else {
    $total = 0
    foreach ($release in ($releases | Sort-Object Published)) {
        $total += $release.Downloads
        $when = if ($release.Published) { $release.Published.ToString('yyyy-MM-dd') } else { '          ' }
        Write-Host ("  {0,-12} {1}  {2,7:N0}" -f $release.Tag, $when, $release.Downloads)
    }
    Write-Host ('  ' + ('-' * 30)) -ForegroundColor DarkGray
    Write-Host ("  {0,-23} {1,7:N0}" -f 'Total', $total) -ForegroundColor Green
}

# ---- traffic (owner only) ---------------------------------------------------------------------------------

Write-Head 'Last 14 days (visible only to the repository owner)'

try {
    $viewLine = Invoke-Gh @('api', "repos/$Repo/traffic/views", '--jq', '[.count, .uniques] | @tsv')
    $viewFields = ($viewLine -join '') -split "`t"
    $cloneLine = Invoke-Gh @('api', "repos/$Repo/traffic/clones", '--jq', '[.count, .uniques] | @tsv')
    $cloneFields = ($cloneLine -join '') -split "`t"

    Write-Host ("  Views    {0,7:N0}   unique visitors {1,7:N0}" -f [int]$viewFields[0], [int]$viewFields[1])
    Write-Host ("  Clones   {0,7:N0}   unique cloners  {1,7:N0}" -f [int]$cloneFields[0], [int]$cloneFields[1])
}
catch {
    Write-Host '  Traffic is not available for this account.' -ForegroundColor Yellow
    Write-Host '  GitHub returns it only to the repository owner, and only for the last 14 days.'
}

Write-Host ''
Write-Host 'Downloads count every asset fetch, including the same person downloading twice.' -ForegroundColor DarkGray
Write-Host 'NitroLite sends no telemetry, so there is no number for "people who ran it".' -ForegroundColor DarkGray
