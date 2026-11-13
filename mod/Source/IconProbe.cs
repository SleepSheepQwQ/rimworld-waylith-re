using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Threading;
using HarmonyLib;
using Verse;
using UnityEngine;

namespace AgentTouchFix
{
    /// <summary>
    /// v15: read-only dump of what the build-menu icons actually are at runtime.
    ///
    /// For up to 10 designators (Verse.DesignationCategoryDef.resolvedDesignators) it prints:
    ///   name | icon type | WxH | CpuData? | HasData? | levels | SourcePath | GpuResource?
    /// which separates the three remaining explanations for a blank icon:
    ///   a) CpuData lost + SourcePath null   -> the texture can never come back (data evicted)
    ///   b) CpuData present, GpuResource null-> upload/residency problem
    ///   c) both present                     -> the blank is not a texture problem (mesh/UV/atlas)
    ///
    /// Runs once, ~25 s after install (well after the brick wall of load), never changes state.
    /// </summary>
    internal static class IconProbe
    {
        private static int _done;
        private static int _started;

        internal static void Start()
        {
            try
            {
                Thread t = new Thread(Loop) { IsBackground = true, Name = "AgentTouchFixIconProbe" };
                t.Start();
            }
            catch (Exception e) { Refl.Warn("iconprobe: start failed: " + e.Message); }
        }

        private static void Loop()
        {
            try { Thread.Sleep(25000); } catch { }
            if (Interlocked.Exchange(ref _done, 1) == 1) return;
            try { Dump(); } catch (Exception e) { Refl.Warn("iconprobe: " + e.Message); }
        }

        private static void Dump()
        {
            Type tCat = Refl.T("Verse.DesignationCategoryDef");
            Type tDb = Type.GetType("Verse.DefDatabase`1, Assembly-CSharp");
            if (tCat == null || tDb == null) { Refl.Warn("iconprobe: game types missing"); return; }

            IEnumerable cats = null;
            try
            {
                Type closed = tDb.MakeGenericType(tCat);
                PropertyInfo p = closed.GetProperty("AllDefsListForReading", BindingFlags.Public | BindingFlags.Static);
                cats = p == null ? null : p.GetValue(null, null) as IEnumerable;
            }
            catch (Exception e) { Refl.Warn("iconprobe: defdb: " + e.Message); }

            if (cats == null) { Refl.Warn("iconprobe: no categories"); return; }

            int dumped = 0, totalDesignators = 0;
            StringBuilder sb = new StringBuilder();
            foreach (object cat in cats)
            {
                if (cat == null) continue;
                object list = null;
                try
                {
                    FieldInfo f = cat.GetType().GetField("resolvedDesignators", BindingFlags.Public | BindingFlags.Instance);
                    if (f != null) list = f.GetValue(cat);
                }
                catch { }
                IEnumerable ds = list as IEnumerable;
                if (ds == null) continue;
                foreach (object d in ds)
                {
                    if (d == null) continue;
                    totalDesignators++;
                    if (dumped >= 10) continue;
                    dumped++;
                    sb.Append("iconprobe: ").Append(Describe(d)).Append('\n');
                }
            }
            Refl.Info("iconprobe: designators=" + totalDesignators + " sampled=" + dumped);
            Refl.Info(sb.ToString().TrimEnd());

            // v15b: ask ContentFinder directly - is the texture missing, or is the icon field unset?
            string[] paths = { "UI/Designators/Deconstruct", "UI/Commands/Mine", "UI/Commands/Attack", "UI/Commands/Cancel" };
            foreach (string p in paths)
            {
                try
                {
                    Texture2D t = ContentFinder<Texture2D>.Get(p, false);
                    Refl.Info("iconprobe: find " + p + " -> "
                        + (t == null ? "NULL" : (t.width + "x" + t.height)));
                }
                catch (Exception e) { Refl.Info("iconprobe: find " + p + " threw " + e.Message); }
            }
        }

        private static string Describe(object designator)
        {
            string name = "?";
            try { name = (designator.GetType().GetProperty("Label")?.GetValue(designator, null) as string) ?? designator.GetType().Name; }
            catch { name = designator.GetType().Name; }

            object icon = null;
            try { icon = designator.GetType().GetProperty("icon", BindingFlags.Public | BindingFlags.Instance)?.GetValue(designator, null); }
            catch { }

            if (icon == null) return name + " | icon=null";

            Type it = icon.GetType();
            object w = null, h = null, cpu = null, hasData = null, levels = null, src = null, gpu = null;
            try { w = it.GetProperty("width", BindingFlags.Public | BindingFlags.Instance)?.GetValue(icon, null); } catch { }
            try { h = it.GetProperty("height", BindingFlags.Public | BindingFlags.Instance)?.GetValue(icon, null); } catch { }
            try { cpu = it.GetProperty("CpuData", BindingFlags.Public | BindingFlags.Instance)?.GetValue(icon, null); } catch { }
            try { src = it.GetProperty("SourcePath", BindingFlags.Public | BindingFlags.Instance)?.GetValue(icon, null); } catch { }
            try { gpu = it.GetProperty("GpuResource", BindingFlags.Public | BindingFlags.Instance)?.GetValue(icon, null); } catch { }
            if (cpu != null)
            {
                try { hasData = cpu.GetType().GetProperty("HasData")?.GetValue(cpu, null); } catch { }
                try
                {
                    object lv = cpu.GetType().GetProperty("Levels")?.GetValue(cpu, null);
                    Array arr = lv as Array;
                    levels = arr == null ? "?" : arr.Length.ToString();
                }
                catch { }
            }

            return name + " | " + it.Name + " " + w + "x" + h
                + " cpu=" + (cpu != null) + " hasData=" + hasData + " levels=" + levels
                + " src=" + (src == null ? "null" : "yes") + " gpu=" + (gpu != null);
        }

        internal static void Install()
        {
            Start();
            Refl.Info("iconprobe: scheduled (25 s after bootstrap, one shot, read-only)");
        }
    }
}
