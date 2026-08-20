using System;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer
{
    /// <summary>
    /// Target quality preset. / 目标质量挡位。
    /// Custom is never overwritten by other presets. / Custom 不会被其他挡位覆盖。
    /// </summary>
    public enum AtoQualityPreset
    {
        NearLossless = 0,
        Ultra = 1,
        High = 2,
        Medium = 3,
        Low = 4,
        Custom = 5
    }

    /// <summary>
    /// Build platform override, matching Unity / VRChat PC-Android-iOS split.
    /// 平台覆盖，对齐 Unity / VRChat 的 PC、Android、iOS。
    /// </summary>
    public enum AtoPlatform
    {
        Generic = 0,
        PC = 1,
        Android = 2,
        iOS = 3
    }

    /// <summary>
    /// Pixel-density stop in px/m. / 像素密度挡位，单位 px/m。
    /// </summary>
    public enum AtoPixelDensityStop
    {
        Px512 = 512,
        Px1024 = 1024,
        Px2048 = 2048,
        Px4096 = 4096,
        Px8192 = 8192
    }

    /// <summary>
    /// Minimum island padding. / 岛间最小 padding。
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
    /// Texture semantic used for format / quality routing.
    /// 贴图语义，用于压缩格式与质量算法分流。
    /// </summary>
    public enum AtoTextureKind
    {
        Unknown = 0,
        OpaqueAlbedo = 1,
        TransparentAlbedo = 2,
        Normal = 3,
        Gray = 4,
        Mask = 5
    }

    /// <summary>
    /// Alpha handling of a material reference. / 材质引用的透明模式。
    /// </summary>
    public enum AtoAlphaMode
    {
        Opaque = 0,
        Cutout = 1,
        Blend = 2
    }

    /// <summary>
    /// Safe compressed format enum shown to the user.
    /// 展示给用户的安全压缩格式枚举。
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
        ETC2_RGB = 9,
        ETC2_RGBA8 = 10,
        ASTC_4x4 = 11,
        ASTC_5x5 = 12,
        ASTC_6x6 = 13,
        ASTC_8x8 = 14,
        PVRTC_RGB4 = 15,
        PVRTC_RGBA4 = 16
    }

    /// <summary>
    /// UI language mode. Auto follows NDMF. / 语言模式。Auto 跟随 NDMF。
    /// </summary>
    public enum AtoLanguageMode
    {
        Auto = 0,
        Manual = 1
    }
}
