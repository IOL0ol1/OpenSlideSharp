#!/usr/bin/env pwsh
#requires -Version 7
<#
.SYNOPSIS
  Checks that a commit may be released, so that nuget.org never gets older code under a newer version, nor one version
  built from two commits. Fails with the reason.
.DESCRIPTION
  Before building (the release workflow's version job), against the repository's v* tags:
  - the commit carries no v* tag: it is released already. A release that stopped half way is finished with "Re-run
    failed jobs" on its own run, which does not run this check again.
  - the commit of the highest v* tag (in NuGet order) is the commit or one of its ancestors: an older commit would be
    released under a higher version than newer code.
  With -Version and -Path, before pushing (the publish job), against nuget.org and the origin remote:
  - nuget.org has no OpenSlideSharp version above -Version;
  - the tag v<Version>, if origin has it, points at the commit;
  - every package of -Version already on nuget.org, and every nupkg in -Path, was built from the commit (the commit
    attribute of the nuspec's repository element), so an earlier attempt of the same release is the only one it joins.
.PARAMETER Sha
  The commit: any revision git resolves.
.PARAMETER Repository
  The git repository, default this one. Its v* tags must be fetched.
#>
[CmdletBinding(DefaultParameterSetName = 'Build')]
param(
    [Parameter(Mandatory)][string]$Sha,
    [Parameter(Mandatory, ParameterSetName = 'Publish')][string]$Version,
    [Parameter(Mandatory, ParameterSetName = 'Publish')][string]$Path,
    [string]$Repository
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
Import-Module (Join-Path $PSScriptRoot 'NuGetVersion.psm1') -Force
Add-Type -AssemblyName System.IO.Compression.FileSystem
if (-not $Repository) { $Repository = Split-Path -Parent $PSScriptRoot }

# The lines git prints (none when it prints nothing).
function Invoke-Git {
    $out = & git -C $Repository @args
    if ($LASTEXITCODE -ne 0) { throw "git $($args -join ' ') failed with exit code $LASTEXITCODE." }
    foreach ($line in @($out)) { if ($null -ne $line) { $line } }
}

# The commit attribute of a nuspec's <repository> element.
function Get-NuspecCommit([string]$Xml, [string]$Where) {
    $commitId = ([xml]$Xml.TrimStart([char]0xFEFF)).package.metadata.repository.commit
    if (-not $commitId) { throw "$Where has no repository commit in its nuspec." }
    $commitId
}

$commit = Invoke-Git rev-parse --verify "$Sha^{commit}"

if ($PSCmdlet.ParameterSetName -eq 'Build') {
    # Release tags: v<NuGet version>. Other v* tags are no releases.
    function Select-ReleaseTag {
        foreach ($tag in @($input)) {
            try { $null = ConvertTo-NuGetVersion $tag.Substring(1); $tag } catch { }
        }
    }
    $own = @(Invoke-Git tag --list 'v*' --points-at $commit | Select-ReleaseTag)
    if ($own.Count -gt 0) {
        throw "Commit $commit is released already, as $($own -join ', '). To finish a release that stopped half way, use `"Re-run failed jobs`" on its run (`"Re-run all jobs`" starts a new release); a new release needs a new commit."
    }
    $latest = $null
    foreach ($tag in @(Invoke-Git tag --list 'v*' | Select-ReleaseTag)) {
        if ($null -eq $latest -or (Compare-NuGetVersion $tag.Substring(1) $latest.Substring(1)) -gt 0) { $latest = $tag }
    }
    if ($null -eq $latest) {
        Write-Host "No release tag yet; commit $commit is not released."
    }
    else {
        $latestCommit = Invoke-Git rev-parse --verify "refs/tags/$latest^{commit}"
        & git -C $Repository merge-base --is-ancestor $latestCommit $commit
        if ($LASTEXITCODE -eq 1) {
            throw "Commit $commit does not contain the latest release, $latest ($latestCommit): releasing it would put older code under a higher version. Release a later commit, such as the head of master."
        }
        if ($LASTEXITCODE -ne 0) { throw "git merge-base failed with exit code $LASTEXITCODE." }
        Write-Host "Commit $commit comes after the latest release, $latest ($latestCommit), and is not released yet."
    }
}
else {
    $ids = 'OpenSlideSharp', 'OpenSlideSharp.SkiaSharp', 'OpenSlideSharp.runtime', 'OpenSlideSharp.AspNetCore', 'OpenSlideSharp.Tool'

    $highest = Get-MaxNuGetVersion (Get-PublishedNuGetVersions)
    if ((Compare-NuGetVersion $Version $highest) -lt 0) {
        throw "nuget.org has OpenSlideSharp $highest, higher than $Version. Run the release again for a new version."
    }
    Write-Host "Highest OpenSlideSharp on nuget.org: $highest"

    $tag = "v$Version"
    # The last line is the commit an annotated tag peels to.
    $remote = @(Invoke-Git ls-remote origin "refs/tags/$tag" "refs/tags/$tag^{}")
    if ($remote.Count -gt 0) {
        $target = ($remote[-1] -split "`t")[0]
        if ($target -ne $commit) { throw "The tag $tag exists already and points at $target, not at $commit." }
        Write-Host "The tag $tag points at $commit already: an earlier attempt of this release pushed it."
    }

    foreach ($id in $ids) {
        $lowerId = $id.ToLowerInvariant()
        $url = "https://api.nuget.org/v3-flatcontainer/$lowerId/$($Version.ToLowerInvariant())/$lowerId.nuspec"
        $response = Invoke-WebRequest -Uri $url -SkipHttpErrorCheck -Headers @{ 'User-Agent' = 'OpenSlideSharp-release' }
        if ($response.StatusCode -eq 404) { Write-Host "$id $Version is not on nuget.org."; continue }
        if ($response.StatusCode -ne 200) { throw "GET $url returned HTTP $($response.StatusCode)." }
        $content = $response.Content -is [byte[]] ? [Text.Encoding]::UTF8.GetString($response.Content) : $response.Content
        $built = Get-NuspecCommit $content "$id $Version on nuget.org"
        if ($built -ne $commit) {
            throw "$id $Version is on nuget.org already, built from commit $built, not ${commit}: this commit's packages must not join that version."
        }
        Write-Host "$id $Version is on nuget.org already, built from ${commit}: an earlier attempt of this release pushed it."
    }

    foreach ($id in $ids) {
        $nupkg = Join-Path $Path "$id.$Version.nupkg"
        $zip = [IO.Compression.ZipFile]::OpenRead($nupkg)
        try {
            $entry = @($zip.Entries | Where-Object { $_.FullName -notmatch '/' -and $_.FullName -like '*.nuspec' })[0]
            $reader = [IO.StreamReader]::new($entry.Open())
            try { $built = Get-NuspecCommit $reader.ReadToEnd() $nupkg } finally { $reader.Dispose() }
        }
        finally { $zip.Dispose() }
        if ($built -ne $commit) { throw "$nupkg was built from commit $built, not $commit." }
    }
    Write-Host "The five nupkgs in $Path were built from $commit, which may be released as $Version."
}
