using System;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer
{
    /// <summary>
    /// Per-metric thresholds for the target-quality binary search.
    /// 目标质量二分搜索使用的各项阈值。
    /// Sources / 依据: Wang et al. MS-SSIM (IEEE TIP 2003); CIEDE2000 (Sharma/Wu/Dalal);
    /// typical game normal-map angular tolerances; cutout IoU / blend RMSE.
    /// A metric of 1 (or ΔE=0) means near-lossless for that term.
    /// 某项为 1（或 ΔE=0）表示该项近无损。
    /// </summary>
    [Serializable]
    public struct QualityParameters
    {
        [Tooltip("MS-SSIM minimum (1 = lossless). / MS-SSIM 下限（1=无损）。")]
        [Range(0.5f, 1f)]
        public float msSsimMin;

        [Tooltip("CIEDE2000 maximum ΔE (0 = lossless). / CIEDE2000 最大色差（0=无损）。")]
        [Range(0f, 15f)]
        public float ciede2000Max;

        [Tooltip("Cutout alpha contour IoU minimum. / Cutout 轮廓 IoU 下限。")]
        [Range(0.5f, 1f)]
        public float cutoutIouMin;

        [Tooltip("Blend alpha linear RMSE maximum. / Blend alpha 线性 RMSE 上限。")]
        [Range(0f, 0.5f)]
        public float blendAlphaRmseMax;

        [Tooltip("Normal mean angular error max (degrees). / 法线平均角误差上限（度）。")]
        [Range(0f, 45f)]
        public float normalMeanAngleDegMax;

        [Tooltip("Normal p95 angular error max (degrees). / 法线 p95 角误差上限（度）。")]
        [Range(0f, 60f)]
        public float normalP95AngleDegMax;

        [Tooltip("Grayscale per-used-channel linear RMSE max. / 灰度已用通道线性 RMSE 上限。")]
        [Range(0f, 0.5f)]
        public float grayRmseMax;

        /// <summary>
        /// True when this set means "target quality = 1" (skip UV scale, copy pixels).
        /// 是否视为目标质量=1（跳过 UV 缩放，原样拷贝）。
        /// </summary>
        public bool IsNearLossless =>
            msSsimMin >= 0.999f &&
            ciede2000Max <= 0.05f &&
            cutoutIouMin >= 0.999f &&
            blendAlphaRmseMax <= 0.001f &&
            normalMeanAngleDegMax <= 0.25f &&
            normalP95AngleDegMax <= 0.5f &&
            grayRmseMax <= 0.001f;

        public static QualityParameters NearLossless() => new QualityParameters
        {
            msSsimMin = 1f,
            ciede2000Max = 0f,
            cutoutIouMin = 1f,
            blendAlphaRmseMax = 0f,
            normalMeanAngleDegMax = 0f,
            normalP95AngleDegMax = 0f,
            grayRmseMax = 0f
        };

        /// <summary>
        /// Ultra: ΔE&lt;1 (not perceptible), MS-SSIM 0.99. / 几乎不可察觉。
        /// </summary>
        public static QualityParameters Ultra() => new QualityParameters
        {
            msSsimMin = 0.99f,
            ciede2000Max = 1.0f,
            cutoutIouMin = 0.99f,
            blendAlphaRmseMax = 0.008f,
            normalMeanAngleDegMax = 5f,
            normalP95AngleDegMax = 8f,
            grayRmseMax = 0.008f
        };

        /// <summary>
        /// High (default): ΔE≈2 close-inspection, MS-SSIM 0.97. / 默认高质量。
        /// </summary>
        public static QualityParameters High() => new QualityParameters
        {
            msSsimMin = 0.97f,
            ciede2000Max = 2.0f,
            cutoutIouMin = 0.97f,
            blendAlphaRmseMax = 0.02f,
            normalMeanAngleDegMax = 10f,
            normalP95AngleDegMax = 15f,
            grayRmseMax = 0.02f
        };

        public static QualityParameters Medium() => new QualityParameters
        {
            msSsimMin = 0.94f,
            ciede2000Max = 3.5f,
            cutoutIouMin = 0.94f,
            blendAlphaRmseMax = 0.04f,
            normalMeanAngleDegMax = 15f,
            normalP95AngleDegMax = 22f,
            grayRmseMax = 0.04f
        };

        public static QualityParameters Low() => new QualityParameters
        {
            msSsimMin = 0.90f,
            ciede2000Max = 5.0f,
            cutoutIouMin = 0.90f,
            blendAlphaRmseMax = 0.08f,
            normalMeanAngleDegMax = 22f,
            normalP95AngleDegMax = 32f,
            grayRmseMax = 0.08f
        };

        public static QualityParameters ForPreset(QualityPreset preset)
        {
            switch (preset)
            {
                case QualityPreset.NearLossless: return NearLossless();
                case QualityPreset.Ultra: return Ultra();
                case QualityPreset.High: return High();
                case QualityPreset.Medium: return Medium();
                case QualityPreset.Low: return Low();
                default: return NearLossless();
            }
        }
    }
}
