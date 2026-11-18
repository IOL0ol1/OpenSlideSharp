#requires -Version 7
# NuGet version parsing and ordering, shared by Get-ReleaseVersion.ps1 and Find-UpstreamUpdate.ps1.
Set-StrictMode -Version Latest

function ConvertTo-NuGetVersion {
    <# Parses 1-4 numeric segments with an optional -prerelease and +metadata. Missing segments count as 0. #>
    [CmdletBinding()]
    param([Parameter(Mandatory, Position = 0)][string]$Version)
    if ($Version -notmatch '^(\d+(?:\.\d+){0,3})(?:-([0-9A-Za-z.-]+))?(?:\+([0-9A-Za-z.-]+))?$') {
        throw "Not a NuGet version: '$Version'."
    }
    $numbers = [bigint[]]@($Matches[1] -split '\.')
    while ($numbers.Count -lt 4) { $numbers += [bigint]0 }
    $pre = if ($Matches.ContainsKey(2)) { $Matches[2] } else { '' }
    $meta = if ($Matches.ContainsKey(3)) { $Matches[3] } else { '' }
    $labels = [string[]]@()
    if ($pre) { $labels = [string[]]($pre -split '\.') }
    [pscustomobject]@{
        Numbers    = $numbers
        Prerelease = $pre
        Labels     = $labels
        Metadata   = $meta
        Original   = $Version
    }
}

function Compare-NuGetVersion {
    <# Returns -1, 0 or 1. Metadata after '+' is ignored; prerelease labels compare per NuGet/SemVer 2. #>
    [CmdletBinding()]
    param([Parameter(Mandatory, Position = 0)][string]$A, [Parameter(Mandatory, Position = 1)][string]$B)
    $x = ConvertTo-NuGetVersion $A
    $y = ConvertTo-NuGetVersion $B
    for ($i = 0; $i -lt 4; $i++) {
        $c = $x.Numbers[$i].CompareTo($y.Numbers[$i])
        if ($c -ne 0) { return [Math]::Sign($c) }
    }
    $xHas = $x.Labels.Count -gt 0
    $yHas = $y.Labels.Count -gt 0
    if (-not $xHas -and -not $yHas) { return 0 }
    if (-not $xHas) { return 1 }   # a release is higher than its prereleases
    if (-not $yHas) { return -1 }
    $n = [Math]::Min($x.Labels.Count, $y.Labels.Count)
    for ($i = 0; $i -lt $n; $i++) {
        $p = $x.Labels[$i]; $q = $y.Labels[$i]
        $pNum = $p -match '^\d+$'; $qNum = $q -match '^\d+$'
        if ($pNum -and $qNum) { $c = ([bigint]$p).CompareTo([bigint]$q) }
        elseif ($pNum) { $c = -1 }   # numeric segments are lower than alphanumeric ones
        elseif ($qNum) { $c = 1 }
        else { $c = [string]::Compare($p, $q, [StringComparison]::OrdinalIgnoreCase) }
        if ($c -ne 0) { return [Math]::Sign($c) }
    }
    [Math]::Sign($x.Labels.Count - $y.Labels.Count)   # fewer segments is lower
}

function Get-MaxNuGetVersion {
    <# The highest of the given versions, or $null for an empty list. #>
    [CmdletBinding()]
    param([string[]]$Versions)
    $max = $null
    foreach ($v in @($Versions)) {
        if ([string]::IsNullOrWhiteSpace($v)) { continue }
        if ($null -eq $max -or (Compare-NuGetVersion $v $max) -gt 0) { $max = $v }
    }
    $max
}

function Get-PublishedNuGetVersions {
    <# All versions of a package on nuget.org (flat container, lower-case ids and versions). 404, any other non-200 status and an empty list are errors. #>
    [CmdletBinding()]
    param([string]$PackageId = 'openslidesharp')
    $url = "https://api.nuget.org/v3-flatcontainer/$($PackageId.ToLowerInvariant())/index.json"
    $r = Invoke-WebRequest -Uri $url -SkipHttpErrorCheck -Headers @{ 'User-Agent' = 'OpenSlideSharp-release' }
    if ($r.StatusCode -ne 200) { throw "GET $url returned HTTP $($r.StatusCode)." }
    $json = $r.Content | ConvertFrom-Json
    if ($null -eq $json -or -not ($json.PSObject.Properties.Name -contains 'versions')) { throw "GET $url returned no 'versions' list." }
    Assert-PublishedNuGetVersions ([string[]]@($json.versions)) $PackageId
}

function Assert-PublishedNuGetVersions {
    <# The package is known to exist on nuget.org, so an empty list means a broken fetch, not "nothing published". #>
    [CmdletBinding()]
    param([string[]]$Versions, [string]$PackageId = 'openslidesharp')
    if (@($Versions | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }).Count -eq 0) {
        throw "nuget.org returned no published versions for $PackageId, which is known to exist; refusing to continue without the monotonic check."
    }
    [string[]]@($Versions)
}

Export-ModuleMember -Function ConvertTo-NuGetVersion, Compare-NuGetVersion, Get-MaxNuGetVersion, Get-PublishedNuGetVersions, Assert-PublishedNuGetVersions
