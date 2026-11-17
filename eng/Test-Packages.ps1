#requires -Version 7
<#
Packs OpenSlideSharp and OpenSlideSharp.runtime into artifacts/smoke-feed and
proves the nupkgs work for real consumers (net10.0, net48 x64 and AnyCPU,
no-runtime, and a Visual Studio C++ project), plus the 32-bit build warning.

-SkipPack reuses the nupkgs already in artifacts/smoke-feed (CI downloads them
there) instead of packing; everything else, including the nupkg content
assertions, still runs. net48 and the C++ stage only run on Windows.
#>
[CmdletBinding()]
param(
    [string]$Version = "0.0.0-smoke.$([DateTime]::UtcNow.ToString('yyyyMMddHHmmss'))",
    [switch]$SkipPack
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

# Start clean: no stale feeds, caches or outputs. With -SkipPack the feed is the input, so keep it.
Get-ChildItem $artifacts -Filter 'smoke*' -ErrorAction SilentlyContinue |
    Where-Object { -not ($SkipPack -and $_.Name -eq 'smoke-feed') } |
    Remove-Item -Recurse -Force
New-Item -ItemType Directory -Force $feed | Out-Null

# 1. Pack both packages (or, with -SkipPack, require them to be in the feed already).
if ($SkipPack) {
    foreach ($id in 'OpenSlideSharp', 'OpenSlideSharp.runtime') {
        $nupkg = Join-Path $feed "$id.$Version.nupkg"
        if (-not (Test-Path $nupkg)) { throw "-SkipPack: '$nupkg' not found; put the nupkgs for version $Version into $feed first" }
    }
}
else {
    foreach ($proj in 'src/OpenSlideSharp/OpenSlideSharp.csproj', 'src/OpenSlideSharp.runtime/OpenSlideSharp.runtime.csproj') {
        Invoke-Native dotnet @('pack', (Join-Path $root $proj), '-c', 'Release', '-o', $feed, "-p:Version=$Version")
    }
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

# Builds the smoke project; returns the output folder and the captured build log.
function Build-Smoke {
    param([string]$Name, [string]$Tfm, [string[]]$Extra)
    $out = Join-Path $artifacts "smoke/$Name"
    $log = & dotnet (@('build', $smokeProj, '-c', 'Release', '-f', $Tfm, '--artifacts-path', $out, "-p:OpenSlideSharpVersion=$Version") + $Extra) 2>&1 | Out-String
    $code = $LASTEXITCODE
    Write-Host $log
    if ($code -ne 0) { throw "[$Name] dotnet build failed with exit code $code" }
    return [pscustomobject]@{ Bin = (Join-Path $out "bin/PackageSmoke/release_$Tfm"); Log = $log }
}

# Reads the target architecture a managed assembly was compiled for: x64, x86, anycpu or anycpu32bitpreferred.
function Get-AssemblyArch([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    try {
        $pe = [Reflection.PortableExecutable.PEReader]::new($stream)
        try {
            $machine = $pe.PEHeaders.CoffHeader.Machine
            $flags = $pe.PEHeaders.CorHeader.Flags
            if ($machine -eq [Reflection.PortableExecutable.Machine]::Amd64) { return 'x64' }
            if ($machine -ne [Reflection.PortableExecutable.Machine]::I386) { return "$machine" }
            if ($flags.HasFlag([Reflection.PortableExecutable.CorFlags]::Requires32Bit)) { return 'x86' }
            if ($flags.HasFlag([Reflection.PortableExecutable.CorFlags]::Prefers32Bit)) { return 'anycpu32bitpreferred' }
            return 'anycpu'
        }
        finally { $pe.Dispose() }
    }
    finally { $stream.Dispose() }
}

function Invoke-Smoke {
    param([string]$Name, [string]$Tfm, [string[]]$Extra, [string]$ExpectedArch, [int]$ExpectedExit, [string]$ExpectedText)
    Write-Host "== smoke: $Name ($Tfm)"
    $build = Build-Smoke -Name $Name -Tfm $Tfm -Extra $Extra
    if ($build.Log -match 'OPENSLIDE001') { throw "[$Name] unexpected 32-bit process warning OPENSLIDE001" }
    $bin = $build.Bin
    $assembly = Join-Path $bin $(if ($Tfm -eq 'net48') { 'PackageSmoke.exe' } else { 'PackageSmoke.dll' })
    $arch = Get-AssemblyArch $assembly
    Write-Host "assembly architecture: $arch"
    if ($arch -ne $ExpectedArch) { throw "[$Name] expected a(n) $ExpectedArch assembly, got $arch" }
    if ($Tfm -eq 'net48') {
        # Exactly one copy, beside the exe: NuGet's x64 RID copy and the package targets must never both apply.
        $dlls = @(Get-ChildItem $bin -Recurse -Filter 'libopenslide-1.dll')
        $dlls | ForEach-Object { Write-Host "native: $($_.FullName)" }
        if ($dlls.Count -ne 1 -or $dlls[0].DirectoryName -ne [IO.Path]::GetFullPath($bin)) {
            throw "[$Name] expected exactly one libopenslide-1.dll directly in $bin, found $($dlls.Count)"
        }
        $output = & $assembly $fixture 2>&1
    }
    else { $output = & dotnet $assembly $fixture 2>&1 }
    $code = $LASTEXITCODE
    $text = ($output | Out-String)
    Write-Host $text
    if ($code -ne $ExpectedExit) { throw "[$Name] expected exit code $ExpectedExit, got $code" }
    if ($text -notmatch [regex]::Escape($ExpectedText)) { throw "[$Name] output does not contain '$ExpectedText'" }
    Write-Host "PASS: $Name"
}

# The net10.0 assembly must stay AnyCPU so it also runs on arm64 hosts.
Invoke-Smoke -Name 'net10.0' -Tfm 'net10.0' -Extra @() -ExpectedArch 'anycpu' -ExpectedExit 0 -ExpectedText 'vendor=aperio'
if ($IsWindows) {
    Invoke-Smoke -Name 'net48' -Tfm 'net48' -Extra @() -ExpectedArch 'x64' -ExpectedExit 0 -ExpectedText 'vendor=aperio'
    # A default .NET Framework console app: AnyCPU, for which the SDK infers the win-x86 RuntimeIdentifier.
    Invoke-Smoke -Name 'net48-anycpu' -Tfm 'net48' -Extra @('-p:SmokeAnyCpu=true') -ExpectedArch 'anycpu' -ExpectedExit 0 -ExpectedText 'vendor=aperio'

    # A 32-bit .NET Framework exe cannot load the x64 library: the build must warn. Build only, never run it.
    Write-Host '== smoke: net48-x86 (build only, expects warning OPENSLIDE001)'
    $build = Build-Smoke -Name 'net48-x86' -Tfm 'net48' -Extra @('-p:PlatformTarget=x86')
    if ($build.Log -notmatch 'OPENSLIDE001: .*x64 only') { throw "[net48-x86] build output lacks warning OPENSLIDE001" }
    Write-Host 'PASS: net48-x86 warns'
}

# 4. Without the runtime package the core must explain what is missing.
Invoke-Smoke -Name 'no-runtime' -Tfm 'net10.0' -Extra @('-p:WithoutRuntime=true') -ExpectedArch 'anycpu' -ExpectedExit 3 -ExpectedText 'OpenSlideSharp.runtime'

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
    # The expected MSBuild error is not echoed on success: CI problem matchers would report it as a failure.
    if ($code32 -eq 0) { Write-Host $text32; throw '[native C++ Win32] build unexpectedly succeeded' }
    if ($text32 -notmatch 'Windows binaries for x64 only') { Write-Host $text32; throw "[native C++ Win32] output lacks 'Windows binaries for x64 only'" }
    Write-Host 'PASS: native C++ Win32 rejected'
}

Write-Host 'All package smoke tests passed.'
# Expected-failure builds above leave a non-zero $LASTEXITCODE, which a CI pwsh step would report as its result.
exit 0
