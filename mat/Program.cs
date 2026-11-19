using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using UnityEngine;
using Verse;

namespace MatPool
{
    /// <summary>
    /// Verse.MaterialPool's cache key, executed.
    ///
    /// The dll in dlls/ is loaded and its own MatFrom / MaterialRequest.Equals / GetHashCode run;
    /// only UnityEngine is replaced (Stub/), because the shipped one needs the Waylith engine.
    /// Four request shapes are built, the pool is counted before and after each, and the census
    /// multiplies variants x colours x masks to say how many materials that costs.
    /// </summary>
    static class Program
    {
        const int ExitPass = 0, ExitFail = 1, ExitBroken = 2;

        static readonly List<string> Failures = new List<string>();
        static readonly StringBuilder Log = new StringBuilder();
        static readonly StringBuilder Rows = new StringBuilder();

        static void Say(string s)
        {
            Console.WriteLine(s);
            Console.Out.Flush();
            Log.AppendLine(s);
        }

        // ------------------------------------------------------------------ pool plumbing

        static FieldInfo DictField(string name)
        {
            FieldInfo f = typeof(MaterialPool).GetField(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (f == null) throw new InvalidOperationException("Verse.MaterialPool." + name + " not found");
            return f;
        }

        static IDictionary Pool() { return (IDictionary)DictField("matDictionary").GetValue(null); }
        static int PoolSize() { return Pool().Count; }

        static void ResetPool()
        {
            Pool().Clear();
            ((IDictionary)DictField("matDictionaryReverse").GetValue(null)).Clear();
        }

        // ------------------------------------------------------------------ requests

        static Shader _shader;

        static Texture2D Tex(string name) { return new Texture2D(4, 4) { name = name }; }

        static MaterialRequest Req(Texture2D tex, Color color, Texture2D mask = null, List<ShaderParameter> pars = null)
        {
            var r = new MaterialRequest(tex, _shader, color) { colorTwo = Color.white };
            r.color = color;
            r.maskTex = mask;
            r.shaderParameters = pars;
            return r;
        }

        static void SetField(object o, string n, object v)
        {
            FieldInfo f = o.GetType().GetField(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (f == null) throw new InvalidOperationException(o.GetType().Name + "." + n + " not found");
            f.SetValue(o, v);
        }

        // ShaderParameter(type=Float, name="Tint", value=(v,0,0,0)) — the field names come from the dll.
        static ShaderParameter Param(string name, float v)
        {
            var p = new ShaderParameter();
            SetField(p, "name", name);
            SetField(p, "value", new Vector4(v, 0f, 0f, 0f));
            FieldInfo tf = typeof(ShaderParameter).GetField("type", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            object floatMember = null;
            foreach (object e in Enum.GetValues(tf.FieldType))
                if (e.ToString().IndexOf("Float", StringComparison.OrdinalIgnoreCase) >= 0) { floatMember = e; break; }
            if (floatMember == null) floatMember = Enum.GetValues(tf.FieldType).GetValue(0);
            tf.SetValue(p, floatMember);
            return p;
        }

        static List<ShaderParameter> Pars(string name, float v) { return new List<ShaderParameter> { Param(name, v) }; }

        static string ParsText(List<ShaderParameter> ps)
        {
            if (ps == null) return "null";
            var parts = new List<string>();
            foreach (var p in ps)
            {
                FieldInfo nf = typeof(ShaderParameter).GetField("name");
                FieldInfo vf = typeof(ShaderParameter).GetField("value");
                FieldInfo tf = typeof(ShaderParameter).GetField("type");
                parts.Add(nf.GetValue(p) + "(" + tf.GetValue(p) + ")=" + vf.GetValue(p));
            }
            return string.Join(";", parts);
        }

        // Property-by-property comparison of two parameter lists, so case D can prove the *contents*
        // are equal and only the List instance differs.
        static bool SameContents(List<ShaderParameter> a, List<ShaderParameter> b)
        {
            if (a == null || b == null) return a == b;
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                foreach (string f in new[] { "name", "value", "valueTex", "type" })
                {
                    object x = typeof(ShaderParameter).GetField(f).GetValue(a[i]);
                    object y = typeof(ShaderParameter).GetField(f).GetValue(b[i]);
                    if (!Equals(x, y)) return false;
                }
            }
            return true;
        }

        // ------------------------------------------------------------------ assertions

        static void Check(string what, bool ok, string detail = "")
        {
            if (ok) Say("[PASS] " + what + (string.IsNullOrEmpty(detail) ? "" : "  (" + detail + ")"));
            else { Say("[FAIL] " + what + (string.IsNullOrEmpty(detail) ? "" : "  (" + detail + ")")); Failures.Add(what); }
        }

        static void Row(string c0, string c1, string c2, string c3, string c4, string c5)
        {
            Rows.AppendLine("| " + c0 + " | " + c1 + " | " + c2 + " | " + c3 + " | " + c4 + " | " + c5 + " |");
        }

        // ------------------------------------------------------------------ main

        static int Main(string[] argv)
        {
            string dlls = Arg(argv, "--dlls") ?? "dlls";
            string tablePath = Arg(argv, "--table");
            bool forceFail = argv.Contains("--force-fail");

            // 1. the dll's own account of the cache, asserted
            string casm = Path.Combine(dlls, "Assembly-CSharp.dll");
            if (!File.Exists(casm)) { Say("[FAIL] " + casm + " not found (pass --dlls <dir>)"); return ExitBroken; }

            var facts = new Facts(Dll.Open(casm));
            facts.Run();
            foreach (string f in facts.Ok) Say("[FACT] " + f);
            foreach (string f in facts.Broken) Say("[BROKEN] " + f);
            if (facts.Broken.Count > 0)
            {
                Say("[SUMMARY] " + facts.Ok.Count + " facts hold, " + facts.Broken.Count + " do not");
                Say("RESULT: BROKEN");
                return ExitBroken;
            }

            // 2. boot the game's own code, head-less
            SetStatic(typeof(UnityDataInitializer), "initializing", true);
            SetStatic(typeof(UnityData), "mainThreadId", Thread.CurrentThread.ManagedThreadId);
            if (!UnityData.IsInMainThread)
            {
                Say("[BROKEN] UnityData.IsInMainThread is false; MatFrom would log-and-return null");
                Say("RESULT: BROKEN");
                return ExitBroken;
            }

            _shader = ShaderDatabase.CutoutComplex;
            Say("[INFO] Assembly-CSharp " + typeof(MaterialPool).Assembly.GetName().Version
                + ", shader=" + _shader.name + ", mask-supported=" + ShaderUtility.SupportsMaskTex(_shader)
                + ", pool=" + PoolSize());

            // A mask difference can only be a cache-key difference if the shader can consume a mask;
            // otherwise MatFrom nulls maskTex and case C would silently equal case A.
            if (!ShaderUtility.SupportsMaskTex(_shader))
            {
                Say("[BROKEN] the stub shader does not declare _MaskTex, so case C cannot be tested");
                Say("RESULT: BROKEN");
                return ExitBroken;
            }

            Rows.AppendLine("| case | shared | differs | pool after | same instance | verdict |");
            Rows.AppendLine("|---|---|---|---|---|---|");

            ScenarioA();
            ScenarioB();
            ScenarioC();
            ScenarioD();
            ScenarioE();

            Census();

            if (forceFail)
            {
                Say("[SCENARIO] control: a deliberately false assertion (--force-fail)");
                Check("control assertion that must fail", false, "forced by --force-fail");
            }

            WriteTable(tablePath);

            Say("[SUMMARY] " + Failures.Count + " failed assertion(s)");
            Say("RESULT: " + (Failures.Count == 0 ? "PASS" : "FAIL"));
            return Failures.Count == 0 ? ExitPass : ExitFail;
        }

        // ------------------------------------------------------------------ A..E

        static void ScenarioA()
        {
            ResetPool();
            var tex = Tex("TexA");
            Material first = MaterialPool.MatFrom(Req(tex, Color.white));
            int afterFirst = PoolSize();
            Material second = MaterialPool.MatFrom(Req(tex, Color.white));
            int afterSecond = PoolSize();

            Say("[SCENARIO] A same texture, same colour, two separate MaterialRequest values");
            Check("A: first call creates one material", first != null && afterFirst == 1, "pool=" + afterFirst);
            Check("A: second call returns the same instance", ReferenceEquals(first, second));
            Check("A: second call adds nothing to the pool", afterSecond == afterFirst, "pool=" + afterSecond);
            Say("[INFO] A material name from MatFrom: " + (first == null ? "null" : first.name));
            Check("A: the reverse table maps the material back to its request",
                  ((IDictionary)DictField("matDictionaryReverse").GetValue(null)).Contains(first));
            Row("A", "tex, colour", "nothing", afterSecond.ToString(), (ReferenceEquals(first, second) ? "yes" : "no"), "hit");
        }

        static void ScenarioB()
        {
            ResetPool();
            var tex = Tex("TexB");
            Material a = MaterialPool.MatFrom(Req(tex, Color.white));
            Material b = MaterialPool.MatFrom(Req(tex, new Color(0.2f, 0.4f, 0.6f)));
            Material b2 = MaterialPool.MatFrom(Req(tex, new Color(0.2f, 0.4f, 0.6f)));

            Say("[SCENARIO] B same texture, colour differs");
            Check("B: different colour is a different material", !ReferenceEquals(a, b));
            Check("B: the pool holds both", PoolSize() == 2, "pool=" + PoolSize());
            Check("B: the second colour is itself cached", ReferenceEquals(b, b2) && PoolSize() == 2, "pool=" + PoolSize());
            Row("B", "tex", "colour", "2", "no", "miss (then cached)");
        }

        static void ScenarioC()
        {
            ResetPool();
            var tex = Tex("TexC");
            var mask1 = Tex("Mask1");
            var mask2 = Tex("Mask2");
            Material a = MaterialPool.MatFrom(Req(tex, Color.white));
            Material c1 = MaterialPool.MatFrom(Req(tex, Color.white, mask1));
            Material c2 = MaterialPool.MatFrom(Req(tex, Color.white, mask2));
            Material c1b = MaterialPool.MatFrom(Req(tex, Color.white, mask1));

            Say("[SCENARIO] C same texture, maskTex differs");
            Check("C: adding a mask is a different material", !ReferenceEquals(a, c1));
            Check("C: a different mask is a different material", !ReferenceEquals(c1, c2));
            Check("C: three distinct materials for tex x {none, mask1, mask2}", PoolSize() == 3, "pool=" + PoolSize());
            Check("C: the same mask is cached", ReferenceEquals(c1, c1b) && PoolSize() == 3, "pool=" + PoolSize());
            Row("C", "tex, colour", "maskTex", "3", "no", "miss per mask");
        }

        static void ScenarioD()
        {
            ResetPool();
            var tex = Tex("TexD");
            var l1 = Pars("Tint", 0.5f);       // the List the caller keeps
            var l2 = Pars("Tint", 0.5f);       // an equal-contents List, built separately

            Material with1 = MaterialPool.MatFrom(Req(tex, Color.white, null, l1));
            int poolAfter1 = PoolSize();
            Material sameList = MaterialPool.MatFrom(Req(tex, Color.white, null, l1));
            int poolAfter2 = PoolSize();
            Material otherList = MaterialPool.MatFrom(Req(tex, Color.white, null, l2));
            int poolAfter3 = PoolSize();

            Say("[SCENARIO] D same shaderParameters contents, different List instance");
            Say("[INFO] D contents: " + ParsText(l1) + "  vs  " + ParsText(l2));
            Check("D: the two lists are element-wise equal", SameContents(l1, l2));
            Check("D: the two lists are different objects", !ReferenceEquals(l1, l2));
            Check("D: MaterialRequest.Equals says the two requests differ",
                  !Req(tex, Color.white, null, l1).Equals(Req(tex, Color.white, null, l2)));
            Check("D: the same List instance hits the cache",
                  ReferenceEquals(with1, sameList) && poolAfter2 == poolAfter1, "pool " + poolAfter1 + " -> " + poolAfter2);
            Check("D: an equal-contents List instance MISSES and allocates",
                  !ReferenceEquals(with1, otherList) && poolAfter3 == poolAfter2 + 1, "pool " + poolAfter2 + " -> " + poolAfter3);
            Say("[BLOAT] D is the unsafe half of the key: the List is compared with `ceq`, so N List instances");
            Say("[BLOAT] with identical contents cost N materials - and nothing evicts them.");
            Row("D", "tex, colour, params contents", "List instance", poolAfter3.ToString(), "no", "miss - BLOAT");
        }

        static void ScenarioE()
        {
            ResetPool();
            var tex = Tex("TexE");
            Material a = MaterialPool.MatFrom(Req(tex, new Color(0.5f, 0.5f, 0.5f)));
            Material near = MaterialPool.MatFrom(Req(tex, new Color(0.5f + 1f / 512f, 0.5f + 1f / 512f, 0.5f + 1f / 512f)));
            Material far = MaterialPool.MatFrom(Req(tex, new Color(0.6f, 0.6f, 0.6f)));

            Say("[SCENARIO] E colours below 1/255 collapse (MatFrom quantises through Color32)");
            Check("E: a sub-1/255 colour difference hits the same material", ReferenceEquals(a, near), "0.5 vs 0.502");
            Check("E: a visible colour difference misses", !ReferenceEquals(a, far), "0.5 vs 0.6");
            Check("E: pool holds exactly two", PoolSize() == 2, "pool=" + PoolSize());
            Row("E", "tex", "colour < 1/255 vs >= 1/255", "2", "yes then no", "quantised hit");
        }

        // ------------------------------------------------------------------ census

        static void Census()
        {
            const int Variants = 5, Colors = 3, Masks = 2;
            var colors = new[] { Color.white, new Color(0.25f, 0.5f, 0.75f), new Color(0.9f, 0.2f, 0.1f) };
            var texes = Enumerable.Range(0, Variants).Select(i => Tex("Census" + i)).ToArray();
            var masks = new[] { Tex("CensusMask0"), Tex("CensusMask1") };

            // (1) the plain matrix: one request per (texture, colour, mask), shared instances
            ResetPool();
            var mats = new List<Material>();
            foreach (var t in texes)
                foreach (var c in colors)
                    foreach (var m in masks)
                        mats.Add(MaterialPool.MatFrom(Req(t, c, m)));
            int created = mats.Count;
            int distinct = mats.Distinct().Count();
            int pool1 = PoolSize();

            Say("[CENSUS] " + Variants + " variants x " + Colors + " colours x " + Masks + " masks");
            Check("census: " + created + " requests create " + created + " materials", distinct == created && pool1 == created,
                  "created=" + created + " distinct=" + distinct + " pool=" + pool1);

            // (2) the same matrix asked again: per-instance requests must not multiply
            var again = new List<Material>();
            foreach (var t in texes)
                foreach (var c in colors)
                    foreach (var m in masks)
                        again.Add(MaterialPool.MatFrom(Req(t, c, m)));
            Check("census: asking the same matrix a second time allocates nothing",
                  PoolSize() == pool1 && again.Select((x, i) => ReferenceEquals(x, mats[i])).All(b => b),
                  "pool=" + PoolSize());

            // (3) the same matrix asked twice, each request carrying its own equal-contents List
            ResetPool();
            int listRequests = 0;
            for (int rep = 0; rep < 2; rep++)
                foreach (var t in texes)
                    foreach (var c in colors)
                        foreach (var m in masks)
                        {
                            MaterialPool.MatFrom(Req(t, c, m, Pars("Tint", 0.5f)));
                            listRequests++;
                        }
            int pool2 = PoolSize();

            Say("[CENSUS] " + listRequests + " requests, each with its own equal-contents List: " + pool2 + " materials");
            Check("census: a second pass with a fresh equal-contents List allocates a second full matrix",
                  pool2 == created * 2, "pool=" + pool2 + " expected=" + (created * 2));

            int tintFree = Variants * Masks;
            int redundantTint = created - tintFree;
            int redundantList = pool2 - created;

            Say("[CENSUS] with the tint carried as a material property instead of a key: " + tintFree + " materials");
            Say("[CENSUS] redundant under that model: " + redundantTint + " of " + created
                + " (" + (100.0 * redundantTint / created).ToString("0.0") + "%)");
            Say("[CENSUS] redundant purely to List instances: " + redundantList + " of " + pool2);

            // One more number the mod cares about: one material per thing instance instead of per key.
            Say("[CENSUS] if each of 500 things asked for its own colour with its own List: "
                + (Variants * 500 * Masks) + " materials where " + tintFree + " would draw the same pixels");

            Rows.AppendLine();
            Rows.AppendLine("| census | requests | materials | redundant |");
            Rows.AppendLine("|---|---|---|---|");
            Rows.AppendLine("| " + Variants + " variants x " + Colors + " colours x " + Masks + " masks | "
                + created + " | " + pool1 + " | 0 by appearance, " + redundantTint + " vs a draw-time tint |");
            Rows.AppendLine("| same matrix, second pass with a fresh equal-contents List | " + listRequests + " | " + pool2 + " | "
                + redundantList + " (the List dimension) |");
            Rows.AppendLine("| draw-time tint (the floor) | " + tintFree + " | " + tintFree + " | - |");
        }

        // ------------------------------------------------------------------ output

        static void WriteTable(string path)
        {
            var md = new StringBuilder();
            md.AppendLine("### matpool - Verse.MaterialPool key, executed against dlls/Assembly-CSharp.dll");
            md.AppendLine();
            md.AppendLine("* `MatFrom` itself, `MaterialRequest.Equals` and `GetHashCode` are the game's own compiled code;");
            md.AppendLine("  UnityEngine is the minimal stub in `mat/Stub/` (no engine, no GPU, no game process).");
            md.AppendLine("* the shader is `ShaderDatabase.CutoutComplex`, which declares `_MaskTex`, so case C is testable.");
            md.AppendLine();
            md.Append(Rows);
            md.AppendLine();
            md.AppendLine("### conclusions");
            md.AppendLine();
            md.AppendLine("1. The key is (shader, mainTex, colour, colourTwo, maskTex, renderQueue, secondaryTex, shaderParameters-by-reference).");
            md.AppendLine("   Same texture + same colour is one instance; a different colour or mask is a new one.");
            md.AppendLine("2. Per-instance tinting does inflate the pool: colour is part of the key, and the pool is a static dictionary");
            md.AppendLine("   that is never evicted. 5 variants x 3 colours x 2 masks = 30 materials, 20 more than a draw-time tint needs.");
            md.AppendLine("3. Colours are quantised through Color32 before lookup, so differences below 1/255 collapse into one material.");
            md.AppendLine("4. `shaderParameters` is compared by reference (`ceq`): equal contents in a new List is a MISS. This is the");
            md.AppendLine("   unbounded part - it multiplies whatever the colour dimension already produced.");
            md.AppendLine("5. Nothing is evicted: matDictionary is append-only (the type has Add, no Remove and no Clear), so a correct");
            md.AppendLine("   material is never pushed out by a wrong one. The failure mode is bloat, not mis-rendering - with one");
            md.AppendLine("   caveat that runs the other way: because colours are collapsed to 8 bit before keying, a request whose tint");
            md.AppendLine("   differs by less than 1/255 is served the *first* material's tint. That is invisible, but it is the only");
            md.AppendLine("   sense in which the pool can return a material that is not exactly the one asked for.");

            Console.WriteLine();
            Console.Write(md.ToString());

            if (!string.IsNullOrEmpty(path))
            {
                string dir = Path.GetDirectoryName(Path.GetFullPath(path));
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(path, md.ToString());
            }
        }

        static void SetStatic(Type t, string n, object v)
        {
            FieldInfo f = t.GetField(n, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (f != null) f.SetValue(null, v);
        }

        static string Arg(string[] a, string name)
        {
            for (int i = 0; i < a.Length - 1; i++) if (a[i] == name) return a[i + 1];
            return null;
        }
    }
}
