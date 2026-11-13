using System;
using System.Reflection;
using Verse;

namespace AgentTouchFix
{
    /// <summary>
    /// Fixes "tapping the map selects something instead of running the active debug tool".
    ///
    /// Root cause: the mobile touch layer converts a tap into a synthetic selection call
    /// (PortUI.MobileSelectionController.Tap) without ever producing a MouseDown that
    /// LudeonTK.DebugTool.DebugToolOnGUI could consume, so an armed dev tool never fires
    /// and the tap is stolen by the selector instead.
    /// </summary>
    internal static class ToolPatches
    {
        // --- cached reflection -------------------------------------------------
        private static bool _curToolResolved;
        private static FieldInfo _fCurTool;          // LudeonTK.DebugTools.curTool
        private static FieldInfo _fClickAction;      // LudeonTK.DebugTool.clickAction
        private static FieldInfo _fDragBox;          // RimWorld.Selector.dragBox
        private static FieldInfo _fDragBoxActive;    // Verse.DragBox.active

        private static bool _timeResolved;
        private static PropertyInfo _pFrameCount;    // UnityEngine.Time.frameCount

        // --- state -------------------------------------------------------------
        private static int _lastFireClock = int.MinValue;
        private static bool _overrideActive;
        private static IntVec3 _overrideCell;

        internal static object CurTool
        {
            get
            {
                if (!_curToolResolved)
                {
                    _curToolResolved = true;
                    Type t = Refl.T("LudeonTK.DebugTools");
                    _fCurTool = Refl.F(t, "curTool");
                    if (_fCurTool == null) Refl.Warn("LudeonTK.DebugTools.curTool not found - dev tool fixes disabled");
                }
                try { return _fCurTool == null ? null : _fCurTool.GetValue(null); }
                catch { return null; }
            }
        }

        private static void ClearTool()
        {
            try { _fCurTool?.SetValue(null, null); }
            catch (Exception e) { Refl.Err("failed to clear curTool: " + e.Message); }
        }

        private static Delegate ClickActionOf(object tool)
        {
            if (tool == null) return null;
            if (_fClickAction == null || _fClickAction.DeclaringType != tool.GetType())
            {
                _fClickAction = Refl.F(tool.GetType(), "clickAction");
            }
            try { return _fClickAction == null ? null : _fClickAction.GetValue(tool) as Delegate; }
            catch { return null; }
        }

        /// <summary>Monotonic "frame" clock: Unity frameCount when present, else 16 ms buckets.</summary>
        private static int Now()
        {
            if (!_timeResolved)
            {
                _timeResolved = true;
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

        private static bool TryCellFrom(object vec3, out IntVec3 cell)
        {
            cell = default(IntVec3);
            if (vec3 == null) return false;
            Type t = vec3.GetType();
            FieldInfo fx = null, fz = null;
            try
            {
                fx = t.GetField("x", BindingFlags.Public | BindingFlags.Instance);
                fz = t.GetField("z", BindingFlags.Public | BindingFlags.Instance);
            }
            catch { }
            if (fx == null || fz == null) return false;
            try
            {
                float x = Convert.ToSingle(fx.GetValue(vec3));
                float z = Convert.ToSingle(fz.GetValue(vec3));
                cell = new IntVec3((int)Math.Round((double)x), 0, (int)Math.Round((double)z));
                return true;
            }
            catch { return false; }
        }

        /// <summary>Run the armed debug tool at the tapped map cell. Returns false only when no tool is armed.</summary>
        internal static bool Fire(object tapPos, bool cancel)
        {
            object tool = CurTool;
            if (tool == null) return false;

            if (cancel)
            {
                ClearTool();
                Refl.Info("debug tool cancelled (long press)");
                return true;
            }

            Delegate action = ClickActionOf(tool);
            if (action == null)
            {
                Refl.Warn("armed debug tool has no clickAction - tap consumed, selection suppressed");
                _lastFireClock = Now();
                return true;
            }

            IntVec3 cell;
            bool haveCell = TryCellFrom(tapPos, out cell);
            if (haveCell)
            {
                _overrideCell = cell;
                _overrideActive = true;
            }
            try
            {
                action.DynamicInvoke();
                Refl.Info("debug tool fired at " + (haveCell ? cell.ToString() : "mouse cell"));
            }
            catch (Exception e)
            {
                Exception inner = e;
                TargetInvocationException tie = e as TargetInvocationException;
                if (tie != null && tie.InnerException != null) inner = tie.InnerException;
                Refl.Err("debug tool clickAction threw: " + inner);
            }
            finally
            {
                _overrideActive = false;
                _lastFireClock = Now();
            }
            return true;
        }

        // ===================== Harmony patches =================================

        /// <summary>
        /// Real-mouse fallback: while a dev tool is armed the selector must not treat the
        /// click as a selection / drag-box start. Mirrors vanilla semantics, where
        /// DebugTool.Use() keeps DragBox.active false.
        /// </summary>
        internal static bool HandleMapClicks_Prefix(object __instance)
        {
            try
            {
                if (CurTool == null) return true;
                if (_fDragBox == null || _fDragBox.DeclaringType != __instance.GetType())
                {
                    _fDragBox = Refl.F(__instance.GetType(), "dragBox");
                    _fDragBoxActive = _fDragBox == null ? null : Refl.F(_fDragBox.FieldType, "active");
                }
                object box = _fDragBox?.GetValue(__instance);
                if (box != null && _fDragBoxActive != null)
                {
                    object active = _fDragBoxActive.GetValue(box);
                    if (active is bool && (bool)active)
                    {
                        _fDragBoxActive.SetValue(box, false);
                        Refl.Info("drag box disarmed (dev tool active)");
                    }
                }
                return false;   // skip vanilla selection handling for this event
            }
            catch (Exception e)
            {
                Refl.Err("HandleMapClicks prefix: " + e.Message);
                return true;
            }
        }

        /// <summary>
        /// The actual fix: PortUI calls Tap() for every map tap. If a dev tool is armed we
        /// consume the tap ourselves (fire the tool / cancel on long press) and skip the
        /// selection logic entirely.
        /// </summary>
        internal static bool Tap_Prefix(object[] __args)
        {
            if (__args == null || __args.Length == 0) return true;
            try
            {
                if (CurTool == null) return true;

                bool longPress = false;
                if (__args.Length > 1)
                {
                    object o = __args[1];
                    if (o is bool) longPress = (bool)o;
                }
                bool consumed = Fire(__args[0], longPress);
                return !consumed;
            }
            catch (Exception e)
            {
                Refl.Err("Tap prefix: " + e.Message);
                return true;
            }
        }

        /// <summary>
        /// While a tool click runs, UI.MouseCell() must report the tapped cell. PortUI
        /// rewrites MouseCell for its designator preview; running at High priority keeps
        /// this override in front of it.
        /// </summary>
        internal static bool MouseCell_Prefix(ref IntVec3 __result)
        {
            if (!_overrideActive) return true;
            __result = _overrideCell;
            return false;
        }

        /// <summary>Do not fire the same armed tool twice when a real MouseDown arrives in the same frame.</summary>
        internal static bool DebugToolOnGUI_Prefix()
        {
            if (_lastFireClock == int.MinValue) return true;
            return Now() != _lastFireClock;
        }
    }
}
