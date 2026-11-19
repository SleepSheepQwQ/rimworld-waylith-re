# flowb — Graphic_Collection verified by *running* it

`flow/` answers "what does `Verse.Graphic_Collection.Init` do?" by re-implementing the
decompiled source. `flowb/` answers the same question the other way round: it **loads
`dlls/Assembly-CSharp.dll` and executes the real, compiled `Init` and
`Graphic_Random.SubGraphicFor`**. If the two disagree, one of them is wrong; here they
agree on the numbers that matter.

## What is real, what is faked

Real, unmodified, straight out of `dlls/Assembly-CSharp.dll` (Waylith port, net10.0,
`1.7.9795.32073`):

* `Verse.Graphic_Collection.Init(GraphicRequest)` — the `Where` / `OrderBy` / `Select` /
  `GroupBy` pipeline, `Graphic_Single.MaskSuffix` filtering, the `_east/_north/_west/_south`
  culling loop, the `maskPath` probe, the `subGraphics` array assignment.
* `Verse.Graphic_Random.SubGraphicFor(Thing)` — the
  `thing.OverrideGraphicIndex ?? thingIDNumber % subGraphics.Length` selection.
* `ContentFinder<Texture2D>.GetAllInFolder` / `.Get`, `ModContentPack.GetContentHolder<T>`,
  `ModContentHolder<T>.GetAllUnderPath`, `GraphicDatabase.GetInner<T>`'s cache lookup.

Not real, and deliberately so — a headless process has no Vulkan device:

| stubbed | why | how |
|---|---|---|
| `LoadedModManager.runningMods` | no mods are loaded in a bare process | replaced with one fake `ModContentPack` |
| `ModContentPack.textures` | no assets on disk | a real `ModContentHolder<Texture2D>` whose `contentList` + `contentListTrie` are filled with `Texture2D` objects whose `.name` we choose |
| `ModContentPack.assetBundles` | the tail of `GetAllInFolder` walks each mod's bundles | uninitialised handler with an **empty** `loadedAssetBundles`, so that loop runs zero times |
| `UnityData.mainThreadId` | `ContentFinder` refuses to work off the main thread | set to the current `ManagedThreadId` |
| `UnityDataInitializer.initializing` | `UnityData`'s static ctor logs a warning through `Verse.Log`, which cannot start without the engine | set to `true` so it takes the quiet branch |
| `GraphicDatabase.allGraphics` | `new Material(shader)` needs the GPU | pre-seeded with stand-in `Graphic_Single` / `Graphic_Multi` objects for every plausible path, so `GetInner<T>` hits its cache and never constructs a material |

No Harmony, and that is not a preference: `0Harmony` 3.x goes through MonoMod, whose
`PlatformTriple.CreateCurrentSystem()` throws `NotImplementedException` on
Android/bionic, so `Harmony.Patch` cannot run on this host at all. Seeding the game's own
content tables gets the same effect without patching anything.

Texture objects are made with `RuntimeHelpers.GetUninitializedObject(typeof(Texture2D))`,
never `new Texture2D(w, h)`: the ctor allocates on the GPU and aborts the process.

## Scenarios

Both naming conventions, same file stems, one folder each (paths differ so
`GraphicDatabase`'s path-keyed cache cannot leak between runs):

* **gun** — `UniqueRevolverA..E` plus `UniqueRevolverA_m..E_m` (5 variants + 5 masks).
* **animal** — `Lynx`, `Lynx_north`, `Lynx_northm` (north art + north mask).
* **clean** — the texture `.name` is `UniqueRevolverA` (Unity strips the extension).
* **ext** — the texture `.name` is `UniqueRevolverA.png` (the Waylith port keeps it).

`thingIDNumber` is swept 0..999 through the real `SubGraphicFor`, counting how often the
selected variant is a mask texture.

## Results

| input naming | subject | files | subGraphics | mask-flavoured variants | mask hits / 1000 |
|---|---|---|---|---|---|
| clean | gun | 10 | 5 | 0 | 0 |
| ext | gun | 10 | 10 | 5 | 500 |
| clean | animal | 3 | 2 | 0 | 0 |
| ext | animal | 3 | 2 | 0 | 0 |

* **clean gun** — the five `_m` files are dropped by the `EndsWith(MaskSuffix)` filter, the
  remaining five group by `name.Split('_')[0]`, and every one of the 1000 ids lands on a
  real variant (200 each).
* **ext gun** — `"...A_m.png".EndsWith("_m")` is **false**, so masks survive the filter;
  `"...A.png".Split('_')[0]` keeps the extension and puts each base file in a group of its
  own, so the array becomes 10 entries where 5 *are* mask textures. `id % 10` therefore
  picks a mask half the time: exactly 500/1000.
* **animal** — the mask does not leak under either convention, because `Lynx_northm`
  contains `_north` and is culled by the direction loop before grouping matters. But the
  `.png` convention still corrupts the list: the directional group's key is `Lynx`, so the
  `Graphic_Multi` is emitted for `/ExtLynx/Lynx`, a path **that has no texture** (the file
  is `Lynx.png`). Half of the animals then ask `GraphicDatabase` for a texture that does
  not exist. The clean convention emits `Graphic_Single` + `Graphic_Multi`, both at
  `/Lynx`, and both are real files.

So the direction-suffixed animal case is not immune to the naming bug — it is immune to
the *mask-as-variant* symptom and instead gets a missing-texture symptom.

## Run

```sh
dotnet build flowb/FlowB.csproj -c Release
dotnet flowb/bin/Release/net10.0/IconFlowB.dll "$PWD/dlls"
```

Exit code is the assertion result (`0` pass). CI: `.github/workflows/iconflow-b.yml`.
