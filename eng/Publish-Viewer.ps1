#!/usr/bin/env pwsh
#requires -Version 7
<#
.SYNOPSIS
  Publishes the desktop viewer the way its releases ship it: self-contained, one executable and its licenses/, with
  the native OpenSlide library from the OpenSlideSharp.runtime package in a local feed.
.DESCRIPTION
  Then checks the publish folder: only the executable and licenses/, with exactly the notices of the openslide-bin
  archive the RID's library comes from (as the runtime package has them), without the runtime package's own
  LICENSE.txt (it describes the nupkg's layout), and a licenses/SOURCE.md that names the
  openslide-bin build of eng/openslide.json, its source tarball and the OpenSlideSharp release v<Version>.
  When -Rid is this machine's, also runs the executable with --check on test/OpenSlideSharp.Tests/Fixtures/small.svs
  (it reads the slide without a window) and checks that the native libraries came out of the bundle.
  Cross-publishing works from any host: for osx-* the SDK signs the bundle ad hoc itself.
.PARAMETER Rid
  win-x64, linux-x64, linux-arm64, osx-arm64 or osx-x64. Default: this machine's.
.PARAMETER Feed
  A folder with OpenSlideSharp.runtime.<Version>.nupkg. The restore takes OpenSlideSharp* packages from it only, never
  from nuget.org, and the publish fails unless the resolved runtime package is that nupkg.
.PARAMETER OpenSlideUpstreamVersion
  Passed to the build, which then sets InformationalVersion (see Directory.Build.props).
.PARAMETER ArtifactsPath
  The build's --artifacts-path. Default: artifacts/viewer/<Rid>.
.PARAMETER ArchiveDirectory
  Also archive the publish folder there, as the release download: OpenSlideSharp.Viewer-<Version>-<Rid>.zip for
  Windows, .tar.gz (the executable with mode 755) otherwise. The executable and licenses/ are at the archive's root.
#>
[CmdletBinding()]
param(
    [string]$Rid,
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$Feed,
    [string]$OpenSlideUpstreamVersion,
    [string]$ArtifactsPath,
    [string]$ArchiveDirectory
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
Import-Module (Join-Path $PSScriptRoot 'LocalFeed.psm1') -Force

$root = Split-Path -Parent $PSScriptRoot
$fixture = Join-Path $root 'test/OpenSlideSharp.Tests/Fixtures/small.svs'
$manifest = Get-Content (Join-Path $root 'eng/openslide.json') -Raw | ConvertFrom-Json

function Invoke-Native {
    param([string]$Exe, [string[]]$Arguments)
    & $Exe @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Exe $($Arguments -join ' ') failed with exit code $LASTEXITCODE" }
}

# A path relative to the current location, as PowerShell (not the process) sees it.
function Get-FullPath([string]$Path) { $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path) }

$hostArch = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture
# Picked the way eng/Get-OpenSlide.ps1 picks this machine's binaries.
$hostRid = if ($IsWindows -and $hostArch -eq 'X64') { 'win-x64' }
elseif ($IsLinux -and $hostArch -eq 'X64') { 'linux-x64' }
elseif ($IsLinux -and $hostArch -eq 'Arm64') { 'linux-arm64' }
elseif ($IsMacOS -and $hostArch -eq 'Arm64') { 'osx-arm64' }
elseif ($IsMacOS -and $hostArch -eq 'X64') { 'osx-x64' }
if (-not $Rid) {
    if (-not $hostRid) { throw "OpenSlide publishes no binaries for this platform ($hostArch); pass -Rid." }
    $Rid = $hostRid
}
# The openslide-bin archive the RID's library comes from.
$binKey = @{ 'win-x64' = 'windows-x64'; 'linux-x64' = 'linux-x86_64'; 'linux-arm64' = 'linux-aarch64'; 'osx-arm64' = 'macos-arm64-x86_64'; 'osx-x64' = 'macos-arm64-x86_64' }[$Rid]
if (-not $binKey) { throw "openslide-bin has no build for '$Rid'. Use win-x64, linux-x64, linux-arm64, osx-arm64 or osx-x64." }

$Feed = Get-FullPath $Feed
$runtimeNupkg = Join-Path $Feed "OpenSlideSharp.runtime.$Version.nupkg"
if (-not (Test-Path -PathType Leaf $runtimeNupkg)) { throw "$runtimeNupkg not found: the viewer takes the native OpenSlide library from it." }
$ArtifactsPath = Get-FullPath ($ArtifactsPath ? $ArtifactsPath : (Join-Path $root "artifacts/viewer/$Rid"))
$publish = Join-Path $ArtifactsPath "publish/OpenSlideSharp.Viewer/release_$Rid"
# The folder must hold this publish's files only.
Remove-Item $publish -Recurse -Force -ErrorAction SilentlyContinue
# OpenSlideSharp.runtime from the feed only, never from nuget.org (see eng/LocalFeed.psm1).
$nugetConfig = New-LocalFeedConfig -Feed $Feed -Path (Join-Path $ArtifactsPath 'NuGet.config')

Write-Host "== viewer: publish $Rid ($Version)"
$publishArgs = @('publish', (Join-Path $root 'samples/OpenSlideSharp.Viewer'), '-c', 'Release', '-r', $Rid, '--self-contained',
    '-p:PublishSingleFile=true', '-p:UseOpenSlideRuntimePackage=true', "-p:Version=$Version", "-p:OpenSlideSharpVersion=$Version",
    "-p:RestoreConfigFile=$nugetConfig", '--artifacts-path', $ArtifactsPath)
if ($OpenSlideUpstreamVersion) { $publishArgs += "-p:OpenSlideUpstreamVersion=$OpenSlideUpstreamVersion" }
Invoke-Native dotnet $publishArgs
Assert-RestoredFromFeed -AssetsFile (Join-Path $ArtifactsPath 'obj/OpenSlideSharp.Viewer/project.assets.json') -Feed $Feed -Require 'OpenSlideSharp.runtime'

# The publish folder is exactly what a release archives: the executable, with the native libraries inside it, and
# licenses/ with the notices of what it contains.
$exeName = $Rid.StartsWith('win-') ? 'OpenSlideSharp.Viewer.exe' : 'OpenSlideSharp.Viewer'
$published = @(Get-ChildItem $publish -Recurse -File | ForEach-Object {
        [pscustomobject]@{ Path = [IO.Path]::GetRelativePath($publish, $_.FullName) -replace '\\', '/'; Length = $_.Length }
    })
$published | ForEach-Object { Write-Host ("published: {0} ({1:N1} KB)" -f $_.Path, ($_.Length / 1KB)) }
$paths = @($published | ForEach-Object Path)
$unexpected = @($paths | Where-Object { $_ -ne $exeName -and -not $_.StartsWith('licenses/') })
if ($paths -notcontains $exeName -or $unexpected.Count -gt 0) {
    throw "[viewer] expected $exeName and licenses/ in $publish, found: $($paths -join ', ')"
}
# The license texts of OpenSlide and of the libraries linked into it, from the runtime package: exactly those of the
# openslide-bin archive this RID's library comes from, and no other platform's.
foreach ($required in 'licenses/THIRD-PARTY-NOTICES.txt', 'licenses/SOURCE.md', 'licenses/openslide-bin/openslide-bin.json',
    "licenses/openslide-bin/$binKey/VERSIONS.md", "licenses/openslide-bin/$binKey/OpenSlide/COPYING.LESSER") {
    if ($paths -notcontains $required) { throw "[viewer] $required is not in $publish" }
}
# The runtime package's own LICENSE.txt describes the nupkg's layout, which this folder does not have.
$runtimeLicense = @($paths | Where-Object { $_ -like 'licenses/OpenSlideSharp.runtime/*' })
if ($runtimeLicense.Count -gt 0) { throw "[viewer] $($runtimeLicense -join ', ') must not be in $publish" }
$zip = [IO.Compression.ZipFile]::OpenRead($runtimeNupkg)
try { $runtimeEntries = @($zip.Entries | ForEach-Object { [Uri]::UnescapeDataString($_.FullName) }) }
finally { $zip.Dispose() }
$wantBinLicenses = @($runtimeEntries | Where-Object { $_ -like "licenses/$binKey/*" } | ForEach-Object { $_.Substring('licenses/'.Length) } | Sort-Object)
$binLicenses = @($paths | Where-Object { $_ -like 'licenses/openslide-bin/*/*' } | ForEach-Object { $_.Substring('licenses/openslide-bin/'.Length) } | Sort-Object)
if (($binLicenses -join "`n") -cne ($wantBinLicenses -join "`n")) {
    throw "[viewer] licenses/openslide-bin/ has $($binLicenses.Count) files ($((@($binLicenses | ForEach-Object { ($_ -split '/')[0] } | Sort-Object -Unique)) -join ', ')), expected the runtime nupkg's $($wantBinLicenses.Count) under licenses/$binKey/ only"
}
Write-Host "licenses/openslide-bin/$binKey`: $($binLicenses.Count) license files"
# SOURCE.md is written at publish from openslide-bin.json: the exact build, its source tarball and the release copy.
$sourceMd = Get-Content (Join-Path $publish 'licenses/SOURCE.md') -Raw
foreach ($phrase in $manifest.binVersion, "openslide-bin-$($manifest.binVersion).tar.gz", $manifest.source.sha256, $manifest.source.url,
    $manifest.openslideVersion, "https://github.com/IOL0ol1/OpenSlideSharp/releases/tag/v$Version", "openslide-bin/$binKey/", 'OPENSLIDE_LIBRARY_PATH') {
    if (-not $sourceMd.Contains("$phrase")) { throw "[viewer] licenses/SOURCE.md does not contain '$phrase'" }
}

if ($Rid -eq $hostRid) {
    # The bundle extracts its native libraries when it starts; a fresh extraction directory shows they came from it.
    $extract = Join-Path $ArtifactsPath 'extract'
    Remove-Item $extract -Recurse -Force -ErrorAction SilentlyContinue
    $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = $extract
    try {
        # Piped, so that PowerShell waits for the viewer, a GUI program on Windows, and captures what it prints.
        $output = & (Join-Path $publish $exeName) --check $fixture 2>&1 | Out-String
        $code = $LASTEXITCODE
    }
    finally {
        Remove-Item Env:DOTNET_BUNDLE_EXTRACT_BASE_DIR -ErrorAction SilentlyContinue
    }
    Write-Host $output
    if ($code -ne 0) { throw "[viewer] --check exited with code $code, expected 0" }
    foreach ($line in 'openslide=4.', 'slide=16x16') {
        if ($output -notmatch [regex]::Escape($line)) { throw "[viewer] --check output does not contain '$line'" }
    }
    $extracted = @(Get-ChildItem $extract -Recurse -File | ForEach-Object { $_.Name })
    $extracted | ForEach-Object { Write-Host "extracted: $_" }
    foreach ($lib in 'libopenslide-1', 'libSkiaSharp', 'libHarfBuzzSharp') {
        if (-not ($extracted | Where-Object { [IO.Path]::GetFileNameWithoutExtension($_) -eq $lib })) { throw "[viewer] $lib is not in the bundle" }
    }
}
else {
    Write-Host "Not running --check: $Rid is not this machine's runtime identifier ($hostRid)."
}

if ($ArchiveDirectory) {
    $ArchiveDirectory = Get-FullPath $ArchiveDirectory
    New-Item -ItemType Directory -Force $ArchiveDirectory | Out-Null
    $files = @(Get-ChildItem $publish -Recurse -File | Sort-Object FullName)
    $dirs = @(Get-ChildItem $publish -Recurse -Directory | Sort-Object FullName)
    function Get-EntryName($Item) { [IO.Path]::GetRelativePath($publish, $Item.FullName) -replace '\\', '/' }
    if ($Rid.StartsWith('win-')) {
        $archive = Join-Path $ArchiveDirectory "OpenSlideSharp.Viewer-$Version-$Rid.zip"
        Remove-Item $archive -Force -ErrorAction SilentlyContinue
        $zip = [IO.Compression.ZipFile]::Open($archive, [IO.Compression.ZipArchiveMode]::Create)
        try {
            foreach ($f in $files) {
                $null = [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $f.FullName, (Get-EntryName $f), [IO.Compression.CompressionLevel]::Optimal)
            }
        }
        finally { $zip.Dispose() }
        $zip = [IO.Compression.ZipFile]::OpenRead($archive)
        try { $entries = @($zip.Entries | ForEach-Object FullName) } finally { $zip.Dispose() }
    }
    else {
        # Written entry by entry, so the modes are the same from any host: 755 for the executable and the folders,
        # 644 for the notices, owned by root.
        Add-Type -AssemblyName System.Formats.Tar
        $archive = Join-Path $ArchiveDirectory "OpenSlideSharp.Viewer-$Version-$Rid.tar.gz"
        $exec = [IO.UnixFileMode]0x1ED   # 0755
        $plain = [IO.UnixFileMode]0x1A4  # 0644
        $stream = [IO.File]::Create($archive)
        try {
            $gzip = [IO.Compression.GZipStream]::new($stream, [IO.Compression.CompressionLevel]::Optimal)
            try {
                $tar = [Formats.Tar.TarWriter]::new($gzip, [Formats.Tar.TarEntryFormat]::Pax, $false)
                try {
                    foreach ($d in $dirs) {
                        $entry = [Formats.Tar.PaxTarEntry]::new([Formats.Tar.TarEntryType]::Directory, "$(Get-EntryName $d)/")
                        $entry.Mode = $exec
                        $entry.ModificationTime = $d.LastWriteTimeUtc
                        $tar.WriteEntry($entry)
                    }
                    foreach ($f in $files) {
                        $name = Get-EntryName $f
                        $entry = [Formats.Tar.PaxTarEntry]::new([Formats.Tar.TarEntryType]::RegularFile, $name)
                        $entry.Mode = ($name -eq $exeName) ? $exec : $plain
                        $entry.ModificationTime = $f.LastWriteTimeUtc
                        $data = [IO.File]::OpenRead($f.FullName)
                        try { $entry.DataStream = $data; $tar.WriteEntry($entry) } finally { $data.Dispose() }
                    }
                }
                finally { $tar.Dispose() }
            }
            finally { $gzip.Dispose() }
        }
        finally { $stream.Dispose() }

        # Read it back: the same files, and the executable can be run.
        $entries = [Collections.Generic.List[string]]::new()
        $stream = [IO.File]::OpenRead($archive)
        try {
            $gzip = [IO.Compression.GZipStream]::new($stream, [IO.Compression.CompressionMode]::Decompress)
            $reader = [Formats.Tar.TarReader]::new($gzip)
            try {
                while ($null -ne ($entry = $reader.GetNextEntry())) {
                    if ($entry.EntryType -ne [Formats.Tar.TarEntryType]::RegularFile) { continue }
                    $entries.Add($entry.Name)
                    if ($entry.Name -eq $exeName -and -not $entry.Mode.HasFlag([IO.UnixFileMode]::UserExecute)) { throw "[viewer] $exeName has mode $($entry.Mode) in $archive" }
                }
            }
            finally { $reader.Dispose(); $gzip.Dispose() }
        }
        finally { $stream.Dispose() }
    }
    $want = @($files | ForEach-Object { Get-EntryName $_ } | Sort-Object)
    if ((@($entries | Sort-Object) -join "`n") -cne ($want -join "`n")) { throw "[viewer] $archive does not hold exactly the $($want.Count) files of $publish" }
    Write-Host ("archive: {0} ({1:N1} MB, {2} files)" -f $archive, ((Get-Item $archive).Length / 1MB), $entries.Count)
}

Write-Host "PASS: viewer ($Rid)"
