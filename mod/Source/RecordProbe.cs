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

                object mat = Prop(draw, "Material");
                Refl.Info("record: '" + name + "' materialDraw=" + (__result ? "OK" : "FAILED")
                    + " fallbackTexId=" + (fallback ?? "?")
                    + " mat=" + MatProps(mat));
            }
            catch (Exception e)
            {
                if (Interlocked.Exchange(ref _warned, 1) == 0) Refl.Warn("record: " + e.Message);
            }
        }


        /// <summary>v31: read the values the shader will actually receive - a zero _Color or a zero
        /// _MainTex_ST makes the icon perfectly invisible while every other stage looks healthy.</summary>
        private static string MatProps(object mat)
        {
            if (mat == null) return "null";
            try
            {
                string nm = Prop(mat, "name") as string ?? "?";
                MethodInfo getColorStr = null, getVecStr = null, hasProp = null;
                foreach (MethodInfo m in mat.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (m.Name == "GetColor" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(string)) getColorStr = m;
                    else if (m.Name == "GetVector" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(string)) getVecStr = m;
                }
                object col = getColorStr == null ? null : getColorStr.Invoke(mat, new object[] { "_Color" });
                object col2 = getColorStr == null ? null : getColorStr.Invoke(mat, new object[] { "_ColorTwo" });
                object st = getVecStr == null ? null : getVecStr.Invoke(mat, new object[] { "_MainTex_ST" });
                return nm + " _Color=" + V(col) + " _ColorTwo=" + V(col2) + " _MainTex_ST=" + V(st);
            }
            catch (Exception e) { return "err:" + e.Message; }
        }

        private static string V(object v)
        {
            if (v == null) return "null";
            try
            {
                Type t = v.GetType();
                var sb = new System.Text.StringBuilder("(");
                bool first = true;
                foreach (FieldInfo f in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (!first) sb.Append(',');
                    first = false;
                    object x = f.GetValue(v);
                    sb.Append(x is float fl ? fl.ToString("0.###") : x);
                }
                return sb.Append(')').ToString();
            }
            catch { return "?"; }
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
