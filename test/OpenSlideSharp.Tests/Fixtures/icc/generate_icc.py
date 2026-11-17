"""Writes the synthetic ICC profiles the color tests use.

Three profiles describe sRGB with the red and blue channels swapped, so converting pure red through any of them
to sRGB yields (nearly) pure blue. They differ only in how the device-to-PCS transform is stored:

- swap-rb-matrix.icc: rXYZ/gXYZ/bXYZ + rTRC/gTRC/bTRC (matrix/TRC), which SKColorSpace.CreateIcc accepts.
- swap-rb-lut.icc: A2B0 only (lut16Type, PCS XYZ), which SKColorSpace.CreateIcc rejects but skcms can still parse.
- swap-rb-lut-lab.icc: A2B0 only (lut16Type, PCS Lab), the same transform with Lab values in the CLUT.

A fourth one is plain sRGB:

- srgb-matrix.icc: the sRGB primaries (D50) with parametric sRGB curves (parametricCurveType, function type 3).

Run: python -I generate_icc.py <output directory>
The profiles are generated from first principles and are dedicated to the public domain (CC0).
"""

from __future__ import annotations

import os
import struct
import sys

# sRGB primaries adapted to D50 (Bradford), as columns: XYZ of R, G, B.
SRGB_D50 = [
    [0.4360747, 0.3850649, 0.1430804],
    [0.2225045, 0.7168786, 0.0606169],
    [0.0139322, 0.0971045, 0.7141733],
]
D50 = (0.9642, 1.0, 0.8249)

# Grid points per axis of the Lab CLUT. With 17, LittleCMS 2.19 maps the pure colors, gray 128 and white exactly to
# the swapped colors, and 5000 random colors to within 15 levels of them.
LAB_GRID = 17


def s15f16(v: float) -> bytes:
    return struct.pack('>i', round(v * 65536))


def xyz_type(x: float, y: float, z: float) -> bytes:
    return b'XYZ ' + b'\0' * 4 + s15f16(x) + s15f16(y) + s15f16(z)


def curv_gamma(gamma: float) -> bytes:
    return b'curv' + b'\0' * 4 + struct.pack('>I', 1) + struct.pack('>H', round(gamma * 256))


def para_srgb() -> bytes:
    # Function type 3: Y = (aX + b)^g for X >= d, Y = cX below; the parameters are those of the sRGB curve.
    params = (2.4, 1 / 1.055, 0.055 / 1.055, 1 / 12.92, 0.04045)
    return b'para' + b'\0' * 4 + struct.pack('>HH', 3, 0) + b''.join(s15f16(p) for p in params)


def desc_type(text: str) -> bytes:
    ascii_ = text.encode('ascii') + b'\0'
    body = b'desc' + b'\0' * 4 + struct.pack('>I', len(ascii_)) + ascii_
    body += struct.pack('>II', 0, 0)  # unicode language code + count
    body += struct.pack('>HB', 0, 0) + b'\0' * 67  # scriptcode code + count + 67 bytes
    return body


def text_type(text: str) -> bytes:
    return b'text' + b'\0' * 4 + text.encode('ascii') + b'\0'


def srgb_to_linear(v: float) -> float:
    return v / 12.92 if v <= 0.04045 else ((v + 0.055) / 1.055) ** 2.4


def lut16(grid: int, clut_entry) -> bytes:
    """lut16Type with sRGB-decoding input tables and identity output tables. clut_entry(r, g, b) returns the three
    16-bit PCS values of a grid point, whose inputs are linear light from 0 to 1."""
    in_entries, out_entries = 256, 2
    body = b'mft2' + b'\0' * 4 + struct.pack('>BBBB', 3, 3, grid, 0)
    for row in range(3):  # identity matrix (only used for XYZ input)
        for col in range(3):
            body += s15f16(1.0 if row == col else 0.0)
    body += struct.pack('>HH', in_entries, out_entries)
    for _ in range(3):  # input tables: sRGB transfer function to linear light
        for i in range(in_entries):
            body += struct.pack('>H', round(srgb_to_linear(i / (in_entries - 1)) * 65535))
    for r in range(grid):  # CLUT: first input channel varies slowest
        for g in range(grid):
            for b in range(grid):
                body += struct.pack('>HHH', *clut_entry(r / (grid - 1), g / (grid - 1), b / (grid - 1)))
    for _ in range(3):  # output tables: identity
        body += struct.pack('>HH', 0, 65535)
    return body


def swapped_xyz(r: float, g: float, b: float) -> list[float]:
    device = (b, g, r)  # the swap: device red is shown as blue and vice versa
    return [sum(SRGB_D50[row][k] * device[k] for k in range(3)) for row in range(3)]


def xyz_to_lab(x: float, y: float, z: float) -> tuple[float, float, float]:
    def f(t: float) -> float:
        return t ** (1 / 3) if t > (6 / 29) ** 3 else t / (3 * (6 / 29) ** 2) + 4 / 29

    fx, fy, fz = f(x / D50[0]), f(y / D50[1]), f(z / D50[2])
    return 116 * fy - 16, 500 * (fx - fy), 200 * (fy - fz)


def lut16_swap_rb() -> bytes:
    # 16-bit PCSXYZ: 0x8000 = 1.0. Two grid points per axis are exact, since XYZ is linear in linear light.
    return lut16(2, lambda r, g, b: [min(65535, round(v * 32768)) for v in swapped_xyz(r, g, b)])


def lut16_swap_rb_lab() -> bytes:
    # lut16Type keeps the ICC v2 ("legacy") Lab encoding: L 0..100 -> 0x0000..0xFF00, a and b -128..127.996 ->
    # 0x0000..0xFFFF with 0x8000 = 0. Lab is not linear in linear light, so the CLUT needs a finer grid.
    def entry(r: float, g: float, b: float) -> list[int]:
        lightness, a, b_ = xyz_to_lab(*swapped_xyz(r, g, b))
        values = (lightness * 0xFF00 / 100, (a + 128) * 256, (b_ + 128) * 256)
        return [max(0, min(65535, round(v))) for v in values]

    return lut16(LAB_GRID, entry)


def profile(tags: list[tuple[bytes, bytes]], pcs: bytes = b'XYZ ') -> bytes:
    count = len(tags)
    offset = 128 + 4 + 12 * count
    table = struct.pack('>I', count)
    data = b''
    for sig, payload in tags:
        while (offset + len(data)) % 4:
            data += b'\0'
        table += sig + struct.pack('>II', offset + len(data), len(payload))
        data += payload
    while (offset + len(data)) % 4:
        data += b'\0'
    size = 128 + len(table) + len(data)
    header = struct.pack('>I', size) + b'\0\0\0\0' + bytes([2, 0x40, 0, 0])  # size, CMM, version 2.4
    header += b'mntr' + b'RGB ' + pcs
    header += struct.pack('>HHHHHH', 2026, 1, 1, 0, 0, 0)
    header += b'acsp' + b'\0' * 4 + struct.pack('>I', 0) + b'\0' * 4 + b'\0' * 4 + b'\0' * 8
    header += struct.pack('>I', 0)  # rendering intent: perceptual
    header += s15f16(D50[0]) + s15f16(D50[1]) + s15f16(D50[2])
    header += b'\0' * 4 + b'\0' * 16 + b'\0' * 28
    assert len(header) == 128
    return header + table + data


def main() -> None:
    out = sys.argv[1]
    os.makedirs(out, exist_ok=True)
    common = [
        (b'desc', None),
        (b'cprt', text_type('No copyright, use freely (CC0)')),
        (b'wtpt', xyz_type(*D50)),
    ]
    swapped = [[SRGB_D50[row][2], SRGB_D50[row][1], SRGB_D50[row][0]] for row in range(3)]
    matrix = [
        (b'desc', desc_type('sRGB with red and blue swapped (matrix/TRC)')),
        *common[1:],
        (b'rXYZ', xyz_type(*(swapped[row][0] for row in range(3)))),
        (b'gXYZ', xyz_type(*(swapped[row][1] for row in range(3)))),
        (b'bXYZ', xyz_type(*(swapped[row][2] for row in range(3)))),
        (b'rTRC', curv_gamma(2.2)),
        (b'gTRC', curv_gamma(2.2)),
        (b'bTRC', curv_gamma(2.2)),
    ]
    lut = [
        (b'desc', desc_type('sRGB with red and blue swapped (A2B0 only)')),
        *common[1:],
        (b'A2B0', lut16_swap_rb()),
    ]
    lut_lab = [
        (b'desc', desc_type('sRGB with red and blue swapped (A2B0 only, PCS Lab)')),
        *common[1:],
        (b'A2B0', lut16_swap_rb_lab()),
    ]
    srgb = [
        (b'desc', desc_type('sRGB (matrix/TRC, parametric curves)')),
        *common[1:],
        (b'rXYZ', xyz_type(*(SRGB_D50[row][0] for row in range(3)))),
        (b'gXYZ', xyz_type(*(SRGB_D50[row][1] for row in range(3)))),
        (b'bXYZ', xyz_type(*(SRGB_D50[row][2] for row in range(3)))),
        (b'rTRC', para_srgb()),
        (b'gTRC', para_srgb()),
        (b'bTRC', para_srgb()),
    ]
    with open(os.path.join(out, 'swap-rb-matrix.icc'), 'wb') as f:
        f.write(profile(matrix))
    with open(os.path.join(out, 'swap-rb-lut.icc'), 'wb') as f:
        f.write(profile(lut))
    with open(os.path.join(out, 'swap-rb-lut-lab.icc'), 'wb') as f:
        f.write(profile(lut_lab, pcs=b'Lab '))
    with open(os.path.join(out, 'srgb-matrix.icc'), 'wb') as f:
        f.write(profile(srgb))


if __name__ == '__main__':
    main()
