using System;
using System.Reflection;
using System.Threading;
using HarmonyLib;

namespace AgentTouchFix
{
    /// <summary>
    /// v27: the pending-build branch is excluded (v26 logged nothing), so the material draw is
    /// failing later. ImGuiMaterialRenderer.TryRecord() has exactly four ways to return false and
    /// each one silently drops the tile:
    ///
    ///   var shader = draw.Material.Shader;  if (shader?.Program == null) return false;   // A
    ///   if (!TryGetPipeline(shader, renderPass, out sp)) return false;                    // B
    ///   if (!TryBuildMaterialSet(...)) return false;                                      // C
    ///   (TryResolveTextureBinding inside C -> GpuTextureService.TryGetSampledImageInfo)   // C'
    ///
    /// and ImGuiGuiBackend.TryResolveTexture() can still reject the image itself (D, logged by
    /// Unresolved). This probe counts A/B/C/D separately, with the shader name, so the frozen tile
    /// gets one specific label instead of another guess.
    /// </summary>
    internal static class MaterialDrawProbe
    {
        private static int _warned, _pipelineFail, _setFail, _unresolved;
        private static long _lastLog = -100000L;

        private static void Tick(string what, string detail)
        {
            long now;
            try { now = Environment.TickCount64; } catch { now = 0L; }
            if (now - _lastLog < 2000L) return;
            _lastLog = now;
            Refl.Info("matdraw: " + what + " " + detail
                + " (pipeline-fail=" + Volatile.Read(ref _pipelineFail)
                + " set-fail=" + Volatile.Read(ref _setFail)
                + " unresolved=" + Volatile.Read(ref _unresolved) + ")");
        }

        // B: TryGetPipeline
        internal static void PipelinePostfix(ref bool __result)
        {
            if (__result) return;
            Interlocked.Increment(ref _pipelineFail);
            Tick("pipeline-missing", "=> TryRecord aborts, tile not drawn");
        }

        // C: TryBuildMaterialSet
        internal static void SetPostfix(ref bool __result)
        {
            if (__result) return;
            Interlocked.Increment(ref _setFail);
            Tick("material-set-failed", "=> descriptor/texture binding rejected");
        }

        // D: Unresolved(Texture, reason)
        internal static void UnresolvedPrefix(object image, string reason)
        {
            try
            {
                Interlocked.Increment(ref _unresolved);
                string name = "?";
                try { name = image?.GetType().GetProperty("name")?.GetValue(image, null) as string ?? "?"; } catch { }
                Tick("image-unresolved", "'" + name + "' :: " + reason);
            }
            catch { }
        }

        internal static void Install()
        {
            try
            {
                Type renderer = Refl.T("RimWorldWaylith.Imgui.ImGuiMaterialRenderer");
                Type backend = Refl.T("RimWorldWaylith.Imgui.ImGuiGuiBackend");
                Harmony h = new Harmony("sleepsheep.waylith.agenttouchfix.matdraw");

                if (renderer != null)
                {
                    MethodInfo pipe = renderer.GetMethod("TryGetPipeline", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (pipe != null)
                        h.Patch(pipe, null, new HarmonyMethod(typeof(MaterialDrawProbe).GetMethod("PipelinePostfix", BindingFlags.Static | BindingFlags.NonPublic)));

                    MethodInfo set = renderer.GetMethod("TryBuildMaterialSet", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (set != null)
                        h.Patch(set, null, new HarmonyMethod(typeof(MaterialDrawProbe).GetMethod("SetPostfix", BindingFlags.Static | BindingFlags.NonPublic)));
                }

                if (backend != null)
                {
                    MethodInfo unres = null;
                    foreach (MethodInfo m in backend.GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public))
                    {
                        if (m.Name != "Unresolved") continue;
                        ParameterInfo[] ps = m.GetParameters();
                        if (ps.Length == 2 && ps[1].ParameterType == typeof(string)) { unres = m; break; }
                    }
                    if (unres != null)
                        h.Patch(unres, new HarmonyMethod(typeof(MaterialDrawProbe).GetMethod("UnresolvedPrefix", BindingFlags.Static | BindingFlags.NonPublic)), null);
                }

                Refl.Info("matdraw: probes installed (pipeline / material-set / unresolved)");
            }
            catch (Exception e)
            {
                if (Interlocked.Exchange(ref _warned, 1) == 0) Refl.Warn("matdraw: install failed: " + e.Message);
            }
        }
    }
}
