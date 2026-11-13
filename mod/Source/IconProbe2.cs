using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Threading;

namespace AgentTouchFix
{
    /// <summary>
    /// v17: which building icons are not GPU-resident, and does that flip over time?
    ///
    /// Verse.BuildableDef.uiIcon is a plain Texture2D per def. If only a handful of build-menu tiles
    /// blink, those textures must be losing their GPU resource (or never getting one) while the rest
    /// stay put. This probe walks DefDatabase&lt;ThingDef&gt; twice, 10 s apart, and prints the first
    /// 15 defs whose uiIcon has no GpuResource, plus their size/format/SourcePath, so a texture that
    /// flaps between resident and not is visible as "resident=False" in both samples.
    ///
    /// Read-only: no state is written. Runs once at 30 s and once at 40 s after bootstrap.
    /// </summary>
    internal static class IconProbe2
    {
        private static int _started;
        private static int _warned;

        internal static void Install()
        {
            if (Interlocked.Exchange(ref _started, 1) == 1) return;
            try
            {
                Thread t = new Thread(Loop) { IsBackground = true, Name = "AgentTouchFixIconProbe2" };
                t.Start();
                Refl.Info("probe2: scheduled two samples (30 s, 40 s), read-only");
            }
            catch (Exception e) { Refl.Warn("probe2: start failed: " + e.Message); }
        }

        private static void Loop()
        {
            try { Thread.Sleep(30000); } catch { }
            Sample("t+30s");
            try { Thread.Sleep(10000); } catch { }
            Sample("t+40s");
        }

        private static void Sample(string tag)
        {
            try
            {
                Type tThing = Refl.T("Verse.ThingDef");
                Type tDb = Type.GetType("Verse.DefDatabase`1, Assembly-CSharp");
                if (tThing == null || tDb == null) { Warn("types missing"); return; }

                Type closed = tDb.MakeGenericType(tThing);
                PropertyInfo p = closed.GetProperty("AllDefsListForReading", BindingFlags.Public | BindingFlags.Static);
                IEnumerable defs = p == null ? null : p.GetValue(null, null) as IEnumerable;
                if (defs == null) { Warn("no defs"); return; }

                int total = 0, noIcon = 0, notResident = 0;
                StringBuilder sb = new StringBuilder();

                foreach (object d in defs)
                {
                    if (d == null) continue;
                    Type dt = d.GetType();
                    FieldInfo fIcon = dt.GetField("uiIcon", BindingFlags.Public | BindingFlags.Instance);
                    if (fIcon == null) continue;
                    object icon = fIcon.GetValue(d);
                    total++;
                    if (icon == null) { noIcon++; continue; }

                    object gpu = null;
                    try { gpu = icon.GetType().GetProperty("GpuResource", BindingFlags.Public | BindingFlags.Instance)?.GetValue(icon, null); } catch { }
                    if (gpu != null) continue;
                    notResident++;
                    if (notResident > 15) continue;

                    string defName = dt.GetField("defName", BindingFlags.Public | BindingFlags.Instance)?.GetValue(d) as string;
                    object w = icon.GetType().GetProperty("width")?.GetValue(icon, null);
                    object h = icon.GetType().GetProperty("height")?.GetValue(icon, null);
                    object src = icon.GetType().GetProperty("SourcePath", BindingFlags.Public | BindingFlags.Instance)?.GetValue(icon, null);
                    object cpu = icon.GetType().GetProperty("CpuData", BindingFlags.Public | BindingFlags.Instance)?.GetValue(icon, null);
                    string hasData = "?", levels = "?";
                    if (cpu != null)
                    {
                        hasData = cpu.GetType().GetProperty("HasData")?.GetValue(cpu, null)?.ToString() ?? "?";
                        Array arr = cpu.GetType().GetProperty("Levels")?.GetValue(cpu, null) as Array;
                        levels = arr == null ? "?" : arr.Length.ToString();
                    }
                    sb.Append("probe2 ").Append(tag).Append(": ").Append(defName)
                      .Append(" ").Append(w).Append('x').Append(h)
                      .Append(" gpu=null cpu=").Append(cpu != null)
                      .Append(" hasData=").Append(hasData)
                      .Append(" levels=").Append(levels)
                      .Append(" src=").Append(src == null ? "null" : "yes").Append('\n');
                }

                Refl.Info("probe2 " + tag + ": defs=" + total + " noIcon=" + noIcon + " notResident=" + notResident);
                if (sb.Length > 0) Refl.Info(sb.ToString().TrimEnd());
            }
            catch (Exception e) { Warn(tag + ": " + e.Message); }
        }

        private static void Warn(string m)
        {
            if (Interlocked.Exchange(ref _warned, 1) == 0) Refl.Warn("probe2: " + m);
        }
    }
}
