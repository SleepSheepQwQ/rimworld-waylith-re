# res — `Texture2D.EnsureResident` as an offline state machine

The GPU-residency gate for every icon/texture in the port:

```
GpuResource -> CpuData -> PendingBuild -> CpuData.HasData -> TryUploadFromCache -> NoteRehydrated
```

`res/` replays that gate as a pure function over six state fields and enumerates all 48
combinations. No game runtime, no Unity, no Vulkan, no phone. Every rule in the model is tied to
a member reference that `Facts.cs` requires to be present, in order, in `dlls/Waylith.dll`; if the
dll drifts, the run is **BROKEN (exit 2)** instead of green.

CI: `.github/workflows/residency.yml`.

```bash
dotnet run --project res/Res.csproj -c Release -- --dlls dlls --table res/out/residency-table.md
echo $?    # 0 pass / 1 assertion failed / 2 harness cannot vouch for the dll
```

Options: `--table <path>` writes the markdown truth table, `--force-fail` adds a failing assertion
(control run that must be red), `--dump Type::Method` prints one method body.

## The state

| field | values | meaning |
|---|---|---|
| `GpuResource` | no / yes | a live `GpuTextureAllocation` in `Texture._gpuResource` |
| `CpuData` | null / released / data | no `TexturePixelData` object; object with `Levels = Array.Empty` (`Release()`); object with pixels |
| `PendingBuild` | no / yes | a `TextureBuildBatch.Job` queued for this texture |
| `SourcePath` | no / yes | the file the texture came from (the cache key) |
| `Cache` | miss / hit | a readable texture cache for that key **and** the same pixel format (the mismatch sub-branch is in the table) |

`call-1` is one `EnsureResident` while `TextureBuildBatch.Resolve` cannot flush (off the main
thread, or the batch gate held). `call-2` is the next frame on the main thread. `retries` is the
number of additional calls needed before the texture can be sampled.

## What the dll says (facts the model is gated on)

| fact | evidence in `dlls/Waylith.dll` |
|---|---|
| `GpuResource` resolves a pending build before it is read | `get_GpuResource`: `ldfld PendingBuild` -> `call TextureBuildBatch.Resolve` -> `ldfld _gpuResource` |
| exit values | `EnsureResident` has 5 `ret`s: `true`, `false` (CpuData null **and** PendingBuild share one epilogue), `GpuResource != null`, `false` (cache failed), `true` (rehydrated) |
| cache path is synchronous | `TryUploadFromCache` -> `BuildCacheUploads` -> `UploadLevels` -> `GpuTextureService.CreateTexture2D` -> `set_GpuResource`; `UploadLevels` contains no `TextureBuildBatch.Enqueue` |
| released data + source + cache miss throws | `TryUploadFromSource` -> `RequireCpuData`: `TryLoadTextureFromCache` fails -> `newobj InvalidOperationException` -> `throw` |
| resolve can defer | `TextureBuildBatch.Resolve` returns early when `Environment.CurrentManagedThreadId != MainThreadId`, or when `Gate.TryEnter` fails; only then does it `FlushLocked` |
| eviction is GPU-only | `TryEvictForResidency` -> `Evict` (disposes the allocation); no `ReleaseCpuData` |
| false means nothing is drawn | `GpuTextureService.TryGetSampledImageInfo` -> `EnsureResident`; on false it returns false with a zeroed view/layout, so the draw binds no image |
| budget policy | `TextureResidencyPolicy` = `OnDemand` / `OffloadUnderPressure`; `EvictWhenCached` is `OnDemand`-only |

## Results (48 states)

* **29 drawn, 19 blank**: 12 blank for exactly one retry, 7 blank permanently (one throws).
* `GpuResource != null` -> **true in all 24 states**, including `CpuData = null`, no `SourcePath`,
  no cache and a pending build. This is the first branch and nothing after it can change the answer.
* `CpuData = null` (and no pending build) -> **false in all 4** source/cache combinations; the
  method never reaches the cache with a null `CpuData`.
* `PendingBuild != null`, no GPU -> **false this call, true next call, 1 retry** (12 states).
* `CpuData` released + `SourcePath` + cache hit -> **true on the first call, 0 retries**. The cache
  upload is synchronous; this state is *not* a fail-then-recover case.
* No GPU, no source, no usable cache, no pending build -> **false forever** (4 states).
* Released data + source + cache miss -> **throws** `InvalidOperationException`; it is not a
  `false`, and a retry cannot fix it without the cache being written.

### Two corrections to the working assumptions

1. The brief expected "released CPU data + source + cache hit -> fails, recovers next call". The
   shipped IL does not do that: with no pending build, `TryUploadFromCache` calls
   `UploadLevels` synchronously and returns true. The fail-then-recover class is **`PendingBuild`**,
   not the cache hit; the cache hit only looks like a retry when a pending build is also present.
2. "`PendingBuild` exists -> this call fails" is conditional. `Resolve` flushes on the main thread
   with a free gate, and then the same state returns true in-call. It fails only when `Resolve`
   returns early (off the main thread, or the gate is held). The truth table's `call-1` is that
   worst case; the "Resolve timing" table shows both.

## The blank states, and how many retries

| class | states | retries | why nothing is drawn |
|---|---:|---|---|
| pending build, no GPU | 12 | 1 | the job owns the upload; `EnsureResident` returns false before CPU data is consulted |
| `CpuData` null, no GPU | 4 | ∞ | field is null; the branch after the GPU check returns false without touching source/cache |
| `CpuData` released, no `SourcePath` | 2 | ∞ | the cache has no key, so `TryUploadFromCache` returns false |
| `CpuData` released, source, cache miss | 1 | ∞ | `RequireCpuData` cannot reload and throws |

So the UI-blank class that a retry *can* fix is exactly the deferred batch build: **1 retry**
(next frame, main thread, gate free). Everything else needs a state change, not a retry.

## Assumptions

* Allocation itself is an oracle: `CreateTexture2D` / `CreateEmptyTexture2D` are assumed to
  succeed. The matrix models control flow, not OOM or device loss.
* A queued job is assumed to complete successfully and carries `Mip0`; `CompleteBuild` then sets
  `GpuResource` even if `CpuData` is null. A failed batch falls back to `BuildOnCpu`, which still
  completes.
* The cache-hit row assumes the entry's pixel format equals `CpuData.Format`; the mismatch
  sub-branch reaches the same true via `TryUploadFromSource`.
