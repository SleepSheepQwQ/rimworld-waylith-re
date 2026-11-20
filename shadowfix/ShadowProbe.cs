using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using RimWorld;
using UnityEngine;
using Verse;

/// <summary>
/// Startup hook for .github/workflows/shadowfix.yml. Runs inside the real game process after boot and
/// reports what the engine itself answers - never what the mod claims:
///
///   [SHADOW] fresh variants=10 masks=5                     Graphic_Collection's variant list
///   [SHADOW] multi path=... resolved=0/4                   Graphic_Multi's four rotations
///   [SHADOW] case key=... hit=True name=IbexMALE_east      the ordinal/case lookup
///   [SHADOW] tex leaf=Shadow hit=True size=128x128 ...     ContentFinder + GetPixels32 statistics
///
/// Everything is measured through ContentFinder / GraphicDatabase / GetPixels32, so a no-op patch
/// shows up as the control's numbers and fails the gate. The pixel hash is over the whole mip 0
/// Color32 array: "the png won" is asserted as byte-identical pixels against PngOnly, not as a
/// plausible-looking mean.
/// </summary>
internal static class StartupHook
{
    private const string T = "[SHADOW]";
    private static readonly string[] Extensions = { ".png", ".jpg", ".jpeg", ".psd", ".dds" };

    public static void Initialize()
    {
        new Thread(WaitForBoot) { IsBackground = true, Name = "shadow-waiter" }.Start();
        new Thread(Watchdog) { IsBackground = true, Name = "shadow-watchdog" }.Start();
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
        try
        {
            // The headless harness never runs StaticConstructorOnStartup, so emulate what the game does
            // before measuring anything. ShadowFix itself must already be installed by then: its
            // module initializer runs when the assembly is loaded, i.e. before any mod content.
            Type startup = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => a.GetName().Name == "GlyphFix")
                .SelectMany(a => { try { return a.GetTypes(); } catch (Exception) { return Array.Empty<Type>(); } })
                .FirstOrDefault(t => t.FullName == "AgentTouchFix.Bootstrap");
            if (startup != null)
            {
                System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(startup.TypeHandle);
                Console.WriteLine($"{T} startup ctor invoked (harness emulation)");
            }
            else Console.WriteLine($"{T} startup ctor: mod not installed");

            Console.WriteLine($"{T} activeMods={ModsConfig.ActiveModsInLoadOrder.Count()}");
            var asms = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => a.GetName().Name == "GlyphFix").Select(a => a.GetName().Name).ToList();
            Console.WriteLine($"{T} loadedAssemblies={string.Join(",", asms)} count={asms.Count}");

            ReportCollection();
            ReportMulti();
            ReportCase();

            foreach (string leaf in new[] { "Shadow", "PngOnly", "Kept", "Dxt3" })
                ReportTexture("Things/ShadowProbe/" + leaf, leaf);
        }
        catch (Exception e) { Console.WriteLine($"{T} fatal {e.GetType().Name}: {e.Message}"); }
        finally
        {
            Console.Out.Flush();
            try { Root.Shutdown(); } catch { }
            Environment.Exit(0);
        }
    }

    /// <summary>The leak mod's revolver: 10 textures, 5 of them masks, must collapse to 5/0.</summary>
    private static void ReportCollection()
    {
        string defName = Environment.GetEnvironmentVariable("SHADOW_DEF") ?? "Gun_Revolver_LeakExt";
        ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(defName);
        if (def == null) { Console.WriteLine($"{T} def={defName} missing"); return; }

        Graphic fresh = GraphicDatabase.Get<Graphic_Random>(def.graphicData.texPath, ShaderDatabase.Cutout,
                                                           Vector2.one, new Color(0.99f, 1f, 1f));
        FieldInfo ff = fresh.GetType().GetField("subGraphics", BindingFlags.Public | BindingFlags.Instance);
        if (ff == null) { Console.WriteLine($"{T} def={defName} no subGraphics"); return; }

        var subs = (Graphic[])ff.GetValue(fresh);
        int masks = 0;
        foreach (Graphic g in subs)
        {
            string n = g == null || string.IsNullOrEmpty(g.path) ? "<null>" : g.path;
            int slash = n.LastIndexOf('/');
            if (slash >= 0) n = n.Substring(slash + 1);
            if (IsMask(n)) masks++;
        }
        Console.WriteLine($"{T} fresh variants={subs.Length} masks={masks}");
    }

    /// <summary>Graphic_Multi resolves "&lt;path&gt;_north|_east|_south|_west"; a leaked key misses all four.</summary>
    private static void ReportMulti()
    {
        string multiPath = Environment.GetEnvironmentVariable("SHADOW_MULTI")
                           ?? "Things/Pawn/Animal/IbexShadow/IbexShadow";
        Graphic mg = GraphicDatabase.Get<Graphic_Multi>(multiPath, ShaderDatabase.Cutout, Vector2.one, Color.white);
        int ok = 0;
        foreach (Rot4 r in new[] { Rot4.South, Rot4.North, Rot4.East, Rot4.West })
        {
            try
            {
                Material m = mg.MatAt(r, null);
                Texture tex = m != null ? m.mainTexture : null;
                if (tex != null) ok++;
            }
            catch (Exception) { }
        }
        Console.WriteLine($"{T} multi path={multiPath} resolved={ok}/4");
    }

    private static void ReportCase()
    {
        foreach (string key in new[]
        {
            "Things/Pawn/Animal/Ibex/IbexMale_east",
            "Things/Pawn/Animal/Ibex/IbexMALE_east",
        })
        {
            try
            {
                Texture2D t = ContentFinder<Texture2D>.Get(key, false);
                Console.WriteLine($"{T} case key={key} hit={(t != null)} name={(t != null ? t.name : "-")}");
            }
            catch (Exception e) { Console.WriteLine($"{T} case key={key} EXC {e.GetType().Name}"); }
        }
    }

    /// <summary>
    /// One lookup with everything the gate needs: did it load, how big, which format, the pixel
    /// statistics (mean channels, mean alpha, fully transparent texels, luminance spread) and a hash of
    /// the whole mip-0 array so two textures can be compared for exact equality.
    /// </summary>
    private static void ReportTexture(string key, string leaf)
    {
        try
        {
            Texture2D t = ContentFinder<Texture2D>.Get(key, false);
            Console.WriteLine($"{T} tex leaf={leaf} hit={(t != null)}"
                              + $" name={(t != null ? t.name : "-")}"
                              + $" size={(t != null ? t.width + "x" + t.height : "-")}"
                              + $" fmt={(t != null ? t.format.ToString() : "-")}"
                              + (t != null ? " " + Stats(t) : ""));
        }
        catch (Exception e)
        {
            Console.WriteLine($"{T} tex leaf={leaf} EXC {e.GetType().Name}: {e.Message}");
        }
    }

    private static string Stats(Texture2D t)
    {
        if (t.width <= 1 || t.height <= 1) return "px=0";
        try
        {
            Color32[] px = t.GetPixels32();
            double r = 0, g = 0, b = 0, a = 0, l = 0, l2 = 0;
            int zero = 0;
            ulong hash = 1469598103934665603UL;
            foreach (Color32 c in px)
            {
                r += c.r; g += c.g; b += c.b; a += c.a;
                double lum = 0.299 * c.r + 0.587 * c.g + 0.114 * c.b;
                l += lum; l2 += lum * lum;
                if (c.a == 0) zero++;
                hash = (hash ^ c.r) * 1099511628211UL;
                hash = (hash ^ c.g) * 1099511628211UL;
                hash = (hash ^ c.b) * 1099511628211UL;
                hash = (hash ^ c.a) * 1099511628211UL;
            }
            int n = px.Length;
            double mean = l / n;
            double sd = Math.Sqrt(Math.Max(0, l2 / n - mean * mean));
            int mips = 1;
            try { mips = t.mipmapCount; } catch { }
            return $"rgb={r / n:F1},{g / n:F1},{b / n:F1} alpha={a / n:F1}"
                   + $" zero={zero}/{n} sd={sd:F2} px={hash:x16} mips={mips}";
        }
        catch (Exception e)
        {
            return "px!" + e.GetType().Name;
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
