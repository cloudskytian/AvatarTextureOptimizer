// ATOQualityParams.cs - Per-preset quality thresholds. / 各挡位的质量阈值参数。
// Threshold rationale (threshold rationale see ATOQualityPreset docs):
//  - MS-SSIM >= 0.99 is considered visually lossless for textures (Wang et al. 2004);
//  - CIEDE2000 mean <= 1.0 is below JND (Sharma et al. 2005, hue-dependent JND ~1.0-2.3);
//  - Normal map: mean angular error < 1 deg is below perceptual limits for shading;
//  - Alpha cutout IoU >= 0.995 keeps silhouettes stable.
// 阈值依据见 ATOQualityPreset 注释：MS-SSIM>=0.99 视为视觉无损；CIEDE2000 均值<=1.0 低于可觉察差；
// 法线平均角度误差<1°；Cutout 轮廓 IoU>=0.995 可保持剪影稳定。
using System;
using UnityEngine;

namespace Fosa.ATO.Runtime
{
    /// <summary>Serializable quality thresholds. Defaults are "near lossless" (all 1 / strictest).
    /// 可序列化质量阈值。默认值为“近无损”（全部 1 / 最严）。</summary>
    [Serializable]
    public class ATOQualityParams
    {
        [Tooltip("MS-SSIM lower bound [0..1]. Smaller islands (bbox short side < 176px) fall back to single-scale SSIM; < 11px ignores this metric. / MS-SSIM 下界 [0..1]。包围盒短边<176px的岛回退单尺度SSIM；<11px忽略此项。")]
        [Range(0f, 1f)] public float msSsimMin = 1f;

        [Tooltip("CIEDE2000 mean upper bound. / CIEDE2000 均值上界。")]
        [Range(0f, 10f)] public float deltaEMeanMax = 1f;

        [Tooltip("CIEDE2000 95th percentile upper bound. / CIEDE2000 95分位上界。")]
        [Range(0f, 20f)] public float deltaEP95Max = 2f;

        [Tooltip("Alpha RMSE (linear, 0..1) upper bound for Blend mode textures. / Blend 模式下 alpha 线性 RMSE 上界。")]
        [Range(0f, 0.2f)] public float alphaRmseMax = 1f / 255f;

        [Tooltip("Alpha clip contour IoU lower bound for Cutout mode textures. / Cutout 模式下 clip 轮廓 IoU 下界。")]
        [Range(0.8f, 1f)] public float alphaCutoutIouMin = 1f;

        [Tooltip("Normal map mean angular error upper bound (degrees). / 法线平均角度误差上界（度）。")]
        [Range(0f, 10f)] public float normalMeanDegMax = 1f;

        [Tooltip("Normal map 95th percentile angular error upper bound (degrees). / 法线 95分位角度误差上界（度）。")]
        [Range(0f, 45f)] public float normalP95DegMax = 2f;

        [Tooltip("Grayscale per-used-channel linear RMSE upper bound. / 灰度贴图（仅被使用的通道）线性 RMSE 上界。")]
        [Range(0f, 0.2f)] public float grayRmseMax = 1f / 255f;

        // ------------------------------------------------------------------

        /// <summary>Returns a preset parameter set. / 返回某个挡位的参数。</summary>
        public static ATOQualityParams ForPreset(ATOQualityPreset preset)
        {
            switch (preset)
            {
                case ATOQualityPreset.NearLossless:
                    return new ATOQualityParams(); // defaults are all-1 / 默认即全1近无损
                case ATOQualityPreset.High:
                    return new ATOQualityParams
                    {
                        msSsimMin = 0.99f, deltaEMeanMax = 1.5f, deltaEP95Max = 3.0f,
                        alphaRmseMax = 0.004f, alphaCutoutIouMin = 0.995f,
                        normalMeanDegMax = 1.0f, normalP95DegMax = 3.0f, grayRmseMax = 0.004f,
                    };
                case ATOQualityPreset.Medium:
                    return new ATOQualityParams
                    {
                        msSsimMin = 0.97f, deltaEMeanMax = 3.0f, deltaEP95Max = 6.0f,
                        alphaRmseMax = 0.010f, alphaCutoutIouMin = 0.98f,
                        normalMeanDegMax = 2.5f, normalP95DegMax = 8.0f, grayRmseMax = 0.010f,
                    };
                case ATOQualityPreset.Low:
                    return new ATOQualityParams
                    {
                        msSsimMin = 0.93f, deltaEMeanMax = 5.0f, deltaEP95Max = 10.0f,
                        alphaRmseMax = 0.020f, alphaCutoutIouMin = 0.95f,
                        normalMeanDegMax = 4.0f, normalP95DegMax = 15.0f, grayRmseMax = 0.020f,
                    };
                case ATOQualityPreset.Custom:
                default:
                    return new ATOQualityParams(); // custom starts from near lossless / 自定义从近无损开始
            }
        }

        /// <summary>Copy from another instance. / 从另一实例拷贝。</summary>
        public void CopyFrom(ATOQualityParams o)
        {
            msSsimMin = o.msSsimMin; deltaEMeanMax = o.deltaEMeanMax; deltaEP95Max = o.deltaEP95Max;
            alphaRmseMax = o.alphaRmseMax; alphaCutoutIouMin = o.alphaCutoutIouMin;
            normalMeanDegMax = o.normalMeanDegMax; normalP95DegMax = o.normalP95DegMax;
            grayRmseMax = o.grayRmseMax;
        }

        public ATOQualityParams Clone() { var p = new ATOQualityParams(); p.CopyFrom(this); return p; }
    }
}
