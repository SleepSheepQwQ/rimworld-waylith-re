using System;
using System.Reflection;
using System.Threading;
using HarmonyLib;

namespace AgentTouchFix
{
    /// <summary>
    /// v13: the texture that is about to be sampled must actually be resident.
    ///
    /// Waylith.Rendering.Texture.EnsureSampledReady(Texture) (Texture.cs:97-110) only allocates a
    /// GPU resource when the texture CANNOT rehydrate:
    ///
    ///     else if (texture2D.GpuResource == null &amp;&amp; !texture2D.CanRehydrate)
    ///         texture2D.TryEnsureGpuAllocated();
    ///
    /// Every texture that came from disk has a SourcePath, so CanRehydrate is true and the method
    /// does nothing. If such a texture is not resident at draw time (deferred load, evicted, upload
    /// still queued) the draw binds a null GpuResource and the frame shows the white fallback -
    /// exactly the reported "only an empty frame, sometimes a half-uploaded image".
    ///
    /// This prefix makes the sampling path force Texture2D.EnsureResident() first (synchronous
    /// upload from CPU data or from the disk cache). Purely additive: if the texture is already
    /// resident it returns immediately, and every reflection failure means "do nothing".
    /// </summary>
    internal static class SampledReady
    {
        private static int _uploaded;
        private static int _failed;
        private static int _warned;
        private static PropertyInfo _pRes;
        private static MethodInfo _mEnsure;
        private static Type _cachedType;

        internal static void Prefix(object texture)
        {
            if (texture == null) return;
            try
            {
                Type t = texture.GetType();
                if (!ReferenceEquals(t, _cachedType))
                {
                    _cachedType = t;
                    _pRes = t.GetProperty("GpuResource", BindingFlags.Public | BindingFlags.Instance);
                    _mEnsure = t.GetMethod("EnsureResident", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                }
                if (_pRes == null || _mEnsure == null)
                {
                    WarnOnce("GpuResource/EnsureResident not resolvable on " + t.Name);
                    return;
                }
                if (_pRes.GetValue(texture, null) != null) return;   // already resident: nothing to do

                object ok = _mEnsure.Invoke(texture, null);
                bool fine = ok is bool b && b;
                if (fine) Interlocked.Increment(ref _uploaded);
                else Interlocked.Increment(ref _failed);

                int n = _uploaded + _failed;
                if (n == 1 || n % 500 == 0)
                    Refl.Info("sampled: forced resident=" + Volatile.Read(ref _uploaded)
                        + " still-null=" + Volatile.Read(ref _failed)
                        + " (first at " + t.Name + ")");
            }
            catch (Exception e)
            {
                WarnOnce("EnsureSampledReady prefix: " + e.Message);
            }
        }

        private static void WarnOnce(string m)
        {
            if (Interlocked.Exchange(ref _warned, 1) == 0) Refl.Warn("sampled: " + m);
        }

        internal static void Install()
        {
            Type t = Refl.T("Waylith.Rendering.Texture");
            if (t == null) { Refl.Warn("sampled: Waylith.Rendering.Texture not found"); return; }

            MethodInfo target = Refl.M(t, "EnsureSampledReady");
            if (target == null) { Refl.Warn("sampled: EnsureSampledReady not found"); return; }

            try
            {
                MethodInfo prefix = typeof(SampledReady).GetMethod("Prefix",
                    BindingFlags.Static | BindingFlags.NonPublic);
                Harmony h = new Harmony("sleepsheep.waylith.agenttouchfix.sampled");
                h.Patch(target, new HarmonyMethod(prefix), null);
                Refl.Info("sampled: patched Waylith.Rendering.Texture.EnsureSampledReady"
                    + " (force EnsureResident when GpuResource is null)");
            }
            catch (Exception e)
            {
                Refl.Warn("sampled: patch failed: " + e.Message);
            }
        }
    }
}
