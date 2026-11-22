#!/usr/bin/env python3
"""Gate three [ANIMALICON] runs and the pixel measurement:

    A  GlyphFix 2.7.0   texture + CutoutComplexUI material, bound ST must be the engine order
    B  GlyphFix 2.7.1   the same texture, material dropped
    C  GlyphFix 2.7.2   material kept and _MainTex_ST normalized, or - only if SetVector is
                        unavailable on that material - dropped to null (tier 2)

Every cell is read from the probe's key=value output. A missing line is a failure, never a
default. In particular C cannot pass by printing nothing: either stRaw is (0,0,1,1) with an
explicit engine write recorded, or materialNull=True with modSetVector=False.

    python3 pawnsicon/gate.py out/a.txt out/b.txt out/c.txt out/st.txt
"""
import re
import sys

TOKEN = re.compile(r"(\w[\w.]*)=('[^']*'|\"[^\"]*\"|\S+)")
CUTOUT_UI = "CutoutComplexUI"
IDENTITY = (0.0, 0.0, 1.0, 1.0)
ENGINE_ORDER = (1.0, 1.0, 0.0, 0.0)
TOL = 0.01
IDENTITY_PIXELS = 15660   # measured on the same texture/shader in run 45006142433


def parse(path):
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
        return tuple(float(p.rstrip("a-zA-Z")) for p in parts)
    except ValueError:
        return None


def close(v, want, tol=TOL):
    return v is not None and all(abs(a - b) <= tol for a, b in zip(v, want))


def fmt(v):
    return "(" + ",".join("%g" % x for x in v) + ")" if v else "<none>"


def nondegenerate(v):
    """A usable ST has a non-zero scale in z/w; (1,1,0,0) is the blank-tile state."""
    return v is not None and abs(v[2]) > 0.5 and abs(v[3]) > 0.5


def mod_version(globals_):
    for _, kv in globals_:
        if "packageId" in kv and "glyphfix" in kv.get("packageId", "").lower():
            return kv.get("version", "<missing>")
    return "<no-glyphfix-mod-line>"


def main():
    paths = sys.argv[1:5]
    if len(paths) < 3:
        print("usage: gate.py a.txt b.txt c.txt [st.txt]")
        return 2
    labels = ("A", "B", "C")
    parsed = [parse(p) for p in paths[:3]]
    ok = True
    problems = []

    def fail(msg):
        nonlocal ok
        ok = False
        problems.append(msg)

    def need(cond, msg):
        if not cond:
            fail(msg)

    # ---- run-level sanity ------------------------------------------------------------------
    for label, (steps, glob) in zip(labels, parsed):
        started = any(kv.get("phase") == "start" for _, kv in glob)
        done = any(kv.get("RESULT") == "DONE" for _, kv in glob)
        print("run %s: started=%s done=%s species=%s" %
              (label, started, done, sorted({s for s, _ in steps})))
        need(started, "%s: no phase=start line (probe never booted)" % label)
        need(done, "%s: no RESULT=DONE line" % label)

    versions = [mod_version(g) for _, g in parsed]
    print("mod versions: A=%s B=%s C=%s" % tuple(versions))
    for label, want, got in zip(labels, ("2.7.0", "2.7.1", "2.7.2"), versions):
        need(got == want, "%s should be GlyphFix %s, About.xml says '%s'" % (label, want, got))

    # ---- C's tier --------------------------------------------------------------------------
    C, _ = parsed[2]
    c_tiers = {}
    for species in ("Rat", "Ibex"):
        mat_null = is_true(get(C, species, "st", "materialNull"))
        if mat_null:
            c_tiers[species] = "dropped"
            need(not is_true(get(C, species, "st", "modSetVector")),
                 "C %s: material was dropped although SetVector is available on the material "
                 "(the repair threw, it did not fall back for lack of an API)" % species)
        else:
            c_tiers[species] = "normalized"
    tier = "normalized" if all(v == "normalized" for v in c_tiers.values()) else (
        "dropped" if all(v == "dropped" for v in c_tiers.values()) else "mixed")
    print("C tier: %s (%s)" % (tier, c_tiers))

    # ---- per-species expectations ----------------------------------------------------------
    # Rat/Ibex: CutoutComplex -> 2.7.0 leaves CutoutComplexUI; 2.7.1 drops it; 2.7.2 repairs or,
    # only when SetVector is unavailable, drops it. Squirrel (plain Cutout) is null in all three.
    rows = []
    for species in ("Rat", "Ibex"):
        for label, (steps, _) in zip(labels, parsed):
            tex = get(steps, species, "icon", "tex")
            size = get(steps, species, "icon", "size")
            mat = get(steps, species, "icon", "material")
            mat_null = is_true(get(steps, species, "icon", "materialNull"))
            complex_ui = is_true(get(steps, species, "icon", "isCutoutComplexUI"))
            st_raw = vec(get(steps, species, "st", "stRaw"))
            st_engine = vec(get(steps, species, "st", "stEngine"))
            st_bound = vec(get(steps, species, "st", "stBound"))
            st_bundle = vec(get(steps, species, "st", "stBundle"))
            rows.append((species, label, tex, size, mat, mat_null, st_raw, st_bound, st_bundle))

            need(tex not in ("<null>", "<missing-line>", "<missing-key>"),
                 "%s %s: no texture (%s)" % (label, species, tex))
            need(size not in ("<missing-line>", "<missing-key>", "-"),
                 "%s %s: no texture size" % (label, species))
            need(not is_true(get(steps, species, "icon", "badTex")),
                 "%s %s: texture is BaseContent.BadTex" % (label, species))
            need(is_true(get(steps, species, "spawn", "spawned")),
                 "%s %s: pawn was not spawned" % (label, species))

            if label == "A":
                need(not mat_null, "A %s: material is null - the CutoutComplexUI state was not reproduced" % species)
                need(complex_ui, "A %s: material '%s' does not start with %s" % (species, mat, CUTOUT_UI))
                need(close(st_bundle, ENGINE_ORDER),
                     "A %s: bundle-bound _MainTex_ST is %s, expected the engine order %s "
                     "(the fixture did not reproduce the defect)" % (species, fmt(st_bundle), fmt(ENGINE_ORDER)))
            elif label == "B":
                need(mat_null, "B %s: material is still '%s' - the 2.7.1 drop did not fire" % (species, mat))
                need(not complex_ui, "B %s: material '%s' is still a CutoutComplexUI material" % (species, mat))
            else:
                if mat_null:
                    need(not is_true(get(steps, species, "st", "modSetVector")),
                         "C %s: null material with SetVector available - unexplained drop" % species)
                else:
                    need(complex_ui, "C %s: material '%s' does not start with %s" % (species, mat, CUTOUT_UI))
                    need(is_true(get(steps, species, "st", "modSetVector")),
                         "C %s: SetVector(string,Vector4) is not available on %s, yet the material "
                         "was kept" % (species, get(steps, species, "st", "matType")))
                    need(is_true(get(steps, species, "st", "stExplicit")),
                         "C %s: _MainTex_ST was not written explicitly (stExplicit=%s)"
                         % (species, get(steps, species, "st", "stExplicit")))
                    effective = st_raw if st_raw is not None else st_engine
                    need(close(effective, IDENTITY),
                         "C %s: _MainTex_ST is %s (raw=%s engine=%s), expected %s"
                         % (species, fmt(effective), fmt(st_raw), fmt(st_engine), fmt(IDENTITY)))
                    need(nondegenerate(st_bound),
                         "C %s: the bound source-shader ST is %s (degenerate)" % (species, fmt(st_bound)))
                    need(nondegenerate(st_bundle),
                         "C %s: the bundle-bound ST is %s - identical to the blank-tile state "
                         "(uv collapses to a constant)" % (species, fmt(st_bundle)))

    # ---- the draw-call observation ---------------------------------------------------------
    for species in ("Rat", "Ibex", "Squirrel"):
        for label, (steps, _) in zip(labels, parsed):
            wc = get(steps, species, "draw", "workerCalls")
            wm = get(steps, species, "draw", "workerMat")
            need(wc not in ("<missing-line>", "<missing-key>", "0"),
                 "%s %s: ThingIconWorker was never reached (workerCalls=%s)" % (label, species, wc))
            need(wm not in ("<missing-line>", "<missing-key>"),
                 "%s %s: no workerMat reported" % (label, species))
            if label in ("B", "C") and c_tiers.get(species) == "dropped":
                need(wm == "<null>", "%s %s: Widgets drew with material '%s', expected the texture path"
                     % (label, species, wm))
            if label == "A":
                need(wm == get(steps, species, "icon", "material"),
                     "A %s: workerMat '%s' != GetIconFor material '%s'"
                     % (species, wm, get(steps, species, "icon", "material")))

    # Squirrel control: plain Cutout, material null in every run.
    for label, (steps, _) in zip(labels, parsed):
        need(is_true(get(steps, "Squirrel", "icon", "materialNull")),
             "%s Squirrel: control species should be null-material, got '%s'"
             % (label, get(steps, "Squirrel", "icon", "material")))
        need(get(steps, "Squirrel", "st", "material") == "<null>",
             "%s Squirrel: st line should report material=<null>" % label)

    # ---- table -----------------------------------------------------------------------------
    print()
    header = ("species", "run", "tex", "size", "material", "matNull", "stRaw", "stBound", "stBundle")
    widths = [max(len(str(r[i])) for r in rows + [header]) for i in range(len(header))]
    fmtrow = "  ".join("{:<%d}" % w for w in widths)
    print(fmtrow.format(*header))
    print(fmtrow.format(*["-" * w for w in widths]))
    for r in rows:
        print(fmtrow.format(*[str(c) for c in r]))

    # ---- pixel measurement -----------------------------------------------------------------
    st_path = paths[3] if len(paths) > 3 else None
    px = {}
    meta = {}
    if st_path:
        try:
            for line in open(st_path, encoding="utf-8", errors="replace"):
                a = line.split()
                if len(a) == 2 and a[0].isdigit() is False:
                    try:
                        px[a[0]] = int(a[1])
                    except ValueError:
                        pass
                elif a and a[0] == "meta":
                    for tok in a[1:]:
                        if "=" in tok:
                            k, v = tok.split("=", 1)
                            meta[k] = v
        except FileNotFoundError:
            pass

    print()
    print("pixel: " + (", ".join("%s=%d" % (k, px[k]) for k in sorted(px)) or "<no measurements>"))
    need(px.get("identity", 0) >= 15000,
         "pixel: identity ST drew %r pixels, expected ~%d" % (px.get("identity"), IDENTITY_PIXELS))
    need(px.get("engine", -1) == 0,
         "pixel: engine-order ST (1,1,0,0) drew %r pixels, expected 0" % px.get("engine"))
    if tier == "normalized":
        need(px.get("c_raw", 0) >= 15000,
             "pixel: C's own _MainTex_ST %s drew %r pixels, expected ~%d"
             % (meta.get("c_st_raw"), px.get("c_raw"), IDENTITY_PIXELS))
        if "c_bundle" in px:
            need(px["c_bundle"] >= 15000,
                 "pixel: C's bundle-bound ST %s drew %d pixels - the device path is still blank"
                 % (meta.get("c_st_bundle"), px["c_bundle"]))
        else:
            fail("pixel: no c_bundle measurement (C's bundle-bound ST was not renderable/reportable)")

    print()
    if tier == "dropped":
        # tier 2 is correct, but tier 1 was not exercised: this is not a green result for the
        # two-tier repair and must not be reported as one.
        fail("C fell back to material=null because SetVector is unavailable on the material: "
             "tier 2 behaves, tier 1 is NOT validated")

    if problems:
        print("GATE FAIL")
        for p in problems:
            print("  - " + p)
        return 1
    print("GATE PASS: A reproduces texture + degenerate CutoutComplexUI ST; B drops the material; "
          "C keeps it with _MainTex_ST=%s and the same ST renders %s/%d pixels."
          % (fmt(IDENTITY), px.get("c_raw"), 16384))
    return 0


if __name__ == "__main__":
    sys.exit(main())
