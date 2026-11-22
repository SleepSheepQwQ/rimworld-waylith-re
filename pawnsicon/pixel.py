#!/usr/bin/env python3
"""Render the real CutoutComplexUI fragment shader with the _MainTex_ST values the C run reports.

The probe reads the property and the bound uniform out of the live material; this script feeds
those numbers to vk/harness (software Vulkan), which runs pipe/shaders/CutoutComplexUI.frag.glsl
unmodified and counts the pixels that survive. The two controls are fixed:

    identity  (0,0,1,1)   the GLSL's own default; the last measured value was 15660 / 16384
    engine    (1,1,0,0)   the degenerate asset-bundle order: uv = vUv*0 + 1 = (1,1) -> 0 pixels

Usage:
    python3 pawnsicon/pixel.py out/c.txt --harness build/harness --frag build/ui.spv \
        --vert build/quad.spv --main vk/assets/royalbed_south.rgba > out/st.txt
"""
import argparse
import re
import subprocess
import sys

TOKEN = re.compile(r"(\w[\w.]*)=('[^']*'|\"[^\"]*\"|\S+)")


def steps_of(path):
    steps = {}
    try:
        text = open(path, encoding="utf-8", errors="replace").read()
    except FileNotFoundError:
        return steps
    for line in text.splitlines():
        line = line.strip()
        if not line.startswith("[ANIMALICON]"):
            continue
        tokens = {k: v.strip("'\"") for k, v in TOKEN.findall(line[len("[ANIMALICON]"):])}
        species = tokens.pop("species", None)
        step = tokens.pop("step", None)
        if species and step:
            steps[(species, step.split()[0].lower())] = tokens
    return steps


def get(steps, species, step, key, default="<missing>"):
    d = steps.get((species, step))
    if d is None:
        return "<missing-line>"
    return d.get(key, default)


def vec(s):
    if s is None:
        return None
    m = re.search(r"\(([^()]*)\)", s)
    if not m:
        return None
    parts = [p.strip() for p in m.group(1).split(",")]
    if len(parts) != 4:
        return None
    try:
        return tuple(float(p) for p in parts)
    except ValueError:
        return None


def fmt(v):
    return "(" + ",".join(("%g" % x) for x in v) + ")" if v else "<none>"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("ctxt")
    ap.add_argument("--harness", default="build/harness")
    ap.add_argument("--frag", default="build/ui.spv")
    ap.add_argument("--vert", default="build/quad.spv")
    ap.add_argument("--main", default="vk/assets/royalbed_south.rgba")
    ap.add_argument("--species", default="Rat")
    args = ap.parse_args()

    steps = steps_of(args.ctxt)
    mat_null = get(steps, args.species, "st", "materialNull") == "True"
    raw = vec(get(steps, args.species, "st", "stRaw"))
    bound = vec(get(steps, args.species, "st", "stBound"))
    bundle = vec(get(steps, args.species, "st", "stBundle"))
    engine = vec(get(steps, args.species, "st", "stEngine"))

    # C material dropped -> no C ST to render; still run the two fixed controls so the shader
    # itself is shown to be pixel-capable in this run.
    cases = []
    if raw:
        cases.append(("c_raw", raw))
    if bound:
        cases.append(("c_bound", bound))
    if bundle:
        cases.append(("c_bundle", bundle))
    cases.append(("identity", (0.0, 0.0, 1.0, 1.0)))
    cases.append(("engine", (1.0, 1.0, 0.0, 0.0)))

    print("meta c_species=%s c_material_null=%s c_st_raw=%s c_st_engine=%s c_st_bound=%s c_st_bundle=%s"
          % (args.species, mat_null, fmt(raw), fmt(engine), fmt(bound), fmt(bundle)))

    seen = {}
    for label, v in cases:
        if v in seen:
            print("%s %d" % (label, seen[v]))
            continue
        cmd = [args.harness, args.frag, args.vert, args.main, "128", "128", "-",
               "%.9g" % v[0], "%.9g" % v[1], "%.9g" % v[2], "%.9g" % v[3], "build/%s.ppm" % label]
        try:
            out = subprocess.run(cmd, capture_output=True, text=True, timeout=120)
        except Exception as e:
            print("%s FAILED %s: %s" % (label, type(e).__name__, e))
            continue
        m = re.search(r"RESULT drawn=(\d+)", out.stdout)
        if not m:
            print("%s FAILED no-RESULT stderr=%s" % (label, out.stderr.strip().replace("\n", " ")[:300]))
            continue
        drawn = int(m.group(1))
        seen[v] = drawn
        print("%s %d" % (label, drawn))
    return 0


if __name__ == "__main__":
    sys.exit(main())
