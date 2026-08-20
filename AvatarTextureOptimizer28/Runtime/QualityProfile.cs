using System;
using UnityEngine;

namespace net.fosa.ato
{
    /// <summary>
    /// EN: The concrete numeric thresholds used by the target quality algorithm.
    ///     Every threshold is a *pass* condition: a candidate island scale is accepted only when
    ///     all of the applicable metrics are at least as good as the value stored here.
    ///     Defaults are derived from published perceptual thresholds, see <see cref="QualityPresets"/>.
    /// ZH: 目标质量算法使用的具体数值阈值。
    ///     每个阈值都是"通过条件"：只有当所有适用的度量都不差于此处的值时，候选的岛缩放比例才被接受。
    ///     默认值来自公开的感知阈值研究，详见 <see cref="QualityPresets"/>。
    /// </summary>
    [Serializable]
    public struct QualityProfile
    {
        /// <summary>
        /// EN: Scalar 0..1 "target quality" knob. 1 means near lossless and short circuits all rescaling.
        /// ZH: 0..1 的标量"目标质量"。为 1 时表示近无损，会短路掉所有缩放。
        /// </summary>
        [Range(0f, 1f)] public float targetQuality;

        /// <summary>EN: Minimum accepted MS-SSIM (or single scale SSIM for small islands), 0..1.
        /// ZH: 可接受的最小 MS-SSIM（小岛为单尺度 SSIM），0..1。</summary>
        [Range(0f, 1f)] public float minMsSsim;

        /// <summary>EN: Maximum accepted mean CIEDE2000 colour difference. ZH: 可接受的最大平均 CIEDE2000 色差。</summary>
        [Min(0f)] public float maxDeltaE2000Mean;

        /// <summary>EN: Maximum accepted 95th percentile CIEDE2000 colour difference.
        /// ZH: 可接受的最大 95 分位 CIEDE2000 色差。</summary>
        [Min(0f)] public float maxDeltaE2000P95;

        /// <summary>EN: Minimum accepted silhouette IoU after applying the material cutoff (Cutout materials).
        /// ZH: 应用材质 Cutoff 后可接受的最小轮廓 IoU（Cutout 材质）。</summary>
        [Range(0f, 1f)] public float minAlphaCutoutIoU;

        /// <summary>EN: Maximum accepted linear RMSE on the alpha channel (Blend materials).
        /// ZH: alpha 通道上可接受的最大线性 RMSE（Blend 材质）。</summary>
        [Min(0f)] public float maxAlphaBlendRmse;

        /// <summary>EN: Maximum accepted mean normal angular error, in degrees.
        /// ZH: 可接受的最大平均法线角度误差（度）。</summary>
        [Min(0f)] public float maxNormalAngleMeanDeg;

        /// <summary>EN: Maximum accepted 95th percentile normal angular error, in degrees.
        /// ZH: 可接受的最大 95 分位法线角度误差（度）。</summary>
        [Min(0f)] public float maxNormalAngleP95Deg;

        /// <summary>EN: Maximum accepted linear RMSE for data / grayscale textures, per used channel.
        /// ZH: 数据/灰度贴图每个被使用通道上可接受的最大线性 RMSE。</summary>
        [Min(0f)] public float maxGrayscaleRmse;

        /// <summary>
        /// EN: Returns true when this profile is effectively lossless and island rescaling must be skipped.
        /// ZH: 当该配置实际为无损、必须跳过岛缩放时返回 true。
        /// </summary>
        public bool IsLossless => targetQuality >= 0.9999f;
    }

    /// <summary>
    /// EN: Built-in quality presets.
    ///
    ///     Rationale for the numbers (kept here so future maintainers do not "tune by vibes"):
    ///       * MS-SSIM  - Wang, Simoncelli &amp; Bovik, "Multi-scale structural similarity for image quality
    ///                    assessment", Asilomar 2003. MS-SSIM >= 0.99 is routinely treated as visually
    ///                    transparent for texture-like content; 0.98 is "high", 0.95 the usual
    ///                    "acceptable for lossy delivery" floor.
    ///       * CIEDE2000 - Luo, Cui &amp; Rigg (2001) and ISO/CIE 11664-6. dE00 = 1.0 is the classical
    ///                    just-noticeable difference for a trained observer under reference conditions;
    ///                    graphic-arts tolerance (ISO 12647 / Fogra) sits around dE00 2.0, and dE00 5.0
    ///                    is where an untrained observer reliably notices a shift.
    ///       * Normal maps - angular error is the meaningful metric, not RGB error. 1 degree is below the
    ///                    quantisation floor of BC5 at typical resolutions, 2 degrees is imperceptible on
    ///                    toon shading, 5 degrees starts to visibly soften highlights.
    ///       * Alpha - Cutout uses silhouette IoU because only the thresholded shape matters; 0.997 keeps
    ///                    sub-pixel hair strands intact. Blend uses linear RMSE because the whole ramp matters.
    ///       * Grayscale - linear RMSE thresholds mirror an 8 bit quantisation step (1/255 = 0.0039) scaled
    ///                    by the tier.
    ///
    /// ZH: 内置质量挡位预设。
    ///
    ///     参数依据（留在此处以避免后来者"凭感觉调参"）：
    ///       * MS-SSIM   - Wang, Simoncelli &amp; Bovik，《Multi-scale structural similarity for image quality
    ///                     assessment》，Asilomar 2003。对纹理类内容，MS-SSIM >= 0.99 通常被视为视觉无损；
    ///                     0.98 为"高质量"；0.95 是常见的"有损分发可接受"下限。
    ///       * CIEDE2000 - Luo, Cui &amp; Rigg (2001) 与 ISO/CIE 11664-6。dE00 = 1.0 是受训观察者在参考条件下
    ///                     的经典恰可察觉差；印刷工业容差（ISO 12647 / Fogra）约在 dE00 2.0；
    ///                     dE00 5.0 时未受训观察者也能稳定察觉。
    ///       * 法线贴图  - 有意义的度量是角度误差而非 RGB 误差。1 度低于 BC5 在常见分辨率下的量化底噪；
    ///                     2 度在卡通着色下不可察觉；5 度开始明显软化高光。
    ///       * Alpha     - Cutout 用轮廓 IoU，因为只有阈值化后的形状有意义，0.997 可保住亚像素发丝；
    ///                     Blend 用线性 RMSE，因为整条渐变都有意义。
    ///       * 灰度      - 线性 RMSE 阈值以 8 位量化步长（1/255 = 0.0039）为基准按挡位放大。
    /// </summary>
    public static class QualityPresets
    {
        /// <summary>
        /// EN: Returns the built-in profile for a tier. <see cref="QualityTier.Custom"/> returns the
        ///     lossless profile, which is the documented default for a fresh custom tier.
        /// ZH: 返回某挡位的内置配置。<see cref="QualityTier.Custom"/> 返回无损配置，
        ///     这也是新建自定义挡位时的默认值。
        /// </summary>
        public static QualityProfile Get(QualityTier tier)
        {
            switch (tier)
            {
                case QualityTier.VeryHigh:
                    return new QualityProfile
                    {
                        targetQuality = 0.95f,
                        minMsSsim = 0.995f,
                        maxDeltaE2000Mean = 1.0f,
                        maxDeltaE2000P95 = 2.0f,
                        minAlphaCutoutIoU = 0.999f,
                        maxAlphaBlendRmse = 0.004f,
                        maxNormalAngleMeanDeg = 1.0f,
                        maxNormalAngleP95Deg = 2.0f,
                        maxGrayscaleRmse = 0.005f,
                    };
                case QualityTier.High:
                    return new QualityProfile
                    {
                        targetQuality = 0.85f,
                        minMsSsim = 0.99f,
                        maxDeltaE2000Mean = 2.0f,
                        maxDeltaE2000P95 = 4.0f,
                        minAlphaCutoutIoU = 0.997f,
                        maxAlphaBlendRmse = 0.008f,
                        maxNormalAngleMeanDeg = 2.0f,
                        maxNormalAngleP95Deg = 4.0f,
                        maxGrayscaleRmse = 0.010f,
                    };
                case QualityTier.Medium:
                    return new QualityProfile
                    {
                        targetQuality = 0.70f,
                        minMsSsim = 0.98f,
                        maxDeltaE2000Mean = 3.0f,
                        maxDeltaE2000P95 = 6.0f,
                        minAlphaCutoutIoU = 0.99f,
                        maxAlphaBlendRmse = 0.016f,
                        maxNormalAngleMeanDeg = 3.5f,
                        maxNormalAngleP95Deg = 7.0f,
                        maxGrayscaleRmse = 0.020f,
                    };
                case QualityTier.Low:
                    return new QualityProfile
                    {
                        targetQuality = 0.50f,
                        minMsSsim = 0.96f,
                        maxDeltaE2000Mean = 5.0f,
                        maxDeltaE2000P95 = 10.0f,
                        minAlphaCutoutIoU = 0.98f,
                        maxAlphaBlendRmse = 0.030f,
                        maxNormalAngleMeanDeg = 5.0f,
                        maxNormalAngleP95Deg = 10.0f,
                        maxGrayscaleRmse = 0.040f,
                    };
                case QualityTier.Lossless:
                case QualityTier.Custom:
                default:
                    return Lossless;
            }
        }

        /// <summary>
        /// EN: The near-lossless profile. targetQuality == 1 means "never rescale, never resample".
        /// ZH: 近无损配置。targetQuality == 1 表示"永不缩放、永不重采样"。
        /// </summary>
        public static QualityProfile Lossless => new QualityProfile
        {
            targetQuality = 1.0f,
            minMsSsim = 1.0f,
            maxDeltaE2000Mean = 0.0f,
            maxDeltaE2000P95 = 0.0f,
            minAlphaCutoutIoU = 1.0f,
            maxAlphaBlendRmse = 0.0f,
            maxNormalAngleMeanDeg = 0.0f,
            maxNormalAngleP95Deg = 0.0f,
            maxGrayscaleRmse = 0.0f,
        };
    }
}
