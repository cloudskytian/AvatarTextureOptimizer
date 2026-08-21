using System;
using UnityEngine;

namespace Fosa.ATO
{
    /// <summary>
    /// Optional per-platform override of optimization parameters (Unity-style platform override).
    /// Hidden in the inspector until `enabled` is checked.
    /// 分平台覆盖（参考 Unity TextureImporter 的 platform override）。未勾选时不显示细节。
    /// </summary>
    [Serializable]
    public class AtoPlatformOverride
    {
        [Tooltip("Enable this platform override. 启用该平台覆盖。")]
        public bool enabled;

        public AtoQualityPreset qualityPreset = AtoQualityPreset.High;
        public AtoQualitySettings quality = AtoQualitySettings.ForPreset(AtoQualityPreset.High);

        public bool generateAtlas = true;
        public bool experimentalNpot;
        public AtoMinPadding minPadding = AtoMinPadding.Px4;

        public AtoPixelDensity minPixelDensity = AtoPixelDensity.D2048;
        public AtoPixelDensity maxPixelDensity = AtoPixelDensity.D4096;

        public AtoFormatSettings formats = new AtoFormatSettings();

        public AtoPlatformOverride Clone()
        {
            return new AtoPlatformOverride
            {
                enabled = enabled,
                qualityPreset = qualityPreset,
                quality = quality.Clone(),
                generateAtlas = generateAtlas,
                experimentalNpot = experimentalNpot,
                minPadding = minPadding,
                minPixelDensity = minPixelDensity,
                maxPixelDensity = maxPixelDensity,
                formats = formats.Clone()
            };
        }
    }
}
