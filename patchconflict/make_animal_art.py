#!/usr/bin/env python3
"""Write 4-direction body + mask PNGs for the animal kinds the probe spawns.

The harness ships 21 textures and no animal art, so every animal graphic would collapse to BadMat
and GetIconFor would never leave its early return. Dropping the files into the game's own Resources
tree (run/RimWorld_Data/Resources/Textures/...) puts them behind the same loader the shipped content
uses, so the texture and material the probe reports are the ones this port produces for real art.

Suffixes come from Graphic_Multi.Init: "<path>_north|_east|_south|_west" for the body and
"<path>_<dir>m" for the mask. texPaths are read from
game/Data/Core/Defs/ThingDefs_Races/{Races_Animal_PigGroup,Races_Animal_SquirrelGroup}.xml

    python3 pawnsicon/make_animal_art.py --resources run/RimWorld_Data/Resources
"""
import argparse
import os

from PIL import Image, ImageDraw

SPECIES = {
    "Rat": ("Things/Pawn/Animal/Rat/Rat", (128, 128, 128)),
    "Ibex": ("Things/Pawn/Animal/Ibex/IbexMale", (176, 96, 44)),
    "Squirrel": ("Things/Pawn/Animal/Squirrel/Squirrel", (161, 95, 38)),
}
DIRS = ("north", "east", "south", "west")
SIZE = 64


def body_png(color, index):
    """Opaque blob on a transparent field; each direction gets a different fill so a mixed-up
    rotation shows up in the pixel statistics."""
    img = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    inset = 4 + index * 4
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
