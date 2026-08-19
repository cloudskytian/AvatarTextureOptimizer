// English: Shared enumerations for Avatar Texture Optimizer settings.
// 中文：Avatar Texture Optimizer 设置用的共享枚举。
using System;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer
{
    /// <summary>
    /// Quality preset. Non-Custom presets overwrite numeric thresholds.
    /// 质量挡位。非 Custom 会覆盖具体阈值。
    /// </summary>
    public enum ATOQualityPreset
    {
        NearLossless = 0,
        Ultra = 1,
        High = 2,
        Medium = 3,
        Low = 4,
        Custom = 5
    }

    /// <summary>
    /// Target build platform, matching Unity / VRChat platform overrides.
    /// 目标构建平台，对齐 Unity / VRChat 的 platform override。
    /// </summary>
    public enum ATOBuildPlatform
    {
        Auto = 0,
        PC = 1,
        Android = 2,
        iOS = 3
    }

    /// <summary>
    /// Pixel-density stop in px/m.
    /// 像素密度挡位，单位 px/m。
    /// </summary>
    public enum ATOPixelDensityStop
    {
        Px512 = 512,
        Px1024 = 1024,
        Px2048 = 2048,
        Px4096 = 4096,
        Px8192 = 8192
    }

    /// <summary>
    /// Minimum island padding in pixels.
    /// 岛间最小 padding（像素）。
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
    /// Texture semantic used for quality metric selection and compression.
    /// 贴图语义，决定质量算法与压缩格式。
    /// </summary>
    public enum ATOTextureSemantic
    {
        Unknown = 0,
        AlbedoOpaque = 1,
        AlbedoTransparent = 2,
        Normal = 3,
        Gray = 4,
        Mask = 5
    }

    /// <summary>
    /// Material transparency mode that affects alpha quality.
    /// 材质透明模式，影响 alpha 质量评估。
    /// </summary>
    public enum ATOAlphaMode
    {
        Opaque = 0,
        Cutout = 1,
        Blend = 2
    }

    /// <summary>
    /// Safe compression format enumerations. Unsafe combinations are stripped at bake time.
    /// 安全压缩格式枚举。不安全组合会在烘焙时剔除。
    /// </summary>
    public enum ATOSafeFormat
    {
        Auto = 0,
        RGBA32 = 1,
        RGB24 = 2,
        DXT1 = 3,
        DXT5 = 4,
        BC4 = 5,
        BC5 = 6,
        BC7 = 7,
        ETC2_RGB4 = 8,
        ETC2_RGBA8 = 9,
        ASTC_4x4 = 10,
        ASTC_5x5 = 11,
        ASTC_6x6 = 12,
        ASTC_8x8 = 13,
        R8 = 14,
        Alpha8 = 15
    }

    /// <summary>
    /// i18n language mode. Auto follows NDMF LanguagePrefs.
    /// 语言模式。Auto 跟随 NDMF 当前语言。
    /// </summary>
    public enum ATOLanguageMode
    {
        Auto = 0,
        Manual = 1
    }

    /// <summary>
    /// Companion map kinds that define a texture type group.
    /// 决定贴图类型组的伴侣贴图种类。
    /// </summary>
    [Flags]
    public enum ATOCompanionKind
    {
        None = 0,
        Normal = 1 << 0,
        Mask = 1 << 1,
        MetallicSmoothness = 1 << 2,
        Emission = 1 << 3
    }
}
