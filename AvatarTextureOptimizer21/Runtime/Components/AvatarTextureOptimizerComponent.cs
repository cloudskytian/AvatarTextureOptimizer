// AvatarTextureOptimizer Component
// AvatarTextureOptimizer 组件
// 
// This is the main component that users add to their VRChat Avatar root.
// 这是用户添加到VRChat Avatar根对象上的主组件。

using System;
using System.Collections.Generic;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.Runtime
{
    /// <summary>
    /// Avatar Texture Optimizer component. Add to avatar root with VRCAvatarDescriptor.
    /// Avatar贴图优化器组件。添加到具有VRCAvatarDescriptor的Avatar根对象上。
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Avatar Texture Optimizer/ATO Component")]
    [RequireComponent(typeof(Animator))]
    public class AvatarTextureOptimizerComponent : MonoBehaviour
    {
        [Header("== General Settings / 通用设置 ==")]
        [Tooltip("Enable atlas generation. When disabled, textures are scaled directly without atlas packing.")]
        public bool generateAtlas = true;

        [Tooltip("Target quality preset level.")]
        public QualityPreset qualityPreset = QualityPreset.Balanced;

        [Tooltip("Target platform for optimization.")]
        public TargetPlatform targetPlatform = TargetPlatform.Auto;

        [Header("== Pixel Density / 像素密度 ==")]
        [Tooltip("Minimum pixel density (px/m) for UV islands.")]
        public float minPixelDensity = 2048f;

        [Tooltip("Maximum pixel density (px/m) for UV islands.")]
        public float maxPixelDensity = 4096f;

        [Tooltip("Pixel density preset for maximum detail.")]
        public PixelDensityPreset pixelDensityPreset = PixelDensityPreset.Px2048;

        [Header("== Atlas Settings / 图集设置 ==")]
        [Tooltip("Maximum atlas size for PC.")]
        public int maxAtlasSizePC = 8192;

        [Tooltip("Maximum atlas size for mobile (Android/iOS).")]
        public int maxAtlasSizeMobile = 4096;

        [Tooltip("Minimum padding between atlas islands.")]
        public AtlasPaddingPreset minPadding = AtlasPaddingPreset.Px4;

        [Tooltip("Enable NPOT (Non-Power-Of-Two) atlas resolutions.")]
        public bool enableNPOTAtlas = false;

        [Header("== Deduplication / 去重 ==")]
        [Tooltip("Enable material deduplication after optimization.")]
        public bool deduplicateMaterials = true;

        [Tooltip("Enable texture/atlas deduplication after optimization.")]
        public bool deduplicateTextures = true;

        [Header("== MipStreaming / Mip流式传输 ==")]
        [Tooltip("Enable MipStreaming for optimized textures (also controls Mipmap).")]
        public bool enableMipStreaming = true;

        [Header("== Whitelist / 白名单 ==")]
        [Tooltip("Objects in this list will skip all optimization. Can be meshes, materials, textures, or animations.")]
        public List<UnityEngine.Object> whitelist = new List<UnityEngine.Object>();

        [Header("== Advanced / 高级选项 ==")]
        [Tooltip("Show advanced quality parameters.")]
        public bool showAdvancedQuality = false;

        public QualityParameters qualityParams = new QualityParameters();

        [Header("== Compression / 压缩格式 ==")]
        public TextureFormatSettings formatSettings = new TextureFormatSettings();

        [Header("== Platform Overrides / 平台覆写 ==")]
        [Tooltip("Enable per-platform parameter overrides.")]
        public bool enablePlatformOverrides = false;

        public PlatformOverrideSettings platformOverrides = new PlatformOverrideSettings();

        [Header("== Debug / 调试 ==")]
        [Tooltip("Enable verbose debug logging.")]
        public bool verboseLogging = false;
    }

    // ========================================================================
    // Enums / 枚举
    // ========================================================================

    public enum QualityPreset
    {
        NearLossless = 0,  // Near-lossless / 近无损
        High = 1,          // High quality / 高质量
        Balanced = 2,      // Balanced (default) / 均衡（默认）
        Performance = 3,   // Performance / 性能优先
        Aggressive = 4,    // Aggressive / 激进
        Custom = 5         // Custom / 自定义
    }

    public enum PixelDensityPreset
    {
        Px512 = 0,
        Px1024 = 1,
        Px2048 = 2,
        Px4096 = 3,
        Px8192 = 4
    }

    public enum AtlasPaddingPreset
    {
        Px4 = 0,
        Px8 = 1,
        Px16 = 2,
        Px32 = 3,
        Px64 = 4
    }

    public enum TargetPlatform
    {
        Auto = 0,     // Auto-detect from build target / 自动检测
        PC = 1,
        Android = 2,
        iOS = 3
    }

    public enum TextureCategory
    {
        Transparent = 0,   // Textures with alpha channel / 带alpha通道的贴图
        Opaque = 1,        // Textures without alpha / 不透明贴图
        Normal = 2,        // Normal maps / 法线贴图
        Grayscale = 3      // Grayscale/mask textures / 灰度/蒙版贴图
    }

    // ========================================================================
    // Settings Data Classes / 设置数据类
    // ========================================================================

    [Serializable]
    public class QualityParameters
    {
        [Tooltip("MS-SSIM threshold (0-1). Higher = more strict.")]
        public float msSsimThreshold = 0.95f;

        [Tooltip("SSIM threshold (0-1). Used for small islands (< 176px bounding box short side).")]
        public float ssimThreshold = 0.95f;

        [Tooltip("CIEDE2000 ΔE threshold.")]
        public float deltaEThreshold = 2.0f;

        [Tooltip("Alpha IoU threshold for cutout materials.")]
        public float alphaIoUThreshold = 0.95f;

        [Tooltip("Alpha RMSE threshold for blend transparent materials.")]
        public float alphaRMSEThreshold = 0.02f;

        [Tooltip("Normal map angle error threshold (degrees).")]
        public float normalAngleErrorThreshold = 5.0f;

        [Tooltip("Normal map P95 angle error threshold (degrees).")]
        public float normalP95AngleErrorThreshold = 10.0f;

        [Tooltip("Grayscale RMSE threshold per channel.")]
        public float grayscaleRMSEThreshold = 0.02f;
    }

    [Serializable]
    public class TextureFormatSettings
    {
        [Tooltip("Compression format for transparent textures.")]
        public TextureCompressionFormat transparentFormat = TextureCompressionFormat.BC7;

        [Tooltip("Compression format for opaque textures.")]
        public TextureCompressionFormat opaqueFormat = TextureCompressionFormat.BC7;

        [Tooltip("Compression format for normal maps.")]
        public TextureCompressionFormat normalFormat = TextureCompressionFormat.BC5;

        [Tooltip("Compression format for grayscale/mask textures.")]
        public TextureCompressionFormat grayscaleFormat = TextureCompressionFormat.BC4;
    }

    public enum TextureCompressionFormat
    {
        Auto = 0,
        BC7 = 1,      // High quality RGBA
        BC5 = 2,      // Two-channel (normal maps)
        BC4 = 3,      // Single channel (grayscale)
        BC1 = 4,      // Low quality, small size (opaque)
        BC3 = 5,      // RGBA with compressed alpha
        DXT1 = 6,     // Same as BC1
        DXT5 = 7,     // Same as BC3
        ASTC_4x4 = 8,
        ASTC_6x6 = 9,
        ASTC_8x8 = 10,
        ASTC_12x12 = 11,
        ETC2_RGB = 12,
        ETC2_RGBA = 13,
        PVRTC_RGB_4BPP = 14,
        PVRTC_RGBA_4BPP = 15,
        RGBA32 = 16,  // Uncompressed
        CrunchedBC7 = 17,
        CrunchedDXT5 = 18
    }

    [Serializable]
    public class PlatformOverrideSettings
    {
        public bool overridePC = false;
        public bool overrideAndroid = false;
        public bool overrideiOS = false;

        public PlatformSpecificSettings pcSettings = new PlatformSpecificSettings();
        public PlatformSpecificSettings androidSettings = new PlatformSpecificSettings();
        public PlatformSpecificSettings iOSSettings = new PlatformSpecificSettings();
    }

    [Serializable]
    public class PlatformSpecificSettings
    {
        public QualityPreset qualityPreset = QualityPreset.Balanced;
        public int maxAtlasSize = 8192;
        public TextureFormatSettings formatSettings = new TextureFormatSettings();
        public bool enableMipStreaming = true;
        public float minPixelDensity = 2048f;
        public float maxPixelDensity = 4096f;
    }
}
