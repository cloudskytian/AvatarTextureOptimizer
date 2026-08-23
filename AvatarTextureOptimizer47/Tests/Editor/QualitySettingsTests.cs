using NUnit.Framework;

namespace Fosa.AvatarTextureOptimizer.Tests
{
    internal sealed class QualitySettingsTests
    {
        [Test]
        public void CustomAndNearLosslessAreExact()
        {
            // EN: User-defined defaults and the lossless preset must not resample.
            // ZH: 自定义默认值与近无损预设不得触发重采样。
            Assert.That(QualityThresholds.ForPreset(QualityPreset.Custom).IsExact, Is.True);
            Assert.That(QualityThresholds.ForPreset(QualityPreset.NearLossless).IsExact, Is.True);
        }

        [Test]
        public void PresetsIncreaseMonotonically()
        {
            var performance = QualityThresholds.ForPreset(QualityPreset.Performance);
            var balanced = QualityThresholds.ForPreset(QualityPreset.Balanced);
            var high = QualityThresholds.ForPreset(QualityPreset.High);
            Assert.That(performance.Strictness, Is.LessThan(balanced.Strictness));
            Assert.That(balanced.Strictness, Is.LessThan(high.Strictness));
            Assert.That(high.DeltaE2000Maximum, Is.LessThan(balanced.DeltaE2000Maximum));
        }

        [Test]
        public void PlatformProfileClampsMobileAtlas()
        {
            var profile = new PlatformProfile { maximumAtlasSize = 8192 };
            profile.Validate(OptimizerPlatform.Android);
            Assert.That(profile.maximumAtlasSize, Is.EqualTo(4096));
            profile.maximumAtlasSize = 8192;
            profile.Validate(OptimizerPlatform.IOS);
            Assert.That(profile.maximumAtlasSize, Is.EqualTo(4096));
        }
    }
}
