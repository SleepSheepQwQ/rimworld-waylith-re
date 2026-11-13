using System;
using System.Reflection;
using System.Threading;
using HarmonyLib;

namespace AgentTouchFix
{
    /// <summary>
    /// v26: act on the exact branch found in Texture2D.EnsureResident().
    ///
    ///   public override bool EnsureResident()           Texture2D.cs:342
    ///   {
    ///       if (GpuResource != null) return true;
    ///       if (CpuData == null || PendingBuild != null) return false;   // <-- (b) transient
    ///       if (CpuData.HasData) { UploadCpuData(); return GpuResource != null; }
    ///       if (!TryUploadFromCache()) return false;
    ///   }
    ///
    /// A texture that is still sitting in TextureBuildBatch (PendingBuild != null) reports
    /// "not resident" even though everything needed to build it is already in memory. Every draw
    /// that hits that window silently skips the texture (ImGuiMaterialRenderer.TryRecord -> false),
    /// which is the blank/flickering build-menu tile.
    ///
    /// The fix is the obvious one: when EnsureResident() fails *because a build is merely pending*,
    /// finish that build synchronously (Texture2D.FinishPendingBuild() flushes TextureBuildBatch)
    /// and ask once more. Nothing else is touched: no eviction, no upload, no cache, no governor.
    /// </summary>
    internal static class ResidencyFix
    {
        private static int _warned, _fixedCount, _stillFail;

        [ThreadStatic] private static bool _inFix;

        internal static void Postfix(object __instance, ref bool __result)
        {
            if (__result || __instance == null || _inFix) return;
            _inFix = true;
            try
            {
                object pending = Field(__instance, "PendingBuild");
                if (pending == null) { Interlocked.Increment(ref _stillFail); return; }

                // flush the queued build, then ask again
                MethodInfo finish = __instance.GetType().GetMethod("FinishPendingBuild",
                    BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                MethodInfo ensure = __instance.GetType().GetMethod("EnsureResident",
                    BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (finish == null || ensure == null) { Interlocked.Increment(ref _stillFail); return; }

                finish.Invoke(__instance, null);
                object again = ensure.Invoke(__instance, null);
                if (again is bool b && b)
                {
                    __result = true;
                    int n = Interlocked.Increment(ref _fixedCount);
                    if (n <= 10 || n % 200 == 0)
                        Refl.Info("resident-fix: finished pending build for '" + Name(__instance) + "' (fixed=" + n + ")");
                }
                else
                {
                    int m = Interlocked.Increment(ref _stillFail);
                    if (m <= 10 || m % 200 == 0)
                        Refl.Info("resident-fix: still not resident after flush: '" + Name(__instance) + "' (fail=" + m + ")");
                }
            }
            catch (Exception e)
            {
                if (Interlocked.Exchange(ref _warned, 1) == 0) Refl.Warn("resident-fix: " + e.Message);
            }
            finally { _inFix = false; }
        }

        private static string Name(object o)
        {
            try { return Prop(o, "name") as string ?? "?"; } catch { return "?"; }
        }

        private static object Field(object o, string n)
        {
            for (Type t = o.GetType(); t != null; t = t.BaseType)
            {
                FieldInfo f = t.GetField(n, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (f != null) return f.GetValue(o);
            }
            return null;
        }

        private static object Prop(object o, string n)
        {
            try
            {
                foreach (PropertyInfo p in o.GetType().GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    if (p.Name == n && p.GetIndexParameters().Length == 0) return p.GetValue(o, null);
            }
            catch { }
            return null;
        }

        internal static void Install()
        {
            Type t = Refl.T("Waylith.Rendering.Texture2D");
            if (t == null) { Refl.Warn("resident-fix: Texture2D not found"); return; }
            MethodInfo target = t.GetMethod("EnsureResident", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (target == null) { Refl.Warn("resident-fix: EnsureResident not found"); return; }
            try
            {
                MethodInfo post = typeof(ResidencyFix).GetMethod("Postfix", BindingFlags.Static | BindingFlags.NonPublic);
                new Harmony("sleepsheep.waylith.agenttouchfix.residentfix").Patch(target, null, new HarmonyMethod(post));
                Refl.Info("resident-fix: EnsureResident postfix installed (flush pending build, then retry once)");
            }
            catch (Exception e) { Refl.Warn("resident-fix: patch failed: " + e.Message); }
        }
    }
}
