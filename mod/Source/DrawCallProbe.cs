using System;
using System.Collections;
using System.Reflection;
using System.Text;
using System.Threading;
using HarmonyLib;

namespace AgentTouchFix
{
    /// <summary>
    /// v29: record what is actually handed to the backend when a build-menu icon is drawn.
    ///
    /// The material draw is apparently recorded (no pipeline / set / unresolved failure), so the tile
    /// must be drawn with invisible parameters. The two entry points are
    ///
    ///   ImGuiGuiBackend.DrawTexture(Rect rect, Texture image, ScaleMode, bool alphaBlend)         // plain
    ///   ImGuiGuiBackend.DrawTextureWithMaterial(Rect rect, Texture image, Rect texCoords,
    ///                                           Color color, Material material, Material? pass)  // material
    ///
    /// This prefix logs, once per unique texture name: rect, texCoords (uv), the vertex colour
    /// including alpha, the material name and the pass material's shader. Read-only: __args is only
    /// inspected, never written, and the original method runs untouched.
    /// </summary>
    internal static class DrawCallProbe
    {
        private static int _warned;
        private static readonly System.Collections.Generic.HashSet<string> _seen =
            new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);

        // named "withMaterial" variant
        internal static void MaterialPrefix(object[] __args)
        {
            try
            {
                if (__args == null || __args.Length < 6) return;
                object image = __args[1];
                if (image == null) return;

                string name = Name(image);
                if (!Register("mat|" + name)) return;

                object mat = __args[4];
                object pass = __args[5];
                Refl.Info("drawcall: WITH-MATERIAL '" + name + "' " + Dims(image)
                    + " rect=" + Fmt(__args[0]) + " uv=" + Fmt(__args[2]) + " color=" + Fmt(__args[3])
                    + " material=" + (mat == null ? "null" : Name(mat))
                    + " engine=" + EngineProps(pass));
            }
            catch (Exception e) { Warn(e.Message); }
        }

        // plain variant: (Rect, Texture, ScaleMode, bool)
        internal static void PlainPrefix(object[] __args)
        {
            try
            {
                if (__args == null || __args.Length < 2) return;
                object image = __args[1];
                if (image == null) return;

                string name = Name(image);
                if (!Register("plain|" + name)) return;

                Refl.Info("drawcall: PLAIN '" + name + "' " + Dims(image) + " rect=" + Fmt(__args[0]));
            }
            catch (Exception e) { Warn(e.Message); }
        }

        private static bool Register(string key)
        {
            lock (_seen)
            {
                if (_seen.Count >= 60) return false;
                return _seen.Add(key);
            }
        }

        private static string Name(object o)
        {
            try
            {
                foreach (PropertyInfo p in o.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                    if (p.Name == "name" && p.GetIndexParameters().Length == 0) return p.GetValue(o, null) as string ?? "?";
                foreach (FieldInfo f in o.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
                    if (f.Name == "name") return f.GetValue(o) as string ?? "?";
            }
            catch { }
            return o.GetType().Name;
        }

        private static string Dims(object o)
        {
            try
            {
                object w = null, h = null;
                foreach (PropertyInfo p in o.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (p.GetIndexParameters().Length != 0) continue;
                    if (p.Name == "width") w = p.GetValue(o, null);
                    else if (p.Name == "height") h = p.GetValue(o, null);
                }
                return w + "x" + h;
            }
            catch { return "?x?"; }
        }

        private static string ShaderName(object passMaterial)
        {
            if (passMaterial == null) return "null";
            try
            {
                object sh = null;
                foreach (PropertyInfo p in passMaterial.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                    if (p.Name == "Shader") { sh = p.GetValue(passMaterial, null); break; }
                if (sh == null) return "no-shader";
                foreach (PropertyInfo p in sh.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                    if (p.Name == "name") return p.GetValue(sh, null) as string ?? "?";
            }
            catch { }
            return "?";
        }


        /// <summary>v32: the values the shader will actually receive (the Unity material's properties
        /// are what ResolveMaterialBindingData reads). Zero _MainTex_ST collapses the uv to a single
        /// texel; zero _Color/_ColorTwo multiplies the icon away.</summary>
        private static string EngineProps(object engineMaterial)
        {
            if (engineMaterial == null) return "null";
            try
            {
                string nm = null;
                try { nm = engineMaterial.GetType().GetProperty("name")?.GetValue(engineMaterial, null) as string; } catch { }
                MethodInfo getColor = null, getVector = null;
                foreach (MethodInfo m in engineMaterial.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    ParameterInfo[] ps = m.GetParameters();
                    if (ps.Length != 1) continue;
                    if (m.Name == "GetColor" && (ps[0].ParameterType == typeof(string) || ps[0].ParameterType == typeof(int))) getColor = getColor ?? m;
                    if (m.Name == "GetVector" && (ps[0].ParameterType == typeof(string) || ps[0].ParameterType == typeof(int))) getVector = getVector ?? m;
                }
                string arg = (getColor != null && getColor.GetParameters()[0].ParameterType == typeof(string)) ? "_Color" : null;
                object col = null, col2 = null, st = null;
                if (getColor != null && arg != null)
                {
                    col = getColor.Invoke(engineMaterial, new object[] { "_Color" });
                    col2 = getColor.Invoke(engineMaterial, new object[] { "_ColorTwo" });
                }
                if (getVector != null && getVector.GetParameters()[0].ParameterType == typeof(string))
                    st = getVector.Invoke(engineMaterial, new object[] { "_MainTex_ST" });
                return (nm ?? "?") + " _Color=" + V(col) + " _ColorTwo=" + V(col2) + " _MainTex_ST=" + V(st);
            }
            catch (Exception e) { return "err:" + e.Message; }
        }

        private static string V(object v)
        {
            if (v == null) return "n/a";
            try
            {
                var sb = new StringBuilder("(");
                bool first = true;
                foreach (FieldInfo f in v.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
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

        private static string Fmt(object v)
        {
            if (v == null) return "null";
            Type t = v.GetType();
            if (!t.IsValueType) return Name(v);
            StringBuilder sb = new StringBuilder("(");
            FieldInfo[] fs = t.GetFields(BindingFlags.Public | BindingFlags.Instance);
            if (fs.Length > 0)
            {
                for (int i = 0; i < fs.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    object val = fs[i].GetValue(v);
                    if (val is float f) sb.Append(f.ToString("0.###"));
                    else sb.Append(val);
                }
            }
            else
            {
                PropertyInfo[] ps = t.GetProperties(BindingFlags.Public | BindingFlags.Instance);
                for (int i = 0; i < ps.Length; i++)
                {
                    if (ps[i].GetIndexParameters().Length != 0) continue;
                    if (i > 0) sb.Append(',');
                    object val = ps[i].GetValue(v, null);
                    if (val is float f) sb.Append(f.ToString("0.###"));
                    else sb.Append(val);
                }
            }
            return sb.Append(')').ToString();
        }

        private static void Warn(string m)
        {
            if (Interlocked.Exchange(ref _warned, 1) == 0) Refl.Warn("drawcall: " + m);
        }

        internal static void Install()
        {
            try
            {
                Type backend = Refl.T("RimWorldWaylith.Imgui.ImGuiGuiBackend");
                if (backend == null) { Refl.Warn("drawcall: backend not found"); return; }
                Harmony h = new Harmony("sleepsheep.waylith.agenttouchfix.drawcall");
                MethodInfo matPrefix = typeof(DrawCallProbe).GetMethod("MaterialPrefix", BindingFlags.Static | BindingFlags.NonPublic);
                MethodInfo plainPrefix = typeof(DrawCallProbe).GetMethod("PlainPrefix", BindingFlags.Static | BindingFlags.NonPublic);

                int ok = 0;
                foreach (MethodInfo m in backend.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    ParameterInfo[] ps = m.GetParameters();
                    if (m.Name == "DrawTextureWithMaterial" && ps.Length == 6)
                    { h.Patch(m, new HarmonyMethod(matPrefix), null); ok++; }
                    else if (m.Name == "DrawTexture" && ps.Length == 2)
                    { h.Patch(m, new HarmonyMethod(plainPrefix), null); ok++; }
                }
                Refl.Info("drawcall: patched " + ok + " backend draw entry point(s) (read-only)");
            }
            catch (Exception e) { Warn("install failed: " + e.Message); }
        }
    }
}
