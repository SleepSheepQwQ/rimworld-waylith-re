using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Metadata.Ecma335;

namespace Residency
{
    sealed class Broken : Exception { public Broken(string m) : base(m) { } }

    // Every claim the replica makes about the shipped binary is checked here first. A dll whose
    // EnsureResident no longer has this shape turns the run BROKEN (exit 2), never green.
    sealed class Facts
    {
        readonly Asm A;
        public readonly List<string> Lines = new List<string>();

        public Facts(Asm a) { A = a; }

        void Fact(string id, string text)
        {
            string line = "[FACT] " + id + " " + text;
            Lines.Add(line);
            Console.WriteLine(line);
        }

        List<Insn> Body(string type, string method) => A.Body(type, method);

        static List<string> Ops(List<Insn> il, params string[] names)
        {
            var set = new HashSet<string>(names);
            return il.Where(i => set.Contains(i.Name)).Select(i => i.Name).ToList();
        }

        static string Describe(Asm a, Insn i)
        {
            if (!i.HasToken) return i.Name;
            try { return a.Describe(MetadataTokens.EntityHandle(i.Token)); }
            catch { return i.Name + "(tok)"; }
        }

        // Ordered list of the interesting member references (call/callvirt/newobj/field access).
        static List<(int Off, string Name)> Refs(Asm a, List<Insn> il)
        {
            var list = new List<(int, string)>();
            foreach (var i in il)
            {
                if (!i.HasToken) continue;
                if (!(i.Name == "call" || i.Name == "callvirt" || i.Name == "newobj"
                      || i.Name == "ldfld" || i.Name == "ldflda" || i.Name == "ldsfld")) continue;
                list.Add((i.Offset, Describe(a, i)));
            }
            return list;
        }

        static int Find(List<(int Off, string Name)> refs, string suffix, int start = 0)
        {
            for (int i = start; i < refs.Count; i++)
                if (refs[i].Name.EndsWith(suffix, StringComparison.Ordinal)) return i;
            return -1;
        }

        void Requires(string type, string method, string label, params string[] suffixes)
        {
            var refs = Refs(A, Body(type, method));
            int at = 0, firstOff = -1;
            foreach (var s in suffixes)
            {
                int i = Find(refs, s, at);
                if (i < 0)
                    throw new Broken(type + "::" + method + " no longer contains `" + s + "` in the expected order (" + label + ")");
                if (firstOff < 0) firstOff = refs[i].Off;
                at = i + 1;
            }
            Fact(label, type.Split('.').Last() + "::" + method + " IL_" + firstOff.ToString("X4")
                 + " -> " + string.Join(" -> ", suffixes.Select(s => s.Split("::").Last())));
        }

        static List<string> ReturnKinds(List<Insn> il)
        {
            var rets = new List<string>();
            for (int i = 0; i < il.Count; i++)
            {
                if (il[i].Name != "ret") continue;
                string kind = "?";
                for (int j = i - 1; j >= 0; j--)
                {
                    string n = il[j].Name;
                    if (n == "nop") continue;
                    if (n == "ldc.i4.0") kind = "false";
                    else if (n == "ldc.i4.1") kind = "true";
                    else if (n == "cgt.un") kind = "gpu-nonnull";
                    else kind = n;
                    break;
                }
                rets.Add(kind);
            }
            return rets;
        }

        public void Check()
        {
            // 1. GpuResource is resolved before it is read: PendingBuild -> TextureBuildBatch.Resolve -> _gpuResource.
            Requires("Waylith.Rendering.Texture", "get_GpuResource", "gpu-getter-resolves-first",
                "Texture::PendingBuild", "TextureBuildBatch::Resolve", "Texture::_gpuResource");

            // 2. EnsureResident's branch order and its six exit values.
            Requires("Waylith.Rendering.Texture2D", "EnsureResident", "ensure-order",
                "Texture::get_GpuResource",
                "Texture2D::get_CpuData",
                "Texture::PendingBuild",
                "Texture2D::get_CpuData",
                "TexturePixelData::get_HasData",
                "Texture2D::UploadCpuData",
                "Texture::get_GpuResource",
                "Texture2D::TryUploadFromCache",
                "Texture2D::get_SourcePath",
                "GpuTextureService::NoteRehydrated");

            var er = ReturnKinds(Body("Waylith.Rendering.Texture2D", "EnsureResident"));
            string got = string.Join(",", er);
            const string want = "true,false,gpu-nonnull,false,true";
            if (got != want)
                throw new Broken("EnsureResident returns [" + got + "], expected [" + want + "]");
            Fact("ensure-exits", "5 exits: gpu=true, cpu-null-or-pending=false (shared epilogue), hasdata=gpu!=null, cache-fail=false, rehydrated=true");

            // 3. The cache path is synchronous and has the miss / format-mismatch re-entry.
            Requires("Waylith.Rendering.Texture2D", "TryUploadFromCache", "cache-path",
                "Texture2D::get_SourcePath",
                "Texture2D::get_CpuData",
                "GpuTextureService::TryOpenTextureCache",
                "Texture2D::TryUploadFromSource",
                "ITextureCacheSource::get_Format",
                "TexturePixelData::get_Format",
                "Texture2D::BuildCacheUploads",
                "Texture2D::UploadLevels",
                "Texture2D::TryUploadFromSource",
                "Texture::get_GpuResource");
            var cr = ReturnKinds(Body("Waylith.Rendering.Texture2D", "TryUploadFromCache"));
            if (string.Join(",", cr) != "false,call,call,gpu-nonnull")
                throw new Broken("TryUploadFromCache exits [" + string.Join(",", cr) + "]");
            Fact("cache-exits", "4 exits: no-source-or-no-cpu=false (shared), miss=TryUploadFromSource, format-mismatch=TryUploadFromSource, hit=GpuResource!=null");

            // 4. Source path: RequireCpuData -> HasData -> UploadCpuData -> ReleaseCpuLevels.
            Requires("Waylith.Rendering.Texture2D", "TryUploadFromSource", "source-path",
                "Texture2D::RequireCpuData",
                "TexturePixelData::get_HasData",
                "Texture2D::UploadCpuData",
                "Texture2D::ReleaseCpuLevels",
                "Texture::get_GpuResource");

            // 5. RequireCpuData can throw: released data + source + no readable cache is an exception, not false.
            var req = Body("Waylith.Rendering.Texture2D", "RequireCpuData");
            bool throws = req.Any(i => i.Name == "throw");
            bool reload = Refs(A, req).Any(r => r.Name.EndsWith("GpuTextureService::TryLoadTextureFromCache", StringComparison.Ordinal));
            if (!throws || !reload)
                throw new Broken("RequireCpuData no longer reloads-from-cache-then-throws (throws=" + throws + ", reload=" + reload + ")");
            Fact("require-cpu", "released CPU data + SourcePath -> TryLoadTextureFromCache, else InvalidOperationException (throw)");

            // 6. UploadLevels allocates synchronously. No batch enqueue on the cache/source path.
            var ul = Body("Waylith.Rendering.Texture2D", "UploadLevels");
            Requires("Waylith.Rendering.Texture2D", "UploadLevels", "upload-levels-sync",
                "GpuTextureService::CreateTexture2D", "Texture::set_GpuResource");
            if (Refs(A, ul).Any(r => r.Name.EndsWith("TextureBuildBatch::Enqueue", StringComparison.Ordinal)))
                throw new Broken("UploadLevels now enqueues a TextureBuildBatch job; the synchronous-cache claim is void");

            // 7. UploadCpuData cancels a pending build, then goes through the same UploadLevels.
            Requires("Waylith.Rendering.Texture2D", "UploadCpuData", "upload-cpu-data",
                "Texture2D::get_CpuData",
                "Texture::PendingBuild",
                "TextureBuildBatch::Cancel",
                "TexturePixelData::BuildLevelUploads",
                "Texture2D::UploadLevels");

            // 8. Resolve really can defer: main-thread test and a non-blocking gate, both early-return.
            Requires("Waylith.Rendering.TextureBuildBatch", "Resolve", "resolve-gate",
                "WaylithRuntime::get_Current",
                "WaylithRuntime::get_App",
                "WaylithAppState::get_MainThreadId",
                "Environment::get_CurrentManagedThreadId",
                "Lock::TryEnter",
                "TextureBuildBatch::FlushLocked");
            Fact("resolve-defer", "Resolve returns without flushing when off the main thread or the batch gate is held");

            // 9. Eviction is GPU-only: it disposes the allocation, it does not release the CPU levels.
            var ev = Body("Waylith.Rendering.Texture2D", "TryEvictForResidency");
            bool releasesCpu = Refs(A, ev).Any(r => r.Name.EndsWith("Texture2D::ReleaseCpuData", StringComparison.Ordinal));
            if (releasesCpu)
                throw new Broken("TryEvictForResidency now releases CPU data; re-upload-after-evict would no longer be one call");
            Requires("Waylith.Rendering.Texture2D", "TryEvictForResidency", "evict-gpu-only",
                "Texture::get_GpuResource",
                "Texture2D::get_CpuData",
                "Texture::get_IsReadable",
                "Texture2D::get_SourcePath",
                "GpuTextureService::HasValidTextureCache",
                "Texture2D::Evict");

            // 9b. The draw-side seam: a false EnsureResident hands the sampler no image at all.
            Requires("Waylith.Rendering.GpuTextureService", "TryGetSampledImageInfo", "blank-path",
                "Texture::get_GpuResource",
                "Texture::EnsureResident",
                "Texture::get_GpuResource");
            Fact("blank-path", "TryGetSampledImageInfo returns false with a zeroed view/layout when EnsureResident is false");

            // 10. The budget policy the eviction decision is made under.
            if (!A.HasField("Waylith.Rendering.TextureResidencyPolicy", "OnDemand")
                || !A.HasField("Waylith.Rendering.TextureResidencyPolicy", "OffloadUnderPressure"))
                throw new Broken("TextureResidencyPolicy no longer has OnDemand/OffloadUnderPressure");
            Fact("policy", "Waylith.Rendering.TextureResidency.Policy in {OnDemand, OffloadUnderPressure}; EvictWhenCached only under OnDemand");
        }
    }
}
