#!/usr/bin/env python3
# Independent check of the PNG the in-process hook wrote.
#
# The hook encodes the texture with its own deflate/CRC32 code; this script decodes that PNG with
# Pillow (a different decoder), decodes the shipped Odyssey asset the icon is supposed to be, and
# compares them. A placeholder, a mask, a different variant or a broken encoder all show up here as
# a size mismatch, a different alpha silhouette, or a large channel delta.
#
# usage: verify_icon.py <out/icon.json> <repo root>   ->  key=value lines on stdout
import json
import sys
from pathlib import Path

from PIL import Image, ImageChops

ICON = Path(sys.argv[1] if len(sys.argv) > 1 else "out/icon.json")
REPO = Path(sys.argv[2] if len(sys.argv) > 2 else ".")
ASSET_DIR = REPO / "game/Resources/Textures/Things/Item/Equipment/WeaponRanged/UniqueWeapons/Revolver"

# The port premultiplies on import and unpremultiplies on GetPixels32, so colours under partial
# alpha round-trip with a few counts of error. 16/255 is far below what a wrong image produces.
MAX_DELTA = 16
MAX_MEAN_DELTA = 1.5

failures = []


def emit(key, value):
    print("verify.%s=%s" % (key, value))


def main():
    if not ICON.exists():
        emit("result", "FAIL")
        print("  - %s missing" % ICON)
        return 1
    icon = json.loads(ICON.read_text())
    name = icon.get("texture") or ""
    shot = Path(icon.get("png") or "")
    # the artifact records the absolute path from the runner; when the artifact is inspected
    # elsewhere the PNG sits next to icon.json
    if shot and not shot.exists() and (ICON.parent / shot.name).exists():
        shot = ICON.parent / shot.name
    emit("texture", name)
    emit("thingId", icon.get("thingId"))

    if not name or name.endswith("_m"):
        failures.append("icon.json names %r, which is not a plain variant" % name)
    if not shot.exists():
        failures.append("png %s missing" % shot)
        emit("result", "FAIL")
        for f in failures:
            print("  -", f)
        return 1

    src_path = ASSET_DIR / (name + ".png")
    if not src_path.exists():
        failures.append("shipped asset %s missing" % src_path)
        emit("result", "FAIL")
        for f in failures:
            print("  -", f)
        return 1

    got = Image.open(shot).convert("RGBA")
    want = Image.open(src_path).convert("RGBA")
    emit("source", str(src_path.relative_to(REPO)))
    emit("size", "%dx%d" % got.size)
    emit("sourceSize", "%dx%d" % want.size)

    if got.size != want.size:
        failures.append("size %s != shipped %s" % (got.size, want.size))

    alpha_identical = got.getchannel("A").tobytes() == want.getchannel("A").tobytes()
    emit("alphaIdentical", alpha_identical)
    if not alpha_identical:
        failures.append("alpha silhouette differs from the shipped asset")

    diff = ImageChops.difference(got.convert("RGB"), want.convert("RGB"))
    data = diff.tobytes()
    max_delta = max(data) if data else 0
    mean_delta = round(sum(data) / len(data), 3) if data else 0.0
    emit("maxChannelDelta", max_delta)
    emit("meanAbsDelta", mean_delta)
    if max_delta > MAX_DELTA:
        failures.append("max channel delta %d > %d" % (max_delta, MAX_DELTA))
    if mean_delta > MAX_MEAN_DELTA:
        failures.append("mean abs delta %.3f > %.3f" % (mean_delta, MAX_MEAN_DELTA))

    # and it must not be the mask: comparing with the _m asset has to be much worse
    mask_path = ASSET_DIR / (name + "_m.png")
    if mask_path.exists():
        mask = Image.open(mask_path).convert("RGBA")
        mdiff = ImageChops.difference(got.convert("RGB"), mask.convert("RGB"))
        mmax = max(mdiff.tobytes()) if mdiff.tobytes() else 0
        emit("maskMaxChannelDelta", mmax)
        if mmax <= MAX_DELTA:
            failures.append("icon is indistinguishable from the %s mask" % name)

    emit("result", "FAIL" if failures else "OK")
    for f in failures:
        print("  -", f)
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
