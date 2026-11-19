#!/usr/bin/env pwsh
#requires -Version 7
<#
.SYNOPSIS
  Resolves the upstream OpenSlide binary release and writes eng/openslide.json.
.DESCRIPTION
  nightly: latest prerelease with assets in openslide/builds.
  stable : latest non-prerelease in openslide/openslide-bin.
  Downloads the four binary archives and the openslide-bin source tarball into artifacts/downloads and records their
  sha256. Every file must hash to the sha256 digest GitHub publishes for that release asset, or nothing is written.
  Honours $env:GITHUB_TOKEN for authenticated GitHub API calls.
#>
[CmdletBinding()]
param(
    [ValidateSet('nightly', 'stable')][string]$Channel = 'nightly',
    [string]$Tag,
    [string]$OutFile
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
Import-Module (Join-Path $PSScriptRoot 'NuGetVersion.psm1') -Force

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutFile) { $OutFile = Join-Path $repoRoot 'eng/openslide.json' }
$downloads = Join-Path $repoRoot 'artifacts/downloads'

$assetSuffixes = [ordered]@{
    'windows-x64'        = '-windows-x64.zip'
    'linux-x86_64'       = '-linux-x86_64.tar.xz'
    'linux-aarch64'      = '-linux-aarch64.tar.xz'
    'macos-arm64-x86_64' = '-macos-arm64-x86_64.tar.xz'
}

function Invoke-GitHubApi([string]$Path) {
    $headers = @{ Accept = 'application/vnd.github+json'; 'User-Agent' = 'OpenSlideSharp-manifest' }
    if ($env:GITHUB_TOKEN) { $headers.Authorization = "Bearer $($env:GITHUB_TOKEN)" }
    $url = "https://api.github.com/$Path"
    $r = Invoke-WebRequestWithRetry -Uri $url -Headers $headers
    if ($r.StatusCode -ne 200) { throw "GET $url returned HTTP $($r.StatusCode)." }
    $r.Content | ConvertFrom-Json
}

function Get-Sha256([string]$Path) { (Get-FileHash $Path -Algorithm SHA256).Hash.ToLowerInvariant() }

$repo = if ($Channel -eq 'nightly') { 'openslide/builds' } else { 'openslide/openslide-bin' }

$release = $null
if ($Tag) {
    $release = Invoke-GitHubApi "repos/$repo/releases/tags/$([uri]::EscapeDataString($Tag))"
} else {
    $wantPre = $Channel -eq 'nightly'
    # foreach (not a pipeline): Invoke-RestMethod output returned from a function is not unrolled into the pipeline.
    foreach ($r in (Invoke-GitHubApi "repos/$repo/releases?per_page=30")) {
        if ([bool]$r.prerelease -eq $wantPre -and -not $r.draft -and $r.assets.Count -gt 0) { $release = $r; break }
    }
}
if (-not $release) { throw "No suitable $Channel release found in $repo." }

$tagName = $release.tag_name
$binVersion = $tagName -replace '^v', ''
Write-Host "Release: $repo $tagName"

# Downloads a release asset into artifacts/downloads (reusing a cached copy only if it hashes right) and checks it
# against the sha256 digest the GitHub release API reports for it. Returns the manifest entry: url and sha256.
function Get-VerifiedAsset($Asset) {
    if ("$($Asset.digest)" -notmatch '^sha256:([0-9a-fA-F]{64})$') {
        throw "Release $tagName reports no sha256 digest for $($Asset.name) (digest: '$($Asset.digest)'), so it cannot be verified."
    }
    $expected = $Matches[1].ToLowerInvariant()
    $url = $Asset.browser_download_url
    if (-not "$url".StartsWith("https://github.com/$repo/releases/download/", [StringComparison]::Ordinal)) {
        throw "Asset $($Asset.name) has an unexpected download URL: $url"
    }
    $path = Join-Path $downloads $Asset.name
    if ((Test-Path $path) -and (Get-Sha256 $path) -ne $expected) {
        Write-Host "Cached $($Asset.name) does not match the release digest; downloading again."
        Remove-Item $path -Force
    }
    if (-not (Test-Path $path)) {
        Write-Host "Downloading $($Asset.name)"
        # Download to .part and rename only after the hash checks out, so a failed download is never cached.
        $part = "$path.part"
        Remove-Item $part -Force -ErrorAction SilentlyContinue
        try {
            $response = Invoke-WebRequestWithRetry -Uri $url -OutFile $part
            if ($response.StatusCode -ne 200) { throw "GET $url returned HTTP $($response.StatusCode)." }
        }
        catch { Remove-Item $part -Force -ErrorAction SilentlyContinue; throw }
        $actual = Get-Sha256 $part
        if ($actual -ne $expected) {
            Remove-Item $part -Force
            throw "SHA256 mismatch for $($Asset.name): the release digest is $expected, the download hashes to $actual. The downloaded file was deleted."
        }
        Move-Item $part $path
    }
    [ordered]@{ url = $url; sha256 = $expected }
}

New-Item -ItemType Directory -Force $downloads | Out-Null
$assets = [ordered]@{}
foreach ($key in $assetSuffixes.Keys) {
    $asset = $release.assets | Where-Object { $_.name -like "openslide-bin-*$($assetSuffixes[$key])" } | Select-Object -First 1
    if (-not $asset) { throw "Release $tagName has no asset matching *$($assetSuffixes[$key])." }
    $assets[$key] = Get-VerifiedAsset $asset
}

# The corresponding source of every binary archive: openslide-bin's source tarball of the same version.
$sourceName = "openslide-bin-$binVersion.tar.gz"
$sourceAsset = $release.assets | Where-Object { $_.name -eq $sourceName } | Select-Object -First 1
if (-not $sourceAsset) { throw "Release $tagName has no source tarball $sourceName." }
$source = Get-VerifiedAsset $sourceAsset

# Read versions.json straight out of the windows zip.
$zipName = ($release.assets | Where-Object { $_.name -like "openslide-bin-*$($assetSuffixes['windows-x64'])" } | Select-Object -First 1).name
$zip = Join-Path $downloads $zipName
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($zip)
try {
    $entry = $archive.Entries | Where-Object { $_.FullName -match '^[^/]+/versions\.json$' } | Select-Object -First 1
    if (-not $entry) { throw "versions.json not found in $zip" }
    $reader = [IO.StreamReader]::new($entry.Open())
    try { $versions = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
} finally {
    $archive.Dispose()
}
$openslideVersion = ($versions.versions | Where-Object { $_.id -eq 'openslide' } | Select-Object -First 1).version
if (-not $openslideVersion) { throw 'versions.json has no entry with id "openslide".' }

if ($Channel -eq 'nightly') {
    if ($openslideVersion -notmatch '-g([0-9a-f]+)$') { throw "Cannot extract commit from '$openslideVersion'." }
    $commit = $Matches[1].Substring(0, 8)
    if ($binVersion -notmatch '\+(\d{4})(\d{2})(\d{2})') { throw "Cannot extract date from '$binVersion'." }
    $date = "$($Matches[1])-$($Matches[2])-$($Matches[3])"
} else {
    # The tag is annotated: ref -> tag object -> commit.
    $ref = Invoke-GitHubApi "repos/openslide/openslide/git/ref/tags/v$openslideVersion"
    $obj = $ref.object
    while ($obj.type -eq 'tag') { $obj = (Invoke-GitHubApi "repos/openslide/openslide/git/tags/$($obj.sha)").object }
    if ($obj.type -ne 'commit') { throw "Tag v$openslideVersion resolves to '$($obj.type)', not a commit." }
    $commit = $obj.sha.Substring(0, 8)
    $date = ([datetime]$release.published_at).ToUniversalTime().ToString('yyyy-MM-dd')
}

$manifest = [ordered]@{
    channel          = $Channel
    binVersion       = $binVersion
    openslideVersion = $openslideVersion
    openslideCommit  = $commit
    date             = $date
    releaseUrl       = "https://github.com/$repo/releases/tag/$([uri]::EscapeDataString($tagName))"
    source           = $source
    assets           = $assets
}
New-Item -ItemType Directory -Force (Split-Path -Parent $OutFile) | Out-Null
($manifest | ConvertTo-Json -Depth 5) + "`n" | Set-Content -Path $OutFile -NoNewline -Encoding utf8NoBOM
Write-Host "Wrote $OutFile"
