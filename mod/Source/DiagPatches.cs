using System;
using System.Reflection;

namespace AgentTouchFix
{
    /// <summary>
    /// v3-diagnostic build: READ-ONLY instrumentation for "one finger drag turns into a zoom".
    ///
    /// Every patch in here is a prefix that only reads state and appends a line to
    /// files/game/agenttouchfix.log (via Refl.Info). Nothing is written back, nothing is
    /// cancelled, no __result is touched - the fix patches in PointerPatches keep working
    /// exactly as before, the diagnostics just describe what reached them.
    ///
    /// Why these four sites:
    ///   RwHost.GameCamera.Add(dx, dy, zoom)  - the single accumulator for every pan/zoom the
    ///                                          port applies (TouchCamera.Add is a delegate that
    ///                                          points at it), so any zoom seen on screen passed
    ///                                          through here. The stack says who asked for it.
    ///   RwHost.TouchCamera.Add               - the call site every gesture branch uses; shows
    ///                                          whether the request came from classic or modern.
    ///   RwHost.ClassicTouchControls.OnTouch  - the live chain when mobile-controls.txt has bit2
    ///                                          (modern) clear. mode/pinch* fields tell whether a
    ///                                          single finger is being handled in Mode.Pinch.
    ///   RwHost.TouchControls.OnTouch         - the entry point; also confirms classic vs modern
    ///                                          and the engine state the modern chain is in.
    /// </summary>
    internal static class Diag
    {
        // ---------------------------------------------------------------- rate limits
        // Add: at most 5 lines per wall clock second (a pinch can emit one per frame).
        private static long _addSec = -1;
        private static int _addCount, _addDropped;
        // tcam.Add: same budget, tracked separately so one can't starve the other.
        private static long _tcamSec = -1;
        private static int _tcamCount, _tcamDropped;
        // MOVE events: at most one line per 100 ms per chain; every other action is always logged.
        private static long _classicMs = -100000L, _modernMs = -100000L;
        private static int _classicDropped, _modernDropped;

        private static long Ms()
        {
            try { return Environment.TickCount64; } catch { return 0L; }
        }

        private static bool Gate(ref long lastMs, long windowMs, ref int dropped)
        {
            long now = Ms();
            if (now - lastMs < windowMs) { dropped++; return false; }
            lastMs = now;
            return true;
        }

        private static bool SecGate(ref long sec, ref int count, int max)
        {
            long now = Ms();
            long s = now / 1000L;
            if (s != sec) { sec = s; count = 0; }
            if (count >= max) return false;
            count++;
            return true;
        }

        // ---------------------------------------------------------------- formatting
        private static float ToF(object o)
        {
            try { return o == null ? 0f : Convert.ToSingle(o); } catch { return 0f; }
        }

        private static string N(float v)
        {
            try { return v.ToString("0.####"); } catch { return "?"; }
        }

        private static object Read(FieldInfo f, object inst)
        {
            if (f == null || inst == null) return null;
            try { return f.GetValue(inst); } catch { return null; }
        }

        private static string S(object v)
        {
            if (v == null) return "?";
            try
            {
                if (v is float) return N((float)v);
                if (v is bool) return ((bool)v) ? "1" : "0";
                if (v is double) return ((double)v).ToString("0.####");
                return Convert.ToString(v);
            }
            catch { return "?"; }
        }

        private static string Yn(FieldInfo f) { return f == null ? "MISSING" : "ok"; }

        /// <summary>
        /// 3-4 caller frames, one line, ' | ' separated. Skips the runtime plumbing, our own
        /// diagnostic frames and GameCamera.Add itself (the interesting part is who called it).
        /// </summary>
        private static string Frames(int want)
        {
            string st;
            try { st = Environment.StackTrace; } catch { return " (no stack)"; }
            if (string.IsNullOrEmpty(st)) return " (no stack)";

            string[] lines = st.Split('\n');
            string outp = "";
            int taken = 0;
            for (int i = 0; i < lines.Length && taken < want; i++)
            {
                string l = lines[i].Trim();
                if (l.Length == 0) continue;
                if (l.IndexOf("System.Environment", StringComparison.Ordinal) >= 0) continue;
                if (l.IndexOf("get_StackTrace", StringComparison.Ordinal) >= 0) continue;
                if (l.IndexOf("AgentTouchFix.", StringComparison.Ordinal) >= 0) continue;
                if (l.IndexOf("GameCamera.Add (", StringComparison.Ordinal) >= 0) continue;
                if (l.IndexOf("GameCamera:Add (", StringComparison.Ordinal) >= 0) continue;
                if (l.IndexOf("HarmonyLib.", StringComparison.Ordinal) >= 0) continue;
                outp += " | " + l;
                taken++;
            }
            return outp.Length == 0 ? " (no stack)" : outp;
        }

        // ---------------------------------------------------------------- reflection state
        private static bool _classicDone, _modernDone;
        private static FieldInfo _fMode, _fPinchZoom, _fPinchDistance, _fPinchMoved, _fPinchOnUi;
        private static FieldInfo _fEngine, _fClassicMode;

        private static void ResolveClassic(object inst)
        {
            if (_classicDone) return;
            _classicDone = true;

            Type t = inst != null ? inst.GetType() : Refl.TPrefer("RwHost", "RwHost.ClassicTouchControls");
            if (t == null)
            {
                Refl.Warn("diag: RwHost.ClassicTouchControls not found - classic touch events will not be logged");
                return;
            }
            _fMode = Refl.F(t, "mode");
            _fPinchZoom = Refl.F(t, "pinchZoom");
            _fPinchDistance = Refl.F(t, "pinchDistance");
            _fPinchMoved = Refl.F(t, "pinchMoved");
            _fPinchOnUi = Refl.F(t, "pinchOnUi");

            Refl.Info("diag: ClassicTouchControls resolved in " + t.Assembly.GetName().Name
                + " mode=" + Yn(_fMode) + " pinchZoom=" + Yn(_fPinchZoom) + " pinchDistance=" + Yn(_fPinchDistance)
                + " pinchMoved=" + Yn(_fPinchMoved) + " pinchOnUi=" + Yn(_fPinchOnUi));
            if (_fMode == null) Refl.Warn("diag: ClassicTouchControls.mode not found - mode will print as ?");
        }

        private static void ResolveModern(object inst)
        {
            if (_modernDone) return;
            _modernDone = true;

            Type t = inst != null ? inst.GetType() : Refl.TPrefer("RwHost", "RwHost.TouchControls");
            if (t == null)
            {
                Refl.Warn("diag: RwHost.TouchControls not found - modern touch events will not be logged");
                return;
            }
            _fEngine = Refl.F(t, "engine");
            _fClassicMode = Refl.F(t, "classicMode");

            Type et = _fEngine != null ? _fEngine.FieldType : null;
            Refl.Info("diag: TouchControls resolved in " + t.Assembly.GetName().Name
                + " engine=" + (_fEngine == null ? "MISSING" : et.FullName)
                + " classicMode=" + Yn(_fClassicMode));
            if (_fEngine == null) Refl.Warn("diag: TouchControls.engine not found - engine state will print as ?");
        }

        // ================================================================ patch targets

        /// <summary>RwHost.GameCamera.Add(float dx, float dy, float zoom) - the camera accumulator.</summary>
        internal static void Add_Prefix(object[] __args)
        {
            try
            {
                if (__args == null || __args.Length < 3) return;
                float dx = ToF(__args[0]);
                float dy = ToF(__args[1]);
                float zoom = ToF(__args[2]);
                if (Math.Abs(zoom) <= 0.0001f) return;

                bool log = SecGate(ref _addSec, ref _addCount, 5);
                if (!log) { _addDropped++; return; }

                string tail = _addDropped > 0 ? " sup=" + _addDropped : "";
                _addDropped = 0;
                Refl.Info("Add dx=" + N(dx) + " dy=" + N(dy) + " zoom=" + N(zoom) + tail + " frames:" + Frames(4));
            }
            catch (Exception e) { Refl.Err("diag Add prefix: " + e.Message); }
        }

        /// <summary>RwHost.TouchCamera.Add - the forwarding entry every gesture branch calls.</summary>
        internal static void TcamAdd_Prefix(object[] __args)
        {
            try
            {
                if (__args == null || __args.Length < 3) return;
                float dx = ToF(__args[0]);
                float dy = ToF(__args[1]);
                float zoom = ToF(__args[2]);
                if (Math.Abs(zoom) <= 0.0001f) return;

                bool log = SecGate(ref _tcamSec, ref _tcamCount, 5);
                if (!log) { _tcamDropped++; return; }

                string tail = _tcamDropped > 0 ? " sup=" + _tcamDropped : "";
                _tcamDropped = 0;
                Refl.Info("tcam.Add dx=" + N(dx) + " dy=" + N(dy) + " zoom=" + N(zoom) + tail + " frames:" + Frames(3));
            }
            catch (Exception e) { Refl.Err("diag tcam.Add prefix: " + e.Message); }
        }

        /// <summary>RwHost.ClassicTouchControls.OnTouch(MotionEvent) - the live chain here.</summary>
        internal static void ClassicOnTouch_Prefix(object __instance, object[] __args)
        {
            try
            {
                if (__args == null || __args.Length == 0) return;
                object e = __args[0];
                if (e == null) return;

                int act = Motion.ActionMasked(e);
                int ptr = Motion.PointerCount(e);
                ResolveClassic(__instance);

                string mode = S(Read(_fMode, __instance));
                bool move = act == 2;
                // A single finger moving while the state machine still believes it is in a pinch
                // (or parked in Ignore) is exactly the reported symptom, so never throttle those.
                bool stuck = move && ptr < 2 && (mode == "Pinch" || mode == "Ignore");

                if (move && !stuck && !Gate(ref _classicMs, 100L, ref _classicDropped)) return;

                string line = "classic act=" + act + " ptr=" + ptr + " mode=" + mode
                    + " x=" + N(Motion.GetX(e, 0)) + " y=" + N(Motion.GetY(e, 0));
                if (ptr > 1) line += " x1=" + N(Motion.GetX(e, 1)) + " y1=" + N(Motion.GetY(e, 1));
                line += " pz=" + S(Read(_fPinchZoom, __instance))
                     + " pd=" + S(Read(_fPinchDistance, __instance))
                     + " pm=" + S(Read(_fPinchMoved, __instance))
                     + " pui=" + S(Read(_fPinchOnUi, __instance));
                if (stuck) line += mode == "Pinch" ? " STUCK-PINCH-SINGLE" : " STUCK-IGNORE-SINGLE";
                if (_classicDropped > 0) { line += " sup=" + _classicDropped; _classicDropped = 0; }

                Refl.Info(line);
            }
            catch (Exception ex) { Refl.Err("diag classic OnTouch prefix: " + ex.Message); }
        }

        /// <summary>RwHost.TouchControls.OnTouch(MotionEvent) - entry point, classic flag + engine state.</summary>
        internal static void ModernOnTouch_Prefix(object __instance, object[] __args)
        {
            try
            {
                if (__args == null || __args.Length == 0) return;
                object e = __args[0];
                if (e == null) return;

                int act = Motion.ActionMasked(e);
                int ptr = Motion.PointerCount(e);
                ResolveModern(__instance);

                bool move = act == 2;
                if (move && !Gate(ref _modernMs, 100L, ref _modernDropped)) return;

                object engine = Read(_fEngine, __instance);
                string line = "modern act=" + act + " ptr=" + ptr
                    + " state=" + PointerPatches.StateStr(engine)
                    + " classic=" + S(Read(_fClassicMode, __instance))
                    + " x=" + N(Motion.GetX(e, 0)) + " y=" + N(Motion.GetY(e, 0));
                if (ptr > 1) line += " x1=" + N(Motion.GetX(e, 1)) + " y1=" + N(Motion.GetY(e, 1));
                if (_modernDropped > 0) { line += " sup=" + _modernDropped; _modernDropped = 0; }

                Refl.Info(line);
            }
            catch (Exception ex) { Refl.Err("diag modern OnTouch prefix: " + ex.Message); }
        }

        // ================================================================ registration

        private static MethodInfo MI(string name)
        {
            try { return typeof(Diag).GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static); }
            catch { return null; }
        }

        private static void Patch(string label, Func<MethodBase> target, string prefixName, int priority,
            ref int ok, ref int skipped, ref int failed)
        {
            try
            {
                MethodBase original = target();
                MethodInfo prefix = MI(prefixName);
                if (original == null)
                {
                    Refl.Warn("diag: skipped (target not found): " + label);
                    skipped++;
                    return;
                }
                if (prefix == null)
                {
                    Refl.Warn("diag: skipped (prefix method missing): " + label);
                    skipped++;
                    return;
                }
                Bootstrap.H.Patch(original, Refl.At(prefix, priority), null);
                Refl.Info("diag: patched " + label + " -> " + original.DeclaringType.Assembly.GetName().Name
                    + "!" + original.DeclaringType.FullName + "." + original.Name);
                ok++;
            }
            catch (Exception e)
            {
                Refl.Err("diag: patch FAILED " + label + ": " + e);
                failed++;
            }
        }

        /// <summary>Called from Bootstrap after the seven fix patches: adds diagnostics only.</summary>
        internal static void Install()
        {
            int ok = 0, skipped = 0, failed = 0;

            // priority 800 (above Normal/High) so the read happens before PointerPatches resets anything
            Patch("RwHost.GameCamera.Add",
                () => Refl.M(Refl.TPrefer("RwHost", "RwHost.GameCamera"), "Add"),
                "Add_Prefix", 800, ref ok, ref skipped, ref failed);

            Patch("RwHost.TouchCamera.Add",
                () => Refl.M(Refl.TPrefer("RwHost", "RwHost.TouchCamera"), "Add"),
                "TcamAdd_Prefix", 800, ref ok, ref skipped, ref failed);

            Patch("RwHost.ClassicTouchControls.OnTouch",
                () => Refl.M(Refl.TPrefer("RwHost", "RwHost.ClassicTouchControls"), "OnTouch"),
                "ClassicOnTouch_Prefix", 800, ref ok, ref skipped, ref failed);

            Patch("RwHost.TouchControls.OnTouch (diag)",
                () => Refl.M(Refl.TPrefer("RwHost", "RwHost.TouchControls"), "OnTouch"),
                "ModernOnTouch_Prefix", 800, ref ok, ref skipped, ref failed);

            LogTouchOptions();

            string summary = "diag v3 summary: ok=" + ok + " skipped=" + skipped + " failed=" + failed;
            if (failed > 0) Refl.Err(summary);
            else if (skipped > 0) Refl.Warn(summary);
            else Refl.Info(summary);
        }

        /// <summary>One line: which control scheme the port actually read from mobile-controls.txt.</summary>
        private static void LogTouchOptions()
        {
            try
            {
                Type t = Refl.TPrefer("RwHost", "Ports1de.TouchPreferences") ?? Refl.T("Ports1de.TouchPreferences");
                PropertyInfo p = Refl.Prop(t, "Options");
                if (p == null)
                {
                    Refl.Warn("diag: Ports1de.TouchPreferences.Options not found - touch scheme not logged");
                    return;
                }
                int v = Convert.ToInt32(p.GetValue(null, null));
                Refl.Info("diag: touch options=" + v
                    + " guide=" + ((v & 1) != 0 ? 1 : 0)
                    + " modern=" + ((v & 2) != 0 ? 1 : 0)
                    + " inertia=" + ((v & 4) != 0 ? 1 : 0));
            }
            catch (Exception e) { Refl.Warn("diag: touch options read failed: " + e.Message); }
        }
    }
}
