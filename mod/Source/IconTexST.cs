using System;
using System.Reflection;
using System.Threading;

namespace AgentTouchFix
{
    /// <summary>
    /// v34: the build-menu icon materials sample with a collapsed uv.
    ///
    /// Data/Core/Shaders/Map/CutoutComplex.frag.glsl does
    ///
    ///     vec2 uv = vUv * uMaterial._MainTex_ST.zw + uMaterial._MainTex_ST.xy;   // zw = scale, xy = offset
    ///
    /// i.e. it reads _MainTex_ST as (offset.xy, scale.zw) - the reverse of Unity's (scale.xy, offset.zw).
    /// Verse.BuildableDef.ResolveIcon() builds uiIconMaterial from the graphic's MaterialRequest, and a
    /// request carrying a non-default texture rect stores _MainTex_ST in the Unity order. The engine only
    /// reorders for shaders whose PrecompiledStages.ScaleOffsetTextureST is set (MaterialBindingResolver
    /// .cs:140/160-164); for this precompiled UI shader it is not, so the shader receives (1,1,0,0),
    /// computes uv = vUv*(0,0)+(1,1) = (1,1), samples one corner texel, gets alpha &lt; 0.5 and discards -
    /// a permanently blank tile that no amount of texture residency work can affect.
    ///
    /// Fix: write _MainTex_ST so that after the engine's own flip/reorder the shader still sees the
    /// identity (offset 0,0 / scale 1,1). The inverse of (flip, then reorder) is computed from the shader
    /// itself, so a material that was already correct keeps the identical value - no guesswork, no
    /// regression for the icons that already render.
    /// </summary>
    internal static class IconTexST
    {
        private static int _ok, _failed, _warned;
        private static readonly System.Collections.Generic.HashSet<string> _logged =
            new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);

        internal static void Apply(object defInstance, FieldInfo matField)
        {
            if (defInstance == null || matField == null) return;
            try
            {
                object mat = matField.GetValue(defInstance);
                if (mat == null) return;

                object shader = ShaderOf(mat);
                bool flipV = Bool(Prop(shader, "FlipTextureST"));
                object stages = Prop(shader, "PrecompiledStages");
                bool scaleFirst = stages != null && Bool(Prop(stages, "ScaleOffsetTextureST"));

                // Identity in shader space, then invert the engine's transform.
                float x = 0f, y = 0f, z = 1f, w = 1f;
                if (scaleFirst) { x = 1f; y = 1f; z = 0f; w = 0f; }   // reorder is its own inverse
                if (flipV) { y = 1f - y; w = -w; }                     // Y flips, W negates

                MethodInfo setVector = FindSetVector(mat);
                if (setVector == null) { WarnOnce("no SetVector(string, Vector4) on " + mat.GetType().FullName); return; }
                object vec = MakeVector(setVector.GetParameters()[1].ParameterType, x, y, z, w);
                if (vec == null) { WarnOnce("cannot construct Vector4"); return; }

                setVector.Invoke(mat, new object[] { "_MainTex_ST", vec });
                int n = Interlocked.Increment(ref _ok);

                string name = (Prop(mat, "name") as string) ?? "?";
                bool first;
                lock (_logged) first = _logged.Add(name);
                if (first && _logged.Count <= 12)
                    Refl.Info("texst: " + name + " <- (" + x + "," + y + "," + z + "," + w + ")"
                        + " flipV=" + flipV + " scaleFirst=" + scaleFirst);
                else if (n % 200 == 0)
                    Refl.Info("texst: normalised " + n + " icon materials");
            }
            catch (Exception e)
            {
                Interlocked.Increment(ref _failed);
                WarnOnce(e.Message);
            }
        }

        private static object ShaderOf(object mat)
        {
            object eng = Prop(mat, "engineMaterial");
            if (eng != null)
            {
                object s = Prop(eng, "Shader");
                if (s != null) return s;
            }
            return Prop(mat, "shader");
        }

        private static bool Bool(object v)
        {
            return v is bool b && b;
        }

        private static object Prop(object o, string name)
        {
            if (o == null) return null;
            try
            {
                foreach (PropertyInfo p in o.GetType().GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    if (p.Name == name && p.GetIndexParameters().Length == 0) return p.GetValue(o, null);
                foreach (FieldInfo f in o.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    if (f.Name == name) return f.GetValue(o);
            }
            catch { }
            return null;
        }

        private static MethodInfo FindSetVector(object mat)
        {
            foreach (MethodInfo m in mat.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                ParameterInfo[] ps = m.GetParameters();
                if (ps.Length != 2 || m.Name != "SetVector" || ps[0].ParameterType != typeof(string)) continue;
                if (ps[1].ParameterType.IsValueType) return m;
            }
            return null;
        }

        private static object MakeVector(Type t, float x, float y, float z, float w)
        {
            try { return Activator.CreateInstance(t, x, y, z, w); }
            catch { }
            try
            {
                object v = Activator.CreateInstance(t);
                string[] names = { "x", "y", "z", "w" };
                float[] vals = { x, y, z, w };
                for (int i = 0; i < 4; i++)
                {
                    FieldInfo f = t.GetField(names[i], BindingFlags.Public | BindingFlags.Instance);
                    if (f == null) return null;
                    f.SetValue(v, vals[i]);
                }
                return v;
            }
            catch { return null; }
        }

        private static void WarnOnce(string m)
        {
            if (Interlocked.Exchange(ref _warned, 1) == 0) Refl.Warn("texst: " + m);
        }
    }
}
