using System;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer
{
    /// <summary>
    /// Per-preset numeric thresholds. / 各挡位数值阈值。
    /// Custom preset defaults to 1 (near-lossless) and is never overwritten by other presets.
    /// 自定义挡位默认全部为 1（近无损），不会被其他挡位覆盖。
    /// </summary>
    [Serializable]
    public class AtoQualityParameters
    {
        [Range(0f, 1f)] public float targetQuality = 1f;
        [Range(0.5f, 1f)] public float msSsimMin = 1f;
        [Range(0f, 20f)] public float ciede2000Max = 0f;
        [Range(0f, 1f)] public float alphaRmseMax = 0f;
        [Range(0f, 1f)] public float cutoutIouMin = 1f;
        [Range(0f, 45f)] public float normalAngleDegMax = 0f;
        [Range(0f, 1f)] public float normalP95AngleDegMax = 0f;
        [Range(0f, 1f)] public float grayRmseMax = 0f;

        public static AtoQualityParameters ForPreset(AtoQualityPreset preset)
        {
            switch (preset)
            {
                case AtoQualityPreset.NearLossless:
                    return Make(1f, 1f, 0f, 0f, 1f, 0f, 0f, 0f);
                case AtoQualityPreset.Ultra:
                    // Near-invisible: SSIM≥0.99, ΔE≤1.0 (well below JND 2.3)
                    return Make(0.99f, 0.99f, 1.0f, 0.01f, 0.995f, 2f, 3f, 0.01f);
                case AtoQualityPreset.High:
                    // Industry default: SSIM≥0.97, ΔE≤2.3 JND
                    return Make(0.97f, 0.97f, 2.3f, 0.02f, 0.99f, 4f, 6f, 0.02f);
                case AtoQualityPreset.Medium:
                    return Make(0.94f, 0.94f, 4.0f, 0.04f, 0.97f, 8f, 10f, 0.04f);
                case AtoQualityPreset.Low:
                    return Make(0.90f, 0.90f, 6.0f, 0.08f, 0.94f, 12f, 16f, 0.08f);
                default:
                    return Make(1f, 1f, 0f, 0f, 1f, 0f, 0f, 0f);
            }
        }

        static AtoQualityParameters Make(
            float q, float ssim, float de, float aRmse, float iou,
            float nAng, float nP95, float gRmse)
        {
            return new AtoQualityParameters
            {
                targetQuality = q,
                msSsimMin = ssim,
                ciede2000Max = de,
                alphaRmseMax = aRmse,
                cutoutIouMin = iou,
                normalAngleDegMax = nAng,
                normalP95AngleDegMax = nP95,
                grayRmseMax = gRmse
            };
        }

        public AtoQualityParameters Clone()
        {
            return (AtoQualityParameters)MemberwiseClone();
        }
    }
}
