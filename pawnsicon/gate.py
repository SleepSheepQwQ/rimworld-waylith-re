#!/usr/bin/env python3
"""Gate the two [ANIMALICON] logs: same species, A (2.7.0) vs B (2.7.1).

Every cell is read from the probe's key=value output; nothing is assumed. The gate fails when a
line it needs is missing, so "the probe did not measure it" can never read as "the fix works".

    python3 pawnsicon/gate.py out/a.txt out/b.txt
"""
import re
import sys

TOKEN = re.compile(r"(\w[\w.]*)=('[^']*'|\"[^\"]*\"|\S+)")
CUTOUT_UI = "CutoutComplexUI"


def parse(path):
    """-> {(species, step): {key: value}} plus run-level lines as a list of dicts."""
    steps = {}
    globals_ = []
    try:
        text = open(path, encoding="utf-8", errors="replace").read()
    except FileNotFoundError:
        return steps, globals_
    for line in text.splitlines():
        line = line.strip()
        if not line.startswith("[ANIMALICON]"):
            continue
        tokens = {k: v.strip("'\"") for k, v in TOKEN.findall(line[len("[ANIMALICON]"):])}
        species = tokens.pop("species", None)
        step = tokens.pop("step", None)
        if species and step:
            # step may carry a suffix such as "generate FAILED" - keep the first token
            steps[(species, step.split()[0].lower())] = tokens
        elif "step" in tokens:
            globals_.append((tokens.get("step", ""), tokens))
        else:
            globals_.append(("", tokens))
    return steps, globals_


def get(steps, species, step, key, default="<missing-line>"):
    d = steps.get((species, step))
    if d is None:
        return "<missing-line>"
    return d.get(key, "<missing-key>")


def is_true(v):
    return str(v).lower() in ("true", "1")


def mod_version(globals_):
    for _, kv in globals_:
        if "packageId" in kv and "glyphfix" in kv.get("packageId", "").lower():
            return kv.get("version", "<missing>")
    return "<no-glyphfix-mod-line>"


def main():
    a_path, b_path = sys.argv[1], sys.argv[2]
    A, Ag = parse(a_path)
    B, Bg = parse(b_path)
    ok = True
    problems = []

    def fail(msg):
        nonlocal ok
        ok = False
        problems.append(msg)

    # ---- run-level sanity ------------------------------------------------------------------
    for label, steps, glob in (("A", A, Ag), ("B", B, Bg)):
        started = any(kv.get("phase") == "start" for _, kv in glob)
        done = any(kv.get("RESULT") == "DONE" for _, kv in glob)
        print(f"run {label}: started={started} done={done} species_lines={sorted({s for s, _ in steps})}")
        if not started:
            fail(f"{label}: no phase=start line (probe never booted)")
        if not done:
            fail(f"{label}: no RESULT=DONE line")

    va, vb = mod_version(Ag), mod_version(Bg)
    print(f"mod version: A='{va}' B='{vb}'")
    if va != "2.7.0":
        fail(f"A should be GlyphFix 2.7.0, About.xml says '{va}'")
    if vb != "2.7.1":
        fail(f"B should be GlyphFix 2.7.1, About.xml says '{vb}'")

    # ---- per-species expectations -----------------------------------------------------------
    # Rat/Ibex: CutoutComplex shader -> 2.7.0 hands Widgets a CutoutComplexUI material;
    #           2.7.1 must hand it null and keep the texture.
    # Squirrel: plain Cutout shader -> material null in both runs (control: the fix is targeted).
    expect = {
        "Rat": ("complex", "complex"),
        "Ibex": ("complex", "complex"),
        "Squirrel": ("plain", "plain"),
    }

    rows = []
    for species, (want_a, want_b) in expect.items():
        for label, steps, want in (("A", A, want_a), ("B", B, want_b)):
            tex = get(steps, species, "icon", "tex")
            mat = get(steps, species, "icon", "material")
            mat_null = get(steps, species, "icon", "materialNull")
            complex_ui = get(steps, species, "icon", "isCutoutComplexUI")
            bad = get(steps, species, "icon", "badTex")
            wt = get(steps, species, "draw", "workerTex")
            wm = get(steps, species, "draw", "workerMat")
            wc = get(steps, species, "draw", "workerCalls")
            rows.append((species, label, tex, mat, mat_null, complex_ui, bad, wt, wm, wc))

            if tex in ("<null>", "<missing-line>", "<missing-key>"):
                fail(f"{label} {species}: texture is '{tex}' - the icon chain did not produce one")
                continue
            if is_true(bad):
                fail(f"{label} {species}: texture is BaseContent.BadTex")
            if not is_true(get(steps, species, "spawn", "spawned")):
                fail(f"{label} {species}: pawn was not spawned "
                     f"(spawned={get(steps, species, 'spawn', 'spawned')})")

            if want == "complex":
                if label == "A":
                    if is_true(mat_null):
                        fail(f"A {species}: material is null - the CutoutComplexUI state was not reproduced")
                    if not is_true(complex_ui):
                        fail(f"A {species}: material '{mat}' does not start with {CUTOUT_UI}")
                else:
                    if not is_true(mat_null):
                        fail(f"B {species}: material is still '{mat}' - the 2.7.1 drop did not fire")
                    if is_true(complex_ui):
                        fail(f"B {species}: material '{mat}' is still a CutoutComplexUI material")
                    if get(steps, species, "icon", "matShader") not in ("<null>", "<missing-key>"):
                        fail(f"B {species}: matShader is '{get(steps, species, 'icon', 'matShader')}', expected <null>")
            else:
                if not is_true(mat_null):
                    fail(f"{label} {species}: control species should be null-material in both runs, got '{mat}'")

            # the draw-call observation
            if not is_true(get(steps, species, "icon", "materialNull")) and label == "B":
                fail(f"B {species}: GetIconFor still returned a material")
            if wc in ("<missing-line>", "<missing-key>", "0"):
                fail(f"{label} {species}: ThingIconWorker was never reached (workerCalls={wc})")
            if wm in ("<missing-line>", "<missing-key>"):
                fail(f"{label} {species}: no workerMat reported")
            if label == "B" and wm != "<null>":
                fail(f"B {species}: Widgets drew with material '{wm}', expected the plain texture path")
            if label == "A" and want == "complex" and wm != mat:
                fail(f"A {species}: workerMat '{wm}' != GetIconFor material '{mat}'")

    print()
    header = ("species", "run", "tex", "material", "matNull", "cutoutUI", "badTex", "workerTex", "workerMat", "calls")
    widths = [max(len(str(r[i])) for r in rows + [header]) for i in range(len(header))]
    fmt = "  ".join("{:<%d}" % w for w in widths)
    print(fmt.format(*header))
    print(fmt.format(*["-" * w for w in widths]))
    for r in rows:
        print(fmt.format(*[str(c) for c in r]))

    print()
    if problems:
        print("GATE FAIL")
        for p in problems:
            print("  - " + p)
        sys.exit(1)
    print("GATE PASS: A reproduces texture + CutoutComplexUI material; B keeps the texture and drops the material.")


if __name__ == "__main__":
    main()
