// IL facts, read out of the shipped assemblies with Mono.Cecil. Every fact names the decompiled
// source line it corresponds to, so the claim can be re-checked by reading the tree.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace AtlasProbe
{
    sealed class Ceci
    {
        public readonly List<string> Facts = new List<string>();
        public readonly List<(string Name, bool Ok, string Detail)> Asserts = new List<(string, bool, string)>();

        AssemblyDefinition W, A, U;

        public static Ceci Open(string dir)
        {
            try
            {
                var c = new Ceci
                {
                    W = AssemblyDefinition.ReadAssembly(Path.Combine(dir, "Waylith.dll")),
                    A = AssemblyDefinition.ReadAssembly(Path.Combine(dir, "Assembly-CSharp.dll")),
                    U = AssemblyDefinition.ReadAssembly(Path.Combine(dir, "UnityStubs.dll")),
                };
                return c;
            }
            catch { return null; }
        }

        // ------------------------------------------------------------------ helpers
        static MethodDefinition M(AssemblyDefinition asm, string type, string name, int? argc = null)
        {
            var t = asm?.MainModule.GetType(type);
            if (t == null) return null;
            return t.Methods.FirstOrDefault(m => m.Name == name && m.HasBody && (argc == null || m.Parameters.Count == argc));
        }
        static IEnumerable<Instruction> Body(MethodDefinition m) => m?.Body?.Instructions ?? Enumerable.Empty<Instruction>();

        static bool Calls(MethodDefinition m, string declType, string name) =>
            Body(m).Any(i => i.Operand is MethodReference mr && mr.Name == name &&
                             (declType == null || mr.DeclaringType.Name == declType || mr.DeclaringType.FullName == declType));

        static bool CallsAny(MethodDefinition m, string declType, params string[] names) =>
            names.Any(n => Calls(m, declType, n));

        static int CallIndex(MethodDefinition m, string declType, string name)
        {
            int k = 0;
            foreach (var i in Body(m))
            {
                if (i.Operand is MethodReference mr && mr.Name == name && (declType == null || mr.DeclaringType.Name == declType)) return k;
                k++;
            }
            return -1;
        }

        static int CountReturns(MethodDefinition m) => Body(m).Count(i => i.OpCode == OpCodes.Ret);

        static bool Throws(MethodDefinition m) => Body(m).Any(i => i.OpCode == OpCodes.Throw || (i.Operand is MethodReference t && t.Name == ".ctor" && t.DeclaringType.Name == "InvalidOperationException"));

        static bool CallsWithInt(MethodDefinition m, string declType, string name, int value) =>
            Body(m).Any(i => i.Operand is MethodReference mr && mr.Name == name &&
                             (declType == null || mr.DeclaringType.Name == declType) &&
                             i.Previous != null && i.Previous.OpCode == OpCodes.Ldc_I4_S && Convert.ToInt32(i.Previous.Operand) == value);

        static bool RefersToFieldNamed(MethodDefinition m, string typeName, Func<string, bool> pred) =>
            Body(m).Any(i => i.Operand is FieldReference fr && (typeName == null || fr.DeclaringType.Name == typeName) && pred(fr.Name));

        void F(string text) => Facts.Add(text);
        void C(string name, bool ok, string detail = "") => Asserts.Add((name, ok, detail));

        // ------------------------------------------------------------------ facts
        public void Run()
        {
            var uploadLevels = M(W, "Waylith.Rendering.Texture2D", "UploadLevels");
            var newFileTexture = M(W, "Waylith.Rendering.Texture2D", "NewFileTexture");
            var requireCpu = M(W, "Waylith.Rendering.Texture2D", "RequireCpuData");
            var fromCache = M(W, "Waylith.Rendering.Texture2D", "TryUploadFromCache");
            var fromSource = M(W, "Waylith.Rendering.Texture2D", "TryUploadFromSource");
            var ensureResident = M(W, "Waylith.Rendering.Texture2D", "EnsureResident");
            var evict = M(W, "Waylith.Rendering.Texture2D", "Evict");
            var drainEvict = M(W, "Waylith.Rendering.Texture2D", "DrainPendingEvictions");
            var evictResidency = M(W, "Waylith.Rendering.Texture2D", "TryEvictForResidency");
            var create = M(W, "Waylith.Rendering.GpuTextureService", "CreateTexture2D", 10) ?? M(W, "Waylith.Rendering.GpuTextureService", "CreateTexture2D");
            var createEmpty = M(W, "Waylith.Rendering.GpuTextureService", "CreateEmptyTexture2D");
            var sampled = M(W, "Waylith.Rendering.GpuTextureService", "TryGetSampledImageInfo");
            var resolve = M(W, "Waylith.Rendering.TextureBuildBatch", "Resolve");
            var frameStart = M(W, "Waylith.Rendering.TextureBuildBatch", "OnFrameStart");

            // 1. mip generation is skipped for block-compressed formats
            bool bcGuard = false; string bcDetail = "no call";
            if (uploadLevels != null)
            {
                int iBc = CallIndex(uploadLevels, "TexturePixelData", "IsBlockCompressed");
                int iGen = CallIndex(uploadLevels, "GpuTextureService", "GenerateMipmaps");
                int iAlloc = CallIndex(uploadLevels, "GpuTextureService", "CreateTexture2D");
                bool branchBetween = false;
                if (iBc >= 0 && iGen > iBc)
                {
                    var ins = Body(uploadLevels).ToArray();
                    for (int k = iBc; k < iGen; k++)
                    {
                        var op = ins[k].OpCode;
                        if (op == OpCodes.Brtrue || op == OpCodes.Brtrue_S || op == OpCodes.Brfalse || op == OpCodes.Brfalse_S) branchBetween = true;
                    }
                }
                bcGuard = iBc >= 0 && iGen > iBc && branchBetween && iAlloc >= 0 && iAlloc < iGen;
                bcDetail = $"IsBlockCompressed@{iBc} CreateTexture2D@{iAlloc} GenerateMipmaps@{iGen} branchBetween={branchBetween}";
                if (bcGuard)
                    F("`Waylith.Rendering/Texture2D.cs:234-239` - UploadLevels allocates `max(MipCount, uploads.Length)` mips (CreateTexture2D), then calls GenerateMipmaps only when `!IsBlockCompressed(format)` (IL: IsBlockCompressed -> conditional branch -> GenerateMipmaps).");
            }
            C("Texture2D::UploadLevels gates GPU mip generation on !IsBlockCompressed", bcGuard, bcDetail);

            // 2. a 1-mip file is promoted to a full chain
            bool promote = newFileTexture != null && Calls(newFileTexture, "TexturePixelData", "CalculateMipCount");
            C("Texture2D::NewFileTexture promotes a 1-mip file to CalculateMipCount(w,h)", promote);
            if (promote)
                F("`Waylith.Rendering/Texture2D.cs:767` - `mipCount2 = mipCount > 1 ? mipCount : TexturePixelData.CalculateMipCount(width, height)`; the 1-mip source file therefore gets a 12-level image descriptor (256x256) and only level 0 is ever written.");

            // 3. recovery can only read the cache, never the source file
            bool cacheOnly = requireCpu != null && Calls(requireCpu, "GpuTextureService", "TryLoadTextureFromCache")
                                       && !CallsAny(requireCpu, null, "LoadTextureFromFile", "LoadTextureFromFileAsync");
            C("Texture2D::RequireCpuData reloads from the texture cache only (never the source file)", cacheOnly);
            if (cacheOnly)
                F("`Waylith.Rendering/Texture2D.cs:162-172` - RequireCpuData calls `GpuTextureService.TryLoadTextureFromCache(SourcePath, ...)`; there is no path back to `LoadTextureFromFile`, so a released CPU copy with no cache entry is unrecoverable even though the PNG/DDS is still on disk.");

            // 4. the rehydrate gate compares the cache format with the CPU format
            bool fmtGate = fromCache != null && Calls(fromCache, null, "get_Format") && Calls(fromCache, "Texture2D", "TryUploadFromSource");
            C("Texture2D::TryUploadFromCache gates on `file.Format == CpuData.Format` then falls back to TryUploadFromSource", fmtGate);
            if (fmtGate)
                F("`Waylith.Rendering/Texture2D.cs:373-386` - `flag = file.Format == CpuData.Format; if (flag) UploadLevels(...); if (!flag) return TryUploadFromSource();` - a cache written under a different target format (the cache directory is keyed on `TextureCompressionSettings.CacheTag`, GpuTextureService.cs:212-224) misses the gate; the fallback then finds no CPU data and returns false.");

            // 5. EnsureResident exit shape
            int rets = ensureResident == null ? -1 : CountReturns(ensureResident);
            C("Texture2D::EnsureResident still has 5 returns (true / false / true / false / true)", rets == 5, "rets=" + rets);
            if (rets == 5)
                F("`Waylith.Rendering/Texture2D.cs:340-361` - EnsureResident: GpuResource!=null -> true; CpuData==null || PendingBuild!=null -> false; HasData -> UploadCpuData; TryUploadFromCache()==false -> false; else true.");

            // 6. the atlas evicts without checking the cache; the residency evictions do check
            var atlasEvict = M(A, "Verse.StaticTextureAtlas", "EvictSourceTextures");
            var atlasEvictAll = M(A, "Verse.GlobalTextureAtlasManager", "EvictAtlasSourceTextures");
            bool atlasBlind = atlasEvict != null && Calls(atlasEvict, "UnityEngine.Texture2D", "EvictGpuResource")
                                            && !CallsAny(atlasEvict, "GpuTextureService", "HasValidTextureCache");
            bool drainChecks = drainEvict != null && Calls(drainEvict, "GpuTextureService", "HasValidTextureCache");
            bool residencyChecks = evictResidency != null && Calls(evictResidency, "GpuTextureService", "HasValidTextureCache");
            C("StaticTextureAtlas::EvictSourceTextures evicts every member without HasValidTextureCache", atlasBlind);
            C("Texture2D::DrainPendingEvictions does check HasValidTextureCache (contrast)", drainChecks);
            C("Texture2D::TryEvictForResidency does check HasValidTextureCache (contrast)", residencyChecks);
            if (atlasBlind && drainChecks && residencyChecks)
                F("`Verse/StaticTextureAtlas.cs:337-347` evicts every member via `EvictGpuResource()` with no cache check; the two residency-driven evictions do check - `Waylith.Rendering/Texture2D.cs:436` (DrainPendingEvictions) and `:531` (TryEvictForResidency) both call `GpuTextureService.HasValidTextureCache(...)`. The atlas path is the one that skips it.");
            bool atlasPerAtlas = atlasEvictAll != null && Calls(atlasEvictAll, "StaticTextureAtlas", "EvictSourceTextures");
            if (atlasPerAtlas)
                F("`Verse/GlobalTextureAtlasManager.cs:284-295` - EvictAtlasSourceTextures walks every registered page and calls EvictSourceTextures(), only gated on the `noEvictAtlasSources` command line flag (AtlasSourceEvictionEnabled).");

            // 7. PackTextures is a stub that returns null
            var pack = M(U, "UnityEngine.Graphics", "PackTextures") ?? M(U, "UnityEngine.Texture2D", "PackTextures");
            bool packStub = false; string packDetail = "not found";
            if (pack != null)
            {
                var ins = Body(pack).Where(i => i.OpCode != OpCodes.Nop).ToArray();
                packStub = ins.Length == 2 && ins[0].OpCode == OpCodes.Ldnull && ins[1].OpCode == OpCodes.Ret;
                packDetail = string.Join("; ", ins.Select(i => i.OpCode.Name));
            }
            C("UnityEngine.Texture2D::PackTextures is `ldnull; ret` (a stub)", packStub, packDetail);
            if (packStub)
                F("`UnityStubs/Texture2D.cs:460` - `PackTextures` logs a stub warning and returns null. `StaticTextureAtlas.CalcRectsForAtlas` (static.cs:450) feeds that null straight into `Bake`, which does `array.Length` (static.cs:165) - so the divisor>=4 fallback is a NullReferenceException, not a pack.");

            // 8. a failed DrawTexture during the bake only logs a stub warning
            var drawTex = M(U, "UnityEngine.Graphics", "DrawTexture", 10);
            bool silentDraw = drawTex != null && Calls(drawTex, null, "TryDrawTextureGpu") && Calls(drawTex, null, "LogStub")
                                            && !Throws(drawTex) && !CallsAny(drawTex, "Debug", "LogError");
            C("UnityEngine.Graphics::DrawTexture reports a failed GPU draw with LogStub only (no throw, no LogError)", silentDraw);
            if (silentDraw)
                F("`UnityStubs/Graphics.cs:591` - `else if (!TryDrawTextureGpu(...)) StubWarner.LogStub(\"DrawTexture\", ...)`; the bake loop (static.cs:483-493) has no other error path, so a tile whose source cannot be made resident is silently skipped.");

            // 9. tiles are registered before the blit
            var bake = M(A, "Verse.StaticTextureAtlas", "Bake");
            bool orderOk = false; string orderDetail = "n/a";
            if (bake != null)
            {
                int iMesh = CallIndex(bake, "StaticTextureAtlas", "BuildMeshesForUvs");
                int iBlit = CallIndex(bake, "StaticTextureAtlas", "BlitTexturesToColorAtlas");
                orderOk = iMesh >= 0 && iBlit >= 0 && iMesh < iBlit;
                orderDetail = $"BuildMeshesForUvs@{iMesh} BlitTexturesToColorAtlas@{iBlit}";
            }
            C("StaticTextureAtlas::Bake registers tiles (BuildMeshesForUvs) before BlitTexturesToColorAtlas", orderOk, orderDetail);
            if (orderOk)
                F("`Verse/StaticTextureAtlas.cs:171-181` - BuildMeshesForUvs runs first, so `tiles` already maps every texture to a uvRect before the blit; `TryGetStaticTile` therefore returns a tile even for a texture that the blit skipped.");

            // 10. clearing: CreateEmptyTexture2D clears non-BC images, CreateTexture2D never clears
            bool emptyClears = createEmpty != null && Calls(createEmpty, null, "ClearColorImage");
            bool createClears = create != null && Calls(create, null, "ClearColorImage");
            C("GpuTextureService::CreateEmptyTexture2D clears (ClearColorImage) non-BC images", emptyClears);
            C("GpuTextureService::CreateTexture2D never clears, even with an empty level list", !createClears);
            if (emptyClears && !createClears)
                F("`Waylith.Rendering/GpuTextureService.cs:592-597` - `clearable = !IsDepthFormat && !(format >= Bc1RGBUNormBlock && format <= Bc7SRGBBlock)`: every BC image skips `vkCmdClearColorImage` and is left holding whatever VMA handed out. `CreateTexture2D` (GpuTextureService.cs:376-390) has no clear at all on its `levels.Length == 0` early return (it only transitions the layout).");

            // 11. residency frame order
            var gpuRes = M(W, "Waylith.Rendering.Texture", "get_GpuResource");
            bool resolvesOnRead = gpuRes != null && Calls(gpuRes, "TextureBuildBatch", "Resolve");
            C("Texture::get_GpuResource resolves a pending build before returning", resolvesOnRead);
            bool threadGate = resolve != null && Calls(resolve, "Environment", "get_CurrentManagedThreadId") && Calls(resolve, null, "TryEnter");
            C("TextureBuildBatch::Resolve returns early off the main thread or when the gate is held", threadGate);
            bool twoFrames = frameStart != null && CallsWithInt(frameStart, "TextureBuildBatch", "FlushLocked", 2);
            C("TextureBuildBatch::OnFrameStart flushes 2 frames after the last enqueue", twoFrames);
            if (resolvesOnRead && threadGate && twoFrames)
                F("`Waylith.Rendering/Texture.cs:59-68` + `TextureBuildBatch.cs:169-187,214-230` - any read of GpuResource on the main thread flushes the *whole* pending batch synchronously (FlushLocked(wait:true)); the 2-frame idle window only matters when nothing reads the texture. So a PendingBuild blank is a main-thread-stall hazard, not a multi-frame one.");

            // 12. the draw path gives up on a non-resident texture
            bool sampledFalse = sampled != null && CountReturns(sampled) >= 2 && Calls(sampled, "Texture", "EnsureResident");
            C("GpuTextureService::TryGetSampledImageInfo calls EnsureResident and returns false when it fails", sampledFalse);
            if (sampledFalse)
                F("`Waylith.Rendering/GpuTextureService.cs:1121-1140` - TryGetSampledImageInfo: `if (gpuResource == null && texture.EnsureResident()) ...; if (gpuResource == null) { imageView = default; imageLayout = Undefined; return false; }` - the draw binds no image.");

            // 13. HandleSourceEvicted is the only thing that keeps a texture evicted
            var endDeferred = M(W, "Waylith.Rendering.Texture2D", "EndDeferredLoading");
            bool leaves = endDeferred != null && Calls(endDeferred, null, "Invoke");
            if (leaves)
                F("`Waylith.Rendering/Texture2D.cs:464-516` - EndDeferredLoading uploads every metadata-only texture that is *not* an atlas member (`stayEvicted(texture)`), i.e. being in `evictableSources` is what keeps a source evicted across the boot batch.");
        }
    }
}
