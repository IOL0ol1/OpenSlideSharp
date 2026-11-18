#!/usr/bin/env pwsh
#requires -Version 7
<#
.SYNOPSIS
  Completes and checks the files of an OpenSlideSharp GitHub release, gathered in one folder.
.DESCRIPTION
  -Path holds the release's nupkgs and viewer archives. This script adds the openslide-bin source tarball of
  eng/openslide.json: source.url, saved as openslide-bin-<binVersion>.tar.gz (the name openslide-bin gives it) once
  its sha256 is source.sha256. Then -Path must hold exactly the release's files, or the script fails:
    OpenSlideSharp, OpenSlideSharp.SkiaSharp, OpenSlideSharp.runtime, OpenSlideSharp.AspNetCore and
    OpenSlideSharp.Tool .<Version>.nupkg;
    OpenSlideSharp.Viewer-<Version>-win-x64.zip, -linux-x64.tar.gz and -osx-arm64.tar.gz;
    openslide-bin-<binVersion>.tar.gz.
  Prints a Markdown table of the files with their sizes and sha256, and appends it to $GITHUB_STEP_SUMMARY when set.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Path,
    [Parameter(Mandatory)][string]$Version,
    [string]$Manifest
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$root = Split-Path -Parent $PSScriptRoot
if (-not $Manifest) { $Manifest = Join-Path $root 'eng/openslide.json' }
$m = Get-Content $Manifest -Raw | ConvertFrom-Json
$Path = (Resolve-Path $Path).Path
function Get-Sha256([string]$File) { (Get-FileHash $File -Algorithm SHA256).Hash.ToLowerInvariant() }

# The source of the bundled OpenSlide binaries, kept with the release: the openslide-bin URL of a nightly expires.
$sourceName = "openslide-bin-$($m.binVersion).tar.gz"
$sourceSha = "$($m.source.sha256)".ToLowerInvariant()
$source = Join-Path $Path $sourceName
$part = "$source.part"
Remove-Item $source, $part -Force -ErrorAction SilentlyContinue
try {
    Write-Host "Downloading $($m.source.url)"
    $response = Invoke-WebRequest -Uri $m.source.url -OutFile $part -SkipHttpErrorCheck -PassThru
    if ($response.StatusCode -ne 200) { throw "GET $($m.source.url) returned HTTP $($response.StatusCode)." }
    $actual = Get-Sha256 $part
    if ($actual -ne $sourceSha) { throw "$sourceName has sha256 $actual, but $Manifest pins $sourceSha." }
    Move-Item $part $source
}
finally {
    Remove-Item $part -Force -ErrorAction SilentlyContinue
}

$expected = @(
    foreach ($id in 'OpenSlideSharp', 'OpenSlideSharp.SkiaSharp', 'OpenSlideSharp.runtime', 'OpenSlideSharp.AspNetCore', 'OpenSlideSharp.Tool') { "$id.$Version.nupkg" }
    foreach ($archive in 'win-x64.zip', 'linux-x64.tar.gz', 'osx-arm64.tar.gz') { "OpenSlideSharp.Viewer-$Version-$archive" }
    $sourceName
)
$present = @(Get-ChildItem $Path -Force | ForEach-Object Name)
$problems = @($expected | Where-Object { $_ -cnotin $present } | ForEach-Object { "missing: $_" }) +
    @($present | Where-Object { $_ -cnotin $expected } | ForEach-Object { "unexpected: $_" })
if ($problems.Count -gt 0) { throw "$Path does not hold exactly the files of release ${Version}:`n  $($problems -join "`n  ")" }

$table = @('| File | Size | sha256 |', '|---|--:|---|') + @($expected | ForEach-Object {
        $file = Get-Item -LiteralPath (Join-Path $Path $_)
        '| `{0}` | {1:N1} MB | `{2}` |' -f $_, ($file.Length / 1MB), (Get-Sha256 $file.FullName)
    })
$table | ForEach-Object { Write-Host $_ }
if ($env:GITHUB_STEP_SUMMARY) { Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value $table -Encoding utf8 }
