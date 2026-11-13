using System;
using System.Reflection;
using System.Text;
using System.Threading;
using HarmonyLib;

namespace AgentTouchFix
{
    /// <summary>
    /// v24: Texture2D.EnsureResident() is the gate every draw goes through.
    ///
    ///   public override bool EnsureResident()                       Texture2D.cs:342
    ///   {
    ///       if (GpuResource != null) return true;
    ///       if (CpuData == null || PendingBuild != null) return false;   // (a) no cpu data  (b) async build in flight
    ///       if (CpuData.HasData) { UploadCpuData(); return GpuResource != null; }
    ///       if (!TryUploadFromCache()) return false;                     // (c) cache miss
    ///       ...
    ///   }
    ///
    /// and GpuTextureService.TryGetSampledImageInfo() calls it before binding:
    ///   if (gpuResource == null &amp;&amp; texture.EnsureResident()) gpuResource = texture.GpuResource;
    ///   if (gpuResource == null) return false;      // -> ImGuiMaterialRenderer.TryResolveTextureBinding
    ///                                              //    -> TryRecord returns false -> the tile is not drawn at all
    ///
    /// So a blank/flickering icon is exactly "EnsureResident() returned false". This postfix records
    /// which of the three branches it was, with the texture name, size and source path.
    /// </summary>
    internal static class ResidencyProbe
    {
        private static int _warned, _fails;
        private static readonly System.Collections.Generic.HashSet<string> _seen =
            new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);

        internal static void Postfix(object __instance, ref bool __result)
        {
            if (__result || __instance == null) return;
            try
            {
                string name = "?", src = "?", dims = "?", branch;
                object cpu = null, pending = null, gpu = null;

                foreach (PropertyInfo p in __instance.GetType().GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (p.GetIndexParameters().Length != 0) continue;
                    switch (p.Name)
                    {
                        case "name": name = p.GetValue(__instance, null)?.ToString() ?? "?"; break;
                        case "width": dims = (p.GetValue(__instance, null) ?? "?") + "x"; break;
                        case "height": dims += p.GetValue(__instance, null) ?? "?"; break;
                        case "CpuData": cpu = p.GetValue(__instance, null); break;
                        case "SourcePath": src = p.GetValue(__instance, null)?.ToString() ?? "null"; break;
                        case "GpuResource": gpu = p.GetValue(__instance, null); break;
                    }
                }
                foreach (FieldInfo f in __instance.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    if (f.Name == "PendingBuild") pending = f.GetValue(__instance);

                if (pending != null) branch = "pending-build";
                else if (cpu == null) branch = "no-cpu-data";
                else branch = "has-cpu-but-upload-produced-nothing(或 cache miss)";

                string key = name + "|" + branch;
                bool first;
                lock (_seen) first = _seen.Add(key);
                int n = Interlocked.Increment(ref _fails);

                if (first || n <= 30)
                    Refl.Info("resident-fail: '" + name + "' " + dims + " src=" + src
                        + " gpu=" + (gpu != null) + " :: " + branch);

                if (n == 1 || n % 100 == 0)
                    Refl.Info("resident-fail: total=" + n + " distinct=" + _seen.Count);
            }
            catch (Exception e)
            {
                if (Interlocked.Exchange(ref _warned, 1) == 0) Refl.Warn("resident-fail probe: " + e.Message);
            }
        }

        internal static void Install()
        {
            Type t = Refl.T("Waylith.Rendering.Texture2D");
            if (t == null) { Refl.Warn("resident-fail: Texture2D not found"); return; }
            MethodInfo target = t.GetMethod("EnsureResident", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (target == null) { Refl.Warn("resident-fail: EnsureResident not found"); return; }
            try
            {
                MethodInfo post = typeof(ResidencyProbe).GetMethod("Postfix", BindingFlags.Static | BindingFlags.NonPublic);
                new Harmony("sleepsheep.waylith.agenttouchfix.residency").Patch(target, null, new HarmonyMethod(post));
                Refl.Info("resident-fail: postfix on Waylith.Rendering.Texture2D.EnsureResident");
            }
            catch (Exception e) { Refl.Warn("resident-fail: patch failed: " + e.Message); }
        }
    }
}
