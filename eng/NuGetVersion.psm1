#requires -Version 7
# NuGet version parsing and ordering, shared by Get-ReleaseVersion.ps1 and Find-UpstreamUpdate.ps1, plus the retrying web request the sync scripts share.
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

function Invoke-WebRequestWithRetry {
    <# Invoke-WebRequest (-SkipHttpErrorCheck, so the caller checks the status) that retries HTTP 500/502/503/504 and connection or timeout
       errors. Total attempts = $DelaySeconds.Count + 1 (default 3, waiting 5 s then 15 s); each retry logs a warning with the reason.
       Any other status, 4xx included, is returned at once. After the last attempt the last response is returned (or the last error rethrown).
       -Send (takes the Invoke-WebRequest parameter hashtable) and -Sleep (takes seconds) exist so self-tests need neither network nor waiting. #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Uri,
        [hashtable]$Headers,
        [string]$OutFile,
        [int[]]$DelaySeconds = @(5, 15),
        [scriptblock]$Send = { param($Params) Invoke-WebRequest @Params },
        [scriptblock]$Sleep = { param($Seconds) Start-Sleep -Seconds $Seconds }
    )
    $params = @{ Uri = $Uri; SkipHttpErrorCheck = $true }
    if ($Headers) { $params.Headers = $Headers }
    if ($OutFile) { $params.OutFile = $OutFile; $params.PassThru = $true }
    $attempts = $DelaySeconds.Count + 1
    for ($attempt = 1; ; $attempt++) {
        $response = $null; $failure = $null
        try { $response = & $Send $params } catch { $failure = $_ }
        $reason = if ($failure) { "connection error: $($failure.Exception.Message)" }
                  elseif ([int]$response.StatusCode -in 500, 502, 503, 504) { "HTTP $([int]$response.StatusCode)" }
                  else { return $response }
        if ($attempt -ge $attempts) {
            if ($failure) { throw $failure }
            return $response
        }
        $delay = $DelaySeconds[$attempt - 1]
        Write-Warning "GET $Uri failed ($reason); attempt $attempt of $attempts, retrying in $delay s."
        & $Sleep $delay
    }
}

function Get-PublishedNuGetVersions {
    <# All versions of a package on nuget.org (flat container, lower-case ids and versions). 404, any other non-200 status and an empty list are errors. #>
    [CmdletBinding()]
    param([string]$PackageId = 'openslidesharp', [scriptblock]$Send, [scriptblock]$Sleep)
    $url = "https://api.nuget.org/v3-flatcontainer/$($PackageId.ToLowerInvariant())/index.json"
    $retry = @{}
    if ($Send) { $retry.Send = $Send }
    if ($Sleep) { $retry.Sleep = $Sleep }
    $r = Invoke-WebRequestWithRetry -Uri $url -Headers @{ 'User-Agent' = 'OpenSlideSharp-release' } @retry
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

Export-ModuleMember -Function ConvertTo-NuGetVersion, Compare-NuGetVersion, Get-MaxNuGetVersion, Invoke-WebRequestWithRetry, Get-PublishedNuGetVersions, Assert-PublishedNuGetVersions
