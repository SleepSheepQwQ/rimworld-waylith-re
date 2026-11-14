using System;
using Xunit;

namespace AgentTouchFix.Tests
{
    public class TexStMathTests
    {
        // ---- the fix's math -------------------------------------------------------------

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public void PropertyForIdentity_SurvivesTheEngineTransform(bool flipV, bool scaleFirst)
        {
            TexStMath.PropertyForIdentity(out float x, out float y, out float z, out float w, flipV, scaleFirst);
            TexStMath.EngineTransform(ref x, ref y, ref z, ref w, flipV, scaleFirst);

            Assert.Equal(0f, x, 4);
            Assert.Equal(0f, y, 4);
            Assert.Equal(1f, z, 4);
            Assert.Equal(1f, w, 4);
        }

        [Fact]
        public void Identity_IsWrittenVerbatimWhenTheEngineDoesNotTransform()
        {
            TexStMath.PropertyForIdentity(out float x, out float y, out float z, out float w, false, false);
            Assert.Equal((0f, 0f, 1f, 1f), (x, y, z, w));
        }

        // ---- the shader's uv convention -------------------------------------------------

        [Fact]
        public void Uv_UsesZwAsScaleAndXyAsOffset()
        {
            // _MainTex_ST = (offset.x, offset.y, scale.x, scale.y)
            CutoutComplexUiFrag.Uv(0.25f, 0.75f, 0f, 0f, 1f, 1f, out float u, out float v);
            Assert.Equal(0.25f, u, 4);
            Assert.Equal(0.75f, v, 4);

            // a Unity-order value (scale=1, offset=0) collapses uv onto the offset corner
            CutoutComplexUiFrag.Uv(0.25f, 0.75f, 1f, 1f, 0f, 0f, out u, out v);
            Assert.Equal(1f, u, 4);
            Assert.Equal(1f, v, 4);
        }

        // ---- end-to-end: does a tile get drawn? ----------------------------------------

        /// <summary>Opaque icon whose last texel is transparent - the real layout of
        /// RoyalBed_south (95.6% opaque) and NutrientDispenser_north (92.4% opaque).</summary>
        private static CutoutComplexUiFrag.Rgba[,] Icon()
        {
            int n = 8;
            var t = new CutoutComplexUiFrag.Rgba[n, n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                    t[x, y] = new CutoutComplexUiFrag.Rgba(0.6f, 0.4f, 0.2f, 1f);
            t[n - 1, n - 1] = new CutoutComplexUiFrag.Rgba(0f, 0f, 0f, 0f);
            return t;
        }

        private static CutoutComplexUiFrag.Rgba[,] BlackMask()
        {
            int n = 8;
            var t = new CutoutComplexUiFrag.Rgba[n, n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                    t[x, y] = new CutoutComplexUiFrag.Rgba(0f, 0f, 0f, 1f);
            return t;
        }

        private static int Drawn(float stx, float sty, float stz, float stw)
        {
            var icon = Icon();
            var mask = BlackMask();
            var white = new CutoutComplexUiFrag.Rgba(1f, 1f, 1f, 1f);
            var stuff = new CutoutComplexUiFrag.Rgba(0.522f, 0.38f, 0.263f, 1f);
            int drawn = 0;
            for (int i = 0; i < 8; i++)
                for (int j = 0; j < 8; j++)
                {
                    float u = (i + 0.5f) / 8f, v = (j + 0.5f) / 8f;
                    if (CutoutComplexUiFrag.Survives(icon, mask, u, v, stx, sty, stz, stw,
                            white, white, stuff))
                        drawn++;
                }
            return drawn;
        }

        [Fact]
        public void DefaultSt_DrawsAlmostTheWholeTile()
        {
            // the shader annotation default: (0,0,1,1)
            Assert.True(Drawn(0f, 0f, 1f, 1f) >= 60);
        }

        [Fact]
        public void UnityOrderSt_DiscardsEveryFragment()
        {
            // what a material holding (scale=1, offset=0) hands this shader: uv pinned at (1,1),
            // which lands on the transparent last texel -> discard -> blank tile, no error anywhere.
            Assert.Equal(0, Drawn(1f, 1f, 0f, 0f));
        }

        [Fact]
        public void PropertyForIdentity_DrawsAlmostTheWholeTile_ForEveryShaderFlagCombination()
        {
            foreach (bool flipV in new[] { false, true })
                foreach (bool scaleFirst in new[] { false, true })
                {
                    TexStMath.PropertyForIdentity(out float x, out float y, out float z, out float w,
                        flipV, scaleFirst);
                    TexStMath.EngineTransform(ref x, ref y, ref z, ref w, flipV, scaleFirst);
                    Assert.True(Drawn(x, y, z, w) >= 60,
                        $"flipV={flipV} scaleFirst={scaleFirst} drew {Drawn(x, y, z, w)}/64");
                }
        }
    }
}
