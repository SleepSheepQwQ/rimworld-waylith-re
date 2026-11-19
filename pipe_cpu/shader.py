"""A pure-CPU implementation of CutoutComplex.frag.glsl plus the sampler semantics a GPU gives it.

Transcribed line by line from Data/Core/Shaders/Map/CutoutComplex.frag.glsl (the copy at
vk/CutoutComplex.frag.glsl is the same file with MaskGate.glsl inlined):

    vec2 uv = vUv * uMaterial._MainTex_ST.zw + uMaterial._MainTex_ST.xy;
    vec4 f = texture(_MainTex, uv);
    vec4 colPower = texture(_MaskTex, uv);
    colPower.rg *= MaskGate(colPower);              // clamp(mask.a * 255, 0, 1)
    f *= colPower.r * (_Color * vColor) + (1 - colPower.r);
    f *= colPower.g * _ColorTwo + (1 - colPower.g);
    if (f.a < 0.5) discard;

Nothing here rasterises triangles: an icon is a screen-aligned quad whose uv is an affine function
of the pixel, so the fragment stage is evaluated per output pixel.  What *does* need real modelling
is minification, because that is the whole question at icon sizes: the GPU picks a mip level
(lod = log2(source/icon)), samples bilinearly inside it, and the 0.5 alpha test then runs on the
*filtered* alpha.  Mip levels come from the DDS file itself (the engine ships a full chain), so the
filtering is the encoder's data, not something invented here.
"""

import math

from dds import load_texture


class Tex:
    """A DDS texture as clamp-to-edge float levels."""

    def __init__(self, path, single_channel="r", force_opaque_alpha=False):
        t = load_texture(path)
        self.path = path
        self.header = t["header"]
        self.raw_levels = t["levels"]
        self.single_channel = single_channel
        self.force_opaque_alpha = force_opaque_alpha
        self._cache = {}

    # ---- channel expansion for BC4 -------------------------------------------------------
    def _expand(self, w, h, px):
        """BC4 carries one channel.  How the engine hands it to the shader is a real fork:

        "r"         -> RGBA = (x, 0, 0, 1) ... D3D's BC4->RGBA expansion: data in R, alpha opaque
                                               (what a single-channel texture sampled as float4
                                               gives, and what Pillow's L decode implies).
        "alpha"     -> RGBA = (0, 0, 0, x) ... data in alpha, which is what MaskGate() reading
                                               mask.a would imply.
        "replicate" -> RGBA = (x, x, x, x) ... data broadcast, i.e. the value doubles as coverage.

        All three agree on the mask's own contribution to the tint product (r*gate is 0 wherever
        the mask is 0, and equals x wherever the mask is 1).  They disagree completely for the mask
        used *as the main texture*, where alpha decides discard - so that case is measured under
        all three.
        """
        out = []
        for y in range(h):
            row = []
            for x in range(w):
                v = px[y][x]
                if self.single_channel == "alpha":
                    row.append((0.0, 0.0, 0.0, 1.0 if self.force_opaque_alpha else v))
                elif self.single_channel == "replicate":
                    row.append((v, v, v, v))
                else:
                    row.append((v, 0.0, 0.0, 1.0))
            out.append(row)
        return out

    def level(self, i):
        if i < 0:
            i = 0
        if i in self._cache:
            return self._cache[i]
        if i >= len(self.raw_levels):
            i = len(self.raw_levels) - 1
        w, h, px = self.raw_levels[i]
        lv = Level(w, h, px if self.header["dxgi"] == 98 else self._expand(w, h, px))
        self._cache[i] = lv
        return lv

    def mip(self, lod):
        i = int(round(lod))
        i = max(0, min(i, max(0, len(self.raw_levels) - 1)))
        return self.level(i)


class Level:
    def __init__(self, w, h, px):
        self.w, self.h, self.px = w, h, px

    def sample(self, u, v):
        """Bilinear, clamp-to-edge, D3D texel-centre convention: x = u*w - 0.5."""
        w, h, px = self.w, self.h, self.px
        fx = u * w - 0.5
        fy = v * h - 0.5
        x0, y0 = math.floor(fx), math.floor(fy)
        tx, ty = fx - x0, fy - y0
        x0c = min(max(x0, 0), w - 1)
        x1c = min(max(x0 + 1, 0), w - 1)
        y0c = min(max(y0, 0), h - 1)
        y1c = min(max(y0 + 1, 0), h - 1)
        a, b = px[y0c][x0c], px[y0c][x1c]
        c, d = px[y1c][x0c], px[y1c][x1c]
        return tuple(
            (a[ch] * (1 - tx) + b[ch] * tx) * (1 - ty) + (c[ch] * (1 - tx) + d[ch] * tx) * ty
            for ch in range(4)
        )


class NullMask:
    """What the engine substitutes when a material has no _MaskTex: a 1x1 black texture."""

    def sample(self, u, v):
        return (0.0, 0.0, 0.0, 0.0)

    def mip(self, lod):
        return self


def mask_gate(col_power):
    """float MaskGate(vec4 mask) { return clamp(mask.a * 255.0, 0.0, 1.0); }"""
    return min(max(col_power[3] * 255.0, 0.0), 1.0)


def fragment(main_tex, mask_tex, u, v, lod=0.0,
             color=(1.0, 1.0, 1.0, 1.0), color_two=(1.0, 1.0, 1.0, 1.0),
             vcolor=(1.0, 1.0, 1.0, 1.0)):
    """One fragment.  Returns (r, g, b, a) *before* the alpha test, plus the discard verdict."""
    f = list(main_tex.mip(lod).sample(u, v))
    cp = list(mask_tex.mip(lod).sample(u, v))

    gate = mask_gate(cp)
    cp[0] *= gate
    cp[1] *= gate

    for ch in range(4):
        m = cp[0] * (color[ch] * vcolor[ch]) + (1.0 - cp[0])
        f[ch] *= m
    for ch in range(4):
        m = cp[1] * color_two[ch] + (1.0 - cp[1])
        f[ch] *= m

    return (f[0], f[1], f[2], f[3]), f[3] < 0.5


def render(main_tex, mask_tex, size, src=128, st=(0.0, 0.0, 1.0, 1.0),
           color=(1.0, 1.0, 1.0, 1.0), color_two=(1.0, 1.0, 1.0, 1.0),
           vcolor=(1.0, 1.0, 1.0, 1.0)):
    """Draw a src x src texel quad into a size x size icon slot. -> list of (r,g,b,a,alive)."""
    lod = math.log2(src / size) if size < src else 0.0
    sxx, syy, szz, sww = st
    out = []
    for y in range(size):
        for x in range(size):
            u0 = (x + 0.5) / size
            v0 = (y + 0.5) / size
            u = u0 * szz + sxx
            v = v0 * sww + syy
            rgba, dead = fragment(main_tex, mask_tex, u, v, lod, color, color_two, vcolor)
            out.append((rgba[0], rgba[1], rgba[2], rgba[3], not dead))
    return out


# The icon material is CutoutComplexUI, which is the same file *minus* `if (f.a < 0.5) discard;`
# (diff of the two shipped shaders: that one statement and nothing else).  So an icon fragment is
# never thrown away; it is composited with src-alpha blending.  That changes what "invisible" means
# at a pixel level and it is measured separately below.

UI_BG = (0.13, 0.13, 0.15)


def render_ui(main_tex, mask_tex, size, src=128, st=(0.0, 0.0, 1.0, 1.0),
              color=(1.0, 1.0, 1.0, 1.0), color_two=(1.0, 1.0, 1.0, 1.0),
              vcolor=(1.0, 1.0, 1.0, 1.0), bg=UI_BG):
    """CutoutComplexUI: same fragment maths, no discard, composite over the button background.

    -> list of (composited r, g, b, src alpha, alpha >= 0.5).  The last field keeps the same shape
    as render() so stats()/compare() can be reused: "alive" here means "at least half opaque".
    """
    lod = math.log2(src / size) if size < src else 0.0
    sxx, syy, szz, sww = st
    out = []
    for y in range(size):
        for x in range(size):
            u0 = (x + 0.5) / size
            v0 = (y + 0.5) / size
            u = u0 * szz + sxx
            v = v0 * sww + syy
            rgba, _ = fragment(main_tex, mask_tex, u, v, lod, color, color_two, vcolor)
            a = rgba[3]
            out.append((rgba[0] * a + bg[0] * (1 - a),
                        rgba[1] * a + bg[1] * (1 - a),
                        rgba[2] * a + bg[2] * (1 - a),
                        a, a >= 0.5))
    return out


def ink(px, bg=UI_BG):
    """How much the slot differs from an empty slot: mean |composite - background| luma.

    This is the UI-side counterpart of tile_luma.  It answers "would a player notice anything here",
    and unlike a coverage count it is non-zero for a black box (which obscures the button) and
    ~zero for a dark smear on a dark background.
    """
    bl = _luma(bg)
    return sum(abs(_luma(p) - bl) for p in px) / len(px)


# ------------------------------------------------------------------ metrics

def _luma(c):
    return 0.2126 * c[0] + 0.7152 * c[1] + 0.0722 * c[2]

def stats(px):
    n = len(px)
    alive = [p for p in px if p[4]]
    cov = len(alive) / n
    if alive:
        mean_rgb = tuple(sum(p[i] for p in alive) / len(alive) for i in range(3))
        mean_a = sum(p[3] for p in alive) / len(alive)
        mean_luma = sum(_luma(p) for p in alive) / len(alive)
    else:
        mean_rgb, mean_a, mean_luma = (0.0, 0.0, 0.0), 0.0, 0.0
    # light actually emitted by the whole icon slot: discarded pixels are the empty background
    tile_luma = sum(_luma(p) for p in alive) / n
    return {
        "size": int(round(math.sqrt(n))),
        "coverage": cov,
        "opaque_px": len(alive),
        "total_px": n,
        "mean_rgb": mean_rgb,
        "mean_alpha": mean_a,
        "mean_luma_alive": mean_luma,
        "tile_luma": tile_luma,
    }


def compare(a_px, b_px):
    """Difference between two renders of the same size, against the b (baseline) render."""
    n = len(a_px)
    both = [(p, q) for p, q in zip(a_px, b_px) if p[4] and q[4]]
    only_a = sum(1 for p, q in zip(a_px, b_px) if p[4] and not q[4])
    only_b = sum(1 for p, q in zip(a_px, b_px) if not p[4] and q[4])
    if both:
        dr = sum(abs(p[0] - q[0]) for p, q in both) / len(both)
        dg = sum(abs(p[1] - q[1]) for p, q in both) / len(both)
        db = sum(abs(p[2] - q[2]) for p, q in both) / len(both)
        maxd = max(max(abs(p[i] - q[i]) for i in range(3)) for p, q in both)
    else:
        dr = dg = db = maxd = 0.0
    inter = len(both)
    union = inter + only_a + only_b
    return {
        "mean_abs_dr": dr, "mean_abs_dg": dg, "mean_abs_db": db,
        "mean_abs_drgb": (dr + dg + db) / 3.0,
        "max_channel_delta": maxd,
        "iou": (inter / union) if union else 1.0,
        "status_flips": only_a + only_b,
        "status_flip_frac": (only_a + only_b) / n,
        "identical": (only_a == 0 and only_b == 0 and maxd == 0.0),
    }
