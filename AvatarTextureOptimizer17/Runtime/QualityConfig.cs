// ============================================================================
// AvatarTextureOptimizer (net.fosa.avatar-texture-optimizer)
// QualityConfig.cs — 质量挡位与参数定义 / Quality presets and parameter definitions
//
// 设计说明 (Coder1/Coder2 共识):
//  - 预设挡位决定全部质量参数的具体值；切换挡位时参数随之变化，避免用户遗漏。
//  - "Custom(自定义)" 挡位参数由用户自行修改，默认全部为 1 / 0 = 近无损(跳过缩放)。
//  - 数值依据: SSIM ≥0.95 人类几乎不可分辨、≥0.98 近无损(Wang et al. 2004);
//    CIEDE2000 JND ≈ 1.0, 工业常用容差 2.3; alpha 轮廓 IoU ≥0.97 视觉一致;
//    法线角度误差 2°~5° 为常见烘焙容差; 灰度 RMSE 参照 8bit 量化噪声 ~1/255≈0.004。
// ============================================================================
using System;

namespace net.fosa.avatar_texture_optimizer
{
    /// <summary>
    /// 贴图类型分类（用于压缩格式/图集/导入设置分组）/
    /// Texture category used to group compression formats, atlases and import settings.
    /// </summary>
    public enum TextureCategory
    {
        /// <summary>不透明主色 / Opaque color (no alpha channel)</summary>
        Opaque,
        /// <summary>透明主色(含 alpha) / Transparent color (has alpha)</summary>
        Transparent,
        /// <summary>法线贴图 / Normal map (tangent-space, never retargeted)</summary>
        Normal,
        /// <summary>灰度/蒙版类贴图 / Grayscale / mask-like textures</summary>
        Grayscale,
    }

    /// <summary>
    /// 平台 / Supported platforms for per-platform override.
    /// </summary>
    public enum ATOPlatform
    {
        PC = 0,
        Android = 1,
        iOS = 2,
    }

    /// <summary>
    /// 目标质量挡位 / Quality presets.
    /// </summary>
    public enum QualityPreset
    {
        /// <summary>均衡(默认) / Balanced (default)</summary>
        Balanced = 0,
        /// <summary>高质量(更保守的缩放) / High quality (more conservative scaling)</summary>
        Quality = 1,
        /// <summary>性能(更激进的缩放) / Performance (more aggressive scaling)</summary>
        Performance = 2,
        /// <summary>近无损 / Near lossless</summary>
        NearLossless = 3,
        /// <summary>自定义(默认全 1 = 近无损) / Custom (defaults all to near-lossless)</summary>
        Custom = 4,
    }

    /// <summary>
    /// 压缩格式安全枚举（构建时按平台/类别过滤 + 安全兜底）/
    /// Safe compression format enum (filtered by platform/category at build time with safe fallback).
    /// </summary>
    public enum ATOCompressionFormat
    {
        /// <summary>自动(平台默认) / Automatic (platform default)</summary>
        Automatic = 0,
        /// <summary>BC7 (PC, 高质量 RGBA) / BC7 (PC, high quality RGBA)</summary>
        BC7 = 1,
        /// <summary>BC5 (PC, 双通道, 适合法线) / BC5 (PC, two channel, good for normals)</summary>
        BC5 = 2,
        /// <summary>BC4 (PC, 单通道灰度) / BC4 (PC, single channel grayscale)</summary>
        BC4 = 3,
        /// <summary>DXT5 (PC, RGBA) / DXT5 (PC, RGBA)</summary>
        DXT5 = 4,
        /// <summary>DXT1 (PC, RGB 无 alpha) / DXT1 (PC, RGB without alpha)</summary>
        DXT1 = 5,
        /// <summary>ASTC 4x4 (移动端/通用, 高质量) / ASTC 4x4 (mobile/universal, high quality)</summary>
        ASTC4x4 = 6,
        /// <summary>ASTC 6x6 (移动端, 均衡) / ASTC 6x6 (mobile, balanced)</summary>
        ASTC6x6 = 7,
        /// <summary>ASTC 8x8 (移动端, 更省) / ASTC 8x8 (mobile, smaller)</summary>
        ASTC8x8 = 8,
        /// <summary>ETC2 RGBA8 (移动端/通用) / ETC2 RGBA8 (mobile/universal)</summary>
        ETC2RGBA8 = 9,
        /// <summary>ETC2 RGB4 (移动端, 无 alpha) / ETC2 RGB4 (mobile, no alpha)</summary>
        ETC2RGB4 = 10,
        /// <summary>RGBA32 未压缩 / RGBA32 uncompressed</summary>
        RGBA32 = 11,
        /// <summary>RGB24 未压缩 / RGB24 uncompressed</summary>
        RGB24 = 12,
    }

    /// <summary>
    /// i18n 语言选项 / Language option (Auto 跟随 ndmf 设置).
    /// </summary>
    public enum ATOLanguage
    {
        /// <summary>自动(跟随 ndmf 当前语言) / Auto (follow NDMF current language)</summary>
        Auto = 0,
        /// <summary>英语 / English</summary>
        English = 1,
        /// <summary>简体中文 / Simplified Chinese</summary>
        ChineseSimplified = 2,
    }

    /// <summary>
    /// 质量目标参数（单个贴图类型组使用；UV 组内取木桶效应最大值）/
    /// Quality target parameters (applied per texture-type group; UV groups take the strictest/bucket value).
    ///
    /// 约定: 对于"越大越好"指标(SSIM/IoU)为最低达标值；对于"越小越好"指标(ΔE/RMSE/角度)为最高允许值。
    /// Convention: for larger-is-better metrics (SSIM/IoU) this is the minimum pass bar;
    /// for smaller-is-better metrics (ΔE/RMSE/angle) this is the maximum allowed.
    /// </summary>
    [Serializable]
    public struct QualityTargets
    {
        /// <summary>目标 MS-SSIM（<176px 岛回退单尺度 SSIM 同值）/ Target MS-SSIM (islands &lt;176px fall back to single-scale SSIM with the same value)</summary>
        public float msSsim;
        /// <summary>最大 CIEDE2000 ΔE / Maximum CIEDE2000 ΔE</summary>
        public float maxDeltaE;
        /// <summary>Cutout 裁剪后轮廓最小 IoU / Minimum silhouette IoU after clipping for Cutout materials</summary>
        public float minAlphaCutoutIoU;
        /// <summary>Blend 透明通道最大线性 RMSE / Maximum linear alpha RMSE for Blend materials</summary>
        public float maxAlphaBlendRmse;
        /// <summary>法线贴图最大角度误差(度, p95) / Maximum normal map angle error in degrees (p95)</summary>
        public float maxNormalAngleDeg;
        /// <summary>灰度贴图最大线性 RMSE / Maximum linear RMSE for grayscale textures</summary>
        public float maxGrayRmse;

        public static QualityTargets NearLossless()
        {
            return new QualityTargets
            {
                msSsim = 0.999f,
                maxDeltaE = 0.3f,
                minAlphaCutoutIoU = 0.999f,
                maxAlphaBlendRmse = 0.002f,
                maxNormalAngleDeg = 0.5f,
                maxGrayRmse = 0.001f,
            };
        }

        public static QualityTargets Balanced()
        {
            return new QualityTargets
            {
                msSsim = 0.93f,
                maxDeltaE = 2.3f,      // CIEDE2000 工业常用容差 / common industrial tolerance
                minAlphaCutoutIoU = 0.97f,
                maxAlphaBlendRmse = 0.02f,
                maxNormalAngleDeg = 5f,
                maxGrayRmse = 0.01f,
            };
        }

        public static QualityTargets HighQuality()
        {
            return new QualityTargets
            {
                msSsim = 0.97f,
                maxDeltaE = 1.2f,
                minAlphaCutoutIoU = 0.99f,
                maxAlphaBlendRmse = 0.008f,
                maxNormalAngleDeg = 2f,
                maxGrayRmse = 0.005f,
            };
        }

        public static QualityTargets Performance()
        {
            return new QualityTargets
            {
                msSsim = 0.85f,
                maxDeltaE = 5.0f,
                minAlphaCutoutIoU = 0.90f,
                maxAlphaBlendRmse = 0.05f,
                maxNormalAngleDeg = 10f,
                maxGrayRmse = 0.03f,
            };
        }

        /// <summary>
        /// 是否"近无损"(目标质量==1)→ 直接跳过该贴图类型岛的 UV 缩放 /
        /// Whether this is near-lossless (target quality == 1) → skip UV scaling for this texture type.
        /// </summary>
        public bool IsNearLossless => msSsim >= 0.9995f && maxDeltaE <= 0.5f;
    }

    /// <summary>
    /// 每分类导入/压缩设置 / Per-category import & compression settings.
    /// </summary>
    [Serializable]
    public class CategoryImportSettings
    {
        /// <summary>压缩格式 / Compression format (safe enum, filtered per platform)</summary>
        public ATOCompressionFormat format = ATOCompressionFormat.Automatic;

        /// <summary>
        /// 是否生成 Mipmap（与 MipStreaming 绑定, 一个开关同时控制二者；
        /// 开 Mipmap 强制开 MipStreaming, 关 Mipmap 强制关 MipStreaming）/
        /// Generate mipmaps (bound to MipStreaming: one toggle controls both).
        /// </summary>
        public bool mipmaps = true;

        /// <summary>平台最大纹理尺寸限制(0=不限制) / Max texture size for this platform (0 = no limit)</summary>
        public int maxSize = 0;
    }

    /// <summary>
    /// 平台覆盖配置 / Per-platform override config.
    /// </summary>
    [Serializable]
    public class PlatformOverrideConfig
    {
        /// <summary>该平台图集最大边长(移动端默认 4096, PC 默认 8192) / Max atlas edge for this platform</summary>
        public int maxAtlasSize = 0;

        /// <summary>该平台是否允许实验性 NPOT / Whether NPOT is allowed on this platform</summary>
        public bool allowNpot = false;

        /// <summary>每分类格式覆盖(null 表示用通用设置) / Per-category format override (null = use global)</summary>
        public CategoryImportSettings opaque = null;
        public CategoryImportSettings transparent = null;
        public CategoryImportSettings normal = null;
        public CategoryImportSettings grayscale = null;
    }
}
