#!/usr/bin/env python3
# Independent check of the forced-colour experiment the in-process hook ran.
#
# The hook takes the same Gun_Revolver_Unique twice - once with CompUniqueWeapon's color removed,
# once with UniqueWeapon_Red forced through Thing.DrawColor (the value CompUniqueWeapon.ForceColor
# returns). It snapshots texture name/variant/size/pixels/material and the clone's subGraphics.
# This script decodes both PNGs with Pillow (a different decoder than the hook's encoder), compares
# them with each other and with the shipped Odyssey asset, and re-checks the clone's structure.
#
# usage: verify_color.py <out/color.json> <repo root>   ->  key=value lines on stdout
import hashlib
import json
import sys
from pathlib import Path

from PIL import Image, ImageChops

COLOR = Path(sys.argv[1] if len(sys.argv) > 1 else "out/color.json")
REPO = Path(sys.argv[2] if len(sys.argv) > 2 else ".")
ASSET_DIR = REPO / "game/Resources/Textures/Things/Item/Equipment/WeaponRanged/UniqueWeapons/Revolver"

MAX_DELTA = 16        # same tolerance as verify_icon.py (premultiply round-trip)
MAX_MEAN_DELTA = 1.5

failures = []


def emit(key, value):
    print("verify.color.%s=%s" % (key, value))


def fail(msg):
    failures.append(msg)


def png_path(entry):
    p = Path(entry.get("png") or "")
    if p and not p.exists() and (COLOR.parent / p.name).exists():
        p = COLOR.parent / p.name
    return p


def compare_images(got, want):
    diff = ImageChops.difference(got.convert("RGB"), want.convert("RGB"))
    data = diff.tobytes()
    return (max(data) if data else 0), (round(sum(data) / len(data), 3) if data else 0.0)


def main():
    if not COLOR.exists():
        emit("result", "FAIL")
        print("  - %s missing" % COLOR)
        return 1
    doc = json.loads(COLOR.read_text())
    white, tinted = doc.get("white") or {}, doc.get("tinted") or {}
    compare = doc.get("compare") or {}
    structure = doc.get("structure") or {}
    matsingle = doc.get("matSingle") or {}

    emit("texture", tinted.get("texture"))
    emit("whiteTexture", white.get("texture"))
    emit("variantIndex", tinted.get("variantIndex"))
    emit("expectedIndex", tinted.get("expectedIndex"))
    emit("size", tinted.get("size"))
    emit("whiteMeanRgb", white.get("meanRgb"))
    emit("tintedMeanRgb", tinted.get("meanRgb"))
    emit("material", tinted.get("material"))
    emit("materialTex", tinted.get("materialTex"))
    emit("materialMask", tinted.get("materialMask"))
    emit("materialColor", tinted.get("materialColor"))
    emit("whiteMaterialColor", white.get("materialColor"))
    emit("forcedColor", doc.get("forcedColor"))
    emit("subGraphics", structure.get("tintedSubCount"))
    emit("tintedPath", structure.get("tintedPath"))
    emit("defPath", structure.get("defPath"))

    # --- the two snapshots must describe the same texture and the same variant ---------------
    for key in ("textureSame", "variantSame", "sizeSame", "pixelsSame", "sha256Same", "meanRgbSame",
                "badTexSame", "maskSame", "materialSame", "materialTexSame", "tintApplied",
                "subgraphicsFive", "pathSame", "subPathsSame", "subTexturesSame", "subColorsTinted"):
        if compare.get(key) is not True:
            fail("color.json compare.%s is %r, expected true" % (key, compare.get(key)))

    name = tinted.get("texture") or ""
    if not name:
        fail("tinted texture is empty")
    if white.get("texture") != name:
        fail("white texture %r != tinted texture %r" % (white.get("texture"), name))
    if tinted.get("variantIndex") != tinted.get("expectedIndex"):
        fail("tinted variantIndex %r != expectedIndex %r" % (tinted.get("variantIndex"), tinted.get("expectedIndex")))
    if tinted.get("variantIndex") != white.get("variantIndex"):
        fail("tinted variantIndex %r != white variantIndex %r" % (tinted.get("variantIndex"), white.get("variantIndex")))
    if tinted.get("size") != "128x128" or white.get("size") != "128x128":
        fail("icon size %r/%r, expected 128x128" % (white.get("size"), tinted.get("size")))
    for side, entry in (("white", white), ("tinted", tinted)):
        if entry.get("badTex") is not False:
            fail("%s badTex=%r" % (side, entry.get("badTex")))
        if entry.get("mask") is not False:
            fail("%s mask=%r" % (side, entry.get("mask")))
        if entry.get("placeholder") is not False:
            fail("%s placeholder=%r" % (side, entry.get("placeholder")))

    # --- the forced tint must be in the material, not baked into the texture ------------------
    wc, tc, fc = white.get("materialColor"), tinted.get("materialColor"), doc.get("forcedColor")
    if not wc or not tc or not fc:
        fail("missing materialColor/forcedColor in color.json")
    else:
        white_flat = max(abs(wc[i] - 1.0) for i in range(3))
        tint_diff = max(abs(tc[i] - fc[i]) for i in range(3))
        emit("tintMaxChannelDiff", round(tint_diff, 6))
        emit("whiteMaterialDistanceFromWhite", round(white_flat, 6))
        if white_flat > 0.01:
            fail("white material is not white: %r" % (wc,))
        if tint_diff > 0.01:
            fail("tinted material %r is not the forced colour %r" % (tc, fc))
        if max(abs(tc[i] - wc[i]) for i in range(3)) <= 0.01:
            fail("tinted material colour equals the white one; the tint was not applied")
    if not str(tinted.get("materialMask") or "").endswith("_m"):
        fail("tinted material mask is %r, expected a *_m texture" % (tinted.get("materialMask"),))

    # --- clone structure ---------------------------------------------------------------------
    if structure.get("tintedSubCount") != 5 or structure.get("whiteSubCount") != 5 or structure.get("defSubCount") != 5:
        fail("subGraphics counts def/white/tinted = %r/%r/%r, expected 5/5/5" %
             (structure.get("defSubCount"), structure.get("whiteSubCount"), structure.get("tintedSubCount")))
    if structure.get("tintedPath") != structure.get("defPath"):
        fail("tinted path %r != def path %r" % (structure.get("tintedPath"), structure.get("defPath")))
    sub_paths = structure.get("tintedSubPaths") or []
    sub_tex = structure.get("tintedSubTextures") or []
    if len(sub_paths) != 5 or len(sub_tex) != 5:
        fail("tinted subPaths/subTextures count = %d/%d, expected 5/5" % (len(sub_paths), len(sub_tex)))
    else:
        if len(set(sub_paths)) != 5:
            fail("tinted subPaths are not five distinct paths: %r" % (sub_paths,))
        for i, (p, t) in enumerate(zip(sub_paths, sub_tex)):
            if Path(p).name != t:
                fail("tinted sub[%d] path %r does not end in its texture %r" % (i, p, t))
        prefix = structure.get("defPath") or ""
        if prefix and any(not str(p).startswith(prefix + "/") for p in sub_paths):
            fail("tinted subPaths do not sit under %r: %r" % (prefix, sub_paths))
    if matsingle.get("matAtTexture") != name:
        fail("MatAt(rot, thing) texture %r != thing variant %r" % (matsingle.get("matAtTexture"), name))
    if matsingle.get("expectedTexture") != name:
        fail("matSingle.expectedTexture %r != %r" % (matsingle.get("expectedTexture"), name))

    # --- PNGs: decode both, compare with each other and the shipped asset --------------------
    wp, tp = png_path(white), png_path(tinted)
    emit("whitePng", wp.name if wp else "")
    emit("tintedPng", tp.name if tp else "")
    if not wp.exists() or not tp.exists():
        fail("icon PNGs missing: white=%s tinted=%s" % (wp, tp))
        emit("result", "FAIL")
        for f in failures:
            print("  -", f)
        return 1

    for side, path, entry in (("white", wp, white), ("tinted", tp, tinted)):
        want_sha = entry.get("sha256") or ""
        got_sha = hashlib.sha256(path.read_bytes()).hexdigest()
        emit("%sSha256" % side, got_sha)
        if want_sha and got_sha != want_sha:
            fail("%s PNG sha256 %s != color.json %s" % (side, got_sha, want_sha))

    wimg = Image.open(wp).convert("RGBA")
    timg = Image.open(tp).convert("RGBA")
    emit("decodedSize", "%dx%d" % timg.size)
    if wimg.size != timg.size:
        fail("white %r and tinted %r sizes differ" % (wimg.size, timg.size))
    if wimg.getchannel("A").tobytes() != timg.getchannel("A").tobytes():
        fail("white and tinted alpha differ")
    max_d, mean_d = compare_images(wimg, timg)
    emit("maxChannelDelta", max_d)
    emit("meanAbsDelta", mean_d)
    if max_d != 0 or mean_d != 0:
        fail("white and tinted pixels differ: max=%d mean=%.3f" % (max_d, mean_d))

    src = ASSET_DIR / (name + ".png")
    if not src.exists():
        fail("shipped asset %s missing" % src)
    else:
        want = Image.open(src).convert("RGBA")
        emit("source", str(src.relative_to(REPO)))
        emit("sourceSize", "%dx%d" % want.size)
        if timg.size != want.size:
            fail("tinted size %r != shipped %r" % (timg.size, want.size))
        smax, smean = compare_images(timg, want)
        emit("shippedMaxChannelDelta", smax)
        emit("shippedMeanAbsDelta", smean)
        if smax > MAX_DELTA:
            fail("tinted PNG max channel delta %d > %d from shipped asset" % (smax, MAX_DELTA))
        if smean > MAX_MEAN_DELTA:
            fail("tinted PNG mean abs delta %.3f > %.3f from shipped asset" % (smean, MAX_MEAN_DELTA))
        mask_path = ASSET_DIR / (name + "_m.png")
        if mask_path.exists():
            mask = Image.open(mask_path).convert("RGBA")
            mmax, _ = compare_images(timg, mask)
            emit("maskMaxChannelDelta", mmax)
            if mmax <= MAX_DELTA:
                fail("tinted icon is indistinguishable from the %s mask" % name)

    # the hook's own failed list has to be empty for a clean colour verdict
    failed = doc.get("failed") or []
    emit("hookFailed", ",".join(failed) if failed else "<none>")
    if failed:
        fail("hook reported failed assertions: %s" % ",".join(failed))

    emit("result", "FAIL" if failures else "OK")
    for f in failures:
        print("  -", f)
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
