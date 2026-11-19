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
| `RimWorld_Data/Resources/Textures/...` | `Resources.Load`/`LoadAll` read `<Application.dataPath>/Resources`; without it every `Graphic_Collection` logs "No textures found" and falls back to `BaseContent.BadGraphic` | four real DDS from `assets/iconpipe` (the unique revolver albedo + mask, the revolver and assault-rifle icons) |
| `Config/ModsConfig.xml` | `ModLister.RebuildModList()` throws `Sequence contains no matching element` when no official mod can be found | `ci/ModsConfig.xml` (Core + Odyssey) written into the game's config folder before boot |

No Vulkan loader, no Vulkan device, no GLFW, no ImGui, no `libbcdec`: `EngineApplication`'s
constructor returns immediately after the audio stub when `Options.Headless` is set, `Bootstrap`
skips `ConfigureFrameRenderer`/`ConfigureTextureRuntime`, and `RunHeadlessLoop` is pure managed
code. Textures stay CPU-side (`UnityStubs.Texture2D` wraps `Waylith.Rendering.Texture2D`), which is
enough for `Widgets.GetIconFor`.

## The scenario

`ci/scenario` is a `DOTNET_STARTUP_HOOKS` assembly. It waits for
`Waylith.Runtime.WaylithAppState.BootCompleted` (raised on the main thread by
`Bootstrap.RaiseBootCompleteOnce` once `PlayDataLoader.Loaded` is true) and then:

1. reports the loaded `DefDatabase<ThingDef>` size and every def carrying a `CompProperties_UniqueWeapon`;
2. builds a real map — `Current.Game = new Game()`, `Scenario` from the `Crashlanded` ScenarioDef
   (`WorldGrid.CreateRequiredLayers` reads `Find.Scenario`), `WorldGenerator.GenerateWorld`,
   `MapGenerator.GenerateMap(75×75, MapGeneratorDefOf.Base_Faction)`;
3. asks `Widgets.GetIconFor(def)` for `Gun_Revolver_Unique` and prints texture name, size and whether
   it is `BaseContent.BadTex`, then `[SCENARIO] RESULT=OK|FAIL`.

`ci/report.py` is the gate: missing version line, missing `initialize finished`, non-zero exit code
or `RESULT != OK` fail the job; the table it prints goes to the job summary, both full logs go to
the artifact.
