// ATOConfig.cs — 全部可配置项与枚举 / All configuration options and enums.
// 说明：本文件为 Runtime 层，禁止引用 UnityEditor，所有枚举均与 Unity 导入器枚举解耦，
// 由 Editor 层负责映射为 TextureImporterFormat 等真实枚举。
// Note: this file lives in the Runtime assembly. It must NOT reference UnityEditor;
// all enums are decoupled from Unity importer enums and are mapped by the Editor layer.

using System;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer
{
    // ============================================================
    // 平台 / Platform
    // ============================================================
    /// <summary>目标平台（参考 Unity platform override）/ Target platform (mirrors Unity's platform override concept).</summary>
    public enum ATOPlatform
    {
        PC = 0,      // 桌面（Windows/Linux）/ Desktop
        Android = 1, // 安卓（Quest）/ Android (Quest)
        iOS = 2,     // iOS
    }

    // ============================================================
    // 质量挡位 / Quality tiers
    // ============================================================
    /// <summary>质量挡位枚举 / Quality tier enum.</summary>
    public enum ATOQualityTier
    {
        Ultra = 0,        // 超高质量 / Ultra
        High = 1,         // 高质量 / High
        Standard = 2,     // 标准（默认）/ Standard (default)
        Performance = 3,  // 性能优先 / Performance
        Custom = 4,       // 自定义（默认近无损，参数不会被其他挡位覆盖）/ Custom (defaults near-lossless; never overwritten by other tiers)
    }

    /// <summary>
    /// 质量挡位参数。所有参数共同构成"全部达标才算通过"的判定。
    /// 参数含义与学术依据：
    ///  - msSsim: 多尺度结构相似度 (Wang et al., 2003, IEEE Asilomar)。短边&lt;176px 回退单尺度 SSIM；短边&lt;11px 忽略该项。
    ///  - deltaEP95: CIEDE2000 色差 (Sharma et al., 2005) 的第 95 百分位阈值。
    ///  - normalAngleP95: 法线贴图角度误差（度）的第 95 百分位阈值。
    ///  - alphaIoU: Cutout 贴图裁剪后轮廓的 IoU 阈值。
    ///  - alphaLinearRmse: Blend 贴图线性空间预乘 alpha 的 RMSE 阈值（0~1）。
    ///  - grayLinearRmse: 灰度贴图逐通道线性空间 RMSE 阈值（0~1），取最差通道。
    /// Quality tier parameters. All parameters must pass together ("全部达标").
    /// Academic references: MS-SSIM (Wang et al. 2003), CIEDE2000 (Sharma et al. 2005),
    /// angular error for normal maps (standard in normal-map compression literature), IoU (segmentation standard).
    /// </summary>
    [System.Serializable]
    public class ATOQualityTierValues
    {
        [Tooltip("MS-SSIM 阈值（0~1，越高越严格）。原尺寸短边<176px 的岛回退单尺度 SSIM，<11px 忽略该项。/ MS-SSIM threshold. Islands with original short side <176px fall back to single-scale SSIM, <11px skip this term.")]
        [Range(0.8f, 1f)] public float msSsim = 0.985f;

        [Tooltip("CIEDE2000 色差 p95 阈值（0~100）。/ CIEDE2000 p95 threshold.")]
        [Min(0f)] public float deltaEP95 = 1.5f;

        [Tooltip("法线贴图角度误差 p95 阈值（度）。/ Normal map angular error p95 threshold in degrees.")]
        [Min(0f)] public float normalAngleP95 = 1.0f;

        [Tooltip("Cutout 轮廓 IoU 阈值（0~1）。/ Cutout silhouette IoU threshold.")]
        [Range(0.5f, 1f)] public float alphaIoU = 0.985f;

        [Tooltip("Blend alpha（线性预乘）RMSE 阈值（0~1）。/ Blend premultiplied-alpha linear RMSE threshold.")]
        [Min(0f)] public float alphaLinearRmse = 0.0118f; // ≈3/255

        [Tooltip("灰度贴图逐通道线性 RMSE 阈值（0~1），取最差通道。/ Grayscale per-channel linear RMSE threshold, worst channel wins.")]
        [Min(0f)] public float grayLinearRmse = 0.0118f;

        /// <summary>是否视为"近无损"（quality==1）。此时跳过该类型贴图的 UV 缩放（含纯色短路），原样拷贝不重采样。
        /// Whether this tier is "near-lossless" (quality==1): skip UV scaling entirely (incl. solid-color short-circuit) and copy pixels without resampling.</summary>
        public bool IsLossless =>
            msSsim >= 1.0f - 1e-6f &&
            deltaEP95 <= 1e-6f &&
            normalAngleP95 <= 1e-6f &&
            alphaIoU >= 1.0f - 1e-6f &&
            alphaLinearRmse <= 1e-6f &&
            grayLinearRmse <= 1e-6f;

        public ATOQualityTierValues Clone() => (ATOQualityTierValues)MemberwiseClone();
    }

    // ============================================================
    // 图集 padding / Atlas padding
    // ============================================================
    /// <summary>最小 padding 挡位（px）。实际 padding = max(用户挡位, ceil(图集最大边长/128))。/ Minimum padding options in px. Effective padding = max(user option, ceil(atlas max side / 128)).</summary>
    public enum ATOMinPadding
    {
        [InspectorName("4 px")] P4 = 4,
        [InspectorName("8 px")] P8 = 8,
        [InspectorName("16 px")] P16 = 16,
        [InspectorName("32 px")] P32 = 32,
        [InspectorName("64 px")] P64 = 64,
    }

    // ============================================================
    // 压缩格式 / Compression formats
    // ============================================================
    /// <summary>
    /// 贴图压缩格式（安全枚举，Editor 层负责映射到 TextureImporterFormat 并按平台/贴图分类过滤不安全的选项）。
    /// Safe compression format enum. The Editor layer maps it to TextureImporterFormat and filters unsafe choices per platform/category.
    /// </summary>
    public enum ATOCompressionFormat
    {
        Auto = 0,          // 自动（跟随项目默认压缩）/ Automatic (project default)
        RGBA32 = 1,        // 未压缩 RGBA32 / Uncompressed RGBA32
        BC1 = 2,           // DXT1/BC1（无 alpha 或 1bit alpha）/ DXT1/BC1
        BC3 = 3,           // DXT5/BC3（有 alpha）/ DXT5/BC3
        BC4 = 4,           // BC4（单通道灰度）/ BC4 (single channel)
        BC5 = 5,           // BC5（双通道，法线贴图推荐）/ BC5 (two channels, recommended for normal maps)
        BC7 = 6,           // BC7（高质量 RGBA）
        ETC2_RGB = 7,      // ETC2 RGB
        ETC2_RGBA = 8,     // ETC2 RGBA
        ASTC_4x4 = 9,      // ASTC 4x4（最高质量 ASTC）
        ASTC_6x6 = 10,     // ASTC 6x6
        ASTC_8x8 = 11,     // ASTC 8x8
        ASTC_12x12 = 12,   // ASTC 12x12（最大压缩）
        PVRTC_RGB4 = 13,   // PVRTC 4bpp RGB（仅 iOS，仅 POT 正方形）
        PVRTC_RGBA4 = 14,  // PVRTC 4bpp RGBA（仅 iOS，仅 POT 正方形）
        R8 = 15,           // R8（单通道）
        RGB24 = 16,        // 未压缩 RGB24 / Uncompressed RGB24
    }

    /// <summary>贴图分类（压缩格式按此分类分别配置）/ Texture category for per-category compression settings.</summary>
    public enum ATOTextureCategory
    {
        Transparent = 0, // 透明贴图（图集含 alpha 通道）/ Transparent (atlas contains alpha)
        Opaque = 1,      // 不透明贴图 / Opaque
        Normal = 2,      // 法线贴图 / Normal map
        Grayscale = 3,   // 灰度贴图（数据/蒙版）/ Grayscale (data/mask)
    }

    /// <summary>
    /// 单平台配置（platform override）。勾选 enabled 后覆盖对应平台的通用设置。
    /// Per-platform override config. When enabled, overrides the global settings for that platform.
    /// </summary>
    [System.Serializable]
    public class ATOPlatformConfig
    {
        [Tooltip("启用该平台覆盖 / Enable this platform override")]
        public bool enabled = false;

        [Tooltip("图集最大边长（2 的幂，64~8192；移动端上限 4096）/ Max atlas side (power of two, 64~8192; mobile max 4096)")]
        [Min(64)] public int atlasMaxSide = 8192;

        [Tooltip("实验性 NPOT 分辨率（已验证支持 MipStreaming 与 Crunch；勾选时会剔除不支持的格式如 iOS 的 PVRTC）/ Experimental NPOT resolution (verified with MipStreaming and Crunch; unsupported formats like PVRTC on iOS are excluded when enabled)")]
        public bool experimentalNPOT = false;

        [Tooltip("透明贴图压缩格式 / Transparent texture compression format")]
        public ATOCompressionFormat transparentFormat = ATOCompressionFormat.Auto;

        [Tooltip("不透明贴图压缩格式 / Opaque texture compression format")]
        public ATOCompressionFormat opaqueFormat = ATOCompressionFormat.Auto;

        [Tooltip("法线贴图压缩格式 / Normal map compression format")]
        public ATOCompressionFormat normalFormat = ATOCompressionFormat.Auto;

        [Tooltip("灰度贴图压缩格式 / Grayscale texture compression format")]
        public ATOCompressionFormat grayscaleFormat = ATOCompressionFormat.Auto;
    }

    // ============================================================
    // 日志详细度 / Log verbosity
    // ============================================================
    /// <summary>日志详细度（高级用户调试开关）/ Log verbosity (debug switch for advanced users).</summary>
    public enum ATOLogVerbosity
    {
        Minimal = 0,  // 仅错误与总结 / Errors and summary only
        Normal = 1,   // 常规信息（默认）/ Normal (default)
        Verbose = 2,  // 每步详细日志 / Verbose per-step logs
    }

    // ============================================================
    // 主配置 / Main configuration
    // ============================================================
    /// <summary>
    /// ATO 主配置。挂在 ATOAvatarTextureOptimizer 组件上，随 Avatar 保存。
    /// Main configuration. Stored on the ATOAvatarTextureOptimizer component and saved with the avatar.
    /// </summary>
    [System.Serializable]
    public class ATOConfig
    {
        [Header("Basic / 基础")]
        [Tooltip("是否生成图集。取消勾选则不生成图集、不剔除未使用 UV、不重排 UV，直接按质量要求缩放整张贴图并执行其他优化。/ Whether to generate atlases. When off: no atlas, no unused-UV trimming, no UV repacking; whole textures are scaled to the quality target and other optimizations still apply.")]
        public bool generateAtlases = true;

        [Tooltip("图集最小 padding 挡位。实际 padding = max(挡位, ceil(图集最大边长/128))。/ Minimum padding option. Effective padding = max(option, ceil(atlas max side / 128)).")]
        public ATOMinPadding minPadding = ATOMinPadding.P4;

        [Tooltip("实验性 NPOT 分辨率（桌面端）。/ Experimental NPOT resolution (desktop).")]
        public bool experimentalNPOT = false;

        [Header("Quality / 质量")]
        [Tooltip("目标质量挡位。/ Target quality tier.")]
        public ATOQualityTier qualityTier = ATOQualityTier.Standard;

        [Tooltip("Ultra 挡位参数。/ Ultra tier parameters.")]
        public ATOQualityTierValues ultra = new ATOQualityTierValues
        {
            msSsim = 0.9985f, deltaEP95 = 0.35f, normalAngleP95 = 0.25f,
            alphaIoU = 0.999f, alphaLinearRmse = 0.0039f, grayLinearRmse = 0.0039f
        };

        [Tooltip("High 挡位参数。/ High tier parameters.")]
        public ATOQualityTierValues high = new ATOQualityTierValues
        {
            msSsim = 0.995f, deltaEP95 = 0.75f, normalAngleP95 = 0.5f,
            alphaIoU = 0.995f, alphaLinearRmse = 0.0078f, grayLinearRmse = 0.0078f
        };

        [Tooltip("Standard 挡位参数（默认）。/ Standard tier parameters (default).")]
        public ATOQualityTierValues standard = new ATOQualityTierValues
        {
            msSsim = 0.985f, deltaEP95 = 1.5f, normalAngleP95 = 1.0f,
            alphaIoU = 0.985f, alphaLinearRmse = 0.0118f, grayLinearRmse = 0.0118f
        };

        [Tooltip("Performance 挡位参数。/ Performance tier parameters.")]
        public ATOQualityTierValues performance = new ATOQualityTierValues
        {
            msSsim = 0.96f, deltaEP95 = 3.0f, normalAngleP95 = 2.0f,
            alphaIoU = 0.95f, alphaLinearRmse = 0.0235f, grayLinearRmse = 0.0235f
        };

        [Tooltip("Custom 挡位参数（默认全部为 1 = 近无损；不会被其他挡位覆盖）。/ Custom tier parameters (defaults all 1 = near-lossless; never overwritten by other tiers).")]
        public ATOQualityTierValues custom = new ATOQualityTierValues
        {
            msSsim = 1.0f, deltaEP95 = 0f, normalAngleP95 = 0f,
            alphaIoU = 1.0f, alphaLinearRmse = 0f, grayLinearRmse = 0f
        };

        [Tooltip("最小像素密度（px/米，下拉挡位 512/1024/2048/4096/8192，默认 2048）。/ Minimum pixel density (px/meter; options 512/1024/2048/4096/8192, default 2048).")]
        public int minPixelDensity = 2048;

        [Tooltip("最大像素密度（px/米，默认 4096）。/ Maximum pixel density (px/meter, default 4096).")]
        public int maxPixelDensity = 4096;

        [Header("Mipmap / MipStreaming")]
        [Tooltip("开启 Mipmap 并强制绑定开启 MipStreaming（VRChat 要求开启 Mipmap 时必须开启 MipStreaming，因此二者绑定为一个开关）。/ Enable mipmaps and force MipStreaming on (VRChat requires MipStreaming when mipmaps are enabled, so both are bound to this single switch).")]
        public bool mipmapAndStreaming = true;

        [Header("Deduplication / 去重")]
        [Tooltip("贴图/图集去重开关（内容与参数完全相同的贴图或图集合并并更新引用）。/ Texture/atlas deduplication switch (identical content & parameters are merged and references updated).")]
        public bool deduplicateTextures = true;

        [Tooltip("材质去重开关（内容与参数完全相同的材质合并；多材质槽网格内相同的不透明材质合并时同步合并材质槽并更新动画等引用）。/ Material deduplication switch (identical materials merged; merging identical opaque materials on multi-slot meshes also merges slots and updates references incl. animations).")]
        public bool deduplicateMaterials = true;

        [Header("Input textures / 输入贴图")]
        [Tooltip("自动临时启用输入贴图的 Read/Write 以读取像素（处理结束后恢复原设置）。关闭后不可读的贴图将被视为白名单跳过优化。/ Temporarily enable Read/Write on input textures to read pixels (restored afterwards). When off, unreadable textures are treated as whitelisted and skipped.")]
        public bool autoEnableReadWrite = true;

        [Header("Platform / 平台")]
        [Tooltip("当前平台（编辑器默认读取当前构建平台；用于决定未覆盖平台时的默认参数）。/ Current platform (preseeded from the active build target; determines defaults when no override is enabled).")]
        public ATOPlatform currentPlatform = ATOPlatform.PC;

        [Tooltip("PC 平台覆盖。/ PC platform override.")]
        public ATOPlatformConfig platformPC = new ATOPlatformConfig();

        [Tooltip("Android 平台覆盖。/ Android platform override.")]
        public ATOPlatformConfig platformAndroid = new ATOPlatformConfig { atlasMaxSide = 4096 };

        [Tooltip("iOS 平台覆盖。/ iOS platform override.")]
        public ATOPlatformConfig platformIOS = new ATOPlatformConfig { atlasMaxSide = 4096 };

        [Header("Logging / 日志")]
        [Tooltip("日志详细度开关（[ATO] 前缀，供高级用户调试）。/ Log verbosity ([ATO] prefix, for advanced debugging).")]
        public ATOLogVerbosity logVerbosity = ATOLogVerbosity.Normal;

        /// <summary>获取当前挡位对应的参数（引用，修改会立即生效并保存到组件）。/ Get the parameter set for the current tier (returns a reference; edits apply immediately).</summary>
        public ATOQualityTierValues GetTierValues(ATOQualityTier tier)
        {
            switch (tier)
            {
                case ATOQualityTier.Ultra: return ultra;
                case ATOQualityTier.High: return high;
                case ATOQualityTier.Performance: return performance;
                case ATOQualityTier.Custom: return custom;
                default: return standard;
            }
        }

        /// <summary>获取指定平台的配置。/ Get the config for a platform.</summary>
        public ATOPlatformConfig GetPlatformConfig(ATOPlatform platform)
        {
            switch (platform)
            {
                case ATOPlatform.Android: return platformAndroid;
                case ATOPlatform.iOS: return platformIOS;
                default: return platformPC;
            }
        }

        /// <summary>
        /// 解析生效的平台配置：若该平台 override 启用则使用其值，否则使用通用值（currentPlatform 的配置作为通用基准）。
        /// Resolve effective platform config: use the override when enabled, otherwise the general defaults (currentPlatform's config as baseline).
        /// </summary>
        public ATOPlatformConfig ResolvePlatformConfig(ATOPlatform platform)
        {
            var cfg = GetPlatformConfig(platform);
            if (cfg.enabled) return cfg;
            var base0 = GetPlatformConfig(currentPlatform);
            var fallback = base0.enabled ? base0 : new ATOPlatformConfig();
            return fallback;
        }
    }

}
