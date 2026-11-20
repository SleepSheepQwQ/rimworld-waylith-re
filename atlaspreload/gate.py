#!/usr/bin/env python3
"""Gate for the AtlasPreloadFix workflow.

Checks:
  * the no-GPU probe passed on the shipped assemblies;
  * the mod installed into the real host and ran its self-test;
  * the boot-time summary exists and tile counts are internally consistent;
  * median patched boot wall time is not more than MAX_OVERHEAD_S above control.

The headless host has no GpuTextureService, so the in-game residency probe cannot succeed there;
that path is proven by the offline probe (real types, forced-resident sentinel). The boot runs are
for overhead and for the live queue/tile counts.
"""

import os
import re
import statistics
import sys

MAX_OVERHEAD_S = 1.5


def read(path):
    with open(path, encoding="utf-8", errors="replace") as handle:
        return handle.read()


def wall(path):
    if not os.path.exists(path):
        return None
    matches = re.findall(r"wall_seconds=([0-9.]+)", read(path))
    return float(matches[-1]) if matches else None


def one(text, pattern, name):
    match = re.search(pattern, text)
    if not match:
        print(f"FAIL: missing {name}")
        return None
    return match


def main(out):
    ok = True
    probe = read(os.path.join(out, "probe.log")) if os.path.exists(os.path.join(out, "probe.log")) else ""
    for need in ("selftest case=poison_removed PASS",
                 "selftest case=good_kept PASS",
                 "selftest case=control_bad_registered PASS",
                 "selftest case=all_fail_guard PASS",
                 "selftest case=budget_guard PASS",
                 "probe result=PASS"):
        if need not in probe:
            print(f"FAIL: probe missing: {need}")
            ok = False

    controls = [wall(os.path.join(out, f"boot_control_{i}.log")) for i in (1, 2)]
    fixes = [wall(os.path.join(out, f"boot_fix_{i}.log")) for i in (1, 2)]
    if None in controls or None in fixes:
        print(f"FAIL: wall_seconds missing control={controls} fixed={fixes}")
        return 1
    control = statistics.median(controls)
    fixed = statistics.median(fixes)
    overhead = fixed - control
    print(f"boot wall: control={control:.2f}s runs={controls}  patched={fixed:.2f}s runs={fixes}  "
          f"overhead={overhead:+.2f}s")

    fixed_text = read(os.path.join(out, "boot_fix_1.log"))
    if "install OK target=StaticTextureAtlas.Bake(bool)" not in fixed_text:
        print("FAIL: patch did not install")
        ok = False
    if "selftest result=PASS" not in fixed_text:
        print("FAIL: in-game self-test did not pass")
        ok = False

    # The seeded Meta page is the live A/B: observer keeps both textures in the tile map, the
    # filter keeps the sentinel-resident one and drops the one with no GPU resource.
    observe_path = os.path.join(out, "boot_seed_observe.log")
    if not os.path.exists(observe_path):
        print("FAIL: missing observe run")
        ok = False
    else:
        observe_text = read(observe_path)
        if not re.search(r"page group=Meta_False cand=2 observe=1", observe_text):
            print("FAIL: observe did not see the two seeded textures")
            ok = False
        if not re.search(r"tiles group=Meta_False count=2", observe_text):
            print("FAIL: observe did not register both seeded tiles")
            ok = False

    seed_fix_path = os.path.join(out, "boot_seed_fix.log")
    if not os.path.exists(seed_fix_path):
        print("FAIL: missing seeded fix run")
        ok = False
        fixed_seed = ""
    else:
        fixed_seed = read(seed_fix_path)

    fix_page = re.search(r"page group=Meta_False cand=2 resident=(\d+) fail=(\d+) maskfail=(\d+) "
                         r"removed=(\d+) ", fixed_seed)
    if not fix_page:
        print("FAIL: missing Meta page line in seeded fix run")
        ok = False
    else:
        fr, ff, fm, frm = map(int, fix_page.groups())
        print(f"fix Meta page: resident={fr} fail={ff} maskfail={fm} removed={frm}")
        if (fr, ff, frm) != (1, 1, 1):
            print("FAIL: Meta page should be resident=1 fail=1 removed=1")
            ok = False
    if not re.search(r"tiles group=Meta_False count=1", fixed_seed):
        print("FAIL: patched run did not leave exactly one Meta tile")
        ok = False
    if not re.search(r"seed group=Meta_False good_forced=1 good_tile=1 bad_tile=0 tiles=1", fixed_seed):
        print("FAIL: seeded atlas did not end with one tile and no poison tile")
        ok = False

    unbounded = os.path.join(out, "boot_seed_unbounded.log")
    if os.path.exists(unbounded):
        text = read(unbounded)
        match = re.search(r"page group=Meta_False cand=2 .*?ms=([0-9.]+)", text)
        if match:
            print(f"unbounded seeded page warmup_ms={match.group(1)}")

    if overhead > MAX_OVERHEAD_S:
        print(f"FAIL: patched boot overhead {overhead:+.2f}s exceeds {MAX_OVERHEAD_S:.2f}s")
        ok = False

    print(f"RESULT={'PASS' if ok else 'FAIL'}")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1] if len(sys.argv) > 1 else "out"))
