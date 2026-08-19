using System;
using System.Collections.Generic;
using NetFosa.AvatarTextureOptimizer.Editor.Logging;
using UnityEditor;
using UnityEngine;

namespace NetFosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// 平台解析：把当前构建目标映射为 ATOPlatform；解析平台覆盖后的有效设置。
    /// </summary>
    public static class PlatformResolver
    {
        public static ATOPlatform CurrentPlatform
        {
            get
            {
                switch (EditorUserBuildSettings.activeBuildTarget)
                {
                    case BuildTarget.Android: return ATOPlatform.Android;
                    case BuildTarget.iOS: return ATOPlatform.iOS;
                    default: return ATOPlatform.PC;
                }
            }
        }
    }

    /// <summary>
    /// 解析（含平台覆盖）后的有效设置快照。管线全程只读它。
    /// </summary>
    public sealed class EffectiveSettings
    {
        public bool generateAtlases;
        public ATOQualityPreset qualityPreset;
        public QualityThresholds quality;
        public int minPixelsPerMeter;
        public int maxPixelsPerMeter;
        public bool npotEnabled;
        public int minPadding;
        public CompressionSettings compression;
        public MipmapSettings mipmaps;
        public bool deduplicateTextures;
        public bool deduplicateMaterials;
        public bool mergeIdenticalMaterialSlots;
        public bool useGPU;
        public bool useBurst;
        public bool verboseLogging;
        public ATOPlatform platform;

        public static EffectiveSettings Resolve(AvatarTextureOptimizer component, ATOPlatform platform)
        {
            var eff = new EffectiveSettings
            {
                generateAtlases = component.generateAtlases,
                qualityPreset = component.qualityPreset,
                quality = component.GetEffectiveQuality(),
                minPixelsPerMeter = component.minPixelsPerMeter,
                maxPixelsPerMeter = component.maxPixelsPerMeter,
                npotEnabled = component.npotEnabled,
                minPadding = component.minPadding,
                compression = component.compression.Clone(),
                mipmaps = component.mipmaps.Clone(),
                deduplicateTextures = component.deduplicateTextures,
                deduplicateMaterials = component.deduplicateMaterials,
                mergeIdenticalMaterialSlots = component.mergeIdenticalMaterialSlots,
                useGPU = component.useGPUAcceleration,
                useBurst = component.useBurstJobs,
                verboseLogging = component.verboseLogging,
                platform = platform,
            };

            var ov = component.platformOverrides.Get(platform);
            if (ov == null) return eff;
            if (ov.enabled)
            {
                if (ov.overrideQuality)
                {
                    eff.qualityPreset = ov.qualityPreset;
                    eff.quality = ov.qualityPreset == ATOQualityPreset.Custom
                        ? (ov.customQuality != null ? ov.customQuality.Clone() : QualityThresholds.NearLossless())
                        : QualityThresholds.ForPreset(ov.qualityPreset);
                }
                if (ov.overrideDensity)
                {
                    eff.minPixelsPerMeter = ov.minPixelsPerMeter;
                    eff.maxPixelsPerMeter = ov.maxPixelsPerMeter;
                }
                if (ov.overrideCompression) eff.compression = ov.compression.Clone();
                if (ov.overrideMipmaps) eff.mipmaps = ov.mipmaps.Clone();
                if (ov.overrideAtlas)
                {
                    eff.npotEnabled = ov.npotEnabled;
                    eff.minPadding = ov.minPadding;
                }
            }
            return eff;
        }
    }
}
