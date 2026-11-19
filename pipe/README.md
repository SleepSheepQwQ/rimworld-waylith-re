# pipe — offline icon-pipeline assertions

Turns the "why is the icon blank" question into pixels on CI. No game install, no phone, no GPU:
real DDS input, the real shaders, software Vulkan (lavapipe), and a pass/fail table.

```
assets/iconpipe/*.dds       12 real textures (6 BC7 albedo + 6 BC4 mask), see PROVENANCE.md
pipe/decode_dds.py          DDS header parse + BC7 (Pillow) / BC4 (own block decoder) -> RGBA
pipe/shaders/               CutoutComplex.frag.glsl (world) + CutoutComplexUI.frag.glsl (UI)
pipe/render/harness.c       offscreen Vulkan renderer, prints a STATS JSON line per case
pipe/run_pipeline.py        build -> decode -> render 6 cases -> assert -> markdown + JSON
.github/workflows/iconpipe.yml
```

## Run it

```sh
sudo apt-get install -y libvulkan-dev mesa-vulkan-drivers glslang-tools build-essential
python3 -m pip install --break-system-packages pillow==12.3.0
export VK_ICD_FILENAMES=$(find /usr/share/vulkan/icd.d -name 'lvp_icd*.json' | head -1)
python3 pipe/run_pipeline.py
```

Locally (Termux, aarch64 lavapipe) the same command works with the Termux ICD path. Exit code 0
means every assertion passed; 1 means at least one failed and each failure is printed with
expected vs actual.

`--inject-fault coverage|color|luma` perturbs one measured value before the assertions so the
failure path can be demonstrated on demand (`workflow_dispatch` exposes the same knob).

## Cases

All six use a full-screen quad, uv 0..1, `_MainTex_ST = (0,0,1,1)`, `_Color = white`,
`_ColorTwo = white`, and the vertex colour from the device log (0.522, 0.380, 0.263).

| case | shader | `_MainTex` | `_MaskTex` |
|---|---|---|---|
| `world_albedo_mask` | CutoutComplex | `od_UniqueRevolverA` | `od_UniqueRevolverA_m` |
| `world_albedo_nullmask` | CutoutComplex | `od_UniqueRevolverA` | unbound → engine's 1×1 black |
| `world_mask_as_main` | CutoutComplex | `od_UniqueRevolverA_m` | unbound |
| `ui_*` | CutoutComplexUI | same three | same three |

`covered` counts pixels with alpha > 0 (i.e. written by the fragment stage), `a50` counts
alpha ≥ 128 (`if (f.a < 0.5) discard;` would keep exactly those). Mean RGB / luma are over the
covered pixels.

## What the numbers say

Reference run (128×128, lavapipe):

| case | coverage | mean RGB | mean luma |
|---|---|---|---|
| world_albedo_mask | 2001 / 16384 = 12.213 % | (85.3, 78.9, 73.8) | 79.92 |
| world_albedo_nullmask | 2001 / 16384 = 12.213 % | (106.5, 106.5, 106.5) | 106.47 |
| world_mask_as_main | 16384 / 16384 = 100 % | (6.4, 0.0, 0.0) | 1.37 |
| ui_albedo_mask | 2120 / 16384 = 12.940 % | (80.5, 74.5, 69.7) | 75.44 |
| ui_mask_as_main | 16384 / 16384 = 100 % | (6.4, 0.0, 0.0) | 1.37 |

* The mask **tints, it does not gate**: real mask vs null mask gives the *same* 2001 pixels and a
  mean-RGB distance of 47.7. `MaskGate` multiplies `colPower.rg`; the multipliers preserve alpha.
* A BC4 mask bound as `_MainTex` samples as `(R, 0, 0, 1)`: alpha 1 everywhere → nothing is
  discarded → an opaque near-black square (luma 1.37/255) over the entire icon rect. This is the
  "blank icon" symptom, reproduced without a device.
* The UI shader has no `discard`, so it paints the 119 extra texels with 0 < alpha < 0.5
  (2120 vs 2001) while agreeing on the a ≥ 128 set. That is the measurable difference between the
  two shader files — a swapped-in world shader can only *remove* pixels, never add them.

## Assertions

1. `coverage.real_mask` — albedo + real mask covers 11.5–13.5 % (≈12 %).
2. `coverage.null_mask_same` — null mask covers exactly the same pixel count; `color.null_mask_differs`
   — mean-RGB distance > 15.
3. `coverage.mask_as_main` — ≥ 99.5 %; `luma.mask_as_main` — mean luma ≤ 8/255;
   `swizzle.bc4` — green and blue exactly 0 (BC4 expands as `(R,0,0,1)`).
4. `shader.ui_no_discard` — UI covers strictly more pixels, same a50 set;
   `alpha.world_discard` — world alpha never below 128.
5. `decode.count` (12 = 6 BC7 + 6 BC4), `decode.bc4` (own BC4 decoder == Pillow, 0 texels differ),
   `decode.checksums` (decoded RGBA matches the pinned sha256 set), `device.software`
   (renderer is a CPU device).

`assets/iconpipe/checksums.json` pins the RGBA bytes of all 12 decodes. Regenerate it only when
the corpus changes on purpose: `python3 pipe/decode_dds.py --update-checksums`.

## Fidelity notes

* Shaders are copies of the device files. `MaskGate.glsl` is inlined (glslangValidator needs
  `GL_GOOGLE_include_directive` for `#include`, which the shipped file does not declare); the
  fragment bodies are unchanged, including the UI shader's missing `discard`.
* Textures are uploaded as RGBA8 after CPU decode and sampled with `LINEAR` + `CLAMP_TO_EDGE`,
  exactly like the engine's sampler for these icons. Pixel centres land on texel centres, so
  bilinear sampling is exact and the result is deterministic.
* The harness draws one full-tile quad; it does not model the engine's UV transform beyond
  `_MainTex_ST`, which is the parameter the shader actually consumes.
