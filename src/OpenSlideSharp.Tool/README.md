# OpenSlideSharp.Tool

`openslide-sharp`, a .NET tool that exports [OpenSlide](https://openslide.org/) slides as Deep Zoom tiles (like openslide-python's `deepzoom_tile.py`) or as an OME-Zarr store. The native OpenSlide library for Windows x64, Linux x64/arm64 and macOS x64/arm64 is included.

```sh
dotnet tool install -g OpenSlideSharp.Tool
openslide-sharp deepzoom slide.svs            # slide.dzi and slide_files/
openslide-sharp deepzoom slide.svs -r         # slide/ with an HTML viewer
openslide-sharp omezarr slide.svs             # slide.zarr/
```

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
| `-j`, `--jobs <count>` | processor count | worker threads |
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
| `-j`, `--jobs <count>` | processor count | worker threads |
| `--associated` | off | also export the associated images |

Every key of the store is written as a file, with `/` in the key as directories. With `--associated`, each associated image becomes a store of its own, `<output>/../<slide name>_associated/<slug>.zarr`.

## Behavior

- Outputs are relative to the current directory unless a path is given.
- Existing output is replaced: `<name>.dzi` and `<name>_files/`, the whole `<name>/` directory with `-r`, or the `.zarr` directory (and the `_associated` directory with `--associated`) are deleted first. A directory is only deleted if everything at its top is something the tool writes (level directories, `.dzi` files, `_files/` directories, `index.html`, `static/`, Zarr metadata, `.zarr` stores), so an `-o` that names another directory, or a slide's own data directory (MIRAX keeps `name.mrxs` next to `name/`), fails with an error instead. Root directories, the current directory and its ancestors, and directories holding the slide are never deleted.
- Every file is written to `.<file>.tmp` next to it and then moved into place, so no file is ever partially written. The `.dzi` and the OME-Zarr metadata are written after the tiles, so an interrupted export is not mistaken for a complete one.
- Progress goes to stderr, at most once a second: `Tiling <image>: <done>/<total> tiles`.
- Ctrl+C stops the export once the files being written are complete.
- Exit codes: 0 on success, 1 if the slide cannot be opened or the export fails, 2 for invalid arguments, 130 when canceled.

## Color

Each image (the slide, and each associated image) is handled with its own ICC profile; an image without one is the same in every mode.

- `default`: tiles and chunks are converted to sRGB. If the profile cannot be converted, a warning is written once for the image, Deep Zoom tiles carry the profile instead (unless it is too large for a JPEG), and OME-Zarr chunks are written unconverted.
- `embed` (Deep Zoom only): pixels are left as they are and tiles carry the profile.
- `ignore`: pixels are left as they are and the profile is dropped.
