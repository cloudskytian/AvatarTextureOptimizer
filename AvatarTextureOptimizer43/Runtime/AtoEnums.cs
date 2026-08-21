using System;
using UnityEngine;

namespace Fosa.ATO
{
    /// <summary>
    /// Quality preset. Changing a named preset overwrites numeric thresholds.
    /// Custom is never overwritten by preset switching.
    /// 质量挡位。切换具名挡位会覆盖数值阈值；Custom 不会被覆盖。
    /// </summary>
    public enum AtoQualityPreset
    {
        /// <summary>Skip UV scaling entirely (near-lossless copy). 跳过 UV 缩放（近无损拷贝）。</summary>
        Lossless = 0,
        /// <summary>Visually lossless at close inspection. 近距离目视无损。</summary>
        Ultra = 1,
        /// <summary>Default. Indistinguishable at typical VRChat viewing distance. 默认。常规距离不可辨。</summary>
        High = 2,
        /// <summary>Slight differences on close look; Quest-friendly. 近看略有差异，适合 Quest。</summary>
        Medium = 3,
        /// <summary>Aggressive size reduction. 激进缩小。</summary>
        Low = 4,
        /// <summary>User-owned numbers; default all 1 (near-lossless). 用户自定义，默认全 1。</summary>
        Custom = 5
    }

    /// <summary>
    /// Target platform used to pick atlas size caps and safe texture formats.
    /// 用于选择图集上限和安全压缩格式的目标平台。
    /// </summary>
    public enum AtoBuildPlatform
    {
        /// <summary>Read the current Unity build target. 读取当前构建平台。</summary>
        Auto = 0,
        PC = 1,
        Android = 2,
        iOS = 3
    }

    /// <summary>
    /// Semantic texture class used for format / mip / quality routing.
    /// 贴图语义分类，决定格式、Mip 与质量算法。
    /// </summary>
    public enum AtoTextureClass
    {
        Opaque = 0,
        Transparent = 1,
        Normal = 2,
        Gray = 3
    }

    /// <summary>
    /// Safe compressed-format enum. Invalid combinations are rejected at bake with fallback.
    /// 安全压缩格式枚举。非法组合在烘焙时回退。
    /// </summary>
    public enum AtoSafeFormat
    {
        Auto = 0,
        RGBA32 = 1,
        RGB24 = 2,
        DXT1 = 3,
        DXT5 = 4,
        BC4 = 5,
        BC5 = 6,
        BC7 = 7,
        ETC2_RGB = 8,
        ETC2_RGBA8 = 9,
        ASTC_4x4 = 10,
        ASTC_5x5 = 11,
        ASTC_6x6 = 12,
        ASTC_8x8 = 13
    }

    /// <summary>
    /// Minimum island padding in pixels (user-facing steps).
    /// 用户可选的最小 padding 挡位。
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
    /// Pixel-density steps in px/m.
    /// 像素密度挡位（px/m）。
    /// </summary>
    public enum AtoPixelDensity
    {
        D512 = 512,
        D1024 = 1024,
        D2048 = 2048,
        D4096 = 4096,
        D8192 = 8192
    }

    /// <summary>
    /// UI language. Auto follows NDMF LanguagePrefs.
    /// 界面语言。Auto 跟随 NDMF。
    /// </summary>
    public enum AtoLanguageMode
    {
        Auto = 0,
        Manual = 1
    }

    /// <summary>
    /// Alpha evaluation mode inferred from shader / material.
    /// 从着色器推断的透明评估模式。
    /// </summary>
    public enum AtoAlphaMode
    {
        Opaque = 0,
        Cutout = 1,
        Blend = 2
    }
}
