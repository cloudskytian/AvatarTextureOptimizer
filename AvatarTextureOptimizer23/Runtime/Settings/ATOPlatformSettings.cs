using System;
using UnityEngine;

namespace FOSA.AvatarTextureOptimizer
{
    /// <summary>
    /// Per-platform override of optimization parameters. Hidden until the platform checkbox is on.
    /// 分平台覆盖。对应平台勾选后才显示。
    /// </summary>
    [Serializable]
    public class ATOPlatformSettings
    {
        public bool enabled;

        public ATOQualityPreset qualityPreset = ATOQualityPreset.High;
        public ATOQualityParameters qualityParameters = ATOQualityParameters.ForPreset(ATOQualityPreset.High);
        public ATOQualityParameters customQualityParameters = ATOQualityParameters.ForPreset(ATOQualityPreset.Custom);

        public bool generateAtlas = true;
        public bool experimentalNpot;
        public ATOMinPadding minPadding = ATOMinPadding.Px4;

        public float minPixelDensity = 2048f;
        public float maxPixelDensity = 4096f;

        public ATOCompressionChoice formatOpaque = ATOCompressionChoice.Auto;
        public ATOCompressionChoice formatTransparent = ATOCompressionChoice.Auto;
        public ATOCompressionChoice formatNormal = ATOCompressionChoice.Auto;
        public ATOCompressionChoice formatGray = ATOCompressionChoice.Auto;

        public bool mipStreamingOpaque = true;
        public bool mipStreamingTransparent = true;
        public bool mipStreamingNormal = true;
        public bool mipStreamingGray = true;

        public ATOPlatformSettings Clone()
        {
            return (ATOPlatformSettings)MemberwiseClone();
        }
    }
}
