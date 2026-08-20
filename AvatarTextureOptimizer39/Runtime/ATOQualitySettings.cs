// Avatar Texture Optimizer (ATO)
// Bilingual comments: English + Simplified Chinese. 双语注释：英文 + 简体中文。

using System;

namespace AvatarTextureOptimizer
{
    /// <summary>
    /// Per-texture-type quality thresholds. All metrics must pass ("worst of all wins")
    /// for an island scale to be accepted. Values are compared after bilinear
    /// upsampling the shrunk island back to the original resolution.
    ///
    /// 每种贴图类型的质量阈值。所有指标必须全部达标（"木桶效应"）才接受该缩放。
    /// 对比前将缩小后的岛双线性上采样回原尺寸。
    ///
    /// Metric reference (学术/业内依据):
    ///  - MS-SSIM: multi-scale structural similarity (Wang et al. 2003). Near-lossless ≥ 0.999,
    ///    perceptually transparent ≈ 0.99, visible degradation &lt; 0.95.
    ///  - ΔE CIEDE2000: perceptual color difference (Sharma et al. 2005). ΔE &lt; 1 imperceptible,
    ///    ΔE &lt; 2.3 "just noticeable difference", ΔE &lt; 4 acceptable for most content.
    ///  - Cutout alpha: clip-then-IoU of silhouettes. Blend alpha: linear RMSE.
    ///  - Normal maps: angular error after correct decode/resample/renormalize/encode.
    /// </summary>
    [Serializable]
    public struct ATOQualityThresholds
    {
        /// <summary>MS-SSIM threshold [0..1], higher = stricter. MS-SSIM 阈值 [0..1]，越大越严。</summary>
        public float msSsim;

        /// <summary>ΔE(CIEDE2000) threshold, lower = stricter. ΔE 阈值，越小越严。</summary>
        public float deltaE;

        /// <summary>Cutout alpha silhouette IoU threshold [0..1]. Cutout 轮廓 IoU 阈值。</summary>
        public float alphaIoU;

        /// <summary>Blend alpha linear RMSE threshold (0..1 in alpha units). Blend 线性 RMSE 阈值。</summary>
        public float alphaRmse;

        /// <summary>Normal map angular error threshold, degrees. 法线角度误差阈值（度）。</summary>
        public float normalAngleDegrees;

        /// <summary>Grayscale per-channel linear RMSE threshold (0..1). 灰度逐通道线性 RMSE 阈值。</summary>
        public float grayRmse;

        public static ATOQualityThresholds NearLossless()
        {
            // Custom tier defaults to all-1 (near lossless). 自定义挡位默认全 1（近无损）。
            return new ATOQualityThresholds
            {
                msSsim = 1.0f,
                deltaE = 1.0f,
                alphaIoU = 1.0f,
                alphaRmse = 0.0f,
                normalAngleDegrees = 0.0f,
                grayRmse = 0.0f,
            };
        }

        public static ATOQualityThresholds Ultra()
        {
            return new ATOQualityThresholds
            {
                msSsim = 0.999f,
                deltaE = 1.5f,
                alphaIoU = 0.995f,
                alphaRmse = 1f / 255f,
                normalAngleDegrees = 0.5f,
                grayRmse = 1f / 255f,
            };
        }

        public static ATOQualityThresholds High()
        {
            return new ATOQualityThresholds
            {
                msSsim = 0.99f,
                deltaE = 2.3f, // just noticeable difference (JND). 刚可察觉差异。
                alphaIoU = 0.99f,
                alphaRmse = 2f / 255f,
                normalAngleDegrees = 1.0f,
                grayRmse = 2f / 255f,
            };
        }

        public static ATOQualityThresholds Balanced()
        {
            return new ATOQualityThresholds
            {
                msSsim = 0.97f,
                deltaE = 4.0f,
                alphaIoU = 0.98f,
                alphaRmse = 3f / 255f,
                normalAngleDegrees = 2.0f,
                grayRmse = 3f / 255f,
            };
        }

        public static ATOQualityThresholds Economy()
        {
            return new ATOQualityThresholds
            {
                msSsim = 0.95f,
                deltaE = 6.0f,
                alphaIoU = 0.95f,
                alphaRmse = 5f / 255f,
                normalAngleDegrees = 3.0f,
                grayRmse = 5f / 255f,
            };
        }
    }

    /// <summary>
    /// Quality settings container. Holds the active preset and the custom override.
    /// 质量设置容器。持有当前挡位与自定义覆盖值。
    /// </summary>
    [Serializable]
    public class ATOQualitySettings
    {
        /// <summary>Active quality preset. 当前质量挡位。</summary>
        public ATOQualityLevel level = ATOQualityLevel.High;

        /// <summary>
        /// Custom thresholds (used only when level == Custom). Defaults to all-1.
        /// 自定义阈值（仅 level==Custom 时生效）。默认全 1。
        /// </summary>
        public ATOQualityThresholds custom = ATOQualityThresholds.NearLossless();

        /// <summary>
        /// Smallest island bounding-box short edge (px) below which MS-SSIM falls back
        /// to single-scale SSIM. 岛包围盒短边小于该值(px)时回退单尺度 SSIM。
        /// </summary>
        public int ssFallbackBelowPx = 176;

        /// <summary>
        /// Islands with bounding-box short edge below this are ignored by the SSIM metric.
        /// 包围盒短边小于该值(px)的岛直接忽略 SSIM 参数。
        /// </summary>
        public int ssIgnoreBelowPx = 11;

        /// <summary>Resolve the effective thresholds for the current preset. 解析当前挡位的有效阈值。</summary>
        public ATOQualityThresholds Resolve()
        {
            switch (level)
            {
                case ATOQualityLevel.Ultra: return ATOQualityThresholds.Ultra();
                case ATOQualityLevel.High: return ATOQualityThresholds.High();
                case ATOQualityLevel.Balanced: return ATOQualityThresholds.Balanced();
                case ATOQualityLevel.Economy: return ATOQualityThresholds.Economy();
                default: return custom;
            }
        }
    }
}
