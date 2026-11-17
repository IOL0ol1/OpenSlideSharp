"""Generates the openslide-python reference data the DeepZoom and thumbnail tests compare against.

Run with openslide-python 1.4.7, openslide-bin 4.0.1.2 and Pillow 13.0.0 (the versions the committed data came
from), from any directory:

    python -I generate_oracles.py <path to test/OpenSlideSharp.Tests/Fixtures> <output directory>

It writes deepzoom.json, thumbnail.json, resample.json and tiles/*.png into the output directory. PNGs are written
without an ICC profile so that decoding them never applies color management. Synthetic slides cover geometry
that the fixtures cannot (bounds properties, large pyramids, non-integer downsamples); they mimic only the slide
members DeepZoomGenerator reads.
"""

from __future__ import annotations

import json
import os
import sys

from PIL import Image
import openslide
from openslide.deepzoom import DeepZoomGenerator

import PIL


class FakeSlide:
    """Duck-typed slide exposing just what DeepZoomGenerator uses."""

    def __init__(self, name, level_dimensions, level_downsamples, properties):
        self.name = name
        self.level_dimensions = tuple(tuple(d) for d in level_dimensions)
        self.level_downsamples = tuple(level_downsamples)
        self.properties = dict(properties)
        self.dimensions = self.level_dimensions[0]
        self.level_count = len(self.level_dimensions)

    def get_best_level_for_downsample(self, downsample):
        # Same algorithm as openslide_get_best_level_for_downsample().
        if downsample < self.level_downsamples[0]:
            return 0
        for i in range(1, self.level_count):
            if downsample < self.level_downsamples[i]:
                return i - 1
        return self.level_count - 1

    def __repr__(self):
        return f'FakeSlide({self.name!r})'


FAKES = [
    FakeSlide('pyramid4x', [(100000, 80000), (25000, 20000), (6250, 5000), (1562, 1250)],
              [1.0, 4.0, 16.0, 64.0128], {}),
    FakeSlide('pyramid4x-bounds', [(100000, 80000), (25000, 20000), (6250, 5000), (1562, 1250)],
              [1.0, 4.0, 16.0, 64.0128],
              {'openslide.bounds-x': '1234', 'openslide.bounds-y': '567',
               'openslide.bounds-width': '50001', 'openslide.bounds-height': '40003'}),
    FakeSlide('pyramid2x-odd-bounds', [(42000, 30001), (21000, 15000), (10500, 7500), (5250, 3750)],
              [1.0, 2.0000333333333336, 4.000066666666667, 8.000133333333334],
              {'openslide.bounds-x': '17', 'openslide.bounds-y': '29',
               'openslide.bounds-width': '41001', 'openslide.bounds-height': '29999'}),
    FakeSlide('offset-only-bounds', [(5000, 3000), (1250, 750)], [1.0, 4.0],
              {'openslide.bounds-x': '100', 'openslide.bounds-y': '200'}),
    FakeSlide('single-level-3x7', [(3, 7)], [1.0], {}),
    FakeSlide('single-pixel', [(1, 1)], [1.0], {}),
    FakeSlide('wide-strip', [(70001, 3)], [1.0], {}),
]

TILE_PARAMS = [(254, 1), (256, 0), (100, 2), (64, 5)]
FAKE_TILE_PARAMS = [(254, 1), (512, 0)]


def sample_addresses(cols, rows):
    if cols * rows <= 64:
        return [(c, r) for r in range(rows) for c in range(cols)]
    picks = {(0, 0), (cols - 1, 0), (0, rows - 1), (cols - 1, rows - 1), (cols // 2, rows // 2),
             (min(1, cols - 1), min(1, rows - 1)), (max(cols - 2, 0), max(rows - 2, 0))}
    return sorted(picks, key=lambda a: (a[1], a[0]))


def describe(dz, all_tiles):
    levels = []
    for level in range(dz.level_count):
        cols, rows = dz.level_tiles[level]
        addresses = [(c, r) for r in range(rows) for c in range(cols)] if all_tiles else sample_addresses(cols, rows)
        tiles = []
        for col, row in addresses:
            (x, y), slide_level, (w, h) = dz.get_tile_coordinates(level, (col, row))
            tw, th = dz.get_tile_dimensions(level, (col, row))
            tiles.append([col, row, x, y, slide_level, w, h, tw, th])
        levels.append({'dimensions': list(dz.level_dimensions[level]), 'tiles': [cols, rows], 'tileInfo': tiles})
    return {
        'levelCount': dz.level_count,
        'tileCount': dz.tile_count,
        'dziJpeg': dz.get_dzi('jpeg'),
        'dziPng': dz.get_dzi('png'),
        'levels': levels,
    }


def reduce_factor(l_size, z_size):
    # Pillow's thumbnail() first box-reduces by this factor when it is above 1 (reducing_gap=2.0).
    return max(int(l_size[0] / z_size[0] / 2) or 1, int(l_size[1] / z_size[1] / 2) or 1)


def main():
    fixtures, out = sys.argv[1], sys.argv[2]
    os.makedirs(os.path.join(out, 'tiles'), exist_ok=True)

    geometry = []
    for name in ['boxes.tiff', 'small.svs', 'boxes_0.dcm']:
        with openslide.OpenSlide(os.path.join(fixtures, name)) as slide:
            for tile_size, overlap in TILE_PARAMS:
                for limit_bounds in (False, True):
                    dz = DeepZoomGenerator(slide, tile_size, overlap, limit_bounds)
                    geometry.append({'source': name, 'kind': 'slide', 'tileSize': tile_size, 'overlap': overlap,
                                     'limitBounds': limit_bounds, **describe(dz, all_tiles=True)})
    for fake in FAKES:
        for tile_size, overlap in FAKE_TILE_PARAMS:
            for limit_bounds in (False, True):
                dz = DeepZoomGenerator(fake, tile_size, overlap, limit_bounds)
                geometry.append({'source': fake.name, 'kind': 'fake', 'tileSize': tile_size, 'overlap': overlap,
                                 'limitBounds': limit_bounds,
                                 'fake': {'levelDimensions': [list(d) for d in fake.level_dimensions],
                                          'levelDownsamples': list(fake.level_downsamples),
                                          'properties': fake.properties},
                                 **describe(dz, all_tiles=False)})

    tiles = []

    def record_tiles(label, source_name, kind, dz, levels):
        for level in levels:
            cols, rows = dz.level_tiles[level]
            for row in range(rows):
                for col in range(cols):
                    _, _, l_size = dz.get_tile_coordinates(level, (col, row))
                    z_size = dz.get_tile_dimensions(level, (col, row))
                    tile = dz.get_tile(level, (col, row))
                    file = f'{label}-{level}-{col}-{row}.png'
                    tile.save(os.path.join(out, 'tiles', file), icc_profile=None)
                    icc = tile.info.get('icc_profile')
                    tiles.append({'source': source_name, 'kind': kind, 'tileSize': dz._z_t_downsample,
                                  'overlap': dz._z_overlap, 'level': level, 'col': col, 'row': row,
                                  'expectedSize': list(z_size), 'pythonSize': list(tile.size),
                                  'exact': list(tile.size) == list(z_size) and reduce_factor(l_size, z_size) == 1,
                                  'iccLength': 0 if icc is None else len(icc), 'file': file})

    with openslide.OpenSlide(os.path.join(fixtures, 'boxes.tiff')) as slide:
        record_tiles('boxes-254-1', 'boxes.tiff', 'slide', DeepZoomGenerator(slide, 254, 1), range(10))
        dz = DeepZoomGenerator(slide, 64, 5)
        record_tiles('boxes-64-5', 'boxes.tiff', 'slide', dz, range(dz.level_count - 4, dz.level_count))
    with openslide.OpenSlide(os.path.join(fixtures, 'boxes_0.dcm')) as slide:
        image = openslide.ImageSlide(slide.associated_images['thumbnail'])
        dz = DeepZoomGenerator(image, 8, 1)
        record_tiles('dcm-thumbnail-8-1', 'boxes_0.dcm#thumbnail', 'associated', dz, range(dz.level_count))

    thumbnails = []
    with openslide.OpenSlide(os.path.join(fixtures, 'boxes.tiff')) as slide:
        for size in [(100, 100), (64, 32), (300, 250), (1000, 1000), (299, 1), (1, 1), (150, 125), (149, 200)]:
            thumb = slide.get_thumbnail(size)
            downsample = max(d / t for d, t in zip(slide.dimensions, size))
            level = slide.get_best_level_for_downsample(downsample)
            l_size = slide.level_dimensions[level]
            file = f'thumb-boxes-{size[0]}x{size[1]}.png'
            thumb.save(os.path.join(out, 'tiles', file), icc_profile=None)
            thumbnails.append({'source': 'boxes.tiff', 'maxSize': list(size), 'level': level,
                               'pythonSize': list(thumb.size),
                               'exact': reduce_factor(l_size, thumb.size) == 1, 'file': file})

    # Plain Pillow LANCZOS resize (no reducing_gap) of a deterministic image with sharp edges.
    src = Image.new('RGB', (37, 23))
    src.putdata([((x * 37 + y * 11) % 256, (x * x + 3 * y) % 256, 255 if (x // 4 + y // 3) % 2 else 0)
                 for y in range(23) for x in range(37)])
    src.save(os.path.join(out, 'tiles', 'resample-src.png'))
    resamples = []
    for size in [(20, 11), (10, 40), (74, 46), (36, 23), (37, 5), (4, 3), (1, 1), (37, 23)]:
        file = f'resample-{size[0]}x{size[1]}.png'
        src.resize(size, Image.Resampling.LANCZOS).save(os.path.join(out, 'tiles', file))
        resamples.append({'size': list(size), 'file': file})

    meta = {'openslidePython': openslide.__version__, 'openslideLibrary': openslide.__library_version__,
            'pillow': PIL.__version__}
    with open(os.path.join(out, 'deepzoom.json'), 'w', encoding='utf-8', newline='\n') as f:
        json.dump({'generator': meta, 'geometry': geometry, 'tiles': tiles}, f, indent=1)
        f.write('\n')
    with open(os.path.join(out, 'thumbnail.json'), 'w', encoding='utf-8', newline='\n') as f:
        json.dump({'generator': meta, 'thumbnails': thumbnails}, f, indent=1)
        f.write('\n')
    with open(os.path.join(out, 'resample.json'), 'w', encoding='utf-8', newline='\n') as f:
        json.dump({'generator': meta, 'source': 'resample-src.png', 'outputs': resamples}, f, indent=1)
        f.write('\n')


if __name__ == '__main__':
    main()
