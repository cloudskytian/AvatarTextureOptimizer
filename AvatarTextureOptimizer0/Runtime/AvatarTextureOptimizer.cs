using System;
using System.Collections.Generic;
using nadena.dev.ndmf;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Fosa.AvatarTextureOptimizer
{
    [DisallowMultipleComponent]
    [AddComponentMenu("Avatar Texture Optimizer/Avatar Texture Optimizer")]
    public sealed class AvatarTextureOptimizer : MonoBehaviour, INDMFEditorOnly
    {
        public ATOOptimizationSettings common = new ATOOptimizationSettings();
        public ATOPlatformOverride pc = NewOverride(ATOPlatform.PC);
        public ATOPlatformOverride android = NewOverride(ATOPlatform.Android);
        public ATOPlatformOverride ios = NewOverride(ATOPlatform.IOS);
        public List<Object> whitelist = new List<Object>();
        public ATOLanguage language = ATOLanguage.Auto;
        public bool verboseLogging;
        public ATODebugSettings debug = new ATODebugSettings();

        public ATOOptimizationSettings Resolve(ATOPlatform platform)
        {
            var selected = platform == ATOPlatform.Android ? android : platform == ATOPlatform.IOS ? ios : pc;
            var source = selected != null && selected.enabled && selected.settings != null ? selected.settings : common;
            var resolved = (source ?? new ATOOptimizationSettings()).DeepClone();
            // OnValidate is not a build-time safety boundary: YAML edits and extensions can provide values
            // without invoking it. Always sanitize the detached settings used by the pipeline.
            // OnValidate 不是构建期安全边界；流水线必须再次清理实际使用的独立设置副本。
            SanitizeSettings(resolved);
            return resolved;
        }

        private static ATOPlatformOverride NewOverride(ATOPlatform platform)
        {
            return new ATOPlatformOverride { platform = platform, settings = new ATOOptimizationSettings() };
        }

        private void Reset()
        {
            common.quality.ApplyPreset(common.qualityPreset);
        }

        private void OnValidate()
        {
            if (common == null) common = new ATOOptimizationSettings();
            if (pc == null) pc = NewOverride(ATOPlatform.PC);
            if (android == null) android = NewOverride(ATOPlatform.Android);
            if (ios == null) ios = NewOverride(ATOPlatform.IOS);
            if (debug == null) debug = new ATODebugSettings();
            if (whitelist == null) whitelist = new List<Object>();
            pc.platform = ATOPlatform.PC; android.platform = ATOPlatform.Android; ios.platform = ATOPlatform.IOS;
            if (pc.settings == null) pc.settings = new ATOOptimizationSettings();
            if (android.settings == null) android.settings = new ATOOptimizationSettings();
            if (ios.settings == null) ios.settings = new ATOOptimizationSettings();
            SanitizeSettings(common); SanitizeSettings(pc.settings); SanitizeSettings(android.settings); SanitizeSettings(ios.settings);
        }

        /// <summary>
        /// Clamps and repairs settings before they cross a build safety boundary. Extensions that change
        /// settings during BeforeAnalysis should call this as well; the pipeline calls it unconditionally.
        /// 在设置跨越构建安全边界前进行限制和修复；流水线会无条件调用。
        /// </summary>
        public static void SanitizeSettings(ATOOptimizationSettings settings)
        {
            if (settings == null) return;
            if (settings.quality == null) settings.quality = new ATOQualitySettings();
            if (settings.customQuality == null) settings.customQuality = ATOQualitySettings.CreateCustomDefaults();
            if (settings.opaque == null) settings.opaque = new ATOTextureClassSettings();
            if (settings.alpha == null) settings.alpha = new ATOTextureClassSettings();
            if (settings.normal == null) settings.normal = new ATOTextureClassSettings { compression = ATOCompression.BC5 };
            if (settings.grayscale == null) settings.grayscale = new ATOTextureClassSettings();

            if (!Enum.IsDefined(typeof(ATOQualityPreset), settings.qualityPreset))
                settings.qualityPreset = ATOQualityPreset.Balanced;
            if (!Enum.IsDefined(typeof(ATOMinimumPadding), settings.minimumPadding))
                settings.minimumPadding = ATOMinimumPadding.Pixels4;
            if (!Enum.IsDefined(typeof(ATOPixelDensity), settings.minimumPixelDensity))
                settings.minimumPixelDensity = ATOPixelDensity.Density2048;
            if (!Enum.IsDefined(typeof(ATOPixelDensity), settings.maximumPixelDensity))
                settings.maximumPixelDensity = ATOPixelDensity.Density4096;
            ValidateClass(settings.opaque, ATOCompression.Auto);
            ValidateClass(settings.alpha, ATOCompression.Auto);
            ValidateClass(settings.normal, ATOCompression.BC5);
            ValidateClass(settings.grayscale, ATOCompression.Auto);

            if ((int)settings.minimumPixelDensity > (int)settings.maximumPixelDensity)
                settings.minimumPixelDensity = settings.maximumPixelDensity;
            settings.maximumAtlasSize = Mathf.Clamp(settings.maximumAtlasSize, 256, 8192);
            if (settings.qualityPreset != ATOQualityPreset.Custom) settings.quality.ApplyPreset(settings.qualityPreset);
            ValidateQuality(settings.quality, new ATOQualitySettings());
            ValidateQuality(settings.customQuality, ATOQualitySettings.CreateCustomDefaults());
        }

        private static void ValidateClass(ATOTextureClassSettings settings, ATOCompression fallback)
        {
            if (!Enum.IsDefined(typeof(ATOCompression), settings.compression)) settings.compression = fallback;
        }

        private static void ValidateQuality(ATOQualitySettings settings, ATOQualitySettings fallback)
        {
            settings.targetQuality = Unit(settings.targetQuality, fallback.targetQuality);
            settings.minMsSsim = Unit(settings.minMsSsim, fallback.minMsSsim);
            settings.minSsim = Unit(settings.minSsim, fallback.minSsim);
            settings.minCutoutIoU = Unit(settings.minCutoutIoU, fallback.minCutoutIoU);
            settings.maxDeltaE2000 = NonNegative(settings.maxDeltaE2000, fallback.maxDeltaE2000);
            settings.maxBlendAlphaRmse = NonNegative(settings.maxBlendAlphaRmse, fallback.maxBlendAlphaRmse);
            settings.maxNormalMeanDegrees = NonNegative(settings.maxNormalMeanDegrees, fallback.maxNormalMeanDegrees);
            settings.maxNormalP95Degrees = NonNegative(settings.maxNormalP95Degrees, fallback.maxNormalP95Degrees);
            settings.maxGrayscaleRmse = NonNegative(settings.maxGrayscaleRmse, fallback.maxGrayscaleRmse);
        }

        private static float Unit(float value, float fallback)
        {
            return float.IsNaN(value) || float.IsInfinity(value) ? fallback : Mathf.Clamp01(value);
        }

        private static float NonNegative(float value, float fallback)
        {
            return float.IsNaN(value) || float.IsInfinity(value) ? fallback : Mathf.Max(0f, value);
        }
    }
}
