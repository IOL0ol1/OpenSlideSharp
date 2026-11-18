#!/usr/bin/env pwsh
#requires -Version 7
<#
.SYNOPSIS
  Computes the NuGet release version for the current eng/openslide.json and prints it, with the release metadata, as JSON.
.DESCRIPTION
  Version table:
    stable manifest  X.Y.Z.B                          -> X.Y.Z.B
    nightly manifest X.Y.Z.B+YYYYMMDD.nightly         -> X.Y.Z.(B+1)-nightly.YYYYMMDD.g<hash8>
    C#-only release of an unchanged upstream (the tag of the base version exists) -> base version + ".N", N from 1
    stable manifest with a C#-only release           -> numbered like a nightly: the stable release's date and commit.
  The version must be higher than every version published on nuget.org, or the script fails.
  -PublishedVersions defaults to the nuget.org flat container, -ExistingTags to `git tag --list 'v*'`.
  The release notes name the openslide-bin build, the OpenSlide version and commit, and the attached source tarball.
  For a stable manifest whose previous release (the highest lower v* tag or published version) was built on a nightly,
  as its tagged eng/openslide.json says (or, without one, its version's form), they also say that functions only the
  nightly had may be gone from OpenSlideSharp.Native.
  -SelfTest runs built-in cases against mock data (no network, no git) and exits 1 if one fails.
#>
[CmdletBinding()]
param(
    [string]$Manifest,
    [string[]]$PublishedVersions,
    [string[]]$ExistingTags,
    [switch]$SelfTest
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
Import-Module (Join-Path $PSScriptRoot 'NuGetVersion.psm1') -Force

function Get-NextSuffix([string]$Base, [string[]]$Tags) {
    # Highest N among the tags v<Base> (counts as 0) and v<Base>.N, plus 1; $null when none of them exists.
    $max = -1
    $pattern = '^v' + [regex]::Escape($Base) + '(?:\.(\d+))?$'
    foreach ($t in @($Tags)) {
        if ($t -match $pattern) {   # -match is case-insensitive
            $n = if ($Matches[1]) { [int]$Matches[1] } else { 0 }
            if ($n -gt $max) { $max = $n }
        }
    }
    if ($max -lt 0) { $null } else { $max + 1 }
}

function Get-PreviousRelease([string]$Version, [string[]]$Tags, [scriptblock]$ChannelOfTag) {
    # The highest v<version> tag below $Version (published versions are among the tags as v<version>), and the channel
    # of the openslide-bin build it was made from: from its eng/openslide.json via $ChannelOfTag when that knows it,
    # else from the version's form (only nightly-numbered versions have a -nightly label). $null without one.
    $previous = $null
    $previousTag = $null
    foreach ($t in @($Tags)) {
        if ("$t" -notmatch '^v(.+)$') { continue }
        $v = $Matches[1]
        try { $lower = (Compare-NuGetVersion $v $Version) -lt 0 } catch { continue }   # a tag that is no version
        if ($lower -and ($null -eq $previous -or (Compare-NuGetVersion $v $previous) -gt 0)) { $previous = $v; $previousTag = $t }
    }
    if ($null -eq $previous) { return $null }
    $channel = if ($ChannelOfTag) { & $ChannelOfTag $previousTag } else { $null }
    if ($channel -notin 'nightly', 'stable') {
        $labels = (ConvertTo-NuGetVersion $previous).Labels
        $channel = ($labels.Count -gt 0 -and $labels[0] -eq 'nightly') ? 'nightly' : 'stable'
    }
    [pscustomobject]@{ Version = $previous; Channel = $channel }
}

function Get-ReleaseVersionInfo {
    param([Parameter(Mandatory)]$Manifest, [string[]]$PublishedVersions, [string[]]$ExistingTags, [scriptblock]$ChannelOfTag)
    $bin = [string]$Manifest.binVersion
    # Existing tags and published versions both occupy a number (published versions are written as tags: v<version>).
    $ExistingTags = @($ExistingTags) + @(@($PublishedVersions) | Where-Object { $_ } | ForEach-Object { "v$_" })
    $core = ($bin -split '\+')[0]
    if ($core -notmatch '^\d+(\.\d+){2,3}$') { throw "Unsupported binVersion '$bin'." }
    $nums = [int[]]@($core -split '\.')
    while ($nums.Count -lt 4) { $nums += 0 }
    $nightlyBase = '{0}.{1}.{2}.{3}-nightly.{4}.g{5}' -f $nums[0], $nums[1], $nums[2], ($nums[3] + 1),
        ([string]$Manifest.date -replace '-', ''), ([string]$Manifest.openslideCommit).Substring(0, 8)
    if ($Manifest.channel -eq 'nightly') {
        $suffix = Get-NextSuffix $nightlyBase $ExistingTags
        $version = if ($null -eq $suffix) { $nightlyBase } else { "$nightlyBase.$suffix" }
    } elseif ($Manifest.channel -eq 'stable') {
        if ($null -eq (Get-NextSuffix $core $ExistingTags)) {
            $version = $core
        } else {
            $suffix = Get-NextSuffix $nightlyBase $ExistingTags
            $version = "$nightlyBase.$([Math]::Max(1, [int]$suffix))"
        }
    } else {
        throw "Unknown manifest channel '$($Manifest.channel)'."
    }

    $max = Get-MaxNuGetVersion $PublishedVersions
    if ($null -ne $max -and (Compare-NuGetVersion $version $max) -le 0) {
        throw "Version $version is not higher than the highest version published on nuget.org ($max); refusing to release an out-of-order version."
    }

    $osv = [string]$Manifest.openslideVersion
    $upstreamVersion = if ($osv -match '-g[0-9a-fA-F]+$') { $osv } else { "$osv.g$($Manifest.openslideCommit)" }
    $tarball = "openslide-bin-$bin.tar.gz"
    $copy = if ($Manifest.channel -eq 'nightly') {
        'a copy of that tarball. The upstream nightly URL expires, so this copy is the permanent source for the bundled binaries.'
    } else {
        'a copy of that tarball, the source for the bundled binaries.'
    }
    $lines = @(
        "OpenSlideSharp $version, built against OpenSlide binaries from openslide-bin ``$bin``.",
        '',
        "- openslide-bin: ``$bin`` ($($Manifest.releaseUrl))",
        "- OpenSlide: ``$($Manifest.openslideVersion)`` (commit ``$($Manifest.openslideCommit)``)",
        "- Upstream source tarball: $($Manifest.source.url)",
        "- This release attaches ``$tarball`` (sha256 ``$($Manifest.source.sha256)``), $copy"
    )
    # Design §7.2, rule 5: the generated Native layer follows the headers of the build, and a nightly may have API that
    # the next release does not; the rest of OpenSlideSharp wraps only API of OpenSlide releases.
    $previous = Get-PreviousRelease $version $ExistingTags $ChannelOfTag
    if ($Manifest.channel -eq 'stable' -and $previous -and $previous.Channel -eq 'nightly') {
        $lines += ''
        $lines += "The previous release, ``$($previous.Version)``, was built on an openslide-bin nightly. ``OpenSlideSharp.Native`` is generated from the headers of the OpenSlide build, so functions that only that nightly had may be gone from it in this release: API that only a nightly has is not stable. The rest of OpenSlideSharp uses only functions that OpenSlide releases have."
    }
    $notes = $lines -join "`n"
    [ordered]@{
        version              = $version
        prerelease           = $version.Contains('-')
        openslideUpstreamVersion = $upstreamVersion
        informationalVersion = "$version+openslide.$upstreamVersion"
        tag                  = "v$version"
        releaseName          = "OpenSlideSharp $version"
        notes                = $notes
    }
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
        [pscustomobject]@{ channel = $Channel; binVersion = $Bin; openslideVersion = $OsVersion; openslideCommit = $Commit; date = $Date
            source = [pscustomobject]@{ url = 'https://example.invalid/src.tar.gz'; sha256 = ('ab' * 32) } }
    }
    function Get-Version($Manifest, $Published = @('1.1.2'), $Tags = @()) {
        (Get-ReleaseVersionInfo -Manifest $Manifest -PublishedVersions $Published -ExistingTags $Tags).version
    }
    $nightly = New-Manifest 'nightly' '4.0.1.2+20261102.nightly' 'v4.0.1-49-g174630bf' '174630bf' '2026-11-02'
    $stable = New-Manifest 'stable' '4.0.1.2' 'v4.0.1' 'fe79a971' '2026-06-20' 
    $nb = '4.0.1.3-nightly.20261102.g174630bf'

    Test-Case "Compare $nb < 4.0.1.3" { (Compare-NuGetVersion $nb '4.0.1.3') -eq -1 }
    Test-Case "Compare $nb > 4.0.1.2" { (Compare-NuGetVersion $nb '4.0.1.2') -eq 1 }
    Test-Case 'Compare ...g174630bf.10 > ...g174630bf.9' { (Compare-NuGetVersion "$nb.10" "$nb.9") -eq 1 }
    Test-Case 'Compare ...g174630bf.1 > ...g174630bf' { (Compare-NuGetVersion "$nb.1" $nb) -eq 1 }
    Test-Case 'Compare 1.1.2 < 4.0.1.2' { (Compare-NuGetVersion '1.1.2' '4.0.1.2') -eq -1 }
    Test-Case 'Compare 4.0.1 == 4.0.1.0' { (Compare-NuGetVersion '4.0.1' '4.0.1.0') -eq 0 }
    Test-Case 'Compare 1.0.0-alpha < 1.0.0-alpha.1 < 1.0.0-beta' {
        (Compare-NuGetVersion '1.0.0-alpha' '1.0.0-alpha.1') -eq -1 -and (Compare-NuGetVersion '1.0.0-alpha.1' '1.0.0-beta') -eq -1 -and
        (Compare-NuGetVersion '1.0.0-beta' '1.0.0-alpha') -eq 1 }
    Test-Case 'Compare ignores case in labels and metadata' {
        (Compare-NuGetVersion '1.0.0-Beta+abc' '1.0.0-beta+xyz') -eq 0 }
    Test-Case 'Compare numeric label segment < alphanumeric segment' { (Compare-NuGetVersion '1.0.0-1' '1.0.0-a') -eq -1 }
    Test-Case 'Compare 4.0.1.3 > 1.1.2-beta' { (Compare-NuGetVersion '4.0.1.3' '1.1.2-beta') -eq 1 }

    Test-Case 'nightly manifest, no tag, published 1.1.2' { (Get-Version $nightly) -eq $nb }
    Test-Case 'nightly manifest, base tag exists -> .1' { (Get-Version $nightly -Tags "v$nb") -eq "$nb.1" }
    Test-Case 'nightly manifest, tags .1 and .2 -> .3' { (Get-Version $nightly -Tags "v$nb.1", "v$nb.2") -eq "$nb.3" }
    Test-Case 'nightly manifest, .9 and .10 -> .11 (numeric max)' { (Get-Version $nightly -Tags "v$nb", "v$nb.9", "v$nb.10") -eq "$nb.11" }
    Test-Case 'unrelated tags are ignored' { (Get-Version $nightly -Tags 'v1.1.1', 'v4.0.1.3', "v$nb-x") -eq $nb }
    Test-Case 'stable manifest, no tag -> 4.0.1.2' { (Get-Version $stable) -eq '4.0.1.2' }
    Test-Case 'stable manifest, tag v4.0.1.2 exists -> nightly-numbered .1' {
        (Get-Version $stable -Tags 'v4.0.1.2') -eq '4.0.1.3-nightly.20260620.gfe79a971.1' }
    Test-Case 'stable manifest, v4.0.1.2 and .1 exist -> .2' {
        (Get-Version $stable -Tags 'v4.0.1.2', 'v4.0.1.3-nightly.20260620.gfe79a971.1') -eq '4.0.1.3-nightly.20260620.gfe79a971.2' }
    Test-Case 'monotonic: published 4.0.1.3 rejects nightly 4.0.1.3-nightly...' {
        try { $null = Get-Version $nightly -Published '1.1.2', '4.0.1.3'; $false } catch { $_.Exception.Message -match 'not higher' } }
    Test-Case 'monotonic: published 4.0.1.4 rejects stable 4.0.1.2' {
        try { $null = Get-Version $stable -Published '4.0.1.4'; $false } catch { $_.Exception.Message -match 'not higher' } }
    Test-Case 'base already published, no tag -> .1' { (Get-Version $nightly -Published '1.1.2', $nb) -eq "$nb.1" }
    Test-Case 'published lower-case version counts case-insensitively -> .1' {
        (Get-Version $nightly -Published $nb.ToLowerInvariant()) -eq "$nb.1" }
    Test-Case 'published .2 and tag .1 -> .3' { (Get-Version $nightly -Published '1.1.2', "$nb.2" -Tags "v$nb.1") -eq "$nb.3" }
    Test-Case 'published .1 and tags base, .2 -> .3' { (Get-Version $nightly -Published "$nb.1" -Tags "v$nb", "v$nb.2") -eq "$nb.3" }
    Test-Case 'empty fetched published list is refused' {
        try { $null = Assert-PublishedNuGetVersions @(); $false } catch { $_.Exception.Message -match 'no published versions' } }
    Test-Case 'non-empty fetched published list is accepted' { @(Assert-PublishedNuGetVersions @('1.1.2')).Count -eq 1 }
    Test-Case 'empty published list is accepted' { (Get-Version $nightly -Published @()) -eq $nb }

    $info = Get-ReleaseVersionInfo -Manifest $nightly -PublishedVersions @('1.1.2') -ExistingTags @()
    Test-Case 'nightly is a prerelease; stable is not' {
        $info.prerelease -eq $true -and (Get-ReleaseVersionInfo -Manifest $stable -PublishedVersions @() -ExistingTags @()).prerelease -eq $false }
    Test-Case 'informationalVersion = version+openslide.<openslideUpstreamVersion>' {
        $info.informationalVersion -eq "$nb+openslide.v4.0.1-49-g174630bf" -and $info.openslideUpstreamVersion -eq 'v4.0.1-49-g174630bf' }
    Test-Case 'stable: openslideUpstreamVersion gets the commit appended' {
        $s = Get-ReleaseVersionInfo -Manifest $stable -PublishedVersions @('1.1.2') -ExistingTags @()
        $s.openslideUpstreamVersion -eq 'v4.0.1.gfe79a971' -and $s.informationalVersion -eq '4.0.1.2+openslide.v4.0.1.gfe79a971' }
    Test-Case 'tag and releaseName' { $info.tag -eq "v$nb" -and $info.releaseName -eq "OpenSlideSharp $nb" }
    Test-Case 'notes name versions, commit, source URL and the attached tarball with its sha256' {
        $info.notes -match [regex]::Escape('4.0.1.2+20261102.nightly') -and $info.notes -match 'v4\.0\.1-49-g174630bf' -and
        $info.notes -match '174630bf' -and $info.notes -match 'example\.invalid/src\.tar\.gz' -and
        $info.notes -match [regex]::Escape('openslide-bin-4.0.1.2+20261102.nightly.tar.gz') -and $info.notes -match ('ab' * 32) }

    function Get-Notes($Manifest, $Published = @('1.1.2'), $Tags = @(), $ChannelOfTag = $null) {
        (Get-ReleaseVersionInfo -Manifest $Manifest -PublishedVersions $Published -ExistingTags $Tags -ChannelOfTag $ChannelOfTag).notes
    }
    $earlyNightly = '4.0.1.2-nightly.20260601.g0123abcd'   # a nightly on openslide-bin 4.0.1.1, below stable 4.0.1.2
    $stableNext = New-Manifest 'stable' '4.0.1.3' 'v4.0.2' '0a1b2c3d' '2026-12-01'
    $stableBasedRelease = '4.0.1.3-nightly.20260620.gfe79a971.1'   # a C#-only release on the stable 4.0.1.2 manifest
    Test-Case 'nightly notes say the upstream nightly URL expires' { $info.notes -match 'nightly URL expires' }
    Test-Case 'stable notes attach the tarball and do not mention an expiring nightly URL' {
        $n = Get-Notes $stable
        $n -notmatch 'nightly' -and $n -match [regex]::Escape('openslide-bin-4.0.1.2.tar.gz') -and $n -match ('ab' * 32) }
    Test-Case 'stable after a nightly release: the notes say nightly-only Native functions may be gone' {
        $n = Get-Notes $stable -Tags 'v1.1.1', "v$earlyNightly"
        $n -match 'OpenSlideSharp\.Native' -and $n -match [regex]::Escape("previous release, ``$earlyNightly``") }
    Test-Case 'stable after a stable release: no such note' { (Get-Notes $stableNext -Tags 'v1.1.1', 'v4.0.1.2') -notmatch 'OpenSlideSharp\.Native' }
    Test-Case 'first release, after 1.x only: no such note' { (Get-Notes $stable -Tags 'v1.0.1.3', 'v1.1.0', 'v1.1.1') -notmatch 'OpenSlideSharp\.Native' }
    Test-Case 'nightly manifest: no such note' { (Get-Notes $nightly -Tags "v$earlyNightly") -notmatch 'OpenSlideSharp\.Native' }
    Test-Case 'a published nightly without a tag counts as the previous release' {
        (Get-Notes $stable -Published '1.1.2', $earlyNightly -ChannelOfTag { $null }) -match [regex]::Escape("previous release, ``$earlyNightly``") }
    Test-Case 'higher and unparsable tags are not the previous release' {
        (Get-Notes $stable -Tags "v$earlyNightly", 'v4.0.1.3', 'v4.0.1.4-nightly.20261201.gaaaaaaaa', 'vnext', 'v1.x') -match [regex]::Escape("previous release, ``$earlyNightly``") }
    Test-Case "the previous release's tagged manifest decides its channel over the version's form" {
        $tags = 'v4.0.1.2', "v$stableBasedRelease"
        $byManifest = Get-Notes $stableNext -Tags $tags -ChannelOfTag { param($Tag) if ($Tag -eq "v$stableBasedRelease") { 'stable' } }
        $byForm = Get-Notes $stableNext -Tags $tags -ChannelOfTag { param($Tag) $null }
        $nightlyByManifest = Get-Notes $stableNext -Tags 'v4.0.1.2', 'v4.0.1.3-nightly.20261102.g174630bf' -ChannelOfTag { param($Tag) 'nightly' }
        $byManifest -notmatch 'OpenSlideSharp\.Native' -and $byForm -match [regex]::Escape("previous release, ``$stableBasedRelease``") -and
        $nightlyByManifest -match 'OpenSlideSharp\.Native' }

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
    if ($null -eq $PSBoundParameters['ExistingTags']) {
        $ExistingTags = @(git -C $repoRoot tag --list 'v*')
        if ($LASTEXITCODE -ne 0) { throw "git tag --list failed with exit code $LASTEXITCODE." }
    }
    # The channel in eng/openslide.json of a tagged commit; $null for a tag that is not here or has no manifest (1.x).
    $channelOfTag = {
        param([string]$Tag)
        $json = git -C $repoRoot show "refs/tags/${Tag}:eng/openslide.json" 2> $null
        if ($LASTEXITCODE -ne 0) { return $null }
        try { [string](($json -join "`n") | ConvertFrom-Json).channel } catch { $null }
    }
    Get-ReleaseVersionInfo -Manifest $manifestObject -PublishedVersions $PublishedVersions -ExistingTags $ExistingTags -ChannelOfTag $channelOfTag |
        ConvertTo-Json -Depth 3
} catch {
    [Console]::Error.WriteLine("Get-ReleaseVersion: $($_.Exception.Message)")
    exit 1
}
