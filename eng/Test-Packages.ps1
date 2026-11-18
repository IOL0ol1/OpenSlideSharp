#requires -Version 7
<#
Packs OpenSlideSharp, OpenSlideSharp.SkiaSharp, OpenSlideSharp.runtime, OpenSlideSharp.AspNetCore and the
OpenSlideSharp.Tool .NET tool into artifacts/smoke-feed and proves the nupkgs work for real consumers (net10.0,
an ASP.NET Core app, net48 x64 and AnyCPU, no-runtime, the installed tool, the desktop viewer published
self-contained as one file for this machine's RID, and a Visual Studio C++ project), plus the 32-bit build warning.

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
$packages = Join-Path $artifacts 'smoke-packages'
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

# 1. Pack all packages (or, with -SkipPack, require them to be in the feed already).
if ($SkipPack) {
    foreach ($id in 'OpenSlideSharp', 'OpenSlideSharp.SkiaSharp', 'OpenSlideSharp.runtime', 'OpenSlideSharp.AspNetCore', 'OpenSlideSharp.Tool') {
        $nupkg = Join-Path $feed "$id.$Version.nupkg"
        if (-not (Test-Path $nupkg)) { throw "-SkipPack: '$nupkg' not found; put the nupkgs for version $Version into $feed first" }
    }
}
else {
    foreach ($proj in 'src/OpenSlideSharp/OpenSlideSharp.csproj', 'src/OpenSlideSharp.SkiaSharp/OpenSlideSharp.SkiaSharp.csproj', 'src/OpenSlideSharp.runtime/OpenSlideSharp.runtime.csproj', 'src/OpenSlideSharp.AspNetCore/OpenSlideSharp.AspNetCore.csproj') {
        Invoke-Native dotnet @('pack', (Join-Path $root $proj), '-c', 'Release', '-o', $feed, "-p:Version=$Version")
    }

    # The tool carries the native library of every RID through OpenSlideSharp.runtime, restored from the feed just
    # packed. It restores into the isolated cache, so smoke versions never reach the user's NuGet cache, and builds
    # into its own artifacts folder, so the regular build output keeps its own restore.
    $env:NUGET_PACKAGES = $packages
    Invoke-Native dotnet @('pack', (Join-Path $root 'src/OpenSlideSharp.Tool/OpenSlideSharp.Tool.csproj'), '-c', 'Release', '-o', $feed,
        "-p:Version=$Version", '-p:UseOpenSlideRuntimePackage=true', "-p:RestoreAdditionalProjectSources=$feed",
        '--artifacts-path', (Join-Path $artifacts 'smoke/pack-tool'))
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
# openslide-bin's layout, licenses/<project>/<file>, without the project directory doubled.
if ($entries -notcontains 'licenses/OpenSlide/COPYING.LESSER') { throw "runtime nupkg is missing 'licenses/OpenSlide/COPYING.LESSER'" }
if ($entries | Where-Object { $_ -match '^licenses/([^/]+)/\1/' }) { throw 'runtime nupkg has doubled directories under licenses/' }
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

$skiaMeta = (Get-NuspecXml (Join-Path $feed "OpenSlideSharp.SkiaSharp.$Version.nupkg")).package.metadata
if ($skiaMeta.SelectNodes("//*[local-name()='dependency'][@id='OpenSlideSharp.runtime']").Count -gt 0) { throw "Skia nuspec depends on OpenSlideSharp.runtime" }
if ($skiaMeta.license.InnerText -ne 'MIT') { throw "Skia nuspec license is '$($skiaMeta.license.InnerText)', expected MIT" }
$skiaGroups = @($skiaMeta.dependencies.group)
if ($skiaGroups.Count -lt 3) { throw "Skia nuspec has $($skiaGroups.Count) dependency groups, expected one per target framework (3)" }
foreach ($g in $skiaGroups) {
    $deps = @{}
    foreach ($d in @($g.dependency)) { $deps[$d.id] = $d.version }
    $expect = @{ 'OpenSlideSharp' = $Version; 'SkiaSharp' = '3.119.4'; 'SkiaSharp.NativeAssets.Linux.NoDependencies' = '3.119.4' }
    foreach ($id in $expect.Keys) {
        if (-not $deps.ContainsKey($id)) { throw "Skia nuspec group '$($g.targetFramework)' lacks a dependency on $id" }
        # NuGet writes a minimum version as '[x, )' or 'x' depending on the packer; accept both.
        if ($deps[$id] -notmatch ('^\[?' + [regex]::Escape($expect[$id]) + '(, \))?\]?$')) { throw "Skia nuspec group '$($g.targetFramework)': $id version is '$($deps[$id])', expected $($expect[$id])" }
    }
}

# OpenSlideSharp.AspNetCore: net10.0 only, on the core and Skia packages of the same version and the ASP.NET Core
# shared framework. The native library is the app's choice, so no runtime package.
$aspMeta = (Get-NuspecXml (Join-Path $feed "OpenSlideSharp.AspNetCore.$Version.nupkg")).package.metadata
$aspGroups = @($aspMeta.dependencies.group)
if ($aspGroups.Count -ne 1 -or $aspGroups[0].targetFramework -ne 'net10.0') {
    throw "AspNetCore nuspec dependency groups are '$(($aspGroups | ForEach-Object { $_.targetFramework }) -join ', ')', expected only net10.0"
}
$aspDeps = @{}
foreach ($d in @($aspGroups[0].dependency)) { $aspDeps[$d.id] = $d.version }
$aspIds = ($aspDeps.Keys | Sort-Object) -join ', '
if ($aspIds -ne 'OpenSlideSharp, OpenSlideSharp.SkiaSharp') { throw "AspNetCore nuspec depends on '$aspIds', expected exactly OpenSlideSharp and OpenSlideSharp.SkiaSharp" }
foreach ($id in $aspDeps.Keys) {
    if ($aspDeps[$id] -notmatch ('^\[?' + [regex]::Escape($Version) + '(, \))?\]?$')) { throw "AspNetCore nuspec: $id version is '$($aspDeps[$id])', expected $Version" }
}
if ($aspMeta.SelectNodes("//*[local-name()='frameworkReference'][@name='Microsoft.AspNetCore.App']").Count -ne 1) {
    throw 'AspNetCore nuspec lacks frameworkReference Microsoft.AspNetCore.App'
}

# OpenSlideSharp.Tool: a .NET tool with the OpenSlide and SkiaSharp native libraries of every supported platform.
$toolNupkg = Join-Path $feed "OpenSlideSharp.Tool.$Version.nupkg"
$toolTypes = @((Get-NuspecXml $toolNupkg).package.metadata.packageTypes.packageType | ForEach-Object { $_.name })
if ($toolTypes -notcontains 'DotnetTool') { throw "tool nuspec package types are '$($toolTypes -join ', ')', expected DotnetTool" }
$toolEntries = Get-NupkgEntries $toolNupkg
$toolNative = 'tools/net10.0/any/runtimes'
$toolEntries | Where-Object { $_ -like "$toolNative/*" } | ForEach-Object { "  tool: $_" }
$toolRequired = @(
    'tools/net10.0/any/DotnetToolSettings.xml',
    "$toolNative/win-x64/native/libopenslide-1.dll",
    "$toolNative/linux-x64/native/libopenslide-1.so",
    "$toolNative/linux-arm64/native/libopenslide-1.so",
    "$toolNative/osx-arm64/native/libopenslide-1.dylib",
    "$toolNative/osx-x64/native/libopenslide-1.dylib",
    "$toolNative/win-x64/native/libSkiaSharp.dll",
    "$toolNative/linux-x64/native/libSkiaSharp.so",
    "$toolNative/linux-arm64/native/libSkiaSharp.so",
    "$toolNative/osx/native/libSkiaSharp.dylib"
)
foreach ($r in $toolRequired) {
    if ($toolEntries -notcontains $r) { throw "tool nupkg is missing '$r'" }
}
if ($toolEntries | Where-Object { $_ -like "$toolNative/*.pdb" }) { throw 'tool nupkg contains native .pdb files' }

# Only the RIDs OpenSlide ships (plus SkiaSharp's universal "osx"): SkiaSharp's other RIDs could never run.
$toolRids = @($toolEntries | Where-Object { $_ -like "$toolNative/*" } | ForEach-Object { ($_.Substring($toolNative.Length + 1) -split '/')[0] } | Sort-Object -Unique)
$extraRids = @($toolRids | Where-Object { $_ -notin 'win-x64', 'linux-x64', 'linux-arm64', 'osx', 'osx-x64', 'osx-arm64' })
if ($extraRids.Count -gt 0) { throw "tool nupkg has native assets for unsupported RIDs: $($extraRids -join ', ')" }

# The tool redistributes LGPL-2.1 OpenSlide (with openslide-bin's statically linked libraries), SkiaSharp and Skia,
# and the OpenSeadragon viewer files: their licenses must travel with it, and the license expression must name them.
$toolEntries | Where-Object { $_ -like 'licenses/*' } | ForEach-Object { "  tool: $_" }
if ($toolEntries -notcontains 'licenses/openslide-bin/VERSIONS.md') { throw "tool nupkg is missing 'licenses/openslide-bin/VERSIONS.md'" }
if ($toolEntries -notcontains 'licenses/openslide-bin/OpenSlide/COPYING.LESSER') { throw "tool nupkg is missing OpenSlide's license, 'licenses/openslide-bin/OpenSlide/COPYING.LESSER'" }
if ($toolEntries | Where-Object { $_ -match '^licenses/openslide-bin/([^/]+)/\1/' }) { throw 'tool nupkg has doubled directories under licenses/openslide-bin/' }
$openslideBinLicenses = @($toolEntries | Where-Object { $_ -match '^licenses/openslide-bin/[^/]+/.' })
$runtimeLicenses = @($entries | Where-Object { $_ -match '^licenses/[^/]+/.' })
if ($openslideBinLicenses.Count -ne $runtimeLicenses.Count) {
    throw "tool nupkg has $($openslideBinLicenses.Count) files under licenses/openslide-bin/, the runtime nupkg $($runtimeLicenses.Count) under licenses/"
}
if (-not ($openslideBinLicenses | Where-Object { $_ -match '/(COPYING|LICENSE)[^/]*$' })) { throw 'tool nupkg has no COPYING or LICENSE file under licenses/openslide-bin/' }
foreach ($r in 'licenses/viewer/LICENSE.openseadragon', 'licenses/viewer/LICENSE.openseadragon-scalebar', 'licenses/skiasharp/LICENSE.txt', 'licenses/skiasharp/THIRD-PARTY-NOTICES.txt') {
    if ($toolEntries -notcontains $r) { throw "tool nupkg is missing '$r'" }
}
$toolMeta = (Get-NuspecXml $toolNupkg).package.metadata
$toolLicense = 'MIT AND LGPL-2.1-only AND BSD-3-Clause'
if ($toolMeta.license.type -ne 'expression' -or $toolMeta.license.InnerText -ne $toolLicense) {
    throw "tool nuspec license is '$($toolMeta.license.InnerText)' ($($toolMeta.license.type)), expected the expression '$toolLicense'"
}
if ($toolMeta.description -notmatch 'openslide-bin source tarball') { throw "tool nuspec description does not name the OpenSlide source: $($toolMeta.description)" }
$toolSize = (Get-Item $toolNupkg).Length
Write-Host ("tool nupkg size: {0:N1} MB ({1} bytes)" -f ($toolSize / 1MB), $toolSize)

# 3. Consume the packages from an isolated global cache.
$env:NUGET_PACKAGES = $packages

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
    if ($ExpectedExit -eq 0 -and $text -notmatch 'png=ok') { throw "[$Name] output does not contain 'png=ok'" }
    Write-Host "PASS: $Name"
}

# The net10.0 assembly must stay AnyCPU so it also runs on arm64 hosts.
Invoke-Smoke -Name 'net10.0' -Tfm 'net10.0' -Extra @() -ExpectedArch 'anycpu' -ExpectedExit 0 -ExpectedText 'vendor=aperio'
# An ASP.NET Core app that serves the slide through OpenSlideSharp.AspNetCore and fetches a DZI, a tile and zarr.json.
Invoke-Smoke -Name 'aspnetcore' -Tfm 'net10.0' -Extra @('-p:SmokeAspNetCore=true') -ExpectedArch 'anycpu' -ExpectedExit 0 -ExpectedText 'aspnetcore=ok'
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

# 5. The tool, installed from the feed the way users install it, exports Deep Zoom tiles.
Write-Host '== smoke: openslide-sharp tool'
# Keep the tool path short: Windows does not load a native library whose path is longer than MAX_PATH (260).
$toolTemp = Join-Path ($env:RUNNER_TEMP ? $env:RUNNER_TEMP : [IO.Path]::GetTempPath()) ('oss-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
try {
    $toolPath = Join-Path $toolTemp 'bin'
    Invoke-Native dotnet @('tool', 'install', 'OpenSlideSharp.Tool', '--tool-path', $toolPath, '--add-source', $feed, '--version', $Version)
    $toolExe = Join-Path $toolPath $(if ($IsWindows) { 'openslide-sharp.exe' } else { 'openslide-sharp' })
    $toolOut = Join-Path $toolTemp 'small'
    Invoke-Native $toolExe @('deepzoom', $fixture, '-o', $toolOut)
    if (-not (Test-Path -PathType Leaf "$toolOut.dzi")) { throw "[tool] $toolOut.dzi was not written" }
    $tiles = @(Get-ChildItem "${toolOut}_files" -Recurse -File -Filter '*.jpeg' -ErrorAction SilentlyContinue)
    if ($tiles.Count -eq 0) { throw "[tool] no .jpeg tiles under ${toolOut}_files" }
    Write-Host "PASS: tool ($($tiles.Count) tiles)"
}
finally {
    Remove-Item $toolTemp -Recurse -Force -ErrorAction SilentlyContinue
}

# 6. The desktop viewer, published the way its releases are: self-contained, one file, for this machine's RID, with
# the native OpenSlide library from the runtime package in the feed. --check reads the slide without a window.
Write-Host '== smoke: viewer (self-contained single file)'
$hostArch = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture
# Picked the way eng/Get-OpenSlide.ps1 picks this machine's binaries.
$viewerRid = if ($IsWindows -and $hostArch -eq 'X64') { 'win-x64' }
elseif ($IsLinux -and $hostArch -eq 'X64') { 'linux-x64' }
elseif ($IsLinux -and $hostArch -eq 'Arm64') { 'linux-arm64' }
elseif ($IsMacOS -and $hostArch -eq 'Arm64') { 'osx-arm64' }
elseif ($IsMacOS -and $hostArch -eq 'X64') { 'osx-x64' }
else { throw "[viewer] OpenSlide publishes no binaries for this platform ($hostArch)" }
$viewerOut = Join-Path $artifacts 'smoke/viewer'
Invoke-Native dotnet @('publish', (Join-Path $root 'samples/OpenSlideSharp.Viewer'), '-c', 'Release', '-r', $viewerRid, '--self-contained',
    '-p:PublishSingleFile=true', '-p:UseOpenSlideRuntimePackage=true', "-p:OpenSlideSharpVersion=$Version",
    "-p:RestoreAdditionalProjectSources=$feed", '--artifacts-path', $viewerOut)
$viewerPublish = Join-Path $viewerOut "publish/OpenSlideSharp.Viewer/release_$viewerRid"
$viewerName = $IsWindows ? 'OpenSlideSharp.Viewer.exe' : 'OpenSlideSharp.Viewer'
# The publish folder is exactly the file a release attaches: the native libraries are inside it.
$published = @(Get-ChildItem $viewerPublish -Recurse -File)
$published | ForEach-Object { Write-Host ("published: {0} ({1:N1} MB)" -f $_.Name, ($_.Length / 1MB)) }
if ($published.Count -ne 1 -or $published[0].Name -ne $viewerName) {
    throw "[viewer] expected only $viewerName in $viewerPublish, found: $(($published | ForEach-Object { $_.Name }) -join ', ')"
}
# The bundle extracts its native libraries when it starts; a fresh extraction directory shows they came from it.
$viewerExtract = Join-Path $viewerOut 'extract'
$env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = $viewerExtract
try {
    # Piped, so that PowerShell waits for the viewer, a GUI program on Windows, and captures what it prints.
    $output = & (Join-Path $viewerPublish $viewerName) --check $fixture 2>&1 | Out-String
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
$extracted = @(Get-ChildItem $viewerExtract -Recurse -File | ForEach-Object { $_.Name })
$extracted | ForEach-Object { Write-Host "extracted: $_" }
foreach ($lib in 'libopenslide-1', 'libSkiaSharp', 'libHarfBuzzSharp') {
    if (-not ($extracted | Where-Object { [IO.Path]::GetFileNameWithoutExtension($_) -eq $lib })) { throw "[viewer] $lib is not in the bundle" }
}
Write-Host "PASS: viewer ($viewerRid)"

# 7. Native C++ consumer (Visual Studio vcxproj via packages.config), Windows only.
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
