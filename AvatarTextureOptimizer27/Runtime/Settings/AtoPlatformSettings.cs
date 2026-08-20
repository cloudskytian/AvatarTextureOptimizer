using System;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer
{
    /// <summary>Per-platform override block. / 分平台覆盖块。</summary>
    [Serializable]
    public class AtoPlatformSettings
    {
        public bool OverrideEnabled;
        public AtoQualityPreset QualityPreset = AtoQualityPreset.High;
        public AtoQualityParameters QualityParameters = AtoQualityParameters.ForPreset(AtoQualityPreset.High);
        public bool GenerateAtlas = true;
        public bool ExperimentalNpot;
        public AtoMinPadding MinPadding = AtoMinPadding.Px4;
        public AtoPixelDensityPreset MinPixelDensity = AtoPixelDensityPreset.Px2048;
        public AtoPixelDensityPreset MaxPixelDensity = AtoPixelDensityPreset.Px4096;
        public AtoSafeOpaqueFormat OpaqueFormat = AtoSafeOpaqueFormat.Auto;
        public AtoSafeAlphaFormat AlphaFormat = AtoSafeAlphaFormat.Auto;
        public AtoSafeNormalFormat NormalFormat = AtoSafeNormalFormat.Auto;
        public AtoSafeGrayFormat GrayFormat = AtoSafeGrayFormat.Auto;
        public bool MipStreamingAlbedo = true;
        public bool MipStreamingNormal = true;
        public bool MipStreamingMask = true;
        public bool MipStreamingGray = true;
        public int MaxAtlasEdge = 0; // 0 = auto 8192 PC / 4096 mobile

        public void ApplyPresetIfNotCustom()
        {
            if (QualityPreset != AtoQualityPreset.Custom)
                QualityParameters = AtoQualityParameters.ForPreset(QualityPreset);
        }

        public int ResolveMaxAtlasEdge(AtoPlatform platform)
        {
            if (MaxAtlasEdge > 0) return MaxAtlasEdge;
            return platform == AtoPlatform.PC ? 8192 : 4096;
        }
    }
}
