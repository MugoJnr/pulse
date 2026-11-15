#Requires -Version 5.1
<#
.SYNOPSIS
  Publish Pulse-Setup.exe and release notes to the public feed and the source repo.

.DESCRIPTION
  Uploads exactly one file, Pulse-Setup.exe. It does not push source code.
  The public feed is MugoJnr/pulse-releases (must stay public, installer only).
  The source repo MugoJnr/pulse also gets the same exe so copies still on the
  old updater can install this build before that repo is made private.

  Tokens (environment variables, never arguments):
    RELEASES_REPO_TOKEN  Fine-grained PAT with Contents read/write on pulse-releases only.
                         Required for -Target releases or both. Store it as the Actions
                         secret RELEASES_REPO_TOKEN on MugoJnr/pulse. Do not commit it.
    GH_TOKEN or GITHUB_TOKEN
                         Write access to MugoJnr/pulse. Actions sets GITHUB_TOKEN.

  The tag must be vX.Y.Z and must match <Version> in CpuTempWidget.csproj.
  Installed copies ignore a release that is not newer than the version they already run,
  so do not republish 1.12.4 as the transition build.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Tag,
    [Parameter(Mandatory = $true)][string]$Setup,
    [string]$NotesFile,
    [string]$Notes,
    [ValidateSet('both', 'source', 'releases')][string]$Target = 'both',
    [string]$SourceRepo = 'MugoJnr/pulse',
    [string]$ReleasesRepo = 'MugoJnr/pulse-releases'
)

$ErrorActionPreference = 'Stop'
if (Get-Variable -Name PSNativeCommandUseErrorActionPreference -ErrorAction SilentlyContinue) {
    $PSNativeCommandUseErrorActionPreference = $false
}

function Get-ReleaseVersion([string]$raw) {
    $v = $raw.Trim().TrimStart('v', 'V')
    $parts = $v.Split('.')
    if ($parts.Length -ge 3) {
        return '{0}.{1}.{2}' -f $parts[0], $parts[1], $parts[2]
    }
    return $v
}

if ($Tag -notmatch '^v\d+\.\d+\.\d+$') {
    throw "Tag '$Tag' must look like v1.2.3. Push an annotated tag (git tag -a) so the message becomes the release notes."
}

$setupItem = Get-Item -LiteralPath $Setup -ErrorAction Stop
if ($setupItem.Name -ne 'Pulse-Setup.exe') {
    throw "Refusing to upload '$($setupItem.Name)'. This script publishes Pulse-Setup.exe only, never source."
}
if ($setupItem.Length -lt 40MB) {
    throw "Pulse-Setup.exe is only $($setupItem.Length) bytes. Expected a self-contained build (~70 MB). Run .\build-setup.ps1 first."
}

$csproj = Join-Path (Split-Path $PSScriptRoot -Parent) 'CpuTempWidget.csproj'
if (-not (Test-Path -LiteralPath $csproj)) {
    throw "Could not find $csproj to verify the tag matches the assembly version."
}
$csprojText = Get-Content -Raw -LiteralPath $csproj
if ($csprojText -notmatch '<Version>([^<]+)</Version>') {
    throw "CpuTempWidget.csproj has no <Version>."
}
$csprojVersion = Get-ReleaseVersion $Matches[1]
$tagVersion = Get-ReleaseVersion $Tag
if ($csprojVersion -ne $tagVersion) {
    throw "Tag $Tag does not match CpuTempWidget.csproj Version $csprojVersion. Bump <Version> and commit before tagging. A release that is not newer than the installed copy will not install."
}

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    throw "GitHub CLI (gh) is required."
}

$publishSource = $Target -eq 'both' -or $Target -eq 'source'
$publishReleases = $Target -eq 'both' -or $Target -eq 'releases'

$sourceToken = $env:GH_TOKEN
if ([string]::IsNullOrWhiteSpace($sourceToken)) { $sourceToken = $env:GITHUB_TOKEN }
if ($publishSource -and [string]::IsNullOrWhiteSpace($sourceToken)) {
    throw "GH_TOKEN or GITHUB_TOKEN is required to publish to $SourceRepo. Actions provides GITHUB_TOKEN. Do not put the token on the command line."
}
if ($publishReleases -and [string]::IsNullOrWhiteSpace($env:RELEASES_REPO_TOKEN)) {
    throw @"
RELEASES_REPO_TOKEN is not set, so $ReleasesRepo would not receive this release.
Create a fine-grained PAT (resource owner MugoJnr, repository pulse-releases only, Contents: Read and write) and add it as the Actions secret RELEASES_REPO_TOKEN on MugoJnr/pulse.
Do not commit the token. Fine-grained PATs expire; renew the secret before it does.
"@
}

$notesPath = $NotesFile
$removeNotes = $false
if ([string]::IsNullOrWhiteSpace($notesPath)) {
    $notesPath = Join-Path ([System.IO.Path]::GetTempPath()) ("pulse-notes-" + [guid]::NewGuid().ToString('n') + '.md')
    $body = $Notes
    if ([string]::IsNullOrWhiteSpace($body)) { $body = "Pulse $tagVersion" }
    Set-Content -LiteralPath $notesPath -Value $body.Trim() -Encoding utf8
    $removeNotes = $true
}
elseif (-not (Test-Path -LiteralPath $notesPath)) {
    throw "Notes file not found: $notesPath"
}

$title = "Pulse $tagVersion"

function Test-GhRelease([string]$Repo, [string]$TagName) {
    & gh release view $TagName --repo $Repo --json tagName 1>$null 2>$null
    return $LASTEXITCODE -eq 0
}

function Publish-One([string]$Repo, [string]$Token) {
    $previous = $env:GH_TOKEN
    try {
        $env:GH_TOKEN = $Token
        if (Test-GhRelease -Repo $Repo -TagName $Tag) {
            Write-Host "Updating $Repo release $Tag"
            & gh release upload $Tag $setupItem.FullName --repo $Repo --clobber
            if ($LASTEXITCODE -ne 0) { throw "gh release upload failed for $Repo (exit $LASTEXITCODE)" }
            & gh release edit $Tag --repo $Repo --title $title --notes-file $notesPath
            if ($LASTEXITCODE -ne 0) { throw "gh release edit failed for $Repo (exit $LASTEXITCODE)" }
        }
        else {
            Write-Host "Creating $Repo release $Tag"
            & gh release create $Tag $setupItem.FullName --repo $Repo --title $title --notes-file $notesPath
            if ($LASTEXITCODE -ne 0) { throw "gh release create failed for $Repo (exit $LASTEXITCODE)" }
        }
        Write-Host "Published Pulse-Setup.exe to https://github.com/$Repo/releases/tag/$Tag"
    }
    finally {
        if ($null -eq $previous) {
            Remove-Item Env:GH_TOKEN -ErrorAction SilentlyContinue
        }
        else {
            $env:GH_TOKEN = $previous
        }
    }
}

try {
    if ($publishSource) {
        Publish-One -Repo $SourceRepo -Token $sourceToken.Trim()
    }
    if ($publishReleases) {
        Publish-One -Repo $ReleasesRepo -Token $env:RELEASES_REPO_TOKEN.Trim()
    }
}
finally {
    if ($removeNotes) {
        Remove-Item -LiteralPath $notesPath -Force -ErrorAction SilentlyContinue
    }
}
