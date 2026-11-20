using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Xml.Linq;
using UnityEngine;
using Verse;

namespace IconExec
{
    /// <summary>
    /// The icon chain of a unique weapon, EXECUTED against the shipped dll.
    ///
    /// Nothing in this file re-implements the game. The harness:
    ///
    ///   1. loads dlls/Assembly-CSharp.dll (Waylith net10.0 build) and reads two real ThingDefs
    ///      out of iconexec/data/Weapons_Unique.xml (verbatim Odyssey data from the device);
    ///   2. seeds the game's own content tables (LoadedModManager.runningMods ->
    ///      ModContentPack.textures -> ModContentHolder&lt;Texture2D&gt;) with one Texture2D per real
    ///      bundle entry, at the real dimensions from iconexec/data/unique_weapons_textures.json;
    ///   3. builds a GraphicRequest out of the def's own &lt;graphicClass&gt; / &lt;texPath&gt; /
    ///      &lt;shaderType&gt; and calls the real Graphic_Collection.Init(GraphicRequest);
    ///   4. calls the real Graphic_Random.SubGraphicFor(Thing) for thingIDNumber 0..999;
    ///   5. calls the real Verse.Widgets.GetIconFor(Thing, Rect, ...) and reads back the Texture2D.
    ///
    /// UnityEngine is the only stand-in (Stub/), because the shipped UnityStubs needs
    /// Brutal.Vulkan + the shader source tree - iconexec/UnityProbe reproduces both failures with
    /// their stack traces. Everything above UnityEngine is compiled game code.
    /// </summary>
    static class Program
    {
        const int ExitPass = 0, ExitFail = 1, ExitBroken = 2;
        const int IdSweep = 1000;       // thingIDNumber 0..999
        const int IconCalls = 2000;     // GetIconFor calls per scenario

        static readonly List<string> Failures = new List<string>();
        static readonly StringBuilder Log = new StringBuilder();
        static readonly StringBuilder Rows = new StringBuilder();
        static readonly List<string> Stubbed = new List<string>();

        static string DllDir, DataDir;
        static bool ForceFail;

        // ------------------------------------------------------------------ io

        static void Say(string s)
        {
            Console.WriteLine(s);
            Console.Out.Flush();
            Log.AppendLine(s);
        }

        static string Arg(string[] a, string name)
        {
            for (int i = 0; i < a.Length - 1; i++) if (a[i] == name) return a[i + 1];
            return null;
        }

        static string FindUp(string leaf)
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            for (int i = 0; i < 8 && d != null; i++, d = d.Parent)
            {
                var c = Path.Combine(d.FullName, leaf);
                if (Directory.Exists(c) || File.Exists(c)) return c;
            }
            return null;
        }

        // ------------------------------------------------------------------ entry

        static int Main(string[] argv)
        {
            // On this runtime an unhandled exception aborts the process and loses stdout, so every
            // failure has to be turned into a line of evidence instead.
            try { return RunMain(argv); }
            catch (Exception ex)
            {
                var inner = Unwrap(ex);
                Say("[FATAL] " + inner.GetType().FullName + ": " + inner.Message);
                foreach (var line in Top(inner, 8)) Say("        " + line);
                Say("RESULT: BROKEN");
                return ExitBroken;
            }
        }

        static int RunMain(string[] argv)
        {
            DllDir = Arg(argv, "--dlls") ?? FindUp("dlls");
            DataDir = Arg(argv, "--data") ?? FindUp("iconexec/data");
            string tablePath = Arg(argv, "--table");
            ForceFail = argv.Contains("--force-fail");

            if (DllDir == null || !File.Exists(Path.Combine(DllDir, "Assembly-CSharp.dll")))
            {
                Say("[FAIL] dlls/Assembly-CSharp.dll not found (pass --dlls <dir>)");
                return ExitBroken;
            }
            if (DataDir == null || !File.Exists(Path.Combine(DataDir, "Weapons_Unique.xml")))
            {
                Say("[FAIL] iconexec/data/Weapons_Unique.xml not found (pass --data <dir>)");
                return ExitBroken;
            }
            AppDomain.CurrentDomain.AssemblyResolve += Resolve;

            // ---- 1. the dll's own account of the icon path, asserted before anything runs
            var asm = Asm.Open(Path.Combine(DllDir, "Assembly-CSharp.dll"));
            var bytes = File.ReadAllBytes(Path.Combine(DllDir, "Assembly-CSharp.dll"));
            var facts = new Facts(asm) { Literal = s => HeapHas(bytes, s) };
            facts.Run();
            foreach (string f in facts.Ok) Say("[FACT] " + f);
            foreach (string f in facts.Broken) Say("[BROKEN] " + f);
            if (facts.Broken.Count > 0)
            {
                Say("[SUMMARY] " + facts.Ok.Count + " facts hold, " + facts.Broken.Count + " do not");
                Say("RESULT: BROKEN");
                return ExitBroken;
            }

            // ---- 2. boot the game's own code, head-less
            Boot();

            Say("[INFO] Assembly-CSharp " + typeof(Graphic_Random).Assembly.GetName().Version
                + " @ " + typeof(Graphic_Random).Assembly.Location);
            Say("[INFO] Graphic_Single.MaskSuffix = '" + Graphic_Single.MaskSuffix + "'"
                + ", Graphic_Random : " + typeof(Graphic_Random).BaseType.Name
                + (typeof(Graphic_Random).BaseType.IsAbstract ? " (abstract)" : ""));
            Say("[INFO] UnityEngine is the stub in iconexec/Stub (the shipped one needs Brutal.Vulkan; see UnityProbe)");

            // ---- 3. real defs + real texture inventory
            var defs = LoadDefs();
            var tex = LoadTextures();
            foreach (var d in defs)
                Say($"[INFO] def {d.DefName}: texPath={d.TexPath} graphicClass={d.GraphicClass} shaderType={d.ShaderType}"
                    + $"  ({tex.Count(t => t.Folder == d.TexPath)} textures on disk)");

            // ---- 4. run the chain
            var results = new List<Result>();
            foreach (var def in defs)
                foreach (string mode in new[] { "bundle", "stripped" })
                    results.Add(Run(def, mode, tex));

            // ---- 5. assert
            int fail = AssertAll(results);

            if (ForceFail)
            {
                Say("[SCENARIO] control: a deliberately false assertion (--force-fail)");
                Check("control assertion that must fail", false, "forced by --force-fail");
                fail++;
            }

            WriteTable(results, tablePath);

            Say("[SUMMARY] " + fail + " failed assertion(s); "
                + Texture2D.Allocated + " Texture2D allocated, "
                + Shader.Resolved.Count + " shader resolves, "
                + stubbedSummary());
            Say("RESULT: " + (fail == 0 ? "PASS" : "FAIL"));
            return fail == 0 ? ExitPass : ExitFail;
        }

        static string stubbedSummary()
        {
            var groups = Stubbed.GroupBy(s => s).Select(g => g.Key + " x" + g.Count());
            return string.Join(", ", groups);
        }

        /// <summary>Is this literal in the #US heap? UTF-16LE byte scan of the shipped image.</summary>
        static bool HeapHas(byte[] dll, string s)
        {
            var pat = Encoding.Unicode.GetBytes(s);
            for (int i = 0; i + pat.Length <= dll.Length; i++)
            {
                int j = 0;
                while (j < pat.Length && dll[i + j] == pat[j]) j++;
                if (j == pat.Length) return true;
            }
            return false;
        }

        // ------------------------------------------------------------------ boot

        static void Boot()
        {
            // UnityData's static ctor warns through Verse.Log when used before UnityDataInitializer
            // has run, and Verse.Log cannot start without the engine. This flag takes the quiet path.
            Say("[BOOT] UnityDataInitializer.initializing = true");
            SetS(typeof(UnityDataInitializer), "initializing", true);
            Say("[BOOT] UnityData.mainThreadId = " + Thread.CurrentThread.ManagedThreadId);
            SetS(typeof(UnityData), "mainThreadId", Thread.CurrentThread.ManagedThreadId);

            // Verse.Rand is MurmurHash(seed, iterations) - no Unity RNG, no System.Random. Pinning the
            // seed makes the run reproducible; the icon pick below is the game's own Rand.Range call.
            Say("[BOOT] Rand.seed");
            SetS(typeof(Rand), "seed", 1234567u);   // seed is UInt32; the cctor sets it from DateTime.Now
            SetS(typeof(Rand), "iterations", 0);

            Say("[BOOT] UnityData.IsInMainThread");
            if (!UnityData.IsInMainThread)
                throw new InvalidOperationException("UnityData.IsInMainThread is false; ContentFinder would refuse to work");
        }

        // ------------------------------------------------------------------ fixtures

        sealed class FxDef
        {
            public string DefName, TexPath, GraphicClass, ShaderType;
        }

        sealed class TexEntry
        {
            public string Folder, File;
            public int W, H, Mips, Dxgi;
        }

        /// <summary>Two real ThingDefs, read out of the device's own XML. Fields come from the file.</summary>
        static List<FxDef> LoadDefs()
        {
            var doc = XDocument.Load(Path.Combine(DataDir, "Weapons_Unique.xml"));
            var list = new List<FxDef>();
            foreach (var td in doc.Root.Elements("ThingDef"))
            {
                var gd = td.Element("graphicData");
                list.Add(new FxDef
                {
                    DefName = (string)td.Element("defName"),
                    TexPath = (string)gd?.Element("texPath"),
                    GraphicClass = (string)gd?.Element("graphicClass") ?? "Graphic_Single",
                    ShaderType = (string)gd?.Element("shaderType") ?? "Cutout",
                });
            }
            if (list.Count == 0) throw new InvalidOperationException("no ThingDef in Weapons_Unique.xml");
            return list;
        }

        /// <summary>One row per bundle entry: the folder is the def texPath, w/h/mips/dxgi are the
        /// real header values from the device's asset bundles.</summary>
        static List<TexEntry> LoadTextures()
        {
            using var fs = File.OpenRead(Path.Combine(DataDir, "unique_weapons_textures.json"));
            using var jd = JsonDocument.Parse(fs);
            var list = new List<TexEntry>();
            foreach (var e in jd.RootElement.GetProperty("textures").EnumerateArray())
                list.Add(new TexEntry
                {
                    Folder = e.GetProperty("folder").GetString(),
                    File = e.GetProperty("file").GetString(),
                    W = e.GetProperty("w").GetInt32(),
                    H = e.GetProperty("h").GetInt32(),
                    Mips = e.GetProperty("mips").GetInt32(),
                    Dxgi = e.GetProperty("dxgi").GetInt32(),
                });
            return list;
        }

        // ------------------------------------------------------------------ one scenario

        sealed class Result
        {
            public string Def, Mode, Folder, Error, IconError;
            public int Files, Variants, MaskVariants, MaskHits, IdsSeen;
            public List<string> Paths = new List<string>();
            public List<SubInfo> Subs = new List<SubInfo>();
            public Dictionary<string, int> Histogram = new Dictionary<string, int>();
            public Dictionary<string, int> IconHistogram = new Dictionary<string, int>();
            public int IconCalls, IconMask, IconNull, IconWrongSize;
            public string IconName, IconSize;
            public int DefPathVariants = -1;
            public string DefPathError;
        }

        sealed class SubInfo
        {
            public int Index;
            public string Type, Path, TexName, TexSize;
            public bool IsMask;
        }

        static Result Run(FxDef def, string mode, List<TexEntry> all)
        {
            var r = new Result { Def = def.DefName, Mode = mode, Folder = def.TexPath };
            var entries = all.Where(t => t.Folder == def.TexPath).ToList();
            r.Files = entries.Count;

            Say($"\n===== {def.DefName} / {mode} =====");
            if (entries.Count == 0) { r.Error = "no textures for " + def.TexPath; Say("[ERROR] " + r.Error); return r; }

            // ---- content: the game's own tables, one Texture2D per bundle entry, real size
            InstallContent(def.TexPath, entries, mode);
            try { GraphicDatabase.Clear(); } catch (Exception ex) { Say("[WARN] GraphicDatabase.Clear: " + ex.GetType().Name); }

            // ---- the request, built from the def's own fields
            ThingDef thingDef;
            GraphicRequest req;
            Graphic_Random g;
            try
            {
                var shader = ResolveShader(def);
                thingDef = BuildDef(def);
                req = BuildRequest(def, thingDef, shader);
                Say($"[INFO] request: graphicClass={req.graphicClass.Name} path={req.path} shader={req.shader.name}"
                    + $" drawSize={req.drawSize} maskPath={(req.maskPath ?? "null")} renderQueue={req.renderQueue}");
                g = (Graphic_Random)New(typeof(Graphic_Random));
                g.Init(req);                                  // <-- the real method
            }
            catch (Exception ex)
            {
                var inner = Unwrap(ex);
                r.Error = inner.GetType().FullName + ": " + inner.Message;
                Say("[ERROR] Init threw " + r.Error);
                foreach (var line in Top(inner, 6)) Say("        " + line);
                Diagnose(def, entries, mode);
                return r;
            }

            // ---- subGraphics, and each one's own material/texture
            var field = typeof(Graphic_Collection).GetField("subGraphics", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var subs = (Graphic[])field.GetValue(g);
            r.Variants = subs?.Length ?? 0;
            Say($"[RESULT] subGraphics = {r.Variants}");
            for (int i = 0; i < r.Variants; i++)
            {
                var sg = subs[i];
                var si = new SubInfo { Index = i, Type = sg?.GetType().Name, Path = sg?.path };
                var mat = sg == null ? null : (Material)sg.GetType()
                    .GetField("mat", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(sg);
                var t2 = mat?.mainTexture as Texture2D;
                si.TexName = t2?.name;
                si.TexSize = t2 == null ? "null" : t2.width + "x" + t2.height;
                si.IsMask = IsMask(si.Path);
                r.Paths.Add(si.Path);
                r.Subs.Add(si);
                Say($"   [{i}] {si.Type,-15} {si.Path,-64} texture={si.TexName} {si.TexSize}"
                    + (si.IsMask ? "   <-- MASK TEXTURE" : ""));
            }
            r.MaskVariants = r.Subs.Count(s => s.IsMask);

            // ---- thingIDNumber 0..999 through the real Graphic_Random.SubGraphicFor
            try
            {
                for (int id = 0; id < IdSweep; id++)
                {
                    var picked = g.SubGraphicFor(NewThing(thingDef, id, null));
                    int idx = Array.IndexOf(subs, picked);
                    string p = idx >= 0 ? subs[idx].path : "<unknown>";
                    r.Histogram[p] = r.Histogram.TryGetValue(p, out int c) ? c + 1 : 1;
                    if (IsMask(p)) r.MaskHits++;
                    r.IdsSeen++;
                }
            }
            catch (Exception ex)
            {
                var inner = Unwrap(ex);
                r.Error = "SubGraphicFor: " + inner.GetType().FullName + ": " + inner.Message;
                Say("[ERROR] " + r.Error);
                foreach (var line in Top(inner, 4)) Say("        " + line);
            }
            Say($"[RESULT] thingIDNumber 0..999 -> mask hits {r.MaskHits}/{r.IdsSeen}; "
                + string.Join(", ", r.Histogram.OrderByDescending(k => k.Value).Select(k => Base(k.Key) + "=" + k.Value)));

            // ---- the real Widgets.GetIconFor(Thing, ...)
            try
            {
                var mi = typeof(Widgets).GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .Where(m => m.Name == "GetIconFor")
                    .Where(m => m.GetParameters().Length > 0 && m.GetParameters()[0].ParameterType == typeof(Thing))
                    .OrderBy(m => m.GetParameters().Length).First();
                var ps = mi.GetParameters();
                Say("[INFO] calling " + mi.DeclaringType.Name + "." + mi.Name + "("
                    + string.Join(", ", ps.Select(p => (p.IsOut ? "out " : "") + p.ParameterType.Name + " " + p.Name)) + ")");

                for (int i = 0; i < IconCalls; i++)
                {
                    var thing = NewThing(thingDef, i, g);
                    var args = BuildIconArgs(ps, thing);
                    var tex2 = (Texture2D)mi.Invoke(null, args);
                    r.IconCalls++;
                    if (tex2 == null) { r.IconNull++; continue; }
                    string n = tex2.name;
                    r.IconHistogram[n] = r.IconHistogram.TryGetValue(n, out int c) ? c + 1 : 1;
                    if (IsMask(n)) r.IconMask++;
                    if (tex2.width != 128 || tex2.height != 128) r.IconWrongSize++;
                    if (i == 0) { r.IconName = n; r.IconSize = tex2.width + "x" + tex2.height; }
                }
            }
            catch (Exception ex)
            {
                var inner = Unwrap(ex);
                r.IconError = inner.GetType().FullName + ": " + inner.Message;
                Say("[ERROR] GetIconFor threw " + r.IconError);
                foreach (var line in Top(inner, 8)) Say("        " + line);
                for (var e = inner.InnerException; e != null; e = e.InnerException)
                {
                    Say("        inner: " + e.GetType().FullName + ": " + e.Message);
                    foreach (var line in Top(e, 5)) Say("            " + line);
                }
            }

            // ---- cross-check: the collection's own accessors agree with the array
            try
            {
                r.DefPathVariants = g.SubGraphicsCount;
                for (int i = 0; i < r.Variants; i++)
                    if (!ReferenceEquals(g.SubGraphicAtIndex(i), subs[i])) { r.DefPathError = "SubGraphicAtIndex(" + i + ") != subGraphics[" + i + "]"; break; }
                if (!ReferenceEquals(g.FirstSubgraphic(), subs[0])) r.DefPathError = (r.DefPathError ?? "") + " FirstSubgraphic() != subGraphics[0]";
            }
            catch (Exception ex) { r.DefPathError = Unwrap(ex).GetType().Name + ": " + Unwrap(ex).Message; }

            // ---- stability: the same Thing across many calls, the Rand counter, and the pin ----
            try { Stability(def.DefName + "/" + mode, thingDef, g, subs); }
            catch (Exception ex)
            {
                var inner = Unwrap(ex);
                Say("[ERROR] Stability threw " + inner.GetType().FullName + ": " + inner.Message);
                foreach (var line in Top(inner, 6)) Say("        " + line);
                Check(def.DefName + "/" + mode + ": stability harness ran", false, inner.GetType().Name + ": " + inner.Message);
            }

            // The def -> graphic path (Thing.DefaultGraphic -> GraphicData.Graphic) is the one the game
            // uses when graphicInt is null; here graphicInt is seeded with the collection built above,
            // so that path is not exercised. It is reported, not asserted: it needs
            // UnityStubs.ImportDefaults (referenced by the port's texture importer), which this stub
            // does not carry.
            try
            {
                var viaDef = thingDef.graphicData.GetType()
                    .GetProperty("Graphic", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    ?.GetValue(thingDef.graphicData);
                var subs2 = viaDef == null ? null : (Graphic[])field.GetValue(viaDef);
                Say("[INFO] GraphicData.Graphic (def path) -> " + (subs2 == null ? "null" : subs2.Length + " subGraphics"));
            }
            catch (Exception ex) { Say("[INFO] GraphicData.Graphic (def path) unavailable in the stub: " + Unwrap(ex).GetType().Name + ": " + Unwrap(ex).Message); }

            return r;
        }

        // ------------------------------------------------------------------ stability

        /// <summary>
        /// The question is not "does GetIconFor allocate" but "does the same Thing produce the same
        /// icon". Each block below resets nothing except Rand, and the Rand counter is read before
        /// and after so "the icon path consumed Rand" is a number, not an opinion.
        /// </summary>
        static void Stability(string tag, ThingDef def, Graphic_Random g, Graphic[] subs)
        {
            const int N = 48;
            var mi = typeof(Widgets).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.Name == "GetIconFor")
                .Where(m => m.GetParameters().Length > 0 && m.GetParameters()[0].ParameterType == typeof(Thing))
                .OrderBy(m => m.GetParameters().Length).First();
            var ps = mi.GetParameters();

            var one = NewThing(def, 1234, g);
            int expectedIdx = 1234 % subs.Length;
            string expected = TexName(subs[expectedIdx]);

            // ---- A. the same Thing, 48 calls, no Thing or Rand reset in between
            SetS(typeof(Rand), "seed", 20261120u);
            SetS(typeof(Rand), "iterations", 0u);
            var same = new List<string>();
            for (int i = 0; i < N; i++) same.Add(IconName(mi, ps, one));
            uint sameRand = (uint)typeof(Rand).GetField("iterations", BindingFlags.Static | BindingFlags.Public).GetValue(null);
            int sameDistinct = same.Distinct(StringComparer.Ordinal).Count();
            Say($"[STAB] {tag} sameThing id=1234 calls={N} distinct={sameDistinct} texture={same[0]} "
                + $"expected={expected} randDelta={sameRand}");
            Check(tag + ": same Thing icon is stable across calls", sameDistinct == 1,
                  "distinct=" + sameDistinct + " sequence=" + string.Join(",", same.Take(6)));
            Check(tag + ": same Thing icon == subGraphics[thingID % n]", same[0] == expected,
                  same[0] + " vs " + expected);
            Check(tag + ": GetIconFor(Thing) does not consume Rand", sameRand == 0, "iterations +" + sameRand);

            // ---- B. OverrideGraphicIndex is the pin (the field is public, the property has no setter)
            var ovField = typeof(Thing).GetField("overrideGraphicIndex", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            ovField.SetValue(one, (int?)2);
            var ov = new List<string>();
            for (int i = 0; i < N; i++) ov.Add(IconName(mi, ps, one));
            int ovDistinct = ov.Distinct(StringComparer.Ordinal).Count();
            string ovExpected = TexName(subs[2 % subs.Length]);
            Say($"[STAB] {tag} overrideIndex=2 calls={N} distinct={ovDistinct} texture={ov[0]} expected={ovExpected}");
            Check(tag + ": OverrideGraphicIndex pins the Thing icon", ovDistinct == 1 && ov[0] == ovExpected,
                  "distinct=" + ovDistinct + " " + ov[0] + " vs " + ovExpected);
            ovField.SetValue(one, null);

            // ---- C. only the collection's own MatSingle re-rolls; GetIconFor unwraps first
            SetS(typeof(Rand), "iterations", 0u);
            var direct = new List<string>();
            for (int i = 0; i < N; i++) direct.Add(TexName(g));
            uint directRand = (uint)typeof(Rand).GetField("iterations", BindingFlags.Static | BindingFlags.Public).GetValue(null);
            int directDistinct = direct.Distinct(StringComparer.Ordinal).Count();
            Say($"[STAB] {tag} graphic.MatSingle calls={N} distinct={directDistinct} first={direct[0]} randDelta={directRand}");
            Check(tag + ": Graphic_Random.MatSingle re-rolls per access", directDistinct > 1,
                  "distinct=" + directDistinct + " of " + N);
            Check(tag + ": Graphic_Random.MatSingle consumes Rand", directRand == N, "iterations +" + directRand);

            SetS(typeof(Rand), "iterations", 0u);
            var matAt = new List<string>();
            for (int i = 0; i < N; i++)
            {
                var mat = g.MatAt(Rot4.North, null);
                var tex = mat == null ? null : mat.mainTexture as Texture2D;
                matAt.Add(tex == null ? "<null>" : tex.name);
            }
            uint matAtRand = (uint)typeof(Rand).GetField("iterations", BindingFlags.Static | BindingFlags.Public).GetValue(null);
            Say($"[STAB] {tag} graphic.MatAt(North,null) calls={N} distinct={matAt.Distinct(StringComparer.Ordinal).Count()} "
                + $"first={matAt[0]} randDelta={matAtRand}");
            Check(tag + ": MatAt(rot, null) on the collection re-rolls", matAt.Distinct(StringComparer.Ordinal).Count() > 1);

            SetS(typeof(Rand), "iterations", 0u);
            var subFor = new List<string>();
            for (int i = 0; i < N; i++) subFor.Add(TexName(g.SubGraphicFor(one)));
            uint subRand = (uint)typeof(Rand).GetField("iterations", BindingFlags.Static | BindingFlags.Public).GetValue(null);
            Say($"[STAB] {tag} SubGraphicFor(sameThing) calls={N} distinct={subFor.Distinct(StringComparer.Ordinal).Count()} "
                + $"texture={subFor[0]} randDelta={subRand}");
            Check(tag + ": SubGraphicFor(thing) is stable", subFor.Distinct(StringComparer.Ordinal).Count() == 1 && subRand == 0,
                  "distinct=" + subFor.Distinct(StringComparer.Ordinal).Count() + " randDelta=" + subRand);

            // ---- D. the def-level icon: ResolveIcon runs once, picks subGraphics[0], stores uiIcon
            SetF(def, "graphic", g);
            var before = def.uiIcon;
            var resolve = typeof(BuildableDef).GetMethod("ResolveIcon", BindingFlags.Instance | BindingFlags.NonPublic);
            resolve.Invoke(def, null);
            var after = def.uiIcon;
            string defExpected = TexName(subs[0]);
            Say($"[STAB] {tag} defIcon before={(before == null ? "<null>" : before.name)} "
                + $"after={(after == null ? "<null>" : after.name)} expected={defExpected}");
            Check(tag + ": def icon is subGraphics[0] (ResolveIcon passes a null Thing)",
                  after != null && after.name == defExpected, (after == null ? "<null>" : after.name) + " vs " + defExpected);

            SetS(typeof(Rand), "iterations", 0u);
            var defSeq = new List<string>();
            for (int i = 0; i < 16; i++)
            {
                var t = Widgets.GetIconFor(def);
                defSeq.Add(t == null ? "<null>" : t.name);
            }
            uint defRand = (uint)typeof(Rand).GetField("iterations", BindingFlags.Static | BindingFlags.Public).GetValue(null);
            int defDistinct = defSeq.Distinct(StringComparer.Ordinal).Count();
            Say($"[STAB] {tag} GetIconFor(def) calls=16 distinct={defDistinct} texture={defSeq[0]} randDelta={defRand}");
            Check(tag + ": def icon is cached in uiIcon and stable", defDistinct == 1, "distinct=" + defDistinct);
            Check(tag + ": the def icon path consumes no Rand", defRand == 0, "iterations +" + defRand);

            // ---- E. Rand is one global counter, and reseeding reproduces the sequence
            var s1 = RandSeq(16);
            var s2 = RandSeq(16);
            Say($"[STAB] {tag} rand seq1={string.Join(",", s1)}");
            Say($"[STAB] {tag} rand seq2={string.Join(",", s2)} equal={s1.SequenceEqual(s2)}");
            Check(tag + ": Rand is a single reproducible global sequence", s1.SequenceEqual(s2));
        }

        static string IconName(MethodInfo mi, ParameterInfo[] ps, Thing t)
        {
            try
            {
                var tex = (Texture2D)mi.Invoke(null, BuildIconArgs(ps, t));
                return tex == null ? "<null>" : tex.name;
            }
            catch (Exception ex) { return "<err:" + Unwrap(ex).GetType().Name + ">"; }
        }

        static string TexName(Graphic sg)
        {
            try
            {
                var mat = sg == null ? null : sg.MatSingle;
                var tex = mat == null ? null : mat.mainTexture as Texture2D;
                return tex == null ? "<null>" : tex.name;
            }
            catch (Exception ex) { return "<err:" + Unwrap(ex).GetType().Name + ">"; }
        }

        static int[] RandSeq(int n)
        {
            SetS(typeof(Rand), "seed", 20261120u);
            SetS(typeof(Rand), "iterations", 0u);
            var a = new int[n];
            for (int i = 0; i < n; i++) a[i] = Rand.Range(0, 1000);
            return a;
        }

        /// <summary>
        /// When Init throws, walk the three calls it makes in order and report which one breaks.
        /// GraphicDatabase.Get catches everything and reports it through Verse.Log, and Verse.Log
        /// cannot start head-less (its cctor calls Brutal.Logging.LogCategory.Make), so the original
        /// exception is otherwise lost behind a TypeInitializationException.
        /// </summary>
        static void Diagnose(FxDef def, List<TexEntry> entries, string mode)
        {
            Say("[DIAG] isolating the failing call");

            try
            {
                var found = ContentFinder<Texture2D>.GetAllInFolder(def.TexPath).ToList();
                Say($"[DIAG] ContentFinder<Texture2D>.GetAllInFolder(\"{def.TexPath}\") -> {found.Count} textures: "
                    + string.Join(", ", found.Select(t => t?.name).Take(12)));
            }
            catch (Exception ex) { Say("[DIAG] GetAllInFolder threw " + Unwrap(ex).GetType().FullName + ": " + Unwrap(ex).Message); }

            string name = mode == "bundle" ? entries[0].File : StripExt(entries[0].File);
            try
            {
                var one = ContentFinder<Texture2D>.Get(def.TexPath + "/" + name, false);
                Say($"[DIAG] ContentFinder<Texture2D>.Get(\"{def.TexPath}/{name}\") -> {(one == null ? "null" : one.name + " " + one.width + "x" + one.height)}");
            }
            catch (Exception ex) { Say("[DIAG] Get threw " + Unwrap(ex).GetType().FullName + ": " + Unwrap(ex).Message); }

            try
            {
                var g = Activator.CreateInstance(typeof(Graphic_Random));
                Say("[DIAG] Activator.CreateInstance(Graphic_Random) -> " + g.GetType().FullName);
            }
            catch (Exception ex) { Say("[DIAG] Activator.CreateInstance threw " + Unwrap(ex).GetType().FullName + ": " + Unwrap(ex).Message); }

            try
            {
                var g1 = GraphicDatabase.Get(typeof(Graphic_Single), def.TexPath + "/" + name,
                           ShaderDatabase.CutoutComplex, Vector2.one, Color.white, Color.white, null, null);
                Say("[DIAG] GraphicDatabase.Get(Graphic_Single, <one file>) -> " + (g1 == null ? "null" : g1.GetType().Name + " path=" + g1.path));
            }
            catch (Exception ex)
            {
                var inner = Unwrap(ex);
                Say("[DIAG] GraphicDatabase.Get(Graphic_Single) threw " + inner.GetType().FullName + ": " + inner.Message);
                if (inner.InnerException != null)
                    Say("[DIAG]    inner: " + inner.InnerException.GetType().FullName + ": " + inner.InnerException.Message);
            }
        }

        static object[] BuildIconArgs(ParameterInfo[] ps, Thing thing)
        {
            var args = new object[ps.Length];
            for (int i = 0; i < ps.Length; i++)
            {
                var t = ps[i].ParameterType;
                var u = t.IsByRef ? t.GetElementType() : t;
                if (i == 0) { args[i] = thing; continue; }
                if (u == typeof(Rect)) { args[i] = new Rect(0f, 0f, 128f, 128f); continue; }
                if (u == typeof(bool)) { args[i] = true; continue; }
                if (u == typeof(float)) { args[i] = 0f; continue; }
                if (u == typeof(Vector2)) { args[i] = Vector2.one; continue; }
                if (u == typeof(Color)) { args[i] = Color.white; continue; }
                args[i] = null;   // Nullable<Rot4>, Material&, ...
            }
            return args;
        }

        static Thing NewThing(ThingDef def, int id, Graphic g)
        {
            var t = (Thing)New(typeof(Thing));
            SetF(t, "def", def);
            SetF(t, "thingIDNumber", id);
            if (g != null) SetF(t, "graphicInt", g);
            return t;
        }

        static bool IsMask(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            var b = path.Substring(path.LastIndexOf('/') + 1);
            return b.EndsWith("_m", StringComparison.OrdinalIgnoreCase)
                || b.EndsWith("_m.png", StringComparison.OrdinalIgnoreCase);
        }

        static string Base(string path) => string.IsNullOrEmpty(path) ? "<null>" : path.Substring(path.LastIndexOf('/') + 1);

        // ------------------------------------------------------------------ game objects

        static Shader ResolveShader(FxDef def)
        {
            // The def names a ShaderTypeDef; the game resolves it through ShaderTypeDef.Shader ->
            // ShaderDatabase.LoadShader(shaderPath). Both are real code; only Shader.Find is stubbed.
            var st = (ShaderTypeDef)New(typeof(ShaderTypeDef));
            st.defName = def.ShaderType;
            st.shaderPath = def.ShaderType;
            st.label = def.ShaderType;
            try
            {
                var sh = st.Shader;
                if (sh != null)
                {
                    Say($"[INFO] shader via ShaderTypeDef('{def.ShaderType}').Shader -> {sh.name}");
                    return sh;
                }
            }
            catch (Exception ex)
            {
                Say("[WARN] ShaderTypeDef.Shader threw " + Unwrap(ex).GetType().Name + ": " + Unwrap(ex).Message
                    + " - falling back to ShaderDatabase.CutoutComplex");
            }
            var cut = ShaderDatabase.CutoutComplex;
            Say($"[INFO] shader via ShaderDatabase.CutoutComplex -> {cut.name}");
            return cut;
        }

        static ThingDef BuildDef(FxDef fx)
        {
            var def = (ThingDef)New(typeof(ThingDef));
            def.defName = fx.DefName;
            var gd = (GraphicData)New(typeof(GraphicData));
            gd.texPath = fx.TexPath;
            gd.graphicClass = ResolveGraphicClass(fx.GraphicClass);
            gd.shaderType = (ShaderTypeDef)New(typeof(ShaderTypeDef));
            gd.shaderType.defName = fx.ShaderType;
            gd.shaderType.shaderPath = fx.ShaderType;
            gd.drawSize = Vector2.one;
            gd.color = Color.white;
            gd.colorTwo = Color.white;
            gd.renderQueue = 0;
            def.graphicData = gd;
            return def;
        }

        /// <summary>&lt;graphicClass&gt;Graphic_Random&lt;/graphicClass&gt; - resolved the way the XML
        /// loader resolves it: by name, in the game assembly.</summary>
        static Type ResolveGraphicClass(string name)
        {
            var t = typeof(Graphic).Assembly.GetType("Verse." + name, false)
                 ?? typeof(Graphic).Assembly.GetType(name, false);
            if (t == null) throw new InvalidOperationException("graphicClass '" + name + "' not found in Assembly-CSharp");
            if (!typeof(Graphic).IsAssignableFrom(t)) throw new InvalidOperationException(name + " is not a Verse.Graphic");
            return t;
        }

        static GraphicRequest BuildRequest(FxDef fx, ThingDef def, Shader shader)
        {
            var req = default(GraphicRequest);
            req.graphicClass = def.graphicData.graphicClass;
            req.texture = null;
            req.path = fx.TexPath;
            req.maskPath = def.graphicData.maskPath;      // null in both defs
            req.shader = shader;
            req.drawSize = def.graphicData.drawSize;
            req.color = def.graphicData.color;
            req.colorTwo = def.graphicData.colorTwo;
            req.graphicData = def.graphicData;
            req.renderQueue = def.graphicData.renderQueue;
            req.shaderParameters = def.graphicData.shaderParameters;
            return req;
        }

        /// <summary>
        /// The game's own content plumbing, seeded with synthetic assets - the one place the harness
        /// substitutes anything on the Verse side, because a bare process has no mods and no disk
        /// assets:
        ///
        ///     LoadedModManager.runningMods      = [ one fake ModContentPack ]
        ///     ModContentPack.textures           = ModContentHolder&lt;Texture2D&gt;
        ///     ModContentHolder.contentList      = { "&lt;texPath&gt;/&lt;name&gt;" -> Texture2D }
        ///     ModContentHolder.contentListTrie  = the same keys
        ///     ModContentPack.assetBundles       = handler with an empty bundle list
        ///
        /// mode "bundle"   the texture name is the asset name in the bundle: "UniqueRevolverA.png"
        /// mode "stripped" the same, with the extension removed: "UniqueRevolverA"
        /// </summary>
        /// <summary>
        /// Verse.Widgets' static ctor loads ~20 UI textures with ContentFinder&lt;Texture2D&gt;.Get(path, 1),
        /// and Get reports a missing asset through Verse.Log, which cannot start head-less - so the
        /// type initializer throws before GetIconFor runs. The paths are read out of the cctor's own
        /// IL (ldstr; ldc.i4.1; call Get) instead of being hard-coded, and each one is seeded with a
        /// 1x1 stand-in. They are UI chrome, not the icon under test.
        /// </summary>
        /// <summary>
        /// Every asset path that some type's static constructor asks ContentFinder/MaterialPool for.
        /// The engine chrome (UI/Widgets/*, UI/Misc/*, Misc/*) is loaded by a dozen different cctors
        /// and any miss goes through Verse.Log, which is fatal to the initializer that is running.
        /// Reading the literals out of the IL means the seed list follows the shipped dll instead of
        /// being guessed; paths built with string.Concat are added by hand below.
        /// </summary>
        static List<string> AllChromePaths(Asm asm, ICollection<string> skipPrefixes)
        {
            var found = new List<string>();
            foreach (var th in asm.Md.TypeDefinitions)
            {
                var td = asm.Md.GetTypeDefinition(th);
                foreach (var mh in td.GetMethods())
                {
                    var md = asm.Md.GetMethodDefinition(mh);
                    if (asm.Md.GetString(md.Name) != ".cctor" || md.RelativeVirtualAddress == 0) continue;
                    foreach (var p in PathsIn(asm, mh))
                    {
                        if (p.Contains("/") && !skipPrefixes.Any(s => p.StartsWith(s, StringComparison.Ordinal)))
                            found.Add(p);
                    }
                }
            }
            return found.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
        }

        static List<string> AssetPathsFrom(Asm asm, string typeName)
        {
            var paths = new List<string>();
            var cctor = asm.Methods(typeName, ".cctor").FirstOrDefault();
            if (cctor.IsNil) return paths;
            return PathsIn(asm, cctor);
        }

        static List<string> PathsIn(Asm asm, System.Reflection.Metadata.MethodDefinitionHandle cctor)
        {
            var paths = new List<string>();
            var ins = asm.Body(cctor);
            for (int i = 0; i < ins.Count; i++)
            {
                if (ins[i].Name != "ldstr" || !ins[i].HasToken) continue;
                // the literal is used by a texture lookup within the next few instructions
                bool used = false;
                for (int j = i + 1; j < Math.Min(ins.Count, i + 4); j++)
                {
                    if (!ins[j].HasToken) continue;
                    string callee = null;
                    try { callee = asm.Describe(System.Reflection.Metadata.Ecma335.MetadataTokens.EntityHandle(ins[j].Token)); }
                    catch { continue; }
                    if (callee == null) continue;
                    if (callee.Contains("ContentFinder`1<UnityEngine.Texture2D>::Get") ||
                        callee.Contains("MaterialPool::MatFrom")) { used = true; break; }
                }
                if (!used) continue;
                try
                {
                    string p = asm.Md.GetUserString(System.Reflection.Metadata.Ecma335.MetadataTokens.UserStringHandle(ins[i].Token & 0x00FFFFFF));
                    if (!string.IsNullOrEmpty(p) && p.Contains("/")) paths.Add(p);
                }
                catch { }
            }
            return paths;
        }

        static void InstallContent(string folder, List<TexEntry> entries, string mode)
        {
            var mod = (ModContentPack)New(typeof(ModContentPack));
            var holder = (ModContentHolder<Texture2D>)New(typeof(ModContentHolder<Texture2D>));
            var dict = new Dictionary<string, Texture2D>(StringComparer.Ordinal);
            var trie = new KTrie.StringTrieSet();

            foreach (var e in entries)
            {
                string name = mode == "bundle" ? e.File : StripExt(e.File);
                var tex = new Texture2D(e.W, e.H) { name = name };   // Stub: real w/h, managed buffer
                Stubbed.Add("UnityEngine.Texture2D(" + e.W + "x" + e.H + ")");
                string key = folder + "/" + name;
                dict[key] = tex;
                trie.Add(key);
            }

            // Widgets' own UI chrome, at the exact paths its cctor asks for
            var chromeAsm = Asm.Open(Path.Combine(DllDir, "Assembly-CSharp.dll"));
            var defPrefixes = entries.Select(e => e.Folder).Distinct().ToList();
            var uiPaths = AllChromePaths(chromeAsm, defPrefixes)
                // built with string.Concat in the cctor, so not visible as a literal; read out of the
                // game's own error output ("Could not load Texture2D at 'UI/Misc/BadTexture'")
                .Concat(new[] { "UI/Misc/BadTexture", "PlaceholderImage" })
                .Distinct(StringComparer.Ordinal).ToList();
            foreach (var p in uiPaths)
            {
                if (dict.ContainsKey(p)) continue;
                var ui = new Texture2D(1, 1) { name = p.Substring(p.LastIndexOf('/') + 1) };
                Stubbed.Add("UnityEngine.Texture2D(1x1 UI chrome)");
                dict[p] = ui;
                trie.Add(p);
            }
            Say($"[INFO] seeded {dict.Count} content keys ({entries.Count} def textures + {uiPaths.Count} engine-chrome keys from Widgets/BaseContent cctors)");

            SetF(holder, "mod", mod);
            SetF(holder, "contentList", dict);
            SetF(holder, "contentListTrie", trie);
            SetF(mod, "nameInt", "IconExecFakeMod");
            SetF(mod, "packageIdInt", "iconsheep.iconexec.fake");
            SetF(mod, "packageIdPlayerFacingInt", "IconExecFakeMod");
            SetF(mod, "loadOrder", 0);
            SetF(mod, "textures", holder);
            SetF(mod, "rootDirInt", new DirectoryInfo(AppContext.BaseDirectory));
            SetF(mod, "official", false);

            // The tail of ContentFinder.GetAllInFolder walks each mod's asset bundles; an empty bundle
            // list keeps that loop at zero iterations instead of dereferencing a null handler.
            var bundles = New(typeof(ModAssetBundlesHandler));
            SetF(bundles, "mod", mod);
            SetF(bundles, "loadedAssetBundles", Activator.CreateInstance(typeof(List<>).MakeGenericType(
                bundles.GetType().GetField("loadedAssetBundles", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).FieldType.GetGenericArguments()[0])));
            SetF(mod, "assetBundles", bundles);

            SetS(typeof(LoadedModManager), "runningMods", new List<ModContentPack> { mod });

            // ContentFinder<T>.Get only consults the mod holders when ModContentHolder<T>.anyReloaded
            // is true (IL_0049 -> brfalse past the whole lookup loop). The game sets it when the mod
            // list is reloaded; a fresh process starts with false, and then Get returns default for
            // every path even though the holder is populated.
            SetS(typeof(ModContentHolder<Texture2D>), "anyReloaded", true);
        }

        static string StripExt(string n)
        {
            int i = n.LastIndexOf('.');
            return i > 0 ? n.Substring(0, i) : n;
        }

        // ------------------------------------------------------------------ reflection helpers

        static object New(Type t)
        {
            try { return Activator.CreateInstance(t, true); }
            catch { return RuntimeHelpers.GetUninitializedObject(t); }
        }

        static Exception Unwrap(Exception ex) => (ex as TargetInvocationException)?.InnerException ?? ex;

        static IEnumerable<string> Top(Exception ex, int n)
        {
            var st = ex.StackTrace?.Split('\n') ?? Array.Empty<string>();
            return st.Take(n).Select(s => s.Trim());
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
            // coerce (Rand.seed is UInt32, UnityData.mainThreadId is Int32, ...) so a caller cannot
            // fail on a width mismatch - on this runtime an unhandled exception aborts silently
            if (v != null && !f.FieldType.IsInstanceOfType(v)) v = Convert.ChangeType(v, f.FieldType);
            f.SetValue(null, v);
        }

        static Assembly Resolve(object s, ResolveEventArgs e)
        {
            var p = Path.Combine(DllDir, new AssemblyName(e.Name).Name + ".dll");
            return File.Exists(p) ? Assembly.LoadFrom(p) : null;
        }

        // ------------------------------------------------------------------ assertions

        static void Check(string what, bool ok, string detail = "")
        {
            if (ok) Say("[PASS] " + what + (string.IsNullOrEmpty(detail) ? "" : "  (" + detail + ")"));
            else { Say("[FAIL] " + what + (string.IsNullOrEmpty(detail) ? "" : "  (" + detail + ")")); Failures.Add(what); }
        }

        /// <summary>A defect the harness has measured. If it ever stops happening the run goes red,
        /// because that is a change in the port's behaviour, not a fix the harness should silently accept.</summary>
        static void Defect(string what, bool present, string detail)
        {
            Say((present ? "[DEFECT] " : "[FAIL] ") + what + "  (" + detail + ")");
            if (!present) Failures.Add("expected defect absent: " + what);
        }

        static int AssertAll(List<Result> rs)
        {
            foreach (var r in rs)
            {
                string tag = r.Def + "/" + r.Mode;
                Say("");
                Check(tag + ": Init completes", r.Error == null, r.Error ?? "ok");
                if (r.Error != null) continue;

                // expected variant set, straight from the texture inventory
                var expected = r.Mode == "bundle"
                    ? r.Subs.Select(s => Base(s.Path)).OrderBy(x => x, StringComparer.Ordinal).ToList()
                    : null;
                var albedo = r.Files / 2;
                int expectedVariants = r.Mode == "bundle" ? r.Files : albedo;

                Check(tag + ": subGraphics == " + expectedVariants, r.Variants == expectedVariants, "got " + r.Variants);
                Check(tag + ": every variant path sits under the def texPath",
                      r.Paths.All(p => !string.IsNullOrEmpty(p) && p.StartsWith(r.Folder + "/")),
                      r.Paths.Count(p => p.StartsWith(r.Folder + "/")) + "/" + r.Paths.Count);

                if (r.Mode == "bundle")
                {
                    // the leak: mask assets are not variants, they are separate files with the same stem
                    Defect(tag + ": " + r.MaskVariants + " of " + r.Variants + " variants are mask textures (the port merges _m into the collection)",
                           r.MaskVariants == albedo, r.MaskVariants + "/" + r.Variants + " mask = " + Math.Round(100.0 * r.MaskVariants / Math.Max(1, r.Variants), 1) + "%");
                }
                else
                {
                    Check(tag + ": no mask texture among the variants", r.MaskVariants == 0, "got " + r.MaskVariants);
                }

                // each variant's own material/texture, at the real size
                Check(tag + ": every variant is a Graphic_Single", r.Subs.All(s => s.Type == "Graphic_Single"),
                      string.Join(",", r.Subs.Select(s => s.Type).Distinct()));
                Check(tag + ": every variant's material carries the texture named by its path",
                      r.Subs.All(s => s.TexName == Base(s.Path)), r.Subs.Count(s => s.TexName == Base(s.Path)) + "/" + r.Subs.Count);
                Check(tag + ": every variant's texture is 128x128 (the real bundle size)",
                      r.Subs.All(s => s.TexSize == "128x128"), string.Join(",", r.Subs.Select(s => s.TexSize).Distinct()));

                // thingIDNumber distribution
                Check(tag + ": thingIDNumber 0..999 selects every variant",
                      r.Histogram.Count == r.Variants, r.Histogram.Count + "/" + r.Variants + " buckets");
                int expectPer = IdSweep / Math.Max(1, r.Variants);
                Check(tag + ": each variant gets " + expectPer + " +-10% of 1000 thing ids",
                      r.Histogram.Values.All(v => Math.Abs(v - expectPer) <= Math.Max(2, expectPer / 10)),
                      "min " + (r.Histogram.Count == 0 ? 0 : r.Histogram.Values.Min()) + " max " + (r.Histogram.Count == 0 ? 0 : r.Histogram.Values.Max()));
                if (r.Mode == "bundle")
                    Defect(tag + ": SubGraphicFor hands out masks: " + r.MaskHits + "/1000 thing ids resolve to a _m texture",
                           Math.Abs(r.MaskHits - IdSweep / 2) <= 40, r.MaskHits + "/" + r.IdsSeen);
                else
                    Check(tag + ": SubGraphicFor never selects a mask", r.MaskHits == 0, r.MaskHits + "/" + r.IdsSeen);

                // GetIconFor
                Check(tag + ": GetIconFor(Thing,...) returns a texture for every call",
                      r.IconError == null && r.IconNull == 0, r.IconError ?? (r.IconNull + " null of " + r.IconCalls));
                Check(tag + ": GetIconFor texture is 128x128", r.IconWrongSize == 0, r.IconWrongSize + " wrong of " + r.IconCalls);
                Check(tag + ": GetIconFor only ever returns a texture that is in the collection",
                      r.IconHistogram.Keys.All(k => r.Paths.Any(p => Base(p) == k)),
                      string.Join(",", r.IconHistogram.Keys.Take(4)));
                if (r.Mode == "bundle")
                    Defect(tag + ": the UI icon is a mask " + r.IconMask + "/" + r.IconCalls + " of the time (GetIconFor -> MatSingle -> Rand.Range)",
                           r.IconCalls > 0 && Math.Abs(r.IconMask - r.IconCalls / 2) <= 60, r.IconMask + "/" + r.IconCalls);
                else
                    Check(tag + ": the UI icon is never a mask", r.IconMask == 0, r.IconMask + "/" + r.IconCalls);

                // the collection's own accessors must agree with the array Init produced
                Check(tag + ": SubGraphicsCount / SubGraphicAtIndex / FirstSubgraphic agree with subGraphics",
                      r.DefPathVariants == r.Variants && r.DefPathError == null,
                      "count " + r.DefPathVariants + " vs " + r.Variants + (r.DefPathError == null ? "" : " [" + r.DefPathError + "]"));
            }

            Say("");
            return Failures.Count;
        }

        // ------------------------------------------------------------------ table

        static void WriteTable(List<Result> rs, string path)
        {
            Rows.AppendLine("| def | naming | files | subGraphics | mask variants | ids -> mask | icon -> mask | icon texture | icon size |");
            Rows.AppendLine("|---|---|---|---|---|---|---|---|---|");
            foreach (var r in rs)
                Rows.AppendLine($"| {r.Def} | {r.Mode} | {r.Files} | {r.Variants} | {r.MaskVariants} | {r.MaskHits}/{r.IdsSeen} | {r.IconMask}/{r.IconCalls} | `{r.IconName}` | {r.IconSize} |");

            var md = new StringBuilder();
            md.AppendLine("### iconexec - the unique-weapon icon chain, executed");
            md.AppendLine();
            md.AppendLine("* `Graphic_Collection.Init`, `Graphic_Random.SubGraphicFor`, `Graphic_Random.MatSingle` and");
            md.AppendLine("  `Widgets.GetIconFor` are the game's own compiled methods from `dlls/Assembly-CSharp.dll`.");
            md.AppendLine("* the request is built from the def's own `<graphicClass>` / `<texPath>` / `<shaderType>`,");
            md.AppendLine("  read out of `iconexec/data/Weapons_Unique.xml` (verbatim Odyssey data from the device).");
            md.AppendLine("* textures are one `Texture2D` per bundle entry at the real 128x128 size");
            md.AppendLine("  (`iconexec/data/unique_weapons_textures.json`, header-scanned from the device bundles).");
            md.AppendLine("* `UnityEngine` is the stub in `iconexec/Stub/`; `iconexec/UnityProbe` shows why the shipped");
            md.AppendLine("  `UnityStubs.dll` cannot do this head-less (Brutal.Vulkan + shader sources).");
            md.AppendLine();
            md.Append(Rows);
            md.AppendLine();
            md.AppendLine("### per-variant texture");
            md.AppendLine();
            md.AppendLine("| def | naming | # | type | path | texture | size | mask |");
            md.AppendLine("|---|---|---|---|---|---|---|---|");
            foreach (var r in rs)
                foreach (var s in r.Subs)
                    md.AppendLine($"| {r.Def} | {r.Mode} | {s.Index} | {s.Type} | `{s.Path}` | `{s.TexName}` | {s.TexSize} | {(s.IsMask ? "yes" : "")} |");

            Console.WriteLine();
            Console.Write(md.ToString());

            if (!string.IsNullOrEmpty(path))
            {
                string dir = Path.GetDirectoryName(Path.GetFullPath(path));
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(path, md.ToString());
            }
        }
    }
}
