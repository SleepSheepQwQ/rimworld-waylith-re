using Xunit;

namespace AgentTouchFix.Tests
{
    public class IconPathTests
    {
        [Theory]
        [InlineData("CutoutComplexUI_RoyalBed_south", null, true)]
        [InlineData("CutoutComplexUI_NutrientDispenser_north", "", true)]
        [InlineData("CutoutComplexUI_Bed_south", "plain", true)]
        [InlineData("CutoutComplexUI_Bed_south", "PLAIN ", true)]
        // switched off by the file -> keep the material
        [InlineData("CutoutComplexUI_Bed_south", "material", false)]
        // no material at all -> nothing to divert
        [InlineData(null, null, false)]
        [InlineData("", null, false)]
        // a non-icon material must never be rerouted
        [InlineData("Cutout", null, false)]
        [InlineData("CutoutComplex", null, false)]
        [InlineData("SolidColor", null, false)]
        public void UsePlain(string materialName, string mode, bool expected)
        {
            Assert.Equal(expected, IconPath.UsePlain(materialName, mode));
        }

        [Fact]
        public void TheDivertedPathOnlyTriggersForTheIconMaterialFamily()
        {
            // the exact names seen in the device log
            Assert.True(IconPath.UsePlain("CutoutComplexUI_RoyalBed_south", null));
            Assert.True(IconPath.UsePlain("CutoutComplexUI_NutrientDispenser_north", null));
            // the world shader of the same building must keep its material
            Assert.False(IconPath.UsePlain("CutoutComplex", null));
        }
    }
}
