using System;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer
{
    [Serializable]
    public sealed class ATOQualitySettings
    {
        [Range(0f, 1f)] public float targetQuality = 0.82f;
        [Range(0f, 1f)] public float minMsSsim = 0.98f;
        [Range(0f, 1f)] public float minSsim = 0.985f;
        [Min(0f)] public float maxDeltaE2000 = 2f;
        [Range(0f, 1f)] public float minCutoutIoU = 0.98f;
        [Min(0f)] public float maxBlendAlphaRmse = 0.02f;
        [Min(0f)] public float maxNormalMeanDegrees = 2.5f;
        [Min(0f)] public float maxNormalP95Degrees = 5f;
        [Min(0f)] public float maxGrayscaleRmse = 0.015f;

        public bool IsLosslessBypass => targetQuality >= 0.999999f;

        public ATOQualitySettings Clone() => (ATOQualitySettings)MemberwiseClone();

        public void ApplyPreset(ATOQualityPreset preset)
        {
            if (preset == ATOQualityPreset.Custom) return;
            switch (preset)
            {
                case ATOQualityPreset.Performance:
                    Set(0.65f, 0.965f, 0.975f, 3f, 0.96f, 0.03f, 4f, 8f, 0.025f); break;
                case ATOQualityPreset.Balanced:
                    Set(0.82f, 0.98f, 0.985f, 2f, 0.98f, 0.02f, 2.5f, 5f, 0.015f); break;
                case ATOQualityPreset.High:
                    Set(0.92f, 0.99f, 0.993f, 1.5f, 0.99f, 0.012f, 1.5f, 3f, 0.01f); break;
                case ATOQualityPreset.Ultra:
                    Set(0.98f, 0.997f, 0.998f, 0.8f, 0.996f, 0.006f, 0.75f, 1.5f, 0.004f); break;
                case ATOQualityPreset.NearLossless:
                    Set(1f, 1f, 1f, 0f, 1f, 0f, 0f, 0f, 0f); break;
            }
        }

        public static ATOQualitySettings CreateCustomDefaults()
        {
            // Starts just below the exact-lossless bypass, with deliberately strict perceptual/channel limits.
            // 默认“自定义”接近无损但仍实际求解；用户手动设为 1 时才严格跳过全部重采样。
            return new ATOQualitySettings
            {
                targetQuality = 0.999f, minMsSsim = 0.999f, minSsim = 0.9995f,
                maxDeltaE2000 = 0.25f, minCutoutIoU = 0.999f, maxBlendAlphaRmse = 0.001f,
                maxNormalMeanDegrees = 0.25f, maxNormalP95Degrees = 0.5f,
                maxGrayscaleRmse = 0.001f
            };
        }

        private void Set(float q, float ms, float ss, float de, float iou, float alpha, float normalMean, float normalP95, float gray)
        {
            targetQuality = q; minMsSsim = ms; minSsim = ss; maxDeltaE2000 = de; minCutoutIoU = iou;
            maxBlendAlphaRmse = alpha; maxNormalMeanDegrees = normalMean; maxNormalP95Degrees = normalP95;
            maxGrayscaleRmse = gray;
        }
    }

    [Serializable]
    public sealed class ATOTextureClassSettings
    {
        public ATOCompression compression = ATOCompression.Auto;
        public bool mipmapsAndStreaming = true;
    }

    [Serializable]
    public sealed class ATOOptimizationSettings
    {
        public ATOQualityPreset qualityPreset = ATOQualityPreset.Balanced;
        public ATOQualitySettings quality = new ATOQualitySettings();
        public ATOQualitySettings customQuality = ATOQualitySettings.CreateCustomDefaults();
        public bool generateAtlases = true;
        public bool experimentalNpot = false;
        [Range(256, 8192)] public int maximumAtlasSize = 4096;
        public ATOMinimumPadding minimumPadding = ATOMinimumPadding.Pixels4;
        public ATOPixelDensity minimumPixelDensity = ATOPixelDensity.Density2048;
        public ATOPixelDensity maximumPixelDensity = ATOPixelDensity.Density4096;
        public bool deduplicateMaterials = true;
        public bool deduplicateTexturesAndAtlases = true;
        public bool mergeSafeOpaqueMaterialSlots = true;
        public ATOTextureClassSettings opaque = new ATOTextureClassSettings();
        public ATOTextureClassSettings alpha = new ATOTextureClassSettings();
        public ATOTextureClassSettings normal = new ATOTextureClassSettings { compression = ATOCompression.BC5 };
        public ATOTextureClassSettings grayscale = new ATOTextureClassSettings();

        public ATOOptimizationSettings DeepClone()
        {
            var value = (ATOOptimizationSettings)MemberwiseClone();
            value.quality = (quality ?? new ATOQualitySettings()).Clone();
            value.customQuality = (customQuality ?? ATOQualitySettings.CreateCustomDefaults()).Clone();
            value.opaque = CloneClass(opaque); value.alpha = CloneClass(alpha);
            value.normal = CloneClass(normal); value.grayscale = CloneClass(grayscale);
            return value;
        }

        public ATOQualitySettings EffectiveQuality => qualityPreset == ATOQualityPreset.Custom ? customQuality : quality;

        private static ATOTextureClassSettings CloneClass(ATOTextureClassSettings source)
        {
            if (source == null) return new ATOTextureClassSettings();
            return new ATOTextureClassSettings { compression = source.compression, mipmapsAndStreaming = source.mipmapsAndStreaming };
        }
    }

    [Serializable]
    public sealed class ATODebugSettings
    {
        public bool analysis;
        public bool uvIslands;
        public bool quality;
        public bool packing;
        public bool generatedAssets;
        public bool animationRewrite;
        public bool resourceLifetime;
    }

    [Serializable]
    public sealed class ATOPlatformOverride
    {
        public ATOPlatform platform;
        public bool enabled;
        public ATOOptimizationSettings settings = new ATOOptimizationSettings();
    }
}
