// English: Avatar-root component. One per avatar subtree; must sit on the VRCAvatarDescriptor object.
// 中文：Avatar 根组件。每个 Avatar 子树只允许一个，且必须挂在含 VRCAvatarDescriptor 的对象上。
using System.Collections.Generic;
using UnityEngine;
#if ATO_NDMF
using nadena.dev.ndmf;
#endif
#if ATO_VRCSDK3_AVATARS
using VRC.SDK3.Avatars.Components;
#endif

namespace Net.Fosa.AvatarTextureOptimizer
{
    [DisallowMultipleComponent]
    [AddComponentMenu("FOSA/Avatar Texture Optimizer")]
    [HelpURL("https://github.com/fosa/avatar-texture-optimizer")]
    public class AvatarTextureOptimizer : MonoBehaviour
#if ATO_NDMF
        , INDMFEditorOnly
#endif
    {
        internal const string LogPrefix = "[ATO]";
        internal const string AtlasNamePrefix = "ATO_";
        internal const string GeneratedFolder = "Assets/ATO_Generated";

        [Header("Quality / 质量")]
        public ATOQualityPreset qualityPreset = ATOQualityPreset.High;

        [Tooltip("Advanced numeric thresholds. Changing a non-Custom preset overwrites these.\n高级数值阈值。非 Custom 挡位变化时会被覆盖。")]
        public ATOQualityParameters quality = ATOQualityParameters.FromPreset(ATOQualityPreset.High);

        [Header("Dedup / 去重")]
        [Tooltip("Deduplicate materials that become identical after optimization.\n优化后内容与参数完全相同的材质去重。")]
        public bool deduplicateMaterials = true;

        [Tooltip("Deduplicate textures / atlases that become identical after optimization.\n优化后内容与参数完全相同的贴图/图集去重。")]
        public bool deduplicateTextures = true;

        [Header("Whitelist / 白名单")]
        [Tooltip("Any object type. All Texture2D referenced by these objects skip ALL optimization.\n不限对象类型。这些对象引用的全部 Texture2D 跳过所有优化。")]
        public List<Object> whitelist = new List<Object>();

        [Header("Shared platform defaults / 全平台默认")]
        [Tooltip("Used when a platform override is not enabled.\n未勾选对应平台 override 时使用。")]
        public ATOPlatformSettings shared = new ATOPlatformSettings();

        [Header("Platform override / 平台覆盖")]
        [Tooltip("Force evaluation platform. Auto = current Unity build target.\n强制评估平台。Auto = 当前 Unity 构建目标。")]
        public ATOBuildPlatform platformHint = ATOBuildPlatform.Auto;

        public bool overridePC;
        public ATOPlatformSettings pc = new ATOPlatformSettings();

        public bool overrideAndroid;
        public ATOPlatformSettings android = new ATOPlatformSettings();

        public bool overrideIOS;
        public ATOPlatformSettings ios = new ATOPlatformSettings();

        [Header("Language / 语言")]
        public ATOLanguageMode languageMode = ATOLanguageMode.Auto;

        [Tooltip("BCP-47 tag matching an i18n JSON file name, e.g. en-us / zh-hans.\n与 i18n JSON 文件名对应的 BCP-47 标签，如 en-us / zh-hans。")]
        public string manualLanguage = "en-us";

        [Header("Debug / 调试")]
        [Tooltip("Verbose [ATO] logs for advanced users.\n面向高级用户的详细 [ATO] 日志。")]
        public bool verboseLogging = true;

        [HideInInspector] public ATOQualityPreset lastAppliedPreset = ATOQualityPreset.High;

        private void Reset()
        {
            qualityPreset = ATOQualityPreset.High;
            quality = ATOQualityParameters.FromPreset(ATOQualityPreset.High);
            lastAppliedPreset = ATOQualityPreset.High;
            shared = new ATOPlatformSettings();
            pc = new ATOPlatformSettings();
            android = new ATOPlatformSettings();
            ios = new ATOPlatformSettings();
        }

        private void OnValidate()
        {
            if (quality == null) quality = ATOQualityParameters.FromPreset(qualityPreset);
            if (shared == null) shared = new ATOPlatformSettings();
            if (pc == null) pc = new ATOPlatformSettings();
            if (android == null) android = new ATOPlatformSettings();
            if (ios == null) ios = new ATOPlatformSettings();
            if (whitelist == null) whitelist = new List<Object>();

            // English: Non-Custom preset changes overwrite numeric fields. Custom is never overwritten.
            // 中文：非 Custom 挡位变化覆盖数值。Custom 永不被其他挡位覆盖。
            if (qualityPreset != ATOQualityPreset.Custom && qualityPreset != lastAppliedPreset)
            {
                quality.CopyFrom(ATOQualityParameters.FromPreset(qualityPreset));
                lastAppliedPreset = qualityPreset;
            }
            else if (qualityPreset == ATOQualityPreset.Custom)
            {
                lastAppliedPreset = ATOQualityPreset.Custom;
            }

            if ((int)shared.minPixelDensity > (int)shared.maxPixelDensity)
                shared.maxPixelDensity = shared.minPixelDensity;
        }

        /// <summary>
        /// Resolve the platform block that should be used for the current bake.
        /// 解析本次烘焙应使用的平台参数块。
        /// </summary>
        public ATOPlatformSettings ResolvePlatformSettings(ATOBuildPlatform resolved)
        {
            switch (resolved)
            {
                case ATOBuildPlatform.PC:
                    return overridePC && pc != null ? pc : shared;
                case ATOBuildPlatform.Android:
                    return overrideAndroid && android != null ? android : shared;
                case ATOBuildPlatform.iOS:
                    return overrideIOS && ios != null ? ios : shared;
                default:
                    return shared;
            }
        }

#if ATO_VRCSDK3_AVATARS
        public bool HasAvatarDescriptor
        {
            get { return GetComponent<VRCAvatarDescriptor>() != null; }
        }
#else
        public bool HasAvatarDescriptor
        {
            get { return true; }
        }
#endif
    }
}
