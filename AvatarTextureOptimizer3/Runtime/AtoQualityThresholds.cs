// English: Quality thresholds applied during UV-island binary search.
// 中文：UV 岛二分缩放时使用的质量阈值。
using System;
using UnityEngine;

namespace net.fosa.ato
{
    /// <summary>
    /// All metrics must pass (worst-of / AND). Values are "minimum acceptable quality".
    /// 所有指标必须同时达标（木桶）。数值为“最低可接受质量”。
    /// </summary>
    [Serializable]
    public struct AtoQualityThresholds
    {
        [Range(0f, 1f)] public float msSsim;
        [Range(0f, 20f)] public float ciede2000; // max allowed ΔE
        [Range(0f, 1f)] public float alphaRmse;  // max allowed linear RMSE
        [Range(0f, 1f)] public float cutoutIou;  // min allowed IoU
        [Range(0f, 45f)] public float normalAngleDeg; // max mean angle
        [Range(0f, 45f)] public float normalP95Deg;   // max p95 angle
        [Range(0f, 1f)] public float grayRmse;        // max channel RMSE

        public static AtoQualityThresholds ForPreset(AtoQualityPreset preset)
        {
            // References (industry / academic, used as conservative VRChat defaults):
            // - MS-SSIM: Wang et al.; 0.99 ≈ near-lossless, 0.97 high, 0.94 medium.
            // - CIEDE2000: ΔE≈1 JND, 2 noticeable, 4 clearly different.
            // - Normal maps: 5–15° mean angular error commonly used in game baking.
            switch (preset)
            {
                case AtoQualityPreset.Lossless:
                case AtoQualityPreset.Custom:
                    return new AtoQualityThresholds
                    {
                        msSsim = 1f, ciede2000 = 0f, alphaRmse = 0f, cutoutIou = 1f,
                        normalAngleDeg = 0f, normalP95Deg = 0f, grayRmse = 0f
                    };
                case AtoQualityPreset.Ultra:
                    return new AtoQualityThresholds
                    {
                        msSsim = 0.995f, ciede2000 = 1.0f, alphaRmse = 0.01f, cutoutIou = 0.995f,
                        normalAngleDeg = 3f, normalP95Deg = 6f, grayRmse = 0.01f
                    };
                case AtoQualityPreset.High:
                    return new AtoQualityThresholds
                    {
                        msSsim = 0.985f, ciede2000 = 2.0f, alphaRmse = 0.02f, cutoutIou = 0.985f,
                        normalAngleDeg = 6f, normalP95Deg = 12f, grayRmse = 0.02f
                    };
                case AtoQualityPreset.Medium:
                    return new AtoQualityThresholds
                    {
                        msSsim = 0.97f, ciede2000 = 3.0f, alphaRmse = 0.04f, cutoutIou = 0.97f,
                        normalAngleDeg = 10f, normalP95Deg = 18f, grayRmse = 0.04f
                    };
                case AtoQualityPreset.Low:
                    return new AtoQualityThresholds
                    {
                        msSsim = 0.94f, ciede2000 = 5.0f, alphaRmse = 0.07f, cutoutIou = 0.94f,
                        normalAngleDeg = 15f, normalP95Deg = 25f, grayRmse = 0.07f
                    };
                default:
                    return ForPreset(AtoQualityPreset.High);
            }
        }
    }
}
