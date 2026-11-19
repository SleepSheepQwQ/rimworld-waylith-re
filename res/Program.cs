using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Residency
{
    sealed class Row
    {
        public int N;
        public State S;
        public CallResult Call1;
        public CallResult Call2;
        public int Retries;
        public bool Blank => Call1.Outcome != Outcome.True;
        public bool Recoverable => Call2.Success;

        public string Ui => Call1.Outcome == Outcome.True ? "drawn" : Call1.Outcome == Outcome.Throw ? "blank (throws)" : "blank";
        public string OutcomeText(Outcome o) => o == Outcome.True ? "true" : o == Outcome.False ? "false" : "throw";
    }

    static class Program
    {
        static int Main(string[] args)
        {
            if (args.Contains("--help") || args.Contains("-h")) { Usage(); return 0; }

            string dllDir = Value(args, "--dlls") ?? FindUp("dlls");
            string tablePath = Value(args, "--table");
            bool forceFail = args.Contains("--force-fail");

            if (dllDir == null || !File.Exists(Path.Combine(dllDir, "Waylith.dll")))
            {
                Console.WriteLine("[FATAL] Waylith.dll not found; pass --dlls <dir>");
                return 2;
            }

            string dll = Path.Combine(dllDir, "Waylith.dll");
            var asm = Asm.Open(dll);
            var facts = new Facts(asm);

            if (args.Contains("--dump"))
            {
                // small escape hatch used while building the harness, not by CI
                int k = args.ToList().IndexOf("--dump");
                var spec = args[k + 1].Split("::");
                foreach (var mh in asm.Methods(spec[0], spec[1]))
                {
                    Console.WriteLine("--- " + asm.TypeName(mh.GetDeclaringTypeOrSelf(asm.Md)) + "::" + spec[1]);
                    foreach (var ins in asm.Body(mh))
                    {
                        string extra = ins.HasImm ? " " + ins.Imm : "";
                        if (ins.HasToken) { try { extra += " " + asm.Describe(System.Reflection.Metadata.Ecma335.MetadataTokens.EntityHandle(ins.Token)); } catch { } }
                        if (ins.Target >= 0) extra += " -> IL_" + ins.Target.ToString("X4");
                        Console.WriteLine("  IL_" + ins.Offset.ToString("X4") + " " + ins.Name + extra);
                    }
                }
                return 0;
            }

            Console.WriteLine("[INFO] assembly : " + dll);
            Console.WriteLine("[INFO] size     : " + new FileInfo(dll).Length + " bytes");
            Console.WriteLine("[INFO] replica  : offline state machine, no game runtime, no Unity, no GPU");

            try { facts.Check(); }
            catch (Broken b)
            {
                Console.WriteLine("[BROKEN] " + b.Message);
                Console.WriteLine("RESULT: BROKEN (the replica can no longer be tied to this dll)");
                return 2;
            }

            var rows = Enumerate();
            var asserts = new List<(string Id, bool Ok, string Detail)>();

            void Assert(string id, bool ok, string detail) => asserts.Add((id, ok, detail));

            // (1) GpuResource present -> success, unconditionally.
            var gpuRows = rows.Where(r => r.S.Gpu).ToList();
            Assert("A1", gpuRows.Count == 24 && gpuRows.All(r => r.Call1.Outcome == Outcome.True),
                "GpuResource present => true in all " + gpuRows.Count + " states (pending/no-cpu/no-source/miss included)");

            // (2) CpuData null -> false, whatever the source/cache says (no pending build).
            var nullCpu = rows.Where(r => !r.S.Gpu && !r.S.Pending && r.S.Cpu == CpuState.Null).ToList();
            Assert("A2", nullCpu.Count == 4 && nullCpu.All(r => r.Call1.Outcome == Outcome.False),
                "CpuData null + no pending => false in all " + nullCpu.Count + " source/cache combinations");

            // (3) PendingBuild -> this call fails, and exactly one retry recovers it.
            var pending = rows.Where(r => !r.S.Gpu && r.S.Pending).ToList();
            Assert("A3", pending.Count == 12 && pending.All(r => r.Call1.Outcome == Outcome.False && r.Call2.Success && r.Retries == 1),
                "PendingBuild + no GPU => false this call, true next call, 1 retry (" + pending.Count + " states)");

            // (4) The brief's cache case: released CpuData + SourcePath + cache hit is synchronous here.
            var cache = rows.Single(r => !r.S.Gpu && !r.S.Pending && r.S.Cpu == CpuState.Released
                                         && r.S.Source && r.S.Cache == CacheState.Hit);
            Assert("A4", cache.Call1.Outcome == Outcome.True && cache.Retries == 0,
                "released CpuData + SourcePath + cache hit => true on the first call, 0 retries (no pending build)");

            // (5) No source and no usable cache -> permanent blank, retry never helps.
            var permanent = rows.Where(r => !r.S.Gpu && !r.S.Pending && !r.S.Source
                                            && (r.S.Cpu == CpuState.Null || r.S.Cpu == CpuState.Released)).ToList();
            Assert("A5", permanent.Count == 4 && permanent.All(r => !r.Call1.Success && !r.Call2.Success && !r.Recoverable),
                "no GPU + no source + no cache + no pending => false forever (" + permanent.Count + " states)");

            // (6) released data + source + cache miss is a throw, not a false.
            var miss = rows.Single(r => !r.S.Gpu && !r.S.Pending && r.S.Cpu == CpuState.Released
                                        && r.S.Source && r.S.Cache == CacheState.Miss);
            Assert("A6", miss.Call1.Outcome == Outcome.Throw,
                "released CpuData + SourcePath + cache miss => RequireCpuData throws InvalidOperationException");

            // (7) Only pending builds are fail-then-recover.
            var recover = rows.Where(r => r.Call1.Outcome != Outcome.True && r.Call2.Success).ToList();
            Assert("A7", recover.Count == pending.Count && recover.All(r => r.S.Pending && !r.S.Gpu),
                "the only fail->recover class is PendingBuild + no GPU; every other failure is permanent");

            // (8) No state claims success without an allocation.
            Assert("A8", rows.Where(r => r.Call1.Success).All(r => r.Call1.After.Gpu),
                "every true result has a GpuResource after the call");

            // (9) Resolve timing: the same pending state succeeds in-call when the batch can flush.
            var p = new State { Gpu = false, Cpu = CpuState.Null, Pending = true, Source = false, Cache = CacheState.Miss };
            var pNo = Engine.Call(p, canResolve: false);
            var pYes = Engine.Call(p, canResolve: true);
            Assert("A9", pNo.Outcome == Outcome.False && pYes.Outcome == Outcome.True,
                "PendingBuild is not an unconditional failure: false only when Resolve cannot flush this call (off main thread / gate held)");

            // (10) Table size floor.
            Assert("A10", rows.Count == 48 && rows.Count >= 16, "truth table has " + rows.Count + " rows (>= 16)");

            if (forceFail) Assert("DEMO", false, "forced failure (--force-fail): the exit code must be 1");

            foreach (var r in rows)
                Console.WriteLine("[STATE] " + r.S.Key
                    + " gpu=" + (r.S.Gpu ? "yes" : "no")
                    + " cpu=" + r.S.CpuText
                    + " pending=" + (r.S.Pending ? "yes" : "no")
                    + " source=" + (r.S.Source ? "yes" : "no")
                    + " cache=" + (r.S.Cache == CacheState.Hit ? "hit" : "miss")
                    + " call1=" + r.OutcomeText(r.Call1.Outcome) + "/" + r.Call1.Path
                    + " call2=" + r.OutcomeText(r.Call2.Outcome)
                    + " retries=" + (r.Retries == int.MaxValue ? "inf" : r.Retries.ToString())
                    + " ui=" + (r.Blank ? "blank" : "drawn"));

            var table = Render(rows);
            Console.WriteLine(table);

            int drawn = rows.Count(r => !r.Blank);
            int blank = rows.Count - drawn;
            int retry1 = rows.Count(r => r.Retries == 1);
            int forever = rows.Count(r => r.Retries == int.MaxValue);
            Console.WriteLine("[SUMMARY] states=" + rows.Count + " drawn=" + drawn + " blank=" + blank
                              + " blank-1-retry=" + retry1 + " blank-permanent=" + forever
                              + " throws=" + rows.Count(r => r.Call1.Outcome == Outcome.Throw));

            foreach (var a in asserts)
                Console.WriteLine("[ASSERT] " + a.Id + " " + (a.Ok ? "PASS" : "FAIL") + " " + a.Detail);

            Console.WriteLine("[FINDING] released-cpu+cache-hit: true in 1 call, 0 retries — the shipped IL does not fail here; "
                              + "the only fail->recover class is a PendingBuild that Resolve could not flush (1 retry)");
            Console.WriteLine("[FINDING] PendingBuild: `this call fails` only off the main thread or with the batch gate held; "
                              + "on the main thread Resolve flushes and the same state returns true in-call");

            bool ok = asserts.All(a => a.Ok);
            foreach (var a in asserts.Where(a => !a.Ok))
                Console.WriteLine("[FAIL] " + a.Id + " " + a.Detail);
            Console.WriteLine(ok ? "[PASS] all " + asserts.Count + " assertions" : "[FAIL] " + asserts.Count(a => !a.Ok) + " assertion(s)");
            Console.WriteLine(ok ? "RESULT: PASS" : "RESULT: FAIL");

            if (!string.IsNullOrEmpty(tablePath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(tablePath)));
                File.WriteAllText(tablePath, table);
                Console.WriteLine("[INFO] table written: " + tablePath + " (" + Encoding.UTF8.GetByteCount(table) + " bytes)");
            }

            return ok ? 0 : 1;
        }

        static List<Row> Enumerate()
        {
            var rows = new List<Row>();
            int n = 0;
            foreach (bool gpu in new[] { false, true })
            foreach (CpuState cpu in new[] { CpuState.Null, CpuState.Released, CpuState.HasData })
            foreach (bool pending in new[] { false, true })
            foreach (bool source in new[] { false, true })
            foreach (CacheState cache in new[] { CacheState.Miss, CacheState.Hit })
            {
                var s = new State { Gpu = gpu, Cpu = cpu, Pending = pending, Source = source, Cache = cache };
                var c1 = Engine.Call(s, canResolve: false);     // this frame: no flush (worst case)
                var c2 = Engine.Call(c1.After, canResolve: true); // next frame: main thread can flush
                rows.Add(new Row
                {
                    N = ++n,
                    S = s,
                    Call1 = c1,
                    Call2 = c2,
                    Retries = c1.Success ? 0 : c2.Success ? 1 : int.MaxValue
                });
            }
            return rows;
        }

        static string Render(List<Row> rows)
        {
            var sb = new StringBuilder();
            sb.AppendLine("### EnsureResident truth table — dlls/Waylith.dll (1.7.9795.32073)");
            sb.AppendLine();
            sb.AppendLine("`call-1` is this frame with `TextureBuildBatch.Resolve` unable to flush (off the main thread or the batch gate held);");
            sb.AppendLine("`call-2` is the next frame, main thread, gate free. `retries` counts additional calls before a draw.");
            sb.AppendLine();
            sb.AppendLine("| # | GpuResource | CpuData | PendingBuild | SourcePath | Cache | call-1 | call-1 path | call-2 | recoverable | retries | on screen |");
            sb.AppendLine("|---:|---|---|---|---|---|---|---|---|---|---:|---|");
            foreach (var r in rows)
            {
                sb.Append("| ").Append(r.N)
                  .Append(" | ").Append(r.S.Gpu ? "yes" : "no")
                  .Append(" | ").Append(r.S.CpuText)
                  .Append(" | ").Append(r.S.Pending ? "yes" : "no")
                  .Append(" | ").Append(r.S.Source ? "yes" : "no")
                  .Append(" | ").Append(r.S.Source ? (r.S.Cache == CacheState.Hit ? "hit" : "miss") : "n/a")
                  .Append(" | ").Append(r.OutcomeText(r.Call1.Outcome))
                  .Append(" | ").Append("`" + r.Call1.Path + "`")
                  .Append(" | ").Append(r.OutcomeText(r.Call2.Outcome))
                  .Append(" | ").Append(r.Recoverable ? "yes" : "no")
                  .Append(" | ").Append(r.Retries == int.MaxValue ? "∞" : r.Retries.ToString())
                  .Append(" | ").Append(r.Ui)
                  .AppendLine(" |");
            }
            sb.AppendLine();
            sb.AppendLine("`Cache = n/a` means SourcePath is null: the cache is keyed by SourcePath, so a hit cannot exist and the row behaves as a miss.");
            sb.AppendLine();
            sb.AppendLine("#### Cache sub-branches (CpuData released, SourcePath present)");
            sb.AppendLine();
            sb.AppendLine("| cache | TryUploadFromCache path | result |");
            sb.AppendLine("|---|---|---|");
            sb.AppendLine("| hit, format == CpuData.Format | BuildCacheUploads -> UploadLevels | true, 0 retries |");
            sb.AppendLine("| hit, format != CpuData.Format | TryUploadFromSource -> RequireCpuData reloads from cache -> UploadCpuData | true, 0 retries |");
            sb.AppendLine("| miss | TryUploadFromSource -> RequireCpuData -> TryLoadTextureFromCache fails | throws InvalidOperationException |");
            sb.AppendLine();
            sb.AppendLine("#### Blank classes (call-1 does not draw)");
            sb.AppendLine();
            sb.AppendLine("| class | states | retries | why nothing is drawn |");
            sb.AppendLine("|---|---:|---|---|");
            var pendingBlank = rows.Where(r => r.Blank && !r.S.Gpu && r.S.Pending).ToList();
            var nullBlank = rows.Where(r => r.Blank && !r.S.Gpu && !r.S.Pending && r.S.Cpu == CpuState.Null).ToList();
            var noSourceBlank = rows.Where(r => r.Blank && !r.S.Gpu && !r.S.Pending && r.S.Cpu == CpuState.Released && !r.S.Source).ToList();
            var missBlank = rows.Where(r => r.Blank && !r.S.Gpu && !r.S.Pending && r.S.Cpu == CpuState.Released && r.S.Source).ToList();
            sb.AppendLine("| PendingBuild, no GPU | " + pendingBlank.Count + " | 1 | a build job owns the upload; EnsureResident returns false before CPU data is consulted |");
            sb.AppendLine("| CpuData null, no GPU, no pending | " + nullBlank.Count + " | ∞ | field is null; the first branch after the GPU check returns false, source/cache are never read |");
            sb.AppendLine("| CpuData released, no source, no GPU | " + noSourceBlank.Count + " | ∞ | TryUploadFromCache has no key: no SourcePath, so it returns false |");
            sb.AppendLine("| CpuData released, source, cache miss | " + missBlank.Count + " | ∞ | RequireCpuData cannot reload and throws InvalidOperationException |");
            sb.AppendLine();
            sb.AppendLine("A non-blank state needs one of: a live `GpuResource`, `CpuData.HasData`, or a cache hit with a `SourcePath`.");
            sb.AppendLine();
            sb.AppendLine("#### Resolve timing (same state, different call site)");
            sb.AppendLine();
            sb.AppendLine("| GpuResource | PendingBuild | Resolve can flush in-call | result |");
            sb.AppendLine("|---|---|---|---|");
            sb.AppendLine("| no | yes | no (off main thread / gate held) | false, 1 retry |");
            sb.AppendLine("| no | yes | yes (main thread, gate free) | true, 0 retries |");
            sb.AppendLine("| yes | yes | no | true (existing allocation is returned) |");
            sb.AppendLine("| yes | yes | yes | true |");
            return sb.ToString();
        }

        static string Value(string[] args, string name)
        {
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        static string FindUp(string dir)
        {
            string d = Directory.GetCurrentDirectory();
            for (int i = 0; i < 6 && d != null; i++)
            {
                string p = Path.Combine(d, dir);
                if (Directory.Exists(p)) return p;
                d = Path.GetDirectoryName(d);
            }
            return null;
        }

        static void Usage()
        {
            Console.WriteLine("residency — offline state machine for Waylith.Rendering.Texture2D.EnsureResident");
            Console.WriteLine("  --dlls <dir>     directory containing Waylith.dll (default: nearest dlls/)");
            Console.WriteLine("  --table <path>   write the markdown truth table");
            Console.WriteLine("  --force-fail     add a failing assertion (control run; exit 1)");
            Console.WriteLine("  --dump Type::M   print one method body and exit");
            Console.WriteLine("exit: 0 pass, 1 assertion failed, 2 harness cannot vouch for the dll");
        }
    }

    static class HandleExt
    {
        public static System.Reflection.Metadata.TypeDefinitionHandle GetDeclaringTypeOrSelf(
            this System.Reflection.Metadata.MethodDefinitionHandle h, System.Reflection.Metadata.MetadataReader md)
            => md.GetMethodDefinition(h).GetDeclaringType();
    }
}
