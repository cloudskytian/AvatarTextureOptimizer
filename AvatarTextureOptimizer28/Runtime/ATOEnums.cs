namespace net.fosa.ato
{
    /// <summary>
    /// EN: Quality preset tiers. Values are stable identifiers, do not reorder in a released build.
    /// ZH: 质量挡位预设。取值为稳定标识，正式发布后请勿重排。
    /// </summary>
    public enum QualityTier
    {
        /// <summary>EN: Near lossless. All island rescaling is skipped entirely. ZH: 近无损，完全跳过岛缩放。</summary>
        Lossless = 0,
        /// <summary>EN: Very high quality. ZH: 极高质量。</summary>
        VeryHigh = 1,
        /// <summary>EN: High quality (default). ZH: 高质量（默认）。</summary>
        High = 2,
        /// <summary>EN: Medium quality. ZH: 中等质量。</summary>
        Medium = 3,
        /// <summary>EN: Low quality, maximum size reduction. ZH: 低质量，体积压缩最大。</summary>
        Low = 4,
        /// <summary>EN: User defined. Never overwritten when other tiers change. ZH: 用户自定义，切换其他挡位时不会被覆盖。</summary>
        Custom = 100,
    }

    /// <summary>
    /// EN: Build platform an override profile applies to.
    /// ZH: 平台覆盖配置所对应的构建平台。
    /// </summary>
    public enum ATOPlatform
    {
        /// <summary>EN: Windows / Mac / Linux standalone. ZH: PC 独立平台。</summary>
        PC = 0,
        /// <summary>EN: Android (Quest / PICO). ZH: 安卓（Quest / PICO）。</summary>
        Android = 1,
        /// <summary>EN: iOS. ZH: iOS。</summary>
        iOS = 2,
    }

    /// <summary>
    /// EN: Semantic class of a texture, used to pick a compression format and to pick a quality metric.
    /// ZH: 贴图的语义分类，用于选择压缩格式与质量度量方式。
    /// </summary>
    public enum TextureClass
    {
        /// <summary>EN: Colour texture without a meaningful alpha channel. ZH: 无有效 alpha 通道的彩色贴图。</summary>
        OpaqueColor = 0,
        /// <summary>EN: Colour texture with a meaningful alpha channel. ZH: 含有效 alpha 通道的彩色贴图。</summary>
        TransparentColor = 1,
        /// <summary>EN: Tangent space normal map. ZH: 切线空间法线贴图。</summary>
        Normal = 2,
        /// <summary>EN: Single or multi channel non colour data (mask, smoothness...). ZH: 单/多通道非色彩数据（蒙版、光滑度等）。</summary>
        Grayscale = 3,
    }

    /// <summary>
    /// EN: How a material treats the alpha channel. Drives which alpha metric is used.
    /// ZH: 材质对 alpha 通道的处理方式，决定使用哪种 alpha 度量。
    /// </summary>
    public enum AlphaMode
    {
        /// <summary>EN: Alpha ignored. ZH: 忽略 alpha。</summary>
        Opaque = 0,
        /// <summary>EN: Alpha thresholded against a cutoff. ZH: 用 Cutoff 阈值裁剪 alpha。</summary>
        Cutout = 1,
        /// <summary>EN: Alpha blended. ZH: alpha 混合。</summary>
        Blend = 2,
    }

    /// <summary>
    /// EN: Safe subset of texture compression formats we are willing to emit for colour data.
    /// ZH: 用于彩色数据的安全压缩格式子集。
    /// </summary>
    public enum ATOColorFormat
    {
        /// <summary>EN: Let ATO decide from the platform and content. ZH: 由 ATO 根据平台与内容自动决定。</summary>
        Auto = 0,
        /// <summary>EN: DXT1 (BC1), PC only, no alpha. ZH: DXT1（BC1），仅 PC，无 alpha。</summary>
        DXT1 = 1,
        /// <summary>EN: DXT5 (BC3), PC only, with alpha. ZH: DXT5（BC3），仅 PC，含 alpha。</summary>
        DXT5 = 2,
        /// <summary>EN: BC7, PC only, high quality with alpha. ZH: BC7，仅 PC，高质量含 alpha。</summary>
        BC7 = 3,
        /// <summary>EN: DXT1 Crunched, PC only, no alpha. ZH: DXT1 Crunch，仅 PC，无 alpha。</summary>
        DXT1Crunched = 4,
        /// <summary>EN: DXT5 Crunched, PC only, with alpha. ZH: DXT5 Crunch，仅 PC，含 alpha。</summary>
        DXT5Crunched = 5,
        /// <summary>EN: ASTC 4x4, mobile. ZH: ASTC 4x4，移动端。</summary>
        ASTC4x4 = 10,
        /// <summary>EN: ASTC 5x5, mobile. ZH: ASTC 5x5，移动端。</summary>
        ASTC5x5 = 11,
        /// <summary>EN: ASTC 6x6, mobile. ZH: ASTC 6x6，移动端。</summary>
        ASTC6x6 = 12,
        /// <summary>EN: ASTC 8x8, mobile. ZH: ASTC 8x8，移动端。</summary>
        ASTC8x8 = 13,
        /// <summary>EN: ASTC 10x10, mobile. ZH: ASTC 10x10，移动端。</summary>
        ASTC10x10 = 14,
        /// <summary>EN: ASTC 12x12, mobile. ZH: ASTC 12x12，移动端。</summary>
        ASTC12x12 = 15,
        /// <summary>EN: ETC2 RGB, mobile, no alpha. ZH: ETC2 RGB，移动端，无 alpha。</summary>
        ETC2RGB = 20,
        /// <summary>EN: ETC2 RGBA8, mobile, with alpha. ZH: ETC2 RGBA8，移动端，含 alpha。</summary>
        ETC2RGBA8 = 21,
        /// <summary>EN: Uncompressed RGBA32. Escape hatch, very large. ZH: 未压缩 RGBA32，兜底选项，体积很大。</summary>
        RGBA32 = 90,
    }

    /// <summary>
    /// EN: Safe subset of formats for tangent space normal maps.
    /// ZH: 切线空间法线贴图的安全格式子集。
    /// </summary>
    public enum ATONormalFormat
    {
        /// <summary>EN: Automatic. ZH: 自动。</summary>
        Auto = 0,
        /// <summary>EN: BC5 two channel, PC, best quality/size for normals. ZH: BC5 双通道，PC，法线最佳性价比。</summary>
        BC5 = 1,
        /// <summary>EN: BC7, PC. ZH: BC7，PC。</summary>
        BC7 = 2,
        /// <summary>EN: DXT5nm, PC, legacy. ZH: DXT5nm，PC，传统方案。</summary>
        DXT5nm = 3,
        /// <summary>EN: ASTC 4x4, mobile. ZH: ASTC 4x4，移动端。</summary>
        ASTC4x4 = 10,
        /// <summary>EN: ASTC 5x5, mobile. ZH: ASTC 5x5，移动端。</summary>
        ASTC5x5 = 11,
        /// <summary>EN: ASTC 6x6, mobile. ZH: ASTC 6x6，移动端。</summary>
        ASTC6x6 = 12,
        /// <summary>EN: ASTC 8x8, mobile. ZH: ASTC 8x8，移动端。</summary>
        ASTC8x8 = 13,
        /// <summary>EN: Uncompressed RGBA32. ZH: 未压缩 RGBA32。</summary>
        RGBA32 = 90,
    }

    /// <summary>
    /// EN: Safe subset of formats for single/multi channel data textures.
    /// ZH: 单/多通道数据贴图的安全格式子集。
    /// </summary>
    public enum ATOGrayscaleFormat
    {
        /// <summary>EN: Automatic. ZH: 自动。</summary>
        Auto = 0,
        /// <summary>EN: BC4 single channel, PC. Downgraded automatically if more than one channel is used.
        /// ZH: BC4 单通道，PC。若实际使用多通道会自动降级。</summary>
        BC4 = 1,
        /// <summary>EN: BC7, PC. ZH: BC7，PC。</summary>
        BC7 = 2,
        /// <summary>EN: DXT1, PC. ZH: DXT1，PC。</summary>
        DXT1 = 3,
        /// <summary>EN: DXT5, PC. ZH: DXT5，PC。</summary>
        DXT5 = 4,
        /// <summary>EN: ASTC 4x4, mobile. ZH: ASTC 4x4，移动端。</summary>
        ASTC4x4 = 10,
        /// <summary>EN: ASTC 6x6, mobile. ZH: ASTC 6x6，移动端。</summary>
        ASTC6x6 = 12,
        /// <summary>EN: ASTC 8x8, mobile. ZH: ASTC 8x8，移动端。</summary>
        ASTC8x8 = 13,
        /// <summary>EN: ETC2 RGB, mobile. ZH: ETC2 RGB，移动端。</summary>
        ETC2RGB = 20,
        /// <summary>EN: Uncompressed R8. ZH: 未压缩 R8。</summary>
        R8 = 89,
        /// <summary>EN: Uncompressed RGBA32. ZH: 未压缩 RGBA32。</summary>
        RGBA32 = 90,
    }

    /// <summary>
    /// EN: Minimum atlas padding options, in pixels.
    /// ZH: 图集最小 padding 选项（像素）。
    /// </summary>
    public enum ATOPadding
    {
        /// <summary>EN: 4 px. ZH: 4 像素。</summary>
        Px4 = 4,
        /// <summary>EN: 8 px. ZH: 8 像素。</summary>
        Px8 = 8,
        /// <summary>EN: 16 px. ZH: 16 像素。</summary>
        Px16 = 16,
        /// <summary>EN: 32 px. ZH: 32 像素。</summary>
        Px32 = 32,
        /// <summary>EN: 64 px. ZH: 64 像素。</summary>
        Px64 = 64,
    }

    /// <summary>
    /// EN: Texel density presets, in pixels per metre of avatar surface.
    /// ZH: 像素密度预设，单位为每米模型表面的像素数。
    /// </summary>
    public enum ATODensity
    {
        /// <summary>EN: 512 px/m. ZH: 512 像素/米。</summary>
        D512 = 512,
        /// <summary>EN: 1024 px/m. ZH: 1024 像素/米。</summary>
        D1024 = 1024,
        /// <summary>EN: 2048 px/m. ZH: 2048 像素/米。</summary>
        D2048 = 2048,
        /// <summary>EN: 4096 px/m. ZH: 4096 像素/米。</summary>
        D4096 = 4096,
        /// <summary>EN: 8192 px/m. ZH: 8192 像素/米。</summary>
        D8192 = 8192,
    }

    /// <summary>
    /// EN: UI language selection mode.
    /// ZH: 界面语言选择模式。
    /// </summary>
    public enum ATOLanguageMode
    {
        /// <summary>EN: Follow NDMF's language preference. ZH: 跟随 NDMF 的语言设置。</summary>
        Auto = 0,
        /// <summary>EN: Force a specific language code. ZH: 强制指定语言代码。</summary>
        Manual = 1,
    }
}
