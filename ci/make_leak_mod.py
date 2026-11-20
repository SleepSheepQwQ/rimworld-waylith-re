#!/usr/bin/env python3
"""Build the minimal "extension leak" mod inside an assembled run directory.

The mod is nothing but metadata, two ThingDefs and two copies of the same ten
revolver textures:

  Textures/.../LeakPlain/UniqueRevolverA.png ... E_m.png
  Textures/.../LeakExt/UniqueRevolverA.png.png ... E_m.png.png

Why the doubled extension: Verse.ModContentLoader.LoadTexture ends with

    texture.name = Path.GetFileNameWithoutExtension(file.Name)

so a loose file called "X.png" is named "X" (the normal, correct contract - it
is what the control folder exercises).  A file whose *inner* name carries .png
is named "X.png", which is exactly the shipped DLC asset naming that makes
Graphic_Collection.Init's `!name.EndsWith("_m")` filter miss the masks.  The
same one-extension peel is what flow/fixtures models with "X.png.dds"; using a
PNG here keeps every pixel real without a DDS encoder.
"""
import argparse
import shutil
import sys
from pathlib import Path

LETTERS = "ABCDE"
FOLDER = Path("Things/Item/Equipment/WeaponRanged/UniqueWeapons")


def main():
    here = Path(__file__).resolve().parent
    repo = here.parent
    ap = argparse.ArgumentParser()
    ap.add_argument("--run", required=True, help="assembled run directory (contains RimWorld)")
    ap.add_argument("--source", default=str(repo / "game/Resources/Textures" / FOLDER / "Revolver"))
    args = ap.parse_args()

    source = Path(args.source)
    mod = Path(args.run) / "Mods" / "WaylithLeakRepro"
    if mod.exists():
        shutil.rmtree(mod)
    (mod / "About").mkdir(parents=True)
    (mod / "Defs").mkdir(parents=True)
    shutil.copy(repo / "ci/leakmod/About.xml", mod / "About/About.xml")
    shutil.copy(repo / "ci/leakmod/Defs.xml", mod / "Defs/ThingDefs_LeakRepro.xml")

    written = []
    for letter in LETTERS:
        for suffix in ("", "_m"):
            name = f"UniqueRevolver{letter}{suffix}.png"
            src = source / name
            if not src.exists():
                sys.exit(f"missing source texture: {src}")
            plain = mod / "Textures" / FOLDER / "LeakPlain" / name
            plain.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(src, plain)
            written.append(plain)
            # The asset name is computed from the file name, not from this comment:
            # stripping the outer .png leaves "UniqueRevolverA.png" / "UniqueRevolverA_m.png".
            ext = mod / "Textures" / FOLDER / "LeakExt" / (name + ".png")
            ext.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(src, ext)
            written.append(ext)

    if len(written) != 20:
        sys.exit(f"expected 20 files, wrote {len(written)}")
    for p in written:
        if p.stat().st_size == 0:
            sys.exit(f"empty texture: {p}")

    print(f"leakmod dir={mod}")
    print(f"leakmod files={len(written)} (10 plain + 10 double-extension)")
    for p in sorted(written):
        rel = p.relative_to(mod)
        print(f"leakmod file={rel} bytes={p.stat().st_size}")


if __name__ == "__main__":
    main()
