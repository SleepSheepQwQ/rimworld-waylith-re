using System;
using System.Collections;
using System.Reflection;
using System.Text;
using System.Threading;

namespace AgentTouchFix
{
    /// <summary>
    /// v19: catch the flicker in the act.
    ///
    /// Samples a fixed set of defs every 500 ms for 10 s (starting 30 s after bootstrap) and prints,
    /// per sample, only the values that can make a build-menu tile blink:
    ///   - the icon material's shader name (CutoutComplexUI for the flickering defs, empty for the fine ones)
    ///   - the material's mainTexture (stub or null)
    ///   - the real texture's (uiIcon.CoreTexture) GpuResource: null means "not resident right now"
    ///   - uiIcon's own GpuResource
    /// A value that flips between samples is the mechanism; a value that never flips means the flicker
    /// comes from the draw path, not from residency.
    /// </summary>
    internal static class FlickerProbe
    {
        private static int _started, _warned;
        private static readonly string[] Names = { "RoyalBed", "NutrientPasteDispenser", "Wall", "Table2x2c", "HospitalBed" };

        internal static void Install()
        {
            if (Interlocked.Exchange(ref _started, 1) == 1) return;
            try
            {
                Thread t = new Thread(Loop) { IsBackground = true, Name = "AgentTouchFixFlickerProbe" };
                t.Start();
                Refl.Info("flicker: scheduled (t+30 s, 20 samples @ 500 ms, read-only)");
            }
            catch (Exception e) { Refl.Warn("flicker: start failed: " + e.Message); }
        }

        private static void Loop()
        {
            try { Thread.Sleep(30000); } catch { }
            for (int i = 1; i <= 20; i++)
            {
                try { Sample(i); } catch (Exception e) { Warn(e.Message); }
                try { Thread.Sleep(500); } catch { }
            }
        }

        private static object Prop(object o, string name)
        {
            if (o == null) return null;
            try
            {
                foreach (PropertyInfo p in o.GetType().GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    if (p.Name == name && p.GetIndexParameters().Length == 0) return p.GetValue(o, null);
            }
            catch { }
            return null;
        }

        private static void Sample(int tick)
        {
            Type tThing = Refl.T("Verse.ThingDef");
            Type tDb = Type.GetType("Verse.DefDatabase`1, Assembly-CSharp");
            if (tThing == null || tDb == null) { Warn("types missing"); return; }

            IEnumerable defs = tDb.MakeGenericType(tThing)
                .GetProperty("AllDefsListForReading", BindingFlags.Public | BindingFlags.Static)
                .GetValue(null, null) as IEnumerable;
            if (defs == null) { Warn("no defs"); return; }

            StringBuilder sb = new StringBuilder();
            foreach (object d in defs)
            {
                if (d == null) continue;
                Type dt = d.GetType();
                string n = dt.GetField("defName", BindingFlags.Public | BindingFlags.Instance)?.GetValue(d) as string;
                if (n == null) continue;
                bool hit = false;
                foreach (string w in Names) if (w == n) { hit = true; break; }
                if (!hit) continue;

                object icon = dt.GetField("uiIcon", BindingFlags.Public | BindingFlags.Instance)?.GetValue(d);
                object core = Prop(icon, "CoreTexture");
                object mat = Prop(d, "uiIconMaterial");
                object shader = Prop(mat, "shader");
                object mtex = Prop(mat, "mainTexture");

                sb.Append("flicker t").Append(tick).Append(' ').Append(n)
                  .Append(" shader=").Append(shader == null ? "-" : (Prop(shader, "name") as string ?? "?"))
                  .Append(" matTex=").Append(mat == null ? "noMat" : (mtex == null ? "null" : "ok"))
                  .Append(" coreGpu=").Append(Prop(core, "GpuResource") == null ? "null" : "ok")
                  .Append(" iconGpu=").Append(Prop(icon, "GpuResource") == null ? "null" : "ok")
                  .Append('\n');
            }
            if (sb.Length > 0) Refl.Info(sb.ToString().TrimEnd());
        }

        private static void Warn(string m)
        {
            if (Interlocked.Exchange(ref _warned, 1) == 0) Refl.Warn("flicker: " + m);
        }
    }
}
