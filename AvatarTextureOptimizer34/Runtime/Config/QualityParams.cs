// AvatarTextureOptimizer - QualityParams
// EN: Quality tier parameters. qualityTarget==1 means near-lossless (skip UV island scaling, copy as-is).
// CN: 质量挡位参数。qualityTarget==1 表示近无损（跳过 UV 岛缩放，原样拷贝）。
using System;

namespace net.fosa.avatar_texture_optimizer
{
    public enum QualityPresetEnum
    {
        NearLossless = 0, // 近无损（质量=1，跳过缩放）
        High = 1,         // 高
        Medium = 2,       // 中
        Low = 3,          // 低
        Custom = 4        // 自定义（参数不被其他挡位覆盖）
    }

    /// <summary>
    /// EN: Per-texture-type quality thresholds used by the evaluator (CPU and GPU paths).
    /// CN: 质量评估器使用的分类型质量阈值（CPU 与 GPU 路径共用）。
    /// </summary>
    [Serializable]
    public struct QualityParams
    {
        /// <summary>EN: 0..1 target; 1 = near-lossless: skip scaling for this texture type. / CN: 0..1 目标；1=近无损：跳过该贴图类型的缩放。</summary>
        public float qualityTarget;

        /// <summary>EN: MS-SSIM target (single-scale SSIM when island short side &lt; 176px). / CN: MS-SSIM 目标（岛短边&lt;176px 时用单尺度 SSIM）。</summary>
        public float ssim;

        /// <summary>EN: Max mean CIEDE2000 ΔE. / CN: 最大平均 CIEDE2000 ΔE。</summary>
        public float deltaE;

        /// <summary>EN: Blend alpha linear RMSE. / CN: Blend 透明度线性 RMSE。</summary>
        public float alphaRmse;

        /// <summary>EN: Cutout alpha-contour IoU target. / CN: Cutout 透明度轮廓 IoU 目标。</summary>
        public float alphaIou;

        /// <summary>EN: Normal angle error mean (degrees). / CN: 法线角度误差均值（度）。</summary>
        public float normalAngleMean;

        /// <summary>EN: Normal angle error p95 (degrees). / CN: 法线角度误差 p95（度）。</summary>
        public float normalAngleP95;

        /// <summary>EN: Grayscale linear RMSE on used channels (worst channel). / CN: 灰度图使用通道的线性 RMSE（取最差通道）。</summary>
        public float grayRmse;

        public bool IsNearLossless => qualityTarget >= 1.0f;

        public static QualityParams NearLossless => new QualityParams
        {
            qualityTarget = 1.0f,
            ssim = 1.0f,
            deltaE = 1.0f,
            alphaRmse = 1.0f,
            alphaIou = 1.0f,
            normalAngleMean = 1.0f,
            normalAngleP95 = 1.0f,
            grayRmse = 1.0f
        };

        // EN: High: perceptually near-lossless references (Wang & Bovik SSIM≈0.98; Sharma ΔE≈2.3 JND).
        // CN: 高：感知近无损参考值（SSIM≈0.98；ΔE≈2.3 为 JND 阈值）。
        public static QualityParams High => new QualityParams
        {
            qualityTarget = 0.98f,
            ssim = 0.98f,
            deltaE = 2.3f,
            alphaRmse = 0.02f,
            alphaIou = 0.98f,
            normalAngleMean = 3.0f,
            normalAngleP95 = 8.0f,
            grayRmse = 0.02f
        };

        // EN: Medium: clearly below JND on average, still high fidelity.
        // CN: 中：平均低于 JND，仍有较高保真度。
        public static QualityParams Medium => new QualityParams
        {
            qualityTarget = 0.95f,
            ssim = 0.95f,
            deltaE = 4.0f,
            alphaRmse = 0.04f,
            alphaIou = 0.96f,
            normalAngleMean = 5.0f,
            normalAngleP95 = 12.0f,
            grayRmse = 0.04f
        };

        // EN: Low: aggressive size reduction, visible on close inspection, fine for small screens.
        // CN: 低：激进减负，近看可见差异，适合小屏。
        public static QualityParams Low => new QualityParams
        {
            qualityTarget = 0.90f,
            ssim = 0.90f,
            deltaE = 8.0f,
            alphaRmse = 0.08f,
            alphaIou = 0.92f,
            normalAngleMean = 8.0f,
            normalAngleP95 = 20.0f,
            grayRmse = 0.08f
        };

        public static QualityParams FromPreset(QualityPresetEnum preset) => preset switch
        {
            QualityPresetEnum.NearLossless => NearLossless,
            QualityPresetEnum.High => High,
            QualityPresetEnum.Medium => Medium,
            QualityPresetEnum.Low => Low,
            _ => NearLossless
        };

        /// <summary>EN: Resolve effective params for the given preset (custom keeps its own values). / CN: 解析指定挡位的有效参数（自定义挡位保留自身值）。</summary>
        public static QualityParams Resolve(QualityPresetEnum preset, QualityParams custom)
        {
            return preset == QualityPresetEnum.Custom ? custom : FromPreset(preset);
        }
    }
}
