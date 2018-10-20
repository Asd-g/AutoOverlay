using AutoOverlay;
using AvsFilterNet;
using NUnit.Framework;

namespace AutoOverlayTests
{
    [TestFixture]
    public class ColorAdjustTest
    {
        [Test]
        public void TestLimitedRange()
        {
            var filter = new ColorAdjust
            {
                LimitedRange = true
            };
            Assert.AreEqual(16, filter.GetLowColor(8));
            Assert.AreEqual(64, filter.GetLowColor(10));

            Assert.AreEqual(235, filter.GetHighColor(8, YUVPlanes.PLANAR_Y));
            Assert.AreEqual(940, filter.GetHighColor(10, YUVPlanes.PLANAR_Y));

            foreach (var plane in new[] {YUVPlanes.PLANAR_U, YUVPlanes.PLANAR_V})
            {
                Assert.AreEqual(240, filter.GetHighColor(8, plane));
                Assert.AreEqual(960, filter.GetHighColor(10, plane));
            }
        }

        [Test]
        public void TestFullRange()
        {
            var filter = new ColorAdjust
            {
                LimitedRange = false
            };
            Assert.AreEqual(0, filter.GetLowColor(8));
            Assert.AreEqual(0, filter.GetLowColor(10));

            foreach (var plane in new[] { YUVPlanes.PLANAR_Y, YUVPlanes.PLANAR_U, YUVPlanes.PLANAR_V })
            {
                Assert.AreEqual(255, filter.GetHighColor(8, plane));
                Assert.AreEqual(1023, filter.GetHighColor(10, plane));
            }
        }

        [Test]
        public void TestRealPlanar()
        {
            Assert.IsTrue(ColorSpaces.CS_GENERIC_YUV444.IsRealPlanar());
        }
    }
}
