#requires -Version 7
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
& (Join-Path $here 'Get-OpenSlide.ps1') -Platform current
Push-Location (Split-Path $here -Parent)
try { dotnet tool restore; if ($LASTEXITCODE) { throw 'dotnet tool restore failed' } } finally { Pop-Location }
Push-Location $here
try {
    $out = dotnet ClangSharpPInvokeGenerator "@GenerateOpenSlide.rsp" 2>&1 | Out-String
    Write-Host $out
    if ($LASTEXITCODE -ne 0 -or $out -match 'Error:') { throw 'ClangSharp generation failed' }
} finally { Pop-Location }
