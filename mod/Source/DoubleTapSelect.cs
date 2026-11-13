using System;
using System.Reflection;
using Verse;

namespace AgentTouchFix
{
    /// <summary>
    /// v7 B: double tapping a wall / workbench / any building must select every one of that kind on
    /// screen, the way a double click does on desktop.
    ///
    /// PortUI.MobileSelectionController.Tap has a double tap branch, but it is written as
    ///
    ///     same.def.category == ThingCategory.Item &amp;&amp; !same.def.neverMultiSelect
    ///
    /// (Item == 2). A building is ThingCategory.Building (== 3), so the branch can never be taken for
    /// one - a double tap just re-selects the single building. Vanilla does not restrict the
    /// category at all: RimWorld.Selector.SelectAllMatchingObjectUnderMouseOnScreen() picks the thing
    /// under the cursor (Pawn first, then any non neverMultiSelect Thing) and selects every
    /// equivalent thing in the screen rect.
    ///
    /// This runs from the existing Tap prefix (ToolPatches.Tap_Prefix), after the debug-tool path:
    ///   1. the tap cell is remembered; a second tap on the same cell < 400 ms later is a double tap,
    ///   2. PortUI's own candidate picker decides what the tap hit (so a tap a few px off the wall
    ///      still counts), and only a Building is taken over,
    ///   3. UI.MouseCell()/UI.MouseMapPosition() are pinned to that building for the duration of the
    ///      vanilla call (it reads both), then restored,
    ///   4. Find.Selector.SelectAllMatchingObjectUnderMouseOnScreen() does the rest.
    /// Nothing is cleared first and the selection is not touched when the call is unavailable.
    /// </summary>
    internal static class DoubleTapSelect
    {
        private const int WindowMs = 400;

        // --- state (tap path is single threaded: PortUI calls Tap from the Unity UI thread) ---
        private static Thing _lastThing;
        private static long _lastTapMs;

        private static bool _selResolved;
        private static PropertyInfo _pSelector;    // Verse.Find.Selector
        private static MethodInfo _mSelectAll;     // RimWorld.Selector.SelectAllMatchingObjectUnderMouseOnScreen

        /// <summary>
        /// True when this tap was consumed as a building double tap, i.e. the caller must skip the
        /// rest of MobileSelectionController.Tap. False means "carry on, nothing changed".
        /// </summary>
        internal static bool TryConsume(object[] args)
        {
            if (args == null || args.Length == 0 || args[0] == null) return false;

            // NOTE: MobileTouchInput calls Tap(pos, kind == Context, kind != Context), so selectOnly
            // is TRUE for every ordinary tap - it must not be used as a filter here. Only a long
            // press (Context) is excluded.
            bool longPress = args.Length > 1 && args[1] is bool && (bool)args[1];
            if (longPress)
            {
                Forget();   // a long press never starts a double tap
                return false;
            }

            // The thing is the honest key: the port fires the second tap as TouchSignal.DoubleTap
            // when two ups land < 300 ms and < 40 dp apart, and 40 dp of screen jitter is more than
            // one cell when zoomed in - so comparing cells would miss real double taps. Same thing
            // twice inside 400 ms is a double tap whatever the pixels did in between.
            Thing hit = null;
            try { hit = SnapGeometry.FirstCandidate(args[0]) as Thing; }
            catch (Exception e)
            {
                Refl.Warn("dbl-tap: candidate lookup failed: " + e.Message);
                Forget();
                return false;
            }

            long now = Environment.TickCount64;
            long delta = now - _lastTapMs;
            bool second = hit != null && _lastThing != null && ReferenceEquals(hit, _lastThing)
                && delta >= 0 && delta < WindowMs;

            _lastThing = hit;
            _lastTapMs = now;
            if (!second) return false;

            if (hit.def == null)
            {
                Forget();
                return false;
            }

            // ThingCategory in this build: None=0, Pawn=1, Item=2, Building=3 (Verse/ThingCategory.cs).
            // PortUI compares against Item, which is exactly the bug; the constant inlines as 3.
            if (hit.def.category != ThingCategory.Building)
            {
                Forget();
                return false;
            }

            if (hit.def.neverMultiSelect)
            {
                Forget();
                Refl.Info("dbl-tap building " + DefName(hit) + " is neverMultiSelect - vanilla single select kept");
                return false;
            }

            Forget();      // one select-all per double tap, even if the call below fails
            return SelectAll(hit);
        }

        private static void Forget()
        {
            _lastThing = null;
            _lastTapMs = 0;
        }

        private static bool SelectAll(Thing thing)
        {
            if (!_selResolved)
            {
                _selResolved = true;
                try
                {
                    _pSelector = Refl.Prop(typeof(Find), "Selector");
                    if (_pSelector != null) _mSelectAll = Refl.M(_pSelector.PropertyType, "SelectAllMatchingObjectUnderMouseOnScreen");
                }
                catch { }
                if (_mSelectAll == null)
                {
                    Refl.Warn("RimWorld.Selector.SelectAllMatchingObjectUnderMouseOnScreen not found -"
                        + " double tap on a building keeps the vanilla single select");
                }
            }
            if (_mSelectAll == null || _pSelector == null) return false;

            object selector = null;
            try { selector = _pSelector.GetValue(null, null); }
            catch { }
            if (selector == null) return false;

            int before = -1;
            try { before = Find.Selector.NumSelected; }
            catch { }

            ToolPatches.PushCellPin(thing.Position, SnapGeometry.DrawPosOf(thing));
            try
            {
                _mSelectAll.Invoke(selector, null);
            }
            catch (Exception e)
            {
                TargetInvocationException tie = e as TargetInvocationException;
                Refl.Err("dbl-tap select-all threw: " + (tie != null && tie.InnerException != null ? (object)tie.InnerException : e));
                return false;   // let the ordinary tap run instead
            }
            finally
            {
                ToolPatches.PopCellPin();
            }

            int after = -1;
            try { after = Find.Selector.NumSelected; }
            catch { }
            Refl.Info("dbl-tap building select-all -> " + DefName(thing) + " at " + thing.Position
                + " (selected " + before + " -> " + after + ")");
            return true;
        }

        private static string DefName(Thing t)
        {
            try { return t.def != null ? t.def.defName : "?"; }
            catch { return "?"; }
        }
    }
}
