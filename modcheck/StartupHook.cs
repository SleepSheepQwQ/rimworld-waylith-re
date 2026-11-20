using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using RimWorld;
using UnityEngine;
using Verse;

/// <summary>
/// Runs inside the real game process after boot and reports what a Graphic_Collection actually built:
///
///   [MODCHECK] def=... type=Graphic_Random variants=10 masks=5 names=A.png,A_m.png,...
///
/// "masks" counts entries whose name ends with "_m" once every texture extension is peeled, i.e. the
/// same rule GlyphFix's CollectionVariants uses. The workflow runs this twice - once without the mod
/// and once with the shipped GlyphFix.zip installed - so the fix is judged by the engine's own
/// variant list, not by the mod's opinion of it.
/// </summary>
internal static class StartupHook
{
    private const string T = "[MODCHECK]";
    private static readonly string[] Extensions = { ".png", ".jpg", ".jpeg", ".psd", ".dds" };

    public static void Initialize()
    {
        new Thread(WaitForBoot) { IsBackground = true, Name = "modcheck-waiter" }.Start();
        new Thread(Watchdog) { IsBackground = true, Name = "modcheck-watchdog" }.Start();
    }

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
        string defName = Environment.GetEnvironmentVariable("MODCHECK_DEF") ?? "Gun_Revolver_LeakExt";
        try
        {
            // The headless harness never runs StaticConstructorOnStartup (the phone does), so do what
            // RimWorld's StaticConstructorOnStartupUtility does before measuring anything.
            Type startup = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => a.GetName().Name == "GlyphFix")
                .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
                .FirstOrDefault(t => t.FullName == "AgentTouchFix.Bootstrap");
            if (startup != null)
            {
                System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(startup.TypeHandle);
                Console.WriteLine($"{T} startup ctor invoked (harness emulation)");
            }
            else Console.WriteLine($"{T} startup ctor: mod not installed");

            Console.WriteLine($"{T} activeMods={ModsConfig.ActiveModsInLoadOrder.Count()}");
            foreach (ModMetaData m in ModsConfig.ActiveModsInLoadOrder)
            {
                Console.WriteLine($"{T} active={m.PackageIdPlayerFacing}|{m.PackageId}|root={m.RootDir}");
            }
            var asms = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => a.GetName().Name != null && a.GetName().Name.IndexOf("GlyphFix", StringComparison.OrdinalIgnoreCase) >= 0)
                .Select(a => a.GetName().Name).ToList();
            Console.WriteLine($"{T} loadedAssemblies={string.Join(",", asms)} count={asms.Count}");

            // Did the mod's types survive loading, and is the startup-attribute type among them?
            try
            {
                System.Reflection.Assembly gf = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == "GlyphFix");
                if (gf != null)
                {
                    Type[] types;
                    try { types = gf.GetTypes(); }
                    catch (System.Reflection.ReflectionTypeLoadException rtle)
                    {
                        types = rtle.Types.Where(t => t != null).ToArray();
                        Console.WriteLine($"{T} typeLoadErrors={rtle.LoaderExceptions.Length}");
                        foreach (Exception le in rtle.LoaderExceptions.Take(3))
                            Console.WriteLine($"{T}   loader={le.GetType().Name}: {le.Message}");
                    }
                    Console.WriteLine($"{T} glyphfixTypes={types.Length} hasBootstrap={types.Any(t => t.FullName == "AgentTouchFix.Bootstrap")}");
                    var attr = AppDomain.CurrentDomain.GetAssemblies()
                        .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
                        .FirstOrDefault(t => t.FullName == "Verse.StaticConstructorOnStartup");
                    Console.WriteLine($"{T} startupAttribute={(attr == null ? "missing" : attr.Assembly.GetName().Name)}");
                }
            }
            catch (Exception e) { Console.WriteLine($"{T} typeProbeFatal {e.GetType().Name}: {e.Message}"); }

            ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(defName);
            if (def == null)
            {
                Console.WriteLine($"{T} def={defName} missing");
                return;
            }

            Graphic outer = def.graphicData.Graphic;
            Console.WriteLine($"{T} def={defName} outer={outer.GetType().Name} path='{outer.path}'");

            Graphic collection = outer;
            FieldInfo inner = outer.GetType().GetField("subGraphic", BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
            if (inner != null && inner.GetValue(outer) is Graphic g2) collection = g2;

            FieldInfo field = collection.GetType().GetField("subGraphics", BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
            if (field == null)
            {
                Console.WriteLine($"{T} def={defName} no subGraphics on {collection.GetType().Name}");
                return;
            }

            var subs = (Graphic[])field.GetValue(collection);
            var names = new List<string>();
            int masks = 0;
            if (subs != null)
            {
                foreach (Graphic g in subs)
                {
                    string n = g != null && !string.IsNullOrEmpty(g.path) ? g.path.Replace('\\', '/') : "<null>";
                    int slash = n.LastIndexOf('/');
                    if (slash >= 0) n = n.Substring(slash + 1);
                    names.Add(n);
                    if (IsMask(n)) masks++;
                }
            }

            Console.WriteLine($"{T} def={defName} cachedCollection={collection.GetType().Name} variants={(subs == null ? 0 : subs.Length)} masks={masks}");
            Console.WriteLine($"{T} cachedNames={string.Join(",", names)}");

            // The def's graphic was built during content load, i.e. before the startup ctor ran. Measure
            // a fresh one for the same folder: a different colour is a different GraphicRequest, so
            // GraphicDatabase runs Init again - this time with the patch installed.
            try
            {
                string texPath = def.graphicData.texPath;
                Graphic fresh = GraphicDatabase.Get<Graphic_Random>(texPath, ShaderDatabase.Cutout,
                    Vector2.one, new Color(0.99f, 1f, 1f));
                FieldInfo ff = fresh.GetType().GetField("subGraphics", BindingFlags.Public | BindingFlags.Instance);
                var fs = (Graphic[])ff.GetValue(fresh);
                var fn = fs.Select(g => (g == null || string.IsNullOrEmpty(g.path)) ? "<null>" : g.path.Substring(g.path.LastIndexOf('/') + 1)).ToList();
                int fm = fn.Count(IsMask);
                Console.WriteLine($"{T} def={defName} fresh variants={fs.Length} masks={fm}");
                Console.WriteLine($"{T} freshNames={string.Join(",", fn)}");
            }
            catch (Exception e) { Console.WriteLine($"{T} freshFatal {e.GetType().Name}: {e.Message}"); }

            // Animal half: Graphic_Multi resolves per-direction keys ("<path>_east"), so a leak makes
            // the main texture miss outright (no mask involved). Count how many rotations resolve.
            // No def needed: Graphic_Multi is built from a path, and the leak mod only ships textures.
            string multiPath = Environment.GetEnvironmentVariable("MODCHECK_MULTI_PATH")
                               ?? "Things/Pawn/Animal/IbexLeak/IbexLeak";
            int ok = 0;
            Graphic mg = GraphicDatabase.Get<Graphic_Multi>(multiPath, ShaderDatabase.Cutout, Vector2.one, Color.white);
            foreach (Rot4 r in new[] { Rot4.South, Rot4.North, Rot4.East, Rot4.West })
            {
                string n = "<null>";
                try
                {
                    Material m = mg.MatAt(r, null);
                    Texture tex = m != null ? m.mainTexture : null;
                    if (tex != null) { n = tex.name; ok++; }
                }
                catch (Exception e) { n = "!" + e.GetType().Name; }
                Console.WriteLine($"{T} multi rot={r} tex={n}");
            }
            Console.WriteLine($"{T} multi path={multiPath} resolved={ok}/4");
        }
            // DDS format support, measured through the real content path: loose mod files go through
            // ModDdsLoader, whose ToTextureFormat knows DXT1/DXT5/BC7 only (anything else throws, and
            // a DX10 header is blindly mapped to BC7 whatever DXGI format it carries).
            if (Environment.GetEnvironmentVariable("MODCHECK_DDS") == "1")
            {
                foreach (string leaf in new[] { "Bc7", "Bc5", "Dxt3" })
                {
                    string key = "Things/DdsProbe/" + leaf;
                    try
                    {
                        Texture2D t = ContentFinder<Texture2D>.Get(key, false);
                        Console.WriteLine($"{T} dds leaf={leaf} hit={(t != null)} name='{(t != null ? t.name : "-")}' size={(t != null ? t.width + "x" + t.height : "-")}");
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine($"{T} dds leaf={leaf} EXC {e.GetType().Name}: {e.Message}");
                    }
                }
            }
        catch (Exception e)
        {
            Console.WriteLine($"{T} fatal {e.GetType().Name}: {e.Message}");
        }
        finally
        {
            Console.Out.Flush();
            try { Root.Shutdown(); } catch { }
            Environment.Exit(0);
        }
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
}
