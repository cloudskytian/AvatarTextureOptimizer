// Copyright (c) fosa. Licensed under the MIT License.
// Serializable enumerations shared by the runtime component and the editor pipeline.
// 运行时组件与编辑器管线共用的可序列化枚举。

namespace Net.Fosa.AvatarTextureOptimizer
{
    /// <summary>
    /// Quality preset tiers. Values are persisted, so DO NOT renumber existing entries.
    /// 质量挡位。数值会被序列化，请勿重新编号已有项。
    /// </summary>
    public enum QualityTier
    {
        /// <summary>Near-lossless. Skips UV rescaling entirely. / 近无损，完全跳过 UV 缩放。</summary>
        Maximum = 0,

        /// <summary>High quality. / 高质量。</summary>
        High = 1,

        /// <summary>Balanced (default). / 均衡（默认）。</summary>
        Balanced = 2,

        /// <summary>Performance oriented. / 性能优先。</summary>
        Performance = 3,

        /// <summary>Extreme compression. / 极限压缩。</summary>
        Extreme = 4,

        /// <summary>User defined; never overwritten by tier switching. / 用户自定义，切换挡位不会覆盖。</summary>
        Custom = 5,
    }

    /// <summary>
    /// Target platform for parameter overrides, mirroring Unity's platform override concept.
    /// 平台覆盖目标，参考 Unity 自身的 platform override 概念。
    /// </summary>
    public enum ATOPlatform
    {
        /// <summary>PC / Standalone.</summary>
        PC = 0,

        /// <summary>Android (Quest).</summary>
        Android = 1,

        /// <summary>iOS.</summary>
        iOS = 2,
    }

    /// <summary>
    /// Classification of a texture, used to pick compression formats and mip settings.
    /// 贴图分类，用于选择压缩格式与 mip 设置。
    /// </summary>
    public enum TextureCategory
    {
        /// <summary>Opaque colour texture (no alpha channel in use). / 不透明颜色贴图。</summary>
        OpaqueColor = 0,

        /// <summary>Colour texture with a meaningful alpha channel. / 含有效 alpha 通道的颜色贴图。</summary>
        TransparentColor = 1,

        /// <summary>Tangent-space normal map. / 切线空间法线贴图。</summary>
        NormalMap = 2,

        /// <summary>Single/multi channel mask or grayscale data. / 单/多通道蒙版或灰度数据。</summary>
        Grayscale = 3,
    }

    /// <summary>
    /// Safe compression format choices exposed to users. Unsafe combinations are filtered
    /// per platform and per texture content at build time.
    /// 提供给用户的安全压缩格式枚举。不安全的组合会在构建时按平台与贴图内容过滤。
    /// </summary>
    public enum ATOCompressionFormat
    {
        /// <summary>Let ATO decide based on platform and content. / 由 ATO 依据平台与内容自动决定。</summary>
        Auto = 0,

        /// <summary>Uncompressed RGBA32. Largest, lossless. / 未压缩 RGBA32，体积最大、无损。</summary>
        Uncompressed = 1,

        /// <summary>BC7 (PC, high quality RGBA). / BC7（PC，高质量 RGBA）。</summary>
        BC7 = 2,

        /// <summary>DXT1 / BC1 (PC, opaque). / DXT1（PC，不透明）。</summary>
        DXT1 = 3,

        /// <summary>DXT5 / BC3 (PC, alpha). / DXT5（PC，带 alpha）。</summary>
        DXT5 = 4,

        /// <summary>BC5 (PC, two channel, ideal for normal maps). / BC5（PC，双通道，适合法线）。</summary>
        BC5 = 5,

        /// <summary>BC4 (PC, single channel grayscale). / BC4（PC，单通道灰度）。</summary>
        BC4 = 6,

        /// <summary>DXT1 with crunch. / DXT1 + Crunch。</summary>
        DXT1Crunched = 7,

        /// <summary>DXT5 with crunch. / DXT5 + Crunch。</summary>
        DXT5Crunched = 8,

        /// <summary>ASTC 4x4 (mobile, highest quality). / ASTC 4x4（移动端，最高质量）。</summary>
        ASTC_4x4 = 9,

        /// <summary>ASTC 6x6 (mobile, balanced). / ASTC 6x6（移动端，均衡）。</summary>
        ASTC_6x6 = 10,

        /// <summary>ASTC 8x8 (mobile, small). / ASTC 8x8（移动端，小体积）。</summary>
        ASTC_8x8 = 11,

        /// <summary>ETC2 RGBA8 (mobile fallback). / ETC2 RGBA8（移动端兜底）。</summary>
        ETC2_RGBA8 = 12,
    }

    /// <summary>
    /// Minimum padding between islands inside an atlas, in pixels.
    /// 图集内岛间最小间距（像素）。
    /// </summary>
    public enum AtlasPadding
    {
        /// <summary>4 pixels (default). / 4 像素（默认）。</summary>
        Px4 = 4,

        /// <summary>8 pixels.</summary>
        Px8 = 8,

        /// <summary>16 pixels.</summary>
        Px16 = 16,

        /// <summary>32 pixels.</summary>
        Px32 = 32,

        /// <summary>64 pixels.</summary>
        Px64 = 64,
    }

    /// <summary>
    /// Pixel density steps offered in the UI, in pixels per metre.
    /// UI 中提供的像素密度挡位，单位 px/m。
    /// </summary>
    public enum PixelDensityStep
    {
        /// <summary>512 px/m.</summary>
        D512 = 512,

        /// <summary>1024 px/m.</summary>
        D1024 = 1024,

        /// <summary>2048 px/m.</summary>
        D2048 = 2048,

        /// <summary>4096 px/m.</summary>
        D4096 = 4096,

        /// <summary>8192 px/m.</summary>
        D8192 = 8192,
    }

    /// <summary>
    /// UI language selection. Auto follows NDMF's current language setting.
    /// 界面语言选择。Auto 跟随 NDMF 的当前语言设置。
    /// </summary>
    public enum LanguageMode
    {
        /// <summary>Follow NDMF language. / 跟随 NDMF 语言。</summary>
        Auto = 0,

        /// <summary>Explicit language code stored separately. / 使用单独存储的显式语言代码。</summary>
        Explicit = 1,
    }

    /// <summary>
    /// How a material treats alpha, used to pick the alpha quality metric.
    /// 材质的 alpha 处理方式，用于选择 alpha 质量指标。
    /// </summary>
    public enum AlphaMode
    {
        /// <summary>Fully opaque; alpha ignored. / 完全不透明，忽略 alpha。</summary>
        Opaque = 0,

        /// <summary>Alpha tested against a cutoff. / 按 cutoff 做 alpha 测试。</summary>
        Cutout = 1,

        /// <summary>Alpha blended. / alpha 混合。</summary>
        Blend = 2,
    }
}
