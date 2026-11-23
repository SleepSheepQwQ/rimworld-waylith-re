#!/usr/bin/env python3
"""Turn the [UNIQ] lines into a table and gate the fixture's own control result.

The gate asserts the *structural* facts only, because those are the ones that must reproduce
whatever the mask format does:
  * the real Odyssey def is loaded and its Graphic_Collection built,
  * the plain-.dds control is 5 art variants / 0 masks and its def icon has opaque pixels,
  * the .png.dds leak is 10 entries / 5 masks and half the instance ids land on a mask,
  * the DX10-mask leak is 10 / 5 as well.

A green gate means "the fixture models the shipped layout", not "the icon renders" - the pixel
numbers printed next to it are the actual measurement.
"""
import re
import sys

REAL = "Gun_Revolver_Unique"
PLAIN = "UniqueRevolverFixturePlain"
LEAK = "UniqueRevolverFixtureLeak"
DX10 = "UniqueRevolverFixtureDx10"

GATE = re.compile(r"^\[UNIQ\] def=(?P<def>\S+) gate (?P<rest>.*)$")
INST = re.compile(r"^\[UNIQ\] def=(?P<def>\S+) instance id=(?P<id>\d+) (?P<rest>.*)$")


def field(text, name, default="?"):
    m = re.search(rf"(?<![\w(]){re.escape(name)}=('[^']*'|\S+?)(?=[ ,]|$)", text)
    return m.group(1).strip("'") if m else default


def main(path):
    lines = [l.rstrip("\n") for l in open(path, encoding="utf-8", errors="replace")]
    gates, instances = {}, {}
    for line in lines:
        m = GATE.match(line)
        if m:
            gates[m.group("def")] = m.group("rest")
            continue
        m = INST.match(line)
        if m:
            instances.setdefault(m.group("def"), []).append((m.group("id"), m.group("rest")))

    print(f"lines={len(lines)} gated_defs={len(gates)} instance_defs={len(instances)}")
    print(f"{'def':34} {'sub':>4} {'art':>4} {'mask':>5} {'uiIcon':12} {'defCov':>8} "
          f"{'material':24} {'picks a/m':10} {'blankIds':>9} verdict (reason)")
    rows = {}
    for dn in (REAL, PLAIN, LEAK, DX10):
        g = gates.get(dn)
        if g is None:
            print(f"{dn:34} MISSING")
            rows[dn] = None
            continue
        pk = re.search(r"picks\(art=(\d+),mask=(\d+),null=(\d+)\)", g)
        picks = f"{pk.group(1)}/{pk.group(2)}" if pk else "-"
        row = {
            "sub": field(g, "subCount"), "art": field(g, "art"), "mask": field(g, "masks"),
            "ui": field(g, "uiIcon"), "cov": field(g, "defCov"),
            "mat": field(g, "uiIconMaterial"), "picks": picks,
            "blank": field(g, "blankPicks"), "verdict": field(g, "verdict"),
            "reason": field(g, "reason"), "props": field(g, "props"),
            "present": field(g, "present"),
        }
        rows[dn] = row
        print(f"{dn:34} {row['sub']:>4} {row['art']:>4} {row['mask']:>5} {row['ui']:12} "
              f"{row['cov']:>8} {row['mat']:24} {picks:10} {row['blank']:>9} "
              f"{row['verdict']} ({row['reason']})")

    print()
    for dn in (REAL, PLAIN, LEAK, DX10):
        for iid, rest in instances.get(dn, []):
            px = re.search(r"px=(.*)$", rest)
            print(f"pick {dn:34} id={iid:>2} sub={field(rest,'sub'):26} mask={field(rest,'mask'):5} "
                  f"icon={field(rest,'icon'):28} cov={field(rest,'cov'):>7} px={px.group(1) if px else '?'}")

    fails = []

    def expect(cond, msg):
        if not cond:
            fails.append(msg)

    expect(rows[REAL] is not None and rows[REAL]["present"] == "true", f"{REAL}: def not loaded")
    if rows[REAL]:
        expect(int(rows[REAL]["sub"]) >= 5, f"{REAL}: subCount={rows[REAL]['sub']}")

    expect(rows[PLAIN] is not None, f"{PLAIN}: no gate line")
    if rows[PLAIN]:
        expect((rows[PLAIN]["sub"], rows[PLAIN]["mask"]) == ("5", "0"),
               f"{PLAIN}: expected 5/0, got {rows[PLAIN]['sub']}/{rows[PLAIN]['mask']}")
        expect(rows[PLAIN]["verdict"] == "DRAW", f"{PLAIN}: verdict={rows[PLAIN]['verdict']}")
        expect(rows[PLAIN]["blank"] == "0/10", f"{PLAIN}: blankPicks={rows[PLAIN]['blank']}")

    for dn in (LEAK, DX10):
        expect(rows[dn] is not None, f"{dn}: no gate line")
        if not rows[dn]:
            continue
        expect((rows[dn]["sub"], rows[dn]["mask"]) == ("10", "5"),
               f"{dn}: expected 10/5, got {rows[dn]['sub']}/{rows[dn]['mask']}")
        expect(rows[dn]["picks"].endswith("/5"), f"{dn}: expected 5 mask picks, got {rows[dn]['picks']}")

    print()
    if fails:
        for f in fails:
            print("FAIL:", f)
        return 1
    print("PASS: fixture models the shipped layout (5/0 healthy, 10/5 leaked)")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1]))
