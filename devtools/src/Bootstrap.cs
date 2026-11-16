using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace AgentDevTools
{
    /// <summary>
    /// Entry point and the only place where patches are installed.
    ///
    /// Design rules enforced here:
    ///   * every patch is installed inside its own try/catch - one missing target must never take the
    ///     others (or the game) down;
    ///   * nothing is patched at all in mode "off";
    ///   * the summary line is written once, to the file, so the user can prove what is active.
    /// </summary>
    [StaticConstructorOnStartup]
    internal static class Bootstrap
    {
        private const string ModePath =
            "/data/data/com.ports1de.waylith/files/game/agentdevtools.mode";

        // created inside the try: a Harmony failure must degrade to "mod inert", never to a
        // TypeInitializationException escaping [StaticConstructorOnStartup]
        private static HarmonyLib.Harmony H;
        private const string HarmonyId = "sleepsheep.waylith.agentdevtools";
        private static bool _installed;

        private static int _ok, _failed;

        static Bootstrap()
        {
            try
            {
                if (_installed) { Refl.Info("already installed in this process, skipping"); return; }
                _installed = true;
                H = new HarmonyLib.Harmony(HarmonyId);

                int mode = ReadMode();
                Refl.Info("BUILD 1.2.1 / AgentDevTools " + typeof(Bootstrap).Assembly.GetName().Version
                          + " bootstrapping, mode=" + SnapPolicy.Name(mode));

                if (mode == SnapPolicy.Off)
                {
                    Refl.Info("mode=off: no patch installed, the port keeps its own behaviour");
                    return;
                }

                SnapGeometry.ReadMode(mode);
                PickMode.ReadMode();

                Patch("RimWorld.Selector.HandleMapClicks",
                      () => Refl.M(Refl.TPrefer("Assembly-CSharp", "RimWorld.Selector"), "HandleMapClicks",
                                   new string[0]),
                      MI(typeof(ToolPatches), "HandleMapClicks_Prefix"));

                Patch("PortUI.MobileSelectionController.Tap",
                      // strict first (Vector3, bool); if the port changed the signature, log every
                      // overload and fall back to a single 2-parameter match so the log shows what
                      // was actually bound instead of silently doing nothing.
                      () => TapTarget(),
                      MI(typeof(ToolPatches), "Tap_Prefix"));

                Patch("Verse.UI.MouseCell",
                      () => Refl.M(Refl.TPrefer("Assembly-CSharp", "Verse.UI"), "MouseCell",
                                   new string[0]),
                      MI(typeof(ToolPatches), "MouseCell_Prefix"));

                PatchClosed("Verse.UI.MouseMapPosition",
                      () => Refl.M(Refl.TPrefer("Assembly-CSharp", "Verse.UI"), "MouseMapPosition",
                                   new string[0]),
                      "MouseMapPosition_Prefix", typeof(ToolPatches));

                PatchClosed("PortUI.Candidates",
                      () => Refl.M(Refl.TPrefer("PortUI", "PortUI.MobileSelectionController"), "Candidates",
                                   new[] { "Vector3" }),
                      "Candidates_Prefix", typeof(PickMode));

                Refl.Info("AgentDevTools " + (_failed == 0 ? "ready" : "degraded")
                          + ": patches ok=" + _ok + " failed=" + _failed
                          + ", snap=" + SnapGeometry.ModeName(SnapGeometry.Mode)
                          + ", pick=" + PickMode.ModeName(PickMode.Mode));
            }
            catch (Exception e) { Refl.ErrOnce("bootstrap failed: " + e); }
        }

        /// <summary>agentdevtools.mode: "off" | "auto" (default) | "always".</summary>
        private static int ReadMode()
        {
            try
            {
                if (!System.IO.File.Exists(ModePath)) return SnapPolicy.Auto;
                string v = (System.IO.File.ReadAllText(ModePath) ?? "").Trim().ToLowerInvariant();
                if (v.StartsWith("off")) return SnapPolicy.Off;
                if (v.StartsWith("always")) return SnapPolicy.Always;
                return SnapPolicy.Auto;
            }
            catch { return SnapPolicy.Auto; }
        }

        private static MethodBase TapTarget()
        {
            Type t = Refl.TPrefer("PortUI", "PortUI.MobileSelectionController");
            if (t == null) return null;
            // 0.0.22: Tap(Vector3, Boolean, Boolean)
            MethodInfo strict = Refl.M(t, "Tap", new[] { "Vector3", "Boolean", "Boolean" });
            if (strict == null) strict = Refl.M(t, "Tap", new[] { "Vector3", "Boolean" });
            if (strict != null) return strict;

            Refl.Warn("Tap has no (Vector3, bool[, bool]) overload; overloads = " + Refl.Overloads(t, "Tap"));
            // last resort: any Tap whose first parameter is the position
            foreach (MethodInfo m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                                  BindingFlags.Instance | BindingFlags.Static))
            {
                if (m.Name != "Tap") continue;
                ParameterInfo[] ps = m.GetParameters();
                if (ps.Length >= 1 && ps[0].ParameterType.Name == "Vector3")
                {
                    Refl.Warn("falling back to " + Signature(m));
                    return m;
                }
            }
            return null;
        }

        private static string Signature(MethodBase m)
        {
            try
            {
                var ps = m.GetParameters();
                var sb = new System.Text.StringBuilder(m.Name).Append('(');
                for (int i = 0; i < ps.Length; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(ps[i].ParameterType.Name);
                }
                return sb.Append(')').ToString();
            }
            catch { return m.Name + "(?)"; }
        }

        private static MethodInfo MI(Type t, string name)
        {
            foreach (MethodInfo m in t.GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public))
                if (m.Name == name) return m;
            return null;
        }

        private static void Patch(string label, Func<MethodBase> target, MethodInfo prefix)
        {
            try
            {
                MethodBase t = target();
                if (t == null || prefix == null) { Refl.Warn("skip " + label + " (target or prefix missing)"); _failed++; return; }
                H.Patch(t, new HarmonyMethod(prefix));
                _ok++;
                Refl.Info("patched " + label + " -> " + Signature(t)
                          + " in " + t.DeclaringType.Assembly.GetName().Name);
            }
            catch (Exception e) { Refl.Warn("patch " + label + " failed: " + e.Message); _failed++; }
        }

        /// <summary>
        /// Harmony needs a prefix whose parameter types match the target exactly; the port's
        /// Vector3/IntVec3 live in assemblies this mod must not reference at compile time, so the
        /// prefix is generic and is closed over the live signature here.
        /// </summary>
        private static void PatchClosed(string label, Func<MethodBase> target, string prefixName, Type owner)
        {
            try
            {
                MethodBase t = target();
                if (t == null) { Refl.Warn("skip " + label + " (target missing)"); _failed++; return; }

                MethodInfo open = null;
                foreach (MethodInfo m in owner.GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public))
                    if (m.Name == prefixName && m.IsGenericMethodDefinition) { open = m; break; }
                if (open == null) { Refl.Warn("skip " + label + " (generic prefix missing)"); _failed++; return; }

                ParameterInfo[] ps = ((MethodInfo)t).GetParameters();
                Type arg = ps.Length > 0 ? ps[0].ParameterType : ((MethodInfo)t).ReturnType;
                MethodInfo closed = open.MakeGenericMethod(arg);
                H.Patch(t, new HarmonyMethod(closed));
                _ok++;
                Refl.Info("patched " + label + " (closed over " + arg.Name + ")");
            }
            catch (Exception e) { Refl.Warn("patch " + label + " failed: " + e.Message); _failed++; }
        }
    }
}
