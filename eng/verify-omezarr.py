"""Verify OME-Zarr stores written by OmeZarrInteropExport with zarr-python and ome-zarr-py.

Usage: python eng/verify-omezarr.py <dir>
"""
import json
import os
import sys

import numpy as np
import zarr
from ome_zarr.io import parse_url
from ome_zarr.reader import Reader


def main(root):
    with open(os.path.join(root, "manifest.json"), encoding="utf-8") as f:
        manifest = json.load(f)
    levels = [tuple(s) for s in manifest["levels"]]
    expected = []
    for i, shape in enumerate(levels):
        raw = np.fromfile(os.path.join(root, "expected", f"{i}.rgb"), dtype=np.uint8)
        if raw.size != int(np.prod(shape)):
            raise AssertionError(f"expected/{i}.rgb has {raw.size} bytes, manifest says {shape}")
        expected.append(raw.reshape(shape))

    for name in manifest["stores"]:
        store = os.path.join(root, name)
        group = zarr.open_group(store, mode="r")
        for i, shape in enumerate(levels):
            arr = np.asarray(group[str(i)])
            if arr.shape != shape or arr.dtype != np.uint8:
                raise AssertionError(f"{name} level {i}: shape/dtype {arr.shape}/{arr.dtype}, expected {shape}/uint8")
            if not np.array_equal(arr, expected[i]):
                diff = int(np.count_nonzero(arr != expected[i]))
                raise AssertionError(f"{name} level {i}: {diff} pixel values differ from expected")

        reader = Reader(parse_url(store))
        nodes = list(reader())
        if not nodes:
            raise AssertionError(f"{name}: ome-zarr found no nodes")
        data = nodes[0].data
        if len(data) != len(levels):
            raise AssertionError(f"{name}: ome-zarr reports {len(data)} levels, expected {len(levels)}")
        for i, shape in enumerate(levels):
            if tuple(data[i].shape) != shape:
                raise AssertionError(f"{name}: ome-zarr level {i} shape {tuple(data[i].shape)}, expected {shape}")
        print(f"verified {name}")

    print(f"OK: {len(manifest['stores'])} stores verified")


if __name__ == "__main__":
    if len(sys.argv) != 2:
        sys.exit("usage: verify-omezarr.py <dir>")
    try:
        main(sys.argv[1])
    except Exception as e:
        print(f"FAIL: {e}", file=sys.stderr)
        sys.exit(1)
