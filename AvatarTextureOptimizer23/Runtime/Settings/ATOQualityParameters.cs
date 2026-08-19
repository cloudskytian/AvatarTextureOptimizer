using System;
using UnityEngine;

namespace FOSA.AvatarTextureOptimizer
{
    /// <summary>
    /// Perceptual quality thresholds. A candidate scale passes only when EVERY metric passes.
    /// 感知质量阈值。候选缩放必须所有指标同时达标。
    /// </summary>
    [Serializable]
    public struct ATOQualityParameters : IEquatable<ATOQualityParameters>
    {
        [Tooltip("Minimum MS-SSIM (higher = stricter). / MS-SSIM 下限（越高越严）。")]
        [Range(0f, 1f)]
        public float msSsim;

        [Tooltip("Maximum CIEDE2000 ΔE (lower = stricter). / CIEDE2000 ΔE 上限（越低越严）。")]
        [Range(0f, 20f)]
        public float deltaE00;

        [Tooltip("Maximum linear alpha RMSE for Blend materials. / Blend 材质的线性 alpha RMSE 上限。")]
        [Range(0f, 1f)]
        public float alphaRmse;

        [Tooltip("Minimum clip-contour IoU for Cutout materials. / Cutout 材质的裁剪轮廓 IoU 下限。")]
        [Range(0f, 1f)]
        public float alphaIou;

        [Tooltip("Maximum mean normal-map angle error in degrees. / 法线贴图平均角度误差上限（度）。")]
        [Range(0f, 45f)]
        public float normalAngleDeg;

        [Tooltip("Maximum p95 normal-map angle error in degrees. / 法线贴图 p95 角度误差上限（度）。")]
        [Range(0f, 90f)]
        public float normalP95Deg;

        [Tooltip("Maximum linear RMSE on used gray channels. / 灰度贴图已用通道的线性 RMSE 上限。")]
        [Range(0f, 1f)]
        public float grayRmse;

        /// <summary>
        /// True when this preset should skip UV scaling entirely (near-lossless).
        /// 是否应完全跳过 UV 缩放（近无损）。
        /// </summary>
        public bool SkipUvScale => msSsim >= 0.999f && deltaE00 <= 0.001f;

        public static ATOQualityParameters ForPreset(ATOQualityPreset preset)
        {
            switch (preset)
            {
                case ATOQualityPreset.Lossless:
                    return new ATOQualityParameters
                    {
                        msSsim = 1f, deltaE00 = 0f, alphaRmse = 0f, alphaIou = 1f,
                        normalAngleDeg = 0f, normalP95Deg = 0f, grayRmse = 0f
                    };
                case ATOQualityPreset.Ultra:
                    return new ATOQualityParameters
                    {
                        msSsim = 0.99f, deltaE00 = 1.0f, alphaRmse = 0.008f, alphaIou = 0.99f,
                        normalAngleDeg = 2f, normalP95Deg = 5f, grayRmse = 0.01f
                    };
                case ATOQualityPreset.High:
                    return new ATOQualityParameters
                    {
                        msSsim = 0.97f, deltaE00 = 2.0f, alphaRmse = 0.02f, alphaIou = 0.98f,
                        normalAngleDeg = 4f, normalP95Deg = 8f, grayRmse = 0.02f
                    };
                case ATOQualityPreset.Medium:
                    return new ATOQualityParameters
                    {
                        msSsim = 0.94f, deltaE00 = 3.5f, alphaRmse = 0.04f, alphaIou = 0.95f,
                        normalAngleDeg = 8f, normalP95Deg = 15f, grayRmse = 0.04f
                    };
                case ATOQualityPreset.Low:
                    return new ATOQualityParameters
                    {
                        msSsim = 0.90f, deltaE00 = 6.0f, alphaRmse = 0.08f, alphaIou = 0.90f,
                        normalAngleDeg = 12f, normalP95Deg = 25f, grayRmse = 0.08f
                    };
                case ATOQualityPreset.Custom:
                default:
                    // Custom defaults are all 1 = near-lossless intent. / Custom 默认全 1，表示近无损意图。
                    return new ATOQualityParameters
                    {
                        msSsim = 1f, deltaE00 = 1f, alphaRmse = 1f, alphaIou = 1f,
                        normalAngleDeg = 1f, normalP95Deg = 1f, grayRmse = 1f
                    };
            }
        }

        public bool Equals(ATOQualityParameters other)
        {
            return msSsim.Equals(other.msSsim) && deltaE00.Equals(other.deltaE00) &&
                   alphaRmse.Equals(other.alphaRmse) && alphaIou.Equals(other.alphaIou) &&
                   normalAngleDeg.Equals(other.normalAngleDeg) &&
                   normalP95Deg.Equals(other.normalP95Deg) && grayRmse.Equals(other.grayRmse);
        }

        public override bool Equals(object obj) => obj is ATOQualityParameters other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = msSsim.GetHashCode();
                hash = (hash * 397) ^ deltaE00.GetHashCode();
                hash = (hash * 397) ^ alphaRmse.GetHashCode();
                hash = (hash * 397) ^ alphaIou.GetHashCode();
                hash = (hash * 397) ^ normalAngleDeg.GetHashCode();
                hash = (hash * 397) ^ normalP95Deg.GetHashCode();
                hash = (hash * 397) ^ grayRmse.GetHashCode();
                return hash;
            }
        }
    }
}
