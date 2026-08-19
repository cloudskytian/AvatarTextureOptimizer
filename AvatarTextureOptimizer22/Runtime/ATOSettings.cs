// AvatarTextureOptimizer
// File: ATOSettings.cs
//
// All serialized settings of the tool. These classes live in the Runtime
// assembly so they can be serialized on the component, but they are pure data —
// no logic beyond validation lives here.
//
// 工具的全部序列化设置。这些类位于 Runtime 程序集以便在组件上序列化，
// 但它们是纯数据——除校验外不含任何逻辑。

using System;
using System.Collections.Generic;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer
{
    // ========================================================================
    // Quality / 质量设置
    // ========================================================================

    /// <summary>
    /// Preset quality tiers. Default thresholds are based on academic / industry
    /// research: CIEDE2000 JND ≈ 2.3 (Sharma 2005); MS-SSIM ≥ 0.99 is commonly
    /// treated as visually lossless; normal-map angular error below ~2° is
    /// imperceptible for most assets.
    ///
    /// 预设质量挡位。默认阈值参考学术界/业界研究：CIEDE2000 的 JND≈2.3
    /// (Sharma 2005)；MS-SSIM≥0.99 通常视为视觉无损；法线贴图角度误差
    /// 低于约 2° 时对大多数资产不可感知。
    /// </summary>
    public enum QualityTier
    {
        [InspectorName("Ultra (Near-Lossless) / 极致（近无损）")]
        Ultra = 0,
        [InspectorName("High / 高")]
        High = 1,
        [InspectorName("Medium / 中")]
        Medium = 2,
        [InspectorName("Low / 低")]
        Low = 3,
        [InspectorName("Custom / 自定义")]
        Custom = 4,
    }

    /// <summary>
    /// Concrete numeric thresholds for one quality tier.
    /// 单个质量挡位的具体数值阈值。
    /// </summary>
    [Serializable]
    public class QualityThresholds
    {
        [Header("Target Quality / 目标质量")]
        [Range(0f, 1f)]
        [Tooltip("Overall target quality (0~1). 1 = near-lossless: the UV islands are NOT scaled, textures are copied without resampling. Values below 1 enable scaling until every metric below passes. / 总体目标质量（0~1）。1=近无损：不缩放 UV 岛、不重采样直接拷贝。小于 1 时启用缩放，直到下面所有指标全部达标。")]
        public float TargetQuality = 1f;

        [Header("Metrics / 指标")]
        [Range(0.5f, 1f)]
        [Tooltip("Minimum MS-SSIM of the resampled island compared against the original (linear space). / 重采样岛相对原图（线性空间）的最低 MS-SSIM。")]
        public float MinMsSsim = 0.99f;

        [Range(0f, 20f)]
        [Tooltip("Maximum CIEDE2000 color difference. / 最大 CIEDE2000 色差。")]
        public float MaxDeltaE = 2f;

        [Range(0f, 0.5f)]
        [Tooltip("Maximum normalized linear RMSE on alpha (Blend mode). / 透明（Blend 模式）最大归一化线性 RMSE。")]
        public float MaxAlphaRmse = 0.008f;

        [Range(0.5f, 1f)]
        [Tooltip("Minimum contour IoU of the alpha clip after Cutout thresholding. / Cutout 模式 clip 后轮廓 IoU 的最低值。")]
        public float MinCutoutIoU = 0.998f;

        [Range(0f, 90f)]
        [Tooltip("Maximum normal-map angular error in degrees (after correct decode/resample/re-encode). / 法线贴图最大角度误差（度，正确解码重采样重归一化编码后）。")]
        public float MaxNormalAngleDeg = 2f;

        [Range(0f, 0.5f)]
        [Tooltip("Maximum linear-space RMSE on used channels only for grayscale textures (worst channel). / 灰度贴图仅在被使用的通道上、线性空间 RMSE（逐通道取最差）。")]
        public float MaxGrayRmse = 0.008f;

        [Tooltip("Solid-color islands are scaled directly to minimum size when TargetQuality < 1. / 目标质量不为 1 时，纯色岛直接缩到最小尺寸。")]
        public bool SolidColorShortcut = true;

        /// <summary>Clone / 克隆。</summary>
        public QualityThresholds Clone()
        {
            return (QualityThresholds)MemberwiseClone();
        }
    }

    /// <summary>
    /// Quality configuration with preset tiers and a user-editable custom tier.
    /// 带预设挡位与用户可编辑自定义挡位的质量配置。
    /// </summary>
    [Serializable]
    public class QualitySettings
    {
        [Tooltip("Quality tier. Changing it rewrites the concrete thresholds below. Custom never gets overwritten. / 质量挡位。切换挡位会重写下方具体阈值；自定义挡位不会被覆盖。")]
        public QualityTier Tier = QualityTier.High;

        [Tooltip("Concrete thresholds of the selected tier (read-only; editable when Custom is selected). / 当前挡位的具体阈值（只读；选择 Custom 后可编辑）。")]
        public QualityThresholds Thresholds = DefaultThresholds(QualityTier.High);

        [Tooltip("Minimum pixel density in px/m. Islands whose density is below this are upscaled. / 最小像素密度（px/m）。低于此值的岛会被放大。")]
        public float MinPixelsPerMeter = 2048f;

        [Tooltip("Maximum pixel density in px/m. Islands whose density is above this are downscaled to save memory. / 最大像素密度（px/m）。高于此值的岛会被缩小以节省内存。")]
        public float MaxPixelsPerMeter = 4096f;

        [Tooltip("Density presets offered in the UI. / UI 中提供的像素密度挡位。")]
        public int[] DensityPresets = { 512, 1024, 2048, 4096, 8192 };

        /// <summary>
        /// Applies a tier preset to Thresholds. Custom is never touched.
        /// 将挡位预设应用到 Thresholds。Custom 永不被覆盖。
        /// </summary>
        public void ApplyTier(QualityTier tier)
        {
            if (tier == QualityTier.Custom) return; // Custom 参数由用户自己修改，不被其他挡位覆盖
            Tier = tier;
            Thresholds = DefaultThresholds(tier);
        }

        /// <summary>
        /// Factory of tier presets.
        /// 挡位预设工厂。
        /// </summary>
        public static QualityThresholds DefaultThresholds(QualityTier tier)
        {
            var t = new QualityThresholds();
            switch (tier)
            {
                case QualityTier.Ultra:
                    t.TargetQuality = 0.995f; t.MinMsSsim = 0.995f; t.MaxDeltaE = 1.0f;
                    t.MaxAlphaRmse = 0.004f; t.MinCutoutIoU = 0.999f; t.MaxNormalAngleDeg = 1.0f;
                    t.MaxGrayRmse = 0.004f;
                    break;
                case QualityTier.High:
                    t.TargetQuality = 0.99f; t.MinMsSsim = 0.99f; t.MaxDeltaE = 2.0f;
                    t.MaxAlphaRmse = 0.008f; t.MinCutoutIoU = 0.998f; t.MaxNormalAngleDeg = 2.0f;
                    t.MaxGrayRmse = 0.008f;
                    break;
                case QualityTier.Medium:
                    t.TargetQuality = 0.97f; t.MinMsSsim = 0.98f; t.MaxDeltaE = 3.0f;
                    t.MaxAlphaRmse = 0.016f; t.MinCutoutIoU = 0.995f; t.MaxNormalAngleDeg = 4.0f;
                    t.MaxGrayRmse = 0.016f;
                    break;
                case QualityTier.Low:
                    t.TargetQuality = 0.95f; t.MinMsSsim = 0.96f; t.MaxDeltaE = 5.0f;
                    t.MaxAlphaRmse = 0.03f; t.MinCutoutIoU = 0.99f; t.MaxNormalAngleDeg = 8.0f;
                    t.MaxGrayRmse = 0.03f;
                    break;
                case QualityTier.Custom:
                default:
                    // Custom 默认全部为 1（近无损），参数由用户自己修改
                    t.TargetQuality = 1f; t.MinMsSsim = 1f; t.MaxDeltaE = 0f;
                    t.MaxAlphaRmse = 0f; t.MinCutoutIoU = 1f; t.MaxNormalAngleDeg = 0f;
                    t.MaxGrayRmse = 0f;
                    break;
            }
            return t;
        }
    }

    // ========================================================================
    // Atlas / 图集设置
    // ========================================================================

    [Serializable]
    public class AtlasSettings
    {
        [Tooltip("Minimum padding between islands (px). / 图集岛间最小 padding（像素）。")]
        public int MinPadding = 4;

        [Tooltip("Allowed padding options in the UI. / UI 中允许的 padding 选项。")]
        public int[] PaddingOptions = { 4, 8, 16, 32, 64 };

        [Tooltip("Experimental: enable non-power-of-two atlas sizes (64px step). Disables incompatible compressed formats (e.g. PVRTC on iOS). / 实验性：启用非 2 的幂图集尺寸（64px 步进）。会剔除不支持的压缩格式（如 iOS 的 PVRTC）。")]
        public bool EnableNPOT = false;

        [Tooltip("Minimum atlas side length (px). / 图集最小边长（像素）。")]
        public int MinSize = 64;

        [Tooltip("Maximum atlas side length on PC (px). / PC 上图集最大边长（像素）。")]
        public int MaxSizePC = 8192;

        [Tooltip("Maximum atlas side length on mobile (px). / 移动端图集最大边长（像素）。")]
        public int MaxSizeMobile = 4096;

        [Tooltip("Fill empty atlas regions by GPU pull-push (infinite extrapolation) of island edge colors. Alpha stays 0 for transparent textures. / 对图集空白区域做 GPU pull-push（无限外扩）边缘填充。透明贴图 alpha 保持 0。")]
        public bool PullPushFill = true;

        [Tooltip("Maximum number of candidate atlas sizes generated per pool. / 每个候选图集池生成的最大候选数量。")]
        public int MaxCandidates = 12;

        [Tooltip("Rasterization granularity for the packer (px). / 装箱器光栅化粒度（像素）。")]
        public int RasterGranularity = 4;

        /// <summary>Compute the padding for a given atlas size: ceil(maxSide/128), clamped to >= MinPadding. / 计算给定图集尺寸的 padding：ceil(最大边长/128)，向下钳制到 MinPadding。</summary>
        public int ComputePadding(int atlasSize)
        {
            int auto = Mathf.CeilToInt(atlasSize / 128f);
            return Mathf.Max(MinPadding, auto);
        }

        /// <summary>Max atlas size for the given platform (mobile caps at 4096). / 给定平台的最大图集尺寸（移动端上限 4096）。</summary>
        public int MaxSizeFor(ATOTargetPlatform platform)
        {
            return platform == ATOTargetPlatform.PC ? MaxSizePC : MaxSizeMobile;
        }
    }

    // ========================================================================
    // Import / 导入参数设置
    // ========================================================================

    /// <summary>
    /// Safe compression format enum. Only formats valid on the target platform
    /// are offered; the actual mapping to Unity's TextureFormat is resolved at
    /// build time with a safe fallback based on pixel content.
    /// 安全压缩格式枚举。只提供目标平台合法的格式；到 Unity TextureFormat 的
    /// 实际映射在构建时完成，并根据像素内容做安全兜底。
    /// </summary>
    public enum ATOCompressionFormat
    {
        [InspectorName("Auto (best for platform) / 自动（平台最优）")]
        Auto = 0,
        [InspectorName("DXT1 (RGBA unsupported) / DXT1（不支持透明）")]
        DXT1 = 1,
        [InspectorName("DXT5 / DXT5")]
        DXT5 = 2,
        [InspectorName("BC7 / BC7")]
        BC7 = 3,
        [InspectorName("ETC2 RGB / ETC2 RGB")]
        ETC2_RGB = 4,
        [InspectorName("ETC2 RGBA / ETC2 RGBA")]
        ETC2_RGBA = 5,
        [InspectorName("ASTC 4x4 / ASTC 4x4")]
        ASTC_4x4 = 6,
        [InspectorName("ASTC 6x6 / ASTC 6x6")]
        ASTC_6x6 = 7,
        [InspectorName("ASTC 8x8 / ASTC 8x8")]
        ASTC_8x8 = 8,
        [InspectorName("RGBA32 (uncompressed) / RGBA32（不压缩）")]
        RGBA32 = 9,
        [InspectorName("RGB24 (uncompressed) / RGB24（不压缩）")]
        RGB24 = 10,
    }

    /// <summary>
    /// Which texture category an import setting applies to.
    /// 导入参数应用于哪张贴图类别。
    /// </summary>
    public enum ATOImportCategory
    {
        Transparent = 0,   // 有 alpha 通道的图集/贴图
        Opaque = 1,        // 无 alpha 通道
        NormalMap = 2,     // 法线贴图
        Grayscale = 3,     // 灰度贴图（蒙版等）
    }

    /// <summary>
    /// Per-category import settings.
    /// 按类别的导入设置。
    /// </summary>
    [Serializable]
    public class ImportCategorySettings
    {
        [Tooltip("Compression format for this category. / 该类别的压缩格式。")]
        public ATOCompressionFormat Compression = ATOCompressionFormat.Auto;

        [Tooltip("Compression quality. / 压缩质量。")]
        public TextureCompressionQuality CompressionQuality = TextureCompressionQuality.Normal;

        [Tooltip("Enable Mipmap. When ON, MipStreaming is forced ON (VRChat requirement). When OFF, MipStreaming is forced OFF. / 是否开启 Mipmap。开启时强制开启 MipStreaming；关闭时强制关闭 MipStreaming（VRChat 要求二者绑定）。")]
        public bool EnableMipmap = true;

        [Tooltip("Maximum texture size. / 最大贴图尺寸。")]
        public int MaxTextureSize = 8192;

        [Tooltip("Use Crunch compression (where supported). / 是否使用 Crunch 压缩（受支持时）。")]
        public bool UseCrunchCompression = false;
    }

    [Serializable]
    public class ImportSettings
    {
        [Tooltip("Import settings for transparent atlases/textures (with alpha channel). / 透明贴图/图集（含 alpha 通道）的导入参数。")]
        public ImportCategorySettings Transparent = new ImportCategorySettings();

        [Tooltip("Import settings for opaque atlases/textures (no alpha channel). / 不透明贴图/图集（无 alpha 通道）的导入参数。")]
        public ImportCategorySettings Opaque = new ImportCategorySettings();

        [Tooltip("Import settings for normal maps. / 法线贴图的导入参数。")]
        public ImportCategorySettings NormalMap = new ImportCategorySettings();

        [Tooltip("Import settings for grayscale textures (masks etc.). / 灰度贴图（蒙版等）的导入参数。")]
        public ImportCategorySettings Grayscale = new ImportCategorySettings();

        // ---- Forced / locked options (not user-editable) ----
        // ---- 强制选项（不允许用户修改） ----
        [Tooltip("(Locked) Read/Write is always disabled for generated atlases. / （锁定）生成的图集始终关闭 Read/Write。")]
        public bool ReadWrite = false;

        [Tooltip("(Locked) Wrap mode is always Clamp. / （锁定）包裹模式始终为 Clamp。")]
        public TextureWrapMode WrapMode = TextureWrapMode.Clamp;

        [Tooltip("Filter mode of generated textures: taken from the highest among all source textures. / 生成贴图的 FilterMode：取所有源贴图中质量最高的。")]
        public FilterMode FilterMode = FilterMode.Bilinear;

        /// <summary>Get the settings for a category. / 获取某个类别的设置。</summary>
        public ImportCategorySettings For(ATOImportCategory cat)
        {
            switch (cat)
            {
                case ATOImportCategory.Transparent: return Transparent;
                case ATOImportCategory.Opaque: return Opaque;
                case ATOImportCategory.NormalMap: return NormalMap;
                default: return Grayscale;
            }
        }
    }

    // ========================================================================
    // Platform / 平台设置
    // ========================================================================

    /// <summary>
    /// Target platforms for override purposes. Mirrors Unity's own platform
    /// override concept (PC / Android / iOS).
    /// 用于覆写目的的目标平台。参考 Unity 自身的 platform override（PC/Android/iOS）。
    /// </summary>
    public enum ATOTargetPlatform
    {
        PC = 0,
        Android = 1,
        iOS = 2,
    }

    /// <summary>
    /// Platform-specific override. When enabled, the override settings are used
    /// for platform-constrained parameters (compression format, max size, ...).
    /// 平台特定覆写。启用后，受平台限制的参数（压缩格式、最大尺寸等）使用覆写值。
    /// </summary>
    [Serializable]
    public class PlatformOverride
    {
        public ATOTargetPlatform Platform = ATOTargetPlatform.PC;

        [Tooltip("Enable this platform's override. / 是否启用该平台的覆写。")]
        public bool Enabled = false;

        [Tooltip("Compression format override. / 压缩格式覆写。")]
        public ATOCompressionFormat Compression = ATOCompressionFormat.Auto;

        [Tooltip("Maximum atlas/texture size override. / 最大图集/贴图尺寸覆写。")]
        public int MaxTextureSize = 8192;

        [Tooltip("Max texture size on mobile for the given platform. / 该平台移动端最大贴图尺寸。")]
        public int MaxAtlasSizeMobile = 4096;
    }

    [Serializable]
    public class PlatformSettings
    {
        [Tooltip("Per-platform overrides. Only visible when the corresponding override is enabled. / 各平台覆写。勾选对应平台后才显示。")]
        public List<PlatformOverride> Overrides = new List<PlatformOverride>
        {
            new PlatformOverride { Platform = ATOTargetPlatform.PC },
            new PlatformOverride { Platform = ATOTargetPlatform.Android },
            new PlatformOverride { Platform = ATOTargetPlatform.iOS },
        };

        /// <summary>Get the override (or a default) for a platform. / 获取某平台的覆写（不存在时返回默认）。</summary>
        public PlatformOverride Get(ATOTargetPlatform platform)
        {
            foreach (var o in Overrides)
                if (o.Platform == platform) return o;
            return new PlatformOverride { Platform = platform };
        }
    }

    // ========================================================================
    // Whitelist / 白名单
    // ========================================================================

    [Serializable]
    public class WhitelistSettings
    {
        [Tooltip("Whitelisted objects. The whitelist does NOT restrict object types: meshes, materials, textures, animations, renderers and arbitrary GameObjects are all allowed. All textures referenced inside whitelisted objects skip ALL optimization (including later parameter optimization). Other textures sharing the same UV skip atlasization but still take part in whole-texture scaling and import-parameter optimization. / 白名单对象。白名单不限制对象类型：网格、材质、贴图、动画、渲染器及任意 GameObject 均可。白名单对象内引用的全部贴图跳过所有优化（含后续的参数优化）。同 UV 的其他贴图跳过图集化，但仍参与整图缩放与导入参数优化。")]
        public List<UnityEngine.Object> Objects = new List<UnityEngine.Object>();
    }
}
