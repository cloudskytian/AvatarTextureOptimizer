using System;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer
{
    /// <summary>
    /// Numeric thresholds for the target-quality algorithm.
    /// 目标质量算法的数值阈值。
    /// When preset != Custom, changing preset overwrites these fields.
    /// 挡位不是 Custom 时，切换挡位会覆盖这些字段。
    /// Custom defaults to all 1 (near-lossless) and is never overwritten.
    /// Custom 默认全部为 1（近无损），不会被其他挡位覆盖。
    /// </summary>
    [Serializable]
    public class AtoQualitySettings
    {
        [Range(0f, 1f)]
        [Tooltip("MS-SSIM minimum (1 = skip / near lossless). / MS-SSIM 下限（1 = 跳过缩放 / 近无损）。")]
        public float msSsim = 1f;

        [Range(0f, 20f)]
        [Tooltip("Max CIEDE2000 ΔE allowed. 0–1 treated as near-lossless skip when combined with msSsim=1.\nCIEDE2000 ΔE 上限。")]
        public float deltaE = 1f;

        [Range(0f, 1f)]
        [Tooltip("Blend-mode linear alpha RMSE limit. / Blend 模式线性 alpha RMSE 上限。")]
        public float alphaRmse = 1f;

        [Range(0f, 1f)]
        [Tooltip("Cutout contour IoU minimum after clip. / Cutout 在 clip 后的轮廓 IoU 下限。")]
        public float cutoutIou = 1f;

        [Range(0f, 45f)]
        [Tooltip("Normal-map mean angle error in degrees. / 法线贴图平均角度误差（度）。")]
        public float normalAngleDeg = 1f;

        [Range(0f, 45f)]
        [Tooltip("Normal-map p95 angle error in degrees. / 法线贴图 p95 角度误差（度）。")]
        public float normalP95Deg = 1f;

        [Range(0f, 1f)]
        [Tooltip("Gray/mask per-used-channel linear RMSE. / 灰度/蒙版按使用通道的线性 RMSE。")]
        public float grayRmse = 1f;

        public static AtoQualitySettings ForPreset(AtoQualityPreset preset)
        {
            // Values from MS-SSIM (Wang 2004) + CIEDE2000 just-noticeable bands + game normal-map practice.
            // 取值参考 MS-SSIM、CIEDE2000 可察觉带以及游戏法线贴图实践。
            switch (preset)
            {
                case AtoQualityPreset.NearLossless:
                    return new AtoQualitySettings
                    {
                        msSsim = 1f, deltaE = 1f, alphaRmse = 1f, cutoutIou = 1f,
                        normalAngleDeg = 1f, normalP95Deg = 1f, grayRmse = 1f
                    };
                case AtoQualityPreset.Ultra:
                    return new AtoQualitySettings
                    {
                        msSsim = 0.995f, deltaE = 1.0f, alphaRmse = 0.015f, cutoutIou = 0.98f,
                        normalAngleDeg = 3f, normalP95Deg = 6f, grayRmse = 0.015f
                    };
                case AtoQualityPreset.High:
                    return new AtoQualitySettings
                    {
                        msSsim = 0.980f, deltaE = 2.0f, alphaRmse = 0.03f, cutoutIou = 0.95f,
                        normalAngleDeg = 5f, normalP95Deg = 10f, grayRmse = 0.03f
                    };
                case AtoQualityPreset.Medium:
                    return new AtoQualitySettings
                    {
                        msSsim = 0.950f, deltaE = 3.5f, alphaRmse = 0.05f, cutoutIou = 0.90f,
                        normalAngleDeg = 8f, normalP95Deg = 15f, grayRmse = 0.05f
                    };
                case AtoQualityPreset.Low:
                    return new AtoQualitySettings
                    {
                        msSsim = 0.900f, deltaE = 6.0f, alphaRmse = 0.08f, cutoutIou = 0.85f,
                        normalAngleDeg = 12f, normalP95Deg = 22f, grayRmse = 0.08f
                    };
                default:
                    return new AtoQualitySettings();
            }
        }

        public bool IsNearLossless =>
            msSsim >= 0.999f && deltaE <= 1.001f && alphaRmse >= 0.999f && cutoutIou >= 0.999f &&
            grayRmse >= 0.999f;

        public AtoQualitySettings Clone()
        {
            return (AtoQualitySettings)MemberwiseClone();
        }
    }
}
