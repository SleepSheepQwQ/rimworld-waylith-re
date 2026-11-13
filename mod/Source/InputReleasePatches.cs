using System;
using System.Collections.Generic;
using System.Reflection;

namespace AgentTouchFix
{
    /// <summary>
    /// v8: input state that the host pushes into GLFW and then forgets.
    ///
    /// A) Mouse buttons stick. MainActivity keeps a mirror of the held buttons in the private
    ///    field <c>mouseButtons</c> and only ever forwards *differences* against it:
    ///
    ///      MouseEvent (MainActivity.cs:995-1035)
    ///        num = (int)e.ButtonState;                  // button bitmask: 1,2,4,8,16
    ///        ...
    ///        for (i = 0..4) if ((mouseButtons & bit) != (num & bit))
    ///                          glfwAndroidPushMouseButton(i, num &amp; bit ? Press : Release, 0);
    ///        mouseButtons = num;                        // the ONLY place a button is released
    ///
    ///    But four lifecycle paths clear the mirror *without* forwarding a release:
    ///    SurfaceDestroyed, OnConfigurationChanged, OnWindowFocusChanged(false) (all three do
    ///    <c>mouseButtons = 0;</c>) and OnPause (which does not even do that). Once the mirror is 0
    ///    while the desktop/game still holds a virtual button down, the next MouseEvent computes a
    ///    0 -&gt; 0 difference and nothing is ever released: the button stays pressed forever.
    ///
    ///    Fix: prefix those four methods, walk the mirror *before* the original method clears it and
    ///    push a real release for every held bit. OnPause additionally zeroes the mirror itself,
    ///    because that method never did - otherwise the release would be invisible to the diff loop
    ///    and the next press would be swallowed.
    ///
    /// B) Keys stick. OnKeyDown/OnKeyUp call Native.glfwAndroidPushKey(num, scanCode, action, mods)
    ///    with no bookkeeping whatsoever, so a key held while the activity loses focus (or is
    ///    paused, or the surface is recreated) stays down in the engine - WASD / Shift keep moving
    ///    the pawn. Fix: remember scanCode -&gt; glfw key on the first ACTION_DOWN of a key, forget it
    ///    on ACTION_UP, and synthesise the missing releases from the same four lifecycle prefixes.
    ///    KEYCODE_BACK (4) is deliberately not tracked: it maps to Escape, and OnBackPressed pushes
    ///    its own press+release pair, so a tracked Back would be released twice.
    ///
    /// C) ACTION_UP threw away every held button. The host does
    ///
    ///      if (action == ACTION_UP || action == ACTION_CANCEL) num = 0;      // line 1021-1024
    ///
    ///    <c>num</c> really is the button bitmask (it is <c>(int)e.ButtonState</c>, and the five bits
    ///    1/2/4/8/16 are fed to glfwAndroidPushMouseButton as buttons 0..4), not a pointer count. On
    ///    devices that report the still-held buttons in ACTION_UP's ButtonState, forcing 0 releases
    ///    buttons the user never let go of, and the next ACTION_MOVE - which does carry the remaining
    ///    ButtonState - presses them again: a phantom up/down pair (a stray click / a dropped drag)
    ///    plus a MouseDown that DebugTool may act on. Fix: for ACTION_UP with a non-empty ButtonState,
    ///    clear only the button that went up (e.ActionButton) and forward exactly the differences,
    ///    which is what the host's own ACTION_POINTER_UP path already does. When ButtonState is 0 -
    ///    the normal single-button case - the original method is left completely alone, so nothing
    ///    about v7 behaviour changes there.
    ///
    /// Everything here is reflection only (no Mono.Android / RwHost / UnityEngine reference) and
    /// fails soft: any resolution failure just means the corresponding fix is off.
    /// </summary>
    internal static class InputReleasePatches
    {
        // MotionEventActions
        private const int ActionUp = 1;

        // InputEvent.Source mouse bitmask the host tests: ((Source & 0x2002) == 8194)
        private const int MouseSourceMask = 0x2002;
        private const int MouseSource = 8194;

        // Android KeyEvent.KEYCODE_BACK -> AndroidKeyMap maps 4 (and 111) to GLFW Escape 256.
        private const int KeycodeBack = 4;

        private static readonly int[] ButtonBits = { 1, 2, 4, 8, 16 };

        private static Type _tActivity;
        private static bool _resolved;
        private static FieldInfo _fMouseButtons;   // MainActivity.mouseButtons (private int)
        private static FieldInfo _fTouch;          // MainActivity.touch (private TouchControls)
        private static MethodInfo _mPushMouse;     // RwHost.Native.glfwAndroidPushMouseButton(int,int,int)
        private static MethodInfo _mPushKey;       // RwHost.Native.glfwAndroidPushKey(int,int,int,int)
        private static MethodInfo _mPushCursor;    // RwHost.Native.glfwAndroidPushCursorPos(double,double)
        private static MethodInfo _mUseMouse;      // RwHost.TouchCamera.UseMouse(bool)
        private static MethodInfo _mTouchCancel;   // RwHost.TouchControls.Cancel()
        private static MethodInfo _mToGlfw;        // Ports1de.AndroidKeyMap.ToGlfw(int)

        private static bool _warnedOnce;

        // Keyboard bookkeeping: scanCode -> glfw key. Cleared on every release / key up.
        private static readonly object _keyLock = new object();
        private static readonly Dictionary<int, int> _keys = new Dictionary<int, int>();

        private static long _lastMouseFixMs;

        internal static Type ActivityType()
        {
            Resolve();
            return _tActivity;
        }

        /// <summary>The patch target inside RwHost.MainActivity, resolved by name and arity.</summary>
        internal static MethodBase Target(string name, int argc)
        {
            if (!Resolve()) return null;
            return FindMethod(_tActivity, name, argc);
        }

        /// <summary>Same, but for the private MouseEvent path only (needs the C patch capable state).</summary>
        internal static MethodBase MouseEventTarget()
        {
            if (!Resolve()) return null;
            return FindMethod(_tActivity, "MouseEvent", 1);
        }

        // ------------------------------------------------------------------ resolution

        private static bool Resolve()
        {
            if (_resolved) return _tActivity != null;
            _resolved = true;

            _tActivity = Refl.TPrefer("RwHost", "RwHost.MainActivity");
            if (_tActivity == null)
            {
                Refl.Warn("RwHost.MainActivity not found - v8 mouse/key release fixes are off");
                return false;
            }

            _fMouseButtons = Refl.F(_tActivity, "mouseButtons");
            _fTouch = Refl.F(_tActivity, "touch");

            Type native = Refl.TPrefer("RwHost", "RwHost.Native");
            if (native != null)
            {
                _mPushMouse = FindMethod(native, "glfwAndroidPushMouseButton", 3);
                _mPushKey = FindMethod(native, "glfwAndroidPushKey", 4);
                _mPushCursor = FindMethod(native, "glfwAndroidPushCursorPos", 2);
            }

            Type camera = Refl.TPrefer("RwHost", "RwHost.TouchCamera");
            if (camera != null) _mUseMouse = FindMethod(camera, "UseMouse", 1);

            Type controls = Refl.TPrefer("RwHost", "RwHost.TouchControls");
            if (controls != null) _mTouchCancel = FindMethod(controls, "Cancel", 0);

            Type keyMap = Refl.TPrefer("RwHost", "Ports1de.AndroidKeyMap");
            if (keyMap != null) _mToGlfw = FindMethod(keyMap, "ToGlfw", 1);

            if (_fMouseButtons == null)
                Refl.Warn("MainActivity.mouseButtons not found - held mouse buttons cannot be released");
            if (_mPushMouse == null)
                Refl.Warn("RwHost.Native.glfwAndroidPushMouseButton not found - held mouse buttons cannot be released");
            if (_mPushKey == null)
                Refl.Warn("RwHost.Native.glfwAndroidPushKey not found - held keys cannot be released");
            if (_mToGlfw == null)
                Refl.Warn("Ports1de.AndroidKeyMap.ToGlfw not found - key tracking disabled (no release possible)");

            return _tActivity != null;
        }

        /// <summary>Declared members first, then base types; ambiguous arity means "not found".</summary>
        internal static MethodInfo FindMethod(Type t, string name, int argc)
        {
            if (t == null) return null;
            try
            {
                for (Type cur = t; cur != null; cur = cur.BaseType)
                {
                    MethodInfo[] all = cur.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                        | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
                    MethodInfo hit = null;
                    for (int i = 0; i < all.Length; i++)
                    {
                        if (all[i].Name != name) continue;
                        if (all[i].GetParameters().Length != argc) continue;
                        if (hit != null) return null;   // overloaded the same way: refuse to guess
                        hit = all[i];
                    }
                    if (hit != null) return hit;
                }
            }
            catch { }
            return null;
        }

        private static void WarnOnce(string m)
        {
            if (_warnedOnce) return;
            _warnedOnce = true;
            Refl.Warn(m);
        }

        // ------------------------------------------------------------------ small helpers

        private static int ToInt(object v, int fallback)
        {
            if (v == null) return fallback;
            try { return Convert.ToInt32(v); }
            catch { return fallback; }
        }

        private static object Read(object o, string prop)
        {
            if (o == null) return null;
            try
            {
                PropertyInfo p = o.GetType().GetProperty(prop, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (p == null) return null;
                return p.GetValue(o);
            }
            catch { return null; }
        }

        private static double CallDouble(object o, string method)
        {
            try
            {
                MethodInfo mi = o.GetType().GetMethod(method, BindingFlags.Public | BindingFlags.Instance, null, new Type[0], null);
                if (mi == null) return 0d;
                return Convert.ToDouble(mi.Invoke(o, null));
            }
            catch { return 0d; }
        }

        private static int GlfwKey(int androidKeycode)
        {
            if (_mToGlfw == null) return int.MinValue;
            try { return Convert.ToInt32(_mToGlfw.Invoke(null, new object[] { androidKeycode })); }
            catch { return int.MinValue; }
        }

        private static bool PushMouseButton(int button, int action)
        {
            if (_mPushMouse == null) return false;
            try
            {
                _mPushMouse.Invoke(null, new object[] { button, action, 0 });
                return true;
            }
            catch (Exception e)
            {
                WarnOnce("glfwAndroidPushMouseButton failed: " + e.Message);
                return false;
            }
        }

        private static bool PushKey(int glfwKey, int scancode)
        {
            if (_mPushKey == null) return false;
            try
            {
                // action 0 == Release; GLFW keys are matched by scancode, the key code only names it.
                _mPushKey.Invoke(null, new object[] { glfwKey, scancode, 0, 0 });
                return true;
            }
            catch (Exception e)
            {
                WarnOnce("glfwAndroidPushKey (release) failed: " + e.Message);
                return false;
            }
        }

        private static int ReadMirror(object activity)
        {
            if (_fMouseButtons == null || activity == null) return 0;
            try { return ToInt(_fMouseButtons.GetValue(activity), 0); }
            catch { return 0; }
        }

        private static void WriteMirror(object activity, int value)
        {
            if (_fMouseButtons == null || activity == null) return;
            try { _fMouseButtons.SetValue(activity, value); }
            catch { WarnOnce("MainActivity.mouseButtons not writable - mirror not reset"); }
        }

        private static void CancelTouch(object activity)
        {
            if (_fTouch == null || _mTouchCancel == null || activity == null) return;
            try
            {
                object touch = _fTouch.GetValue(activity);
                if (touch != null) _mTouchCancel.Invoke(touch, null);
            }
            catch { }
        }

        // ------------------------------------------------------------------ release paths

        internal static void ReleaseMouseButtons(object activity, string why)
        {
            if (_fMouseButtons == null) return;
            int held = ReadMirror(activity);
            if (held == 0) return;

            int released = 0;
            for (int i = 0; i < ButtonBits.Length; i++)
            {
                if ((held & ButtonBits[i]) == 0) continue;
                if (PushMouseButton(i, 0)) released++;
            }
            Refl.Info(why + ": released " + released + " held mouse button(s), mirror was 0x" + held.ToString("x"));
        }

        internal static void ReleaseKeys(string why)
        {
            List<KeyValuePair<int, int>> pending;
            lock (_keyLock)
            {
                if (_keys.Count == 0) return;
                pending = new List<KeyValuePair<int, int>>(_keys);
                _keys.Clear();
            }

            int released = 0;
            for (int i = 0; i < pending.Count; i++)
            {
                if (PushKey(pending[i].Value, pending[i].Key)) released++;
            }
            Refl.Info(why + ": released " + released + " held key(s)");
        }

        /// <summary>Release everything, then optionally reset the mirror (OnPause never did).</summary>
        internal static void ReleaseAll(object activity, string why, bool zeroMirror)
        {
            if (!Resolve()) return;
            ReleaseMouseButtons(activity, why);
            ReleaseKeys(why);
            if (zeroMirror) WriteMirror(activity, 0);
        }

        // ------------------------------------------------------------------ A: lifecycle prefixes

        internal static void SurfaceDestroyed_Prefix(object __instance)
        {
            try { ReleaseAll(__instance, "surface destroyed", false); }
            catch (Exception e) { WarnOnce("SurfaceDestroyed prefix failed: " + e.Message); }
        }

        internal static void OnConfigurationChanged_Prefix(object __instance)
        {
            try { ReleaseAll(__instance, "configuration changed", false); }
            catch (Exception e) { WarnOnce("OnConfigurationChanged prefix failed: " + e.Message); }
        }

        internal static void OnWindowFocusChanged_Prefix(object __instance, bool hasFocus)
        {
            // Only the focus-lost branch clears the mirror; focus gain must not touch anything.
            if (hasFocus) return;
            try { ReleaseAll(__instance, "window focus lost", false); }
            catch (Exception e) { WarnOnce("OnWindowFocusChanged prefix failed: " + e.Message); }
        }

        internal static void OnPause_Prefix(object __instance)
        {
            try { ReleaseAll(__instance, "activity paused", true); }
            catch (Exception e) { WarnOnce("OnPause prefix failed: " + e.Message); }
        }

        // ------------------------------------------------------------------ B: key bookkeeping

        internal static void OnKeyDown_Prefix(object[] __args)
        {
            try
            {
                if (!Resolve() || _mToGlfw == null) return;
                if (__args == null || __args.Length < 2 || __args[1] == null) return;

                object e = __args[1];
                int code = ToInt(__args[0], -1);
                if (code == KeycodeBack) return;                       // OnBackPressed sends its own pair
                if (ToInt(Read(e, "RepeatCount"), 0) != 0) return;      // auto-repeat, already tracked

                int scan = ToInt(Read(e, "ScanCode"), -1);
                if (scan < 0) return;

                int glfw = GlfwKey(code);
                if (glfw < 0) return;                                   // host forwards nothing for it

                lock (_keyLock) _keys[scan] = glfw;
            }
            catch (Exception ex) { WarnOnce("OnKeyDown prefix failed (keys can still stick): " + ex.Message); }
        }

        internal static void OnKeyUp_Prefix(object[] __args)
        {
            try
            {
                if (__args == null || __args.Length < 2 || __args[1] == null) return;
                int scan = ToInt(Read(__args[1], "ScanCode"), -1);
                if (scan < 0) return;
                lock (_keyLock) _keys.Remove(scan);
            }
            catch { }
        }

        // ------------------------------------------------------------------ C: ACTION_UP

        /// <summary>
        /// ACTION_UP with a ButtonState that still names other held buttons: the host sets num = 0
        /// and releases all of them. Take the event over and forward only the difference to the
        /// button that went up, exactly like the host's ACTION_POINTER_UP path (which never forces
        /// num). Every other event - including ACTION_CANCEL, and ACTION_UP with ButtonState == 0 -
        /// falls through to the untouched original, so the common case keeps v7 behaviour bit for
        /// bit.
        /// </summary>
        internal static bool MouseEvent_Prefix(object __instance, object[] __args, ref bool __result)
        {
            try
            {
                if (!Resolve() || _fMouseButtons == null || _mPushMouse == null) return true;
                if (__args == null || __args.Length < 1 || __args[0] == null) return true;

                object e = __args[0];
                if (ToInt(Read(e, "ActionMasked"), -1) != ActionUp) return true;

                int source = ToInt(Read(e, "Source"), 0);
                if ((source & MouseSourceMask) != MouseSource) return true;   // not a mouse event

                int buttonState = ToInt(Read(e, "ButtonState"), 0) & 0x1F;   // bits 1,2,4,8,16
                if (buttonState == 0) return true;                            // nothing else is held

                int lifted = ToInt(Read(e, "ActionButton"), 0);
                int remaining = buttonState;
                for (int i = 0; i < ButtonBits.Length; i++)
                {
                    if (ButtonBits[i] == lifted) remaining &= ~lifted;        // never keep the one that went up
                }
                if (remaining == 0) return true;                              // only the lifted one was down

                int old = ReadMirror(__instance);

                // Same order as the host: mouse mode, touch cancel, cursor, then the diff.
                try { if (_mUseMouse != null) _mUseMouse.Invoke(null, new object[] { true }); } catch { }
                CancelTouch(__instance);
                try
                {
                    if (_mPushCursor != null)
                        _mPushCursor.Invoke(null, new object[] { CallDouble(e, "GetX"), CallDouble(e, "GetY") });
                }
                catch { }

                for (int i = 0; i < ButtonBits.Length; i++)
                {
                    if ((old & ButtonBits[i]) == (remaining & ButtonBits[i])) continue;
                    PushMouseButton(i, (remaining & ButtonBits[i]) != 0 ? 1 : 0);
                }
                WriteMirror(__instance, remaining);

                __result = true;
                long now = Environment.TickCount64;
                if (now - _lastMouseFixMs >= 1000)
                {
                    _lastMouseFixMs = now;
                    Refl.Info("action up kept other buttons down: mirror 0x" + old.ToString("x") + " -> 0x"
                        + remaining.ToString("x") + " (released only the lifted button, ButtonState 0x"
                        + buttonState.ToString("x") + ", ActionButton 0x" + lifted.ToString("x") + ")");
                }
                return false;   // original must not run: it would force num = 0
            }
            catch (Exception ex)
            {
                WarnOnce("MouseEvent prefix failed (ACTION_UP still clears every button): " + ex.Message);
                return true;
            }
        }
    }
}
