# OpenSlideSharp.Viewer

A desktop viewer for whole-slide images on Windows, Linux and macOS, built on Avalonia and `OpenSlideSharp` after openslide-java's demo viewer. From a checkout:

```sh
pwsh eng/Get-OpenSlide.ps1
dotnet run -c Release --project samples/OpenSlideSharp.Viewer -- /path/to/slide.svs
```

Open a slide from the command line, with File → Open, or by dropping it on the window. A file that cannot be opened is reported in the status bar, and the slide shown stays.

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

`artifacts/publish/OpenSlideSharp.Viewer/release_win-x64/` then holds `OpenSlideSharp.Viewer.exe`, which runs without .NET installed, and `licenses/`. The other runtime identifiers are `linux-x64`, `linux-arm64`, `osx-arm64` and `osx-x64`. OpenSlide comes from the `OpenSlideSharp.runtime` package of that version, restored from nuget.org or from a folder given with `-p:RestoreAdditionalProjectSources=<folder>`, as `eng/Test-Packages.ps1` does.

The native libraries are inside the executable. It extracts them on first start to `%TEMP%\.net` on Windows and `~/.net` on Linux and macOS (or to `DOTNET_BUNDLE_EXTRACT_BASE_DIR`), and reuses them after that. The folders that older versions of the viewer extracted there can be deleted.

## Licenses

`licenses/` holds the license and notice files of what the executable contains: OpenSlide and the libraries linked into it (LGPL-2.1 and others, with their versions in each platform's `VERSIONS.md` under `openslide-bin/`), SkiaSharp, HarfBuzzSharp, ANGLE, Avalonia, the .NET runtime and the Inter font. `THIRD-PARTY-NOTICES.txt` lists them, and `SOURCE.md` says where OpenSlide's source code is and how to run the viewer with another OpenSlide build (`OPENSLIDE_LIBRARY_PATH`). Keep the folder with the executable. The planned GitHub release builds will ship this publish folder, executable and `licenses/`, as one archive per platform.

## Known issue on Windows

Corrupt JPEG or PNG data in a slide terminates the viewer with `0xC0000409`, without an error message; see [the details](../../docs/known-issues/windows-cfg-longjmp-crash.md). Linux and macOS report such tiles in the status bar.
