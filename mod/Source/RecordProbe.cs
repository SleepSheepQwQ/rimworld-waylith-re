using System;
using System.Collections;
using System.Reflection;
using System.Threading;
using HarmonyLib;

namespace AgentTouchFix
{
    /// <summary>
    /// v30: the draw call reaches the backend with sane rect/uv/colour (v29), so the question is what
    /// ImGuiVulkanRenderer.TryRecord() answers for each icon.
    ///
    ///   if (reference3.Material != null &amp;&amp; materialRenderer.TryRecord(...))
    ///   { flag = true; continue; }                       // ImGuiVulkanRenderer.cs:508
    ///   num3 = reference3.FallbackTextureId;             // material draw failed -> plain texture
    ///
    /// So a material failure *should* still paint the plain texture. If the tile is blank anyway, either
    /// TryRecord returns false AND the fallback id is unusable, or the command never reaches this loop
    /// (empty clip rect). This postfix records TryRecord's verdict per texture name, plus the fallback
    /// id the material draw carries, which separates those two.
    /// </summary>
    internal static class RecordProbe
    {
        private static int _warned;
        private static readonly System.Collections.Generic.HashSet<string> _seen =
            new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);

        internal static void Postfix(object[] __args, ref bool __result)
        {
            try
            {
                if (__args == null || __args.Length < 4) return;
                object draw = __args[3];
                if (draw == null) return;

                object main = Prop(draw, "MainTexture");
                object fallback = Prop(draw, "FallbackTextureId");
                string name = main == null ? "null" : (Prop(main, "name") as string ?? "?");

                string key = name + "|" + __result;
                bool first;
                lock (_seen) first = _seen.Add(key);
                if (!first) return;

                Refl.Info("record: '" + name + "' materialDraw=" + (__result ? "OK" : "FAILED")
                    + " fallbackTexId=" + (fallback ?? "?"));
            }
            catch (Exception e)
            {
                if (Interlocked.Exchange(ref _warned, 1) == 0) Refl.Warn("record: " + e.Message);
            }
        }

        private static object Prop(object o, string n)
        {
            if (o == null) return null;
            try
            {
                foreach (PropertyInfo p in o.GetType().GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    if (p.Name == n && p.GetIndexParameters().Length == 0) return p.GetValue(o, null);
                foreach (FieldInfo f in o.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    if (f.Name == n) return f.GetValue(o);
            }
            catch { }
            return null;
        }

        internal static void Install()
        {
            try
            {
                Type t = Refl.T("RimWorldWaylith.Imgui.ImGuiMaterialRenderer");
                if (t == null) { Refl.Warn("record: renderer not found"); return; }
                MethodInfo target = null;
                foreach (MethodInfo m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    if (m.Name == "TryRecord") { target = m; break; }
                if (target == null) { Refl.Warn("record: TryRecord not found"); return; }

                MethodInfo post = typeof(RecordProbe).GetMethod("Postfix", BindingFlags.Static | BindingFlags.NonPublic);
                new Harmony("sleepsheep.waylith.agenttouchfix.record").Patch(target, null, new HarmonyMethod(post));
                Refl.Info("record: TryRecord postfix installed (per-texture verdict, read-only)");
            }
            catch (Exception e)
            {
                if (Interlocked.Exchange(ref _warned, 1) == 0) Refl.Warn("record: install failed: " + e.Message);
            }
        }
    }
}
