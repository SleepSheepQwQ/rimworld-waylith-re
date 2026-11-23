using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using UnityEngine.SceneManagement;
using Verse;

/// <summary>
/// OPEN-A from docs/LOADING-FACTS.md, measured instead of argued:
///
///   "Can GlyphFix's Harmony patches be silently disabled when another mod patches the same method
///    first?"
///
/// The fake mod is installed in Initialize() — before any mod assembly is loaded — so it is
/// literally the mod that patched first. GlyphFix is installed afterwards by running its
/// StaticConstructorOnStartup ctor, the way RimWorld's startup utility does. Then the real
/// Widgets.GetIconFor(pawn, ...) is called on a spawned animal and the returned Material is read:
/// GlyphFix 2.7.7 drops a CutoutComplexUI material and writes its colour into `out color`, so
/// `materialNull=True` is the observable effect of its postfix.
///
/// PC_MODE selects what the fake postfix does:
///   off         no fake patch (control: GlyphFix alone, or no mod at all)
///   observe     postfix reads the values, writes nothing
///   null_first  postfix sets __result = null, priority First
///   null_last   postfix sets __result = null, priority Last
///   multi       two postfixes, both ref __result, Normal: WhiteTex then BlackTex
///
/// Every line is prefixed [PATCHCONFLICT] and is parsed by patchconflict/gate.py.
/// </summary>
internal static class StartupHook
{
    private const string T = "[PATCHCONFLICT]";

    private static readonly string[] Species = { "Rat", "Ibex", "Squirrel" };
    private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();

    private static string _mode = "off";
    private static int _started, _done, _phase2;

    // fake-mod bookkeeping
    private static int _fakeThingCalls, _fakeDefCalls, _fakeErrors;
    private static readonly List<string> Seen = new List<string>();

    // synthetic-target bookkeeping
    private static int _p1, _p2, _plast, _pthrow, _p2t, _plastt;
    private static string _objectSeen = "-";

    public static void Initialize()
    {
        _mode = (Environment.GetEnvironmentVariable("PC_MODE") ?? "off").Trim().ToLowerInvariant();
        Line($"start mode={_mode} harmony={typeof(Harmony).Assembly.GetName().Version} " +
             $"harmonyLocation={typeof(Harmony).Assembly.Location} runtime={Environment.Version}");
        SynthMultiplePostfixes();
        InstallFakeMod();
        new Thread(WaitForBoot) { IsBackground = true, Name = "patchconflict-waiter" }.Start();
        new Thread(Watchdog) { IsBackground = true, Name = "patchconflict-watchdog" }.Start();
    }

    // ================================================================= part 3: Harmony 3.x semantics
    //
    // Three postfixes on one method, all declaring `ref string __result`; one throwing postfix to see
    // whether Harmony isolates it; one wrong-typed `ref int __result`; one `ref object __result`.
    // None of this needs the game, so it runs before boot.

    private static void SynthMultiplePostfixes()
    {
        try
        {
            MethodInfo value = AccessTools.Method(typeof(SynthTarget), nameof(SynthTarget.Value));
            var h = new Harmony("waylith.patchconflict.synth");
            PatchSynth(h, value, nameof(SynthP1), HarmonyLib.Priority.Normal, "p1");
            PatchSynth(h, value, nameof(SynthP2), HarmonyLib.Priority.Normal, "p2");
            PatchSynth(h, value, nameof(SynthPLast), HarmonyLib.Priority.Last, "plast");
            string r = TryCall(SynthTarget.Value, out string err);
            Patches info = Harmony.GetPatchInfo(value);
            Line($"synth.three result='{r}' error='{err}' postfixes={info?.Postfixes.Count ?? -1} " +
                 $"ran(p1={_p1},p2={_p2},plast={_plast})");

            string r2 = TryCall(SynthTarget.Value, out string err2);
            Line($"synth.three.second result='{r2}' error='{err2}' " +
                 $"ran(p1={_p1},p2={_p2},plast={_plast})");

            // A postfix that throws: does Harmony catch it, and do the later postfixes still run?
            MethodInfo throwTarget = AccessTools.Method(typeof(SynthTarget), nameof(SynthTarget.ValueThrow));
            var ht = new Harmony("waylith.patchconflict.synth.throw");
            PatchSynth(ht, throwTarget, nameof(SynthPThrow), HarmonyLib.Priority.Normal, "throw");
            PatchSynth(ht, throwTarget, nameof(SynthP2Throw), HarmonyLib.Priority.Normal, "p2t");
            PatchSynth(ht, throwTarget, nameof(SynthPLastThrow), HarmonyLib.Priority.Last, "plastt");
            string rt = TryCall(SynthTarget.ValueThrow, out string errt);
            Line($"synth.throwing result='{rt}' error='{errt}' ran(throw={_pthrow},p2t={_p2t},plastt={_plastt})");

            // Wrong __result type on a string-returning method.
            MethodInfo wrong = AccessTools.Method(typeof(SynthTarget), nameof(SynthTarget.Value3));
            try
            {
                new Harmony("waylith.patchconflict.synth.wrong").Patch(wrong,
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(StartupHook), nameof(SynthPWrong))));
                string accepted = TryCall(SynthTarget.Value3, out string we);
                Line($"synth.wrongtype patch=ACCEPTED result='{accepted}' error='{we}'");
            }
            catch (Exception e)
            {
                Line($"synth.wrongtype patch=REJECTED {e.GetType().Name}: {OneLine(e.Message)}");
            }

            // `ref object __result` on a string-returning method (does Harmony widen?).
            MethodInfo obj = AccessTools.Method(typeof(SynthTarget), nameof(SynthTarget.Value2));
            try
            {
                new Harmony("waylith.patchconflict.synth.object").Patch(obj,
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(StartupHook), nameof(SynthPObject))));
                string or = TryCall(SynthTarget.Value2, out string oe);
                Line($"synth.objectResult patch=ACCEPTED result='{or}' error='{oe}' seen='{_objectSeen}'");
            }
            catch (Exception e)
            {
                Line($"synth.objectResult patch=REJECTED {e.GetType().Name}: {OneLine(e.Message)}");
            }
        }
        catch (Exception e) { Line($"synth FAILED {e.GetType().Name}: {OneLine(e.Message)}"); }
    }

    public static void SynthP1(ref string __result) { Interlocked.Increment(ref _p1); __result += "|p1"; }
    public static void SynthP2(ref string __result) { Interlocked.Increment(ref _p2); __result += "|p2"; }
    public static void SynthPLast(ref string __result) { Interlocked.Increment(ref _plast); __result += "|plast"; }
    public static void SynthPThrow(ref string __result) { Interlocked.Increment(ref _pthrow); throw new InvalidOperationException("fake-mod-postfix-boom"); }
    public static void SynthP2Throw(ref string __result) { Interlocked.Increment(ref _p2t); __result += "|p2t"; }
    public static void SynthPLastThrow(ref string __result) { Interlocked.Increment(ref _plastt); __result += "|plastt"; }
    public static void SynthPWrong(ref int __result) { }
    public static void SynthPObject(ref object __result) { _objectSeen = __result == null ? "<null>" : __result.ToString(); }

    private static void PatchSynth(Harmony h, MethodInfo target, string postfix, int priority, string label)
    {
        try
        {
            // The 5-argument ctor is the shape tests/HarmonyProbe already compiles against
            // dlls/0Harmony.dll (Harmony 3.0.0.0); the property initializer is only proven against
            // the 2.3.3 NuGet reference the mod itself builds with.
            h.Patch(target, postfix: new HarmonyMethod(AccessTools.Method(typeof(StartupHook), postfix), priority, null, null, null));
            Line($"synth.{label} patch=ok target={Sig(target)} priority={priority}");
        }
        catch (Exception e) { Line($"synth.{label} patch=FAILED {e.GetType().Name}: {OneLine(e.Message)}"); }
    }

    // ================================================================= part 2: the fake mod
    //
    // Installed before GlyphFix exists. Declares the same by-ref parameters a real icon mod would:
    // __result, material and (on the Thing overload) color.

    private static void InstallFakeMod()
    {
        if (_mode == "off") return;

        MethodInfo thing = FindIconFor(typeof(Thing), wantColor: true);
        MethodInfo def = FindIconFor(typeof(ThingDef), wantColor: false);
        Line($"fake.targets thing={(thing == null ? "<null>" : Sig(thing))} def={(def == null ? "<null>" : Sig(def))}");

        int priority = _mode == "null_first" ? HarmonyLib.Priority.First
                     : _mode == "null_last" ? HarmonyLib.Priority.Last
                     : HarmonyLib.Priority.Normal;

        if (thing != null) PatchFake(thing, nameof(FakeThingPostfix), priority, "fake.thing");
        if (def != null) PatchFake(def, nameof(FakeDefPostfix), priority, "fake.def");

        if (_mode == "multi")
        {
            if (thing != null) PatchFake(thing, nameof(FakeThingPostfixB), HarmonyLib.Priority.Normal, "fake.thingB");
            if (def != null) PatchFake(def, nameof(FakeDefPostfixB), HarmonyLib.Priority.Normal, "fake.defB");
        }
    }

    private static void PatchFake(MethodInfo target, string postfix, int priority, string label)
    {
        try
        {
            new Harmony("waylith.patchconflict." + label)
                .Patch(target, postfix: new HarmonyMethod(AccessTools.Method(typeof(StartupHook), postfix), priority, null, null, null));
            Line($"{label} patch=ok priority={priority} target={Sig(target)}");
        }
        catch (Exception e) { Line($"{label} patch=FAILED {e.GetType().Name}: {OneLine(e.Message)}"); }
    }

    public static void FakeThingPostfix(Thing thing, ref Texture __result, ref Color color, ref Material material)
    {
        Enter("thing", thing?.def?.defName, __result, material, color);
        try
        {
            switch (_mode)
            {
                case "null_first":
                case "null_last":
                    __result = null;
                    break;
                case "multi":
                    __result = BaseContent.WhiteTex;
                    break;
            }
        }
        catch (Exception e) { FakeFail(e); }
    }

    public static void FakeThingPostfixB(Thing thing, ref Texture __result, ref Color color, ref Material material)
    {
        Enter("thingB", thing?.def?.defName, __result, material, color);
        try { if (_mode == "multi") __result = BaseContent.BlackTex; }
        catch (Exception e) { FakeFail(e); }
    }

    public static void FakeDefPostfix(ThingDef thingDef, ref Texture2D __result, ref Material material)
    {
        Enter("def", thingDef?.defName, __result, material, Color.white);
        try
        {
            switch (_mode)
            {
                case "null_first":
                case "null_last":
                    __result = null;
                    break;
                case "multi":
                    __result = BaseContent.WhiteTex;
                    break;
            }
        }
        catch (Exception e) { FakeFail(e); }
    }

    public static void FakeDefPostfixB(ThingDef thingDef, ref Texture2D __result, ref Material material)
    {
        Enter("defB", thingDef?.defName, __result, material, Color.white);
        try { if (_mode == "multi") __result = BaseContent.BlackTex; }
        catch (Exception e) { FakeFail(e); }
    }

    private static void Enter(string which, string def, Texture tex, Material mat, Color col)
    {
        int n = which.StartsWith("def", StringComparison.Ordinal)
            ? Interlocked.Increment(ref _fakeDefCalls)
            : Interlocked.Increment(ref _fakeThingCalls);
        string line = $"call{n} def={def ?? "?"} tex={Name(tex)} mat={Name(mat)} color={C(col)}";
        lock (Seen) { if (Seen.Count < 8) Seen.Add($"{which}:{line}"); }
    }

    private static void FakeFail(Exception e)
    {
        Interlocked.Increment(ref _fakeErrors);
        Line($"fake.EXCEPTION {e.GetType().Name}: {OneLine(e.Message)}");
    }

    private static MethodInfo FindIconFor(Type first, bool wantColor)
    {
        foreach (MethodInfo m in typeof(Widgets).GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (m.Name != "GetIconFor") continue;
            ParameterInfo[] ps = m.GetParameters();
            if (ps.Length == 0 || ps[0].ParameterType != first) continue;
            bool hasMat = ps.Any(p => p.ParameterType.IsByRef && p.ParameterType.GetElementType() == typeof(Material));
            bool hasCol = ps.Any(p => p.ParameterType.IsByRef && p.ParameterType.GetElementType() == typeof(Color));
            if (hasMat && hasCol == wantColor) return m;
        }
        return null;
    }

    // ================================================================= boot + inventory

    private static void Watchdog()
    {
        int ms = int.TryParse(Environment.GetEnvironmentVariable("PROBE_TIMEOUT_MS"), out int v) ? v : 420000;
        Thread.Sleep(ms);
        if (Volatile.Read(ref _done) != 0) return;
        Line($"watchdog game={(Current.Game == null ? "<null>" : "set")} maps={SafeMaps()} state={Current.ProgramState} t={Sec():F1}s");
        Line("RESULT=FAIL failed=watchdog-timeout");
        Environment.Exit(4);
    }

    private static void WaitForBoot()
    {
        while (Waylith.Runtime.WaylithRuntime.Current == null) Thread.Sleep(20);
        Waylith.Runtime.WaylithRuntime.Current.App.BootCompleted += Run;
        Console.Error.WriteLine($"{T} armed");
    }

    private static void Run()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        try { Phase1(); }
        catch (Exception ex)
        {
            Line($"phase1 stack:\n{ex}");
            Line("RESULT=FAIL failed=phase1-exception");
            Quit();
        }
    }

    private static void Phase1()
    {
        Line($"phase=start programState={Current.ProgramState} scene={Scene()} inPlay={SafeInPlay()} t={Sec():F1}s");

        try
        {
            foreach (ModMetaData m in ModsConfig.ActiveModsInLoadOrder)
                Line($"mod packageId={m.PackageId} version='{m.ModVersion}'");
        }
        catch (Exception e) { Line($"mod.list FAILED {e.GetType().Name}: {OneLine(e.Message)}"); }

        Assembly gf = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "GlyphFix");
        Line($"glyphfix assembly={(gf == null ? "<null>" : gf.GetName().Name)} " +
             $"version={(gf == null ? "-" : gf.GetName().Version.ToString())}");

        // The headless harness does not run StaticConstructorOnStartup; do exactly what
        // Verse.StaticConstructorOnStartupUtility.CallAll would do for GlyphFix.
        Type bootstrap = gf == null ? null : TypesOf(gf).FirstOrDefault(t => t.FullName == "AgentTouchFix.Bootstrap");
        if (bootstrap != null)
        {
            try
            {
                RuntimeHelpers.RunClassConstructor(bootstrap.TypeHandle);
                Line("mod.startup=ctor-invoked");
            }
            catch (Exception e) { Line($"mod.startup=FAILED {e.GetType().Name}: {OneLine(e.Message)}"); }
        }
        else Line("mod.startup=GlyphFix/Bootstrap not loaded");

        DumpPatchInventory();

        if (!SafeInPlay())
        {
            Line("scene=Play requested");
            SceneManager.LoadScene("Play");
        }
        new Thread(WaitForMap) { IsBackground = true, Name = "patchconflict-map-waiter" }.Start();
    }

    /// <summary>Every method GlyphFix owns a patch on, with its exact runtime signature.</summary>
    private static void DumpPatchInventory()
    {
        try
        {
            List<MethodBase> all = Harmony.GetAllPatchedMethods().ToList();
            Line($"harmony.patched.total={all.Count}");
            int ours = 0;
            foreach (MethodBase mb in all
                .OrderBy(x => x.DeclaringType == null ? "" : x.DeclaringType.FullName, StringComparer.Ordinal)
                .ThenBy(x => x.Name, StringComparer.Ordinal))
            {
                Patches pi = Harmony.GetPatchInfo(mb);
                if (pi == null) continue;
                var mine = new List<string>();
                foreach (Patch p in pi.Prefixes) if (Owned(p)) mine.Add("pre:" + Desc(p));
                foreach (Patch p in pi.Postfixes) if (Owned(p)) mine.Add("post:" + Desc(p));
                foreach (Patch p in pi.Transpilers) if (Owned(p)) mine.Add("tr:" + Desc(p));
                foreach (Patch p in pi.Finalizers) if (Owned(p)) mine.Add("fin:" + Desc(p));
                if (mine.Count == 0) continue;
                ours++;
                Line($"glyphfix.target {Sig(mb)} :: {string.Join(" ; ", mine)}");
            }
            Line($"glyphfix.patched.total={ours}");
        }
        catch (Exception e) { Line($"inventory FAILED {e.GetType().Name}: {OneLine(e.Message)}"); }

        try
        {
            foreach (MethodInfo mi in typeof(Widgets).GetMethods(BindingFlags.Public | BindingFlags.Static)
                                                   .Where(m => m.Name == "GetIconFor")
                                                   .OrderBy(m => m.GetParameters().Length)
                                                   .ThenBy(m => m.GetParameters().FirstOrDefault()?.ParameterType.Name))
            {
                Patches pi = Harmony.GetPatchInfo(mi);
                int total = pi?.Postfixes.Count ?? 0;
                int mine = pi == null ? 0 : pi.Postfixes.Count(Owned);
                string who = pi == null ? "-" : string.Join(",", pi.Postfixes.Select(p => $"{Owner(p)}:{p.PatchMethod?.DeclaringType?.Name}.{p.PatchMethod?.Name}@{Attr(p, "priority")}"));
                Line($"geticonfor {Sig(mi)} postfixes={total} glyphfixPostfixes={mine} who=[{who}]");
            }
        }
        catch (Exception e) { Line($"geticonfor.list FAILED {e.GetType().Name}: {OneLine(e.Message)}"); }
    }

    // ================================================================= map + measurement

    private static void WaitForMap()
    {
        while (Sec() < 300)
        {
            Map found = null;
            try { found = Current.Game?.CurrentMap; } catch { }
            if (found == null) { try { found = Find.AnyPlayerHomeMap; } catch { } }
            if (found != null)
            {
                Map map = found;
                Line($"map=published queueing phase2 t={Sec():F1}s");
                LongEventHandler.QueueLongEvent(() => Phase2(map), null, doAsynchronously: false, null);
                return;
            }
            Thread.Sleep(100);
        }
        Line($"map=FAILED timeout game={(Current.Game == null ? "<null>" : "set")} maps={SafeMaps()}");
        LongEventHandler.QueueLongEvent(() => { Line("RESULT=FAIL failed=map-timeout"); Quit(); }, null, doAsynchronously: false, null);
    }

    private static void Phase2(Map map)
    {
        if (Interlocked.Exchange(ref _phase2, 1) != 0) return;
        try
        {
            Line($"map=OK size={map.Size.x}x{map.Size.z} things={map.listerThings.AllThings.Count} biome={map.Biome?.defName}");
            Line($"mapMesh={MapMeshState(map)}");

            foreach (string species in Species)
            {
                try { Probe(map, species); }
                catch (Exception e) { Line($"species={species} step=EXCEPTION {e.GetType().Name}: {OneLine(e.Message)}"); }
            }

            Line("RESULT=DONE");
        }
        catch (Exception ex)
        {
            Line($"phase2 stack:\n{ex}");
            Line("RESULT=FAIL failed=phase2-exception");
        }
        finally { Quit(); }
    }

    private static void Probe(Map map, string name)
    {
        PawnKindDef kind = DefDatabase<PawnKindDef>.GetNamedSilentFail(name);
        if (kind == null) { Line($"species={name} step=def missing"); return; }
        ThingDef race = kind.race;
        if (race == null) { Line($"species={name} step=def race=<null>"); return; }

        GraphicData gd = null;
        if (kind.lifeStages != null && kind.lifeStages.Count > 0)
            gd = kind.lifeStages[kind.lifeStages.Count - 1]?.bodyGraphicData;
        Line($"species={name} step=body texPath='{Name(gd?.texPath)}' shaderType='{Name(gd?.shaderType?.defName)}' color={C(gd?.color ?? Color.white)}");

        Pawn pawn;
        try { pawn = PawnGenerator.GeneratePawn(kind, null); }
        catch (Exception e) { Line($"species={name} step=generate FAILED {e.GetType().Name}: {OneLine(e.Message)}"); return; }

        IntVec3 cell;
        if (!CellFinder.TryFindRandomCellNear(map.Center, map, 30, c => c.Standable(map), out cell)) cell = map.Center;
        try { GenSpawn.Spawn(pawn, cell, map, WipeMode.Vanish); }
        catch (Exception e) { Line($"species={name} step=spawn FAILED {e.GetType().Name}: {OneLine(e.Message)}"); return; }

        Material bodyMat = null;
        try
        {
            pawn.Drawer?.renderer?.EnsureGraphicsInitialized();
            bodyMat = pawn.Drawer?.renderer?.BodyGraphic?.MatAt(Rot4.East);
        }
        catch (Exception e) { Line($"species={name} step=bodygraphic FAILED {e.GetType().Name}: {OneLine(e.Message)}"); }
        Line($"species={name} step=bodygraphic mat='{Name(bodyMat)}' shader='{Name(bodyMat?.shader)}' tex='{Name(bodyMat?.mainTexture)}'");

        int before = _fakeThingCalls;
        lock (Seen) Seen.Clear();

        float scale = 0f, angle = 0f;
        Vector2 proportions = Vector2.zero;
        Color color = Color.magenta;
        Material material = null;
        Texture icon = null;
        string err = "-";
        try
        {
            icon = Widgets.GetIconFor(pawn, Vector2.one, null, false,
                                      out scale, out angle, out proportions, out color, out material);
        }
        catch (Exception e) { err = $"{e.GetType().Name}: {OneLine(e.Message)}"; }

        Texture2D tex2d = icon as Texture2D;
        bool isBad = icon != null && ReferenceEquals(icon, BaseContent.BadTex);
        bool isComplexUi = material != null && !string.IsNullOrEmpty(material.name) &&
                           material.name.StartsWith("CutoutComplexUI", StringComparison.Ordinal);
        MethodInfo thingOverload = FindIconFor(typeof(Thing), wantColor: true);
        Patches thingInfo = thingOverload == null ? null : Harmony.GetPatchInfo(thingOverload);
        int glyphfixPosts = thingInfo == null ? 0 : thingInfo.Postfixes.Count(Owned);
        int totalPosts = thingInfo?.Postfixes.Count ?? 0;
        int delta = _fakeThingCalls - before;
        string seen;
        lock (Seen) seen = string.Join(" || ", Seen);

        Line($"species={name} step=icon tex='{Name(icon)}' texNull={(icon == null)} badTex={isBad} " +
             $"isWhiteTex={ReferenceEquals(icon, BaseContent.WhiteTex)} isBlackTex={ReferenceEquals(icon, BaseContent.BlackTex)} " +
             $"size={(tex2d == null ? "-" : tex2d.width + "x" + tex2d.height)} " +
             $"material='{Name(material)}' materialNull={(material == null)} isCutoutComplexUI={isComplexUi} " +
             $"color={C(color)} fakeThingCalls={delta} glyphfixPostfixes={glyphfixPosts} totalPostfixes={totalPosts} " +
             $"error='{err}'");
        Line($"species={name} step=fakeSeen [{seen}]");

        // The def overload (PostfixDef): patches the other GlyphFix entry point with a real call.
        Material dm = null;
        Texture dtex = null;
        string derr = "-";
        try { dtex = Widgets.GetIconFor(race, out dm, null, null, null); }
        catch (Exception e) { derr = $"{e.GetType().Name}: {OneLine(e.Message)}"; }
        bool defComplexUi = dm != null && !string.IsNullOrEmpty(dm.name) &&
                            dm.name.StartsWith("CutoutComplexUI", StringComparison.Ordinal);
        Line($"species={name} step=deficon tex='{Name(dtex)}' texNull={(dtex == null)} material='{Name(dm)}' " +
             $"materialNull={(dm == null)} isCutoutComplexUI={defComplexUi} error='{derr}'");

        // The consumer path: Widgets.ThingIcon early-returns on a null result.
        int drawBefore = _fakeThingCalls;
        string drawErr = "-";
        try { Widgets.ThingIcon(new Rect(0f, 0f, 32f, 32f), pawn); }
        catch (Exception e) { drawErr = $"{e.GetType().Name}: {OneLine(e.Message)}"; }
        Line($"species={name} step=draw fakeCallsInDraw={_fakeThingCalls - drawBefore} error='{drawErr}'");
    }

    // ================================================================= helpers

    private static string TryCall(Func<string> f, out string err)
    {
        try { err = "-"; return f(); }
        catch (Exception e) { err = $"{e.GetType().Name}: {OneLine(e.Message)}"; return "<threw>"; }
    }

    private static bool Owned(Patch p) => (Owner(p) ?? "").StartsWith("sleepsheep.waylith", StringComparison.Ordinal);

    private static string Owner(Patch p) => Attr(p, "owner") as string;

    private static string Desc(Patch p)
    {
        string pm = p.PatchMethod == null ? "<null>" : $"{p.PatchMethod.DeclaringType?.Name}.{p.PatchMethod.Name}";
        return $"{Owner(p)}/{pm} prio={Attr(p, "priority")} idx={Attr(p, "index")}";
    }

    /// <summary>Reads a Patch member by name as field or property (Harmony 2 and 3 differ).</summary>
    private static object Attr(object o, string name)
    {
        if (o == null) return null;
        Type t = o.GetType();
        PropertyInfo p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        if (p != null) return p.GetValue(o);
        FieldInfo f = t.GetField(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        return f?.GetValue(o);
    }

    private static IEnumerable<Type> TypesOf(Assembly a)
    {
        try { return a.GetTypes(); }
        catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t != null); }
        catch { return Array.Empty<Type>(); }
    }

    private static string Sig(MethodBase mb)
    {
        var parts = mb.GetParameters().Select(p =>
        {
            Type t = p.ParameterType;
            string tn = t.IsByRef ? t.GetElementType().Name + "&" : t.Name;
            return tn + " " + p.Name;
        });
        return (mb.DeclaringType == null ? "?" : mb.DeclaringType.FullName) + "." + mb.Name + "(" + string.Join(", ", parts) + ")";
    }

    private static string Name(object o)
    {
        if (o == null) return "<null>";
        try
        {
            // Field-then-property: the stubs expose Object.name as a property today, but the mod's
            // own MaterialName() carries a field branch because an earlier build used one.
            for (Type t = o.GetType(); t != null; t = t.BaseType)
            {
                FieldInfo f = t.GetField("name", BindingFlags.Public | BindingFlags.Instance);
                if (f != null) return (f.GetValue(o) as string) ?? "<unnamed>";
                PropertyInfo p = t.GetProperty("name", BindingFlags.Public | BindingFlags.Instance);
                if (p != null && p.GetIndexParameters().Length == 0) return (p.GetValue(o) as string) ?? "<unnamed>";
            }
        }
        catch { }
        return o.ToString();
    }

    private static string C(Color c) => "RGBA(" + F(c.r) + "," + F(c.g) + "," + F(c.b) + "," + F(c.a) + ")";

    private static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    private static string OneLine(string s)
    {
        if (s == null) return "";
        s = s.Replace("\r", " ").Replace("\n", " ");
        return s.Length > 300 ? s.Substring(0, 300) : s;
    }

    private static void Line(string s)
    {
        Console.WriteLine($"{T} {s}");
        Console.Out.Flush();
    }

    private static void Quit()
    {
        if (Interlocked.Exchange(ref _done, 1) != 0) return;
        try { Root.Shutdown(); }
        catch (Exception e) { Console.Error.WriteLine($"{T} shutdown failed: {e.GetType().Name}: {e.Message}"); }
    }

    private static string Scene()
    {
        try { return SceneManager.GetActiveScene().name; } catch (Exception e) { return "err:" + e.GetType().Name; }
    }

    private static bool SafeInPlay() { try { return GenScene.InPlayScene; } catch { return false; } }

    private static string SafeMaps()
    {
        try { return Current.Game == null || Current.Game.Maps == null ? "-" : Current.Game.Maps.Count.ToString(); }
        catch (Exception e) { return "err:" + e.GetType().Name; }
    }

    private static string MapMeshState(Map map)
    {
        try
        {
            MapDrawer drawer = map.mapDrawer;
            if (drawer == null) return "no-drawer";
            int sx = drawer.SectionCount.x, sz = drawer.SectionCount.z, total = sx * sz, built = 0;
            Section[,] secs = drawer.sections;
            if (secs != null)
                for (int i = 0; i < sx; i++)
                    for (int j = 0; j < sz; j++)
                        if (secs[i, j] != null) built++;
            if (secs != null && built == total) return $"complete({built}/{total})";
            drawer.sections = null;
            return $"incomplete({built}/{total})->null";
        }
        catch (Exception e) { return "probe-failed:" + e.GetType().Name; }
    }

    private static double Sec() => Clock.Elapsed.TotalSeconds;
}

/// <summary>Targets that exist only for the Harmony-semantics half; no game state involved.</summary>
public static class SynthTarget
{
    public static string Value() => "orig";
    public static string Value2() => "orig2";
    public static string Value3() => "orig3";
    public static string ValueThrow() => "origT";
}
