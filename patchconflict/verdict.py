#!/usr/bin/env python3
"""Compare the scenario logs and print the answer in one table.

    python3 patchconflict/verdict.py out

Reads out/<scenario>.txt for every scenario, prints a matrix, then derives the four claims the
experiment exists to decide:

  install      GlyphFix still binds both GetIconFor overloads when a foreign postfix is present
  merge        Harmony accepts a second/third `ref __result` postfix on the same method
  ordering     which postfix executes last for each priority combination
  defeat       a later postfix that rewrites __result undoes GlyphFix's repair
  mitigation   which GlyphFix priority (First/Last) survives a Priority.Last clobber
"""

import pathlib
import re
import sys

ICON = re.compile(
    r"species=(?P<species>\w+) step=icon tex='(?P<tex>[^']*)' texNull=(?P<texnull>\w+) "
    r".*?material='(?P<mat>[^']*)' materialNull=(?P<matnull>\w+) "
    r"isCutoutComplexUI=(?P<complex>\w+) color=(?P<color>\S+) "
    r"fakeThingCalls=(?P<fakecalls>\d+) glyphfixPostfixes=(?P<gfposts>\d+) "
    r"totalPostfixes=(?P<total>\d+)"
)
SEEN = re.compile(r"species=(?P<species>\w+) step=fakeSeen \[(?P<seen>.*)\]")
SYNTH3 = re.compile(r"synth\.three result='(?P<r>[^']*)'.*?ran\(p1=(?P<p1>\d+),p2=(?P<p2>\d+),plast=(?P<plast>\d+)\)")
SYNTHTHROW = re.compile(r"synth\.throwing result='(?P<r>[^']*)' error='(?P<e>[^']*)' "
                        r"ran\(throw=(?P<t>\d+),p2t=(?P<p2t>\d+),plastt=(?P<plastt>\d+)\)")

SCENARIOS = [
    ("control_nogf", "off", "none"),
    ("glyphfix_only", "off", "stock"),
    ("fake_observe", "observe", "stock"),
    ("fake_null_first", "null_first/First", "stock"),
    ("fake_null_last", "null_last/Last", "stock"),
    ("fake_multi", "multi x2", "stock"),
    ("restore_stock", "restore_material", "stock"),
    ("mitig_first", "null_last/Last", "prio=First"),
    ("mitig_last", "null_last/Last", "prio=Last"),
    ("mitig_min", "null_last/Last", "prio=MinValue"),
    ("restore_min", "restore_material", "prio=MinValue"),
]


def read(out, name):
    p = pathlib.Path(out) / f"{name}.txt"
    if not p.exists():
        return ""
    return p.read_text(errors="replace")


def main():
    out = sys.argv[1] if len(sys.argv) > 1 else "out"
    data = {}
    print(f"{'scenario':18} {'mode':16} {'build':9} {'Rat tex / mat':44} {'fake':>4} {'posts':>5}")
    print("-" * 106)
    for name, mode, build in SCENARIOS:
        body = read(out, name)
        icons = {m.group("species"): m.groupdict() for m in ICON.finditer(body)}
        data[name] = {"icons": icons, "body": body}
        rat = icons.get("Rat", {})
        cell = f"{rat.get('tex','?')} / {rat.get('mat','?')}"
        if rat.get("texnull") == "True":
            cell = "<null> / " + rat.get("mat", "?")
        print(f"{name:18} {mode:16} {build:9} {cell:44} {rat.get('fakecalls','-'):>4} {rat.get('total','-'):>5}")

    print()
    for name, _, _ in SCENARIOS:
        for sp, d in data[name]["icons"].items():
            print(f"{name:18} {sp:8} tex={d['tex']:20} mat={d['mat']:24} matNull={d['matnull']:5} "
                  f"complexUI={d['complex']:5} color={d['color']}")
        for m in SEEN.finditer(data[name]["body"]):
            if m.group("species") == "Rat":
                print(f"{name:18} fakeSaw(Rat) {m.group('seen')}")
        print()

    # ---- derived claims -------------------------------------------------------------------------
    def mat_null(name, sp):
        return data[name]["icons"].get(sp, {}).get("matnull") == "True"

    def tex_null(name, sp):
        return data[name]["icons"].get(sp, {}).get("texnull") == "True"

    conflict = ["fake_observe", "fake_null_first", "fake_null_last", "fake_multi"]
    install_ok = all(mat_null(n, "Rat") and mat_null(n, "Ibex") for n in ["glyphfix_only"] + conflict)
    binds = all(int(data[n]["icons"].get("Rat", {}).get("gfposts", 0)) == 1 for n in conflict)
    multi_ok = int(data["fake_multi"]["icons"].get("Rat", {}).get("total", 0)) >= 3
    defeat = tex_null("fake_null_last", "Rat") or tex_null("fake_null_last", "Ibex")
    first_survives = not tex_null("mitig_first", "Rat")
    last_survives = not tex_null("mitig_last", "Rat")
    min_survives = not tex_null("mitig_min", "Rat")
    material_defeat = not mat_null("restore_stock", "Rat")
    material_mitigated = mat_null("restore_min", "Rat")

    synth = SYNTH3.search(data["glyphfix_only"]["body"]) or SYNTH3.search(data["control_nogf"]["body"])
    synth_result = synth.group("r") if synth else "?"
    throw = SYNTHTHROW.search(data["glyphfix_only"]["body"]) or SYNTHTHROW.search(data["control_nogf"]["body"])

    print("=== derived ===")
    print(f"glyphfix.installs_with_foreign_postfix={install_ok}")
    print(f"glyphfix.postfix_on_target={binds}")
    print(f"harmony.multi_ref_result_postfix_accepted={multi_ok}")
    print(f"harmony.synth_result='{synth_result}'")
    if throw:
        print(f"harmony.throwing_postfix error='{throw.group('e')}' ran(throw={throw.group('t')},p2t={throw.group('p2t')},plastt={throw.group('plastt')})")
    print(f"glyphfix.silently_defeated_by_later_postfix={defeat}")
    print(f"glyphfix.material_drop_silently_restored={material_defeat}")
    print(f"mitigation.priority_first_survives={first_survives}")
    print(f"mitigation.priority_last_survives={last_survives}")
    print(f"mitigation.priority_minvalue_survives={min_survives}")
    print(f"mitigation.minvalue_keeps_material_dropped={material_mitigated}")
    if min_survives:
        print("mitigation.recommended=int.MinValue (runs after First/Normal/Last; only a later patch at the same priority can still win)")
    elif first_survives or last_survives:
        print(f"mitigation.recommended={'Priority.First' if first_survives else 'Priority.Last'}")
    else:
        print("mitigation.recommended=none-of-the-priorities; forward to TryRepair at the last consumer instead")

    print()
    if install_ok and binds and multi_ok:
        extra = "MATERIAL_ALSO_RESTORABLE" if material_defeat else "MATERIAL_KEPT_NULL"
        print("verdict=" + ("FOREIGN_POSTFIX_CAN_CLOBBER_SILENTLY" if defeat else "FOREIGN_POSTFIX_CANNOT_CLOBBER") + "_" + extra)
        print(f"mitigation_verdict={'FIXED_BY_MINVALUE' if (min_survives and material_mitigated) else 'NOT_FIXED_BY_MINVALUE'}")
    else:
        print("verdict=INCONCLUSIVE")
    return 0


if __name__ == "__main__":
    sys.exit(main())
