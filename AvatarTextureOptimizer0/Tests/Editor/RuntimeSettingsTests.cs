using NUnit.Framework;

namespace Fosa.AvatarTextureOptimizer.Tests
{
    public sealed class RuntimeSettingsTests
    {
        [Test]
        public void SanitizeSettingsRepairsInvalidSerializedValues()
        {
            var settings = new ATOOptimizationSettings
            {
                qualityPreset = (ATOQualityPreset)999,
                minimumPadding = (ATOMinimumPadding)3,
                minimumPixelDensity = (ATOPixelDensity)999999,
                maximumPixelDensity = (ATOPixelDensity)(-1),
                maximumAtlasSize = int.MaxValue
            };
            settings.opaque.compression = (ATOCompression)999;
            settings.quality.targetQuality = float.NaN;
            settings.quality.minSsim = float.PositiveInfinity;
            settings.quality.maxDeltaE2000 = float.NegativeInfinity;
            settings.customQuality.targetQuality = -10f;
            settings.customQuality.maxNormalP95Degrees = -2f;

            AvatarTextureOptimizer.SanitizeSettings(settings);

            Assert.That(settings.qualityPreset, Is.EqualTo(ATOQualityPreset.Balanced));
            Assert.That(settings.minimumPadding, Is.EqualTo(ATOMinimumPadding.Pixels4));
            Assert.That(settings.minimumPixelDensity, Is.EqualTo(ATOPixelDensity.Density2048));
            Assert.That(settings.maximumPixelDensity, Is.EqualTo(ATOPixelDensity.Density4096));
            Assert.That(settings.maximumAtlasSize, Is.EqualTo(8192));
            Assert.That(settings.opaque.compression, Is.EqualTo(ATOCompression.Auto));
            Assert.That(settings.quality.targetQuality, Is.InRange(0f, 1f));
            Assert.That(settings.quality.minSsim, Is.InRange(0f, 1f));
            Assert.That(settings.quality.maxDeltaE2000, Is.GreaterThanOrEqualTo(0f));
            Assert.That(settings.customQuality.targetQuality, Is.EqualTo(0f));
            Assert.That(settings.customQuality.maxNormalP95Degrees, Is.EqualTo(0f));
        }

        [Test]
        public void ResolveReturnsDetachedSanitizedSettings()
        {
            var gameObject = new UnityEngine.GameObject("settings-test");
            try
            {
                var component = gameObject.AddComponent<AvatarTextureOptimizer>();
                component.common.qualityPreset = ATOQualityPreset.Custom;
                component.common.customQuality.targetQuality = float.NaN;
                component.common.maximumAtlasSize = -100;

                var resolved = component.Resolve(ATOPlatform.PC);

                Assert.That(resolved, Is.Not.SameAs(component.common));
                Assert.That(resolved.customQuality, Is.Not.SameAs(component.common.customQuality));
                Assert.That(resolved.customQuality.targetQuality, Is.EqualTo(0.999f).Within(1e-6f));
                Assert.That(resolved.maximumAtlasSize, Is.EqualTo(256));
                Assert.That(component.common.maximumAtlasSize, Is.EqualTo(-100));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }
    }
}
