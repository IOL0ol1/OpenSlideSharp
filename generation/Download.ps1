param(
    [string]$OutputDir = "openslide"
)

$ErrorActionPreference = 'Stop'

$dir = Split-Path $MyInvocation.MyCommand.Path
$tmp = Get-Location
Set-Location $dir

# 获取最新 tag 的 release（含 assets）
$releasesApi = "https://api.github.com/repos/openslide/builds/releases"
$releases = Invoke-RestMethod -Uri $releasesApi -Headers @{ 'User-Agent' = 'PowerShell' }
$release = $releases | Where-Object { $_.assets } | Sort-Object {[datetime]$_.published_at} -Descending | Select-Object -First 1

$assets = $release.assets | Where-Object { $_.name -match 'windows' -and $_.name -like '*.zip' }

if (-not $assets) {
    Write-Error "No windows zip assets found in latest release."
    exit 1
}

foreach ($asset in $assets) {
    $zipName = $asset.name
    $zipUrl = $asset.browser_download_url
    $extractDir = Join-Path $dir ([System.IO.Path]::GetFileNameWithoutExtension($zipName) + "_tmp")
    $zipPath = Join-Path $dir $zipName

    if (!(Test-Path $zipPath)) {
        Write-Host "Downloading $zipUrl ..."
        Invoke-WebRequest -Uri $zipUrl -OutFile $zipPath
    } else {
        Write-Host "$zipName already exists, skip downloading."
    }

    if (Test-Path $extractDir) {
        Remove-Item $extractDir -Recurse -Force
    }
    New-Item -Path $extractDir -ItemType Directory | Out-Null

    Write-Host "Extracting $zipName to $extractDir ..."
    Expand-Archive -Path $zipPath -DestinationPath $extractDir -Force

    # 查找 bin 文件夹的父目录
    $binDir = Get-ChildItem -Path $extractDir -Recurse -Directory | Where-Object { $_.Name -ieq 'bin' } | Select-Object -First 1
    if ($binDir) {
        $parentDir = $binDir.Parent
        Write-Host "Copying contents of $($parentDir.FullName) to $OutputDir ..."
        Get-ChildItem -Path $parentDir.FullName | ForEach-Object {
            $target = Join-Path $OutputDir $_.Name
            if ($_.PSIsContainer) {
                Copy-Item $_.FullName -Destination $target -Recurse -Force
            } else {
                Copy-Item $_.FullName -Destination $target -Force
            }
        }
    } else {
        Write-Warning "No bin directory found in $zipName"
    }

    Remove-Item $extractDir -Recurse -Force
}

Set-Location $tmp