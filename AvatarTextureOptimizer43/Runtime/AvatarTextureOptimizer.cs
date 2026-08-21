using System;
using System.Collections.Generic;
using UnityEngine;
#if ATO_VRCSDK3
using VRC.SDK3.Avatars.Components;
#endif

namespace Fosa.ATO
{
    /// <summary>
    /// Root component. Hang exactly one on the avatar root that also has VRCAvatarDescriptor.
    /// 根组件。整个 Avatar 及其子级只允许挂一个，且挂载对象必须有 VRCAvatarDescriptor。
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("FOSA/Avatar Texture Optimizer")]
    [HelpURL("https://github.com/fosa/avatar-texture-optimizer")]
    [ExecuteAlways]
    public class AvatarTextureOptimizer : MonoBehaviour
    {
        public const string LogPrefix = "[ATO]";
        public const string AtlasNamePrefix = "ATO_";
        public const string PackageName = "net.fosa.avatar-texture-optimizer";
        public const string DisplayName = "Avatar Texture Optimizer";

        [Header("Basic / 基础")]
        [Tooltip("Generate atlases. Off = scale whole textures, keep original UVs.\n生成图集。关闭则只缩放整张贴图、不改 UV、不剔除未使用区域。")]
        public bool generateAtlas = true;

        [Tooltip("Quality preset. Named presets overwrite the numeric fields below; Custom is never overwritten.\n质量挡位。具名挡位会改写下方数值；Custom 不会被覆盖。")]
        public AtoQualityPreset qualityPreset = AtoQualityPreset.High;

        [Tooltip("Numeric quality thresholds (advanced). 质量数值（高级）。")]
        public AtoQualitySettings quality = AtoQualitySettings.ForPreset(AtoQualityPreset.High);

        [Header("Atlas / 图集")]
        [Tooltip("Experimental NPOT atlas sizes (64 px steps). Verified with MipStreaming + Crunch.\n实验性 NPOT 图集边长（64 步进）。已验证可与 MipStreaming、Crunch 共用。")]
        public bool experimentalNpot;

        [Tooltip("Minimum padding between islands. Actual padding = max(this, ceil(atlasMaxSide/128)).\n岛间最小 padding。实际值 = max(本值, ceil(图集长边/128))。")]
        public AtoMinPadding minPadding = AtoMinPadding.Px4;

        [Header("Pixel density / 像素密度")]
        [Tooltip("Minimum texel density (px/m). Prevents blur. 最小像素密度，防止发糊。")]
        public AtoPixelDensity minPixelDensity = AtoPixelDensity.D2048;

        [Tooltip("Maximum texel density (px/m). Prevents waste. 最大像素密度，防止浪费。")]
        public AtoPixelDensity maxPixelDensity = AtoPixelDensity.D4096;

        [Header("Dedup / 去重")]
        [Tooltip("After optimize, merge byte-identical materials and update references (including slots / clips).\n优化后合并内容完全相同的材质并更新引用。")]
        public bool dedupMaterials = true;

        [Tooltip("After optimize, merge byte-identical textures/atlases and update references.\n优化后合并内容完全相同的贴图/图集并更新引用。")]
        public bool dedupTextures = true;

        [Header("Whitelist / 白名单")]
        [Tooltip("Any object type. All textures referenced by these objects skip ALL optimization.\n不限制对象类型。这些对象引用到的全部贴图跳过所有优化。")]
        public List<UnityEngine.Object> whitelist = new List<UnityEngine.Object>();

        [Header("Output formats / 输出格式")]
        [Tooltip("Default (all-platform) format and mip/streaming. Folded in the inspector.\n全平台默认格式与 Mip。检查器中默认折叠。")]
        public AtoFormatSettings formats = new AtoFormatSettings();

        [Header("Platform / 平台")]
        [Tooltip("Which platform's settings to apply on this bake. Auto = current Unity build target.\n本次烘焙使用哪套平台参数。Auto = 当前 Unity 构建目标。")]
        public AtoBuildPlatform platform = AtoBuildPlatform.Auto;

        public AtoPlatformOverride pcOverride = new AtoPlatformOverride();
        public AtoPlatformOverride androidOverride = new AtoPlatformOverride();
        public AtoPlatformOverride iosOverride = new AtoPlatformOverride();

        [Header("Language / 语言")]
        [Tooltip("Auto follows NDMF's current language. Manual uses languageCode.\nAuto 跟随 NDMF；Manual 使用 languageCode。")]
        public AtoLanguageMode languageMode = AtoLanguageMode.Auto;

        [Tooltip("BCP-47 code matching a Localization/*.json file, e.g. en-US or zh-Hans.\n对应 Localization 下 json 文件名的语言代码。")]
        public string languageCode = "en-US";

        [Header("Debug / 调试")]
        [Tooltip("Verbose [ATO] logs for advanced users. 高级用户详细日志。")]
        public bool verboseLog;

        /// <summary>
        /// Last preset applied to `quality`. Used by the inspector to detect user preset changes.
        /// 上次应用到 quality 的挡位，供检查器检测切换。
        /// </summary>
        [SerializeField] internal AtoQualityPreset _appliedPreset = AtoQualityPreset.High;

        void Reset()
        {
            qualityPreset = AtoQualityPreset.High;
            quality = AtoQualitySettings.ForPreset(AtoQualityPreset.High);
            _appliedPreset = AtoQualityPreset.High;
            generateAtlas = true;
            minPadding = AtoMinPadding.Px4;
            minPixelDensity = AtoPixelDensity.D2048;
            maxPixelDensity = AtoPixelDensity.D4096;
            dedupMaterials = true;
            dedupTextures = true;
            formats = new AtoFormatSettings();
        }

        void OnValidate()
        {
            if (quality == null) quality = AtoQualitySettings.ForPreset(qualityPreset);
            if (formats == null) formats = new AtoFormatSettings();
            if (pcOverride == null) pcOverride = new AtoPlatformOverride();
            if (androidOverride == null) androidOverride = new AtoPlatformOverride();
            if (iosOverride == null) iosOverride = new AtoPlatformOverride();
            if (whitelist == null) whitelist = new List<UnityEngine.Object>();

            // Named presets overwrite numeric fields. Switching TO Custom loads all-1 defaults once.
            // 具名挡位覆盖数值；切到 Custom 时一次性载入全 1 默认，之后不再被覆盖。
            if (qualityPreset != _appliedPreset)
            {
                quality.CopyFrom(AtoQualitySettings.ForPreset(qualityPreset));
                _appliedPreset = qualityPreset;
            }

            if ((int)minPadding < 4) minPadding = AtoMinPadding.Px4;
            if ((int)minPixelDensity > (int)maxPixelDensity)
                maxPixelDensity = minPixelDensity;
        }

        /// <summary>
        /// Resolve the platform that will actually be used this bake.
        /// 解析本次烘焙真正使用的平台。
        /// </summary>
        public AtoBuildPlatform ResolvePlatform()
        {
            // Runtime assembly must not reference UnityEditor. The editor pipeline
            // overwrites Auto via ResolvePlatformEditor().
            // Runtime 程序集禁止引用 UnityEditor。Auto 由 Editor 管线覆盖。
            if (platform != AtoBuildPlatform.Auto) return platform;
            return AtoBuildPlatform.PC;
        }

        /// <summary>
        /// Merge generic settings with the matching platform override (if enabled).
        /// 将通用设置与已启用的平台覆盖合并。
        /// </summary>
        public AtoResolvedSettings ResolveSettings()
        {
            var s = new AtoResolvedSettings
            {
                platform = ResolvePlatform(),
                generateAtlas = generateAtlas,
                qualityPreset = qualityPreset,
                quality = quality.Clone(),
                experimentalNpot = experimentalNpot,
                minPadding = (int)minPadding,
                minPixelDensity = (int)minPixelDensity,
                maxPixelDensity = (int)maxPixelDensity,
                formats = formats.Clone(),
                dedupMaterials = dedupMaterials,
                dedupTextures = dedupTextures,
                verboseLog = verboseLog
            };

            AtoPlatformOverride ov = null;
            switch (s.platform)
            {
                case AtoBuildPlatform.PC: ov = pcOverride; break;
                case AtoBuildPlatform.Android: ov = androidOverride; break;
                case AtoBuildPlatform.iOS: ov = iosOverride; break;
            }

            if (ov != null && ov.enabled)
            {
                s.generateAtlas = ov.generateAtlas;
                s.qualityPreset = ov.qualityPreset;
                s.quality = ov.quality.Clone();
                s.experimentalNpot = ov.experimentalNpot;
                s.minPadding = (int)ov.minPadding;
                s.minPixelDensity = (int)ov.minPixelDensity;
                s.maxPixelDensity = (int)ov.maxPixelDensity;
                s.formats = ov.formats.Clone();
            }

            if ((int)s.platform == (int)AtoBuildPlatform.Android || (int)s.platform == (int)AtoBuildPlatform.iOS)
                s.maxAtlasSide = 4096;
            else
                s.maxAtlasSide = 8192;

            s.minAtlasSide = 64;
            return s;
        }

        /// <summary>
        /// True if this GameObject has a VRCAvatarDescriptor (when the SDK is present).
        /// 挂载对象是否具备 VRCAvatarDescriptor。
        /// </summary>
        public bool HasAvatarDescriptor()
        {
#if ATO_VRCSDK3
            return GetComponent<VRCAvatarDescriptor>() != null;
#else
            return GetComponent("VRCAvatarDescriptor") != null
                   || GetComponent("VRC.SDK3.Avatars.Components.VRCAvatarDescriptor") != null;
#endif
        }
    }

    /// <summary>
    /// Fully resolved bake settings (generic + platform override).
    /// 解析完成的烘焙设置。
    /// </summary>
    public class AtoResolvedSettings
    {
        public AtoBuildPlatform platform;
        public bool generateAtlas;
        public AtoQualityPreset qualityPreset;
        public AtoQualitySettings quality;
        public bool experimentalNpot;
        public int minPadding;
        public int minPixelDensity;
        public int maxPixelDensity;
        public AtoFormatSettings formats;
        public bool dedupMaterials;
        public bool dedupTextures;
        public bool verboseLog;
        public int maxAtlasSide = 8192;
        public int minAtlasSide = 64;
    }
}
