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
/// The animal-list icon chain, measured on a real spawned pawn inside the real host:
///
///   Pawngen: PawnGenerator.GeneratePawn(PawnKindDef) -> GenSpawn.Spawn(pawn, cell, map)
///   Widgets.GetIconFor(pawn, Vector2.one, rot: null, stackOfOne: false,
///                      out scale, out angle, out proportions, out color, out material)
///   Widgets.ThingIcon(rect, pawn) -> Widgets.ThingIconWorker(..., material, ...)  (hooked)
///
/// Every value printed is read from the objects the shipped assembly built. The Python gate
/// (pawnsicon/gate.py) compares the same species across the two runs:
///
///   A  GlyphFix 2.7.0   texture present, material = CutoutComplexUI_<tex>   (the blank-icon state)
///   B  GlyphFix 2.7.1   texture present, material = null                    (plain-texture path)
///
/// A missing def, a missing API or a throwing call is printed as such - never replaced by a value.
/// </summary>
internal static class StartupHook
{
    private const string T = "[ANIMALICON]";

    // Rat and Ibex use ShaderTypeDef CutoutComplex (so GetIconFor synthesises a UI material);
    // Squirrel uses the plain Cutout shader, i.e. material == null in BOTH runs - the control that
    // shows the drop is aimed at CutoutComplexUI and not at "any material".
    private static readonly string[] Species = { "Rat", "Ibex", "Squirrel" };

    private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
    private static int _started, _done, _phase2;

    private static int _workerCalls;
    private static string _workerTex = "-";
    private static string _workerMat = "-";

    public static void Initialize()
    {
        try { PatchWorker(); }
        catch (Exception e) { Line($"patch=FAILED {e.GetType().Name}: {OneLine(e.Message)}"); }
        new Thread(WaitForBoot) { IsBackground = true, Name = "animalicon-waiter" }.Start();
        new Thread(Watchdog) { IsBackground = true, Name = "animalicon-watchdog" }.Start();
    }

    /// <summary>
    /// ThingIconWorker is only reached when Widgets.ThingIcon got past its early return, so the hook
    /// turns "the plain-texture branch was taken" from an inference into an observation.
    /// </summary>
    private static void PatchWorker()
    {
        MethodInfo worker = AccessTools.Method(typeof(Widgets), "ThingIconWorker");
        if (worker == null) { Line("patch=not-found Widgets.ThingIconWorker"); return; }
        var harmony = new Harmony("waylith.animalicon");
        string names = string.Join(",", worker.GetParameters().Select(p => p.Name));
        try
        {
            harmony.Patch(worker, prefix: new HarmonyMethod(AccessTools.Method(typeof(StartupHook), nameof(WorkerPrefix))));
            Line($"patch=ThingIconWorker by-name params={worker.GetParameters().Length} names={names}");
        }
        catch (Exception e1)
        {
            // Names are metadata, but do not bet the measurement on them: fall back to positions.
            try
            {
                harmony.Patch(worker, prefix: new HarmonyMethod(AccessTools.Method(typeof(StartupHook), nameof(WorkerPrefixArgs))));
                Line($"patch=ThingIconWorker by-args params={worker.GetParameters().Length} names={names} " +
                     $"(by-name failed: {OneLine(e1.Message)})");
            }
            catch (Exception e2) { Line($"patch=FAILED {e2.GetType().Name}: {OneLine(e2.Message)}"); }
        }
    }

    public static void WorkerPrefix(Texture resolvedIcon, Material mat)
    {
        Record(resolvedIcon, mat);
    }

    public static void WorkerPrefixArgs(object[] __args)
    {
        Texture tex = __args != null && __args.Length > 2 ? __args[2] as Texture : null;
        Material mat = __args != null && __args.Length > 6 ? __args[6] as Material : null;
        Record(tex, mat);
    }

    private static void Record(Texture resolvedIcon, Material mat)
    {
        Interlocked.Increment(ref _workerCalls);
        _workerTex = resolvedIcon == null ? "<null>" : resolvedIcon.name;
        _workerMat = mat == null ? "<null>" : mat.name;
    }

    private static void Watchdog()
    {
        int ms = int.TryParse(Environment.GetEnvironmentVariable("PROBE_TIMEOUT_MS"), out int v) ? v : 420000;
        Thread.Sleep(ms);
        if (Volatile.Read(ref _done) != 0) return;
        Line($"watchdog state game={(Current.Game == null ? "<null>" : "set")} maps={SafeMaps()} " +
             $"programState={Current.ProgramState} inPlay={SafeInPlay()} t={Sec():F1}s");
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

    // ---- 1. what was loaded, then hand the process to the game's own Play scene -----------------
    private static void Phase1()
    {
        Line($"phase=start programState={Current.ProgramState} scene={Scene()} inPlay={SafeInPlay()} t={Sec():F1}s");
        Line($"defs.thingDefs={DefDatabase<ThingDef>.AllDefsListForReading.Count} " +
             $"defs.pawnKindDefs={DefDatabase<PawnKindDef>.AllDefsListForReading.Count}");

        // Who is installed, and which version of its About.xml is on disk.
        try
        {
            foreach (ModMetaData m in ModsConfig.ActiveModsInLoadOrder)
                Line($"mod packageId={m.PackageId} version='{m.ModVersion}' root={m.RootDir}");
        }
        catch (Exception e) { Line($"mod list failed {e.GetType().Name}: {OneLine(e.Message)}"); }

        Assembly gf = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "GlyphFix");
        Line($"glyphfix assembly={(gf == null ? "<null>" : gf.GetName().Name)} " +
             $"version={(gf == null ? "-" : gf.GetName().Version.ToString())} " +
             $"location={(gf == null ? "-" : gf.Location)}");

        // The headless harness never runs StaticConstructorOnStartup (the phone does), so do what
        // RimWorld's StaticConstructorOnStartupUtility does before anything is measured.
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

        // Which GetIconFor overloads carry the mod's postfix - binding, not behaviour.
        try
        {
            foreach (MethodInfo mi in typeof(Widgets).GetMethods(BindingFlags.Public | BindingFlags.Static)
                                                        .Where(m => m.Name == "GetIconFor"))
            {
                PatchInfo info = Harmony.GetPatchInfo(mi);
                string posts = info == null ? "-" :
                    string.Join(";", info.Postfixes.Select(p =>
                        $"{p.PatchMethod.DeclaringType?.Name}.{p.PatchMethod.Name}[{p.owner}]"));
                Line($"harmony target={Signature(mi)} postfixes='{posts}'");
            }
        }
        catch (Exception e) { Line($"harmony.introspect=FAILED {e.GetType().Name}: {OneLine(e.Message)}"); }

        if (!SafeInPlay())
        {
            Line("scene=Play requested (Root_Play.Start runs the game's quick-test new game)");
            SceneManager.LoadScene("Play");
        }

        new Thread(WaitForMap) { IsBackground = true, Name = "animalicon-map-waiter" }.Start();
    }

    // ---- 2. wait (off the main thread) for the game's generator to publish CurrentMap -----------
    private static void WaitForMap()
    {
        string lastScene = null, lastGame = null;
        while (Sec() < 300)
        {
            string scene = Scene();
            if (scene != lastScene)
            {
                Console.Error.WriteLine($"{T} scene={scene} inPlay={SafeInPlay()} t={Sec():F1}s");
                lastScene = scene;
            }
            string game = (Current.Game == null ? "<null>" : "set") + "/" + SafeMaps();
            if (game != lastGame)
            {
                Console.Error.WriteLine($"{T} game={game} programState={Current.ProgramState} t={Sec():F1}s");
                lastGame = game;
            }

            Map found = null;
            try { found = Current.Game?.CurrentMap; } catch { }
            if (found == null)
            {
                try { found = Find.AnyPlayerHomeMap; } catch { }
            }
            if (found != null)
            {
                Map map = found;
                Line($"map=published queueing phase2 t={Sec():F1}s");
                LongEventHandler.QueueLongEvent(() => Phase2(map), null, doAsynchronously: false, null);
                return;
            }
            Thread.Sleep(100);
        }

        Line($"map=FAILED timeout game={(Current.Game == null ? "<null>" : "set")} maps={SafeMaps()} " +
             $"programState={Current.ProgramState} scene={Scene()}");
        LongEventHandler.QueueLongEvent(() =>
        {
            Line("RESULT=FAIL failed=map-timeout");
            Quit();
        }, null, doAsynchronously: false, null);
    }

    // ---- 3. generate + spawn one real animal per species, then measure its icon ------------------
    private static void Phase2(Map map)
    {
        if (Interlocked.Exchange(ref _phase2, 1) != 0) return;
        try
        {
            Line($"map=OK size={map.Size.x}x{map.Size.z} things={map.listerThings.AllThings.Count} " +
                 $"biome={map.Biome?.defName} programState={Current.ProgramState} t={Sec():F1}s");

            // The map mesh is a renderer artifact. Without the full texture set the deferred
            // MapDrawer.RegenerateEverythingNow() dies in SectionLayer_Gas, leaving `sections`
            // allocated but mostly null; Thing.SpawnSetup then dereferences a half-built array.
            // MapMeshDirty is a no-op while `sections == null`, so put the drawer back into that
            // state. Nothing in the icon path reads the mesh.
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
        // ---- the def --------------------------------------------------------------------------
        PawnKindDef kind = DefDatabase<PawnKindDef>.GetNamedSilentFail(name);
        if (kind == null)
        {
            Line($"species={name} step=def missing=PawnKindDef '{name}'");
            return;
        }
        ThingDef race = kind.race;
        if (race == null)
        {
            Line($"species={name} step=def kindDef={kind.defName} race=<null>");
            return;
        }
        Line($"species={name} step=def kindDef={kind.defName} race={race.defName} " +
             $"category={race.category} animal={race.race?.Animal} humanlike={race.race?.Humanlike} " +
             $"lifeStages={(kind.lifeStages == null ? -1 : kind.lifeStages.Count)}");

        GraphicData gd = null;
        if (kind.lifeStages != null && kind.lifeStages.Count > 0)
            gd = kind.lifeStages[kind.lifeStages.Count - 1]?.bodyGraphicData;
        if (gd == null)
        {
            Line($"species={name} step=body bodyGraphicData=<null>");
            return;
        }
        Line($"species={name} step=body texPath='{gd.texPath}' shaderType='{N(gd.shaderType?.defName)}' " +
             $"graphicClass={(gd.graphicClass == null ? "<null>" : gd.graphicClass.Name)} " +
             $"maskPath='{N(gd.maskPath)}' drawSize={F(gd.drawSize.x)}x{F(gd.drawSize.y)} color={C(gd.color)}");

        // ---- the pawn -------------------------------------------------------------------------
        Pawn pawn = null;
        try { pawn = PawnGenerator.GeneratePawn(kind, null); }
        catch (Exception e)
        {
            Line($"species={name} step=generate FAILED {e.GetType().Name}: {OneLine(e.Message)}");
            return;
        }
        if (pawn == null) { Line($"species={name} step=generate pawn=<null>"); return; }
        Line($"species={name} step=generate pawn={pawn.GetType().Name} kindDef={pawn.kindDef?.defName} " +
             $"def={pawn.def?.defName} humanlike={pawn.RaceProps.Humanlike} animal={pawn.RaceProps.Animal} " +
             $"age={F(pawn.ageTracker == null ? 0f : pawn.ageTracker.AgeBiologicalYearsFloat)} " +
             $"lifeStage={(pawn.ageTracker?.CurLifeStage?.defName ?? "<null>")}");

        // ---- spawn into the real map ----------------------------------------------------------
        IntVec3 cell;
        if (!CellFinder.TryFindRandomCellNear(map.Center, map, 30, c => c.Standable(map), out cell)) cell = map.Center;
        try { GenSpawn.Spawn(pawn, cell, map, WipeMode.Vanish); }
        catch (Exception e)
        {
            Line($"species={name} step=spawn FAILED {e.GetType().Name}: {OneLine(e.Message)}");
            return;
        }
        Line($"species={name} step=spawn spawned={pawn.Spawned} map={(pawn.Map == null ? "<null>" : "set")} " +
             $"cell={cell.x},{cell.y},{cell.z}");

        // ---- the body graphic the icon is built from ------------------------------------------
        Graphic body = null;
        Material bodyMat = null;
        string bodyErr = "-";
        try
        {
            pawn.Drawer?.renderer?.EnsureGraphicsInitialized();
            body = pawn.Drawer?.renderer?.BodyGraphic;
            bodyMat = body?.MatAt(Rot4.East);
        }
        catch (Exception e) { bodyErr = $"{e.GetType().Name}: {OneLine(e.Message)}"; }
        Line($"species={name} step=bodygraphic type={(body == null ? "<null>" : body.GetType().Name)} " +
             $"path='{(body == null ? "<null>" : body.path)}' matEast='{N(bodyMat?.name)}' " +
             $"shader='{N(bodyMat?.shader?.name)}' mainTex='{N(bodyMat?.mainTexture?.name)}' error='{bodyErr}'");

        // ---- the exact call Widgets.ThingIcon makes -------------------------------------------
        float scale = 0f, angle = 0f;
        Vector2 proportions = Vector2.zero;
        Color color = Color.magenta;
        Material material = null;
        Texture icon = null;
        string iconErr = "-";
        try
        {
            icon = Widgets.GetIconFor(pawn, Vector2.one, null, false,
                                      out scale, out angle, out proportions, out color, out material);
        }
        catch (Exception e) { iconErr = $"{e.GetType().Name}: {OneLine(e.Message)}"; }

        Texture2D tex2d = icon as Texture2D;
        bool isBad = icon != null && ReferenceEquals(icon, BaseContent.BadTex);
        string matName = material == null ? "<null>" : material.name;
        bool isComplexUi = material != null && !string.IsNullOrEmpty(material.name) &&
                           material.name.StartsWith("CutoutComplexUI", StringComparison.Ordinal);
        Line($"species={name} step=icon tex='{(icon == null ? "<null>" : icon.name)}' " +
             $"isTexture2D={(tex2d != null)} size={(tex2d == null ? "-" : tex2d.width + "x" + tex2d.height)} " +
             $"format={(tex2d == null ? "-" : tex2d.format.ToString())} badTex={isBad} " +
             $"material='{matName}' materialNull={(material == null)} isCutoutComplexUI={isComplexUi} " +
             $"matShader='{N(material?.shader?.name)}' matMainTex='{N(material?.mainTexture?.name)}' " +
             $"color={C(color)} scale={F(scale)} angle={F(angle)} proportions={F(proportions.x)},{F(proportions.y)} " +
             $"error='{iconErr}'");

        // ---- what the draw call actually receives (Widgets.ThingIcon -> ThingIconWorker) -------
        _workerCalls = 0; _workerTex = "-"; _workerMat = "-";
        string drawErr = "-";
        try { Widgets.ThingIcon(new Rect(0f, 0f, 32f, 32f), pawn); }
        catch (Exception e) { drawErr = $"{e.GetType().Name}: {OneLine(e.Message)}"; }
        Line($"species={name} step=draw workerCalls={_workerCalls} workerTex='{_workerTex}' " +
             $"workerMat='{_workerMat}' error='{drawErr}'");
    }

    // ---- helpers ------------------------------------------------------------------------------
    private static IEnumerable<Type> TypesOf(Assembly a)
    {
        try { return a.GetTypes(); }
        catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t != null); }
        catch { return Array.Empty<Type>(); }
    }

    private static string Signature(MethodInfo m)
    {
        var parts = m.GetParameters().Select(p =>
        {
            Type t = p.ParameterType;
            string tn = t.IsByRef ? t.GetElementType().Name + "&" : t.Name;
            return tn + " " + p.Name;
        });
        return m.Name + "(" + string.Join(", ", parts) + ")";
    }

    private static string OneLine(string s)
    {
        if (s == null) return "";
        s = s.Replace("\r", " ").Replace("\n", " ");
        return s.Length > 300 ? s.Substring(0, 300) : s;
    }

    private static string F(float v) { return v.ToString("0.###", CultureInfo.InvariantCulture); }

    private static string N(object v) { return v == null ? "<null>" : v.ToString(); }

    private static string C(Color c)
    {
        return "RGBA(" + F(c.r) + "," + F(c.g) + "," + F(c.b) + "," + F(c.a) + ")";
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
        catch (Exception ex) { Console.Error.WriteLine($"{T} shutdown failed: {ex.GetType().Name}: {ex.Message}"); }
    }

    private static string Scene()
    {
        try { return SceneManager.GetActiveScene().name; } catch (Exception ex) { return "err:" + ex.GetType().Name; }
    }

    private static bool SafeInPlay()
    {
        try { return GenScene.InPlayScene; } catch { return false; }
    }

    private static string SafeMaps()
    {
        try { return Current.Game == null || Current.Game.Maps == null ? "-" : Current.Game.Maps.Count.ToString(); }
        catch (Exception ex) { return "err:" + ex.GetType().Name; }
    }

    /// <summary>
    /// "complete(n/n)" when every Section exists, otherwise nulls the array (the state
    /// MapDrawer.MapMeshDirty treats as "no mesh yet") and returns "incomplete(k/n)->null".
    /// </summary>
    private static string MapMeshState(Map map)
    {
        try
        {
            MapDrawer drawer = map.mapDrawer;
            if (drawer == null) return "no-drawer";
            int sx = drawer.SectionCount.x, sz = drawer.SectionCount.z;
            int total = sx * sz;
            Section[,] secs = drawer.sections;
            int built = 0;
            if (secs != null)
                for (int i = 0; i < sx; i++)
                    for (int j = 0; j < sz; j++)
                        if (secs[i, j] != null) built++;
            if (secs != null && built == total) return $"complete({built}/{total})";
            drawer.sections = null;
            return $"incomplete({built}/{total})->null";
        }
        catch (Exception ex) { return "probe-failed:" + ex.GetType().Name; }
    }

    private static double Sec() { return Clock.Elapsed.TotalSeconds; }
}
