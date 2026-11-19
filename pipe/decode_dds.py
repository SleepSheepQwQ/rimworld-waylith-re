#!/usr/bin/env python3
"""Decode the iconpipe DDS corpus to RGBA8 exactly the way the shader will sample it.

Two decoders are used, on purpose:

  * BC7 (DXGI 98/99) -> Pillow's DDS plugin (a real BPTC decoder, not a raw dump).
  * BC4 (DXGI 80/81) -> decoded here, block by block, from the 8-byte BC4 block layout.
                        The result is cross-checked against Pillow, bit for bit.

Single-channel formats are expanded with GPU sampling semantics, i.e. a BC4 texel reads back as
(R, 0, 0, 1) - NOT as grey replicated over RGB. That difference matters: CutoutComplex multiplies
the green channel by colPower.g, so replicating R into G would tint differently from the device.

usage:
  decode_dds.py --assets assets/iconpipe --out build/decoded [--checksums assets/iconpipe/checksums.json]
                [--update-checksums] [--json build/decode.json]
"""
import argparse
import hashlib
import json
import os
import struct
import sys

from PIL import Image

DDS_MAGIC = b"DDS "
DDPF_FOURCC = 0x4
DXGI_BC7_UNORM = 98
DXGI_BC7_UNORM_SRGB = 99
DXGI_BC4_UNORM = 80
DXGI_BC4_SNORM = 81

FORMATS = {
    DXGI_BC7_UNORM: ("BC7", "rgba", 16),
    DXGI_BC7_UNORM_SRGB: ("BC7", "rgba", 16),
    DXGI_BC4_UNORM: ("BC4", "r", 8),
    DXGI_BC4_SNORM: ("BC4", "r", 8),
}


def parse_header(path):
    with open(path, "rb") as f:
        b = f.read()
    if len(b) < 148 or b[:4] != DDS_MAGIC:
        raise ValueError(f"{path}: not a DDS file")
    hdr_size, flags, height, width = struct.unpack_from("<IIII", b, 4)
    mip_count = struct.unpack_from("<I", b, 28)[0]
    pf_flags = struct.unpack_from("<I", b, 80)[0]
    fourcc = b[84:88]
    if not (pf_flags & DDPF_FOURCC):
        raise ValueError(f"{path}: not block-compressed (pfFlags=0x{pf_flags:x})")
    if fourcc != b"DX10":
        raise ValueError(f"{path}: legacy fourCC {fourcc!r}; this corpus is DX10 only")
    dxgi = struct.unpack_from("<I", b, 128)[0]
    if dxgi not in FORMATS:
        raise ValueError(f"{path}: unsupported DXGI format {dxgi}")
    name, kind, block_bytes = FORMATS[dxgi]
    offset = 128 + 20  # DDS_HEADER + DDS_HEADER_DXT10
    bw, bh = (width + 3) // 4, (height + 3) // 4
    level0 = bw * bh * block_bytes
    if len(b) < offset + level0:
        raise ValueError(f"{path}: truncated, need {offset + level0} bytes for mip0, have {len(b)}")
    return {
        "bytes": len(b),
        "width": width,
        "height": height,
        "mip_count": mip_count,
        "dxgi": dxgi,
        "format": name,
        "kind": kind,
        "data_offset": offset,
        "level0_bytes": level0,
        "blocks": (bw, bh),
        "payload": b[offset:offset + level0],
    }


def decode_bc4_block(block, out, x0, y0, w, h):
    a0, a1 = block[0], block[1]
    if a0 > a1:
        table = [a0, a1,
                 (6 * a0 + 1 * a1) // 7, (5 * a0 + 2 * a1) // 7,
                 (4 * a0 + 3 * a1) // 7, (3 * a0 + 4 * a1) // 7,
                 (2 * a0 + 5 * a1) // 7, (1 * a0 + 6 * a1) // 7]
    else:
        table = [a0, a1,
                 (4 * a0 + 1 * a1) // 5, (3 * a0 + 2 * a1) // 5,
                 (2 * a0 + 3 * a1) // 5, (1 * a0 + 4 * a1) // 5,
                 0, 255]
    bits = int.from_bytes(block[2:8], "little")
    for i in range(16):
        x, y = x0 + (i % 4), y0 + (i // 4)
        if x >= w or y >= h:
            continue
        out[y * w + x] = table[(bits >> (3 * i)) & 7]


def decode_bc4(info):
    w, h = info["width"], info["height"]
    bw, bh = info["blocks"]
    out = bytearray(w * h)
    payload = info["payload"]
    for by in range(bh):
        for bx in range(bw):
            off = (by * bw + bx) * 8
            decode_bc4_block(payload[off:off + 8], out, bx * 4, by * 4, w, h)
    return bytes(out)


def decode_bc7(path, info):
    im = Image.open(path)
    if im.size != (info["width"], info["height"]):
        raise ValueError(f"{path}: Pillow decoded {im.size}, header says "
                         f"{(info['width'], info['height'])}")
    if im.mode not in ("RGBA", "RGB"):
        raise ValueError(f"{path}: Pillow returned mode {im.mode}, expected RGBA")
    return im.convert("RGBA").tobytes()


def to_rgba(info, single):
    """Expand a single-channel decode to the RGBA byte order the harness uploads."""
    w, h = info["width"], info["height"]
    out = bytearray(w * h * 4)
    for i, v in enumerate(single):
        out[i * 4 + 0] = v
        out[i * 4 + 3] = 255
    return bytes(out)


def stats(rgba):
    n = len(rgba) // 4
    opaque = alpha_sum = 0
    amin, amax = 255, 0
    for i in range(0, len(rgba), 4):
        a = rgba[i + 3]
        alpha_sum += a
        if a >= 128:
            opaque += 1
        amin = min(amin, a)
        amax = max(amax, a)
    return {
        "pixels": n,
        "opaque": opaque,
        "opaque_pct": round(100.0 * opaque / n, 4) if n else 0.0,
        "alpha_min": amin,
        "alpha_max": amax,
        "alpha_mean": round(alpha_sum / n, 3) if n else 0.0,
    }


def decode_one(path):
    info = parse_header(path)
    single = None
    if info["format"] == "BC4":
        single = decode_bc4(info)
        ref = Image.open(path)
        if ref.mode != "L" or ref.size != (info["width"], info["height"]):
            raise ValueError(f"{path}: Pillow gave mode={ref.mode} size={ref.size}, expected L "
                             f"{info['width']}x{info['height']}")
        ref_bytes = ref.tobytes()
        diff = max(abs(a - b) for a, b in zip(single, ref_bytes)) if single else 0
        mismatches = sum(1 for a, b in zip(single, ref_bytes) if a != b)
        rgba = to_rgba(info, single)
    else:
        rgba = decode_bc7(path, info)
        diff, mismatches = None, None

    sha = hashlib.sha256(rgba).hexdigest()
    rec = {
        "file": os.path.basename(path),
        "format": info["format"],
        "dxgi": info["dxgi"],
        "width": info["width"],
        "height": info["height"],
        "mip_count_header": info["mip_count"],
        "level0_bytes": info["level0_bytes"],
        "file_bytes": info["bytes"],
        "sha256_rgba": sha,
        "stats": stats(rgba),
        "bc4_vs_pillow_max_diff": diff,
        "bc4_vs_pillow_mismatches": mismatches,
    }
    return rec, rgba


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--assets", default="assets/iconpipe")
    ap.add_argument("--out", default="build/decoded")
    ap.add_argument("--checksums", default="assets/iconpipe/checksums.json")
    ap.add_argument("--update-checksums", action="store_true")
    ap.add_argument("--json", default="build/decode.json")
    args = ap.parse_args()

    files = sorted(f for f in os.listdir(args.assets) if f.lower().endswith(".dds"))
    if not files:
        print(f"decode: no .dds under {args.assets}", file=sys.stderr)
        return 2
    os.makedirs(args.out, exist_ok=True)
    os.makedirs(os.path.dirname(args.json) or ".", exist_ok=True)

    records, errors = [], []
    for name in files:
        path = os.path.join(args.assets, name)
        try:
            rec, rgba = decode_one(path)
        except Exception as e:  # noqa: BLE001 - report every bad asset, not just the first
            errors.append(f"{name}: {e}")
            print(f"decode: FAIL {name}: {e}", file=sys.stderr)
            continue
        with open(os.path.join(args.out, os.path.splitext(name)[0] + ".rgba"), "wb") as f:
            f.write(rgba)
        records.append(rec)
        print(f"decode: {name:38s} {rec['format']} {rec['width']}x{rec['height']} "
              f"dxgi={rec['dxgi']} mips={rec['mip_count_header']} "
              f"opaque={rec['stats']['opaque_pct']:.2f}% sha={rec['sha256_rgba'][:12]}")

    if errors:
        print(f"decode: {len(errors)} asset(s) failed", file=sys.stderr)
        return 1

    pinned = {}
    if os.path.exists(args.checksums):
        with open(args.checksums) as f:
            pinned = json.load(f)

    if args.update_checksums:
        with open(args.checksums, "w") as f:
            json.dump({r["file"]: r["sha256_rgba"] for r in records}, f, indent=2, sort_keys=True)
            f.write("\n")
        print(f"decode: wrote {args.checksums}")

    drift = []
    if pinned and not args.update_checksums:
        for r in records:
            want = pinned.get(r["file"])
            if want is None:
                drift.append(f"{r['file']}: not pinned")
            elif want != r["sha256_rgba"]:
                drift.append(f"{r['file']}: pinned {want[:12]} != decoded {r['sha256_rgba'][:12]}")

    with open(args.json, "w") as f:
        json.dump({"assets": records, "checksum_expected": pinned, "checksum_drift": drift,
                   "errors": errors}, f, indent=2)

    bad = [r for r in records if r["bc4_vs_pillow_mismatches"]]
    for r in bad:
        print(f"decode: BC4 cross-check FAIL {r['file']}: "
              f"{r['bc4_vs_pillow_mismatches']} texels differ (max {r['bc4_vs_pillow_max_diff']})",
              file=sys.stderr)
    if drift:
        for d in drift:
            print(f"decode: checksum drift {d}", file=sys.stderr)
    if bad or drift:
        return 1
    print(f"decode: {len(records)} assets ok, pinned checksums match, BC4 == Pillow")
    return 0


if __name__ == "__main__":
    sys.exit(main())
