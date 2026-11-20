#!/usr/bin/env python3
"""Generate the DdsFormatFixAlt fixtures.

Three files, all 128x128, because that is the size real mod art uses and the size the icon-size mip
defect needs to be visible at:

  alt_dxt3_128.dds    DXT3, 8 levels   - no TextureFormat member exists, so the shipped loader throws
                                          and the file becomes BaseContent.BadTex.
  alt_mip1_128.dds    DXT3, 1 level    - a one-level source: the engine's texture gets a full chain
                                          and only level 0 is written unless the payload carries the
                                          whole thing.
  alt_bc3_dx10_128.dds BC3 behind a DX10 header (dxgi 77) - the loader maps every DX10 file to BC7
                                          without reading the DXGI word, so this file is created as
                                          BC7 and fed 16-byte BC3 blocks: garbage, not a crash.

The picture is deliberately not flat - a red/green gradient with a horizontal alpha ramp - so the
gate can tell decoded art from a placeholder (lumSd, meanA, alpha0) and so a decoded BC4 mask shows a
non-zero brightest channel.

    python3 ddsalt/make_fixtures.py --out ddsalt

Writes plain DDS bytes; nothing here depends on Pillow or on the game.
"""
import argparse
import struct
from pathlib import Path

SIZE = 128


# ---------------------------------------------------------------------------- picture

def make_image(w, h):
    """Top-down RGBA rows: two-channel gradient plus a 0..255 horizontal alpha ramp."""
    rows = []
    for y in range(h):
        row = []
        for x in range(w):
            r = (x * 255) // max(1, w - 1)
            g = (y * 255) // max(1, h - 1)
            b = ((x + y) * 255) // max(1, w + h - 2)
            a = (x * 255) // max(1, w - 1)
            row.append((r, g, b, a))
        rows.append(row)
    return rows


def box_down(rows, w, h):
    """2x2 average, edges clamped - the same filter DdsAltFormat.Downsample implements."""
    ow, oh = max(1, w // 2), max(1, h // 2)
    out = []
    for y in range(oh):
        row = []
        y0, y1 = min(h - 1, y * 2), min(h - 1, y * 2 + 1)
        for x in range(ow):
            x0, x1 = min(w - 1, x * 2), min(w - 1, x * 2 + 1)
            px = []
            for ch in range(4):
                total = (rows[y0][x0][ch] + rows[y0][x1][ch] +
                         rows[y1][x0][ch] + rows[y1][x1][ch])
                px.append((total + 2) // 4)
            row.append(tuple(px))
        out.append(row)
    return out, ow, oh


# ---------------------------------------------------------------------------- blocks

def lum(c):
    return 0.299 * c[0] + 0.587 * c[1] + 0.114 * c[2]


def to565(c):
    return ((c[0] >> 3) << 11) | ((c[1] >> 2) << 5) | (c[2] >> 3)


def from565(v):
    r, g, b = (v >> 11) & 0x1F, (v >> 5) & 0x3F, v & 0x1F
    return ((r * 255 + 15) // 31, (g * 255 + 31) // 63, (b * 255 + 15) // 31)


def dist2(a, b):
    return sum((a[i] - b[i]) ** 2 for i in range(3))


def colour_block(pixels):
    """8 bytes, always the four-colour mode (DXT3/DXT5 keep alpha in a separate block)."""
    hi = max(pixels, key=lum)
    lo = min(pixels, key=lum)
    c0, c1 = to565(hi), to565(lo)
    if c0 == c1:
        # Keep c0 > c1 so the decoder takes the four-colour path even for a flat block.
        if c1 < 0xFFFF:
            c0 = c1 + 1
        else:
            c1 = c0 - 1
    if c0 < c1:
        c0, c1 = c1, c0

    p0, p1 = from565(c0), from565(c1)
    palette = [p0, p1,
               tuple((2 * p0[i] + p1[i]) // 3 for i in range(3)),
               tuple((p0[i] + 2 * p1[i]) // 3 for i in range(3))]

    bits = 0
    for i, c in enumerate(pixels):
        best = min(range(4), key=lambda k: dist2(c, palette[k]))
        bits |= best << (2 * i)
    return bytes([c0 & 0xFF, c0 >> 8, c1 & 0xFF, c1 >> 8]) + bits.to_bytes(4, "little")


def alpha_ramp_block(pixels):
    """DXT5/BC3 alpha: two endpoints plus sixteen 3-bit indices (a0 > a1 -> eight-step ramp)."""
    a0 = max(p[3] for p in pixels)
    a1 = min(p[3] for p in pixels)
    if a0 == a1:
        if a1 < 255:
            a0 = a1 + 1
        else:
            a1 = a0 - 1
    palette = [a0, a1] + [((7 - i) * a0 + i * a1) // 7 for i in range(1, 7)]
    bits = 0
    for i, p in enumerate(pixels):
        best = min(range(8), key=lambda k: abs(palette[k] - p[3]))
        bits |= best << (3 * i)
    return bytes([a0, a1]) + bits.to_bytes(6, "little")


def dxt3_block(pixels):
    """Sixteen 4-bit alphas (expanded by 17) followed by the colour block."""
    nibbles = bytearray(8)
    for i, p in enumerate(pixels):
        v = (p[3] + 8) // 17
        v = 0 if v < 0 else 15 if v > 15 else v
        nibbles[i // 2] |= v << ((i % 2) * 4)
    return bytes(nibbles) + colour_block(pixels)


def bc3_block(pixels):
    return alpha_ramp_block(pixels) + colour_block(pixels)


ENCODERS = {"dxt3": (dxt3_block, 16), "bc3": (bc3_block, 16)}


def encode_level(rows, w, h, kind):
    fn, block_bytes = ENCODERS[kind]
    out = bytearray()
    for by in range(0, h, 4):
        for bx in range(0, w, 4):
            pixels = []
            for y in range(4):
                for x in range(4):
                    px = rows[min(h - 1, by + y)][min(w - 1, bx + x)]
                    pixels.append(px)
            out += fn(pixels)
    assert len(out) == ((w + 3) // 4) * ((h + 3) // 4) * block_bytes
    return bytes(out)


# ---------------------------------------------------------------------------- container

FLAGS = 0x1 | 0x2 | 0x4 | 0x1000 | 0x80000        # caps|height|width|pixelformat|linearsize
FLAGS_MIPS = FLAGS | 0x20000                       # + mipmapcount


def header(w, h, levels, fourcc, dxgi=None, top_level_bytes=0):
    flags = FLAGS_MIPS if levels > 1 else FLAGS
    out = bytearray()
    out += struct.pack("<8I", 0x20534444, 124, flags, h, w, top_level_bytes,
                       0, levels if levels > 1 else 0)
    out += b"\0" * 44                                        # reserved[11]
    pf_flags = 0x4 | 0x1 if dxgi is not None else 0x4         # FOURCC (+ ALPHAPIXELS for DX10)
    out += struct.pack("<2I4s5I", 32, pf_flags, fourcc.encode("ascii"), 0, 0, 0, 0, 0)
    out += struct.pack("<5I", 0x1000, 0, 0, 0, 0)            # caps, caps2..4, reserved2
    if dxgi is not None:
        out += struct.pack("<5I", dxgi, 3, 0, 1, 0)          # dxgi, TEXTURE2D, misc, arraySize, misc2
    return bytes(out)


def write_dds(path, kind, levels, fourcc="DXT3", dxgi=None, width=SIZE, height=SIZE):
    rows = make_image(width, height)
    chunks = []
    w, h = width, height
    for level in range(levels):
        if level:
            rows, w, h = box_down(rows, w, h)
        chunks.append(encode_level(rows, w, h, kind))
        if w == 1 and h == 1:
            while len(chunks) < levels:                       # 1x1 repeats, like the file format
                chunks.append(chunks[-1])
            break
    blob = header(width, height, levels, fourcc, dxgi, len(chunks[0])) + b"".join(chunks)
    path.write_bytes(blob)
    return len(blob)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", default="ddsalt", help="directory for the generated .dds files")
    args = ap.parse_args()

    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)

    made = [
        ("alt_dxt3_128.dds", dict(kind="dxt3", levels=8, fourcc="DXT3")),
        ("alt_mip1_128.dds", dict(kind="dxt3", levels=1, fourcc="DXT3")),
        ("alt_bc3_dx10_128.dds", dict(kind="bc3", levels=8, fourcc="DX10", dxgi=77)),
    ]
    for name, kwargs in made:
        size = write_dds(out / name, **kwargs)
        print(f"{out / name}  {size} bytes  {kwargs}")


if __name__ == "__main__":
    main()
