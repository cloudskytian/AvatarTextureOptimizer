using System;

// ATO shared enums.
// 共享枚举定义（英文注释 | 中文注释）。

namespace Net.Fosa.AvatarTextureOptimizer
{
    /// <summary>
    /// Build target platform, used for platform overrides and format rules.
    /// 构建目标平台：用于平台覆盖与格式规则。
    /// </summary>
    public enum ATOPlatform
    {
        PC = 0,
        Android = 1,
        iOS = 2,
    }

    /// <summary>
    /// Texture usage category inside a shader / material.
    /// 贴图在着色器/材质中的用途分类。
    /// </summary>
    [Flags]
    public enum TextureKind : byte
    {
        None = 0,
        /// <summary>Main/albedo color texture. 主色贴图。</summary>
        Color = 1 << 0,
        /// <summary>Normal map. 法线贴图。</summary>
        Normal = 1 << 1,
        /// <summary>Mask / grayscale (metallic, AO, shading grade...). 蒙版/灰度贴图。</summary>
        Mask = 1 << 2,
        /// <summary>Any other texture that cannot be classified. 无法分类的其他贴图。</summary>
        Unknown = 1 << 3,
    }

    /// <summary>
    /// Alpha test mode of a material, as seen for a texture use.
    /// 材质透明测试模式（针对某次贴图引用）。
    /// </summary>
    public enum AlphaMode
    {
        /// <summary>Opaque. 不透明。</summary>
        Opaque = 0,
        /// <summary>Alpha clip (cutout). Cutout（clip）。</summary>
        Cutout = 1,
        /// <summary>Blend / translucent. 混合/半透明。</summary>
        Blend = 2,
    }

    /// <summary>
    /// How a texture's pixels were classified (drives atlas bucket + compression).
    /// 贴图像素分类（决定图集桶与压缩）。
    /// </summary>
    public enum TextureClass
    {
        /// <summary>Opaque color. 不透明主色。</summary>
        ColorOpaque = 0,
        /// <summary>Color with meaningful alpha. 带有效 alpha 的主色。</summary>
        ColorAlpha = 1,
        /// <summary>Normal map. 法线贴图。</summary>
        Normal = 2,
        /// <summary>Grayscale mask. 灰度蒙版。</summary>
        Mask = 3,
        /// <summary>Could not be classified safely (treated as opaque color). 无法安全分类（按不透明主色处理）。</summary>
        Unknown = 4,
    }

    /// <summary>
    /// Texture filtering mode, part of the atlas bucket key.
    /// 过滤模式（图集桶 key 的一部分）。
    /// </summary>
    public enum ATOFilterMode
    {
        Point = 0,
        Bilinear = 1,
        Trilinear = 2,
    }

    /// <summary>
    /// Quality tier selector.
    /// 质量挡位选择器。
    /// </summary>
    public enum QualityTierId
    {
        /// <summary>Custom (user-edited, defaults all to near-lossless 1). 自定义（默认全部≈1 近无损）。</summary>
        Custom = 0,
        Ultra = 1,
        High = 2,
        Medium = 3,
        Low = 4,
        Minimum = 5,
    }

    /// <summary>
    /// Mip / streaming binding switch.
    /// Mip 与 MipStreaming 联动开关。
    /// </summary>
    public enum MipMode
    {
        /// <summary>Off: mipmap off, streaming off. 关：mip 关、streaming 关。</summary>
        Off = 0,
        /// <summary>On: mipmap on and streaming forced on (VRChat requirement). 开：mip 开、streaming 强制开（VRChat 要求）。</summary>
        On = 1,
    }

    /// <summary>
    /// Atlas candidate pool mode.
    /// 图集候选池模式。
    /// </summary>
    public enum AtlasSizeMode
    {
        /// <summary>Power-of-two edge lengths, min 64, max 8192 (mobile 4096). 2 的 n 次幂边长。</summary>
        PowerOfTwo = 0,
        /// <summary>Experimental NPOT: 64px step up to max 8192 (mobile 4096). 实验性 NPOT：64 步进。</summary>
        NonPowerOfTwo = 1,
    }

    /// <summary>
    /// i18n language selection.
    /// i18n 语言选择。
    /// </summary>
    public enum ATOLanguageMode
    {
        /// <summary>Follow NDMF's current language. 跟随 NDMF 当前语言。</summary>
        Auto = 0,
        /// <summary>Manually selected from available i18n files. 从可用 i18n 文件手动选择。</summary>
        Manual = 1,
    }

    /// <summary>
    /// Safe compression format choices surfaced to the user.
    /// 提供给用户的安全压缩格式枚举。
    /// </summary>
    public enum ATOCompressionFormat
    {
        /// <summary>Unity automatic. Unity 自动。</summary>
        Automatic = 0,
        RGBA32 = 1,
        RGB24 = 2,
        BC7 = 3,
        BC4 = 4,
        BC5 = 5,
        DXT1 = 6,
        DXT5 = 7,
        ETC2_RGB = 8,
        ETC2_RGBA8 = 9,
        ASTC_4x4 = 10,
        ASTC_6x6 = 11,
        ASTC_8x8 = 12,
        ASTC_10x10 = 13,
        ASTC_12x12 = 14,
        PVRTC_RGB4 = 15,
        PVRTC_RGBA4 = 16,
    }

    /// <summary>
    /// Which part of the pipeline a log line / report row belongs to.
    /// 日志/报告行所属管线阶段。
    /// </summary>
    public enum ATOStage
    {
        Validation = 0,
        Analysis = 1,
        Dedup = 2,
        Scaling = 3,
        Packing = 4,
        Baking = 5,
        Remap = 6,
        Post = 7,
        Final = 8,
    }
}
