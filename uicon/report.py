#!/usr/bin/env python3
"""Turn the three [UICON] logs into one table per run and a per-def verdict.

No verdict is hard-coded: every cell is read from the probe's key=value output. The script only
fails when a def or a whole run is missing, i.e. when the probe did not measure what it claims.
"""
import re
import sys
from collections import OrderedDict

TOKEN = re.compile(r"(\w+)=('[^']*'|\S+)")

ORDER = [
    ("graphicData", "graphicData"),
    ("bodySource", "bodySource"),
    ("def.graphic", "def.graphic"),
    ("def.inner", "def.inner"),
    ("def.mat", "def.mat"),
    ("body.inner", "body.inner"),
    ("body.mat", "body.mat"),
    ("body.mats", "body.mats"),
    ("uiIcon", "uiIcon"),
    ("uiIcon.fields", "uiIcon.fields"),
    ("uiIcon.pixels", "uiIcon.pixels"),
    ("uiIconMaterial", "uiIconMaterial"),
    ("uiIconMaterial.material", "uiIconMaterial.material"),
    ("resolve", "resolve"),
    ("thingicon.def", "thingicon.def"),
    ("widgets.GetIconFor", "widgets.GetIconFor"),
    ("thingicon.instance", "thingicon.instance"),
    ("thingicon.call", "thingicon.call"),
]


def parse(path):
    """-> {(def, step): {key: value}} plus the run's global lines."""
    steps = OrderedDict()
    globals_ = []
    try:
        text = open(path, encoding="utf-8", errors="replace").read()
    except FileNotFoundError:
        return steps, globals_, False
    started = "phase=start" in text
    for line in text.splitlines():
        if not line.startswith("[UICON]"):
            continue
        body = line[len("[UICON]"):].strip()
        tokens = dict(TOKEN.findall(body))
        defname = tokens.pop("def", None)
        step = tokens.pop("step", None)
        if defname and step:
            steps[(defname, step)] = tokens
        else:
            globals_.append(tokens)
    return steps, globals_, started


def get(steps, defname, step, key, default="-"):
    d = steps.get((defname, step))
    if d is None:
        return "<no-line>"
    return d.get(key, default).strip("'")


def one_line(steps, defname):
    body = steps.get((defname, "bodySource"), {})
    ui = steps.get((defname, "uiIcon"), {})
    uim = steps.get((defname, "uiIconMaterial"), {})
    uimd = steps.get((defname, "uiIconMaterial.material"), {})
    inst = steps.get((defname, "thingicon.instance"), {})
    pix = steps.get((defname, "uiIcon.pixels"), {})
    fields = steps.get((defname, "uiIcon.fields"), {})
    tex = steps.get((defname, "uiIcon.texture"), {})
    return {
        "bodyTexPath": body.get("texPath", "-").strip("'"),
        "bodyShader": body.get("shaderType", "-"),
        "bodyInner": (steps.get((defname, "body.inner")) or {}).get("type", "-"),
        "bodyMatShader": (steps.get((defname, "body.mat.material")) or {}).get("shader", "-").strip("'"),
        "bodyMatMain": (steps.get((defname, "body.mat.material")) or {}).get("main", "-").strip("'"),
        "bodyMats": (steps.get((defname, "body.mats")) or {}).get("step", "?"),
        "uiIconNull": ui.get("null", "?"),
        "uiIconBad": ui.get("badTex", "?"),
        "uiIconValue": ui.get("value", "?"),
        "uiIconMaterialNull": uim.get("null", "?"),
        "uiMaterialName": uimd.get("name", "-").strip("'"),
        "uiMaterialShader": uimd.get("shader", "-").strip("'"),
        "uiMaterialMain": uimd.get("main", "-").strip("'"),
        "defEarly": (steps.get((defname, "thingicon.def")) or {}).get("earlyReturn", "?"),
        "instIcon": inst.get("icon", "-").strip("'"),
        "instEarly": inst.get("earlyReturn", "?"),
        "cpu": fields.get("CpuData", "-"),
        "levels": fields.get("levels", "-"),
        "src": fields.get("SourcePath", "-").strip("'"),
        "gpu": fields.get("GpuResource", "-"),
        "pixels": pix.get("n", pix.get("step", "?")),
        "pixMean": pix.get("meanRGB", "-").strip("'"),
        "alpha0": pix.get("alpha0", "-"),
        "lumSd": pix.get("lumSd", "-"),
    }


def classify(r):
    if r["uiIconNull"] == "True":
        return "uiIcon=NULL"
    if r["uiIconBad"] == "True":
        return "uiIcon=BadTex"
    if r["uiIconNull"] == "?":
        return "uiIcon=<not measured>"
    if r["pixels"] in ("<no-line>", "?") or str(r["pixels"]).startswith("!"):
        return "uiIcon=object-without-pixels"
    return "uiIcon=texture"


def main():
    paths = sys.argv[1:]
    runs = OrderedDict()
    ok = True
    for path in paths:
        label = path.split("/")[-1]
        steps, globals_, started = parse(path)
        defs = sorted({d for (d, _) in steps})
        runs[label] = (steps, globals_, started, defs)
        print(f"### run {label}: started={started} defs={','.join(defs) or '<none>'} lines={len(steps)}")
        if not started:
            print(f"GATE FAIL: {label} has no phase=start line")
            ok = False
        for d in ("Ibex", "Squirrel", "Rat", "Boomrat"):
            if d not in defs:
                print(f"GATE FAIL: {label} has no report for def {d}")
                ok = False

    table = ["", "| run | def | body texPath | body shader | body inner | body mat shader | body mainTex | uiIcon | uiIconMaterial | uiMat shader | def early | inst early | pixels |",
             "|---|---|---|---|---|---|---|---|---|---|---|---|---|"]
    for label, (steps, _, _, defs) in runs.items():
        for d in defs:
            r = one_line(steps, d)
            table.append(
                f"| {label} | {d} | `{r['bodyTexPath']}` | {r['bodyShader']} | {r['bodyInner']} |"
                f" {r['bodyMatShader']} | `{r['bodyMatMain']}` | {classify(r)} |"
                f" {r['uiMaterialName'] or '<null>'} | {r['uiMaterialShader']} |"
                f" {r['defEarly']} | {r['instEarly']} | n={r['pixels']} mean={r['pixMean']} a0={r['alpha0']} sd={r['lumSd']} |"
            )
    print("\n".join(table))

    print("\n#### per-def detail")
    for label, (steps, _, _, defs) in runs.items():
        for d in defs:
            r = one_line(steps, d)
            print(f"- {label} {d}: {classify(r)}; CpuData={r['cpu']} levels={r['levels']} "
                  f"SourcePath={r['src'] or '<null>'} GpuResource={r['gpu']}; "
                  f"uiIconMaterial={'<null>' if r['uiIconMaterialNull']=='True' else r['uiMaterialName']}"
                  f" ({r['uiMaterialShader']}); def-overload early={r['defEarly']}, "
                  f"instance-path icon={r['instIcon'] or '<null>'} early={r['instEarly']}")

    # raw step dump, so a value that the table dropped is still in the log
    print("\n#### raw steps")
    for label, (steps, _, _, _) in runs.items():
        for (d, step), kv in steps.items():
            cells = " ".join(f"{k}={v}" for k, v in kv.items())
            print(f"{label} {d} {step}: {cells}")
    sys.exit(0 if ok else 1)


if __name__ == "__main__":
    main()
