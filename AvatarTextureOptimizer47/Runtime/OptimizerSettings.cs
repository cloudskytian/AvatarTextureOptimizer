using System;
using System.Collections.Generic;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer
{
    /// <summary>
    /// EN: User-facing quality presets. ZH: 面向用户的质量预设。
    /// </summary>
    public enum QualityPreset
    {
        Performance,
        Balanced,
        High,
        NearLossless,
        Custom,
    }

    /// <summary>
    /// EN: Build platform selected for profile resolution. ZH: 用于解析配置的构建平台。
    /// </summary>
    public enum OptimizerPlatform
    {
        Auto,
        PC,
        Android,
        IOS,
    }

    /// <summary>
    /// EN: Minimum atlas padding in output pixels. ZH: 图集输出像素中的最小间距。
    /// </summary>
    public enum MinimumPadding
    {
        Pixels4 = 4,
        Pixels8 = 8,
        Pixels16 = 16,
        Pixels32 = 32,
        Pixels64 = 64,
    }

    /// <summary>
    /// EN: A deliberately small, platform-filtered compression vocabulary. ZH: 有意保持精简并按平台过滤的压缩格式集合。
    /// </summary>
    public enum SafeTextureFormat
    {
        Automatic,
        UncompressedRGBA32,
        BC1,
        BC3,
        BC5,
        BC7,
        ASTC4x4,
        ASTC6x6,
        ASTC8x8,
        ETC2RGB,
        ETC2RGBA8,
        PVRTCRGB4,
        PVRTCRGBA4,
        DXT1Crunched,
        DXT5Crunched,
        ETC1Crunched,
        ETC2RGBA8Crunched,
    }

    /// <summary>
    /// EN: Semantic texture classes used for grouping and safe output settings. ZH: 用于分组与安全输出设置的语义贴图类别。
    /// </summary>
    public enum TextureSemantic
    {
        ColorOpaque,
        ColorAlpha,
        Normal,
        Grayscale,
    }

    /// <summary>
    /// EN: Normalized fidelity targets. A value of one means exact/no resampling.
    /// ZH: 归一化保真目标。值为 1 表示精确/不重采样。
    /// </summary>
    [Serializable]
    public sealed class QualityThresholds
    {
        [Range(0f, 1f)] public float structuralFidelity = 0.970f;
        [Range(0f, 1f)] public float colorFidelity = 0.950f;
        [Range(0f, 1f)] public float alphaFidelity = 0.980f;
        [Range(0f, 1f)] public float normalFidelity = 0.950f;
        [Range(0f, 1f)] public float grayscaleFidelity = 0.970f;

        /// <summary>EN: Effective strictness used by the binary search. ZH: 二分搜索使用的有效严格度。</summary>
        public float Strictness => Mathf.Min(
            structuralFidelity,
            colorFidelity,
            alphaFidelity,
            normalFidelity,
            grayscaleFidelity);

        /// <summary>EN: SSIM/MS-SSIM lower bound. ZH: SSIM/MS-SSIM 下限。</summary>
        public float SsimMinimum => structuralFidelity;

        /// <summary>EN: CIEDE2000 upper bound in perceptual units. ZH: CIEDE2000 感知单位上限。</summary>
        public float DeltaE2000Maximum => (1f - colorFidelity) * 10f;

        /// <summary>EN: Cutout contour IoU lower bound. ZH: Cutout 轮廓 IoU 下限。</summary>
        public float CutoutIouMinimum => alphaFidelity;

        /// <summary>EN: Blend alpha linear RMSE upper bound. ZH: Blend Alpha 线性 RMSE 上限。</summary>
        public float AlphaRmseMaximum => (1f - alphaFidelity) * 0.10f;

        /// <summary>EN: Mean decoded-normal angle upper bound. ZH: 解码法线平均夹角上限。</summary>
        public float NormalMeanDegreesMaximum => (1f - normalFidelity) * 20f;

        /// <summary>EN: p95 decoded-normal angle upper bound. ZH: 解码法线 p95 夹角上限。</summary>
        public float NormalP95DegreesMaximum => (1f - normalFidelity) * 40f;

        /// <summary>EN: Used-channel linear RMSE upper bound. ZH: 已用通道线性 RMSE 上限。</summary>
        public float GrayscaleRmseMaximum => (1f - grayscaleFidelity) * 0.10f;

        public bool IsExact => structuralFidelity >= 1f && colorFidelity >= 1f && alphaFidelity >= 1f &&
                               normalFidelity >= 1f && grayscaleFidelity >= 1f;

        public QualityThresholds Clone()
        {
            return new QualityThresholds
            {
                structuralFidelity = structuralFidelity,
                colorFidelity = colorFidelity,
                alphaFidelity = alphaFidelity,
                normalFidelity = normalFidelity,
                grayscaleFidelity = grayscaleFidelity,
            };
        }

        public static QualityThresholds ForPreset(QualityPreset preset)
        {
            switch (preset)
            {
                case QualityPreset.Performance:
                    return New(0.940f, 0.850f, 0.960f, 0.850f, 0.940f);
                case QualityPreset.Balanced:
                    return New(0.970f, 0.950f, 0.980f, 0.950f, 0.970f);
                case QualityPreset.High:
                    return New(0.985f, 0.980f, 0.992f, 0.980f, 0.985f);
                case QualityPreset.NearLossless:
                case QualityPreset.Custom:
                    return New(1f, 1f, 1f, 1f, 1f);
                default:
                    throw new ArgumentOutOfRangeException(nameof(preset), preset, null);
            }
        }

        private static QualityThresholds New(float structural, float color, float alpha, float normal, float gray)
        {
            return new QualityThresholds
            {
                structuralFidelity = structural,
                colorFidelity = color,
                alphaFidelity = alpha,
                normalFidelity = normal,
                grayscaleFidelity = gray,
            };
        }
    }

    /// <summary>
    /// EN: Output options for one semantic category. Mipmap and streaming are intentionally one switch.
    /// ZH: 单一语义类别的输出选项。Mipmap 与流式加载有意绑定为同一开关。
    /// </summary>
    [Serializable]
    public sealed class TextureCategorySettings
    {
        public bool mipmapsAndStreaming = true;
        public SafeTextureFormat compression = SafeTextureFormat.Automatic;
    }

    /// <summary>
    /// EN: Complete platform profile; override profiles never partially inherit hidden values.
    /// ZH: 完整平台配置；覆盖配置不会隐式继承部分隐藏值。
    /// </summary>
    [Serializable]
    public sealed class PlatformProfile
    {
        public QualityPreset qualityPreset = QualityPreset.Balanced;
        public QualityThresholds quality = QualityThresholds.ForPreset(QualityPreset.Balanced);
        [Min(1)] public int minimumPixelDensity = 2048;
        [Min(1)] public int maximumPixelDensity = 4096;
        [Range(64, 8192)] public int maximumAtlasSize = 8192;
        public bool generateAtlases = true;
        public bool experimentalNpotAtlases;
        public MinimumPadding minimumPadding = MinimumPadding.Pixels4;
        public TextureCategorySettings opaque = new TextureCategorySettings();
        public TextureCategorySettings alpha = new TextureCategorySettings();
        public TextureCategorySettings normal = new TextureCategorySettings();
        public TextureCategorySettings grayscale = new TextureCategorySettings();

        [SerializeField, HideInInspector] private QualityPreset appliedPreset = QualityPreset.Balanced;

        public void Validate(OptimizerPlatform platform)
        {
            minimumPixelDensity = Mathf.Max(1, minimumPixelDensity);
            maximumPixelDensity = Mathf.Max(minimumPixelDensity, maximumPixelDensity);
            var platformMaximum = platform == OptimizerPlatform.Android || platform == OptimizerPlatform.IOS ? 4096 : 8192;
            maximumAtlasSize = Mathf.Clamp(maximumAtlasSize, 64, platformMaximum);
            maximumAtlasSize = experimentalNpotAtlases
                ? Mathf.Max(64, (maximumAtlasSize / 64) * 64)
                : Mathf.ClosestPowerOfTwo(maximumAtlasSize);

            if (quality == null) quality = QualityThresholds.ForPreset(qualityPreset);
            if (appliedPreset != qualityPreset)
            {
                // EN: Entering Custom initializes every normalized target to one once; subsequent edits are preserved.
                // ZH: 首次进入自定义时将全部归一化目标初始化为 1；之后保留用户修改。
                quality = QualityThresholds.ForPreset(qualityPreset);
                appliedPreset = qualityPreset;
            }

            opaque = opaque ?? new TextureCategorySettings();
            alpha = alpha ?? new TextureCategorySettings();
            normal = normal ?? new TextureCategorySettings();
            grayscale = grayscale ?? new TextureCategorySettings();
        }

        public TextureCategorySettings ForSemantic(TextureSemantic semantic)
        {
            switch (semantic)
            {
                case TextureSemantic.ColorOpaque: return opaque;
                case TextureSemantic.ColorAlpha: return alpha;
                case TextureSemantic.Normal: return normal;
                case TextureSemantic.Grayscale: return grayscale;
                default: throw new ArgumentOutOfRangeException(nameof(semantic), semantic, null);
            }
        }
    }

    /// <summary>
    /// EN: One optional platform override. ZH: 单个平台可选覆盖项。
    /// </summary>
    [Serializable]
    public sealed class PlatformOverride
    {
        public bool enabled;
        public PlatformProfile profile = new PlatformProfile();
    }

    /// <summary>
    /// EN: Serializable optimizer configuration. ZH: 可序列化的优化器配置。
    /// </summary>
    [Serializable]
    public sealed class OptimizerSettings
    {
        public OptimizerPlatform previewPlatform = OptimizerPlatform.Auto;
        public PlatformProfile common = new PlatformProfile();
        public PlatformOverride pc = new PlatformOverride();
        public PlatformOverride android = new PlatformOverride();
        public PlatformOverride ios = new PlatformOverride();

        public bool deduplicateTextures = true;
        public bool deduplicateMaterials = true;
        public bool verboseLogging;
        public string language = "Auto";
        public List<UnityEngine.Object> whitelist = new List<UnityEngine.Object>();

        public PlatformProfile Resolve(OptimizerPlatform platform)
        {
            switch (platform)
            {
                case OptimizerPlatform.PC when pc.enabled: return pc.profile;
                case OptimizerPlatform.Android when android.enabled: return android.profile;
                case OptimizerPlatform.IOS when ios.enabled: return ios.profile;
                default: return common;
            }
        }

        public void Validate()
        {
            common = common ?? new PlatformProfile();
            pc = pc ?? new PlatformOverride();
            android = android ?? new PlatformOverride();
            ios = ios ?? new PlatformOverride();
            whitelist = whitelist ?? new List<UnityEngine.Object>();
            common.Validate(OptimizerPlatform.PC);
            pc.profile = pc.profile ?? new PlatformProfile();
            android.profile = android.profile ?? new PlatformProfile();
            ios.profile = ios.profile ?? new PlatformProfile();
            pc.profile.Validate(OptimizerPlatform.PC);
            android.profile.Validate(OptimizerPlatform.Android);
            ios.profile.Validate(OptimizerPlatform.IOS);
        }
    }
}
