using System;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer
{
    /// <summary>
    /// Per-kind compression / mip settings. / 按贴图类型区分的压缩与 Mip 设置。
    /// </summary>
    [Serializable]
    public class AtoFormatSettings
    {
        public AtoSafeFormat opaqueFormat = AtoSafeFormat.Auto;
        public AtoSafeFormat transparentFormat = AtoSafeFormat.Auto;
        public AtoSafeFormat normalFormat = AtoSafeFormat.Auto;
        public AtoSafeFormat grayFormat = AtoSafeFormat.Auto;

        [Tooltip("Bound together: mipmaps ON implies Mip Streaming ON (VRChat requirement).\n绑定：开 Mipmap 必须开 MipStreaming（VRChat 要求）。")]
        public bool opaqueMipStreaming = true;
        public bool transparentMipStreaming = true;
        public bool normalMipStreaming = true;
        public bool grayMipStreaming = true;

        public AtoFormatSettings Clone()
        {
            return (AtoFormatSettings)MemberwiseClone();
        }
    }

    /// <summary>
    /// Platform override block. Unchecked platforms use generic settings.
    /// 平台覆盖块。未勾选的平台使用通用设置。
    /// </summary>
    [Serializable]
    public class AtoPlatformOverride
    {
        public bool enabled;
        public AtoQualityPreset qualityPreset = AtoQualityPreset.High;
        public AtoQualitySettings quality = AtoQualitySettings.ForPreset(AtoQualityPreset.High);
        public bool generateAtlas = true;
        public bool experimentalNpot;
        public AtoMinPadding minPadding = AtoMinPadding.Px4;
        public AtoPixelDensityStop minDensity = AtoPixelDensityStop.Px2048;
        public AtoPixelDensityStop maxDensity = AtoPixelDensityStop.Px4096;
        public AtoFormatSettings formats = new AtoFormatSettings();
        public bool dedupeMaterials = true;
        public bool dedupeTextures = true;
        public bool verboseLog;

        public AtoPlatformOverride Clone()
        {
            return new AtoPlatformOverride
            {
                enabled = enabled,
                qualityPreset = qualityPreset,
                quality = quality != null ? quality.Clone() : new AtoQualitySettings(),
                generateAtlas = generateAtlas,
                experimentalNpot = experimentalNpot,
                minPadding = minPadding,
                minDensity = minDensity,
                maxDensity = maxDensity,
                formats = formats != null ? formats.Clone() : new AtoFormatSettings(),
                dedupeMaterials = dedupeMaterials,
                dedupeTextures = dedupeTextures,
                verboseLog = verboseLog
            };
        }
    }
}
