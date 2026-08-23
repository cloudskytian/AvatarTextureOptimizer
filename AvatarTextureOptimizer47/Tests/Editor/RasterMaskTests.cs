using Fosa.AvatarTextureOptimizer.Editor.Atlas;
using NUnit.Framework;

namespace Fosa.AvatarTextureOptimizer.Tests
{
    internal sealed class RasterMaskTests
    {
        [Test]
        public void RotationTransposesBits()
        {
            var source = RasterMaskBuilder.Create(3, 2);
            RasterMaskBuilder.Set(source, 0, 0);
            RasterMaskBuilder.Set(source, 2, 1);
            var rotated = RasterMaskBuilder.Rotate(source);
            Assert.That(rotated.Width, Is.EqualTo(2));
            Assert.That(rotated.Height, Is.EqualTo(3));
            Assert.That(RasterMaskBuilder.Get(rotated, 1, 0), Is.True);
            Assert.That(RasterMaskBuilder.Get(rotated, 0, 2), Is.True);
        }

        [Test]
        public void PaddingNeverLosesSourceCoverage()
        {
            var source = RasterMaskBuilder.Create(2, 2);
            RasterMaskBuilder.Set(source, 0, 1);
            var padded = RasterMaskBuilder.Pad(source, 2);
            Assert.That(padded.Width, Is.EqualTo(6));
            Assert.That(padded.Height, Is.EqualTo(6));
            Assert.That(RasterMaskBuilder.Get(padded, 2, 3), Is.True);
            Assert.That(padded.SetBitCount, Is.GreaterThanOrEqualTo(source.SetBitCount));
        }
    }
}
