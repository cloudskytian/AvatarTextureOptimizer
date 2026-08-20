// Avatar Texture Optimizer (ATO)
// All code comments are bilingual (English + Simplified Chinese).
// 所有代码注释均为双语（英文 + 简体中文）。

namespace AvatarTextureOptimizer
{
    /// <summary>
    /// Texture category used to group textures that should share an atlas and
    /// share compression settings. Categories differ in color space, filter mode
    /// and "special" semantics (normal/mask maps must not be mixed with color maps).
    /// 贴图类别：用于决定哪些贴图应共享图集、共享压缩设置。
    /// 类别在色彩空间、filterMode 以及"特殊"语义（法线/蒙版不能与主色混装）上互不相同。
    /// </summary>
    public enum ATOTextureCategory
    {
        /// <summary>Main color / albedo (sRGB). 主色贴图（sRGB）。</summary>
        Albedo = 0,

        /// <summary>Normal map (linear, tangent-space). 法线贴图（线性空间切线法线）。</summary>
        Normal = 1,

        /// <summary>Mask / data map (linear, single-channel or packed). 蒙版/数据贴图（线性）。</summary>
        Mask = 2,

        /// <summary>Emission / other sRGB color texture. 自发光或其他 sRGB 彩色贴图。</summary>
        Emission = 3,

        /// <summary>Other / unknown. 其他/未知。</summary>
        Other = 4,
    }

    /// <summary>
    /// Quality level preset. The concrete thresholds are defined in
    /// <see cref="ATOQualitySettings"/> based on MS-SSIM / ΔE(CIEDE2000) literature.
    /// 质量挡位预设。具体阈值在 ATOQualitySettings 中基于 MS-SSIM/ΔE 学术与业内经验定义。
    /// </summary>
    public enum ATOQualityLevel
    {
        /// <summary>Ultra — near lossless. 超高清 —— 接近无损。</summary>
        Ultra = 0,

        /// <summary>High — recommended default. 高清 —— 推荐默认。</summary>
        High = 1,

        /// <summary>Balanced — good size/quality trade-off. 均衡 —— 体积/质量折中。</summary>
        Balanced = 2,

        /// <summary>Economy — smallest size. 经济 —— 最小体积。</summary>
        Economy = 3,

        /// <summary>Custom — user-defined, defaults to all-1 (near lossless). 自定义 —— 用户自定义，默认全 1（近无损）。</summary>
        Custom = 4,
    }

    /// <summary>
    /// Target platform. Mirrors Unity's platform override concept.
    /// 目标平台。对应 Unity 的 platform override 概念。
    /// </summary>
    public enum ATOPlatform
    {
        /// <summary>PC (Windows/Linux). 桌面端。</summary>
        PC = 0,

        /// <summary>Android (Quest). 安卓（Quest）。</summary>
        Android = 1,

        /// <summary>iOS. iOS 端。</summary>
        iOS = 2,
    }

    /// <summary>
    /// Minimum atlas padding (distance between islands), in texels.
    /// 图集最小 padding（岛间距），单位 texel。
    /// </summary>
    public enum ATOAtlasPadding
    {
        Px4 = 4,
        Px8 = 8,
        Px16 = 16,
        Px32 = 32,
        Px64 = 64,
    }

    /// <summary>
    /// Pixel density presets (px per meter). Used for min/max density clamping.
    /// 像素密度挡位（px/m）。用于最小/最大密度钳制。
    /// </summary>
    public enum ATOPixelDensityPreset
    {
        Px512 = 512,
        Px1024 = 1024,
        Px2048 = 2048,
        Px4096 = 4096,
        Px8192 = 8192,
    }

    /// <summary>
    /// Texture compression format option. This is a *safe* enumeration that is
    /// filtered per category and per platform at build time (see compression settings).
    /// 贴图压缩格式选项。这是一个"安全"枚举，构建时会按类别与平台过滤。
    /// </summary>
    public enum ATOCompressionFormat
    {
        /// <summary>Keep the per-platform default (build-time resolved). 保持各平台默认（构建时解析）。</summary>
        Auto = 0,

        /// <summary>RGBA32 uncompressed. RGBA32 不压缩。</summary>
        RGBA32 = 1,

        /// <summary>BC7 (PC, high quality, supports alpha). BC7（PC，高质量，支持 alpha）。</summary>
        BC7 = 2,

        /// <summary>BC5 (normal maps, two channels). BC5（法线贴图，双通道）。</summary>
        BC5 = 3,

        /// <summary>BC4 (single-channel). BC4（单通道）。</summary>
        BC4 = 4,

        /// <summary>BC1 / DXT1 (opaque only). BC1/DXT1（仅不透明）。</summary>
        BC1 = 5,

        /// <summary>BC3 / DXT5 (with alpha). BC3/DXT5（含 alpha）。</summary>
        BC3 = 6,

        /// <summary>ASTC 6x6 (mobile). ASTC 6x6（移动端）。</summary>
        ASTC_6x6 = 7,

        /// <summary>ASTC 4x4 (mobile, high quality). ASTC 4x4（移动端，高质量）。</summary>
        ASTC_4x4 = 8,

        /// <summary>ETC2 RGBA (mobile fallback). ETC2 RGBA（移动端兜底）。</summary>
        ETC2_RGBA = 9,
    }
}
