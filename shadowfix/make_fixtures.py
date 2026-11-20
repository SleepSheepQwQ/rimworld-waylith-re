#!/usr/bin/env python3
"""Build the ShadowProbe mod used by .github/workflows/shadowfix.yml.

The mod is a miniature of what a weapon/animal texture pack ships:

  Shadow.png   128x128 png          + Shadow.dds  8x8 DXT3   -> the png must win
  PngOnly.png  128x128 png, same bytes as Shadow.png            -> reference pixels
  Kept.png     128x128 png          + Kept.dds    8x8 DXT1   -> the dds must still win
  Dxt3.dds     8x8 DXT3, no png                                 -> must stay a miss
  IbexMALE_east.png                                             -> case fallback fixture
  IbexShadow_{south,north,east,west}[m].png.png                 -> Graphic_Multi leak fixture

Why the .png.png names: ModContentLoader.LoadTexture names a texture by
Path.GetFileNameWithoutExtension, so a file called "X_east.png.png" is named "X_east.png" and the
directory key keeps the inner extension - the shipped-pack leak Graphic_Multi cannot resolve. The
four rotations are the only files in that folder: shipping a base "IbexShadow.png" would let
Graphic_Multi fill all four directions from the fallback lookup and the 0/4 control would disappear.

The images are generated rather than copied so the pixels are known: a horizontal/vertical ramp with
a diagonal term, which no solid placeholder can imitate (the gate compares luminance spread and the
exact pixel hash against PngOnly).

    python3 shadowfix/make_fixtures.py --run run
"""
import argparse
import shutil
import struct
import sys
import zlib
from pathlib import Path

FOLDER = Path("Textures/Things/ShadowProbe")
MULTI_FOLDER = Path("Textures/Things/Pawn/Animal/IbexShadow")
CASE_FOLDER = Path("Textures/Things/Pawn/Animal/Ibex")


def png(width, height, pixel):
    raw = bytearray()
    for y in range(height):
        raw.append(0)                      # filter: none
        for x in range(width):
            r, g, b, a = pixel(x, y)
            raw += bytes((r & 0xFF, g & 0xFF, b & 0xFF, a & 0xFF))

    def chunk(tag, data):
        return (struct.pack(">I", len(data)) + tag + data
                + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF))

    ihdr = struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0)   # 8-bit RGBA
    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", ihdr)
            + chunk(b"IDAT", zlib.compress(bytes(raw), 9)) + chunk(b"IEND", b""))


def ramp(x, y):
    return (x * 2, y * 2, (x * 7 + y * 3) & 0xFF, 255)


def flat(x, y):
    return (40, 90, 160, 255)


def dds(fourcc, width, height, payload):
    hdr = bytearray(128)
    struct.pack_into("<I", hdr, 0, 0x20534444)          # "DDS "
    struct.pack_into("<I", hdr, 4, 124)
    struct.pack_into("<I", hdr, 8, 0x1 | 0x2 | 0x4 | 0x1000 | 0x80000)
    struct.pack_into("<I", hdr, 12, height)
    struct.pack_into("<I", hdr, 16, width)
    struct.pack_into("<I", hdr, 20, len(payload))      # linear size
    struct.pack_into("<I", hdr, 28, 1)                 # mip count
    struct.pack_into("<I", hdr, 76, 32)                # DDS_PIXELFORMAT.dwSize
    struct.pack_into("<I", hdr, 80, 0x4)               # DDPF_FOURCC
    hdr[84:88] = fourcc.encode("ascii")
    struct.pack_into("<I", hdr, 108, 0x1000)           # DDSCAPS_TEXTURE
    return bytes(hdr) + payload


def dxt1_green_8x8():
    # 8x8 = 2x2 blocks, 8 bytes each: c0 green, c1 blue, all indices 0 (so every texel is c0).
    block = struct.pack("<HHI", 0x07E0, 0x001F, 0)
    return block * 4


def dxt3_green_8x8():
    # 8x8 = 2x2 blocks, 16 bytes each: 8 bytes of 4-bit alpha (all opaque) + a DXT1 colour block.
    alpha = b"\xff" * 8
    colour = struct.pack("<HHI", 0x07E0, 0x001F, 0)
    return (alpha + colour) * 4


ABOUT = """<?xml version="1.0" encoding="utf-8"?>
<ModMetaData>
  <name>ShadowProbe</name>
  <author>ci</author>
  <packageId>waylith.shadowprobe</packageId>
  <supportedVersions><li>1.7</li></supportedVersions>
  <description>Texture-pack shadowing fixture for CI.</description>
</ModMetaData>
"""


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--run", required=True, help="assembled run directory (contains RimWorld)")
    args = ap.parse_args()

    run = Path(args.run)
    mod = run / "Mods" / "ShadowProbe"
    if mod.exists():
        shutil.rmtree(mod)
    (mod / "About").mkdir(parents=True)
    (mod / "About" / "About.xml").write_text(ABOUT, encoding="utf-8")

    tex = mod / FOLDER
    tex.mkdir(parents=True)

    big = png(128, 128, ramp)
    (tex / "Shadow.png").write_bytes(big)
    (tex / "PngOnly.png").write_bytes(big)             # byte-identical: "same pixels" is exact
    (tex / "Kept.png").write_bytes(png(128, 128, flat))

    (tex / "Shadow.dds").write_bytes(dds("DXT3", 8, 8, dxt3_green_8x8()))
    (tex / "Dxt3.dds").write_bytes(dds("DXT3", 8, 8, dxt3_green_8x8()))
    (tex / "Kept.dds").write_bytes(dds("DXT1", 8, 8, dxt1_green_8x8()))

    case = mod / CASE_FOLDER
    case.mkdir(parents=True)
    (case / "IbexMALE_east.png").write_bytes(big)

    multi = mod / MULTI_FOLDER
    multi.mkdir(parents=True)
    small = png(32, 32, ramp)
    for suffix in ("south", "north", "east", "west"):
        for tail in ("", "m"):
            name = f"IbexShadow_{suffix}{tail}.png.png"     # the leak: inner name keeps ".png"
            (multi / name).write_bytes(small)

    written = sorted(p.relative_to(mod).as_posix() for p in mod.rglob("*") if p.is_file())
    for w in written:
        print(f"shadowfixture file={w} bytes={(mod / w).stat().st_size}")
    if len(written) != 16:
        print(f"expected 16 files, wrote {len(written)}", file=sys.stderr)
        return 2
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
