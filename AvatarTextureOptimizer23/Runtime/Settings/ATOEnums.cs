using System;
using UnityEngine;

namespace FOSA.AvatarTextureOptimizer
{
    /// <summary>
    /// Quality preset. Switching a non-Custom preset overwrites the detailed parameters.
    /// 质量挡位。切换非 Custom 挡位会覆盖详细参数。
    /// </summary>
    public enum ATOQualityPreset
    {
        Lossless = 0,
        Ultra = 1,
        High = 2,
        Medium = 3,
        Low = 4,
        Custom = 5
    }

    /// <summary>
    /// Build / override platform. Mirrors Unity's platform override split for VRChat.
    /// 构建/覆盖平台。对齐 Unity 平台覆盖在 VRChat 上的三分法。
    /// </summary>
    public enum ATOPlatform
    {
        Generic = 0,
        PC = 1,
        Android = 2,
        iOS = 3
    }

    /// <summary>
    /// Texture semantic category used for format / mip / quality routing.
    /// 贴图语义分类，用于格式、Mip、质量路由。
    /// </summary>
    public enum ATOTextureCategory
    {
        OpaqueAlbedo = 0,
        TransparentAlbedo = 1,
        Normal = 2,
        Gray = 3,
        Unknown = 4
    }

    /// <summary>
    /// Safe compression choices. Invalid combinations are rejected at build time.
    /// 安全压缩枚举。非法组合会在构建时被拒绝并 fallback。
    /// </summary>
    public enum ATOCompressionChoice
    {
        Auto = 0,
        Uncompressed = 1,
        DXT1_BC1 = 2,
        DXT5_BC3 = 3,
        BC4 = 4,
        BC5 = 5,
        BC7 = 6,
        ETC2_RGB = 7,
        ETC2_RGBA = 8,
        ASTC_4x4 = 9,
        ASTC_6x6 = 10,
        ASTC_8x8 = 11,
        PVRTC_RGB4 = 12,
        PVRTC_RGBA4 = 13,
        R8 = 14,
        Alpha8 = 15
    }

    /// <summary>
    /// Minimum atlas padding in pixels.
    /// 图集最小 padding（像素）。
    /// </summary>
    public enum ATOMinPadding
    {
        Px4 = 4,
        Px8 = 8,
        Px16 = 16,
        Px32 = 32,
        Px64 = 64
    }

    /// <summary>
    /// Pixel-density preset in px/m. Used as a clamp, not as a hard resize target.
    /// 像素密度挡位（px/m）。用作钳制，不是硬性缩放目标。
    /// </summary>
    public enum ATOPixelDensityPreset
    {
        Px512 = 512,
        Px1024 = 1024,
        Px2048 = 2048,
        Px4096 = 4096,
        Px8192 = 8192
    }

    /// <summary>
    /// UI language. Auto follows NDMF LanguagePrefs.
    /// 界面语言。Auto 跟随 NDMF LanguagePrefs。
    /// </summary>
    public enum ATOLanguageMode
    {
        Auto = 0,
        English = 1,
        SimplifiedChinese = 2
    }

    /// <summary>
    /// Alpha evaluation mode inferred from the material (most stringent wins).
    /// 由材质推断的 alpha 评估模式（取最严苛）。
    /// </summary>
    public enum ATOAlphaMode
    {
        Opaque = 0,
        Cutout = 1,
        Blend = 2
    }
}
