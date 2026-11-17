#requires -Version 7
<#
Packs OpenSlideSharp and OpenSlideSharp.runtime into artifacts/smoke-feed and
proves the nupkgs work for real consumers (net10.0, net48, and no-runtime).
#>
[CmdletBinding()]
param(
    [string]$Version = "0.0.0-smoke.$([DateTime]::UtcNow.ToString('yyyyMMddHHmmss'))"
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root 'artifacts'
$feed = Join-Path $artifacts 'smoke-feed'
$smokeProj = Join-Path $root 'test/PackageSmoke'
$fixture = Join-Path $root 'test/OpenSlideSharp.Tests/Fixtures/small.svs'

function Invoke-Native {
    param([string]$Exe, [string[]]$Arguments)
    & $Exe @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Exe $($Arguments -join ' ') failed with exit code $LASTEXITCODE" }
}

# Start clean: no stale feeds, caches or outputs.
Get-ChildItem $artifacts -Filter 'smoke*' -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force
New-Item -ItemType Directory -Force $feed | Out-Null

# 1. Pack both packages.
foreach ($proj in 'src/OpenSlideSharp/OpenSlideSharp.csproj', 'src/OpenSlideSharp.runtime/OpenSlideSharp.runtime.csproj') {
    Invoke-Native dotnet @('pack', (Join-Path $root $proj), '-c', 'Release', '-o', $feed, "-p:Version=$Version")
}

# 2. Assert nupkg contents.
function Get-NupkgEntries([string]$Path) {
    $zip = [IO.Compression.ZipFile]::OpenRead($Path)
    try { return @($zip.Entries | ForEach-Object { [Uri]::UnescapeDataString($_.FullName) }) }
    finally { $zip.Dispose() }
}

$runtimeNupkg = Join-Path $feed "OpenSlideSharp.runtime.$Version.nupkg"
$entries = Get-NupkgEntries $runtimeNupkg
$entries | ForEach-Object { "  runtime: $_" }
$required = @(
    'runtimes/win-x64/native/libopenslide-1.dll',
    'runtimes/linux-x64/native/libopenslide-1.so',
    'runtimes/linux-arm64/native/libopenslide-1.so',
    'runtimes/osx-arm64/native/libopenslide-1.dylib',
    'runtimes/osx-x64/native/libopenslide-1.dylib',
    'build/net462/OpenSlideSharp.runtime.targets',
    'buildTransitive/net462/OpenSlideSharp.runtime.targets',
    'VERSIONS.md', 'versions.json', 'README.md', 'openslide_logo.png'
)
foreach ($r in $required) {
    if ($entries -notcontains $r) { throw "runtime nupkg is missing '$r'" }
}
if (-not ($entries | Where-Object { $_ -like 'licenses/*' })) { throw "runtime nupkg has no licenses/**" }
if ($entries | Where-Object { $_ -match '\.debug$' }) { throw "runtime nupkg contains .debug files" }

# 3. Consume the packages from an isolated global cache.
$env:NUGET_PACKAGES = Join-Path $artifacts 'smoke-packages'

function Invoke-Smoke {
    param([string]$Name, [string]$Tfm, [string[]]$Extra, [int]$ExpectedExit, [string]$ExpectedText)
    Write-Host "== smoke: $Name ($Tfm)"
    $out = Join-Path $artifacts "smoke/$Name"
    Invoke-Native dotnet (@('build', $smokeProj, '-c', 'Release', '-f', $Tfm, '--artifacts-path', $out, "-p:OpenSlideSharpVersion=$Version") + $Extra)
    $bin = Join-Path $out "bin/PackageSmoke/release_$Tfm"
    if ($Tfm -eq 'net48') { $output = & (Join-Path $bin 'PackageSmoke.exe') $fixture 2>&1 }
    else { $output = & dotnet (Join-Path $bin 'PackageSmoke.dll') $fixture 2>&1 }
    $code = $LASTEXITCODE
    $text = ($output | Out-String)
    Write-Host $text
    if ($code -ne $ExpectedExit) { throw "[$Name] expected exit code $ExpectedExit, got $code" }
    if ($text -notmatch [regex]::Escape($ExpectedText)) { throw "[$Name] output does not contain '$ExpectedText'" }
    Write-Host "PASS: $Name"
}

$tfms = @('net10.0')
if ($IsWindows) { $tfms += 'net48' }
foreach ($tfm in $tfms) {
    Invoke-Smoke -Name $tfm -Tfm $tfm -Extra @() -ExpectedExit 0 -ExpectedText 'vendor=aperio'
}

# 4. Without the runtime package the core must explain what is missing.
Invoke-Smoke -Name 'no-runtime' -Tfm 'net10.0' -Extra @('-p:WithoutRuntime=true') -ExpectedExit 3 -ExpectedText 'OpenSlideSharp.runtime'

Write-Host 'All package smoke tests passed.'
