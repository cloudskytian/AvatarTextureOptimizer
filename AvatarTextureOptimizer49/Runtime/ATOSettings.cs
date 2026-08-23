using System;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer
{
    /// <summary>
    /// Quality metric parameters. / 质量度量参数。
    /// </summary>
    [Serializable]
    public class QualityParams
    {
        // All thresholds are "worst allowed"; island passes only if EVERY metric is within its threshold.
        // 所有阈值均为“允许的最差值”；所有指标全部达标才算通过。

        /// <summary>MS-SSIM threshold (1 = lossless). Short side &lt;176px falls back to single-scale SSIM; &lt;11px skips this metric. / MS-SSIM 阈值，短边小于176px回退单尺度SSIM，小于11px忽略。</summary>
        [Range(0.5f, 1f)] public float msSsim = 0.99f;

        /// <summary>Mean CIEDE2000 color difference threshold. / 平均 CIEDE2000 色差阈值。</summary>
        [Range(0f, 10f)] public float deltaE2000Mean = 1.5f;

        /// <summary>Alpha IoU threshold after cutoff clip (cutout mode). / Cutout 裁剪后轮廓 IoU 阈值。</summary>
        [Range(0.5f, 1f)] public float alphaCutoutIoU = 0.995f;

        /// <summary>Alpha linear RMSE threshold (blend mode). / Blend 模式 alpha 线性 RMSE 阈值。</summary>
        [Range(0f, 0.25f)] public float alphaBlendRmse = 0.015f;

        /// <summary>Normal map mean angular error (degrees). / 法线平均角度误差（度）。</summary>
        [Range(0f, 15f)] public float normalAngleMeanDeg = 1.0f;

        /// <summary>Normal map p95 angular error (degrees). / 法线 p95 角度误差（度）。</summary>
        [Range(0f, 30f)] public float normalAngleP95Deg = 2.5f;

        /// <summary>Grayscale/linear RMSE threshold, worst used channel. / 灰度线性 RMSE 阈值，取使用通道最差值。</summary>
        [Range(0f, 0.25f)] public float grayRmse = 0.012f;

        /// <summary>True when every metric is at its strictest (near-lossless copy mode). / 全部阈值最严格时视为近无损，跳过缩放原样拷贝。</summary>
        public bool IsNearLossless =>
            msSsim >= 0.9999f && deltaE2000Mean <= 1.0f && alphaCutoutIoU >= 0.9999f
            && alphaBlendRmse <= 0.001f && normalAngleMeanDeg <= 1.0f && normalAngleP95Deg <= 1.0f
            && grayRmse <= 0.001f;

        public QualityParams Clone() => (QualityParams)MemberwiseClone();
    }

    /// <summary>Quality presets. / 质量挡位。</summary>
    public enum QualityPreset
    {
        /// <summary>User-managed parameters (default all near-lossless). / 用户自定义（默认全部近无损）。</summary>
        Custom = 0,
        /// <summary>Near lossless: copy pixels untouched. / 近无损：原样拷贝。</summary>
        NearLossless = 1,
        Ultra = 2,
        /// <summary>Default preset. / 默认挡位。</summary>
        High = 3,
        Balanced = 4,
        Aggressive = 5,
    }

    /// <summary>Texture categories used for compression / mipmap options. / 用于压缩与 Mipmap 选项的贴图分类。</summary>
    public enum TextureCategory
    {
        Opaque = 0,
        Transparent = 1,
        Normal = 2,
        Grayscale = 3,
    }

    /// <summary>
    /// Safe, platform-aware format choices exposed to users. Not every value is valid for every
    /// platform/category; the build pipeline filters and falls back with a console warning.
    /// / 面向用户的安全格式枚举；非法组合在构建时被过滤并回退，同时在控制台警告。
    /// </summary>
    public enum AtoFormat
    {
        Auto = 0,

        // PC (DXT/BC family) / PC 平台
        BC7 = 10,
        DXT1 = 11,
        DXT5 = 12,
        BC5 = 13,   // normal maps only / 仅法线
        BC4 = 14,   // single channel grayscale / 单通道灰度
        CrunchDXT1 = 15,
        CrunchDXT5 = 16,

        // Mobile (ASTC/ETC2) / 移动平台
        ASTC_4x4 = 30,
        ASTC_5x5 = 31,
        ASTC_6x6 = 32,
        ASTC_8x8 = 33,
        ASTC_10x10 = 34,
        ASTC_12x12 = 35,
        ETC2_RGBA8 = 36,
        ETC2_RGB = 37,

        Uncompressed = 90,
    }

    /// <summary>Target platform for overrides. / 平台覆盖目标。</summary>
    public enum AtoPlatform
    {
        PC = 0,
        Android = 1,
        iOS = 2,
    }

    /// <summary>
    /// All optimization settings. Used as the common setting block and as per-platform overrides
    /// (a platform override, when enabled, fully replaces the common block).
    /// / 全部优化参数。通用块与平台覆盖共用此类型；勾选平台覆盖后整体替换通用参数。
    /// </summary>
    [Serializable]
    public class AtoSettings
    {
        // ---- Quality / 质量 ----
        [Tooltip("Quality preset / 质量挡位")] public QualityPreset preset = QualityPreset.High;
        public QualityParams quality = new QualityParams();

        [Tooltip("Minimum pixel density (px per meter on the avatar) / 最小像素密度（每米像素）")]
        public int minPixelsPerMeter = 2048;
        [Tooltip("Maximum pixel density (px per meter on the avatar) / 最大像素密度（每米像素）")]
        public int maxPixelsPerMeter = 4096;

        // ---- Atlas / 图集 ----
        [Tooltip("Generate atlases. Unchecked = only whole-texture scaling and other optimizations. / 生成图集。不勾选则仅整图缩放与其他优化。")]
        public bool generateAtlas = true;
        [Tooltip("EXPERIMENTAL: non-power-of-two atlases in 64px steps. / 实验：以64px步进的非2次幂图集。")]
        public bool experimentalNpot = false;
        [Tooltip("Minimum padding between islands (px). / 岛间最小间距（像素）。")]
        public int minPadding = 4;
        [Tooltip("Maximum atlas side. / 图集最大边长。")]
        public int maxAtlasSize = 8192;

        // ---- Compression & mipmaps / 压缩与 Mipmap ----
        public AtoFormat opaqueFormat = AtoFormat.Auto;
        public AtoFormat transparentFormat = AtoFormat.Auto;
        public AtoFormat normalFormat = AtoFormat.Auto;
        public AtoFormat grayscaleFormat = AtoFormat.Auto;

        /// <summary>Mip + MipStreaming are one bound switch (VRChat requires streaming when mips on). / Mip 与 MipStreaming 绑定为同一开关。</summary>
        public bool opaqueMip = true;
        public bool transparentMip = true;
        public bool normalMip = true;
        public bool grayscaleMip = true;

        // ---- Dedup / 去重 ----
        [Tooltip("Deduplicate identical materials after optimization / 优化后对完全相同的材质去重")]
        public bool dedupMaterials = true;
        [Tooltip("Deduplicate identical textures/atlases after optimization / 优化后对完全相同的贴图与图集去重")]
        public bool dedupTextures = true;

        // ---- Debug / 调试 ----
        [Tooltip("Verbose [ATO] logging for advanced debugging / 详细 [ATO] 日志，供高级用户调试")]
        public bool verboseLog = false;

        public AtoSettings Clone()
        {
            var c = (AtoSettings)MemberwiseClone();
            c.quality = quality.Clone();
            return c;
        }

        /// <summary>Resolve the settings for a platform (override replaces the block when enabled). / 解析某平台生效参数（勾选覆盖后整体替换）。</summary>
        public static AtoSettings Resolve(AtoSettings common, AtoPlatformOverride pc, AtoPlatformOverride android,
            AtoPlatformOverride ios, AtoPlatform platform)
        {
            switch (platform)
            {
                case AtoPlatform.PC: return pc != null && pc.enabled ? pc.settings : common;
                case AtoPlatform.Android: return android != null && android.enabled ? android.settings : common;
                case AtoPlatform.iOS: return ios != null && ios.enabled ? ios.settings : common;
                default: return common;
            }
        }
    }

    /// <summary>Per-platform override block. / 平台覆盖块。</summary>
    [Serializable]
    public class AtoPlatformOverride
    {
        public bool enabled = false;
        public AtoSettings settings = DefaultOverride();

        static AtoSettings DefaultOverride()
        {
            // Mobile default: 4096 atlas cap. / 移动端默认图集上限 4096。
            var s = new AtoSettings();
            s.maxAtlasSize = 4096;
            return s;
        }
    }

    /// <summary>Preset parameter table, based on published research (see README). / 基于公开研究的挡位参数表（依据见 README）。</summary>
    public static class AtoPresets
    {
        public static QualityParams For(QualityPreset preset)
        {
            switch (preset)
            {
                case QualityPreset.NearLossless:
                case QualityPreset.Custom:
                    return new QualityParams
                    {
                        // Custom default: all near-lossless (1). / 自定义默认全部为 1（近无损）。
                        msSsim = 1f, deltaE2000Mean = 1f, alphaCutoutIoU = 1f, alphaBlendRmse = 0.001f,
                        normalAngleMeanDeg = 1f, normalAngleP95Deg = 1f, grayRmse = 0.001f,
                    };
                case QualityPreset.Ultra:
                    return new QualityParams
                    {
                        msSsim = 0.995f, deltaE2000Mean = 1.0f, alphaCutoutIoU = 0.998f, alphaBlendRmse = 0.008f,
                        normalAngleMeanDeg = 0.75f, normalAngleP95Deg = 1.5f, grayRmse = 0.008f,
                    };
                case QualityPreset.High:
                    return new QualityParams
                    {
                        msSsim = 0.99f, deltaE2000Mean = 1.5f, alphaCutoutIoU = 0.995f, alphaBlendRmse = 0.015f,
                        normalAngleMeanDeg = 1.0f, normalAngleP95Deg = 2.5f, grayRmse = 0.012f,
                    };
                case QualityPreset.Balanced:
                    return new QualityParams
                    {
                        msSsim = 0.98f, deltaE2000Mean = 2.3f, alphaCutoutIoU = 0.99f, alphaBlendRmse = 0.025f,
                        normalAngleMeanDeg = 1.5f, normalAngleP95Deg = 3.5f, grayRmse = 0.02f,
                    };
                case QualityPreset.Aggressive:
                    return new QualityParams
                    {
                        msSsim = 0.95f, deltaE2000Mean = 3.5f, alphaCutoutIoU = 0.98f, alphaBlendRmse = 0.04f,
                        normalAngleMeanDeg = 2.5f, normalAngleP95Deg = 5.0f, grayRmse = 0.035f,
                    };
                default:
                    return new QualityParams();
            }
        }
    }
}
