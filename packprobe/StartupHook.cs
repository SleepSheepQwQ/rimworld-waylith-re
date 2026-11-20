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
            Console.WriteLine($"{T}   raw name='{t.name}' {t.width}x{t.height}");
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
}
