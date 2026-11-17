# OpenSlideSharp

.NET bindings for [OpenSlide](https://openslide.org/) 4.x, a library for reading whole-slide images, with Deep Zoom and OME-Zarr generators.

```csharp
using OpenSlideSharp;

using var slide = OpenSlide.Open("slide.svs");
Console.WriteLine(slide.Properties[OpenSlideProperties.Vendor]);
Console.WriteLine($"{slide.Dimensions.Width}x{slide.Dimensions.Height}");
```

The native library is not included. Add the [OpenSlideSharp.runtime](https://www.nuget.org/packages/OpenSlideSharp.runtime) package (win-x64, linux-x64, linux-arm64, osx-arm64, osx-x64), or provide your own OpenSlide 4.x build.

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

JPEG and PNG encoding is in the [OpenSlideSharp.SkiaSharp](https://www.nuget.org/packages/OpenSlideSharp.SkiaSharp) package.

## Known issues

On Windows, corrupt JPEG or PNG data in a slide terminates the whole process with `0xC0000409`, and the failure cannot be caught. The cause is in the Windows build of the native OpenSlide library, and no workaround is shipped. Services that open untrusted slides on Windows should decode them in a separate worker process. See [the details](https://github.com/IOL0ol1/OpenSlideSharp/blob/master/docs/known-issues/windows-cfg-longjmp-crash.md).
