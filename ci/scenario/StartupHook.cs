using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using UnityEngine.SceneManagement;
using Verse;

/// <summary>
/// Runs inside the real game process (DOTNET_STARTUP_HOOKS) and drives the same path the UI drives:
///
///   BootCompleted -> SceneManager.LoadScene("Play")
///     -> Bootstrap.BuildPlayScene adds Root_Play -> Root.Start() creates UIRoot_Play (Find.MapUI casts to it)
///     -> Root_Play.Start() queues its own quick-test long event:
///          SetupForQuickTestPlay + PrepForMapGen + PreMapGenerate + Game.InitNewGame
///     -> MapGenerator.GenerateMap -> Game.AddMap -> Find.ColonistBar / Find.MapUI
///
/// No map is re-implemented here. Once the game's own generator has produced CurrentMap, this hook:
///   * makes Gun_Revolver_Unique with ThingMaker.MakeThing and spawns it with GenSpawn into that map
///   * asks Widgets.GetIconFor(thing, ...) - the exact call Widgets.ThingIcon makes - for the icon
///   * decodes the returned texture to RGBA and writes a PNG (zlib/deflate written here, no engine
///     encoder involved), plus pixel statistics
///   * asserts the texture is one of the def's Graphic_Random variants (not BaseContent.BadTex,
///     not the 2x2 decode-failure placeholder, not a *_m mask, and the variant index the graphic
///     itself picks for this thing id)
///
/// Everything it prints is prefixed [SCENARIO]; ci/report.py turns those lines into the gate.
/// </summary>
internal static class StartupHook
{
    private const string T = "[SCENARIO]";

    private static int _started;
    private static int _done;
    private static int _phase2;
    private static ThingDef _target;
    private static readonly List<string> Failed = new List<string>();
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    public static void Initialize()
    {
        new Thread(WaitForBoot) { IsBackground = true, Name = "scenario-waiter" }.Start();
        new Thread(Watchdog) { IsBackground = true, Name = "scenario-watchdog" }.Start();
    }

    private static void WaitForBoot()
    {
        while (Waylith.Runtime.WaylithRuntime.Current == null) Thread.Sleep(20);
        Waylith.Runtime.WaylithRuntime.Current.App.BootCompleted += Run;
        Console.Error.WriteLine($"{T} hook armed");
    }

    private static void Watchdog()
    {
        Thread.Sleep(TimeSpan.FromSeconds(420));
        if (Volatile.Read(ref _done) != 0) return;
        Console.Error.WriteLine($"{T} watchdog state game={(Current.Game == null ? "<null>" : "set")} " +
                                $"maps={SafeMaps()} programState={Current.ProgramState} inPlay={SafeInPlay()} t={Sec():F1}s");
        Console.Error.WriteLine($"{T} RESULT=FAIL failed=watchdog-timeout");
        Environment.Exit(4);
    }

    private static void Run()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        try
        {
            Phase1();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{T} phase1 stack:\n{ex}");
            Console.Error.WriteLine($"{T} RESULT=FAIL failed=phase1-exception");
            Quit();
        }
    }

    // ---- 1. defs, then hand the process to the game's own Play scene --------------------------
    private static void Phase1()
    {
        Console.Error.WriteLine($"{T} boot=complete programState={Current.ProgramState} scene={Scene()} inPlay={SafeInPlay()} t={Sec():F1}s");

        var thingDefs = DefDatabase<ThingDef>.AllDefsListForReading;
        Console.Error.WriteLine($"{T} defs.thingDefs={thingDefs.Count}");

        var uniques = thingDefs
            .Where(d => d.comps != null && d.comps.Any(c => c != null && c.GetType().Name.Contains("UniqueWeapon")))
            .ToList();
        Console.Error.WriteLine($"{T} uniqueWeaponDefs={uniques.Count} " +
                                string.Join(",", uniques.Take(8).Select(d => d.defName)));

        _target = uniques.FirstOrDefault(d => d.defName == "Gun_Revolver_Unique")
                  ?? uniques.FirstOrDefault()
                  ?? DefDatabase<ThingDef>.GetNamedSilentFail("Gun_Revolver");
        if (_target == null)
        {
            Console.Error.WriteLine($"{T} RESULT=FAIL failed=no-target-def");
            Quit();
            return;
        }

        var variants = VariantNames(_target);
        Console.Error.WriteLine($"{T} target={_target.defName} label='{_target.label}' " +
                                $"graphic={(_target.graphic == null ? "<null>" : _target.graphic.GetType().Name)} " +
                                $"texPath={_target.graphicData?.texPath} graphicClass={_target.graphicData?.graphicClass?.Name}");
        Console.Error.WriteLine($"{T} def.variants={variants.Count} names={string.Join(",", variants)}");

        var folder = new List<string>();
        try
        {
            folder = ContentFinder<Texture2D>.GetAllInFolder(_target.graphicData?.texPath)
                .Select(t => t.name).OrderBy(n => n, StringComparer.Ordinal).ToList();
        }
        catch (Exception ex) { Console.Error.WriteLine($"{T} folder probe failed: {ex.GetType().Name}: {ex.Message}"); }
        Console.Error.WriteLine($"{T} folder[{_target.graphicData?.texPath}] textures={folder.Count} names={string.Join(",", folder)}");

        if (!SafeInPlay())
        {
            Console.Error.WriteLine($"{T} scene=Play requested (Root_Play.Start runs the game's quick-test new game)");
            SceneManager.LoadScene("Play");
        }

        new Thread(WaitForMap) { IsBackground = true, Name = "scenario-map-waiter" }.Start();
    }

    // ---- 2. wait (off the main thread) for the game's generator to publish CurrentMap ----------
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
                Console.Error.WriteLine($"{T} map published by the game, queueing phase2 on the main thread t={Sec():F1}s");
                LongEventHandler.QueueLongEvent(() => Phase2(map), null, doAsynchronously: false, null);
                return;
            }
            Thread.Sleep(100);
        }

        Console.Error.WriteLine($"{T} map=FAILED timeout game={(Current.Game == null ? "<null>" : "set")} maps={SafeMaps()} " +
                                $"programState={Current.ProgramState} scene={Scene()}");
        LongEventHandler.QueueLongEvent(() =>
        {
            Console.Error.WriteLine($"{T} RESULT=FAIL failed=map-timeout");
            Quit();
        }, null, doAsynchronously: false, null);
    }

    // ---- 3. spawn the real thing, take its icon, decode it, assert ----------------------------
    private static void Phase2(Map map)
    {
        if (Interlocked.Exchange(ref _phase2, 1) != 0) return;
        try
        {
            Console.Error.WriteLine($"{T} map=OK size={map.Size.x}x{map.Size.z} things={map.listerThings.AllThings.Count} " +
                                    $"cells={map.cellIndices.NumGridCells} parent={map.Parent?.def?.defName ?? "<null>"} " +
                                    $"biome={map.Biome?.defName} programState={Current.ProgramState} t={Sec():F1}s");

            Thing thing = ThingMaker.MakeThing(_target);
            var twc = thing as ThingWithComps;
            Console.Error.WriteLine($"{T} made def={thing.def.defName} thingID={thing.thingIDNumber} " +
                                    $"type={thing.GetType().Name} comps=" +
                                    string.Join(",", (twc == null ? new List<ThingComp>() : twc.AllComps).Select(c => c.GetType().Name)));

            // The map mesh is a renderer artifact. On a runner without the full texture set the
            // deferred MapDrawer.RegenerateEverythingNow() in Map.FinalizeInit dies in
            // SectionLayer_Gas (GasMat is null because Things/Gas/GasCloudThickA is not in the
            // shipped subset), which leaves `sections` allocated but mostly null. MapMeshDirty is
            // written to be a no-op while `sections == null` (the state every map is in before
            // that deferred pass), so put the drawer back into that state instead of letting
            // Thing.SpawnSetup dereference a half-built section array. Nothing in the icon path
            // reads the mesh.
            Console.Error.WriteLine($"{T} mapMesh={MapMeshState(map)}");

            IntVec3 cell;
            if (!CellFinder.TryFindRandomCellNear(map.Center, map, 30, c => c.Standable(map), out cell)) cell = map.Center;
            Thing spawned = GenSpawn.Spawn(thing, cell, map, WipeMode.Vanish);
            Check("spawn", spawned != null && thing.Spawned && thing.Map == map,
                  $"cell={cell} spawned={thing.Spawned} map={(thing.Map == null ? "<null>" : "set")}");

            CompQuality quality = twc == null ? null : twc.TryGetComp<CompQuality>();
            Console.Error.WriteLine($"{T} spawn=OK def={thing.def.defName} thingID={thing.thingIDNumber} cell={cell} " +
                                    $"quality={(quality == null ? "<none>" : quality.Quality.ToString())} " +
                                    $"stuff={(thing.Stuff == null ? "<none>" : thing.Stuff.defName)}");

            // ---- the exact call Widgets.ThingIcon makes -------------------------------------
            float scale, angle;
            Vector2 proportions;
            Color color;
            Material mat;
            Texture iconTex = Widgets.GetIconFor(thing, new Vector2(128f, 128f), null, stackOfOne: false,
                                                 out scale, out angle, out proportions, out color, out mat);
            Texture2D tex = iconTex as Texture2D;
            bool readBack = false;
            if (tex == null && iconTex is RenderTexture rt)
            {
                var tmp = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, mipChain: false);
                RenderTexture prev = RenderTexture.active;
                RenderTexture.active = rt;
                tmp.ReadPixels(new Rect(0f, 0f, rt.width, rt.height), 0, 0);
                tmp.Apply();
                RenderTexture.active = prev;
                tex = tmp;
                readBack = true;
            }

            var variants = VariantNames(_target);
            var masks = MaskNames(_target);
            bool bad = tex == null || tex == BaseContent.BadTex;
            bool placeholder = tex != null && tex.width <= 2 && tex.height <= 2;
            string name = tex == null ? "<null>" : tex.name;
            int variantIndex = variants.IndexOf(name);
            int pick = (thing.OverrideGraphicIndex ?? thing.thingIDNumber) % Math.Max(1, variants.Count);
            bool isMask = name.EndsWith("_m", StringComparison.Ordinal) || masks.Contains(name);

            Color32[] px = null;
            string pixelError = null;
            if (tex != null)
            {
                try { px = tex.GetPixels32(); }
                catch (Exception ex) { pixelError = ex.GetType().Name + ": " + ex.Message; }
            }

            Console.Error.WriteLine($"{T} icon=def={_target.defName} tex={name} size={(tex == null ? "-" : tex.width + "x" + tex.height)} " +
                                    $"badTex={bad} placeholder={placeholder} mask={isMask} readBack={readBack} " +
                                    $"variantIndex={variantIndex} expectedIndex={pick} variants={variants.Count} " +
                                    $"material={(mat == null ? "<null>" : mat.name)} " +
                                    $"materialTex={(mat == null || mat.mainTexture == null ? "<null>" : mat.mainTexture.name)} " +
                                    $"uiIcon={(_target.uiIcon == null ? "<null>" : _target.uiIcon.name)} " +
                                    $"scale={scale.ToString("0.###", CultureInfo.InvariantCulture)} " +
                                    $"color={color.r.ToString("0.###", CultureInfo.InvariantCulture)},{color.g.ToString("0.###", CultureInfo.InvariantCulture)},{color.b.ToString("0.###", CultureInfo.InvariantCulture)}");

            string stats = "no-pixels";
            double coverage = 0, mr = 0, mg = 0, mb = 0, ma = 0;
            int distinct = 0, bboxW = 0, bboxH = 0;
            string pngPath = null, sha = null;
            long pngBytes = 0;

            if (px != null && px.Length > 0 && tex.width > 0 && tex.height > 0)
            {
                stats = PixelStats(px, tex.width, tex.height, out coverage, out mr, out mg, out mb, out ma,
                                   out distinct, out bboxW, out bboxH);
                Console.Error.WriteLine($"{T} pixels={stats}");

                try
                {
                    pngPath = Path.Combine(OutDir, $"icon_thing_{_target.defName}_{thing.thingIDNumber}.png");
                    WritePng(pngPath, tex.width, tex.height, px);
                    byte[] bytes = File.ReadAllBytes(pngPath);
                    pngBytes = bytes.Length;
                    using (var sha256 = SHA256.Create())
                        sha = Convert.ToHexString(sha256.ComputeHash(bytes)).ToLowerInvariant();
                    Console.Error.WriteLine($"{T} png={pngPath} bytes={pngBytes} sha256={sha}");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"{T} png write failed: {ex.GetType().Name}: {ex.Message}");
                }

                // the def-level icon (what a thing-less UI path draws) for comparison
                try
                {
                    Texture2D defIcon = Widgets.GetIconFor(_target);
                    string dp = Path.Combine(OutDir, $"icon_def_{_target.defName}.png");
                    if (defIcon != null && defIcon != BaseContent.BadTex && defIcon.width > 2 && defIcon.height > 2)
                    {
                        WritePng(dp, defIcon.width, defIcon.height, defIcon.GetPixels32());
                        Console.Error.WriteLine($"{T} defIcon=tex={defIcon.name} size={defIcon.width}x{defIcon.height} png={dp}");
                    }
                    else
                    {
                        Console.Error.WriteLine($"{T} defIcon=tex={(defIcon == null ? "<null>" : defIcon.name)} " +
                                                $"size={(defIcon == null ? "-" : defIcon.width + "x" + defIcon.height)}");
                    }
                }
                catch (Exception ex) { Console.Error.WriteLine($"{T} defIcon probe failed: {ex.GetType().Name}: {ex.Message}"); }
            }
            else if (pixelError != null)
            {
                Console.Error.WriteLine($"{T} pixels=unavailable {pixelError}");
            }

            // ---- assertions -----------------------------------------------------------------
            Check("variants-five", variants.Count == 5, $"count={variants.Count} names={string.Join(",", variants)}");

            // every variant, not only the one this thing drew: a real 128x128 texture with the
            // expected name. If the folder were masks or placeholders these would not resolve.
            var resolved = new List<string>();
            Graphic_Collection coll = Collection(_target);
            int collCount = coll == null ? 0 : coll.subGraphics.Length;
            for (int i = 0; i < collCount; i++)
            {
                Texture2D vt = null;
                try
                {
                    Material vm = coll.subGraphics[i].MatSingle;
                    vt = vm == null ? null : vm.mainTexture as Texture2D;
                }
                catch { }
                resolved.Add($"{i}:{(vt == null ? "<null>" : vt.name)}:{(vt == null ? "-" : vt.width + "x" + vt.height)}");
                Check($"variant-{i}-texture",
                      vt != null && vt.width == 128 && vt.height == 128 && variants.Contains(vt.name),
                      $"name={(vt == null ? "<null>" : vt.name)} size={(vt == null ? "-" : vt.width + "x" + vt.height)}");
            }
            Console.Error.WriteLine($"{T} variantTextures={string.Join(",", resolved)}");

            // a second thing of the same def: Graphic_Random picks by thingIDNumber, so the icon
            // must follow the id, not be a constant for the def.
            string name2 = "<none>";
            int index2 = -1, pick2 = -1;
            try
            {
                Thing second = ThingMaker.MakeThing(_target);
                IntVec3 cell2;
                if (!CellFinder.TryFindRandomCellNear(map.Center, map, 30, c => c.Standable(map), out cell2)) cell2 = map.Center;
                GenSpawn.Spawn(second, cell2, map, WipeMode.Vanish);
                float s2, a2;
                Vector2 pr2;
                Color c2;
                Material m2;
                Texture2D tex2 = Widgets.GetIconFor(second, new Vector2(128f, 128f), null, stackOfOne: false,
                                                    out s2, out a2, out pr2, out c2, out m2) as Texture2D;
                name2 = tex2 == null ? "<null>" : tex2.name;
                index2 = variants.IndexOf(name2);
                pick2 = (second.OverrideGraphicIndex ?? second.thingIDNumber) % Math.Max(1, variants.Count);
                Console.Error.WriteLine($"{T} icon2=def={_target.defName} thingID={second.thingIDNumber} tex={name2} " +
                                        $"size={(tex2 == null ? "-" : tex2.width + "x" + tex2.height)} " +
                                        $"variantIndex={index2} expectedIndex={pick2} spawned={second.Spawned}");
                Check("second-thing-variant", index2 == pick2 && index2 >= 0,
                      $"thingID={second.thingIDNumber} picked={pick2} actual={index2}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"{T} icon2 stack:\n{ex}");
                Check("second-thing-variant", false, "exception " + ex.GetType().Name);
            }

            Check("icon-not-null", tex != null, $"tex={name}");
            Check("icon-not-badtex", !bad, $"badTex={bad}");
            Check("icon-not-placeholder", !placeholder, $"size={(tex == null ? "-" : tex.width + "x" + tex.height)}");
            Check("icon-not-mask", !isMask, $"name={name} maskNames={string.Join(",", masks)}");
            Check("icon-is-variant", variantIndex >= 0, $"name={name} variants={string.Join(",", variants)}");
            Check("icon-variant-index", variantIndex == pick, $"picked={pick} actual={variantIndex} thingID={thing.thingIDNumber}");
            Check("icon-size", tex != null && tex.width == 128 && tex.height == 128,
                  $"size={(tex == null ? "-" : tex.width + "x" + tex.height)}");
            Check("icon-material", mat != null && mat.mainTexture != null && mat.mainTexture.name == name,
                  $"material={(mat == null ? "<null>" : mat.name)} materialTex={(mat == null || mat.mainTexture == null ? "<null>" : mat.mainTexture.name)}");
            Check("pixels-decoded", px != null && tex != null && px.Length == tex.width * tex.height,
                  $"pixels={(px == null ? "<null>" : px.Length.ToString())} size={(tex == null ? "-" : tex.width + "x" + tex.height)}");
            Check("coverage", coverage >= 0.005 && coverage <= 0.90, $"coverage={coverage.ToString("P2", CultureInfo.InvariantCulture)}");
            Check("not-red-only-mask", mg > 0.5 && mb > 0.5, $"meanRGB=({mr:0.##},{mg:0.##},{mb:0.##})");
            Check("png-written", pngPath != null && pngBytes > 0, $"png={pngPath ?? "<none>"} bytes={pngBytes}");
            Check("spawned-on-map", thing.Spawned && thing.Map == map, $"spawned={thing.Spawned}");

            // ---- the "modified weapon" risk: CompUniqueWeapon.ForceColor -> GraphicColoredFor ----
            // A unique weapon's DrawColor is not white (CompUniqueWeapon picks a ColorDef), so the
            // icon path may run through Graphic.GetColoredVersion, i.e. a *clone* of the
            // Graphic_RandomRotated/Graphic_Random pair. Take the same thing twice: first with the
            // comp colour removed (plain def graphic), then with a forced non-white colour, and
            // compare the icon at texture level (name, variant, size, pixels, mask) plus the
            // clone's subGraphics array (count/paths/textures).
            ColorExperiment(map);

            WriteJson(thing, name, tex, mat, variants, masks, variantIndex, pick, coverage, mr, mg, mb, ma,
                      distinct, bboxW, bboxH, pngPath, pngBytes, sha, stats);

            Console.Error.WriteLine($"{T} RESULT={(Failed.Count == 0 ? "OK" : "FAIL")}" +
                                    (Failed.Count == 0 ? "" : " failed=" + string.Join(",", Failed)));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{T} phase2 stack:\n{ex}");
            Console.Error.WriteLine($"{T} RESULT=FAIL failed=phase2-exception");
        }
        Quit();
    }

    // ---- helpers -----------------------------------------------------------------------------
    private static void Check(string name, bool ok, string detail)
    {
        Console.Error.WriteLine($"{T} assert={name} ok={(ok ? "true" : "false")} {detail}");
        if (!ok) Failed.Add(name);
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

    /// <summary>
    /// Returns "complete(n/n)" when every Section exists, otherwise nulls the array (the state
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
        catch (Exception ex)
        {
            return "probe-failed:" + ex.GetType().Name;
        }
    }

    private static string SafeMaps()
    {
        try { return Current.Game == null || Current.Game.Maps == null ? "-" : Current.Game.Maps.Count.ToString(); }
        catch (Exception ex) { return "err:" + ex.GetType().Name; }
    }

    private static double Sec()
    {
        return Clock.Elapsed.TotalSeconds;
    }

    private static string OutDir
    {
        get
        {
            string d = Environment.GetEnvironmentVariable("SCENARIO_OUT");
            if (string.IsNullOrEmpty(d)) d = Directory.GetCurrentDirectory();
            Directory.CreateDirectory(d);
            return d;
        }
    }

    /// <summary>The def's Graphic_Collection (Graphic_Random), unwrapping Graphic_RandomRotated.</summary>
    private static Graphic_Collection Collection(ThingDef def)
    {
        Graphic g = def.graphic;
        if (g is Graphic_RandomRotated) g = ((Graphic_RandomRotated)g).subGraphic;
        return g as Graphic_Collection;
    }

    /// <summary>The Graphic_Single sub-graphics of the def's Graphic_Random, as texture names.</summary>
    private static List<string> VariantNames(ThingDef def)
    {
        var names = new List<string>();
        try
        {
            Graphic g = def.graphic;
            if (g is Graphic_RandomRotated) g = ((Graphic_RandomRotated)g).subGraphic;
            if (g is Graphic_Collection)
            {
                foreach (Graphic sg in ((Graphic_Collection)g).subGraphics)
                {
                    string n = null;
                    try { n = sg.MatSingle == null || sg.MatSingle.mainTexture == null ? null : sg.MatSingle.mainTexture.name; }
                    catch { }
                    if (string.IsNullOrEmpty(n)) n = Path.GetFileName(sg.path);
                    names.Add(n);
                }
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"{T} variant probe failed: {ex.GetType().Name}: {ex.Message}"); }
        return names;
    }

    private static List<string> MaskNames(ThingDef def)
    {
        var masks = new List<string>();
        try
        {
            string tp = def.graphicData == null ? null : def.graphicData.texPath;
            if (!tp.NullOrEmpty())
                masks = ContentFinder<Texture2D>.GetAllInFolder(tp)
                    .Select(t => t.name)
                    .Where(n => n.EndsWith("_m", StringComparison.Ordinal))
                    .OrderBy(n => n, StringComparer.Ordinal)
                    .ToList();
        }
        catch { }
        return masks;
    }

    private static string PixelStats(Color32[] px, int w, int h, out double coverage, out double mr, out double mg,
                                     out double mb, out double ma, out int distinct, out int bboxW, out int bboxH)
    {
        long opaque = 0;
        long sr = 0, sg = 0, sb = 0, sa = 0;
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        var set = new HashSet<int>();
        for (int i = 0; i < px.Length; i++)
        {
            Color32 c = px[i];
            sa += c.a;
            if (c.a == 0) continue;
            opaque++;
            sr += c.r; sg += c.g; sb += c.b;
            set.Add((c.r << 16) | (c.g << 8) | c.b);
            int x = i % w, y = i / w;
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
        }
        coverage = px.Length == 0 ? 0 : (double)opaque / px.Length;
        mr = opaque == 0 ? 0 : (double)sr / opaque;
        mg = opaque == 0 ? 0 : (double)sg / opaque;
        mb = opaque == 0 ? 0 : (double)sb / opaque;
        ma = px.Length == 0 ? 0 : (double)sa / px.Length;
        distinct = set.Count;
        bboxW = maxX < 0 ? 0 : maxX - minX + 1;
        bboxH = maxY < 0 ? 0 : maxY - minY + 1;
        return $"size={w}x{h} opaque={opaque}/{px.Length} " +
               $"coverage={coverage.ToString("P2", CultureInfo.InvariantCulture)} " +
               $"meanRGB=({mr.ToString("0.##", CultureInfo.InvariantCulture)},{mg.ToString("0.##", CultureInfo.InvariantCulture)},{mb.ToString("0.##", CultureInfo.InvariantCulture)}) " +
               $"meanA={ma.ToString("0.##", CultureInfo.InvariantCulture)} distinctColors={distinct} " +
               $"bbox={(maxX < 0 ? "empty" : bboxW + "x" + bboxH + " at " + minX + "," + minY)}";
    }

    // ---- 4. colored-clone experiment ----------------------------------------------------------
    private sealed class IconSnap
    {
        public string Tag;
        public string Tex = "<null>";
        public int Width;
        public int Height;
        public bool Bad = true;
        public bool Placeholder;
        public bool Mask;
        public int VariantIndex = -1;
        public int ExpectedIndex = -1;
        public string Material = "<null>";
        public string MaterialTex = "<null>";
        public string MaterialMask = "<null>";
        public Color MaterialColor = Color.white;
        public double Coverage;
        public double MeanR;
        public double MeanG;
        public double MeanB;
        public double MeanA;
        public int Distinct;
        public int BboxW;
        public int BboxH;
        public Color32[] Pixels;
        public string Png = "<none>";
        public long PngBytes;
        public string Sha = "";
        public string Size => Width + "x" + Height;
    }

    private sealed class CollInfo
    {
        public string Type = "<null>";
        public string Path = "<null>";
        public int Count = -1;
        public string[] SubPaths = Array.Empty<string>();
        public string[] SubTex = Array.Empty<string>();
        public string[] SubMask = Array.Empty<string>();
        public Color[] SubColors = Array.Empty<Color>();
    }

    /// <summary>
    /// Takes one thing, removes CompUniqueWeapon's color, snapshots its icon, then forces the same
    /// thing to a known non-white DrawColor (the value ForceColor feeds GraphicColoredFor) and
    /// snapshots again. Everything here is on the main thread, inside the same long event.
    /// </summary>
    private static void ColorExperiment(Map map)
    {
        try
        {
            Console.Error.WriteLine($"{T} color.begin def={_target.defName} ignoreThingDrawColor={_target.graphicData?.ignoreThingDrawColor} t={Sec():F1}s");
            Thing probe = ThingMaker.MakeThing(_target);
            ThingWithComps twc = probe as ThingWithComps;
            CompUniqueWeapon cuw = twc == null ? null : twc.TryGetComp<CompUniqueWeapon>();
            string naturalColorDef = cuw == null || cuw.color == null ? "" : cuw.color.defName;
            Color naturalDraw = probe.DrawColor;
            Console.Error.WriteLine($"{T} color.made thingID={probe.thingIDNumber} type={probe.GetType().Name} " +
                                    $"cuw={(cuw == null ? "<null>" : "present")} naturalColorDef={(naturalColorDef == "" ? "<null>" : naturalColorDef)} " +
                                    $"naturalDraw={C(naturalDraw)} traits={(cuw == null || cuw.traits == null ? 0 : cuw.traits.Count)}");

            IntVec3 cell;
            if (!CellFinder.TryFindRandomCellNear(map.Center, map, 30, c => c.Standable(map), out cell)) cell = map.Center;
            Thing spawned = GenSpawn.Spawn(probe, cell, map, WipeMode.Vanish);
            Check("color-spawn", spawned != null && probe.Spawned && probe.Map == map,
                  $"cell={cell} spawned={probe.Spawned} map={(probe.Map == null ? "<null>" : "set")}");
            if (cuw == null)
            {
                Check("color-comp", false, "no CompUniqueWeapon on the made thing");
                return;
            }

            ColorDef forcedDef = DefDatabase<ColorDef>.GetNamedSilentFail("UniqueWeapon_Red");
            if (forcedDef == null)
            {
                forcedDef = DefDatabase<ColorDef>.AllDefs
                    .Where(c => c != null && c.colorType == ColorType.Weapon)
                    .OrderBy(c => c.defName, StringComparer.Ordinal)
                    .FirstOrDefault();
            }
            if (forcedDef == null)
            {
                Check("color-forced-def", false, "no ColorDef with colorType=Weapon");
                return;
            }
            Color forced = forcedDef.color;
            Console.Error.WriteLine($"{T} color.forced colorDef={forcedDef.defName} color={C(forced)}");

            List<string> variants = VariantNames(_target);
            List<string> masks = MaskNames(_target);

            // (a) same thing with the comp colour removed: plain def graphic path.
            cuw.color = null;
            probe.Notify_ColorChanged();
            Color whiteDraw = probe.DrawColor;
            bool white = whiteDraw.IndistinguishableFrom(Color.white);
            Graphic wg = probe.Graphic;
            bool whiteClone = !ReferenceEquals(wg, _target.graphic);
            IconSnap ws = CaptureIcon(probe, variants, masks, "white");
            CollInfo wci = InspectGraphic(wg);
            Console.Error.WriteLine($"{T} color.white drawColor={C(whiteDraw)} white={white} clone={whiteClone} " +
                                    $"graphic={wg.GetType().Name} tex={ws.Tex} size={ws.Size} variantIndex={ws.VariantIndex} " +
                                    $"expectedIndex={ws.ExpectedIndex} badTex={ws.Bad} mask={ws.Mask} " +
                                    $"meanRGB=({ws.MeanR:0.##},{ws.MeanG:0.##},{ws.MeanB:0.##}) " +
                                    $"material={ws.Material} materialTex={ws.MaterialTex} materialColor={C(ws.MaterialColor)}");
            Console.Error.WriteLine($"{T} color.white.structure graphic={wci.Type} path={wci.Path} subCount={wci.Count} " +
                                    $"subPaths={string.Join("|", wci.SubPaths)} subTextures={string.Join("|", wci.SubTex)} " +
                                    $"subMasks={string.Join("|", wci.SubMask)}");

            // (b) same thing, forced non-white DrawColor exactly as CompUniqueWeapon.ForceColor does.
            cuw.color = forcedDef;
            probe.Notify_ColorChanged();
            Color tintedDraw = probe.DrawColor;
            bool tinted = Close(tintedDraw, forced, 0.01f);
            Graphic tg = probe.Graphic;
            bool tintedClone = !ReferenceEquals(tg, _target.graphic);
            IconSnap ts = CaptureIcon(probe, variants, masks, "tinted");
            CollInfo tci = InspectGraphic(tg);
            CollInfo dci = InspectGraphic(_target.graphic);
            Console.Error.WriteLine($"{T} color.tinted drawColor={C(tintedDraw)} matchesForced={tinted} clone={tintedClone} " +
                                    $"graphic={tg.GetType().Name} tex={ts.Tex} size={ts.Size} variantIndex={ts.VariantIndex} " +
                                    $"expectedIndex={ts.ExpectedIndex} badTex={ts.Bad} mask={ts.Mask} " +
                                    $"meanRGB=({ts.MeanR:0.##},{ts.MeanG:0.##},{ts.MeanB:0.##}) " +
                                    $"material={ts.Material} materialTex={ts.MaterialTex} materialColor={C(ts.MaterialColor)}");
            Console.Error.WriteLine($"{T} color.tinted.structure graphic={tci.Type} path={tci.Path} subCount={tci.Count} " +
                                    $"subPaths={string.Join("|", tci.SubPaths)} subTextures={string.Join("|", tci.SubTex)} " +
                                    $"subMasks={string.Join("|", tci.SubMask)} subColors={CArray(tci.SubColors)}");
            Console.Error.WriteLine($"{T} color.def.structure graphic={dci.Type} path={dci.Path} subCount={dci.Count} " +
                                    $"subPaths={string.Join("|", dci.SubPaths)} subTextures={string.Join("|", dci.SubTex)}");

            bool pixelsSame = PixelsEqual(ws.Pixels, ts.Pixels);
            bool meanSame = Math.Abs(ws.MeanR - ts.MeanR) < 0.01 && Math.Abs(ws.MeanG - ts.MeanG) < 0.01 &&
                            Math.Abs(ws.MeanB - ts.MeanB) < 0.01;
            bool textureSame = ws.Tex == ts.Tex;
            bool variantSame = ws.VariantIndex == ts.VariantIndex && ts.VariantIndex == ts.ExpectedIndex;
            bool sizeSame = ws.Width == ts.Width && ws.Height == ts.Height;
            bool matTexSame = ws.MaterialTex == ts.MaterialTex;
            bool matNameSame = ws.Material == ts.Material;
            bool badSame = ws.Bad == ts.Bad && !ts.Bad;
            bool maskSame = ws.Mask == ts.Mask && !ts.Mask;
            bool tintApplied = Close(ws.MaterialColor, Color.white, 0.01f) && !Close(ts.MaterialColor, Color.white, 0.01f) &&
                               Close(ts.MaterialColor, forced, 0.01f);
            bool subFive = tci.Count == 5 && wci.Count == 5 && dci.Count == 5;
            bool pathSame = tci.Path == dci.Path && wci.Path == dci.Path;
            bool subPathsSame = SameStrings(tci.SubPaths, dci.SubPaths) && SameStrings(wci.SubPaths, dci.SubPaths);
            bool subTexSame = SameStrings(tci.SubTex, dci.SubTex) && SameStrings(wci.SubTex, dci.SubTex);
            bool subTint = tci.SubColors.Length == 5 && tci.SubColors.All(c => Close(c, forced, 0.01f));

            // MatAt(rot, thing) forwards the thing; MatSingle does not. Widgets.GetIconFor uses
            // ExtractInnerGraphicFor(thing) before MatAt, so it must land on the thing's variant.
            Material matAtMat = null;
            try { matAtMat = tg.MatAt(probe.Rotation, probe); } catch { }
            string matAtTex = matAtMat == null || matAtMat.mainTexture == null ? "<null>" : matAtMat.mainTexture.name;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < 12; i++)
            {
                try
                {
                    Material m = tg.MatSingle;
                    if (m != null && m.mainTexture != null) seen.Add(m.mainTexture.name);
                }
                catch { }
            }
            Console.Error.WriteLine($"{T} color.matsingle root={tg.GetType().Name} reads=12 distinct={seen.Count} " +
                                    $"names={string.Join(",", seen.OrderBy(n => n, StringComparer.Ordinal))} " +
                                    $"matAt(rot,thing)={matAtTex} expected={ts.Tex}");
            Console.Error.WriteLine($"{T} color.compare textureSame={textureSame} variantSame={variantSame} sizeSame={sizeSame} " +
                                    $"pixelsSame={pixelsSame} shaSame={ws.Sha == ts.Sha} meanRgbSame={meanSame} " +
                                    $"badTexSame={badSame} maskSame={maskSame} materialTexSame={matTexSame} " +
                                    $"materialNameSame={matNameSame} tintApplied={tintApplied} subFive={subFive} " +
                                    $"pathSame={pathSame} subPathsSame={subPathsSame} subTexturesSame={subTexSame} " +
                                    $"subTint={subTint}");

            Check("color-comp", true, $"colorDef={forcedDef.defName}");
            Check("color-white-drawcolor", white, $"drawColor={C(whiteDraw)}");
            Check("color-white-not-clone", !whiteClone, $"graphic={wg.GetType().Name} clone={whiteClone}");
            Check("color-tinted-drawcolor", tinted, $"drawColor={C(tintedDraw)} forced={C(forced)}");
            Check("color-tinted-clone", tintedClone, $"graphic={tg.GetType().Name} clone={tintedClone}");
            Check("color-subgraphics-five", subFive, $"def={dci.Count} white={wci.Count} tinted={tci.Count}");
            Check("color-path", pathSame, $"def={dci.Path} white={wci.Path} tinted={tci.Path}");
            Check("color-subpaths", subPathsSame, $"tinted={string.Join("|", tci.SubPaths)} def={string.Join("|", dci.SubPaths)}");
            Check("color-subtextures", subTexSame, $"tinted={string.Join("|", tci.SubTex)} def={string.Join("|", dci.SubTex)}");
            Check("color-subtint", subTint, $"colors={CArray(tci.SubColors)} forced={C(forced)}");
            Check("color-texture-stable", textureSame, $"white={ws.Tex} tinted={ts.Tex}");
            Check("color-variant-stable", variantSame, $"white={ws.VariantIndex} tinted={ts.VariantIndex} expected={ts.ExpectedIndex}");
            Check("color-size-stable", sizeSame, $"white={ws.Size} tinted={ts.Size}");
            Check("color-pixels-stable", pixelsSame && ws.Sha == ts.Sha, $"whiteSha={ws.Sha} tintedSha={ts.Sha}");
            Check("color-meanrgb-stable", meanSame, $"white=({ws.MeanR:0.##},{ws.MeanG:0.##},{ws.MeanB:0.##}) tinted=({ts.MeanR:0.##},{ts.MeanG:0.##},{ts.MeanB:0.##})");
            Check("color-material-tint", tintApplied, $"white={C(ws.MaterialColor)} tinted={C(ts.MaterialColor)} forced={C(forced)}");
            Check("color-material-tex-stable", matTexSame && matNameSame, $"white={ws.Material}/{ws.MaterialTex} tinted={ts.Material}/{ts.MaterialTex}");
            Check("color-badtex-stable", badSame, $"white={ws.Bad} tinted={ts.Bad}");
            Check("color-mask-stable", maskSame && ts.MaterialMask.EndsWith("_m", StringComparison.Ordinal),
                  $"white={ws.Mask} tinted={ts.Mask} tintedMaskTex={ts.MaterialMask}");
            Check("color-matsingle-at", matAtTex == ts.Tex, $"matAt={matAtTex} expected={ts.Tex}");

            WriteColorJson(probe, naturalColorDef, naturalDraw, forcedDef, forced, ws, ts, wci, tci, dci, seen,
                           matAtTex, textureSame, variantSame, sizeSame, pixelsSame, meanSame, matTexSame,
                           matNameSame, badSame, maskSame, tintApplied, subFive, pathSame, subPathsSame,
                           subTexSame, subTint);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{T} color stack:\n{ex}");
            Check("color-experiment", false, "exception " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static IconSnap CaptureIcon(Thing thing, List<string> variants, List<string> masks, string tag)
    {
        var s = new IconSnap { Tag = tag };
        float scale, angle;
        Vector2 proportions;
        Color color;
        Material mat;
        Texture iconTex = Widgets.GetIconFor(thing, new Vector2(128f, 128f), null, stackOfOne: false,
                                             out scale, out angle, out proportions, out color, out mat);
        Texture2D tex = iconTex as Texture2D;
        if (tex == null && iconTex is RenderTexture rt)
        {
            var tmp = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, mipChain: false);
            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = rt;
            tmp.ReadPixels(new Rect(0f, 0f, rt.width, rt.height), 0, 0);
            tmp.Apply();
            RenderTexture.active = prev;
            tex = tmp;
        }
        s.Tex = tex == null ? "<null>" : tex.name;
        s.Width = tex == null ? 0 : tex.width;
        s.Height = tex == null ? 0 : tex.height;
        s.Bad = tex == null || tex == BaseContent.BadTex;
        s.Placeholder = tex != null && tex.width <= 2 && tex.height <= 2;
        s.Mask = s.Tex.EndsWith("_m", StringComparison.Ordinal) || masks.Contains(s.Tex);
        s.VariantIndex = variants.IndexOf(s.Tex);
        s.ExpectedIndex = (thing.OverrideGraphicIndex ?? thing.thingIDNumber) % Math.Max(1, variants.Count);
        s.Material = mat == null ? "<null>" : mat.name;
        s.MaterialTex = mat == null || mat.mainTexture == null ? "<null>" : mat.mainTexture.name;
        try { s.MaterialColor = mat == null ? Color.white : mat.color; } catch { s.MaterialColor = Color.white; }
        try
        {
            Texture mt = mat == null ? null : mat.GetTexture(ShaderPropertyIDs.MaskTex);
            s.MaterialMask = mt == null ? "<null>" : mt.name;
        }
        catch { s.MaterialMask = "<null>"; }

        if (tex != null)
        {
            try { s.Pixels = tex.GetPixels32(); } catch { s.Pixels = null; }
        }
        if (s.Pixels != null && s.Pixels.Length > 0 && s.Width > 0 && s.Height > 0)
        {
            PixelStats(s.Pixels, s.Width, s.Height, out s.Coverage, out s.MeanR, out s.MeanG, out s.MeanB,
                       out s.MeanA, out s.Distinct, out s.BboxW, out s.BboxH);
            try
            {
                s.Png = Path.Combine(OutDir, $"icon_color_{tag}_{thing.def.defName}_{thing.thingIDNumber}.png");
                WritePng(s.Png, s.Width, s.Height, s.Pixels);
                byte[] bytes = File.ReadAllBytes(s.Png);
                s.PngBytes = bytes.Length;
                using (var sha256 = SHA256.Create())
                    s.Sha = Convert.ToHexString(sha256.ComputeHash(bytes)).ToLowerInvariant();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"{T} color.png[{tag}] failed: {ex.GetType().Name}: {ex.Message}");
                s.Png = "<none>";
            }
        }
        Console.Error.WriteLine($"{T} color.snap[{tag}] tex={s.Tex} size={s.Size} variantIndex={s.VariantIndex} " +
                                $"expectedIndex={s.ExpectedIndex} badTex={s.Bad} placeholder={s.Placeholder} mask={s.Mask} " +
                                $"meanRGB=({s.MeanR:0.##},{s.MeanG:0.##},{s.MeanB:0.##}) coverage={s.Coverage.ToString("P2", CultureInfo.InvariantCulture)} " +
                                $"material={s.Material} materialTex={s.MaterialTex} materialMask={s.MaterialMask} " +
                                $"materialColor={C(s.MaterialColor)} png={s.Png} sha256={s.Sha}");
        return s;
    }

    private static CollInfo InspectGraphic(Graphic g)
    {
        var info = new CollInfo();
        if (g == null) return info;
        info.Type = g.GetType().Name;
        Graphic inner = g;
        if (inner is Graphic_RandomRotated rr) inner = rr.subGraphic;
        Graphic_Collection coll = inner as Graphic_Collection;
        if (coll == null) return info;
        info.Path = coll.path ?? "<null>";
        info.Count = coll.subGraphics == null ? 0 : coll.subGraphics.Length;
        info.SubPaths = new string[Math.Max(0, info.Count)];
        info.SubTex = new string[Math.Max(0, info.Count)];
        info.SubMask = new string[Math.Max(0, info.Count)];
        info.SubColors = new Color[Math.Max(0, info.Count)];
        for (int i = 0; i < info.Count; i++)
        {
            Graphic sg = coll.subGraphics[i];
            info.SubPaths[i] = sg == null ? "<null>" : (sg.path ?? "<null>");
            Material m = null;
            try { m = sg == null ? null : sg.MatSingle; } catch { }
            info.SubTex[i] = m == null || m.mainTexture == null ? "<null>" : m.mainTexture.name;
            info.SubColors[i] = m == null ? Color.white : m.color;
            try
            {
                Texture mt = m == null ? null : m.GetTexture(ShaderPropertyIDs.MaskTex);
                info.SubMask[i] = mt == null ? "<null>" : mt.name;
            }
            catch { info.SubMask[i] = "<null>"; }
        }
        return info;
    }

    private static bool PixelsEqual(Color32[] a, Color32[] b)
    {
        if (a == null || b == null || a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (a[i].r != b[i].r || a[i].g != b[i].g || a[i].b != b[i].b || a[i].a != b[i].a) return false;
        return true;
    }

    private static bool SameStrings(string[] a, string[] b)
    {
        if (a == null || b == null || a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return false;
        return true;
    }

    private static bool Close(Color a, Color b, float eps)
    {
        return Math.Abs(a.r - b.r) <= eps && Math.Abs(a.g - b.g) <= eps && Math.Abs(a.b - b.b) <= eps &&
               Math.Abs(a.a - b.a) <= eps;
    }

    private static string C(Color c)
    {
        return c.r.ToString("0.###", CultureInfo.InvariantCulture) + "," + c.g.ToString("0.###", CultureInfo.InvariantCulture) +
               "," + c.b.ToString("0.###", CultureInfo.InvariantCulture) + "," + c.a.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static string CArray(Color[] colors)
    {
        if (colors == null) return "[]";
        return "[" + string.Join(",", colors.Select(c => "[" + C(c) + "]")) + "]";
    }

    private static string Arr(Color c)
    {
        return "[" + c.r.ToString("0.########", CultureInfo.InvariantCulture) + "," +
               c.g.ToString("0.########", CultureInfo.InvariantCulture) + "," +
               c.b.ToString("0.########", CultureInfo.InvariantCulture) + "," +
               c.a.ToString("0.########", CultureInfo.InvariantCulture) + "]";
    }

    private static string StrArray(string[] values)
    {
        if (values == null) return "[]";
        return "[" + string.Join(",", values.Select(v => "\"" + Esc(v) + "\"")) + "]";
    }

    private static string B(bool v) => v ? "true" : "false";

    private static void WriteColorJson(Thing probe, string naturalColorDef, Color naturalDraw, ColorDef forcedDef,
                                       Color forced, IconSnap ws, IconSnap ts, CollInfo wci, CollInfo tci,
                                       CollInfo dci, HashSet<string> seen, string matAtTex, bool textureSame,
                                       bool variantSame, bool sizeSame, bool pixelsSame, bool meanSame,
                                       bool matTexSame, bool matNameSame, bool badSame, bool maskSame,
                                       bool tintApplied, bool subFive, bool pathSame, bool subPathsSame,
                                       bool subTexSame, bool subTint)
    {
        try
        {
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"def\": \"").Append(Esc(_target.defName)).Append("\",\n");
            sb.Append("  \"thingId\": ").Append(probe.thingIDNumber).Append(",\n");
            sb.Append("  \"naturalColorDef\": \"").Append(Esc(naturalColorDef)).Append("\",\n");
            sb.Append("  \"naturalDrawColor\": ").Append(Arr(naturalDraw)).Append(",\n");
            sb.Append("  \"forcedColorDef\": \"").Append(Esc(forcedDef.defName)).Append("\",\n");
            sb.Append("  \"forcedColor\": ").Append(Arr(forced)).Append(",\n");
            AppendSnap(sb, "white", ws, false, wci.Type);
            sb.Append(",\n");
            AppendSnap(sb, "tinted", ts, true, tci.Type);
            sb.Append(",\n");
            sb.Append("  \"compare\": {\n");
            sb.Append("    \"textureSame\": ").Append(B(textureSame)).Append(",\n");
            sb.Append("    \"variantSame\": ").Append(B(variantSame)).Append(",\n");
            sb.Append("    \"sizeSame\": ").Append(B(sizeSame)).Append(",\n");
            sb.Append("    \"pixelsSame\": ").Append(B(pixelsSame)).Append(",\n");
            sb.Append("    \"sha256Same\": ").Append(B(ws.Sha == ts.Sha)).Append(",\n");
            sb.Append("    \"meanRgbSame\": ").Append(B(meanSame)).Append(",\n");
            sb.Append("    \"badTexSame\": ").Append(B(badSame)).Append(",\n");
            sb.Append("    \"maskSame\": ").Append(B(maskSame)).Append(",\n");
            sb.Append("    \"materialSame\": ").Append(B(matNameSame)).Append(",\n");
            sb.Append("    \"materialTexSame\": ").Append(B(matTexSame)).Append(",\n");
            sb.Append("    \"tintApplied\": ").Append(B(tintApplied)).Append(",\n");
            sb.Append("    \"subgraphicsFive\": ").Append(B(subFive)).Append(",\n");
            sb.Append("    \"pathSame\": ").Append(B(pathSame)).Append(",\n");
            sb.Append("    \"subPathsSame\": ").Append(B(subPathsSame)).Append(",\n");
            sb.Append("    \"subTexturesSame\": ").Append(B(subTexSame)).Append(",\n");
            sb.Append("    \"subColorsTinted\": ").Append(B(subTint)).Append("\n");
            sb.Append("  },\n");
            sb.Append("  \"structure\": {\n");
            sb.Append("    \"defGraphic\": \"").Append(Esc(dci.Type)).Append("\",\n");
            sb.Append("    \"defPath\": \"").Append(Esc(dci.Path)).Append("\",\n");
            sb.Append("    \"defSubCount\": ").Append(dci.Count).Append(",\n");
            sb.Append("    \"whiteSubCount\": ").Append(wci.Count).Append(",\n");
            sb.Append("    \"tintedSubCount\": ").Append(tci.Count).Append(",\n");
            sb.Append("    \"tintedPath\": \"").Append(Esc(tci.Path)).Append("\",\n");
            sb.Append("    \"tintedSubPaths\": ").Append(StrArray(tci.SubPaths)).Append(",\n");
            sb.Append("    \"tintedSubTextures\": ").Append(StrArray(tci.SubTex)).Append(",\n");
            sb.Append("    \"tintedSubMasks\": ").Append(StrArray(tci.SubMask)).Append(",\n");
            sb.Append("    \"tintedSubColors\": ").Append(CArray(tci.SubColors)).Append("\n");
            sb.Append("  },\n");
            sb.Append("  \"matSingle\": {\n");
            sb.Append("    \"root\": \"").Append(Esc(tci.Type)).Append("\",\n");
            sb.Append("    \"reads\": 12,\n");
            sb.Append("    \"distinct\": ").Append(seen.Count).Append(",\n");
            sb.Append("    \"names\": ").Append(StrArray(seen.OrderBy(n => n, StringComparer.Ordinal).ToArray())).Append(",\n");
            sb.Append("    \"matAtTexture\": \"").Append(Esc(matAtTex)).Append("\",\n");
            sb.Append("    \"expectedTexture\": \"").Append(Esc(ts.Tex)).Append("\"\n");
            sb.Append("  },\n");
            sb.Append("  \"failed\": ").Append(StrArray(Failed.ToArray())).Append("\n");
            sb.Append("}\n");
            string path = Path.Combine(OutDir, "color.json");
            File.WriteAllText(path, sb.ToString());
            Console.Error.WriteLine($"{T} color.json={path}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{T} color.json write failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void AppendSnap(StringBuilder sb, string name, IconSnap s, bool clone, string graphicType)
    {
        sb.Append("  \"").Append(name).Append("\": {\n");
        sb.Append("    \"texture\": \"").Append(Esc(s.Tex)).Append("\",\n");
        sb.Append("    \"size\": \"").Append(Esc(s.Size)).Append("\",\n");
        sb.Append("    \"width\": ").Append(s.Width).Append(",\n");
        sb.Append("    \"height\": ").Append(s.Height).Append(",\n");
        sb.Append("    \"variantIndex\": ").Append(s.VariantIndex).Append(",\n");
        sb.Append("    \"expectedIndex\": ").Append(s.ExpectedIndex).Append(",\n");
        sb.Append("    \"badTex\": ").Append(B(s.Bad)).Append(",\n");
        sb.Append("    \"placeholder\": ").Append(B(s.Placeholder)).Append(",\n");
        sb.Append("    \"mask\": ").Append(B(s.Mask)).Append(",\n");
        sb.Append("    \"meanRgb\": [")
          .Append(s.MeanR.ToString("0.00", CultureInfo.InvariantCulture)).Append(",")
          .Append(s.MeanG.ToString("0.00", CultureInfo.InvariantCulture)).Append(",")
          .Append(s.MeanB.ToString("0.00", CultureInfo.InvariantCulture)).Append("],\n");
        sb.Append("    \"meanAlpha\": ").Append(s.MeanA.ToString("0.00", CultureInfo.InvariantCulture)).Append(",\n");
        sb.Append("    \"coverage\": ").Append(s.Coverage.ToString("0.000000", CultureInfo.InvariantCulture)).Append(",\n");
        sb.Append("    \"distinctColors\": ").Append(s.Distinct).Append(",\n");
        sb.Append("    \"bbox\": [").Append(s.BboxW).Append(",").Append(s.BboxH).Append("],\n");
        sb.Append("    \"material\": \"").Append(Esc(s.Material)).Append("\",\n");
        sb.Append("    \"materialTex\": \"").Append(Esc(s.MaterialTex)).Append("\",\n");
        sb.Append("    \"materialMask\": \"").Append(Esc(s.MaterialMask)).Append("\",\n");
        sb.Append("    \"materialColor\": ").Append(Arr(s.MaterialColor)).Append(",\n");
        sb.Append("    \"png\": \"").Append(Esc(s.Png)).Append("\",\n");
        sb.Append("    \"pngBytes\": ").Append(s.PngBytes).Append(",\n");
        sb.Append("    \"sha256\": \"").Append(Esc(s.Sha)).Append("\",\n");
        sb.Append("    \"clone\": ").Append(B(clone)).Append(",\n");
        sb.Append("    \"graphicType\": \"").Append(Esc(graphicType)).Append("\"\n");
        sb.Append("  }");
    }

    private static void WriteJson(Thing thing, string name, Texture2D tex, Material mat, List<string> variants,
                                  List<string> masks, int variantIndex, int pick, double coverage, double mr,
                                  double mg, double mb, double ma, int distinct, int bboxW, int bboxH,
                                  string pngPath, long pngBytes, string sha, string stats)
    {
        try
        {
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"def\": \"").Append(_target.defName).Append("\",\n");
            sb.Append("  \"thingId\": ").Append(thing.thingIDNumber).Append(",\n");
            sb.Append("  \"texture\": \"").Append(Esc(name)).Append("\",\n");
            sb.Append("  \"size\": \"").Append(tex == null ? "" : tex.width + "x" + tex.height).Append("\",\n");
            sb.Append("  \"badTex\": ").Append(tex == null || tex == BaseContent.BadTex ? "true" : "false").Append(",\n");
            sb.Append("  \"placeholder\": ").Append(tex != null && tex.width <= 2 && tex.height <= 2 ? "true" : "false").Append(",\n");
            sb.Append("  \"mask\": ").Append(name.EndsWith("_m", StringComparison.Ordinal) || masks.Contains(name) ? "true" : "false").Append(",\n");
            sb.Append("  \"variantIndex\": ").Append(variantIndex).Append(",\n");
            sb.Append("  \"expectedIndex\": ").Append(pick).Append(",\n");
            sb.Append("  \"variants\": [").Append(string.Join(",", variants.Select(v => "\"" + Esc(v) + "\""))).Append("],\n");
            sb.Append("  \"masks\": [").Append(string.Join(",", masks.Select(v => "\"" + Esc(v) + "\""))).Append("],\n");
            sb.Append("  \"material\": \"").Append(Esc(mat == null ? "" : mat.name)).Append("\",\n");
            sb.Append("  \"coverage\": ").Append(coverage.ToString("0.000000", CultureInfo.InvariantCulture)).Append(",\n");
            sb.Append("  \"meanRgb\": [")
              .Append(mr.ToString("0.00", CultureInfo.InvariantCulture)).Append(",")
              .Append(mg.ToString("0.00", CultureInfo.InvariantCulture)).Append(",")
              .Append(mb.ToString("0.00", CultureInfo.InvariantCulture)).Append("],\n");
            sb.Append("  \"meanAlpha\": ").Append(ma.ToString("0.00", CultureInfo.InvariantCulture)).Append(",\n");
            sb.Append("  \"distinctColors\": ").Append(distinct).Append(",\n");
            sb.Append("  \"bbox\": [").Append(bboxW).Append(",").Append(bboxH).Append("],\n");
            sb.Append("  \"png\": \"").Append(Esc(pngPath == null ? "" : pngPath)).Append("\",\n");
            sb.Append("  \"pngBytes\": ").Append(pngBytes).Append(",\n");
            sb.Append("  \"sha256\": \"").Append(sha == null ? "" : sha).Append("\",\n");
            sb.Append("  \"stats\": \"").Append(Esc(stats)).Append("\",\n");
            sb.Append("  \"failed\": [").Append(string.Join(",", Failed.Select(v => "\"" + Esc(v) + "\""))).Append("]\n");
            sb.Append("}\n");
            string path = Path.Combine(OutDir, "icon.json");
            File.WriteAllText(path, sb.ToString());
            Console.Error.WriteLine($"{T} json={path}");
        }
        catch (Exception ex) { Console.Error.WriteLine($"{T} json write failed: {ex.GetType().Name}: {ex.Message}"); }
    }

    private static string Esc(string s)
    {
        return (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    // ---- PNG (RGBA8, filter 0) ----------------------------------------------------------------
    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xedb88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    private static uint Crc32(byte[] data, int offset, int count)
    {
        uint c = 0xffffffffu;
        for (int i = offset; i < offset + count; i++) c = CrcTable[(c ^ data[i]) & 0xff] ^ (c >> 8);
        return c ^ 0xffffffffu;
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4];
        WriteBe(len, 0, data.Length);
        s.Write(len, 0, 4);
        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes, 0, 4);
        s.Write(data, 0, data.Length);
        var crcInput = new byte[4 + data.Length];
        Buffer.BlockCopy(typeBytes, 0, crcInput, 0, 4);
        Buffer.BlockCopy(data, 0, crcInput, 4, data.Length);
        var crc = new byte[4];
        WriteBe(crc, 0, unchecked((int)Crc32(crcInput, 0, crcInput.Length)));
        s.Write(crc, 0, 4);
    }

    private static void WriteBe(byte[] b, int o, int v)
    {
        b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v;
    }

    /// <summary>Writes Texture2D.GetPixels32() (bottom-up, Unity convention) as a top-down RGBA8 PNG.</summary>
    private static void WritePng(string path, int w, int h, Color32[] pixels)
    {
        var raw = new byte[h * (1 + w * 4)];
        int o = 0;
        for (int y = 0; y < h; y++)
        {
            raw[o++] = 0; // filter type: none
            int src = (h - 1 - y) * w; // GetPixels32 is bottom-up, PNG is top-down
            for (int x = 0; x < w; x++)
            {
                Color32 c = pixels[src + x];
                raw[o++] = c.r; raw[o++] = c.g; raw[o++] = c.b; raw[o++] = c.a;
            }
        }

        using var ms = new MemoryStream();
        ms.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, 0, 8);

        var ihdr = new byte[13];
        WriteBe(ihdr, 0, w);
        WriteBe(ihdr, 4, h);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 6;  // colour type RGBA
        WriteChunk(ms, "IHDR", ihdr);

        byte[] compressed;
        using (var comp = new MemoryStream())
        {
            using (var z = new ZLibStream(comp, CompressionLevel.Optimal, leaveOpen: true))
                z.Write(raw, 0, raw.Length);
            compressed = comp.ToArray();
        }
        WriteChunk(ms, "IDAT", compressed);
        WriteChunk(ms, "IEND", Array.Empty<byte>());

        string dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllBytes(path, ms.ToArray());
    }
}
