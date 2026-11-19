using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace IconFlow
{
    /// <summary>
    /// "def -> icon texture" as an assertable function.
    ///
    ///   fixtures/*.png.dds  ->  ContentFinder stub  ->  Graphic_Collection.Init replica
    ///                        ->  subGraphics[]      ->  SubGraphicFor(thingIDNumber)
    ///                        ->  MatAt(rot).mainTexture  ->  one texture name
    ///
    /// Exit code is the result: 0 pass, 1 assertion failed, 2 harness could not vouch (BROKEN).
    /// </summary>
    static class Program
    {
        const int ThingIds = 1000;   // thingIDNumber 0..999

        sealed class Scenario
        {
            public string Id;
            public string Folder;     // req.path passed to Init
            public string Dir;        // fixture directory with the real texture file names
            public string Note;
        }

        sealed class Row
        {
            public Scenario S;
            public NameContract Naming;
            public InitResult Init;
            public int MaskVariants;
            public int MaskHits;
            public double MaskRatio => MaskHits / (double)ThingIds;
            public bool PathsDistinct => Init.SubGraphics.Where(v => v.Path != null).Select(v => v.Path).Distinct(StringComparer.Ordinal).Count()
                                         == Init.SubGraphics.Count(v => v.Path != null);
        }

        static int Main(string[] args)
        {
            if (args.Contains("--help") || args.Contains("-h")) { Usage(); return 0; }

            string dllDir = Value(args, "--dlls") ?? FindUp("dlls");
            string fixtureRoot = Value(args, "--fixtures") ?? FindFlowFixtures();
            string tablePath = Value(args, "--table");
            bool forceFail = args.Contains("--force-fail");

            if (dllDir == null || !File.Exists(Path.Combine(dllDir, "Assembly-CSharp.dll")))
            {
                Console.WriteLine("[FATAL] dlls/Assembly-CSharp.dll not found; pass --dlls <dir>");
                return 2;
            }
            if (fixtureRoot == null || !Directory.Exists(fixtureRoot))
            {
                Console.WriteLine("[FATAL] flow/fixtures not found; pass --fixtures <dir>");
                return 2;
            }

            string asm = Path.Combine(dllDir, "Assembly-CSharp.dll");
            if (args.Contains("--dump"))
            {
                var dd = Dll.Open(asm);
                var spec = args[args.Length - 1].Split("::");
                foreach (var mh in dd.Methods(spec[0], spec[1]))
                {
                    Console.WriteLine("--- " + dd.MethodSignature(mh));
                    foreach (var ins in dd.Body(mh))
                    {
                        string extra = "";
                        if (ins.HasImm) extra += " " + ins.Imm;
                        if (ins.HasToken) extra += " " + ((ins.Token >> 24) == 0x70 ? "\"" + dd.UserString(ins.Token) + "\"" : dd.Describe(System.Reflection.Metadata.Ecma335.MetadataTokens.EntityHandle(ins.Token)));
                        Console.WriteLine("  IL_" + ins.Offset.ToString("X4") + " " + ins.Name + extra);
                    }
                }
                return 0;
            }
            Console.WriteLine("[INFO] assembly : " + asm);
            Console.WriteLine("[INFO] fixtures : " + fixtureRoot);

            FlowFacts facts;
            try { facts = FlowFacts.Read(asm); }
            catch (Exception ex)
            {
                Console.WriteLine("[BROKEN] could not read facts from the assembly: " + ex);
                Console.WriteLine("RESULT: BROKEN");
                return 2;
            }

            Console.WriteLine("[INFO] version  : " + facts.AssemblyVersion);
            foreach (var e in facts.Evidence) Console.WriteLine("[FACT] " + e);
            foreach (var p in facts.Problems) Console.WriteLine("[BROKEN] " + p);
            if (!facts.Ok)
            {
                Console.WriteLine();
                Console.WriteLine("[SUMMARY] pass=0 fail=0 broken=" + facts.Problems.Count);
                Console.WriteLine("RESULT: BROKEN (the replica's assumptions no longer match the dll)");
                return 2;
            }

            var scenarios = new[]
            {
                new Scenario { Id = "revolver", Folder = "Things/Weapon/Ranged/UniqueRevolver", Dir = Path.Combine(fixtureRoot, "revolver"),
                               Note = "5 unique variants A..E, each with a _m mask" },
                new Scenario { Id = "lynx", Folder = "Things/Animal/Lynx", Dir = Path.Combine(fixtureRoot, "lynx"),
                               Note = "one per-direction pair: Lynx_north + Lynx_northm" },
            };
            var modes = new[] { NameContract.GameContract, NameContract.PortLeak };

            Console.WriteLine();
            var rows = new List<Row>();
            foreach (var s in scenarios)
            {
                Console.WriteLine("[SCENARIO] " + s.Id + "  req.path=" + s.Folder + "  (" + s.Note + ")");
                foreach (var m in modes)
                {
                    var finder = FolderContentFinder.Load(s.Dir, m);
                    var init = GraphicCollectionInit.Init(s.Folder, finder.GetAllInFolder(s.Folder), facts, finder);

                    var row = new Row { S = s, Naming = m, Init = init };
                    row.MaskVariants = init.SubGraphics.Count(v => v.IsMaskFlavoured);

                    for (int id = 0; id < ThingIds; id++)
                    {
                        string tex = IconChain.IconTextureNameFor(init, id);
                        if (TextureNaming.IsMask(tex)) row.MaskHits++;
                    }

                    rows.Add(row);
                    Console.WriteLine("  " + Naming(m) + ": files=" + finder.FileCount
                                      + " afterMaskFilter=" + init.AfterMaskFilter
                                      + " subGraphics=" + init.SubGraphics.Count
                                      + " maskHits=" + row.MaskHits + "/" + ThingIds);
                }
            }

            string table = Table(rows, facts);
            Console.WriteLine();
            Console.WriteLine(table);
            if (!string.IsNullOrEmpty(tablePath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(tablePath)));
                File.WriteAllText(tablePath, table);
                Console.WriteLine("[INFO] table written to " + tablePath);
            }

            return Assert(rows, forceFail);
        }

        static string Naming(NameContract c) => c == NameContract.GameContract ? "game-contract" : "port-leak(.png)";

        static string Table(List<Row> rows, FlowFacts facts)
        {
            var sb = new StringBuilder();
            sb.AppendLine("## chain: fixture dir -> ContentFinder stub -> Graphic_Collection.Init -> SubGraphicFor -> mainTexture");
            sb.AppendLine();
            sb.AppendLine("mask suffix \"_m\" and the grouping/quote characters are read from `" + Path.GetFileName(facts.DllPath) + "` (see [FACT] lines).");
            sb.AppendLine();
            sb.AppendLine("| scenario | texture names | files | after `_m` filter | groups | subGraphics | mask-flavoured variants | mask hits / " + ThingIds + " | mask ratio | variant paths distinct |");
            sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|");
            foreach (var r in rows)
            {
                sb.AppendLine("| " + r.S.Id + " | " + Naming(r.Naming) + " | " + r.Init.FilesInFolder + " | " + r.Init.AfterMaskFilter
                              + " | " + r.Init.Groups + " | " + r.Init.SubGraphics.Count + " | " + r.MaskVariants
                              + " | " + r.MaskHits + " | " + r.MaskRatio.ToString("0.0%") + " | " + (r.PathsDistinct ? "yes" : "NO") + " |");
            }
            sb.AppendLine();
            sb.AppendLine("### subGraphics / icon texture per variant");
            sb.AppendLine();
            sb.AppendLine("| scenario | texture names | # | kind | req.path + \"/\" + name | icon texture (north) | mask probe (path + \"_m\") |");
            sb.AppendLine("|---|---|---|---|---|---|---|");
            foreach (var r in rows)
            {
                for (int i = 0; i < r.Init.SubGraphics.Count; i++)
                {
                    var v = r.Init.SubGraphics[i];
                    sb.AppendLine("| " + r.S.Id + " | " + Naming(r.Naming) + " | " + i + " | " + v.Kind + " | `" + v.Path + "` | `"
                                  + v.TextureName + "`" + (v.IsMaskFlavoured ? " **(mask)**" : "") + " | `" + (v.MaskPath ?? "null") + "` |");
                }
            }
            return sb.ToString();
        }

        static int Assert(List<Row> rows, bool forceFail)
        {
            var checks = new List<(string id, string what, bool ok)>();
            void Check(string id, string what, bool ok) => checks.Add((id, what, ok));

            Row R(string scenario, NameContract n) => rows.First(r => r.S.Id == scenario && r.Naming == n);
            var revClean = R("revolver", NameContract.GameContract);
            var revLeak = R("revolver", NameContract.PortLeak);
            var lynxClean = R("lynx", NameContract.GameContract);
            var lynxLeak = R("lynx", NameContract.PortLeak);

            // (0) the fixtures must really be the port's file names, not a cleaned-up stand-in
            var disk = Directory.GetFiles(revClean.S.Dir).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToList();
            Check("A0", "fixtures are the real double-extension names: 10 files, all *.png.dds",
                  disk.Count == 10 && disk.All(n => n.EndsWith(".png.dds", StringComparison.Ordinal)));

            // (4) the mandated assertions on the correct pipeline
            Check("A1", "revolver / game-contract: subGraphics == 5", revClean.Init.SubGraphics.Count == 5);
            Check("A2", "revolver / game-contract: mask hit rate over 1000 thingIDNumbers == 0", revClean.MaskHits == 0);
            Check("A3", "revolver / game-contract: every variant path is distinct", revClean.PathsDistinct);

            // (5) the negative control: the same code fed the port's names
            Check("A4", "revolver / port-leak: subGraphics == 10 (5 textures + 5 masks)", revLeak.Init.SubGraphics.Count == 10);
            Check("A5", "revolver / port-leak: mask hit rate ~ 50% (500/1000 +-20)", Math.Abs(revLeak.MaskHits - 500) <= 20);
            Check("A6", "revolver / port-leak: every variant path is still distinct (so only the *content* differs)", revLeak.PathsDistinct);

            // the harness can tell the two apart at all
            Check("A7", "control: the same replica yields 5 vs 10 variants for the two name contracts",
                  revClean.Init.SubGraphics.Count != revLeak.Init.SubGraphics.Count);

            // direction-carrying names take the other branch of Init, and are immune to this leak
            Check("A8", "lynx / game-contract: one Graphic_Multi at .../Lynx, 0 masks",
                  lynxClean.Init.SubGraphics.Count == 1 && lynxClean.Init.SubGraphics[0].Kind == GraphicKind.Multi && lynxClean.MaskHits == 0);
            Check("A9", "lynx / port-leak: still one Graphic_Multi, 0 masks (Contains(\"_north\") does not care about \".png\")",
                  lynxLeak.Init.SubGraphics.Count == 1 && lynxLeak.MaskHits == 0);

            if (forceFail)
                Check("C0", "CONTROL --force-fail: this run is meant to be red", false);

            Console.WriteLine("## assertions");
            int fail = 0;
            foreach (var c in checks)
            {
                Console.WriteLine((c.ok ? "[PASS] " : "[FAIL] ") + c.id + "  " + c.what);
                if (!c.ok) fail++;
            }

            Console.WriteLine();
            Console.WriteLine("[SUMMARY] pass=" + (checks.Count - fail) + " fail=" + fail + " broken=0");
            Console.WriteLine("RESULT: " + (fail == 0 ? "PASS" : "FAIL"));
            return fail == 0 ? 0 : 1;
        }

        static string Value(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++) if (args[i] == name) return args[i + 1];
            return null;
        }

        static string FindUp(string leaf)
        {
            foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            {
                string d = start;
                for (int i = 0; i < 6 && !string.IsNullOrEmpty(d); i++)
                {
                    string cand = Path.Combine(d, leaf);
                    if (Directory.Exists(cand)) return cand;
                    d = Path.GetDirectoryName(d);
                }
            }
            return null;
        }

        static string FindFlowFixtures()
        {
            foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            {
                string d = start;
                for (int i = 0; i < 6 && !string.IsNullOrEmpty(d); i++)
                {
                    foreach (var cand in new[] { Path.Combine(d, "flow", "fixtures"), Path.Combine(d, "fixtures") })
                        if (Directory.Exists(cand)) return cand;
                    d = Path.GetDirectoryName(d);
                }
            }
            return null;
        }

        static void Usage()
        {
            Console.WriteLine("IconFlow - def -> icon texture, as functions over the shipped dll.");
            Console.WriteLine("  --dlls <dir>       directory holding Assembly-CSharp.dll (default: nearest dlls/)");
            Console.WriteLine("  --fixtures <dir>   fixture root (default: nearest flow/fixtures)");
            Console.WriteLine("  --table <path>     also write the markdown table here");
            Console.WriteLine("  --force-fail       add a deliberately failing assertion (exit 1), for CI control runs");
        }
    }
}
