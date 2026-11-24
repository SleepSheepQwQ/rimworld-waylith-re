#!/usr/bin/env python3
"""Gate the real-pack GlyphFix probe."""
import re
import sys


def main(path):
    text = open(path, encoding="utf-8", errors="replace").read()
    failures = []

    def need(pattern, label):
        if not re.search(pattern, text, re.MULTILINE):
            failures.append(label)

    need(r"^\[VFS\] startup: GlyphFix Bootstrap invoked$",
         "GlyphFix Bootstrap was not invoked")
    need(r"ddsalt: first pass swept \d+ DDS entries from loose\+pack content,.*replaced=([1-9]\d*)",
         "DdsPrefixAlt did not replace an unusable pack DDS")
    need(r"^\[VFS\] unique def=Gun_Revolver_Unique .* subCount=5 masks=0$",
         "the loaded unique-weapon collection is not 5 art / 0 masks")
    need(r"^\[VFS\] unique uiIcon='[^']+' bad=False mask=False size=128x128 pixels=16384 verdict=DRAW$",
         "the unique-weapon def icon is not readable non-mask art")

    mask_rows = re.findall(
        r"^\[VFS\]\s+raw name='UniqueRevolver[A-E]_m\.png' 128x128 fmt=\S+ mask=True pixels=(\d+)$",
        text, re.MULTILINE)
    readable = sum(1 for value in mask_rows if int(value) == 16384)
    if readable < 5:
        failures.append(f"only {readable}/5 packed DX10 BC4 masks are readable")

    print(f"packed BC4 masks readable={readable}/5")
    for line in text.splitlines():
        if line.startswith("[VFS] unique") or "ddsalt: first pass swept" in line:
            print("| " + line)

    if failures:
        for failure in failures:
            print("FAIL:", failure)
        return 1
    print("PASS: real Odyssey Assets.zip DDS entries were repaired; unique weapon is 5/0 and DRAW")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1] if len(sys.argv) > 1 else "out/vfs.txt"))
