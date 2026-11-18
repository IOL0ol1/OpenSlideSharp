# OpenSlideSharp.Web

A sample web viewer for whole-slide images, built on `OpenSlideSharp.AspNetCore`. It merges openslide-python's `deepzoom_server.py` (one slide) and `deepzoom_multiserver.py` (a directory of slides) into one app.

```
dotnet run -c Release --project samples/OpenSlideSharp.Web -- /data/slide.svs            # single slide
dotnet run -c Release --project samples/OpenSlideSharp.Web -- /data/slides/ -p 8080     # directory of slides, port 8080
```

Under `dotnet run` the working directory is the project directory, so give the path as an absolute one. Then open http://127.0.0.1:5000/ (or the port given with `-p`).

The app listens on `http://127.0.0.1:5000` only when nothing else sets an address: `-l`/`-p`, `--urls`, `ASPNETCORE_URLS`, `ASPNETCORE_HTTP_PORTS` (which the official container images set) and Kestrel endpoints in the configuration all take its place. Invalid arguments or options (for example `-s 0`) print the problem and the usage, and the app exits with code 2.

## Pages and endpoints

| URL | What |
|---|---|
| `/` | Single-slide mode: the viewer. Directory mode: a tree of the slides OpenSlide recognizes (listing cached for 60 seconds). |
| `/slide/{path}` | The viewer for one slide: associated images on the left, OpenSeadragon with a scale bar (from `openslide.mpp-x`/`mpp-y`) in the middle, properties on the right. The OME-Zarr address and an "Open in vizarr" link are at the bottom. |
| `/dz/{slide}.dzi`, `/dz/{slide}_files/{level}/{col}_{row}.jpeg` | Deep Zoom (`MapDeepZoom`). |
| `/dz/{slide}/associated/{name}.dzi` | Deep Zoom of an associated image, such as `macro`. |
| `/zarr/{slide}.zarr/{key}`, `/zarr/{slide}/associated/{name}.zarr/{key}` | OME-Zarr (`MapOmeZarr`): open `/zarr/{slide}.zarr` as the store. Readable from other origins. |

In single-slide mode the slide is named `slide`, so the Deep Zoom image is `/dz/slide.dzi` and the OME-Zarr store `/zarr/slide.zarr`. In directory mode the name is the path relative to the directory, for example `/dz/a/boxes.tiff.dzi`. Status codes, content types and CORS are those of [OpenSlideSharp.AspNetCore](../../src/OpenSlideSharp.AspNetCore/README.md#endpoints).

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

## Color

`--color-mode` applies to each image (the slide and each associated image) with its own ICC profile:

- `default`: tiles and chunks are converted to sRGB. If a profile cannot be converted, Deep Zoom tiles carry it instead and OME-Zarr chunks stay unconverted, with a warning in the log.
- `embed`: pixels are left as they are and Deep Zoom tiles carry the profile. OME-Zarr cannot hold one, so its chunks are unconverted, with a warning.
- `ignore`: pixels are left as they are and the profile is dropped.

Images without a profile look the same in every mode.

## Logging

The log goes to the console. `appsettings.json` sets `Information` for the app and `Warning` for `Microsoft.AspNetCore`, so single requests are not logged. OpenSlideSharp.AspNetCore logs a warning once per image when a profile cannot be used as asked (see [Color](#color)), and an error when a slide enters the OpenSlide error state (the request gets a 500 and the slide is opened again on the next one). Files that OpenSlide cannot open are logged at `Debug`: `--Logging:LogLevel:OpenSlideSharp=Debug` shows them.

## Viewer assets

The viewer uses OpenSeadragon and its scale bar plugin, served from `/lib` without any CDN. The files are vendored in `assets/viewer/` at the repository root (see its README for sources, licenses and checksums) and copied to `wwwroot/lib` by the build.

The page scripts are in `wwwroot/viewer/` and use plain DOM APIs, with no jQuery.

## Notes

- Do not expose this sample to untrusted networks as it is: it has no authentication, and directory mode serves every slide below the directory.
- OpenSlide itself comes from `artifacts/native` through `eng/OpenSlideNative.targets`; run `eng/Get-OpenSlide.ps1` first in a fresh checkout.
