#!/usr/bin/env python3
"""Gate the unique-weapon fixture after GlyphFix is installed.

This is the positive counterpart to gate.py. It requires every layout to end at the same
observable state: five art variants, zero leaked masks, a readable non-mask def icon, and no
instance id selecting a blank or mask texture.
"""
import re
import sys

REAL = "Gun_Revolver_Unique"
PLAIN = "UniqueRevolverFixturePlain"
LEAK = "UniqueRevolverFixtureLeak"
DX10 = "UniqueRevolverFixtureDx10"
DEFS = (REAL, PLAIN, LEAK, DX10)

GATE = re.compile(r"^\[UNIQ\] def=(?P<def>\S+) gate (?P<rest>.*)$")


def field(text, name, default="?"):
    m = re.search(rf"(?<![\w(]){re.escape(name)}=('[^']*'|\S+?)(?=[ ,]|$)", text)
    return m.group(1).strip("'") if m else default


def percent(text, name):
    value = field(text, name)
    if not value.endswith("%"):
        return None
    try:
        return float(value[:-1])
    except ValueError:
        return None


def main(uniq_path, probe_log):
    gates = {}
    for line in open(uniq_path, encoding="utf-8", errors="replace"):
        m = GATE.match(line.rstrip("\n"))
        if m:
            gates[m.group("def")] = m.group("rest")

    log = open(probe_log, encoding="utf-8", errors="replace").read()
    failures = []

    def expect(condition, message):
        if not condition:
            failures.append(message)

    print(f"gated_defs={len(gates)}")
    print(f"{'def':34} {'sub':>4} {'art':>4} {'mask':>5} {'uiIcon':7} {'uiCov':>8} "
          f"{'defCov':>8} {'blank':>9} {'picks a/m/n':13} verdict reason")

    for def_name in DEFS:
        gate = gates.get(def_name)
        if gate is None:
            failures.append(f"{def_name}: no gate line")
            print(f"{def_name:34} MISSING")
            continue

        picks = re.search(r"picks\(art=(\d+),mask=(\d+),null=(\d+)\)", gate)
        pick_text = "/".join(picks.groups()) if picks else "?"
        row = {
            "present": field(gate, "present"),
            "sub": field(gate, "subCount"),
            "art": field(gate, "art"),
            "mask": field(gate, "masks"),
            "ui": field(gate, "uiIcon"),
            "ui_cov": percent(gate, "uiIconCov"),
            "def_cov": percent(gate, "defCov"),
            "blank_picks": field(gate, "blankPicks"),
            "verdict": field(gate, "verdict"),
            "reason": field(gate, "reason"),
        }
        print(f"{def_name:34} {row['sub']:>4} {row['art']:>4} {row['mask']:>5} "
              f"{row['ui']:7} {field(gate, 'uiIconCov'):>8} {field(gate, 'defCov'):>8} "
              f"{row['blank_picks']:>9} {pick_text:13} {row['verdict']} {row['reason']}")

        expect(row["present"] == "true", f"{def_name}: def not loaded")
        expect(row["sub"] == "5", f"{def_name}: subCount={row['sub']}, expected 5")
        expect(row["art"] == "5", f"{def_name}: art={row['art']}, expected 5")
        expect(row["mask"] == "0", f"{def_name}: masks={row['mask']}, expected 0")
        expect(row["ui"] == "art", f"{def_name}: uiIcon={row['ui']}, expected art")
        expect(row["verdict"] == "DRAW", f"{def_name}: verdict={row['verdict']}")
        expect(row["reason"] == "art-subgraphs-only",
               f"{def_name}: reason={row['reason']}, expected art-subgraphs-only")
        expect(row["ui_cov"] is not None and row["ui_cov"] > 0,
               f"{def_name}: uiIcon coverage={field(gate, 'uiIconCov')}")
        expect(row["def_cov"] is not None and row["def_cov"] > 0,
               f"{def_name}: def icon coverage={field(gate, 'defCov')}")
        expect(bool(picks) and picks.group(2) == "0" and picks.group(3) == "0",
               f"{def_name}: picks={pick_text}, expected only art")
        expect(row["blank_picks"] == "0/10",
               f"{def_name}: blank picks={row['blank_picks']}, expected 0/10")

    expect("startup ctor invoked (harness emulation)" in log,
           "GlyphFix startup constructor was not invoked")
    expect("BUILD 2.7.8" in log, "the tested DLL did not report GlyphFix 2.7.8")
    expect("drawfix: patched" in log, "IconFieldFix did not patch the icon draw paths")
    expect("GetIconFor(ThingDef, ...) overload" in log,
           "IconFieldFix did not patch direct GetIconFor(ThingDef) callers")
    expect("collection: existing graphic scan" in log,
           "prebuilt Graphic_Collection objects were not swept")
    expect("ddsalt: armed" in log, "DdsPrefixAlt never armed")
    expect("ddsalt: repaired" in log, "no DX10 BC4 texture was repaired")

    print()
    if failures:
        for failure in failures:
            print("FAIL:", failure)
        return 1
    print("PASS: all four layouts are 5/0, draw non-mask art, and have zero blank picks;"
          " the DX10 BC4 fixture was repaired")
    return 0


if __name__ == "__main__":
    if len(sys.argv) != 3:
        sys.exit("usage: gate_fixed.py <uniq.txt> <probe.log>")
    sys.exit(main(sys.argv[1], sys.argv[2]))
