# iconexec — the unique-weapon icon chain, executed

`flow/` re-implements `Graphic_Collection.Init` from the decompiled source. `flowb/` runs the real
`Init` and `SubGraphicFor` against a *synthetic* folder. `iconexec/` goes the rest of the way: it
takes two real `ThingDef`s out of the game data, builds the `GraphicRequest` the game would build,
and calls the game's own compiled methods — `Graphic_Collection.Init`, `Graphic_Random.SubGraphicFor`,
`Graphic_Random.MatSingle` and `Verse.Widgets.GetIconFor(Thing, …)` — then asserts what came back.

No game process, no GPU, no Unity: `dlls/Assembly-CSharp.dll` (Waylith net10.0 build
`1.7.9795.32073`) is loaded into a console app and driven.

## What is real

| real, unmodified | where |
|---|---|
| the two defs and their `graphicClass` / `texPath` / `shaderType` | `data/Weapons_Unique.xml`, verbatim from `Data/Odyssey/Defs/ThingDefs_Items/Weapons_Unique.xml` |
| the texture set of each def: names, 128x128 size, 8 mips, BC7 albedo / BC4 mask | `data/unique_weapons_textures.json`, header-scanned from `Data/Odyssey/Assets.zip` |
| `Verse.Graphic_Collection.Init(GraphicRequest)` | `dlls/Assembly-CSharp.dll` |
| `Verse.Graphic_Random.SubGraphicFor(Thing)` | idem |
| `Verse.Graphic_Random.get_MatSingle` (`subGraphics[Rand.Range(0, n)]`) | idem |
| `Verse.Graphic_Single.Init` (texture lookup, mask probe, `MaterialPool.MatFrom`) | idem |
| `Verse.Widgets.GetIconFor(Thing, Vector2, Nullable<Rot4>, bool, out …)` | idem |
| `Verse.ContentFinder<T>`, `ModContentHolder<T>`, `GraphicDatabase`, `MaterialPool`, `ShaderTypeDef.Shader` | idem |

## What is stubbed, and why

`UnityEngine` is the only stand-in (`Stub/`), plus 30 lines of the engine logger
(`Stub/Logging/`, assembly `Brutal.Core.Logging`).

`UnityProbe` answers "why not the shipped `dlls/UnityStubs.dll`?" by trying it, in a separate
process, and printing what happens (`ICONEXEC_PROBE=1`, or run the probe directly):

| call | shipped UnityStubs | consequence |
|---|---|---|
| `new Texture2D(128,128)`, `SetPixel`/`GetPixel` | **works** — CPU-side buffer, real pixels | textures could be real |
| `Shader.Find("CutoutComplex")` | `TypeInitializationException` → `FileNotFoundException: Unable to locate shader source 'SceneDefault.frag.glsl'` | no shader |
| `new Material(shader)` (even `Shader.CreateLateBound`) | same: `Material..ctor` reads `Shader.WaitingForEngineRegistry` → `ShaderRegistry..cctor` | no material |

`Graphic_Single.Init` ends in `MaterialPool.MatFrom` → `new Material(shader)`, so the icon chain
cannot run on the shipped stub. The registry wants the GLSL sources registered **before** its
static ctor runs, and the ctor is what fails first — which is why `Stub/` exists instead of a
half-real mix. `vk/CutoutComplex.frag.glsl` in this repo shows the sources exist; registering them
before the first `Shader` access is the open path to dropping the Unity stub entirely (see
"where this could go" below).

## What the harness seeds

The one place it substitutes on the Verse side is the content plumbing a bare process does not have:

```
LoadedModManager.runningMods     = [ one fake ModContentPack ]
ModContentPack.textures          = ModContentHolder<Texture2D>
ModContentHolder.contentList     = { "<texPath>/<name>" -> Texture2D(name, 128, 128) }
ModContentHolder.anyReloaded     = true      <-- required, see findings
UnityData.mainThreadId           = current thread
UnityDataInitializer.initializing= true
Rand.seed                        = 1234567   (UInt32)
```

Asset paths for the engine's own chrome are **read out of the IL**: every type's `.cctor` that calls
`ContentFinder<Texture2D>.Get` / `MaterialPool.MatFrom` contributes its literals to the seed list
(1580 keys). That is why `Widgets`' static ctor can run at all — it loads ~20 UI textures and dies
on the first miss, because a miss is reported through `Verse.Log`.

## Findings (all produced by executing the shipped methods)

1. **`Init` merges the mask textures into the variant list when names keep the bundle extension.**
   With the asset names as the bundle stores them (`UniqueRevolverA.png`), `subGraphics` is **10**:
   five albedo plus five `_m` masks, alternating. With the extension stripped — what Unity does to
   `Texture2D.name` — it is **5**, and no mask is ever selected. So the defect `flow/` predicted is
   reproduced here by the real method, and it is a naming-convention difference, nothing deeper.

2. **`thingIDNumber` picks uniformly, masks included.** Over 0..999 each of the ten variants gets
   exactly 100 ids; **500/1000 ids resolve to a mask texture**.

3. **The UI icon is not the thing's variant.** `Widgets.GetIconFor(thing, …)` calls
   `MatAt(rot, null)`, and `Graphic_Random.MatAt` with a null thing returns `MatSingle`, which is
   `subGraphics[Rand.Range(0, subGraphics.Length)].MatSingle` — a **fresh random variant per call**,
   unrelated to `thingIDNumber`. Over 2000 calls the icon was a mask **1000** times, and every
   returned texture was one of the ten, at the real 128x128.

4. **`ContentFinder<T>.Get` returns nothing unless `ModContentHolder<T>.anyReloaded` is true.**
   `Get` branches past the whole mod-lookup loop when the flag is false, so a host that populates
   `contentList` but not the flag sees `null` for every path while `GetAllInFolder` happily lists
   them. The game sets it on mod reload; a fresh process must set it by hand.

5. **`Verse.Log` cannot start without the engine.** Its ctor is
   `Brutal.Logging.LogCategory.Make("Verse")` + `Application.add_logMessageReceivedThreaded(...)`,
   and `LogCategory` lives in an assembly named `Brutal.Core.Logging` (version `2026.9.0.0`).
   Without it, every "asset not found" becomes a `TypeInitializationException` that hides the real
   reason — which is exactly how the first four iterations of this harness failed. The stub logger
   prints those messages, so the game's own diagnostics reach the CI log.

## The assertions

Per def (`Gun_Revolver_Unique`, `Gun_HeavySMG_Unique`) × naming (`bundle`, `stripped`):

* `Init` completes; `subGraphics` equals the texture count the def's folder really has
* every variant path sits under the def's `texPath`
* every variant is a `Graphic_Single` whose material carries the texture its path names, at 128x128
* `thingIDNumber` 0..999 hits every variant, evenly
* `GetIconFor` returns a texture on every call, always 128x128, always one of the collection's
* `SubGraphicsCount`, `SubGraphicAtIndex(i)` and `FirstSubgraphic()` agree with the array
* **defects are asserted as present** (`[DEFECT]`): 5/10 mask variants and 500/1000 mask ids in
  `bundle` naming, 1000/2000 mask icons. If the port ever stops doing this the run goes red, because
  that is a behaviour change, not something the harness should absorb silently.

Exit codes: `0` pass, `1` assertion failed, `2` broken (the dll no longer has the shape the facts
pin, or a fixture is missing). `--force-fail` adds one deliberately false assertion so a red run can
be checked to actually be red.

## Run it

```
dotnet build iconexec/IconExec.csproj -c Release
dotnet iconexec/bin/Release/net10.0/IconExec.dll --dlls dlls --data iconexec/data --table iconexec/out/table.md
```

## Where this could go

* `GraphicData.Graphic` (the `Thing.DefaultGraphic` path) is not exercised — it needs
  `UnityStubs.ImportDefaults`, which the stub does not carry. The request built here is the same
  request that path builds; `Facts` pins the fields it copies.
* Dropping the Unity stub: register the GLSL source directory before the first `Shader` access so
  `ShaderRegistry..cctor` succeeds, then `Texture2D`, `Shader` and `Material` are all real. The
  remaining question is whether `Material..ctor` defers or needs a device; `UnityProbe` is the place
  to answer it.
