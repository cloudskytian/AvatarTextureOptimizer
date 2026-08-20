using System;
using System.Collections.Generic;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer
{
    /// <summary>
    /// Log verbosity levels. / 日志详细度级别。
    /// </summary>
    public enum AtoLogLevel
    {
        /// <summary>Only the final summary report. / 仅最终摘要报告。</summary>
        Summary = 0,
        /// <summary>Normal logging (default): stages, warnings, per-atlas summaries. / 常规（默认）：阶段、警告、每图集摘要。</summary>
        Normal = 1,
        /// <summary>Verbose debug logging (per-island/per-texture details). / 详细调试（每岛/每贴图细节）。</summary>
        Verbose = 2,
    }

    /// <summary>
    /// Per-category compression format configuration. / 按分类的压缩格式配置。
    /// Categories: opaque / transparent / normal map / grayscale. / 分类：不透明/透明/法线/灰度。
    /// </summary>
    [Serializable]
    public class AtoCompressionConfig
    {
        [Tooltip("Opaque textures / 不透明贴图")]
        public AtoCompressionFormat opaque = AtoCompressionFormat.Auto;

        [Tooltip("Transparent textures / 透明贴图")]
        public AtoCompressionFormat transparent = AtoCompressionFormat.Auto;

        [Tooltip("Normal maps / 法线贴图")]
        public AtoCompressionFormat normalMap = AtoCompressionFormat.Auto;

        [Tooltip("Grayscale/mask textures / 灰度/蒙版贴图")]
        public AtoCompressionFormat grayscale = AtoCompressionFormat.Auto;
    }

    /// <summary>
    /// Per-platform override. Only shown/effective when the platform is enabled. / 平台 override，勾选对应平台才显示并生效。
    /// </summary>
    [Serializable]
    public class AtoPlatformOverride
    {
        /// <summary>Enable override for this platform. / 为该平台启用 override。</summary>
        public bool enabled = false;

        /// <summary>Compression formats for this platform. / 该平台的压缩格式。</summary>
        public AtoCompressionConfig compression = new AtoCompressionConfig();

        /// <summary>Allow experimental NPOT atlases on this platform. / 该平台是否允许实验性 NPOT 图集。</summary>
        public bool npot = false;
    }

    /// <summary>
    /// All per-platform overrides. / 全部平台 override。
    /// </summary>
    [Serializable]
    public class AtoPlatformSettings
    {
        public AtoPlatformOverride pc = new AtoPlatformOverride();
        public AtoPlatformOverride android = new AtoPlatformOverride();
        public AtoPlatformOverride ios = new AtoPlatformOverride();
    }

    /// <summary>
    /// All ATO settings. Stored on the AtoAvatarRoot component. / ATO 全部设置，保存在 AtoAvatarRoot 组件上。
    /// Development stage: fields may change freely, no version compatibility needed. / 开发阶段：字段可随意调整，无需兼容旧版本。
    /// </summary>
    [Serializable]
    public class AtoSettings
    {
        [Header("Atlas / 图集")]
        [Tooltip("Generate atlases (pack islands from multiple textures). When disabled, textures are scaled as a whole instead. / 生成图集（把多张贴图的岛打包）。关闭时不生成图集，直接整图缩放。")]
        public bool generateAtlases = true;

        [Tooltip("Minimum island padding in atlas (px). / 图集岛间最小 padding（px）。")]
        public AtoPaddingOption minPadding = AtoPaddingOption.Px4;

        [Tooltip("Experimental: allow NPOT atlas sizes (64px steps). May exclude formats like PVRTC. / 实验性：允许 NPOT 图集边长（64 步进）。可能剔除 PVRTC 等格式。")]
        public bool experimentalNpot = false;

        [Header("Target Quality / 目标质量")]
        [Tooltip("Quality preset. Changing the preset changes the threshold values. / 质量挡位。切换挡位会改变阈值参数。")]
        public AtoQualityPreset preset = AtoQualityPreset.High;

        [Tooltip("Thresholds for the Custom preset. Defaults are all 1 (near lossless). / 自定义挡位参数。默认全部为 1（近无损）。")]
        public AtoQualityThresholds customThresholds = AtoQualityThresholds.NearLossless();

        [Header("Pixel Density Band (px/m) / 像素密度带（px/m）")]
        [Tooltip("Minimum allowed texel density. Islands never shrink below this (prevents blur). / 最小允许像素密度。岛不会缩到此值以下（防糊）。")]
        public AtoDensityPreset minPixelDensity = AtoDensityPreset.Px2048;

        [Tooltip("Maximum recommended texel density. Exceeding it produces a warning only (quality wins). / 最大建议像素密度。超过仅告警（质量优先）。")]
        public AtoDensityPreset maxPixelDensity = AtoDensityPreset.Px4096;

        [Header("Mipmaps / Mip 与 Streaming")]
        [Tooltip("Enable mipmaps AND MipStreaming together (VRChat requires both). Single switch controls both. / 同时控制 Mipmap 与 MipStreaming（VRChat 要求二者同开同关），只提供一个开关。")]
        public bool mipmapsAndStreaming = true;

        [Header("Compression / 压缩")]
        [Tooltip("General compression formats (used when no platform override is enabled). / 通用压缩格式（平台 override 未启用时使用）。")]
        public AtoCompressionConfig compression = new AtoCompressionConfig();

        [Header("Platform Overrides / 平台覆盖")]
        public AtoPlatformSettings platforms = new AtoPlatformSettings();

        [Header("Whitelist / 白名单")]
        [Tooltip("Objects (any type: meshes, materials, textures, animations...) whose referenced textures skip ALL optimization. / 白名单对象（不限类型）。其引用的全部贴图跳过所有优化。")]
        public List<UnityEngine.Object> whitelist = new List<UnityEngine.Object>();

        [Header("Localization / 本地化")]
        [Tooltip("Display language. Auto follows NDMF's language; falls back to English. / 显示语言。Auto 跟随 NDMF 当前语言，缺失翻译回退英文。")]
        public string language = "auto";

        [Header("Logging / 日志")]
        [Tooltip("Log verbosity. / 日志详细度。")]
        public AtoLogLevel logLevel = AtoLogLevel.Normal;

        /// <summary>
        /// Get the effective thresholds for the current preset. / 获取当前挡位的有效阈值。
        /// </summary>
        public AtoQualityThresholds GetThresholds() =>
            preset == AtoQualityPreset.Custom ? customThresholds : AtoQualityPresets.Get(preset);

        /// <summary>
        /// Whether the current preset means near-lossless (skip island scaling). / 当前挡位是否近无损（跳过岛缩放）。
        /// </summary>
        public bool IsNearLossless() => AtoQualityPresets.IsNearLossless(preset, customThresholds);
    }
}
