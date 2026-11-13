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
        private static bool _errLogged;

        // --- file sink ---------------------------------------------------------
        // One long-lived handle (FileMode.Append + FileShare.ReadWrite) with a reused StreamWriter,
        // so a line costs a write instead of an open/close pair. FileShare.ReadWrite keeps the log
        // readable with run-as/adb while the game is running.
        private const long MaxLogBytes = 1024L * 1024L;   // rotate at 1 MB
        private const int CheckEveryWrites = 200;         // ... checked at most this often ...
        private const int CheckEveryMs = 30000;           // ... or once per 30 s, whichever comes first
        private static readonly object _lock = new object();
        private static FileStream _fs;
        private static StreamWriter _w;
        private static int _writes;
        private static int _lastCheckTick = int.MinValue / 2;

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

        private static StreamWriter Writer()
        {
            if (_w != null) return _w;
            try
            {
                _fs = new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                _w = new StreamWriter(_fs);
                _w.AutoFlush = false;
                return _w;
            }
            catch
            {
                CloseSink();
                return null;
            }
        }

        private static void CloseSink()
        {
            try { if (_w != null) _w.Flush(); } catch { }
            try { if (_w != null) _w.Dispose(); } catch { }
            try { if (_fs != null) _fs.Dispose(); } catch { }
            _w = null;
            _fs = null;
        }

        /// <summary>
        /// 1 MB cap: rename to agenttouchfix.log.1 (replacing the previous one) and start a fresh
        /// file. Only probed every CheckEveryWrites lines or every CheckEveryMs, never per line.
        /// </summary>
        private static void MaybeRotate()
        {
            int now = Environment.TickCount;
            bool everyWrites = _writes > 0 && (_writes % CheckEveryWrites) == 0;
            if (!everyWrites && unchecked(now - _lastCheckTick) < CheckEveryMs) return;
            _lastCheckTick = now;

            long len;
            try
            {
                FileInfo fi = new FileInfo(LogPath);
                len = fi.Exists ? fi.Length : 0L;
            }
            catch { return; }
            if (len < MaxLogBytes) return;

            CloseSink();
            try
            {
                string bak = LogPath + ".1";
                if (File.Exists(bak)) File.Delete(bak);
                File.Move(LogPath, bak);
            }
            catch { }
        }

        private static void Sink(string level, string m)
        {
            try
            {
                lock (_lock)
                {
                    StreamWriter w = Writer();
                    if (w == null) return;
                    w.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + " " + level + " " + m);
                    w.Flush();     // per line, so a killed process never loses the last events
                    _writes++;
                    MaybeRotate();
                }
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

            // v6: the snap aggressiveness switch (always / auto / off), read once, same folder.
            SnapGeometry.ReadMode();

            // v9: the tooltip switch (park / suppress / off), read once, same folder.
            TipMode.ReadMode();

            // v10: the PortUI candidate switch (port / exact), read once, same folder. Independent
            // of the snap switch: snap only governs the developer tool path.
            PickMode.ReadMode();

            // v11: the texture-residency switch (grace / on / off) plus its frame window, read once,
            // same folder. grace is the default: only evictions of textures used within the last
            // agenttouchfix.texgrace frames are skipped.
            TexKeep.ReadMode();

            Refl.Info("AgentTouchFix " + (typeof(Bootstrap).Assembly.GetName().Version) + " bootstrapping (harmony " + H.Id + ")");

            Add("RimWorld.Selector.HandleMapClicks", () => Refl.M(Refl.TPrefer("Assembly-CSharp", "RimWorld.Selector"), "HandleMapClicks"),
                MI(typeof(ToolPatches), "HandleMapClicks_Prefix"), null, -1, ref ok, ref skipped, ref failed);

            Add("PortUI.MobileSelectionController.Tap", () => Refl.M(Refl.TPrefer("PortUI", "PortUI.MobileSelectionController"), "Tap"),
                MI(typeof(ToolPatches), "Tap_Prefix"), null, -1, ref ok, ref skipped, ref failed);

            // v10: pick=exact drops PortUI's 28.8 px / 5x5 radius search when the tapped cell itself
            // holds nothing selectable. Prefix only, and only that one branch is replaced - see
            // PickMode. Installed in both modes (in port mode it returns immediately) so the log
            // always shows which mode is live.
            Add("PortUI.MobileSelectionController.Candidates",
                () => Refl.M(Refl.TPrefer("PortUI", "PortUI.MobileSelectionController"), "Candidates"),
                PickPrefix(), null, -1, ref ok, ref skipped, ref failed);

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

            // v6: the classic chain (mobile-controls.txt without bit 2) never reaches the patch
            // above, so the tooltip suppressor would never see a touch there. v9 also parks the
            // cursor from the postfix, because the classic chain pushes its own Click() last.
            Add("RwHost.ClassicTouchControls.OnTouch", () => Refl.M(Refl.TPrefer("RwHost", "RwHost.ClassicTouchControls"), "OnTouch"),
                MI(typeof(TooltipPatches), "Touch_Prefix"), MI(typeof(TooltipPatches), "Touch_Postfix"),
                -1, ref ok, ref skipped, ref failed);

            // v9: NO AndroidMemoryAndGcGovernor patch of any kind. v7 skipped the second of the two
            // texture releases a pressure event produced and v8 answered TRIM_MEMORY_UI_HIDDEN(20)
            // with Request(1); both touched the texture/memory path and are gone for good. The grey /
            // flickering / scrambled textures were caused by the port itself (quality tier down to
            // Economy, a 32 MiB decoded-texture budget, BC sampling by software decode), so nothing
            // here may call RequestMemoryTrim, PushTexture or any governor method again.

            // v8 A/B: the host clears its mouse mirror on four lifecycle paths without forwarding a
            // release (SurfaceDestroyed, OnConfigurationChanged, OnWindowFocusChanged(false) - and
            // OnPause does not even clear it), and it pushes keys with no bookkeeping at all. Both
            // are released from a prefix on the same four methods, before the original runs.
            Add("RwHost.MainActivity.SurfaceDestroyed", () => InputReleasePatches.Target("SurfaceDestroyed", 1),
                MI(typeof(InputReleasePatches), "SurfaceDestroyed_Prefix"), null, -1, ref ok, ref skipped, ref failed);

            Add("RwHost.MainActivity.OnConfigurationChanged", () => InputReleasePatches.Target("OnConfigurationChanged", 1),
                MI(typeof(InputReleasePatches), "OnConfigurationChanged_Prefix"), null, -1, ref ok, ref skipped, ref failed);

            Add("RwHost.MainActivity.OnWindowFocusChanged", () => InputReleasePatches.Target("OnWindowFocusChanged", 1),
                MI(typeof(InputReleasePatches), "OnWindowFocusChanged_Prefix"), null, -1, ref ok, ref skipped, ref failed);

            Add("RwHost.MainActivity.OnPause", () => InputReleasePatches.Target("OnPause", 0),
                MI(typeof(InputReleasePatches), "OnPause_Prefix"), null, -1, ref ok, ref skipped, ref failed);

            Add("RwHost.MainActivity.OnKeyDown", () => InputReleasePatches.Target("OnKeyDown", 2),
                MI(typeof(InputReleasePatches), "OnKeyDown_Prefix"), null, -1, ref ok, ref skipped, ref failed);

            Add("RwHost.MainActivity.OnKeyUp", () => InputReleasePatches.Target("OnKeyUp", 2),
                MI(typeof(InputReleasePatches), "OnKeyUp_Prefix"), null, -1, ref ok, ref skipped, ref failed);

            // v8 C: ACTION_UP forces num = 0, i.e. it releases every held mouse button. Only the
            // taken-over case (ACTION_UP whose ButtonState still names other buttons) skips the
            // original; every other event runs it untouched.
            Add("RwHost.MainActivity.MouseEvent", () => InputReleasePatches.MouseEventTarget(),
                MI(typeof(InputReleasePatches), "MouseEvent_Prefix"), null, -1, ref ok, ref skipped, ref failed);

            // v7 C: the IME answer is dropped whenever the field's TextEditTarget changed geometry
            // between opening the editor and the callback (Chinese input hits this almost every
            // time). Postfix only: the exact match keeps priority, the relaxed one is same-window,
            // single-shot.
            Add("PortUI.Ports1de.TextEditMailbox.TryTake",
                () => Refl.M(TextInputPatches.MailboxType(), "TryTake"),
                null, MI(typeof(TextInputPatches), "TryTake_Postfix"), -1, ref ok, ref skipped, ref failed);

            // v7 B needs no patch of its own: MobileSelectionController.Tap is already patched above
            // and ToolPatches.Tap_Prefix now also takes over a building double tap.

            string summary = "patch summary: ok=" + ok + " skipped=" + skipped + " failed=" + failed;
            // v5: silent by default. Only a real patch failure reaches the in-game dialog, and only
            // once per process; everything else lives in agenttouchfix.log.
            if (failed > 0) Refl.ErrOnce(summary);
            else Refl.Info(summary);

            // v6: tooltips are registered from the GUI layer, so they need their own patches. A
            // failure here must never take the tool/pointer fixes down with it.
            try { TooltipPatches.Install(); }
            catch (Exception e) { Refl.Err("tooltip install failed: " + e); }

            // v26: the only render-path patch: if EnsureResident() fails because a texture build is
            // merely pending, flush that build and retry once (Texture2D.cs:342 branch (b)).
            try { ResidencyFix.Install(); }
            catch (Exception e) { Refl.ErrOnce("resident-fix install failed: " + e); }

            // v27: which stage of the material draw drops these tiles (read-only counters).
            try { MaterialDrawProbe.Install(); }
            catch (Exception e) { Refl.ErrOnce("matdraw probe install failed: " + e); }

            // v28: a NULL pipeline must not stay cached (it is cached as-is and then every frame
            // returns false, so the tile can never be drawn again).
            try { PipelineRetry.Install(); }
            catch (Exception e) { Refl.ErrOnce("pipeline-retry install failed: " + e); }

            // v3-diagnostic: read-only instrumentation (DiagPatches.cs). Behaviour is unchanged;
            // these prefixes only append log lines, and they do nothing at all unless
            // files/game/agenttouchfix.diag exists. Runs last so the fix summary is never buried.
            Refl.Info("BUILD v28-piperetry (2.1.3) - input layer only (tool click + snap, pick, double-tap select-all, IME, focus-loss release, ACTION_UP fix, SecondUp reset, tooltip park). ONE render patch: EnsureResident() flushes a pending build and retries once (that branch made build icons blank/flicker). No eviction/atlas/upload/cache/governor patch.");
            try { Diag.Install(); }
            catch (Exception e) { Refl.ErrOnce("diag install failed: " + e); }

            // v10: read-only texture counters. Always installed (not gated by the diag flag), and

            // v11: the eviction gate. Installed last, after the read-only Evict counter, and it only










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

        /// <summary>
        /// PortUI.MobileSelectionController.Candidates(Vector3) -> List&lt;object&gt;. Same trick as
        /// MouseMapPositionPrefix: the parameter is UnityEngine.Vector3, which this mod refuses to
        /// reference at compile time, so PickMode.Candidates_Prefix&lt;T&gt; is closed over the live
        /// parameter type here and really takes '(UnityEngine.Vector3, ref List&lt;object&gt;)'.
        /// </summary>
        private static MethodInfo PickPrefix()
        {
            try
            {
                MethodBase target = Refl.M(Refl.TPrefer("PortUI", "PortUI.MobileSelectionController"), "Candidates");
                if (target == null) return null;
                ParameterInfo[] ps = target.GetParameters();
                if (ps.Length != 1)
                {
                    Refl.Warn("pick: Candidates has " + ps.Length + " parameters (1 expected) - pick switch not installed");
                    return null;
                }
                MethodInfo open = typeof(PickMode).GetMethod("Candidates_Prefix",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (open == null || !open.IsGenericMethodDefinition)
                {
                    Refl.Warn("pick: Candidates_Prefix missing or not generic - pick switch not installed");
                    return null;
                }
                MethodInfo closed = open.MakeGenericMethod(ps[0].ParameterType);
                Refl.Info("pick: Candidates prefix closed over " + ps[0].ParameterType.FullName);
                return closed;
            }
            catch (Exception e)
            {
                Refl.Warn("pick: Candidates prefix could not be built: " + e.Message);
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
