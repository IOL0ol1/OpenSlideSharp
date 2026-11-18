![OpenSlide](./openslide_logo.png)

[![CI](https://github.com/IOL0ol1/OpenSlideSharp/actions/workflows/ci.yml/badge.svg)](https://github.com/IOL0ol1/OpenSlideSharp/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/OpenSlideSharp)](https://www.nuget.org/packages/OpenSlideSharp)

# OpenSlideSharp

.NET bindings for [OpenSlide](https://openslide.org/) 4.x, a C library for reading whole-slide images.

## Packages

| Package | Contents |
|---|---|
| `OpenSlideSharp` | Managed API over P/Invoke bindings generated from the OpenSlide headers with ClangSharp. Targets `netstandard2.0`, `netstandard2.1` and `net10.0`. |
| `OpenSlideSharp.SkiaSharp` | JPEG and PNG encoding of `PixelImage` and ICC to sRGB conversion, built on SkiaSharp. |
| `OpenSlideSharp.runtime` | Native OpenSlide libraries from [openslide-bin](https://github.com/openslide/openslide-bin) for `win-x64`, `linux-x64`, `linux-arm64`, `osx-arm64` and `osx-x64`. Also works with .NET Framework (64-bit process) and Visual Studio C++ projects (x64). |
| `OpenSlideSharp.AspNetCore` | ASP.NET Core endpoints that serve slides as Deep Zoom and OME-Zarr, with a shared slide cache. Targets `net10.0`. |
| `OpenSlideSharp.Tool` | `openslide-sharp`, a .NET tool that exports slides as Deep Zoom tiles or OME-Zarr. Includes the native libraries. |

## Versioning

Package versions map to the openslide-bin builds listed on <https://openslide.org/builds/>:

| openslide-bin build | Package version |
|---|---|
| Release `4.0.1.2` | `4.0.1.2` |
| Nightly `4.0.1.2+20261102.nightly` (OpenSlide `g174630bf`) | `4.0.1.3-nightly.20261102.g174630bf` |

A nightly version increments the fourth number, then carries the build date and the OpenSlide commit hash. It is a prerelease (`dotnet add package OpenSlideSharp --prerelease`) that sorts above the openslide-bin release it builds on. A release that changes only OpenSlideSharp, on the same openslide-bin build, appends `.1`, `.2`, … to the version, such as `4.0.1.3-nightly.20261102.g174630bf.1`. When that build is an openslide-bin release, such a version takes the nightly form, with the release's date and OpenSlide commit.

Releases are made by GitHub Actions:

- Every day, the Upstream sync workflow looks for a newer openslide-bin release or nightly build. It pins the next one in `eng/openslide.json`, regenerates the bindings, runs CI, fast-forwards `master` and releases that commit. It takes one build per run, a release before a nightly. A build that fails before the fast-forward, in CI for example, changes nothing. A failure at any step gets one issue titled `Upstream sync failed: openslide-bin <tag>`, which says whether `master` was fast-forwarded already; if it was, only the release is left, and "Re-run failed jobs" on the run finishes it. If `master` moved during the run, the update comes as a pull request instead.
- A push to `master` that changes `src/**`, `eng/openslide.json` or `Directory.*.props` releases that commit.
- Other changes, such as to the viewer sample or the `eng/` scripts, are released by running the Release workflow by hand.

A release tests the commit on all platforms, pushes the five packages to nuget.org, tags the commit `v<version>` and creates a GitHub release with the packages, the viewer archives and the openslide-bin source tarball. Without the `NUGET_API_KEY` secret it is a dry run: it builds and checks everything, publishes nothing and lists what it would release in the run summary. Only the release is a dry run then: the daily sync still commits each newer build (as IOL0ol1) and fast-forwards `master` to it.

## Install

```sh
dotnet add package OpenSlideSharp
dotnet add package OpenSlideSharp.SkiaSharp   # optional: JPEG/PNG encoding
dotnet add package OpenSlideSharp.runtime
```

To use your own OpenSlide 4.x build, leave out `OpenSlideSharp.runtime`. On .NET 10, point to the library with the `OPENSLIDE_LIBRARY_PATH` environment variable or `OpenSlideLibrary.SetLibraryPath`. The netstandard builds use the platform's default library search.

## Usage

```csharp
using OpenSlideSharp;

using var slide = OpenSlide.Open("slide.svs");

Console.WriteLine(slide.Properties[OpenSlideProperties.Vendor]);
Console.WriteLine($"{slide.Dimensions.Width} x {slide.Dimensions.Height}, {slide.LevelCount} levels");

// Read a 512 x 512 region from level 0. Pixels are BGRA, premultiplied alpha.
PixelImage region = slide.ReadRegion(x: 0, y: 0, level: 0, width: 512, height: 512);
```

Deep Zoom tiles match openslide-python's `DeepZoomGenerator`:

```csharp
var dz = new DeepZoomGenerator(slide, tileSize: 254, overlap: 1);
string dzi = dz.GetDzi("jpeg");
PixelImage tile = dz.GetTile(level: dz.LevelCount - 1, column: 0, row: 0);   // opaque BGRA
```

An OME-Zarr store (NGFF 0.5 by default, 0.4 with `Version = NgffVersion.V0_4`) is produced as key/content entries that you write to a directory, a zip or an object store:

```csharp
var zarr = new OmeZarrGenerator(slide, new OmeZarrOptions { ChunkSize = 512, Compression = OmeZarrCompression.Gzip });
foreach (string key in zarr.EnumerateKeys())
{
    zarr.TryGetEntry(key, out byte[]? content);
    string path = Path.Combine("slide.zarr", key);   // keys use '/'
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllBytes(path, content!);
}
```

A thumbnail that fits in a box:

```csharp
PixelImage thumbnail = slide.GetThumbnail(256, 256);
```

Encoding to JPEG or PNG needs the `OpenSlideSharp.SkiaSharp` package:

```csharp
using OpenSlideSharp.SkiaSharp;

byte[] jpeg = thumbnail.Encode(ImageFormat.Jpeg, quality: 85);
```

OpenSlide errors surface as `OpenSlideException`. They are sticky: after the first one, every later call on that slide throws.

### ASP.NET Core

With `OpenSlideSharp.AspNetCore` (plus `OpenSlideSharp.runtime`, or your own OpenSlide), an app serves slides to OpenSeadragon and OME-Zarr viewers:

```csharp
using OpenSlideSharp.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOpenSlide();

var app = builder.Build();
var slides = new DirectorySlideResolver("/data/slides");
app.MapDeepZoom("/dz", slides);    // /dz/{slide}.dzi, /dz/{slide}_files/{level}/{col}_{row}.jpeg
app.MapOmeZarr("/zarr", slides);   // /zarr/{slide}.zarr/{key}
app.Run();
```

Options, URLs and color handling are in [its README](src/OpenSlideSharp.AspNetCore/README.md).

### Command-line tool

```sh
dotnet tool install -g OpenSlideSharp.Tool
openslide-sharp deepzoom slide.svs        # slide.dzi and slide_files/, like deepzoom_tile.py
openslide-sharp deepzoom slide.svs -r     # slide/ with an HTML viewer
openslide-sharp omezarr slide.svs         # slide.zarr/
```

Options are in [its README](src/OpenSlideSharp.Tool/README.md).

## Web sample

[`samples/OpenSlideSharp.Web`](samples/OpenSlideSharp.Web/README.md) shows one slide, or a directory of slides, in OpenSeadragon, like openslide-python's `deepzoom_server.py` and `deepzoom_multiserver.py`, and serves each slide as OME-Zarr too. From a checkout:

```sh
pwsh eng/Get-OpenSlide.ps1
dotnet run -c Release --project samples/OpenSlideSharp.Web -- /path/to/slide.svs    # or a directory
```

Then open <http://127.0.0.1:5000/>. Give an absolute path: under `dotnet run` the working directory is the project directory.

## Desktop viewer

[`samples/OpenSlideSharp.Viewer`](samples/OpenSlideSharp.Viewer/README.md) is an Avalonia viewer for Windows, Linux and macOS, like openslide-java's demo viewer. From a checkout:

```sh
pwsh eng/Get-OpenSlide.ps1
dotnet run -c Release --project samples/OpenSlideSharp.Viewer -- /path/to/slide.svs    # or File → Open, or drop a file
```

Its README lists the shortcuts, the `--check` mode and the self-contained publish.

## Known issues

On Windows, corrupt JPEG or PNG data in a slide terminates the whole process with `0xC0000409`, and the failure cannot be caught. Services that open untrusted slides on Windows should decode them in a separate worker process. See [the details](docs/known-issues/windows-cfg-longjmp-crash.md).

## Building from source

Requires the .NET 10 SDK and PowerShell 7.

```sh
pwsh eng/Get-OpenSlide.ps1    # download the native OpenSlide pinned in eng/openslide.json
dotnet test OpenSlideSharp.slnx
```

## Old packages

These 1.x packages end at the version shown and are not updated anymore. Use the replacement instead.

| Package | Last version | Replacement |
|---|---|---|
| `OpenSlideSharp.Interop` | 1.0.445.17 | `OpenSlideSharp`; the generated bindings are in its `OpenSlideSharp.Native` namespace |
| `OpenSlideSharp.Windows` | 1.1.2 | `OpenSlideSharp` and `OpenSlideSharp.runtime` |
| `OpenSlideSharp.runtime.win` | 1.1.2 | `OpenSlideSharp.runtime` |
| `OpenSlideSharp.BitmapExtensions` | 1.1.2 | `OpenSlideSharp.SkiaSharp` |
| `OpenSlideSharp.OpenCVExtensions` | 1.1.2 | None; `PixelImage.Data` holds the BGRA pixels |
| `OpenSlideSharp.BruTile` | 1.1.2 | None |

`OpenSlideSharp` itself goes on from 1.1.2 to 4.x, with a new API.

## License

The managed code is MIT licensed; see [LICENSE](LICENSE). `OpenSlideSharp.runtime` redistributes OpenSlide (LGPL-2.1) built by openslide-bin, with the components linked into each platform's library under their own licenses; its `LICENSE.txt` explains the terms, and `licenses/<platform>/` holds the license texts. The corresponding source is the openslide-bin source tarball named in the package's `openslide-bin.json`; each [GitHub release](https://github.com/IOL0ol1/OpenSlideSharp/releases) attaches a copy for its version. `OpenSlideSharp.Tool` states its terms in its own `LICENSE.txt`, and the viewer in `licenses/` next to its executable.
