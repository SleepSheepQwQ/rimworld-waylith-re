using System;
using System.Collections;
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
    ///
    /// v4: the tap is no longer forced onto the rounded cell only. The tap position is resolved
    /// through PortUI's own nearest-target picker (MobileSelectionController.Candidates: exact cell
    /// first, then the 5x5 surrounding cells inside a 28.8 px screen radius, nearest first), and
    /// both UI.MouseCell() and UI.MouseMapPosition() are pinned to the resolved target for the
    /// duration of the clickAction. A tap that is a few pixels off the entity now still hits the
    /// entity instead of landing on empty ground.
    /// </summary>
    internal static class ToolPatches
    {
        // --- cached reflection -------------------------------------------------
        private static bool _curToolResolved;
        private static FieldInfo _fCurTool;          // LudeonTK.DebugTools.curTool
        private static FieldInfo _fClickAction;      // LudeonTK.DebugTool.clickAction
        private static FieldInfo _fDragBox;          // RimWorld.Selector.dragBox
        private static FieldInfo _fDragBoxActive;    // RimWorld.DragBox.active (public bool field)

        private static bool _timeResolved;
        private static PropertyInfo _pFrameCount;    // UnityEngine.Time.frameCount

        private static bool _snapResolved;
        private static MethodInfo _mCandidates;      // PortUI.MobileSelectionController.Candidates(Vector3)
        private static PropertyInfo _pDrawPos;       // Verse.Thing.DrawPos (UnityEngine.Vector3)

        // --- state -------------------------------------------------------------
        private static int _lastFireClock = int.MinValue;
        private static bool _overrideActive;
        private static IntVec3 _overrideCell;        // what UI.MouseCell() reports during clickAction
        private static object _overrideMapPos;       // boxed UnityEngine.Vector3 for UI.MouseMapPosition()
        private static bool _snapWarned;
        private static int _candFailures;

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

        internal static void CancelArmedTool()
        {
            ClearTool();
            Refl.Info("debug tool cancelled");
        }

        private static void ClearTool()
        {
            try { _fCurTool?.SetValue(null, null); }
            catch (Exception e) { Refl.Err("failed to clear curTool: " + e.Message); }
        }

        /// <summary>
        /// clickAction is a public field (Action) in 1.7, but never assume: fall back to a
        /// same-named readable property if the field lookup failed.
        /// </summary>
        private static Delegate ClickActionOf(object tool)
        {
            if (tool == null) return null;
            Type t = tool.GetType();
            if (_fClickAction == null || _fClickAction.DeclaringType != t)
            {
                _fClickAction = Refl.F(t, "clickAction");
            }
            if (_fClickAction != null)
            {
                try
                {
                    Delegate d = _fClickAction.GetValue(tool) as Delegate;
                    if (d != null) return d;
                }
                catch { }
            }
            try
            {
                PropertyInfo p = Refl.Prop(t, "clickAction");
                if (p != null && p.CanRead) return p.GetValue(tool, null) as Delegate;
            }
            catch { }
            return null;
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

        private static string N(float v)
        {
            try { return v.ToString("0.##"); }
            catch { return "?"; }
        }

        /// <summary>Read x/z out of a Vector3 boxed as object (no UnityEngine reference, no cast).</summary>
        private static bool TryXZ(object vec3, out float x, out float z)
        {
            x = 0f; z = 0f;
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
                x = Convert.ToSingle(fx.GetValue(vec3));
                z = Convert.ToSingle(fz.GetValue(vec3));
                return true;
            }
            catch { x = 0f; z = 0f; return false; }
        }

        private static bool TryCellFrom(object vec3, out IntVec3 cell)
        {
            cell = default(IntVec3);
            float x, z;
            if (!TryXZ(vec3, out x, out z)) return false;
            cell = new IntVec3((int)Math.Round((double)x), 0, (int)Math.Round((double)z));
            return true;
        }

        /// <summary>How many Things sit on that cell (-1 when it cannot be read).</summary>
        private static int CountThingsAt(IntVec3 cell)
        {
            try
            {
                Map map = Find.CurrentMap;
                if (map == null) return -1;
                // Open coded bounds test: the Verse IntVec3.InBounds extension lives next to
                // overloads that mention UnityEngine types, and binding it would force a
                // compile-time UnityEngine reference (CS0012) that this mod must not carry.
                IntVec3 size = map.Size;
                if (cell.x < 0 || cell.z < 0 || cell.x >= size.x || cell.z >= size.z) return -1;
                return map.thingGrid.ThingsListAt(cell).Count;
            }
            catch { return -1; }
        }

        private static string DefName(Thing t)
        {
            try { return t.def != null ? t.def.defName : "?"; }
            catch { return "?"; }
        }

        /// <summary>
        /// First entry of PortUI.MobileSelectionController.Candidates(tapPos), or null.
        /// Candidates is the port's own "what did the user aim at" resolution: objects on the exact
        /// cell, plus - when that cell holds no Thing - every Thing within the 5x5 surrounding cells
        /// and a 28.8 px screen radius, sorted nearest first. Reusing it keeps dev tool taps
        /// consistent with what a normal tap would have selected.
        /// </summary>
        private static object CandidatesFirst(object tapPos)
        {
            if (tapPos == null) return null;
            if (!_snapResolved)
            {
                _snapResolved = true;
                Type t = Refl.TPrefer("PortUI", "PortUI.MobileSelectionController");
                _mCandidates = Refl.M(t, "Candidates");
                if (_mCandidates == null)
                    Refl.Warn("PortUI.MobileSelectionController.Candidates not found - tap snapping disabled");
            }
            if (_mCandidates == null) return null;

            object list;
            try
            {
                list = _mCandidates.Invoke(null, new object[] { tapPos });
                _candFailures = 0;
            }
            catch (Exception e)
            {
                // A wrong signature must not flood the log or be retried forever: give up after
                // three failures and keep the plain rounded-cell behaviour.
                if (++_candFailures < 3) return null;
                _mCandidates = null;
                Refl.Warn("Candidates invoke failed " + _candFailures + "x - tap snapping disabled: " + e.Message);
                return null;
            }

            IEnumerable items = list as IEnumerable;
            if (items == null) return null;
            try
            {
                foreach (object o in items) return o;
            }
            catch { }
            return null;
        }

        /// <summary>Thing.DrawPos, boxed. Virtual property: reflection dispatches to the override.</summary>
        private static object DrawPosOf(Thing thing)
        {
            try
            {
                if (_pDrawPos == null)
                {
                    Type tt = Refl.TPrefer("Assembly-CSharp", "Verse.Thing");
                    _pDrawPos = Refl.Prop(tt, "DrawPos");
                }
                if (_pDrawPos == null) return null;
                return _pDrawPos.GetValue(thing, null);
            }
            catch { return null; }
        }

        /// <summary>Run the armed debug tool at the tapped map position. Returns false only when no tool is armed.</summary>
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

            float tapX, tapZ;
            bool haveTap = TryXZ(tapPos, out tapX, out tapZ);
            IntVec3 tapCell;
            bool haveCell = TryCellFrom(tapPos, out tapCell);
            int things = haveCell ? CountThingsAt(tapCell) : -1;

            // Always logged: this is what tells "the tap landed on an empty cell" apart from
            // "the tap landed correctly but the snap picked the wrong thing".
            Refl.Info("tool fire tap=" + (haveTap ? "(" + N(tapX) + "," + N(tapZ) + ")" : "?")
                + " cell=" + (haveCell ? "(" + tapCell.x + "," + tapCell.z + ")" : "?")
                + " things=" + things);

            // Nearest-target snapping through PortUI's own candidate picker.
            Thing snapped = null;
            IntVec3 targetCell = tapCell;
            object targetPos = tapPos;
            try
            {
                snapped = CandidatesFirst(tapPos) as Thing;
                if (snapped != null)
                {
                    targetCell = snapped.Position;
                    object dp = DrawPosOf(snapped);
                    if (dp != null) targetPos = dp;
                    Refl.Info("tool snap -> " + DefName(snapped) + " at " + targetCell);
                }
            }
            catch (Exception e)
            {
                // Reflection trouble must never change behaviour: fall back to the raw tap.
                snapped = null;
                targetCell = tapCell;
                targetPos = tapPos;
                if (!_snapWarned)
                {
                    _snapWarned = true;
                    Refl.Warn("tap snapping disabled after reflection error: " + e.Message);
                }
            }

            if (haveTap)
            {
                _overrideCell = targetCell;
                _overrideMapPos = targetPos;
                _overrideActive = true;
            }
            try
            {
                action.DynamicInvoke();
                Refl.Info("debug tool fired at " + (haveTap ? targetCell.ToString() : "mouse cell")
                    + (snapped != null ? " (snapped)" : ""));
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
                _overrideMapPos = null;
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
        /// While a tool click runs, UI.MouseCell() must report the tapped (or snapped) cell. PortUI
        /// rewrites MouseCell for its designator preview; running at High priority keeps
        /// this override in front of it.
        /// </summary>
        internal static bool MouseCell_Prefix(ref IntVec3 __result)
        {
            if (!_overrideActive) return true;
            __result = _overrideCell;
            return false;
        }

        /// <summary>
        /// While a tool click runs, UI.MouseMapPosition() must report the exact tapped map position
        /// (or the snapped target's DrawPos). Most LudeonTK tools read the map position rather than
        /// the cell, and without this they see wherever the port last pushed the cursor - which is
        /// why a tap a few pixels off still missed.
        ///
        /// The prefix is generic on purpose: MouseMapPosition returns UnityEngine.Vector3, and this
        /// mod deliberately carries zero compile-time UnityEngine references (the CI guard rejects a
        /// UnityEngine assembly reference). Bootstrap closes this method over the live return type,
        /// so in IL the parameter really is 'ref UnityEngine.Vector3'. The value is handed back as a
        /// boxed Vector3 and unboxed here.
        /// </summary>
        internal static bool MouseMapPosition_Prefix<T>(ref T __result)
        {
            if (!_overrideActive) return true;
            object v = _overrideMapPos;
            if (v == null) return true;
            try
            {
                __result = (T)v;
                return false;
            }
            catch { return true; }
        }

        /// <summary>
        /// Long press on empty map normally opens the quick order wheel; while a dev tool is armed
        /// it must cancel the tool instead (touch equivalent of the right mouse button).
        /// </summary>
        internal static bool QuickOrderWheel_Prefix(object[] __args)
        {
            try
            {
                if (CurTool == null) return true;
                CancelArmedTool();
                return false;
            }
            catch (Exception e)
            {
                Refl.Err("QuickOrderWheel prefix: " + e.Message);
                return true;
            }
        }

        /// <summary>Do not fire the same armed tool twice when a real MouseDown arrives in the same frame.</summary>
        internal static bool DebugToolOnGUI_Prefix()
        {
            if (_lastFireClock == int.MinValue) return true;
            return Now() != _lastFireClock;
        }
    }
}
