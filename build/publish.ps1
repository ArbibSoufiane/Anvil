<#
.SYNOPSIS
    Builds the ArbiPache release: dist\ArbiPache\, dist\ArbiPache-<version>-win64.zip, dist\SHA256SUMS and
    dist\RELEASE_NOTES.md.

.DESCRIPTION
    The zip and checksum names are the ones the in-app updater matches (ReleaseFeed.ZipName, ReleaseFeed.SumsName).
    Change one here and ArbiPache stops seeing its own releases.

    Refuses uncommitted changes and a version without a dated CHANGELOG.md section, runs the unit tests, publishes the
    three programs self-contained (not single-file, not trimmed) into one folder, checks that folder with
    PublishLayoutTests, then zips it and writes the checksum. Creating the GitHub release is the release workflow's job.

.PARAMETER Trial
    Builds from uncommitted changes and an undated CHANGELOG.md section, to try the script. Never publish the result.
#>
[CmdletBinding()]
param([switch]$Trial)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

function Say($message) { Write-Host "==> $message" -ForegroundColor Cyan }

# Native commands do not throw when they fail; their exit code decides.
function Run([string]$What, [scriptblock]$Command) {
    & $Command
    if ($LASTEXITCODE -ne 0) { throw "$What failed (exit code $LASTEXITCODE)." }
}

function WriteText([string]$Path, [string]$Text) {
    # No byte order mark: it would become part of the first hash in SHA256SUMS and fail every download.
    [System.IO.File]::WriteAllText($Path, $Text, (New-Object System.Text.UTF8Encoding($false)))
}

# --- version, from the one place that defines it ----------------------------------------------------------------
$props = [xml](Get-Content (Join-Path $root 'Directory.Build.props') -Raw)
$version = $props.SelectSingleNode('/Project/PropertyGroup/Version').InnerText.Trim()
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "Directory.Build.props has no x.y.z <Version> (found '$version')." }
$zipName = "ArbiPache-$version-win64.zip"
Say "Building ArbiPache $version"

# --- refuse what cannot be released -------------------------------------------------------------------------------
$dirty = git status --porcelain
if ($LASTEXITCODE -ne 0) { throw 'git status failed. Run this from a git checkout of ArbiPache.' }
if ($dirty -and -not $Trial) {
    throw "The working tree has uncommitted changes. A release must be reproducible from its commit.`n$($dirty -join "`n")"
}

$changelog = @(Get-Content (Join-Path $root 'CHANGELOG.md') -Encoding UTF8)
$start = -1
for ($i = 0; $i -lt $changelog.Count; $i++) {
    if ($changelog[$i] -match "^## $([regex]::Escape($version))(\s|$)") { $start = $i; break }
}
if ($start -lt 0) { throw "CHANGELOG.md has no section for $version. Add one before publishing." }
if ($changelog[$start] -match 'unreleased' -and -not $Trial) {
    throw "CHANGELOG.md still marks $version unreleased. Put the release date in its heading: $($changelog[$start])"
}
$end = $changelog.Count
for ($i = $start + 1; $i -lt $changelog.Count; $i++) {
    if ($changelog[$i] -match '^## ') { $end = $i; break }
}
$notes = if ($end -gt $start + 1) { ($changelog[($start + 1)..($end - 1)] -join "`n").Trim() } else { '' }
if (-not $notes) { throw "The CHANGELOG.md section for $version is empty." }
if ($Trial) { Write-Warning 'Trial build: uncommitted changes and undated notes are allowed. Do not publish it.' }

# --- test, publish, check -------------------------------------------------------------------------------------------
$dist = Join-Path $root 'dist'
$out = Join-Path $dist 'ArbiPache'
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }

Say 'Running the unit tests'
Run 'The Core tests' { dotnet test --project tests/ArbiPache.Core.Tests -c Release }
Run 'The App tests' { dotnet test --project tests/ArbiPache.App.Tests -c Release }

foreach ($project in 'ArbiPache.App', 'ArbiPache.Agent', 'ArbiPache.Elevator') {
    Say "Publishing $project"
    Run "Publishing $project" {
        dotnet publish "src/$project" -c Release -r win-x64 --self-contained -p:PublishSingleFile=false -p:PublishTrimmed=false -o $out
    }
}

Say 'Adding the catalog, readme and licenses'
New-Item -ItemType Directory -Force (Join-Path $out 'catalog'), (Join-Path $out 'licenses') | Out-Null
Copy-Item (Join-Path $root 'catalog\*') (Join-Path $out 'catalog') -Recurse -Force
Copy-Item (Join-Path $root 'packaging\README.txt') (Join-Path $out 'README.txt')
Copy-Item (Join-Path $root 'LICENSE') (Join-Path $out 'LICENSE.txt')
Copy-Item (Join-Path $root 'src\ArbiPache.App\Assets\Fonts\OFL.txt') (Join-Path $out 'licenses\IBM-Plex-OFL.txt')

Say 'Checking the published folder'
$env:ARBIPACHE_REQUIRE_DIST = '1'
try {
    Run 'PublishLayoutTests' { dotnet test --project tests/ArbiPache.Core.Tests -c Release --no-build -- --filter-class '*PublishLayoutTests' }
}
finally { Remove-Item Env:ARBIPACHE_REQUIRE_DIST }

# --- zip, checksum, notes -----------------------------------------------------------------------------------------
Say "Zipping $zipName"
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = Join-Path $dist $zipName
# includeBaseDirectory: the zip holds one ArbiPache\ folder, which the updater looks inside.
[System.IO.Compression.ZipFile]::CreateFromDirectory($out, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $true)

$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
WriteText (Join-Path $dist 'SHA256SUMS') "$hash  $zipName`n"
WriteText (Join-Path $dist 'RELEASE_NOTES.md') "$notes`n"

Write-Host ''
Write-Host "Built ArbiPache $version" -ForegroundColor Green
Write-Host "  $zip ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB)"
Write-Host "  sha256 $hash"
