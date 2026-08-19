using System;

namespace NetFosa.AvatarTextureOptimizer
{
    /// <summary>
    /// 质量挡位。数值见 QualityPresets.cs 的阈值表。
    /// Quality presets. Numeric thresholds live in QualityPresets.cs.
    /// </summary>
    public enum ATOQualityPreset
    {
        /// <summary>自定义挡位：参数由用户修改，不会被其他挡位覆盖。默认全部为 1（近无损）。</summary>
        Custom = 0,
        /// <summary>几乎无损：跳过 UV 缩放，原样拷贝。</summary>
        NearLossless = 1,
        /// <summary>高质量。</summary>
        High = 2,
        /// <summary>平衡（默认）。</summary>
        Balanced = 3,
        /// <summary>性能优先。</summary>
        Performance = 4,
        /// <summary>极限压缩。</summary>
        Extreme = 5,
    }

    /// <summary>
    /// 目标平台（平台 override 用）。
    /// Target platforms for platform overrides.
    /// </summary>
    public enum ATOPlatform
    {
        PC = 0,
        Android = 1,
        iOS = 2,
    }

    /// <summary>
    /// 贴图类别（决定压缩格式等默认值）。
    /// Texture category used to pick compression defaults.
    /// </summary>
    public enum ATOTextureCategory
    {
        /// <summary>不透明主色贴图。</summary>
        MainOpaque = 0,
        /// <summary>含透明通道的主色贴图（图集存在 alpha 时）。</summary>
        MainTransparent = 1,
        /// <summary>法线贴图。</summary>
        Normal = 2,
        /// <summary>灰度/蒙版贴图。</summary>
        GrayMask = 3,
        /// <summary>其他（emission 等）。</summary>
        Other = 4,
    }

    /// <summary>
    /// 安全的压缩格式枚举（剔除会导致问题的组合，如 PVRTC 要求 POT）。
    /// Safe compression format enumeration (formats that can break things, e.g. PVRTC on NPOT, are excluded).
    /// </summary>
    public enum ATOCompressionFormat
    {
        /// <summary>自动：根据像素内容与平台选择最佳格式。</summary>
        Auto = 0,
        /// <summary>不压缩（RGBA32/RGB24）。</summary>
        None = 1,
        RGBA32 = 2,
        RGB24 = 3,
        DXT1 = 4,
        DXT5 = 5,
        BC7 = 6,
        ETC2_RGB = 7,
        ETC2_RGBA = 8,
        ASTC_4x4 = 9,
        ASTC_6x6 = 10,
        ASTC_8x8 = 11,
        ASTC_10x10 = 12,
        ASTC_12x12 = 13,
        ETC_RGB4 = 14,
        PVRTC_RGB4 = 15,
        PVRTC_RGBA4 = 16,
        CrunchDXT1 = 17,
        CrunchDXT5 = 18,
        CrunchETC2_RGB = 19,
        CrunchETC2_RGBA = 20,
        CrunchASTC_4x4 = 21,
        CrunchASTC_6x6 = 22,
        CrunchASTC_8x8 = 23,
        CrunchASTC_12x12 = 24,
    }

    /// <summary>
    /// 色彩空间。
    /// </summary>
    public enum ATOColorSpace
    {
        SRGB = 0,
        Linear = 1,
    }

    /// <summary>
    /// 过滤模式（对应 TextureImporter.filterMode）。
    /// </summary>
    public enum ATOFilterMode
    {
        Point = 0,
        Bilinear = 1,
        Trilinear = 2,
    }

    /// <summary>
    /// 界面语言（i18n）。
    /// </summary>
    public enum ATOI18nLanguage
    {
        /// <summary>自动读取 NDMF 当前语言，缺翻译回退英文。</summary>
        Auto = 0,
        English = 1,
        SimplifiedChinese = 2,
        /// <summary>用户扩展语言（读取到的自定义 JSON 动态追加，见 Localization）。</summary>
        Custom = 100,
    }

    /// <summary>
    /// 白名单影响级别（内部使用）。
    /// </summary>
    public enum ATOWhitelistLevel
    {
        /// <summary>完全跳过所有优化（含导入参数优化）。</summary>
        Full = 0,
        /// <summary>跳过图集化，但仍参与整图缩放与导入参数优化（同 UV 的其他贴图）。</summary>
        NoAtlas = 1,
        /// <summary>正常参与所有优化。</summary>
        Normal = 2,
    }

    /// <summary>
    /// 贴图用途分类（决定质量指标类型）。
    /// Texture usage kind, decides which quality metric applies.
    /// </summary>
    public enum ATOUsageKind
    {
        /// <summary>主色（不透明）：MS-SSIM + ΔE2000。</summary>
        Main = 0,
        /// <summary>主色（透明）：MS-SSIM + ΔE2000 + alpha 指标。</summary>
        MainAlpha = 1,
        /// <summary>法线贴图：解码→重采样→重归一化→编码后角度误差 p95。</summary>
        Normal = 2,
        /// <summary>灰度/蒙版：仅被使用通道、线性空间 RMSE 逐通道取最差。</summary>
        GrayMask = 3,
        /// <summary>其他（按主色处理兜底）。</summary>
        Other = 4,
    }
}
