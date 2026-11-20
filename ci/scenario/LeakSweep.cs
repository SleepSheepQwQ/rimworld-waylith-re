using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using RimWorld;
using UnityEngine;
using Verse;

/// <summary>
/// The extension-leak experiment, executed inside the real game process.
///
/// It reads the two ThingDefs ci/leakmod/Defs.xml adds (one pointing at ordinary
/// "X.png" textures, one at "X.png.png" textures whose asset name keeps .png),
/// prints the subGraphics table Graphic_Collection.Init built, then sweeps
/// thingIDNumber 0..19 through Widgets.GetIconFor and measures every icon.
///
/// The only metric that decides "blank" is measured on the icon pixels:
///   coverage = opaque pixels / all pixels
///   structure = mean |luma(x) - luma(x+1)| over adjacent opaque pixels
/// A real revolver sprite is ~13% covered and has edge energy ~12; the _m mask
/// is 100% covered, one flat near-black plane, edge energy ~0.2.
/// </summary>
internal static class LeakSweep
{
    private const string T = "[SCENARIO]";
    private const int Ids = 20;

    private static readonly List<string> Failed = new List<string>();
    private static readonly string[] TextureExtensions = { ".png", ".jpg", ".jpeg", ".psd", ".dds" };

    private sealed class Row
    {
        public string Def;
        public int Id;
        public int Index;
        public string Texture;
        public bool Mask;
        public Stats Stats;
    }

    private struct Stats
    {
        public int W, H, Opaque, Distinct, BBoxW, BBoxH;
        public double Coverage, R, G, B, A, Grad;
        public bool Valid;
    }

    public static List<string> Run(Map map, string outDir)
    {
        Failed.Clear();
        var table = new List<string>
        {
            "| id | id % n | texture | mask | coverage | meanRGB | structure | colors |",
            "|---|---|---|---|---|---|---|---|",
        };

        ThingDef ext = DefDatabase<ThingDef>.GetNamedSilentFail("Gun_Revolver_LeakExt");
        ThingDef plain = DefDatabase<ThingDef>.GetNamedSilentFail("Gun_Revolver_LeakPlain");
        Check("leak-defs-present", ext != null && plain != null,
              $"ext={(ext == null ? "<null>" : ext.defName)} plain={(plain == null ? "<null>" : plain.defName)}");

        if (ext != null)
        {
            Console.Error.WriteLine($"{T} leak.section def={ext.defName} expectation=n10-half-masks");
            RunOne(ext, map, outDir, table, recordRows: true);
        }
        if (plain != null)
        {
            Console.Error.WriteLine($"{T} leak.section def={plain.defName} expectation=n5-no-masks (control)");
            RunOne(plain, map, outDir, table, recordRows: false);
        }

        try
        {
            string path = Path.Combine(outDir, "leak_table.md");
            File.WriteAllText(path, string.Join("\n", table) + "\n");
            Console.Error.WriteLine($"{T} leak.table={path} rows={table.Count - 2}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{T} leak.table write failed: {ex.GetType().Name}: {ex.Message}");
        }

        return new List<string>(Failed);
    }

    // ---------------------------------------------------------------- one def

    private static void RunOne(ThingDef def, Map map, string outDir, List<string> table, bool recordRows)
    {
        string tag = recordRows ? "leak-ext" : "leak-plain";
        Graphic outer = def.graphic;
        Graphic inner = Child(outer) ?? outer;
        var coll = inner as Graphic_Collection;
        int n = coll == null || coll.subGraphics == null ? 0 : coll.subGraphics.Length;

        var names = new List<string>();
        Console.Error.WriteLine($"{T} leak.tree def={def.defName} xml.graphicClass={def.graphicData?.graphicClass?.Name} " +
                                $"texPath={def.graphicData?.texPath} outer={outer.GetType().Name} inner={inner.GetType().Name} " +
                                $"subGraphics={n}");
        for (int i = 0; i < n; i++)
        {
            Graphic sg = coll.subGraphics[i];
            string path = Str(sg, "path");
            string texName = Name(sg);
            Stats st = Measure(Tex(sg));
            names.Add(texName);
            Console.Error.WriteLine($"{T} leak.sub def={def.defName} i={i} type={sg.GetType().Name} path={S(path)} " +
                                    $"tex={S(texName)} size={st.W}x{st.H} mask={IsMask(texName)} " +
                                    $"coverage={P(st.Coverage)} meanRGB={Rgb(st)} structure={F(st.Grad)} colors={st.Distinct}");
        }
        Console.Error.WriteLine($"{T} leak.names def={def.defName} n={n} names={string.Join(",", names)}");

        int maskVariants = names.Count(IsMask);
        bool alternating = n > 1 && Enumerable.Range(1, n - 1).All(i => IsMask(names[i]) != IsMask(names[i - 1]));
        bool allKeepPng = n > 0 && names.All(x => x != null && x.EndsWith(".png", StringComparison.Ordinal));

        if (recordRows)
        {
            Check("leak-ext-n-10", n == 10, $"subGraphics={n} names={string.Join(",", names)}");
            Check("leak-ext-mask-variants-5", maskVariants == 5,
                  $"maskFlavoured={maskVariants}/{n} names={string.Join(",", names)}");
            Check("leak-ext-names-keep-png", allKeepPng, $"names={string.Join(",", names)}");
            Check("leak-ext-alternating", alternating, $"names={string.Join(",", names)}");
        }
        else
        {
            Check("leak-plain-n-5", n == 5, $"subGraphics={n} names={string.Join(",", names)}");
            Check("leak-plain-mask-variants-0", maskVariants == 0,
                  $"maskFlavoured={maskVariants}/{n} names={string.Join(",", names)}");
        }

        var rows = new List<Row>();
        int badSize = 0, badIndex = 0, badMaskPattern = 0;
        for (int id = 0; id < Ids; id++)
        {
            Thing thing = null;
            Texture2D tex = null;
            try
            {
                thing = ThingMaker.MakeThing(def);
                thing.thingIDNumber = id;
                // Widgets.GetIconFor(Thing) only reads thing.Graphic -> ExtractInnerGraphicFor,
                // which for Graphic_Random calls SubGraphicFor(thing) (id % n); it never reads
                // Spawned/Position. Keep the sweep unspawned: the real map above is what proves
                // the rest of the scenario, and a half-built map mesh is not involved here.
                tex = Icon(thing);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"{T} leak.id={id} def={def.defName} threw {ex}");
            }
            finally
            {
                try { if (thing != null && thing.Spawned) thing.DeSpawn(DestroyMode.Vanish); }
                catch (Exception ex) { Console.Error.WriteLine($"{T} leak.cleanup id={id}: {ex.GetType().Name}"); }
            }

            string texName = tex == null ? "<null>" : tex.name;
            Stats st = Measure(tex);
            int idx = names.IndexOf(texName);
            bool mask = IsMask(texName);
            var row = new Row
            {
                Def = def.defName,
                Id = id,
                Index = idx,
                Texture = texName,
                Mask = mask,
                Stats = st,
            };
            rows.Add(row);

            if (st.W != 128 || st.H != 128) badSize++;
            if (n > 0 && idx != id % n) badIndex++;
            if (n > 0 && mask != IsMask(names[id % n])) badMaskPattern++;

            Console.Error.WriteLine($"{T} leak.row def={def.defName} id={id} mod={Mod(id, n)} idx={idx} tex={S(texName)} " +
                                    $"mask={mask} size={st.W}x{st.H} coverage={P(st.Coverage)} meanRGB={Rgb(st)} " +
                                    $"structure={F(st.Grad)} colors={st.Distinct}");

            if (recordRows)
                table.Add($"| {id} | {Mod(id, n)} | {S(texName)} | {(mask ? "**yes**" : "no")} | {P(st.Coverage)} | " +
                          $"{Rgb(st)} | {F(st.Grad)} | {st.Distinct} |");

            if (tex != null)
            {
                try
                {
                    string p = Path.Combine(outDir, $"leak_icon_{def.defName}_{id}.png");
                    StartupHook.WritePng(p, tex.width, tex.height, tex.GetPixels32());
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"{T} leak.png id={id} failed: {ex.GetType().Name}: {ex.Message}");
                }
            }

            if (id == 0)
            {
                // report.py requires an icon= line for a RESULT=OK run; this is the
                // same signature the normal scenario prints, for the same call.
                Console.Error.WriteLine($"{T} icon=def={def.defName} tex={S(texName)} size={st.W}x{st.H} " +
                                        $"badTex={(tex == null || tex == BaseContent.BadTex ? "true" : "false")} " +
                                        $"placeholder={(st.W > 0 && st.W <= 2 && st.H <= 2 ? "true" : "false")} mask={mask} " +
                                        $"variantIndex={idx} expectedIndex={Mod(id, n)} variants={n}");
            }
        }

        var maskRows = rows.Where(r => r.Mask).ToList();
        var artRows = rows.Where(r => !r.Mask).ToList();
        double maskCovMin = maskRows.Count == 0 ? 0 : maskRows.Min(r => r.Stats.Coverage);
        double maskGradMax = maskRows.Count == 0 ? 0 : maskRows.Max(r => r.Stats.Grad);
        double artCovMin = artRows.Count == 0 ? 0 : artRows.Min(r => r.Stats.Coverage);
        double artCovMax = artRows.Count == 0 ? 0 : artRows.Max(r => r.Stats.Coverage);
        double artGradMin = artRows.Count == 0 ? 0 : artRows.Min(r => r.Stats.Grad);

        Console.Error.WriteLine($"{T} leak.summary def={def.defName} n={n} rows={rows.Count} maskVariants={maskVariants} " +
                                $"maskHits={maskRows.Count}/{rows.Count} maskCoverageMin={P(maskCovMin)} " +
                                $"maskStructureMax={F(maskGradMax)} artCoverage={P(artCovMin)}..{P(artCovMax)} " +
                                $"artStructureMin={F(artGradMin)}");

        if (recordRows)
        {
            Check("leak-ext-sweep-20", rows.Count == Ids, $"rows={rows.Count}/{Ids}");
            Check("leak-ext-index-is-id-mod-n", badIndex == 0, $"mismatches={badIndex}/{Ids} n={n}");
            Check("leak-ext-mask-follows-table", badMaskPattern == 0,
                  $"mismatches={badMaskPattern}/{Ids} order={string.Join(",", names)}");
            Check("leak-ext-half-masks", maskRows.Count * 2 == rows.Count && maskRows.Count > 0,
                  $"maskHits={maskRows.Count}/{rows.Count} (expected exactly half for id 0..19, n={n})");
            Check("leak-ext-mask-full-coverage", maskRows.Count > 0 && maskRows.All(r => r.Stats.Coverage >= 0.95),
                  $"minCoverage={P(maskCovMin)} (mask draws every pixel)");
            Check("leak-ext-mask-no-structure", maskRows.Count > 0 && maskRows.All(r =>
                      r.Stats.Grad <= 1.0 && r.Stats.G <= 1 && r.Stats.B <= 1 && r.Stats.Distinct <= 64),
                  $"maxStructure={F(maskGradMax)} (a real variant is ~12)");
            Check("leak-ext-art-structured", artRows.Count > 0 && artRows.All(r =>
                      r.Stats.Coverage >= 0.02 && r.Stats.Coverage <= 0.60 &&
                      r.Stats.G >= 50 && r.Stats.B >= 50 && r.Stats.Grad >= 3.0),
                  $"coverage={P(artCovMin)}..{P(artCovMax)} structureMin={F(artGradMin)} " +
                  $"meanRGB={Rgb(artRows.Count == 0 ? default : artRows[0].Stats)}");
            Check("leak-ext-icons-128", badSize == 0, $"wrongSize={badSize}/{Ids}");
        }
        else
        {
            Check("leak-plain-sweep-20", rows.Count == Ids, $"rows={rows.Count}/{Ids}");
            Check("leak-plain-index-is-id-mod-n", badIndex == 0, $"mismatches={badIndex}/{Ids} n={n}");
            Check("leak-plain-no-mask-hits", maskRows.Count == 0, $"maskHits={maskRows.Count}/{rows.Count}");
            Check("leak-plain-art-structured", artRows.Count == rows.Count && artRows.All(r =>
                      r.Stats.Coverage >= 0.02 && r.Stats.Coverage <= 0.60 &&
                      r.Stats.G >= 50 && r.Stats.B >= 50 && r.Stats.Grad >= 3.0),
                  $"coverage={P(artCovMin)}..{P(artCovMax)} structureMin={F(artGradMin)}");
            Check("leak-plain-icons-128", badSize == 0, $"wrongSize={badSize}/{Ids}");
        }
    }

    // ---------------------------------------------------------------- pixels

    private static Stats Measure(Texture2D tex)
    {
        var st = new Stats();
        if (tex == null) return st;
        st.W = tex.width;
        st.H = tex.height;
        Color32[] px;
        try { px = tex.GetPixels32(); }
        catch { return st; }
        if (px == null || px.Length != st.W * st.H) return st;

        long opaque = 0, sr = 0, sg = 0, sb = 0, sa = 0, gradCount = 0;
        double gradSum = 0;
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        var colors = new HashSet<int>();

        for (int y = 0; y < st.H; y++)
        {
            for (int x = 0; x < st.W; x++)
            {
                int i = y * st.W + x;
                Color32 c = px[i];
                sa += c.a;
                if (c.a == 0) continue;
                opaque++;
                sr += c.r; sg += c.g; sb += c.b;
                colors.Add((c.r << 16) | (c.g << 8) | c.b);
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;

                if (x + 1 < st.W)
                {
                    Color32 d = px[i + 1];
                    if (d.a != 0)
                    {
                        gradSum += Math.Abs(Luma(c) - Luma(d));
                        gradCount++;
                    }
                }
            }
        }

        st.Opaque = (int)opaque;
        st.Coverage = px.Length == 0 ? 0 : (double)opaque / px.Length;
        st.R = opaque == 0 ? 0 : (double)sr / opaque;
        st.G = opaque == 0 ? 0 : (double)sg / opaque;
        st.B = opaque == 0 ? 0 : (double)sb / opaque;
        st.A = px.Length == 0 ? 0 : (double)sa / px.Length;
        st.Grad = gradCount == 0 ? 0 : gradSum / gradCount;
        st.Distinct = colors.Count;
        st.BBoxW = maxX < 0 ? 0 : maxX - minX + 1;
        st.BBoxH = maxY < 0 ? 0 : maxY - minY + 1;
        st.Valid = true;
        return st;
    }

    private static double Luma(Color32 c) => 0.299 * c.r + 0.587 * c.g + 0.114 * c.b;

    // ---------------------------------------------------------------- helpers

    private static Texture2D Icon(Thing t)
    {
        float scale, angle;
        Vector2 proportions;
        Color color;
        Material mat;
        Texture icon = Widgets.GetIconFor(t, new Vector2(128f, 128f), null, stackOfOne: false,
                                           out scale, out angle, out proportions, out color, out mat);
        return icon as Texture2D;
    }

    private static Texture2D Tex(Graphic g)
    {
        try
        {
            Material m = g == null ? null : g.MatSingle;
            return m == null ? null : m.mainTexture as Texture2D;
        }
        catch { return null; }
    }

    private static string Name(Graphic g)
    {
        Texture2D t = Tex(g);
        if (t != null) return t.name;
        string p = Str(g, "path");
        return p == null ? null : Leaf(p);
    }

    /// <summary>One extension is what the loader peels; the test is on the stem.</summary>
    private static bool IsMask(string name)
    {
        string s = StripAll(name);
        if (string.IsNullOrEmpty(s) || s.Length <= 2) return false;
        if (!s.EndsWith("_m", StringComparison.Ordinal)) return false;
        char before = s[s.Length - 3];
        return before != 'm' && before != 'M';
    }

    private static string StripAll(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        bool peeled = true;
        while (peeled)
        {
            peeled = false;
            foreach (string ext in TextureExtensions)
            {
                if (s.Length <= ext.Length) continue;
                if (!s.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) continue;
                s = s.Substring(0, s.Length - ext.Length);
                peeled = true;
                break;
            }
        }
        return s;
    }

    private static Graphic Child(Graphic g)
    {
        if (g == null) return null;
        for (Type t = g.GetType(); t != null && t != typeof(object); t = t.BaseType)
        {
            FieldInfo f = t.GetField("subGraphic", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (f != null && typeof(Graphic).IsAssignableFrom(f.FieldType)) return f.GetValue(g) as Graphic;
        }
        return null;
    }

    private static string Str(object o, string name)
    {
        if (o == null) return null;
        Type t = o.GetType();
        try
        {
            FieldInfo f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (f != null && f.FieldType == typeof(string)) return (string)f.GetValue(o);
            PropertyInfo p = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (p != null && p.PropertyType == typeof(string) && p.GetIndexParameters().Length == 0)
                return (string)p.GetValue(o);
        }
        catch { }
        return null;
    }

    private static int Mod(int id, int n) => n <= 0 ? -1 : id % n;
    private static string Leaf(string p) => string.IsNullOrEmpty(p) ? null : p.Substring(p.LastIndexOf('/') + 1);
    private static string S(string s) => string.IsNullOrEmpty(s) ? "<null>" : s;
    private static string P(double v) => (v * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%";
    private static string F(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);
    private static string Rgb(Stats s) => $"({s.R.ToString("0", CultureInfo.InvariantCulture)}," +
                                          $"{s.G.ToString("0", CultureInfo.InvariantCulture)}," +
                                          $"{s.B.ToString("0", CultureInfo.InvariantCulture)})";

    private static void Check(string name, bool ok, string detail)
    {
        Console.Error.WriteLine($"{T} assert={name} ok={(ok ? "true" : "false")} {detail}");
        if (!ok) Failed.Add(name);
    }
}
