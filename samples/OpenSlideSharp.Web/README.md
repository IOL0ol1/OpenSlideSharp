# OpenSlideSharp.Web

A sample web viewer for whole-slide images, built on `OpenSlideSharp.AspNetCore`. It merges openslide-python's `deepzoom_server.py` (one slide) and `deepzoom_multiserver.py` (a directory of slides) into one app.

```
dotnet run --project samples/OpenSlideSharp.Web -- path/to/slide.svs      # single slide
dotnet run --project samples/OpenSlideSharp.Web -- path/to/slides/        # directory of slides
```

Then open http://127.0.0.1:5000/.

## Pages and endpoints

| URL | What |
|---|---|
| `/` | Single-slide mode: the viewer. Directory mode: a tree of the slides OpenSlide recognizes (listing cached for 60 seconds). |
| `/slide/{path}` | The viewer for one slide: associated images on the left, OpenSeadragon with a scale bar (from `openslide.mpp-x`/`mpp-y`) in the middle, properties on the right. The OME-Zarr address and an "Open in vizarr" link are at the bottom. |
| `/dz/{slide}.dzi`, `/dz/{slide}/associated/{name}.dzi` | Deep Zoom (`MapDeepZoom`). |
| `/zarr/{slide}.zarr/`, `/zarr/{slide}/associated/{name}.zarr/` | OME-Zarr (`MapOmeZarr`), readable from other origins. |

In single-slide mode the slide is named `slide`, so the Deep Zoom image is `/dz/slide.dzi`. In directory mode the name is the path relative to the directory, for example `/dz/a/boxes.tiff.dzi`.

"Open in vizarr" opens `https://hms-dbmi.github.io/vizarr/?source=<OME-Zarr address>`. The address is built from the request, so vizarr (which runs in your browser) can read it when the browser can reach the server, and `localhost` works.

## Options

The first positional argument is `SlidePath`: a file means single-slide mode, a directory directory mode. The other options mirror the Python scripts and set configuration values:

| Option | Configuration key |
|---|---|
| `-s`, `--size` | `OpenSlide:DeepZoom:TileSize` (default 254) |
| `-e`, `--overlap` | `OpenSlide:DeepZoom:Overlap` (default 1) |
| `-f`, `--format` | `OpenSlide:DeepZoom:Format`, `jpeg` or `png` |
| `-Q`, `--quality` | `OpenSlide:DeepZoom:Quality` (default 75) |
| `--color-mode` | `OpenSlide:ColorMode`, `default`, `embed` or `ignore` |
| `-l`, `--listen` | host of `urls` (default `127.0.0.1`) |
| `-p`, `--port` | port of `urls` (default `5000`) |
| `-B`, `--ignore-bounds` | sets `OpenSlide:DeepZoom:LimitBounds` and `OpenSlide:OmeZarr:LimitBounds` to `false` |

Any other configuration value can be set as `--Key=Value` (for example `--OpenSlide:OmeZarr:Compression=Gzip`), in `appsettings.json` or through environment variables (`OpenSlide__SlideCacheSize=20`).

## Viewer assets

The viewer uses OpenSeadragon and its scale bar plugin, served from `/lib` without any CDN. The files are vendored in `assets/viewer/` at the repository root (see its README for sources, licenses and checksums) and copied to `wwwroot/lib` by the build.

The page scripts are in `wwwroot/viewer/` and use plain DOM APIs, with no jQuery.

## Notes

- Do not expose this sample to untrusted networks as it is: it has no authentication, and directory mode serves every slide below the directory.
- OpenSlide itself comes from `artifacts/native` through `eng/OpenSlideNative.targets`; run `eng/Get-OpenSlide.ps1` first in a fresh checkout.
