# pipe_cpu — the icon material evaluated on the CPU, on the real textures

An independent answer to "what do these three inputs actually look like at icon size", built without
Vulkan, without lavapipe, and without sharing code with the `pipe/` / `vk/` harnesses. The shader
maths is transcribed by hand from the shipped fragment stages, the textures are the game's real BC7 /
BC4 bytes, and the minification uses the game's own mip chain.

```
python3 pipe_cpu/validate.py    # decoder + fixture checks (must pass first)
python3 pipe_cpu/run.py         # 3 input classes x 3 sizes x 2 shader variants; asserts its claims
```

## Method

**Geometry.** An icon is a screen-aligned quad, so its uv is an affine function of the output pixel.
There is no triangle to rasterise: for each of the S x S output pixels the fragment stage is evaluated
at `uv = ((x+0.5)/S, (y+0.5)/S)` with `_MainTex_ST = (0,0,1,1)` — the identity the shader's own
annotation asks for. That is the whole "rasteriser": 16 384 / 4 096 / 1 024 fragment evaluations.

**Minification.** This is the part that needs real modelling, and it is where a GPU harness and a CPU
one can silently diverge. At icon size S the quad covers S/S of the texture, so the GPU's screen-space
derivatives give `lod = log2(128/S)`; it samples bilinearly *inside that mip level* and only then does
whatever the shader does with the filtered value. Here the DDS file's own mip chain is used (every
128x128 icon ships 8 levels), bilinear is clamp-to-edge with `texel = uv*W - 0.5`, and the alpha test
(where the variant has one) runs on the filtered alpha.

**Shader.** Line for line:

```
f          = texture(_MainTex, uv)
colPower   = texture(_MaskTex, uv)
colPower.rg *= clamp(colPower.a * 255.0, 0.0, 1.0)      // MaskGate
f *= colPower.r * (_Color * vColor) + (1 - colPower.r)
f *= colPower.g * _ColorTwo        + (1 - colPower.g)
[CutoutComplex only] if (f.a < 0.5) discard
```

**The two shipped variants differ by exactly that one statement,** and the icon slot uses the one
*without* it:

| shader | used for | discard | blending |
|---|---|---|---|
| `CutoutComplex.frag.glsl` | world/map things | yes | opaque |
| `CutoutComplexUI.frag.glsl` | build-menu / inventory icon material | **no** | src-alpha over the button |

Both are measured (`render()` / `render_ui()`), because "almost invisible" means different things:
with the alpha test, an invisible icon is one whose fragments are thrown away; without it, every
fragment is written and "invisible" means the fragment is transparent or the same colour as the
button. Collapsing the two is the easiest way to get the wrong answer for `mask_as_main`.

**Inputs.**

| case | `_MainTex` | `_MaskTex` | meaning |
|---|---|---|---|
| `passthrough` | main | none (1x1 black) | "the main image straight through" reference |
| `main_mask` | main | real BC4 | what a correctly bound unique weapon has |
| `main_mask_tint` | main | real BC4, `_Color=(0.55,0.55,0.62)` | does the mask change the picture at all? |
| `main_null` | main | none (1x1 black) | the engine's substitute when the mask is missing |
| `mask_as_main` | mask | none | the two slots crossed |
| `full_swap` | mask | main | both slots crossed |

`passthrough` and `main_null` are the same binding on purpose: the engine has no "empty mask" state
other than the 1x1 black substitute, so the null-mask hypothesis *is* the passthrough case, and
measuring it separately only proves the code does what the shader says.

**Metrics.** `opaque %` = fraction of pixels with (filtered) alpha >= 0.5. `tile luma` = mean
luminance of the whole slot with discarded pixels counting as empty background. `ink` (icon variant) =
mean |composite − button background| luma over the whole slot, i.e. 0 means "an empty slot". `IoU` and
`status flips` compare the survivor set against passthrough; `dRGB` is the mean absolute per-channel
difference over pixels both renders draw.

**BC4 → RGBA is a fork, not a detail.** BC4 carries one channel. Three expansions are measured,
because they decide the `mask_as_main` verdict and because they are the most likely source of a
disagreement with another harness:

* `__r` — `(x,0,0,1)`: D3D's BC4 expansion, data in R, alpha opaque. Primary reading.
* `__alpha` — `(0,0,0,x)`: data in alpha, which is what `MaskGate()` reading `mask.a` would imply.
* `__replicate` — `(x,x,x,x)`: broadcast, value doubles as coverage.

All three agree on the mask's contribution to the tint (`r*gate` is 0 wherever the mask is 0), and
disagree completely when the mask is what decides `discard` / output alpha.

## Fixtures

Six real unique-weapon pairs copied byte-identically out of the installed game's
`Data/Odyssey/Assets.zip` (`fixtures/PROVENANCE.txt` has entry names, byte counts and sha256). They
span the full mask-coverage range of all 71 main+mask pairs in that archive: 1.37 % (ChainShotgunB),
2.62 %, 3.92 %, 5.22 %, 10.95 %, 19.16 % (ChargeLanceB). Main = BC7_UNORM (dxgi 98, engine format
code 25); mask = BC4_UNORM (dxgi 80, format code 26, `single_channel=1` in `textures.info`).

## Decoders

* **BC4** — written here from the block spec (two UNORM8 endpoints, 16 x 3-bit indices, the
  `e0<=e1` six-value branch, truncating interpolation as the reference decoder does). Bit-exact
  against Pillow's independent BC4 decode on all six masks: 0 mismatched texels of 16 384 per file.
* **BC7** — delegated to Pillow's `DdsImagePlugin`, one mip level at a time (the level's bytes are
  re-wrapped in the file's own 148-byte DX10 header). Re-typing BC7's 64-entry partition tables from
  memory is how a decoder becomes plausible-but-wrong, so the vetted decoder is the safer choice —
  and it is checked structurally instead: level 1 must correlate with a box filter of level 0
  (`r = 0.988 … 0.9998`, which collapses if the block walk drifts) and level 0 must not be constant
  (a mis-built wrapper decodes to near-zero pixels *and* a passing correlation, so this check is
  what actually catches it).

`validate.py` runs 36 such checks. `run.py` exits non-zero unless all 28 numeric claims still hold.

## Results

`out/TABLE.md` (alpha-test variant), `out/TABLE_UI.md` (icon variant, with `ink`), raw
`out/results.json`, machine-readable `out/claims.json`, pictures `out/preview_*.png`,
`out/ui/preview_*.png`, grids `out/compare_sheet_alpha.png` / `out/compare_sheet_ui.png`.

Means over the six pairs, white `_Color`, white `vColor`.

**`CutoutComplex` — the alpha-test variant** (opaque %, tile luma, mean RGB):

| size | case | opaque % | tile luma | mean RGB | dRGB vs passthru | IoU | flips % |
|---|---|---|---|---|---|---|---|
| 128 | `passthrough` | 21.61 | 0.0771 | (0.349, 0.334, 0.330) | – | – | – |
| 128 | `main_mask` | 21.61 | 0.0771 | (0.349, 0.334, 0.330) | 0.0000 | 1.000 | 0.00 |
| 128 | `main_mask_tint` | 21.61 | 0.0564 | (0.260, 0.246, 0.255) | 0.0842 | 1.000 | 0.00 |
| 128 | `main_null` | 21.61 | 0.0771 | (0.349, 0.334, 0.330) | 0.0000 | 1.000 | 0.00 |
| 128 | `mask_as_main__r` | 100.00 | 0.0137 | (0.064, 0.000, 0.000) | 0.2921 | 0.216 | 78.39 |
| 128 | `mask_as_main__alpha` | 6.54 | 0.0000 | (0.000, 0.000, 0.000) | 0.7560 | 0.270 | 15.07 |
| 128 | `mask_as_main__replicate` | 6.54 | 0.0630 | (0.950, 0.950, 0.950) | 0.1958 | 0.270 | 15.07 |
| 64 | `passthrough` | 21.54 | 0.0810 | (0.370, 0.355, 0.350) | – | – | – |
| 64 | `main_mask_tint` | 21.54 | 0.0596 | (0.277, 0.262, 0.272) | 0.0876 | 1.000 | 0.00 |
| 64 | `mask_as_main__r` | 100.00 | 0.0146 | (0.069, 0.000, 0.000) | 0.3056 | 0.215 | 78.46 |
| 64 | `mask_as_main__alpha` | 7.02 | 0.0000 | (0.000, 0.000, 0.000) | 0.7376 | 0.293 | 14.51 |
| 32 | `passthrough` | 21.08 | 0.0871 | (0.408, 0.393, 0.388) | – | – | – |
| 32 | `main_mask_tint` | 21.08 | 0.0650 | (0.311, 0.296, 0.306) | 0.0922 | 1.000 | 0.00 |
| 32 | `mask_as_main__r` | 100.00 | 0.0161 | (0.076, 0.000, 0.000) | 0.3334 | 0.211 | 78.92 |
| 32 | `mask_as_main__alpha` | 7.80 | 0.0000 | (0.000, 0.000, 0.000) | 0.7014 | 0.331 | 13.28 |

`main_mask` and `main_null` are bit-identical to passthrough at every size (max channel delta 0), so
their 64/32 rows are just the passthrough rows.

`tile luma` = mean luminance of *every* pixel of the slot with discarded pixels counting as empty
background, i.e. how much light the icon puts on screen. That is the metric that separates "different
colour" from "not there", which a drawn-pixel count alone cannot do.

**`CutoutComplexUI` — the icon material** (no discard, composited over a dark button):

| size | case | opaque % | ink | ink vs empty slot | mean RGB |
|---|---|---|---|---|---|
| 128 | `passthrough` | 21.61 | 0.0636 | 1.00x | (0.351, 0.337, 0.333) |
| 128 | `main_mask` | 21.61 | 0.0636 | 1.00x | (0.351, 0.337, 0.333) |
| 128 | `main_mask_tint` | 21.61 | 0.0430 | 0.68x | (0.262, 0.248, 0.258) |
| 128 | `main_null` | 21.61 | 0.0636 | 1.00x | (0.351, 0.337, 0.333) |
| 128 | `mask_as_main__r` | 100.00 | 0.1275 | **2.00x** | (0.064, 0.000, 0.000) |
| 128 | `mask_as_main__alpha` | 6.54 | 0.0085 | **0.13x** | (0.007, 0.007, 0.008) |
| 128 | `mask_as_main__replicate` | 6.54 | 0.0534 | 0.84x | (0.925, 0.925, 0.926) |
| 32 | `passthrough` | 21.08 | 0.0646 | 1.00x | (0.407, 0.392, 0.387) |
| 32 | `mask_as_main__r` | 100.00 | 0.1238 | 1.92x | (0.076, 0.000, 0.000) |
| 32 | `mask_as_main__alpha` | 7.80 | 0.0100 | 0.15x | (0.020, 0.020, 0.023) |
| 32 | `mask_as_main__replicate` | 7.80 | 0.0536 | 0.83x | (0.757, 0.757, 0.760) |

`mask_as_main__r`'s ink is nearly fixture-independent (0.1213…0.1305 at 128 px) — the signature of a
box whose colour does not depend on the artwork at all.

## Conclusion

1. **`mask_as_main` is the only input class that reads as "almost nothing there".**
   * Alpha-test variant, R reading: 100 % of the slot survives the test, but every pixel is the mask's
     R channel — mean RGB `(0.064,0,0)`, tile luma **0.0137 vs 0.0771** (5.6x darker), 78.4 % of
     pixels flip survivor status, IoU 0.216. The artwork is replaced by a near-black square with a red
     bar.
   * Alpha-test variant, alpha reading: only the mask's own extent survives (6.54 % of the slot;
     1.3…18.4 % per fixture) and **every surviving pixel is pure black — tile luma 0.00000 at all
     three sizes.** An empty slot.
   * Icon variant (no discard), R reading: **opaque 100 %, ink 0.1275 = 2.00x the empty-slot
     baseline** — the icon area becomes a black box that also blots out the button behind it.
   * Icon variant, alpha reading: coverage stays at the mask's extent and **ink 0.0085 = 13 % of the
     baseline** (0.15x at 32 px) — a dark smudge on a dark button, i.e. effectively nothing.
   * Replicate reading is the outlier: 6.5 % of the slot at RGB ≈ 0.95 (icon-variant ink 0.84x). Still
     the wrong picture, but bright rather than dark. So the *kind* of wreckage depends on the channel
     reading; the *fact* of it does not.

2. **`main_mask` is "变色" only, and with a white `_Color` it is not even that.** At the material's
   default `_Color = (1,1,1,1)`, `r*1 + (1-r) = 1`: the render is **bit-identical to passthrough**
   (max channel delta 0, every fixture, every size, both variants). Give `_Color` a value and the
   picture darkens *inside the mask only*: mean RGB `(0.349,0.334,0.330) → (0.260,0.246,0.255)`, mean
   |dRGB| 0.0842, max channel delta 0.450, while the silhouette does not move at all — IoU 1.000, 0
   status flips, opaque 21.61 % → 21.61 %, tile luma 0.0771 → 0.0564 (−27 %), icon-variant ink
   0.0636 → 0.0430. The mask's extent over the six fixtures is 1.4…19.2 % of the face, so at most a
   fifth of the icon can change colour.

3. **`main_null` is a bit-exact no-op, in both variants.** A 1x1 black mask gives `colPower = 0`, both
   multipliers collapse to 1, and the output equals passthrough exactly — 0 delta, 6 fixtures, 3
   sizes, 2 variants. So "the def has no maskPath / the mask failed to load" **cannot** produce a
   broken icon; it produces a perfectly correct untinted icon. Any theory that blames a missing mask
   needs a second mechanism, and `_ColorTwo` cannot supply one: BC4 has no G channel, so
   `colPower.g = 0` and `colTwoMultiplier` is 1 for these assets.

4. **Icon size is not the driver.** Opaque fraction 21.61 % → 21.54 % → 21.08 % from 128 to 32 px
   (2.5 % relative); mean RGB rises slightly (0.349 → 0.408) because mip averaging pulls mid-alpha
   texels into the survivor set; mean alpha falls 0.980 → 0.944. No case changes class between sizes,
   and `mask_as_main` gets marginally *lighter* under minification (tile luma 0.0137 → 0.0161). If
   the icons are wrong, it is the binding, not the slot size.

5. **Therefore the only class that can make a unique-weapon icon read as "empty" is the mask reaching
   `_MainTex`.** Not a missing mask (no-op), not a resize (2.5 %), not a tint (shape unchanged). And
   the in-game signature depends on the variant: an opaque near-black box (R reading) or a nearly
   invisible dark smudge (alpha reading) — both read to a player as "the icon didn't draw", and
   neither is fixed by repairing the mask path.

## Where another harness can legitimately disagree, and why

1. **Which shader variant is being measured.** `CutoutComplex` and `CutoutComplexUI` differ by
   `if (f.a < 0.5) discard;` and nothing else, and the icon material is the one *without* it. A
   harness that runs the map shader for icons reports "6.5 % of pixels survive" for
   `mask_as_main__alpha`; the icon variant really paints that 6.5 % *fully transparent-black*, which
   is a different (and equally broken) picture — ink 0.0085 vs a 0.0636 baseline. `passthrough` also
   differs in kind: with no discard, transparent texels still write, they just write nothing visible.
2. **BC4 channel expansion.** Any harness uploads `R8G8B8A8_UNORM` from an `.rgba` it built itself, so
   whatever it wrote there *is* its answer. `(x,0,0,255)` reproduces my `__r` column (opaque black
   box, 100 % coverage, tile luma 0.0137); `(0,0,0,x)` reproduces `__alpha` (6.54 % coverage, tile
   luma 0.00000). Those two differ by 15x in coverage and in mean RGB (0.064 vs 0.000) — an input
   convention, not a disagreement about the shader.
3. **Mip levels.** A harness that creates images with `mipLevels = 1` and samples level 0 only does
   not reproduce icon-size drawing: level 0 sampled into a 32 px target is point-ish undersampling of
   a 4x4 texel footprint, so coverage and mean RGB scatter with subpixel phase instead of moving
   smoothly, and thin features can appear or vanish. My 128→32 rows are the mip-filtered case
   (coverage 21.6 %→21.1 %); a level-0-only run will be noisier and a few points off in either
   direction.
4. **Metric.** A drawn-pixel count cannot separate 变色 from 看不到. `main_mask_tint` has *exactly* the
   passthrough drawn count (21.61 %, 0 flips, IoU 1.000) while its mean RGB drops 0.084…0.092.
   Conversely `mask_as_main__r` has "100 % drawn" and is the most broken case in the set. Mean RGB over
   survivors plus a slot-level luma/ink is what discriminates; coverage alone inverts the answer.
5. **Fixture set.** `vk/assets` holds furniture (`royalbed_south`, `nutrient_north`), not weapons.
   Passthrough coverage across my six weapons is 13.7…34.7 %, so a fixed `drawn > 3000/16384` (18.3 %)
   threshold is fixture-dependent — it fails on ChainShotgunB (13.7 %) with everything correct, and it
   passes trivially for furniture. Weapon masks are also a different distribution from furniture
   masks: 1.4…19.2 % of the face here.
6. **`_MainTex_ST`.** Held at the identity `(0,0,1,1)` here. A Unity-order `(1,1,0,0)` pins uv to a
   single corner texel and draws nothing at all — a separate real bug (the `TexStMath` inverse
   transform), not this one. Mixing the two in one table makes the mask question unreadable.
7. **`_Color` / `vColor`.** With white `_Color` the mask is provably inert (claim 2), so a harness that
   never tints sees "mask irrelevant" while one that tints sees "colour shift". Both are right; the
   tint is the only channel through which these masks act on RGB. If the defs never set a colour, the
   real-world `main_mask` case is indistinguishable from passthrough — itself a result.
8. **Numerics.** GPU bilinear is 8-bit fixed point with quantised LOD; this is float64 with exact
   `log2`. Expect ±1 LSB on edge texels and a handful of pixels crossing the alpha test, not a
   systematic offset. The BC7 decode also comes from Pillow here, so another Pillow version could
   shift a few texels.

## CI

`.github/workflows/iconpipe-cpu.yml` (`iconpipe-cpu`) runs `validate.py` then `run.py` on
`ubuntu-latest` with Pillow as the only dependency, prints both tables, publishes them to the job
summary, and uploads `pipe_cpu/out/`. The job fails if any of the 28 claims regresses.
