using Fosa.AvatarTextureOptimizer.Editor.UI;
using NUnit.Framework;

namespace Fosa.AvatarTextureOptimizer.Tests
{
    internal sealed class InspectorSafetyTests
    {
        [Test]
        public void AlphaDoesNotOfferAlphaLessFormats()
        {
            var formats = AvatarTextureOptimizerEditor.GetAllowedFormats(TextureSemantic.ColorAlpha, OptimizerPlatform.PC, false);
            Assert.That(formats, Does.Not.Contain(SafeTextureFormat.BC1));
            Assert.That(formats, Does.Contain(SafeTextureFormat.BC7));
        }

        [Test]
        public void NpotIosDoesNotOfferPvrtc()
        {
            var formats = AvatarTextureOptimizerEditor.GetAllowedFormats(TextureSemantic.ColorOpaque, OptimizerPlatform.IOS, true);
            Assert.That(formats, Does.Not.Contain(SafeTextureFormat.PVRTCRGB4));
            Assert.That(formats, Does.Contain(SafeTextureFormat.ASTC6x6));
        }
    }
}
