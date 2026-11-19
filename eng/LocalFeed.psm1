#requires -Version 7
# Restores that consume OpenSlideSharp packages from a local feed (the tool pack, the viewer publish, the package smoke
# tests) must take them from that feed only. The OpenSlideSharp* ids are not reserved on nuget.org, and NuGet does not say
# which source wins when two have the same id and version, so a package there must never be picked instead.
Set-StrictMode -Version Latest

function New-LocalFeedConfig {
    <# Writes a NuGet.config with the local feed and nuget.org as the only sources, OpenSlideSharp* mapped to the feed
       only and everything else to nuget.org. Returns its full path, for -p:RestoreConfigFile. #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Feed, [Parameter(Mandatory)][string]$Path)
    $Feed = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Feed)
    $Path = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path)
    New-Item -ItemType Directory -Force (Split-Path -Parent $Path) | Out-Null
    $text = @"
<?xml version="1.0" encoding="utf-8"?>
<!-- Written by eng/LocalFeed.psm1: OpenSlideSharp packages come from the local feed only. -->
<configuration>
  <packageSources>
    <clear />
    <add key="local-feed" value="$([Security.SecurityElement]::Escape($Feed))" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <clear />
    <packageSource key="local-feed">
      <package pattern="OpenSlideSharp*" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
"@
    Set-Content -Path $Path -Value $text -Encoding utf8NoBOM
    $Path
}

function Get-NupkgSha512 {
    <# The base64 SHA-512 of a file, as NuGet records a package's hash in project.assets.json. #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path)
    $stream = [IO.File]::OpenRead($Path)
    try { [Convert]::ToBase64String([Security.Cryptography.SHA512]::HashData($stream)) } finally { $stream.Dispose() }
}

function Assert-RestoredFromFeed {
    <# Fails unless every OpenSlideSharp* package a restore resolved (project.assets.json) is byte for byte the nupkg of the
       same id and version in the feed, and the -Require ids are among them. #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$AssetsFile, [Parameter(Mandatory)][string]$Feed, [string[]]$Require = @())
    $assets = Get-Content -Raw $AssetsFile | ConvertFrom-Json -AsHashtable
    $checked = @()
    foreach ($key in @($assets['libraries'].Keys)) {
        $library = $assets['libraries'][$key]
        $id, $version = $key -split '/', 2
        if ($library['type'] -ne 'package' -or -not $id.StartsWith('OpenSlideSharp', [StringComparison]::OrdinalIgnoreCase)) { continue }
        $nupkg = @(Get-ChildItem -LiteralPath $Feed -File | Where-Object { $_.Name -ieq "$id.$version.nupkg" })
        if ($nupkg.Count -ne 1) { throw "$AssetsFile resolved $id $version, which is not in $Feed." }
        $want = Get-NupkgSha512 $nupkg[0].FullName
        if ($library['sha512'] -cne $want) {
            throw "$AssetsFile resolved $id $version with sha512 $($library['sha512']), but $($nupkg[0].FullName) has ${want}: the package did not come from the local feed."
        }
        $checked += $id
    }
    foreach ($id in $Require) {
        if ($checked -notcontains $id) { throw "$AssetsFile has no package ${id}: expected it from $Feed." }
    }
    Write-Host "Restored from $Feed`: $($checked -join ', ')"
}

Export-ModuleMember -Function New-LocalFeedConfig, Get-NupkgSha512, Assert-RestoredFromFeed
