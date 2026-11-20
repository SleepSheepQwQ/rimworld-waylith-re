import argparse
import shutil
import sys
from pathlib import Path

"""Generates a mod whose animal textures carry the port's double-extension leak.

Graphic_Multi asks ContentFinder for "<path>_east" (and "_east" + "m" for the mask). When the asset
name keeps its inner extension ("IbexMale_east.png"), the exact key misses and the animal draws
nothing. The texture bytes are copied from whatever single-file images the caller supplies; only the
names matter for the lookup.

    python3 ci/make_multimod.py --run run --source <dir with *.png>
"""

FOLDER = Path("Textures/Things/Pawn/Animal/IbexLeak")


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--run", required=True)
    ap.add_argument("--source", required=True, help="directory holding at least one .png")
    args = ap.parse_args()

    src = Path(args.source)
    images = sorted(p for p in src.rglob("*.png"))
    if not images:
        print(f"no .png under {src}", file=sys.stderr)
        return 2
    img = images[0]

    mod = Path(args.run) / "Mods" / "WaylithMultiLeak"
    if mod.exists():
        shutil.rmtree(mod)
    (mod / "About").mkdir(parents=True)
    (mod / "About" / "About.xml").write_text(
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n"
        "<ModMetaData>\n"
        "  <name>WaylithMultiLeak</name>\n"
        "  <author>ci</author>\n"
        "  <packageId>waylith.multileak</packageId>\n"
        "  <supportedVersions><li>1.7</li></supportedVersions>\n"
        "  <description>Animal-style double-extension leak for CI.</description>\n"
        "</ModMetaData>\n",
        encoding="utf-8")

    (mod / "Defs").mkdir()
    (mod / "Defs" / "Defs.xml").write_text(
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n"
        "<Defs>\n"
        "  <ThingDef ParentName=\"AnimalThingBase\">\n"
        "    <defName>IbexLeakAnimal</defName>\n"
        "    <label>leak ibex</label>\n"
        "    <graphicData>\n"
        "      <texPath>Things/Pawn/Animal/IbexLeak/IbexLeak</texPath>\n"
        "      <graphicClass>Graphic_Multi</graphicClass>\n"
        "      <shaderType>CutoutComplex</shaderType>\n"
        "    </graphicData>\n"
        "    <race><body>QuadrupedAnimalWithPawsAndTail</body><animalType>Mammal</animalType></race>\n"
        "    <statBases><MoveSpeed>4.6</MoveSpeed></statBases>\n"
        "    <tools><li><label>teeth</label><capacities><li>Bite</li></capacities>"
        "<power>8.2</power><cooldownTime>2</cooldownTime></li></tools>\n"
        "  </ThingDef>\n"
        "</Defs>\n",
        encoding="utf-8")

    tex = mod / FOLDER
    tex.mkdir(parents=True)
    written = []
    for suffix in ("south", "north", "east", "west"):
        for name, extra in ((f"IbexLeak_{suffix}.png", ""), (f"IbexLeak_{suffix}m.png", "m")):
            # double extension: the loader strips one, so the asset name keeps ".png"
            dest = tex / (name + ".png")
            shutil.copyfile(img, dest)
            written.append(dest.name)

    print("mod:", mod)
    for w in sorted(written):
        print("  ", w)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
