using System;
using System.Reflection;
using HarmonyLib;

namespace AgentTouchFix
{
    /// <summary>
    /// v9: files/game/agenttouchfix.tips decides what happens to tooltips under a finger.
    ///
    ///   park     (default) - after the last finger goes up the virtual cursor is pushed off
    ///                        screen, so nothing under the tap stays hovered; the TipRegion
    ///                        suppression below stays installed as a second line of defence.
    ///   suppress           - only the TipRegion suppression (no cursor movement).
    ///   off                - no suppression at all; the TipRegion prefix is installed but returns
    ///                        immediately (pass-through), and the cursor is never moved.
    ///
    /// Read once from Bootstrap, before the first Repaint can register a tip.
    /// </summary>
    internal static class TipMode
    {
        internal const int Off = 0;
        internal const int Suppress = 1;
        internal const int Park = 2;

        /// <summary>Same folder as agenttouchfix.log / agenttouchfix.diag / agenttouchfix.snap.</summary>
        internal const string ModePath = "/data/data/com.ports1de.waylith/files/game/agenttouchfix.tips";

        private static int _mode = -1;          // -1 = not read yet

        internal static int Mode
        {
            get { return _mode < 0 ? Park : _mode; }
        }

        internal static string ModeName(int m)
        {
            return m == Off ? "off" : (m == Suppress ? "suppress" : "park");
        }

        private static string Short(string s)
        {
            if (s == null) return "";
            s = s.Trim();
            return s.Length <= 20 ? s : s.Substring(0, 20) + "...";
        }

        internal static void ReadMode()
        {
            string raw = null;
            try
            {
                if (System.IO.File.Exists(ModePath)) raw = System.IO.File.ReadAllText(ModePath);
            }
            catch (Exception e)
            {
                Refl.Warn("tips switch unreadable (" + ModePath + "): " + e.Message);
            }

            string s = raw == null ? "" : raw.Trim().ToLowerInvariant();
            int m = Park;
            if (s.StartsWith("park")) m = Park;
            else if (s.StartsWith("suppress")) m = Suppress;
            else if (s.StartsWith("off")) m = Off;
            else if (s.Length > 0) Refl.Warn("tips switch '" + Short(s) + "' not understood (park/suppress/off) - using park");

            _mode = m;
            Refl.Info("tips mode=" + ModeName(m)
                + (raw == null ? " (no " + ModePath + ", default park)" : " (" + ModePath + " = '" + Short(s) + "')"));
        }
    }

    /// <summary>
    /// v6/v9: the touch pointer ends up parked wherever the last tap landed (the port's
    /// TouchControls.Translate calls Click(), which pushes the cursor position and never moves it
    /// away), so Verse.TooltipHandler's gate `Mouse.IsOver(rect)` stays true and every widget under
    /// that point keeps re-registering its tip.
    ///
    /// v9 replaces the old "cursor has been sitting still for 300 ms" inference with what the touch
    /// layer actually reports:
    ///
    ///   * no touch event for TouchModeMs        -> not a touch, nothing is suppressed (desktop)
    ///   * finger held for HoldMs or longer      -> allowed: the user asked for the description
    ///   * released after such a hold            -> allowed for PostAllowMs, so it does not blink
    ///   * SuppressMs after the last up/cancel   -> suppressed
    ///   * afterwards, while the cursor has not moved since the previous sample -> still parked,
    ///     still suppressed (this is a movement test, not a dwell-time threshold)
    ///
    /// A suppressed call returns false from the Harmony prefix and does *nothing* else: no
    /// Event.current.Use(), no GUI state change. Exceptions fail open (tip shown) and go to the log.
    /// </summary>
    internal static class TouchActivity
    {
        // No touch event this recent means "the finger is not the input device right now".
        internal const long TouchModeMs = 2000L;
        // Every tap hides tooltips for at least this long after the finger leaves the screen ...
        internal const long SuppressMs = 2000L;
        // ... and a hold this long explicitly re-enables them ...
        internal const long HoldMs = 500L;
        // ... which then stays enabled this long after the release, so the tip does not blink.
        internal const long PostAllowMs = 800L;

        // Verdict() results. Suppress is the only one the prefix acts on; the rest are counters.
        internal const int VerdictSuppress = 0;
        internal const int VerdictHold = 1;
        internal const int VerdictMouse = 2;
        internal const int VerdictIdle = 3;

        private static long _lastEventMs = -100000L;
        private static long _lastTapMs = -100000L;
        private static long _downMs = -100000L;
        private static long _releaseMs = -100000L;
        private static bool _down;
        private static bool _releasedAfterHold;

        private static long Now()
        {
            try { return Environment.TickCount64; }
            catch { return 0L; }
        }

        /// <summary>
        /// Fed from both touch chains: RwHost.TouchControls.OnTouch (modern, PointerPatches) and
        /// RwHost.ClassicTouchControls.OnTouch (TooltipPatches.Touch_Prefix). Timestamps only - no
        /// logging, no allocation, no reflection beyond the cached MotionEvent accessors.
        /// </summary>
        internal static void Note(object motionEvent)
        {
            if (motionEvent == null) return;
            try
            {
                int action = Motion.ActionMasked(motionEvent);
                long now = Now();
                _lastEventMs = now;
                if (action == 0)                       // ACTION_DOWN
                {
                    _down = true;
                    _downMs = now;
                    _releasedAfterHold = false;
                }
                else if (action == 1 || action == 3)   // ACTION_UP / ACTION_CANCEL
                {
                    if (_down) _releasedAfterHold = now - _downMs >= HoldMs;
                    _down = false;
                    _releaseMs = now;
                    _lastTapMs = now;
                }
                else if (action == 6)                  // ACTION_POINTER_UP: a finger left
                {
                    _lastTapMs = now;
                }
            }
            catch { }
        }

        /// <summary>True while the finger is down and has been down for at least HoldMs.</summary>
        internal static bool Holding()
        {
            return _down && Now() - _downMs >= HoldMs;
        }

        /// <summary>Decide what to do with the tip that is asking to be registered right now.</summary>
        internal static int Verdict()
        {
            int mode = TipMode.Mode;
            if (mode == TipMode.Off) return VerdictIdle;      // pass-through, nothing installed matters

            long now = Now();

            // Fast path, and the one that matters on desktop: no touch for 2 s.
            if (now - _lastEventMs > TouchModeMs) return VerdictMouse;

            // Finger still down: the description is allowed once the hold is deliberate.
            if (_down) return (now - _downMs >= HoldMs) ? VerdictHold : VerdictSuppress;

            // Just released after a deliberate hold: keep it on screen for a moment.
            if (_releasedAfterHold && now - _releaseMs < PostAllowMs) return VerdictHold;

            if (now - _lastTapMs < SuppressMs) return VerdictSuppress;

            // Past the window: the tip comes back as soon as the pointer is not sitting where the
            // touch left it. A real mouse that moves ends the suppression immediately.
            return CursorStill() ? VerdictSuppress : VerdictIdle;
        }

        // ---------------------------------------------------------------- cursor movement

        private static bool _mouseResolved;
        private static PropertyInfo _pMousePos;      // UnityEngine.Input.mousePosition (Vector2)
        private static PropertyInfo _pFrameCount;    // UnityEngine.Time.frameCount
        private static bool _mouseWarned;

        private static bool _haveSample;
        private static float _lastMx, _lastMy;

        private static int _sampleFrame = int.MinValue;
        private static long _sampleMs = -100000L;
        private static bool _still = true;

        private static void ResolveInput()
        {
            if (_mouseResolved) return;
            _mouseResolved = true;
            Type input = Refl.T("UnityEngine.Input");
            if (input == null) { Refl.Warn("UnityEngine.Input not found - cursor movement test disabled (fixed 2 s window only)"); return; }
            _pMousePos = Refl.Prop(input, "mousePosition");
            if (_pMousePos == null) { Refl.Warn("Input.mousePosition not found - cursor movement test disabled"); return; }
            Type time = Refl.T("UnityEngine.Time");
            if (time != null) _pFrameCount = Refl.Prop(time, "frameCount");
        }

        /// <summary>
        /// Has the pointer moved since the previous sample? Cached per frame (or per 50 ms when
        /// Time.frameCount is unavailable): TipRegion is called for dozens of widgets per Repaint.
        /// "Still" is the definition of parked - no dwell threshold is involved.
        /// </summary>
        private static bool CursorStill()
        {
            ResolveInput();
            if (_pMousePos == null) return true;      // cannot tell: keep suppressing during touch mode

            int frame = int.MinValue;
            try { if (_pFrameCount != null) frame = Convert.ToInt32(_pFrameCount.GetValue(null, null)); }
            catch { }

            long now = Now();
            if (frame != int.MinValue)
            {
                if (frame == _sampleFrame) return _still;
                _sampleFrame = frame;
            }
            else
            {
                if (now - _sampleMs < 50L) return _still;
                _sampleMs = now;
            }

            try
            {
                object v = _pMousePos.GetValue(null, null);
                if (v == null) { _still = true; return true; }
                Type t = v.GetType();
                FieldInfo fx = t.GetField("x", BindingFlags.Public | BindingFlags.Instance);
                FieldInfo fy = t.GetField("y", BindingFlags.Public | BindingFlags.Instance);
                if (fx == null || fy == null) { _still = true; return true; }

                float mx = Convert.ToSingle(fx.GetValue(v));
                float my = Convert.ToSingle(fy.GetValue(v));

                bool first = !_haveSample;
                _haveSample = true;
                _still = first || (Math.Abs(mx - _lastMx) <= 1f && Math.Abs(my - _lastMy) <= 1f);
                _lastMx = mx;
                _lastMy = my;
                return _still;
            }
            catch (Exception e)
            {
                if (!_mouseWarned) { _mouseWarned = true; Refl.Warn("mousePosition read failed: " + e.Message); }
                _still = true;
                return true;
            }
        }

        // ---------------------------------------------------------------- bookkeeping

        private static long _suppressed, _allowedHold, _allowedMouse, _allowedIdle;
        private static long _lastReportMs = -100000L;

        /// <summary>Count one decision and emit the (rate limited) tips line.</summary>
        internal static void Count(int verdict)
        {
            switch (verdict)
            {
                case VerdictSuppress: _suppressed++; break;
                case VerdictHold: _allowedHold++; break;
                case VerdictMouse: _allowedMouse++; break;
                default: _allowedIdle++; break;
            }

            long now = Now();
            if (_lastReportMs > 0 && now - _lastReportMs < 10000L) return;   // one line per 10 s at most
            _lastReportMs = now < 0 ? 1 : now;

            Refl.Info("tips mode=" + TipMode.ModeName(TipMode.Mode)
                + " suppressed=" + _suppressed
                + " allowed=hold/real-mouse hold=" + _allowedHold
                + " real-mouse=" + _allowedMouse
                + " idle=" + _allowedIdle
                + (TipMode.Mode == TipMode.Park ? " parked=" + CursorPark.Parks : ""));
        }
    }

    /// <summary>
    /// v9 park: after the last finger leaves the screen nothing on the map/UI should stay hovered,
    /// but the port leaves the virtual cursor sitting on the tap point. Push it off screen instead
    /// (Native.glfwAndroidPushCursorPos(-1, -1) - the host takes double, resolved at runtime so the
    /// mod keeps zero RwHost/UnityEngine references).
    ///
    /// Enabled in TipMode.Park only. One push per frame at most: the classic chain calls into the
    /// modern one, so both postfixes see the same ACTION_UP.
    /// </summary>
    internal static class CursorPark
    {
        private static bool _resolved;
        private static MethodInfo _mPush;      // RwHost.Native.glfwAndroidPushCursorPos(double, double)
        private static Type _tParam0, _tParam1;
        private static bool _warned;

        private static PropertyInfo _pFrameCount;
        private static bool _frameResolved;

        private static int _lastFrame = int.MinValue;
        private static long _parks;
        private static long _lastLogMs = -100000L;

        internal static long Parks { get { return _parks; } }

        private static bool Resolve()
        {
            if (_resolved) return _mPush != null;
            _resolved = true;
            try
            {
                Type native = Refl.TPrefer("RwHost", "RwHost.Native");
                if (native != null) _mPush = InputReleasePatches.FindMethod(native, "glfwAndroidPushCursorPos", 2);
                if (_mPush != null)
                {
                    ParameterInfo[] ps = _mPush.GetParameters();
                    _tParam0 = ps[0].ParameterType;
                    _tParam1 = ps[1].ParameterType;
                }
            }
            catch (Exception e) { Refl.Warn("glfwAndroidPushCursorPos resolve failed: " + e.Message); }

            if (_mPush == null)
                Refl.Warn("RwHost.Native.glfwAndroidPushCursorPos not found - tips mode=park cannot move the cursor (suppression stays active)");
            return _mPush != null;
        }

        private static int Frame()
        {
            if (!_frameResolved)
            {
                _frameResolved = true;
                Type t = Refl.T("UnityEngine.Time");
                if (t != null) _pFrameCount = Refl.Prop(t, "frameCount");
            }
            if (_pFrameCount != null)
            {
                try { return (int)_pFrameCount.GetValue(null, null); }
                catch { _pFrameCount = null; }
            }
            return Environment.TickCount / 16;
        }

        private static object Conv(double v, Type t)
        {
            if (t == null || t == typeof(double)) return v;
            try { return Convert.ChangeType(v, t); }
            catch { return v; }
        }

        /// <summary>
        /// Called from the OnTouch postfixes of both chains, after the original handler has already
        /// pushed the click at the tap point.
        /// </summary>
        internal static void OnGestureEvent(object motionEvent)
        {
            if (motionEvent == null) return;
            if (TipMode.Mode != TipMode.Park) return;

            try
            {
                int action = Motion.ActionMasked(motionEvent);
                // ACTION_CANCEL, or ACTION_UP with no other finger left. For ACTION_UP the event's
                // PointerCount still includes the lifting pointer, so "last finger" is <= 1.
                bool lastUp = action == 3 || (action == 1 && Motion.PointerCount(motionEvent) <= 1);
                if (!lastUp) return;

                int frame = Frame();
                if (frame == _lastFrame) return;      // one push per frame at most
                if (!Resolve()) return;
                _lastFrame = frame;

                _mPush.Invoke(null, new object[] { Conv(-1.0, _tParam0), Conv(-1.0, _tParam1) });
                _parks++;

                long now = Environment.TickCount64;
                if (now - _lastLogMs >= 10000L)
                {
                    _lastLogMs = now;
                    Refl.Info("tips mode=park parked=" + _parks
                        + " (cursor pushed to (-1,-1) after the last finger went up, so nothing stays hovered)");
                }
            }
            catch (Exception e)
            {
                if (!_warned) { _warned = true; Refl.Warn("cursor park failed (falling back to suppression only): " + e.Message); }
            }
        }
    }

    /// <summary>Harmony layer for the touch tooltip suppression.</summary>
    internal static class TooltipPatches
    {
        /// <summary>
        /// Shared prefix for every TooltipHandler.TipRegion / TipRegionByKey overload.
        /// No parameters on purpose: the overloads differ in their signatures (Rect+TipSignal,
        /// Rect+Func&lt;string&gt;+int, Rect+string+NamedArgument...), and a parameterless prefix binds
        /// to all of them. Returns false to skip *only* the registration - Event.current is never
        /// touched, so the surrounding GUI keeps working. With tips=off it is a pass-through.
        /// </summary>
        internal static bool Suppress_Prefix()
        {
            try
            {
                if (TipMode.Mode == TipMode.Off) return true;
                int verdict = TouchActivity.Verdict();
                TouchActivity.Count(verdict);
                return verdict != TouchActivity.VerdictSuppress;
            }
            catch (Exception e)
            {
                Refl.Err("tooltip prefix: " + e.Message);
                return true;    // fail open: a tooltip is better than a broken UI
            }
        }

        /// <summary>Timestamp source for the second (classic) touch chain. No behaviour change.</summary>
        internal static bool Touch_Prefix(object[] __args)
        {
            try
            {
                if (__args != null && __args.Length > 0) TouchActivity.Note(__args[0]);
            }
            catch (Exception e) { Refl.Err("touch stamp prefix: " + e.Message); }
            return true;
        }

        /// <summary>
        /// v9: the classic chain runs its own Click() at the end of ACTION_UP, so the cursor has to be
        /// parked here, after the original handler. (TouchControls.OnTouch has its own postfix in
        /// PointerPatches; CursorPark drops the second call within the same frame.)
        /// </summary>
        internal static void Touch_Postfix(object[] __args)
        {
            try
            {
                if (__args != null && __args.Length > 0) CursorPark.OnGestureEvent(__args[0]);
            }
            catch (Exception e) { Refl.Err("touch park postfix: " + e.Message); }
        }

        /// <summary>
        /// Patch every TipRegion / TipRegionByKey overload on Verse.TooltipHandler. The patches are
        /// installed in every non-off mode; with tips=off the prefix is a pass-through.
        /// </summary>
        internal static void Install()
        {
            Type t = Refl.TPrefer("Assembly-CSharp", "Verse.TooltipHandler");
            if (t == null)
            {
                Refl.Warn("Verse.TooltipHandler not found - tooltips not suppressed");
                return;
            }

            MethodInfo prefix = null;
            try { prefix = typeof(TooltipPatches).GetMethod("Suppress_Prefix", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static); }
            catch { }
            if (prefix == null)
            {
                Refl.Warn("tooltip prefix method missing - tooltips not suppressed");
                return;
            }

            int ok = 0, failed = 0;
            MethodInfo[] all;
            try { all = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static); }
            catch (Exception e) { Refl.Err("tooltip: cannot enumerate TooltipHandler: " + e.Message); return; }

            for (int i = 0; i < all.Length; i++)
            {
                MethodInfo m = all[i];
                if (m == null) continue;
                if (m.Name != "TipRegion" && m.Name != "TipRegionByKey") continue;
                if (m.IsGenericMethodDefinition) continue;
                try
                {
                    Bootstrap.H.Patch(m, Refl.At(prefix, 700), null);
                    ok++;
                }
                catch (Exception e)
                {
                    failed++;
                    Refl.Err("tooltip patch FAILED " + m.Name + "(" + m.GetParameters().Length + " args): " + e.Message);
                }
            }

            Refl.Info("tooltip: patched " + ok + "/" + (ok + failed) + " TipRegion+TipRegionByKey overload(s)"
                + (failed > 0 ? " FAILED=" + failed : "")
                + " - tips mode=" + TipMode.ModeName(TipMode.Mode)
                + (TipMode.Mode == TipMode.Park ? " (cursor parked on release)" : "")
                + ", hold >= " + TouchActivity.HoldMs + " ms allows the tip, release keeps it " + TouchActivity.PostAllowMs + " ms");
        }
    }
}
