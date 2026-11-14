using System;

namespace AgentTouchFix.Tests
{
    /// <summary>
    /// Replica of the fragment stage of CutoutComplexUI, transcribed from
    /// Data/Core/Shaders/Map/CutoutComplex.frag.glsl on the device:
    ///
    ///     vec2 uv = vUv * uMaterial._MainTex_ST.zw + uMaterial._MainTex_ST.xy;
    ///     vec4 f = texture(_MainTex, uv);
    ///     vec4 colPower = texture(_MaskTex, uv);
    ///     colPower.rg *= MaskGate(colPower);
    ///     vec4 colMultiplier = colPower.r * (uMaterial._Color * vColor) + (1.0 - colPower.r);
    ///     f *= colMultiplier;
    ///     vec4 colTwoMultiplier = colPower.g * uMaterial._ColorTwo + (1.0 - colPower.g);
    ///     f *= colTwoMultiplier;
    ///     if (f.a < 0.5) discard;
    ///     outColor = f;
    ///
    /// Only what decides "does anything get drawn" is modelled: uv, the main-texture alpha, the mask
    /// (black mask = colPower 0 = no multiplication) and the alpha test.
    /// </summary>
    internal static class CutoutComplexUiFrag
    {
        internal struct Rgba
        {
            public float R, G, B, A;
            public Rgba(float r, float g, float b, float a) { R = r; G = g; B = b; A = a; }
        }

        internal static void Uv(float u, float v, float stx, float sty, float stz, float stw,
                               out float ou, out float ov)
        {
            ou = u * stz + stx;
            ov = v * stw + sty;
        }

        /// <summary>Clamp-to-edge sample of a square texture given as row-major RGBA.</summary>
        internal static Rgba Sample(Rgba[,] tex, float u, float v)
        {
            int w = tex.GetLength(0), h = tex.GetLength(1);
            int x = (int)MathF.Floor(u * w);
            int y = (int)MathF.Floor(v * h);
            if (x < 0) x = 0; if (x > w - 1) x = w - 1;
            if (y < 0) y = 0; if (y > h - 1) y = h - 1;
            return tex[x, y];
        }

        /// <summary>True when the fragment survives (is drawn).</summary>
        internal static bool Survives(Rgba[,] main, Rgba[,] mask, float u, float v,
                                      float stx, float sty, float stz, float stw,
                                      Rgba color, Rgba colorTwo, Rgba vColor)
        {
            Uv(u, v, stx, sty, stz, stw, out float uu, out float vv);
            Rgba f = Sample(main, uu, vv);
            Rgba colPower = Sample(mask, uu, vv);

            float m1r = colPower.R * (color.R * vColor.R) + (1f - colPower.R);
            float m1g = colPower.R * (color.G * vColor.G) + (1f - colPower.R);
            float m1b = colPower.R * (color.B * vColor.B) + (1f - colPower.R);
            float m1a = colPower.R * (color.A * vColor.A) + (1f - colPower.R);
            f = new Rgba(f.R * m1r, f.G * m1g, f.B * m1b, f.A * m1a);

            float m2r = colPower.G * colorTwo.R + (1f - colPower.G);
            float m2g = colPower.G * colorTwo.G + (1f - colPower.G);
            float m2b = colPower.G * colorTwo.B + (1f - colPower.G);
            float m2a = colPower.G * colorTwo.A + (1f - colPower.G);
            f = new Rgba(f.R * m2r, f.G * m2g, f.B * m2b, f.A * m2a);

            return f.A >= 0.5f;
        }
    }
}
