using System;
using System.Collections.Generic;
using UnityEngine;

namespace Fosa.ATO
{
    /// <summary>
    /// 目标质量挡位（预设）。具体参数值见 ATOQualityParams。
    /// Target quality presets. Concrete thresholds live in ATOQualityParams.
    /// </summary>
    public enum ATOQualityPreset
    {
        Lowest = 0,  // 最低 / lowest
        Low = 1,     // 低 / low
        Medium = 2,  // 中 / medium (default)
        High = 3,    // 高 / high
        Ultra = 4,   // 极高 / ultra
        Custom = 5   // 自定义 / custom
    }

    /// <summary>平台目标（用于压缩格式与图集边长上限的 override）。Target platform for format/size overrides.</summary>
    public enum ATOPlatformTarget
    {
        PC = 0,
        Android = 1,
        iOS = 2
    }

    /// <summary>图集 padding（岛间距）挡位。Atlas island padding presets (px).</summary>
    public enum ATOPadding
    {
        P4 = 4,
        P8 = 8,
        P16 = 16,
        P32 = 32,
        P64 = 64
    }

    /// <summary>
    /// 贴图类型组类别，用于区分需要同组图集化的贴图。
    /// Texture type category used for grouping textures that must be atlased together.
    /// </summary>
    public enum ATOTextureType
    {
        MainColor = 0,    // 主色 / albedo
        NormalMap = 1,    // 法线 / normal
        Mask = 2,         // 蒙版/alpha/cutout 遮罩 / mask
        MetallicGloss = 3,// 金属度光滑度 / metallic-smoothness
        Emission = 4,     // 自发光 / emission
        Occlusion = 5,    // AO / occlusion
        MatCap = 6,       // matcap
        Grayscale = 7,    // 灰度（单通道为主）/ grayscale
        Other = 8         // 其他 / other
    }

    /// <summary>
    /// 压缩格式安全枚举。按平台过滤（见 ATOPlatformSettings）。
    /// Safe enumeration of compression formats; filtered per platform.
    /// </summary>
    public enum ATOCompressionFormat
    {
        Auto = 0,   // 由工具选择最优 / auto pick
        None = 1,   // 不压缩（RGBA32 等）/ uncompressed
        DXT1 = 2,   // PC (no alpha)
        DXT5 = 3,   // PC (alpha)
        BC7 = 4,    // PC high quality
        ETC2 = 5,   // Android
        ASTC_6x6 = 6,  // Android/iOS
        ASTC_4x4 = 7,  // Android/iOS
        PVRTC_4BPP = 8, // iOS
        Crunch = 9  // crunch (PC)
    }

    /// <summary>
    /// 质量算法阈值。每个岛在质量算法下必须全部达标才通过。
    /// Quality thresholds. Every island must satisfy ALL thresholds simultaneously.
    /// </summary>
    [Serializable]
    public class ATOQualityParams
    {
        [Tooltip("MS-SSIM 下限（1=近无损）。Lower bound for MS-SSIM (1 = near lossless).")]
        [Range(0.5f, 1f)] public float msSsimThreshold = 0.98f;

        [Tooltip("ΔE(CIEDE2000) 上限（JND≈2.3，<1 几乎不可察觉）。Upper bound for ΔE2000.")]
        [Range(0f, 20f)] public float deltaEThreshold = 2.5f;

        [Tooltip("法线角度误差上限（度）。Max normal angle error in degrees.")]
        [Range(0f, 45f)] public float normalAngleThresholdDeg = 2f;

        [Tooltip("灰度通道线性 RMSE 上限。Max per-channel linear RMSE for grayscale.")]
        [Range(0f, 0.2f)] public float grayscaleRmseThreshold = 0.012f;

        [Tooltip("alpha 轮廓 IoU / 线性 RMSE 阈值。Alpha cutout IoU / blend RMSE threshold.")]
        [Range(0f, 1f)] public float alphaThreshold = 0.98f;

        public static ATOQualityParams FromPreset(ATOQualityPreset preset)
        {
            switch (preset)
            {
                // 参考依据：CIEDE2000 的 JND≈2.3；MS-SSIM≈0.99 可视作近无损（Wang et al.）
                case ATOQualityPreset.Lowest: return new ATOQualityParams { msSsimThreshold = 0.90f, deltaEThreshold = 6.0f, normalAngleThresholdDeg = 6f, grayscaleRmseThreshold = 0.03f, alphaThreshold = 0.90f };
                case ATOQualityPreset.Low:    return new ATOQualityParams { msSsimThreshold = 0.95f, deltaEThreshold = 4.0f, normalAngleThresholdDeg = 4f, grayscaleRmseThreshold = 0.02f, alphaThreshold = 0.95f };
                case ATOQualityPreset.Medium: return new ATOQualityParams { msSsimThreshold = 0.98f, deltaEThreshold = 2.5f, normalAngleThresholdDeg = 2f, grayscaleRmseThreshold = 0.012f, alphaThreshold = 0.98f };
                case ATOQualityPreset.High:   return new ATOQualityParams { msSsimThreshold = 0.995f, deltaEThreshold = 1.5f, normalAngleThresholdDeg = 1f, grayscaleRmseThreshold = 0.006f, alphaThreshold = 0.99f };
                case ATOQualityPreset.Ultra:  return new ATOQualityParams { msSsimThreshold = 0.999f, deltaEThreshold = 0.75f, normalAngleThresholdDeg = 0.5f, grayscaleRmseThreshold = 0.003f, alphaThreshold = 0.995f };
                // Custom 默认全 1（近无损），用户自行修改，不被其他挡位覆盖。
                case ATOQualityPreset.Custom: return new ATOQualityParams { msSsimThreshold = 1f, deltaEThreshold = 0f, normalAngleThresholdDeg = 0f, grayscaleRmseThreshold = 0f, alphaThreshold = 1f };
                default: return new ATOQualityParams();
            }
        }

        public ATOQualityParams Clone() => (ATOQualityParams)MemberwiseClone();
    }

    /// <summary>
    /// 按贴图分类的压缩设置。Compression settings per texture category.
    /// </summary>
    [Serializable]
    public class ATOCompressionSettings
    {
        public ATOCompressionFormat transparent = ATOCompressionFormat.Auto;   // 透明贴图 / alpha textures
        public ATOCompressionFormat opaque = ATOCompressionFormat.Auto;        // 不透明贴图 / opaque textures
        public ATOCompressionFormat normalMap = ATOCompressionFormat.Auto;     // 法线贴图 / normal maps
        public ATOCompressionFormat grayscale = ATOCompressionFormat.Auto;     // 灰度贴图 / grayscale
    }

    /// <summary>
    /// 平台 override 设置。勾选后使用该平台的独立参数，否则用通用最优解。
    /// Per-platform override. When enabled, platform-specific values are used.
    /// </summary>
    [Serializable]
    public class ATOPlatformSettings
    {
        public bool overrideEnabled = false;
        public int maxAtlasSize = 4096; // 移动端默认 4096；PC 默认 8192
        public ATOCompressionSettings compression = new ATOCompressionSettings();
    }
}
