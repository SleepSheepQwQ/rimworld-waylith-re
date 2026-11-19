using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using KTrie;
using UnityEngine;
using Verse;

namespace IconFlowB
{
    /// <summary>
    /// Independent verification of Verse.Graphic_Collection, by RUNNING the game's own
    /// Verse.Graphic_Collection.Init and Verse.Graphic_Random.SubGraphicFor out of
    /// dlls/Assembly-CSharp.dll (the Waylith net10.0 build).
    ///
    /// Nothing is patched or re-implemented. The harness only seeds the game's own
    /// content plumbing with synthetic assets:
    ///
    ///     LoadedModManager.runningMods      = [ one fake ModContentPack ]
    ///     ModContentPack.textures           = ModContentHolder&lt;Texture2D&gt;
    ///     ModContentHolder.contentList      = { "/folder/Name" -> Texture2D(Name) }
    ///     ModContentHolder.contentListTrie  = same keys
    ///     UnityData.mainThreadId            = current thread
    ///
    /// After that, ContentFinder&lt;Texture2D&gt;.GetAllInFolder / .Get, the grouping,
    /// sorting, mask-suffix filtering, direction culling, maskPath probing, the
    /// GraphicDatabase material construction and the thingIDNumber modulo selection all
    /// execute as real compiled code. The only variable is the file naming convention
    /// handed to the folder.
    /// </summary>
    static class Program
    {
        static string DllDir;

        static int Main(string[] args)
        {
            DllDir = args.FirstOrDefault(a => !a.StartsWith("--")) ?? FindDllDir();
            if (DllDir == null)
            {
                Console.WriteLine("[FAIL] could not locate dlls/ (pass the directory as argv[0])");
                return 2;
            }
            AppDomain.CurrentDomain.AssemblyResolve += Resolve;

            // UnityData's static ctor warns through Verse.Log when it is used before
            // UnityDataInitializer has run, and Verse.Log cannot start without the
            // engine. Flipping this flag takes the quiet branch.
            SetS(typeof(UnityDataInitializer), "initializing", true);

            Console.WriteLine($"[INFO] Assembly-CSharp {typeof(Graphic_Collection).Assembly.GetName().Version} @ {typeof(Graphic_Collection).Assembly.Location}");
            Console.WriteLine($"[INFO] Graphic_Single.MaskSuffix = '{Graphic_Single.MaskSuffix}'");

            var results = new List<Result>
            {
                Run(Scenario.Gun("clean")),
                Run(Scenario.Gun("ext")),
                Run(Scenario.Animal("clean")),
                Run(Scenario.Animal("ext")),
            };

            Console.WriteLine();
            Console.WriteLine("## table");
            Console.WriteLine("| input naming | subject | files | subGraphics | variant paths | mask-flavoured variants | mask hits / 1000 |");
            Console.WriteLine("|---|---|---|---|---|---|---|");
            foreach (var r in results)
                Console.WriteLine($"| {r.Convention} | {r.Subject} | {r.Files} | {r.Variants} | {string.Join("<br>", r.Paths.Select(Esc))} | {r.MaskVariants} | {r.MaskHits} |");

            Console.WriteLine();
            return AssertAll(results);
        }

        static string Esc(string s) => s.Replace("|", "\\|");

        // ---------------------------------------------------------------- scenarios

        sealed class Scenario
        {
            public string Subject;
            public string Convention;
            public string Folder;
            public string[] Files;

            public static Scenario Gun(string convention)
            {
                var stems = new[] { "UniqueRevolverA", "UniqueRevolverB", "UniqueRevolverC", "UniqueRevolverD", "UniqueRevolverE" };
                var names = stems.Concat(stems.Select(s => s + "_m")).ToArray();
                return new Scenario
                {
                    Subject = "gun",
                    Convention = convention,
                    // distinct folder per convention: GraphicDatabase caches by path
                    Folder = convention == "clean" ? "/Things/Weapons/UniqueRevolver" : "/Things/Weapons/ExtUniqueRevolver",
                    Files = convention == "clean" ? names : names.Select(n => n + ".png").ToArray(),
                };
            }

            public static Scenario Animal(string convention)
            {
                var names = new[] { "Lynx", "Lynx_north", "Lynx_northm" };
                return new Scenario
                {
                    Subject = "animal",
                    Convention = convention,
                    Folder = convention == "clean" ? "/Things/Animal/Lynx" : "/Things/Animal/ExtLynx",
                    Files = convention == "clean" ? names : names.Select(n => n + ".png").ToArray(),
                };
            }
        }

        sealed class Result
        {
            public string Subject, Convention;
            public int Files, Variants, MaskVariants, MaskHits, PhantomVariants;
            public List<string> Paths = new();
            public List<string> MaskPaths = new();
            public string Error;
            public Dictionary<string, int> Histogram = new();
        }

        static Result Run(Scenario sc)
        {
            var r = new Result { Subject = sc.Subject, Convention = sc.Convention, Files = sc.Files.Length };
            Console.WriteLine($"\n===== {sc.Subject} / {sc.Convention} =====");
            Console.WriteLine($"[INFO] folder '{sc.Folder}', {sc.Files.Length} files: {string.Join(", ", sc.Files)}");

            InstallContent(sc.Folder, sc.Files);
            var knownPaths = new HashSet<string>(sc.Files.Select(n => sc.Folder + "/" + n), StringComparer.Ordinal);
            try { GraphicDatabase.Clear(); } catch (Exception ex) { Console.WriteLine($"[WARN] GraphicDatabase.Clear: {ex.GetType().Name}"); }

            var shader = (Shader)RuntimeHelpers.GetUninitializedObject(typeof(Shader));
            SeedGraphicCache(sc.Folder, sc.Files, shader);

            var g = (Graphic_Collection)RuntimeHelpers.GetUninitializedObject(typeof(Graphic_Random));
            var req = MakeRequest(typeof(Graphic_Random), sc.Folder, shader);

            try
            {
                g.Init(req);                       // <-- the real method
            }
            catch (Exception ex)
            {
                var inner = (ex as TargetInvocationException)?.InnerException ?? ex;
                r.Error = inner.GetType().Name + ": " + inner.Message;
                Console.WriteLine($"[ERROR] Init threw {r.Error}");
                Console.WriteLine(inner.StackTrace);
                return r;
            }

            var subs = (Graphic[])typeof(Graphic_Collection)
                .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .First(f => f.Name == "subGraphics")
                .GetValue(g);
            r.Variants = subs?.Length ?? 0;
            foreach (var s in subs ?? Array.Empty<Graphic>())
                r.Paths.Add(s?.path ?? "<null>");

            Console.WriteLine($"[RESULT] subGraphics = {r.Variants}");
            for (int i = 0; i < r.Paths.Count; i++)
            {
                bool mask = IsMaskName(r.Paths[i]);
                if (mask) r.MaskPaths.Add(r.Paths[i]);
                bool phantom = !knownPaths.Contains(r.Paths[i]);
                if (phantom) r.PhantomVariants++;
                Console.WriteLine($"   [{i}] {subs[i]?.GetType().Name,-14} {r.Paths[i]}   maskPath={subs[i]?.maskPath ?? "<null>"}" +
                                  (mask ? "   <-- MASK TEXTURE" : "") + (phantom ? "   <-- NOT IN THE FOLDER" : ""));
            }
            r.MaskVariants = r.MaskPaths.Count;

            // thingIDNumber 0..999 through the real Graphic_Random.SubGraphicFor
            var rnd = (Graphic_Random)g;
            var idField = typeof(Thing).GetField("thingIDNumber", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            for (int id = 0; id < 1000; id++)
            {
                Graphic picked;
                try
                {
                    var thing = (Thing)RuntimeHelpers.GetUninitializedObject(typeof(Thing));
                    idField.SetValue(thing, id);
                    picked = rnd.SubGraphicFor(thing);
                }
                catch (Exception ex)
                {
                    r.Error = "SubGraphicFor: " + ((ex as TargetInvocationException)?.InnerException ?? ex).GetType().Name;
                    break;
                }
                int idx = Array.IndexOf(subs, picked);
                string path = idx >= 0 ? subs[idx].path : "<unknown>";
                r.Histogram[path] = r.Histogram.TryGetValue(path, out var c) ? c + 1 : 1;
                if (IsMaskName(path)) r.MaskHits++;
            }
            Console.WriteLine($"[RESULT] mask-flavoured variants = {r.MaskVariants}; mask hits over thingIDNumber 0..999 = {r.MaskHits}");
            foreach (var kv in r.Histogram.OrderByDescending(k => k.Value))
                Console.WriteLine($"   {(IsMaskName(kv.Key) ? "MASK " : "     ")}{kv.Value,4}x  {kv.Key}");
            return r;
        }

        static bool IsMaskName(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            var b = path.Substring(path.LastIndexOf('/') + 1);
            return b.EndsWith("_m", StringComparison.Ordinal) || b.EndsWith("_m.png", StringComparison.Ordinal);
        }

        // ---------------------------------------------------------------- assertions

        static int AssertAll(List<Result> rs)
        {
            int fail = 0;
            void Check(bool ok, string what)
            {
                Console.WriteLine($"{(ok ? "[PASS]" : "[FAIL]")} {what}");
                if (!ok) fail++;
            }

            var gunClean = rs[0]; var gunExt = rs[1];
            var aniClean = rs[2]; var aniExt = rs[3];

            Check(gunClean.Error == null, $"gun/clean: Init completes ({gunClean.Error ?? "ok"})");
            Check(gunClean.Variants == 5, $"gun/clean: subGraphics == 5 (got {gunClean.Variants})");
            Check(gunClean.MaskHits == 0, $"gun/clean: 0 mask selections in 1000 (got {gunClean.MaskHits})");
            Check(gunClean.MaskVariants == 0, $"gun/clean: no mask texture among variants (got {gunClean.MaskVariants})");

            Check(gunExt.Error == null, $"gun/ext: Init completes ({gunExt.Error ?? "ok"})");
            Check(gunExt.Variants == 10, $"gun/ext: subGraphics == 10 (got {gunExt.Variants})");
            Check(Math.Abs(gunExt.MaskHits - 500) <= 5, $"gun/ext: ~500 mask selections in 1000 (got {gunExt.MaskHits})");
            Check(gunExt.MaskVariants == 5, $"gun/ext: 5 mask textures among the variants (got {gunExt.MaskVariants})");

            Check(aniClean.Error == null && aniExt.Error == null, $"animal: Init completes in both namings ({aniClean.Error ?? "-"} / {aniExt.Error ?? "-"})");
            Check(aniClean.MaskHits == 0, $"animal/clean: 0 mask selections (got {aniClean.MaskHits})");
            Check(aniExt.MaskHits == 0, $"animal/ext: 0 mask selections (got {aniExt.MaskHits})");
            Check(aniClean.MaskVariants == 0 && aniExt.MaskVariants == 0,
                $"animal: the _north suffix keeps the mask out of the variant list (clean {aniClean.MaskVariants}, ext {aniExt.MaskVariants})");
            Check(aniClean.PhantomVariants == 0, $"animal/clean: every variant resolves to a real file (phantom {aniClean.PhantomVariants})");
            Check(aniExt.PhantomVariants == 1,
                $"animal/ext: the _north group still emits a Multi at the extension-less key (phantom {aniExt.PhantomVariants})");

            Console.WriteLine($"RESULT: {(fail == 0 ? "PASS" : "FAIL")} ({fail} failing assertions)");
            return fail == 0 ? 0 : 1;
        }

        // ---------------------------------------------------------------- content seeding

        static void InstallContent(string folder, string[] files)
        {
            var mod = (ModContentPack)RuntimeHelpers.GetUninitializedObject(typeof(ModContentPack));
            var holder = (ModContentHolder<Texture2D>)RuntimeHelpers.GetUninitializedObject(typeof(ModContentHolder<Texture2D>));

            var dict = new Dictionary<string, Texture2D>(StringComparer.Ordinal);
            var trie = new StringTrieSet();
            foreach (var name in files)
            {
                var tex = (Texture2D)RuntimeHelpers.GetUninitializedObject(typeof(Texture2D));
                tex.name = name;
                var key = folder + "/" + name;
                dict[key] = tex;
                trie.Add(key);
            }

            SetF(holder, "mod", mod);
            SetF(holder, "contentList", dict);
            SetF(holder, "contentListTrie", trie);
            SetF(mod, "nameInt", "IconFlowBFakeMod");
            SetF(mod, "packageIdInt", "iconsheep.flowb.fake");
            SetF(mod, "packageIdPlayerFacingInt", "IconFlowBFakeMod");
            SetF(mod, "loadOrder", 0);
            SetF(mod, "textures", holder);
            SetF(mod, "rootDirInt", new DirectoryInfo(AppContext.BaseDirectory));
            SetF(mod, "loadOrder", 0);
            SetF(mod, "official", false);
            // the tail of ContentFinder.GetAllInFolder walks each mod's asset bundles;
            // an empty handler keeps that loop at zero iterations instead of a null deref
            var bundles = RuntimeHelpers.GetUninitializedObject(typeof(ModAssetBundlesHandler));
            SetF(bundles, "mod", mod);
            SetEmptyList(bundles, "loadedAssetBundles");
            SetF(mod, "assetBundles", bundles);

            SetS(typeof(LoadedModManager), "runningMods", new List<ModContentPack> { mod });
            SetS(typeof(UnityData), "mainThreadId", Thread.CurrentThread.ManagedThreadId);
        }

        /// <summary>
        /// GraphicDatabase.GetInner looks the request up in GraphicDatabase.allGraphics
        /// before constructing anything, and construction is the one step that cannot
        /// run headless (new Material(shader) needs the Vulkan device). So we pre-seed
        /// that cache with stand-in Graphics for every path the folder could plausibly
        /// produce - each file name, its extension-less form, and its "_"-stem - for
        /// both Graphic_Single and Graphic_Multi. Seeding is deliberately a superset:
        /// it never decides which variant wins, only what object a hit returns.
        /// </summary>
        static void SeedGraphicCache(string folder, string[] files, Shader shader)
        {
            var cache = new Dictionary<GraphicRequest, Graphic>();
            var paths = new HashSet<string>(StringComparer.Ordinal);
            foreach (var f in files)
            {
                foreach (var candidate in new[] { f, StripExt(f) })
                {
                    if (string.IsNullOrEmpty(candidate)) continue;
                    paths.Add(folder + "/" + candidate);
                    paths.Add(folder + "/" + candidate.Split('_')[0]);
                }
            }

            foreach (var path in paths)
                foreach (var cls in new[] { typeof(Graphic_Single), typeof(Graphic_Multi) })
                {
                    var key = MakeRequest(cls, path, shader);
                    var g = (Graphic)RuntimeHelpers.GetUninitializedObject(cls);
                    g.path = path;
                    g.maskPath = key.maskPath;
                    g.color = key.color;
                    g.colorTwo = key.colorTwo;
                    g.drawSize = key.drawSize;
                    g.data = key.graphicData;
                    cache[key] = g;
                }

            var f2 = typeof(GraphicDatabase).GetField("allGraphics", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            Console.WriteLine($"[INFO] seeded {cache.Count} graphic-cache keys into GraphicDatabase.{f2.Name} ({f2.FieldType.Name})");
            f2.SetValue(null, cache);
        }

        static string StripExt(string n)
        {
            int i = n.LastIndexOf('.');
            return i > 0 ? n.Substring(0, i) : n;
        }

        static GraphicRequest MakeRequest(Type cls, string path, Shader shader)
        {
            var r = default(GraphicRequest);
            r.graphicClass = cls;
            r.path = path;
            r.maskPath = null;
            r.shader = shader;
            r.drawSize = Vector2.one;
            r.color = Color.white;
            r.colorTwo = Color.white;
            r.renderQueue = 0;
            return r;
        }

        static void SetEmptyList(object o, string name)
        {
            var f = o.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    ?? throw new MissingFieldException(o.GetType().FullName, name);
            var elem = f.FieldType.GetGenericArguments()[0];
            f.SetValue(o, Activator.CreateInstance(typeof(List<>).MakeGenericType(elem)));
        }

        static void SetF(object o, string name, object v)
        {
            var f = o.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    ?? throw new MissingFieldException(o.GetType().FullName, name);
            f.SetValue(o, v);
        }

        static void SetS(Type t, string name, object v)
        {
            var f = t.GetField(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                    ?? throw new MissingFieldException(t.FullName, name);
            f.SetValue(null, v);
        }

        // ---------------------------------------------------------------- plumbing

        static string FindDllDir()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            for (int i = 0; i < 8 && d != null; i++, d = d.Parent)
            {
                var c = Path.Combine(d.FullName, "dlls");
                if (File.Exists(Path.Combine(c, "Assembly-CSharp.dll"))) return c;
            }
            return null;
        }

        static Assembly Resolve(object s, ResolveEventArgs e)
        {
            var p = Path.Combine(DllDir, new AssemblyName(e.Name).Name + ".dll");
            return File.Exists(p) ? Assembly.LoadFrom(p) : null;
        }
    }
}
