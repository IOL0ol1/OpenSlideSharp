#!/usr/bin/env pwsh
#requires -Version 7
<#
.SYNOPSIS
  Lists the upstream openslide-bin releases that should be synced into eng/openslide.json, as a JSON array.
.DESCRIPTION
  Looks at the highest stable openslide-bin release (from the release list, not releases/latest) and the latest nightly in openslide/builds. A release is a
  candidate only when its NuGet version (version table in Get-ReleaseVersion.ps1) is strictly higher than the manifest's
  and than the highest version published on nuget.org. Old-series maintenance releases are therefore skipped, and the
  manifest never goes down. Output order is ascending by NuGet version, stable first. No update prints [].
  Each item: channel, tag, binVersion, version. A GitHub API failure exits non-zero; it never prints [].
  Transient failures (HTTP 500/502/503/504, connection errors) are retried, 3 attempts in all. Honours $env:GITHUB_TOKEN. -SelfTest runs built-in cases against mock data (no network) and exits 1 if one fails.
#>
[CmdletBinding()]
param(
    [string]$Manifest,
    [string[]]$PublishedVersions,
    [switch]$SelfTest
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
Import-Module (Join-Path $PSScriptRoot 'NuGetVersion.psm1') -Force

function Get-UpstreamNuGetVersion([string]$BinVersion, [string]$OpenSlideVersion, [string]$Date8, [bool]$Nightly) {
    # Version table: stable X.Y.Z.B -> X.Y.Z.B; nightly X.Y.Z.B+YYYYMMDD.nightly -> X.Y.Z.(B+1)-nightly.YYYYMMDD.g<hash8>.
    $core = ($BinVersion -split '\+')[0]
    if ($core -notmatch '^\d+(\.\d+){2,3}$') { throw "Unsupported openslide-bin version '$BinVersion'." }
    if (-not $Nightly) { return $core }
    if ($OpenSlideVersion -notmatch '-g([0-9a-f]{8,})$') { throw "Cannot extract the OpenSlide commit from '$OpenSlideVersion'." }
    $n = [int[]]@($core -split '\.')
    while ($n.Count -lt 4) { $n += 0 }
    '{0}.{1}.{2}.{3}-nightly.{4}.g{5}' -f $n[0], $n[1], $n[2], ($n[3] + 1), $Date8, $Matches[1].Substring(0, 8)
}

function Get-NightlyReleaseInfo($Release) {
    $tag = [string]$Release.tag_name
    if ($tag -notmatch '^v(\d+(?:\.\d+){2,3}\+(\d{8})\.nightly)$') { throw "Unexpected nightly tag '$tag'." }
    $bin = $Matches[1]; $date8 = $Matches[2]
    if ([string]$Release.body -notmatch '\*\*OpenSlide\*\*\s*\|\s*\*\*([^*\s|]+)\*\*') {
        throw "Release $tag lists no OpenSlide version in its notes, so its NuGet version cannot be computed."
    }
    $osv = $Matches[1]
    [pscustomobject]@{ Tag = $tag; BinVersion = $bin; OpenSlideVersion = $osv; Version = (Get-UpstreamNuGetVersion $bin $osv $date8 $true) }
}

function Get-UpstreamCandidates {
    param([Parameter(Mandatory)]$Manifest, $StableRelease, $NightlyRelease, [string[]]$PublishedVersions)
    $isNightlyManifest = $Manifest.channel -eq 'nightly'
    $manifestVersion = Get-UpstreamNuGetVersion ([string]$Manifest.binVersion) ([string]$Manifest.openslideVersion) ([string]$Manifest.date -replace '-', '') $isNightlyManifest
    $manifestTag = "v$($Manifest.binVersion)"
    $published = Get-MaxNuGetVersion $PublishedVersions
    # Strictly above both the manifest and the highest published version, and never the manifest's own tag.
    $isNew = {
        param($Tag, $Version)
        $Tag -ne $manifestTag -and (Compare-NuGetVersion $Version $manifestVersion) -gt 0 -and
            ($null -eq $published -or (Compare-NuGetVersion $Version $published) -gt 0)
    }
    $candidates = [System.Collections.Generic.List[object]]::new()
    if ($StableRelease) {
        $tag = [string]$StableRelease.tag_name
        $bin = $tag -replace '^v', ''
        $version = Get-UpstreamNuGetVersion $bin '' '' $false
        if (& $isNew $tag $version) {
            $candidates.Add([ordered]@{ channel = 'stable'; tag = $tag; binVersion = $bin; version = $version })
        }
    }
    if ($NightlyRelease) {
        $n = Get-NightlyReleaseInfo $NightlyRelease
        # After a stable candidate the manifest sits on that version, so the nightly has to beat it as well.
        $floor = if ($candidates.Count -gt 0) { $candidates[0].version } else { $null }
        if ((& $isNew $n.Tag $n.Version) -and ($null -eq $floor -or (Compare-NuGetVersion $n.Version $floor) -gt 0)) {
            $candidates.Add([ordered]@{ channel = 'nightly'; tag = $n.Tag; binVersion = $n.BinVersion; version = $n.Version })
        }
    }
    $candidates.ToArray()
}

function Select-LatestStableRelease($Releases) {
    # The highest stable release by NuGet order. releases/latest is creation-ordered and can point at an old-series maintenance release.
    $best = $null
    foreach ($r in @($Releases)) {
        if ($r.prerelease -or $r.draft -or [string]$r.tag_name -notmatch '^v\d+(\.\d+){2,3}$') { continue }
        if ($null -eq $best -or (Compare-NuGetVersion ([string]$r.tag_name -replace '^v', '') ([string]$best.tag_name -replace '^v', '')) -gt 0) { $best = $r }
    }
    $best
}

function Invoke-GitHubApi([string]$Path, [scriptblock]$Send, [scriptblock]$Sleep) {
    $headers = @{ Accept = 'application/vnd.github+json'; 'User-Agent' = 'OpenSlideSharp-upstream-sync' }
    if ($env:GITHUB_TOKEN) { $headers.Authorization = "Bearer $($env:GITHUB_TOKEN)" }
    $url = "https://api.github.com/$Path"
    $retry = @{}
    if ($Send) { $retry.Send = $Send }
    if ($Sleep) { $retry.Sleep = $Sleep }
    # Retries HTTP 500/502/503/504 and connection errors (3 attempts in all), never 4xx.
    $r = Invoke-WebRequestWithRetry -Uri $url -Headers $headers @retry
    if ($r.StatusCode -ne 200) { throw "GET $url returned HTTP $($r.StatusCode)." }
    $r.Content | ConvertFrom-Json -NoEnumerate
}

if ($SelfTest) {
    $results = [System.Collections.Generic.List[object]]::new()
    function Test-Case([string]$Name, [scriptblock]$Body) {
        try {
            # A case must produce exactly one boolean; stray output (e.g. a negative case that did not throw) is a failure.
            $out = @(& $Body)
            $ok = $out.Count -eq 1 -and $out[0] -is [bool] -and $out[0]
        } catch { $ok = $false; $Name += " (threw: $($_.Exception.Message))" }
        $results.Add([pscustomobject]@{ Name = $Name; Ok = $ok })
    }
    function New-Manifest($Channel, $Bin, $OsVersion, $Commit, $Date) {
        [pscustomobject]@{ channel = $Channel; binVersion = $Bin; openslideVersion = $OsVersion; openslideCommit = $Commit; date = $Date }
    }
    function New-Stable($Tag) { [pscustomobject]@{ tag_name = $Tag; prerelease = $false; draft = $false; body = '## Changes' } }
    function New-Nightly($Tag, $OsVersion) {
        [pscustomobject]@{ tag_name = $Tag; prerelease = $true; draft = $false
            body = "See the build index.`n`n| Software | Version |`n| -------- | ------- |`n| **OpenSlide**         | **$OsVersion**         |`n| cairo | 1.18.4-2 |" }
    }
    function Find($Manifest, $Stable, $Nightly, $Published = @('1.1.2')) {
        ,@(Get-UpstreamCandidates -Manifest $Manifest -StableRelease $Stable -NightlyRelease $Nightly -PublishedVersions $Published)
    }
    $nightlyManifest = New-Manifest 'nightly' '4.0.1.2+20261102.nightly' 'v4.0.1-49-g174630bf' '174630bf' '2026-11-02'
    $stableManifest = New-Manifest 'stable' '4.0.1.2' 'v4.0.1' 'fe79a971' '2026-06-20'

    Test-Case 'nightly manifest equal to upstream: no candidates' {
        (Find $nightlyManifest (New-Stable 'v4.0.1.2') (New-Nightly 'v4.0.1.2+20261102.nightly' 'v4.0.1-49-g174630bf')).Count -eq 0 }
    Test-Case 'old-series stable 4.0.0.14 vs manifest 4.0.1.2: skipped' {
        (Find $stableManifest (New-Stable 'v4.0.0.14') $null).Count -eq 0 }
    Test-Case 'stable 4.0.2.0 and nightly 4.0.2.0+...: stable first, then nightly' {
        $c = Find $stableManifest (New-Stable 'v4.0.2.0') (New-Nightly 'v4.0.2.0+20261201.nightly' 'v4.0.2-3-gabcdef0123')
        $c.Count -eq 2 -and $c[0].channel -eq 'stable' -and $c[0].tag -eq 'v4.0.2.0' -and $c[0].version -eq '4.0.2.0' -and $c[0].binVersion -eq '4.0.2.0' -and
        $c[1].channel -eq 'nightly' -and $c[1].tag -eq 'v4.0.2.0+20261201.nightly' -and $c[1].version -eq '4.0.2.1-nightly.20261201.gabcdef01' -and
        $c[1].binVersion -eq '4.0.2.0+20261201.nightly' }
    Test-Case 'newer nightly only, manifest nightly: one nightly candidate' {
        $c = Find $nightlyManifest (New-Stable 'v4.0.1.2') (New-Nightly 'v4.0.1.2+20261110.nightly' 'v4.0.1-52-g11112222')
        $c.Count -eq 1 -and $c[0].channel -eq 'nightly' -and $c[0].version -eq '4.0.1.3-nightly.20261110.g11112222' }
    Test-Case 'older nightly than the manifest: skipped (never downgrade)' {
        (Find $nightlyManifest $null (New-Nightly 'v4.0.1.2+20261101.nightly' 'v4.0.1-47-g377f40b1')).Count -eq 0 }
    Test-Case 'nightly not above the highest published version: skipped' {
        (Find $nightlyManifest $null (New-Nightly 'v4.0.1.2+20261110.nightly' 'v4.0.1-52-g11112222') @('1.1.2', '4.0.1.3')).Count -eq 0 }
    Test-Case 'stable manifest 4.0.1.2, stable upstream 4.0.1.2: not a candidate (manifest tag)' {
        (Find $stableManifest (New-Stable 'v4.0.1.2') $null).Count -eq 0 }
    Test-Case 'stable not above the highest published version: skipped' {
        (Find $stableManifest (New-Stable 'v4.0.2.0') $null @('4.0.2.0')).Count -eq 0 }
    Test-Case 'nightly based on an older series than a new stable: only the stable is kept' {
        $c = Find $stableManifest (New-Stable 'v4.0.2.0') (New-Nightly 'v4.0.1.9+20261201.nightly' 'v4.0.1-60-g99998888')
        $c.Count -eq 1 -and $c[0].channel -eq 'stable' }
    Test-Case 'only stable exists upstream' {
        $c = Find $nightlyManifest (New-Stable 'v4.1.0.0') $null
        $c.Count -eq 1 -and $c[0].version -eq '4.1.0.0' }
    Test-Case 'only nightly exists upstream' {
        $c = Find $stableManifest $null (New-Nightly 'v4.0.1.2+20261110.nightly' 'v4.0.1-52-g11112222')
        $c.Count -eq 1 -and $c[0].version -eq '4.0.1.3-nightly.20261110.g11112222' }
    Test-Case 'nothing exists upstream' { (Find $stableManifest $null $null).Count -eq 0 }
    Test-Case 'empty published list is accepted' { (Find $stableManifest (New-Stable 'v4.0.2.0') $null @()).Count -eq 1 }
    Test-Case 'nightly release without an OpenSlide row in the notes is an error' {
        try { $null = Find $stableManifest $null ([pscustomobject]@{ tag_name = 'v4.0.1.3+20261110.nightly'; body = 'nothing' }); $false } catch { $_.Exception.Message -match 'lists no OpenSlide version' } }
    Test-Case 'latest stable is the highest, not the newest created (old-series maintenance release listed first)' {
        $rel = @((New-Stable 'v4.0.0.14'), (New-Stable 'v4.0.1.2'), (New-Stable 'v4.0.1.1'), [pscustomobject]@{ tag_name = 'v9.9.9.9'; prerelease = $true; draft = $false })
        (Select-LatestStableRelease $rel).tag_name -eq 'v4.0.1.2' -and
        (Select-LatestStableRelease @((New-Stable 'v4.0.0.14'), (New-Stable 'v4.0.2.0'), (New-Stable 'v4.0.1.9'))).tag_name -eq 'v4.0.2.0' }
    Test-Case 'a newer-created old-series stable does not hide the new stable' {
        $latest = Select-LatestStableRelease @((New-Stable 'v4.0.0.14'), (New-Stable 'v4.0.2.0'))
        $c = Find $stableManifest $latest $null
        $c.Count -eq 1 -and $c[0].tag -eq 'v4.0.2.0' }
    Test-Case 'no stable releases at all gives $null' { $null -eq (Select-LatestStableRelease @()) }
    Test-Case 'Compare 4.0.1.3-nightly.20261102.g174630bf < 4.0.1.3' {
        (Compare-NuGetVersion '4.0.1.3-nightly.20261102.g174630bf' '4.0.1.3') -eq -1 }
    Test-Case 'Compare 1.1.2 < 4.0.1.2' { (Compare-NuGetVersion '1.1.2' '4.0.1.2') -eq -1 }

    function New-Response([int]$Status, [string]$Content = '[]') { [pscustomobject]@{ StatusCode = $Status; Content = $Content } }
    function New-Sender($Script) {
        # A mock Send that serves the given statuses in order, repeating the last (a string is a thrown connection error), and counts its calls.
        $state = @{ Calls = 0; Waits = [System.Collections.Generic.List[int]]::new() }
        $state.Send = { param($Params) $i = $state.Calls++; $x = $Script[[Math]::Min($i, $Script.Count - 1)]; if ($x -is [string]) { throw $x }; New-Response $x }.GetNewClosure()
        $state.Sleep = { param($Seconds) $state.Waits.Add($Seconds) }.GetNewClosure()
        $state
    }
    function Get-ApiOutcome($State) {
        # 'ok' or the error message; -WarningAction Ignore keeps the retry warnings out of the self-test output.
        try { $null = Invoke-GitHubApi 'x' -Send $State.Send -Sleep $State.Sleep -WarningAction Ignore; 'ok' } catch { $_.Exception.Message }
    }
    Test-Case 'retry: 502 then 200 succeeds after exactly one retry' {
        $s = New-Sender @(502, 200)
        (Get-ApiOutcome $s) -eq 'ok' -and $s.Calls -eq 2 -and ($s.Waits -join ',') -eq '5' }
    Test-Case 'retry: 502 x3 fails with HTTP 502 after 3 attempts and waits 5 s, 15 s' {
        $s = New-Sender @(502)
        (Get-ApiOutcome $s) -match 'returned HTTP 502\.$' -and $s.Calls -eq 3 -and ($s.Waits -join ',') -eq '5,15' }
    Test-Case 'retry: 404 fails at once without a retry' {
        $s = New-Sender @(404, 200)
        (Get-ApiOutcome $s) -match 'returned HTTP 404\.$' -and $s.Calls -eq 1 -and $s.Waits.Count -eq 0 }
    Test-Case 'retry: 403 fails at once without a retry' {
        $s = New-Sender @(403, 200)
        (Get-ApiOutcome $s) -match 'returned HTTP 403\.$' -and $s.Calls -eq 1 }
    Test-Case 'retry: a connection error then 200 succeeds after one retry' {
        $s = New-Sender @('The operation was canceled.', 200)
        $s.Send = { param($Params) if ($s.Calls++ -eq 0) { throw 'The operation was canceled.' }; New-Response 200 }.GetNewClosure()
        (Get-ApiOutcome $s) -eq 'ok' -and $s.Calls -eq 2 }
    Test-Case 'retry: connection errors x3 are rethrown after 3 attempts' {
        $s = New-Sender @('boom')
        (Get-ApiOutcome $s) -match 'boom' -and $s.Calls -eq 3 }
    Test-Case 'retry: nuget.org 503 then 200 succeeds' {
        $s = New-Sender @(503)
        $s.Send = { param($Params) if ($s.Calls++ -eq 0) { New-Response 503 } else { New-Response 200 '{"versions":["1.1.2"]}' } }.GetNewClosure()
        $v = @(Get-PublishedNuGetVersions -Send $s.Send -Sleep $s.Sleep -WarningAction Ignore)
        $v.Count -eq 1 -and $v[0] -eq '1.1.2' -and $s.Calls -eq 2 }
    Test-Case 'retry: nuget.org 404 stays an error and is not retried' {
        $s = New-Sender @(404)
        $msg = try { $null = Get-PublishedNuGetVersions -Send $s.Send -Sleep $s.Sleep; 'no error' } catch { $_.Exception.Message }
        $msg -match 'returned HTTP 404\.$' -and $s.Calls -eq 1 }

    foreach ($r in $results) { Write-Host ("{0} {1}" -f ($r.Ok ? 'PASS' : 'FAIL'), $r.Name) }
    $failed = @($results | Where-Object { -not $_.Ok }).Count
    Write-Host "$($results.Count - $failed) passed, $failed failed"
    exit ($failed -gt 0 ? 1 : 0)
}

$repoRoot = Split-Path -Parent $PSScriptRoot
try {
    if (-not $Manifest) { $Manifest = Join-Path $repoRoot 'eng/openslide.json' }
    $manifestObject = Get-Content -Raw -Path $Manifest | ConvertFrom-Json
    if ($null -eq $PSBoundParameters['PublishedVersions']) { $PublishedVersions = Get-PublishedNuGetVersions }

    $stable = Select-LatestStableRelease (Invoke-GitHubApi 'repos/openslide/openslide-bin/releases?per_page=100')
    $nightly = $null
    foreach ($r in (Invoke-GitHubApi 'repos/openslide/builds/releases?per_page=30')) {
        if ($r.prerelease -and -not $r.draft -and $r.assets.Count -gt 0) { $nightly = $r; break }
    }
    $found = @(Get-UpstreamCandidates -Manifest $manifestObject -StableRelease $stable -NightlyRelease $nightly -PublishedVersions $PublishedVersions)
    if ($found.Count -eq 0) { '[]' } else { $found | ConvertTo-Json -AsArray -Depth 3 }
} catch {
    [Console]::Error.WriteLine("Find-UpstreamUpdate: $($_.Exception.Message)")
    exit 1
}
