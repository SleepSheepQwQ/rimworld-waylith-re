using System;
using System.Collections;
using System.Reflection;
using System.Threading;
using HarmonyLib;

namespace AgentTouchFix
{
    /// <summary>
    /// v28: a failed pipeline is cached forever, so the tile can never come back.
    ///
    ///   private bool TryGetPipeline(Shader shader, VkRenderPass renderPass, out ShaderPipeline sp)
    ///   {                                                     // ImGuiMaterialRenderer.cs:236
    ///       if (pipelines.TryGetValue(shader, out sp)) return !sp.Pipeline.IsNull();
    ///       ...
    ///       VkPipeline pipeline = (frag.IsNull() || vert.IsNull()) ? default : CreatePipeline(...);
    ///       sp = new ShaderPipeline(pipeline, layout, setLayout);
    ///       pipelines[shader] = sp;          // <-- cached even when pipeline is NULL
    ///       return !pipeline.IsNull();
    ///   }
    ///
    /// The first attempt happens while the ImGui render pass / shader modules are still settling, so
    /// it can legitimately fail. The result - a NULL pipeline - is stored in the dictionary and every
    /// later call hits the cache and returns false, which makes TryRecord() drop the draw: the tile
    /// stays blank forever (with a flicker while the first frames still tried).
    ///
    /// Only shaders used by UI *materials* take this path, which is exactly the reported split:
    /// plain icons draw fine, the ones with uiIconMaterial (CutoutComplexUI) stay empty.
    ///
    /// Fix: when the cached entry has no pipeline, evict it so the next frame tries again. Nothing
    /// else changes - same shader, same render pass, same pipeline state, no override of results.
    /// </summary>
    internal static class PipelineRetry
    {
        private static int _warned, _evicted, _stillNull;
        private static readonly System.Collections.Generic.HashSet<string> _seen =
            new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);

        internal static void Postfix(object __instance, object shader, ref bool __result)
        {
            if (__result || __instance == null || shader == null) return;
            try
            {
                IDictionary cache = Cache(__instance);
                if (cache == null) return;

                if (cache.Contains(shader)) cache.Remove(shader);

                string name = "?";
                try { name = shader.GetType().GetProperty("name")?.GetValue(shader, null) as string ?? "?"; } catch { }
                int n = Interlocked.Increment(ref _evicted);
                bool first;
                lock (_seen) first = _seen.Add(name);
                if (first || n <= 20 || n % 200 == 0)
                    Refl.Info("pipeline-retry: no pipeline for shader '" + name + "', cache entry dropped (retries=" + n + ")");
            }
            catch (Exception e)
            {
                if (Interlocked.Exchange(ref _warned, 1) == 0) Refl.Warn("pipeline-retry: " + e.Message);
            }
        }

        private static IDictionary Cache(object instance)
        {
            for (Type t = instance.GetType(); t != null; t = t.BaseType)
            {
                FieldInfo f = t.GetField("pipelines", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (f != null) return f.GetValue(instance) as IDictionary;
            }
            return null;
        }

        internal static void Install()
        {
            try
            {
                Type renderer = Refl.T("RimWorldWaylith.Imgui.ImGuiMaterialRenderer");
                if (renderer == null) { Refl.Warn("pipeline-retry: renderer not found"); return; }

                MethodInfo target = null;
                foreach (MethodInfo m in renderer.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
                    if (m.Name == "TryGetPipeline") { target = m; break; }
                if (target == null) { Refl.Warn("pipeline-retry: TryGetPipeline not found"); return; }

                MethodInfo post = typeof(PipelineRetry).GetMethod("Postfix", BindingFlags.Static | BindingFlags.NonPublic);
                new Harmony("sleepsheep.waylith.agenttouchfix.pipelineretry").Patch(target, null, new HarmonyMethod(post));
                Refl.Info("pipeline-retry: TryGetPipeline postfix installed (failed pipeline is no longer cached)");
            }
            catch (Exception e)
            {
                if (Interlocked.Exchange(ref _warned, 1) == 0) Refl.Warn("pipeline-retry: install failed: " + e.Message);
            }
        }
    }
}
