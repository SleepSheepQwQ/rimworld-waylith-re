using System;

namespace AgentDevTools
{
    /// <summary>
    /// The whole decision of this mod, as three pure functions.
    ///
    /// Philosophy in one line: **the finger's cell is the answer; snapping is a named exception that
    /// must be asked for, bounded, and explained.**
    ///
    /// Everything here is deliberately free of game types so it can be unit-tested on CI without the
    /// port, the engine, or a device. The Harmony layer only measures (where did the finger land, what
    /// did PortUI report) and then asks this file what to do.
    /// </summary>
    internal static class SnapPolicy
    {
        // ---- modes -------------------------------------------------------------------------
        /// <summary>No patches at all: the port behaves exactly as shipped.</summary>
        internal const int Off = 0;

        /// <summary>Snap only when the tap lands on the object's own footprint (default).</summary>
        internal const int Auto = 1;

        /// <summary>Always take the first thing PortUI reports, even off-footprint (legacy behaviour).</summary>
        internal const int Always = 2;

        internal static string Name(int mode)
        {
            return mode == Off ? "off" : (mode == Always ? "always" : "auto");
        }

        // ---- geometry ----------------------------------------------------------------------
        /// <summary>Snap tolerance in dp: the size of a fingertip, not a cell.</summary>
        internal const float SpecDp = 16f;

        /// <summary>When zoomed far out a dp can be sub-pixel; never go below this.</summary>
        internal const float MinRadiusPx = 6f;

        /// <summary>Hard ceiling so a bad density reading cannot make the tolerance swallow a room.</summary>
        internal const float MaxRadiusPx = 64f;

        /// <summary>dp * density, clamped. Pure: same inputs, same output, no globals.</summary>
        internal static float RadiusPx(float densityScale)
        {
            float r = SpecDp * densityScale;
            if (float.IsNaN(r) || r < MinRadiusPx) r = MinRadiusPx;
            if (r > MaxRadiusPx) r = MaxRadiusPx;
            return r;
        }

        /// <summary>
        /// The one rule this mod exists for: a hit-test result may only be used when the tap was aimed
        /// at something. An empty candidate list means "PortUI sees nothing here" - that is a
        /// deliberate aim at the terrain, and it must be kept as a raw cell.
        /// </summary>
        internal static bool Accept(int mode, bool candidateListHadEntries, bool hitWithinRadius)
        {
            if (mode == Off) return false;
            if (!candidateListHadEntries) return false;
            if (mode == Always) return true;
            return hitWithinRadius;
        }
    }
}
