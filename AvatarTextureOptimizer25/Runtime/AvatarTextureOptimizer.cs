// Avatar Texture Optimizer / 头像贴图优化器
// The single avatar component. Add it next to VRCAvatarDescriptor (same GameObject).
// 唯一的 Avatar 组件。请与 VRCAvatarDescriptor 挂在同一 GameObject 上。
//
// Exactly one instance is allowed per avatar (including children). The NDMF
// validation pass aborts the build with an error if this rule is violated.
// 每个 Avatar（含子级）只允许存在一个本组件；校验阶段违反此规则会报错并中止构建。

using System.Collections.Generic;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace FOSA.AvatarTextureOptimizer
{
    /// <summary>
    /// Avatar-wide texture optimizer settings. All quality-sensitive parameters
    /// live here so third party tools can pre-seed them from scripts.
    /// Avatar 级贴图优化设置。所有与质量相关的参数都集中在此，便于第三方工具以脚本预置。
    /// </summary>
    [AddComponentMenu("Avatar Texture Optimizer/Avatar Texture Optimizer")]
    [DisallowMultipleComponent]
    [Icon("")]
    public class AvatarTextureOptimizer : MonoBehaviour
    {
        // ---------------- Quality / 质量 ----------------

        [Header("Quality / 质量")]
        [Tooltip("Quality preset; detailed thresholds are folded under Advanced / 质量挡位；具体阈值折叠在高级选项中")]
        public ATOQualityPreset qualityPreset = ATOQualityPreset.High;

        [Tooltip("Custom quality parameters (only used when preset = Custom; defaults to near-lossless all-1) / 自定义质量参数（仅挡位=Custom 时生效；默认全 1 近无损）")]
        public ATOQualitySettings customQuality = ATOQualitySettings.Lossless();

        [Tooltip("Min pixel density in px/m (island world area -> cap island pixels) / 最小像素密度 px/m（按岛的真实世界面积给岛上限）")]
        public int minPixelDensity = 2048;

        [Tooltip("Max pixel density in px/m / 最大像素密度 px/m")]
        public int maxPixelDensity = 4096;

        // ---------------- Atlas / 图集 ----------------

        [Header("Atlas / 图集")]
        [Tooltip("Generate texture atlases. If off, textures are only scaled as a whole / 生成图集。关闭时仅整体缩放贴图")]
        public bool generateAtlas = true;

        [Tooltip("Minimum island padding in px (actual padding = clamp(ceil(atlasMax/128), this, ...)) / 最小岛间距（实际 padding = 在 max(本项, 向上取整(图集最大边/128)) 间取值）")]
        public ATOAtlasPadding minAtlasPadding = ATOAtlasPadding.Pad4;

        [Tooltip("Experimental: allow NPOT atlas candidates (64px steps, platform-unsupported formats are filtered) / 实验性：允许 NPOT 候选图集（64px 步进，自动剔除平台不支持的格式）")]
        public bool experimentalNPOT = false;

        // ---------------- Deduplication / 去重 ----------------

        [Header("Deduplication / 去重")]
        [Tooltip("Deduplicate identical textures by content+import settings and update references / 按内容+导入设置对贴图去重并更新引用")]
        public bool deduplicateTextures = true;

        [Tooltip("Deduplicate identical materials (and merge opaque duplicate material slots when provably safe) / 对内容与参数完全相同的材质去重（可判定时合并不透明重复槽）")]
        public bool deduplicateMaterials = true;

        // ---------------- Platform overrides / 平台覆盖 ----------------

        [Header("Platform overrides (folded) / 平台覆盖（折叠）")]
        public ATOPlatformOverride pcOverride = new ATOPlatformOverride();
        public ATOPlatformOverride androidOverride = new ATOPlatformOverride();
        public ATOPlatformOverride iosOverride = new ATOPlatformOverride();

        // ---------------- Whitelist / 白名单 ----------------

        [Header("Whitelist / 白名单")]
        [Tooltip("Any referenced objects: GameObject, Renderer, Material, Texture2D, AnimationClip... All textures referenced within are excluded from optimization / 任意类型对象：游戏物体、渲染器、材质、贴图、动画……其中引用到的全部贴图跳过一切优化")]
        public List<Object> whitelist = new List<Object>();

        // ---------------- Language / 语言 ----------------

        [Header("Language / 语言")]
        public ATOLanguageMode languageMode = ATOLanguageMode.Auto;

        [Tooltip("Manual language code, e.g. en-US, zh-Hans / 手动语言代码，如 en-US、zh-Hans")]
        public string manualLanguage = "en-US";

        // ---------------- Debug / 调试 ----------------

        [Header("Debug / 调试")]
        [Tooltip("Verbose [ATO] logs for every pipeline step / 每一步输出详细的 [ATO] 日志")]
        public bool verboseLogging = false;

        /// <summary>
        /// Resolves the effective quality settings for this component.
        /// 解析本组件当前生效的质量参数。
        /// </summary>
        public ATOQualitySettings EffectiveQuality()
        {
            if (qualityPreset == ATOQualityPreset.Custom) return customQuality.Clone();
            return ATOQualityPresets.For(qualityPreset);
        }

        /// <summary>
        /// Returns the override object for a platform (may be disabled).
        /// 取指定平台的覆盖对象（可能未启用）。
        /// </summary>
        public ATOPlatformOverride OverrideFor(ATOPlatform platform)
        {
            switch (platform)
            {
                case ATOPlatform.Android: return androidOverride;
                case ATOPlatform.iOS: return iosOverride;
                default: return pcOverride;
            }
        }
    }

    /// <summary>
    /// Preset table anchored on literature/industry practice.
    /// 基于学术与业内实践参考值的挡位表。
    /// References / 参考:
    /// - MS-SSIM: Wang et al. 2003; values >=0.98 commonly treated as visually lossless.
    /// - ΔE2000 (CIEDE2000, Sharma et al. 2005): ~1.0 JND, 2.0 perceptible-by-trained-eye.
    /// - Normal maps: angular error practice from Simplygon/Bungie-style pipelines (1-3 deg).
    /// </summary>
    public static class ATOQualityPresets
    {
        public static ATOQualitySettings For(ATOQualityPreset preset)
        {
            switch (preset)
            {
                case ATOQualityPreset.Performance:
                    return new ATOQualitySettings
                    {
                        targetQuality = 0.80f,
                        msSsimMin = 0.90f, deltaEMax = 6.0f,
                        normalMeanDegMax = 4.0f, normalP95DegMax = 8.0f,
                        alphaRmseMax = 0.06f, cutoutIouMin = 0.90f, grayRmseMax = 0.05f,
                    };
                case ATOQualityPreset.Low:
                    return new ATOQualitySettings
                    {
                        targetQuality = 0.90f,
                        msSsimMin = 0.935f, deltaEMax = 4.5f,
                        normalMeanDegMax = 3.0f, normalP95DegMax = 6.0f,
                        alphaRmseMax = 0.045f, cutoutIouMin = 0.93f, grayRmseMax = 0.04f,
                    };
                case ATOQualityPreset.Balanced:
                    return new ATOQualitySettings
                    {
                        targetQuality = 0.95f,
                        msSsimMin = 0.96f, deltaEMax = 3.0f,
                        normalMeanDegMax = 2.0f, normalP95DegMax = 4.0f,
                        alphaRmseMax = 0.03f, cutoutIouMin = 0.95f, grayRmseMax = 0.03f,
                    };
                case ATOQualityPreset.High:
                    return new ATOQualitySettings
                    {
                        targetQuality = 0.975f,
                        msSsimMin = 0.975f, deltaEMax = 2.0f,
                        normalMeanDegMax = 1.5f, normalP95DegMax = 3.0f,
                        alphaRmseMax = 0.02f, cutoutIouMin = 0.97f, grayRmseMax = 0.02f,
                    };
                case ATOQualityPreset.Maximum:
                    return new ATOQualitySettings
                    {
                        targetQuality = 0.99f,
                        msSsimMin = 0.985f, deltaEMax = 1.0f,
                        normalMeanDegMax = 1.0f, normalP95DegMax = 2.0f,
                        alphaRmseMax = 0.01f, cutoutIouMin = 0.985f, grayRmseMax = 0.01f,
                    };
                default: // Custom: all-lossless defaults, user controlled / 自定义：默认全 1，用户自控
                    return ATOQualitySettings.Lossless();
            }
        }

        /// <summary>Pixel density options offered in the UI (px/m). / UI 中提供的像素密度档（px/m）。</summary>
        public static readonly int[] PixelDensitySteps = { 512, 1024, 2048, 4096, 8192 };
    }
}
