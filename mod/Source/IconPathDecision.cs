using System;

namespace AgentTouchFix
{
    /// <summary>
    /// Pure routing decision: must a build-menu icon be drawn through the plain texture path instead of
    /// its CutoutComplexUI material? Lives in its own file so CI can unit-test it without Harmony.
    /// </summary>
    internal static class IconPath
    {
        internal const string Plain = "plain";
        internal const string Material = "material";

        internal static bool UsePlain(string materialName, string mode)
        {
            if (mode != null && mode.Trim().ToLowerInvariant() == Material) return false;
            if (string.IsNullOrEmpty(materialName)) return false;
            return materialName.StartsWith("CutoutComplexUI", StringComparison.Ordinal);
        }
    }
}
