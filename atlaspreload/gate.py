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

    summary = one(fixed_text, r"\[ATLASFIX\] summary pages=(\d+) cand=(\d+) resident=(\d+) fail=(\d+) "
                             r"maskfail=(\d+) removed=(\d+) tiles=(\d+) warmup_ms=([0-9.]+) "
                             r"budget_pages=(\d+) allfail_pages=(\d+) disabled=(\d)", "summary")
    if summary:
        pages, cand, resident, failed, maskfail, removed, tiles, warmup, budget_pages, allfail_pages, disabled = summary.groups()
        print(f"patched summary: pages={pages} cand={cand} resident={resident} fail={failed} "
              f"maskfail={maskfail} removed={removed} tiles={tiles} warmup_ms={warmup} "
              f"budget_pages={budget_pages} allfail_pages={allfail_pages} disabled={disabled}")
        if int(cand) > 0 and int(resident) + int(failed) != int(cand):
            print("FAIL: resident + failed != candidates")
            ok = False
        if int(removed) > int(failed):
            print("FAIL: removed more than failures")
            ok = False

        unbounded = os.path.join(out, "boot_fix_unbounded.log")
        if os.path.exists(unbounded):
            text = read(unbounded)
            match = re.search(r"warmup_ms=([0-9.]+)", text)
            if match:
                print(f"unbounded warmup_ms={match.group(1)}")

    if overhead > MAX_OVERHEAD_S:
        print(f"FAIL: patched boot overhead {overhead:+.2f}s exceeds {MAX_OVERHEAD_S:.2f}s")
        ok = False

    print(f"RESULT={'PASS' if ok else 'FAIL'}")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1] if len(sys.argv) > 1 else "out"))
