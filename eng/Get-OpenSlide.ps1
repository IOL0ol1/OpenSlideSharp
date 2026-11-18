#!/usr/bin/env pwsh
#requires -Version 7
<#
.SYNOPSIS
  Downloads, verifies and extracts the OpenSlide binaries pinned in eng/openslide.json.
.DESCRIPTION
  Archives are cached in artifacts/downloads. Output layout (default artifacts/native):
    win-x64/libopenslide-1.dll, linux-x64|linux-arm64/libopenslide-1.so,
    osx-arm64|osx-x64/libopenslide-1.dylib, msvc/x64/libopenslide.lib, include/openslide/*.h,
    licenses/<archive key>/  the archive's licenses/ (one folder per component) and its VERSIONS.md,
    openslide-bin.json       channel, versions, date and source tarball (url, sha256) from the manifest,
    .stamp                   the sha256 of each extracted archive
  Each archive links its own set of components, so each keeps its own licenses/<archive key>/ folder: the output
  for one platform is the same whichever other platforms were extracted before.
.PARAMETER Platform
  current (default), all, or one or more manifest asset keys
  (windows-x64, linux-x86_64, linux-aarch64, macos-arm64-x86_64).
.PARAMETER Force
  Download (if not cached) and extract the chosen archives again even if .stamp says they are up to date.
.PARAMETER Verify
  Check that the output holds what the manifest pins for the chosen platforms, and fail if it does not: .stamp has
  each archive's manifest sha256, every expected file exists, licenses/ has only archive folders, and
  openslide-bin.json matches the manifest. Without -Force nothing is downloaded or extracted; with -Force the chosen
  archives are extracted again first. Packing runs this (-Platform all -Verify) first.
#>
[CmdletBinding()]
param(
    [string[]]$Platform = @('current'),
    [string]$Manifest,
    [string]$Destination,
    [switch]$Force,
    [switch]$Verify
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $Manifest) { $Manifest = Join-Path $repoRoot 'eng/openslide.json' }
if (-not $Destination) { $Destination = Join-Path $repoRoot 'artifacts/native' }
$downloads = Join-Path $repoRoot 'artifacts/downloads'

$m = Get-Content $Manifest -Raw | ConvertFrom-Json
$allKeys = @($m.assets.PSObject.Properties.Name)
if (-not $m.source -or -not $m.source.url -or -not $m.source.sha256) {
    throw "$Manifest has no source tarball (source.url and source.sha256). Run eng/Update-OpenSlideManifest.ps1 to regenerate it."
}

# --- choose archives -------------------------------------------------------
$keys = foreach ($p in $Platform) {
    switch ($p) {
        'all' { $allKeys }
        'current' {
            $arch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
            if ($IsWindows -and $arch -eq 'X64') { 'windows-x64' }
            elseif ($IsLinux -and $arch -eq 'X64') { 'linux-x86_64' }
            elseif ($IsLinux -and $arch -eq 'Arm64') { 'linux-aarch64' }
            elseif ($IsMacOS) { 'macos-arm64-x86_64' }
            else { throw "No OpenSlide binaries are published for this platform ($arch)." }
        }
        default {
            if ($p -notin $allKeys) { throw "Unknown platform '$p'. Use current, all, or one of: $($allKeys -join ', ')." }
            $p
        }
    }
}
$keys = @($keys | Select-Object -Unique)

# openslide-bin.json: which openslide-bin build the binaries are, and its source tarball.
$binInfo = [ordered]@{
    channel          = "$($m.channel)"
    binVersion       = "$($m.binVersion)"
    openslideVersion = "$($m.openslideVersion)"
    openslideCommit  = "$($m.openslideCommit)"
    date             = if ($m.date -is [datetime]) { $m.date.ToString('yyyy-MM-dd') } else { "$($m.date)" }
    source           = [ordered]@{ url = "$($m.source.url)"; sha256 = "$($m.source.sha256)".ToLowerInvariant() }
}
$binInfoJson = (($binInfo | ConvertTo-Json -Depth 5) -replace "`r`n", "`n") + "`n"
$binInfoPath = Join-Path $Destination 'openslide-bin.json'

# --- stamp -----------------------------------------------------------------
$stampPath = Join-Path $Destination '.stamp'
function Read-Stamp {
    $s = [ordered]@{}
    if (Test-Path $stampPath) {
        foreach ($line in Get-Content $stampPath) {
            $parts = $line -split '\s+', 2
            if ($parts.Count -eq 2) { $s[$parts[0]] = $parts[1] }
        }
    }
    $s
}
function Save-Stamp($Stamp) {
    [string[]]$lines = @($Stamp.GetEnumerator() | ForEach-Object { "$($_.Key) $($_.Value)" })
    Set-Content -Path $stampPath -Value $lines -Encoding utf8NoBOM
}

# --- helpers ---------------------------------------------------------------
function Get-Sha256([string]$Path) { (Get-FileHash $Path -Algorithm SHA256).Hash.ToLowerInvariant() }

function Get-Archive([string]$Key) {
    $asset = $m.assets.$Key
    $name = [uri]::UnescapeDataString(([uri]$asset.url).Segments[-1])
    $path = Join-Path $downloads $name
    $expected = $asset.sha256.ToLowerInvariant()
    New-Item -ItemType Directory -Force $downloads | Out-Null

    if ((Test-Path $path) -and (Get-Sha256 $path) -ne $expected) {
        Write-Host "Cached $name does not match the manifest; downloading again."
        Remove-Item $path -Force
    }
    if (-not (Test-Path $path)) {
        Write-Host "Downloading $name"
        # Download to .part and rename only after the hash checks out, so an interrupted download is never cached.
        $part = "$path.part"
        Remove-Item $part -Force -ErrorAction SilentlyContinue
        try {
            Invoke-WebRequest -Uri $asset.url -OutFile $part
        } catch {
            Remove-Item $part -Force -ErrorAction SilentlyContinue
            $status = $_.Exception.Response.StatusCode.value__
            if ($status -eq 404) {
                throw "$name is no longer available upstream (HTTP 404: $($asset.url)). Nightly builds expire; run eng/Update-OpenSlideManifest.ps1 to refresh $Manifest."
            }
            throw
        }
        $actual = Get-Sha256 $part
        if ($actual -ne $expected) {
            Remove-Item $part -Force
            throw "SHA256 mismatch for ${name}: expected $expected, got $actual. The downloaded file was deleted."
        }
        Move-Item $part $path
    }
    $path
}

function Expand-OpenSlideArchive([string]$Archive, [string]$To) {
    if ($Archive.EndsWith('.zip')) {
        Expand-Archive -Path $Archive -DestinationPath $To -Force
        return
    }
    # Prefer the OS bsdtar on Windows (Git for Windows' GNU tar mistakes "E:" for a remote host).
    $tar = 'tar'
    if ($IsWindows -and (Test-Path "$env:SystemRoot\System32\tar.exe")) { $tar = "$env:SystemRoot\System32\tar.exe" }
    # Symlinks (libopenslide.so, .so.1, .dylib) are excluded: they cannot be created on Windows and are not needed.
    Push-Location (Split-Path -Parent $Archive)
    try {
        & $tar -xf (Split-Path -Leaf $Archive) -C $To '--exclude=*.debug' '--exclude=*.dSYM' '--exclude=*slidetool*' '--exclude=*/libopenslide.so' '--exclude=*/libopenslide.so.1' '--exclude=*/libopenslide.dylib'
        if ($LASTEXITCODE -ne 0) { throw "tar failed (exit $LASTEXITCODE) extracting $Archive" }
    } finally { Pop-Location }
}

# Files that must exist for a key to count as extracted (guards against a stale .stamp or an older layout).
function Get-ExpectedOutputs([string]$Key) {
    switch ($Key) {
        'windows-x64' { 'win-x64/libopenslide-1.dll', 'msvc/x64/libopenslide.lib', 'include/openslide/openslide.h', 'include/openslide/openslide-features.h' }
        'linux-x86_64' { 'linux-x64/libopenslide-1.so' }
        'linux-aarch64' { 'linux-arm64/libopenslide-1.so' }
        'macos-arm64-x86_64' { 'osx-arm64/libopenslide-1.dylib', 'osx-x64/libopenslide-1.dylib' }
    }
    "licenses/$Key/VERSIONS.md"
    "licenses/$Key/OpenSlide/COPYING.LESSER"
}

function Copy-Native([string]$From, [string]$ToRelative) {
    $target = Join-Path $Destination $ToRelative
    New-Item -ItemType Directory -Force (Split-Path -Parent $target) | Out-Null
    Copy-Item $From $target -Force
}

function Find-Single([string]$Dir, [scriptblock]$Filter, [string]$What) {
    $hits = @(Get-ChildItem $Dir -File -ErrorAction SilentlyContinue | Where-Object { -not $_.LinkType } | Where-Object $Filter)
    if ($hits.Count -ne 1) { throw "Expected exactly one $What in $Dir, found $($hits.Count)." }
    $hits[0].FullName
}

function Test-Staged {
    $stamp = Read-Stamp
    $problems = [Collections.Generic.List[string]]::new()
    foreach ($key in $keys) {
        $sha = $m.assets.$key.sha256.ToLowerInvariant()
        if ($stamp[$key] -ne $sha) { $problems.Add("${key}: .stamp has '$($stamp[$key])', the manifest pins $sha") }
        foreach ($rel in Get-ExpectedOutputs $key) {
            if (-not (Test-Path -PathType Leaf (Join-Path $Destination $rel))) { $problems.Add("${key}: $rel is missing") }
        }
    }
    foreach ($entry in @(Get-ChildItem (Join-Path $Destination 'licenses') -Force -ErrorAction SilentlyContinue)) {
        if (-not $entry.PSIsContainer -or $entry.Name -notin $allKeys) { $problems.Add("licenses/$($entry.Name) is not the licenses folder of an archive in the manifest") }
    }
    if (-not (Test-Path -PathType Leaf $binInfoPath)) { $problems.Add('openslide-bin.json is missing') }
    elseif (((Get-Content $binInfoPath -Raw) -replace "`r`n", "`n") -cne $binInfoJson) { $problems.Add('openslide-bin.json does not match the manifest') }
    if ($problems.Count -gt 0) {
        throw "$Destination does not hold what $Manifest pins:`n  - $($problems -join "`n  - ")`nRun eng/Get-OpenSlide.ps1 -Platform $($Platform -join ',') -Force."
    }
    Write-Host "Verified $Destination against $Manifest ($($keys -join ', '))."
}

# --- verify only -------------------------------------------------------------
if ($Verify -and -not $Force) {
    Test-Staged
    return
}

# --- process ---------------------------------------------------------------
New-Item -ItemType Directory -Force $Destination | Out-Null
# Earlier versions put the licenses/, VERSIONS.md, versions.json and CHANGELOG.md of whichever archive came last at
# the root; remove them so that only the per-archive folders remain.
foreach ($file in 'VERSIONS.md', 'versions.json', 'CHANGELOG.md') { Remove-Item (Join-Path $Destination $file) -Force -ErrorAction SilentlyContinue }
Get-ChildItem (Join-Path $Destination 'licenses') -Force -ErrorAction SilentlyContinue |
    Where-Object { -not $_.PSIsContainer -or $_.Name -notin $allKeys } |
    Remove-Item -Recurse -Force

$stamp = Read-Stamp
foreach ($key in $keys) {
    $sha = $m.assets.$key.sha256.ToLowerInvariant()
    $missing = @(Get-ExpectedOutputs $key | Where-Object { -not (Test-Path (Join-Path $Destination $_)) })
    if (-not $Force -and $stamp[$key] -eq $sha -and $missing.Count -eq 0) {
        Write-Host "$key is up to date."
        continue
    }
    $archive = Get-Archive $key

    # Until this archive is fully extracted, .stamp must not claim it.
    $stamp.Remove($key)
    Save-Stamp $stamp

    $tmp = Join-Path ([IO.Path]::GetTempPath()) ('osl-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory $tmp | Out-Null
    try {
        Expand-OpenSlideArchive $archive $tmp
        $top = @(Get-ChildItem $tmp -Directory)
        $root = if ($top.Count -eq 1 -and -not (Get-ChildItem $tmp -File)) { $top[0].FullName } else { $tmp }
        $lib = Join-Path $root 'lib'

        switch ($key) {
            'windows-x64' {
                Copy-Native (Join-Path $root 'bin/libopenslide-1.dll') 'win-x64/libopenslide-1.dll'
                Copy-Native (Join-Path $lib 'libopenslide.lib') 'msvc/x64/libopenslide.lib'
            }
            { $_ -in 'linux-x86_64', 'linux-aarch64' } {
                $rid = if ($key -eq 'linux-x86_64') { 'linux-x64' } else { 'linux-arm64' }
                $so = Find-Single $lib { $_.Name -like 'libopenslide.so.1.*' -and $_.Name -notlike '*.debug' } 'libopenslide.so.1.* file'
                Copy-Native $so "$rid/libopenslide-1.so"
            }
            'macos-arm64-x86_64' {
                $dylib = Join-Path $lib 'libopenslide.1.dylib'
                Copy-Native $dylib 'osx-arm64/libopenslide-1.dylib'
                Copy-Native $dylib 'osx-x64/libopenslide-1.dylib'
            }
        }

        # The public headers are the same in every archive.
        $include = Join-Path $root 'include'
        if (Test-Path $include) {
            New-Item -ItemType Directory -Force (Join-Path $Destination 'include') | Out-Null
            Copy-Item (Join-Path $include '*') (Join-Path $Destination 'include') -Recurse -Force
        }

        # This archive's licenses and component versions, replacing any earlier copy.
        $licenses = Join-Path $root 'licenses'
        $versionsMd = Join-Path $root 'VERSIONS.md'
        if (-not (Test-Path -PathType Container $licenses)) { throw "$archive has no licenses/ folder." }
        if (-not (Test-Path -PathType Leaf $versionsMd)) { throw "$archive has no VERSIONS.md." }
        $licenseDir = Join-Path $Destination "licenses/$key"
        if (Test-Path $licenseDir) { Remove-Item $licenseDir -Recurse -Force }
        New-Item -ItemType Directory -Force $licenseDir | Out-Null
        Copy-Item (Join-Path $licenses '*') $licenseDir -Recurse -Force
        Copy-Item $versionsMd $licenseDir -Force
    } finally {
        Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
    }

    $stamp[$key] = $sha
    Save-Stamp $stamp
    Write-Host "Extracted $key"
}

if (-not (Test-Path -PathType Leaf $binInfoPath) -or (Get-Content $binInfoPath -Raw) -cne $binInfoJson) {
    Set-Content -Path $binInfoPath -Value $binInfoJson -NoNewline -Encoding utf8NoBOM
    Write-Host "Wrote $binInfoPath"
}

if ($Verify) { Test-Staged }
