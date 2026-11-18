# OpenSlideSharp.AspNetCore

ASP.NET Core endpoints that serve [OpenSlide](https://openslide.org/) slides with [OpenSlideSharp](https://www.nuget.org/packages/OpenSlideSharp): Deep Zoom for OpenSeadragon, like openslide-python's `deepzoom_server.py`, and OME-Zarr for viewers such as vizarr, with a shared slide cache.

```csharp
using OpenSlideSharp.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOpenSlide(builder.Configuration.GetSection("OpenSlide"));
// or: builder.Services.AddOpenSlide(options => options.DeepZoom.TileSize = 510);

var app = builder.Build();
var slides = new DirectorySlideResolver("/data/slides");
app.MapDeepZoom("/dz", slides);
app.MapOmeZarr("/zarr", slides);
app.Run();
```

The native library is not included. Add the [OpenSlideSharp.runtime](https://www.nuget.org/packages/OpenSlideSharp.runtime) package, or provide your own OpenSlide 4.x build.

## Options

`AddOpenSlide` binds `OpenSlideServerOptions` and validates it when it is first resolved and when the host starts. The defaults match the openslide-python examples. Binding from `IConfiguration` uses reflection; trimmed and native AOT apps should set the options in code with `AddOpenSlide(options => ...)`.

| Option | Default | |
|---|---|---|
| `SlideCacheSize` | 10 | slides kept open, at least 1 |
| `TileCacheSize` | 128 | MiB of OpenSlide tile cache shared by all open slides, at least 0; 0 keeps OpenSlide's own cache of each slide (32 MiB each) |
| `ColorMode` | `Default` | see [Color](#color) |
| `DeepZoom:TileSize` | 254 | at least 1 |
| `DeepZoom:Overlap` | 1 | at least 0 |
| `DeepZoom:Format` | `Jpeg` | the format named in the DZI |
| `DeepZoom:Quality` | 75 | JPEG quality, 0 to 100 |
| `DeepZoom:LimitBounds` | `true` | only the non-empty slide region |
| `OmeZarr:Version` | `V0_5` | `V0_4` for NGFF 0.4 on Zarr v2 |
| `OmeZarr:ChunkSize` | 512 | 16 to 8192 |
| `OmeZarr:RenderCacheSize` | 16 | rendered chunk regions kept per image, so the three channels of a chunk are rendered once; at least 0 (off) |
| `OmeZarr:Compression` | `None` | or `Gzip` |
| `OmeZarr:GzipLevel` | 6 | 0 to 9 |
| `OmeZarr:LimitBounds` | `true` | only the non-empty slide region |
| `OmeZarr:AllowCrossOrigin` | `true` | `Access-Control-Allow-Origin: *`, see [Cross-origin reads](#cross-origin-reads) |

Associated images (label, macro, thumbnail) are always shown whole, whatever `LimitBounds` says.

Memory: each image served through OME-Zarr (the slide, and each associated image asked for) keeps a render cache of up to `RenderCacheSize × ChunkSize² × 4` bytes, 16 MiB with the defaults. With every open slide in use, that is up to `SlideCacheSize × images × RenderCacheSize × ChunkSize² × 4` bytes, where `images` is the number of images per slide served through OME-Zarr, plus the `TileCacheSize` MiB tile cache all slides share. Lower `RenderCacheSize` for large chunks or a large `SlideCacheSize`.

## Endpoints

`MapDeepZoom(prefix, resolver)` answers GET and HEAD:

| URL | Response |
|---|---|
| `{prefix}/{slide}.dzi` | the DZI document, `application/xml` |
| `{prefix}/{slide}_files/{level}/{col}_{row}.{jpeg\|png}` | a tile, `image/jpeg` or `image/png` |
| `{prefix}/{slide}/associated/{name}.dzi` and `{prefix}/{slide}/associated/{name}_files/...` | the same for an associated image |

The DZI names `DeepZoom:Format`; a tile URL may ask for either format.

`MapOmeZarr(prefix, resolver)` answers GET, HEAD and OPTIONS:

| URL | Response |
|---|---|
| `{prefix}/{slide}.zarr/{key}` | an entry of the store: metadata (`zarr.json`, or `.zgroup`, `.zattrs` and `.zarray` for NGFF 0.4) as `application/json`, chunks as `application/octet-stream` |
| `{prefix}/{slide}/associated/{name}.zarr/{key}` | the same for an associated image |

A viewer opens `{prefix}/{slide}.zarr` as the store. HEAD answers 200 and the Content-Type for an existing key without rendering the chunk. OPTIONS answers 204; for the CORS headers see [Cross-origin reads](#cross-origin-reads).

`{slide}` is the name the resolver knows (see [Resolvers](#resolvers)): the fixed name of a `SingleSlideResolver`, or the `/`-separated path below the root of a `DirectorySlideResolver`, such as `a/boxes.tiff`. `{name}` is an associated image name, such as `macro`, escaped as one URL segment (`Uri.EscapeDataString`). Associated images whose names contain `/`, and slides in a subdirectory named `associated` (such as `a/associated/x.svs`), are not addressable.

Status codes:

- 404: an unknown slide or associated image, a path outside the root, a file OpenSlide cannot open, a level, tile or chunk outside the pyramid, a tile format other than `jpeg` or `png`, and an unknown key, including probes such as `.zmetadata`.
- 500: the slide is in the OpenSlide error state. It is evicted from the cache, and the next request opens it again.

### Cross-origin reads

With `OmeZarr:AllowCrossOrigin` (the default), every OME-Zarr response, 404 and 500 included, carries `Access-Control-Allow-Origin: *`, and OPTIONS answers 204 with `Access-Control-Allow-Methods: GET, HEAD, OPTIONS`, `Access-Control-Max-Age: 86400` and, when the preflight has `Access-Control-Request-Headers`, `Access-Control-Allow-Headers` with the same value. A viewer on another origin, such as vizarr, can then read the stores.

- These headers override any CORS policy of the app on these URLs. To use ASP.NET Core's CORS middleware instead (only some origins, or credentials), set `AllowCrossOrigin` to `false`: the endpoint then writes no CORS headers, and OPTIONS answers a bare 204.
- `*` lets any website read every store that the visitor's browser can reach without credentials, including servers on an intranet or on `localhost`. That includes associated images: a label or macro image may show patient information.

Deep Zoom responses carry no CORS headers; OpenSeadragon on the same origin does not need them.

## Deployment

The endpoints have no access control of their own. For access control, add `.RequireAuthorization(...)` to the builders they return. (Browsers send CORS preflights without credentials, so cross-origin readers of protected stores need the app's own CORS policy rather than `AllowCrossOrigin`.)

Tiles and chunks are read through native OpenSlide calls that block a thread-pool thread for the whole read. For internet-facing deployments, limit concurrency with ASP.NET Core rate limiting:

```csharp
builder.Services.AddRateLimiter(o => o.AddConcurrencyLimiter("slides", l => { l.PermitLimit = 32; l.QueueLimit = 256; }));
// ...
app.UseRateLimiter();
app.MapDeepZoom("/dz", slides).RequireRateLimiting("slides");
app.MapOmeZarr("/zarr", slides).RequireRateLimiting("slides");
```

## Resolvers

An `ISlideResolver` turns the `{slide}` part of a URL into a file:

- `new SingleSlideResolver("slide.svs", name: "slide")` serves one file under a fixed name, like `deepzoom_server.py`.
- `new DirectorySlideResolver("/data/slides")` serves the files under a directory by their relative path, like `deepzoom_multiserver.py`. Only existing files inside the root resolve: `..`, `.`, empty segments, backslashes, colons, absolute paths and symbolic links leading outside the root are all rejected, and look the same as a missing file.

## Slide cache

`SlideCache` keeps the most recently used slides open. Each request takes a `SlideLease` and disposes it when done. A slide is opened once however many requests ask for it at the same time; a file that cannot be opened is not remembered, and never takes the place of a cached slide (a slide counts towards `SlideCacheSize` only once it is open). A slide evicted while requests still use it is closed when the last of them finishes. `Evict` drops a slide that entered the OpenSlide error state, so the next request opens it again. All open slides share one OpenSlide tile cache of `TileCacheSize` MiB.

The cache is keyed by the full path of the file, without case on Windows and with case on Linux. macOS is case-sensitive here too, but its file systems (APFS, by default) are not: `A.svs` and `a.svs` there name one file, which may then be open twice, once under each name.

`AddOpenSlide` registers the cache. Constructed directly, `new SlideCache(options, logger)` validates the options like `AddOpenSlide` does (throwing `OptionsValidationException`) and copies them, so later changes to the options object have no effect. `MapDeepZoom` and `MapOmeZarr` throw `InvalidOperationException` when `AddOpenSlide` was not called.

Within a cached slide, each image (the slide and each associated image) gets its color transform and its Deep Zoom and OME-Zarr generators once, and keeps them while the slide stays open.

## Color

`ColorMode` applies to each image with that image's own ICC profile:

| `ColorMode` | Deep Zoom tiles | OME-Zarr chunks |
|---|---|---|
| `Default` | converted to sRGB; if the profile cannot be converted, unconverted with the profile embedded | converted to sRGB; if the profile cannot be converted, unconverted |
| `Embed` | unconverted, profile embedded | unconverted (OME-Zarr cannot hold a profile) |
| `Ignore` | unconverted, no profile | unconverted |

Images without a profile are served the same way in every mode.

## Logging

| Category | Level | When |
|---|---|---|
| `OpenSlideSharp.AspNetCore.SlideCache` | Warning | an image's profile cannot be converted (`Default`), or `Embed` meets OME-Zarr; once per image while its slide stays open |
| `OpenSlideSharp.AspNetCore.SlideCache` | Debug | a file cannot be opened as a slide (the request gets a 404) |
| `OpenSlideSharp.AspNetCore.DeepZoom`, `OpenSlideSharp.AspNetCore.OmeZarr` | Error | a request found the slide in the OpenSlide error state (500, slide evicted) |

Requests that succeed are not logged; use ASP.NET Core's request logging for that.

## Known issues

On Windows, corrupt JPEG or PNG data in a slide terminates the whole process with `0xC0000409`, and the failure cannot be caught. Services that open untrusted slides on Windows should decode them in a separate worker process. See [the details](https://github.com/IOL0ol1/OpenSlideSharp/blob/master/docs/known-issues/windows-cfg-longjmp-crash.md).
