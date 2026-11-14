using System;
using System.Reflection;
using System.Text;
using System.Threading;

namespace AgentTouchFix
{
    /// <summary>
    /// v37: one complete, read-only dump of the icon material state - every value that decides what the
    /// CutoutComplexUI fragment shader receives, read by reflection at the moment ResolveIcon finishes.
    ///
    /// The dump answers, without guessing, the questions the earlier probes each took a restart to ask:
    ///   * does the shader have a texture slot whose PropertyName is exactly "_MainTex"?
    ///     TryBuildMaterialSet binds the icon only when it does (ImGuiMaterialRenderer.cs:340-360);
    ///     otherwise the sampler falls back to the material's own _MainTex.
    ///   * is there a _MaskTex slot, and what does the material hold for it? (black fallback keeps
    ///     colPower at 0, i.e. the icon untouched; a bound white/garbage mask multiplies it away)
    ///   * what are _Color / _ColorTwo / _MainTex_ST on the material, and does _MainTex_ST even exist?
    ///   * shader.HasErrors, shader name, uniform slot names+offsets, and the uiIcon/core identity.
    /// </summary>
    internal static class MatDump
    {
        private static int _dumped;
        private static int _warned;

        internal static void Dump(object defInstance, FieldInfo matField, FieldInfo uiIconField, FieldInfo defNameField)
        {
            if (defInstance == null || matField == null) return;
            if (Volatile.Read(ref _dumped) >= 14) return;
            try
            {
                object mat = matField.GetValue(defInstance);
                if (mat == null) return;
                Interlocked.Increment(ref _dumped);

                string def = defNameField == null ? "?" : (defNameField.GetValue(defInstance) as string) ?? "?";
                object eng = Prop(mat, "engineMaterial");
                object shader = eng != null ? Prop(eng, "Shader") : Prop(mat, "shader");

                StringBuilder sb = new StringBuilder();
                sb.Append("matdump ").Append(def);
                sb.Append(" mat=").Append(Prop(mat, "name") as string ?? "?");
                sb.Append(" shader=").Append(Prop(shader, "name") as string ?? "?");
                sb.Append(" hasErrors=").Append(Prop(shader, "HasErrors"));
                sb.Append(" flipV=").Append(Prop(shader, "FlipTextureST"));
                sb.Append(" precompiled=").Append(Prop(shader, "PrecompiledStages") != null);
                Refl.Info(sb.ToString());

                object layout = Prop(shader, "MaterialLayout");
                Refl.Info("matdump   texslots: " + Slots(Prop(layout, "TextureSlots"), "PropertyName", "ResourceSlot", "Kind"));
                Refl.Info("matdump   unislots: " + Slots(Prop(layout, "UniformSlots"), "PropertyName", "Offset", "Type"));
                Refl.Info("matdump   props: _MainTex=" + Has(mat, "_MainTex") + " " + TexName(mat, "_MainTex")
                    + " | _MaskTex=" + Has(mat, "_MaskTex") + " " + TexName(mat, "_MaskTex")
                    + " | _Color=" + Vec(mat, "_Color")
                    + " | _ColorTwo=" + Vec(mat, "_ColorTwo")
                    + " | _MainTex_ST=" + Has(mat, "_MainTex_ST") + " " + Vec(mat, "_MainTex_ST"));

                if (uiIconField != null)
                {
                    object icon = uiIconField.GetValue(defInstance);
                    object core = icon == null ? null : Prop(icon, "CoreTexture");
                    Refl.Info("matdump   icon=" + (icon == null ? "null" : (Prop(icon, "name") as string ?? "?"))
                        + " " + Dim(icon) + " core=" + (core == null ? "null" : (Prop(core, "name") as string ?? "?")) + " " + Dim(core)
                        + " same=" + ReferenceEquals(icon, Prop(mat, "_MainTex")));
                }
            }
            catch (Exception e) { WarnOnce(e.Message); }
        }

        private static string Slots(object arr, string f1, string f2, string f3)
        {
            if (!(arr is Array a)) return "n/a";
            StringBuilder sb = new StringBuilder("[");
            for (int i = 0; i < a.Length; i++)
            {
                object s = a.GetValue(i);
                if (i > 0) sb.Append(", ");
                sb.Append(Prop(s, f1)).Append('/').Append(Prop(s, f2));
                object third = Prop(s, f3);
                if (third != null) sb.Append('/').Append(third);
            }
            return sb.Append(']').ToString();
        }

        private static string Has(object mat, string name)
        {
            MethodInfo m = Find1(mat, "HasProperty");
            if (m == null) return "?";
            try { return m.Invoke(mat, new object[] { name }) is bool b ? (b ? "yes" : "no") : "?"; }
            catch { return "?"; }
        }

        private static string TexName(object mat, string name)
        {
            MethodInfo m = Find1(mat, "GetTexture");
            if (m == null) return "";
            try
            {
                object t = m.Invoke(mat, new object[] { name });
                return t == null ? "null" : ((Prop(t, "name") as string) ?? "?") + " " + Dim(t);
            }
            catch { return "err"; }
        }

        private static string Vec(object mat, string name)
        {
            MethodInfo m = Find1(mat, "GetVector");
            if (m == null) return "?";
            try
            {
                object v = m.Invoke(mat, new object[] { name });
                if (v == null) return "null";
                StringBuilder sb = new StringBuilder("(");
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
            catch { return "err"; }
        }

        private static string Dim(object tex)
        {
            if (tex == null) return "";
            object w = Prop(tex, "width"), h = Prop(tex, "height");
            return w == null || h == null ? "" : w + "x" + h;
        }

        private static MethodInfo Find1(object o, string name)
        {
            if (o == null) return null;
            foreach (MethodInfo m in o.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                ParameterInfo[] ps = m.GetParameters();
                if (ps.Length == 1 && ps[0].ParameterType == typeof(string) && m.Name == name) return m;
            }
            return null;
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

        private static void WarnOnce(string m)
        {
            if (Interlocked.Exchange(ref _warned, 1) == 0) Refl.Warn("matdump: " + m);
        }
    }
}
