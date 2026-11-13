using System;
using System.Collections;
using System.Reflection;
using System.Text;
using System.Threading;

namespace AgentTouchFix
{
    /// <summary>
    /// v18: focused read-only dump for a few defs, so the difference between a build-menu tile that
    /// renders and one that blinks becomes visible: graphic type, graphic.path, core texture,
    /// uiIcon material and its shader.
    ///
    /// Controls: Wall / Table (render fine).  Targets: NutrientPasteDispenser / BedRoyal-style beds.
    /// </summary>
    internal static class DefProbe
    {
        private static int _started, _warned;

        internal static void Install()
        {
            if (Interlocked.Exchange(ref _started, 1) == 1) return;
            try
            {
                Thread t = new Thread(Loop) { IsBackground = true, Name = "AgentTouchFixDefProbe" };
                t.Start();
                Refl.Info("defprobe: scheduled at t+35 s (read-only)");
            }
            catch (Exception e) { Refl.Warn("defprobe: start failed: " + e.Message); }
        }

        private static void Loop()
        {
            try { Thread.Sleep(35000); } catch { }
            try { Dump(); } catch (Exception e) { Warn(e.Message); }
        }

        private static bool Wanted(string n)
        {
            if (n == null) return false;
            return n.IndexOf("Nutrient", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("Bed", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("TableButcher", StringComparison.OrdinalIgnoreCase) >= 0
                || n == "Wall" || n == "Table2x2c" || n == "DiningChair";
        }

        private static void Dump()
        {
            Type tThing = Refl.T("Verse.ThingDef");
            Type tDb = Type.GetType("Verse.DefDatabase`1, Assembly-CSharp");
            if (tThing == null || tDb == null) { Warn("types missing"); return; }
            IEnumerable defs = tDb.MakeGenericType(tThing)
                .GetProperty("AllDefsListForReading", BindingFlags.Public | BindingFlags.Static)
                .GetValue(null, null) as IEnumerable;
            if (defs == null) { Warn("no defs"); return; }

            StringBuilder sb = new StringBuilder();
            int rows = 0;
            foreach (object d in defs)
            {
                if (d == null) continue;
                Type dt = d.GetType();
                string defName = dt.GetField("defName", BindingFlags.Public | BindingFlags.Instance)?.GetValue(d) as string;
                if (!Wanted(defName)) continue;

                object graphic = dt.GetField("graphic", BindingFlags.Public | BindingFlags.Instance)?.GetValue(d);
                object uiIcon = dt.GetField("uiIcon", BindingFlags.Public | BindingFlags.Instance)?.GetValue(d);
                string gtype = graphic == null ? "null" : graphic.GetType().Name;
                string gpath = "?";
                if (graphic != null)
                {
                    FieldInfo fp = null;
                    for (Type t = graphic.GetType(); t != null && fp == null; t = t.BaseType)
                        fp = t.GetField("path", BindingFlags.Public | BindingFlags.Instance);
                    gpath = fp == null ? "?" : (fp.GetValue(graphic) as string ?? "null");
                }

                string core = "?";
                if (uiIcon != null)
                {
                    PropertyInfo pc = uiIcon.GetType().GetProperty("CoreTexture", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    object ct = pc == null ? null : pc.GetValue(uiIcon, null);
                    if (ct == null) core = "null";
                    else
                    {
                        object gpu = ct.GetType().GetProperty("GpuResource")?.GetValue(ct, null);
                        object w = ct.GetType().GetProperty("width")?.GetValue(ct, null);
                        object h = ct.GetType().GetProperty("height")?.GetValue(ct, null);
                        object src = ct.GetType().GetProperty("SourcePath")?.GetValue(ct, null);
                        core = w + "x" + h + " gpu=" + (gpu != null) + " src=" + (src == null ? "null" : "yes");
                    }
                }

                string mat = "?";
                object iconMat = dt.GetField("uiIconMaterial", BindingFlags.Public | BindingFlags.Instance)?.GetValue(d);
                if (iconMat == null) mat = "null";
                else
                {
                    object sh = iconMat.GetType().GetProperty("shader")?.GetValue(iconMat, null);
                    object texObj = iconMat.GetType().GetProperty("mainTexture")?.GetValue(iconMat, null);
                    mat = "shader=" + (sh == null ? "null" : (sh.GetType().GetProperty("name")?.GetValue(sh, null) as string ?? "?"))
                        + " tex=" + (texObj == null ? "null" : texObj.GetType().Name);
                }

                sb.Append("defprobe: ").Append(defName)
                  .Append(" graphic=").Append(gtype)
                  .Append(" path=").Append(gpath)
                  .Append(" core=").Append(core)
                  .Append(" mat=").Append(mat).Append('\n');
                rows++;
            }
            Refl.Info("defprobe: rows=" + rows);
            if (sb.Length > 0) Refl.Info(sb.ToString().TrimEnd());
        }

        private static void Warn(string m)
        {
            if (Interlocked.Exchange(ref _warned, 1) == 0) Refl.Warn("defprobe: " + m);
        }
    }
}
