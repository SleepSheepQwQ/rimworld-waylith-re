using System;
using System.Reflection;
using System.Threading;
using HarmonyLib;

namespace AgentTouchFix
{
    internal static class IconPlainPath
    {
        private static int _routed;
        private static int _warned;
        private static string _mode = IconPath.Plain;
        private static MethodInfo _plain;      // cached DrawTextureWithTexCoords(Rect, Texture, Rect, Color)
        private static Type _backendType;

        internal static void Init(string mode)
        {
            _mode = string.IsNullOrEmpty(mode) ? IconPath.Plain : mode.Trim().ToLowerInvariant();
            Refl.Info("iconpath: mode=" + _mode + " (icon materials "
                + (_mode == IconPath.Material ? "keep their CutoutComplexUI pass" : "go through DrawTextureWithTexCoords")
                + ")");
        }

        internal static bool Prefix(object __instance, object[] __args)
        {
            try
            {
                if (__args == null || __args.Length < 4) return true;
                object mat = __args.Length > 4 ? __args[4] : null;
                string name = mat == null ? null : (NameOf(mat) ?? "");
                if (!IconPath.UsePlain(name, _mode)) return true;

                if (_plain == null || _backendType != __instance.GetType())
                {
                    _backendType = __instance.GetType();
                    _plain = FindPlain(_backendType);
                    if (_plain == null) { WarnOnce("DrawTextureWithTexCoords not found"); return true; }
                }

                _plain.Invoke(__instance, new object[] { __args[0], __args[1], __args[2], __args[3] });
                int n = Interlocked.Increment(ref _routed);
                if (n <= 5 || n % 5000 == 0)
                    Refl.Info("iconpath: '" + name + "' -> plain texture path (" + n + " icons so far)");
                return false;
            }
            catch (Exception e)
            {
                WarnOnce(e.Message);
                return true;
            }
        }

        private static MethodInfo FindPlain(Type t)
        {
            foreach (MethodInfo m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                ParameterInfo[] ps = m.GetParameters();
                if (m.Name == "DrawTextureWithTexCoords" && ps.Length == 4) return m;
            }
            return null;
        }

        private static string NameOf(object mat)
        {
            try
            {
                foreach (PropertyInfo p in mat.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                    if (p.Name == "name" && p.GetIndexParameters().Length == 0) return p.GetValue(mat, null) as string;
                foreach (FieldInfo f in mat.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
                    if (f.Name == "name") return f.GetValue(mat) as string;
            }
            catch { }
            return null;
        }

        private static void WarnOnce(string m)
        {
            if (Interlocked.Exchange(ref _warned, 1) == 0) Refl.Warn("iconpath: " + m);
        }

        internal static void Install()
        {
            Type backend = Refl.T("RimWorldWaylith.Imgui.ImGuiGuiBackend");
            if (backend == null) { Refl.Warn("iconpath: backend not found"); return; }

            MethodInfo target = null;
            foreach (MethodInfo m in backend.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                if (m.Name == "DrawTextureWithMaterial" && m.GetParameters().Length == 6) { target = m; break; }
            if (target == null) { Refl.Warn("iconpath: DrawTextureWithMaterial not found"); return; }

            try
            {
                MethodInfo pre = typeof(IconPlainPath).GetMethod("Prefix",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                new Harmony("sleepsheep.waylith.agenttouchfix.iconpath").Patch(target, new HarmonyMethod(pre));
                Refl.Info("iconpath: prefix on ImGuiGuiBackend.DrawTextureWithMaterial "
                    + "(icon materials divert to the plain texture path)");
            }
            catch (Exception e) { Refl.Warn("iconpath: patch failed: " + e.Message); }
        }
    }
}
