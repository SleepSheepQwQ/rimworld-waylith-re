# mat — Verse.MaterialPool's cache key, executed

`MaterialPool.MatFrom(request)` is the only way the game turns a `MaterialRequest` into a
`Material`, and `matDictionary` is a process-wide dictionary that is never emptied. So the
question "how many materials does per-instance tinting cost, and can it serve the wrong one?"
is a question about `MaterialRequest.Equals` / `GetHashCode`, not about rendering.

`mat/` answers it by *running the shipped code*: `dlls/Assembly-CSharp.dll` is loaded and its
own `MatFrom`, `MaterialRequest.Equals` and `MaterialRequest.GetHashCode` execute. There is no
game, no Unity, no GPU, no engine and no Harmony. CI: `.github/workflows/matpool.yml`.

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
| `Verse.Log`, `Prefs`, `WindManager` | never reached: every scenario keeps `mainTex`/`shader` non-null and avoids the plant-shader branch |

The shipped `UnityStubs.dll` cannot be used: it binds to the Waylith engine (`Brutal.Vulkan`,
`Brutal.Core.Logging`, `ShaderRegistry` wanting `SceneDefault.frag.glsl`), so not even
`new Texture2D(4,4)` succeeds outside the game. `mat/Stub/` declares only the surface the
material path touches, with the three semantics that actually decide cache behaviour:

* `Object`/`Texture` `==` is reference identity, and `name` is a property (the IL calls
  `get_name`/`set_name`/`op_Equality` by name).
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

Twelve `[FACT]` lines are read from `dlls/Assembly-CSharp.dll` with `System.Reflection.Metadata`
and IL decoding (`Assembly.cs`, `MiniIl.cs`), each a property the replica-free run depends on:
the two dictionary field types, the
`TryGetValue → MaterialAllocator.Create → Add → Add(reverse)` order, the mask nulling and the
`Color32` quantisation happening *before* the lookup, the eight fields `Equals` compares in order,
`shaderParameters` being compared by a bare `ceq`, `GetHashCode` hashing exactly the same eight
fields, and `needsMainTex` appearing in neither. Any drift prints `[BROKEN]` and exits 2.

## The four request shapes

| case | shared | differs | result |
|---|---|---|---|
| A | texture, colour | nothing (two separate `MaterialRequest` values) | hit, same instance, pool unchanged |
| B | texture | `color` / `colorTwo` | miss, new instance, +1 |
| C | texture, colour | `maskTex` | miss per mask, +1 each |
| D | texture, colour, **parameter contents** | only the `List<ShaderParameter>` instance | **miss**, +1 — the bloat point |
| E | texture | colour by less than 1/255 | hit (quantised to the same `Color32`) |

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

Nothing is ever evicted, so a *correct* material is never pushed out by a wrong one. The one
way the pool returns something other than what was asked for runs the other way: because the key
is 8-bit per channel, a tint differing by less than 1/255 gets the first material's colour. That
is invisible — the failure mode here is memory, not mis-rendering.
