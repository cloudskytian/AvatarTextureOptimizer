using System;
using System.Collections.Generic;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer
{
    /// <summary>
    /// Quality presets exposed by the inspector. / 检视面板中显示的质量挡位。
    /// </summary>
    public enum ATOQualityPreset
    {
        Economy = 0,
        Balanced = 1,
        High = 2,
        NearLossless = 3,
        Custom = 4
    }

    /// <summary>
    /// Build target families supported by the optimizer. / 优化器支持的构建平台族。
    /// </summary>
    public enum ATOPlatform
    {
        PC = 0,
        Android = 1,
        iOS = 2
    }

    /// <summary>
    /// Logical texture categories used for safe format and quality decisions. / 用于安全格式与质量决策的纹理类别。
    /// </summary>
    public enum ATOTextureCategory
    {
        Opaque = 0,
        Transparent = 1,
        Normal = 2,
        Grayscale = 3,
        Unknown = 4
    }

    /// <summary>
    /// Safe format choices. The editor validates every choice against content and platform. / 安全格式选项，编辑器会根据内容与平台再次验证。
    /// </summary>
    public enum ATOFormatChoice
    {
        Automatic = 0,
        RGBA32 = 1,
        RGB24 = 2,
        RG8 = 3,
        R8 = 4,
        BC7 = 5,
        BC3 = 6,
        BC1 = 7,
        ETC2RGBA8 = 8,
        ETC2RGB = 9,
        ASTC6x6 = 10,
        ASTC4x4 = 11,
        PVRTC_RGBA4 = 12
    }

    /// <summary>
    /// Localization selection mode. / 本地化选择模式。
    /// </summary>
    public enum ATOLocalizationMode
    {
        Auto = 0,
        English = 1,
        SimplifiedChinese = 2
    }

    /// <summary>
    /// Convenient pixel density presets. / 方便用户选择的像素密度挡位。
    /// </summary>
    public enum ATOPixelDensityPreset
    {
        Custom = 0,
        P512 = 512,
        P1024 = 1024,
        P2048 = 2048,
        P4096 = 4096,
        P8192 = 8192
    }

    /// <summary>
    /// Normalized quality controls. A value of one means near-lossless for that metric. / 归一化质量控制，1 表示该指标近无损。
    /// </summary>
    [Serializable]
    public struct ATOQualityParameters
    {
        [Range(0f, 1f)] public float targetQuality;
        [Range(0f, 1f)] public float msSsimQuality;
        [Range(0f, 1f)] public float deltaEQuality;
        [Range(0f, 1f)] public float alphaQuality;
        [Range(0f, 1f)] public float normalQuality;
        [Range(0f, 1f)] public float grayscaleQuality;
        [Range(0f, 1f)] public float mipQuality;

        /// <summary>
        /// Returns the near-lossless custom baseline. / 返回近无损自定义基线。
        /// </summary>
        public static ATOQualityParameters NearLossless()
        {
            return new ATOQualityParameters
            {
                targetQuality = 1f,
                msSsimQuality = 1f,
                deltaEQuality = 1f,
                alphaQuality = 1f,
                normalQuality = 1f,
                grayscaleQuality = 1f,
                mipQuality = 1f
            };
        }

        /// <summary>
        /// Converts a public preset to explicit parameters. / 将公开挡位转换为明确参数。
        /// </summary>
        public static ATOQualityParameters FromPreset(ATOQualityPreset preset)
        {
            switch (preset)
            {
                case ATOQualityPreset.Economy:
                    return new ATOQualityParameters
                    {
                        targetQuality = 0.72f,
                        msSsimQuality = 0.72f,
                        deltaEQuality = 0.68f,
                        alphaQuality = 0.74f,
                        normalQuality = 0.70f,
                        grayscaleQuality = 0.72f,
                        mipQuality = 0.70f
                    };
                case ATOQualityPreset.High:
                    return new ATOQualityParameters
                    {
                        targetQuality = 0.96f,
                        msSsimQuality = 0.96f,
                        deltaEQuality = 0.95f,
                        alphaQuality = 0.97f,
                        normalQuality = 0.95f,
                        grayscaleQuality = 0.96f,
                        mipQuality = 0.95f
                    };
                case ATOQualityPreset.NearLossless:
                    return NearLossless();
                case ATOQualityPreset.Balanced:
                default:
                    return new ATOQualityParameters
                    {
                        targetQuality = 0.88f,
                        msSsimQuality = 0.88f,
                        deltaEQuality = 0.86f,
                        alphaQuality = 0.90f,
                        normalQuality = 0.86f,
                        grayscaleQuality = 0.88f,
                        mipQuality = 0.86f
                    };
            }
        }
    }

    /// <summary>
    /// Platform-specific texture and atlas options. / 平台专属的纹理与图集选项。
    /// </summary>
    [Serializable]
    public sealed class ATOPlatformOptions
    {
        public bool optimizeTextures = true;
        public bool optimizeMaterials = true;
        public bool generateAtlases = true;
        public bool experimentalNpotAtlases = false;
        public bool enableMipStreaming = true;
        public bool allowTextureFormatOverride = false;
        public int maxSourceTextureSize = 8192;
        public int maxAtlasSize = 8192;
        public int atlasMinimumSize = 64;
        public ATOFormatChoice transparentFormat = ATOFormatChoice.Automatic;
        public ATOFormatChoice opaqueFormat = ATOFormatChoice.Automatic;
        public ATOFormatChoice normalFormat = ATOFormatChoice.Automatic;
        public ATOFormatChoice grayscaleFormat = ATOFormatChoice.Automatic;
        public ATOFormatChoice fallbackFormat = ATOFormatChoice.Automatic;

        /// <summary>
        /// Makes a defensive copy so overrides never share mutable state. / 深拷贝，避免平台覆盖之间共享可变状态。
        /// </summary>
        public ATOPlatformOptions Clone()
        {
            return new ATOPlatformOptions
            {
                optimizeTextures = optimizeTextures,
                optimizeMaterials = optimizeMaterials,
                generateAtlases = generateAtlases,
                experimentalNpotAtlases = experimentalNpotAtlases,
                enableMipStreaming = enableMipStreaming,
                allowTextureFormatOverride = allowTextureFormatOverride,
                maxSourceTextureSize = maxSourceTextureSize,
                maxAtlasSize = maxAtlasSize,
                atlasMinimumSize = atlasMinimumSize,
                transparentFormat = transparentFormat,
                opaqueFormat = opaqueFormat,
                normalFormat = normalFormat,
                grayscaleFormat = grayscaleFormat,
                fallbackFormat = fallbackFormat
            };
        }
    }

    /// <summary>
    /// A platform override. / 单个平台的覆盖配置。
    /// </summary>
    [Serializable]
    public sealed class ATOPlatformOverride
    {
        public bool enabled;
        public ATOPlatformOptions options = new ATOPlatformOptions();
    }

    /// <summary>
    /// The single user-facing component. It is intentionally runtime-safe and editor-independent. / 唯一面向用户的组件，故意保持运行时安全且不依赖 Editor。
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Fosa/Avatar Texture Optimizer")]
    public sealed class AvatarTextureOptimizer : MonoBehaviour
    {
        [Header("Core / 核心")]
        public bool generateAtlases = true;
        public bool optimizeMaterials = true;
        public bool optimizeTextures = true;
        public bool scanAnimationReferences = true;
        public bool enableSourceDeduplication = true;
        public bool enableMaterialDeduplication = true;

        [Header("Quality / 质量")]
        public ATOQualityPreset qualityPreset = ATOQualityPreset.Balanced;
        public ATOQualityParameters qualityParameters = default(ATOQualityParameters);
        [SerializeField] private int lastAppliedPreset = -1;

        [Header("Pixel density / 像素密度")]
        public ATOPixelDensityPreset pixelDensityPreset = ATOPixelDensityPreset.Custom;
        public int minimumPixelsPerMeter = 2048;
        public int maximumPixelsPerMeter = 4096;

        [Header("Atlas / 图集")]
        [Min(4)] public int minimumPadding = 4;
        [Min(4)] public int rasterGranularity = 4;
        public bool allowUVTranslationIntoUnitSquare = true;

        [Header("Platforms / 平台")]
        public ATOPlatformOptions commonOptions = new ATOPlatformOptions();
        public ATOPlatformOverride pcOverride = new ATOPlatformOverride();
        public ATOPlatformOverride androidOverride = new ATOPlatformOverride();
        public ATOPlatformOverride iosOverride = new ATOPlatformOverride();

        [Header("Safety and diagnostics / 安全与诊断")]
        public List<UnityEngine.Object> whitelist = new List<UnityEngine.Object>();
        public ATOLocalizationMode localization = ATOLocalizationMode.Auto;
        public bool showProgress = true;
        public bool detailedLogging = false;
        public bool keepTemporaryAssetsOnCancel = true;

        /// <summary>
        /// Applies a preset only when the preset actually changes. / 只有挡位真正变化时才应用挡位参数。
        /// </summary>
        public void EnsureQualityParameters()
        {
            if (qualityPreset == ATOQualityPreset.Custom)
            {
                if (lastAppliedPreset < 0)
                {
                    qualityParameters = ATOQualityParameters.NearLossless();
                    lastAppliedPreset = (int)ATOQualityPreset.Custom;
                }
                return;
            }

            if (lastAppliedPreset != (int)qualityPreset || qualityParameters.targetQuality <= 0f)
            {
                qualityParameters = ATOQualityParameters.FromPreset(qualityPreset);
                lastAppliedPreset = (int)qualityPreset;
            }
        }

        /// <summary>
        /// Resolves the effective platform options without mutating serialized settings. / 解析平台最终配置且不修改序列化设置。
        /// </summary>
        public ATOPlatformOptions ResolvePlatformOptions(ATOPlatform platform)
        {
            ATOPlatformOverride overrideSettings;
            switch (platform)
            {
                case ATOPlatform.Android:
                    overrideSettings = androidOverride;
                    break;
                case ATOPlatform.iOS:
                    overrideSettings = iosOverride;
                    break;
                default:
                    overrideSettings = pcOverride;
                    break;
            }

            return overrideSettings != null && overrideSettings.enabled && overrideSettings.options != null
                ? overrideSettings.options.Clone()
                : (commonOptions == null ? new ATOPlatformOptions() : commonOptions.Clone());
        }

        /// <summary>
        /// Returns whether an object or one of its containing objects is explicitly whitelisted. / 判断对象或其父级对象是否在白名单中。
        /// </summary>
        public bool IsWhitelisted(UnityEngine.Object candidate)
        {
            if (candidate == null || whitelist == null) return false;
            for (int i = 0; i < whitelist.Count; i++)
            {
                UnityEngine.Object entry = whitelist[i];
                if (entry == null) continue;
                if (entry == candidate) return true;

                GameObject candidateObject = candidate as GameObject;
                Component candidateComponent = candidate as Component;
                GameObject entryObject = entry as GameObject;
                Component entryComponent = entry as Component;
                Transform candidateTransform = candidateObject != null
                    ? candidateObject.transform
                    : candidateComponent != null ? candidateComponent.transform : null;
                Transform entryTransform = entryObject != null
                    ? entryObject.transform
                    : entryComponent != null ? entryComponent.transform : null;
                if (candidateTransform != null && entryTransform != null &&
                    (candidateTransform == entryTransform || candidateTransform.IsChildOf(entryTransform)))
                    return true;
            }

            return false;
        }

        private void OnValidate()
        {
            EnsureQualityParameters();
            minimumPadding = Mathf.Clamp(minimumPadding, 4, 256);
            rasterGranularity = 4;
            minimumPixelsPerMeter = Mathf.Clamp(minimumPixelsPerMeter, 512, 8192);
            maximumPixelsPerMeter = Mathf.Clamp(maximumPixelsPerMeter, minimumPixelsPerMeter, 8192);
            if (pixelDensityPreset != ATOPixelDensityPreset.Custom)
            {
                int density = (int)pixelDensityPreset;
                minimumPixelsPerMeter = density;
                maximumPixelsPerMeter = density;
            }
        }
    }
}
