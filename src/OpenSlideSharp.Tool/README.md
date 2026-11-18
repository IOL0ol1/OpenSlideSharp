# OpenSlideSharp.Tool

`openslide-sharp`, a .NET tool that exports [OpenSlide](https://openslide.org/) slides as Deep Zoom tiles (like openslide-python's `deepzoom_tile.py`) or as an OME-Zarr store. The native OpenSlide library for Windows x64, Linux x64/arm64 and macOS x64/arm64 is included.

```sh
dotnet tool install -g OpenSlideSharp.Tool
openslide-sharp deepzoom slide.svs            # slide.dzi and slide_files/
openslide-sharp deepzoom slide.svs -r         # slide/ with an HTML viewer
openslide-sharp omezarr slide.svs             # slide.zarr/
```

The package is about 37 MB (85 MB installed), as it holds the OpenSlide and SkiaSharp libraries of every platform OpenSlide supports. On Windows, a native library cannot be loaded from a path longer than 260 characters (MAX_PATH), and the libraries sit up to about 180 characters below the tool path. Keep a `--tool-path` short; the `-g` location (`%USERPROFILE%\.dotnet\tools`) is fine.

## deepzoom

| Option | Default | |
|---|---|---|
| `-o`, `--output <name>` | slide file name without extension | output base name |
| `-s`, `--size <pixels>` | 254 | tile size |
| `-e`, `--overlap <pixels>` | 1 | overlap of adjacent tiles |
| `-f`, `--format jpeg\|png` | `jpeg` | tile format |
| `-Q`, `--quality <quality>` | 90 | JPEG quality, 0 to 100 |
| `-B`, `--ignore-bounds` | off | export the entire scan area |
| `-c`, `--color-mode default\|embed\|ignore` | `default` | see [Color](#color) |
| `-j`, `--jobs <count>` | processor count | worker threads, at most 4 × the processor count |
| `-r`, `--viewer` | off | write a directory with an HTML viewer |

Without `-r` the output is `<name>.dzi` and `<name>_files/{level}/{col}_{row}.{jpeg|png}`. With `-r` it is a directory `<name>/` holding `slide.dzi` and `slide_files/`, the same for each associated image under a file-safe name (`<slug>.dzi`, `<slug>_files/`, the slug made as `deepzoom_tile.py` does), `index.html` and `static/` (OpenSeadragon, its scale bar plugin, their images and licenses). The page embeds the DZI documents, so it also works when opened from disk.

## omezarr

| Option | Default | |
|---|---|---|
| `-o`, `--output <dir>` | `<slide name>.zarr` | output directory |
| `--ngff 0.5\|0.4` | `0.5` | NGFF 0.5 on Zarr v3, or 0.4 on Zarr v2 |
| `--chunk <pixels>` | 512 | chunk size, 16 to 8192 |
| `--compression none\|gzip` | `none` | chunk compression |
| `--gzip-level <level>` | 6 | 0 to 9 |
| `-B`, `--ignore-bounds` | off | export the entire scan area |
| `-c`, `--color-mode default\|ignore` | `default` | see [Color](#color) |
| `-j`, `--jobs <count>` | processor count | worker threads, at most 4 × the processor count |
| `--associated` | off | also export the associated images |

Every key of the store is written as a file, with `/` in the key as directories. With `--associated`, each associated image becomes a store of its own, `<output>/../<slide name>_associated/<slug>.zarr`; an output that is that `_associated` directory itself is refused (exit code 1).

## Behavior

- Outputs are relative to the current directory unless a path is given.
- Every output directory (`<name>_files/`, the `-r` directory `<name>/`, each `.zarr` store, the `_associated` directory) gets an empty `.openslide-sharp-output` marker file before anything else. Existing output is replaced, but an existing output directory only if it holds that marker or is empty. Any other existing directory, or a file where an output directory would go (such as an extensionless slide with `-r`), stops the export with exit code 1 and nothing is deleted. Root directories, the current directory and its ancestors, directories holding the slide, and a MIRAX slide's data directory (`name/` next to `name.mrxs`) are never deleted.
- An output directory being replaced is first renamed to `.<name>.openslide-sharp-old-<id>` next to it, in one step; then the old `<name>.dzi` is deleted, before any new file is written, and the renamed directory is deleted. If the rename fails (on Windows, while a program has a file in it open), the export stops with exit code 1 and nothing has changed. If only the delete fails, a warning names what is left and the export goes on; the next export to the same output deletes it (only while it still holds the marker, which is deleted last).
- Every file is written to `.<file>.tmp` next to it and then moved into place, so no file is ever partially written. The `.dzi` is written after the tiles, and the OME-Zarr root metadata (`zarr.json`, or `.zgroup` and `.zattrs`) after every chunk and level, so an interrupted export is not mistaken for a complete one.
- Progress goes to stderr, at most once a second: `Tiling <image>: <done>/<total> tiles`. Warnings (see [Color](#color)) and errors go to stderr too, prefixed with `openslide-sharp:`.
- Ctrl+C stops the export once the files being written are complete.
- Exit codes: 0 on success, 1 if the slide cannot be opened or the export fails, 2 for invalid arguments, 130 when canceled.
- `-j` above 4 × the processor count is lowered to that, with a note on stderr.

## Memory

Each worker holds one tile or chunk at a time, and OpenSlide keeps a tile cache of 32 MiB for the slide. On top of that:

- `deepzoom`: about `jobs × 5 × (size + 2 × overlap)² × 4` bytes at most, for the region each worker reads, the tile it scales it to, and the encoded file (about 1.3 MiB per worker with the defaults).
- `omezarr`: about `(cache + jobs) × chunk² × 4` bytes, where `cache = min(max(2 × jobs, 4), max(jobs, 1 GiB / (chunk² × 4)))` regions are kept so that the three channels of a chunk are rendered once. With the default 512-pixel chunks that is about `3 × jobs` MiB; the cache never holds more than 1 GiB, unless the jobs alone need more (one region each, for example 256 MiB per job with `--chunk 8192`).

## Licenses

The tool's own code is under the MIT license. The package also contains, each under its own license, which are in the package's `licenses/` folder:

- OpenSlide 4.x from [openslide-bin](https://github.com/openslide/openslide-bin), LGPL-2.1, with the libraries built into it (`licenses/openslide-bin/`, and `VERSIONS.md` listing their versions). The source is the openslide-bin source tarball of the same version.
- [SkiaSharp](https://github.com/mono/SkiaSharp), MIT, with Skia (BSD-3-Clause) and the libraries in its native build (`licenses/skiasharp/`).
- [OpenSeadragon](https://openseadragon.github.io/), BSD-3-Clause, and its [scale bar plugin](https://github.com/usnistgov/OpenSeadragonScalebar), in the public domain (NIST), written with `-r` (`licenses/viewer/`).

The package license expression is `MIT AND LGPL-2.1-only AND BSD-3-Clause`.

## Color

Each image (the slide, and each associated image) is handled with its own ICC profile; an image without one is the same in every mode.

- `default`: tiles and chunks are converted to sRGB. If the profile cannot be converted, a warning is written once for the image, Deep Zoom tiles carry the profile instead (unless it is too large for a JPEG), and OME-Zarr chunks are written unconverted.
- `embed` (Deep Zoom only): pixels are left as they are and tiles carry the profile.
- `ignore`: pixels are left as they are and the profile is dropped.
