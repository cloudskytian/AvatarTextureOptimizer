// ============================================================================
// ATO quality threshold model
// ATO 质量阈值模型
//
// A single "quality" value in [0,1] (1 ≈ lossless) is mapped to concrete
// metric thresholds. The mapping is documented in docs/PLAN.md and tuned
// against published MS-SSIM / CIEDE2000 visibility studies.
// 单一 [0,1] 质量值（1≈近无损）映射为具体指标阈值。映射关系记录于
// docs/PLAN.md，并参考 MS-SSIM / CIEDE2000 可见性研究结果调参。
//
// Metric set 指标集：
//  - ssim        : MS-SSIM (fallback: single-scale SSIM for small islands)
//  - deltaE2000  : max CIEDE2000 color difference (linear space)
//  - alphaRMSE   : linear-space alpha RMSE (blend/premultiply)
//  - cutoutIoU   : silhouette IoU after alpha clipping (cutout)
//  - normalAngleP95 : p95 normal angle error in degrees (after decode/
//                     resample/renormalize/encode round trip)
//  - grayRMSE    : linear-space RMSE on used channels only (worst channel)
// 所有指标同时达标才算通过（取最严格者）。
// ============================================================================

#region

using System;

#endregion

namespace net.fosa.AvatarTextureOptimizer
{
    /// <summary>Serializable quality thresholds for one tier.
    /// 单个质量档位的可序列化阈值。</summary>
    [Serializable]
    public class ATOQualityParams
    {
        [Range(0f, 1f)]
        public float ssim = 1f;
        public float deltaE2000 = ATOQualityParams.LosslessDeltaE2000;
        public float alphaRMSE = ATOQualityParams.LosslessAlphaRMSE;
        [Range(0f, 1f)]
        public float cutoutIoU = ATOQualityParams.LosslessCutoutIoU;
        public float normalAngleP95 = ATOQualityParams.LosslessNormalAngleP95;
        public float grayRMSE = ATOQualityParams.LosslessGrayRMSE;

        // Lossless reference values 近无损参考值
        public const float LosslessDeltaE2000 = 0.4f;
        public const float LosslessAlphaRMSE = 0.002f;
        public const float LosslessCutoutIoU = 0.99f;
        public const float LosslessNormalAngleP95 = 0.5f;
        public const float LosslessGrayRMSE = 0.002f;

        public ATOQualityParams()
        {
        }

        /// <summary>Creates the parameter set for a quality value (default all
        /// lossless when q >= 1).
        /// 按质量值生成参数集（q>=1 时全部近无损）。</summary>
        public ATOQualityParams(float quality)
        {
            SetFromQuality(quality);
        }

        /// <summary>Recomputes all thresholds from a quality value.
        /// 由质量值重算全部阈值。</summary>
        public void SetFromQuality(float q)
        {
            q = Mathf.Clamp01(q);
            float t = 1f - q; // 0 = lossless, 1 = lowest  0=无损 1=最低
            ssim = q;
            deltaE2000 = 0.4f + 6.0f * t * t;
            alphaRMSE = 0.002f + 0.12f * Mathf.Pow(t, 1.5f);
            cutoutIoU = 1f - 0.01f - 0.5f * t * t;
            normalAngleP95 = 0.5f + 12f * Mathf.Pow(t, 1.5f);
            grayRMSE = 0.002f + 0.12f * Mathf.Pow(t, 1.5f);
        }

        public static ATOQualityParams FromQuality(float q)
        {
            return new ATOQualityParams(q);
        }

        public ATOQualityParams Clone()
        {
            return (ATOQualityParams) MemberwiseClone();
        }
    }

    /// <summary>Maps quality tiers to their quality values.
    /// 质量档位 → 质量值。</summary>
    public static class ATOQualityTierMap
    {
        public const float Lossless = 1f;
        public const float High = 0.95f;
        public const float Medium = 0.90f;
        public const float Low = 0.80f;
        public const float Extreme = 0.70f;

        public static float GetQuality(ATOQualityTier tier, float customQuality)
        {
            switch (tier)
            {
                case ATOQualityTier.Lossless: return Lossless;
                case ATOQualityTier.High: return High;
                case ATOQualityTier.Medium: return Medium;
                case ATOQualityTier.Low: return Low;
                case ATOQualityTier.Extreme: return Extreme;
                case ATOQualityTier.Custom: return customQuality;
                default: return Medium;
            }
        }
    }
}
