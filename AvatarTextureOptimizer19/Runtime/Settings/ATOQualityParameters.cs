// English: Numeric quality thresholds. Preset changes overwrite these unless Custom.
// 中文：质量数值阈值。非 Custom 挡位变化时覆盖这些值。
using System;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer
{
    [Serializable]
    public class ATOQualityParameters
    {
        [Range(0f, 1f)]
        [Tooltip("1 = near-lossless, skip UV scale for this texture type (including solid islands).\n1 = 近无损，跳过该贴图类型岛的 UV 缩放（含纯色）。")]
        public float targetQuality = 0.75f;

        [Range(0f, 1f)]
        [Tooltip("Minimum MS-SSIM after upsample. Higher is stricter.\n上采样后最低 MS-SSIM，越大越严。")]
        public float msSsimMin = 0.980f;

        [Range(0f, 20f)]
        [Tooltip("Maximum CIEDE2000. Lower is stricter.\n最大 CIEDE2000，越小越严。")]
        public float deltaEMax = 2.0f;

        [Range(0f, 1f)]
        [Tooltip("Maximum linear alpha RMSE for Blend materials.\nBlend 材质的最大线性 alpha RMSE。")]
        public float alphaRmseMax = 0.030f;

        [Range(0f, 1f)]
        [Tooltip("Minimum clip-contour IoU for Cutout materials.\nCutout 材质裁剪轮廓的最小 IoU。")]
        public float cutoutIouMin = 0.980f;

        [Range(0f, 45f)]
        [Tooltip("Maximum p95 normal-map angle error in degrees.\n法线贴图角度误差 p95 上限（度）。")]
        public float normalP95DegMax = 8.0f;

        [Range(0f, 1f)]
        [Tooltip("Maximum per-channel linear RMSE for gray / mask maps (worst channel).\n灰度/蒙版逐通道线性 RMSE 上限（取最差通道）。")]
        public float grayRmseMax = 0.030f;

        public static ATOQualityParameters FromPreset(ATOQualityPreset preset)
        {
            var p = new ATOQualityParameters();
            switch (preset)
            {
                case ATOQualityPreset.NearLossless:
                    p.targetQuality = 1.00f;
                    p.msSsimMin = 1.00f;
                    p.deltaEMax = 0.00f;
                    p.alphaRmseMax = 0.00f;
                    p.cutoutIouMin = 1.00f;
                    p.normalP95DegMax = 0.00f;
                    p.grayRmseMax = 0.00f;
                    break;
                case ATOQualityPreset.Ultra:
                    p.targetQuality = 0.90f;
                    p.msSsimMin = 0.995f;
                    p.deltaEMax = 0.80f;
                    p.alphaRmseMax = 0.010f;
                    p.cutoutIouMin = 0.995f;
                    p.normalP95DegMax = 3.0f;
                    p.grayRmseMax = 0.010f;
                    break;
                case ATOQualityPreset.High:
                    p.targetQuality = 0.75f;
                    p.msSsimMin = 0.980f;
                    p.deltaEMax = 2.00f;
                    p.alphaRmseMax = 0.030f;
                    p.cutoutIouMin = 0.980f;
                    p.normalP95DegMax = 8.0f;
                    p.grayRmseMax = 0.030f;
                    break;
                case ATOQualityPreset.Medium:
                    p.targetQuality = 0.55f;
                    p.msSsimMin = 0.950f;
                    p.deltaEMax = 3.50f;
                    p.alphaRmseMax = 0.060f;
                    p.cutoutIouMin = 0.950f;
                    p.normalP95DegMax = 12.0f;
                    p.grayRmseMax = 0.060f;
                    break;
                case ATOQualityPreset.Low:
                    p.targetQuality = 0.35f;
                    p.msSsimMin = 0.900f;
                    p.deltaEMax = 6.00f;
                    p.alphaRmseMax = 0.100f;
                    p.cutoutIouMin = 0.900f;
                    p.normalP95DegMax = 18.0f;
                    p.grayRmseMax = 0.100f;
                    break;
                case ATOQualityPreset.Custom:
                    p.targetQuality = 1.00f;
                    p.msSsimMin = 1.00f;
                    p.deltaEMax = 0.00f;
                    p.alphaRmseMax = 0.00f;
                    p.cutoutIouMin = 1.00f;
                    p.normalP95DegMax = 0.00f;
                    p.grayRmseMax = 0.00f;
                    break;
            }

            return p;
        }

        public void CopyFrom(ATOQualityParameters other)
        {
            if (other == null) return;
            targetQuality = other.targetQuality;
            msSsimMin = other.msSsimMin;
            deltaEMax = other.deltaEMax;
            alphaRmseMax = other.alphaRmseMax;
            cutoutIouMin = other.cutoutIouMin;
            normalP95DegMax = other.normalP95DegMax;
            grayRmseMax = other.grayRmseMax;
        }

        public ATOQualityParameters Clone()
        {
            var c = new ATOQualityParameters();
            c.CopyFrom(this);
            return c;
        }
    }
}
