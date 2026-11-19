#!/usr/bin/env python3
# Reads the headless run logs and turns them into (a) a job summary and (b) an exit code.
# The exit code is the result: a run that did not boot, or a scenario that did not print
# RESULT=OK, fails the job instead of producing a green log nobody reads.
import re
import sys
from pathlib import Path

BOOT = Path(sys.argv[1] if len(sys.argv) > 1 else "boot.log")
SCEN = Path(sys.argv[2] if len(sys.argv) > 2 else "scenario.log")
SUMMARY = Path(sys.argv[3] if len(sys.argv) > 3 else "summary.md")

failures = []


def read(path):
    try:
        return path.read_text(errors="replace")
    except FileNotFoundError:
        failures.append(str(path) + " missing")
        return ""


boot, scen = read(BOOT), read(SCEN)

init_ms = re.search(r"initialize finished\. Entry scene boot took (\d+)ms", boot)
version = re.search(r"INFO RimWorld (\S+ rev\d+)", boot)
pack = re.search(r"Asset pack: (\d+) entries from '([^']+)'", boot)
defs = re.findall(r"\[SCENARIO\] defs\.thingDefs=(\d+)", scen)
uniques = re.findall(r"\[SCENARIO\] uniqueWeaponDefs=(\d+) (.*)", scen)
map_line = re.search(r"\[SCENARIO\] map=(.*)", scen)
icon_line = re.search(r"\[SCENARIO\] icon=(.*)", scen)
result = re.search(r"\[SCENARIO\] RESULT=(\w+)", scen)
wall = re.search(r"wall_seconds=([0-9.]+)", boot + scen)
exit_code = re.search(r"exit_code=(-?\d+)", boot + scen)

if version is None:
    failures.append("no 'RimWorld <version>' line: the managed host never reached Root.Start")
if init_ms is None:
    failures.append("no 'initialize finished' line: Bootstrap.Initialize did not complete")
if exit_code is not None and exit_code.group(1) != "0":
    failures.append("process exit code " + exit_code.group(1))
if result is None:
    failures.append("scenario never printed RESULT=")
elif result.group(1) != "OK":
    failures.append("scenario RESULT=" + result.group(1))

counts = {
    "ERROR": len(re.findall(r"^.*ERROR ", boot + scen, re.M)),
    "missing-texture": len(re.findall(r"Could not load Texture2D at", boot + scen)),
    "cross-reference": len(re.findall(r"Could not resolve cross-reference", boot + scen)),
    "def-load-exception": len(re.findall(r"Exception loading def from file", boot + scen)),
}

lines = ["## headless - real managed host, no GPU", ""]
lines.append("| item | value |")
lines.append("|---|---|")
lines.append("| game version | `%s` |" % (version.group(1) if version else "?"))
lines.append("| bootstrap init | `%s` |" % (init_ms.group(1) + " ms" if init_ms else "did not finish"))
lines.append("| process wall time | `%s` |" % (wall.group(1) + " s" if wall else "?"))
lines.append("| exit code | `%s` |" % (exit_code.group(1) if exit_code else "?"))
lines.append("| asset pack | `%s` |" % (pack.group(1) + " entries" if pack else "not mounted (-NoAssetPack)"))
lines.append("| thing defs loaded | `%s` |" % (defs[0] if defs else "?"))
lines.append("| unique-weapon defs | `%s` |" % (uniques[0][0] if uniques else "?"))
lines.append("| map | `%s` |" % (map_line.group(1) if map_line else "not attempted"))
lines.append("| icon | `%s` |" % (icon_line.group(1) if icon_line else "not attempted"))
lines.append("| scenario result | `%s` |" % (result.group(1) if result else "MISSING"))
lines.append("")
lines.append("log noise: " + ", ".join("%s=%d" % (k, v) for k, v in counts.items()))

out = "\n".join(lines)
print(out)
SUMMARY.write_text(out + "\n")
print("")
print("RESULT:", "FAIL" if failures else "OK")
for f in failures:
    print("  -", f)
sys.exit(1 if failures else 0)
