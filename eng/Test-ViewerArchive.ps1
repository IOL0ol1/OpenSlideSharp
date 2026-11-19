#!/usr/bin/env pwsh
#requires -Version 7
<#
.SYNOPSIS
  Runs a release archive of the desktop viewer on its own platform, as a user would after downloading it.
.DESCRIPTION
  The release publishes the viewer for every RID on Linux, and eng/Publish-Viewer.ps1 can run only the host's RID. This
  extracts OpenSlideSharp.Viewer-<Version>-<Rid>.zip (Windows) or .tar.gz into a new folder, checks that it holds the
  executable (with mode 755 outside Windows) and licenses/ only, and runs the executable with --check on
  test/OpenSlideSharp.Tests/Fixtures/small.svs: it must start, load the native libraries from its bundle and read the
  slide. On Windows it also checks what the SDK copies into the executable from the viewer's assembly: the version
  resource (ProductVersion = -InformationalVersion), the application manifest from app.manifest and the GUI subsystem.
  Must run on a machine of -Rid.
.PARAMETER Path
  The folder with the archive.
.PARAMETER Rid
  win-x64, linux-x64, linux-arm64, osx-arm64 or osx-x64: this machine's.
.PARAMETER Version
  The release version in the archive name.
.PARAMETER InformationalVersion
  The executable's expected ProductVersion on Windows. Default: <Version>, optionally followed by +<metadata>.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Path,
    [Parameter(Mandatory)][string]$Rid,
    [Parameter(Mandatory)][string]$Version,
    [string]$InformationalVersion
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

$root = Split-Path -Parent $PSScriptRoot
$fixture = Join-Path $root 'test/OpenSlideSharp.Tests/Fixtures/small.svs'

$hostArch = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture
$hostRid = if ($IsWindows -and $hostArch -eq 'X64') { 'win-x64' }
elseif ($IsLinux -and $hostArch -eq 'X64') { 'linux-x64' }
elseif ($IsLinux -and $hostArch -eq 'Arm64') { 'linux-arm64' }
elseif ($IsMacOS -and $hostArch -eq 'Arm64') { 'osx-arm64' }
elseif ($IsMacOS -and $hostArch -eq 'X64') { 'osx-x64' }
if ($Rid -ne $hostRid) { throw "The $Rid archive must be run on a $Rid machine; this one is $($hostRid ?? $hostArch)." }

# The subsystem and the RT_MANIFEST (type 24) texts of a PE file's Win32 resources.
function Get-PeResourceInfo([string]$File) {
    $stream = [IO.File]::OpenRead($File)
    try {
        $pe = [Reflection.PortableExecutable.PEReader]::new($stream)
        $header = $pe.PEHeaders.PEHeader
        $manifests = [Collections.Generic.List[string]]::new()
        $directory = $header.ResourceTableDirectory
        if ($directory.Size -gt 0) {
            $base = $directory.RelativeVirtualAddress
            $rsrc = [Linq.Enumerable]::ToArray($pe.GetSectionData($base).GetContent())
            # The entries of the resource directory at $Offset: an id (or $null for a name), and the offset of the
            # subdirectory or data entry it points to.
            function Get-Entries([int]$Offset) {
                $count = [BitConverter]::ToUInt16($rsrc, $Offset + 12) + [BitConverter]::ToUInt16($rsrc, $Offset + 14)
                for ($i = 0; $i -lt $count; $i++) {
                    $name = [BitConverter]::ToUInt32($rsrc, $Offset + 16 + 8 * $i)
                    $target = [BitConverter]::ToUInt32($rsrc, $Offset + 20 + 8 * $i)
                    [pscustomobject]@{
                        Id = ($name -band 0x80000000) -eq 0 ? [int]$name : $null
                        IsDirectory = ($target -band 0x80000000) -ne 0
                        Offset = [int]($target -band 0x7FFFFFFF)
                    }
                }
            }
            function Get-Leaves([int]$Offset) {
                foreach ($e in Get-Entries $Offset) { if ($e.IsDirectory) { Get-Leaves $e.Offset } else { $e.Offset } }
            }
            foreach ($type in Get-Entries 0) {
                if ($type.Id -ne 24 -or -not $type.IsDirectory) { continue }
                foreach ($leaf in Get-Leaves $type.Offset) {
                    $start = [BitConverter]::ToInt32($rsrc, $leaf) - $base
                    $size = [BitConverter]::ToInt32($rsrc, $leaf + 4)
                    if ($start -lt 0 -or $start + $size -gt $rsrc.Length) { throw "A manifest resource of $File lies outside its resource section." }
                    $manifests.Add([Text.Encoding]::UTF8.GetString($rsrc, $start, $size))
                }
            }
        }
        [pscustomobject]@{ Subsystem = $header.Subsystem; Manifests = $manifests.ToArray() }
    }
    finally { $stream.Dispose() }
}

$isWindowsRid = $Rid.StartsWith('win-')
$archive = Join-Path $Path "OpenSlideSharp.Viewer-$Version-$Rid$($isWindowsRid ? '.zip' : '.tar.gz')"
if (-not (Test-Path -PathType Leaf $archive)) { throw "$archive not found." }
$work = Join-Path ([IO.Path]::GetTempPath()) "viewer-archive-$([Guid]::NewGuid().ToString('N'))"
$extracted = Join-Path $work 'viewer'
New-Item -ItemType Directory -Force $extracted | Out-Null
try {
    Write-Host "== viewer archive: $archive"
    if ($isWindowsRid) {
        [IO.Compression.ZipFile]::ExtractToDirectory($archive, $extracted)
    }
    else {
        tar -xzf $archive -C $extracted
        if ($LASTEXITCODE -ne 0) { throw "tar -xzf $archive failed with exit code $LASTEXITCODE." }
    }

    $exeName = $isWindowsRid ? 'OpenSlideSharp.Viewer.exe' : 'OpenSlideSharp.Viewer'
    $exe = Join-Path $extracted $exeName
    $top = (@(Get-ChildItem $extracted -Force | ForEach-Object Name) | Sort-Object) -join ','
    $expected = (@($exeName, 'licenses') | Sort-Object) -join ','
    if ($top -cne $expected) {
        throw "The archive's root holds $top, expected $exeName and licenses/."
    }
    if (-not $isWindowsRid -and ((Get-Item $exe).UnixFileMode -band [IO.UnixFileMode]::UserExecute) -eq 0) {
        throw "$exeName is not executable after extraction (mode $((Get-Item $exe).UnixFileMode))."
    }

    if ($isWindowsRid) {
        # Copied from the managed assembly into the apphost; an SDK that cannot do that on the publishing host leaves
        # the apphost's own resources (NETSDK1074).
        $info = (Get-Item $exe).VersionInfo
        $productVersion = "$($info.ProductVersion)"
        Write-Host "ProductVersion: $productVersion, FileVersion: $($info.FileVersion)"
        $versionOk = $InformationalVersion ? $productVersion -ceq $InformationalVersion : ($productVersion -ceq $Version -or $productVersion.StartsWith("$Version+", [StringComparison]::Ordinal))
        if (-not $versionOk) {
            throw "$exeName has ProductVersion '$productVersion', expected '$($InformationalVersion ? $InformationalVersion : $Version)'."
        }
        $pe = Get-PeResourceInfo $exe
        Write-Host "Subsystem: $($pe.Subsystem), manifests: $($pe.Manifests.Count)"
        if ($pe.Subsystem -ne [Reflection.PortableExecutable.Subsystem]::WindowsGui) { throw "$exeName has the $($pe.Subsystem) subsystem, expected WindowsGui." }
        if (-not ($pe.Manifests | Where-Object { $_.Contains('OpenSlideSharp.Viewer.Desktop') })) {
            throw "$exeName does not have the application manifest of samples/OpenSlideSharp.Viewer/app.manifest."
        }
    }

    # The bundle extracts its native libraries when it starts; a fresh extraction directory shows they came from it.
    $bundleExtract = Join-Path $work 'extract'
    $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = $bundleExtract
    try {
        # Piped, so that PowerShell waits for the viewer, a GUI program on Windows, and captures what it prints.
        $output = & $exe --check $fixture 2>&1 | Out-String
        $code = $LASTEXITCODE
    }
    finally {
        Remove-Item Env:DOTNET_BUNDLE_EXTRACT_BASE_DIR -ErrorAction SilentlyContinue
    }
    Write-Host $output
    if ($code -ne 0) { throw "$exeName --check exited with code $code, expected 0." }
    foreach ($line in 'openslide=4.', 'slide=16x16') {
        if ($output -notmatch [regex]::Escape($line)) { throw "$exeName --check did not print '$line'." }
    }
    $libraries = @(Get-ChildItem $bundleExtract -Recurse -File | ForEach-Object Name)
    $libraries | ForEach-Object { Write-Host "extracted: $_" }
    foreach ($lib in 'libopenslide-1', 'libSkiaSharp', 'libHarfBuzzSharp') {
        if (-not ($libraries | Where-Object { [IO.Path]::GetFileNameWithoutExtension($_) -eq $lib })) { throw "$lib is not in the bundle." }
    }
    Write-Host "The $Rid archive runs."
}
finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
