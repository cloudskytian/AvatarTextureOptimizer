// SPDX-License-Identifier: MIT
// EN: Quality parameter model and the built-in presets.
// ZH: 质量参数模型与内置挡位。

using System;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer
{
    /// <summary>
    /// EN: The full set of thresholds used by the target quality algorithm. Every threshold is an
    ///     "acceptance" bound: a candidate downscale is accepted only when EVERY metric passes.
    /// ZH: 目标质量算法使用的全部阈值。每一项都是“通过”界限：只有全部度量都达标，候选缩放才算通过。
    /// </summary>
    /// <remarks>
    /// EN: Default values are derived from published perceptual studies:
    ///     * MS-SSIM (Wang, Simoncelli &amp; Bovik, 2003) - values above ~0.98 are commonly reported as
    ///       visually indistinguishable for texture-like content; 0.95 is the usual "good" boundary.
    ///     * CIEDE2000 (Sharma, Wu &amp; Dalal, 2005) - dE00 of about 1.0 is the just-noticeable difference
    ///       for side by side patches, ~2.3 is the classic JND used for print, &gt;5 is obvious.
    ///     * Normal maps are compared in angular degrees; 1 degree is below shading noise for typical
    ///       avatar lighting, 5+ degrees starts to visibly flatten details.
    /// ZH: 默认值来源于公开的感知研究：
    ///     * MS-SSIM（Wang/Simoncelli/Bovik, 2003）——纹理类内容在约 0.98 以上通常被报告为肉眼无法区分，
    ///       0.95 是常用的“良好”分界线。
    ///     * CIEDE2000（Sharma/Wu/Dalal, 2005）——并排色块的恰可察觉差约为 dE00 = 1.0，
    ///       2.3 是印刷业经典 JND，大于 5 时差异明显。
    ///     * 法线贴图使用角度误差比较；1 度低于常见虚拟形象光照下的着色噪声，超过 5 度开始明显丢失细节。
    /// </remarks>
    [Serializable]
    public sealed class AtoQualityParameters
    {
        /// <summary>EN: Nominal target quality in [0,1]. 1 means "skip resampling entirely". ZH: 名义目标质量，取值 [0,1]。1 表示“完全跳过重采样”。</summary>
        [Range(0f, 1f)] public float targetQuality = 0.70f;

        /// <summary>EN: Minimum accepted MS-SSIM for color textures. ZH: 颜色贴图可接受的最低 MS-SSIM。</summary>
        [Range(0.5f, 1f)] public float minMsSsim = 0.980f;

        /// <summary>EN: Maximum accepted CIEDE2000 colour difference (95th percentile). ZH: 可接受的最大 CIEDE2000 色差（95 分位）。</summary>
        [Range(0f, 20f)] public float maxDeltaE2000 = 3.5f;

        /// <summary>EN: Minimum accepted silhouette IoU after applying the material cutoff (Cutout materials). ZH: 应用材质 cutoff 后可接受的最低轮廓 IoU（Cutout 材质）。</summary>
        [Range(0.5f, 1f)] public float minAlphaIoU = 0.990f;

        /// <summary>EN: Maximum accepted linear RMSE on the alpha channel (Blend materials). ZH: alpha 通道可接受的最大线性 RMSE（Blend 材质）。</summary>
        [Range(0f, 0.5f)] public float maxAlphaRmse = 0.015f;

        /// <summary>EN: Maximum accepted 95th percentile normal deviation, in degrees. ZH: 可接受的法线偏差 95 分位最大值（度）。</summary>
        [Range(0f, 45f)] public float maxNormalAngleP95 = 4.0f;

        /// <summary>EN: Maximum accepted linear RMSE per used channel for grayscale/mask textures. ZH: 灰度/蒙版贴图每个被使用通道可接受的最大线性 RMSE。</summary>
        [Range(0f, 0.5f)] public float maxGrayscaleRmse = 0.015f;

        /// <summary>EN: Lower clamp on texel density (texels per meter of world space surface). ZH: 像素密度下限（世界空间每米表面的像素数）。</summary>
        public AtoPixelDensity minPixelDensity = AtoPixelDensity.D2048;

        /// <summary>EN: Upper clamp on texel density. ZH: 像素密度上限。</summary>
        public AtoPixelDensity maxPixelDensity = AtoPixelDensity.D4096;

        /// <summary>
        /// EN: Creates an independent copy. Used so that switching tiers never mutates the custom tier.
        /// ZH: 创建独立副本。用于保证切换挡位时不会改动自定义挡位。
        /// </summary>
        public AtoQualityParameters Clone()
        {
            return (AtoQualityParameters)MemberwiseClone();
        }

        /// <summary>
        /// EN: Copies every value from <paramref name="other"/> into this instance.
        /// ZH: 将 <paramref name="other"/> 的所有值复制到本实例。
        /// </summary>
        public void CopyFrom(AtoQualityParameters other)
        {
            if (other == null) return;
            targetQuality = other.targetQuality;
            minMsSsim = other.minMsSsim;
            maxDeltaE2000 = other.maxDeltaE2000;
            minAlphaIoU = other.minAlphaIoU;
            maxAlphaRmse = other.maxAlphaRmse;
            maxNormalAngleP95 = other.maxNormalAngleP95;
            maxGrayscaleRmse = other.maxGrayscaleRmse;
            minPixelDensity = other.minPixelDensity;
            maxPixelDensity = other.maxPixelDensity;
        }

        /// <summary>
        /// EN: True when the tier requests a bit-exact result, in which case island scaling is skipped.
        /// ZH: 当挡位要求逐位一致时为 true，此时跳过 UV 岛缩放。
        /// </summary>
        public bool IsLossless => targetQuality >= 0.999f;
    }

    /// <summary>
    /// EN: Factory for the built-in quality tiers.
    /// ZH: 内置质量挡位的工厂。
    /// </summary>
    public static class AtoQualityPresets
    {
        /// <summary>
        /// EN: Returns a fresh parameter set for the given tier. <see cref="AtoQualityTier.Custom"/>
        ///     returns the lossless defaults, which is what a brand new custom tier starts from.
        /// ZH: 返回给定挡位的全新参数集。<see cref="AtoQualityTier.Custom"/> 返回近无损默认值，
        ///     这也是新建自定义挡位的初始状态。
        /// </summary>
        public static AtoQualityParameters Create(AtoQualityTier tier)
        {
            switch (tier)
            {
                case AtoQualityTier.Lossless:
                case AtoQualityTier.Custom:
                    return new AtoQualityParameters
                    {
                        targetQuality = 1.00f,
                        minMsSsim = 1.000f,
                        maxDeltaE2000 = 0.0f,
                        minAlphaIoU = 1.000f,
                        maxAlphaRmse = 0.000f,
                        maxNormalAngleP95 = 0.0f,
                        maxGrayscaleRmse = 0.000f,
                        minPixelDensity = AtoPixelDensity.D2048,
                        maxPixelDensity = AtoPixelDensity.D8192,
                    };
                case AtoQualityTier.VeryHigh:
                    return new AtoQualityParameters
                    {
                        targetQuality = 0.95f,
                        minMsSsim = 0.995f,
                        maxDeltaE2000 = 1.0f,
                        minAlphaIoU = 0.999f,
                        maxAlphaRmse = 0.004f,
                        maxNormalAngleP95 = 1.0f,
                        maxGrayscaleRmse = 0.004f,
                        minPixelDensity = AtoPixelDensity.D2048,
                        maxPixelDensity = AtoPixelDensity.D8192,
                    };
                case AtoQualityTier.High:
                    return new AtoQualityParameters
                    {
                        targetQuality = 0.85f,
                        minMsSsim = 0.990f,
                        maxDeltaE2000 = 2.3f,
                        minAlphaIoU = 0.995f,
                        maxAlphaRmse = 0.008f,
                        maxNormalAngleP95 = 2.0f,
                        maxGrayscaleRmse = 0.008f,
                        minPixelDensity = AtoPixelDensity.D2048,
                        maxPixelDensity = AtoPixelDensity.D4096,
                    };
                case AtoQualityTier.Balanced:
                    return new AtoQualityParameters
                    {
                        targetQuality = 0.70f,
                        minMsSsim = 0.980f,
                        maxDeltaE2000 = 3.5f,
                        minAlphaIoU = 0.990f,
                        maxAlphaRmse = 0.015f,
                        maxNormalAngleP95 = 4.0f,
                        maxGrayscaleRmse = 0.015f,
                        minPixelDensity = AtoPixelDensity.D2048,
                        maxPixelDensity = AtoPixelDensity.D4096,
                    };
                case AtoQualityTier.Performance:
                    return new AtoQualityParameters
                    {
                        targetQuality = 0.50f,
                        minMsSsim = 0.960f,
                        maxDeltaE2000 = 5.0f,
                        minAlphaIoU = 0.980f,
                        maxAlphaRmse = 0.030f,
                        maxNormalAngleP95 = 6.0f,
                        maxGrayscaleRmse = 0.030f,
                        minPixelDensity = AtoPixelDensity.D1024,
                        maxPixelDensity = AtoPixelDensity.D2048,
                    };
                case AtoQualityTier.Mobile:
                    return new AtoQualityParameters
                    {
                        targetQuality = 0.30f,
                        minMsSsim = 0.930f,
                        maxDeltaE2000 = 8.0f,
                        minAlphaIoU = 0.960f,
                        maxAlphaRmse = 0.050f,
                        maxNormalAngleP95 = 10.0f,
                        maxGrayscaleRmse = 0.050f,
                        minPixelDensity = AtoPixelDensity.D512,
                        maxPixelDensity = AtoPixelDensity.D2048,
                    };
                default:
                    return Create(AtoQualityTier.Balanced);
            }
        }
    }
}
