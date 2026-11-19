"""Three input classes x three icon sizes, on real BC7 main + real BC4 mask textures.

Two shader variants are measured, because the game ships both and they are one statement apart:

  CutoutComplex    (map/world)   ...  if (f.a < 0.5) discard;   -> render()     table in TABLE.md
  CutoutComplexUI  (icon slot)   ...  same maths, NO discard    -> render_ui()  table in TABLE_UI.md

Input classes (what the material ends up holding for _MainTex / _MaskTex):

  passthrough   _MainTex = main,  _MaskTex = none        the "main image straight through" reference
  main_mask     _MainTex = main,  _MaskTex = real BC4    what a correctly bound unique weapon has
  main_mask_tint same, with _Color != white              does the mask change the picture at all?
  main_null     _MainTex = main,  _MaskTex = 1x1 black   what the engine substitutes when the mask
                                                         is missing / failed to load
  mask_as_main  _MainTex = mask,  _MaskTex = none        the two slots crossed
  full_swap     _MainTex = mask,  _MaskTex = main        the two slots crossed, both filled

Sizes 128 (native) -> 64 -> 32 model the icon slot shrinking: lod 0 / 1 / 2 with the encoder's own
mip chain, bilinear filtering inside the level, and the alpha test (where the shader has one) on the
*filtered* alpha.

Writes out/results.json, out/table*.csv, out/TABLE*.md, out/preview_*.png, out/compare_sheet*.png,
out/claims.json; exits non-zero if any numeric claim stops holding.
"""

import glob
import json
import os

from shader import NullMask, Tex, UI_BG, compare, ink, render, render_ui, stats

HERE = os.path.dirname(os.path.abspath(__file__))
FIX = os.path.join(HERE, "fixtures")
OUT = os.path.join(HERE, "out")

SIZES = [128, 64, 32]
WHITE = (1.0, 1.0, 1.0, 1.0)
# A representative non-white _Color, for the "does the tint actually bite" question.  Nothing in
# the defs forces a tint (GraphicData color defaults to white), so this is a parameter sweep, not a
# claim about a particular def.
TINT = (0.55, 0.55, 0.62, 1.0)

CASES = [
    # name,           main,   mask,     color
    ("passthrough", "main", None, WHITE),
    ("main_mask", "main", "mask", WHITE),
    ("main_mask_tint", "main", "mask", TINT),
    ("main_null", "main", "null", WHITE),
    ("mask_as_main", "mask", None, WHITE),
    ("full_swap", "mask", "main", WHITE),
]
# BC4 -> RGBA expansion variants, measured for the one case where they can disagree
VARIANTS = ["r", "alpha", "replicate"]


def pairs():
    out = []
    for m in sorted(glob.glob(os.path.join(FIX, "*.dds"))):
        if "_mask" in os.path.basename(m):
            continue
        name = os.path.basename(m)[:-4]
        mask = os.path.join(FIX, name + "_m_mask.dds")
        if os.path.exists(mask):
            out.append((name, m, mask))
    return out


def bindings(main_tex, masks):
    """-> [(case name, _MainTex, _MaskTex, _Color)] for every measured combination."""
    out = []
    for (cname, which_main, which_mask, color) in CASES:
        mt = masks["r"] if which_main == "mask" else main_tex
        if which_mask in (None, "null"):
            mk = NullMask()
        elif which_mask == "main":
            mk = main_tex
        else:
            mk = masks["r"]
        out.append((cname, mt, mk, color))
    for v in VARIANTS:
        out.append(("mask_as_main__%s" % v, masks[v], NullMask(), WHITE))
    return out


def to_image(px, size, bg=None):
    """Preview.  bg=None -> checkerboard marks transparent pixels (alpha-test variant).

    With bg, the pixels are already composited, so they are drawn as-is.
    """
    from PIL import Image
    im = Image.new("RGB", (size, size))
    buf = bytearray()
    for y in range(size):
        for x in range(size):
            p = px[y * size + x]
            if bg is not None or p[4]:
                buf += bytes((max(0, min(255, int(round(p[0] * 255)))),
                              max(0, min(255, int(round(p[1] * 255)))),
                              max(0, min(255, int(round(p[2] * 255))))))
            else:
                v = 40 if ((x // 4) + (y // 4)) % 2 else 70
                buf += bytes((v, v, v))
    im.frombytes(bytes(buf))
    return im


def sheet(path, cells, cols, cell=128):
    from PIL import Image, ImageDraw
    w, h = cell * cols, cell * len(cells)
    img = Image.new("RGB", (w, h), (24, 24, 28))
    d = ImageDraw.Draw(img)
    for i, (label, im) in enumerate(cells):
        r, c = divmod(i, cols)
        img.paste(im.resize((cell, cell), Image.Resampling.NEAREST), (c * cell, r * cell))
        d.rectangle([c * cell, r * cell, c * cell + cell - 1, r * cell + cell - 1],
                    outline=(60, 60, 66))
        d.text((c * cell + 3, r * cell + 2), label, fill=(255, 240, 160))
    img.save(path)


def aggregate(per_case, sizes, names, tag):
    out = []
    for size in sizes:
        for cname in names:
            rows = per_case.get((tag, size, cname))
            if not rows:
                continue
            n = len(rows)
            a = {
                "variant": tag, "size": size, "case": cname, "n_fixtures": n,
                "coverage": sum(r["coverage"] for r in rows) / n,
                "tile_luma": sum(r["tile_luma"] for r in rows) / n,
                "mean_luma_alive": sum(r["mean_luma_alive"] for r in rows) / n,
                "mean_r": sum(r["mean_rgb"][0] for r in rows) / n,
                "mean_g": sum(r["mean_rgb"][1] for r in rows) / n,
                "mean_b": sum(r["mean_rgb"][2] for r in rows) / n,
                "mean_alpha": sum(r["mean_alpha"] for r in rows) / n,
                "coverage_min": min(r["coverage"] for r in rows),
                "coverage_max": max(r["coverage"] for r in rows),
                "tile_luma_min": min(r["tile_luma"] for r in rows),
                "tile_luma_max": max(r["tile_luma"] for r in rows),
            }
            if "ink" in rows[0]:
                a["ink"] = sum(r["ink"] for r in rows) / n
                a["ink_min"] = min(r["ink"] for r in rows)
                a["ink_max"] = max(r["ink"] for r in rows)
            if "vs_passthrough" in rows[0]:
                a["d_rgb"] = sum(r["vs_passthrough"]["mean_abs_drgb"] for r in rows) / n
                a["max_delta"] = max(r["vs_passthrough"]["max_channel_delta"] for r in rows)
                a["iou"] = sum(r["vs_passthrough"]["iou"] for r in rows) / n
                a["flips"] = sum(r["vs_passthrough"]["status_flip_frac"] for r in rows) / n
                a["identical_all"] = all(r["vs_passthrough"]["identical"] for r in rows)
            out.append(a)
    return out


def table(agg, with_ink):
    header = ("| size | case | opaque % | tile luma | mean RGB | mean A | dRGB vs passthrough | "
              "max ch delta | IoU | status flips % |")
    sep = "|---" * (11 if with_ink else 10) + "|"
    if with_ink:
        header = header[:-1] + " ink |"
    lines = [header, sep]
    for a in agg:
        d = ("%.4f" % a["d_rgb"]) if "d_rgb" in a else "-"
        mx = ("%.3f" % a["max_delta"]) if "max_delta" in a else "-"
        iou = ("%.3f" % a["iou"]) if "iou" in a else "-"
        fl = ("%.2f" % (a["flips"] * 100)) if "flips" in a else "-"
        note = " (identical)" if a.get("identical_all") else ""
        row = ("| %d | `%s`%s | %.2f | %.4f | (%.3f, %.3f, %.3f) | %.3f | %s | %s | %s | %s |"
               % (a["size"], a["case"], note, a["coverage"] * 100, a["tile_luma"],
                  a["mean_r"], a["mean_g"], a["mean_b"], a["mean_alpha"], d, mx, iou, fl))
        if with_ink:
            row = row[:-1] + " %.4f |" % a["ink"]
        lines.append(row)
    return "\n".join(lines)


def main():
    os.makedirs(OUT, exist_ok=True)
    os.makedirs(os.path.join(OUT, "ui"), exist_ok=True)
    fixtures = pairs()
    if not fixtures:
        print("no fixtures found under %s" % FIX)
        return 1

    results = {"fixtures": [], "sizes": SIZES, "rows": [], "ui_rows": [], "ui_bg": UI_BG}
    per_case = {}
    first = fixtures[0][0]
    names = [c[0] for c in CASES] + ["mask_as_main__%s" % v for v in VARIANTS]

    for name, main_path, mask_path in fixtures:
        main_tex = Tex(main_path)
        masks = {v: Tex(mask_path, single_channel=v) for v in VARIANTS}
        hdr = main_tex.header
        assert hdr["width"] == 128 and hdr["height"] == 128, (name, hdr)
        results["fixtures"].append({
            "name": name,
            "main": os.path.basename(main_path), "main_format": hdr["format"],
            "mask": os.path.basename(mask_path), "mask_format": masks["r"].header["format"],
            "mask_coverage": sum(1 for row in masks["r"].level(0).px for p in row if p[0] > 0) / 16384,
        })

        for size in SIZES:
            base_alpha = base_ui = None
            for (cname, mt, mk, color) in bindings(main_tex, masks):
                px = render(mt, mk, size, src=128, color=color)
                if cname == "passthrough":
                    base_alpha = px
                st = stats(px)
                st.update({"fixture": name, "case": cname, "size": size})
                if cname != "passthrough":
                    st["vs_passthrough"] = compare(px, base_alpha)
                results["rows"].append(st)
                per_case.setdefault(("alpha", size, cname), []).append(st)
                if name == first:
                    to_image(px, size).save(os.path.join(OUT, "preview_%s_%d.png" % (cname, size)))

                pui = render_ui(mt, mk, size, src=128, color=color)
                if cname == "passthrough":
                    base_ui = pui
                sui = stats(pui)
                sui["ink"] = ink(pui)
                sui.update({"fixture": name, "case": cname, "size": size})
                if cname != "passthrough":
                    sui["vs_passthrough"] = compare(pui, base_ui)
                results["ui_rows"].append(sui)
                per_case.setdefault(("ui", size, cname), []).append(sui)
                if name == first:
                    to_image(pui, size, bg=True).save(
                        os.path.join(OUT, "ui", "preview_%s_%d.png" % (cname, size)))

    agg_alpha = aggregate(per_case, SIZES, names, "alpha")
    agg_ui = aggregate(per_case, SIZES, names, "ui")
    results["aggregate"] = agg_alpha
    results["ui_aggregate"] = agg_ui

    with open(os.path.join(OUT, "results.json"), "w") as f:
        json.dump(results, f, indent=2, sort_keys=True)

    for tag, agg in (("alpha", agg_alpha), ("ui", agg_ui)):
        with open(os.path.join(OUT, "table_%s.csv" % tag), "w") as f:
            f.write("size,case,n_fixtures,opaque_frac,tile_luma,ink,mean_r,mean_g,mean_b,mean_alpha,"
                    "d_rgb_vs_passthrough,max_channel_delta,iou,status_flip_frac,identical\n")
            for a in agg:
                f.write("%d,%s,%d,%.6f,%.6f,%s,%.6f,%.6f,%.6f,%.6f,%s,%s,%s,%s,%s\n" % (
                    a["size"], a["case"], a["n_fixtures"], a["coverage"], a["tile_luma"],
                    "%.6f" % a["ink"] if "ink" in a else "",
                    a["mean_r"], a["mean_g"], a["mean_b"], a["mean_alpha"],
                    "%.6f" % a["d_rgb"] if "d_rgb" in a else "",
                    "%.6f" % a["max_delta"] if "max_delta" in a else "",
                    "%.6f" % a["iou"] if "iou" in a else "",
                    "%.6f" % a["flips"] if "flips" in a else "",
                    "1" if a.get("identical_all") else "0"))

    t_alpha, t_ui = table(agg_alpha, False), table(agg_ui, True)
    with open(os.path.join(OUT, "TABLE.md"), "w") as f:
        f.write("# CutoutComplex (with `if (f.a < 0.5) discard;`)\n\n"
                "Mean over %d real BC7(main)+BC4(mask) unique-weapon pairs; shader maths transcribed "
                "from the shipped fragment stage, evaluated on CPU, mip chain from the DDS.\n\n"
                % len(fixtures))
        f.write(t_alpha + "\n\n## Per-fixture spread\n\n")
        f.write("| size | case | opaque % min..max | tile luma min..max |\n|---|---|---|---|\n")
        for a in agg_alpha:
            f.write("| %d | `%s` | %.2f..%.2f | %.4f..%.4f |\n"
                    % (a["size"], a["case"], a["coverage_min"] * 100, a["coverage_max"] * 100,
                       a["tile_luma_min"], a["tile_luma_max"]))
    with open(os.path.join(OUT, "TABLE_UI.md"), "w") as f:
        f.write("# CutoutComplexUI (the icon material: same maths, **no** discard, alpha blended)\n\n"
                "Composite over a dark button background (%.2f, %.2f, %.2f). `ink` = mean "
                "|composite - background| luma over the whole slot: 0 would mean \"an empty slot\".\n\n"
                % UI_BG)
        f.write("`opaque %` = fraction of pixels arriving at alpha >= 0.5.\n\n")
        f.write(t_ui + "\n\n## Per-fixture spread\n\n")
        f.write("| size | case | opaque % min..max | ink min..max |\n|---|---|---|---|\n")
        for a in agg_ui:
            f.write("| %d | `%s` | %.2f..%.2f | %.4f..%.4f |\n"
                    % (a["size"], a["case"], a["coverage_min"] * 100, a["coverage_max"] * 100,
                       a["ink_min"], a["ink_max"]))

    print("== CutoutComplex (discard) ==")
    print(t_alpha)
    print()
    print("== CutoutComplexUI (no discard, alpha blended) ==")
    print(t_ui)

    # comparison sheets, first fixture: rows = size, cols = case
    cols = ["passthrough", "main_mask", "main_mask_tint", "main_null",
            "mask_as_main__r", "mask_as_main__alpha"]
    from PIL import Image
    for tag, sub in (("alpha", ""), ("ui", "ui")):
        cells = []
        for size in SIZES:
            for cname in cols:
                p = os.path.join(OUT, sub, "preview_%s_%d.png" % (cname, size))
                if os.path.exists(p):
                    cells.append(("%s %dpx" % (cname, size), Image.open(p).convert("RGB")))
        if cells:
            sheet(os.path.join(OUT, "compare_sheet_%s.png" % tag), cells, len(cols))

    print()
    for size in SIZES:
        for cname in ("main_mask", "main_null", "mask_as_main__r", "mask_as_main__alpha"):
            rows = per_case[("alpha", size, cname)]
            lo = min(rows, key=lambda r: r["tile_luma"])
            hi = max(rows, key=lambda r: r["tile_luma"])
            print("%3d %-22s tile_luma %.4f (%s) .. %.4f (%s)"
                  % (size, cname, lo["tile_luma"], lo["fixture"], hi["tile_luma"], hi["fixture"]))
        for cname in ("main_mask", "main_null", "mask_as_main__r", "mask_as_main__alpha"):
            rows = per_case[("ui", size, cname)]
            print("    %-22s UI ink %.4f .. %.4f"
                  % (cname, min(r["ink"] for r in rows), max(r["ink"] for r in rows)))

    checks = claims(per_case)
    with open(os.path.join(OUT, "claims.json"), "w") as f:
        json.dump(checks, f, indent=2)
    print()
    failed = 0
    for c in checks:
        print("%s  %s\n        %s" % ("PASS" if c["pass"] else "FAIL", c["claim"], c["detail"]))
        failed += 0 if c["pass"] else 1
    print("\n%d/%d claims hold" % (len(checks) - failed, len(checks)))
    return 1 if failed else 0


# ------------------------------------------------------------------ claims

def claims(per_case):
    """Each claim is a numeric statement the run has to reproduce; CI fails if one stops holding."""
    checks = []

    def add(name, ok, detail):
        checks.append({"claim": name, "pass": bool(ok), "detail": detail})

    def A(size, cname):
        return per_case[("alpha", size, cname)]

    def U(size, cname):
        return per_case[("ui", size, cname)]

    for size in SIZES:
        for tag, get in (("discard", A), ("UI", U)):
            mm, mn = get(size, "main_mask"), get(size, "main_null")
            add("%s %dpx: real mask with white _Color is bit-identical to passthrough" % (tag, size),
                all(r["vs_passthrough"]["identical"] for r in mm),
                "max channel delta over %d fixtures = %g"
                % (len(mm), max(r["vs_passthrough"]["max_channel_delta"] for r in mm)))
            add("%s %dpx: null (black) mask is bit-identical to passthrough" % (tag, size),
                all(r["vs_passthrough"]["identical"] for r in mn),
                "max channel delta over %d fixtures = %g"
                % (len(mn), max(r["vs_passthrough"]["max_channel_delta"] for r in mn)))

        tint = A(size, "main_mask_tint")
        shape_same = all(r["vs_passthrough"]["iou"] == 1.0 and
                         r["vs_passthrough"]["status_flips"] == 0 for r in tint)
        dmin = min(r["vs_passthrough"]["mean_abs_drgb"] for r in tint)
        add("discard %dpx: tinted mask changes colour only (identical survivor set)" % size,
            shape_same and dmin > 0.02,
            "IoU=1.0, 0 flips for all %d; min mean|dRGB|=%.4f, max channel delta %.3f"
            % (len(tint), dmin, max(r["vs_passthrough"]["max_channel_delta"] for r in tint)))

        mr, base = A(size, "mask_as_main__r"), A(size, "passthrough")
        dark = min(r["tile_luma"] for r in mr) / min(r["tile_luma"] for r in base)
        add("discard %dpx: mask-as-main (R reading) fills the slot nearly black" % size,
            min(r["coverage"] for r in mr) == 1.0 and dark <= 0.30,
            "coverage=100%%, tile luma %.4f vs passthrough %.4f (x%.2f)"
            % (min(r["tile_luma"] for r in mr), min(r["tile_luma"] for r in base), dark))

        ma = A(size, "mask_as_main__alpha")
        add("discard %dpx: mask-as-main (alpha reading) survives only where the mask is" % size,
            max(r["tile_luma"] for r in ma) < 0.002 and
            all(r["mean_rgb"] == (0.0, 0.0, 0.0) for r in ma),
            "tile luma max %.5f, every surviving pixel pure black, coverage %.1f..%.1f%%"
            % (max(r["tile_luma"] for r in ma),
               min(r["coverage"] for r in ma) * 100, max(r["coverage"] for r in ma) * 100))

        # UI variant: the icon is never discarded, so the question becomes "does it differ from an
        # empty slot" (ink) and "does it cover the button" (opaque %).
        ui_base = U(size, "passthrough")
        ui_r = U(size, "mask_as_main__r")
        ui_a = U(size, "mask_as_main__alpha")
        b = sum(r["ink"] for r in ui_base) / len(ui_base)
        ir = sum(r["ink"] for r in ui_r) / len(ui_r)
        add("UI %dpx: mask-as-main (R reading) becomes an opaque dark box, not a missing icon" % size,
            all(r["coverage"] == 1.0 for r in ui_r) and ir > 1.5 * b,
            "opaque 100%%, ink %.4f vs empty-slot baseline %.4f (x%.1f), mean R=%.3f"
            % (ir, b, ir / b, sum(r["mean_rgb"][0] for r in ui_r) / len(ui_r)))
        ia = sum(r["ink"] for r in ui_a) / len(ui_a)
        add("UI %dpx: mask-as-main (alpha reading) leaves almost no trace on the button" % size,
            ia < 0.40 * b,
            "ink %.4f vs baseline %.4f (%.0f%%), opaque %.1f..%.1f%%"
            % (ia, b, 100 * ia / b,
               min(r["coverage"] for r in ui_a) * 100, max(r["coverage"] for r in ui_a) * 100))

    c128 = sum(r["coverage"] for r in A(128, "passthrough")) / len(A(128, "passthrough"))
    c32 = sum(r["coverage"] for r in A(32, "passthrough")) / len(A(32, "passthrough"))
    rel = abs(c32 - c128) / c128
    add("icon size barely moves coverage (128 -> 32)", rel < 0.05,
        "opaque fraction %.2f%% -> %.2f%% (%.1f%% relative)" % (c128 * 100, c32 * 100, rel * 100))
    return checks


if __name__ == "__main__":
    raise SystemExit(main())
