using System;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer
{
    /// <summary>
    /// Perceptual thresholds for one quality preset.
    /// 单一质量挡位的感知阈值。
    /// </summary>
    [Serializable]
    public struct AtoQualityParameters
    {
        [Range(0.5f, 1f)] public float MsSsimMin;
        [Range(0f, 20f)] public float Ciede2000Max;
        [Range(0f, 1f)] public float AlphaRmseMax;
        [Range(0f, 1f)] public float CutoutIouMin;
        [Range(0f, 45f)] public float NormalAngleDegMax;
        [Range(0f, 90f)] public float NormalP95DegMax;
        [Range(0f, 1f)] public float GrayRmseMax;

        public static AtoQualityParameters ForPreset(AtoQualityPreset preset)
        {
            // Literature-inspired defaults (Wang MS-SSIM; Sharma CIEDE2000 JND ≈ 1–2).
            // 文献启发默认值（Wang MS-SSIM；Sharma CIEDE2000 可感差约 1–2）。
            switch (preset)
            {
                case AtoQualityPreset.Ultra:
                    return new AtoQualityParameters
                    {
                        MsSsimMin = 0.995f,
                        Ciede2000Max = 0.8f,
                        AlphaRmseMax = 0.01f,
                        CutoutIouMin = 0.995f,
                        NormalAngleDegMax = 2f,
                        NormalP95DegMax = 6f,
                        GrayRmseMax = 0.01f
                    };
                case AtoQualityPreset.High:
                    return new AtoQualityParameters
                    {
                        MsSsimMin = 0.985f,
                        Ciede2000Max = 1.5f,
                        AlphaRmseMax = 0.02f,
                        CutoutIouMin = 0.99f,
                        NormalAngleDegMax = 4f,
                        NormalP95DegMax = 10f,
                        GrayRmseMax = 0.02f
                    };
                case AtoQualityPreset.Medium:
                    return new AtoQualityParameters
                    {
                        MsSsimMin = 0.97f,
                        Ciede2000Max = 2.5f,
                        AlphaRmseMax = 0.04f,
                        CutoutIouMin = 0.98f,
                        NormalAngleDegMax = 7f,
                        NormalP95DegMax = 16f,
                        GrayRmseMax = 0.04f
                    };
                case AtoQualityPreset.Low:
                    return new AtoQualityParameters
                    {
                        MsSsimMin = 0.94f,
                        Ciede2000Max = 4.0f,
                        AlphaRmseMax = 0.07f,
                        CutoutIouMin = 0.96f,
                        NormalAngleDegMax = 12f,
                        NormalP95DegMax = 24f,
                        GrayRmseMax = 0.07f
                    };
                default:
                    return NearLossless();
            }
        }

        public static AtoQualityParameters NearLossless()
        {
            return new AtoQualityParameters
            {
                MsSsimMin = 1f,
                Ciede2000Max = 0f,
                AlphaRmseMax = 0f,
                CutoutIouMin = 1f,
                NormalAngleDegMax = 0f,
                NormalP95DegMax = 0f,
                GrayRmseMax = 0f
            };
        }

        public bool IsNearLossless =>
            MsSsimMin >= 0.999f && Ciede2000Max <= 0.001f;
    }
}
