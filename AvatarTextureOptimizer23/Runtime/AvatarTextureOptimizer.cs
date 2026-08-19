using System.Collections.Generic;
using UnityEngine;
#if ATO_NDMF
using nadena.dev.ndmf;
#endif
#if ATO_VRCSDK3_AVATARS
using VRC.SDK3.Avatars.Components;
#endif

namespace FOSA.AvatarTextureOptimizer
{
    /// <summary>
    /// Root component. One per avatar (including children). The host must have a VRCAvatarDescriptor.
    /// 根组件。一个 Avatar（含子孙）只允许一个。挂载对象必须有 VRCAvatarDescriptor。
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("FOSA/Avatar Texture Optimizer")]
    [HelpURL("https://github.com/fosa/avatar-texture-optimizer")]
    public class AvatarTextureOptimizer : MonoBehaviour
#if ATO_NDMF
        , INDMFEditorOnly
#endif
    {
        public const string LogPrefix = "[ATO]";
        public const string AtlasNamePrefix = "ATO_";
        public const string PackageName = "net.fosa.avatar-texture-optimizer";

        [Header("Basic / 基础")]
        [Tooltip("Generate atlases. Off = scale whole textures, keep original UVs.\n生成图集。关闭则只缩放整图、保留原 UV。")]
        public bool generateAtlas = true;

        [Tooltip("Quality preset. Custom is never overwritten by other presets.\n质量挡位。Custom 不会被其它挡位覆盖。")]
        public ATOQualityPreset qualityPreset = ATOQualityPreset.High;

        [Tooltip("Active quality parameters (follow the preset unless Custom).\n当前质量参数（非 Custom 时跟随挡位）。")]
        public ATOQualityParameters qualityParameters = ATOQualityParameters.ForPreset(ATOQualityPreset.High);

        [Tooltip("Independent Custom parameters. Default all 1 = near-lossless.\n独立的 Custom 参数。默认全 1 = 近无损。")]
        public ATOQualityParameters customQualityParameters = ATOQualityParameters.ForPreset(ATOQualityPreset.Custom);

        [Tooltip("Minimum texel density in px/m. Prevents blur.\n最小像素密度（px/m），防止发糊。")]
        public float minPixelDensity = 2048f;

        [Tooltip("Maximum texel density in px/m. Prevents waste.\n最大像素密度（px/m），防止浪费。")]
        public float maxPixelDensity = 4096f;

        [Header("Atlas / 图集")]
        [Tooltip("Experimental NPOT atlas sizes (64 px steps). Verified with MipStreaming and Crunch.\n实验性非 2 次幂图集（64 步进）。已验证 MipStreaming 与 Crunch。")]
        public bool experimentalNpot;

        [Tooltip("Minimum padding between islands.\n岛之间的最小 padding。")]
        public ATOMinPadding minPadding = ATOMinPadding.Px4;

        [Header("Dedup / 去重")]
        [Tooltip("Deduplicate identical materials after optimization.\n优化后对完全相同的材质去重。")]
        public bool enableMaterialDedup = true;

        [Tooltip("Deduplicate identical textures / atlases after optimization.\n优化后对完全相同的贴图/图集去重。")]
        public bool enableTextureDedup = true;

        [Header("Mip / Streaming")]
        public bool mipStreamingOpaque = true;
        public bool mipStreamingTransparent = true;
        public bool mipStreamingNormal = true;
        public bool mipStreamingGray = true;

        [Header("Compression / 压缩")]
        public ATOCompressionChoice formatOpaque = ATOCompressionChoice.Auto;
        public ATOCompressionChoice formatTransparent = ATOCompressionChoice.Auto;
        public ATOCompressionChoice formatNormal = ATOCompressionChoice.Auto;
        public ATOCompressionChoice formatGray = ATOCompressionChoice.Auto;

        [Header("Whitelist / 白名单")]
        [Tooltip("Any object type. All textures referenced by these objects skip every optimization.\n任意对象类型。它们引用的全部贴图跳过一切优化。")]
        public List<UnityEngine.Object> whitelist = new List<UnityEngine.Object>();

        [Header("Platform override / 平台覆盖")]
        public ATOPlatformSettings pcOverride = new ATOPlatformSettings();
        public ATOPlatformSettings androidOverride = new ATOPlatformSettings();
        public ATOPlatformSettings iosOverride = new ATOPlatformSettings();

        [Header("UI / Debug")]
        public ATOLanguageMode language = ATOLanguageMode.Auto;

        [Tooltip("Verbose [ATO] logs. On by default while the tool is in development.\n详细 [ATO] 日志。开发阶段默认开启。")]
        public bool debugLog = true;

        /// <summary>
        /// Quality parameters that should actually be used (Custom vs preset).
        /// 实际应使用的质量参数（Custom 或当前挡位）。
        /// </summary>
        public ATOQualityParameters ActiveQuality =>
            qualityPreset == ATOQualityPreset.Custom ? customQualityParameters : qualityParameters;

        public ATOPlatformSettings GetOverride(ATOPlatform platform)
        {
            switch (platform)
            {
                case ATOPlatform.PC: return pcOverride;
                case ATOPlatform.Android: return androidOverride;
                case ATOPlatform.iOS: return iosOverride;
                default: return null;
            }
        }

        /// <summary>
        /// Merge generic settings with an optional platform override.
        /// 把通用设置与可选的平台覆盖合并。
        /// </summary>
        public ATOResolvedSettings Resolve(ATOPlatform platform)
        {
            var resolved = ATOResolvedSettings.FromComponent(this);
            var ov = GetOverride(platform);
            if (ov != null && ov.enabled)
            {
                resolved.ApplyOverride(ov);
            }
            resolved.platform = platform;
            return resolved;
        }

        private void Reset()
        {
            qualityPreset = ATOQualityPreset.High;
            qualityParameters = ATOQualityParameters.ForPreset(ATOQualityPreset.High);
            customQualityParameters = ATOQualityParameters.ForPreset(ATOQualityPreset.Custom);
        }

        private void OnValidate()
        {
            if (qualityPreset != ATOQualityPreset.Custom)
            {
                qualityParameters = ATOQualityParameters.ForPreset(qualityPreset);
            }

            minPixelDensity = Mathf.Max(1f, minPixelDensity);
            maxPixelDensity = Mathf.Max(minPixelDensity, maxPixelDensity);

            if (whitelist == null) whitelist = new List<UnityEngine.Object>();
            if (pcOverride == null) pcOverride = new ATOPlatformSettings();
            if (androidOverride == null) androidOverride = new ATOPlatformSettings();
            if (iosOverride == null) iosOverride = new ATOPlatformSettings();
        }

#if ATO_VRCSDK3_AVATARS
        /// <summary>
        /// Bake-time structural check. Returns an error key or null.
        /// 烘焙期结构检查。返回错误 key 或 null。
        /// </summary>
        public static string ValidateMount(GameObject avatarRoot, AvatarTextureOptimizer self)
        {
            if (self == null) return "ato.error.missing_component";
            if (self.GetComponent<VRCAvatarDescriptor>() == null)
                return "ato.error.need_descriptor";

            var all = avatarRoot.GetComponentsInChildren<AvatarTextureOptimizer>(true);
            if (all.Length > 1) return "ato.error.multiple_components";
            return null;
        }
#endif
    }

    /// <summary>
    /// Fully resolved settings after applying the active platform override.
    /// 应用当前平台覆盖之后的最终设置。
    /// </summary>
    public class ATOResolvedSettings
    {
        public ATOPlatform platform;
        public bool generateAtlas = true;
        public bool experimentalNpot;
        public ATOMinPadding minPadding = ATOMinPadding.Px4;
        public ATOQualityPreset qualityPreset = ATOQualityPreset.High;
        public ATOQualityParameters quality;
        public float minPixelDensity = 2048f;
        public float maxPixelDensity = 4096f;
        public bool enableMaterialDedup = true;
        public bool enableTextureDedup = true;
        public bool mipStreamingOpaque = true;
        public bool mipStreamingTransparent = true;
        public bool mipStreamingNormal = true;
        public bool mipStreamingGray = true;
        public ATOCompressionChoice formatOpaque = ATOCompressionChoice.Auto;
        public ATOCompressionChoice formatTransparent = ATOCompressionChoice.Auto;
        public ATOCompressionChoice formatNormal = ATOCompressionChoice.Auto;
        public ATOCompressionChoice formatGray = ATOCompressionChoice.Auto;
        public bool debugLog = true;
        public List<UnityEngine.Object> whitelist;

        public static ATOResolvedSettings FromComponent(AvatarTextureOptimizer c)
        {
            return new ATOResolvedSettings
            {
                generateAtlas = c.generateAtlas,
                experimentalNpot = c.experimentalNpot,
                minPadding = c.minPadding,
                qualityPreset = c.qualityPreset,
                quality = c.ActiveQuality,
                minPixelDensity = c.minPixelDensity,
                maxPixelDensity = c.maxPixelDensity,
                enableMaterialDedup = c.enableMaterialDedup,
                enableTextureDedup = c.enableTextureDedup,
                mipStreamingOpaque = c.mipStreamingOpaque,
                mipStreamingTransparent = c.mipStreamingTransparent,
                mipStreamingNormal = c.mipStreamingNormal,
                mipStreamingGray = c.mipStreamingGray,
                formatOpaque = c.formatOpaque,
                formatTransparent = c.formatTransparent,
                formatNormal = c.formatNormal,
                formatGray = c.formatGray,
                debugLog = c.debugLog,
                whitelist = c.whitelist
            };
        }

        public void ApplyOverride(ATOPlatformSettings ov)
        {
            generateAtlas = ov.generateAtlas;
            experimentalNpot = ov.experimentalNpot;
            minPadding = ov.minPadding;
            qualityPreset = ov.qualityPreset;
            quality = ov.qualityPreset == ATOQualityPreset.Custom
                ? ov.customQualityParameters
                : ov.qualityParameters;
            minPixelDensity = ov.minPixelDensity;
            maxPixelDensity = ov.maxPixelDensity;
            formatOpaque = ov.formatOpaque;
            formatTransparent = ov.formatTransparent;
            formatNormal = ov.formatNormal;
            formatGray = ov.formatGray;
            mipStreamingOpaque = ov.mipStreamingOpaque;
            mipStreamingTransparent = ov.mipStreamingTransparent;
            mipStreamingNormal = ov.mipStreamingNormal;
            mipStreamingGray = ov.mipStreamingGray;
        }

        public bool MipStreamingFor(ATOTextureCategory cat)
        {
            switch (cat)
            {
                case ATOTextureCategory.OpaqueAlbedo: return mipStreamingOpaque;
                case ATOTextureCategory.TransparentAlbedo: return mipStreamingTransparent;
                case ATOTextureCategory.Normal: return mipStreamingNormal;
                case ATOTextureCategory.Gray: return mipStreamingGray;
                default: return mipStreamingOpaque;
            }
        }

        public ATOCompressionChoice FormatFor(ATOTextureCategory cat)
        {
            switch (cat)
            {
                case ATOTextureCategory.OpaqueAlbedo: return formatOpaque;
                case ATOTextureCategory.TransparentAlbedo: return formatTransparent;
                case ATOTextureCategory.Normal: return formatNormal;
                case ATOTextureCategory.Gray: return formatGray;
                default: return formatOpaque;
            }
        }

        public int MaxAtlasEdge
        {
            get
            {
                switch (platform)
                {
                    case ATOPlatform.Android:
                    case ATOPlatform.iOS:
                        return 4096;
                    default:
                        return 8192;
                }
            }
        }
    }
}
