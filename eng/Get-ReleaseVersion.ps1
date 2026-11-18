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

function Get-ReleaseVersionInfo {
    param([Parameter(Mandatory)]$Manifest, [string[]]$PublishedVersions, [string[]]$ExistingTags)
    $bin = [string]$Manifest.binVersion
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

    $tarball = "openslide-bin-$bin.tar.gz"
    $notes = @(
        "OpenSlideSharp $version, built against OpenSlide binaries from openslide-bin ``$bin``.",
        '',
        "- openslide-bin: ``$bin`` ($($Manifest.releaseUrl))",
        "- OpenSlide: ``$($Manifest.openslideVersion)`` (commit ``$($Manifest.openslideCommit)``)",
        "- Upstream source tarball: $($Manifest.source.url)",
        "- This release attaches ``$tarball`` (sha256 ``$($Manifest.source.sha256)``), a copy of that tarball. The upstream nightly URL expires, so this copy is the permanent source for the bundled binaries."
    ) -join "`n"
    [ordered]@{
        version              = $version
        prerelease           = $version.Contains('-')
        informationalVersion = "$version+openslide.$($Manifest.openslideVersion)"
        tag                  = "v$version"
        releaseName          = "OpenSlideSharp $version"
        notes                = $notes
    }
}

if ($SelfTest) {
    $results = [System.Collections.Generic.List[object]]::new()
    function Test-Case([string]$Name, [scriptblock]$Body) {
        try { $ok = [bool](& $Body) } catch { $ok = $false; $Name += " (threw: $($_.Exception.Message))" }
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
        try { Get-Version $nightly -Published '1.1.2', '4.0.1.3'; $false } catch { $_.Exception.Message -match 'not higher' } }
    Test-Case 'monotonic: published version equal to the computed one is rejected' {
        try { Get-Version $stable -Published '4.0.1.2'; $false } catch { $_.Exception.Message -match 'not higher' } }
    Test-Case 'published versions are lower-case and compared case-insensitively' {
        try { Get-Version $nightly -Published $nb.ToLowerInvariant(); $false } catch { $_.Exception.Message -match 'not higher' } }
    Test-Case 'empty published list is accepted' { (Get-Version $nightly -Published @()) -eq $nb }

    $info = Get-ReleaseVersionInfo -Manifest $nightly -PublishedVersions @('1.1.2') -ExistingTags @()
    Test-Case 'nightly is a prerelease; stable is not' {
        $info.prerelease -eq $true -and (Get-ReleaseVersionInfo -Manifest $stable -PublishedVersions @() -ExistingTags @()).prerelease -eq $false }
    Test-Case 'informationalVersion = version+openslide.<openslideVersion>' {
        $info.informationalVersion -eq "$nb+openslide.v4.0.1-49-g174630bf" }
    Test-Case 'tag and releaseName' { $info.tag -eq "v$nb" -and $info.releaseName -eq "OpenSlideSharp $nb" }
    Test-Case 'notes name versions, commit, source URL and the attached tarball with its sha256' {
        $info.notes -match [regex]::Escape('4.0.1.2+20261102.nightly') -and $info.notes -match 'v4\.0\.1-49-g174630bf' -and
        $info.notes -match '174630bf' -and $info.notes -match 'example\.invalid/src\.tar\.gz' -and
        $info.notes -match [regex]::Escape('openslide-bin-4.0.1.2+20261102.nightly.tar.gz') -and $info.notes -match ('ab' * 32) }

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
    Get-ReleaseVersionInfo -Manifest $manifestObject -PublishedVersions $PublishedVersions -ExistingTags $ExistingTags | ConvertTo-Json -Depth 3
} catch {
    [Console]::Error.WriteLine("Get-ReleaseVersion: $($_.Exception.Message)")
    exit 1
}
