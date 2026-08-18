// AvatarTextureOptimizer.cs / AvatarTextureOptimizer.cs
// Main component that users place on their avatar root to enable texture optimization.
// 放置在Avatar根对象上启用贴图优化的主组件。

using System;
using System.Collections.Generic;
using UnityEngine;
#if ATO_VRCSDK_INSTALLED
using VRC.SDK3.Avatars.Components;
#endif

namespace net.fosa.avatar_texture_optimizer
{
    /// <summary>
    /// Quality preset for texture optimization.
    /// 贴图优化质量挡位。
    /// </summary>
    public enum QualityPreset
    {
        VeryLow = 0,   // 非常低 / Very Low
        Low = 1,       // 低 / Low
        Medium = 2,    // 中 / Medium (default)
        High = 3,      // 高 / High
        VeryHigh = 4,  // 非常高 / Very High
        Custom = 5     // 自定义 / Custom (near-lossless defaults)
    }

    /// <summary>
    /// Target platform for texture settings.
    /// 目标平台（用于贴图设置）。
    /// </summary>
    public enum TargetPlatform
    {
        Auto = 0,   // 自动（读取当前构建平台）/ Auto (read current build platform)
        PC = 1,     // PC (Windows/Mac/Linux)
        Android = 2,// Android / Quest 等移动端
        iOS = 3     // iOS
    }

    /// <summary>
    /// Padding level for atlas island gaps (in pixels).
    /// 图集岛间距挡位（像素）。
    /// </summary>
    public enum PaddingLevel
    {
        Px4 = 4,
        Px8 = 8,
        Px16 = 16,
        Px32 = 32,
        Px64 = 64
    }

    /// <summary>
    /// Pixel density preset (px per meter).
    /// 像素密度挡位（像素/米）。
    /// </summary>
    public enum PixelDensityPreset
    {
        Px512 = 512,
        Px1024 = 1024,
        Px2048 = 2048,
        Px4096 = 4096,
        Px8192 = 8192
    }

    /// <summary>
    /// Texture compression format.
    /// 贴图压缩格式。
    /// </summary>
    public enum CompressionFormat
    {
        Auto = 0,
        // PC / Desktop formats
        DXT1 = 1,       // BC1, opaque
        DXT5 = 2,       // BC3, with alpha
        BC7 = 3,        // high quality
        BC5 = 4,        // normal maps
        // Mobile / Cross-platform
        ASTC_4x4 = 10,
        ASTC_6x6 = 11,
        ASTC_8x8 = 12,
        ETC2 = 20,
        ETC2_Alpha = 21,
        PVRTC_RGB = 30, // iOS only, POT-only
        PVRTC_RGBA = 31,// iOS only, POT-only
        // Uncompressed
        RGBA32 = 40,
        R8 = 50,        // single channel (grayscale/mask)
    }

    /// <summary>
    /// Platform-specific override settings.
    /// 平台覆盖设置。
    /// </summary>
    [Serializable]
    public class PlatformOverride
    {
        public bool enabled = false;
        public CompressionFormat opaqueFormat = CompressionFormat.Auto;
        public CompressionFormat alphaFormat = CompressionFormat.Auto;
        public CompressionFormat normalFormat = CompressionFormat.Auto;
        public CompressionFormat grayscaleFormat = CompressionFormat.Auto;
        public bool mipmapEnabled = true;
        public bool crunchCompression = false;
        [Range(0, 100)] public int crunchCompressorQuality = 75;
        public int maxAtlasSize = 4096; // 移动端默认4096/PC默认8192
    }

    /// <summary>
    /// Custom quality thresholds for advanced users.
    /// 高级用户自定义质量阈值。
    /// </summary>
    [Serializable]
    public class CustomQualityThresholds
    {
        [Range(0.8f, 1.0f)] public float msSSIM = 1.0f;
        [Range(0f, 20f)]  public float deltaE = 0f;          // CIEDE2000
        [Range(0f, 30f)]  public float normalAngleDeg = 0f;  // 法线角度误差
        [Range(0f, 0.5f)] public float alphaRMSE = 0f;       // Blend模式alpha RMSE
        [Range(0.5f, 1.0f)] public float cutoutIoU = 1.0f;   // Cutout模式轮廓IoU
        [Range(0f, 0.5f)] public float grayscaleRMSE = 0f;   // 灰度单通道RMSE
    }

    /// <summary>
    /// Main Avatar Texture Optimizer component. Place on the avatar root
    /// (must have VRCAvatarDescriptor). Exactly one per avatar hierarchy.
    /// Avatar贴图优化器主组件。放置于Avatar根（必须有VRCAvatarDescriptor），
    /// 一个Avatar层级上只允许一个。
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("FOSA/Avatar Texture Optimizer")]
    public class AvatarTextureOptimizer : MonoBehaviour
    {
        [Header("General / 通用设置")]
        [Tooltip("Enable atlas generation. When disabled, unused UV areas are kept (no repack), whole textures are scaled only.\n开启图集生成。关闭时不剔除未使用UV、不重排UV，仅整体缩放贴图。")]
        public bool generateAtlas = true;

        [Tooltip("Quality preset. Custom uses user-defined thresholds (near-lossless by default).\n质量挡位。自定义使用用户定义阈值（默认近无损）。")]
        public QualityPreset qualityPreset = QualityPreset.High;

        [Tooltip("Target platform. Auto uses current build platform.\n目标平台。自动读取当前构建平台。")]
        public TargetPlatform targetPlatform = TargetPlatform.Auto;

        [Header("Pixel Density / 像素密度")]
        [Tooltip("Minimum pixel density (px/m). UV islands that need higher density than this still get it, but this prevents blurring on small objects.\n最小像素密度(px/m)。防止小物体发糊。")]
        public PixelDensityPreset minPixelDensity = PixelDensityPreset.Px2048;

        [Tooltip("Maximum pixel density (px/m). UV islands won't exceed this density to save space.\n最大像素密度(px/m)。防止浪费贴图空间。")]
        public PixelDensityPreset maxPixelDensity = PixelDensityPreset.Px4096;

        [Header("Atlas / 图集设置")]
        [Tooltip("Padding between UV islands (pixels). Higher values reduce bleeding but reduce density.\nUV岛间距(像素)。更大减少渗色但降低密度。")]
        public PaddingLevel atlasPadding = PaddingLevel.Px4;

        [Tooltip("Experimental NPOT (non-power-of-two) atlas support. When enabled generates non-power-of-two atlases at 64px step (disables incompatible formats like PVRTC on iOS).\n实验性NPOT（非2次幂）图集。以64px为步长生成非2次幂图集（自动剔除iOS PVRTC等不兼容格式）。")]
        public bool allowNPOT = false;

        [Header("Deduplication / 去重")]
        [Tooltip("Deduplicate identical materials/textures after optimization and merge identical material slots when safe.\n优化后对内容和参数完全相同的材质/贴图去重，并在安全时合并相同材质槽。")]
        public bool deduplicate = true;

        [Header("Advanced / 高级选项")]
        [Tooltip("Show detailed processing logs in the NDMF console.\n在NDMF控制台显示详细处理日志。")]
        public bool verboseLogging = false;

        [Tooltip("Enable GPU-accelerated quality evaluation and atlas dilation.\n启用GPU加速质量评估和图集美術外扩。")]
        public bool useGPU = true;

        [Tooltip("Custom quality thresholds (used when preset is Custom).\n自定义质量阈值（仅自定义挡位使用）。")]
        public CustomQualityThresholds customThresholds = new CustomQualityThresholds();

        [Header("Platform Overrides / 平台覆盖")]
        public PlatformOverride pcOverride = new PlatformOverride { maxAtlasSize = 8192 };
        public PlatformOverride androidOverride = new PlatformOverride { maxAtlasSize = 4096 };
        public PlatformOverride iosOverride = new PlatformOverride { maxAtlasSize = 4096 };

        // --- Whitelist references (populated via editor UI) ---
        // --- 白名单引用（通过Editor UI填充） ---
        [HideInInspector] public List<UnityEngine.Object> whitelist = new List<UnityEngine.Object>();

        /// <summary>
        /// Validate that the component is on a valid avatar root.
        /// 验证组件挂载在合法的Avatar根上。
        /// </summary>
        public bool IsValidAvatarRoot()
        {
#if ATO_VRCSDK_INSTALLED
            return GetComponent<VRCAvatarDescriptor>() != null;
#else
            // Without VRCSDK we allow any Animator as a fallback.
            // 没有VRCSDK时回退允许任何带Animator的对象。
            return GetComponent<Animator>() != null;
#endif
        }

        /// <summary>
        /// Get the effective platform settings based on targetPlatform and overrides.
        /// 根据目标平台和覆盖设置获取有效平台参数。
        /// </summary>
        public PlatformOverride GetEffectivePlatformSettings(TargetPlatform resolvedPlatform)
        {
            return resolvedPlatform switch
            {
                TargetPlatform.PC => pcOverride.enabled ? pcOverride : GetDefaultPcSettings(),
                TargetPlatform.Android => androidOverride.enabled ? androidOverride : GetDefaultAndroidSettings(),
                TargetPlatform.iOS => iosOverride.enabled ? iosOverride : GetDefaultIosSettings(),
                _ => GetDefaultPcSettings()
            };
        }

        private static PlatformOverride GetDefaultPcSettings() =>
            new PlatformOverride { maxAtlasSize = 8192, mipmapEnabled = true, crunchCompression = false };

        private static PlatformOverride GetDefaultAndroidSettings() =>
            new PlatformOverride { maxAtlasSize = 4096, mipmapEnabled = true, crunchCompression = true, crunchCompressorQuality = 50 };

        private static PlatformOverride GetDefaultIosSettings() =>
            new PlatformOverride { maxAtlasSize = 4096, mipmapEnabled = true, crunchCompression = false };
    }
}
