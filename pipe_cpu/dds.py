"""DDS parsing + BC4 / BC7 block decoding for the icon-pipeline CPU model.

No GPU, no Vulkan, no third-party image codec for BC4: the BC4 path here is written from the
D3D spec (8-byte block = two UNORM8 endpoints + 16 x 3-bit indices, the BC3-alpha interpolation
rule with the 6/7-value split for e0 <= e1).

BC7 is decoded by Pillow's DdsImagePlugin.  That is deliberate: BC7's partition tables are
arbitrary constant tables, and re-typing them from memory is exactly how a decoder ends up
plausible-but-wrong.  Instead every level is handed to a vetted third-party decoder and the
result is cross-checked structurally (see validate.py: each encoder mip level must correlate
with a box filter of the level above it, which fails loudly if the block walk drifts).
"""

import io
import struct
from PIL import Image

DDS_MAGIC = b"DDS "
HEADER_SIZE = 128
DX10_SIZE = 20
PIXEL_OFFSET = HEADER_SIZE + DX10_SIZE  # 148

DXGI_BC4_UNORM = 80
DXGI_BC7_UNORM = 98

FMT_NAME = {DXGI_BC4_UNORM: "BC4_UNORM", DXGI_BC7_UNORM: "BC7_UNORM"}
BLOCK_BYTES = {DXGI_BC4_UNORM: 8, DXGI_BC7_UNORM: 16}


def parse_header(data):
    if data[:4] != DDS_MAGIC:
        raise ValueError("not a DDS file")
    h = struct.unpack("<7I", data[4:32])
    # h = (size, flags, height, width, pitchOrLinearSize, depth, mipCount)
    size, flags, height, width, pitch, depth, mips = h
    if size != 124:
        raise ValueError("unexpected DDS header size %d" % size)
    fourcc = data[84:88]
    if fourcc != b"DX10":
        raise NotImplementedError("only DX10 DDS supported, got %r" % fourcc)
    dxgi, dim, misc, arr, misc2 = struct.unpack("<5I", data[128:148])
    if dxgi not in BLOCK_BYTES:
        raise NotImplementedError("unsupported dxgi format %d" % dxgi)
    return {
        "width": width, "height": height, "mips": max(1, mips),
        "dxgi": dxgi, "format": FMT_NAME[dxgi],
        "depth": depth, "dim": dim, "pitch": pitch,
    }


def level_bytes(w, h, dxgi):
    return ((w + 3) // 4) * ((h + 3) // 4) * BLOCK_BYTES[dxgi]


def split_levels(data):
    """Return [(w, h, raw_block_bytes)] for every mip level stored in the file."""
    hdr = parse_header(data)
    w, h, dxgi = hdr["width"], hdr["height"], hdr["dxgi"]
    off = PIXEL_OFFSET
    out = []
    for _ in range(hdr["mips"]):
        n = level_bytes(w, h, dxgi)
        if off + n > len(data):
            break
        out.append((w, h, data[off:off + n]))
        off += n
        if w == 1 and h == 1:
            break
        w, h = max(1, w // 2), max(1, h // 2)
    return hdr, out


# ---------------------------------------------------------------- BC4 (own implementation)

def _bc4_block_values(block):
    e0, e1 = block[0], block[1]
    idx = int.from_bytes(block[2:8], "little")
    pal = [0] * 8
    pal[0], pal[1] = e0, e1
    if e0 > e1:
        for i in range(1, 7):
            pal[i + 1] = (e0 * (7 - i) + e1 * i) // 7
    else:
        for i in range(1, 5):
            pal[i + 1] = (e0 * (5 - i) + e1 * i) // 5
        pal[6], pal[7] = 0, 255
    return [pal[(idx >> (3 * p)) & 7] for p in range(16)]


def decode_bc4(raw, w, h):
    """-> list[list[float]] rows of 0..1 red values, single channel."""
    bw, bh = (w + 3) // 4, (h + 3) // 4
    out = [[0.0] * w for _ in range(h)]
    p = 0
    for by in range(bh):
        for bx in range(bw):
            vals = _bc4_block_values(raw[p:p + 8])
            p += 8
            for i in range(16):
                x, y = bx * 4 + (i & 3), by * 4 + (i >> 2)
                if x < w and y < h:
                    out[y][x] = vals[i] / 255.0
    return out


# ---------------------------------------------------------------- BC7 (Pillow, per-level)

_TEMPLATE = None


def decode_bc7(raw, w, h, template=None):
    """Decode one BC7 mip level by re-wrapping it in a single-mip DDS and letting Pillow decode it.

    The wrapper copies the *file's own* 148-byte header and only patches width/height/mipCount:
    a hand-built header that gets DDSD_CAPS or the DX10 fourcc flags wrong silently decodes to
    near-zero pixels (Pillow then takes a DXT path with the wrong block size), which is worse than
    failing.  Callers pass the header of the file the level came from.
    """
    if template is None:
        template = _TEMPLATE
    hdr = bytearray(template[:PIXEL_OFFSET])
    struct.pack_into("<I", hdr, 12, h)   # height
    struct.pack_into("<I", hdr, 16, w)   # width
    struct.pack_into("<I", hdr, 20, level_bytes(w, h, DXGI_BC7_UNORM))  # linear size
    struct.pack_into("<I", hdr, 28, 1)   # mip count
    im = Image.open(io.BytesIO(bytes(hdr) + raw))
    im.load()
    if im.mode != "RGBA":
        im = im.convert("RGBA")
    buf = im.tobytes()  # RGBA8
    return [[tuple(buf[(y * w + x) * 4 + c] / 255.0 for c in range(4)) for x in range(w)]
            for y in range(h)]


def load_texture(path):
    """-> dict(header=..., levels=[(w,h,pixels)]) with pixels normalized floats.

    BC4 levels come back as rows of floats; BC7 levels as rows of 4-tuples of floats.
    """
    data = open(path, "rb").read()
    hdr, levels = split_levels(data)
    template = data[:PIXEL_OFFSET]
    out = []
    for (w, h, raw) in levels:
        if hdr["dxgi"] == DXGI_BC4_UNORM:
            px = decode_bc4(raw, w, h)
        else:
            px = decode_bc7(raw, w, h, template)  # already normalized 0..1 floats
        out.append((w, h, px))
    hdr["levels_loaded"] = len(out)
    return {"header": hdr, "levels": out}
