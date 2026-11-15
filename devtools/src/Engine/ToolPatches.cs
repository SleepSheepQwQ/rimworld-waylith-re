using System;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace AgentDevTools
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
    ///
    /// v6: that forgiveness was too wide for tools that must be exact (clear terrain, spawn). A
    /// candidate is only accepted when the tap lies inside the thing's own footprint or within
    /// 16 dp (never more than half a cell) of its projected DrawPos; otherwise the raw tap is used.
    /// See SnapGeometry for the modes (files/game/agenttouchfix.snap: always / auto / off).
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

        // --- state -------------------------------------------------------------
        private static int _lastFireClock = int.MinValue;
        private static bool _overrideActive;
        private static IntVec3 _overrideCell;        // what UI.MouseCell() reports during clickAction
        private static object _overrideMapPos;       // boxed UnityEngine.Vector3 for UI.MouseMapPosition()
        private static bool _snapWarned;
        private static bool _multiWarned;

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

        // --- v7: nested pin (the double-tap select-all call needs the same override) -------

        private static bool _savedActive;
        private static IntVec3 _savedCell;
        private static object _savedMapPos;

        /// <summary>
        /// Pin UI.MouseCell()/UI.MouseMapPosition() to a cell for the duration of a vanilla call,
        /// keeping whatever the outer scope (a running tool click) had pinned.
        /// </summary>
        internal static void PushCellPin(IntVec3 cell, object mapPos)
        {
            _savedActive = _overrideActive;
            _savedCell = _overrideCell;
            _savedMapPos = _overrideMapPos;

            _overrideCell = cell;
            if (mapPos != null) _overrideMapPos = mapPos;
            _overrideActive = true;
        }

        internal static void PopCellPin()
        {
            _overrideCell = _savedCell;
            _overrideMapPos = _savedMapPos;
            _overrideActive = _savedActive;
            _savedMapPos = null;
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
            // v6: floor, not round. A map position of 123.7 belongs to cell 123 (this is exactly what
            // UI.MouseMapPosition().ToIntVec3() does); rounding sent taps in the lower half of a cell
            // into the next cell, which is where the "empty ground snaps onto the neighbouring item"
            // reports started.
            cell = new IntVec3((int)Math.Floor((double)x), 0, (int)Math.Floor((double)z));
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
        /// v6 target resolution. PortUI's candidate picker (exact cell first, then the 5x5
        /// neighbourhood inside a 28.8 px radius, nearest first) is only used as a *proposal*:
        /// SnapGeometry accepts it when the tap really landed on the thing (own footprint, or within
        /// 16 dp of its projected DrawPos), and drops it otherwise. With no candidate at all and a
        /// single thing selected, the selected thing is used (tools that answer to the selection).
        /// Mode off disables all of this; mode always restores the v5 unconditional snap.
        /// </summary>
        private static void ResolveTarget(object tapPos, bool haveTap, float tapX, float tapZ, IntVec3 tapCell,
            out Thing snapped, out IntVec3 targetCell, out object targetPos, out string how)
        {
            int mode = SnapGeometry.Mode;
            snapped = null;
            targetCell = tapCell;
            targetPos = tapPos;
            how = null;

            if (!haveTap || mode == SnapGeometry.Off) return;

            bool candEmpty = true;
            snapped = SnapGeometry.Resolve(tapPos, tapX, tapZ, out how, out candEmpty);

            // An empty candidate list means the finger aimed at terrain. Never substitute the
            // current selection here - that is what made "tap empty ground" hit a pawn/bed.

            if (snapped != null)
            {
                // keep the cell the finger is on when it lies inside the footprint; a 2x3 bench
                // must not turn a tap on its right edge into its min corner
                targetCell = tapCell;
                try
                {
                    CellRect rect = GenAdj.OccupiedRect(snapped);
                    if (!rect.Contains(tapCell)) targetCell = snapped.Position;
                }
                catch { targetCell = snapped.Position; }

                object dp = SnapGeometry.DrawPosOf(snapped);
                if (dp != null) targetPos = dp;

                // single decision point, mirroring the unit-tested policy
                if (!SnapPolicy.Accept(mode, !candEmpty, true)) { snapped = null; how = null; }
            }
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
                + " things=" + things + " snap=" + SnapGeometry.ModeName(SnapGeometry.Mode));

            // v6: the candidate is only accepted when the tap really aimed at it.
            Thing snapped = null;
            IntVec3 targetCell = tapCell;
            object targetPos = tapPos;
            string how = null;
            try
            {
                ResolveTarget(tapPos, haveTap, tapX, tapZ, tapCell, out snapped, out targetCell, out targetPos, out how);
                if (snapped != null)
                    Refl.Info("tool snap -> " + DefName(snapped) + " at " + targetCell + " (" + how + ")");
                else if (SnapGeometry.Mode != SnapGeometry.Off)
                    Refl.Info("tool snap none - using the raw tap");
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
                TryClampCell(ref targetCell);
                _overrideCell = targetCell;
                _overrideMapPos = targetPos;
                _overrideActive = true;
            }
            try
            {
                action.DynamicInvoke();
                Refl.Info("debug tool fired at " + (haveTap ? targetCell.ToString() : "mouse cell")
                    + (snapped != null ? " (snapped: " + how + ")" : " (raw tap)"));
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

        /// <summary>Clamp to the current map: a tap at the screen edge can land outside it.</summary>
        private static bool TryClampCell(ref IntVec3 c)
        {
            Map map = Find.CurrentMap;
            if (map == null) return false;
            if (c.x < 0) c.x = 0;
            if (c.z < 0) c.z = 0;
            if (c.x >= map.Size.x) c.x = map.Size.x - 1;
            if (c.z >= map.Size.z) c.z = map.Size.z - 1;
            return true;
        }

        private static PropertyInfo _pTouchCount;

        /// <summary>Fingers on the screen right now, read reflectively (no UnityEngine dependency).</summary>
        internal static int TouchCount()
        {
            try
            {
                if (_pTouchCount == null)
                {
                    Type input = Refl.T("UnityEngine.Input");
                    if (input != null)
                        _pTouchCount = input.GetProperty("touchCount",
                            BindingFlags.Public | BindingFlags.Static);
                }
                object v = _pTouchCount == null ? null : _pTouchCount.GetValue(null);
                return v is int i ? i : 1;
            }
            catch { return 1; }
        }

        // ===================== Harmony patches =================================

        /// <summary>
        /// Real-mouse fallback: while a dev tool is armed the selector must not treat the
        /// click as a selection / drag-box start. Mirrors vanilla semantics, where
        /// DebugTool.Use() keeps DragBox.active false.
        /// </summary>
        [HarmonyPriority(Priority.First)]
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
        ///
        /// v7: with no tool armed, a double tap on a *building* is handled here as well, because
        /// PortUI's own double tap branch only ever matches ThingCategory.Item.
        /// </summary>
        [HarmonyPriority(Priority.First)]
        internal static bool Tap_Prefix(object[] __args)
        {
            if (__args == null || __args.Length == 0) return true;
            try
            {
                // A pinch must never fire a tool: with two fingers down the tap that the port
                // reports is not a tap the player meant (this is the "stuck/misfire during zoom"
                // class of bug every mobile port hits).
                int fingers = TouchCount();
                if (fingers > 1)
                {
                    if (!_multiWarned)
                    {
                        _multiWarned = true;
                        Refl.Info("tap ignored: " + fingers + " fingers down (gesture, not a tap)");
                    }
                    return true;
                }

                if (CurTool == null) return true;   // no dev tool armed: nothing for this mod to do

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
        [HarmonyPriority(Priority.First)]
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
        [HarmonyPriority(Priority.First)]
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
