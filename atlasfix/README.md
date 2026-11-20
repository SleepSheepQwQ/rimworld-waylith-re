# atlasfix — the static atlas registers tiles for textures that never reached the page

`Verse.StaticTextureAtlas.Bake` builds every tile first (`BuildMeshesForUvs` writes
texture → uvRect into `tiles`, static.cs:164-171) and only then blits each texture into a page that
was cleared to `Color.clear` (`BlitTexturesToColorAtlas`, static.cs:474-492). The blit's only gate on
the source is `GpuTexture != null` after an `EnsureGpuResident()` attempt; when that fails,
`Graphics.DrawTexture` calls `StubWarner.LogStub` and returns — no exception, no log line that names
the texture, no cell written (UnityStubs `Graphics.cs`). Afterwards
`Graphic.TryGetTextureAtlasReplacementInfo` finds the tile and swaps the material's main texture for
the atlas page plus that empty cell, so the graphic stops drawing the texture it does have. One
transparent hole, permanent, for every texture that could not be hydrated inside the deferred-loading
window (`PlayDataLoader.DoPlayLoad` → `BeginDeferredLoading` → `BakeStaticAtlases`).

The probe executes the shipped assemblies — it re-implements neither `Bake` nor the residency gate:

| phase | what it does |
|---|---|
| 0 | environment facts: `DeepProfiler.enabled=false`, atlas compression off (so `Bake` takes the blit path), `UnityData.mainThreadId` set (otherwise `MaterialAllocator` refuses and `Bake` NREs before the loop) |
| 0b | two textures: `live` = 128² RGBA32 with opaque pixels; `dead` = `Waylith.Rendering.Texture2D.NewFileTexture` pointed at a file that is not there (CpuData with no levels, `SourcePath` set, no GPU resource) — the deferred-loading shape |
| 1 | control: shipped `Bake` on a fresh atlas, no patch |
| 2 | control: `Graphic.TryGetTextureAtlasReplacementInfo` for both textures (the atlas is registered in `GlobalTextureAtlasManager.staticTextureAtlases` for the duration) |
| 3 | install GlyphFix (`[StaticConstructorOnStartup]` static ctor via `RuntimeHelpers.RunClassConstructor`) and print `Harmony.GetPatchInfo(Bake).Postfixes` |
| 4 | patched: the same two textures through the same shipped `Bake` |
| 5 | patched: replacement info again |
| 6 | verdicts |

With `--gpu` the run first brings up a real Vulkan device (lavapipe in CI) without the engine's
window or render loop: hidden GLFW window → `glfwCreateWindowSurface` → `GraphicsDevice.CreateInstance`
→ `GraphicsDevice.Create` → `GpuTextureService.Configure`, i.e. the same calls
`EngineApplication.ConfigureGpuServices` makes. Then `Bake`'s blits are real, the page is read back
with `GpuTextureService.ReadbackTexture2DLevels`, and each tile's cell is counted at the rect the blit
loop itself computed. Without `--gpu` the device-less run can only show the registration and the
replacement hit — the pixel and positive-control halves are `[SKIP]`ped rather than guessed.

Harness instrumentation (both runs, logged as `[FACT]`): `Verse.GenTexture.GenerateMipmaps` is
no-op'd. That is the compute pass over the finished page, it runs after `CopyTexture` and after every
blit, and it cannot change mip0 or the tile bookkeeping being measured.

```sh
dotnet run --project atlasfix/AtlasFix.csproj -c Release -- \
  --dlls dlls --mod path/to/GlyphFix.dll            # negative control
xvfb-run -a dotnet run --project atlasfix/AtlasFix.csproj -c Release -- \
  --dlls dlls --mod path/to/GlyphFix.dll --gpu      # real device, real pixels
```

Exit status is the gate: any `[FAIL]` line fails the job, and `--gpu` without a device fails too.
