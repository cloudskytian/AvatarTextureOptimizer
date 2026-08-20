using System;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer
{
    /// <summary>
    /// Quality presets. / 质量挡位。
    /// The parameter values change with the preset. Custom is user-defined and is NOT overwritten by other presets. /
    /// 挡位变化时具体参数值相应变化；自定义挡位由用户修改，不会被其他挡位覆盖。
    /// </summary>
    public enum AtoQualityPreset
    {
        /// <summary>Ultra high quality. / 极高。</summary>
        Ultra = 0,
        /// <summary>High quality (default). / 高（默认）。</summary>
        High = 1,
        /// <summary>Medium quality. / 中。</summary>
        Medium = 2,
        /// <summary>Low quality. / 低。</summary>
        Low = 3,
        /// <summary>User custom (all thresholds default to 1 = near lossless). / 自定义（默认全 1，近无损）。</summary>
        Custom = 4,
    }

    /// <summary>
    /// All quality thresholds used by the target-quality algorithm. / 目标质量算法使用的全部阈值。
    /// </summary>
    [Serializable]
    public struct AtoQualityThresholds
    {
        /// <summary>MS-SSIM minimum (structure similarity). / MS-SSIM 下限（结构相似度）。</summary>
        [Range(0, 1)] public float msSsim;

        /// <summary>Mean ΔE00 (CIEDE2000) maximum. / ΔE00（CIEDE2000）均值上限。</summary>
        [Min(0)] public float deltaE00Mean;

        /// <summary>Cutout alpha contour IoU minimum. / Cutout alpha 轮廓 IoU 下限。</summary>
        [Range(0, 1)] public float cutoutIou;

        /// <summary>Blend alpha linear RMSE maximum. / Blend alpha 线性 RMSE 上限。</summary>
        [Min(0)] public float blendAlphaRmse;

        /// <summary>Normal map mean angle error maximum (degrees). / 法线贴图角度误差均值上限（度）。</summary>
        [Min(0)] public float normalAngleMean;

        /// <summary>Normal map p95 angle error maximum (degrees). / 法线贴图角度误差 p95 上限（度）。</summary>
        [Min(0)] public float normalAngleP95;

        /// <summary>Grayscale linear RMSE maximum (per used channel, worst wins). / 灰度线性 RMSE 上限（逐使用通道取最差）。</summary>
        [Min(0)] public float grayscaleRmse;

        /// <summary>Create thresholds with all values set to 1 (near lossless). / 生成全 1（近无损）阈值。</summary>
        public static AtoQualityThresholds NearLossless() => new AtoQualityThresholds
        {
            msSsim = 1f,
            deltaE00Mean = 1f,
            cutoutIou = 1f,
            blendAlphaRmse = 1f,
            normalAngleMean = 1f,
            normalAngleP95 = 1f,
            grayscaleRmse = 1f,
        };
    }

    /// <summary>
    /// Preset parameter table. / 预设参数表。
    /// Literature basis: SSIM/MS-SSIM (Wang et al. 2003); CIEDE2000 (Sharma et al. 2005);
    /// JND ΔE&lt;1; 3Dc/BC5 normal compression error studies; 8-bit alpha quantization JND. /
    /// 文献依据：SSIM/MS-SSIM（Wang et al. 2003）；CIEDE2000（Sharma et al. 2005）；JND ΔE&lt;1；
    /// 3Dc/BC5 法线压缩误差研究；8bit alpha 量化 JND。
    /// </summary>
    public static class AtoQualityPresets
    {
        public static AtoQualityThresholds Get(AtoQualityPreset preset) => preset switch
        {
            AtoQualityPreset.Ultra => new AtoQualityThresholds
            {
                msSsim = 0.9995f, deltaE00Mean = 0.5f, cutoutIou = 0.9999f, blendAlphaRmse = 0.002f,
                normalAngleMean = 0.15f, normalAngleP95 = 0.5f, grayscaleRmse = 0.002f,
            },
            AtoQualityPreset.High => new AtoQualityThresholds
            {
                msSsim = 0.999f, deltaE00Mean = 1.0f, cutoutIou = 0.9995f, blendAlphaRmse = 0.005f,
                normalAngleMean = 0.25f, normalAngleP95 = 1.0f, grayscaleRmse = 0.005f,
            },
            AtoQualityPreset.Medium => new AtoQualityThresholds
            {
                msSsim = 0.997f, deltaE00Mean = 2.0f, cutoutIou = 0.998f, blendAlphaRmse = 0.01f,
                normalAngleMean = 0.5f, normalAngleP95 = 2.0f, grayscaleRmse = 0.01f,
            },
            AtoQualityPreset.Low => new AtoQualityThresholds
            {
                msSsim = 0.995f, deltaE00Mean = 3.0f, cutoutIou = 0.995f, blendAlphaRmse = 0.02f,
                normalAngleMean = 1.0f, normalAngleP95 = 4.0f, grayscaleRmse = 0.02f,
            },
            _ => AtoQualityThresholds.NearLossless(),
        };

        /// <summary>
        /// Whether this preset means "target quality = 1" (skip island scaling, copy as-is). /
        /// 该挡位是否意味着“目标质量 = 1”（跳过岛缩放，原样拷贝）。
        /// Lossless only happens when the user explicitly keeps the Custom preset's all-1 defaults
        /// (or raises them to 1). Standard presets always scale islands. /
        /// 仅当用户将自定义挡位参数保持（或提高为）全 1 默认值时视为近无损；标准挡位始终缩放岛。
        /// </summary>
        public static bool IsNearLossless(AtoQualityPreset preset, AtoQualityThresholds custom)
        {
            if (preset == AtoQualityPreset.Custom)
            {
                var one = AtoQualityThresholds.NearLossless();
                return custom.msSsim >= one.msSsim - 1e-6f
                       && custom.deltaE00Mean >= one.deltaE00Mean - 1e-6f
                       && custom.cutoutIou >= one.cutoutIou - 1e-6f
                       && custom.blendAlphaRmse >= one.blendAlphaRmse - 1e-6f
                       && custom.normalAngleMean >= one.normalAngleMean - 1e-6f
                       && custom.normalAngleP95 >= one.normalAngleP95 - 1e-6f
                       && custom.grayscaleRmse >= one.grayscaleRmse - 1e-6f;
            }
            return false;
        }
    }

    /// <summary>
    /// Pixel density presets (px/m) for the density band. / 像素密度挡位（px/m）。
    /// </summary>
    public enum AtoDensityPreset
    {
        Px512 = 512,
        Px1024 = 1024,
        Px2048 = 2048,
        Px4096 = 4096,
        Px8192 = 8192,
    }

    /// <summary>
    /// Minimum atlas padding options (pixels). / 图集最小 padding 选项（像素）。
    /// </summary>
    public enum AtoPaddingOption
    {
        Px4 = 4,
        Px8 = 8,
        Px16 = 16,
        Px32 = 32,
        Px64 = 64,
    }

    /// <summary>
    /// Texture compression format options (safe enumeration). / 贴图压缩格式选项（安全枚举）。
    /// Values are filtered per category and platform at build time; unsafe combinations fall back. /
    /// 构建时按分类与平台过滤；不安全组合走 fallback。
    /// </summary>
    public enum AtoCompressionFormat
    {
        /// <summary>Platform default. / 平台默认。</summary>
        Auto = 0,
        // ASTC family / ASTC 家族
        ASTC_4x4,
        ASTC_5x5,
        ASTC_6x6,
        ASTC_8x8,
        ASTC_10x10,
        ASTC_12x12,
        // BC family (PC) / BC 家族（PC）
        BC1,
        BC3,
        BC4,
        BC5,
        BC7,
        // ETC family / ETC 家族
        ETC_RGB4,
        ETC2_RGB4,
        ETC2_RGBA8,
        // PVRTC family / PVRTC 家族
        PVRTC_RGB2,
        PVRTC_RGB4,
        PVRTC_RGBA2,
        PVRTC_RGBA4,
        // Uncompressed / 不压缩
        RGB24,
        RGBA32,
        R8,
        RG16,
        RGBAHalf,
        RGBAFloat,
    }

    /// <summary>
    /// Texture categories for compression settings. / 压缩设置的贴图分类。
    /// </summary>
    public enum AtoTextureCategory
    {
        /// <summary>Opaque color textures. / 不透明贴图。</summary>
        Opaque = 0,
        /// <summary>Transparent color textures. / 透明贴图。</summary>
        Transparent = 1,
        /// <summary>Normal maps. / 法线贴图。</summary>
        NormalMap = 2,
        /// <summary>Grayscale/mask textures. / 灰度/蒙版贴图。</summary>
        Grayscale = 3,
    }

    /// <summary>
    /// Supported target platforms for per-platform overrides. / 平台 override 支持的平台。
    /// </summary>
    public enum AtoTargetPlatform
    {
        PC = 0,
        Android = 1,
        IOS = 2,
    }
}
