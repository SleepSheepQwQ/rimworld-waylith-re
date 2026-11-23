#!/usr/bin/env python3
"""Build the minimal unique-weapon icon fixture mod inside an assembled run directory.

Three texture layouts, one XML def each, so the naming variable is isolated:

  UniqueIconPlain   UniqueRevolverA.dds        ...E.dds    + ...E_m.dds      (healthy layout)
  UniqueIconLeak    UniqueRevolverA.png.dds    ...E.png.dds + ..._m.png.dds  (the shipped leak)
  UniqueIconDx10    UniqueRevolverA.png.dds    ...E.png.dds + ..._m.png.dds  (mask with the
                                                                             shipped DX10 header)

All three carry the same 128x128 BC1 art with a full 8-level mip chain (a 1-mip BC1 file goes
transparent the moment it is drawn smaller than native - that defect must not be confused with
the naming one).  The `_m` files are flat grey, i.e. what a mask looks like when it is drawn as
an icon.  Only the file names differ, which is exactly the variable under test.

Every file is re-read here before the run: header fields and, when Pillow is available, the
decoded size and a sampled texel.  A fixture that fails its own control read must never be used
to accuse the engine.
"""
import argparse
import hashlib
import struct
import sys
from pathlib import Path

LETTERS = "ABCDE"
FOLDER = Path("Things/Item/Equipment/WeaponRanged/UniqueWeapons")
PALETTE = [(196, 64, 48), (72, 96, 160), (216, 184, 88), (56, 56, 64), (144, 148, 152)]


def rgb565(rgb):
    r, g, b = rgb
    return ((r >> 3) << 11) | ((g >> 2) << 5) | (b >> 3)


def bc1_block(rgb):
    """One 4x4 block, all texels index 0 -> exactly colour c0, opaque (c0 > c1 = 4-colour mode)."""
    c0 = rgb565(rgb)
    c1 = c0 - 1 if c0 > 1 else 1
    return struct.pack("<HHI", c0, c1, 0)


def art_rgb(bx, by):
    if (bx * 7 + by * 3) % 5 == 0:
        return PALETTE[3]
    return PALETTE[(bx + by) % 4]


def mask_rgb(bx, by):
    return (128, 128, 128) if (bx + by) % 7 else (144, 144, 144)


def level_bytes(w, h, colour):
    blocks_x, blocks_y = max(1, (w + 3) // 4), max(1, (h + 3) // 4)
    out = bytearray()
    for by in range(blocks_y):
        for bx in range(blocks_x):
            out += bc1_block(colour(bx, by))
    return bytes(out)


def dds(w, h, colour, dxgi=None):
    """BC1 (or DX10+BC1 blocks) DDS with the full mip chain, correct linear size and mip count."""
    mips = []
    mw, mh = w, h
    while True:
        mips.append(level_bytes(mw, mh, colour))
        if mw == 1 and mh == 1:
            break
        mw, mh = max(1, mw // 2), max(1, mh // 2)

    fourcc = b"DX10" if dxgi is not None else b"DXT1"
    flags = 0x1 | 0x2 | 0x4 | 0x1000 | 0x80000          # CAPS|HEIGHT|WIDTH|PIXELFORMAT|LINEARSIZE
    hdr = struct.pack("<4sIIIIIII", b"DDS ", 124, flags, h, w, len(mips[0]), 0, len(mips))
    hdr += b"\x00" * (76 - len(hdr))
    hdr += struct.pack("<II4sIIIII", 32, 0x4, fourcc, 0, 0, 0, 0, 0)
    hdr += struct.pack("<IIIII", 4198408, 0, 0, 0, 0)    # TEXTURE|COMPLEX|MIPMAP
    if dxgi is not None:
        hdr += struct.pack("<IIIII", dxgi, 3, 0, 1, 0)   # dxgiFormat, TEXTURE2D, misc, array, misc2
    return hdr + b"".join(mips), len(mips)


def read_header(data):
    if data[:4] != b"DDS " or len(data) < 128:
        sys.exit("not a DDS file")
    (_magic, size, flags, h, w, linear, _depth, mips) = struct.unpack("<4sIIIIIII", data[:32])
    fourcc = data[84:88].decode("ascii", "replace")
    dxgi = struct.unpack("<I", data[128:132])[0] if fourcc == "DX10" else None
    skip = 148 if fourcc == "DX10" else 128
    return dict(size=size, w=w, h=h, mips=mips, linear=linear, fourcc=fourcc, dxgi=dxgi,
                header=skip, data=len(data) - skip)


def bc1_sample(data, skip, w, x, y):
    """Independent BC1 decode of one texel - Pillow refuses BC1_UNORM_SRGB behind DX10."""
    blocks_per_row = max(1, (w + 3) // 4)
    block = skip + ((y // 4) * blocks_per_row + (x // 4)) * 8
    c0, c1, bits = struct.unpack("<HHI", data[block:block + 8])
    idx = (bits >> (2 * ((y % 4) * 4 + (x % 4)))) & 0x3

    def un565(c):
        return ((c >> 11) & 0x1F) * 255 // 31, ((c >> 5) & 0x3F) * 255 // 63, (c & 0x1F) * 255 // 31

    colours = [un565(c0), un565(c1)]
    if c0 > c1:
        colours += [tuple((2 * a + b) // 3 for a, b in zip(colours[0], colours[1])),
                    tuple((a + 2 * b) // 3 for a, b in zip(colours[0], colours[1]))]
    else:
        colours += [tuple((a + b) // 2 for a, b in zip(colours[0], colours[1])), (0, 0, 0)]
    rgb = colours[idx]
    return rgb + ((0,) if (c0 <= c1 and idx == 3) else (255,))


def verify(path, want_w=128, want_h=128):
    data = path.read_bytes()
    hdr = read_header(data)
    problems = []
    if hdr["size"] != 124:
        problems.append(f"header size {hdr['size']}")
    if (hdr["w"], hdr["h"]) != (want_w, want_h):
        problems.append(f"size {hdr['w']}x{hdr['h']}")
    if hdr["mips"] != 8:
        problems.append(f"mips {hdr['mips']}")
    if hdr["linear"] != 8192:
        problems.append(f"linear {hdr['linear']}")
    if hdr["data"] != 10936:
        problems.append(f"data {hdr['data']}")
    sample = None
    try:
        from PIL import Image
        with Image.open(path) as im:
            if im.size != (want_w, want_h):
                problems.append(f"pillow size {im.size}")
            raw = im.convert("RGBA").getpixel((want_w // 2, want_h // 2))
            sample = (int(raw[0]), int(raw[1]), int(raw[2]), int(raw[3]))  # type: ignore[index]
    except ImportError:
        problems.append("pillow missing")
    except NotImplementedError:
        # Pillow has no BC1_UNORM_SRGB (DXGI 72); the independent BC1 decode below covers it.
        pass
    except Exception as exc:                                  # noqa: BLE001 - report, never hide
        problems.append(f"pillow {type(exc).__name__}: {exc}")

    bc1 = bc1_sample(data, hdr["header"], hdr["w"], want_w // 2, want_h // 2)
    if max(bc1[:3]) == 0:
        problems.append(f"bc1 sample is black {bc1}")
    if sample is None:
        sample = bc1
    elif max(abs(a - b) for a, b in zip(sample[:3], bc1[:3])) > 2:
        problems.append(f"pillow {sample[:3]} != bc1 {bc1[:3]}")
    return hdr, sample, problems


def write_dds(path, data):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(data)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--run", required=True, help="assembled run directory (contains RimWorld)")
    ap.add_argument("--verify", action="store_true")
    args = ap.parse_args()

    run = Path(args.run)
    mod = run / "Mods" / "UniqueIconFixture"
    if mod.exists():
        import shutil
        shutil.rmtree(mod)
    (mod / "About").mkdir(parents=True)

    (mod / "About/About.xml").write_text(
        '<?xml version="1.0" encoding="utf-8"?>\n'
        "<ModMetaData>\n"
        "  <name>UniqueIconFixture</name>\n"
        "  <author>ci</author>\n"
        "  <packageId>waylith.uniqueiconfixture</packageId>\n"
        "  <supportedVersions><li>1.7</li></supportedVersions>\n"
        "</ModMetaData>\n", encoding="utf-8")

    defs = []
    layouts = [
        ("UniqueRevolverFixturePlain", "UniqueIconPlain"),
        ("UniqueRevolverFixtureLeak", "UniqueIconLeak"),
        ("UniqueRevolverFixtureDx10", "UniqueIconDx10"),
    ]
    for def_name, folder in layouts:
        defs.append(
            '  <ThingDef ParentName="Gun_Revolver">\n'
            f"    <defName>{def_name}</defName>\n"
            f"    <label>{def_name.lower()}</label>\n"
            "    <graphicData>\n"
            f"      <texPath>{FOLDER.as_posix()}/{folder}</texPath>\n"
            "      <graphicClass>Graphic_Random</graphicClass>\n"
            "      <shaderType>CutoutComplex</shaderType>\n"
            "    </graphicData>\n"
            "    <recipeMaker Inherit=\"False\" IsNull=\"True\" />\n"
            "  </ThingDef>\n")
    (mod / "Defs").mkdir(parents=True)
    (mod / "Defs/ThingDefs_UniqueIconFixture.xml").write_text(
        '<?xml version="1.0" encoding="utf-8"?>\n<Defs>\n' + "".join(defs) + "</Defs>\n",
        encoding="utf-8")

    art, mips = dds(128, 128, art_rgb)
    mask, _ = dds(128, 128, mask_rgb)
    mask_dx10, _ = dds(128, 128, mask_rgb, dxgi=72)   # BC1_UNORM_SRGB behind a DX10 header

    written = []

    def put(folder, name, blob):
        p = mod / "Textures" / FOLDER / folder / name
        write_dds(p, blob)
        written.append(p)

    for letter in LETTERS:
        put("UniqueIconPlain", f"UniqueRevolver{letter}.dds", art)
        put("UniqueIconPlain", f"UniqueRevolver{letter}_m.dds", mask)
        put("UniqueIconLeak", f"UniqueRevolver{letter}.png.dds", art)
        put("UniqueIconLeak", f"UniqueRevolver{letter}_m.png.dds", mask)
        put("UniqueIconDx10", f"UniqueRevolver{letter}.png.dds", art)
        put("UniqueIconDx10", f"UniqueRevolver{letter}_m.png.dds", mask_dx10)

    print(f"fixture dir={mod}")
    print(f"fixture file=Defs/ThingDefs_UniqueIconFixture.xml defs={','.join(d[0] for d in layouts)}")
    print(f"fixture textures={len(written)} art_bytes={len(art)} mask_bytes={len(mask)}"
          f" mask_dx10_bytes={len(mask_dx10)} mips={mips}")
    for p in written:
        rel = p.relative_to(mod).as_posix()
        print(f"fixture file={rel} bytes={p.stat().st_size} sha256={hashlib.sha256(p.read_bytes()).hexdigest()[:16]}")

    if args.verify:
        bad = 0
        for p in written:
            hdr, sample, problems = verify(p)
            tag = "ok" if not problems else "BAD"
            if problems:
                bad += 1
            print(f"verify {tag} file={p.relative_to(mod).as_posix()} fourcc={hdr['fourcc']}"
                  f" dxgi={hdr['dxgi']} header={hdr['header']} data={hdr['data']}"
                  f" mips={hdr['mips']} sample={sample} problems={';'.join(problems) or '-'}")
        if bad:
            sys.exit(f"{bad} fixture(s) failed verification")


if __name__ == "__main__":
    main()
