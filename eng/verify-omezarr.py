"""Verify OME-Zarr stores with zarr-python and ome-zarr-py.

Usage:
  python eng/verify-omezarr.py <dir>
      The stores that OmeZarrInteropExport lists in <dir>/manifest.json. Every level must equal the expected
      pixels in <dir>/expected/.
  python eng/verify-omezarr.py --stores <store> [<store> ...] [--manifest <dir>]
  python eng/verify-omezarr.py --url <url> [--manifest <dir>]
      OME-Zarr stores in directories, or one served over HTTP (read through fsspec). Every level is read in full
      and must be uint8 with 3 channels (c, y, x). The levels must halve until one fits in a chunk, as
      OmeZarrGenerator makes them, and ome-zarr must find the same levels. With --manifest, the levels must also
      equal the first levels of <dir>/manifest.json and <dir>/expected/, shapes and pixels. A store with larger
      chunks has fewer levels. Its level 0 is read straight from the slide, but its lower levels are scaled region
      by region, so they are only sure to match when its chunks are the manifest's 64 pixels.
"""
import argparse
import json
import os
import sys
from pathlib import Path

import numpy as np
import zarr
from ome_zarr.io import parse_url
from ome_zarr.reader import Reader


def load_manifest(root):
    """Returns the store names in root/manifest.json and the expected pixels of each level, as c, y, x uint8."""
    path = os.path.join(root, "manifest.json")
    with open(path, encoding="utf-8") as f:
        manifest = json.load(f)
    if not manifest.get("stores"):
        raise AssertionError(f"{path} lists no stores")
    expected = []
    for i, shape in enumerate(tuple(s) for s in manifest["levels"]):
        raw = np.fromfile(os.path.join(root, "expected", f"{i}.rgb"), dtype=np.uint8)
        if raw.size != int(np.prod(shape)):
            raise AssertionError(f"expected/{i}.rgb has {raw.size} bytes, manifest says {shape}")
        expected.append(raw.reshape(shape))
    return manifest["stores"], expected


def check_reader(location, name, shapes):
    """ome-zarr must find an image at location (a path or URL) with exactly these level shapes."""
    zarr_location = parse_url(location)
    if zarr_location is None:
        raise AssertionError(f"{name}: ome-zarr cannot open it")
    nodes = list(Reader(zarr_location)())
    if not nodes:
        raise AssertionError(f"{name}: ome-zarr found no nodes")
    data = nodes[0].data
    if len(data) != len(shapes):
        raise AssertionError(f"{name}: ome-zarr reports {len(data)} levels, expected {len(shapes)}")
    for i, shape in enumerate(shapes):
        if tuple(data[i].shape) != shape:
            raise AssertionError(f"{name}: ome-zarr level {i} shape {tuple(data[i].shape)}, expected {shape}")


def verify_manifest(root):
    stores, expected = load_manifest(root)
    levels = [e.shape for e in expected]
    for name in stores:
        store = os.path.join(root, name)
        group = zarr.open_group(store, mode="r")
        for i, shape in enumerate(levels):
            arr = np.asarray(group[str(i)])
            if arr.shape != shape or arr.dtype != np.uint8:
                raise AssertionError(f"{name} level {i}: shape/dtype {arr.shape}/{arr.dtype}, expected {shape}/uint8")
            if not np.array_equal(arr, expected[i]):
                diff = int(np.count_nonzero(arr != expected[i]))
                raise AssertionError(f"{name} level {i}: {diff} pixel values differ from expected")

        check_reader(store, name, levels)
        print(f"verified {name}")

    print(f"OK: {len(stores)} stores verified")


def multiscale(group, name):
    """The first multiscales entry: under "ome" for NGFF 0.5 (Zarr v3), at the top for NGFF 0.4 (Zarr v2)."""
    attrs = group.attrs.asdict()
    if group.metadata.zarr_format == 3:
        ome = attrs.get("ome", {})
        version, multiscales, expected_version = ome.get("version"), ome.get("multiscales"), "0.5"
    else:
        multiscales = attrs.get("multiscales")
        version, expected_version = (multiscales[0].get("version") if multiscales else None), "0.4"
    if not multiscales:
        raise AssertionError(f"{name}: no multiscales metadata")
    if version != expected_version:
        raise AssertionError(f"{name}: NGFF version {version!r} on Zarr v{group.metadata.zarr_format}, expected {expected_version}")
    return multiscales[0]


def verify_store(location, name, expected):
    """Checks one store (a Path or a URL) against the expected levels, if any, and returns its level shapes."""
    group = zarr.open_group(location, mode="r")
    ms = multiscale(group, name)
    axes = [axis["name"] for axis in ms["axes"]]
    if axes != ["c", "y", "x"]:
        raise AssertionError(f"{name}: axes {axes}, expected ['c', 'y', 'x']")
    paths = [dataset["path"] for dataset in ms["datasets"]]
    if not paths:
        raise AssertionError(f"{name}: multiscales lists no datasets")

    shapes = []
    for i, path in enumerate(paths):
        arr = group[path]
        shape = tuple(arr.shape)
        if arr.dtype != np.uint8 or len(shape) != 3 or shape[0] != 3:
            raise AssertionError(f"{name} level {i}: shape/dtype {shape}/{arr.dtype}, expected 3 channels (c, y, x) of uint8")
        if shapes:
            _, h, w = shapes[-1]
            halved = (3, (h + 1) // 2, (w + 1) // 2)
            if shape != halved:
                raise AssertionError(f"{name} level {i}: shape {shape}, expected {halved} (half of level {i - 1})")
        data = arr[...]  # reads and decodes every chunk
        if data.shape != shape:
            raise AssertionError(f"{name} level {i}: read {data.shape}, expected {shape}")
        if expected is not None:
            if i >= len(expected) or shape != expected[i].shape:
                raise AssertionError(f"{name} level {i}: shape {shape}, the manifest has {expected[i].shape if i < len(expected) else 'no such level'}")
            if not np.array_equal(data, expected[i]):
                diff = int(np.count_nonzero(data != expected[i]))
                raise AssertionError(f"{name} level {i}: {diff} pixel values differ from expected")

        # The pyramid ends with the first level that fits in one chunk.
        _, chunk_h, chunk_w = arr.chunks
        fits = shape[1] <= chunk_h and shape[2] <= chunk_w
        last = i == len(paths) - 1
        if fits != last:
            raise AssertionError(
                f"{name} level {i}: {shape[1]} x {shape[2]} {'fits' if fits else 'does not fit'} in one "
                f"{chunk_h} x {chunk_w} chunk, but it is {'the last' if last else 'not the last'} of {len(paths)} levels")
        shapes.append(shape)

    check_reader(location, name, shapes)
    print(f"verified {name}: {len(shapes)} level{'' if len(shapes) == 1 else 's'}, level 0 is {shapes[0][2]} x {shapes[0][1]}")
    return shapes


def main(argv):
    parser = argparse.ArgumentParser(
        description="Verify OME-Zarr stores with zarr-python and ome-zarr-py.",
        usage="%(prog)s DIR | --stores STORE [STORE ...] [--manifest DIR] | --url URL [--manifest DIR]")
    parser.add_argument("dir", nargs="?", help="directory with manifest.json, written by OmeZarrInteropExport")
    parser.add_argument("--stores", nargs="+", metavar="STORE", help="OME-Zarr store directories")
    parser.add_argument("--url", help="URL of an OME-Zarr store served over HTTP")
    parser.add_argument("--manifest", metavar="DIR", help="compare the levels with DIR/manifest.json and DIR/expected/")
    args = parser.parse_args(argv)
    if sum(x is not None for x in (args.dir, args.stores, args.url)) != 1:
        parser.error("give exactly one of DIR, --stores or --url")
    if args.dir is not None and args.manifest is not None:
        parser.error("--manifest goes with --stores or --url")

    try:
        if args.dir is not None:
            verify_manifest(args.dir)
            return
        expected = load_manifest(args.manifest)[1] if args.manifest is not None else None
        if args.url is not None:
            verify_store(args.url, args.url, expected)
            print("OK: 1 store verified")
        else:
            for store in args.stores:
                verify_store(Path(store), store, expected)
            print(f"OK: {len(args.stores)} store{'' if len(args.stores) == 1 else 's'} verified")
    except Exception as e:
        print(f"FAIL: {e}", file=sys.stderr)
        sys.exit(1)


if __name__ == "__main__":
    main(sys.argv[1:])
