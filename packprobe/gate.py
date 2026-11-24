#!/usr/bin/env python3
"""Gate the real-pack GlyphFix probe."""
import re
import sys


def read(path):
    return open(path, encoding="utf-8", errors="replace").read()


def mask_rows(text):
    return re.findall(
        r"^\[VFS\]\s+raw name='UniqueRevolver[A-E]_m(?:\.png)?' 128x128 fmt=\S+ "
        r"mask=True pixels=(\d+)$",
        text, re.MULTILINE)


def readable_masks(text):
    return sum(1 for value in mask_rows(text) if int(value) == 16384)


def main(control_path, fixed_path):
    control = read(control_path)
    text = read(fixed_path)
    failures = []

    def need(pattern, label):
        if not re.search(pattern, text, re.MULTILINE):
            failures.append(label)

    need(r"^\[VFS\] startup: GlyphFix Bootstrap invoked$",
         "GlyphFix Bootstrap was not invoked")
    need(r"^\[VFS\] unique def=Gun_Revolver_Unique .* subCount=5 masks=0$",
         "the loaded unique-weapon collection is not 5 art / 0 masks")
    need(r"^\[VFS\] unique uiIcon='[^']+' bad=False mask=False size=128x128 pixels=16384 verdict=DRAW$",
         "the unique-weapon def icon is not readable non-mask art")

    control_readable = readable_masks(control)
    fixed_readable = readable_masks(text)
    if fixed_readable < 5:
        failures.append(f"only {fixed_readable}/5 packed DX10 BC4 masks are readable after GlyphFix")

    print(f"packed BC4 masks readable: control={control_readable}/5 fixed={fixed_readable}/5")
    for line in text.splitlines():
        if line.startswith("[VFS] unique") or "ddsalt: first pass swept" in line:
            print("| " + line)

    if failures:
        for failure in failures:
            print("FAIL:", failure)
        return 1
    if control_readable < 5:
        print("PASS: GlyphFix repaired packed BC4 masks; unique weapon is 5/0 and DRAW")
    else:
        print("PASS: real pack integration is regression-free and the unique weapon is 5/0 DRAW;"
              " this headless pack path was already native, so it does not reproduce the"
              " device-side loader defect")
    return 0


if __name__ == "__main__":
    if len(sys.argv) != 3:
        sys.exit("usage: gate.py <control.txt> <fixed.txt>")
    sys.exit(main(sys.argv[1], sys.argv[2]))
