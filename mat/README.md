# mat — Verse.MaterialPool's cache key, executed

`MaterialPool.MatFrom(request)` is the only way the game turns a `MaterialRequest` into a
`Material`, and `matDictionary` is a process-wide dictionary that is never emptied. So the
question "how many materials does per-instance tinting cost, and can it serve the wrong one?"
is a question about `MaterialRequest.Equals` / `GetHashCode`, not about rendering.

`mat/` answers it by *running the shipped code*: `dlls/Assembly-CSharp.dll` is loaded and its
own `MatFrom`, `MaterialRequest.Equals` and `MaterialRequest.GetHashCode` execute. There is no
game process, no Unity world, no GPU, no Vulkan and no Harmony. CI: `.github/workflows/matpool.yml`.

```bash
dotnet build mat/Mat.csproj -c Release
dotnet run --project mat/Mat.csproj -c Release --no-build -- --dlls dlls --table mat/out/table.md
echo $?    # 0 pass / 1 assertion failed / 2 harness cannot vouch for what it ran
```

Options: `--dlls <dir>` (default `dlls`), `--table <path>` (also writes the markdown table),
`--force-fail` (adds one deliberately false assertion, for a control run that must go red).

## What is real, and what had to be replaced

| piece | source |
|---|---|
| `MaterialPool.MatFrom`, `MaterialRequest.Equals/GetHashCode`, `MaterialAllocator.Create`, `ShaderUtility.SupportsMaskTex`, `ShaderDatabase`, `Gen.HashCombine`, the two dictionaries | **the shipped dll**, executed |
| `UnityEngine` (`Object`, `Texture`, `Texture2D`, `Material`, `Shader`, `Color`, `Color32`, `Vector4`) | `mat/Stub/` — hand-written, semantics documented inline |
| `Verse.Log` | reached only by G (MatFrom's null-source error path); its cctor runs against the `Brutal.Core.Logging` types, logging itself is disabled |
| `Prefs`, `WindManager` | never reached: every scenario keeps `shader` non-null and avoids the plant-shader branch |

The shipped `UnityStubs.dll` cannot be used: it binds to the Waylith engine (`Brutal.Vulkan`,
`Brutal.Core.Logging`, `ShaderRegistry` wanting `SceneDefault.frag.glsl`), so not even
`new Texture2D(4,4)` succeeds outside the game. `mat/Stub/` declares only the surface the
material path touches, with the semantics that actually decide cache behaviour:

* `Object`/`Texture` `==` is reference identity for live objects. A destroyed object is
  fake-null (`Object.op_Equality` reads `_destroyed`; `Texture.op_Equality` reads `_disposed`,
  the shipped UnityStubs shape). `name` is a property (the IL calls `get_name`/`set_name`).
* `Color` compares by value, and `Color32` is the 8-bit quantiser that `MatFrom` routes
  `color`/`colorTwo` through *before* it builds the key.
* `Material` records every `Set*` call in `Applied`, so two materials can be compared property
  by property instead of "different instances, therefore different".

`Stub/Names.cs` holds skeleton types (`AudioClip`, `Mesh`, `Rect`, …). They exist only because a
reached `Verse` method mentions those names in a branch the harness never takes and the CLR
resolves them before it will JIT the method. Nothing there is called; if one ever is, the run
fails with `MissingMethodException` rather than inventing a value.

The shader is `ShaderDatabase.CutoutComplex` — the real `Verse` shader object — and the stub
declares `_MaskTex` on every shader, which is what a real CutoutComplex does. The harness asserts
`ShaderUtility.SupportsMaskTex(shader)` is true before case C: `MatFrom` nulls `maskTex` on a
shader without it, so a stub that quietly lost mask support would make C collapse into A and the
"mask is part of the key" claim would be a lie. It goes BROKEN instead.

## The dll's own account of the key, asserted (not assumed)

Nineteen `[FACT]` lines are read from `dlls/Assembly-CSharp.dll`, `UnityStubs.dll` and
`Waylith.dll` with `System.Reflection.Metadata` and IL decoding (`Assembly.cs`, `MiniIl.cs`).
The first twelve cover the pool key:
the two dictionary field types, the
`TryGetValue → MaterialAllocator.Create → Add → Add(reverse)` order, the mask nulling and the
`Color32` quantisation happening *before* the lookup, the eight fields `Equals` compares in order,
`shaderParameters` being compared by a bare `ceq`, `GetHashCode` hashing exactly the same eight
fields, and `needsMainTex` appearing in neither. The other seven pin the cases F/G model:
`Object.Destroy` disposes then sets `_destroyed`; `Texture.Dispose(bool)` disposes the core
texture and sets `_disposed`; `Texture.op_Equality` reads `_disposed`, never `_destroyed`;
`Object.op_Equality` reads `_destroyed`; `Waylith.Rendering.Texture.Dispose` releases
`GpuResource` and leaves `CpuData`; and `SetTextureScale` / `SetVector("_MainTex_ST")` both
reach the engine's one `textureTransforms` slot. Any drift prints `[BROKEN]` and exits 2.

## The request shapes A–G

| case | shared | differs | result |
|---|---|---|---|
| A | texture, colour | nothing (two separate `MaterialRequest` values) | hit, same instance, pool unchanged |
| B | texture | `color` / `colorTwo` | miss, new instance, +1 |
| C | texture, colour | `maskTex` | miss per mask, +1 each |
| D | texture, colour, **parameter contents** | only the `List<ShaderParameter>` instance | **miss**, +1 — the bloat point |
| E | texture | colour by less than 1/255 | hit (quantised to the same `Color32`) |
| F | texture, colour (all 8 fields) | `_MainTex_ST` | hit — one material, last writer wins |
| G | texture (destroyed) | same request | normal request -> BadMat; `needsMainTex=false` -> stale material |

D is asserted from both sides: the same `List` instance hits, an equal-contents `List` misses.
The contents are compared field by field by the harness first, so "equal contents" is not a claim,
it is checked before the differing verdict is asserted.

## Census: 5 variants × 3 colours × 2 masks

All measured, not computed:

```
30 requests (one per texture×colour×mask, shared textures/masks)  -> 30 materials
the same 30 asked again                                           -> 30 materials (0 new)
60 requests, each with its own equal-contents List                -> 60 materials
floor if the tint were a draw-time property                       -> 10 materials
```

So the answer to "how many instances" is **30**, and **20 of them (66.7%) are redundant** against
the 10 a draw-time tint needs: the colour dimension multiplies the pool by 3 for pixels that could
have been a per-draw property. Add a per-request `ShaderParameter` list and every one of those 30
gets a twin with identical contents — 30 more materials, 100% redundant by content. Nothing caps
this: `MaterialPool` exposes `Add` and no `Remove`/`Clear`, and `MaterialAllocator.references`
grows with it (it prints its own "may be a sign of a material leak" warning past a threshold).

Nothing is ever evicted, so a *correct* material is never pushed out by a wrong one. The
mis-rendering paths are F and G, and neither is a tint rounding artefact:

* **F — `_MainTex_ST` is not in the key.** Two requests with the same eight fields get one
  `Material`; `mainTextureScale/Offset` (and `SetVector("_MainTex_ST")`) write the one shared
  slot, so the last writer wins. Measured: A sets scale (2,3)/offset (0.25,0.5), B asks for the
  same material and writes identity, A reads (1,1)/(0,0); B then sets scale (4,5), A reads
  (0,0,4,5). Pool size stays 1. A clone has its own ST and leaves the pooled material at (4,5).
* **G — a destroyed mainTex.** Destroy releases `GpuResource`, keeps `CpuData` and leaves the
  managed `Texture2D` reference in place. A normal request then hits `req.mainTex == null`
  (Texture.op_Equality sees `_disposed`) and returns `BaseContent.BadMat`; the old material is
  **not** served and stays in the pool with a fake-null mainTexture. A `needsMainTex=false`
  request carrying the same destroyed reference skips that guard and does return the stale
  material. A destroyed `Material` is not guarded at all: `MatFrom` returns the dead instance.
  Each stale entry pins the request, the `Material`, the `Texture2D` wrapper and its
  `CoreTexture`, so the retained `CpuData` cannot be collected either. At 128x128 RGBA that is
  64 KiB (about 85 KiB with a mip chain) per destroyed icon texture, times every texture a mod
  reload or atlas rebuild leaves behind; the pool has no eviction path.

## Call-site scan (shipped Assembly-CSharp, IL)

`STScan` reads all 91,616 methods and reports every write of `mainTextureScale/Offset`,
`SetTextureScale/Offset`, or `SetVector("<name>_ST")`, plus the methods calling `MatFrom`
(112). There are **10 write sites in 6 methods**, and **no** `SetVector` with a literal
`_MainTex_ST` name:

| method | writes | material origin | shared with the pool? |
|---|---|---|---|
| `Verse.MaterialAtlasPool/MaterialAtlas::.ctor` | `mainTextureScale`, `mainTextureOffset` | `MaterialAllocator.Create(newRootMat)` ×16 | **no — clones** |
| `Verse.MaterialDef::ApplyTextures` | `SetTextureScale`, `SetTextureOffset` | `MaterialDef.Material` (fresh per def) | no |
| `Verse.TexturePanner::Tick` | `SetTextureOffset` | caller's material (weather overlay) | no (MatLoader copy) |
| `Verse.TexturePannerSpeedCurve::Tick` | `SetTextureOffset` | caller's material | no |
| `Verse.WeatherOverlayDualPanner::TickOverlay` | `SetTextureOffset` ×2 | static overlay material | no (one per overlay) |
| `Verse.WeatherOverlayDualPanner::Reset` | `SetTextureOffset` ×2 | static overlay material | no |

So the shipped ST writers are safe by construction: the atlas bake clones first, `MaterialDef`
owns a per-def material, and the panners animate dedicated overlay materials. The ST hole is a
**mod-side trap**, not a current game bug. It becomes visible when a mod writes ST on a material
obtained from `MaterialPool.MatFrom`, or on `BuildableDef.uiIconMaterial` — both are process-wide
shared. `uiIconMaterial` is read by 36 `Widgets.ThingIcon` call sites (architect/build designator,
info card, gear/bills/trade/caravan tabs, styling/drug-policy/map-search/ritual dialogs, resource
readout, transferables, float menus, …) and by `BuildCopyCommandUtility`'s command override, so
one write changes every screen that shows that def at once. The `TexStMath` helper in `mod/` is
exactly such a write and is currently dead code (only tests reference it).
