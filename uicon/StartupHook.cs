using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

/// <summary>
/// Prints the whole "animal list icon" chain for four real core defs (Ibex, Squirrel, Rat, Boomrat)
/// inside the real host, one step per line, so an empty icon can be attributed to a step:
///
///   def.graphicData / pawnKind.lifeStages.Last().bodyGraphicData
///     -> GraphicData.Graphic (Graphic_Multi)
///     -> ExtractInnerGraphicFor(null) -> MatAt(rot) -> material, shader, mainTexture, mask
///     -> BuildableDef.ResolveIcon / ThingDef.ResolveIcon -> def.uiIcon, def.uiIconMaterial
///     -> Widgets.ThingIcon(Rect, ThingDef) early return (uiIcon == null || uiIcon == BadTex)
///     -> Widgets.GetIconFor(ThingDef) and the instance path the wildlife list really uses
///        (PawnColumnWorker_Label -> Widgets.ThingIcon(Rect, Thing) -> BodyGraphic.MatAt(East))
///
/// Every value is read from the running game; nothing is inferred from the XML here.
/// </summary>
internal static class StartupHook
{
    private const string T = "[UICON]";
    private static readonly string[] DefNames = { "Ibex", "Squirrel", "Rat", "Boomrat" };

    private static bool workerHit;

    public static void Initialize()
    {
        try { PatchWorker(); }
        catch (Exception e) { Console.WriteLine($"{T} patch=failed {e.GetType().Name}: {OneLine(e.Message)}"); }
        new Thread(WaitForBoot) { IsBackground = true, Name = "uicon-waiter" }.Start();
        new Thread(Watchdog) { IsBackground = true, Name = "uicon-watchdog" }.Start();
    }

    /// <summary>
    /// ThingIconWorker is only reached when Widgets.ThingIcon got past its early return, so the hook
    /// turns "the branch was taken" from an inference into an observation.
    /// </summary>
    private static void PatchWorker()
    {
        MethodInfo worker = AccessTools.Method(typeof(Widgets), "ThingIconWorker");
        if (worker == null)
        {
            Console.WriteLine($"{T} patch: ThingIconWorker not found");
            return;
        }
        var harmony = new HarmonyLib.Harmony("waylith.uicon.chain");
        harmony.Patch(worker, prefix: new HarmonyMethod(AccessTools.Method(typeof(StartupHook), nameof(WorkerPrefix))));
        Console.WriteLine($"{T} patch: ThingIconWorker hooked params={worker.GetParameters().Length}");
    }

    public static void WorkerPrefix() { workerHit = true; }

    private static void Watchdog()
    {
        int ms = int.TryParse(Environment.GetEnvironmentVariable("PROBE_TIMEOUT_MS"), out int v) ? v : 180000;
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
            Console.WriteLine($"{T} phase=start culture={CultureInfo.CurrentCulture.Name} defs={string.Join(",", DefNames)}");
            Console.WriteLine($"{T} base badtex={Describe(BaseContent.BadTex)} badtexIsNull={(BaseContent.BadTex == null)}"
                              + $" badgraphic={(BaseContent.BadGraphic == null ? "<null>" : BaseContent.BadGraphic.GetType().Name)}"
                              + $" badmat={(BaseContent.BadMat == null ? "<null>" : BaseContent.BadMat.name)}");
            DumpMembers("base.BadTex", BaseContent.BadTex, 24);

            foreach (string dn in DefNames)
            {
                try { Report(dn); }
                catch (Exception e) { Console.WriteLine($"{T} def={dn} step=fatal {e.GetType().Name}: {OneLine(e.Message)}"); }
            }

            Controls();
        }
        catch (Exception e)
        {
            Console.WriteLine($"{T} fatal {e.GetType().Name}: {OneLine(e.Message)}");
        }
        finally
        {
            Console.Out.Flush();
            try { Root.Shutdown(); } catch { }
            Environment.Exit(0);
        }
    }

    private static void Report(string dn)
    {
        ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(dn);
        if (def == null)
        {
            Console.WriteLine($"{T} def={dn} step=lookup MISSING");
            return;
        }

        Console.WriteLine($"{T} def={dn} step=def category={def.category} rot={def.defaultPlacingRot}"
                          + $" uiIconPath={(def.uiIconPath.NullOrEmpty() ? "<null>" : def.uiIconPath)}"
                          + $" uiIconScale={Get(def, "uiIconScale")} uiIconAngle={Get(def, "uiIconAngle")}"
                          + $" uiIconColor={Col(Get(def, "uiIconColor"))} graphicData={(def.graphicData == null ? "<null>" : "set")}");

        // (1) def.graphicData -- what the XML carries on the ThingDef itself.
        GraphicData gd = def.graphicData;
        if (gd == null)
        {
            Console.WriteLine($"{T} def={dn} step=graphicData value=<null> note=no-graphicData-on-this-def");
        }
        else
        {
            Console.WriteLine($"{T} def={dn} step=graphicData texPath='{gd.texPath}' graphicClass={Typ(gd.graphicClass)}"
                              + $" shaderType={(gd.shaderType == null ? "<null>" : gd.shaderType.defName)}"
                              + $" maskPath={(gd.maskPath.NullOrEmpty() ? "<null>" : gd.maskPath)}"
                              + $" drawSize={V2(gd.drawSize)} color={Col(gd.color)}");
        }

        // (1b) The body graphic of the last life stage: for a pawn this is the branch
        // ThingDef.ResolveIcon actually takes (uiIconPath empty -> AnyPawnKind.lifeStages.Last()).
        RaceProperties race = def.race;
        PawnKindDef kind = race?.AnyPawnKind;
        GraphicData bgd = null;
        if (race == null) Console.WriteLine($"{T} def={dn} step=bodySource value=<null> note=no-race");
        else if (kind == null) Console.WriteLine($"{T} def={dn} step=bodySource value=<null> note=no-anyPawnKind");
        else if (kind.lifeStages == null || kind.lifeStages.Count == 0) Console.WriteLine($"{T} def={dn} step=bodySource value=<null> note=no-lifeStages");
        else
        {
            PawnKindLifeStage kindStage = kind.lifeStages[kind.lifeStages.Count - 1];
            bgd = kindStage.bodyGraphicData;
            LifeStageDef stage = race.lifeStageAges != null && race.lifeStageAges.Count > 0
                ? race.lifeStageAges[race.lifeStageAges.Count - 1].def
                : null;
            Console.WriteLine($"{T} def={dn} step=bodySource pawnKind={kind.defName} lifeStages={kind.lifeStages.Count}"
                              + $" lastStage={(stage == null ? "?" : stage.defName)} texPath='{(bgd == null ? "<null>" : bgd.texPath)}'"
                              + $" graphicClass={(bgd == null ? "<null>" : Typ(bgd.graphicClass))}"
                              + $" shaderType={(bgd?.shaderType == null ? "<null>" : bgd.shaderType.defName)}"
                              + $" maskPath={(bgd == null || bgd.maskPath.NullOrEmpty() ? "<null>" : bgd.maskPath)}"
                              + $" drawSize={(bgd == null ? "-" : V2(bgd.drawSize))} color={(bgd == null ? "-" : Col(bgd.color))}");
        }

        // (4) def.graphic: type, inner graphic, material for defaultPlacingRot.
        Graphic g = def.graphic;
        Console.WriteLine($"{T} def={dn} step=def.graphic value={(g == null ? "<null>" : g.GetType().FullName)}"
                          + $" path='{(g == null ? "-" : g.path)}' isBadGraphic={(g != null && g == BaseContent.BadGraphic)}");
        if (g != null) ReportGraphic(dn, "defgraphic", g, def.defaultPlacingRot);

        // (4b) The same three steps on the graphic the pawn icon really comes from.
        Graphic bg = null;
        if (bgd != null)
        {
            try { bg = bgd.Graphic; }
            catch (Exception e) { Console.WriteLine($"{T} def={dn} step=bodygraphic !{e.GetType().Name}: {OneLine(e.Message)}"); }
        }
        Console.WriteLine($"{T} def={dn} step=body.graphic value={(bg == null ? "<null>" : bg.GetType().FullName)}"
                          + $" path='{(bg == null ? "-" : bg.path)}' isBadGraphic={(bg != null && bg == BaseContent.BadGraphic)}");
        Material bodyMat = null;
        if (bg != null)
        {
            ReportGraphic(dn, "bodygraphic", bg, Rot4.East);
            try { bodyMat = bg.ExtractInnerGraphicFor(null).MatAt(Rot4.East); }
            catch (Exception e) { Console.WriteLine($"{T} def={dn} step=body.mat !{e.GetType().Name}: {OneLine(e.Message)}"); }
        }

        // (2) def.uiIcon.
        Texture2D ui = def.uiIcon;
        bool uiNull = ui == null;
        bool uiBad = ui != null && ReferenceEquals(ui, BaseContent.BadTex);
        Console.WriteLine($"{T} def={dn} step=uiIcon null={uiNull} badTex={uiBad} value={Describe(ui)}");
        if (ui != null) ReportTexture(dn, "uiIcon", ui);

        // (3) def.uiIconMaterial.
        Material uim = def.uiIconMaterial;
        if (uim == null)
        {
            Console.WriteLine($"{T} def={dn} step=uiIconMaterial null=True");
        }
        else
        {
            Console.WriteLine($"{T} def={dn} step=uiIconMaterial null=False {MatFields(uim)}");
            DumpMembers(dn + "/uiIconMaterial", uim, 24);
            DumpMembers(dn + "/uiIconMaterial.shader", uim.shader, 24);
        }

        // The exact decision point inside ResolveIcon, replayed on the body material.
        if (bodyMat != null)
        {
            bool hasUi = false, hasReq = false;
            Shader uiShader = null;
            MaterialRequest req = default;
            try { hasUi = ShaderDatabase.TryGetUIShader(bodyMat.shader, out uiShader); }
            catch (Exception e) { Console.WriteLine($"{T} def={dn} step=resolve.uiShader !{e.GetType().Name}: {OneLine(e.Message)}"); }
            try { hasReq = MaterialPool.TryGetRequestForMat(bodyMat, out req); }
            catch (Exception e) { Console.WriteLine($"{T} def={dn} step=resolve.poolRequest !{e.GetType().Name}: {OneLine(e.Message)}"); }
            Console.WriteLine($"{T} def={dn} step=resolve uiShader={hasUi}"
                              + $" uiShaderName='{(uiShader == null ? "<null>" : uiShader.name)}'"
                              + $" poolRequest={hasReq}"
                              + $" requestShader='{(hasReq && req.shader != null ? req.shader.name : "<null>")}'"
                              + $" requestMain='{(hasReq && req.mainTex != null ? req.mainTex.name : "<null>")}'"
                              + $" requestMask='{(hasReq && req.maskTex != null ? req.maskTex.name : "<null>")}'");
            if (hasUi && hasReq)
            {
                try
                {
                    req.shader = uiShader;
                    Material would = MaterialPool.MatFrom(req);
                    Console.WriteLine($"{T} def={dn} step=resolve.wouldBeMaterial value={(would == null ? "<null>" : would.name)}"
                                      + $" shader={(would?.shader == null ? "<null>" : would.shader.name)}"
                                      + $" main={(would?.mainTexture == null ? "<null>" : would.mainTexture.name)}");
                }
                catch (Exception e) { Console.WriteLine($"{T} def={dn} step=resolve.wouldBeMaterial !{e.GetType().Name}: {OneLine(e.Message)}"); }
            }
        }

        // (5) The early return in Widgets.ThingIcon(Rect, ThingDef), first as the predicate the
        // decompiled branch evaluates, then by calling the engine function itself.
        bool early = uiNull || uiBad;
        Console.WriteLine($"{T} def={dn} step=thingicon.def earlyReturn={early} nullBranch={uiNull} badTexBranch={uiBad}"
                          + $" expr=uiIcon==null||uiIcon==BaseContent.BadTex");
        Material gotMat = null;
        try
        {
            Texture2D got = Widgets.GetIconFor(def, out gotMat);
            Console.WriteLine($"{T} def={dn} step=widgets.GetIconFor ok tex={Describe(got)}"
                              + $" material={(gotMat == null ? "<null>" : gotMat.name)}");
        }
        catch (Exception e) { Console.WriteLine($"{T} def={dn} step=widgets.GetIconFor !{e.GetType().Name}: {OneLine(e.Message)}"); }

        // The path the wildlife tab actually draws (PawnColumnWorker_Label -> ThingIcon(Rect, Thing)).
        Texture2D instIcon = bodyMat?.mainTexture as Texture2D;
        bool instEarly = instIcon == null || ReferenceEquals(instIcon, BaseContent.BadTex);
        Console.WriteLine($"{T} def={dn} step=thingicon.instance icon={Describe(instIcon)}"
                          + $" earlyReturn={instEarly} material={(bodyMat == null ? "<null>" : bodyMat.name)}");

        if (Environment.GetEnvironmentVariable("UICON_CALL") == "1")
        {
            workerHit = false;
            Console.Out.Flush();
            try
            {
                Widgets.ThingIcon(new Rect(0f, 0f, 64f, 64f), def);
                Console.WriteLine($"{T} def={dn} step=thingicon.call returned workerReached={workerHit}");
            }
            catch (Exception e)
            {
                Console.WriteLine($"{T} def={dn} step=thingicon.call threw {e.GetType().Name}: {OneLine(e.Message)}"
                                  + $" workerReached={workerHit} at={Where(e)}");
            }
        }
    }

    /// <summary>
    /// Two controls, so "the CutoutComplexUI material cannot render" is measured rather than assumed:
    /// every Map/* shader the port declares (Cutout is the one the working world draw uses) and one
    /// texture the harness already draws, loaded through the same ContentFinder path as the animals.
    /// </summary>
    private static void Controls()
    {
        foreach (Shader sh in new[] { ShaderDatabase.Cutout, ShaderDatabase.CutoutComplex, ShaderDatabase.CutoutComplexUI })
        {
            Console.WriteLine($"{T} control shader name='{(sh == null ? "<null>" : sh.name)}'");
            DumpMembers("shader/" + (sh == null ? "<null>" : sh.name), sh, 24);
        }

        foreach (string key in new[]
        {
            "Things/Item/Equipment/WeaponRanged/UniqueWeapons/Revolver/UniqueRevolverA",
            "Things/Item/Equipment/WeaponRanged/Revolver",
        })
        {
            Texture2D t = ContentFinder<Texture2D>.Get(key, false);
            Console.WriteLine($"{T} control texture key={key} value={Describe(t)}");
            if (t != null) ReportTexture("control", "worldart", t);
        }
    }

    private static void ReportGraphic(string dn, string tag, Graphic g, Rot4 rot)
    {
        Graphic inner = null;
        string err = "";
        try { inner = g.ExtractInnerGraphicFor(null); }
        catch (Exception e) { err = $" error={e.GetType().Name}:{OneLine(e.Message)}"; }
        Console.WriteLine($"{T} def={dn} step={tag}.inner type={(inner == null ? "<null>" : inner.GetType().Name)}"
                          + $" path='{(inner == null ? "-" : inner.path)}'{err}");

        Material m = null;
        err = "";
        try { m = inner?.MatAt(rot); }
        catch (Exception e) { err = $" error={e.GetType().Name}:{OneLine(e.Message)}"; }
        Console.WriteLine($"{T} def={dn} step={tag}.mat rot={rot} {(m == null ? "value=<null>" : MatFields(m))}{err}");

        // Direction table for a Graphic_Multi: which suffixes resolved, which fell back.
        FieldInfo matsField = g.GetType().GetField("mats", BindingFlags.Public | BindingFlags.Instance);
        if (matsField?.GetValue(g) is Material[] mats)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < mats.Length; i++)
            {
                Material mm = mats[i];
                sb.Append($" [{i}]={(mm == null ? "<null>" : (mm.mainTexture == null ? "<null-tex>" : mm.mainTexture.name))}"
                          + $"/{((mm?.shader) == null ? "<null>" : mm.shader.name)}");
            }
            Console.WriteLine($"{T} def={dn} step={tag}.mats{sb}");
            Console.WriteLine($"{T} def={dn} step={tag}.matsFlags"
                              + $" drawRotatedExtraAngleOffset={Get(g, "drawRotatedExtraAngleOffset")}"
                              + $" eastFlipped={Get(g, "eastFlipped")} westFlipped={Get(g, "westFlipped")}");
        }
    }

    /// <summary>name/shader/mainTexture/mask/queue on one line, no spaces inside a value.</summary>
    private static string MatFields(Material m)
    {
        string name = Safe(() => m.name);
        string shader = Safe(() => m.shader == null ? "<null>" : m.shader.name);
        string shaderType = Safe(() => m.shader == null ? "<null>" : m.shader.GetType().FullName);
        string main = Safe(() => m.mainTexture == null ? "<null>" : $"{m.mainTexture.name}({m.mainTexture.width}x{m.mainTexture.height})");
        string mask = Safe(() =>
        {
            Texture mt = m.GetTexture(ShaderPropertyIDs.MaskTex);
            return mt == null ? "<null>" : mt.name;
        });
        string color = Safe(() => Col(m.color));
        string queue = Safe(() => m.renderQueue.ToString());
        string maskSupport = Safe(() => (m.shader != null && m.shader.SupportsMaskTex()).ToString());
        return $"name='{name}' shader='{shader}' shaderType={shaderType} main='{main}' mask='{mask}'"
               + $" color={color} queue={queue} maskTexSupport={maskSupport}";
    }

    private static void ReportTexture(string dn, string tag, Texture2D t)
    {
        Console.WriteLine($"{T} def={dn} step={tag}.texture name='{t.name}' size={t.width}x{t.height}"
                          + $" format={t.format} mips={t.mipmapCount} badTexRef={ReferenceEquals(t, BaseContent.BadTex)}"
                          + $" type={t.GetType().FullName} assembly={t.GetType().Assembly.GetName().Name}");
        DumpMembers(dn + "/" + tag, t, 40);

        try
        {
            Color32[] px = t.GetPixels32();
            if (px == null || px.Length == 0)
            {
                Console.WriteLine($"{T} def={dn} step={tag}.pixels EMPTY length={(px == null ? -1 : 0)}");
            }
            else
            {
                double r = 0, g2 = 0, b = 0, a = 0, l = 0, l2 = 0;
                int zero = 0;
                foreach (Color32 c in px)
                {
                    r += c.r; g2 += c.g; b += c.b; a += c.a;
                    double lum = 0.299 * c.r + 0.587 * c.g + 0.114 * c.b;
                    l += lum; l2 += lum * lum;
                    if (c.a == 0) zero++;
                }
                int n = px.Length;
                double mean = l / n;
                double sd = Math.Sqrt(Math.Max(0, l2 / n - mean * mean));
                Console.WriteLine($"{T} def={dn} step={tag}.pixels n={n} meanRGB=({r / n:F1},{g2 / n:F1},{b / n:F1})"
                                  + $" meanA={a / n:F1} alpha0={zero}/{n} lumSd={sd:F2}");
            }
        }
        catch (Exception e)
        {
            Console.WriteLine($"{T} def={dn} step={tag}.pixels !{e.GetType().Name}: {OneLine(e.Message)}");
        }
    }

    /// <summary>
    /// Every field and every property that returns a primitive/string, so the shim's real state
    /// members show up (the port's Texture2D may not have UnityEngine's CpuData/levels names).
    /// </summary>
    private static void DumpMembers(string tag, object o, int max)
    {
        if (o == null)
        {
            Console.WriteLine($"{T} members {tag} value=<null>");
            return;
        }
        Type t = o.GetType();
        var parts = new List<string>();
        foreach (FieldInfo f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            if (parts.Count >= max) break;
            object v;
            try { v = f.GetValue(o); } catch (Exception e) { v = "!" + e.GetType().Name; }
            parts.Add($"{f.Name}={Short(v)}");
        }
        foreach (PropertyInfo p in t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            if (parts.Count >= max) break;
            if (p.GetIndexParameters().Length > 0) continue;
            object v;
            try { v = p.GetValue(o); }
            catch (Exception e) { v = "!" + e.GetType().Name; continue; }
            if (v == null || v.GetType().IsPrimitive || v is string || v is decimal || v.GetType().IsEnum)
            {
                parts.Add($"{p.Name}={Short(v)}");
            }
        }
        Console.WriteLine($"{T} members {tag} type={t.FullName} {string.Join(" ", parts)}");
    }

    private static string Safe(Func<string> f)
    {
        try { return f(); } catch (Exception e) { return "!" + e.GetType().Name; }
    }

    private static string Where(Exception e)
    {
        string st = e.StackTrace ?? "";
        string[] frames = st.Split('\n');
        return string.Join("|", frames.Take(3).Select(s => s.Trim().Replace(' ', '_'))).Replace("\r", "");
    }

    private static object Get(object o, string member)
    {
        if (o == null) return "<null>";
        Type type = o.GetType();
        try
        {
            PropertyInfo p = type.GetProperty(member, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (p != null) return p.GetValue(o) ?? "<null>";
            FieldInfo f = type.GetField(member, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (f != null) return f.GetValue(o) ?? "<null>";
        }
        catch { return "!"; }
        return "?";
    }

    private static string Describe(Texture2D t)
    {
        if (t == null) return "<null>";
        try { return $"'{t.name}'({t.width}x{t.height})"; }
        catch { return $"'{t.name}'(?)"; }
    }

    private static string Col(object v)
    {
        if (v is Color c) return $"{c.r:F2}/{c.g:F2}/{c.b:F2}/{c.a:F2}";
        return Short(v);
    }

    private static string Short(object v)
    {
        if (v == null) return "null";
        if (v is string s) return s.Length == 0 ? "''" : s.Replace(' ', '_');
        if (v is bool b) return b ? "True" : "False";
        if (v.GetType().IsPrimitive || v is decimal) return Convert.ToString(v, CultureInfo.InvariantCulture);
        if (v is Array arr) return $"{v.GetType().Name}[{arr.Length}]";
        Type t = v.GetType();
        try
        {
            PropertyInfo np = t.GetProperty("name", BindingFlags.Public | BindingFlags.Instance);
            if (np != null && np.GetValue(v) is string n) return $"{t.Name}:'{n}'";
        }
        catch { }
        return t.Name;
    }

    private static string Typ(Type t) => t == null ? "<null>" : t.Name;
    private static string V2(Vector2 v) => $"{v.x:F2}x{v.y:F2}";
    private static string OneLine(string s) => (s ?? "").Replace('\n', ' ').Replace('\r', ' ').Replace(' ', '_');
}
