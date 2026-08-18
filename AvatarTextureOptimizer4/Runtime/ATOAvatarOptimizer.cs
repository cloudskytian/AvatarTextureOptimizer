// Avatar Texture Optimizer (ATO)
// Runtime component: attach to an avatar to optimize its textures/UVs at build time.
// 运行时组件：挂到 Avatar 上，构建时优化贴图与 UV。

using System;
using System.Collections.Generic;
using UnityEngine;

namespace NetFosa.ATO
{
    /// <summary>
    /// The single component that enables ATO on an avatar. Exactly one instance is allowed
    /// on an avatar and its children; it must live on an object with a VRCAvatarDescriptor.
    /// 启用 ATO 的唯一组件。一个 Avatar 及其子级上最多只能挂一个，且必须挂在带
    /// VRCAvatarDescriptor 的对象上。
    /// </summary>
    [AddComponentMenu("Avatar Texture Optimizer/ATO Avatar Optimizer")]
    [DisallowMultipleComponent]
    public class ATOAvatarOptimizer : MonoBehaviour
    {
        [Tooltip("General optimization profile. / 通用优化配置。")]
        public ATOGeneralSettings general = new ATOGeneralSettings();

        [Tooltip("Per-platform overrides (PC / Android / iOS). / 分平台覆写（PC / Android / iOS）。")]
        public ATOPlatformSettings platform = new ATOPlatformSettings();

        [Tooltip("Whitelist: every texture referenced by these objects is skipped entirely. / 白名单：这些对象引用的贴图全部跳过优化。")]
        public ATOWhitelistSettings whitelist = new ATOWhitelistSettings();

        [Tooltip("Compression & mip-streaming options per texture category. / 按贴图分类的压缩与 mip 流式选项。")]
        public ATOCompressionSettings compression = new ATOCompressionSettings();

        [Tooltip("Advanced / developer options. / 高级/开发者选项。")]
        public ATOAdvancedSettings advanced = new ATOAdvancedSettings();
    }

    /// <summary>
    /// Per-metric quality thresholds. The scalar "target quality" is a coarse level indicator;
    /// the actual gate is the strictest of the per-metric thresholds below.
    /// 各指标质量阈值。标量 targetQuality 只是粗略挡位；实际判定取下列各指标阈值的"木桶"最严者。
    /// </summary>
    [Serializable]
    public struct ATOQualityThresholds
    {
        /// <summary>Coarse target quality in [0,1]; 1.0 means ~lossless (skip island scaling). / 粗略目标质量，1.0 表示近无损（跳过岛缩放）。</summary>
        [Range(0f, 1f)] public float targetQuality;

        /// <summary>Minimum acceptable MS-SSIM (or SSIM when the island is small). / 可接受的最低 MS-SSIM（小岛回退 SSIM）。</summary>
        [Range(0f, 1f)] public float msSsimMin;

        /// <summary>Maximum acceptable CIEDE2000 delta-E. / 可接受的最大 CIEDE2000 ΔE。</summary>
        [Range(0f, 100f)] public float deltaEMax;

        /// <summary>Maximum acceptable linear alpha RMSE (blend transparent). / 可接受的最大线性 alpha RMSE（Blend 透明）。</summary>
        [Range(0f, 1f)] public float alphaRmseMax;

        /// <summary>Minimum acceptable clip-outline IoU (cutout transparent). / 可接受的最小 clip 轮廓 IoU（Cutout 透明）。</summary>
        [Range(0f, 1f)] public float alphaIoUMin;

        /// <summary>Maximum acceptable normal-map angular error in degrees. / 可接受的最大法线角度误差（度）。</summary>
        [Range(0f, 90f)] public float angleDegMax;

        /// <summary>Maximum acceptable linear grayscale RMSE. / 可接受的最大线性灰度 RMSE。</summary>
        [Range(0f, 1f)] public float grayRmseMax;
    }

    /// <summary>
    /// A full optimization profile. Used both for the general settings and per-platform overrides.
    /// 一份完整的优化配置。既用于通用设置，也用于分平台覆写。
    /// </summary>
    [Serializable]
    public class ATOOptimizationProfile
    {
        [Tooltip("Quality preset. / 质量挡位。")]
        public ATOQualityLevel qualityLevel = ATOQualityLevel.High;

        [Tooltip("Custom thresholds (only used when qualityLevel == Custom). / 自定义阈值（仅挡位为 Custom 时生效）。")]
        public ATOQualityThresholds customThresholds = new ATOQualityThresholds
        {
            targetQuality = 1f,  // near-lossless default / 默认近无损
            msSsimMin = 1f,
            deltaEMax = 1f,
            alphaRmseMax = 0.01f,
            alphaIoUMin = 0.999f,
            angleDegMax = 0.5f,
            grayRmseMax = 0.01f
        };

        [Tooltip("Generate atlases. When off: no atlas, no unused-UV culling, no re-UV; textures are resized directly. / 是否生成图集。关闭时不生成图集、不剔除未使用 UV、不重排 UV，直接缩放贴图。")]
        public bool generateAtlas = true;

        [Tooltip("Experimental NPOT atlas sizes (steps of 64 px). / 实验性 NPOT 图集尺寸（64px 步进）。")]
        public bool npotAtlas = false;

        [Tooltip("Padding between packed islands in pixels. / 装箱岛间距（像素）。")]
        public int padding = 4;

        [Tooltip("Maximum atlas side length. / 图集最大边长。")]
        public int maxAtlasSize = 8192;

        [Tooltip("Minimum pixel density (px per meter). / 最小像素密度（每米像素）。")]
        public int pixelDensityMin = 2048;

        [Tooltip("Maximum pixel density (px per meter). / 最大像素密度（每米像素）。")]
        public int pixelDensityMax = 4096;

        [Tooltip("Deduplicate identical textures (content + import settings). / 对完全相同的贴图去重（内容+导入设置）。")]
        public bool dedupTextures = true;

        [Tooltip("Deduplicate identical materials (content + parameters). / 对完全相同的材质去重（内容+参数）。")]
        public bool dedupMaterials = true;

        [Tooltip("Merge identical opaque material slots and update animation references. / 合并相同的不透明材质槽并更新动画引用。")]
        public bool mergeOpaqueSlots = true;
    }

    /// <summary>
    /// General settings wrapper. / 通用设置包装。
    /// </summary>
    [Serializable]
    public class ATOGeneralSettings
    {
        public ATOOptimizationProfile profile = new ATOOptimizationProfile();
    }

    /// <summary>
    /// A per-platform override. When enabled, every optimization parameter of that platform
    /// overrides the general profile. / 分平台覆写。启用后该平台的全部优化参数覆盖通用配置。
    /// </summary>
    [Serializable]
    public class ATOPlatformOverride
    {
        [Tooltip("Enable override for this platform. / 为该平台启用覆写。")]
        public bool enabled = false;

        public ATOOptimizationProfile profile = new ATOOptimizationProfile();
    }

    /// <summary>
    /// Per-platform override container. / 分平台覆写容器。
    /// </summary>
    [Serializable]
    public class ATOPlatformSettings
    {
        public ATOPlatformOverride pc = new ATOPlatformOverride();
        public ATOPlatformOverride android = new ATOPlatformOverride();
        public ATOPlatformOverride ios = new ATOPlatformOverride();

        // Mobile defaults are stricter (smaller max atlas). / 移动端默认更保守（图集上限更小）。
        public void EnsureDefaults()
        {
            android.profile.maxAtlasSize = ATOConstants.MaxAtlasSizeMobile;
            ios.profile.maxAtlasSize = ATOConstants.MaxAtlasSizeMobile;
        }
    }

    /// <summary>
    /// Whitelist. Whitelisted objects may be any type (mesh, material, texture, animation...).
    /// All textures referenced by whitelisted objects skip every optimization.
    /// 白名单。白名单不限对象类型。白名单对象引用的全部贴图跳过所有优化。
    /// </summary>
    [Serializable]
    public class ATOWhitelistSettings
    {
        public List<UnityEngine.Object> whitelist = new List<UnityEngine.Object>();
    }

    /// <summary>
    /// A compression choice for one texture category.
    /// 单个贴图分类的压缩选项。
    /// </summary>
    [Serializable]
    public class ATOCompressionChoice
    {
        public ATOCompressionFormat format = ATOCompressionFormat.Auto;

        [Tooltip("Mip Streaming (bound to mipmaps; VRChat requires mipmaps ⇒ streaming). / Mip 流式（与 mipmap 绑定；VRChat 要求 mipmap ⇒ 流式）。")]
        public bool mipStreaming = true;
    }

    /// <summary>
    /// Per-category compression settings. / 按分类的压缩设置。
    /// </summary>
    [Serializable]
    public class ATOCompressionSettings
    {
        public ATOCompressionChoice opaque = new ATOCompressionChoice();
        public ATOCompressionChoice alpha = new ATOCompressionChoice();
        public ATOCompressionChoice normal = new ATOCompressionChoice();
        public ATOCompressionChoice grayscale = new ATOCompressionChoice();
    }

    /// <summary>
    /// Advanced / developer settings. / 高级/开发者设置。
    /// </summary>
    [Serializable]
    public class ATOAdvancedSettings
    {
        [Tooltip("Enable [ATO] debug logging (recommended during development). / 开启 [ATO] 调试日志（开发期推荐）。")]
        public bool debugLogging = true;

        [Tooltip("Verbose per-island logging (very chatty). / 逐岛详细日志（非常啰嗦）。")]
        public bool verboseLogging = false;

        [Tooltip("UI language mode. / UI 语言模式。")]
        public ATOLanguageMode languageMode = ATOLanguageMode.Auto;
    }
}
