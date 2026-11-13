using System;
using System.Reflection;
using HarmonyLib;

namespace AgentTouchFix
{
    /// <summary>
    /// v6: the touch pointer stays parked wherever the last tap landed (the port calls
    /// glfwAndroidPushCursorPos on every tap), so Verse.TooltipHandler's gate
    /// `Mouse.IsOver(rect)` is true forever and every button under that point keeps opening its
    /// tooltip, covering the button labels.
    ///
    /// The fix lives in the TooltipHandler layer, where the decision is actually made
    /// (TooltipHandler.TipRegion / TipRegionByKey register an ActiveTip when
    /// Event.current.type == Repaint && Mouse.IsOver(rect)). While the user is in touch mode we
    /// simply do not register the tip:
    ///
    ///   * no touch event for TouchModeMs        -> nothing changes (desktop / real mouse)
    ///   * finger held down for HoldMs or longer -> the tip is allowed, so a long press still shows
    ///                                              the description the user asked for
    ///   * otherwise, SuppressMs after the last tap, and as long as the cursor does not move
    ///     (i.e. it is still parked on the tap point) -> suppressed
    ///
    /// A suppressed call returns false from the Harmony prefix and does *nothing* else: no
    /// Event.current.Use(), no GUI state change - the widget keeps drawing normally, only the
    /// tooltip is not registered. Exceptions never escape: the prefix fails open (tip shown) and
    /// the error goes to the log file.
    /// </summary>
    internal static class TouchActivity
    {
        // A touch event this recent means "the finger is the input device right now".
        internal const long TouchModeMs = 2000L;
        // Every tap hides tooltips for at least this long (spec) ...
        internal const long SuppressMs = 1500L;
        // ... and a hold this long explicitly re-enables them.
        internal const long HoldMs = 600L;
        // The cursor has to be stationary this long before we call it "parked on the tap point".
        internal const long ParkedMs = 300L;

        private static long _lastEventMs = -100000L;
        private static long _lastTapMs = -100000L;
        private static long _downMs = -100000L;
        private static bool _down;

        private static long Now()
        {
            try { return Environment.TickCount64; }
            catch { return 0L; }
        }

        /// <summary>
        /// Fed from the OnTouch patches (modern chain via PointerPatches, classic chain via
        /// TooltipPatches.Touch_Prefix). One property read per motion event - no logging, no
        /// allocation, no reflection beyond the cached MotionEvent accessors.
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
                }
                else if (action == 1 || action == 3)   // ACTION_UP / ACTION_CANCEL
                {
                    _down = false;
                    _lastTapMs = now;
                }
                else if (action == 6)                  // ACTION_POINTER_UP: a finger left, still a gesture
                {
                    _lastTapMs = now;
                }
            }
            catch { }
        }

        /// <summary>True while a tip must not be registered.</summary>
        internal static bool Suppress()
        {
            long now = Now();

            // Fast path, and the one that matters on desktop: no touch for 2 s -> untouched behaviour.
            if (now - _lastEventMs > TouchModeMs) return false;

            // Long press: the user is deliberately asking for the description.
            if (_down && now - _downMs >= HoldMs) return false;

            if (now - _lastTapMs < SuppressMs) return true;

            // Past the window: keep suppressing while the pointer is still parked where the tap put
            // it. A real mouse that moves ends the suppression immediately.
            return CursorParked();
        }

        // ---------------------------------------------------------------- parked cursor

        private static bool _mouseResolved;
        private static PropertyInfo _pMousePos;      // UnityEngine.Input.mousePosition (Vector2)
        private static PropertyInfo _pFrameCount;    // UnityEngine.Time.frameCount
        private static bool _mouseWarned;

        private static long _lastMoveMs = -100000L;
        private static float _lastMx, _lastMy;
        private static bool _haveLast;

        private static int _parkFrame = int.MinValue;
        private static long _parkGateMs = -100000L;
        private static bool _parked;

        private static void ResolveInput()
        {
            if (_mouseResolved) return;
            _mouseResolved = true;
            Type input = Refl.T("UnityEngine.Input");
            if (input == null) { Refl.Warn("UnityEngine.Input not found - parked cursor check disabled (fixed 1.5 s window only)"); return; }
            _pMousePos = Refl.Prop(input, "mousePosition");
            if (_pMousePos == null) { Refl.Warn("Input.mousePosition not found - parked cursor check disabled"); return; }
            Type time = Refl.T("UnityEngine.Time");
            if (time != null) _pFrameCount = Refl.Prop(time, "frameCount");
        }

        /// <summary>
        /// Has the pointer stopped moving? Cached per frame (or per 50 ms when Time.frameCount is
        /// unavailable): TipRegion is called for dozens of widgets per Repaint and has to stay cheap.
        /// </summary>
        private static bool CursorParked()
        {
            ResolveInput();
            if (_pMousePos == null) return false;

            int frame = int.MinValue;
            try { if (_pFrameCount != null) frame = Convert.ToInt32(_pFrameCount.GetValue(null, null)); }
            catch { }

            long now = Now();
            if (frame != int.MinValue)
            {
                if (frame == _parkFrame) return _parked;
                _parkFrame = frame;
            }
            else
            {
                if (now - _parkGateMs < 50L) return _parked;
                _parkGateMs = now;
            }

            try
            {
                object v = _pMousePos.GetValue(null, null);
                if (v == null) { _parked = false; return false; }
                Type t = v.GetType();
                FieldInfo fx = t.GetField("x", BindingFlags.Public | BindingFlags.Instance);
                FieldInfo fy = t.GetField("y", BindingFlags.Public | BindingFlags.Instance);
                if (fx == null || fy == null) { _parked = false; return false; }

                float mx = Convert.ToSingle(fx.GetValue(v));
                float my = Convert.ToSingle(fy.GetValue(v));

                if (!_haveLast || Math.Abs(mx - _lastMx) > 1f || Math.Abs(my - _lastMy) > 1f)
                {
                    _haveLast = true;
                    _lastMx = mx;
                    _lastMy = my;
                    _lastMoveMs = now;
                    _parked = false;
                    return false;
                }
                _parked = now - _lastMoveMs >= ParkedMs;
                return _parked;
            }
            catch (Exception e)
            {
                if (!_mouseWarned) { _mouseWarned = true; Refl.Warn("mousePosition read failed: " + e.Message); }
                _parked = false;
                return false;
            }
        }

        // ---------------------------------------------------------------- bookkeeping

        private static int _suppressed;
        private static long _lastReportMs = -100000L;

        internal static void CountSuppressed()
        {
            _suppressed++;
            long now = Now();
            if (now - _lastReportMs < 10000L) return;      // one line per 10 s at most
            _lastReportMs = now;
            Refl.Info("tooltip: " + _suppressed + " tip(s) suppressed in touch mode (mode="
                + SnapGeometry.ModeName(SnapGeometry.Mode) + ")");
            _suppressed = 0;
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
        /// touched, so the surrounding GUI keeps working.
        /// </summary>
        internal static bool Suppress_Prefix()
        {
            try
            {
                if (!TouchActivity.Suppress()) return true;
                TouchActivity.CountSuppressed();
                return false;
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
        /// Patch every TipRegion / TipRegionByKey overload on Verse.TooltipHandler (2 and 4 in this
        /// build: Rect+Func&lt;string&gt;+int, Rect+TipSignal, and four ByKey shapes).
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
                + " - suppress while touch is recent, allow on a >= " + TouchActivity.HoldMs + " ms hold");
        }
    }
}
