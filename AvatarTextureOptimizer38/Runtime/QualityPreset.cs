using System;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer
{
    /// <summary>
    /// Quality preset. / 质量挡位。
    /// NearLossless skips UV scaling (target quality = 1). Custom defaults to 1 and is never overwritten by other presets.
    /// 近无损跳过 UV 缩放（目标质量=1）。自定义默认全 1，不会被其它挡位覆盖。
    /// </summary>
    public enum QualityPreset
    {
        NearLossless = 0,
        Ultra = 1,
        High = 2,
        Medium = 3,
        Low = 4,
        Custom = 5
    }

    /// <summary>
    /// Pixel-density cap steps (px/m). / 像素密度挡位（像素/米）。
    /// </summary>
    public enum PixelDensityStep
    {
        Px512 = 512,
        Px1024 = 1024,
        Px2048 = 2048,
        Px4096 = 4096,
        Px8192 = 8192
    }

    /// <summary>
    /// Build platform for override settings. / 用于覆盖设置的构建平台。
    /// </summary>
    public enum AtoBuildPlatform
    {
        PC = 0,
        Android = 1,
        iOS = 2
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
    /// Min padding between islands (px). / 岛间最小 padding（像素）。
    /// </summary>
    public enum AtlasPaddingPreset
    {
        Px4 = 4,
        Px8 = 8,
        Px16 = 16,
        Px32 = 32,
        Px64 = 64
    }

    /// <summary>
    /// Safe texture compression choices, filtered per platform and texture class.
    /// 按平台与贴图类别过滤的安全压缩枚举。
    /// </summary>
    public enum AtoCompressionFormat
    {
        Auto = 0,
        Uncompressed = 1,
        DXT1_BC1 = 2,
        DXT5_BC3 = 3,
        BC4 = 4,
        BC5 = 5,
        BC7 = 6,
        ETC2_RGB = 7,
        ETC2_RGBA8 = 8,
        EAC_R = 9,
        ASTC_4x4 = 10,
        ASTC_6x6 = 11,
        ASTC_8x8 = 12,
        PVRTC_RGB4 = 13,
        PVRTC_RGBA4 = 14
    }
}
