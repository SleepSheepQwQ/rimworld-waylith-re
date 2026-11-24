using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using RimWorld;
using UnityEngine;
using Verse;

/// <summary>
/// Runs inside the real game process (DOTNET_STARTUP_HOOKS) after PlayDataLoader reports boot
/// completion, and asks the VFS the same questions Graphic_* asks:
///
///   ContentFinder&lt;Texture2D&gt;.GetAllInFolder(folder)   -> what names does the pack produce?
///   ContentFinder&lt;Texture2D&gt;.Get(key)                  -> does the exact key the graphic wants resolve?
///   GraphicDatabase.Get&lt;Graphic_Multi&gt;(...) .MatAt(rot) -> what texture does a real animal draw with?
///
/// The packs under packprobe/pack are copied verbatim out of the device install, so the entries keep
/// their shipped names (Core uses "X_east.png.dds.split", Odyssey uses "X.png.dds"). Nothing is
/// re-encoded, so whatever this prints is what the device's own install would produce.
/// </summary>
internal static class StartupHook
{
    private const string T = "[VFS]";

    public static void Initialize()
    {
        new Thread(WaitForBoot) { IsBackground = true, Name = "packprobe-waiter" }.Start();
        new Thread(Watchdog) { IsBackground = true, Name = "packprobe-watchdog" }.Start();
    }

    private static void Watchdog()
    {
        Thread.Sleep(int.TryParse(Environment.GetEnvironmentVariable("PROBE_TIMEOUT_MS"), out int ms) ? ms : 180000);
        Console.WriteLine($"{T} watchdog: boot never completed, exiting");
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
            bool startup = InvokeGlyphFix();
            Console.WriteLine($"{T} glyphfixStartup={startup}");

            ProbeUniqueDef();
            Probe("Things/Pawn/Animal/Alpaca", "Alpaca");
            Probe("Things/Pawn/Animal/Ibex", "IbexMale");
            Probe("Things/Pawn/Animal/Lynx", "Lynx");
            Probe("Things/Pawn/Animal/Cow", "Cow");
            Probe("Things/Item/Equipment/WeaponRanged/UniqueWeapons/Revolver", "UniqueRevolverA");
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

    private static bool InvokeGlyphFix()
    {
        try
        {
            Type startup = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
                .FirstOrDefault(t => t.FullName == "AgentTouchFix.Bootstrap");
            if (startup == null)
            {
                Console.WriteLine($"{T} startup: GlyphFix not installed");
                return false;
            }
            System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(startup.TypeHandle);
            Console.WriteLine($"{T} startup: GlyphFix Bootstrap invoked");
            return true;
        }
        catch (Exception e)
        {
            Console.WriteLine($"{T} startup fatal {e.GetType().Name}: {e.Message}");
            return false;
        }
    }

    private static void ProbeUniqueDef()
    {
        const string defName = "Gun_Revolver_Unique";
        ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(defName);
        if (def == null)
        {
            Console.WriteLine($"{T} unique present=false def={defName}");
            return;
        }

        try { Widgets.ThingIcon(new Rect(0f, 0f, 128f, 128f), def); }
        catch (Exception e) { Console.WriteLine($"{T} unique thingIcon EXC {e.GetType().Name}: {e.Message}"); }

        Graphic outer = def.graphic;
        Graphic inner = outer;
        for (int depth = 0; inner != null && depth < 8; depth++)
        {
            if (inner is Graphic_Collection) break;
            System.Reflection.FieldInfo child = inner.GetType().GetField("subGraphic",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance);
            if (child == null || !(child.GetValue(inner) is Graphic next)) break;
            inner = next;
        }

        Graphic_Collection collection = inner as Graphic_Collection;
        Graphic[] subs = collection?.subGraphics ?? Array.Empty<Graphic>();
        int masks = subs.Count(g => IsMask(Leaf(g?.path)));
        Console.WriteLine($"{T} unique def={defName} outer={outer?.GetType().Name ?? "<null>"}"
                          + $" inner={inner?.GetType().Name ?? "<null>"} subCount={subs.Length} masks={masks}");

        Texture2D ui = def.uiIcon;
        bool bad = ui == null || (BaseContent.BadTex != null && ReferenceEquals(ui, BaseContent.BadTex));
        bool mask = ui != null && IsMask(Leaf(ui.name));
        int pixels = Pixels(ui);
        string verdict = !bad && ui != null && ui.width > 2 && !mask && pixels > 0 ? "DRAW" : "BAD";
        Console.WriteLine($"{T} unique uiIcon='{(ui == null ? "<null>" : ui.name)}'"
                          + $" bad={bad} mask={mask} size={(ui == null ? "-" : ui.width + "x" + ui.height)}"
                          + $" pixels={pixels} verdict={verdict}");
    }

    private static int Pixels(Texture2D texture)
    {
        if (texture == null) return -1;
        try { return texture.GetPixels32()?.Length ?? -1; }
        catch { return -1; }
    }

    private static void Probe(string folder, string leaf)
    {
        Console.WriteLine($"{T} === folder={folder} leaf={leaf} ===");

        List<Texture2D> all;
        try
        {
            all = ContentFinder<Texture2D>.GetAllInFolder(folder).ToList();
        }
        catch (Exception e)
        {
            Console.WriteLine($"{T} folder-enum fatal {e.GetType().Name}: {e.Message}");
            all = new List<Texture2D>();
        }

        Console.WriteLine($"{T} folder count={all.Count}");
        foreach (Texture2D t in all.Take(24))
        {
            Console.WriteLine($"{T}   raw name='{t.name}' {t.width}x{t.height}"
                              + $" fmt={t.format} mask={IsMask(Leaf(t.name))} pixels={Pixels(t)}");
        }

        foreach (string suffix in new[] { "_south", "_north", "_east", "_west" })
        {
            string key = folder + "/" + leaf + suffix;
            Texture2D tex = null;
            try { tex = ContentFinder<Texture2D>.Get(key, false); } catch { }
            Console.WriteLine($"{T} get key='{key}' hit={tex != null} name='{(tex != null ? tex.name : "-")}' size={(tex != null ? tex.width + "x" + tex.height : "-")}");
        }

        try
        {
            Graphic g = GraphicDatabase.Get<Graphic_Multi>(folder + "/" + leaf, ShaderDatabase.Cutout, Vector2.one, Color.white);
            Console.WriteLine($"{T} graphic type={g.GetType().Name} path='{g.path}'");
            foreach (Rot4 r in new[] { Rot4.South, Rot4.North, Rot4.East, Rot4.West })
            {
                Material m = null;
                string texName = "<none>";
                string size = "-";
                try
                {
                    m = g.MatAt(r, null);
                    Texture tex = m != null ? m.mainTexture : null;
                    texName = tex != null ? tex.name : "<null>";
                    if (tex != null) size = tex.width + "x" + tex.height;
                }
                catch (Exception e)
                {
                    texName = "<<" + e.GetType().Name + ": " + e.Message + ">>";
                }
                Console.WriteLine($"{T} mat rot={r} tex='{texName}' size={size}");
            }
        }
        catch (Exception e)
        {
            Console.WriteLine($"{T} graphic fatal {e.GetType().Name}: {e.Message}");
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
            foreach (string ext in new[] { ".png", ".jpg", ".jpeg", ".psd", ".dds" })
            {
                if (s.Length > ext.Length && s.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                {
                    s = s.Substring(0, s.Length - ext.Length);
                    peeled = true;
                    break;
                }
            }
        }
        return s.EndsWith("_m", StringComparison.Ordinal);
    }

    private static string Leaf(string path)
    {
        if (string.IsNullOrEmpty(path)) return "<null>";
        int slash = path.LastIndexOfAny(new[] { '/', '\\' });
        return slash < 0 ? path : path.Substring(slash + 1);
    }
}
