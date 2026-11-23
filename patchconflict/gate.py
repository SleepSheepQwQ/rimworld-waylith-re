#!/usr/bin/env python3
"""Gate for the patch-conflict experiment.

Reads one [PATCHCONFLICT] log and asserts the facts that make the run interpretable, then prints
the measurement. Anything that would make a "green" meaningless (no fake patch installed, no
GlyphFix postfix on the target, a control that is not CutoutComplexUI, a phase that never ran) is a
hard failure. The *outcome* of the ordering experiment is printed, not asserted, because it is the
answer we are looking for.

    python3 patchconflict/gate.py <scenario> <log> [<log> ...]
"""

import re
import sys

ICON = re.compile(
    r"species=(?P<species>\w+) step=icon tex='(?P<tex>[^']*)' texNull=(?P<texnull>\w+) "
    r"badTex=(?P<bad>\w+) isWhiteTex=(?P<white>\w+) isBlackTex=(?P<black>\w+) "
    r".*?material='(?P<mat>[^']*)' materialNull=(?P<matnull>\w+) "
    r"isCutoutComplexUI=(?P<complex>\w+) color=(?P<color>\S+) "
    r"fakeThingCalls=(?P<fakecalls>\d+) glyphfixPostfixes=(?P<gfposts>\d+) "
    r"totalPostfixes=(?P<total>\d+) error='(?P<err>[^']*)'"
)
SEEN = re.compile(r"species=(?P<species>\w+) step=fakeSeen \[(?P<seen>.*)\]")
GETICONFOR = re.compile(
    r"geticonfor (?P<sig>.*?) postfixes=(?P<total>\d+) glyphfixPostfixes=(?P<mine>\d+) who=\[(?P<who>.*)\]"
)
SYNTH3 = re.compile(
    r"synth\.three result='(?P<r>[^']*)' error='(?P<e>[^']*)' postfixes=(?P<n>-?\d+) "
    r"ran\(p1=(?P<p1>\d+),p2=(?P<p2>\d+),plast=(?P<plast>\d+)\)"
)
SYNTHTHROW = re.compile(
    r"synth\.throwing result='(?P<r>[^']*)' error='(?P<e>[^']*)' "
    r"ran\(throw=(?P<t>\d+),p2t=(?P<p2t>\d+),plastt=(?P<plastt>\d+)\)"
)

checks = []


def check(name, ok, detail=""):
    checks.append((name, ok, detail))
    print(f"check={name} ok={'true' if ok else 'false'} {detail}".rstrip())


def main():
    scenario, logs = sys.argv[1], sys.argv[2:]
    print(f"scenario={scenario} logs={len(logs)}")
    text = ""
    for path in logs:
        with open(path, "r", errors="replace") as fh:
            text += fh.read() + "\n"

    lines = [l for l in text.splitlines() if l.startswith("[PATCHCONFLICT]")]
    body = "\n".join(lines)

    check("phase.completed", "RESULT=DONE" in body, "" if "RESULT=DONE" in body else "no RESULT=DONE")
    check("phase.no_failure", "RESULT=FAIL" not in body,
          "" if "RESULT=FAIL" not in body else "RESULT=FAIL present")
    check("probe.no_exception", "step=EXCEPTION" not in body,
          "" if "step=EXCEPTION" not in body else "a species step threw")

    glyphfix_run = scenario != "control_nogf"
    if glyphfix_run:
        check("mod.bootstrap", "mod.startup=ctor-invoked" in body,
              "" if "mod.startup=ctor-invoked" in body else "Bootstrap ctor did not run")
        installed = "iconfallback: patched 2 GetIconFor overload(s)" in text
        check("glyphfix.installed_2_overloads", installed,
              "" if installed else "mod log does not say it patched 2 GetIconFor overloads")
        bad = "iconfallback: expected 2 GetIconFor overloads" in text
        check("glyphfix.no_install_warning", not bad,
              "" if not bad else "mod logged 'expected 2 GetIconFor overloads'")
        refused = re.search(r"iconfallback: .*not patched: ", text)
        check("glyphfix.no_patch_rejection", refused is None,
              "" if refused is None else refused.group(0)[:160])

    icons = {}
    for m in ICON.finditer(body):
        icons[m.group("species")] = m.groupdict()
    seen = {}
    for m in SEEN.finditer(body):
        seen[m.group("species")] = m.group("seen")

    print("--- icon measurements ---")
    for sp, d in icons.items():
        print(
            f"  {sp}: tex={d['tex']} texNull={d['texnull']} white={d['white']} black={d['black']} "
            f"material={d['mat']} materialNull={d['matnull']} complexUI={d['complex']} "
            f"color={d['color']} fakeCalls={d['fakecalls']} gfPostfixes={d['gfposts']} totalPostfixes={d['total']} "
            f"error={d['err']}"
        )
        if d["species"] in seen:
            print(f"    fake saw: {seen[d['species']]}")

    check("species.rat_measured", "Rat" in icons)
    check("species.ibex_measured", "Ibex" in icons)
    check("species.squirrel_measured", "Squirrel" in icons)

    if scenario == "control_nogf":
        for sp in ("Rat", "Ibex"):
            d = icons.get(sp)
            check(f"{sp}.control_is_complexui",
                  bool(d) and d["complex"] == "True" and d["matnull"] == "False",
                  f"{sp} material={d['mat'] if d else '?'} (must be CutoutComplexUI_*)")
            check(f"{sp}.control_texture_present", bool(d) and d["texnull"] == "False",
                  f"{sp} tex={d['tex'] if d else '?'} (a life stage without art makes the measurement vacuous)")
        d = icons.get("Squirrel")
        check("Squirrel.control_material_null", bool(d) and d["matnull"] == "True",
              "plain Cutout control must have no material")

    if scenario in ("glyphfix_only", "fake_observe", "fake_null_first", "fake_multi", "mitig_first", "mitig_last",
                    "mitig_min", "restore_min"):
        for sp in ("Rat", "Ibex"):
            d = icons.get(sp)
            check(f"{sp}.glyphfix_dropped_material", bool(d) and d["matnull"] == "True",
                  f"{sp} material={d['mat'] if d else '?'} (GlyphFix 2.7.7 must leave null)")

    if scenario in ("fake_observe", "fake_null_first", "fake_null_last", "fake_multi", "restore_stock", "restore_min"):
        ok_patch = "fake.thing patch=ok" in body
        check("fake.patched_before_glyphfix", ok_patch, "" if ok_patch else "fake postfix was not installed")

    if scenario == "fake_observe":
        d = icons.get("Rat")
        check("fake.ran", bool(d) and int(d["fakecalls"]) >= 1, f"fakeThingCalls={d['fakecalls'] if d else '?'}")
        check("target.has_two_postfixes", bool(d) and int(d["total"]) >= 2,
              f"totalPostfixes={d['total'] if d else '?'}")

    if scenario == "fake_null_first":
        d = icons.get("Rat")
        print(f"note: fake at Priority.First -> Rat tex={d['tex'] if d else '?'} "
              f"texNull={d['texnull'] if d else '?'} (survival is the ordering measurement, not a gate)")

    if scenario == "fake_multi":
        dc = icons.get("Rat")
        check("multi.three_postfixes_on_real_target",
              bool(dc) and int(dc["total"]) >= 3,
              f"totalPostfixes={dc['total'] if dc else '?'} (2 fake + GlyphFix)")
        check("multi.both_fake_postfixes_ran",
              bool(dc) and int(dc["fakecalls"]) >= 2,
              f"fakeThingCalls={dc['fakecalls'] if dc else '?'}")
        check("multi.result_is_one_of_the_sentinels",
              bool(dc) and (dc["white"] == "True" or dc["black"] == "True"),
              f"tex={dc['tex'] if dc else '?'}")

    if scenario in ("mitig_first", "mitig_last", "mitig_min"):
        d = icons.get("Rat")
        check("Rat.mitigation_run_measured", bool(d) and d["matnull"] == "True",
              f"tex={d['tex'] if d else '?'} texNull={d['texnull'] if d else '?'} (survival is compared across the builds)")

    if scenario == "mitig_min":
        d = icons.get("Rat")
        check("Rat.min_priority_survives_last_clobber",
              bool(d) and d["texnull"] == "False",
              f"tex={d['tex'] if d else '?'} (int.MinValue must run after Priority.Last)")

    if scenario in ("restore_stock", "restore_min"):
        d = icons.get("Rat")
        print(f"note: material restore -> Rat material={d['mat'] if d else '?'} "
              f"materialNull={d['matnull'] if d else '?'} (restore_stock may legitimately show the material back)")

    if scenario == "restore_min":
        d = icons.get("Rat")
        check("Rat.min_priority_keeps_material_dropped",
              bool(d) and d["matnull"] == "True",
              f"material={d['mat'] if d else '?'} (int.MinValue must drop the restored material)")

    # Harmony-semantics half: only run on the first scenario (it is process-global, run once).
    m3 = SYNTH3.search(body)
    if m3:
        d = m3.groupdict()
        print(f"synth.three: result={d['r']} error={d['e']} postfixes={d['n']} ran(p1={d['p1']},p2={d['p2']},plast={d['plast']})")
        check("synth.three_postfixes_all_ran",
              int(d["p1"]) >= 1 and int(d["p2"]) >= 1 and int(d["plast"]) >= 1,
              "every ref __result postfix must execute")
        check("synth.three_order", True, f"result='{d['r']}' (order is the measurement)")
        mt = SYNTHTHROW.search(body)
        if mt:
            t = mt.groupdict()
            print(f"synth.throwing: result={t['r']} error={t['e']} ran(throw={t['t']},p2t={t['p2t']},plastt={t['plastt']})")
            check("synth.throwing_postfix_observed", int(t["t"]) >= 1, "the throwing postfix ran at least once")
        check("synth.wrongtype_rejected", "synth.wrongtype patch=REJECTED" in body,
              "" if "synth.wrongtype patch=REJECTED" in body else "Harmony accepted ref int on a string return")

    failed = [c for c in checks if not c[1]]
    print(f"--- {len(checks) - len(failed)}/{len(checks)} checks passed ---")
    if failed:
        for name, _, detail in failed:
            print(f"FAILED {name} {detail}")
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
