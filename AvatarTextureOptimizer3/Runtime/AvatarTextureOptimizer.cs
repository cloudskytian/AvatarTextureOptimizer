// English: Avatar-root component. Only one allowed under an avatar with VRCAvatarDescriptor.
// 中文：挂在 Avatar 根上的组件。同一 Avatar 子树只允许一个，且必须有 VRCAvatarDescriptor。
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace net.fosa.ato
{
    [DisallowMultipleComponent]
    [AddComponentMenu("FOSA/Avatar Texture Optimizer")]
    [HelpURL("https://github.com/fosa/avatar-texture-optimizer")]
    public class AvatarTextureOptimizer : MonoBehaviour
#if ATO_VRCSDK3
        , VRC.SDKBase.IEditorOnly
#endif
    {
        [Tooltip("Generate atlases (default on). Off: scale whole textures, keep original UVs.")]
        public bool generateAtlas = true;

        [Tooltip("Experimental NPOT atlas sizes (64px steps).")]
        public bool experimentalNpot;

        public AtoQualityPreset qualityPreset = AtoQualityPreset.High;
        public AtoQualityThresholds quality = AtoQualityThresholds.ForPreset(AtoQualityPreset.High);

        public AtoMinPadding minPadding = AtoMinPadding.Px4;
        public AtoPixelDensity minPixelDensity = AtoPixelDensity.D2048;
        public AtoPixelDensity maxPixelDensity = AtoPixelDensity.D4096;

        public AtoCompressionSet compression = new AtoCompressionSet();

        public bool dedupeTextures = true;
        public bool dedupeMaterials = true;

        [Tooltip("Verbose [ATO] logs for advanced users.")]
        public bool verboseLogs = true;

        public AtoLanguageMode language = AtoLanguageMode.Auto;

        [Tooltip("Any referenced Texture2D / Material / Renderer / AnimationClip / GameObject. All textures they reference skip ALL optimization.")]
        public List<ObjectRef> whitelist = new List<ObjectRef>();

        public bool overridePC;
        public bool overrideAndroid;
        public bool overrideIOS;
        public AtoPlatformSettings pc = new AtoPlatformSettings();
        public AtoPlatformSettings android = new AtoPlatformSettings();
        public AtoPlatformSettings ios = new AtoPlatformSettings();

        [System.Serializable]
        public class ObjectRef
        {
            public Object target;
        }

        private void OnValidate()
        {
            if (qualityPreset != AtoQualityPreset.Custom)
                quality = AtoQualityThresholds.ForPreset(qualityPreset);
            if (pc != null && overridePC) pc.ApplyPresetIfNotCustom();
            if (android != null && overrideAndroid) android.ApplyPresetIfNotCustom();
            if (ios != null && overrideIOS) ios.ApplyPresetIfNotCustom();
        }

        /// <summary>Resolve effective settings for a platform. / 解析某平台的有效设置。</summary>
        public AtoPlatformSettings Resolve(AtoPlatform platform)
        {
            AtoPlatformSettings ov = null;
            if (platform == AtoPlatform.PC && overridePC) ov = pc;
            else if (platform == AtoPlatform.Android && overrideAndroid) ov = android;
            else if (platform == AtoPlatform.iOS && overrideIOS) ov = ios;

            var s = new AtoPlatformSettings
            {
                enabled = true,
                qualityPreset = qualityPreset,
                thresholds = quality,
                generateAtlas = generateAtlas,
                experimentalNpot = experimentalNpot,
                minPadding = minPadding,
                minDensity = minPixelDensity,
                maxDensity = maxPixelDensity,
                compression = CloneCompression(compression),
                dedupeTextures = dedupeTextures,
                dedupeMaterials = dedupeMaterials
            };
            if (ov == null) return s;
            s.qualityPreset = ov.qualityPreset;
            s.thresholds = ov.thresholds;
            s.generateAtlas = ov.generateAtlas;
            s.experimentalNpot = ov.experimentalNpot;
            s.minPadding = ov.minPadding;
            s.minDensity = ov.minDensity;
            s.maxDensity = ov.maxDensity;
            s.compression = CloneCompression(ov.compression);
            s.dedupeTextures = ov.dedupeTextures;
            s.dedupeMaterials = ov.dedupeMaterials;
            return s;
        }

        private static AtoCompressionSet CloneCompression(AtoCompressionSet c)
        {
            if (c == null) return new AtoCompressionSet();
            return new AtoCompressionSet
            {
                opaque = c.opaque,
                transparent = c.transparent,
                normal = c.normal,
                gray = c.gray,
                mipStreamingOpaque = c.mipStreamingOpaque,
                mipStreamingTransparent = c.mipStreamingTransparent,
                mipStreamingNormal = c.mipStreamingNormal,
                mipStreamingGray = c.mipStreamingGray
            };
        }
    }
}
