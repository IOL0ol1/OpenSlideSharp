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
    'build/native/OpenSlideSharp.runtime.targets',
    'build/native/include/openslide/openslide.h',
    'build/native/include/openslide/openslide-features.h',
    'build/native/lib/x64/libopenslide.lib',
    'lib/netstandard2.0/_._',
    'buildTransitive/net462/OpenSlideSharp.runtime.targets',
    'VERSIONS.md', 'versions.json', 'README.md', 'openslide_logo.png'
)
foreach ($r in $required) {
    if ($entries -notcontains $r) { throw "runtime nupkg is missing '$r'" }
}
if (-not ($entries | Where-Object { $_ -like 'licenses/*' })) { throw "runtime nupkg has no licenses/**" }
if ($entries | Where-Object { $_ -match '\.debug$' }) { throw "runtime nupkg contains .debug files" }
if ($entries | Where-Object { $_ -match '\.dSYM(/|$)' }) { throw "runtime nupkg contains .dSYM entries" }
if ($entries | Where-Object { $_ -match '(^|/)slidetool' }) { throw "runtime nupkg contains slidetool" }
if ($entries | Where-Object { $_ -like 'lib/*.dll' -or $_ -like 'lib/*/*.dll' }) { throw "runtime nupkg contains lib/**/*.dll" }

function Get-NuspecXml([string]$Path) {
    $zip = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $e = $zip.Entries | Where-Object { $_.FullName -like '*.nuspec' } | Select-Object -First 1
        $sr = New-Object IO.StreamReader($e.Open())
        try { return [xml]$sr.ReadToEnd() } finally { $sr.Dispose() }
    }
    finally { $zip.Dispose() }
}

$rtMeta = (Get-NuspecXml $runtimeNupkg).package.metadata
$rtDeps = @($rtMeta.dependencies.group | Where-Object { $_ -and $_.HasChildNodes })
if ($rtMeta.dependencies -and ($rtMeta.dependencies.dependency -or $rtDeps.Count -gt 0)) { throw "runtime nuspec declares dependencies" }
if ($rtMeta.license.InnerText -ne 'LGPL-2.1-only') { throw "runtime nuspec license is '$($rtMeta.license.InnerText)', expected LGPL-2.1-only" }
if (($rtMeta.tags -split '\s+') -notcontains 'native') { throw "runtime nuspec tags lack 'native': $($rtMeta.tags)" }

$coreMeta = (Get-NuspecXml (Join-Path $feed "OpenSlideSharp.$Version.nupkg")).package.metadata
if ($coreMeta.SelectNodes("//*[local-name()='dependency'][@id='OpenSlideSharp.runtime']").Count -gt 0) { throw "core nuspec depends on OpenSlideSharp.runtime" }

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

# 5. Native C++ consumer (Visual Studio vcxproj via packages.config), Windows only.
if ($IsWindows) {
    $cppProj = Join-Path $root 'test/NativeCppSmoke'
    $nuget = Join-Path $artifacts 'tools/nuget.exe'
    if (-not (Test-Path $nuget)) {
        New-Item -ItemType Directory -Force (Split-Path $nuget) | Out-Null
        Invoke-WebRequest 'https://dist.nuget.org/win-x86-commandline/latest/nuget.exe' -OutFile $nuget
    }

    Write-Host '== smoke: native C++ (x64)'
    $pkgConfig = Join-Path $cppProj 'packages.config'
    Set-Content $pkgConfig -Encoding utf8 -Value @"
<?xml version="1.0" encoding="utf-8"?>
<packages>
  <package id="OpenSlideSharp.runtime" version="$Version" targetFramework="native" />
</packages>
"@
    # Fresh restore: remove packages and previous build output.
    foreach ($d in 'packages', 'Release', 'x64', 'Win32') { Remove-Item (Join-Path $cppProj $d) -Recurse -Force -ErrorAction SilentlyContinue }
    Invoke-Native $nuget @('restore', $pkgConfig, '-PackagesDirectory', (Join-Path $cppProj 'packages'), '-Source', $feed, '-NonInteractive')

    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    $msbuild = & $vswhere -latest -requires Microsoft.Component.MSBuild -find 'MSBuild/**/Bin/MSBuild.exe' | Select-Object -First 1
    if (-not $msbuild) { throw 'MSBuild not found via vswhere' }
    $vcxproj = Join-Path $cppProj 'NativeCppSmoke.vcxproj'

    Invoke-Native $msbuild @($vcxproj, '/nologo', '/v:minimal', '/p:Configuration=Release', '/p:Platform=x64', "/p:OpenSlideRuntimeVersion=$Version")
    $exe = Join-Path $cppProj 'x64/Release/NativeCppSmoke.exe'
    if (-not (Test-Path (Join-Path (Split-Path $exe) 'libopenslide-1.dll'))) { throw 'libopenslide-1.dll was not copied beside the exe' }
    $output = & $exe $fixture 2>&1
    $code = $LASTEXITCODE
    $text = ($output | Out-String)
    Write-Host $text
    if ($code -ne 0) { throw "[native C++] expected exit code 0, got $code" }
    if ($text -notmatch 'version=4\.') { throw "[native C++] output lacks 'version=4.'" }
    if ($text -notmatch 'size=16x16') { throw "[native C++] output lacks 'size=16x16'" }
    Write-Host 'PASS: native C++ x64'

    # Win32 must be rejected with an explanation.
    $out32 = & $msbuild $vcxproj /nologo /v:minimal /p:Configuration=Release /p:Platform=Win32 "/p:OpenSlideRuntimeVersion=$Version" 2>&1
    $code32 = $LASTEXITCODE
    $text32 = ($out32 | Out-String)
    Write-Host $text32
    if ($code32 -eq 0) { throw '[native C++ Win32] build unexpectedly succeeded' }
    if ($text32 -notmatch 'Windows binaries for x64 only') { throw "[native C++ Win32] output lacks 'Windows binaries for x64 only'" }
    Write-Host 'PASS: native C++ Win32 rejected'
}

Write-Host 'All package smoke tests passed.'
