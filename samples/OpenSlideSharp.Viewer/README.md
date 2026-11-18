# OpenSlideSharp.Viewer

A desktop viewer for whole-slide images on Windows, Linux and macOS, built on Avalonia and `OpenSlideSharp` after openslide-java's demo viewer. From a checkout:

```sh
pwsh eng/Get-OpenSlide.ps1
dotnet run -c Release --project samples/OpenSlideSharp.Viewer -- /path/to/slide.svs
```

Open a slide from the command line, with File → Open, or by dropping it on the window. A file that cannot be opened is reported in the status bar, and the slide shown stays.

## Download

Each 4.x [GitHub release](https://github.com/IOL0ol1/OpenSlideSharp/releases) has the viewer for three platforms:

| Platform | Archive |
|---|---|
| Windows x64 | `OpenSlideSharp.Viewer-<version>-win-x64.zip` |
| Linux x64 | `OpenSlideSharp.Viewer-<version>-linux-x64.tar.gz` |
| macOS on Apple silicon | `OpenSlideSharp.Viewer-<version>-osx-arm64.tar.gz` |

An archive holds the self-contained executable (`OpenSlideSharp.Viewer.exe` on Windows, `OpenSlideSharp.Viewer` on Linux and macOS), which runs without .NET installed, and `licenses/`, at its root. Extract it into a folder of its own and start the executable.

The macOS executable is signed ad hoc only: it has no Developer ID signature and is not notarized. If macOS blocks a downloaded copy, remove its quarantine attribute and start it again:

```sh
xattr -d com.apple.quarantine OpenSlideSharp.Viewer
./OpenSlideSharp.Viewer
```

## Features

- Pan and zoom; tiles are read in the background, and coarser levels show until the finer ones arrive.
- A navigator, the slide's properties (Ctrl+C, Cmd+C on macOS, or the context menu copies them) and its associated images (double-click to open).
- Rectangle selections, listed in the Selections tab with "Zoom to" and "Delete".
- A status bar with the level-0 pixel under the pointer, the zoom and magnification, and physical sizes when the slide has mpp.

Pixels are shown as OpenSlide returns them, without ICC color conversion, as in openslide-java's viewer.

## Shortcuts

| Input | Action |
|---|---|
| Drag | Pan |
| Wheel | Zoom at the pointer |
| Shift + drag | Select a rectangle |
| Arrow keys, `W` `A` `S` `D` | Pan by 100 pixels |
| `+` or `=`, `-` | Zoom in, out |
| `Z` | Fit the slide to the window |
| `1` | Zoom to 1:1 (one level-0 pixel per screen pixel) |
| Space | Center the slide |
| Ctrl+O (Cmd+O on macOS) | Open a slide |
| Ctrl+W (Cmd+W on macOS) | Close the slide |

The view keys work while the view has the keyboard focus: click it first.

## `--check`

`OpenSlideSharp.Viewer --check slide.svs` opens the slide without a window, reads a tile of its lowest-resolution level and prints `openslide=<version>` and `slide=<width>x<height>`. It exits with 0 on success, 1 if the slide cannot be opened or read, 2 for invalid arguments and 3 if the native OpenSlide library cannot be loaded.

On Windows the viewer is a GUI program, so a console does not wait for it. To get the output and the exit code, use `start /wait OpenSlideSharp.Viewer.exe --check slide.svs` in cmd, or pipe it in PowerShell: `.\OpenSlideSharp.Viewer.exe --check slide.svs | Out-Host`, then `$LASTEXITCODE`.

## Self-contained publish

```sh
dotnet publish samples/OpenSlideSharp.Viewer -c Release -r win-x64 --self-contained -p:PublishSingleFile=true \
  -p:UseOpenSlideRuntimePackage=true -p:OpenSlideSharpVersion=<version>
```

`artifacts/publish/OpenSlideSharp.Viewer/release_win-x64/` then holds `OpenSlideSharp.Viewer.exe`, which runs without .NET installed, and `licenses/`. The other runtime identifiers are `linux-x64`, `linux-arm64`, `osx-arm64` and `osx-x64`. OpenSlide comes from the `OpenSlideSharp.runtime` package of that version, restored from nuget.org.

To publish with an `OpenSlideSharp.runtime` package in a local folder instead, such as the one `pwsh eng/Test-Packages.ps1 -PackOnly -Version <version>` packs into `artifacts/smoke-feed` (after `pwsh eng/Get-OpenSlide.ps1 -Platform all`), use the script the releases use:

```sh
pwsh eng/Publish-Viewer.ps1 -Rid win-x64 -Version <version> -Feed artifacts/smoke-feed
```

It restores `OpenSlideSharp*` packages from that folder only, checks that the runtime package came from it, checks the publish folder (under `artifacts/viewer/<rid>/`) and, for this machine's runtime identifier, runs `--check`. Do not add the folder to `dotnet publish` as a second package source (for example with `-p:RestoreAdditionalProjectSources`): with the same package and version in two sources, NuGet may take either one.

The native libraries are inside the executable. It extracts them on first start to `%TEMP%\.net` on Windows and `~/.net` on Linux and macOS (or to `DOTNET_BUNDLE_EXTRACT_BASE_DIR`), and reuses them after that. The folders that older versions of the viewer extracted there can be deleted.

## Licenses

`licenses/` holds the license and notice files of what the executable contains: OpenSlide and the libraries linked into it (LGPL-2.1 and others, under `openslide-bin/<platform>/` for the openslide-bin build of the publish's platform, with their versions in its `VERSIONS.md`), SkiaSharp, HarfBuzzSharp, ANGLE, Avalonia, the .NET runtime and the Inter font. `THIRD-PARTY-NOTICES.txt` lists them. `SOURCE.md`, written at publish time from `openslide-bin/openslide-bin.json`, names the exact openslide-bin build and its source tarball (file name, sha256, the openslide-bin download URL, and the copy attached to the [GitHub release](https://github.com/IOL0ol1/OpenSlideSharp/releases) of the same version), and says how to run the viewer with another OpenSlide build (`OPENSLIDE_LIBRARY_PATH`). Keep the folder with the executable. The release archives (see [Download](#download)) are this publish folder: the executable and `licenses/`.

## Known issue on Windows

Corrupt JPEG or PNG data in a slide terminates the viewer with `0xC0000409`, without an error message; see [the details](../../docs/known-issues/windows-cfg-longjmp-crash.md). Linux and macOS report such tiles in the status bar.
