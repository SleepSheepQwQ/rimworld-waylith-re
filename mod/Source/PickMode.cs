using System;
using System.Collections.Generic;
using System.IO;
using RimWorld;
using Verse;

namespace AgentTouchFix
{
    /// <summary>
    /// v10: files/game/agenttouchfix.pick decides what PortUI's own candidate search is allowed to
    /// do when a tap lands on a map cell.
    ///
    ///   port  (default, also used when the file is absent) - untouched, exactly as shipped.
    ///         PortUI.MobileSelectionController.Candidates takes everything selectable on the
    ///         tapped cell and, when that cell holds no Thing at all, additionally scans the 5x5
    ///         cells around it and inserts every Thing within 28.8 px of the tap in FRONT of the
    ///         cell's own contents, nearest first. That radius search - not the mod's developer-tool
    ///         snapping - is what makes a tap on empty ground "jump to the nearest entity".
    ///   exact - the radius search is skipped. When the tapped cell holds no selectable Thing the
    ///         candidate list contains only what is really on that cell (a zone or a plan, usually
    ///         nothing at all), so a tap on empty ground selects nothing and the raw tapped position
    ///         is used. When the cell does hold something the original method runs untouched - its
    ///         radius branch would not have fired there anyway.
    ///
    /// Independent of files/game/agenttouchfix.snap: snap is the developer-tool path only
    /// (ToolPatches.Fire). A dev tool tap still snaps onto the entity the finger aimed at, whatever
    /// this switch says.
    ///
    /// Read once from Bootstrap. The prefix is installed in both modes (so the log always names the
    /// mode); in port mode it returns true immediately and the game behaves as if the mod were not
    /// there. It either replaces the result with the tapped cell's own list (skip) or lets the
    /// original run - it never rewrites the arguments.
    /// </summary>
    internal static class PickMode
    {
        internal const int Port = 0;
        internal const int Exact = 1;

        /// <summary>Same folder as agenttouchfix.log / .diag / .snap / .tips.</summary>
        internal const string ModePath = "/data/data/com.ports1de.waylith/files/game/agenttouchfix.pick";

        private static int _mode = Port;         // port == ship behaviour

        private static int _skipped;             // taps whose radius search was skipped
        private static long _lastLogMs = -100000L;

        internal static int Mode { get { return _mode; } }

        internal static string ModeName(int m)
        {
            return m == Exact ? "exact" : "port";
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
                Refl.Warn("pick switch unreadable (" + ModePath + "): " + e.Message);
            }

            string s = raw == null ? "" : raw.Trim().ToLowerInvariant();
            int m = Port;
            if (s.StartsWith("exact")) m = Exact;
            else if (s.StartsWith("port")) m = Port;
            else if (s.Length > 0) Refl.Warn("pick switch '" + Short(s) + "' not understood (port/exact) - using port");

            _mode = m;
            Refl.Info("pick mode=" + ModeName(m)
                + (raw == null ? " (no " + ModePath + ", default port: PortUI's own radius search)" : " (" + ModePath + " = '" + Short(s) + "')"));
        }

        // ================================================================ PortUI.Candidates prefix

        /// <summary>
        /// Prefix for PortUI.MobileSelectionController.Candidates(Vector3) -&gt; List&lt;object&gt;.
        /// The parameter type is UnityEngine.Vector3 (a value type this mod refuses to reference at
        /// compile time), so the method is generic and is closed over the live parameter type in
        /// Bootstrap.PickPrefix(), exactly like ToolPatches.MouseMapPosition_Prefix&lt;T&gt;.
        ///
        /// In exact mode: iterate the tapped cell's own selectables (the same call the original makes
        /// first), and when none of them is a Thing, answer with that list and skip the original.
        /// Everything else - port mode, an unreadable position, no map, out of bounds, or any
        /// failure inside this method - falls through to the untouched game code.
        /// </summary>
        internal static bool Candidates_Prefix<T>(T position, ref List<object> __result)
        {
            if (_mode != Exact) return true;
            try
            {
                object boxed = position;                    // boxes UnityEngine.Vector3
                float x, z;
                if (!SnapGeometry.TryXZ(boxed, out x, out z)) return true;

                Map map = Find.CurrentMap;
                if (map == null) return true;

                // Same rounding as the original: IntVec3(Vector3) truncates, it does not round.
                IntVec3 cell = new IntVec3((int)x, 0, (int)z);
                if (!cell.InBounds(map)) return true;

                List<object> at = new List<object>();
                foreach (object o in Selector.SelectableObjectsAt(cell, map)) at.Add(o);

                bool hasThing = false;
                for (int i = 0; i < at.Count; i++)
                {
                    if (at[i] is Thing) { hasThing = true; break; }
                }
                // The original only reaches its radius branch when the tapped cell held no Thing.
                if (hasThing) return true;

                _skipped++;
                LogSkipped();
                __result = at;
                return false;
            }
            catch (Exception e)
            {
                Refl.Warn("pick: exact-cell check failed, PortUI kept in charge: " + e.Message);
                return true;
            }
        }

        /// <summary>At most one line per 2 s; the number is the running total for this session.</summary>
        private static void LogSkipped()
        {
            long now;
            try { now = Environment.TickCount64; } catch { now = 0L; }
            if (now - _lastLogMs < 2000L) return;
            _lastLogMs = now;
            Refl.Info("pick mode=exact skipped-radius=" + _skipped);
        }
    }
}
