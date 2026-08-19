using System;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer
{
    /// <summary>
    /// Perceptual quality thresholds used by the scaler. / 缩放器使用的感知质量阈值。
    /// Changing the quality preset overwrites these unless the preset is Custom.
    /// 切换挡位会覆盖这些值；Custom 挡位永不被覆盖。
    /// </summary>
    [Serializable]
    public class AtoQualityThresholds
    {
        [Tooltip("MS-SSIM minimum (1 = identical). / MS-SSIM 下限（1 为完全相同）。")]
        [Range(0f, 1f)]
        public float msSsim = 1f;

        [Tooltip("CIEDE2000 maximum (1 ≈ just noticeable). / CIEDE2000 上限（1 约为一眼刚可辨）。")]
        public float deltaE = 1f;

        [Tooltip("Linear alpha RMSE maximum for Blend materials. / Blend 材质的线性 Alpha RMSE 上限。")]
        public float alphaRmse = 1f;

        [Tooltip("Cutout silhouette IoU minimum after applying cutoff. / 应用 Cutoff 后的轮廓 IoU 下限。")]
        [Range(0f, 1f)]
        public float cutoutIou = 1f;

        [Tooltip("Mean normal-map angle error in degrees. / 法线平均角度误差（度）。")]
        public float normalMeanDegrees = 1f;

        [Tooltip("p95 normal-map angle error in degrees. / 法线 p95 角度误差（度）。")]
        public float normalP95Degrees = 1f;

        [Tooltip("Linear RMSE maximum on used gray channels. / 灰度已用通道的线性 RMSE 上限。")]
        public float grayRmse = 1f;

        /// <summary>
        /// True when every field is ~1, which Custom uses as near-lossless. / 全部接近 1 时视为近无损。
        /// </summary>
        public bool IsNearLossless =>
            msSsim >= 0.999f &&
            deltaE <= 1.001f &&
            alphaRmse <= 1.001f &&
            cutoutIou >= 0.999f &&
            normalMeanDegrees <= 1.001f &&
            normalP95Degrees <= 1.001f &&
            grayRmse <= 1.001f;

        public AtoQualityThresholds Clone()
        {
            return (AtoQualityThresholds)MemberwiseClone();
        }

        public void CopyFrom(AtoQualityThresholds other)
        {
            if (other == null) return;
            msSsim = other.msSsim;
            deltaE = other.deltaE;
            alphaRmse = other.alphaRmse;
            cutoutIou = other.cutoutIou;
            normalMeanDegrees = other.normalMeanDegrees;
            normalP95Degrees = other.normalP95Degrees;
            grayRmse = other.grayRmse;
        }
    }

    /// <summary>
    /// Per-kind import / compression switches. / 按贴图类型区分的导入与压缩开关。
    /// </summary>
    [Serializable]
    public class AtoKindFormatSettings
    {
        public AtoSafeFormat opaqueFormat = AtoSafeFormat.Auto;
        public AtoSafeFormat transparentFormat = AtoSafeFormat.Auto;
        public AtoSafeFormat normalFormat = AtoSafeFormat.Auto;
        public AtoSafeFormat grayFormat = AtoSafeFormat.Auto;

        [Tooltip("Single toggle: mipmaps AND streaming mipmaps (VRChat requires them together). / 同时控制 Mipmap 与 MipStreaming（VRChat 要求绑定）。")]
        public bool enableMipStreaming = true;

        public AtoKindFormatSettings Clone()
        {
            return (AtoKindFormatSettings)MemberwiseClone();
        }
    }

    /// <summary>
    /// Full set of platform-overridable parameters. / 可按平台覆盖的全部参数。
    /// </summary>
    [Serializable]
    public class AtoPlatformSettings
    {
        public AtoKindFormatSettings formats = new AtoKindFormatSettings();

        [Tooltip("Experimental non-power-of-two atlas sizes. / 实验性非 2 次幂图集边长。")]
        public bool experimentalNpot;

        public AtoPlatformSettings Clone()
        {
            return new AtoPlatformSettings
            {
                formats = formats != null ? formats.Clone() : new AtoKindFormatSettings(),
                experimentalNpot = experimentalNpot
            };
        }
    }
}
