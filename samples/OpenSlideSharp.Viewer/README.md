# OpenSlideSharp.Viewer

A desktop viewer for whole-slide images, built on Avalonia and `OpenSlideSharp`, for Windows, Linux and macOS. It follows openslide-java's demo viewer: one slide, dragged and zoomed with the mouse or the keyboard, with rectangle selections.

From a checkout:

```sh
pwsh eng/Get-OpenSlide.ps1
dotnet run -c Release --project samples/OpenSlideSharp.Viewer -- /path/to/slide.svs
```

Open a slide by passing it as the first argument, with File → Open, or by dropping it on the window. A file that cannot be opened is reported in the status bar, and the slide shown stays.

## Features

- **Slide view:** tiles of the level that best fits the zoom, read on background threads. Coarser tiles already read are drawn first, so the view fills in from low to high resolution. Read tiles are kept in a 256 MiB cache. Areas outside the slide take the slide's `openslide.background-color` (white if it has none).
- **Navigator:** a picture of the whole slide with the part in view outlined. Click or drag on it to move the view there.
- **Properties:** every property of the slide. Ctrl+C or the context menu copies the selected rows as `key<TAB>value`, or just the values.
- **Associated images:** the label, macro and other images of the slide. Double-click one, or press Enter, to open it in its own window.
- **Selections:** Shift + drag draws a rectangle. The Selections tab lists them, with "Zoom to" and "Delete" buttons.
- **Status bar:** the level-0 pixel under the pointer, and the zoom as `1:<level-0 pixels per screen pixel>`, followed by the magnification when the slide has `openslide.objective-power`. When the slide has `openslide.mpp-x` and `openslide.mpp-y`, it also shows the size of the view and of a selection (the one picked in the Selections tab, else the latest) in µm or mm.

## Shortcuts

| Input | Action |
|---|---|
| Drag | Pan |
| Wheel | Zoom at the pointer, by 2^(1/4) per notch |
| Shift + drag | Select a rectangle |
| Arrow keys, `W` `A` `S` `D` | Pan by 100 pixels |
| `+` or `=`, `-` | Zoom in, out, at the center of the view |
| `Z` | Fit the slide to the window |
| `1` | Zoom to 1:1 (one level-0 pixel per screen pixel) |
| Space | Center the slide |
| Ctrl+O | Open a slide |
| Ctrl+W | Close the slide |

The view keys work while the view has the keyboard focus: click it first if another part of the window has it.

## Colors

The viewer shows the pixels as OpenSlide returns them, like openslide-java's viewer: it does not convert colors with the slide's ICC profile. The web sample and the `openslide-sharp` tool convert to sRGB with `OpenSlideSharp.SkiaSharp`.

## Known issue on Windows

Corrupt JPEG or PNG data in a slide terminates the viewer with `0xC0000409`, without an error message. The failure cannot be caught; see [the details](../../docs/known-issues/windows-cfg-longjmp-crash.md). Linux and macOS report such tiles as errors in the status bar.

## `--check`

```sh
OpenSlideSharp.Viewer --check slide.svs
```

opens the slide without a window (or a display), reads the top-left 256 × 256 pixels of its lowest-resolution level and prints the OpenSlide version and the slide's size:

```
openslide=4.0.1-174630b
slide=16x16
```

| Exit code | Meaning |
|---|---|
| 0 | The slide was read. |
| 1 | The slide cannot be opened or read; the reason is printed. |
| 2 | Invalid arguments. |
| 3 | The native OpenSlide library cannot be loaded. |

On Windows the viewer is a GUI program, so shells do not wait for it. Started from a console, `--check` prints to that console, but the prompt can come back before the output, and the exit code is lost. To wait for it and get the exit code:

```bat
:: cmd
start /wait OpenSlideSharp.Viewer.exe --check slide.svs
echo %ERRORLEVEL%
```

```powershell
# PowerShell
(Start-Process .\OpenSlideSharp.Viewer.exe '--check', 'slide.svs' -NoNewWindow -Wait -PassThru).ExitCode
```

PowerShell also waits for it when its output is piped, and then sets `$LASTEXITCODE`: `.\OpenSlideSharp.Viewer.exe --check slide.svs | Out-Host`. Output redirected to a pipe or a file, as in scripts and CI, works like any program's.

## Self-contained publish

```sh
dotnet publish samples/OpenSlideSharp.Viewer -c Release -r win-x64 --self-contained -p:PublishSingleFile=true \
  -p:UseOpenSlideRuntimePackage=true -p:OpenSlideSharpVersion=<version>
```

The result, in `artifacts/publish/OpenSlideSharp.Viewer/release_win-x64/`, is one file that runs without .NET installed: `OpenSlideSharp.Viewer.exe` (about 50 MB), or `OpenSlideSharp.Viewer` for `linux-x64`, `linux-arm64`, `osx-arm64` and `osx-x64`. The native OpenSlide library comes from the `OpenSlideSharp.runtime` package of that version, restored from nuget.org or from a local folder given with `-p:RestoreAdditionalProjectSources=<folder>` (as `eng/Test-Packages.ps1` does, which publishes and runs `--check` on every CI platform).

The native libraries (OpenSlide, SkiaSharp, HarfBuzzSharp, and ANGLE on Windows or Avalonia's macOS library) are inside the file, which is compressed: on first start they are extracted to `%TEMP%\.net` on Windows and `~/.net` on Linux and macOS (or to `DOTNET_BUNDLE_EXTRACT_BASE_DIR`), and reused after that. Bundling them keeps a release to one download per platform: the GitHub Releases attach this file for `win-x64`, `linux-x64` and `osx-arm64`.
