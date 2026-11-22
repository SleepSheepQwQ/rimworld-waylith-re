#!/usr/bin/env python3
"""Write 4-direction body PNGs for the four animal defs at the exact texPaths their XML asks for.

The harness ships 21 textures and no animal art, so without this every animal graphic collapses to
BadMat and the icon chain never leaves its first branch. Dropping these files into the *game*
Resources tree (RimWorld_Data/Resources/Textures/...) makes the run use the same loader the shipped
content uses, not a mod-side path, so the values the probe prints are the ones the port would
produce for real art.

Suffixes come from Graphic_Multi.Init: "<path>_north|_east|_south|_west" for the body and
"<path>_<dir>m" for the mask (mask suffix is the bare letter "m" when maskPath is empty).

    python3 uicon/make_animal_art.py --resources run/RimWorld_Data/Resources
"""
import argparse
import os

from PIL import Image, ImageDraw

# texPath of pawnKind.lifeStages.Last().bodyGraphicData for each def, read from
# game/Data/Core/Defs/ThingDefs_Races/{Races_Animal_PigGroup,Races_Animal_SquirrelGroup}.xml
SPECIES = {
    "Ibex": ("Things/Pawn/Animal/Ibex/IbexMale", (176, 96, 44)),
    "Squirrel": ("Things/Pawn/Animal/Squirrel/Squirrel", (161, 95, 38)),
    "Rat": ("Things/Pawn/Animal/Rat/Rat", (128, 128, 128)),
    "Boomrat": ("Things/Pawn/Animal/Boomrat/Boomrat", (200, 60, 40)),
}
DIRS = ("north", "east", "south", "west")
SIZE = 64


def body_png(color, index):
    """Opaque blob on a transparent field; each direction gets a different fill ratio so a
    mixed-up rotation shows up in the pixel statistics."""
    img = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    inset = 4 + index * 3
    d.ellipse((inset, inset, SIZE - inset, SIZE - inset), fill=color + (255,))
    d.rectangle((0, 0, SIZE - 1, 2), fill=(255, 255, 255, 255))
    return img


def mask_png():
    img = Image.new("RGBA", (SIZE, SIZE), (128, 128, 128, 255))
    ImageDraw.Draw(img).rectangle((0, 0, SIZE // 2, SIZE - 1), fill=(32, 32, 32, 255))
    return img


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--resources", required=True, help="run/RimWorld_Data/Resources")
    args = ap.parse_args()

    root = os.path.join(args.resources, "Textures")
    written = []
    for name, (tex_path, color) in SPECIES.items():
        folder = os.path.join(root, *tex_path.split("/")[:-1])
        leaf = tex_path.split("/")[-1]
        os.makedirs(folder, exist_ok=True)
        for i, d in enumerate(DIRS):
            p = os.path.join(folder, f"{leaf}_{d}.png")
            body_png(color, i).save(p)
            written.append(p)
            mp = os.path.join(folder, f"{leaf}_{d}m.png")
            mask_png().save(mp)
            written.append(mp)
    print(f"wrote {len(written)} png files under {root}")
    for p in written:
        print(f"  {p} {os.path.getsize(p)}B")


if __name__ == "__main__":
    main()
