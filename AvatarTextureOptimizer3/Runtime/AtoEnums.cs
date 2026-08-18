// English: Shared enumerations for Avatar Texture Optimizer.
// 中文：Avatar Texture Optimizer 的共享枚举。
using System;
using UnityEngine;

namespace net.fosa.ato
{
    /// <summary>Quality preset / 质量挡位</summary>
    public enum AtoQualityPreset
    {
        Lossless = 0,
        Ultra = 1,
        High = 2,
        Medium = 3,
        Low = 4,
        Custom = 5
    }

    /// <summary>Build platform override / 平台覆盖</summary>
    public enum AtoPlatform
    {
        Generic = 0,
        PC = 1,
        Android = 2,
        iOS = 3
    }

    /// <summary>Texture semantic class / 贴图语义分类</summary>
    public enum AtoTextureClass
    {
        OpaqueAlbedo = 0,
        TransparentAlbedo = 1,
        Normal = 2,
        Gray = 3,
        Mask = 4,
        Unknown = 5
    }

    /// <summary>Material alpha mode used for quality / 质量评估用透明模式</summary>
    public enum AtoAlphaMode
    {
        Opaque = 0,
        Cutout = 1,
        Blend = 2
    }

    /// <summary>Minimum island padding / 最小岛间距</summary>
    public enum AtoMinPadding
    {
        Px4 = 4,
        Px8 = 8,
        Px16 = 16,
        Px32 = 32,
        Px64 = 64
    }

    /// <summary>Pixel density presets (px/m) / 像素密度挡位</summary>
    public enum AtoPixelDensity
    {
        D512 = 512,
        D1024 = 1024,
        D2048 = 2048,
        D4096 = 4096,
        D8192 = 8192
    }

    /// <summary>Safe compression choices / 安全压缩枚举</summary>
    public enum AtoSafeCompression
    {
        Auto = 0,
        Uncompressed = 1,
        HighQuality = 2, // BC7 / ASTC 8x8 / etc depending on platform
        Balanced = 3,    // DXT5 / ASTC 6x6
        Small = 4        // DXT1 / ASTC 8x8 or 10x10
    }

    /// <summary>Language override / 语言覆盖</summary>
    public enum AtoLanguageMode
    {
        Auto = 0,
        English = 1,
        SimplifiedChinese = 2
    }
}
