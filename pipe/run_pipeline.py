#!/usr/bin/env python3
"""End-to-end, offline check of the icon pipeline: real DDS -> BC7/BC4 decode -> real GLSL on
lavapipe -> pixel statistics -> assertions.

Everything here runs on a plain ubuntu x64 box: no game install, no phone, no GPU.

Cases (all 128x128, uv 0..1, _MainTex_ST = (0,0,1,1), _Color = white, _ColorTwo = white,
vertex colour = the stuff colour from the device log):

  world_albedo_mask      CutoutComplex    UniqueRevolverA + its real BC4 mask
  world_albedo_nullmask  CutoutComplex    UniqueRevolverA + engine's 1x1 black mask
  world_mask_as_main     CutoutComplex    the BC4 mask bound as _MainTex, no mask
  ui_*                   CutoutComplexUI  the same three, without `if (f.a < 0.5) discard;`

Assertions:
  1. albedo+real mask covers ~12% of the tile (12.2% for this icon)
  2. albedo+null mask covers exactly the same pixels, with a clearly different colour
     (the mask tints, it does not gate alpha)
  3. mask-as-main covers ~100% of the tile at a very low luminance
     (BC4 samples as (R,0,0,1), so alpha is 1 everywhere and the icon is an opaque near-black square)
  4. the UI shader paints strictly more pixels than the world shader (it has no alpha test) while
     agreeing on the a>=128 pixel set: the two shader variants are provably different files
  5. decode: 12 real DDS, BC7+BC4, BC4 block decoder == Pillow, pinned RGBA checksums

usage:
  python3 pipe/run_pipeline.py [--build-dir build] [--assets assets/iconpipe]
                               [--summary build/summary.md] [--json build/iconpipe-report.json]
                               [--inject-fault none|coverage|color|luma]
"""
import argparse
import json
import math
import os
import shutil
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
W = H = 128

CASES = [
    # name, shader, main asset, mask asset (None = unbound -> engine substitutes 1x1 black)
    ("world_albedo_mask", "world", "od_UniqueRevolverA", "od_UniqueRevolverA_m"),
    ("world_albedo_nullmask", "world", "od_UniqueRevolverA", None),
    ("world_mask_as_main", "world", "od_UniqueRevolverA_m", None),
    ("ui_albedo_mask", "ui", "od_UniqueRevolverA", "od_UniqueRevolverA_m"),
    ("ui_albedo_nullmask", "ui", "od_UniqueRevolverA", None),
    ("ui_mask_as_main", "ui", "od_UniqueRevolverA_m", None),
]

SHADER_SRC = {
    "world": "pipe/shaders/CutoutComplex.frag.glsl",
    "ui": "pipe/shaders/CutoutComplexUI.frag.glsl",
}


def run(cmd, **kw):
    return subprocess.run(cmd, cwd=ROOT, capture_output=True, text=True, **kw)


def build(build_dir):
    os.makedirs(os.path.join(ROOT, build_dir), exist_ok=True)
    steps = [
        (["glslangValidator", "-V", "pipe/render/quad.vert", "-o", f"{build_dir}/quad.spv"], "vert"),
        (["glslangValidator", "-V", SHADER_SRC["world"], "-o", f"{build_dir}/cutout.spv"], "world"),
        (["glslangValidator", "-V", SHADER_SRC["ui"], "-o", f"{build_dir}/cutout_ui.spv"], "ui"),
        (["cc", "-O2", "-std=c11", "-Wall", "-Wextra", "-o", f"{build_dir}/harness",
          "pipe/render/harness.c", "-lvulkan", "-lm"], "harness"),
    ]
    for cmd, what in steps:
        r = run(cmd)
        if r.returncode != 0:
            print(f"build: {what} failed\n{r.stdout}\n{r.stderr}", file=sys.stderr)
            raise SystemExit(2)
        print(f"build: {what} ok")


def decode(build_dir, assets):
    r = run([sys.executable, "pipe/decode_dds.py", "--assets", assets,
             "--out", f"{build_dir}/decoded", "--checksums", f"{assets}/checksums.json",
             "--json", f"{build_dir}/decode.json"])
    print(r.stdout.rstrip())
    if r.returncode != 0:
        print(r.stderr.rstrip(), file=sys.stderr)
        raise SystemExit(2)
    with open(os.path.join(ROOT, build_dir, "decode.json")) as f:
        return json.load(f)


def render_case(build_dir, name, shader, main, mask):
    mask_arg = f"{build_dir}/decoded/{mask}.rgba" if mask else "-"
    cmd = [f"{build_dir}/harness", f"{build_dir}/{'cutout_ui' if shader == 'ui' else 'cutout'}.spv",
           f"{build_dir}/quad.spv", f"{build_dir}/decoded/{main}.rgba", str(W), str(H), mask_arg,
           "0", "0", "1", "1", f"{build_dir}/{name}.ppm"]
    r = run(cmd)
    if r.returncode != 0:
        print(f"render {name}: harness failed\n{r.stdout}\n{r.stderr}", file=sys.stderr)
        raise SystemExit(2)
    stats = None
    device = ""
    for line in r.stdout.splitlines():
        if line.startswith("STATS "):
            stats = json.loads(line[len("STATS "):])
        elif line.startswith("harness: device "):
            device = line.split("device ", 1)[1]
    if stats is None:
        print(f"render {name}: no STATS line\n{r.stdout}", file=sys.stderr)
        raise SystemExit(2)
    stats.update({"name": name, "shader": shader, "main": main, "mask": mask or "null(1x1 black)",
                  "device": device})
    return stats


def pct(s):
    return 100.0 * s["coverage"]


def color_dist(a, b):
    return math.sqrt(sum((a["mean_rgb"][i] - b["mean_rgb"][i]) ** 2 for i in range(3)))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--build-dir", default="build")
    ap.add_argument("--assets", default="assets/iconpipe")
    ap.add_argument("--summary", default="build/summary.md")
    ap.add_argument("--json", default="build/iconpipe-report.json")
    ap.add_argument("--inject-fault", default="none",
                    choices=["none", "coverage", "color", "luma"],
                    help="perturb one measured value before asserting, to prove the failure path")
    ap.add_argument("--skip-build", action="store_true")
    args = ap.parse_args()

    t0 = time.time()
    if not args.skip_build:
        build(args.build_dir)
    dec = decode(args.build_dir, args.assets)

    results = {}
    for name, shader, main, mask in CASES:
        results[name] = render_case(args.build_dir, name, shader, main, mask)
        s = results[name]
        print(f"case {name:22s} shader={shader:5s} covered={s['covered']:5d}/{s['total']} "
              f"({pct(s):6.3f}%) meanRGB=({s['mean_rgb'][0]:6.1f},{s['mean_rgb'][1]:6.1f},"
              f"{s['mean_rgb'][2]:6.1f}) luma={s['mean_luma']:6.2f} "
              f"alpha={s['alpha_min']}/{s['alpha_mean']:.1f}/{s['alpha_max']}")

    A = results["world_albedo_mask"]
    B = results["world_albedo_nullmask"]
    C = results["world_mask_as_main"]
    Aui = results["ui_albedo_mask"]
    Bui = results["ui_albedo_nullmask"]
    Cui = results["ui_mask_as_main"]

    if args.inject_fault != "none":
        # deliberately break ONE value so the assertion report shows the diff path on CI
        print(f"fault injection: {args.inject_fault}")
        if args.inject_fault == "coverage":
            A = dict(A, covered=A["covered"] + 600, coverage=(A["covered"] + 600) / A["total"])
        elif args.inject_fault == "color":
            B = dict(B, mean_rgb=list(A["mean_rgb"]))
        elif args.inject_fault == "luma":
            C = dict(C, mean_luma=42.0)

    assets = dec["assets"]
    bc7 = [a for a in assets if a["format"] == "BC7"]
    bc4 = [a for a in assets if a["format"] == "BC4"]
    bc4_mismatch = sum(a["bc4_vs_pillow_mismatches"] or 0 for a in bc4)
    checksum_drift = dec["checksum_drift"]

    checks = []

    def check(cid, desc, ok, expected, actual, detail=""):
        checks.append({"id": cid, "desc": desc, "ok": bool(ok),
                       "expected": expected, "actual": actual, "detail": detail})

    # ---- decode ----------------------------------------------------------------
    check("decode.count", "12 real DDS assets decoded (6 BC7 albedo, 6 BC4 mask)",
          len(assets) == 12 and len(bc7) == 6 and len(bc4) == 6,
          "12 assets = 6 BC7 (98) + 6 BC4 (80)",
          f"{len(assets)} assets = {len(bc7)} BC7 + {len(bc4)} BC4")
    check("decode.bc4", "own BC4 block decoder matches Pillow bit for bit",
          bc4_mismatch == 0, "0 differing texels", f"{bc4_mismatch} differing texels")
    check("decode.checksums", "decoded RGBA matches the pinned sha256 set",
          not checksum_drift, "no drift", f"{len(checksum_drift)} drifted" if checksum_drift else "no drift",
          "; ".join(checksum_drift))
    check("device.software", "rendered on a software Vulkan device (lavapipe/llvmpipe)",
          "(cpu)" in A["device"], "CPU device", A["device"])

    # ---- 1. albedo + real mask ~= 12% -----------------------------------------
    check("coverage.real_mask", "albedo + real BC4 mask covers ~12% of the tile",
          11.5 <= pct(A) <= 13.5, "11.5%..13.5% (~12%)", f"{pct(A):.3f}%",
          f"covered {A['covered']}/{A['total']}")

    # ---- 2. null mask: same coverage, different colour -------------------------
    check("coverage.null_mask_same", "albedo + null mask covers exactly the same pixels",
          A["covered"] == B["covered"], f"{A['covered']} px", f"{B['covered']} px",
          f"delta {B['covered'] - A['covered']} px")
    d = color_dist(A, B)
    check("color.null_mask_differs", "null mask changes colour (mask tints, alpha untouched)",
          d > 15.0, "mean-RGB distance > 15", f"distance {d:.2f}",
          f"real mask RGB ({A['mean_rgb'][0]:.1f},{A['mean_rgb'][1]:.1f},{A['mean_rgb'][2]:.1f}) vs "
          f"null ({B['mean_rgb'][0]:.1f},{B['mean_rgb'][1]:.1f},{B['mean_rgb'][2]:.1f})")

    # ---- 3. mask bound as _MainTex: 100% coverage, near-black ------------------
    check("coverage.mask_as_main", "BC4 mask bound as _MainTex covers ~100% of the tile",
          pct(C) >= 99.5, ">= 99.5%", f"{pct(C):.3f}%")
    check("luma.mask_as_main", "mask-as-main output is near black (low luminance)",
          C["mean_luma"] <= 8.0, "mean luma <= 8/255", f"{C['mean_luma']:.2f}/255",
          f"mean RGB ({C['mean_rgb'][0]:.2f},{C['mean_rgb'][1]:.2f},{C['mean_rgb'][2]:.2f})")
    check("swizzle.bc4", "BC4 sampled as (R,0,0,1): green and blue are exactly 0",
          C["mean_rgb"][1] == 0.0 and C["mean_rgb"][2] == 0.0,
          "G=0, B=0", f"G={C['mean_rgb'][1]:.2f}, B={C['mean_rgb'][2]:.2f}")

    # ---- 4. the two shader variants are provably different ---------------------
    check("shader.ui_no_discard", "UI shader paints every a>0 texel, world shader discards a<0.5",
          Aui["covered"] > A["covered"] and Aui["a50"] == A["a50"],
          f"ui covered > {A['covered']} and ui a50 == {A['a50']}",
          f"ui covered {Aui['covered']}, ui a50 {Aui['a50']}")
    check("alpha.world_discard", "world shader never emits alpha below 128",
          A["alpha_min"] >= 128, "alpha_min >= 128", f"alpha_min {A['alpha_min']}",
          f"ui alpha_min {Aui['alpha_min']} (no discard)")
    check("coverage.ui_null_mask_same", "UI: null mask covers the same pixels as the real mask",
          Aui["covered"] == Bui["covered"], f"{Aui['covered']} px", f"{Bui['covered']} px")
    du = color_dist(Aui, Bui)
    check("color.ui_null_mask_differs", "UI: null mask changes colour",
          du > 15.0, "mean-RGB distance > 15", f"distance {du:.2f}")
    check("coverage.ui_mask_as_main", "UI: mask as _MainTex covers 100% at low luma",
          pct(Cui) >= 99.5 and Cui["mean_luma"] <= 8.0,
          ">= 99.5% and luma <= 8", f"{pct(Cui):.3f}% / luma {Cui['mean_luma']:.2f}")

    failed = [c for c in checks if not c["ok"]]
    display = {"world_albedo_mask": A, "world_albedo_nullmask": B, "world_mask_as_main": C,
               "ui_albedo_mask": Aui, "ui_albedo_nullmask": Bui, "ui_mask_as_main": Cui}

    # ---- outputs ---------------------------------------------------------------
    report = {
        "generated_at": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
        "run_id": os.environ.get("GITHUB_RUN_ID", "local"),
        "run_url": (f"{os.environ.get('GITHUB_SERVER_URL', 'https://github.com')}/"
                    f"{os.environ.get('GITHUB_REPOSITORY', '')}/actions/runs/"
                    f"{os.environ.get('GITHUB_RUN_ID', '')}")
        if os.environ.get("GITHUB_RUN_ID") else "local",
        "git_sha": os.environ.get("GITHUB_SHA", run(["git", "rev-parse", "HEAD"]).stdout.strip()),
        "fault_injected": args.inject_fault,
        "device": A["device"],
        "cases": display,
        "assets": assets,
        "checks": checks,
        "passed": not failed,
    }
    os.makedirs(os.path.dirname(os.path.join(ROOT, args.json)) or ".", exist_ok=True)
    with open(os.path.join(ROOT, args.json), "w") as f:
        json.dump(report, f, indent=2)

    md = []
    md.append("# icon pipeline — real DDS -> BC7/BC4 -> real GLSL on lavapipe\n")
    if os.environ.get("GITHUB_RUN_ID"):
        md.append(f"Run: [{os.environ['GITHUB_RUN_ID']}]({report['run_url']})  ")
    md.append(f"Commit `{report['git_sha'][:12]}` · device `{report['device']}` · "
              f"assets `{args.assets}` · fault `{args.inject_fault}`\n")
    md.append("| case | shader | _MainTex | _MaskTex | covered | coverage | mean RGB | mean luma | alpha min/mean/max |")
    md.append("|---|---|---|---|---|---|---|---|---|")
    for name, shader, main, mask in CASES:
        s = display[name]
        md.append(f"| `{name}` | {shader} | {main} | {mask or 'null (1x1 black)'} | "
                  f"{s['covered']}/{s['total']} | {pct(s):.3f}% | "
                  f"({s['mean_rgb'][0]:.1f}, {s['mean_rgb'][1]:.1f}, {s['mean_rgb'][2]:.1f}) | "
                  f"{s['mean_luma']:.2f} | {s['alpha_min']}/{s['alpha_mean']:.1f}/{s['alpha_max']} |")
    md.append("")
    md.append("| assertion | expected | actual | result |")
    md.append("|---|---|---|---|")
    for c in checks:
        md.append(f"| `{c['id']}` — {c['desc']} | {c['expected']} | {c['actual']} | "
                  f"{'PASS' if c['ok'] else '**FAIL**'} |")
    md.append("")
    md.append("## decode corpus\n")
    md.append("| dds | format | size | mips (header) | opaque | sha256(rgba) |")
    md.append("|---|---|---|---|---|---|")
    for a in assets:
        md.append(f"| `{a['file']}` | {a['format']} (dxgi {a['dxgi']}) | {a['width']}x{a['height']} | "
                  f"{a['mip_count_header']} | {a['stats']['opaque_pct']:.2f}% | `{a['sha256_rgba'][:16]}` |")
    md.append("")
    md.append(f"## {'PASS' if not failed else 'FAIL'} — {len(checks) - len(failed)}/{len(checks)} assertions\n")
    if failed:
        md.append("```")
        for c in failed:
            md.append(f"FAIL {c['id']}: expected {c['expected']}, got {c['actual']}"
                      + (f" ({c['detail']})" if c["detail"] else ""))
        md.append("```")

    with open(os.path.join(ROOT, args.summary), "w") as f:
        f.write("\n".join(md) + "\n")

    print("\n".join(md))
    print(f"\niconpipe: {'PASS' if not failed else 'FAIL'} in {time.time() - t0:.1f}s")
    if failed:
        print("\nfailures (expected vs actual):")
        for c in failed:
            print(f"  FAIL {c['id']}: expected {c['expected']}, got {c['actual']}"
                  + (f" [{c['detail']}]" if c["detail"] else ""))
        print(f"\nrun id: {report['run_id']}")
        return 1
    print(f"run id: {report['run_id']}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
