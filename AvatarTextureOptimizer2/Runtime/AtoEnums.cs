using System;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer
{
    /// <summary>
    /// Quality preset. / 质量挡位。
    /// Defaults are based on SSIM / CIEDE2000 JND literature (ΔE≈2.3 just-noticeable).
    /// 默认值参考 SSIM 与 CIEDE2000 恰可辨差（ΔE≈2.3）研究。
    /// </summary>
    public enum AtoQualityPreset
    {
        Custom = 0,
        NearLossless = 1,
        Ultra = 2,
        High = 3,
        Medium = 4,
        Low = 5
    }

    public enum AtoPlatform
    {
        Generic = 0,
        PC = 1,
        Android = 2,
        iOS = 3
    }

    public enum AtoMinPadding
    {
        Px4 = 4,
        Px8 = 8,
        Px16 = 16,
        Px32 = 32,
        Px64 = 64
    }

    public enum AtoPixelDensity
    {
        Px512 = 512,
        Px1024 = 1024,
        Px2048 = 2048,
        Px4096 = 4096,
        Px8192 = 8192
    }

    public enum AtoOpaqueFormat
    {
        Auto = 0,
        DXT1 = 1,
        DXT5 = 2,
        BC7 = 3,
        ASTC_6x6 = 4,
        ASTC_4x4 = 5,
        ETC2_RGB = 6,
        RGBA32 = 7
    }

    public enum AtoTransparentFormat
    {
        Auto = 0,
        DXT5 = 1,
        BC7 = 2,
        ASTC_6x6 = 3,
        ASTC_4x4 = 4,
        ETC2_RGBA8 = 5,
        RGBA32 = 6
    }

    public enum AtoNormalFormat
    {
        Auto = 0,
        DXT5nm = 1,
        BC5 = 2,
        ASTC_4x4 = 3,
        RGBA32 = 4
    }

    public enum AtoGrayFormat
    {
        Auto = 0,
        BC4 = 1,
        DXT1 = 2,
        ASTC_6x6 = 3,
        R8 = 4,
        RGBA32 = 5
    }

    public enum AtoLanguageMode
    {
        Auto = 0,
        English = 1,
        ChineseSimplified = 2
    }

    /// <summary>
    /// Texture semantic role used for grouping / metrics. / 贴图语义角色，用于分组与质量评估。
    /// </summary>
    public enum AtoTextureRole
    {
        Albedo = 0,
        Normal = 1,
        Mask = 2,
        Gray = 3,
        Unknown = 4
    }

    public enum AtoBlendMode
    {
        Opaque = 0,
        Cutout = 1,
        Blend = 2
    }
}
