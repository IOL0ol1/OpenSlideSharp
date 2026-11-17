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

## Versioning

Package versions map to the openslide-bin builds listed on <https://openslide.org/builds/>:

| openslide-bin build | Package version |
|---|---|
| Release `4.0.1.2` | `4.0.1.2` |
| Nightly `4.0.1.2+20261102.nightly` (OpenSlide `g174630bf`) | `4.0.1.3-nightly.20261102.g174630bf` |

A nightly version increments the fourth number, then carries the build date and the OpenSlide commit hash.

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

## Known issues

On Windows, corrupt JPEG or PNG data in a slide terminates the whole process with `0xC0000409`, and the failure cannot be caught. Services that open untrusted slides on Windows should decode them in a separate worker process. See [the details](docs/known-issues/windows-cfg-longjmp-crash.md).

## Building from source

Requires the .NET 10 SDK and PowerShell 7.

```sh
pwsh eng/Get-OpenSlide.ps1    # download the native OpenSlide pinned in eng/openslide.json
dotnet test OpenSlideSharp.slnx
```

## License

The managed code is MIT licensed; see [LICENSE](LICENSE). `OpenSlideSharp.runtime` redistributes OpenSlide and its dependencies under LGPL-2.1-only.
