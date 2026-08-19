using System.Collections.Generic;
using UnityEngine;
#if ATO_VRCSDK3_AVATARS
using VRC.SDKBase;
#endif

namespace Fosa.AvatarTextureOptimizer
{
    /// <summary>
    /// Root component. Put it on the same GameObject as VRCAvatarDescriptor.
    /// Only one instance is allowed on an avatar hierarchy.
    /// 根组件。挂在与 VRCAvatarDescriptor 同一物体上。一个 Avatar 层级只允许一个。
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("FOSA/Avatar Texture Optimizer")]
    [HelpURL("https://github.com/fosa/avatar-texture-optimizer")]
    public class AvatarTextureOptimizer : MonoBehaviour
#if ATO_VRCSDK3_AVATARS
        , IEditorOnly
#endif
    {
        [Header("Atlas / 图集")]
        [Tooltip("When off: scale whole textures, do not cull unused UV, do not rearrange UV. / 关闭时：整图缩放，不剔除未使用 UV，不重排 UV。")]
        public bool generateAtlas = true;

        [Tooltip("Experimental NPOT atlas sizes (64 px step). Verified with MipStreaming and Crunch. / 实验性非 2 次幂图集（64 步进）。已验证 MipStreaming 与 Crunch。")]
        public bool experimentalNpot;

        public AtoMinPadding minPadding = AtoMinPadding.Px4;

        [Header("Quality / 质量")]
        public AtoQualityPreset qualityPreset = AtoQualityPreset.High;

        [Tooltip("Active thresholds. Overwritten when the preset is not Custom. / 当前阈值。非 Custom 挡位切换时会被覆盖。")]
        public AtoQualityThresholds quality = new AtoQualityThresholds();

        [Tooltip("User Custom preset. Never overwritten by other presets. Defaults are all 1. / 自定义挡位，不被其他挡位覆盖，默认全 1。")]
        public AtoQualityThresholds customQuality = new AtoQualityThresholds();

        [Header("Pixel density / 像素密度")]
        public AtoPixelDensityPreset minPixelDensity = AtoPixelDensityPreset.Px2048;
        public AtoPixelDensityPreset maxPixelDensity = AtoPixelDensityPreset.Px4096;

        [Header("Dedup / 去重")]
        public bool deduplicateMaterials = true;
        public bool deduplicateTextures = true;

        [Header("Whitelist / 白名单")]
        [Tooltip("Any object type. Every Texture2D referenced by these objects skips ALL optimisation. / 不限类型。这些对象引用到的全部 Texture2D 跳过一切优化。")]
        public List<Object> whitelist = new List<Object>();

        [Header("Platform / 平台")]
        public AtoPlatform platform = AtoPlatform.Auto;

        public bool overridePC;
        public bool overrideAndroid;
        public bool overrideIOS;

        public AtoPlatformSettings sharedPlatform = new AtoPlatformSettings();
        public AtoPlatformSettings pcPlatform = new AtoPlatformSettings();
        public AtoPlatformSettings androidPlatform = new AtoPlatformSettings();
        public AtoPlatformSettings iosPlatform = new AtoPlatformSettings();

        [Header("Language / 语言")]
        public AtoLanguageMode language = AtoLanguageMode.Auto;

        [Header("Debug / 调试")]
        [Tooltip("Verbose [ATO] logs for advanced users. / 高级用户的详细 [ATO] 日志。")]
        public bool verboseLogging;

        /// <summary>
        /// Applies preset values onto <see cref="quality"/>. Custom copies from <see cref="customQuality"/>.
        /// 将挡位写入 quality。Custom 从 customQuality 拷贝。
        /// </summary>
        public void ApplyPresetToQuality()
        {
            if (quality == null) quality = new AtoQualityThresholds();
            if (customQuality == null) customQuality = new AtoQualityThresholds();

            if (qualityPreset == AtoQualityPreset.Custom)
            {
                quality.CopyFrom(customQuality);
                return;
            }

            quality.CopyFrom(GetBuiltinPreset(qualityPreset));
        }

        /// <summary>
        /// Built-in thresholds. Sources: Wang et al. MS-SSIM; CIEDE2000 JND ≈ 1; game-art normal tolerances.
        /// 内置阈值。来源：Wang 等 MS-SSIM；CIEDE2000 恰可辨约 1；游戏法线公差。
        /// </summary>
        public static AtoQualityThresholds GetBuiltinPreset(AtoQualityPreset preset)
        {
            switch (preset)
            {
                case AtoQualityPreset.Lossless:
                    return new AtoQualityThresholds
                    {
                        msSsim = 1f, deltaE = 0f, alphaRmse = 0f, cutoutIou = 1f,
                        normalMeanDegrees = 0f, normalP95Degrees = 0f, grayRmse = 0f
                    };
                case AtoQualityPreset.Ultra:
                    return new AtoQualityThresholds
                    {
                        msSsim = 0.990f, deltaE = 1.0f, alphaRmse = 0.010f, cutoutIou = 0.995f,
                        normalMeanDegrees = 2.0f, normalP95Degrees = 4.0f, grayRmse = 0.010f
                    };
                case AtoQualityPreset.High:
                    return new AtoQualityThresholds
                    {
                        msSsim = 0.970f, deltaE = 2.0f, alphaRmse = 0.020f, cutoutIou = 0.985f,
                        normalMeanDegrees = 4.0f, normalP95Degrees = 8.0f, grayRmse = 0.020f
                    };
                case AtoQualityPreset.Medium:
                    return new AtoQualityThresholds
                    {
                        msSsim = 0.940f, deltaE = 3.5f, alphaRmse = 0.040f, cutoutIou = 0.970f,
                        normalMeanDegrees = 6.0f, normalP95Degrees = 12.0f, grayRmse = 0.040f
                    };
                case AtoQualityPreset.Low:
                    return new AtoQualityThresholds
                    {
                        msSsim = 0.900f, deltaE = 5.0f, alphaRmse = 0.080f, cutoutIou = 0.940f,
                        normalMeanDegrees = 10.0f, normalP95Degrees = 18.0f, grayRmse = 0.080f
                    };
                default:
                    return new AtoQualityThresholds();
            }
        }

        public bool IsLosslessPreset =>
            qualityPreset == AtoQualityPreset.Lossless ||
            (qualityPreset == AtoQualityPreset.Custom && (customQuality == null || customQuality.IsNearLossless));

        public AtoPlatformSettings ResolvePlatformSettings(AtoPlatform resolved)
        {
            var shared = sharedPlatform != null ? sharedPlatform.Clone() : new AtoPlatformSettings();
            AtoPlatformSettings over = null;
            bool useOver = false;
            switch (resolved)
            {
                case AtoPlatform.PC:
                    useOver = overridePC;
                    over = pcPlatform;
                    break;
                case AtoPlatform.Android:
                    useOver = overrideAndroid;
                    over = androidPlatform;
                    break;
                case AtoPlatform.iOS:
                    useOver = overrideIOS;
                    over = iosPlatform;
                    break;
            }

            if (useOver && over != null) return over.Clone();
            return shared;
        }

        void Reset()
        {
            generateAtlas = true;
            qualityPreset = AtoQualityPreset.High;
            customQuality = new AtoQualityThresholds();
            ApplyPresetToQuality();
            minPixelDensity = AtoPixelDensityPreset.Px2048;
            maxPixelDensity = AtoPixelDensityPreset.Px4096;
            minPadding = AtoMinPadding.Px4;
            deduplicateMaterials = true;
            deduplicateTextures = true;
            sharedPlatform = new AtoPlatformSettings();
        }

        void OnValidate()
        {
            if (quality == null) quality = new AtoQualityThresholds();
            if (customQuality == null) customQuality = new AtoQualityThresholds();
            if (sharedPlatform == null) sharedPlatform = new AtoPlatformSettings();
            if (pcPlatform == null) pcPlatform = new AtoPlatformSettings();
            if (androidPlatform == null) androidPlatform = new AtoPlatformSettings();
            if (iosPlatform == null) iosPlatform = new AtoPlatformSettings();
            if (whitelist == null) whitelist = new List<Object>();

            if (qualityPreset != AtoQualityPreset.Custom)
            {
                quality.CopyFrom(GetBuiltinPreset(qualityPreset));
            }
            else
            {
                quality.CopyFrom(customQuality);
            }

            if ((int)maxPixelDensity < (int)minPixelDensity)
            {
                maxPixelDensity = minPixelDensity;
            }
        }
    }
}
