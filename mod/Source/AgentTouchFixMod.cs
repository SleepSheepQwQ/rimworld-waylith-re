using System;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace AgentTouchFix
{
    /// <summary>Shared reflection helpers. No PortUI / RwHost / UnityEngine compile-time dependency.</summary>
    internal static class Refl
    {
        internal const string P = "[AgentTouchFix] ";

        internal static void Info(string m)
        {
            try { Log.Message(P + m); } catch { }
        }

        internal static void Warn(string m)
        {
            try { Log.Warning(P + m); } catch { }
        }

        internal static void Err(string m)
        {
            try { Log.Error(P + m); } catch { }
        }

        /// <summary>Resolve a type by full name, across all loaded assemblies (falls back to Assembly.Load).</summary>
        internal static Type T(string name)
        {
            try
            {
                Type t = AccessTools.TypeByName(name);
                if (t != null) return t;
            }
            catch { }

            Assembly[] asms;
            try { asms = AppDomain.CurrentDomain.GetAssemblies(); }
            catch { asms = new Assembly[0]; }

            for (int i = 0; i < asms.Length; i++)
            {
                try
                {
                    Type t = asms[i].GetType(name, false);
                    if (t != null) return t;
                }
                catch { }
            }

            string[] guess = { "Assembly-CSharp", "RwHost", "PortUI" };
            for (int i = 0; i < guess.Length; i++)
            {
                try
                {
                    Type t = Assembly.Load(guess[i]).GetType(name, false);
                    if (t != null) return t;
                }
                catch { }
            }
            return null;
        }

        internal static MethodInfo M(Type t, string name)
        {
            if (t == null) return null;
            try
            {
                MethodInfo mi = AccessTools.Method(t, name);
                if (mi != null) return mi;
            }
            catch { }
            try
            {
                MethodInfo[] all = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
                for (int i = 0; i < all.Length; i++)
                {
                    if (all[i].Name == name) return all[i];
                }
            }
            catch { }
            return null;
        }

        internal static FieldInfo F(Type t, string name)
        {
            if (t == null) return null;
            try
            {
                FieldInfo fi = AccessTools.Field(t, name);
                if (fi != null) return fi;
            }
            catch { }
            try { return t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static); }
            catch { return null; }
        }

        internal static PropertyInfo Prop(Type t, string name)
        {
            if (t == null) return null;
            try { return t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static); }
            catch { return null; }
        }

        /// <summary>Build a HarmonyMethod; priority 700 == HarmonyLib.Priority.High.</summary>
        internal static HarmonyMethod At(MethodInfo mi, int priority)
        {
            if (mi == null) return null;
            if (priority < 0) return new HarmonyMethod(mi);
            try { return new HarmonyMethod(mi, priority, null, null, null); }
            catch { return new HarmonyMethod(mi); }
        }
    }

    [StaticConstructorOnStartup]
    internal static class Bootstrap
    {
        internal const string HarmonyId = "sleepsheep.waylith.agenttouchfix";
        internal static readonly HarmonyLib.Harmony H = new HarmonyLib.Harmony(HarmonyId);

        static Bootstrap()
        {
            int ok = 0, skipped = 0, failed = 0;
            Refl.Info("AgentTouchFix " + (typeof(Bootstrap).Assembly.GetName().Version) + " bootstrapping (harmony " + H.Id + ")");

            Add("RimWorld.Selector.HandleMapClicks", () => Refl.M(Refl.T("RimWorld.Selector"), "HandleMapClicks"),
                ToolPatches.HandleMapClicks_Prefix, null, -1, ref ok, ref skipped, ref failed);

            Add("PortUI.MobileSelectionController.Tap", () => Refl.M(Refl.T("PortUI.MobileSelectionController"), "Tap"),
                ToolPatches.Tap_Prefix, null, -1, ref ok, ref skipped, ref failed);

            Add("Verse.UI.MouseCell", () => Refl.M(Refl.T("Verse.UI"), "MouseCell"),
                ToolPatches.MouseCell_Prefix, null, 700, ref ok, ref skipped, ref failed);

            Add("LudeonTK.DebugTool.DebugToolOnGUI", () => Refl.M(Refl.T("LudeonTK.DebugTool"), "DebugToolOnGUI"),
                ToolPatches.DebugToolOnGUI_Prefix, null, -1, ref ok, ref skipped, ref failed);

            Add("Ports1de.TouchGestureEngine.SecondUp", () => Refl.M(Refl.T("Ports1de.TouchGestureEngine"), "SecondUp"),
                null, PointerPatches.SecondUp_Postfix, -1, ref ok, ref skipped, ref failed);

            Add("RwHost.TouchControls.OnTouch", () => Refl.M(Refl.T("RwHost.TouchControls"), "OnTouch"),
                PointerPatches.OnTouch_Prefix, PointerPatches.OnTouch_Postfix, -1, ref ok, ref skipped, ref failed);

            string summary = "patch summary: ok=" + ok + " skipped=" + skipped + " failed=" + failed;
            if (failed > 0) Refl.Err(summary);
            else if (skipped > 0) Refl.Warn(summary);
            else Refl.Info(summary);
        }

        private static void Add(string label, Func<MethodBase> target, MethodInfo prefix, MethodInfo postfix, int priority,
            ref int ok, ref int skipped, ref int failed)
        {
            try
            {
                MethodBase original = target();
                if (original == null)
                {
                    Refl.Warn("skipped (target not found): " + label);
                    skipped++;
                    return;
                }
                if (prefix == null && postfix == null)
                {
                    Refl.Warn("skipped (no patch methods): " + label);
                    skipped++;
                    return;
                }
                H.Patch(original,
                    prefix == null ? null : Refl.At(prefix, priority),
                    postfix == null ? null : Refl.At(postfix, priority));
                Refl.Info("patched " + label + " (" + original.DeclaringType.FullName + "." + original.Name + ")");
                ok++;
            }
            catch (Exception e)
            {
                Refl.Err("patch FAILED " + label + ": " + e);
                failed++;
            }
        }
    }
}
