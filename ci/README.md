# headless — the game's managed host on a plain Linux runner

`.github/workflows/headless.yml` starts the real port (`RimWorld.dll` → `RimWorldWaylith.Program.Main`)
with `-Headless` on `ubuntu-latest`, x64, .NET 10, no GPU, no window, no device. Nothing in `dlls/`
or `game/` is modified; the host is executed as shipped.

## Why the process is started from an apphost, not `dotnet RimWorld.dll`

`RimWorldWaylith.Bootstrap.ConfigureRuntime` derives every path from
`Path.GetDirectoryName(Process.GetProcessPath())`:

```
dataPath   = <process dir>/RimWorld_Data      (Unity data dir: <dataPath>/Resources/Textures is the
                                               texture root ContentFinder reads)
Data/      = <process dir>/Data               (ModLister's official content folder: Core, Odyssey, …)
Assets.zip = <process dir>/Assets.zip
```

`dotnet RimWorld.dll` reports the *dotnet installation* as the process path, so the host looks for
`/usr/lib/dotnet/RimWorld_Data` and `/usr/lib/dotnet/Assets.zip`. `ci/apphost` is a throwaway
`AssemblyName=RimWorld` project: the SDK emits a native apphost that loads `RimWorld.dll` from its
own directory, which is how the game is started on the device too.

## What the host needs that the port ships natively

| need | why it stops the boot | what CI does |
|---|---|---|
| `libsteam_api.so` | `Verse.Steam.SteamManager.InitIfNeeded()` calls `Application.Quit()` on `DllNotFoundException` — the process exits 0 mid-boot | `natives/steam_stub.c`: 941 exports, all return 0, so `SteamAPI.Init()` reports "no Steam client" and the game continues offline |
| `libwaylithaudio.so` | `MiniAudioBackend.Initialize()` runs before the headless branch and P/Invokes `wa_set_allocator`/`wa_init` | `natives/waylithaudio_stub.c`: 57 exports; `wa_init(.., nullBackend=1)` succeeds, nothing produces sound |
| `System.IO.Hashing.dll` | `AssetPack`/`AssetPackManifest` and `ResourceFiles` use XxHash; the Android bundle carries it, the shared framework does not | copied out of the .NET 10 SDK |
| `Rendering/Shaders/*.glsl` | `Waylith.Rendering.ShaderRegistry`'s static ctor resolves shader sources by file name; a missing `SceneDefault.frag.glsl` makes `BaseContent`'s static ctor throw, and *every* def that touches materials then fails to load | `game/Rendering/Shaders` (4 files + Compute/PostFX) |
| `Data/<DLC>/Defs` | the DefDatabase is empty without it | `game/Data/{Core,Royalty,Ideology,Biotech,Anomaly,Odyssey}` Defs + About + English keyed strings |
| `RimWorld_Data/Resources/Textures/...` | `Resources.Load`/`LoadAll` read `<Application.dataPath>/Resources`; without it every `Graphic_Collection` logs "No textures found" and falls back to `BaseContent.BadGraphic` | the five real `UniqueRevolverA..E` PNGs and their five `_m` masks (128×128, pulled out of the real `Odyssey` asset pack), plus the revolver and assault-rifle icons and four DDS from `assets/iconpipe` |
| `Config/ModsConfig.xml` | `ModLister.RebuildModList()` throws `Sequence contains no matching element` when no official mod can be found | `ci/ModsConfig.xml` (Core + Odyssey) written into the game's config folder before boot |
| `libstb.so` | `.dds` icons are decoded through the port's native `gli` bridge (`Brutal.Gli` → `libgli.so`, a C shim over g-truc/gli with `load`, `loadFromMemory`, `extent`, `format`, `levels`, …). Without it `Texture2D.LoadFromFile` logs *"Substituting magenta placeholder"* and the icon is a 2×2 magenta texture | `natives/stb_shim.c` compiles `stb_image`/`stb_image_write` into `libstb.so`, and `ci/dds_to_png.py` converts the DDS corpus to PNG first (BC7 via Pillow, BC4 block-by-block, both already used by `pipe/`) |
| name/backstory text | `PawnNameDatabaseShuffled`'s static ctor reads `Resources/Text/Names/*.txt`; `WorldGenerator` touches it, so world generation dies with a `TypeInitializationException` | `game/Resources/Text/Names` + `game/Resources/Backstories/Solid` (pulled out of the real `Assets.zip`) |

No Vulkan loader, no Vulkan device, no GLFW, no ImGui: `EngineApplication`'s
constructor returns immediately after the audio stub when `Options.Headless` is set, `Bootstrap`
skips `ConfigureFrameRenderer`/`ConfigureTextureRuntime`, and `RunHeadlessLoop` is pure managed
code. Textures stay CPU-side (`UnityStubs.Texture2D` wraps `Waylith.Rendering.Texture2D`), which is
enough for `Widgets.GetIconFor`.

## The scenario

`ci/scenario` is a `DOTNET_STARTUP_HOOKS` assembly. It waits for
`Waylith.Runtime.WaylithAppState.BootCompleted` (raised on the main thread by
`Bootstrap.RaiseBootCompleteOnce` once `PlayDataLoader.Loaded` is true) and then:

1. reports the loaded `DefDatabase<ThingDef>` size, every def carrying a `CompProperties_UniqueWeapon`,
   and the five `Graphic_Single` sub-graphics behind `Gun_Revolver_Unique`'s `Graphic_Random`;
2. calls `SceneManager.LoadScene("Play")`. `Bootstrap.BuildPlayScene` adds a `Root_Play`, whose
   `Start()` creates `UIRoot_Play` (the cast `Find.MapUI` makes) and then queues the game's **own**
   quick-test long event: `SetupForQuickTestPlay` → `GameInitData.PrepForMapGen` →
   `Scenario.PreMapGenerate` (`ScenPart_PlayerFaction` plants the player `Settlement`) →
   `Game.InitNewGame` → `MapGenerator.GenerateMap`. No world or map is re-implemented here; the
   scenario only waits (off the main thread) for `Current.Game.CurrentMap` and then queues its own
   phase 2 as a non-async long event, i.e. on the main thread;
3. makes `Gun_Revolver_Unique` with `ThingMaker.MakeThing`, spawns it with `GenSpawn.Spawn` into that
   map, and calls `Widgets.GetIconFor(thing, size, rot, stackOfOne, …)` — the exact overload
   `Widgets.ThingIcon` uses, so the texture is the one the graphic picks for this thing id
   (`Graphic_Random.SubGraphicFor` → `thingIDNumber % subGraphics.Length`);
4. decodes the texture with `GetPixels32()` and writes a PNG with a small encoder inside the hook
   (deflate via `ZLibStream`, CRC32 written here — the engine's `EncodeToPNG` is not used), then
   prints coverage, mean RGB, mean alpha, distinct colours, bbox and the PNG's sha256;
5. asserts: five variants loaded, the icon is one of them, its index is the index the graphic picks
   for that thing, it is not `BaseContent.BadTex`, not the 2×2 placeholder, not a `*_m` mask,
   128×128, its material's main texture is the same texture, all 16384 pixels decoded, coverage in a
   plausible band, G and B are not zero (a BC4 mask would be (R,0,0)), the PNG is on disk, the thing
   is spawned, a second thing gets the variant its own id selects, and each of the five variants
   resolves to a real 128×128 texture. `RESULT=OK` requires all of them.

### Why the map mesh is left unbuilt

`Map.FinalizeInit` defers `MapDrawer.RegenerateEverythingNow()` to the end of the `GeneratingMap`
long event. On this runner that pass dies in `SectionLayer_Gas`'s constructor: `GasMat` is null
because `Things/Gas/GasCloudThickA` is not in the shipped texture subset, so only the first
`Section` is attempted and `sections` is left allocated with null entries. `Thing.SpawnSetup` →
`DirtyMapMesh` then dereferences a null `Section`. The scenario puts the drawer back into the state
`MapDrawer.MapMeshDirty` explicitly treats as a no-op (`sections == null`, the state every map is in
before that deferred pass) and logs `mapMesh=incomplete(0/225)->null`. The icon path never reads the
mesh; the spawn, the thing, the map and the def are all real.

`ci/report.py` is the gate: missing version line, missing `initialize finished`, non-zero exit code,
`RESULT != OK`, a map that is not `OK`, or **any** `assert=… ok=false` line fail the job; the table
it prints goes to the job summary, both full logs and the PNGs/`icon.json` go to the artifact.

