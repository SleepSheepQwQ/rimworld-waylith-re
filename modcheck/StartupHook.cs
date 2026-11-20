using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using RimWorld;
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

            Console.WriteLine($"{T} def={defName} collection={collection.GetType().Name} variants={(subs == null ? 0 : subs.Length)} masks={masks}");
            Console.WriteLine($"{T} names={string.Join(",", names)}");
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
