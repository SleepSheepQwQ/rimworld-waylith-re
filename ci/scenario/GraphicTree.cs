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
/// Prints the graphic tree of one ThingDef exactly as the shipped classes build it, then maps
/// thingIDNumber 0..19 to the icon the game's own Widgets.GetIconFor returns, and asserts both.
///
/// Nothing here re-implements the game:
///
///   def.graphic                        -> Graphic_RandomRotated   (Verse)
///     .subGraphic                      -> Graphic_Random          (Graphic_Collection)
///       .subGraphics[i]                -> Graphic_Single          (one per <texPath>X texture)
///
/// The wrapper exists because the def inherits &lt;onGroundRandomRotateAngle&gt;35&lt;/&gt; from
/// BaseWeapons.xml; the inner class is the def's own &lt;graphicClass&gt;Graphic_Random&lt;/&gt;.
/// The sweep then uses Graphic_Random.SubGraphicFor(thing) - thing.OverrideGraphicIndex ??
/// thingIDNumber % subGraphics.Length - and Widgets.GetIconFor(thing, ...), the exact call
/// Widgets.ThingIcon makes.
/// </summary>
internal static class GraphicTree
{
    private const string T = "[SCENARIO]";
    private const int Ids = 20;

    private static readonly List<string> Lines = new List<string>();
    private static readonly List<string> Failed = new List<string>();
    private static string OutDir = ".";

    /// <summary>Prints the tree and the sweep; returns the names of the assertions that failed.</summary>
    public static List<string> Run(ThingDef def, Thing probe, Map map, List<string> variants, List<string> masks, string outDir)
    {
        Lines.Clear();
        Failed.Clear();
        OutDir = string.IsNullOrEmpty(outDir) ? "." : outDir;

        var clock = System.Diagnostics.Stopwatch.StartNew();
        try { Walk(def, probe); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{T} tree.stack:\n{ex}");
            Ok("tree-printed", false, "exception " + ex.GetType().Name);
        }
        try { Sweep(def, map, variants, masks); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{T} sweep.stack:\n{ex}");
            Ok("sweep-ran", false, "exception " + ex.GetType().Name);
        }

        Console.Error.WriteLine($"{T} tree.lines={Lines.Count} failed={Failed.Count} elapsed_ms={clock.ElapsedMilliseconds}");
        try
        {
            string p = Path.Combine(OutDir, "tree.txt");
            File.WriteAllText(p, string.Join("\n", Lines) + "\n");
            Console.Error.WriteLine($"{T} tree.file={p}");
        }
        catch (Exception ex) { Console.Error.WriteLine($"{T} tree.file failed: {ex.GetType().Name}: {ex.Message}"); }

        return new List<string>(Failed);
    }

    // ---------------------------------------------------------------- the tree, layer by layer

    private static void Walk(ThingDef def, Thing probe)
    {
        Graphic outer = def.graphic;
        Graphic inner = Child(outer);
        var coll = inner as Graphic_Collection;
        int n = coll == null || coll.subGraphics == null ? 0 : coll.subGraphics.Length;

        Say($"tree.def={def.defName} xml.graphicClass={def.graphicData.graphicClass.Name} xml.texPath={def.graphicData.texPath} " +
            $"xml.shaderType={(def.graphicData.shaderType == null ? "<null>" : def.graphicData.shaderType.defName)} " +
            $"xml.drawSize={V2(def.graphicData.drawSize)} xml.maskPath={S(Str(def.graphicData, "maskPath"))} " +
            $"def.onGroundRandomRotateAngle={Fl(def, "onGroundRandomRotateAngle")}");
        Say($"tree.L0 type={outer.GetType().Name} path={S(Str(outer, "path"))} drawSize={V2(Vec2(outer, "drawSize"))} " +
            $"color={Col(outer)} child={(inner == null ? "<none>" : inner.GetType().Name)}");
        if (inner != null)
            Say($"tree.L1 type={inner.GetType().Name} path={S(Str(inner, "path"))} drawSize={V2(Vec2(inner, "drawSize"))} " +
                $"color={Col(inner)} subGraphics={(coll == null ? -1 : n)}");

        // the folder the texPath names, as the content system sees it
        var folder = new List<string>();
        try
        {
            folder = ContentFinder<Texture2D>.GetAllInFolder(def.graphicData.texPath)
                .Select(t => t.name).OrderBy(x => x, StringComparer.Ordinal).ToList();
        }
        catch (Exception ex) { Say($"tree.folder=FAILED {ex.GetType().Name}: {ex.Message}"); }
        var maskSet = new HashSet<string>(folder.Where(x => x.EndsWith("_m", StringComparison.Ordinal)), StringComparer.Ordinal);
        Say($"tree.folder={def.graphicData.texPath} textures={folder.Count} names={string.Join(",", folder)}");

        int ext = 0, missing = 0, maskInTable = 0, underFolder = 0;
        for (int i = 0; i < n; i++)
        {
            Graphic sg = coll.subGraphics[i];
            string path = Str(sg, "path");
            string leaf = Leaf(path);

            Texture2D tex = null;
            try
            {
                Material m = sg.MatSingle;
                tex = m == null ? null : m.mainTexture as Texture2D;
            }
            catch (Exception ex) { Say($"tree.L2[{i}] mat-single threw {ex.GetType().Name}: {ex.Message}"); }

            string declaredMask = Str(sg, "maskPath");
            string maskPath = string.IsNullOrEmpty(declaredMask) ? (path == null ? null : path + "_m") : declaredMask;
            Texture2D maskTex = Find(maskPath);

            if (leaf != null && leaf.IndexOf('.') >= 0)
            {
                ext++;
                Say($"tree.bad-path[{i}] path={path} <-- the leaf carries an extension");
            }
            if (Find(path) == null)
            {
                missing++;
                Say($"tree.bad-path[{i}] path={path} <-- does not resolve to a texture");
            }
            if (leaf != null && maskSet.Contains(leaf)) maskInTable++;
            if (path != null && path.StartsWith(def.graphicData.texPath + "/", StringComparison.Ordinal)) underFolder++;

            Say($"tree.L2[{i}] type={sg.GetType().Name} path={S(path)} drawSize={V2(Vec2(sg, "drawSize"))} " +
                $"tex={(tex == null ? "<null>" : tex.name)} size={(tex == null ? "-" : tex.width + "x" + tex.height)} " +
                $"maskPath={S(maskPath)} maskResolves={(maskTex != null)} maskSize={(maskTex == null ? "-" : maskTex.width + "x" + maskTex.height)}");
        }

        // GraphicUtility.ExtractInnerGraphicFor: what the draw/icon path calls to unwrap, executed.
        try
        {
            var mi = typeof(GraphicUtility).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.Name == "ExtractInnerGraphicFor")
                .OrderBy(m => m.GetParameters().Length).FirstOrDefault();
            if (mi == null) Say("tree.extractInner=<method not found>");
            else
            {
                ParameterInfo[] ps = mi.GetParameters();
                var args = new object[ps.Length];
                for (int i = 0; i < ps.Length; i++)
                    args[i] = i == 0 ? (object)outer : (ps[i].ParameterType == typeof(Thing) ? probe : null);
                var r = mi.Invoke(null, args) as Graphic;
                Say($"tree.extractInner method=GraphicUtility.ExtractInnerGraphicFor({string.Join(",", ps.Select(p => p.ParameterType.Name))}) " +
                    $"in={outer.GetType().Name} out={(r == null ? "<null>" : r.GetType().Name)} path={S(Str(r, "path"))}");
                if (r != null) Ok("tree-extract-inner", ReferenceEquals(r, inner) || r is Graphic_Collection,
                                  $"out={r.GetType().Name} sameInstance={ReferenceEquals(r, inner)}");
            }
        }
        catch (Exception ex) { Say($"tree.extractInner=threw {ex.GetType().Name}: {ex.Message}"); }

        int layers = 1 + (inner == null ? 0 : 1) + (n > 0 ? 1 : 0);
        Say($"tree.summary layers={layers} nodes={1 + (inner == null ? 0 : 1) + n} leaves={n} " +
            $"outer={outer.GetType().Name} inner={(inner == null ? "<null>" : inner.GetType().Name)} " +
            $"masksInFolder={maskSet.Count} masksInTable={maskInTable}");

        Ok("tree-outer-randomrotated", outer is Graphic_RandomRotated, $"type={outer.GetType().Name}");
        Ok("tree-inner-collection", coll != null, $"inner={(inner == null ? "<null>" : inner.GetType().Name)}");
        Ok("tree-subgraphics", n == 5, $"subGraphics.Length={n}");
        Ok("tree-sub-paths-extensionless", ext == 0, $"pathsWithExtension={ext}/{n}");
        Ok("tree-sub-paths-resolve", missing == 0, $"unresolved={missing}/{n}");
        Ok("tree-sub-paths-under-folder", underFolder == n, $"{underFolder}/{n} under {def.graphicData.texPath}");
        Ok("tree-mask-not-in-table", maskInTable == 0, $"maskTextures={maskSet.Count} collisions={maskInTable}");
    }

    // ---------------------------------------------------------------- thingIDNumber -> icon

    private static void Sweep(ThingDef def, Map map, List<string> variants, List<string> masks)
    {
        Graphic outer = def.graphic;
        Graphic inner = Child(outer) ?? outer;
        MethodInfo subFor = inner.GetType().GetMethod("SubGraphicFor", BindingFlags.Instance | BindingFlags.Public,
                                                      null, new[] { typeof(Thing) }, null);
        int mod = Math.Max(1, variants.Count);
        Say($"sweep.graphic={inner.GetType().Name} subGraphicFor={(subFor == null ? "<none>" : "public Graphic SubGraphicFor(Thing)")} " +
            $"variants={variants.Count} modulus={mod} id={IdFieldState()}");

        // A throwaway probe decides whether the icon path needs the thing spawned. If the unspawned
        // call throws, the whole sweep spawns its probe things instead (and says so).
        int mode = 0;
        try { Icon(Make(def, map, 0, false)); }
        catch (Exception ex)
        {
            mode = 1;
            Say($"sweep.notes=unspawned GetIconFor threw {ex.GetType().Name} ({ex.Message}); spawning each probe instead");
        }
        Say($"sweep.mode={(mode == 0 ? "unspawned" : "spawned")}");

        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        int ran = 0, badRepeat = 0, badFresh = 0, badMod = 0, badSub = 0, maskHits = 0, differ = 0;
        string first = null;

        for (int id = 0; id < Ids; id++)
        {
            Thing a = Make(def, map, id, mode == 1);
            Texture2D t1 = Icon(a);
            Texture2D t2 = Icon(a);
            Thing b = Make(def, map, id, mode == 1);
            Texture2D t3 = Icon(b);

            string direct = null;
            if (subFor != null)
            {
                var picked = (Graphic)subFor.Invoke(inner, new object[] { a });
                direct = picked == null ? "<null>" : Leaf(Str(picked, "path"));
            }
            Cleanup(a);
            Cleanup(b);

            string n1 = Name(t1), n2 = Name(t2), n3 = Name(t3);
            int idx = variants.IndexOf(n1);
            int expect = id % mod;
            bool sameRepeat = string.Equals(n1, n2, StringComparison.Ordinal);
            bool sameFresh = string.Equals(n1, n3, StringComparison.Ordinal);
            bool modOk = idx == expect;
            bool isMask = masks.Contains(n1) || n1.EndsWith("_m", StringComparison.Ordinal);
            bool subOk = direct == null || string.Equals(direct, n1, StringComparison.Ordinal);

            if (!sameRepeat) badRepeat++;
            if (!sameFresh) badFresh++;
            if (!modOk) badMod++;
            if (!subOk) badSub++;
            if (isMask) maskHits++;
            if (First(name) == null) { }
            if (first == null) first = n1;
            else if (!string.Equals(n1, first, StringComparison.Ordinal)) differ++;
            seen[n1] = seen.TryGetValue(n1, out int c) ? c + 1 : 1;
            ran++;

            Say($"sweep id={id} tex={n1} idx={idx} expected={expect} repeat={n2} freshThing={n3} " +
                $"subGraphicFor={direct ?? "<n/a>"} sameRepeat={sameRepeat} sameFresh={sameFresh} " +
                $"size={(t1 == null ? "-" : t1.width + "x" + t1.height)} mask={isMask}");
        }

        int per = Ids / mod;
        bool balanced = seen.Count == mod && per * mod == Ids && seen.Values.All(c => c == per);
        Ok("sweep-ran", ran == Ids, $"ran={ran}/{Ids}");
        Ok("sweep-repeat-idempotent", badRepeat == 0, $"same-id repeat mismatches={badRepeat}/{Ids}");
        Ok("sweep-fresh-thing-same-id", badFresh == 0, $"fresh-instance mismatches={badFresh}/{Ids}");
        Ok("sweep-id-modulo", badMod == 0, $"mismatches={badMod}/{Ids} mapping=thingIDNumber%{mod}");
        Ok("sweep-subgraphicfor-agrees", badSub == 0, $"mismatches={badSub}/{Ids}");
        Ok("sweep-variant-coverage", balanced,
           $"distinct={seen.Count}/{mod} counts={string.Join(",", seen.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => k.Key + "=" + k.Value))}");
        Ok("sweep-no-mask", maskHits == 0, $"maskHits={maskHits}/{Ids}");
        Ok("sweep-ids-differ", differ > 0 && seen.Count > 1, $"ids that differ from id=0: {differ}/{Ids} distinct={seen.Count}");

        Say($"sweep.histogram={string.Join(",", seen.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => k.Key + "=" + k.Value))}");
        bool unstable = badRepeat + badFresh + badMod > 0;
        Say($"DETERMINISM={(unstable ? "UNSTABLE" : "deterministic")} mapping=thingIDNumber%{mod} variants={mod} " +
            $"ids=0..{Ids - 1} calls={Ids * 3} repeatMismatches={badRepeat} freshMismatches={badFresh} " +
            $"moduloMismatches={badMod} maskHits={maskHits} maskInTable=0");
    }

    private static string First(string s) => s;

    // ---------------------------------------------------------------- helpers

    private static Thing Make(ThingDef def, Map map, int id, bool spawn)
    {
        Thing t = ThingMaker.MakeThing(def);
        if (spawn && map != null)
        {
            IntVec3 cell;
            if (!CellFinder.TryFindRandomCellNear(map.Center, map, 30, c => c.Standable(map), out cell)) cell = map.Center;
            GenSpawn.Spawn(t, cell, map, WipeMode.Vanish);
        }
        t.thingIDNumber = id;
        return t;
    }

    /// <summary>GenSpawn assigned the id the map's lister keyed this thing by; restore before removing.</summary>
    private static void Cleanup(Thing t)
    {
        try { if (t.Spawned) t.DeSpawn(DestroyMode.Vanish); }
        catch (Exception ex) { Console.Error.WriteLine($"{T} sweep.cleanup failed: {ex.GetType().Name}: {ex.Message}"); }
    }

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

    private static string Name(Texture2D t) => t == null ? "<null>" : t.name;

    /// <summary>The sub-graphic a wrapper points at (Graphic_RandomRotated.subGraphic), by reflection.</summary>
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

    private static Texture2D Find(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        try { return ContentFinder<Texture2D>.Get(path, false); }
        catch { return null; }
    }

    private static string IdFieldState()
    {
        try
        {
            FieldInfo f = typeof(Thing).GetField("thingIDNumber", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return f == null ? "<none>" : $"thingIDNumber:{f.FieldType.Name}:public={f.IsPublic}:readonly={f.IsInitOnly}";
        }
        catch (Exception ex) { return "err:" + ex.GetType().Name; }
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
            if (p != null && p.PropertyType == typeof(string) && p.GetIndexParameters().Length == 0) return (string)p.GetValue(o);
        }
        catch { }
        return null;
    }

    private static Vector2 Vec2(object o, string name)
    {
        if (o == null) return default;
        Type t = o.GetType();
        try
        {
            FieldInfo f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (f != null && f.FieldType == typeof(Vector2)) return (Vector2)f.GetValue(o);
            PropertyInfo p = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (p != null && p.PropertyType == typeof(Vector2) && p.GetIndexParameters().Length == 0) return (Vector2)p.GetValue(o);
        }
        catch { }
        return default;
    }

    private static string Col(object o)
    {
        if (o == null) return "-";
        Type t = o.GetType();
        try
        {
            FieldInfo f = t.GetField("color", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (f != null && f.FieldType == typeof(Color))
            {
                Color c = (Color)f.GetValue(o);
                return $"{N(c.r)},{N(c.g)},{N(c.b)},{N(c.a)}";
            }
        }
        catch { }
        return "-";
    }

    private static float Fl(object o, string name)
    {
        if (o == null) return 0f;
        Type t = o.GetType();
        try
        {
            FieldInfo f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (f != null && f.FieldType == typeof(float)) return (float)f.GetValue(o);
            PropertyInfo p = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (p != null && p.PropertyType == typeof(float)) return (float)p.GetValue(o);
        }
        catch { }
        return 0f;
    }

    private static string V2(Vector2 v) => $"{N(v.x)}x{N(v.y)}";
    private static string N(float f) => f.ToString("0.###", CultureInfo.InvariantCulture);
    private static string S(string s) => string.IsNullOrEmpty(s) ? "<none>" : s;
    private static string Leaf(string path) =>
        string.IsNullOrEmpty(path) ? "<none>" : path.Substring(path.LastIndexOf('/') + 1);

    private static void Say(string line)
    {
        Lines.Add(line);
        Console.Error.WriteLine($"{T} {line}");
    }

    private static void Ok(string name, bool ok, string detail)
    {
        Console.Error.WriteLine($"{T} assert={name} ok={(ok ? "true" : "false")} {detail}");
        if (!ok) Failed.Add(name);
    }
}
