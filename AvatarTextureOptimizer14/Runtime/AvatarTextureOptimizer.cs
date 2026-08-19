// AvatarTextureOptimizer — core component / 核心组件
// Holds every user-facing setting. Pure data (no UnityEditor deps) so it can live on avatars.<br>
// 保存全部用户设置，纯数据（不依赖 UnityEditor），可安全挂在 Avatar 上。
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Fosa.ATO
{
    /// <summary>
    /// Quality preset selector. Custom is user-editable and never overwritten by other presets.<br/>
    /// 质量挡位。Custom 由用户自行修改，不会被其他挡位覆盖。
    /// </summary>
    public enum ATOQualityPreset
    {
        /// <summary>Lossless: no island/texture rescaling at all. 近无损：完全不缩放。</summary>
        Lossless = 0,
        Extreme = 1,
        High = 2,
        /// <summary>Default tier / 默认挡位。</summary>
        Medium = 3,
        Low = 4,
        Potato = 5,
        /// <summary>User defined / 自定义。</summary>
        Custom = 6,
    }

    /// <summary>
    /// Texture content category used for per-category options (mip streaming, compression).<br/>
    /// 贴图内容分类：用于按分类提供 mipmap/压缩等选项。
    /// </summary>
    public enum ATOTextureCategory
    {
        AlbedoWithAlpha = 0, // 透明主色 / transparent albedo
        AlbedoOpaque = 1,    // 不透明主色 / opaque albedo
        Normal = 2,          // 法线 / normal map
        Mask = 3,            // 灰度蒙版 / grayscale mask
    }

    /// <summary>
    /// Safe compression format choices (subset exposed to users; mapped to TextureImporterFormat in editor).<br/>
    /// 提供给用户的安全压缩格式枚举（Editor 内映射到 TextureImporterFormat）。
    /// </summary>
    public enum ATOFormatChoice
    {
        /// <summary>Platform recommended / 平台推荐。</summary>
        Auto = 0,
        DXT1 = 1, DXT5 = 2, BC7 = 3, BC5 = 4, BC4 = 5,
        RGBA32 = 6, RGB32 = 7, R8 = 8,
        ASTC_6x6 = 9, ETC2_RGB4 = 10, ETC2_RGBA8 = 11,
    }

    /// <summary>Build platform bucket / 平台分组。</summary>
    public enum ATOPlatform { PC = 0, Android = 1, IOS = 2 }

    /// <summary>
    /// Numerical quality thresholds for one tier. Evaluated per island; the metric "最差的阈值" wins.<br/>
    /// 单挡位的数值化质量阈值，逐岛评估并取最差判定。
    /// </summary>
    [Serializable]
    public struct ATOQualityThresholds
    {
        [Range(0.9f, 1f)] public float msSsimMin;        // MS-SSIM 下限 (single-scale fallback for small islands / 小岛回退单尺度SSIM)
        public float deltaEMaxP95;                       // CIEDE2000 P95 上限
        [Range(0f, 1f)] public float alphaRmseMax;       // Blend 模式 alpha 线性RMSE上限
        [Range(0f, 1f)] public float cutoutIouMin;       // Cutout clip后轮廓IoU下限
        public float normalAngleMeanDeg;                 // 法线角度误差均值(度)
        public float normalAngleP95Deg;                  // 法线角度误差P95(度)
        [Range(0f, 1f)] public float maskRmseMax;        // 灰度蒙版线性RMSE上限(逐通道取最差)

        /// <summary>Near-lossless defaults for the Custom tier / 自定义挡位默认的近无损参数。</summary>
        public static ATOQualityThresholds NearLossless => new ATOQualityThresholds
        {
            msSsimMin = 0.999f, deltaEMaxP95 = 0.8f, alphaRmseMax = 0.004f, cutoutIouMin = 0.999f,
            normalAngleMeanDeg = 0.25f, normalAngleP95Deg = 0.5f, maskRmseMax = 0.002f,
        };

        // Builtin tiers grounded in SSIM/ΔE literature: SSIM≈0.99 + ΔE≤2 ≈ visually indistinguishable at
        // typical avatar viewing distances; lower tiers trade memory for imperceptible-ish loss.
        // 内置挡位依据：SSIM≈0.99+ΔE≤2 在常见观看距离近似无感；低档以轻微可察觉为代价节省体积。
        public static ATOQualityThresholds ForPreset(ATOQualityPreset p) => p switch
        {
            ATOQualityPreset.Extreme => new ATOQualityThresholds { msSsimMin = 0.998f, deltaEMaxP95 = 1.0f, alphaRmseMax = 0.005f, cutoutIouMin = 0.999f, normalAngleMeanDeg = 0.3f, normalAngleP95Deg = 0.6f, maskRmseMax = 0.003f },
            ATOQualityPreset.High => new ATOQualityThresholds { msSsimMin = 0.995f, deltaEMaxP95 = 2.0f, alphaRmseMax = 0.010f, cutoutIouMin = 0.997f, normalAngleMeanDeg = 0.5f, normalAngleP95Deg = 1.0f, maskRmseMax = 0.006f },
            ATOQualityPreset.Medium => new ATOQualityThresholds { msSsimMin = 0.990f, deltaEMaxP95 = 3.5f, alphaRmseMax = 0.020f, cutoutIouMin = 0.995f, normalAngleMeanDeg = 0.8f, normalAngleP95Deg = 1.5f, maskRmseMax = 0.010f },
            ATOQualityPreset.Low => new ATOQualityThresholds { msSsimMin = 0.980f, deltaEMaxP95 = 6.0f, alphaRmseMax = 0.035f, cutoutIouMin = 0.990f, normalAngleMeanDeg = 1.2f, normalAngleP95Deg = 2.5f, maskRmseMax = 0.020f },
            ATOQualityPreset.Potato => new ATOQualityThresholds { msSsimMin = 0.965f, deltaEMaxP95 = 9.0f, alphaRmseMax = 0.060f, cutoutIouMin = 0.980f, normalAngleMeanDeg = 2.0f, normalAngleP95Deg = 4.0f, maskRmseMax = 0.035f },
            _ => NearLossless, // Lossless/Custom fall back here; Lossless is short-circuited upstream. / Lossless 在上游短路
        };
    }

    /// <summary>
    /// Per-platform override block (mirrors Unity's platform override UX).<br/>
    /// 单平台 override 参数（参考 Unity 自身的 platform override 交互）。
    /// </summary>
    [Serializable]
    public class ATOPlatformOverride
    {
        public bool enabled;                               // 勾选后才显示并生效 / only effective when enabled
        [Tooltip("Max atlas side / 图集最大边长")] public int maxAtlasSize = 8192;
        public ATOFormatChoice albedoAlpha = ATOFormatChoice.Auto;
        public ATOFormatChoice albedoOpaque = ATOFormatChoice.Auto;
        public ATOFormatChoice normal = ATOFormatChoice.Auto;
        public ATOFormatChoice mask = ATOFormatChoice.Auto;

        public static ATOPlatformOverride DefaultMobile() => new ATOPlatformOverride { maxAtlasSize = 4096 };
    }

    /// <summary>Mipmap+MipStreaming binding per category (VRC forces mips⇒streaming). / 每分类 Mipmap+MipStreaming 绑定（VRC 强制二者同开）。</summary>
    [Serializable]
    public class ATOMipSettings
    {
        public bool albedo = true;  // 同时控制 mipmap 开关与 streamingMipmaps / controls both mipmapEnabled and streamingMipmaps
        public bool normal = true;
        public bool mask = true;
    }

    /// <summary>
    /// The single allowed optimizer component per avatar. Must sit on the object that owns VRCAvatarDescriptor.<br/>
    /// 每个 Avatar 仅允许一个；必须挂在带 VRCAvatarDescriptor 的对象上（违规在构建时报错中止）。
    /// </summary>
    [AddComponentMenu("Avatar Texture Optimizer/Avatar Texture Optimizer")]
    [DisallowMultipleComponent]
    [HelpURL("https://github.com/fosa/AvatarTextureOptimizer")]
    public sealed class AvatarTextureOptimizer : MonoBehaviour
    {
        public const int MinAtlasSize = 64;                  // 候选图集最小边长 / min candidate atlas side
        public const int MaxAtlasSizePC = 8192;
        public const int MaxAtlasSizeMobile = 4096;
        public static readonly int[] PaddingOptions = { 4, 8, 16, 32, 64 };
        public static readonly int[] DensityTiers = { 512, 1024, 2048, 4096, 8192 };

        [Header("Main / 主要设置")]
        [Tooltip("Generate atlases; off = whole-texture scaling only / 生成图集；关闭则只做整图缩放")]
        public bool generateAtlas = true;

        public ATOQualityPreset qualityPreset = ATOQualityPreset.Medium;

        [Tooltip("Custom thresholds (only used with Custom preset) / 自定义阈值（仅 Custom 挡位生效）")]
        public ATOQualityThresholds customThresholds = ATOQualityThresholds.NearLossless;

        [Tooltip("Min pixel density px/m / 最小像素密度 px/m")]
        public int minPixelDensity = 2048;
        [Tooltip("Max pixel density px/m / 最大像素密度 px/m")]
        public int maxPixelDensity = 4096;

        [Tooltip("Min atlas padding (4/8/16/32/64) / 图集最小padding")]
        public int minPadding = 4;

        [Tooltip("Experimental NPOT atlas sizes / 实验性 NPOT 图集分辨率")]
        public bool allowNPOT = false;

        [Header("Whitelist / 白名单")]
        [Tooltip("Any referenced textures under these objects skip ALL optimization / 这些对象引用到的全部贴图跳过所有优化")]
        public List<UnityEngine.Object> whitelist = new List<UnityEngine.Object>();

        [Header("Mips & Streaming / Mipmap 与流送")]
        public ATOMipSettings mipSettings = new ATOMipSettings();

        [Header("Dedup / 去重")]
        public bool dedupTextures = true;   // 图集/贴图去重 / dedupe atlases and textures by content+params
        public bool dedupMaterials = true;  // 材质去重 + target identical opaque slot merge / 材质去重并合并可判定相同的槽

        [Header("Platform Overrides / 平台覆盖")]
        public ATOPlatformOverride pcOverride = new ATOPlatformOverride();
        public ATOPlatformOverride androidOverride = ATOPlatformOverride.DefaultMobile();
        public ATOPlatformOverride iosOverride = ATOPlatformOverride.DefaultMobile();

        [Header("Advanced / 高级")]
        [Tooltip("Verbose [ATO] logs / 输出详细调试日志")]
        public bool verboseLogging = false;

        [Tooltip("UI language: Auto follows NDMF / 界面语言：Auto 跟随 NDMF 当前语言")]
        public string languageOverride = "Auto";

        /// <summary>Resolved thresholds for the active preset. / 当前挡位解析后的阈值。</summary>
        public ATOQualityThresholds Thresholds =>
            qualityPreset == ATOQualityPreset.Custom ? customThresholds : ATOQualityThresholds.ForPreset(qualityPreset);

        public ATOPlatformOverride OverrideFor(ATOPlatform p) => p switch
        {
            ATOPlatform.PC => pcOverride,
            ATOPlatform.Android => androidOverride,
            _ => iosOverride,
        };

#if UNITY_EDITOR
        private void OnValidate()
        {
            // Keep values sane without fighting the user. / 温和钳制，避免用户输错。
            minPixelDensity = Mathf.Max(1, minPixelDensity);
            maxPixelDensity = Mathf.Max(minPixelDensity, maxPixelDensity);
            minPadding = Mathf.Max(4, minPadding);
            pcOverride.maxAtlasSize = Mathf.Clamp(pcOverride.maxAtlasSize, MinAtlasSize, MaxAtlasSizePC);
            androidOverride.maxAtlasSize = Mathf.Clamp(androidOverride.maxAtlasSize, MinAtlasSize, MaxAtlasSizeMobile);
            iosOverride.maxAtlasSize = Mathf.Clamp(iosOverride.maxAtlasSize, MinAtlasSize, MaxAtlasSizeMobile);
        }
#endif
    }
}
