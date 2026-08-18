using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Net.Fosa.AvatarTextureOptimizer
{
    /// <summary>
    /// Root component. One per avatar, must sit on the VRCAvatarDescriptor object.
    /// 根组件：每个 Avatar 仅允许一个，必须挂在带 VRCAvatarDescriptor 的对象上。
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("FOSA/Avatar Texture Optimizer")]
    [HelpURL("https://github.com/fosa/avatar-texture-optimizer")]
    public class AvatarTextureOptimizerComponent : MonoBehaviour
#if ATO_VRCSDK3
        , VRC.SDKBase.IEditorOnly
#endif
    {
        [Header("General / 常规")]
        public AtoLanguageMode language = AtoLanguageMode.Auto;
        public bool verboseLogging;
        public bool generateAtlas = true;
        public bool experimentalNpot;

        [Header("Quality / 质量")]
        public AtoQualityPreset qualityPreset = AtoQualityPreset.High;
        public AtoQualityParameters quality = null;
        public AtoPixelDensity minPixelDensity = AtoPixelDensity.Px2048;
        public AtoPixelDensity maxPixelDensity = AtoPixelDensity.Px4096;
        public AtoMinPadding minPadding = AtoMinPadding.Px4;

        [Header("Formats / 压缩格式")]
        public AtoOpaqueFormat opaqueFormat = AtoOpaqueFormat.Auto;
        public AtoTransparentFormat transparentFormat = AtoTransparentFormat.Auto;
        public AtoNormalFormat normalFormat = AtoNormalFormat.Auto;
        public AtoGrayFormat grayFormat = AtoGrayFormat.Auto;
        public bool mipStreamingAlbedo = true;
        public bool mipStreamingNormal = true;
        public bool mipStreamingMask = true;
        public bool mipStreamingGray = true;

        [Header("Dedup / 去重")]
        public bool deduplicateMaterials = true;
        public bool deduplicateTextures = true;

        [Header("Platform override / 平台覆盖")]
        public AtoPlatformOverride pc = new AtoPlatformOverride();
        public AtoPlatformOverride android = new AtoPlatformOverride();
        public AtoPlatformOverride ios = new AtoPlatformOverride();

        [Header("Whitelist / 白名单")]
        [Tooltip("Any referenced Texture2D under these objects is fully skipped. / 这些对象引用到的贴图全部跳过优化。")]
        public List<ObjectRef> whitelist = new List<ObjectRef>();

        [System.Serializable]
        public class ObjectRef
        {
            public Object target;
        }

        void Reset()
        {
            quality = AtoQualityParameters.ForPreset(AtoQualityPreset.High);
        }

        void OnValidate()
        {
            if (quality == null)
                quality = AtoQualityParameters.ForPreset(qualityPreset);
            else if (qualityPreset != AtoQualityPreset.Custom)
            {
                var fresh = AtoQualityParameters.ForPreset(qualityPreset);
                quality.targetQuality = fresh.targetQuality;
                quality.msSsimMin = fresh.msSsimMin;
                quality.ciede2000Max = fresh.ciede2000Max;
                quality.alphaRmseMax = fresh.alphaRmseMax;
                quality.cutoutIouMin = fresh.cutoutIouMin;
                quality.normalAngleDegMax = fresh.normalAngleDegMax;
                quality.normalP95AngleDegMax = fresh.normalP95AngleDegMax;
                quality.grayRmseMax = fresh.grayRmseMax;
            }

            if ((int)minPixelDensity > (int)maxPixelDensity)
                maxPixelDensity = minPixelDensity;
        }

        /// <summary>
        /// Effective settings for a platform. / 某平台的生效设置。
        /// </summary>
        public AtoPlatformOverride Resolve(AtoPlatform platform)
        {
            AtoPlatformOverride ov = null;
            if (platform == AtoPlatform.PC && pc != null && pc.enabled) ov = pc;
            else if (platform == AtoPlatform.Android && android != null && android.enabled) ov = android;
            else if (platform == AtoPlatform.iOS && ios != null && ios.enabled) ov = ios;

            var r = new AtoPlatformOverride
            {
                enabled = true,
                qualityPreset = ov != null ? ov.qualityPreset : qualityPreset,
                quality = (ov != null && ov.quality != null ? ov.quality : quality)?.Clone()
                          ?? AtoQualityParameters.ForPreset(AtoQualityPreset.High),
                generateAtlas = ov != null ? ov.generateAtlas : generateAtlas,
                experimentalNpot = ov != null ? ov.experimentalNpot : experimentalNpot,
                minPadding = ov != null ? ov.minPadding : minPadding,
                minPixelDensity = ov != null ? ov.minPixelDensity : minPixelDensity,
                maxPixelDensity = ov != null ? ov.maxPixelDensity : maxPixelDensity,
                opaqueFormat = ov != null ? ov.opaqueFormat : opaqueFormat,
                transparentFormat = ov != null ? ov.transparentFormat : transparentFormat,
                normalFormat = ov != null ? ov.normalFormat : normalFormat,
                grayFormat = ov != null ? ov.grayFormat : grayFormat,
                mipStreamingAlbedo = ov != null ? ov.mipStreamingAlbedo : mipStreamingAlbedo,
                mipStreamingNormal = ov != null ? ov.mipStreamingNormal : mipStreamingNormal,
                mipStreamingMask = ov != null ? ov.mipStreamingMask : mipStreamingMask,
                mipStreamingGray = ov != null ? ov.mipStreamingGray : mipStreamingGray,
                deduplicateMaterials = ov != null ? ov.deduplicateMaterials : deduplicateMaterials,
                deduplicateTextures = ov != null ? ov.deduplicateTextures : deduplicateTextures
            };
            return r;
        }
    }
}
