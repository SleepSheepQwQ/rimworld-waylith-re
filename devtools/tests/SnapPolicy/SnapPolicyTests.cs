using Xunit;

namespace AgentDevTools.Tests
{
    /// <summary>
    /// The mod's whole policy, pinned by tests. These run on CI with no game, no engine and no device.
    /// </summary>
    public class SnapPolicyTests
    {
        // ---- the rule the mod exists for ------------------------------------------------

        [Theory]
        // off: nothing is ever snapped
        [InlineData(SnapPolicy.Off, true, true, false)]
        // auto: only when the tap actually landed on something
        [InlineData(SnapPolicy.Auto, true, true, true)]
        [InlineData(SnapPolicy.Auto, true, false, false)]
        // ... and never when PortUI reports an empty list (the finger aimed at terrain)
        [InlineData(SnapPolicy.Auto, false, true, false)]
        [InlineData(SnapPolicy.Auto, false, false, false)]
        // always: keep the object whenever PortUI named one, even off-footprint
        [InlineData(SnapPolicy.Always, true, false, true)]
        [InlineData(SnapPolicy.Always, false, false, false)]
        public void Accept(int mode, bool hadCandidates, bool hit, bool expected)
        {
            Assert.Equal(expected, SnapPolicy.Accept(mode, hadCandidates, hit));
        }

        [Fact]
        public void AnEmptyCandidateListAlwaysMeansRawCell()
        {
            foreach (int mode in new[] { SnapPolicy.Off, SnapPolicy.Auto, SnapPolicy.Always })
                Assert.False(SnapPolicy.Accept(mode, candidateListHadEntries: false, hitWithinRadius: true));
        }

        // ---- fingertip tolerance --------------------------------------------------------

        [Fact]
        public void RadiusIsSixteenDp()
        {
            Assert.Equal(16f, SnapPolicy.RadiusPx(1f));
            Assert.Equal(32f, SnapPolicy.RadiusPx(2f));
            Assert.Equal(48f, SnapPolicy.RadiusPx(3f));
        }

        [Theory]
        [InlineData(0f)]
        [InlineData(0.1f)]
        [InlineData(-4f)]
        [InlineData(float.NaN)]
        public void RadiusNeverGoesBelowTheFloor(float density)
        {
            Assert.Equal(SnapPolicy.MinRadiusPx, SnapPolicy.RadiusPx(density));
        }

        [Fact]
        public void RadiusNeverExceedsTheCeiling()
        {
            Assert.Equal(SnapPolicy.MaxRadiusPx, SnapPolicy.RadiusPx(1e6f));
        }

        // ---- vocabulary -----------------------------------------------------------------

        [Theory]
        [InlineData(SnapPolicy.Off, "off")]
        [InlineData(SnapPolicy.Auto, "auto")]
        [InlineData(SnapPolicy.Always, "always")]
        [InlineData(99, "auto")]
        public void Names(int mode, string expected)
        {
            Assert.Equal(expected, SnapPolicy.Name(mode));
        }
    }
}
