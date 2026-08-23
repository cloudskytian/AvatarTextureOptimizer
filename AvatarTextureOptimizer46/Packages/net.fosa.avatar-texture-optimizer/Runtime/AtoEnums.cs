// SPDX-License-Identifier: MIT
// Avatar Texture Optimizer (ATO)
// EN: Core enumerations shared by runtime settings and the editor pipeline.
// ZH: 运行时设置与编辑器管线共用的核心枚举。

namespace Net.Fosa.AvatarTextureOptimizer
{
    /// <summary>
    /// EN: Target platform for per-platform overrides. Mirrors Unity's texture platform overrides.
    /// ZH: 平台覆盖用的目标平台，对应 Unity 自身的贴图 platform override。
    /// </summary>
    public enum AtoPlatform
    {
        /// <summary>EN: Windows / Mac / Linux standalone. ZH: PC（Windows/Mac/Linux）。</summary>
        PC = 0,
        /// <summary>EN: Android (Quest). ZH: Android（Quest）。</summary>
        Android = 1,
        /// <summary>EN: iOS. ZH: iOS。</summary>
        iOS = 2,
    }

    /// <summary>
    /// EN: Semantic classification of a texture. Drives quality metrics, compression and atlas grouping.
    /// ZH: 贴图的语义分类。决定质量度量方式、压缩格式与图集分组。
    /// </summary>
    public enum AtoTextureKind
    {
        /// <summary>EN: Opaque color/albedo (sRGB, no meaningful alpha). ZH: 不透明颜色/主色贴图（sRGB，无有效 alpha）。</summary>
        ColorOpaque = 0,
        /// <summary>EN: Color/albedo with meaningful alpha (sRGB). ZH: 带有效 alpha 的颜色贴图（sRGB）。</summary>
        ColorAlpha = 1,
        /// <summary>EN: Tangent space normal map (linear, possibly DXTnm/BC5 packed). ZH: 切线空间法线贴图（线性，可能是 DXTnm/BC5 编码）。</summary>
        Normal = 2,
        /// <summary>EN: Grayscale / mask / data map (linear). ZH: 灰度、蒙版或数据贴图（线性）。</summary>
        Grayscale = 3,
    }

    /// <summary>
    /// EN: How a material treats alpha for the texture. Determines the alpha metric used.
    /// ZH: 材质对贴图 alpha 的处理方式，决定使用哪种 alpha 度量。
    /// </summary>
    public enum AtoAlphaMode
    {
        /// <summary>EN: Alpha ignored. ZH: 忽略 alpha。</summary>
        Opaque = 0,
        /// <summary>EN: Alpha clipped by cutoff; evaluated by silhouette IoU. ZH: 由 cutoff 裁剪；用轮廓 IoU 评估。</summary>
        Cutout = 1,
        /// <summary>EN: Alpha blended; evaluated by linear RMSE. ZH: alpha 混合；用线性 RMSE 评估。</summary>
        Blend = 2,
    }

    /// <summary>
    /// EN: Built-in quality tiers. Parameter values come from <c>AtoQualityPresets</c>.
    /// ZH: 内置质量挡位，具体参数取自 <c>AtoQualityPresets</c>。
    /// </summary>
    public enum AtoQualityTier
    {
        /// <summary>EN: Near lossless. Island scaling is skipped entirely. ZH: 近无损，完全跳过 UV 岛缩放。</summary>
        Lossless = 0,
        /// <summary>EN: Very high quality. ZH: 极高质量。</summary>
        VeryHigh = 1,
        /// <summary>EN: High quality. ZH: 高质量。</summary>
        High = 2,
        /// <summary>EN: Balanced (default). ZH: 均衡（默认）。</summary>
        Balanced = 3,
        /// <summary>EN: Performance oriented. ZH: 性能优先。</summary>
        Performance = 4,
        /// <summary>EN: Aggressive, aimed at mobile/Quest. ZH: 激进，面向移动端/Quest。</summary>
        Mobile = 5,
        /// <summary>EN: User defined. Never overwritten by tier changes. ZH: 用户自定义，不会被其他挡位覆盖。</summary>
        Custom = 6,
    }

    /// <summary>
    /// EN: Minimum padding options (pixels) between islands inside an atlas.
    /// ZH: 图集内岛间最小间距（像素）选项。
    /// </summary>
    public enum AtoPaddingOption
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
    /// EN: Pixel density steps (texels per meter) offered in the UI.
    /// ZH: UI 中提供的像素密度挡位（每米像素数）。
    /// </summary>
    public enum AtoPixelDensity
    {
        /// <summary>EN: 512 texels/m. ZH: 512 像素/米。</summary>
        D512 = 512,
        /// <summary>EN: 1024 texels/m. ZH: 1024 像素/米。</summary>
        D1024 = 1024,
        /// <summary>EN: 2048 texels/m. ZH: 2048 像素/米。</summary>
        D2048 = 2048,
        /// <summary>EN: 4096 texels/m. ZH: 4096 像素/米。</summary>
        D4096 = 4096,
        /// <summary>EN: 8192 texels/m. ZH: 8192 像素/米。</summary>
        D8192 = 8192,
    }

    /// <summary>
    /// EN: Safe compression formats for sRGB color atlases WITHOUT alpha.
    /// ZH: 不含 alpha 的 sRGB 颜色图集的安全压缩格式。
    /// </summary>
    public enum AtoColorOpaqueFormat
    {
        /// <summary>EN: Let ATO pick the best format for the platform. ZH: 由 ATO 按平台自动选择最优格式。</summary>
        Auto = 0,
        /// <summary>EN: BC7 (PC, high quality). ZH: BC7（PC，高质量）。</summary>
        BC7 = 1,
        /// <summary>EN: DXT1 / BC1 (PC, small). ZH: DXT1/BC1（PC，体积小）。</summary>
        DXT1 = 2,
        /// <summary>EN: DXT1 Crunched (PC, small on disk). ZH: DXT1 Crunched（PC，硬盘体积小）。</summary>
        DXT1Crunched = 3,
        /// <summary>EN: ASTC 4x4 (mobile, highest quality). ZH: ASTC 4x4（移动端，质量最高）。</summary>
        ASTC4x4 = 4,
        /// <summary>EN: ASTC 5x5 (mobile). ZH: ASTC 5x5（移动端）。</summary>
        ASTC5x5 = 5,
        /// <summary>EN: ASTC 6x6 (mobile, balanced). ZH: ASTC 6x6（移动端，均衡）。</summary>
        ASTC6x6 = 6,
        /// <summary>EN: ASTC 8x8 (mobile, small). ZH: ASTC 8x8（移动端，体积小）。</summary>
        ASTC8x8 = 7,
        /// <summary>EN: ETC2 RGB4 (Android fallback). ZH: ETC2 RGB4（Android 回退）。</summary>
        ETC2RGB4 = 8,
        /// <summary>EN: Uncompressed RGBA32. Only as a last resort. ZH: 未压缩 RGBA32，仅作最后手段。</summary>
        Uncompressed = 9,
    }

    /// <summary>
    /// EN: Safe compression formats for sRGB color atlases WITH alpha.
    /// ZH: 含 alpha 的 sRGB 颜色图集的安全压缩格式。
    /// </summary>
    public enum AtoColorAlphaFormat
    {
        /// <summary>EN: Let ATO pick the best format for the platform. ZH: 由 ATO 按平台自动选择最优格式。</summary>
        Auto = 0,
        /// <summary>EN: BC7 (PC, high quality alpha). ZH: BC7（PC，高质量 alpha）。</summary>
        BC7 = 1,
        /// <summary>EN: DXT5 / BC3 (PC). ZH: DXT5/BC3（PC）。</summary>
        DXT5 = 2,
        /// <summary>EN: DXT5 Crunched (PC). ZH: DXT5 Crunched（PC）。</summary>
        DXT5Crunched = 3,
        /// <summary>EN: ASTC 4x4 (mobile, highest quality). ZH: ASTC 4x4（移动端，质量最高）。</summary>
        ASTC4x4 = 4,
        /// <summary>EN: ASTC 5x5 (mobile). ZH: ASTC 5x5（移动端）。</summary>
        ASTC5x5 = 5,
        /// <summary>EN: ASTC 6x6 (mobile, balanced). ZH: ASTC 6x6（移动端，均衡）。</summary>
        ASTC6x6 = 6,
        /// <summary>EN: ASTC 8x8 (mobile, small). ZH: ASTC 8x8（移动端，体积小）。</summary>
        ASTC8x8 = 7,
        /// <summary>EN: ETC2 RGBA8 (Android fallback). ZH: ETC2 RGBA8（Android 回退）。</summary>
        ETC2RGBA8 = 8,
        /// <summary>EN: Uncompressed RGBA32. Only as a last resort. ZH: 未压缩 RGBA32，仅作最后手段。</summary>
        Uncompressed = 9,
    }

    /// <summary>
    /// EN: Safe compression formats for tangent space normal maps.
    /// ZH: 切线空间法线贴图的安全压缩格式。
    /// </summary>
    public enum AtoNormalFormat
    {
        /// <summary>EN: Let ATO pick the best format for the platform. ZH: 由 ATO 按平台自动选择最优格式。</summary>
        Auto = 0,
        /// <summary>EN: BC5 two-channel (PC, best for normals). ZH: BC5 双通道（PC，法线最佳）。</summary>
        BC5 = 1,
        /// <summary>EN: BC7 (PC). ZH: BC7（PC）。</summary>
        BC7 = 2,
        /// <summary>EN: DXT5nm (PC, legacy). ZH: DXT5nm（PC，传统）。</summary>
        DXT5 = 3,
        /// <summary>EN: ASTC 4x4 (mobile). ZH: ASTC 4x4（移动端）。</summary>
        ASTC4x4 = 4,
        /// <summary>EN: ASTC 5x5 (mobile). ZH: ASTC 5x5（移动端）。</summary>
        ASTC5x5 = 5,
        /// <summary>EN: ASTC 6x6 (mobile). ZH: ASTC 6x6（移动端）。</summary>
        ASTC6x6 = 6,
        /// <summary>EN: Uncompressed RGBA32. ZH: 未压缩 RGBA32。</summary>
        Uncompressed = 7,
    }

    /// <summary>
    /// EN: Safe compression formats for grayscale / mask textures.
    /// ZH: 灰度与蒙版贴图的安全压缩格式。
    /// </summary>
    public enum AtoGrayscaleFormat
    {
        /// <summary>EN: Let ATO pick the best format for the platform. ZH: 由 ATO 按平台自动选择最优格式。</summary>
        Auto = 0,
        /// <summary>EN: BC4 single channel (PC). Downgraded automatically if the mask is multi channel. ZH: BC4 单通道（PC）。若蒙版为多通道会自动降级。</summary>
        BC4 = 1,
        /// <summary>EN: BC7 (PC, multi channel). ZH: BC7（PC，多通道）。</summary>
        BC7 = 2,
        /// <summary>EN: DXT1 (PC, multi channel, small). ZH: DXT1（PC，多通道，体积小）。</summary>
        DXT1 = 3,
        /// <summary>EN: ASTC 4x4 (mobile). ZH: ASTC 4x4（移动端）。</summary>
        ASTC4x4 = 4,
        /// <summary>EN: ASTC 6x6 (mobile). ZH: ASTC 6x6（移动端）。</summary>
        ASTC6x6 = 5,
        /// <summary>EN: ASTC 8x8 (mobile). ZH: ASTC 8x8（移动端）。</summary>
        ASTC8x8 = 6,
        /// <summary>EN: ETC2 RGB4 (Android fallback). ZH: ETC2 RGB4（Android 回退）。</summary>
        ETC2RGB4 = 7,
        /// <summary>EN: Uncompressed RGBA32. ZH: 未压缩 RGBA32。</summary>
        Uncompressed = 8,
    }

    /// <summary>
    /// EN: Reasons a texture or renderer was excluded from optimization.
    /// ZH: 贴图或渲染器被排除在优化之外的原因。
    /// </summary>
    public enum AtoSkipReason
    {
        /// <summary>EN: Not skipped. ZH: 未跳过。</summary>
        None = 0,
        /// <summary>EN: Explicitly whitelisted by the user. ZH: 被用户显式加入白名单。</summary>
        UserWhitelist,
        /// <summary>EN: Material applies a non identity texture scale/offset. ZH: 材质对贴图施加了非单位的缩放/平移。</summary>
        NonIdentityST,
        /// <summary>EN: Animation drives an _ST / scroll / rotate property. ZH: 动画驱动了 _ST / 滚动 / 旋转属性。</summary>
        AnimatedUVTransform,
        /// <summary>EN: Shader samples this texture in a non mesh-UV space (matcap, screen, gradient...). ZH: 着色器以非网格 UV 空间采样该贴图（matcap/屏幕/渐变等）。</summary>
        NonMeshUVSampling,
        /// <summary>EN: Shader feature relies on UV wrapping (e.g. lilToon _ShiftBackfaceUV). ZH: 着色器特性依赖 UV wrap（如 lilToon 的 _ShiftBackfaceUV）。</summary>
        WrapDependentShaderFeature,
        /// <summary>EN: UVs leave [0,1] and cross a wrap seam. ZH: UV 越界且跨越 wrap 缝。</summary>
        UVOutOfRangeCrossingSeam,
        /// <summary>EN: Same texture sampled through different UV channels. ZH: 同一贴图被不同 UV 通道采样。</summary>
        ConflictingUVChannels,
        /// <summary>EN: Texture is not a Texture2D. ZH: 贴图不是 Texture2D。</summary>
        NotTexture2D,
        /// <summary>EN: Shader could not be analyzed. ZH: 无法分析该着色器。</summary>
        UnknownShader,
        /// <summary>EN: No free UV channel left to evacuate for AAO. ZH: 没有空闲 UV 通道可供 AAO evacuate。</summary>
        NoFreeUVChannel,
        /// <summary>EN: The island does not fit even in the largest candidate atlas. ZH: 该岛在最大候选图集中也放不下。</summary>
        DoesNotFitAnyAtlas,
        /// <summary>EN: Texture could not be decoded. ZH: 贴图无法解码。</summary>
        DecodeFailed,
    }
}
