using System;
using UnityEngine;

namespace net.fosa.ato
{
    /// <summary>
    /// EN: Per texture class output settings (compression format + mip behaviour).
    ///     Mipmap and Mip Streaming are intentionally exposed as a single toggle: VRChat requires
    ///     Streaming Mip Maps to be enabled whenever mip maps exist, so they are hard-bound here.
    /// ZH: 按贴图分类的输出设置（压缩格式 + mip 行为）。
    ///     Mipmap 与 Mip Streaming 刻意合并成一个开关：VRChat 要求只要存在 mipmap 就必须开启
    ///     Streaming Mip Maps，因此二者在此做硬绑定。
    /// </summary>
    [Serializable]
    public struct TextureClassSettings
    {
        /// <summary>EN: Generate mip maps AND enable streaming mip maps. ZH: 生成 mipmap 并同时开启 Mip Streaming。</summary>
        public bool mipmapAndStreaming;

        /// <summary>EN: Format used for opaque colour output. ZH: 不透明彩色输出所用格式。</summary>
        public ATOColorFormat opaqueColorFormat;

        /// <summary>EN: Format used for transparent colour output. ZH: 透明彩色输出所用格式。</summary>
        public ATOColorFormat transparentColorFormat;

        /// <summary>EN: Format used for normal map output. ZH: 法线贴图输出所用格式。</summary>
        public ATONormalFormat normalFormat;

        /// <summary>EN: Format used for data / grayscale output. ZH: 数据/灰度输出所用格式。</summary>
        public ATOGrayscaleFormat grayscaleFormat;

        /// <summary>EN: Unity compressor quality 0..100. ZH: Unity 压缩器质量 0..100。</summary>
        [Range(0, 100)] public int compressorQuality;
    }

    /// <summary>
    /// EN: The complete set of optimisation parameters for one platform. The "common" profile is always
    ///     present; a platform override profile is only consulted when its toggle is enabled.
    /// ZH: 单个平台的完整优化参数集合。"通用"配置始终存在；平台覆盖配置仅在其开关被勾选时生效。
    /// </summary>
    [Serializable]
    public class PlatformProfile
    {
        /// <summary>EN: Whether this platform override is active. Ignored for the common profile.
        /// ZH: 该平台覆盖是否启用。对通用配置无意义。</summary>
        public bool enabled = false;

        /// <summary>EN: Which platform this profile targets. ZH: 该配置针对的平台。</summary>
        public ATOPlatform platform = ATOPlatform.PC;

        // ---- Quality ----------------------------------------------------------------------------

        /// <summary>EN: Selected quality tier. ZH: 选中的质量挡位。</summary>
        public QualityTier qualityTier = QualityTier.High;

        /// <summary>EN: Live thresholds. Overwritten whenever <see cref="qualityTier"/> changes, except for Custom.
        /// ZH: 实际生效的阈值。除 Custom 外，切换 <see cref="qualityTier"/> 时会被覆盖。</summary>
        public QualityProfile quality = QualityPresets.Get(QualityTier.High);

        /// <summary>EN: User editable Custom tier thresholds. Never overwritten by tier changes.
        /// ZH: 用户可编辑的 Custom 挡位阈值，切换挡位时永不被覆盖。</summary>
        public QualityProfile customQuality = QualityPresets.Lossless;

        // ---- Texel density ----------------------------------------------------------------------

        /// <summary>EN: Lower clamp on texel density, px per metre. ZH: 像素密度下限，像素/米。</summary>
        public ATODensity minTexelDensity = ATODensity.D2048;

        /// <summary>EN: Upper clamp on texel density, px per metre. ZH: 像素密度上限，像素/米。</summary>
        public ATODensity maxTexelDensity = ATODensity.D4096;

        // ---- Atlas ------------------------------------------------------------------------------

        /// <summary>EN: Master switch for atlas generation. When off we only rescale whole textures.
        /// ZH: 图集生成总开关。关闭时只做整图缩放。</summary>
        public bool generateAtlas = true;

        /// <summary>EN: Allow non power of two atlas side lengths, stepping by 64 px.
        /// ZH: 允许非 2 次幂的图集边长，以 64 像素步进。</summary>
        public bool experimentalNPOT = false;

        /// <summary>EN: Minimum padding between islands, in pixels. ZH: 岛之间的最小间距（像素）。</summary>
        public ATOPadding minPadding = ATOPadding.Px4;

        /// <summary>EN: Allow rotating islands by 90 degrees while packing. Tangent data is never recomputed.
        /// ZH: 装箱时允许岛旋转 90 度。切线数据绝不重算。</summary>
        public bool allowIslandRotation = true;

        // ---- Output -----------------------------------------------------------------------------

        /// <summary>EN: Output settings per texture class. ZH: 按贴图分类的输出设置。</summary>
        public TextureClassSettings output = new TextureClassSettings
        {
            mipmapAndStreaming = true,
            opaqueColorFormat = ATOColorFormat.Auto,
            transparentColorFormat = ATOColorFormat.Auto,
            normalFormat = ATONormalFormat.Auto,
            grayscaleFormat = ATOGrayscaleFormat.Auto,
            compressorQuality = 100,
        };

        // ---- Post passes ------------------------------------------------------------------------

        /// <summary>EN: Deduplicate materials that end up byte-identical. ZH: 对最终完全相同的材质去重。</summary>
        public bool deduplicateMaterials = true;

        /// <summary>EN: Deduplicate textures / atlases that end up byte-identical. ZH: 对最终完全相同的贴图/图集去重。</summary>
        public bool deduplicateTextures = true;

        /// <summary>
        /// EN: Resolve the effective quality thresholds, honouring the Custom tier.
        /// ZH: 解析实际生效的质量阈值，正确处理 Custom 挡位。
        /// </summary>
        public QualityProfile EffectiveQuality =>
            qualityTier == QualityTier.Custom ? customQuality : quality;

        /// <summary>
        /// EN: Push the preset thresholds of the current tier into <see cref="quality"/>.
        ///     Called by the inspector when the tier dropdown changes. Custom is left untouched.
        /// ZH: 把当前挡位的预设阈值写入 <see cref="quality"/>。挡位下拉变化时由 Inspector 调用。Custom 不受影响。
        /// </summary>
        public void SyncQualityFromTier()
        {
            if (qualityTier == QualityTier.Custom) return;
            quality = QualityPresets.Get(qualityTier);
        }

        /// <summary>
        /// EN: Deep copy, used to seed a platform override from the common profile.
        /// ZH: 深拷贝，用于以通用配置初始化平台覆盖配置。
        /// </summary>
        public PlatformProfile Clone()
        {
            return (PlatformProfile)MemberwiseClone();
        }
    }
}
