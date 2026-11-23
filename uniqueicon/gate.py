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
    print(f"{'def':34} {'sub':>4} {'art':>4} {'mask':>5} {'uiIcon':12} {'uiCov':>8} {'defCov':>8} "
          f"{'material':24} {'props':>5} {'picks a/m':10} {'blankIds':>9} verdict (reason)")
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
            "ui": field(g, "uiIcon"), "uicov": field(g, "uiIconCov"), "cov": field(g, "defCov"),
            "mat": field(g, "uiIconMaterial"), "props": field(g, "props"), "picks": picks,
            "blank": field(g, "blankPicks"), "verdict": field(g, "verdict"),
            "reason": field(g, "reason"), "present": field(g, "present"),
            "matrisk": field(g, "matRisk"),
        }
        rows[dn] = row
        print(f"{dn:34} {row['sub']:>4} {row['art']:>4} {row['mask']:>5} {row['ui']:12} "
              f"{row['uicov']:>8} {row['cov']:>8} {row['mat']:24} {row['props']:>5} {picks:10} "
              f"{row['blank']:>9} {row['verdict']} ({row['reason']})")

    print()
    for dn in (REAL, PLAIN, LEAK, DX10):
        for iid, rest in instances.get(dn, []):
            px = re.search(r"px=(.*)$", rest)
            print(f"pick {dn:34} id={iid:>2} sub={field(rest,'sub'):26} mask={field(rest,'mask'):5} "
                  f"icon={field(rest,'icon'):28} src={field(rest,'src'):18} cov={field(rest,'cov'):>7} "
                  f"px={px.group(1) if px else '?'}")

    fails = []

    def expect(cond, msg):
        if not cond:
            fails.append(msg)

    # The real def is the control the harness can actually build: loose PNGs, healthy names.
    expect(rows[REAL] is not None and rows[REAL]["present"] == "true", f"{REAL}: def not loaded")
    if rows[REAL]:
        expect(rows[REAL]["sub"] == "5", f"{REAL}: expected 5 art variants, got {rows[REAL]['sub']}")
        expect(rows[REAL]["mask"] == "0", f"{REAL}: masks={rows[REAL]['mask']}")
        expect(rows[REAL]["ui"] == "art", f"{REAL}: uiIcon kind={rows[REAL]['ui']}")
        expect(rows[REAL]["verdict"] == "DRAW", f"{REAL}: verdict={rows[REAL]['verdict']}")

    # Plain .dds: same bytes, healthy names -> 5/0 and an art icon that draws.
    expect(rows[PLAIN] is not None, f"{PLAIN}: no gate line")
    if rows[PLAIN]:
        expect((rows[PLAIN]["sub"], rows[PLAIN]["mask"]) == ("5", "0"),
               f"{PLAIN}: expected 5/0, got {rows[PLAIN]['sub']}/{rows[PLAIN]['mask']}")
        expect(rows[PLAIN]["verdict"] == "DRAW", f"{PLAIN}: verdict={rows[PLAIN]['verdict']}")
        expect(rows[PLAIN]["blank"] == "0/10", f"{PLAIN}: blankPicks={rows[PLAIN]['blank']}")

    # The leak: 10 entries / 5 masks, and ResolveIcon's subGraphics[0] is a mask.
    expect(rows[LEAK] is not None, f"{LEAK}: no gate line")
    if rows[LEAK]:
        expect((rows[LEAK]["sub"], rows[LEAK]["mask"]) == ("10", "5"),
               f"{LEAK}: expected 10/5, got {rows[LEAK]['sub']}/{rows[LEAK]['mask']}")
        expect(rows[LEAK]["ui"] == "mask", f"{LEAK}: uiIcon kind={rows[LEAK]['ui']}")
        expect(rows[LEAK]["verdict"] in ("MASK-ICON", "MASK-VARIANTS"),
               f"{LEAK}: verdict={rows[LEAK]['verdict']}")
        expect(rows[LEAK]["picks"].endswith("/5"), f"{LEAK}: expected 5 mask picks, got {rows[LEAK]['picks']}")

    # The shipped mask shape: 10/5 and nothing readable to draw.
    expect(rows[DX10] is not None, f"{DX10}: no gate line")
    if rows[DX10]:
        expect((rows[DX10]["sub"], rows[DX10]["mask"]) == ("10", "5"),
               f"{DX10}: expected 10/5, got {rows[DX10]['sub']}/{rows[DX10]['mask']}")
        expect(rows[DX10]["verdict"] == "BLANK", f"{DX10}: verdict={rows[DX10]['verdict']}")
        expect(rows[DX10]["reason"] == "uiIcon-unreadable", f"{DX10}: reason={rows[DX10]['reason']}")

    print()
    if fails:
        for f in fails:
            print("FAIL:", f)
        return 1
    print("PASS: real def draws, plain fixture draws 5/0, .png.dds leak is 10/5 with a mask icon,"
          " DX10-mask leak is unreadable")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1]))
