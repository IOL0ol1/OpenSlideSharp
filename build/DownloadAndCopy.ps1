

param(
[string]$x86="https://github.com/openslide/openslide-winbuild/releases/download/v20171122/openslide-win32-20171122.zip",
[string]$x64="https://github.com/openslide/openslide-winbuild/releases/download/v20171122/openslide-win64-20171122.zip",
[string]$x86Output="..\openslide\x86\",
[string]$x64Output="..\openslide\x64\")

$dir = Split-Path $MyInvocation.MyCommand.Path
$tmp = Get-Location
Set-Location $dir

$x86Rename = "openslide-win32"
$x64Rename = "openslide-win64"
$x86Archive = ($x86Rename + ".zip")
$x64Archive = ($x64Rename + ".zip") 

# Download zip 
Invoke-WebRequest -uri $x86 -OutFile $x86Archive
Invoke-WebRequest -uri $x64 -OutFile $x64Archive
# Expand zip
Expand-Archive -Path $x86Archive -DestinationPath $x86Rename -Force
Expand-Archive -Path $x64Archive -DestinationPath $x64Rename -Force
# Folder exist or create
if(!(Test-Path -Path $x86Output -PathType Container)){
    New-Item -Path $x86Output -ItemType Directory
}
# Move to dst folder
Get-ChildItem -Path ($x86Rename+"\*\bin") -Filter "*.dll" -Recurse | ForEach-Object {
    Move-Item -Path $_.FullName -Destination ($x86Output+$_.Name) -Force
}

if(!(Test-Path -Path $x64Output -PathType Container)){
    New-Item -Path $x64Output -ItemType Directory
}
Get-ChildItem -Path ($x64Rename+"\*\bin") -Filter "*.dll" -Recurse | ForEach-Object {
    Move-Item -Path $_.FullName -Destination ($x64Output+$_.Name) -Force
}
# Remove zip and src folder
Remove-Item $x86Archive
Remove-Item $x64Archive
Remove-Item $x86Rename -Recurse -Force
Remove-Item $x64Rename -Recurse -Force
Set-Location $tmp
 