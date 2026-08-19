// SPDX-License-Identifier: MIT
// AvatarTextureOptimizer (ATO) - Runtime enumerations.
// AvatarTextureOptimizer (ATO) - 运行时枚举定义。

namespace Net.Fosa.AvatarTextureOptimizer
{
    /// <summary>
    /// EN: Target platform for per-platform overrides. Mirrors Unity's platform override concept.
    /// ZH: 平台覆盖用的目标平台。对应 Unity 自身的 platform override 概念。
    /// </summary>
    public enum ATOPlatform
    {
        /// <summary>EN: Windows / Mac / Linux standalone. ZH: PC 独立平台。</summary>
        PC = 0,

        /// <summary>EN: Android (Quest). ZH: 安卓（Quest）。</summary>
        Android = 1,

        /// <summary>EN: iOS. ZH: iOS。</summary>
        iOS = 2,
    }

    /// <summary>
    /// EN: Quality presets. Every preset maps onto a concrete <see cref="ATOQualityParams"/> set which the
    ///     user may inspect (and, for <see cref="Custom"/>, edit) under the advanced foldout.
    /// ZH: 质量挡位。每个挡位都会映射到一组具体的 <see cref="ATOQualityParams"/> 参数，
    ///     用户可在高级选项折叠区内查看；<see cref="Custom"/> 挡位允许用户自行修改且不会被其他挡位覆盖。
    /// </summary>
    public enum ATOQualityTier
    {
        /// <summary>EN: Aggressive size reduction, visible on close inspection. ZH: 激进压缩，近看可见差异。</summary>
        Draft = 0,

        /// <summary>EN: Performance oriented. ZH: 偏性能。</summary>
        Performance = 1,

        /// <summary>EN: Default. Balanced quality / size. ZH: 默认挡位，质量与体积平衡。</summary>
        Balanced = 2,

        /// <summary>EN: High quality, near transparent to the eye. ZH: 高质量，肉眼几乎无法分辨。</summary>
        High = 3,

        /// <summary>EN: Mathematically near-lossless: island rescaling is skipped entirely. ZH: 近无损：完全跳过 UV 岛缩放。</summary>
        Lossless = 4,

        /// <summary>EN: User defined. Defaults to the Lossless parameter set; never overwritten by other tiers.
        ///     ZH: 用户自定义。默认等于近无损参数，且不会被其他挡位覆盖。</summary>
        Custom = 5,
    }

    /// <summary>
    /// EN: Classification of a texture, used for per-class compression / mipmap options and for choosing
    ///     the correct quality metric.
    /// ZH: 贴图分类。用于按类别设置压缩格式 / Mipmap 选项，并决定使用哪种质量评估指标。
    /// </summary>
    public enum ATOTextureClass
    {
        /// <summary>EN: Colour texture without meaningful alpha. ZH: 无有效 alpha 的彩色贴图。</summary>
        OpaqueColor = 0,

        /// <summary>EN: Colour texture with meaningful alpha. ZH: 含有效 alpha 的彩色贴图。</summary>
        TransparentColor = 1,

        /// <summary>EN: Tangent-space normal map. ZH: 切线空间法线贴图。</summary>
        NormalMap = 2,

        /// <summary>EN: Data / mask / grayscale texture. ZH: 数据 / 蒙版 / 灰度贴图。</summary>
        Grayscale = 3,
    }

    /// <summary>
    /// EN: Safe compression format choices. The build-time resolver validates each choice against the
    ///     platform, the alpha requirement and the actual channel usage, and falls back safely when needed.
    /// ZH: 安全的压缩格式枚举。构建时会针对平台、alpha 需求与实际通道使用情况进行校验，
    ///     必要时安全回退，保证任何选项组合都不会让材质表现出错。
    /// </summary>
    public enum ATOCompressionFormat
    {
        /// <summary>EN: Let ATO pick the best format for the platform and content. ZH: 由 ATO 按平台与内容自动选择最优格式。</summary>
        Auto = 0,

        // ---- Desktop / PC ----
        BC7 = 10,
        BC5 = 11,
        BC4 = 12,
        DXT1 = 13,
        DXT5 = 14,
        DXT1Crunched = 15,
        DXT5Crunched = 16,

        // ---- Mobile (Android / iOS) ----
        ASTC_4x4 = 30,
        ASTC_5x5 = 31,
        ASTC_6x6 = 32,
        ASTC_8x8 = 33,
        ASTC_10x10 = 34,
        ASTC_12x12 = 35,
        ETC2_RGB4 = 40,
        ETC2_RGBA8 = 41,
        ETC2_RGB4Crunched = 42,
        ETC2_RGBA8Crunched = 43,

        // ---- Uncompressed ----
        RGBA32 = 60,
        RGB24 = 61,
        RG16 = 62,
        R8 = 63,
    }

    /// <summary>
    /// EN: Minimum padding (in pixels) between atlas islands.
    /// ZH: 图集内 UV 岛之间的最小间距（像素）。
    /// </summary>
    public enum ATOMinPadding
    {
        Px4 = 4,
        Px8 = 8,
        Px16 = 16,
        Px32 = 32,
        Px64 = 64,
    }

    /// <summary>
    /// EN: Pixel-density steps offered to the user (texels per meter of avatar surface).
    /// ZH: 提供给用户的像素密度挡位（每米模型表面对应的贴图像素数）。
    /// </summary>
    public enum ATOPixelDensity
    {
        Px512 = 512,
        Px1024 = 1024,
        Px2048 = 2048,
        Px4096 = 4096,
        Px8192 = 8192,
    }

    /// <summary>
    /// EN: UI language selection. Auto follows NDMF's current language.
    /// ZH: 界面语言选择。Auto 跟随 NDMF 当前语言设置。
    /// </summary>
    public enum ATOLanguageMode
    {
        Auto = 0,
        Explicit = 1,
    }

    /// <summary>
    /// EN: Alpha blending mode inferred for a material, used to pick the alpha quality metric.
    /// ZH: 从材质推断出的透明模式，用于选择 alpha 质量评估指标。
    /// </summary>
    public enum ATOAlphaMode
    {
        /// <summary>EN: Fully opaque; alpha is ignored. ZH: 完全不透明，忽略 alpha。</summary>
        Opaque = 0,

        /// <summary>EN: Alpha test / cutout; compared with clipped-silhouette IoU. ZH: Alpha 裁剪，使用 clip 后轮廓 IoU 比较。</summary>
        Cutout = 1,

        /// <summary>EN: Alpha blend; compared with linear RMSE. ZH: Alpha 混合，使用线性 RMSE 比较。</summary>
        Blend = 2,
    }
}
