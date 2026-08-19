using System;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer
{
    /// <summary>
    /// Quality preset. / 质量挡位。
    /// Thresholds come from SSIM / CIEDE2000 literature and typical real-time art tolerances.
    /// 阈值参考 SSIM / CIEDE2000 文献与实时美术可接受误差。
    /// </summary>
    public enum AtoQualityPreset
    {
        /// <summary>Skip UV scaling entirely. / 完全跳过 UV 缩放。</summary>
        Lossless = 0,
        /// <summary>Near-transparent change (MS-SSIM ~0.99, ΔE ~1). / 几乎看不出差异。</summary>
        Ultra = 1,
        /// <summary>Default. Subtle at typical VRChat viewing distance. / 默认，常见观看距离下几乎不可察。</summary>
        High = 2,
        /// <summary>Visible only on close inspection. / 近看才明显。</summary>
        Medium = 3,
        /// <summary>Aggressive. / 激进。</summary>
        Low = 4,
        /// <summary>User-owned values, never overwritten by other presets. Defaults are all 1 (near-lossless). / 自定义，不被其他挡位覆盖，默认全 1。</summary>
        Custom = 5
    }

    /// <summary>
    /// Target build platform for format / atlas-size limits. / 影响格式与图集上限的目标平台。
    /// </summary>
    public enum AtoPlatform
    {
        Auto = 0,
        PC = 1,
        Android = 2,
        iOS = 3
    }

    /// <summary>
    /// UI language. Auto follows NDMF LanguagePrefs. / 界面语言。Auto 跟随 NDMF。
    /// </summary>
    public enum AtoLanguageMode
    {
        Auto = 0,
        English = 1,
        SimplifiedChinese = 2
    }

    /// <summary>
    /// Semantic type of a Texture2D after shader analysis. / 着色器分析后的贴图语义类型。
    /// </summary>
    public enum AtoTextureKind
    {
        Albedo = 0,
        Normal = 1,
        Mask = 2,
        Gray = 3,
        Unknown = 4
    }

    /// <summary>
    /// Alpha usage of a material slot. / 材质槽的透明用法。
    /// </summary>
    public enum AtoAlphaMode
    {
        Opaque = 0,
        Cutout = 1,
        Blend = 2
    }

    /// <summary>
    /// Safe compression choices exposed in the inspector. / Inspector 中的安全压缩枚举。
    /// Actual availability is filtered per platform / alpha / NPOT.
    /// 实际可选项会按平台 / 是否透明 / NPOT 再过滤。
    /// </summary>
    public enum AtoSafeFormat
    {
        Auto = 0,
        RGBA32 = 1,
        RGB24 = 2,
        RGBAHalf = 3,
        DXT1 = 4,
        DXT5 = 5,
        BC4 = 6,
        BC5 = 7,
        BC7 = 8,
        DXT1Crunched = 9,
        DXT5Crunched = 10,
        ETC2_RGB = 11,
        ETC2_RGBA8 = 12,
        ASTC_4x4 = 13,
        ASTC_5x5 = 14,
        ASTC_6x6 = 15,
        ASTC_8x8 = 16,
        PVRTC_RGB4 = 17,
        PVRTC_RGBA4 = 18
    }

    /// <summary>
    /// Minimum island padding presets. / 岛间最小 padding 挡位。
    /// </summary>
    public enum AtoMinPadding
    {
        Px4 = 4,
        Px8 = 8,
        Px16 = 16,
        Px32 = 32,
        Px64 = 64
    }

    /// <summary>
    /// Pixel-density clamp presets (px per world metre). / 像素密度挡位（像素/米）。
    /// </summary>
    public enum AtoPixelDensityPreset
    {
        Px512 = 512,
        Px1024 = 1024,
        Px2048 = 2048,
        Px4096 = 4096,
        Px8192 = 8192
    }

    /// <summary>
    /// Why a texture or UV was treated as whitelist. / 被视作白名单的原因。
    /// </summary>
    public enum AtoSkipReason
    {
        None = 0,
        UserWhitelist = 1,
        NotTexture2D = 2,
        HasSTTransform = 3,
        HasAnimatedST = 4,
        SpecialUse = 5,
        UvWrapOrCrossSeam = 6,
        UnsupportedShader = 7,
        RendererDisabled = 8,
        AtlasWouldNotFit = 9,
        NotMeshUvSampled = 10
    }
}
