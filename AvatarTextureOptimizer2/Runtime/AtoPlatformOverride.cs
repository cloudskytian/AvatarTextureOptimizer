using System;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer
{
    /// <summary>
    /// Per-platform override of optimization parameters. / 按平台覆盖优化参数。
    /// Hidden until the platform checkbox is enabled. / 未勾选平台时折叠隐藏。
    /// </summary>
    [Serializable]
    public class AtoPlatformOverride
    {
        public bool enabled;
        public AtoQualityPreset qualityPreset = AtoQualityPreset.High;
        public AtoQualityParameters quality = AtoQualityParameters.ForPreset(AtoQualityPreset.High);
        public bool generateAtlas = true;
        public bool experimentalNpot;
        public AtoMinPadding minPadding = AtoMinPadding.Px4;
        public AtoPixelDensity minPixelDensity = AtoPixelDensity.Px2048;
        public AtoPixelDensity maxPixelDensity = AtoPixelDensity.Px4096;
        public AtoOpaqueFormat opaqueFormat = AtoOpaqueFormat.Auto;
        public AtoTransparentFormat transparentFormat = AtoTransparentFormat.Auto;
        public AtoNormalFormat normalFormat = AtoNormalFormat.Auto;
        public AtoGrayFormat grayFormat = AtoGrayFormat.Auto;
        public bool mipStreamingAlbedo = true;
        public bool mipStreamingNormal = true;
        public bool mipStreamingMask = true;
        public bool mipStreamingGray = true;
        public bool deduplicateMaterials = true;
        public bool deduplicateTextures = true;
    }
}
