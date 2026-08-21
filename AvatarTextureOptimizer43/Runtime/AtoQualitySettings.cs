using System;
using UnityEngine;

namespace Fosa.ATO
{
    /// <summary>
    /// Numeric quality thresholds. Named presets overwrite these; Custom does not.
    /// Values of 1 mean "near-lossless / skip this metric's contribution as a pass bar of 1".
    /// 质量数值阈值。具名挡位会覆盖；Custom 不会。
    /// 值为 1 表示近无损（MS-SSIM/IoU 为 1，误差类阈值为“几乎不许误差”的归一化上限）。
    /// </summary>
    [Serializable]
    public class AtoQualitySettings
    {
        [Tooltip("MS-SSIM pass threshold in [0,1]. Higher = stricter. MS-SSIM 通过阈值，越高越严。")]
        [Range(0f, 1f)] public float msSsim = 1f;

        [Tooltip("Mean CIEDE2000 pass ceiling. Lower = stricter. 平均 ΔE00 上限，越低越严。")]
        [Min(0f)] public float deltaE00Mean = 1f;

        [Tooltip("p95 CIEDE2000 pass ceiling. p95 ΔE00 上限。")]
        [Min(0f)] public float deltaE00P95 = 1f;

        [Tooltip("Mean normal-map angular error in degrees. 法线平均角度误差（度）。")]
        [Min(0f)] public float normalAngleMeanDeg = 1f;

        [Tooltip("p95 normal-map angular error in degrees. 法线 p95 角度误差（度）。")]
        [Min(0f)] public float normalAngleP95Deg = 1f;

        [Tooltip("Cutout silhouette IoU pass threshold. Cutout 轮廓 IoU 阈值。")]
        [Range(0f, 1f)] public float alphaIou = 1f;

        [Tooltip("Blend-mode linear-space alpha RMSE ceiling. Blend 模式线性 alpha RMSE 上限。")]
        [Min(0f)] public float alphaRmse = 1f;

        [Tooltip("Gray-texture per-used-channel linear RMSE ceiling. 灰度贴图已用通道线性 RMSE 上限。")]
        [Min(0f)] public float grayRmse = 1f;

        /// <summary>
        /// True when this preset means "do not scale UV islands / copy pixels as-is".
        /// 是否跳过 UV 缩放、原样拷贝。
        /// </summary>
        public bool IsLossless =>
            msSsim >= 0.999f
            && deltaE00Mean <= 1.001f
            && deltaE00P95 <= 1.001f
            && normalAngleMeanDeg <= 1.001f
            && normalAngleP95Deg <= 1.001f
            && alphaIou >= 0.999f
            && alphaRmse <= 1.001f
            && grayRmse <= 1.001f
            && _forceLossless;

        [SerializeField] public bool _forceLossless;

        public AtoQualitySettings Clone()
        {
            return (AtoQualitySettings)MemberwiseClone();
        }

        /// <summary>
        /// Research-backed defaults (Wang 2003 MS-SSIM; Sharma 2005 CIEDE2000; typical JND ΔE00≈2.3).
        /// 依据学术与业内常用阈值给出的默认挡位。
        /// </summary>
        public static AtoQualitySettings ForPreset(AtoQualityPreset preset)
        {
            switch (preset)
            {
                case AtoQualityPreset.Lossless:
                    return Make(1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, forceLossless: true);
                case AtoQualityPreset.Ultra:
                    // Visually lossless under close inspection.
                    return Make(0.99f, 1.0f, 2.3f, 5f, 10f, 0.995f, 0.01f, 0.01f);
                case AtoQualityPreset.High:
                    // Default. Indistinguishable at typical VRChat distance (~0.5–3 m).
                    return Make(0.97f, 2.0f, 4.0f, 8f, 15f, 0.99f, 0.02f, 0.02f);
                case AtoQualityPreset.Medium:
                    return Make(0.94f, 3.5f, 6.0f, 12f, 20f, 0.97f, 0.04f, 0.04f);
                case AtoQualityPreset.Low:
                    return Make(0.90f, 5.0f, 10.0f, 18f, 30f, 0.94f, 0.08f, 0.08f);
                default:
                    // Custom: all 1 = near-lossless, user edits freely.
                    return Make(1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, forceLossless: true);
            }
        }

        static AtoQualitySettings Make(
            float ssim, float deMean, float deP95, float nMean, float nP95,
            float iou, float aRmse, float gRmse, bool forceLossless = false)
        {
            return new AtoQualitySettings
            {
                msSsim = ssim,
                deltaE00Mean = deMean,
                deltaE00P95 = deP95,
                normalAngleMeanDeg = nMean,
                normalAngleP95Deg = nP95,
                alphaIou = iou,
                alphaRmse = aRmse,
                grayRmse = gRmse,
                _forceLossless = forceLossless
            };
        }

        public void CopyFrom(AtoQualitySettings other)
        {
            msSsim = other.msSsim;
            deltaE00Mean = other.deltaE00Mean;
            deltaE00P95 = other.deltaE00P95;
            normalAngleMeanDeg = other.normalAngleMeanDeg;
            normalAngleP95Deg = other.normalAngleP95Deg;
            alphaIou = other.alphaIou;
            alphaRmse = other.alphaRmse;
            grayRmse = other.grayRmse;
            _forceLossless = other._forceLossless;
        }
    }
}
