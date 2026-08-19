using System;
using UnityEngine;

namespace AvatarTextureOptimizer
{
    /// <summary>
    /// Quality presets for the target-quality scaling algorithm. / 目标质量缩放算法的质量挡位。
    /// </summary>
    public enum ATOQualityPreset
    {
        /// <summary>Near-lossless, skip scaling. / 近无损，跳过缩放。</summary>
        NearLossless = 0,
        /// <summary>High quality (default). / 高质量（默认）。</summary>
        High = 1,
        /// <summary>Balanced. / 均衡。</summary>
        Balanced = 2,
        /// <summary>Performance. / 性能。</summary>
        Performance = 3,
        /// <summary>Custom, user-defined, not overwritten by other presets. / 自定义，用户定义，不被其他挡位覆盖。</summary>
        Custom = 4,
    }

    /// <summary>
    /// Per-metric quality thresholds used by the target-quality algorithm.
    /// 目标质量算法使用的各项指标阈值。
    /// </summary>
    [Serializable]
    public struct ATOQualityParameters
    {
        /// <summary>MS-SSIM threshold (1 = lossless). / MS-SSIM 阈值（1 = 无损）。</summary>
        [Range(0, 1)] public float msSsim;

        /// <summary>CIEDE2000 ΔE p95 threshold (0 = lossless). / CIEDE2000 ΔE p95 阈值（0 = 无损）。</summary>
        [Range(0, 100)] public float deltaE;

        /// <summary>Cutout alpha outline IoU threshold (1 = lossless). / Cutout alpha 轮廓 IoU 阈值（1 = 无损）。</summary>
        [Range(0, 1)] public float alphaIoU;

        /// <summary>Blend alpha linear RMSE threshold (0 = lossless). / Blend alpha 线性 RMSE 阈值（0 = 无损）。</summary>
        [Range(0, 1)] public float alphaRmse;

        /// <summary>Normal map angular error p95 threshold in degrees (0 = lossless). / 法线贴图角度误差 p95 阈值（度，0 = 无损）。</summary>
        [Range(0, 180)] public float normalAngle;

        /// <summary>Gray texture linear-space RMSE threshold (0 = lossless). / 灰度贴图线性空间 RMSE 阈值（0 = 无损）。</summary>
        [Range(0, 1)] public float grayRmse;
    }

    /// <summary>
    /// Texture compression formats, distinguished by texture category. / 贴图压缩格式（按贴图分类区分）。
    /// </summary>
    public enum ATOCompressionFormat
    {
        /// <summary>Auto (safe default per category). / 自动（按分类的安全默认）。</summary>
        Auto = 0,
        ASTC_4x4 = 1,
        ASTC_6x6 = 2,
        ASTC_8x8 = 3,
        ASTC_12x12 = 4,
        BC7 = 5,
        BC5 = 6,
        BC1 = 7,
        BC4 = 8,
        ETC2 = 9,
        ETC2_A = 10,
        DXT5 = 11,
        DXT1 = 12,
        Uncompressed = 13,
    }

    /// <summary>
    /// The target platform for platform-specific overrides. / 平台 override 的目标平台。
    /// </summary>
    public enum ATOPlatform
    {
        PC = 0,
        Android = 1,
        iOS = 2,
    }

    /// <summary>
    /// Padding (distance between packed islands) options, in pixels. / 图集 padding（岛间距离）挡位，单位像素。
    /// </summary>
    public enum ATOPadding
    {
        P4 = 4,
        P8 = 8,
        P16 = 16,
        P32 = 32,
        P64 = 64,
    }

    /// <summary>
    /// All user-facing settings for one platform. / 单个平台的全部用户设置。
    /// </summary>
    [Serializable]
    public class ATOPlatformSettings
    {
        [Tooltip("Generate atlases from UV islands. / 从 UV 岛生成图集。")]
        public bool generateAtlas = true;

        [Tooltip("Target quality preset. / 目标质量挡位。")]
        public ATOQualityPreset qualityPreset = ATOQualityPreset.High;

        [Tooltip("Custom quality parameters (used only when preset is Custom). / 自定义质量参数（仅挡位为自定义时使用）。")]
        public ATOQualityParameters customQuality = new ATOQualityParameters
        {
            msSsim = 1f, deltaE = 0f, alphaIoU = 1f, alphaRmse = 0f, normalAngle = 0f, grayRmse = 0f,
        };

        [Tooltip("Minimum pixel density in px per meter. / 最小像素密度（px/米）。")]
        public int minPixelDensity = 2048;

        [Tooltip("Maximum pixel density in px per meter. / 最大像素密度（px/米）。")]
        public int maxPixelDensity = 4096;

        [Tooltip("Padding between packed islands. / 装箱岛间距离。")]
        public ATOPadding padding = ATOPadding.P4;

        [Tooltip("Allow non-power-of-two atlas sizes (experimental). / 允许 NPOT 图集（实验性）。")]
        public bool npotAtlas = false;

        [Tooltip("Maximum atlas edge length. / 图集最大边长。")]
        public int maxAtlasSize = 8192;

        [Tooltip("Compression format for opaque textures. / 不透明贴图压缩格式。")]
        public ATOCompressionFormat opaqueFormat = ATOCompressionFormat.Auto;

        [Tooltip("Compression format for transparent textures. / 透明贴图压缩格式。")]
        public ATOCompressionFormat transparentFormat = ATOCompressionFormat.Auto;

        [Tooltip("Compression format for normal maps. / 法线贴图压缩格式。")]
        public ATOCompressionFormat normalFormat = ATOCompressionFormat.Auto;

        [Tooltip("Compression format for gray/mask textures. / 灰度/蒙版贴图压缩格式。")]
        public ATOCompressionFormat grayFormat = ATOCompressionFormat.Auto;

        [Tooltip("Enable mipmaps (bound to MipStreaming as required by VRChat). / 开启 Mipmap（与 MipStreaming 绑定，VRChat 要求）。")]
        public bool mipmaps = true;
    }

    /// <summary>
    /// Main component: optimize textures of the whole avatar. / 主组件：优化整个 Avatar 的贴图。
    /// </summary>
    [AddComponentMenu("Avatar Texture Optimizer/Avatar Texture Optimizer")]
    [DisallowMultipleComponent]
    public sealed class AvatarTextureOptimizer : MonoBehaviour
    {
        [Tooltip("General settings (default platform). / 通用设置（默认平台）。")]
        public ATOPlatformSettings general = new ATOPlatformSettings();

        [Tooltip("Enable per-platform overrides. / 启用分平台 override。")]
        public bool enablePlatformOverride = false;

        [Tooltip("PC platform override. / PC 平台 override。")]
        public ATOPlatformSettings pc = new ATOPlatformSettings();

        [Tooltip("Android platform override. / Android 平台 override。")]
        public ATOPlatformSettings android = new ATOPlatformSettings();

        [Tooltip("iOS platform override. / iOS 平台 override。")]
        public ATOPlatformSettings ios = new ATOPlatformSettings();

        [Tooltip("Preferred UI language; Auto follows NDMF language. / 首选 UI 语言；Auto 跟随 NDMF 语言。")]
        public string language = "Auto";

        /// <summary>
        /// Resolve the active settings for the given platform. / 解析指定平台生效的设置。
        /// </summary>
        public ATOPlatformSettings GetSettings(ATOPlatform platform)
        {
            if (!enablePlatformOverride) return general;
            switch (platform)
            {
                case ATOPlatform.PC: return pc;
                case ATOPlatform.Android: return android;
                case ATOPlatform.iOS: return ios;
                default: return general;
            }
        }
    }
}
