// ATOComponent.cs
// Avatar Texture Optimizer - The main component users add to their avatar.
// 用户在 Avatar 上添加的主要组件。
//
// Copyright (c) 2024 fosa. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using UnityEngine;
#if ATO_VRCSDK_PRESENT
using VRC.SDK3.Avatars.Components;
using VRC.SDKBase;
#endif

namespace Fosa.AvatarTextureOptimizer
{
    /// <summary>
    /// Add this component to the avatar root to enable texture optimization.
    /// Only one instance is allowed per avatar hierarchy (including children).
    /// 在 Avatar 根节点上添加此组件以启用贴图优化。整个层级只允许一个实例。
    /// </summary>
    [DisallowMultipleComponent]
    [HelpURL("https://github.com/fosa/avatar-texture-optimizer")]
    [AddComponentMenu("Avatar Texture Optimizer/ATO")]
    public class ATOComponent : MonoBehaviour
    {
        [Tooltip("Enable or disable the entire optimization pipeline. / 启用或禁用整个优化管线。")]
        [SerializeField] internal bool _enabled = true;

        [Tooltip("Global target quality preset. / 全局目标质量挡位。")]
        [SerializeField] internal QualityPreset _qualityPreset = QualityPreset.High;

        [Tooltip("Enable texture atlas generation. When disabled, textures are individually scaled instead. / 启用图集生成。禁用时则单独缩放贴图。")]
        [SerializeField] internal bool _generateAtlas = true;

        [Tooltip("Enable material deduplication. / 启用材质去重。")]
        [SerializeField] internal bool _deduplicateMaterials = true;

        [Tooltip("Enable texture / atlas deduplication. / 启用贴图/图集去重。")]
        [SerializeField] internal bool _deduplicateTextures = true;

        [Tooltip("Padding between atlas islands in pixels. / 图集岛间间距（像素）。")]
        [SerializeField] internal PaddingSize _padding = PaddingSize.Size4;

        [Tooltip("Enable experimental NPOT (non-power-of-two) atlas resolutions. / 启用实验性 NPOT（非2的幂）图集分辨率。")]
        [SerializeField] internal bool _useNPOT = false;

        [Tooltip("Enable detailed logging for debugging. / 启用详细调试日志。")]
        [SerializeField] internal bool _verboseLogging = false;

        [Tooltip("Maximum pixel density (pixels per meter) for UV island scaling. / UV 岛缩放的最大像素密度（像素/米）。")]
        [SerializeField, Range(256, 8192)] internal float _maxPixelDensity = 4096f;

        [Tooltip("Minimum pixel density (pixels per meter) for UV island scaling. / UV 岛缩放的最小像素密度（像素/米）。")]
        [SerializeField, Range(256, 8192)] internal float _minPixelDensity = 2048f;

        [Tooltip("Objects in this whitelist are completely excluded from optimization. / 白名单中的对象完全跳过优化。")]
        [SerializeField] internal List<Object> _whitelist = new List<Object>();

        // ──────────────────────────────────────────────
        // Advanced settings (folded in inspector)
        // 高级设置（在 Inspector 中折叠）
        // ──────────────────────────────────────────────

        [SerializeField] internal AdvancedSettings _advanced = new AdvancedSettings();

        [SerializeField] internal PlatformSettings _platformSettings = new PlatformSettings();

        [SerializeField] internal TextureFormatSettings _textureFormats = new TextureFormatSettings();

        /// <summary>Whether this component is enabled.</summary>
        public bool IsEnabled => _enabled;

        internal bool Enabled
        {
            get => _enabled;
            set => _enabled = value;
        }

        void Reset()
        {
            _qualityPreset = QualityPreset.High;
            _generateAtlas = true;
            _deduplicateMaterials = true;
            _deduplicateTextures = true;
            _padding = PaddingSize.Size4;
            _useNPOT = false;
            _verboseLogging = false;
            _maxPixelDensity = 4096f;
            _minPixelDensity = 2048f;
            _advanced = new AdvancedSettings();
            _platformSettings = new PlatformSettings();
            _textureFormats = new TextureFormatSettings();
        }
    }

    /// <summary>
    /// Quality presets backed by academic and industry research.
    /// MS-SSIM reference: Wang et al. (2003). SSIM reference: Wang et al. (2004).
    /// CIEDE2000 reference: Sharma et al. (2005).
    /// 基于学术和业界的质量挡位。
    /// </summary>
    public enum QualityPreset
    {
        /// <summary>Maximum quality, near-lossless. / 最高质量，近无损。</summary>
        NearLossless,
        /// <summary>High quality, very minor loss. / 高质量，极轻微损失。</summary>
        High,
        /// <summary>Medium quality, good balance. / 中等质量，平衡良好。</summary>
        Medium,
        /// <summary>Low quality, aggressive optimization. / 低质量，激进优化。</summary>
        Low,
        /// <summary>Custom parameters set by user. / 用户自定义参数。</summary>
        Custom
    }

    /// <summary>Atlas island padding sizes. / 图集岛间间距大小。</summary>
    public enum PaddingSize
    {
        Size4 = 4,
        Size8 = 8,
        Size16 = 16,
        Size32 = 32,
        Size64 = 64
    }

    /// <summary>Target build platforms for per-platform overrides. / 用于按平台覆盖的目标构建平台。</summary>
    [Flags]
    public enum ATOPlatform
    {
        None = 0,
        PC = 1,
        Android = 2,
        iOS = 4
    }

    /// <summary>Texture semantic categories used throughout the pipeline. / 管线中使用的贴图语义类别。</summary>
    public enum TextureCategory
    {
        Color,      // Main color / albedo (may have alpha)
        ColorOpaque,// Main color without alpha
        Normal,     // Normal/bump map
        Mask,       // Grayscale mask texture
        Emission,   // Emission map
        Other       // Uncategorized
    }

    /// <summary>
    /// Advanced tunable parameters. When the preset is Custom, these are used directly.
    /// Otherwise they are overridden by the preset's defaults.
    /// 高级可调参数。当挡位为 Custom 时直接使用这些值，否则被挡位默认值覆盖。
    /// </summary>
    [Serializable]
    public class AdvancedSettings
    {
        [Header("Quality Thresholds / 质量阈值")]
        [Tooltip("Minimum MS-SSIM score (0-1). Higher = stricter. / 最小 MS-SSIM 分数（0-1），越高越严格。")]
        [Range(0.5f, 1.0f)] public float mSSSIMThreshold = 0.995f;
        [Tooltip("Maximum CIEDE2000 ΔE. Lower = stricter. / 最大 CIEDE2000 ΔE，越低越严格。")]
        [Range(0.1f, 10.0f)] public float deltaEThreshold = 1.0f;
        [Tooltip("Maximum alpha RMSE for blend mode. / 混合模式的最大 alpha RMSE。")]
        [Range(0.001f, 0.5f)] public float alphaRMSEThreshold = 0.01f;
        [Tooltip("Minimum alpha IoU for cutout mode. / Cutout 模式的最小 alpha IoU。")]
        [Range(0.9f, 1.0f)] public float alphaIoUThreshold = 0.999f;
        [Tooltip("Maximum normal angular error (degrees). / 法线最大角度误差（度）。")]
        [Range(0.1f, 30.0f)] public float normalAngleThreshold = 5.0f;
        [Tooltip("Maximum grayscale RMSE. / 灰度贴图最大 RMSE。")]
        [Range(0.001f, 0.5f)] public float grayscaleRMSEThreshold = 0.005f;

        [Header("Island Size / 岛尺寸")]
        [Tooltip("Minimum island bounding-box short edge (pixels) below which SSIM reverts to single-scale. / 低于此值的岛短边回退到单尺度 SSIM。")]
        public int singleScaleSSIMThreshold = 176;
        [Tooltip("Minimum island bounding-box short edge (pixels) below which the metric is ignored. / 低于此值的岛短边忽略此参数。")]
        public int ignoreIslandThreshold = 11;
        [Tooltip("Minimum padding for sub-quality atlas compression. / 质量较低图集压缩的最小 padding。")]
        public int minCompressionPadding = 4;

        [Header("Performance / 性能")]
        [Tooltip("Use GPU (RenderTexture) for batch quality evaluation. / 使用 GPU（RenderTexture）批量评估质量。")]
        public bool useGPUAcceleration = true;
        [Tooltip("Use Burst-compiled jobs for parallel operations. / 使用 Burst 编译作业进行并行操作。")]
        public bool useBurstParallelism = true;
        [Tooltip("Rasterization granularity in pixels. / 光栅化粒度（像素）。")]
        public int rasterGranularity = 4;

        public AdvancedSettings Clone()
        {
            return (AdvancedSettings)MemberwiseClone();
        }

        /// <summary>Apply defaults for a given preset. / 为指定挡位应用默认值。</summary>
        public static AdvancedSettings ForPreset(QualityPreset preset)
        {
            return preset switch
            {
                QualityPreset.NearLossless => new AdvancedSettings
                {
                    mSSSIMThreshold = 1.0f,       // effectively lossless
                    deltaEThreshold = 0.0f,
                    alphaRMSEThreshold = 0.0f,
                    alphaIoUThreshold = 1.0f,
                    normalAngleThreshold = 0.0f,
                    grayscaleRMSEThreshold = 0.0f,
                    useGPUAcceleration = true,
                    useBurstParallelism = true,
                },
                QualityPreset.High => new AdvancedSettings
                {
                    mSSSIMThreshold = 0.995f,
                    deltaEThreshold = 1.0f,
                    alphaRMSEThreshold = 0.01f,
                    alphaIoUThreshold = 0.999f,
                    normalAngleThreshold = 5.0f,
                    grayscaleRMSEThreshold = 0.005f,
                    useGPUAcceleration = true,
                    useBurstParallelism = true,
                },
                QualityPreset.Medium => new AdvancedSettings
                {
                    mSSSIMThreshold = 0.97f,
                    deltaEThreshold = 3.0f,
                    alphaRMSEThreshold = 0.03f,
                    alphaIoUThreshold = 0.99f,
                    normalAngleThreshold = 10.0f,
                    grayscaleRMSEThreshold = 0.02f,
                    useGPUAcceleration = true,
                    useBurstParallelism = true,
                },
                QualityPreset.Low => new AdvancedSettings
                {
                    mSSSIMThreshold = 0.93f,
                    deltaEThreshold = 6.0f,
                    alphaRMSEThreshold = 0.08f,
                    alphaIoUThreshold = 0.95f,
                    normalAngleThreshold = 20.0f,
                    grayscaleRMSEThreshold = 0.05f,
                    useGPUAcceleration = true,
                    useBurstParallelism = true,
                },
                QualityPreset.Custom => new AdvancedSettings
                {
                    // Custom defaults: near-lossless (all ~1.0)
                    mSSSIMThreshold = 1.0f,
                    deltaEThreshold = 1.0f,
                    alphaRMSEThreshold = 0.01f,
                    alphaIoUThreshold = 0.999f,
                    normalAngleThreshold = 5.0f,
                    grayscaleRMSEThreshold = 0.005f,
                    useGPUAcceleration = true,
                    useBurstParallelism = true,
                },
                _ => new AdvancedSettings()
            };
        }
    }

    /// <summary>
    /// Per-platform texture and atlas format overrides.
    /// 按平台的贴图和图集格式覆盖。
    /// </summary>
    [Serializable]
    public class PlatformSettings
    {
        [Tooltip("Override settings for PC (Windows) builds. / 覆盖 PC（Windows）构建设置。")]
        public bool overridePC = false;
        [Tooltip("Override settings for Android (Quest) builds. / 覆盖 Android（Quest）构建设置。")]
        public bool overrideAndroid = false;
        [Tooltip("Override settings for iOS builds. / 覆盖 iOS 构建设置。")]
        public bool overrideIOS = false;

        // Max atlas size per platform
        [Range(64, 8192)] public int maxAtlasSizePC = 8192;
        [Range(64, 4096)] public int maxAtlasSizeAndroid = 4096;
        [Range(64, 4096)] public int maxAtlasSizeIOS = 4096;
    }

    /// <summary>
    /// Compression format settings categorized by texture type.
    /// 按贴图类型分类的压缩格式设置。
    /// </summary>
    [Serializable]
    public class TextureFormatSettings
    {
        [Tooltip("Whether to let ATO manage MipStreaming / Mipmap. / 是否由 ATO 管理 MipStreaming / Mipmap。")]
        public bool enableMipStreaming = true;

        [Header("PC Formats / PC 格式")]
        public TextureCompressionFormat transparentFormatPC = TextureCompressionFormat.BC7;
        public TextureCompressionFormat opaqueFormatPC = TextureCompressionFormat.BC7;
        public TextureCompressionFormat normalFormatPC = TextureCompressionFormat.BC5;
        public TextureCompressionFormat maskFormatPC = TextureCompressionFormat.BC4;

        [Header("Android Formats / Android 格式")]
        public TextureCompressionFormat transparentFormatAndroid = TextureCompressionFormat.ASTC;
        public TextureCompressionFormat opaqueFormatAndroid = TextureCompressionFormat.ASTC;
        public TextureCompressionFormat normalFormatAndroid = TextureCompressionFormat.ASTC;
        public TextureCompressionFormat maskFormatAndroid = TextureCompressionFormat.ASTC;

        [Header("iOS Formats / iOS 格式")]
        public TextureCompressionFormat transparentFormatIOS = TextureCompressionFormat.ASTC;
        public TextureCompressionFormat opaqueFormatIOS = TextureCompressionFormat.ASTC;
        public TextureCompressionFormat normalFormatIOS = TextureCompressionFormat.ASTC;
        public TextureCompressionFormat maskFormatIOS = TextureCompressionFormat.ASTC;
    }

    /// <summary>
    /// Safe enumeration of texture compression formats.
    /// 贴图压缩格式的安全枚举。
    /// </summary>
    public enum TextureCompressionFormat
    {
        Automatic,  // Let Unity decide / 由 Unity 决定
        None,       // No compression / 不压缩
        BC7,        // High quality, RGBA / 高质量 RGBA
        BC1,        // DXT1, opaque / 不透明
        BC3,        // DXT5, with alpha / 带 alpha
        BC4,        // Grayscale / 灰度
        BC5,        // Normal map / 法线
        ASTC,       // Mobile, configurable quality / 移动端
        ETC2,       // Android default / Android 默认
        RGBA32,     // Uncompressed RGBA / 不压缩
    }
}
