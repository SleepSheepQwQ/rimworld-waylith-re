#!/usr/bin/env python3
"""Turn the three [UICON] logs into one table per run and a per-def verdict.

No verdict is hard-coded: every cell comes from the probe's key=value output. The script fails only
when a def or a whole run is missing, i.e. when the probe did not measure what it claims.
"""
import re
import sys
from collections import OrderedDict

TOKEN = re.compile(r"(\w+)=('[^']*'|\S+)")
DEF_NAMES = ("Ibex", "Squirrel", "Rat", "Boomrat")


def parse(path):
    """-> {(def, step): {key: value}}, {member-tag: {key: value}}, globals, started."""
    steps, members, globals_ = OrderedDict(), OrderedDict(), []
    try:
        text = open(path, encoding="utf-8", errors="replace").read()
    except FileNotFoundError:
        return steps, members, globals_, False
    started = "phase=start" in text
    for line in text.splitlines():
        if not line.startswith("[UICON]"):
            continue
        body = line[len("[UICON]"):].strip()
        if body.startswith("members "):
            rest = body[len("members "):]
            tag, _, kv = rest.partition(" type=")
            members[tag] = dict(TOKEN.findall("type=" + kv))
            continue
        tokens = dict(TOKEN.findall(body))
        defname = tokens.pop("def", None)
        step = tokens.pop("step", None)
        if defname and step:
            steps[(defname, step)] = tokens
        else:
            globals_.append(tokens)
    return steps, members, globals_, started


def g(steps, defname, step, key, default="-"):
    d = steps.get((defname, step))
    if d is None:
        return default
    return d.get(key, default).strip("'")


def row(steps, members, defname):
    pb = defname + "/uiIcon"
    return {
        "bodyTexPath": g(steps, defname, "bodySource", "texPath"),
        "bodyShader": g(steps, defname, "bodySource", "shaderType"),
        "bodyInner": g(steps, defname, "bodygraphic.inner", "type"),
        "bodyMatShader": g(steps, defname, "bodygraphic.mat", "shader"),
        "bodyMatMain": g(steps, defname, "bodygraphic.mat", "main"),
        "bodyMats": g(steps, defname, "bodygraphic.mats", "0", "-"),
        "uiNull": g(steps, defname, "uiIcon", "null", "?"),
        "uiBad": g(steps, defname, "uiIcon", "badTex", "?"),
        "uiValue": g(steps, defname, "uiIcon", "value"),
        "uiMatNull": g(steps, defname, "uiIconMaterial", "null", "?"),
        "uiMatName": g(steps, defname, "uiIconMaterial", "name", ""),
        "uiMatShader": g(steps, defname, "uiIconMaterial", "shader", ""),
        "uiMatMain": g(steps, defname, "uiIconMaterial", "main", ""),
        "defEarly": g(steps, defname, "thingicon.def", "earlyReturn", "?"),
        "instIcon": g(steps, defname, "thingicon.instance", "icon"),
        "instEarly": g(steps, defname, "thingicon.instance", "earlyReturn", "?"),
        "call": g(steps, defname, "thingicon.call", "threw", ""),
        "callWorker": g(steps, defname, "thingicon.call", "workerReached", ""),
        "pix": g(steps, defname, "uiIcon.pixels", "n", "?"),
        "pixMean": g(steps, defname, "uiIcon.pixels", "meanRGB"),
        "alpha0": g(steps, defname, "uiIcon.pixels", "alpha0"),
        "lumSd": g(steps, defname, "uiIcon.pixels", "lumSd"),
        "members": members.get(pb, {}),
    }


def classify(r):
    if r["uiNull"] == "True":
        return "uiIcon=NULL"
    if r["uiBad"] == "True":
        return "uiIcon=BadTex"
    if r["uiNull"] == "?":
        return "uiIcon=<not measured>"
    if r["pix"] in ("?", "<no-line>") or r["pix"].startswith("!"):
        return "uiIcon=object-without-pixels"
    return "uiIcon=texture"


def main():
    runs = OrderedDict()
    ok = True
    for path in sys.argv[1:]:
        label = path.split("/")[-1]
        steps, members, _, started = parse(path)
        defs = sorted({d for (d, _) in steps})
        runs[label] = (steps, members, started, defs)
        print(f"### run {label}: started={started} defs={','.join(defs) or '<none>'} lines={len(steps)}")
        if not started:
            print(f"GATE FAIL: {label} has no phase=start line")
            ok = False
        for d in DEF_NAMES:
            if d not in defs:
                print(f"GATE FAIL: {label} has no report for def {d}")
                ok = False

    table = ["", "| run | def | body texPath | body shader | inner | body mat shader | body mainTex | uiIcon | uiIconMaterial | uiMat shader | def-early | inst-early | pixels |",
             "|---|---|---|---|---|---|---|---|---|---|---|---|---|"]
    for label, (steps, members, _, defs) in runs.items():
        for d in defs:
            r = row(steps, members, d)
            table.append(
                f"| {label} | {d} | `{r['bodyTexPath']}` | {r['bodyShader']} | {r['bodyInner']} |"
                f" {r['bodyMatShader']} | `{r['bodyMatMain']}` | {classify(r)} |"
                f" {r['uiMatName'] or '<null>'} | {r['uiMatShader']} | {r['defEarly']} | {r['instEarly']} |"
                f" n={r['pix']} mean={r['pixMean']} a0={r['alpha0']} sd={r['lumSd']} |"
            )
    print("\n".join(table))

    print("\n#### per-def detail")
    for label, (steps, members, _, defs) in runs.items():
        for d in defs:
            r = row(steps, members, d)
            m = " ".join(f"{k}={v}" for k, v in r["members"].items() if k != "type")
            print(f"- {label} {d}: {classify(r)}; uiIconMaterial="
                  f"{'<null>' if r['uiMatNull'] == 'True' else r['uiMatName'] + ' (' + r['uiMatShader'] + ')'};"
                  f" def-overload early={r['defEarly']}, instance icon={r['instIcon']} early={r['instEarly']}"
                  + (f", ThingIcon call threw {r['call']} workerReached={r['callWorker']}" if r["call"] else ""))
            if m:
                print(f"    texture members: {m}")

    print("\n#### raw steps")
    for label, (steps, _, _, _) in runs.items():
        for (d, step), kv in steps.items():
            print(f"{label} {d} {step}: " + " ".join(f"{k}={v}" for k, v in kv.items()))
    sys.exit(0 if ok else 1)


if __name__ == "__main__":
    main()
