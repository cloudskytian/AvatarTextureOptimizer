// ATO — Avatar Texture Optimizer
// Quality presets and tunable quality parameters.
// 质量挡位与可调质量参数。
//
// Preset values are chosen from academic / industry references:
//   MS-SSIM / SSIM  — Wang et al., "Multi-scale structural similarity" (2003/2004).
//   Delta-E 2000    — Sharma, Wu & Dalal, "The CIEDE2000 color-difference formula" (2005).
//                     Thresholds ~= just-noticeable difference: ΔE <= 2 imperceptible, <= 3 almost imperceptible.
//   Normal angle    — angular deviation of decoded normals; ~2 deg is visually hard to notice.
//   Alpha RMSE / IoU— linear-space alpha error (blend) and clipped-outline intersection-over-union (cutout).
// 挡位取值参考学术/业内成果：
//   MS-SSIM / SSIM — Wang et al. 多尺度结构相似度；
//   ΔE2000         — Sharma 等的 CIEDE2000 色差公式（ΔE≤2 几乎不可感知，≤3 极难感知）；
//   法线角度误差    — 解码后法线方向偏差，约 2° 肉眼难辨；
//   Alpha RMSE/IoU — 线性空间 alpha 误差（Blend）与裁剪轮廓交并比（Cutout）。

using System;
using UnityEngine;

namespace net.fosa.ato
{
    /// <summary>
    /// Target quality presets. 目标质量挡位。
    /// </summary>
    public enum ATOQualityPreset
    {
        /// <summary>Balanced — the default. 均衡（默认）。</summary>
        Balanced = 0,
        /// <summary>High quality. 高质量。</summary>
        High = 1,
        /// <summary>Low quality / small size. 低质量 / 体积优先。</summary>
        Low = 2,
        /// <summary>Lossless: quality == 1, islands are copied as-is. 近无损：质量 = 1，原样拷贝。</summary>
        Lossless = 3,
        /// <summary>User-defined; defaults to all-1 (near lossless). 自定义；默认全 1（近无损）。</summary>
        Custom = 4,
    }

    /// <summary>
    /// The full set of tunable quality thresholds used by the target-quality algorithm.
    /// 目标质量算法使用的全部可调阈值。
    /// </summary>
    [Serializable]
    public struct ATOQualityParameters
    {
        /// <summary>MS-SSIM threshold (0..1). Islands with bounding box short side &lt; 176px fall back to single-scale SSIM; &lt; 11px ignore this metric. MS-SSIM 阈值（0..1）。</summary>
        [Range(0f, 1f)] public float msSsim;
        /// <summary>CIEDE2000 Delta-E threshold (>= 0). ΔE2000 色差阈值（>= 0）。</summary>
        [Min(0f)] public float deltaE;
        /// <summary>Normal map angular error threshold in degrees. 法线贴图角度误差阈值（度）。</summary>
        [Min(0f)] public float normalAngleDeg;
        /// <summary>Normal map p95 angular error threshold in degrees. 法线贴图 p95 角度误差阈值（度）。</summary>
        [Min(0f)] public float normalAngleP95Deg;
        /// <summary>Alpha linear RMSE threshold (blend mode). Alpha 线性 RMSE 阈值（Blend 模式）。</summary>
        [Min(0f)] public float alphaRmse;
        /// <summary>Alpha clipped-outline IoU threshold (cutout mode). Alpha 裁剪轮廓 IoU 阈值（Cutout 模式）。</summary>
        [Range(0f, 1f)] public float alphaIou;
        /// <summary>Grayscale linear-space RMSE threshold, per channel worst. 灰度贴图线性空间 RMSE 阈值（逐通道取最差）。</summary>
        [Min(0f)] public float grayRmse;

        /// <summary>True when all metrics demand perfect fidelity (quality == 1). 所有指标均为无损（质量 = 1）时为 true。</summary>
        public bool IsLossless => msSsim >= 1f - 1e-6f &&
                                  deltaE <= 1e-6f &&
                                  normalAngleDeg <= 1e-6f &&
                                  normalAngleP95Deg <= 1e-6f &&
                                  alphaRmse <= 1e-6f &&
                                  alphaIou >= 1f - 1e-6f &&
                                  grayRmse <= 1e-6f;

        public static ATOQualityParameters Lossless() => new ATOQualityParameters
        {
            msSsim = 1f,
            deltaE = 0f,
            normalAngleDeg = 0f,
            normalAngleP95Deg = 0f,
            alphaRmse = 0f,
            alphaIou = 1f,
            grayRmse = 0f,
        };

        public static ATOQualityParameters High() => new ATOQualityParameters
        {
            msSsim = 0.990f,
            deltaE = 1.5f,
            normalAngleDeg = 1.5f,
            normalAngleP95Deg = 3.0f,
            alphaRmse = 0.012f,
            alphaIou = 0.992f,
            grayRmse = 0.010f,
        };

        public static ATOQualityParameters Balanced() => new ATOQualityParameters
        {
            msSsim = 0.980f,
            deltaE = 2.5f,
            normalAngleDeg = 2.5f,
            normalAngleP95Deg = 5.0f,
            alphaRmse = 0.02f,
            alphaIou = 0.985f,
            grayRmse = 0.016f,
        };

        public static ATOQualityParameters Low() => new ATOQualityParameters
        {
            msSsim = 0.960f,
            deltaE = 4.0f,
            normalAngleDeg = 4.5f,
            normalAngleP95Deg = 8.0f,
            alphaRmse = 0.035f,
            alphaIou = 0.970f,
            grayRmse = 0.030f,
        };

        /// <summary>Resolve the parameters for a preset. 解析某挡位的参数。</summary>
        public static ATOQualityParameters For(ATOQualityPreset preset, ATOQualityParameters custom)
        {
            switch (preset)
            {
                case ATOQualityPreset.High: return High();
                case ATOQualityPreset.Low: return Low();
                case ATOQualityPreset.Lossless: return Lossless();
                case ATOQualityPreset.Custom: return custom;
                case ATOQualityPreset.Balanced:
                default:
                    return Balanced();
            }
        }
    }
}
