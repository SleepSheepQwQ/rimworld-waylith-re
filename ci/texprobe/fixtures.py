#!/usr/bin/env python3
"""Builds the texture fixtures the probe loads, from first principles (stdlib only).

Every fixture is 8x8 so the whole matrix is one screen of log lines. Each PNG is
re-read with Pillow when it is available: a fixture that fails its own control read
must never be used to accuse the engine (see waylith-port-ci-verification).

    fixtures.py <outdir>

Layout written:
  ok.png          8x8 RGBA, four quadrants: red / green / blue / white      (control)
  inter.png       the same pixels, Adam7 interlaced                        (interlace-flag test)
  pal.png         8x8 palette + tRNS, 2x2 tiling red/green/blue/transparent
  g16.png         8x8 16-bit RGB, left half red, right half blue
  gray.png        8x8 8-bit greyscale, left half black, right half white
  trunc.png       ok.png with its IDAT payload cut in half
  dxt1_ok.dds     8x8 BC1, every texel index 0 -> red
  dxt1_alpha.dds  8x8 BC1 in 3-colour (punch-through) mode: texels 0,1 blue, 2,3 transparent
  dxt3.dds        8x8 DXT3 (fourCC 'DXT3')                                 (rejected format)
  bc5_dx10.dds    8x8 BC5 blocks behind a DX10 header, dxgiFormat=83      (DX10 ignored)
  mip4.dds        8x8 BC1, header MipMapCount=4, all four levels present
  short_mips.dds  8x8 BC1, header MipMapCount=4, only level 0 present     (header lies)
  cache_a.png     red 8x8, stored with level-0 deflate so its length is fixed
  cache_b.png     green 8x8, byte-for-byte the same length as cache_a.png
"""
import struct
import sys
import zlib
from pathlib import Path

W = H = 8
RED, GREEN, BLUE, WHITE, CLEAR = (255, 0, 0, 255), (0, 255, 0, 255), (0, 0, 255, 255), (255, 255, 255, 255), (0, 0, 0, 0)

PASSES = [(0, 0, 8, 8), (4, 0, 8, 8), (0, 4, 4, 8), (2, 0, 4, 4), (0, 2, 2, 4), (1, 0, 2, 2), (0, 1, 1, 2)]


def chunk(kind, data):
    return struct.pack('>I', len(data)) + kind + data + struct.pack('>I', zlib.crc32(kind + data) & 0xFFFFFFFF)


def rows_rgba(pixel):
    """pixel(x,y) -> 4-byte RGBA, rows top-down as PNG wants them."""
    return [b''.join(bytes(pixel(x, y)) for x in range(W)) for y in range(H)]


def encode_png(rows, interlace, color=6, depth=8, extra=b''):
    if interlace:
        raw = []
        for x0, y0, dx, dy in PASSES:
            xs, ys = list(range(x0, W, dx)), list(range(y0, H, dy))
            if not xs or not ys:
                continue
            for y in ys:
                raw.append(b'\x00' + b''.join(_slice(rows[y], x, color, depth) for x in xs))
    else:
        raw = [b'\x00' + r for r in rows]
    return (b'\x89PNG\r\n\x1a\n'
            + chunk(b'IHDR', struct.pack('>IIBBBBB', W, H, depth, color, 0, 0, interlace))
            + extra
            + chunk(b'IDAT', zlib.compress(b''.join(raw), 9))
            + chunk(b'IEND', b''))


def _slice(row, x, color, depth):
    step = (4 if color == 6 else 3 if color == 2 else 1) * (depth // 8)
    return row[x * step:(x + 1) * step]


def quad_pixel(x, y):
    if y < 4:
        return RED if x < 4 else GREEN
    return BLUE if x < 4 else WHITE


def solid(pixel):
    return rows_rgba(lambda x, y: pixel)


def dds(fourcc, mip_count, data, dxgi=None):
    flags = 0x1 | 0x2 | 0x4 | 0x1000 | 0x20000 | 0x80000
    hdr = struct.pack('<4sIIIIIII', b'DDS ', 124, flags, H, W, len(data) // max(1, mip_count), 0, mip_count)
    hdr += b'\x00' * (76 - len(hdr))
    hdr += struct.pack('<II4sIIIII', 32, 0x4, fourcc, 0, 0, 0, 0, 0)
    hdr += struct.pack('<IIIII', 4198408, 0, 0, 0, 0)
    assert len(hdr) == 128, len(hdr)
    if dxgi is not None:
        hdr += struct.pack('<IIIII', dxgi, 3, 0, 1, 0)
    return hdr + data


def bc1_block(c0, c1, indices):
    return struct.pack('<HHI', c0, c1, indices)


def main(out):
    out = Path(out)
    out.mkdir(parents=True, exist_ok=True)

    ok = encode_png(rows_rgba(quad_pixel), 0)
    (out / 'ok.png').write_bytes(ok)
    (out / 'inter.png').write_bytes(encode_png(rows_rgba(quad_pixel), 1))
    (out / 'trunc.png').write_bytes(ok[:len(ok) // 2])

    # palette + tRNS: 2x2 tiling of red / green / blue / transparent
    pal_rows = [bytes([(x % 2) + 2 * (y % 2) for x in range(W)]) for y in range(H)]
    plte = bytes([255, 0, 0, 0, 255, 0, 0, 0, 255])
    (out / 'pal.png').write_bytes(
        encode_png(pal_rows, 0, color=3, extra=chunk(b'PLTE', plte) + chunk(b'tRNS', bytes([255, 255, 255, 0]))))

    # 16-bit RGB: left half red, right half blue (big-endian samples)
    red16 = struct.pack('>HHH', 65535, 0, 0)
    blue16 = struct.pack('>HHH', 0, 0, 65535)
    (out / 'g16.png').write_bytes(encode_png([b''.join(red16 if x < 4 else blue16 for x in range(W)) for _ in range(H)],
                                             0, color=2, depth=16))

    gray_row = lambda y: bytes(0 if x < 4 else 255 for x in range(W))  # noqa: E731
    (out / 'gray.png').write_bytes(encode_png([gray_row(y) for y in range(H)], 0, color=0))

    # same length, different pixels: both are level-0 (stored) deflate, so the only
    # variable between them is the byte content.
    def stored_png(name, pixel):
        rows = [b'\x00' + r for r in rows_rgba(lambda x, y: pixel)]
        comp = zlib.compressobj(level=0)
        body = comp.compress(b''.join(rows)) + comp.flush()
        (out / name).write_bytes(b'\x89PNG\r\n\x1a\n'
                                 + chunk(b'IHDR', struct.pack('>IIBBBBB', W, H, 8, 6, 0, 0, 0))
                                 + chunk(b'IDAT', body) + chunk(b'IEND', b''))

    stored_png('cache_a.png', RED)
    stored_png('cache_b.png', GREEN)
    a, b = (out / 'cache_a.png').read_bytes(), (out / 'cache_b.png').read_bytes()
    assert len(a) == len(b), (len(a), len(b))

    red_block = bc1_block(0xF800, 0x001F, 0)                       # 4-colour mode, every texel index 0 (red)
    punch_block = bc1_block(0x001F, 0xFFFF, 0xC0C0C0C0)            # 3-colour mode, texels 2,3 -> transparent
    lvl = lambda n, blk: blk * 4                                    # noqa: E731  four blocks = one 8x8 level

    (out / 'dxt1_ok.dds').write_bytes(dds(b'DXT1', 1, red_block * 4))
    (out / 'dxt1_alpha.dds').write_bytes(dds(b'DXT1', 1, punch_block * 4))
    (out / 'dxt3.dds').write_bytes(dds(b'DXT3', 1, b'\x00' * 64))
    (out / 'bc5_dx10.dds').write_bytes(dds(b'DX10', 1, b'\x00' * 64, dxgi=83))

    full = red_block * 4 + bc1_block(0xF800, 0x001F, 0) * 1 + bc1_block(0xF800, 0x001F, 0) * 1 + bc1_block(0xF800, 0x001F, 0) * 1
    (out / 'mip4.dds').write_bytes(dds(b'DXT1', 4, full))
    (out / 'short_mips.dds').write_bytes(dds(b'DXT1', 4, red_block * 4))

    # ---- controls ---------------------------------------------------------------------
    try:
        from PIL import Image
    except ImportError:
        print('fixtures: Pillow absent, structural checks only')
    else:
        def check(name, w, h, samples):
            im = Image.open(out / name).convert('RGBA')
            assert (im.width, im.height) == (w, h), (name, im.size)
            for (x, y), want in samples.items():
                got = im.getpixel((x, y))
                assert got == want, f'{name} {(x, y)}: {got} != {want}'
            print(f'fixtures: control {name} {w}x{h} ok')

        check('ok.png', 8, 8, {(0, 0): RED, (7, 0): GREEN, (0, 7): BLUE, (7, 7): WHITE})
        check('inter.png', 8, 8, {(0, 0): RED, (7, 0): GREEN, (0, 7): BLUE, (7, 7): WHITE})
        check('pal.png', 8, 8, {(0, 0): RED, (1, 0): GREEN, (0, 1): BLUE, (1, 1): CLEAR})
        check('g16.png', 8, 8, {(0, 0): RED, (7, 0): BLUE})
        check('gray.png', 8, 8, {(0, 0): (0, 0, 0, 255), (7, 0): (255, 255, 255, 255)})
        check('cache_a.png', 8, 8, {(0, 0): RED, (7, 7): RED})
        check('cache_b.png', 8, 8, {(0, 0): GREEN, (7, 7): GREEN})

    sizes = {p.name: p.stat().st_size for p in sorted(out.iterdir())}
    print('fixtures: ' + ' '.join(f'{k}={v}' for k, v in sizes.items()))


if __name__ == '__main__':
    main(sys.argv[1] if len(sys.argv) > 1 else 'fixtures')
