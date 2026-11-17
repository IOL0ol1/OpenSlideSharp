# OpenSlideSharp.AspNetCore

ASP.NET Core support for serving [OpenSlide](https://openslide.org/) slides with [OpenSlideSharp](https://www.nuget.org/packages/OpenSlideSharp): options, slide resolvers and a shared slide cache, for Deep Zoom and OME-Zarr endpoints.

```csharp
using OpenSlideSharp.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOpenSlide(builder.Configuration.GetSection("OpenSlide"));
// or: builder.Services.AddOpenSlide(options => options.DeepZoom.TileSize = 510);
```

The native library is not included. Add the [OpenSlideSharp.runtime](https://www.nuget.org/packages/OpenSlideSharp.runtime) package, or provide your own OpenSlide 4.x build.

## Options

`AddOpenSlide` binds `OpenSlideServerOptions` and validates it when it is first resolved and when the host starts. The defaults match the openslide-python examples.

| Option | Default | |
|---|---|---|
| `SlideCacheSize` | 10 | slides kept open, at least 1 |
| `ColorMode` | `Default` | see [Color](#color) |
| `DeepZoom:TileSize` | 254 | at least 1 |
| `DeepZoom:Overlap` | 1 | at least 0 |
| `DeepZoom:Format` | `Jpeg` | the format named in the DZI |
| `DeepZoom:Quality` | 75 | JPEG quality, 0 to 100 |
| `DeepZoom:LimitBounds` | `true` | only the non-empty slide region |
| `OmeZarr:Version` | `V0_5` | `V0_4` for NGFF 0.4 on Zarr v2 |
| `OmeZarr:ChunkSize` | 512 | 16 to 8192 |
| `OmeZarr:Compression` | `None` | or `Gzip` |
| `OmeZarr:GzipLevel` | 6 | 0 to 9 |
| `OmeZarr:LimitBounds` | `true` | only the non-empty slide region |
| `OmeZarr:AllowCrossOrigin` | `true` | `Access-Control-Allow-Origin: *` |

Associated images (label, macro, thumbnail) are always shown whole, whatever `LimitBounds` says.

## Resolvers

An `ISlideResolver` turns the `{slide}` part of a URL into a file:

- `new SingleSlideResolver("slide.svs", name: "slide")` serves one file under a fixed name, like `deepzoom_server.py`.
- `new DirectorySlideResolver("/data/slides")` serves the files under a directory by their relative path, like `deepzoom_multiserver.py`. Only existing files inside the root resolve: `..`, `.`, empty segments, backslashes, colons, absolute paths and symbolic links leading outside the root are all rejected, and look the same as a missing file.

## Slide cache

`SlideCache` keeps the most recently used slides open. Each request takes a `SlideLease` and disposes it when done. A slide is opened once however many requests ask for it at the same time; a file that cannot be opened is not remembered. A slide evicted while requests still use it is closed when the last of them finishes. `Evict` drops a slide that entered the OpenSlide error state, so the next request opens it again.

Within a cached slide, each image (the slide and each associated image) gets its color transform and its Deep Zoom and OME-Zarr generators once, and keeps them while the slide stays open.

## Color

`ColorMode` applies to each image with that image's own ICC profile:

| `ColorMode` | Deep Zoom tiles | OME-Zarr chunks |
|---|---|---|
| `Default` | converted to sRGB; if the profile cannot be converted, unconverted with the profile embedded | converted to sRGB; if the profile cannot be converted, unconverted |
| `Embed` | unconverted, profile embedded | unconverted (OME-Zarr cannot hold a profile) |
| `Ignore` | unconverted, no profile | unconverted |

Images without a profile are served the same way in every mode. A warning is logged once per image when a profile cannot be converted, and once per image when `Embed` meets OME-Zarr. Slides that cannot be opened are logged at debug level.

## Known issues

On Windows, corrupt JPEG or PNG data in a slide terminates the whole process with `0xC0000409`, and the failure cannot be caught. Services that open untrusted slides on Windows should decode them in a separate worker process. See [the details](https://github.com/IOL0ol1/OpenSlideSharp/blob/master/docs/known-issues/windows-cfg-longjmp-crash.md).
