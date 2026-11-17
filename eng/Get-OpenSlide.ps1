#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Downloads, verifies and extracts the OpenSlide binaries pinned in eng/openslide.json.
.DESCRIPTION
  Archives are cached in artifacts/downloads. Output layout (default artifacts/native):
    win-x64/libopenslide-1.dll, linux-x64|linux-arm64/libopenslide-1.so,
    osx-arm64|osx-x64/libopenslide-1.dylib, msvc/x64/libopenslide.lib,
    include/, licenses/, VERSIONS.md, versions.json, CHANGELOG.md, .stamp
.PARAMETER Platform
  current (default), all, or one or more manifest asset keys
  (windows-x64, linux-x86_64, linux-aarch64, macos-arm64-x86_64).
#>
[CmdletBinding()]
param(
    [string[]]$Platform = @('current'),
    [string]$Manifest,
    [string]$Destination,
    [switch]$Force
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $Manifest) { $Manifest = Join-Path $repoRoot 'eng/openslide.json' }
if (-not $Destination) { $Destination = Join-Path $repoRoot 'artifacts/native' }
$downloads = Join-Path $repoRoot 'artifacts/downloads'

$m = Get-Content $Manifest -Raw | ConvertFrom-Json
$allKeys = @($m.assets.PSObject.Properties.Name)

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

# --- stamp -----------------------------------------------------------------
New-Item -ItemType Directory -Force $Destination | Out-Null
$stampPath = Join-Path $Destination '.stamp'
$stamp = [ordered]@{}
if (Test-Path $stampPath) {
    foreach ($line in Get-Content $stampPath) {
        $parts = $line -split '\s+', 2
        if ($parts.Count -eq 2) { $stamp[$parts[0]] = $parts[1] }
    }
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
        try {
            Invoke-WebRequest -Uri $asset.url -OutFile $path
        } catch {
            Remove-Item $path -Force -ErrorAction SilentlyContinue
            $status = $_.Exception.Response.StatusCode.value__
            if ($status -eq 404) {
                throw "$name is no longer available upstream (HTTP 404: $($asset.url)). Nightly builds expire; run eng/Update-OpenSlideManifest.ps1 to refresh $Manifest."
            }
            throw
        }
        $actual = Get-Sha256 $path
        if ($actual -ne $expected) {
            Remove-Item $path -Force
            throw "SHA256 mismatch for ${name}: expected $expected, got $actual. The downloaded file was deleted."
        }
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

# --- process ---------------------------------------------------------------
foreach ($key in $keys) {
    $sha = $m.assets.$key.sha256.ToLowerInvariant()
    if (-not $Force -and $stamp[$key] -eq $sha) {
        Write-Host "$key is up to date."
        continue
    }
    $archive = Get-Archive $key

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

        # Shared, platform-independent files (identical across archives; last one wins).
        foreach ($dir in 'include', 'licenses') {
            $src = Join-Path $root $dir
            if (Test-Path $src) {
                New-Item -ItemType Directory -Force (Join-Path $Destination $dir) | Out-Null
                Copy-Item (Join-Path $src '*') (Join-Path $Destination $dir) -Recurse -Force
            }
        }
        foreach ($file in 'VERSIONS.md', 'versions.json', 'CHANGELOG.md') {
            $src = Join-Path $root $file
            if (Test-Path $src) { Copy-Item $src (Join-Path $Destination $file) -Force }
        }
    } finally {
        Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
    }

    $stamp[$key] = $sha
    $stamp.GetEnumerator() | ForEach-Object { "$($_.Key) $($_.Value)" } | Set-Content $stampPath -Encoding utf8NoBOM
    Write-Host "Extracted $key"
}
