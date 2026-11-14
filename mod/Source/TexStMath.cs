using System;

namespace AgentTouchFix
{
    /// <summary>
    /// Pure math for the icon-material _MainTex_ST fix. No reflection, no engine types, so it can be
    /// unit-tested on CI without the game.
    ///
    /// CutoutComplex.frag.glsl (Data/Core/Shaders/Map/CutoutComplex.frag.glsl) samples with
    ///
    ///     uv = vUv * _MainTex_ST.zw + _MainTex_ST.xy;     // offset in xy, scale in zw
    ///
    /// The engine's transform of the material property before it reaches the shader is
    /// (Waylith.Rendering/MaterialBindingResolver.cs:152-165)
    ///
    ///     if (flipV)      { y = 1 - y; w = -w; }
    ///     if (scaleFirst) { (x,y,z,w) = (z,w,x,y); }
    ///
    /// so writing "the identity the shader wants" into the property needs the inverse of that
    /// transform - which is what PropertyForIdentity computes.
    /// </summary>
    internal static class TexStMath
    {
        internal const float IdentX = 0f, IdentY = 0f, IdentZ = 1f, IdentW = 1f;

        /// <summary>The engine transform, exactly as MaterialBindingResolver.ResolveTextureST applies it.</summary>
        internal static void EngineTransform(ref float x, ref float y, ref float z, ref float w, bool flipV, bool scaleFirst)
        {
            if (flipV) { y = 1f - y; w = -w; }
            if (scaleFirst)
            {
                float nx = z, ny = w, nz = x, nw = y;
                x = nx; y = ny; z = nz; w = nw;
            }
        }

        /// <summary>Property value whose engine transform equals the identity (offset 0,0 / scale 1,1).</summary>
        internal static void PropertyForIdentity(out float x, out float y, out float z, out float w, bool flipV, bool scaleFirst)
        {
            x = IdentX; y = IdentY; z = IdentZ; w = IdentW;
            if (scaleFirst)
            {
                float nx = z, ny = w, nz = x, nw = y;
                x = nx; y = ny; z = nz; w = nw;
            }
            if (flipV) { y = 1f - y; w = -w; }
        }
    }
}
