

param(
[string]$x86="https://github.com/openslide/openslide-winbuild/releases/download/v20171122/openslide-win32-20171122.zip",
[string]$x64="https://github.com/openslide/openslide-winbuild/releases/download/v20171122/openslide-win64-20171122.zip")


$x86Rename = "openslide-win32"
$x64Rename = "openslide-win64"

$x86Archive = ($x86Rename + ".zip")
$x64Archive = ($x64Rename + ".zip")
 
#Invoke-WebRequest -uri $x86 -OutFile $x86Archive
#Invoke-WebRequest -uri $x64 -OutFile $x64Archive

#Expand-Archive -Path $x86Archive -DestinationPath $x86Rename -Force
#Expand-Archive -Path $x64Archive -DestinationPath $x64Rename -Force

$x86Output = "..\openslide\x86\"
$x64Output = "..\openslide\x64\"

if(!(Test-Path -Path $x86Output -PathType Container)){
    New-Item -Path $x86Output -ItemType Directory
}
Get-ChildItem -Path ($x86Rename+"\*\bin") -Filter "*.dll" -Recurse | ForEach-Object {
    Copy-Item -Path $_.FullName -Destination ($x86Output+$_.Name) -Force
}

if(!(Test-Path -Path $x64Output -PathType Container)){
    New-Item -Path $x64Output -ItemType Directory
}
Get-ChildItem -Path ($x64Rename+"\*\bin") -Filter "*.dll" -Recurse | ForEach-Object {
    Copy-Item -Path $_.FullName -Destination ($x64Output+$_.Name) -Force
}
#Copy-Item -Path ($x86Rename+"\*\bin") -Destination ..\openslide\x86 -Force -Recurse -Include "*.dll"

# 'https://github.com/openslide/openslide-winbuild/releases/download/v20171122/openslide-win32-20171122.zip'
