#!/usr/bin/env python3
"""Build GlyphFix 2.7.7 three ways, so the mitigation can be measured instead of asserted.

    stock      the branch as published
    priofirst  the GetIconFor postfix installed at Harmony Priority.First
    priolast   the GetIconFor postfix installed at Harmony Priority.Last

One line differs; everything else (install order, patched overloads) is identical. The source file is
restored after every build.

    python3 patchconflict/make_mitigated.py iconfix out
"""

import pathlib
import shutil
import subprocess
import sys

root = pathlib.Path(sys.argv[1]).resolve()
out = pathlib.Path(sys.argv[2]).resolve()
out.mkdir(parents=True, exist_ok=True)

src = root / "mod/Source/IconFallbackFix.cs"
orig = src.read_text()
needle = "harmony.Patch(m, postfix: new HarmonyMethod(closed));"
if orig.count(needle) != 1:
    sys.exit(f"expected exactly one '{needle}', found {orig.count(needle)} in {src}")


def build(name: str, replacement: str) -> None:
    src.write_text(orig.replace(needle, replacement))
    try:
        subprocess.run(
            ["dotnet", "build", str(root / "mod/AgentTouchFix.csproj"), "-c", "Release", "-v", "minimal"],
            check=True,
        )
    finally:
        src.write_text(orig)
    dlls = sorted((root / "mod/bin/Release").rglob("GlyphFix.dll"))
    if not dlls:
        sys.exit(f"{name}: no GlyphFix.dll produced")
    dst = out / f"GlyphFix-{name}.dll"
    shutil.copy2(dlls[0], dst)
    print(f"{name}: {dst} {dst.stat().st_size} bytes")


build("stock", needle)
build("priofirst", "harmony.Patch(m, postfix: new HarmonyMethod(closed) { priority = Priority.First });")
build("priolast", "harmony.Patch(m, postfix: new HarmonyMethod(closed) { priority = Priority.Last });")
# Strictly after every standard priority (First=800, Normal=0, Last=-800): the repair runs once, on
# the result every other postfix produced.
build("priomin", "harmony.Patch(m, postfix: new HarmonyMethod(closed) { priority = int.MinValue });")
