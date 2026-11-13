using System;
using System.IO;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace AgentTouchFix
{
    /// <summary>Shared reflection helpers. No PortUI / RwHost / UnityEngine compile-time dependency.</summary>
    internal static class Refl
    {
        internal const string P = "[AgentTouchFix] ";

        // v5: the mod is silent by default. Everything goes to this file only - Verse.Log.Warning /
        // Verse.Log.Error open the in-game "there were errors" dialog, and Log.Message spams the
        // console, so neither is called unless the user opts in (see DiagOn below).
        internal const string LogPath = "/data/data/com.ports1de.waylith/files/game/agenttouchfix.log";
        internal const string DiagFlagPath = "/data/data/com.ports1de.waylith/files/game/agenttouchfix.diag";

        // 0 = off, 1 = on, -1 = not read yet (read exactly once, from Bootstrap).
        private static int _diagState = -1;
        private static bool _sessionFileChecked;
        private static bool _errLogged;

        /// <summary>
        /// True when files/game/agenttouchfix.diag exists. Read once per process (Bootstrap calls
        /// ReadDiagFlag); the lazy path here is only a safety net. When false the diagnostics do
        /// nothing at all - no Verse.Log, not even a file line.
        /// </summary>
        internal static bool DiagOn
        {
            get
            {
                int s = _diagState;
                if (s >= 0) return s == 1;
                bool on = false;
                try { on = File.Exists(DiagFlagPath); } catch { }
                _diagState = on ? 1 : 0;
                return on;
            }
        }

        /// <summary>Called once from Bootstrap, before any patch is installed.</summary>
        internal static void ReadDiagFlag()
        {
            bool on = false;
            try { on = File.Exists(DiagFlagPath); } catch { }
            _diagState = on ? 1 : 0;
            Sink("INFO", "diagnostics " + (on ? "ON (" + DiagFlagPath + " exists): INFO lines also mirrored to Log.Message" : "OFF (create " + DiagFlagPath + " to enable): file only, no Verse.Log"));
        }

        private static void Sink(string level, string m)
        {
            try
            {
                // Keep the file bounded: one truncation check per process, not per line.
                if (!_sessionFileChecked)
                {
                    _sessionFileChecked = true;
                    try
                    {
                        FileInfo fi = new FileInfo(LogPath);
                        if (fi.Exists && fi.Length > 512 * 1024) fi.Delete();
                    }
                    catch { }
                }
                File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss.fff") + " " + level + " " + m + "\n");
            }
            catch { }
        }

        /// <summary>File only. Console mirroring only when the diag flag is present.</summary>
        internal static void Info(string m)
        {
            Sink("INFO", m);
            if (_diagState == 1) { try { Log.Message(P + m); } catch { } }
        }

        /// <summary>File only - Verse.Log.Warning would pop the "warnings" dialog.</summary>
        internal static void Warn(string m)
        {
            Sink("WARN", m);
        }

        /// <summary>File only - used for every runtime (click / gesture) failure.</summary>
        internal static void Err(string m)
        {
            Sink("ERROR", m);
        }

        /// <summary>
        /// Bootstrap-only: a genuine patch failure still gets exactly one Verse.Log.Error (so the
        /// user knows the mod did not load) and every later call is file only.
        /// </summary>
        internal static void ErrOnce(string m)
        {
            Sink("ERROR", m);
            if (_errLogged) return;
            _errLogged = true;
            try { Log.Error(P + m); } catch { }
        }

        /// <summary>
        /// Resolve a type inside one specific assembly. RwHost.dll and PortUI.dll both contain a
        /// compiled copy of Ports1de.TouchGestureEngine (PortUI's copy has no callers), so name
        /// based lookup alone is ambiguous - always prefer the assembly that actually runs.
        /// </summary>
        internal static Type TIn(string asmName, string typeName)
        {
            try
            {
                Assembly a = Assembly.Load(asmName);
                if (a == null) return null;
                return a.GetType(typeName, false);
            }
            catch { return null; }
        }

        /// <summary>Assembly-qualified lookup first, then the ambiguity-prone global search.</summary>
        internal static Type TPrefer(string asmName, string typeName)
        {
            Type t = TIn(asmName, typeName);
            if (t != null) return t;
            t = T(typeName);
            if (t != null)
            {
                Warn("type not found in " + asmName + ", fell back to " + t.Assembly.GetName().Name + " for " + typeName);
            }
            return t;
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

            // v5: decides silence vs. diagnostics exactly once, before anything can log.
            Refl.ReadDiagFlag();

            Refl.Info("AgentTouchFix " + (typeof(Bootstrap).Assembly.GetName().Version) + " bootstrapping (harmony " + H.Id + ")");

            Add("RimWorld.Selector.HandleMapClicks", () => Refl.M(Refl.TPrefer("Assembly-CSharp", "RimWorld.Selector"), "HandleMapClicks"),
                MI(typeof(ToolPatches), "HandleMapClicks_Prefix"), null, -1, ref ok, ref skipped, ref failed);

            Add("PortUI.MobileSelectionController.Tap", () => Refl.M(Refl.TPrefer("PortUI", "PortUI.MobileSelectionController"), "Tap"),
                MI(typeof(ToolPatches), "Tap_Prefix"), null, -1, ref ok, ref skipped, ref failed);

            Add("Verse.UI.MouseCell", () => Refl.M(Refl.TPrefer("Assembly-CSharp", "Verse.UI"), "MouseCell"),
                MI(typeof(ToolPatches), "MouseCell_Prefix"), null, 700, ref ok, ref skipped, ref failed);

            Add("Verse.UI.MouseMapPosition", () => Refl.M(Refl.TPrefer("Assembly-CSharp", "Verse.UI"), "MouseMapPosition"),
                MouseMapPositionPrefix(), null, 700, ref ok, ref skipped, ref failed);

            Add("PortUI.QuickOrderWheel.Open", () => Refl.M(Refl.TPrefer("PortUI", "PortUI.QuickOrderWheel"), "Open"),
                MI(typeof(ToolPatches), "QuickOrderWheel_Prefix"), null, -1, ref ok, ref skipped, ref failed);

            Add("LudeonTK.DebugTool.DebugToolOnGUI", () => Refl.M(Refl.TPrefer("Assembly-CSharp", "LudeonTK.DebugTool"), "DebugToolOnGUI"),
                MI(typeof(ToolPatches), "DebugToolOnGUI_Prefix"), null, -1, ref ok, ref skipped, ref failed);

            // NOTE: Ports1de.TouchGestureEngine exists in BOTH RwHost.dll and PortUI.dll
            // (same source compiled twice, PortUI's copy unused). PointerPatches.EngineType()
            // pins the RwHost copy - the one TouchControls actually drives.
            Add("Ports1de.TouchGestureEngine.SecondUp", () => Refl.M(PointerPatches.EngineType(), "SecondUp"),
                null, MI(typeof(PointerPatches), "SecondUp_Postfix"), -1, ref ok, ref skipped, ref failed);

            Add("RwHost.TouchControls.OnTouch", () => Refl.M(PointerPatches.ControlsType(), "OnTouch"),
                MI(typeof(PointerPatches), "OnTouch_Prefix"), MI(typeof(PointerPatches), "OnTouch_Postfix"),
                -1, ref ok, ref skipped, ref failed);

            string summary = "patch summary: ok=" + ok + " skipped=" + skipped + " failed=" + failed;
            // v5: silent by default. Only a real patch failure reaches the in-game dialog, and only
            // once per process; everything else lives in agenttouchfix.log.
            if (failed > 0) Refl.ErrOnce(summary);
            else Refl.Info(summary);

            // v3-diagnostic: read-only instrumentation (DiagPatches.cs). Behaviour is unchanged;
            // these prefixes only append log lines, and they do nothing at all unless
            // files/game/agenttouchfix.diag exists. Runs last so the fix summary is never buried.
            Refl.Info("BUILD v5-quiet (1.3.0) - file-only logging, diagnostics opt-in via agenttouchfix.diag; tap snapping via PortUI.Candidates + MouseMapPosition pin unchanged");
            try { Diag.Install(); }
            catch (Exception e) { Refl.ErrOnce("diag install failed: " + e); }
        }

        /// <summary>
        /// UI.MouseMapPosition returns UnityEngine.Vector3. To keep the mod free of a compile-time
        /// UnityEngine reference (the CI guard rejects a UnityEngine assembly reference) the prefix is
        /// a generic method that is closed over the live return type here: the closed MethodInfo really
        /// takes 'ref UnityEngine.Vector3', which is what Harmony binds against.
        /// </summary>
        private static MethodInfo MouseMapPositionPrefix()
        {
            try
            {
                MethodBase target = Refl.M(Refl.TPrefer("Assembly-CSharp", "Verse.UI"), "MouseMapPosition");
                if (target == null) return null;
                MethodInfo open = typeof(ToolPatches).GetMethod("MouseMapPosition_Prefix",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (open == null || !open.IsGenericMethodDefinition)
                {
                    Refl.Warn("MouseMapPosition prefix method missing or not generic - map position not pinned");
                    return null;
                }
                Type ret = ((MethodInfo)target).ReturnType;
                MethodInfo closed = open.MakeGenericMethod(ret);
                Refl.Info("UI.MouseMapPosition prefix closed over " + ret.FullName);
                return closed;
            }
            catch (Exception e)
            {
                Refl.Warn("UI.MouseMapPosition prefix could not be built: " + e.Message);
                return null;
            }
        }

        private static MethodInfo MI(Type t, string name)
        {
            try
            {
                return t.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            }
            catch { return null; }
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
                    Refl.Warn("skipped (patch methods missing): " + label);
                    skipped++;
                    return;
                }
                H.Patch(original,
                    prefix == null ? null : Refl.At(prefix, priority),
                    postfix == null ? null : Refl.At(postfix, priority));
                Refl.Info("patched " + label + " -> " + original.DeclaringType.Assembly.GetName().Name + "!"
                    + original.DeclaringType.FullName + "." + original.Name);
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
