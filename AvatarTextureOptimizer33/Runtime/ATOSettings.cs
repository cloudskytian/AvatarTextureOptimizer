// SPDX-License-Identifier: MIT
// Avatar Texture Optimizer (ATO)
// EN: Serializable settings model shared by the runtime component and the editor pipeline.
// ZH: 运行时组件与编辑器管线共用的可序列化设置模型。

using System;
using System.Collections.Generic;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer
{
    /// <summary>
    /// EN: Quality tier presets. Values are derived from published perceptual-metric studies
    ///     (CIEDE2000 JND ~= 1.0, MS-SSIM >= 0.99 is usually called "visually lossless").
    /// ZH: 质量挡位预设。参数取自公开的感知度量研究（CIEDE2000 的 JND 约为 1.0，
    ///     MS-SSIM >= 0.99 通常被视为“视觉无损”）。
    /// </summary>
    public enum ATOQualityTier
    {
        /// <summary>EN: Near lossless, no island rescaling at all. ZH: 近无损，完全不缩放 UV 岛。</summary>
        Lossless = 0,

        /// <summary>EN: Visually lossless. ZH: 视觉无损。</summary>
        VeryHigh = 1,

        /// <summary>EN: High quality (default). ZH: 高质量（默认）。</summary>
        High = 2,

        /// <summary>EN: Balanced quality / size. ZH: 质量与体积平衡。</summary>
        Balanced = 3,

        /// <summary>EN: Performance oriented. ZH: 偏性能。</summary>
        Performance = 4,

        /// <summary>EN: Aggressive, for quest / mobile. ZH: 激进，适合 Quest / 移动端。</summary>
        Aggressive = 5,

        /// <summary>EN: User defined, never overwritten by tier changes. ZH: 用户自定义，不会被挡位覆盖。</summary>
        Custom = 6,
    }

    /// <summary>
    /// EN: Texture "role" as understood by the shader analyser. Every role is evaluated with its own metric set.
    /// ZH: 着色器分析器识别出的贴图“角色”。每种角色使用各自的质量度量集合。
    /// </summary>
    public enum ATOTextureRole
    {
        /// <summary>EN: Colour texture without alpha usage. ZH: 不使用 alpha 的颜色贴图。</summary>
        ColorOpaque = 0,

        /// <summary>EN: Colour texture whose alpha matters. ZH: alpha 有意义的颜色贴图。</summary>
        ColorTransparent = 1,

        /// <summary>EN: Tangent space normal map. ZH: 切线空间法线贴图。</summary>
        Normal = 2,

        /// <summary>EN: Mask / data texture (per channel grayscale). ZH: 蒙版/数据贴图（逐通道灰度）。</summary>
        Grayscale = 3,
    }

    /// <summary>
    /// EN: Alpha blending mode of a material that references a texture.
    /// ZH: 引用贴图的材质的 alpha 混合模式。
    /// </summary>
    public enum ATOAlphaMode
    {
        Opaque = 0,
        Cutout = 1,
        Blend = 2,
    }

    /// <summary>
    /// EN: Target platform for parameter overrides (mirrors Unity's platform override UI).
    /// ZH: 参数覆盖用的目标平台（对应 Unity 自身的 platform override 界面）。
    /// </summary>
    public enum ATOPlatform
    {
        PC = 0,
        Android = 1,
        iOS = 2,
    }

    /// <summary>
    /// EN: Safe compression choices for colour textures/atlases that contain alpha.
    /// ZH: 含 alpha 的颜色贴图/图集的安全压缩格式枚举。
    /// </summary>
    public enum ATOFormatColorAlpha
    {
        Automatic = 0,
        DXT5 = 1,
        BC7 = 2,
        ASTC_4x4 = 3,
        ASTC_5x5 = 4,
        ASTC_6x6 = 5,
        ASTC_8x8 = 6,
        Uncompressed_RGBA32 = 7,
    }

    /// <summary>
    /// EN: Safe compression choices for colour textures/atlases without alpha.
    /// ZH: 不含 alpha 的颜色贴图/图集的安全压缩格式枚举。
    /// </summary>
    public enum ATOFormatColorOpaque
    {
        Automatic = 0,
        DXT1 = 1,
        BC7 = 2,
        ASTC_4x4 = 3,
        ASTC_5x5 = 4,
        ASTC_6x6 = 5,
        ASTC_8x8 = 6,
        Uncompressed_RGB24 = 7,
    }

    /// <summary>
    /// EN: Safe compression choices for normal maps.
    /// ZH: 法线贴图的安全压缩格式枚举。
    /// </summary>
    public enum ATOFormatNormal
    {
        Automatic = 0,
        DXT5nm = 1,
        BC5 = 2,
        BC7 = 3,
        ASTC_4x4 = 4,
        ASTC_5x5 = 5,
        ASTC_6x6 = 6,
        Uncompressed_RGBA32 = 7,
    }

    /// <summary>
    /// EN: Safe compression choices for mask / grayscale textures.
    /// ZH: 蒙版/灰度贴图的安全压缩格式枚举。
    /// </summary>
    public enum ATOFormatGrayscale
    {
        Automatic = 0,
        BC4 = 1,
        BC7 = 2,
        DXT1 = 3,
        DXT5 = 4,
        ASTC_4x4 = 5,
        ASTC_6x6 = 6,
        Uncompressed_R8 = 7,
        Uncompressed_RGBA32 = 8,
    }

    /// <summary>
    /// EN: Numeric thresholds used by the target quality algorithm. One instance per texture-role family.
    /// ZH: 目标质量算法使用的阈值集合，每个贴图角色族各一份。
    /// </summary>
    [Serializable]
    public class ATOQualityParameters
    {
        [Tooltip("EN: Minimum MS-SSIM (or single scale SSIM for small islands). ZH: 最小 MS-SSIM（小岛回退到单尺度 SSIM）。")]
        [Range(0.5f, 1f)] public float minStructuralSimilarity = 0.99f;

        [Tooltip("EN: Maximum mean CIEDE2000 colour difference. ZH: CIEDE2000 平均色差上限。")]
        [Range(0f, 20f)] public float maxDeltaE2000Mean = 2.0f;

        [Tooltip("EN: Maximum 95th percentile CIEDE2000 colour difference. ZH: CIEDE2000 p95 色差上限。")]
        [Range(0f, 40f)] public float maxDeltaE2000P95 = 4.0f;

        [Tooltip("EN: Minimum alpha silhouette IoU for cutout materials. ZH: Cutout 材质的 alpha 轮廓 IoU 下限。")]
        [Range(0.5f, 1f)] public float minAlphaIoU = 0.99f;

        [Tooltip("EN: Maximum linear alpha RMSE for blended materials. ZH: 混合材质的线性 alpha RMSE 上限。")]
        [Range(0f, 0.5f)] public float maxAlphaRmse = 0.02f;

        [Tooltip("EN: Maximum mean normal angular error (degrees). ZH: 法线平均角度误差上限（度）。")]
        [Range(0f, 45f)] public float maxNormalAngleMeanDeg = 2.0f;

        [Tooltip("EN: Maximum 95th percentile normal angular error (degrees). ZH: 法线 p95 角度误差上限（度）。")]
        [Range(0f, 60f)] public float maxNormalAngleP95Deg = 5.0f;

        [Tooltip("EN: Maximum per-channel linear RMSE for grayscale/mask textures. ZH: 灰度/蒙版贴图逐通道线性 RMSE 上限。")]
        [Range(0f, 0.5f)] public float maxGrayscaleRmse = 0.02f;

        [Tooltip("EN: Minimum texel density in pixels per meter. ZH: 最小像素密度（像素/米）。")]
        public int minPixelDensity = 2048;

        [Tooltip("EN: Maximum texel density in pixels per meter. ZH: 最大像素密度（像素/米）。")]
        public int maxPixelDensity = 4096;

        /// <summary>EN: Deep copy. ZH: 深拷贝。</summary>
        public ATOQualityParameters Clone()
        {
            return (ATOQualityParameters)MemberwiseClone();
        }

        /// <summary>
        /// EN: Returns the built-in parameter set for a tier. Custom returns near-lossless defaults.
        /// ZH: 返回某个挡位的内置参数集合。Custom 返回近无损默认值。
        /// </summary>
        public static ATOQualityParameters ForTier(ATOQualityTier tier)
        {
            var p = new ATOQualityParameters();
            switch (tier)
            {
                case ATOQualityTier.Lossless:
                case ATOQualityTier.Custom:
                    p.minStructuralSimilarity = 1.0f;
                    p.maxDeltaE2000Mean = 0.0f;
                    p.maxDeltaE2000P95 = 0.0f;
                    p.minAlphaIoU = 1.0f;
                    p.maxAlphaRmse = 0.0f;
                    p.maxNormalAngleMeanDeg = 0.0f;
                    p.maxNormalAngleP95Deg = 0.0f;
                    p.maxGrayscaleRmse = 0.0f;
                    p.minPixelDensity = 2048;
                    p.maxPixelDensity = 4096;
                    break;
                case ATOQualityTier.VeryHigh:
                    p.minStructuralSimilarity = 0.995f;
                    p.maxDeltaE2000Mean = 1.0f;
                    p.maxDeltaE2000P95 = 2.0f;
                    p.minAlphaIoU = 0.995f;
                    p.maxAlphaRmse = 0.01f;
                    p.maxNormalAngleMeanDeg = 1.0f;
                    p.maxNormalAngleP95Deg = 2.5f;
                    p.maxGrayscaleRmse = 0.01f;
                    p.minPixelDensity = 2048;
                    p.maxPixelDensity = 8192;
                    break;
                case ATOQualityTier.High:
                    p.minStructuralSimilarity = 0.99f;
                    p.maxDeltaE2000Mean = 2.0f;
                    p.maxDeltaE2000P95 = 4.0f;
                    p.minAlphaIoU = 0.99f;
                    p.maxAlphaRmse = 0.02f;
                    p.maxNormalAngleMeanDeg = 2.0f;
                    p.maxNormalAngleP95Deg = 5.0f;
                    p.maxGrayscaleRmse = 0.02f;
                    p.minPixelDensity = 2048;
                    p.maxPixelDensity = 4096;
                    break;
                case ATOQualityTier.Balanced:
                    p.minStructuralSimilarity = 0.98f;
                    p.maxDeltaE2000Mean = 3.0f;
                    p.maxDeltaE2000P95 = 6.0f;
                    p.minAlphaIoU = 0.98f;
                    p.maxAlphaRmse = 0.03f;
                    p.maxNormalAngleMeanDeg = 3.0f;
                    p.maxNormalAngleP95Deg = 7.0f;
                    p.maxGrayscaleRmse = 0.03f;
                    p.minPixelDensity = 1024;
                    p.maxPixelDensity = 4096;
                    break;
                case ATOQualityTier.Performance:
                    p.minStructuralSimilarity = 0.96f;
                    p.maxDeltaE2000Mean = 5.0f;
                    p.maxDeltaE2000P95 = 10.0f;
                    p.minAlphaIoU = 0.96f;
                    p.maxAlphaRmse = 0.05f;
                    p.maxNormalAngleMeanDeg = 5.0f;
                    p.maxNormalAngleP95Deg = 12.0f;
                    p.maxGrayscaleRmse = 0.05f;
                    p.minPixelDensity = 1024;
                    p.maxPixelDensity = 2048;
                    break;
                case ATOQualityTier.Aggressive:
                    p.minStructuralSimilarity = 0.93f;
                    p.maxDeltaE2000Mean = 8.0f;
                    p.maxDeltaE2000P95 = 16.0f;
                    p.minAlphaIoU = 0.93f;
                    p.maxAlphaRmse = 0.08f;
                    p.maxNormalAngleMeanDeg = 8.0f;
                    p.maxNormalAngleP95Deg = 18.0f;
                    p.maxGrayscaleRmse = 0.08f;
                    p.minPixelDensity = 512;
                    p.maxPixelDensity = 2048;
                    break;
            }

            return p;
        }
    }

    /// <summary>
    /// EN: Output-side (texture import / compression) settings for one platform.
    /// ZH: 某个平台的输出侧（贴图导入/压缩）设置。
    /// </summary>
    [Serializable]
    public class ATOPlatformProfile
    {
        [Tooltip("EN: When false this platform inherits the shared settings. ZH: 未勾选时该平台沿用通用设置。")]
        public bool enabled;

        public ATOPlatform platform = ATOPlatform.PC;

        [Tooltip("EN: Maximum atlas edge length. ZH: 图集最大边长。")]
        public int maxAtlasSize = 8192;

        public ATOFormatColorAlpha formatColorAlpha = ATOFormatColorAlpha.Automatic;
        public ATOFormatColorOpaque formatColorOpaque = ATOFormatColorOpaque.Automatic;
        public ATOFormatNormal formatNormal = ATOFormatNormal.Automatic;
        public ATOFormatGrayscale formatGrayscale = ATOFormatGrayscale.Automatic;

        [Tooltip("EN: Mipmaps + mip streaming for colour textures (VRChat couples them). ZH: 颜色贴图的 Mipmap + MipStreaming（VRChat 要求二者绑定）。")]
        public bool mipmapColor = true;

        public bool mipmapNormal = true;
        public bool mipmapGrayscale = true;

        [Range(0, 100)] public int compressionQuality = 100;

        public ATOPlatformProfile Clone()
        {
            return (ATOPlatformProfile)MemberwiseClone();
        }

        /// <summary>EN: Platform aware default. ZH: 按平台生成默认值。</summary>
        public static ATOPlatformProfile Default(ATOPlatform platform)
        {
            return new ATOPlatformProfile
            {
                enabled = false,
                platform = platform,
                maxAtlasSize = platform == ATOPlatform.PC ? 8192 : 4096,
            };
        }
    }

    /// <summary>
    /// EN: The complete, serialised configuration of the optimizer.
    /// ZH: 优化器的完整序列化配置。
    /// </summary>
    [Serializable]
    public class ATOSettings
    {
        // ---------------------------------------------------------------- general

        [Tooltip("EN: Generate atlases. When off, only whole-texture rescaling + import optimisation happens. ZH: 生成图集。关闭时只做整图缩放与导入参数优化。")]
        public bool generateAtlas = true;

        [Tooltip("EN: Quality tier. ZH: 质量挡位。")]
        public ATOQualityTier qualityTier = ATOQualityTier.High;

        [Tooltip("EN: Parameters of the currently selected tier. ZH: 当前挡位的具体参数。")]
        public ATOQualityParameters quality = ATOQualityParameters.ForTier(ATOQualityTier.High);

        [Tooltip("EN: Parameters of the user defined tier, never overwritten. ZH: 自定义挡位参数，不会被覆盖。")]
        public ATOQualityParameters customQuality = ATOQualityParameters.ForTier(ATOQualityTier.Custom);

        // ---------------------------------------------------------------- atlas

        [Tooltip("EN: Minimum padding between islands in pixels. ZH: 岛之间的最小间距（像素）。")]
        public int minPadding = 4;

        [Tooltip("EN: Experimental: allow non power of two atlas sizes (64px steps). ZH: 实验性：允许非 2 的幂图集尺寸（64px 步进）。")]
        public bool allowNPOT;

        [Tooltip("EN: Allow rotating islands by 90 degrees while packing. ZH: 装箱时允许旋转 90 度。")]
        public bool allowIslandRotation = true;

        [Tooltip("EN: Merge fully overlapping islands inside one texture. ZH: 合并同一贴图内完全重叠的岛。")]
        public bool mergeOverlappingIslands = true;

        // ---------------------------------------------------------------- dedup

        [Tooltip("EN: Deduplicate identical materials after optimisation. ZH: 优化后去重完全相同的材质。")]
        public bool deduplicateMaterials = true;

        [Tooltip("EN: Deduplicate identical textures/atlases after optimisation. ZH: 优化后去重完全相同的贴图/图集。")]
        public bool deduplicateTextures = true;

        // ---------------------------------------------------------------- output

        [Tooltip("EN: Shared (all platform) output profile. ZH: 通用（全平台）输出配置。")]
        public ATOPlatformProfile sharedProfile = new ATOPlatformProfile { enabled = true, maxAtlasSize = 8192 };

        [Tooltip("EN: Per platform overrides. ZH: 各平台覆盖。")]
        public List<ATOPlatformProfile> platformProfiles = new List<ATOPlatformProfile>
        {
            ATOPlatformProfile.Default(ATOPlatform.PC),
            ATOPlatformProfile.Default(ATOPlatform.Android),
            ATOPlatformProfile.Default(ATOPlatform.iOS),
        };

        // ---------------------------------------------------------------- whitelist

        [Tooltip("EN: Any object listed here (mesh, material, texture, animation, GameObject, ...) is excluded. ZH: 此处列出的任意对象（网格、材质、贴图、动画、游戏对象等）都会被排除。")]
        public List<UnityEngine.Object> whitelist = new List<UnityEngine.Object>();

        // ---------------------------------------------------------------- debug / i18n

        [Tooltip("EN: Verbose [ATO] logging to the Unity console. ZH: 向 Unity 控制台输出详细的 [ATO] 日志。")]
        public bool verboseLogging;

        [Tooltip("EN: Write a per-step timing profile to the report. ZH: 在报告中输出每一步耗时。")]
        public bool timingProfile = true;

        [Tooltip("EN: Language override; empty means follow NDMF. ZH: 语言覆盖；留空表示跟随 NDMF。")]
        public string languageOverride = "";

        /// <summary>
        /// EN: Returns the effective quality parameters (custom tier uses its own storage).
        /// ZH: 返回生效的质量参数（自定义挡位使用独立存储）。
        /// </summary>
        public ATOQualityParameters EffectiveQuality()
        {
            return qualityTier == ATOQualityTier.Custom ? customQuality : quality;
        }

        /// <summary>
        /// EN: Returns the profile for a platform, falling back to the shared profile.
        /// ZH: 返回某平台的配置，未启用时回退到通用配置。
        /// </summary>
        public ATOPlatformProfile EffectiveProfile(ATOPlatform platform)
        {
            if (platformProfiles != null)
            {
                foreach (var p in platformProfiles)
                {
                    if (p != null && p.enabled && p.platform == platform) return p;
                }
            }

            return sharedProfile;
        }

        /// <summary>
        /// EN: True when the tier requests bit-exact output (no resampling at all).
        /// ZH: 当挡位要求逐位无损（完全不重采样）时返回 true。
        /// </summary>
        public bool IsLossless()
        {
            var q = EffectiveQuality();
            return qualityTier == ATOQualityTier.Lossless ||
                   (q.minStructuralSimilarity >= 1.0f && q.maxDeltaE2000Mean <= 0f && q.maxNormalAngleMeanDeg <= 0f &&
                    q.maxGrayscaleRmse <= 0f);
        }
    }
}
