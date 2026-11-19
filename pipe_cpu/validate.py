"""Decoder checks that have to pass before the pixel numbers mean anything.

1. BC4 -- the hand-written block decoder is compared against Pillow's independent BC4 decode of
   the same file.  BC4 has one correct answer, so this must be a bit-exact match.
2. BC7 level slicing -- Pillow decodes each level from a synthesised single-mip DDS.  If the block
   walk ever drifted (padding between levels, wrong level size), the decoded level would be
   garbage; a garbage level cannot correlate ~0.99 with a box filter of the level above it.  So
   correlation against the box-filtered level 0 is a real test, not decoration.
"""

import math

import dds
from shader import Tex

FAILS = []


def check(name, ok, detail):
    print("%-46s %s  %s" % (name, "PASS" if ok else "FAIL", detail))
    if not ok:
        FAILS.append(name)


def bc4_matches_pillow(path):
    import io
    from PIL import Image
    data = open(path, "rb").read()
    hdr, levels = dds.split_levels(data)
    w, h, raw = levels[0]
    mine = dds.decode_bc4(raw, w, h)
    im = Image.open(io.BytesIO(data))
    im.load()
    theirs = im.convert("L").tobytes()
    diff = 0
    maxd = 0
    for y in range(h):
        for x in range(w):
            a = int(round(mine[y][x] * 255))
            b = theirs[y * w + x]
            if a != b:
                diff += 1
                maxd = max(maxd, abs(a - b))
    check("BC4 own decoder vs Pillow (%s)" % path.split("/")[-1],
          diff == 0, "mismatched=%d maxdelta=%d of %d" % (diff, maxd, w * h))


def box_down(px):
    h, w = len(px), len(px[0])
    out = []
    for y in range(0, h - 1, 2):
        row = []
        for x in range(0, w - 1, 2):
            row.append(tuple(
                (px[y][x][c] + px[y][x + 1][c] + px[y + 1][x][c] + px[y + 1][x + 1][c]) / 4.0
                for c in range(4)))
        out.append(row)
    return out


def corr(a, b, ch):
    """Pearson correlation of one channel between two equally sized levels."""
    xs = [p[ch] for row in a for p in row]
    ys = [p[ch] for row in b for p in row]
    return corr1d(xs, ys)


def corr1d(xs, ys):
    n = len(xs)
    mx, my = sum(xs) / n, sum(ys) / n
    num = sum((x - mx) * (y - my) for x, y in zip(xs, ys))
    dx = math.sqrt(sum((x - mx) ** 2 for x in xs))
    dy = math.sqrt(sum((y - my) ** 2 for y in ys))
    if dx == 0 or dy == 0:
        return 1.0 if dx == dy else 0.0
    return num / (dx * dy)


def nondegenerate(path):
    """A decode that collapses to a constant (all zero) is a bug, not a texture.

    This is the check that catches a mis-built DDS wrapper: Pillow then takes a DXT path with the
    wrong block size and yields near-zero pixels, and a correlation between two zero arrays would
    happily report 1.0.
    """
    t = Tex(path)
    lv = t.level(0).px
    ch = 3 if t.header["dxgi"] == 98 else 0
    vals = [p[ch] for row in lv for p in row]
    uniq = len(set(round(v, 3) for v in vals))
    mean = sum(vals) / len(vals)
    check("level0 non-constant (%s)" % path.split("/")[-1],
          uniq > 3 and mean > 0.005,
          "distinct=%d mean=%.4f" % (uniq, mean))


def mip_chain_consistent(path):
    t = Tex(path)
    l0 = t.level(0).px
    got = t.level(1).px
    pred = box_down(l0)
    if t.header["dxgi"] == 98:
        c = corr(pred, got, 3)
    else:  # BC4: the expansion put the data in channel 0
        c = corr(pred, got, 0)
    check("mip1 == box(mip0) correlation (%s)" % path.split("/")[-1],
          c > 0.9, "pearson r=%.4f" % c)


def main():
    import glob
    import os
    here = os.path.dirname(os.path.abspath(__file__))
    masks = sorted(glob.glob(os.path.join(here, "fixtures", "*_mask.dds")))
    mains = sorted(glob.glob(os.path.join(here, "fixtures", "*.dds")))
    mains = [m for m in mains if "_mask" not in m]
    for m in masks:
        bc4_matches_pillow(m)
    for m in mains + masks:
        nondegenerate(m)
        mip_chain_consistent(m)
    print()
    if FAILS:
        print("VALIDATION FAILED: %s" % ", ".join(FAILS))
        return 1
    print("VALIDATION OK (%d checks)" % (len(masks) * 2 + len(mains + masks) * 2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
