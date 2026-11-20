#!/usr/bin/env python3
# Reads the headless run logs and turns them into (a) a job summary and (b) an exit code.
# The exit code is the result: a run that did not boot, a map the game's own generator never
# produced, an icon that is not one of the def's variants, or a scenario that did not print
# RESULT=OK all fail the job instead of producing a green log nobody reads.
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


ANSI = re.compile(r"\x1b\[[0-9;]*m")


def plain(text):
    return ANSI.sub("", text)


boot, scen = plain(read(BOOT)), plain(read(SCEN))

init_ms = re.search(r"initialize finished\. Entry scene boot took (\d+)ms", boot)
version = re.search(r"INFO RimWorld (\S+ rev\d+)", boot)
pack = re.search(r"Asset pack: (\d+) entries from '([^']+)'", boot)
defs = re.findall(r"\[SCENARIO\] defs\.thingDefs=(\d+)", scen)
uniques = re.findall(r"\[SCENARIO\] uniqueWeaponDefs=(\d+) (.*)", scen)
variants = re.search(r"\[SCENARIO\] def\.variants=(\d+) names=(.*)", scen)
map_line = re.search(r"\[SCENARIO\] map=(\w+)(.*)", scen)
spawn_line = re.search(r"\[SCENARIO\] spawn=(.*)", scen)
icon_line = re.search(r"\[SCENARIO\] icon=(.*)", scen)
pixels_line = re.search(r"\[SCENARIO\] pixels=(.*)", scen)
png_line = re.search(r"\[SCENARIO\] png=(.*)", scen)
def_icon = re.search(r"\[SCENARIO\] defIcon=(.*)", scen)
asserts = re.findall(r"\[SCENARIO\] assert=(\S+) ok=(true|false) ?(.*)", scen)
result = re.search(r"\[SCENARIO\] RESULT=(\w+)", scen)
wall = re.search(r"wall_seconds=([0-9.]+)", boot + scen)
exit_code = re.search(r"exit_code=(-?\d+)", boot + scen)

# optional: written by ci/verify_icon.py, which decodes the PNG Pillow and compares it with the
# shipped asset - an independent decoder and an independent comparison from the in-process hook
verify = {}
verify_path = SUMMARY.parent / "verify.txt"
if verify_path.exists():
    for line in verify_path.read_text(errors="replace").splitlines():
        if line.startswith("verify.") and "=" in line:
            k, v = line[len("verify."):].split("=", 1)
            verify[k] = v

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
if map_line is None:
    failures.append("scenario never reported a map")
elif map_line.group(1) != "OK":
    failures.append("map the game generated: " + map_line.group(1) + map_line.group(2))
if not asserts:
    failures.append("scenario printed no assertions")
for name, ok, detail in asserts:
    if ok != "true":
        failures.append("assert %s failed: %s" % (name, detail.strip()))
if result is not None and result.group(1) == "OK" and not icon_line:
    failures.append("RESULT=OK but no icon line")

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
lines.append("| def variants | `%s` |" % (variants.group(1) + " " + variants.group(2) if variants else "?"))
lines.append("| map | `%s` |" % (map_line.group(1) + map_line.group(2) if map_line else "not attempted"))
lines.append("| spawn | `%s` |" % (spawn_line.group(1) if spawn_line else "not attempted"))
lines.append("| icon | `%s` |" % (icon_line.group(1) if icon_line else "not attempted"))
lines.append("| pixels | `%s` |" % (pixels_line.group(1) if pixels_line else "not decoded"))
lines.append("| png | `%s` |" % (png_line.group(1) if png_line else "not written"))
lines.append("| def-level icon | `%s` |" % (def_icon.group(1) if def_icon else "not probed"))
if verify:
    lines.append("| shipped-asset match | `%s` (%s vs `%s`, alpha identical=%s, max delta=%s, mean delta=%s) |" % (
        verify.get("result", "?"), verify.get("size", "?"), verify.get("source", "?"),
        verify.get("alphaIdentical", "?"), verify.get("maxChannelDelta", "?"), verify.get("meanAbsDelta", "?")))
    lines.append("| mask distance | `max channel delta %s` (the icon must not be the mask) |" % verify.get("maskMaxChannelDelta", "?"))
    if verify.get("result") not in (None, "OK"):
        failures.append("ci/verify_icon.py: PNG does not match the shipped asset")
lines.append("| scenario result | `%s` |" % (result.group(1) if result else "MISSING"))
lines.append("")
lines.append("| assertion | result | detail |")
lines.append("|---|---|---|")
for name, ok, detail in asserts:
    lines.append("| `%s` | %s | `%s` |" % (name, "ok" if ok == "true" else "**FAIL**", detail.strip().replace("|", "\\|")))
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
