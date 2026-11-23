using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using RimWorld;
using UnityEngine;
using Verse;

/// <summary>
/// Measures the unique-weapon icon chain inside the real host, one def per run-class:
///
///   [UNIQ] def=Gun_Revolver_Unique present=true graphic=Graphic_RandomRotated inner=Graphic_Random subCount=5 masks=0
///   [UNIQ] def=... sub[0] path=... tex=... size=128x128 fmt=DXT1 px=...
///   [UNIQ] def=... uiIcon ... uiIconMaterial shader=CutoutComplexUI props=0
///   [UNIQ] def=... getIconForDef tex=... cov=...
///   [UNIQ] def=... instance id=0 sub=... icon=... mask=false cov=...
///   [UNIQ] def=... gate ... verdict=DRAW|MASK-VARIANTS|BLANK reason=...
///
/// The decision rule, and nothing else, decides the verdict:
///   BLANK          uiIcon is null / BaseContent.BadTex / <=2x2, or GetIconFor(def) has zero
///                  non-transparent texels. Widgets.ThingIcon(Rect, ThingDef) returns before
///                  GetIconFor in the first three cases, so nothing is drawn at all.
///   MASK-VARIANTS  subCount is larger than the art count because _m masks became subgraphics:
///                  Graphic_Random picks subGraphics[thingIDNumber % n], so the mask entries are
///                  drawn as icons for part of the ids (the shipped leak).
///   DRAW-MATERIAL  texture is real with pixels, but uiIconMaterial is a CutoutComplexUI material
///                  (measured elsewhere: 0 of 16384 pixels when _MainTex_ST is not identity).
///   DRAW           real texture, opaque pixels, no uiIconMaterial -> the plain GUI.DrawTexture
///                  branch every Cutout species' icon already uses.
/// </summary>
internal static class StartupHook
{
    private const string T = "[UNIQ]";
    private const string DefaultDefs = "Gun_Revolver_Unique,UniqueRevolverFixturePlain,UniqueRevolverFixtureLeak,UniqueRevolverFixtureDx10";
    private const int Ids = 10;

    private static readonly HashSet<string> Failed = new HashSet<string>(StringComparer.Ordinal);
    private static readonly string[] Extensions = { ".png", ".jpg", ".jpeg", ".psd", ".dds" };

    public static void Initialize()
    {
        new Thread(WaitForBoot) { IsBackground = true, Name = "uniq-waiter" }.Start();
        new Thread(Watchdog) { IsBackground = true, Name = "uniq-watchdog" }.Start();
    }

    private static void Watchdog()
    {
        int ms = int.TryParse(Environment.GetEnvironmentVariable("PROBE_TIMEOUT_MS"), out int v) ? v : 300000;
        Thread.Sleep(ms);
        Console.WriteLine($"{T} watchdog: boot never completed");
        Console.Out.Flush();
        Environment.Exit(9);
    }

    private static void WaitForBoot()
    {
        while (Waylith.Runtime.WaylithRuntime.Current == null) Thread.Sleep(20);
        Waylith.Runtime.WaylithRuntime.Current.App.BootCompleted += Run;
        Console.Error.WriteLine($"{T} armed");
    }

    private static void Run()
    {
        try
        {
            Console.WriteLine($"{T} boot activeMods={ModsConfig.ActiveModsInLoadOrder.Count()}"
                              + $" thingDefs={DefDatabase<ThingDef>.AllDefsListForReading.Count}");
            foreach (ModMetaData m in ModsConfig.ActiveModsInLoadOrder)
                Console.WriteLine($"{T} active={m.PackageId}");

            // The harness never runs StaticConstructorOnStartup; the phone does. Emulate it so a
            // GlyphFix-style ctor is executed before anything is measured.
            Type startup = null;
            try
            {
                startup = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
                    .FirstOrDefault(t => t.FullName == "AgentTouchFix.Bootstrap");
            }
            catch { }
            if (startup != null)
            {
                System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(startup.TypeHandle);
                Console.WriteLine($"{T} startup ctor invoked (harness emulation)");
            }
            else Console.WriteLine($"{T} startup ctor: GlyphFix not installed");

            string[] defs = (Environment.GetEnvironmentVariable("UNIQ_DEFS") ?? DefaultDefs)
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
            Console.WriteLine($"{T} defs={string.Join(",", defs)}");

            foreach (string dn in defs)
            {
                try { Measure(dn); }
                catch (Exception e) { Console.WriteLine($"{T} def={dn} fatal {e.GetType().Name}: {One(e.Message)}"); Fail("fatal:" + dn); }
            }
        }
        catch (Exception e)
        {
            Console.WriteLine($"{T} fatal {e.GetType().Name}: {One(e.Message)}");
            Fail("run");
        }
        finally
        {
            Console.WriteLine($"{T} RESULT={(Failed.Count == 0 ? "OK" : "FAIL")}"
                              + (Failed.Count == 0 ? "" : " failed=" + string.Join(",", Failed)));
            Console.Out.Flush();
            try { Root.Shutdown(); } catch { }
            Environment.Exit(0);
        }
    }

    // ------------------------------------------------------------------ one def, all layers

    private static void Measure(string defName)
    {
        ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(defName);
        if (def == null)
        {
            Console.WriteLine($"{T} def={defName} present=false note=def-not-loaded");
            return;
        }

        GraphicData gd = def.graphicData;
        Console.WriteLine($"{T} def={defName} present=true label='{One(def.label)}'"
                          + $" texPath={(gd == null ? "<null>" : gd.texPath)}"
                          + $" xml.graphicClass={(gd == null || gd.graphicClass == null ? "<null>" : gd.graphicClass.Name)}"
                          + $" xml.shader={(gd == null || gd.shaderType == null ? "<null>" : gd.shaderType.defName)}"
                          + $" xml.drawSize={(gd == null ? "-" : V2(gd.drawSize))}"
                          + $" uiIconPath={(def.uiIconPath.NullOrEmpty() ? "<null>" : def.uiIconPath)}");

        Graphic outer = def.graphic;
        if (outer == null)
        {
            Console.WriteLine($"{T} def={defName} graphic=<null> verdict=BLANK reason=def.graphic-null");
            Fail("graphic:" + defName);
            return;
        }
        Graphic inner = Unwrap(outer);
        var coll = inner as Graphic_Collection;
        Graphic[] subs = coll == null || coll.subGraphics == null ? Array.Empty<Graphic>() : coll.subGraphics;
        Console.WriteLine($"{T} def={defName} graphic outer={outer.GetType().Name} path='{S(outer.path)}'"
                          + $" inner={inner.GetType().Name} path='{S(inner.path)}' subCount={subs.Length}"
                          + $" isRandom={inner is Graphic_Random}");

        var names = new List<string>();
        int masks = 0;
        for (int i = 0; i < subs.Length; i++)
        {
            string leaf = Leaf(subs[i] == null ? null : subs[i].path);
            names.Add(leaf);
            if (IsMask(leaf)) masks++;
        }
        Console.WriteLine($"{T} def={defName} names={string.Join("|", names)} masks={masks} art={subs.Length - masks}");

        // Per-subgraphic texture, so "the entry exists" and "the entry draws" stay separate.
        for (int i = 0; i < subs.Length; i++)
        {
            Graphic sg = subs[i];
            string path = sg == null ? "<null>" : S(sg.path);
            Material m = null;
            string matErr = null;
            try { m = sg == null ? null : sg.MatSingle; }
            catch (Exception e) { matErr = "!" + e.GetType().Name; }
            Texture2D tex = m == null ? null : m.mainTexture as Texture2D;
            Console.WriteLine($"{T} def={defName} sub[{i}] mask={IsMask(names[i])} path='{path}'"
                              + $" tex={(tex == null ? "<null>" : "'" + tex.name + "'")}"
                              + $" size={(tex == null ? "-" : tex.width + "x" + tex.height)}"
                              + $" fmt={(tex == null ? "-" : tex.format.ToString())}"
                              + $" shader={(m == null || m.shader == null ? "<null>" : m.shader.name)}"
                              + $" mat={(m == null ? "<null>" : m.name)}{(matErr == null ? "" : " matErr=" + matErr)}"
                              + $" px={Px(tex)}");
        }

        // What the folder actually holds, as the content system sees it (empty = pack not mounted).
        string texPath = gd == null ? null : gd.texPath;
        var folder = new List<Texture2D>();
        try
        {
            if (!texPath.NullOrEmpty()) folder = ContentFinder<Texture2D>.GetAllInFolder(texPath).ToList();
        }
        catch (Exception e) { Console.WriteLine($"{T} def={defName} folder EXC {e.GetType().Name}: {One(e.Message)}"); }
        Console.WriteLine($"{T} def={defName} folder texPath={(texPath ?? "<null>")} textures={folder.Count}"
                          + $" names={string.Join("|", folder.Select(t => t == null ? "<null>" : t.name))}");
        for (int i = 0; i < folder.Count; i++)
        {
            Texture2D t = folder[i];
            Console.WriteLine($"{T} def={defName} folder[{i}] name='{(t == null ? "<null>" : t.name)}'"
                              + $" size={(t == null ? "-" : t.width + "x" + t.height)}"
                              + $" fmt={(t == null ? "-" : t.format.ToString())} px={Px(t)}");
        }

        // ResolveIcon's two outputs.
        Texture2D ui = def.uiIcon;
        bool uiNull = ui == null;
        bool uiBad = ui != null && BaseContent.BadTex != null && ReferenceEquals(ui, BaseContent.BadTex);
        bool uiPlaceholder = ui != null && ui.width <= 2 && ui.height <= 2;
        Console.WriteLine($"{T} def={defName} uiIcon null={uiNull} badTex={uiBad} placeholder={uiPlaceholder}"
                          + $" size={(ui == null ? "-" : ui.width + "x" + ui.height)} name='{(ui == null ? "<null>" : ui.name)}'"
                          + $" px={Px(ui)}");

        Material uim = def.uiIconMaterial;
        int props = -1;
        string propsErr = null;
        if (uim != null && uim.shader != null)
        {
            try { props = uim.shader.GetPropertyCount(); }
            catch (Exception e) { propsErr = "!" + e.GetType().Name + ":" + e.Message; }
        }
        Console.WriteLine($"{T} def={defName} uiIconMaterial null={uim == null}"
                          + $" name='{(uim == null ? "<null>" : uim.name)}'"
                          + $" shader='{(uim == null || uim.shader == null ? "<null>" : uim.shader.name)}'"
                          + $" props={(propsErr ?? props.ToString())}"
                          + $" main='{(uim == null || uim.mainTexture == null ? "<null>" : uim.mainTexture.name)}'");

        // The def-level icon a list/bill/gizmo draws.
        Texture2D defIcon = null;
        Material defMat = null;
        string defIconErr = null;
        try { defIcon = Widgets.GetIconFor(def, out defMat); }
        catch (Exception e) { defIconErr = "!" + e.GetType().Name + ":" + One(e.Message); }
        double defCov = Coverage(defIcon);
        Console.WriteLine($"{T} def={defName} getIconForDef tex='{(defIcon == null ? "<null>" : defIcon.name)}'"
                          + $" size={(defIcon == null ? "-" : defIcon.width + "x" + defIcon.height)}"
                          + $" cov={Pct(defCov)} material='{(defMat == null ? "<null>" : defMat.name)}'"
                          + $" shader='{(defMat == null || defMat.shader == null ? "<null>" : defMat.shader.name)}'"
                          + $" px={Px(defIcon)}{(defIconErr == null ? "" : " err=" + defIconErr)}");

        // Graphic_Random picks per instance: subGraphics[thingIDNumber % n].
        var rand = inner as Graphic_Random;
        int maskPicks = 0, artPicks = 0, nullPicks = 0, blankPicks = 0;
        var pickLines = new List<string>();
        for (int id = 0; id < Ids; id++)
        {
            Thing thing = null;
            Graphic picked = null;
            string subErr = null;
            try
            {
                thing = ThingMaker.MakeThing(def);
                thing.thingIDNumber = id;
                picked = rand == null ? null : rand.SubGraphicFor(thing);
            }
            catch (Exception e) { subErr = "!" + e.GetType().Name + ":" + One(e.Message); }

            string subLeaf = Leaf(picked == null ? null : picked.path);
            bool isMask = IsMask(subLeaf);

            Texture2D icon = null;
            Material iconMat = null;
            string iconErr = null;
            try
            {
                float sc, ang;
                Vector2 pr;
                Color col;
                if (thing != null)
                    icon = Widgets.GetIconFor(thing, new Vector2(128f, 128f), null, false,
                                              out sc, out ang, out pr, out col, out iconMat) as Texture2D;
            }
            catch (Exception e) { iconErr = "!" + e.GetType().Name + ":" + One(e.Message); }

            double cov = Coverage(icon);
            if (isMask) maskPicks++; else if (picked != null) artPicks++; else nullPicks++;
            if (icon == null || (BaseContent.BadTex != null && ReferenceEquals(icon, BaseContent.BadTex))
                || (icon != null && icon.width <= 2) || cov <= 0.0) blankPicks++;

            pickLines.Add($"{T} def={defName} instance id={id} sub='{subLeaf}' mask={isMask}"
                          + $" icon='{(icon == null ? "<null>" : icon.name)}'"
                          + $" size={(icon == null ? "-" : icon.width + "x" + icon.height)}"
                          + $" cov={Pct(cov)}"
                          + $" shader='{(iconMat == null || iconMat.shader == null ? "<null>" : iconMat.shader.name)}'"
                          + $" px={Px(icon)}"
                          + $"{(subErr == null ? "" : " subErr=" + subErr)}"
                          + $"{(iconErr == null ? "" : " iconErr=" + iconErr)}");
        }
        foreach (string l in pickLines) Console.WriteLine(l);

        // ---- the decision rule ------------------------------------------------------------
        string uiState = uiNull ? "null" : uiBad ? "badtex" : uiPlaceholder ? "placeholder(2x2)" : "texture";
        string matShader = uim == null || uim.shader == null ? "<null>" : uim.shader.name;
        bool defBlank = defIcon == null
                        || (BaseContent.BadTex != null && ReferenceEquals(defIcon, BaseContent.BadTex))
                        || (defIcon.width <= 2 || defIcon.height <= 2)
                        || defCov <= 0.0;
        bool maskLeak = masks > 0;
        bool matRisk = uim != null && matShader.StartsWith("CutoutComplexUI", StringComparison.Ordinal);

        string verdict, reason;
        if (uiState == "null" || uiState == "badtex" || uiState == "placeholder(2x2)" || defBlank)
        {
            verdict = "BLANK";
            reason = uiState != "texture" ? "uiIcon=" + uiState : "defIcon-empty";
        }
        else if (maskLeak)
        {
            verdict = "MASK-VARIANTS";
            reason = $"masks-as-subgraphics({masks}/{subs.Length})";
        }
        else if (matRisk)
        {
            verdict = "DRAW-MATERIAL";
            reason = "uiIconMaterial=" + matShader;
        }
        else
        {
            verdict = "DRAW";
            reason = "plain-texture-branch";
        }

        Console.WriteLine($"{T} def={defName} gate present=true subCount={subs.Length} art={subs.Length - masks}"
                          + $" masks={masks} uiIcon={uiState} uiIconCov={Pct(Coverage(ui))}"
                          + $" defIconBlank={defBlank} defCov={Pct(defCov)}"
                          + $" uiIconMaterial={(uim == null ? "<null>" : matShader)} props={props}"
                          + $" picks(art={artPicks},mask={maskPicks},null={nullPicks}) blankPicks={blankPicks}/{Ids}"
                          + $" verdict={verdict} reason={reason}");

        if (subs.Length == 0) Fail("subgraphs:" + defName);
        if (verdict == "BLANK") Fail("blank:" + defName);
        if (defIconErr != null) Fail("geticonfor:" + defName);
    }

    // ------------------------------------------------------------------ helpers

    private static void Fail(string why) { Failed.Add(why); }

    /// <summary>Graphic_RandomRotated.subGraphic (and any other wrapper's child), by name.</summary>
    private static Graphic Unwrap(Graphic g)
    {
        if (g == null) return null;
        for (Type t = g.GetType(); t != null && t != typeof(object); t = t.BaseType)
        {
            FieldInfo f = t.GetField("subGraphic", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (f != null && typeof(Graphic).IsAssignableFrom(f.FieldType))
            {
                if (f.GetValue(g) is Graphic child) return child;
                return g;
            }
        }
        return g;
    }

    /// <summary>Non-transparent texel fraction, or -1 when the texture has no readable pixels.</summary>
    private static double Coverage(Texture2D t)
    {
        if (t == null) return -1;
        try
        {
            Color32[] px = t.GetPixels32();
            if (px == null || px.Length == 0) return -1;
            int opaque = 0;
            foreach (Color32 c in px) if (c.a > 0) opaque++;
            return (double)opaque / px.Length;
        }
        catch { return -1; }
    }

    /// <summary>"n=16384 opaque=16000 a0=384 cov=97.66% a128=... sd=... colors=..." or the exception.</summary>
    private static string Px(Texture2D t)
    {
        if (t == null) return "-";
        try
        {
            Color32[] px = t.GetPixels32();
            if (px == null || px.Length == 0) return "empty";
            int opaque = 0, zero = 0, a128 = 0;
            double l = 0, l2 = 0;
            var set = new HashSet<int>();
            foreach (Color32 c in px)
            {
                if (c.a == 0) zero++;
                if (c.a > 0) opaque++;
                if (c.a >= 128) a128++;
                double lum = 0.299 * c.r + 0.587 * c.g + 0.114 * c.b;
                l += lum; l2 += lum * lum;
                set.Add((c.r << 16) | (c.g << 8) | c.b);
            }
            int n = px.Length;
            double mean = l / n;
            double sd = Math.Sqrt(Math.Max(0, l2 / n - mean * mean));
            return $"n={n} opaque={opaque} a0={zero} a128={a128}"
                   + $" cov={Pct((double)opaque / n)} sd={sd.ToString("0.0", CultureInfo.InvariantCulture)}"
                   + $" colors={set.Count}";
        }
        catch (Exception e) { return "!" + e.GetType().Name + ":" + One(e.Message); }
    }

    private static string Pct(double v)
    {
        return v < 0 ? "-" : (v * 100.0).ToString("0.00", CultureInfo.InvariantCulture) + "%";
    }

    private static bool IsMask(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        string s = name;
        bool peeled = true;
        while (peeled)
        {
            peeled = false;
            foreach (string ext in Extensions)
            {
                if (s.Length > ext.Length && s.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                {
                    s = s.Substring(0, s.Length - ext.Length);
                    peeled = true;
                    break;
                }
            }
        }
        return s.Length > 1 && s.EndsWith("_m", StringComparison.Ordinal);
    }

    private static string Leaf(string path)
    {
        if (string.IsNullOrEmpty(path)) return "<null>";
        string n = path.Replace('\\', '/');
        int slash = n.LastIndexOf('/');
        return slash >= 0 ? n.Substring(slash + 1) : n;
    }

    private static string S(string s) { return string.IsNullOrEmpty(s) ? "<none>" : s; }
    private static string V2(Vector2 v) { return $"{v.x:0.##}x{v.y:0.##}"; }
    private static string One(string s) { return (s ?? "").Replace('\n', ' ').Replace('\r', ' '); }
}
