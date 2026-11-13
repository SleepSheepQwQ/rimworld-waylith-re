using System;
using System.Reflection;

namespace AgentTouchFix
{
    /// <summary>
    /// Fixes "pointer stays dead after a two finger gesture / one finger drag becomes zoom".
    ///
    /// Root cause (RwHost 0.0.22): TouchGestureEngine.SecondUp() leaves state = Ignore, and
    /// neither Move() nor Up() has an Ignore branch, so the finger that is still down emits
    /// nothing until it is lifted and pressed again. Nothing resets the pinch baseline
    /// (twoStartDistance) or the GameCamera pan/zoom accumulators at gesture end either, so a
    /// stale zoom can be applied later, which reads as "one finger drag suddenly zooms".
    /// </summary>
    internal static class PointerPatches
    {
        // TouchGestureEngine.State (Ports1de, RwHost.dll)
        private const int StateIdle = 0;
        private const int StatePan = 3;

        private static Type _tEngine;
        private static Type _tControls;
        private static Type _tCamera;

        private static FieldInfo _fState, _fTwoMoved, _fTwoPan, _fVx, _fVy, _fInertia;
        private static FieldInfo _fTwoDistance, _fTwoStartDistance, _fLastTap, _fContext, _fX0, _fY0, _fX, _fY;
        private static FieldInfo _fEngineField;   // TouchControls.engine
        private static MethodInfo _mDown, _mSecondDown, _mCameraClear;
        private static bool _resolved;

        private static void Resolve()
        {
            if (_resolved) return;
            _resolved = true;

            _tEngine = Refl.T("Ports1de.TouchGestureEngine");
            _tControls = Refl.T("RwHost.TouchControls");
            _tCamera = Refl.T("RwHost.TouchCamera");

            if (_tEngine == null) { Refl.Warn("Ports1de.TouchGestureEngine not found - pointer fixes limited"); return; }

            _fState = Refl.F(_tEngine, "state");
            _fTwoMoved = Refl.F(_tEngine, "twoMoved");
            _fTwoPan = Refl.F(_tEngine, "twoPan");
            _fVx = Refl.F(_tEngine, "vx");
            _fVy = Refl.F(_tEngine, "vy");
            _fInertia = Refl.F(_tEngine, "inertia");
            _fTwoDistance = Refl.F(_tEngine, "twoDistance");
            _fTwoStartDistance = Refl.F(_tEngine, "twoStartDistance");
            _fLastTap = Refl.F(_tEngine, "lastTap");
            _fContext = Refl.F(_tEngine, "context");
            _fX0 = Refl.F(_tEngine, "x0");
            _fY0 = Refl.F(_tEngine, "y0");
            _fX = Refl.F(_tEngine, "x");
            _fY = Refl.F(_tEngine, "y");

            _mDown = Refl.M(_tEngine, "Down");
            _mSecondDown = Refl.M(_tEngine, "SecondDown");

            if (_tControls != null) _fEngineField = Refl.F(_tControls, "engine");
            if (_tCamera != null) _mCameraClear = Refl.M(_tCamera, "Clear");

            if (_fState == null) Refl.Warn("TouchGestureEngine.state not found - state resets are no-ops");
            if (_mDown == null) Refl.Warn("TouchGestureEngine.Down not found - remaining finger cannot be re-armed");
            if (_mCameraClear == null) Refl.Warn("TouchCamera.Clear not found - camera accumulators not cleared");
        }

        private static object EngineOf(object controls)
        {
            if (controls == null) return null;
            try { return _fEngineField?.GetValue(controls); }
            catch { return null; }
        }

        private static void SetState(object engine, int value)
        {
            try
            {
                if (_fState == null || engine == null) return;
                _fState.SetValue(engine, Enum.ToObject(_fState.FieldType, value));
            }
            catch { }
        }

        private static void SetBool(FieldInfo f, object engine, bool v)
        {
            if (f == null || engine == null) return;
            try { f.SetValue(engine, v); } catch { }
        }

        private static void SetFloat(FieldInfo f, object engine, float v)
        {
            if (f == null || engine == null) return;
            try { f.SetValue(engine, v); } catch { }
        }

        /// <summary>Idle + no inertia + collapsed pinch baseline.</summary>
        private static void ResetEngine(object engine)
        {
            if (engine == null) return;
            SetState(engine, StateIdle);
            SetBool(_fTwoMoved, engine, false);
            SetBool(_fTwoPan, engine, false);
            SetBool(_fInertia, engine, false);
            SetFloat(_fVx, engine, 0f);
            SetFloat(_fVy, engine, 0f);
            SetFloat(_fTwoDistance, engine, 1f);
            SetFloat(_fTwoStartDistance, engine, 1f);
        }

        private static void ClearCamera()
        {
            try { _mCameraClear?.Invoke(null, null); }
            catch (Exception e) { Refl.Err("TouchCamera.Clear failed: " + e.Message); }
        }

        /// <summary>Hand the gesture over to the finger that is still down, as a pan (never a tap).</summary>
        private static void RearmSingle(object engine, float x, float y, long time)
        {
            if (_mDown == null) return;
            object ctx = null;
            try { ctx = _fContext?.GetValue(engine); } catch { }
            if (ctx == null) return;
            try
            {
                _mDown.Invoke(engine, new object[] { x, y, time, ctx });
            }
            catch (Exception e)
            {
                Refl.Err("engine.Down re-arm failed: " + e.Message);
                return;
            }

            // Down() left us in Pending: a lift without movement would become a stray Tap
            // (a selection right after the pinch). Pan semantics are what the user expects.
            SetState(engine, StatePan);
            SetFloat(_fVx, engine, 0f);
            SetFloat(_fVy, engine, 0f);
            SetFloat(_fX0, engine, x);
            SetFloat(_fY0, engine, y);
            SetFloat(_fX, engine, x);
            SetFloat(_fY, engine, y);
            try { _fLastTap?.SetValue(engine, -10000L); } catch { }
        }

        /// <summary>Two fingers still down after a third was lifted: re-enter the pinch state.</summary>
        private static void RearmTwo(object engine, float ax, float ay, float bx, float by, long time)
        {
            if (_mSecondDown == null) return;
            try
            {
                _mSecondDown.Invoke(engine, new object[] { ax, ay, bx, by, time, false });
            }
            catch (Exception e)
            {
                Refl.Err("engine.SecondDown re-arm failed: " + e.Message);
            }
        }

        // ===================== Harmony patches =================================

        /// <summary>
        /// ACTION_POINTER_UP / ACTION_CANCEL: drop the dead "Ignore"/"Two" state and stale
        /// camera accumulators *before* the original handler inspects them. Runs ahead of the
        /// letter layer, which swallows ACTION_POINTER_UP entirely.
        /// </summary>
        internal static bool OnTouch_Prefix(object __instance, object[] __args)
        {
            Resolve();
            if (__args == null || __args.Length == 0) return true;
            try
            {
                object e = __args[0];
                int action = Motion.ActionMasked(e);
                if (action != 6 && action != 3) return true;

                object engine = EngineOf(__instance);
                if (engine == null) return true;

                ResetEngine(engine);
                ClearCamera();
                return true;   // let the original handler run (it may still emit the pan wrap-up)
            }
            catch (Exception ex)
            {
                Refl.Err("OnTouch prefix: " + ex.Message);
                return true;
            }
        }

        /// <summary>
        /// ACTION_POINTER_UP: give the remaining finger(s) a fresh, working gesture instead of
        /// leaving the engine parked in Idle/Ignore until the next ACTION_DOWN.
        /// </summary>
        internal static void OnTouch_Postfix(object __instance, object[] __args)
        {
            Resolve();
            if (__args == null || __args.Length == 0) return;
            try
            {
                object e = __args[0];
                if (Motion.ActionMasked(e) != 6) return;

                object engine = EngineOf(__instance);
                if (engine == null) return;

                int count = Motion.PointerCount(e);
                int remain = count - 1;
                long time = Motion.EventTime(e);
                int lifted = Motion.ActionIndex(e);

                if (remain == 1)
                {
                    int idx = -1;
                    for (int i = 0; i < count; i++)
                    {
                        if (i != lifted) { idx = i; break; }
                    }
                    if (idx < 0) return;
                    RearmSingle(engine, Motion.GetX(e, idx), Motion.GetY(e, idx), time);
                    Refl.Info("two finger gesture ended: remaining finger re-armed as pan");
                }
                else if (remain == 2)
                {
                    int a = -1, b = -1;
                    for (int i = 0; i < count; i++)
                    {
                        if (i == lifted) continue;
                        if (a < 0) a = i;
                        else if (b < 0) b = i;
                    }
                    if (a < 0 || b < 0) return;
                    RearmTwo(engine, Motion.GetX(e, a), Motion.GetY(e, a), Motion.GetX(e, b), Motion.GetY(e, b), time);
                    Refl.Info("pointer lifted: remaining two fingers re-armed as pinch");
                }
                ClearCamera();
            }
            catch (Exception ex)
            {
                Refl.Err("OnTouch postfix: " + ex.Message);
            }
        }

        /// <summary>
        /// Belt and braces: SecondUp() always leaves state = Ignore, which no other branch of
        /// the state machine handles. Force a clean Idle and drop the pinch baseline.
        /// </summary>
        internal static void SecondUp_Postfix(object __instance)
        {
            Resolve();
            try
            {
                if (__instance == null) return;
                SetState(__instance, StateIdle);
                SetBool(_fTwoMoved, __instance, false);
                SetBool(_fTwoPan, __instance, false);
                SetBool(_fInertia, __instance, false);
                SetFloat(_fVx, __instance, 0f);
                SetFloat(_fVy, __instance, 0f);
                SetFloat(_fTwoDistance, __instance, 1f);
                SetFloat(_fTwoStartDistance, __instance, 1f);
            }
            catch (Exception e)
            {
                Refl.Err("SecondUp postfix: " + e.Message);
            }
        }
    }

    /// <summary>Reflection accessors for Android.Views.MotionEvent (no Mono.Android reference).</summary>
    internal static class Motion
    {
        private static Type _type;
        private static PropertyInfo _pAction, _pActionMasked, _pPointerCount, _pEventTime;
        private static MethodInfo _mGetX, _mGetY;
        private static bool _resolved;

        private static void Resolve(object e)
        {
            if (_resolved || e == null) return;
            _resolved = true;
            _type = e.GetType();
            const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            _pAction = _type.GetProperty("Action", F);
            _pActionMasked = _type.GetProperty("ActionMasked", F);
            _pPointerCount = _type.GetProperty("PointerCount", F);
            _pEventTime = _type.GetProperty("EventTime", F);
            _mGetX = _type.GetMethod("GetX", F, null, new[] { typeof(int) }, null);
            _mGetY = _type.GetMethod("GetY", F, null, new[] { typeof(int) }, null);
            if (_pActionMasked == null) Refl.Warn("MotionEvent.ActionMasked not found (type " + _type.FullName + ")");
            if (_mGetX == null) Refl.Warn("MotionEvent.GetX(int) not found");
        }

        internal static int ActionMasked(object e)
        {
            Resolve(e);
            try
            {
                if (_pActionMasked != null) return Convert.ToInt32(_pActionMasked.GetValue(e, null));
                if (_pAction != null) return Convert.ToInt32(_pAction.GetValue(e, null)) & 0xff;
            }
            catch { }
            return -1;
        }

        internal static int ActionIndex(object e)
        {
            Resolve(e);
            try
            {
                if (_pAction != null) return (Convert.ToInt32(_pAction.GetValue(e, null)) >> 8) & 0xff;
            }
            catch { }
            return -1;
        }

        internal static int PointerCount(object e)
        {
            Resolve(e);
            try
            {
                if (_pPointerCount != null) return Convert.ToInt32(_pPointerCount.GetValue(e, null));
            }
            catch { }
            return 1;
        }

        internal static long EventTime(object e)
        {
            Resolve(e);
            try
            {
                if (_pEventTime != null) return Convert.ToInt64(_pEventTime.GetValue(e, null));
            }
            catch { }
            return 0L;
        }

        internal static float GetX(object e, int index)
        {
            Resolve(e);
            try { return Convert.ToSingle(_mGetX.Invoke(e, new object[] { index })); }
            catch { return 0f; }
        }

        internal static float GetY(object e, int index)
        {
            Resolve(e);
            try { return Convert.ToSingle(_mGetY.Invoke(e, new object[] { index })); }
            catch { return 0f; }
        }
    }
}
