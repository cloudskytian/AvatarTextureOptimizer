// English: Per-platform overridable optimizer settings.
// 中文：可按平台覆盖的优化器设置。
using System;
using System.Collections.Generic;
using UnityEngine;

namespace net.fosa.ato
{
    [Serializable]
    public class AtoCompressionSet
    {
        public AtoSafeCompression opaque = AtoSafeCompression.Balanced;
        public AtoSafeCompression transparent = AtoSafeCompression.Balanced;
        public AtoSafeCompression normal = AtoSafeCompression.HighQuality;
        public AtoSafeCompression gray = AtoSafeCompression.Balanced;
        public bool mipStreamingOpaque = true;
        public bool mipStreamingTransparent = true;
        public bool mipStreamingNormal = true;
        public bool mipStreamingGray = true;
    }

    [Serializable]
    public class AtoPlatformSettings
    {
        public bool enabled;
        public AtoQualityPreset qualityPreset = AtoQualityPreset.High;
        public AtoQualityThresholds thresholds = AtoQualityThresholds.ForPreset(AtoQualityPreset.High);
        public bool generateAtlas = true;
        public bool experimentalNpot;
        public AtoMinPadding minPadding = AtoMinPadding.Px4;
        public AtoPixelDensity minDensity = AtoPixelDensity.D2048;
        public AtoPixelDensity maxDensity = AtoPixelDensity.D4096;
        public AtoCompressionSet compression = new AtoCompressionSet();
        public bool dedupeTextures = true;
        public bool dedupeMaterials = true;

        public void ApplyPresetIfNotCustom()
        {
            if (qualityPreset != AtoQualityPreset.Custom)
                thresholds = AtoQualityThresholds.ForPreset(qualityPreset);
        }
    }
}
