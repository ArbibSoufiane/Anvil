<#
.SYNOPSIS
    Builds the release on this PC and publishes it on GitHub: the v<version> tag, the zip, SHA256SUMS and the release
    notes that Settings -> Check for updates looks for.

.DESCRIPTION
    Releases are built locally; GitHub only hosts them. Before running this, put the release date in the version's
    CHANGELOG.md heading and commit it on main.

    Refuses a branch other than main, a version that already has a tag, and a main that is behind origin. Then runs
    build\publish.ps1, which refuses uncommitted changes, an undated CHANGELOG.md section and failing tests, and writes
    dist\. Only then does it tag, push main and the tag to origin, and create the GitHub release with gh.

    Needs git with an 'origin' remote, and the GitHub CLI (gh) signed in to an account that can write to it.

.PARAMETER DryRun
    Runs the checks and a trial build (publish.ps1 -Trial), then stops before tagging, pushing or publishing anything.
#>
[CmdletBinding()]
param([switch]$DryRun)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

function Say($message) { Write-Host "==> $message" -ForegroundColor Cyan }

# Native commands do not throw when they fail; their exit code decides.
function Run([string]$What, [scriptblock]$Command) {
    & $Command
    if ($LASTEXITCODE -ne 0) { throw "$What failed (exit code $LASTEXITCODE)." }
}

# The exit code of a native command whose output does not matter. Windows PowerShell turns redirected stderr into
# errors, which 'Stop' would throw, so this one function continues instead.
function Quietly([scriptblock]$Command) {
    $ErrorActionPreference = 'Continue'
    & $Command *> $null
    $LASTEXITCODE
}

$props = [xml](Get-Content (Join-Path $root 'Directory.Build.props') -Raw)
$version = $props.SelectSingleNode('/Project/PropertyGroup/Version').InnerText.Trim()
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "Directory.Build.props has no x.y.z <Version> (found '$version')." }
$tag = "v$version"
Say "Releasing Anvil $version as $tag"

# --- refuse what cannot be released -------------------------------------------------------------------------------
if ((Quietly { gh auth status }) -ne 0) { throw 'The GitHub CLI is not signed in. Run: gh auth login' }
$origin = git config --get remote.origin.url
if (-not $origin) { throw 'This checkout has no origin remote. Run: git remote add origin https://github.com/ArbibSoufiane/Anvil.git' }

$branch = git rev-parse --abbrev-ref HEAD
if ($branch -ne 'main' -and -not $DryRun) { throw "Releases are made from main; this is $branch." }

Run 'Fetching origin' { git fetch --quiet --tags origin }
if (git tag --list $tag) { throw "Tag $tag exists already. Bump <Version> in Directory.Build.props for a new release." }
if ((Quietly { git rev-parse --verify --quiet refs/remotes/origin/main }) -eq 0 -and
    (Quietly { git merge-base --is-ancestor refs/remotes/origin/main HEAD }) -ne 0) {
    throw 'origin/main has commits this main does not. Pull them first: git pull --ff-only origin main'
}

# --- build ----------------------------------------------------------------------------------------------------------
if ($DryRun) { & (Join-Path $PSScriptRoot 'publish.ps1') -Trial } else { & (Join-Path $PSScriptRoot 'publish.ps1') }
$dist = Join-Path $root 'dist'
$zip = Join-Path $dist "Anvil-$version-win64.zip"
$sums = Join-Path $dist 'SHA256SUMS'
$notes = Join-Path $dist 'RELEASE_NOTES.md'
foreach ($file in $zip, $sums, $notes) { if (-not (Test-Path $file)) { throw "publish.ps1 did not write $file." } }

if ($DryRun) {
    Write-Host ''
    Write-Host "Dry run: would tag $tag, push main and $tag to $origin, and publish:" -ForegroundColor Yellow
    Write-Host "  $zip"
    Write-Host "  $sums"
    Write-Host "  notes from $notes"
    return
}

# --- tag, push, publish ---------------------------------------------------------------------------------------------
Say "Tagging $tag"
Run 'Tagging' { git tag -a $tag -m "Anvil $version" }
Say "Pushing main and $tag to $origin"
Run 'Pushing' { git push origin HEAD:refs/heads/main "refs/tags/$tag" }
Say 'Publishing the release on GitHub'
gh release create $tag $zip $sums --verify-tag --title "Anvil $version" --notes-file $notes
if ($LASTEXITCODE -ne 0) {
    throw "gh release create failed. The tag is pushed; publish the release again with:`n" +
        "  gh release create $tag `"$zip`" `"$sums`" --verify-tag --title `"Anvil $version`" --notes-file `"$notes`""
}

Write-Host ''
Write-Host "Released Anvil $version" -ForegroundColor Green
