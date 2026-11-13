using System;
using System.Collections;
using System.IO;
using System.Reflection;
using Verse;

namespace AgentTouchFix
{
    /// <summary>
    /// v6: decides *whether* a tapped map position really aimed at the candidate PortUI offers.
    ///
    /// v4/v5 snapped unconditionally to the first entry of
    /// PortUI.MobileSelectionController.Candidates(tapPos), which searches the exact cell and then
    /// the whole 5x5 neighbourhood inside a 28.8 px screen radius. That radius is designed for
    /// selection ("tap near it and we select it"), but a dev tool must be exact: emptying terrain or
    /// spawning a thing next to a dropped item jumped onto the item instead of the empty cell.
    ///
    /// v6 accepts a candidate only when
    ///   (a) the tapped point lies inside the thing's own drawn footprint (its occupied cell rect in
    ///       map space, which is what the graphic covers), or
    ///   (b) the tapped point is at most 16 dp away from the projected screen position of the
    ///       thing's DrawPos - and never more than half a cell on screen, so that zooming out cannot
    ///       widen the snap into neighbouring cells.
    /// Otherwise the raw tapped cell/position is used (v4 behaviour minus the rounding loss).
    ///
    /// Three modes, read once at startup from files/game/agenttouchfix.snap:
    ///   always - v5 behaviour: first candidate wins, no hit test (also used when the file is absent
    ///            *and* the caller wants the old semantics)   [opt-in only]
    ///   auto   - the hit test above (default)
    ///   off    - no snapping at all: raw tap cell + exact tap position
    /// </summary>
    internal static class SnapGeometry
    {
        internal const int Off = 0;
        internal const int Auto = 1;
        internal const int Always = 2;

        /// <summary>Same folder as agenttouchfix.log / agenttouchfix.diag.</summary>
        internal const string ModePath = "/data/data/com.ports1de.waylith/files/game/agenttouchfix.snap";

        private const float MaxDp = 16f;        // spec: 16 dp
        private const float MinRadiusPx = 6f;   // never smaller than this, even when zoomed far out
        private const float MaxRadiusPx = 64f;  // hard ceiling, whatever the density reports

        private static int _mode = -1;          // -1 = not read yet

        internal static int Mode
        {
            get { return _mode < 0 ? Auto : _mode; }
        }

        internal static string ModeName(int m)
        {
            return m == Off ? "off" : (m == Always ? "always" : "auto");
        }

        private static string Short(string s)
        {
            if (s == null) return "";
            s = s.Trim();
            return s.Length <= 20 ? s : s.Substring(0, 20) + "...";
        }

        /// <summary>Read once from Bootstrap, before the first tap can happen.</summary>
        internal static void ReadMode()
        {
            string raw = null;
            try
            {
                if (File.Exists(ModePath)) raw = File.ReadAllText(ModePath);
            }
            catch (Exception e)
            {
                Refl.Warn("snap switch unreadable (" + ModePath + "): " + e.Message);
            }

            string s = raw == null ? "" : raw.Trim().ToLowerInvariant();
            int m = Auto;
            if (s.StartsWith("always")) m = Always;
            else if (s.StartsWith("off")) m = Off;
            else if (s.StartsWith("auto")) m = Auto;
            else if (s.Length > 0) Refl.Warn("snap switch '" + Short(s) + "' not understood (always/auto/off) - using auto");

            _mode = m;
            Refl.Info("snap mode=" + ModeName(m)
                + (raw == null ? " (no " + ModePath + ", default auto)" : " (" + ModePath + " = '" + Short(s) + "')"));
        }

        // ================================================================ PortUI.Candidates

        private static bool _candResolved;
        private static MethodInfo _mCandidates;    // PortUI.MobileSelectionController.Candidates(Vector3) -> List<object>
        private static int _candFailures;

        /// <summary>The raw candidate list (untyped: List&lt;object&gt; lives in PortUI, which this mod never references).</summary>
        private static IEnumerable Candidates(object tapPos)
        {
            if (tapPos == null) return null;
            if (!_candResolved)
            {
                _candResolved = true;
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
                // A wrong signature must not be retried per tap forever.
                if (++_candFailures < 3) return null;
                _mCandidates = null;
                Refl.Warn("Candidates invoke failed " + _candFailures + "x - tap snapping disabled: " + e.Message);
                return null;
            }
            return list as IEnumerable;
        }

        /// <summary>
        /// Pick the candidate the tap really aimed at.
        /// how describes the accepting rule ("rect", "near N/Mpx", "always") for the log;
        /// candidatesEmpty is true when PortUI offered no Thing at all (drives the selection fallback).
        /// </summary>
        internal static Thing Resolve(object tapPos, float tapX, float tapZ, out string how, out bool candidatesEmpty)
        {
            how = null;
            // Only "PortUI answered and named no Thing" counts as empty; a failed/unavailable
            // candidate list must not silently fall back to whatever happens to be selected.
            candidatesEmpty = false;

            IEnumerable items = Candidates(tapPos);
            if (items == null) return null;

            int mode = Mode;
            Thing found = null;
            bool sawEntry = false;
            try
            {
                foreach (object o in items)
                {
                    sawEntry = true;
                    Thing t = o as Thing;
                    if (t == null) continue;

                    if (mode == Always)
                    {
                        found = t;
                        how = "always";
                        break;
                    }

                    string h = Hits(t, tapX, tapZ);
                    if (h != null)
                    {
                        found = t;
                        how = h;
                        break;
                    }
                }
            }
            catch (Exception e)
            {
                Refl.Warn("candidate scan failed: " + e.Message);
                return null;
            }

            // Empty answer = PortUI knows of nothing around the tap -> the selection may stand in.
            // A non-empty list whose entries all failed the hit test is a deliberate "not aimed at
            // anything": keep the raw tap instead of hijacking it for the selected thing.
            candidatesEmpty = !sawEntry;
            return found;
        }

        /// <summary>Return the accepting rule name, or null when the tap did not aim at this thing.</summary>
        private static string Hits(Thing t, float tapX, float tapZ)
        {
            CellRect r;
            try { r = GenAdj.OccupiedRect(t); }
            catch { r = new CellRect(t.Position.x, t.Position.z, 1, 1); }

            // (a) the tapped point is inside the thing's own footprint - this is the drawn rect of a
            // single cell thing, and the full multi cell rect of a building.
            if (tapX >= r.minX && tapX < r.maxX + 1 && tapZ >= r.minZ && tapZ < r.maxZ + 1) return "rect";

            // (b) or it is within 16 dp of the projected DrawPos (never more than half a cell).
            float cx, cz;
            if (!TryXZ(DrawPosOf(t), out cx, out cz)) return null;
            float tx, ty, dx, dy;
            if (!Project(tapX, tapZ, out tx, out ty)) return null;
            if (!Project(cx, cz, out dx, out dy)) return null;

            float ddx = tx - dx, ddy = ty - dy;
            float d = (float)Math.Sqrt(ddx * ddx + ddy * ddy);
            float radius = RadiusPx(cx, cz);
            if (d > radius) return null;
            return "near " + N(d) + "/" + N(radius) + "px";
        }

        private static string N(float v)
        {
            try { return v.ToString("0.#"); }
            catch { return "?"; }
        }

        // ================================================================ world -> screen

        private static bool _projResolved;
        private static PropertyInfo _pCamera;          // Verse.Find.Camera (UnityEngine.Camera)
        private static PropertyInfo _pPixelHeight;     // Camera.pixelHeight
        private static PropertyInfo _pScreenDpi;       // UnityEngine.Screen.dpi
        private static MethodInfo _mWorldToScreen;     // Camera.WorldToScreenPoint(Vector3)
        private static ConstructorInfo _vec3Ctor;      // new Vector3(float, float, float)
        private static bool _projWarned;

        private static void ResolveProjection()
        {
            if (_projResolved) return;
            _projResolved = true;
            try
            {
                _pCamera = Refl.Prop(typeof(Verse.Find), "Camera");
                if (_pCamera == null) Refl.Warn("Verse.Find.Camera not found - snapping falls back to footprint only");

                Type screen = Refl.T("UnityEngine.Screen");
                _pScreenDpi = Refl.Prop(screen, "dpi");
            }
            catch (Exception e)
            {
                Refl.Warn("projection setup failed: " + e.Message);
            }
        }

        internal static bool Project(float x, float z, out float sx, out float sy)
        {
            sx = 0f; sy = 0f;
            ResolveProjection();
            if (_pCamera == null) return false;

            object cam;
            try { cam = _pCamera.GetValue(null, null); }
            catch { return false; }
            if (cam == null) return false;

            if (_mWorldToScreen == null)
            {
                try
                {
                    MethodInfo[] all = cam.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance);
                    for (int i = 0; i < all.Length; i++)
                    {
                        if (all[i].Name == "WorldToScreenPoint" && all[i].GetParameters().Length == 1)
                        {
                            _mWorldToScreen = all[i];
                            break;
                        }
                    }
                }
                catch { }
                if (_mWorldToScreen == null)
                {
                    if (!_projWarned) { _projWarned = true; Refl.Warn("Camera.WorldToScreenPoint not found - snapping falls back to footprint only"); }
                    return false;
                }
                _pPixelHeight = Refl.Prop(cam.GetType(), "pixelHeight");
                Type vt = _mWorldToScreen.GetParameters()[0].ParameterType;
                if (vt != null && vt.IsValueType)
                {
                    try { _vec3Ctor = vt.GetConstructor(new Type[] { typeof(float), typeof(float), typeof(float) }); }
                    catch { }
                }
                if (_vec3Ctor == null)
                {
                    if (!_projWarned) { _projWarned = true; Refl.Warn("Vector3(float,float,float) ctor not found - snapping falls back to footprint only"); }
                    return false;
                }
            }

            object world;
            try { world = _vec3Ctor.Invoke(new object[] { x, 0f, z }); }
            catch { return false; }

            object sp;
            try { sp = _mWorldToScreen.Invoke(cam, new object[] { world }); }
            catch (Exception e)
            {
                Refl.Warn("WorldToScreenPoint failed (" + e.Message + ") - using footprint only");
                _mWorldToScreen = null;   // try to rebuild once next tap
                _projWarned = true;
                return false;
            }

            float px, py, pz;
            if (!TryXYZ(sp, out px, out py, out pz)) return false;

            int h = 0;
            try { if (_pPixelHeight != null) h = Convert.ToInt32(_pPixelHeight.GetValue(cam, null)); }
            catch { }
            sx = px;
            sy = h > 0 ? h - py : py;     // Unity screen y is bottom up, touch coords are top down
            return true;
        }

        /// <summary>16 dp in screen pixels, capped so a zoomed out view never snaps across cells.</summary>
        private static float RadiusPx(float cx, float cz)
        {
            float cellPx = 0f;
            float ax, ay, bx, by;
            if (Project(cx, cz, out ax, out ay) && Project(cx + 1f, cz, out bx, out by))
            {
                float dx = bx - ax, dy = by - ay;
                cellPx = (float)Math.Sqrt(dx * dx + dy * dy);
            }

            float r = MaxDp * DpScale();
            if (cellPx > 1f) r = Math.Min(r, cellPx * 0.5f);
            if (r < MinRadiusPx) r = MinRadiusPx;
            if (r > MaxRadiusPx) r = MaxRadiusPx;
            return r;
        }

        private static float _dpScale = -1f;

        private static float DpScale()
        {
            if (_dpScale > 0f) return _dpScale;
            float dpi = 0f;
            try { if (_pScreenDpi != null) dpi = Convert.ToSingle(_pScreenDpi.GetValue(null, null)); }
            catch { }
            float s = dpi > 1f ? dpi / 160f : 1f;     // Android: 160 dpi == 1 dp
            if (s < 0.75f) s = 0.75f;
            if (s > 3.5f) s = 3.5f;
            _dpScale = s;
            Refl.Info("snap: density scale " + s.ToString("0.##") + " (" + (dpi > 0f ? dpi.ToString("0") + " dpi" : "no Screen.dpi") + ") -> 16dp = " + (16f * s).ToString("0.#") + "px before the half cell cap");
            return _dpScale;
        }

        // ================================================================ small helpers

        private static bool _drawPosResolved;
        private static PropertyInfo _pDrawPos;

        /// <summary>Thing.DrawPos as a boxed Vector3 (no compile time UnityEngine reference).</summary>
        internal static object DrawPosOf(Thing thing)
        {
            try
            {
                if (!_drawPosResolved)
                {
                    _drawPosResolved = true;
                    _pDrawPos = Refl.Prop(Refl.TPrefer("Assembly-CSharp", "Verse.Thing"), "DrawPos");
                    if (_pDrawPos == null) Refl.Warn("Verse.Thing.DrawPos not found - snap distance test degraded to footprint only");
                }
                if (_pDrawPos == null) return null;
                return _pDrawPos.GetValue(thing, null);
            }
            catch { return null; }
        }

        /// <summary>Read x/z out of a Vector3 boxed as object (no UnityEngine reference, no cast).</summary>
        internal static bool TryXZ(object vec3, out float x, out float z)
        {
            x = 0f; z = 0f;
            if (vec3 == null) return false;
            float y;
            return TryXYZ(vec3, out x, out y, out z);
        }

        private static bool TryXYZ(object vec3, out float x, out float y, out float z)
        {
            x = 0f; y = 0f; z = 0f;
            if (vec3 == null) return false;
            Type t = vec3.GetType();
            FieldInfo fx = null, fy = null, fz = null;
            try
            {
                fx = t.GetField("x", BindingFlags.Public | BindingFlags.Instance);
                fy = t.GetField("y", BindingFlags.Public | BindingFlags.Instance);
                fz = t.GetField("z", BindingFlags.Public | BindingFlags.Instance);
            }
            catch { }
            if (fx == null || fz == null) return false;
            try
            {
                x = Convert.ToSingle(fx.GetValue(vec3));
                z = Convert.ToSingle(fz.GetValue(vec3));
                if (fy != null) y = Convert.ToSingle(fy.GetValue(vec3));
                return true;
            }
            catch { x = 0f; y = 0f; z = 0f; return false; }
        }

        /// <summary>Find.Selector.SingleSelectedThing, or null (read through reflection: no assumption about visibility).</summary>
        internal static Thing SelectedThing()
        {
            try
            {
                if (_pSelector == null)
                {
                    _pSelector = Refl.Prop(typeof(Verse.Find), "Selector");
                    if (_pSelector == null) { Refl.Warn("Verse.Find.Selector not found - selection fallback disabled"); return null; }
                    _pSingleSelected = Refl.Prop(_pSelector.PropertyType, "SingleSelectedThing");
                    if (_pSingleSelected == null) { Refl.Warn("Selector.SingleSelectedThing not found - selection fallback disabled"); return null; }
                }
                object sel = _pSelector.GetValue(null, null);
                if (sel == null) return null;
                return _pSingleSelected.GetValue(sel, null) as Thing;
            }
            catch { return null; }
        }

        private static PropertyInfo _pSelector;
        private static PropertyInfo _pSingleSelected;
    }
}
