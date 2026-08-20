using System;
using System.Collections.Generic;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer
{
    /// <summary>
    /// Root component. One per avatar, must sit on the same GameObject as VRCAvatarDescriptor.
    /// 根组件。每个 Avatar 仅允许一个，且必须挂在带 VRCAvatarDescriptor 的对象上。
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Fosa/Avatar Texture Optimizer")]
    [HelpURL("https://github.com")]
    public class AvatarTextureOptimizer : MonoBehaviour
    {
        [Tooltip("Generate atlases. If off: no unused-UV trim, no UV rearrange, only whole-texture scale + other opts.\n是否生成图集。关闭则不剔除未使用 UV、不重排 UV，只做整图缩放及其他优化。")]
        public bool generateAtlas = true;

        [Tooltip("Experimental NPOT atlas sizes (64 px steps). Verified with MipStreaming and Crunch.\n实验性 NPOT 图集（64px 步进）。已验证 MipStreaming 与 Crunch。")]
        public bool experimentalNpot;

        public AtoQualityPreset qualityPreset = AtoQualityPreset.High;
        public AtoQualitySettings quality = AtoQualitySettings.ForPreset(AtoQualityPreset.High);

        public AtoMinPadding minPadding = AtoMinPadding.Px4;
        public AtoPixelDensityStop minDensity = AtoPixelDensityStop.Px2048;
        public AtoPixelDensityStop maxDensity = AtoPixelDensityStop.Px4096;

        public AtoFormatSettings formats = new AtoFormatSettings();

        [Tooltip("Dedupe materials that became identical after optimize.\n优化后对完全相同的材质去重。")]
        public bool dedupeMaterials = true;

        [Tooltip("Dedupe textures/atlases that became identical after optimize.\n优化后对完全相同的贴图/图集去重。")]
        public bool dedupeTextures = true;

        [Tooltip("Objects of any type. All Texture2D they reference skip every optimization.\n任意类型对象。其引用的全部 Texture2D 跳过所有优化。")]
        public List<UnityEngine.Object> whitelist = new List<UnityEngine.Object>();

        [Header("Platform override / 平台覆盖")]
        public bool overridePC;
        public AtoPlatformOverride pc = new AtoPlatformOverride { qualityPreset = AtoQualityPreset.High, quality = AtoQualitySettings.ForPreset(AtoQualityPreset.High) };
        public bool overrideAndroid;
        public AtoPlatformOverride android = new AtoPlatformOverride { qualityPreset = AtoQualityPreset.Medium, quality = AtoQualitySettings.ForPreset(AtoQualityPreset.Medium) };
        public bool overrideIOS;
        public AtoPlatformOverride ios = new AtoPlatformOverride { qualityPreset = AtoQualityPreset.Medium, quality = AtoQualitySettings.ForPreset(AtoQualityPreset.Medium) };

        [Header("Language / 语言")]
        public AtoLanguageMode languageMode = AtoLanguageMode.Auto;
        [Tooltip("BCP-47 like en-us / zh-hans. Ignored when mode is Auto.\nBCP-47 语言码。Auto 时忽略。")]
        public string manualLanguage = "en-us";

        [Header("Debug / 调试")]
        public bool verboseLog;

        /// <summary>
        /// Resolve effective settings for a platform. / 按平台解析生效设置。
        /// </summary>
        public AtoPlatformOverride Resolve(AtoPlatform platform)
        {
            AtoPlatformOverride ov = null;
            switch (platform)
            {
                case AtoPlatform.PC when overridePC: ov = pc; break;
                case AtoPlatform.Android when overrideAndroid: ov = android; break;
                case AtoPlatform.iOS when overrideIOS: ov = ios; break;
            }

            if (ov != null && ov.enabled)
            {
                var c = ov.Clone();
                if (c.qualityPreset != AtoQualityPreset.Custom)
                    c.quality = AtoQualitySettings.ForPreset(c.qualityPreset);
                return c;
            }

            return new AtoPlatformOverride
            {
                enabled = true,
                qualityPreset = qualityPreset,
                quality = qualityPreset == AtoQualityPreset.Custom
                    ? (quality != null ? quality.Clone() : new AtoQualitySettings())
                    : AtoQualitySettings.ForPreset(qualityPreset),
                generateAtlas = generateAtlas,
                experimentalNpot = experimentalNpot,
                minPadding = minPadding,
                minDensity = minDensity,
                maxDensity = maxDensity,
                formats = formats != null ? formats.Clone() : new AtoFormatSettings(),
                dedupeMaterials = dedupeMaterials,
                dedupeTextures = dedupeTextures,
                verboseLog = verboseLog
            };
        }

        private void OnValidate()
        {
            if (quality == null) quality = new AtoQualitySettings();
            if (formats == null) formats = new AtoFormatSettings();
            if (pc == null) pc = new AtoPlatformOverride();
            if (android == null) android = new AtoPlatformOverride();
            if (ios == null) ios = new AtoPlatformOverride();
            if (whitelist == null) whitelist = new List<UnityEngine.Object>();

            if (qualityPreset != AtoQualityPreset.Custom)
                quality = AtoQualitySettings.ForPreset(qualityPreset);

            if ((int)minDensity > (int)maxDensity)
                maxDensity = minDensity;

            SyncOverridePreset(pc);
            SyncOverridePreset(android);
            SyncOverridePreset(ios);
        }

        private static void SyncOverridePreset(AtoPlatformOverride ov)
        {
            if (ov == null) return;
            if (ov.quality == null) ov.quality = new AtoQualitySettings();
            if (ov.formats == null) ov.formats = new AtoFormatSettings();
            if (ov.qualityPreset != AtoQualityPreset.Custom)
                ov.quality = AtoQualitySettings.ForPreset(ov.qualityPreset);
        }
    }
}
