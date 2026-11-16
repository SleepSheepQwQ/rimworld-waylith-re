#!/usr/bin/env python3
"""Map UI elements from a landscape screenshot to candidate device tap coordinates.

Device facts (from the phone): natural display 1220x2712, current rotation 1,
touchscreen 'goodix_ts' ABS_MT_POSITION_X 0..12200, ABS_MT_POSITION_Y 0..27120.

For every recognised text box it prints the box in image space and the four possible
image->display rotations, each converted to both `input tap` (display) and sendevent
(raw) coordinates. One of the four is correct; the device tells us which.
"""
import subprocess
import sys

from PIL import Image

NAT_W, NAT_H = 1220, 2712          # natural (portrait) display
RAW_X, RAW_Y = 12200, 27120        # touchscreen raw ranges


def ocr(path, lang="chi_sim+eng"):
    out = subprocess.run(["tesseract", path, "stdout", "-l", lang, "--psm", "6", "tsv"],
                         capture_output=True, text=True).stdout.splitlines()
    if not out:
        return []
    hdr = out[0].split("\t")
    idx = {n: i for i, n in enumerate(hdr)}
    rows = []
    for line in out[1:]:
        f = line.split("\t")
        if len(f) < len(hdr):
            continue
        text = f[idx["text"]].strip()
        if not text:
            continue
        try:
            conf = float(f[idx["conf"]])
        except ValueError:
            continue
        if conf < 30:
            continue
        x, y = int(f[idx["left"]]), int(f[idx["top"]])
        w, h = int(f[idx["width"]]), int(f[idx["height"]])
        rows.append((text, x + w // 2, y + h // 2, conf))
    return rows


def candidates(cx, cy, iw, ih):
    """Return [(rotation, disp_x, disp_y)] for the four possible orientations."""
    out = []
    # rot 0: display == image
    out.append((0, cx, cy))
    # rot 90: display width = ih, height = iw  (landscape screenshot of portrait device)
    out.append((90, cy, iw - cx))
    # rot 270
    out.append((270, ih - cy, cx))
    # rot 180
    out.append((180, iw - cx, ih - cy))
    return out


def raw_of(disp_x, disp_y, iw, ih):
    """Raw touchscreen coordinates for rotation 90 (natural x <- display y)."""
    rx = round(disp_y / ih * RAW_X)
    ry = round(disp_x / iw * RAW_Y)
    return rx, ry


def main():
    path = sys.argv[1] if len(sys.argv) > 1 else "ui/shot-dialog.png"
    img = Image.open(path)
    iw, ih = img.size
    print(f"image {iw}x{ih}  natural {NAT_W}x{NAT_H}  raw {RAW_X}x{RAW_Y}")
    rows = ocr(path)
    print(f"{len(rows)} text runs\n")
    for text, cx, cy, conf in rows:
        print(f"'{text}'  center=({cx},{cy}) conf={conf:.0f}")
        for rot, dx, dy in candidates(cx, cy, iw, ih):
            if not (0 <= dx <= iw and 0 <= dy <= ih):
                continue
            rx, ry = raw_of(dx, dy, iw, ih)
            print(f"    rot{rot:<3} input tap {dx} {dy:<5} sendevent raw {rx} {ry}")
        print()


if __name__ == "__main__":
    main()
