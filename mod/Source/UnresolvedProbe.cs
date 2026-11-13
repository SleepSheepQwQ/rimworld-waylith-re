using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using HarmonyLib;

namespace AgentTouchFix
{
    /// <summary>
    /// v22: the port already knows why a texture cannot be drawn - it just whispers it into the
    /// "Verbose" log category, which is off by default.
    ///
    ///   RimWorldWaylith.Imgui.ImGuiGuiBackend.TryResolveTexture()          (ImGuiGuiBackend.cs:1454)
    ///       if (!texture2D.EnsureGpuResident())
    ///           return Unresolved(image, DescribeNonResident(texture2D));
    ///   ... and three more Unresolved(...) branches: unsupported type, no gpu allocation,
    ///       disposed / no image view.
    ///
    /// DescribeNonResident() (ImGuiGuiBackend.cs:1499) returns one of:
    ///   "no core texture"
    ///   "no cpu data (destroyed or never built)"
    ///   "cpu data present but upload produced no gpu resource (pending gpu build?)"
    ///   "cpu data released and no source path to rehydrate from"
    ///   "cpu data released and texture cache miss for '<path>'"
    ///
    /// This prefix copies that reason into our own log file, with the texture name and size, so the
    /// real cause of a blank UI tile is recorded instead of guessed. Read-only: returns true so the
    /// original method still runs and still returns false to the caller.
    /// </summary>
    internal static class UnresolvedProbe
    {
        private static int _started, _warned, _count;
        private static readonly HashSet<string> _seen = new HashSet<string>(StringComparer.Ordinal);

        internal static bool Prefix(object image, string reason)
        {
            try
            {
                string name = "?", dims = "?";
                string src = "?";
                if (image != null)
                {
                    object n = Prop(image, "name");
                    if (n != null) name = n.ToString();
                    object w = Prop(image, "width"), h = Prop(image, "height");
                    dims = w + "x" + h;
                    object core = Prop(image, "CoreTexture");
                    if (core != null)
                    {
                        object p = Prop(core, "SourcePath");
                        src = p == null ? "null" : p.ToString();
                    }
                    else src = "no-core";
                }

                string key = name + "|" + reason;
                bool first;
                lock (_seen) first = _seen.Add(key);
                int n2 = Interlocked.Increment(ref _count);

                if (first || n2 <= 40)
                    Refl.Info("imgui-unresolved: '" + name + "' " + dims + " src=" + src + " :: " + reason);

                if (n2 == 1 || n2 % 50 == 0)
                    Refl.Info("imgui-unresolved: total=" + n2 + " distinct=" + _seen.Count);
            }
            catch (Exception e)
            {
                if (Interlocked.Exchange(ref _warned, 1) == 0) Refl.Warn("imgui-unresolved: " + e.Message);
            }
            return true;
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

        internal static void Install()
        {
            if (Interlocked.Exchange(ref _started, 1) == 1) return;
            try
            {
                Type t = Refl.T("RimWorldWaylith.Imgui.ImGuiGuiBackend");
                if (t == null) { Refl.Warn("imgui-unresolved: ImGuiGuiBackend not found"); return; }

                MethodInfo target = null;
                foreach (MethodInfo m in t.GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public))
                {
                    if (m.Name != "Unresolved") continue;
                    ParameterInfo[] ps = m.GetParameters();
                    if (ps.Length == 2 && ps[1].ParameterType == typeof(string)) { target = m; break; }
                }
                if (target == null) { Refl.Warn("imgui-unresolved: Unresolved(Texture,string) not found"); return; }

                MethodInfo pre = typeof(UnresolvedProbe).GetMethod("Prefix", BindingFlags.Static | BindingFlags.NonPublic);
                new Harmony("sleepsheep.waylith.agenttouchfix.imgui").Patch(target, new HarmonyMethod(pre), null);
                Refl.Info("imgui-unresolved: patched ImGuiGuiBackend.Unresolved - engine's own reason is now logged");
            }
            catch (Exception e) { Refl.Warn("imgui-unresolved: patch failed: " + e.Message); }
        }
    }
}
