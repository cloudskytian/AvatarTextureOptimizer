// Avatar Texture Optimizer (ATO)
// Enums shared between the Runtime component and the Editor toolchain.
// 运行时组件与编辑器工具链共享的枚举定义。

namespace NetFosa.ATO
{
    /// <summary>
    /// Preset quality levels. / 预设质量挡位。
    /// The exact metric thresholds for each level live in the editor-side ATOQualityModel.
    /// 每个挡位的具体指标阈值定义在编辑器侧 ATOQualityModel 中。
    /// </summary>
    public enum ATOQualityLevel
    {
        Ultra = 0,   // ~lossless, skips UV island scaling entirely / 近无损，完全跳过 UV 岛缩放
        High = 1,    // recommended default / 推荐默认
        Medium = 2,
        Low = 3,
        Custom = 4   // user-defined thresholds, never overridden by other presets / 自定义阈值，不会被其他挡位覆盖
    }

    /// <summary>
    /// Target build platform (mirrors Unity's platform override concept).
    /// 目标构建平台（参考 Unity 的 platform override 概念）。
    /// </summary>
    public enum ATOPlatform
    {
        PC = 0,
        Android = 1,
        iOS = 2
    }

    /// <summary>
    /// Semantic category of a texture reference. Drives type-grouping, metrics and formats.
    /// 贴图引用的语义分类。驱动类型分组、质量评估与压缩格式选择。
    /// </summary>
    public enum ATOTextureCategory
    {
        MainColor = 0, // albedo / base color / 主色
        NormalMap = 1, // tangent-space normal map / 切线空间法线贴图
        Mask = 2,      // cutout/alpha/emission masks, single-channel-ish / 遮罩类（cutout/alpha/发光遮罩等）
        Grayscale = 3, // generic grayscale data (metallic/roughness/ao/ramp...) / 通用灰度数据
        Other = 4      // anything else; treated via fallback rules / 其他，走兜底规则
    }

    /// <summary>
    /// Safe compression-format enumeration. The concrete TextureFormat is resolved at
    /// build time per platform + category + actual alpha content, with fallback + warning.
    /// 安全的压缩格式枚举。具体 TextureFormat 在构建时按 平台 + 分类 + 实际 alpha 内容 解析，
    /// 并带兜底与告警。
    /// </summary>
    public enum ATOCompressionFormat
    {
        Auto = 0,
        RGBA32 = 1,        // uncompressed RGBA / 未压缩 RGBA
        RGB24 = 2,         // uncompressed RGB / 未压缩 RGB
        ASTC_4x4 = 3,
        ASTC_6x6 = 4,
        ASTC_8x8 = 5,
        BC7 = 6,
        BC5 = 7,           // two-channel, typical for normal maps / 双通道，常用于法线
        BC4 = 8,           // single-channel, typical for grayscale / 单通道，常用于灰度
        BC1 = 9,           // DXT1
        BC3 = 10,          // DXT5
        ETC2_RGBA8 = 11,
        ETC2_RGB8 = 12,
        PVRTC_RGBA4 = 13,  // iOS only / 仅 iOS
        PVRTC_RGB4 = 14    // iOS only / 仅 iOS
    }

    /// <summary>
    /// UI language mode. Auto follows NDMF's current language preference.
    /// UI 语言模式。Auto 跟随 NDMF 当前语言配置。
    /// </summary>
    public enum ATOLanguageMode
    {
        Auto = 0,
        English = 1,
        ChineseSimplified = 2
    }
}
