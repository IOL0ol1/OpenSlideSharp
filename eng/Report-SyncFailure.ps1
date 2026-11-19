#!/usr/bin/env pwsh
#requires -Version 7
<#
.SYNOPSIS
  Reports a failed upstream sync: one open issue per openslide-bin build, then deletes the run's sync branch.
.DESCRIPTION
  The issue is titled "Upstream sync failed: openslide-bin <tag>", or "Upstream sync failed: openslide-bin update
  check" when the run failed before it knew which build to take. If an open issue (not a pull request) has exactly that
  title, the failure is added to it as a comment, so a build never gets a second open issue; otherwise a new issue is
  opened. Both link the run and list the result of each job. Then, with -Branch, the branch is deleted; a branch that
  is gone already is fine, and only sync/* branches are accepted.
  Uses the GitHub REST API with $env:GITHUB_TOKEN (issues: write, and contents: write to delete the branch).
  -SelfTest runs built-in cases against a mocked API (no network) and exits 1 if one fails.
.PARAMETER Repository
  owner/name.
.PARAMETER RunUrl
  The workflow run to link.
.PARAMETER Tag
  The openslide-bin release tag the run synced; empty if it failed before finding one.
.PARAMETER Jobs
  The workflow's needs context as JSON (toJSON(needs)). Each job's result goes into the report, and the merge job's
  outputs say whether master was fast-forwarded before the failure.
.PARAMETER Branch
  The sync branch this run pushed, to delete, e.g. sync/v4.0.1.2+20261110.nightly. Empty: nothing to delete.
#>
[CmdletBinding()]
param(
    [string]$Repository,
    [string]$RunUrl,
    [string]$Tag,
    [string]$Jobs,
    [string]$Branch,
    [switch]$SelfTest
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Get-SyncFailureTitle([string]$Tag) {
    if ($Tag) { "Upstream sync failed: openslide-bin $Tag" } else { 'Upstream sync failed: openslide-bin update check' }
}

# The report text: what failed, the run, each job's result, and whether master changed.
function Get-SyncFailureBody([string]$Tag, [string]$RunUrl, [string]$Jobs, [bool]$Again) {
    $lines = [System.Collections.Generic.List[string]]::new()
    if ($Again) { $lines.Add("Failed again: $RunUrl") }
    elseif ($Tag) { $lines.Add("The upstream sync of openslide-bin ``$Tag`` failed: $RunUrl") }
    else { $lines.Add("The upstream sync failed while looking for a newer openslide-bin build: $RunUrl") }
    $needs = $null
    if ($Jobs) { try { $needs = $Jobs | ConvertFrom-Json } catch { $needs = $null } }
    if ($needs) {
        $lines.Add('')
        $lines.Add('| Job | Result |')
        $lines.Add('|---|---|')
        foreach ($p in $needs.PSObject.Properties) { $lines.Add("| $($p.Name) | $($p.Value.result) |") }
    }
    $lines.Add('')
    $merge = if ($needs) { $needs.PSObject.Properties['merge'] } else { $null }
    if ($merge -and $merge.Value.outputs -and $merge.Value.outputs.merged -eq 'true') {
        $lines.Add("master was fast-forwarded to ``$($merge.Value.outputs.sha)`` before the failure, so the release of that commit did not finish. Use ""Re-run failed jobs"" on the run to finish it.")
    } else {
        $lines.Add('master is unchanged and nothing was released.')
    }
    if (-not $Again) {
        $lines.Add('')
        $lines.Add('Later failures of the same build are added here as comments. The sync tries the build again on its next runs while it is the newest one; close this issue when it passes or a newer build replaces it.')
    }
    $lines -join "`n"
}

# The GitHub REST API. Every call returns Status (the HTTP status), Content (the parsed JSON, or $null) and Next (the
# path of the next page from the Link header, or $null).
function New-GitHubApi {
    {
        param([string]$Method, [string]$Path, $Body)
        $headers = @{ Accept = 'application/vnd.github+json'; 'User-Agent' = 'OpenSlideSharp-upstream-sync'; 'X-GitHub-Api-Version' = '2022-11-28' }
        if ($env:GITHUB_TOKEN) { $headers.Authorization = "Bearer $($env:GITHUB_TOKEN)" }
        $request = @{ Uri = "https://api.github.com/$Path"; Method = $Method; Headers = $headers; SkipHttpErrorCheck = $true }
        if ($null -ne $Body) { $request.Body = $Body | ConvertTo-Json -Depth 5; $request.ContentType = 'application/json; charset=utf-8' }
        $r = Invoke-WebRequest @request
        $next = $null
        if ("$($r.Headers['Link'])" -match '<https://api\.github\.com/([^>]+)>;\s*rel="next"') { $next = $Matches[1] }
        $content = if ("$($r.Content)".Trim()) { "$($r.Content)" | ConvertFrom-Json -NoEnumerate } else { $null }
        [pscustomobject]@{ Status = [int]$r.StatusCode; Content = $content; Next = $next }
    }
}

function Get-ApiError($Response) {
    $message = if ($Response.Content -and $Response.Content.PSObject.Properties['message']) { ": $($Response.Content.message)" } else { '' }
    "HTTP $($Response.Status)$message"
}

# The number of the open issue (not pull request) titled exactly $Title, or $null. Reads every page.
function Find-OpenIssue([scriptblock]$Api, [string]$Repository, [string]$Title) {
    $path = "repos/$Repository/issues?state=open&per_page=100"
    while ($path) {
        $r = & $Api 'GET' $path $null
        if ($r.Status -ne 200) { throw "Listing the open issues failed with $(Get-ApiError $r)." }
        foreach ($issue in @($r.Content)) {
            if ($issue.PSObject.Properties['pull_request'] -and $null -ne $issue.pull_request) { continue }
            if ([string]$issue.state -ne 'open') { continue }
            if ([string]$issue.title -ceq $Title) { return [int]$issue.number }
        }
        $path = $r.Next
    }
    $null
}

function Invoke-SyncFailureReport {
    param([Parameter(Mandatory)][scriptblock]$Api, [Parameter(Mandatory)][string]$Repository, [Parameter(Mandatory)][string]$RunUrl,
        [string]$Tag, [string]$Jobs, [string]$Branch)
    # Checked before any call, so a bad argument changes nothing.
    if ($Repository -notmatch '^[\w.-]+/[\w.-]+$') { throw "Unexpected repository '$Repository'." }
    if ($RunUrl -notmatch '^https://\S+$') { throw "Unexpected run URL '$RunUrl'." }
    if ($Tag -and $Tag -notmatch '^v[0-9A-Za-z.+-]+$') { throw "Unexpected tag '$Tag'." }
    if ($Branch -and $Branch -notmatch '^sync/v[0-9A-Za-z.+-]+$') { throw "Refusing to delete '$Branch': only sync/<tag> branches are deleted." }

    $title = Get-SyncFailureTitle $Tag
    $number = Find-OpenIssue $Api $Repository $title
    $created = $null -eq $number
    if ($created) {
        $r = & $Api 'POST' "repos/$Repository/issues" @{ title = $title; body = (Get-SyncFailureBody $Tag $RunUrl $Jobs $false) }
        if ($r.Status -ne 201) { throw "Opening the issue '$title' failed with $(Get-ApiError $r)." }
        $number = [int]$r.Content.number
        $url = [string]$r.Content.html_url
        Write-Host "Opened issue #${number}: $title"
    } else {
        $r = & $Api 'POST' "repos/$Repository/issues/$number/comments" @{ body = (Get-SyncFailureBody $Tag $RunUrl $Jobs $true) }
        if ($r.Status -ne 201) { throw "Commenting on issue #$number failed with $(Get-ApiError $r)." }
        $url = [string]$r.Content.html_url
        Write-Host "Issue #$number is open already for this build; added the run as a comment."
    }

    $deleted = $false
    if ($Branch) {
        $ref = ($Branch -split '/' | ForEach-Object { [uri]::EscapeDataString($_) }) -join '/'
        $r = & $Api 'DELETE' "repos/$Repository/git/refs/heads/$ref" $null
        if ($r.Status -eq 204) { $deleted = $true; Write-Host "Deleted the branch $Branch." }
        elseif ($r.Status -in 404, 422) { Write-Host "The branch $Branch does not exist (anymore)." }
        else { throw "Deleting the branch $Branch failed with $(Get-ApiError $r)." }
    }
    [pscustomobject]@{ Title = $title; Issue = $number; Url = $url; Created = $created; BranchDeleted = $deleted }
}

if ($SelfTest) {
    $results = [System.Collections.Generic.List[object]]::new()
    function Test-Case([string]$Name, [scriptblock]$Body) {
        try {
            # A case must produce exactly one boolean; stray output (e.g. a negative case that did not throw) is a failure.
            $out = @(& $Body)
            $ok = $out.Count -eq 1 -and $out[0] -is [bool] -and $out[0]
        } catch { $ok = $false; $Name += " (threw: $($_.Exception.Message))" }
        $results.Add([pscustomobject]@{ Name = $Name; Ok = $ok })
    }
    # A fake GitHub API over a list of issues (open and closed) that records every call. Like the real one, it lists
    # only open issues when asked for state=open, pages by per_page, and fails with the given statuses.
    function New-MockApi {
        param([object[]]$Issues = @(), [int]$PageSize = 100, [int]$ListStatus = 200, [int]$CreateStatus = 201,
            [int]$CommentStatus = 201, [int]$DeleteStatus = 204)
        $calls = [System.Collections.Generic.List[object]]::new()
        $api = {
            param([string]$Method, [string]$Path, $Body)
            $calls.Add([pscustomobject]@{ Method = $Method; Path = $Path; Body = $Body })
            $failure = [pscustomobject]@{ message = 'mock failure' }
            if ($Method -eq 'GET' -and $Path -match '^(repos/o/r|repositories/1)/issues\?') {
                if ($ListStatus -ne 200) { return [pscustomobject]@{ Status = $ListStatus; Content = $failure; Next = $null } }
                $all = @($Issues | Where-Object { $Path -notmatch '(^|[?&])state=open(&|$)' -or $_.state -eq 'open' })
                $page = if ($Path -match '[?&]page=(\d+)') { [int]$Matches[1] } else { 1 }
                $items = @($all | Select-Object -Skip (($page - 1) * $PageSize) -First $PageSize)
                $next = if ($page * $PageSize -lt $all.Count) { "repositories/1/issues?state=open&per_page=100&page=$($page + 1)" } else { $null }
                return [pscustomobject]@{ Status = 200; Content = $items; Next = $next }
            }
            if ($Method -eq 'POST' -and $Path -eq 'repos/o/r/issues') {
                $content = if ($CreateStatus -eq 201) { [pscustomobject]@{ number = 42; html_url = 'https://github.com/o/r/issues/42' } } else { $failure }
                return [pscustomobject]@{ Status = $CreateStatus; Content = $content; Next = $null }
            }
            if ($Method -eq 'POST' -and $Path -match '^repos/o/r/issues/(\d+)/comments$') {
                $content = if ($CommentStatus -eq 201) { [pscustomobject]@{ html_url = "https://github.com/o/r/issues/$($Matches[1])#issuecomment-1" } } else { $failure }
                return [pscustomobject]@{ Status = $CommentStatus; Content = $content; Next = $null }
            }
            if ($Method -eq 'DELETE' -and $Path -match '^repos/o/r/git/refs/heads/') {
                return [pscustomobject]@{ Status = $DeleteStatus; Content = ($DeleteStatus -eq 204 ? $null : $failure); Next = $null }
            }
            throw "Unexpected API call $Method $Path"
        }.GetNewClosure()
        [pscustomobject]@{ Api = $api; Calls = $calls }
    }
    function New-Issue([int]$Number, [string]$Title, [string]$State = 'open', [switch]$PullRequest) {
        $i = [pscustomobject]@{ number = $Number; title = $Title; state = $State }
        if ($PullRequest) { $i | Add-Member pull_request ([pscustomobject]@{ url = 'https://api.github.com/repos/o/r/pulls/1' }) }
        $i
    }
    function Report($Mock, [string]$Tag = $tag, [string]$Branch = $branch, [string]$Jobs = $ciFailed) {
        Invoke-SyncFailureReport -Api $Mock.Api -Repository 'o/r' -RunUrl $run -Tag $Tag -Jobs $Jobs -Branch $Branch 6> $null
    }
    function Get-Calls($Mock, [string]$Method, [string]$Pattern) { @($Mock.Calls | Where-Object { $_.Method -eq $Method -and $_.Path -match $Pattern }) }

    $tag = 'v4.0.1.2+20261110.nightly'
    $title = "Upstream sync failed: openslide-bin $tag"
    $branch = "sync/$tag"
    $run = 'https://github.com/o/r/actions/runs/123'
    $ciFailed = '{"detect":{"result":"success","outputs":{"tag":"v4.0.1.2+20261110.nightly"}},"update":{"result":"success","outputs":{}},"ci":{"result":"failure","outputs":{}},"merge":{"result":"skipped","outputs":{}},"release":{"result":"skipped","outputs":{}}}'
    $releaseFailed = '{"detect":{"result":"success","outputs":{}},"update":{"result":"success","outputs":{}},"ci":{"result":"success","outputs":{}},"merge":{"result":"success","outputs":{"merged":"true","sha":"0123456789abcdef0123456789abcdef01234567"}},"release":{"result":"failure","outputs":{}}}'

    Test-Case 'title names the upstream tag' { (Get-SyncFailureTitle $tag) -ceq 'Upstream sync failed: openslide-bin v4.0.1.2+20261110.nightly' }
    Test-Case 'title when the check failed before finding a tag' { (Get-SyncFailureTitle '') -ceq 'Upstream sync failed: openslide-bin update check' }
    Test-Case 'no open issue: opens one with the title, the run link and the job results' {
        $m = New-MockApi
        $r = Report $m
        $create = Get-Calls $m 'POST' '^repos/o/r/issues$'
        $list = Get-Calls $m 'GET' 'issues\?'
        $r.Created -and $r.Issue -eq 42 -and $create.Count -eq 1 -and $create[0].Body.title -ceq $title -and
        $create[0].Body.body.Contains($run) -and $create[0].Body.body.Contains('| ci | failure |') -and
        $create[0].Body.body.Contains('master is unchanged and nothing was released.') -and
        (Get-Calls $m 'POST' 'comments$').Count -eq 0 -and $list.Count -eq 1 -and $list[0].Path -match '[?&]state=open(&|$)' }
    Test-Case 'open issue with the same title: comments on it with the run link, opens none' {
        $m = New-MockApi -Issues @((New-Issue 3 'Something else'), (New-Issue 7 $title))
        $r = Report $m
        $comment = Get-Calls $m 'POST' '^repos/o/r/issues/7/comments$'
        -not $r.Created -and $r.Issue -eq 7 -and $comment.Count -eq 1 -and $comment[0].Body.body.Contains($run) -and
        $comment[0].Body.body.Contains('| ci | failure |') -and (Get-Calls $m 'POST' '^repos/o/r/issues$').Count -eq 0 }
    Test-Case 'a pull request with the same title is not the issue' {
        $m = New-MockApi -Issues @(New-Issue 5 $title -PullRequest)
        $r = Report $m
        $r.Created -and (Get-Calls $m 'POST' 'comments$').Count -eq 0 }
    Test-Case 'a closed issue with the same title is not reused' {
        $m = New-MockApi -Issues @(New-Issue 4 $title -State closed)
        (Report $m).Created }
    Test-Case "another build's issue, or a longer title, is not reused" {
        $m = New-MockApi -Issues @((New-Issue 8 'Upstream sync failed: openslide-bin v4.0.1.2+20261102.nightly'),
            (New-Issue 9 "$title (retry)"), (New-Issue 10 $title.ToUpperInvariant()))
        (Report $m).Created }
    Test-Case 'finds the issue on a later page, so it is not opened twice' {
        $issues = @(1..5 | ForEach-Object { New-Issue $_ "Other $_" }) + @(New-Issue 11 $title)
        $m = New-MockApi -Issues $issues -PageSize 2
        $r = Report $m
        -not $r.Created -and $r.Issue -eq 11 -and (Get-Calls $m 'GET' 'issues\?').Count -eq 3 -and (Get-Calls $m 'POST' '^repos/o/r/issues$').Count -eq 0 }
    Test-Case 'listing fails: throws, opens nothing and keeps the branch' {
        $m = New-MockApi -ListStatus 500
        try { $null = Report $m; $false } catch { $_.Exception.Message -match 'HTTP 500' -and (Get-Calls $m 'POST' '.').Count -eq 0 -and (Get-Calls $m 'DELETE' '.').Count -eq 0 } }
    Test-Case 'opening the issue fails: throws' {
        $m = New-MockApi -CreateStatus 403
        try { $null = Report $m; $false } catch { $_.Exception.Message -match 'HTTP 403' } }
    Test-Case 'commenting fails: throws' {
        $m = New-MockApi -Issues @(New-Issue 7 $title) -CommentStatus 500
        try { $null = Report $m; $false } catch { $_.Exception.Message -match 'HTTP 500' } }
    Test-Case 'deletes the sync branch after reporting, with + escaped in the ref' {
        $m = New-MockApi
        $r = Report $m
        $delete = Get-Calls $m 'DELETE' '.'
        $r.BranchDeleted -and $delete.Count -eq 1 -and $delete[0].Path -ceq 'repos/o/r/git/refs/heads/sync/v4.0.1.2%2B20261110.nightly' -and
        $m.Calls[$m.Calls.Count - 1].Method -eq 'DELETE' }
    Test-Case 'a branch that is gone already is fine' {
        $r404 = Report (New-MockApi -DeleteStatus 404)
        $r422 = Report (New-MockApi -DeleteStatus 422)
        $r404.Created -and -not $r404.BranchDeleted -and $r422.Created -and -not $r422.BranchDeleted }
    Test-Case 'deleting the branch fails otherwise: throws after the issue is opened' {
        $m = New-MockApi -DeleteStatus 500
        try { $null = Report $m; $false } catch { $_.Exception.Message -match 'HTTP 500' -and (Get-Calls $m 'POST' '^repos/o/r/issues$').Count -eq 1 } }
    Test-Case 'no branch given: nothing is deleted' {
        $m = New-MockApi
        $r = Report $m -Branch ''
        $r.Created -and -not $r.BranchDeleted -and (Get-Calls $m 'DELETE' '.').Count -eq 0 }
    Test-Case 'refuses to delete a branch outside sync/, before any call' {
        $outcomes = foreach ($b in 'master', 'refs/heads/master', 'sync/../master', 'feature/sync/v1.0.0.0') {
            $m = New-MockApi
            try { $null = Report $m -Branch $b; $false } catch { $_.Exception.Message -match 'Refusing' -and $m.Calls.Count -eq 0 }
        }
        @($outcomes | Where-Object { $_ -ne $true }).Count -eq 0 }
    Test-Case 'the check failed before finding a tag: fixed title, says so, deletes nothing' {
        $m = New-MockApi
        $r = Report $m -Tag '' -Branch '' -Jobs '{"detect":{"result":"failure","outputs":{}},"update":{"result":"skipped","outputs":{}}}'
        $create = Get-Calls $m 'POST' '^repos/o/r/issues$'
        $r.Created -and $create[0].Body.title -ceq 'Upstream sync failed: openslide-bin update check' -and
        $create[0].Body.body.Contains('while looking for a newer openslide-bin build') -and $create[0].Body.body.Contains('| detect | failure |') -and
        (Get-Calls $m 'DELETE' '.').Count -eq 0 }
    Test-Case 'the same title is found again when the check fails twice' {
        $m = New-MockApi -Issues @(New-Issue 12 'Upstream sync failed: openslide-bin update check')
        $r = Report $m -Tag '' -Branch ''
        -not $r.Created -and $r.Issue -eq 12 }
    Test-Case 'release failed after the fast-forward: the report says master moved to the commit' {
        $m = New-MockApi
        $null = Report $m -Jobs $releaseFailed
        $body = (Get-Calls $m 'POST' '^repos/o/r/issues$')[0].Body.body
        $body.Contains('fast-forwarded to `0123456789abcdef0123456789abcdef01234567`') -and $body.Contains('| release | failure |') -and
        -not $body.Contains('nothing was released') }
    Test-Case 'unreadable job results still give a report' {
        $m = New-MockApi
        $r = Report $m -Jobs 'not json'
        $r.Created -and (Get-Calls $m 'POST' '^repos/o/r/issues$')[0].Body.body.Contains($run) }
    Test-Case 'rejects an unexpected tag, before any call' {
        $m = New-MockApi
        try { $null = Report $m -Tag 'v1; rm -rf /'; $false } catch { $_.Exception.Message -match 'Unexpected tag' -and $m.Calls.Count -eq 0 } }

    foreach ($r in $results) { Write-Host ("{0} {1}" -f ($r.Ok ? 'PASS' : 'FAIL'), $r.Name) }
    $failed = @($results | Where-Object { -not $_.Ok }).Count
    Write-Host "$($results.Count - $failed) passed, $failed failed"
    exit ($failed -gt 0 ? 1 : 0)
}

try {
    if (-not $env:GITHUB_TOKEN) { throw 'GITHUB_TOKEN is not set.' }
    $report = Invoke-SyncFailureReport -Api (New-GitHubApi) -Repository $Repository -RunUrl $RunUrl -Tag $Tag -Jobs $Jobs -Branch $Branch
    if ($env:GITHUB_STEP_SUMMARY) {
        $issueLine = $report.Created ? "Opened [#$($report.Issue)]($($report.Url)): $($report.Title)" : "Added this run to the open issue [#$($report.Issue)]($($report.Url)): $($report.Title)"
        $lines = @('## Upstream sync failed', '', $issueLine)
        if ($Branch) { $lines += '', ($report.BranchDeleted ? "Deleted the branch ``$Branch``." : "The branch ``$Branch`` did not exist.") }
        $lines | Add-Content -Path $env:GITHUB_STEP_SUMMARY -Encoding utf8
    }
} catch {
    [Console]::Error.WriteLine("Report-SyncFailure: $($_.Exception.Message)")
    exit 1
}
