#!/usr/bin/env python3
# Converts the DDS icons the repository carries into PNGs the headless run can decode.
#
# Why: on the device every texture is decoded by the port's own native `gli` bridge
# (Brutal.Gli -> libgli.so, a C shim over g-truc/gli). That library is part of the Android
# bundle and is not in dlls/, so on a plain Linux runner every .dds falls back to
# Texture2D.DecodeFailurePlaceholder ("Substituting magenta placeholder", 2x2). PNG goes through
# stb instead, whose exports are a single public-domain header we can compile ourselves
# (natives/stb_shim.c -> libstb.so).
#
# The decode itself is not re-implemented here: pipe/decode_dds.py already decodes BC7 (via
# Pillow) and BC4 (block by block, cross-checked against Pillow) exactly the way the shader samples it.
#
# usage: dds_to_png.py <directory with .dds files, converted in place>
import os
import sys

from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, "..", "pipe"))

import decode_dds  # noqa: E402


def main():
    root = sys.argv[1]
    files = []
    for dirpath, _, names in os.walk(root):
        files += [os.path.join(dirpath, n) for n in names if n.lower().endswith(".dds")]
    if not files:
        print("dds_to_png: nothing to convert")
        return 0
    for path in sorted(files):
        rec, rgba = decode_dds.decode_one(path)
        w, h = rec["width"], rec["height"]
        png = os.path.splitext(path)[0] + ".png"
        if rec["format"] == "BC4":
            # single-channel mask: the GPU samples it as (R,0,0,1); an 8-bit grey PNG keeps the
            # channel, which is what the mask sampler reads.
            Image.frombytes("L", (w, h), bytes(rgba[0::4])).save(png)
        else:
            Image.frombytes("RGBA", (w, h), rgba).save(png)
        print(f"dds_to_png: {os.path.basename(path)} {rec['format']} {w}x{h} -> {os.path.basename(png)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
