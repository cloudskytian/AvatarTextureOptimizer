// Copyright (c) fosa. Licensed under the MIT License.
// Quality thresholds per tier. Values approved by the user; see docs/QualityPresets.md
// for the academic/industry rationale behind each number.
// 各挡位的质量阈值。数值已经用户确认，依据见 docs/QualityPresets.md。

using System;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer
{
    /// <summary>
    /// A complete set of perceptual thresholds driving the binary search over UV island scale.
    /// An island may only shrink while every enabled metric still meets its threshold.
    /// 驱动 UV 岛缩放二分搜索的完整感知阈值集合。只有当所有启用的指标全部达标时，岛才允许继续缩小。
    /// </summary>
    [Serializable]
    public sealed class QualityParameters
    {
        /// <summary>
        /// Overall quality factor. 1.0 means near-lossless: UV rescaling and resampling are
        /// skipped entirely (atlasing and deduplication still apply).
        /// 总体质量因子。1.0 表示近无损：完全跳过 UV 缩放与重采样（仍进行图集化与去重）。
        /// </summary>
        [Range(0f, 1f)]
        public float quality = 0.85f;

        /// <summary>Minimum MS-SSIM. Islands whose short side is &gt;= 176px use this. / MS-SSIM 下限，短边 &gt;=176px 的岛使用。</summary>
        public float msSsimMin = 0.985f;

        /// <summary>Minimum single-scale SSIM, used when the short side is &lt; 176px. / 单尺度 SSIM 下限，短边 &lt;176px 时使用。</summary>
        public float ssimMin = 0.975f;

        /// <summary>Maximum mean CIEDE2000 colour difference. / CIEDE2000 平均色差上限。</summary>
        public float deltaE00Mean = 1.5f;

        /// <summary>Maximum 95th percentile CIEDE2000 colour difference. / CIEDE2000 p95 色差上限。</summary>
        public float deltaE00P95 = 2.5f;

        /// <summary>Maximum mean normal angular error, degrees. / 法线平均角度误差上限（度）。</summary>
        public float normalAngleMeanDeg = 0.75f;

        /// <summary>Maximum 95th percentile normal angular error, degrees. / 法线 p95 角度误差上限（度）。</summary>
        public float normalAngleP95Deg = 1.5f;

        /// <summary>Maximum grayscale linear RMSE, expressed in 1/255 units. / 灰度线性 RMSE 上限，单位 1/255。</summary>
        public float grayscaleRmse255 = 2.0f;

        /// <summary>Minimum silhouette IoU after applying the cutoff, for Cutout materials. / Cutout 材质应用 cutoff 后的轮廓 IoU 下限。</summary>
        public float cutoutIoUMin = 0.993f;

        /// <summary>Maximum linear alpha RMSE for Blend materials, in 1/255 units. / Blend 材质 alpha 线性 RMSE 上限，单位 1/255。</summary>
        public float blendAlphaRmse255 = 2.0f;

        /// <summary>Lower clamp on texel density, pixels per metre. / 像素密度下限，px/m。</summary>
        public int minPixelDensity = 2048;

        /// <summary>Upper clamp on texel density, pixels per metre. / 像素密度上限，px/m。</summary>
        public int maxPixelDensity = 4096;

        /// <summary>
        /// True when the tier is near-lossless and all resampling must be skipped.
        /// 当挡位为近无损、必须跳过所有重采样时为 true。
        /// </summary>
        public bool IsLossless => quality >= 1f - 1e-6f;

        /// <summary>Creates an independent copy. / 创建独立副本。</summary>
        public QualityParameters Clone()
        {
            return (QualityParameters)MemberwiseClone();
        }

        /// <summary>
        /// Copies every field from another instance in place.
        /// 就地复制另一个实例的所有字段。
        /// </summary>
        public void CopyFrom(QualityParameters other)
        {
            if (other == null) return;
            quality = other.quality;
            msSsimMin = other.msSsimMin;
            ssimMin = other.ssimMin;
            deltaE00Mean = other.deltaE00Mean;
            deltaE00P95 = other.deltaE00P95;
            normalAngleMeanDeg = other.normalAngleMeanDeg;
            normalAngleP95Deg = other.normalAngleP95Deg;
            grayscaleRmse255 = other.grayscaleRmse255;
            cutoutIoUMin = other.cutoutIoUMin;
            blendAlphaRmse255 = other.blendAlphaRmse255;
            minPixelDensity = other.minPixelDensity;
            maxPixelDensity = other.maxPixelDensity;
        }
    }

    /// <summary>
    /// Factory for the built-in quality tiers.
    /// 内置质量挡位的工厂。
    /// </summary>
    public static class QualityPresets
    {
        /// <summary>
        /// The minimum island short side, in pixels, at or above which multi-scale SSIM is valid.
        /// MS-SSIM uses 5 scales with an 11x11 Gaussian window, so 11 * 2^4 = 176.
        /// MS-SSIM 有效的最小岛短边（像素）。5 尺度 + 11x11 高斯窗，故 11 * 2^4 = 176。
        /// </summary>
        public const int MsSsimMinShortSide = 176;

        /// <summary>
        /// Below this island short side, structural metrics are skipped entirely.
        /// 低于此岛短边时，完全忽略结构相似度指标。
        /// </summary>
        public const int StructuralMetricIgnoreShortSide = 11;

        /// <summary>
        /// Builds the default parameter set for a tier. Custom returns near-lossless defaults,
        /// which the user then edits freely.
        /// 构建指定挡位的默认参数。Custom 返回近无损默认值，之后由用户自由修改。
        /// </summary>
        public static QualityParameters Create(QualityTier tier)
        {
            switch (tier)
            {
                case QualityTier.Maximum:
                    // quality == 1: every perceptual metric is skipped because no resampling occurs.
                    // quality == 1：不发生任何重采样，因此所有感知指标都被跳过。
                    return new QualityParameters
                    {
                        quality = 1.00f,
                        msSsimMin = 1.0f,
                        ssimMin = 1.0f,
                        deltaE00Mean = 0f,
                        deltaE00P95 = 0f,
                        normalAngleMeanDeg = 0f,
                        normalAngleP95Deg = 0f,
                        grayscaleRmse255 = 0f,
                        cutoutIoUMin = 1.0f,
                        blendAlphaRmse255 = 0f,
                        minPixelDensity = 4096,
                        maxPixelDensity = 8192,
                    };

                case QualityTier.High:
                    return new QualityParameters
                    {
                        quality = 0.95f,
                        msSsimMin = 0.995f,
                        ssimMin = 0.990f,
                        deltaE00Mean = 0.8f,
                        deltaE00P95 = 1.5f,
                        normalAngleMeanDeg = 0.75f,
                        normalAngleP95Deg = 1.5f,
                        grayscaleRmse255 = 1.0f,
                        cutoutIoUMin = 0.998f,
                        blendAlphaRmse255 = 1.0f,
                        minPixelDensity = 2048,
                        maxPixelDensity = 4096,
                    };

                case QualityTier.Balanced:
                    return new QualityParameters
                    {
                        quality = 0.85f,
                        msSsimMin = 0.985f,
                        ssimMin = 0.975f,
                        deltaE00Mean = 1.5f,
                        deltaE00P95 = 2.5f,
                        normalAngleMeanDeg = 1.5f,
                        normalAngleP95Deg = 3.0f,
                        grayscaleRmse255 = 2.0f,
                        cutoutIoUMin = 0.993f,
                        blendAlphaRmse255 = 2.0f,
                        minPixelDensity = 2048,
                        maxPixelDensity = 4096,
                    };

                case QualityTier.Performance:
                    return new QualityParameters
                    {
                        quality = 0.70f,
                        msSsimMin = 0.970f,
                        ssimMin = 0.955f,
                        deltaE00Mean = 2.5f,
                        deltaE00P95 = 4.0f,
                        normalAngleMeanDeg = 3.0f,
                        normalAngleP95Deg = 6.0f,
                        grayscaleRmse255 = 4.0f,
                        cutoutIoUMin = 0.985f,
                        blendAlphaRmse255 = 3.5f,
                        minPixelDensity = 1024,
                        maxPixelDensity = 2048,
                    };

                case QualityTier.Extreme:
                    return new QualityParameters
                    {
                        quality = 0.50f,
                        msSsimMin = 0.945f,
                        ssimMin = 0.925f,
                        deltaE00Mean = 4.0f,
                        deltaE00P95 = 6.5f,
                        normalAngleMeanDeg = 5.0f,
                        normalAngleP95Deg = 10.0f,
                        grayscaleRmse255 = 7.0f,
                        cutoutIoUMin = 0.970f,
                        blendAlphaRmse255 = 6.0f,
                        minPixelDensity = 512,
                        maxPixelDensity = 1024,
                    };

                case QualityTier.Custom:
                default:
                    // Custom starts at near-lossless and is never overwritten by tier switching.
                    // 自定义挡位从近无损起步，切换其他挡位时不会被覆盖。
                    return new QualityParameters
                    {
                        quality = 1.00f,
                        msSsimMin = 1.0f,
                        ssimMin = 1.0f,
                        deltaE00Mean = 0f,
                        deltaE00P95 = 0f,
                        normalAngleMeanDeg = 0f,
                        normalAngleP95Deg = 0f,
                        grayscaleRmse255 = 0f,
                        cutoutIoUMin = 1.0f,
                        blendAlphaRmse255 = 0f,
                        minPixelDensity = 2048,
                        maxPixelDensity = 4096,
                    };
            }
        }
    }
}
