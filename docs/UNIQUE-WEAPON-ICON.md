# Unique weapon icons — seed facts (read before exploring)

Everything below is verified; do not re-derive it. Paths are on the device (`com.ports1de.waylith`)
and in the repos `SleepSheepQwQ/rimworld-waylith-icon-fix` (mod) and `SleepSheepQwQ/rimworld-waylith-re`
(CI harness, decompiles, fixtures).

## The def

`Data/Odyssey/Defs/ThingDefs_Items/Weapons_Unique.xml`:

```xml
<defName>Gun_Revolver_Unique</defName>
<label>unique revolver</label>
<graphicData>
  <texPath>Things/Item/Equipment/WeaponRanged/UniqueWeapons/Revolver</texPath>
  <graphicClass>Graphic_Random</graphicClass>
  <shaderType>CutoutComplex</shaderType>
</graphicData>
```

There are 12 `*_Unique` weapons in Odyssey (`Gun_AssaultRifle_Unique`, `Gun_ChargeLance_Unique`,
`Gun_HeavySMG_Unique`, `Bow_Great_Unique`, …). They all use `Graphic_Random`, not `Graphic_RandomRotated`.

## The assets

Textures are **not** loose files; they live inside `Data/Odyssey/Assets.zip`
(pack channel, not `Textures/` on disk). 128 entries under
`Textures/Things/Item/Equipment/WeaponRanged/UniqueWeapons/`, with two shapes:

- `UniqueRevolverC.png.dds` — 22,020 B (full colour variant, e.g. `...C`)
- `UniqueRevolverC_m.png.dds` — 11,084 B (mask variant, `_m`, half the bytes)

i.e. the **double extension** `.png.dds` that already broke content-key lookups elsewhere, and the
`_m` masks that must not appear as drawable variants.

## What the port does with it (established)

- `ContentPack` key = relative path + `.dds`; lookups want `Textures/...` — the pack keys carry that
  prefix, so `ContentFinder` must ask with the same shape.
- `ModDdsLoader` branches on the **name suffix**; a DX10 header used to be treated as BC7 whatever its
  DXGI format, so BC5 masks decoded as garbage or threw. GlyphFix 2.7.0+ fixes that (`DdsFormatFix`).
- `BuildableDef.ResolveIcon()` sets `uiIcon = material.mainTexture` and `uiIconMaterial` from the
  `ShaderDatabase.TryGetUIShader` request when the material has a UI shader.
- `Widgets.ThingIcon(Rect, ThingDef, …)` returns **before** calling `GetIconFor` when
  `uiIcon == null || uiIcon == BaseContent.BadTex` — a fully blank tile.
- `CutoutComplexUI` materials on this port render 0 of 16384 pixels when `_MainTex_ST` is not identity;
  GlyphFix drops that material at `Widgets.GetIconFor` time (2.7.1+) and keeps the tint (2.7.7).
- `Graphic_Collection` in the leak layout put `_m` masks into `subGraphics` (10 variants / 5 masks);
  GlyphFix's `CollectionVariants` removes them (5/0).
- Harmony 3.0 runs postfixes in **descending priority**; a postfix below 400 runs after GlyphFix and
  can silently undo it. No installed mod does this today.

## Known exclusions for the general icon work

Mod loading order, duplicate package ids, and a throwing mod cannot stop GlyphFix initialising —
see `docs/LOADING-FACTS.md` §1. `DevInYourLanguage` does not reference any icon/texture/material API.

## Open question

Why does the unique-weapon icon in the equipment panel appear as a blank/grey tile while ordinary
weapons are fine? Candidate 1 is settled by the measured section above (the def icon is
`subGraphics[0]`, and the culture-sensitive order makes that the mask); 2-4 remain open:

1. `Graphic_Random` picks one of N subgraphics per **instance**; some picks are masks or textures whose
   pack key does not resolve, leaving `uiIcon`/`Graphic` at BadGraphic.
2. `ResolveIcon` runs once at PostLoad; for these defs `graphic.MatAt` may return a material whose
   `mainTexture` is null on this port (Material.mainTexture comes back null here — documented behaviour
   that motivated `IconFieldFix`).
3. The `CutoutComplexUI` material path again, but for a `Graphic_Random` def rather than a
   `Graphic_Collection`.
4. Instance-vs-def mismatch: the inventory icon uses the `Thing` overload, which reads the *instance*
   graphic (`thing.Graphic`), and that instance may have been built before the pack was ready.

## Measured: the def-level icon is `subGraphics[0]`, and that is the mask

CI run **45237630449** (`uniqueicon` workflow, one boot, four defs). Code path, line by line:

- `BuildableDef.ResolveIcon` → `graphic.ExtractInnerGraphicFor(null).MatAt(defaultPlacingRot)`
  (`defgraphic/src/Assembly-CSharp.decompiled.cs:37497`), then `uiIcon = material.mainTexture`
  (`:37507`).
- `GraphicUtility.ExtractInnerGraphicFor` (`:173192`) forwards the **null** thing to
  `Graphic_Random.SubGraphicFor` (`:175385`), which returns `subGraphics[0]` for null. The
  `thing.OverrideGraphicIndex ?? thing.thingIDNumber` branch (`:175386`) is the *instance* path
  (`Widgets.GetIconFor(Thing)`, `:216497`) and never runs for the def icon.
- `Graphic_Collection.Init` keeps `_m` out with `!x.name.EndsWith("_m")` (`:173720`) and orders with
  LINQ `orderby x.name` (`:173721`) — **culture-sensitive**, not ordinal. `UniqueRevolverA_m.png`
  sorts *before* `UniqueRevolverA.png` (the `_`/`.` difference is punctuation weight, the letters
  `m` vs `p` decide), so with the shipped `.png.dds` names the mask is `subGraphics[0]`.

Consequence: for a leaked def the def-level icon is a mask for **every** draw (bills, gizmos, thing
filters), not only for the instances whose id lands on an odd index. Sorted names measured in CI:

```
[UNIQ] def=UniqueRevolverFixtureLeak names=UniqueRevolverA_m.png|UniqueRevolverA.png|UniqueRevolverB_m.png|...
```

| def | layout | subCount | masks | `uiIcon` | pixels | verdict |
|---|---|---|---|---|---|---|
| `Gun_Revolver_Unique` | harness loose PNGs (no pack) | 5 | 0 | `UniqueRevolverA` 128×128 | 2112/16384 = 12.89 %, sd 44.8, 141 colours | DRAW |
| `UniqueRevolverFixturePlain` | `X.dds` + `X_m.dds` | 5 | 0 | `UniqueRevolverA` | 100 %, sd 46.9, 4 colours | DRAW |
| `UniqueRevolverFixtureLeak` | `X.png.dds` + `X_m.png.dds` | 10 | 5 | `UniqueRevolverA_m.png` (mask) | 100 %, sd 5.6, **2 colours** | MASK-ICON |
| `UniqueRevolverFixtureDx10` | same, masks behind a DX10 header | 10 | 5 | `UniqueRevolverA_m.png` | `GetPixels32` throws `IndexOutOfRangeException` | BLANK |

`uiIconMaterial` is `CutoutComplexUI_<texture>` with `GetPropertyCount() == 5` on **all four**,
including the healthy control, so "material present" is not a discriminator and must not be used as
one.

### Decidable pass/fail

- **Cannot draw (blank)**: `uiIcon == null || uiIcon == BaseContent.BadTex || width <= 2` — then
  `Widgets.ThingIcon(Rect, ThingDef)` returns before `GetIconFor` (`:216293`); or the texture's
  `GetPixels32()` throws (a BC1-sized mask behind a DX10 header is created as BC7, so no pixels ever
  reach the GPU); or its non-transparent texel fraction is 0.
- **Can draw**: `uiIcon` is a real ≥8×8 texture, `GetPixels32()` succeeds, opaque fraction > 0
  (12.89 % for the real art here), and `uiIcon` is not a `_m` mask.
- In between — readable texture, but `uiIcon` is a mask — is the reported grey tile: 100 % coverage
  with 2 distinct colours and luminance sd ≈ 5.6, against ≈ 45 and >100 colours for art.

Fixture/probe: `uniqueicon/` (`make_fixture.py`, `StartupHook.cs`, `gate.py`) and
`.github/workflows/uniqueicon.yml`. The fixture's DX10 mask is 11,084 B — exactly the shipped
`UniqueRevolver*_m.png.dds` size (148-byte DX10 header + a 10,936-byte BC1 8-mip chain) — which is
why that def reproduces a *blank* icon while the DXT1-mask leak reproduces a *grey* one.

Harness limitation worth knowing: `ThingMaker.MakeThing` NREs at this point of the boot
(`Verse.ThingIDMaker.GiveIDTo`), because no `Current.Game` exists yet. The instance selection was
therefore read through `Graphic_Random.SubGraphicAtIndex(id)` — the same `subGraphics[index % n]`
expression the Thing branch uses — and `src=graphic` marks those lines.

## Rules for every probe

- Do not touch the device. Nothing may read device logs. CI only.
- Commands ≤ 15 s; use short polls, never long sleeps.
- Quote the doc (`docs/UNIQUE-WEAPON-ICON.md §…`) instead of re-proving these facts.
- Every hypothesis must come with a file:line, an observable criterion, and a test that could falsify it.
