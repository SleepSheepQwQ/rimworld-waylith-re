using System;
using System.Collections.Generic;

namespace Residency
{
    enum CpuState { Null, Released, HasData }
    enum Outcome { True, False, Throw }
    enum CacheState { Miss, Hit }

    sealed class State
    {
        public bool Gpu;
        public CpuState Cpu;
        public bool Pending;
        public bool Source;
        public CacheState Cache;

        public State Clone() => (State)MemberwiseClone();

        public string CpuText => Cpu == CpuState.Null ? "null" : Cpu == CpuState.Released ? "released" : "data";

        public string Key => (Gpu ? "g1" : "g0") + "-c" + (Cpu == CpuState.Null ? "n" : Cpu == CpuState.Released ? "r" : "d")
                           + "-p" + (Pending ? 1 : 0) + "-s" + (Source ? 1 : 0) + "-k" + (Cache == CacheState.Hit ? 1 : 0);
    }

    sealed class CallResult
    {
        public Outcome Outcome;
        public string Path;
        public State After;

        public bool Success => Outcome == Outcome.True;
    }

    // The offline replica of Waylith.Rendering.Texture2D.EnsureResident.
    //
    // Each rule below is anchored to a call that Facts.cs requires to exist, in this order, in
    // dlls/Waylith.dll:
    //
    //   get_GpuResource              -> Resolve a queued TextureBuildBatch job first (facts: gpu-getter-resolves-first)
    //   if (gpu != null) return true
    //   if (CpuData == null) return false                      (facts: ensure-exits)
    //   if (PendingBuild != null) return false                 (facts: ensure-exits)
    //   if (CpuData.HasData) { UploadCpuData(); return gpu != null; }
    //   if (!TryUploadFromCache()) return false;
    //   NoteRehydrated(SourcePath, w, h); return true;
    //
    // TryUploadFromCache:
    //   no SourcePath -> false;  no CpuData -> false
    //   cache miss      -> TryUploadFromSource()   (facts: cache-path)
    //   format mismatch -> TryUploadFromSource()
    //   cache hit       -> UploadLevels(cache) ; return gpu != null
    //
    // TryUploadFromSource -> RequireCpuData(): no data + SourcePath -> reload from cache, else throw.
    // UploadLevels allocates synchronously (facts: upload-levels-sync), so a cache hit is one call.
    sealed class Engine
    {
        // Whether TextureBuildBatch.Resolve can flush in this call. Off the main thread, or while
        // the gate is held, it returns before FlushLocked (facts: resolve-defer) and the job stays
        // pending for the next call.
        public static CallResult Call(State s, bool canResolve)
        {
            var st = s.Clone();

            if (st.Pending && canResolve)
            {
                // Resolve -> FlushLocked -> Submit/Complete -> BuildOnCpu/CompleteBuild ->
                // set_GpuResource. The queued job carries Mip0, so completion produces an allocation.
                st.Pending = false;
                st.Gpu = true;
            }

            if (st.Gpu) return R(st, Outcome.True, "gpu-resident");
            if (st.Cpu == CpuState.Null) return R(st, Outcome.False, "cpu-null");
            if (st.Pending) return R(st, Outcome.False, "pending-deferred");
            if (st.Cpu == CpuState.HasData)
            {
                st.Gpu = true;                        // UploadCpuData -> UploadLevels -> CreateTexture2D
                return R(st, Outcome.True, "upload-cpu-data");
            }

            // CpuState.Released: only the cache or the source file can bring the levels back.
            if (!st.Source) return R(st, Outcome.False, "released-no-source");
            if (st.Cache == CacheState.Hit)
            {
                st.Gpu = true;                        // BuildCacheUploads -> UploadLevels -> CreateTexture2D
                return R(st, Outcome.True, "cache-hit-upload");
            }
            // Miss -> TryUploadFromSource -> RequireCpuData: TryLoadTextureFromCache fails and the
            // method throws InvalidOperationException rather than returning false.
            return R(st, Outcome.Throw, "cache-miss-throws");
        }

        static CallResult R(State s, Outcome o, string path) => new CallResult { Outcome = o, Path = path, After = s };

        // A retry is a second EnsureResident on a later frame: the main thread can now resolve.
        public static int RetriesToDraw(State s)
        {
            var first = Call(s, canResolve: false);
            if (first.Success) return 0;
            var second = Call(first.After, canResolve: true);
            return second.Success ? 1 : int.MaxValue;
        }
    }
}
