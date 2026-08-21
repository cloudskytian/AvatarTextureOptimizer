using System;
using System.Collections.Generic;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer
{
    /// <summary>
    /// Main user-facing component for Avatar Texture Optimizer.
    /// 主用户组件，供用户在 Avatar 根对象上启用 Avatar Texture Optimizer。
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Avatar Texture Optimizer/Avatar Texture Optimizer")]
    public sealed class AvatarTextureOptimizer : MonoBehaviour
    {
        public const string PackageName = "net.fosa.avatar-texture-optimizer";
        public const string ProductName = "AvatarTextureOptimizer";
        public const string CurrentDataVersion = "0.1.0-dev.1";

        [SerializeField] private bool _enableOptimization = true;
        [SerializeField] private bool _generateAtlases = true;
        [SerializeField] private bool _deduplicateTextures = true;
        [SerializeField] private bool _deduplicateMaterials = true;
        [SerializeField] private bool _debugLogging = true;
        [SerializeField] private string _language = "Auto";

        [SerializeField] private AvatarTextureOptimizerGeneralSettings _general = AvatarTextureOptimizerGeneralSettings.CreateDefault();
        [SerializeField] private AvatarTextureOptimizerQualitySettings _quality = AvatarTextureOptimizerQualitySettings.CreateDefault();
        [SerializeField] private AvatarTextureOptimizerTexturePipelineSettings _textures = AvatarTextureOptimizerTexturePipelineSettings.CreateDefault();
        [SerializeField] private AvatarTextureOptimizerPlatformOverrides _platformOverrides = AvatarTextureOptimizerPlatformOverrides.CreateDefault();
        [SerializeField] private List<UnityEngine.Object> _whitelist = new List<UnityEngine.Object>();

        /// <summary>
        /// Whether the optimizer should participate in the current build.
        /// 当前构建是否启用优化器。
        /// </summary>
        public bool EnableOptimization => _enableOptimization;

        /// <summary>
        /// Whether atlas generation is enabled.
        /// 是否启用图集生成。
        /// </summary>
        public bool GenerateAtlases => _generateAtlases;

        /// <summary>
        /// Whether identical textures or generated atlases may be deduplicated after optimization.
        /// 是否允许在优化后去重内容和参数完全一致的贴图或图集。
        /// </summary>
        public bool DeduplicateTextures => _deduplicateTextures;

        /// <summary>
        /// Whether identical materials may be deduplicated after optimization.
        /// 是否允许在优化后去重内容和参数完全一致的材质。
        /// </summary>
        public bool DeduplicateMaterials => _deduplicateMaterials;

        /// <summary>
        /// Enables verbose development logging.
        /// 启用详细调试日志。
        /// </summary>
        public bool DebugLogging => _debugLogging;

        /// <summary>
        /// Manual language selection. "Auto" means follow NDMF language.
        /// 手动语言选择；“Auto”表示跟随 NDMF 当前语言。
        /// </summary>
        public string Language
        {
            get => string.IsNullOrWhiteSpace(_language) ? "Auto" : _language;
            set => _language = string.IsNullOrWhiteSpace(value) ? "Auto" : value;
        }

        /// <summary>
        /// General analysis and safety settings.
        /// 通用分析与安全设置。
        /// </summary>
        public AvatarTextureOptimizerGeneralSettings General => _general ??= AvatarTextureOptimizerGeneralSettings.CreateDefault();

        /// <summary>
        /// Quality preset and advanced thresholds.
        /// 质量挡位与高级阈值设置。
        /// </summary>
        public AvatarTextureOptimizerQualitySettings Quality => _quality ??= AvatarTextureOptimizerQualitySettings.CreateDefault();

        /// <summary>
        /// Texture pipeline defaults shared across platforms.
        /// 全平台共享的贴图流程默认设置。
        /// </summary>
        public AvatarTextureOptimizerTexturePipelineSettings Textures => _textures ??= AvatarTextureOptimizerTexturePipelineSettings.CreateDefault();

        /// <summary>
        /// Per-platform override settings.
        /// 分平台覆盖设置。
        /// </summary>
        public AvatarTextureOptimizerPlatformOverrides PlatformOverrides => _platformOverrides ??= AvatarTextureOptimizerPlatformOverrides.CreateDefault();

        /// <summary>
        /// User-managed whitelist. Objects referenced here are analyzed as explicit skip roots.
        /// 用户维护的白名单；被引用对象会作为显式跳过根对象分析。
        /// </summary>
        public IReadOnlyList<UnityEngine.Object> Whitelist => _whitelist;

        private void Reset()
        {
            _enableOptimization = true;
            _generateAtlases = true;
            _deduplicateTextures = true;
            _deduplicateMaterials = true;
            _debugLogging = true;
            _language = "Auto";
            _general = AvatarTextureOptimizerGeneralSettings.CreateDefault();
            _quality = AvatarTextureOptimizerQualitySettings.CreateDefault();
            _textures = AvatarTextureOptimizerTexturePipelineSettings.CreateDefault();
            _platformOverrides = AvatarTextureOptimizerPlatformOverrides.CreateDefault();
            _whitelist = new List<UnityEngine.Object>();
        }

        private void OnValidate()
        {
            _general ??= AvatarTextureOptimizerGeneralSettings.CreateDefault();
            _quality ??= AvatarTextureOptimizerQualitySettings.CreateDefault();
            _textures ??= AvatarTextureOptimizerTexturePipelineSettings.CreateDefault();
            _platformOverrides ??= AvatarTextureOptimizerPlatformOverrides.CreateDefault();
            _whitelist ??= new List<UnityEngine.Object>();

            _general.Clamp();
            _quality.Clamp();
            _textures.Clamp();
            _platformOverrides.Clamp();
        }
    }

    [Serializable]
    public sealed class AvatarTextureOptimizerGeneralSettings
    {
        [SerializeField] private int _minimumPixelDensity = 2048;
        [SerializeField] private int _maximumPixelDensity = 4096;
        [SerializeField] private int _minimumPadding = 4;
        [SerializeField] private bool _experimentalNpotAtlasSizes = false;
        [SerializeField] private bool _enableMipMapAndStreamingForColor = true;
        [SerializeField] private bool _enableMipMapAndStreamingForNormal = true;
        [SerializeField] private bool _enableMipMapAndStreamingForMask = true;
        [SerializeField] private bool _enableProgressBar = true;
        [SerializeField] private bool _enableCancellation = true;

        public int MinimumPixelDensity => _minimumPixelDensity;
        public int MaximumPixelDensity => _maximumPixelDensity;
        public int MinimumPadding => _minimumPadding;
        public bool ExperimentalNpotAtlasSizes => _experimentalNpotAtlasSizes;
        public bool EnableMipMapAndStreamingForColor => _enableMipMapAndStreamingForColor;
        public bool EnableMipMapAndStreamingForNormal => _enableMipMapAndStreamingForNormal;
        public bool EnableMipMapAndStreamingForMask => _enableMipMapAndStreamingForMask;
        public bool EnableProgressBar => _enableProgressBar;
        public bool EnableCancellation => _enableCancellation;

        public static AvatarTextureOptimizerGeneralSettings CreateDefault()
        {
            return new AvatarTextureOptimizerGeneralSettings();
        }

        public void Clamp()
        {
            _minimumPixelDensity = Mathf.Clamp(_minimumPixelDensity, 128, 16384);
            _maximumPixelDensity = Mathf.Clamp(_maximumPixelDensity, _minimumPixelDensity, 16384);
            _minimumPadding = Mathf.Clamp(_minimumPadding, 4, 64);
        }
    }

    /// <summary>
    /// High-level preset names used by the current milestone.
    /// 当前里程碑使用的高层质量挡位名称。
    /// </summary>
    public enum AvatarTextureOptimizerQualityPreset
    {
        Compact = 0,
        Balanced = 1,
        High = 2,
        NearLossless = 3,
        Custom = 4,
    }

    [Serializable]
    public sealed class AvatarTextureOptimizerQualitySettings
    {
        [SerializeField] private AvatarTextureOptimizerQualityPreset _preset = AvatarTextureOptimizerQualityPreset.Balanced;
        [SerializeField] private AvatarTextureOptimizerQualityParameters _parameters = AvatarTextureOptimizerQualityParameters.Balanced();
        [SerializeField] private bool _showAdvanced = false;

        public AvatarTextureOptimizerQualityPreset Preset
        {
            get => _preset;
            set => _preset = value;
        }

        public AvatarTextureOptimizerQualityParameters Parameters => _parameters ??= AvatarTextureOptimizerQualityParameters.Balanced();
        public bool ShowAdvanced
        {
            get => _showAdvanced;
            set => _showAdvanced = value;
        }

        public static AvatarTextureOptimizerQualitySettings CreateDefault()
        {
            return new AvatarTextureOptimizerQualitySettings();
        }

        public void ApplyPresetIfNeeded()
        {
            switch (_preset)
            {
                case AvatarTextureOptimizerQualityPreset.Compact:
                    _parameters = AvatarTextureOptimizerQualityParameters.Compact();
                    break;
                case AvatarTextureOptimizerQualityPreset.Balanced:
                    _parameters = AvatarTextureOptimizerQualityParameters.Balanced();
                    break;
                case AvatarTextureOptimizerQualityPreset.High:
                    _parameters = AvatarTextureOptimizerQualityParameters.High();
                    break;
                case AvatarTextureOptimizerQualityPreset.NearLossless:
                    _parameters = AvatarTextureOptimizerQualityParameters.NearLossless();
                    break;
                case AvatarTextureOptimizerQualityPreset.Custom:
                default:
                    _parameters ??= AvatarTextureOptimizerQualityParameters.NearLossless();
                    break;
            }
        }

        public void Clamp()
        {
            _parameters ??= AvatarTextureOptimizerQualityParameters.Balanced();
            _parameters.Clamp();
        }
    }

    [Serializable]
    public sealed class AvatarTextureOptimizerQualityParameters
    {
        [Range(0.0f, 1.0f)] [SerializeField] private float _structuralSimilarity = 0.975f;
        [Range(0.0f, 50.0f)] [SerializeField] private float _maxDeltaE2000 = 3.0f;
        [Range(0.0f, 1.0f)] [SerializeField] private float _alphaEdgeIou = 0.98f;
        [Range(0.0f, 1.0f)] [SerializeField] private float _alphaBlendRmse = 0.025f;
        [Range(0.0f, 45.0f)] [SerializeField] private float _normalAngularErrorDegrees = 6.0f;
        [Range(0.0f, 45.0f)] [SerializeField] private float _normalP95AngularErrorDegrees = 10.0f;
        [Range(0.0f, 1.0f)] [SerializeField] private float _grayscaleRmse = 0.02f;
        [Range(0.0f, 1.0f)] [SerializeField] private float _globalTargetQuality = 0.92f;

        public float StructuralSimilarity => _structuralSimilarity;
        public float MaxDeltaE2000 => _maxDeltaE2000;
        public float AlphaEdgeIou => _alphaEdgeIou;
        public float AlphaBlendRmse => _alphaBlendRmse;
        public float NormalAngularErrorDegrees => _normalAngularErrorDegrees;
        public float NormalP95AngularErrorDegrees => _normalP95AngularErrorDegrees;
        public float GrayscaleRmse => _grayscaleRmse;
        public float GlobalTargetQuality => _globalTargetQuality;

        public static AvatarTextureOptimizerQualityParameters Compact()
        {
            return new AvatarTextureOptimizerQualityParameters
            {
                _structuralSimilarity = 0.93f,
                _maxDeltaE2000 = 6.0f,
                _alphaEdgeIou = 0.94f,
                _alphaBlendRmse = 0.045f,
                _normalAngularErrorDegrees = 10.0f,
                _normalP95AngularErrorDegrees = 16.0f,
                _grayscaleRmse = 0.04f,
                _globalTargetQuality = 0.75f,
            };
        }

        public static AvatarTextureOptimizerQualityParameters Balanced()
        {
            return new AvatarTextureOptimizerQualityParameters
            {
                _structuralSimilarity = 0.975f,
                _maxDeltaE2000 = 3.0f,
                _alphaEdgeIou = 0.98f,
                _alphaBlendRmse = 0.025f,
                _normalAngularErrorDegrees = 6.0f,
                _normalP95AngularErrorDegrees = 10.0f,
                _grayscaleRmse = 0.02f,
                _globalTargetQuality = 0.92f,
            };
        }

        public static AvatarTextureOptimizerQualityParameters High()
        {
            return new AvatarTextureOptimizerQualityParameters
            {
                _structuralSimilarity = 0.988f,
                _maxDeltaE2000 = 2.0f,
                _alphaEdgeIou = 0.992f,
                _alphaBlendRmse = 0.015f,
                _normalAngularErrorDegrees = 4.0f,
                _normalP95AngularErrorDegrees = 7.0f,
                _grayscaleRmse = 0.012f,
                _globalTargetQuality = 0.97f,
            };
        }

        public static AvatarTextureOptimizerQualityParameters NearLossless()
        {
            return new AvatarTextureOptimizerQualityParameters
            {
                _structuralSimilarity = 1.0f,
                _maxDeltaE2000 = 1.0f,
                _alphaEdgeIou = 1.0f,
                _alphaBlendRmse = 0.0f,
                _normalAngularErrorDegrees = 1.0f,
                _normalP95AngularErrorDegrees = 2.0f,
                _grayscaleRmse = 0.0f,
                _globalTargetQuality = 1.0f,
            };
        }

        public void Clamp()
        {
            _structuralSimilarity = Mathf.Clamp01(_structuralSimilarity);
            _maxDeltaE2000 = Mathf.Clamp(_maxDeltaE2000, 0.0f, 50.0f);
            _alphaEdgeIou = Mathf.Clamp01(_alphaEdgeIou);
            _alphaBlendRmse = Mathf.Clamp01(_alphaBlendRmse);
            _normalAngularErrorDegrees = Mathf.Clamp(_normalAngularErrorDegrees, 0.0f, 45.0f);
            _normalP95AngularErrorDegrees = Mathf.Clamp(_normalP95AngularErrorDegrees, 0.0f, 45.0f);
            _grayscaleRmse = Mathf.Clamp01(_grayscaleRmse);
            _globalTargetQuality = Mathf.Clamp01(_globalTargetQuality);
        }
    }

    public enum AvatarTextureOptimizerTextureFormatPolicy
    {
        Automatic = 0,
        Quality = 1,
        Balanced = 2,
        Compact = 3,
        Uncompressed = 4,
    }

    [Serializable]
    public sealed class AvatarTextureOptimizerTexturePipelineSettings
    {
        [SerializeField] private AvatarTextureOptimizerTextureFormatPolicy _opaquePolicy = AvatarTextureOptimizerTextureFormatPolicy.Automatic;
        [SerializeField] private AvatarTextureOptimizerTextureFormatPolicy _transparentPolicy = AvatarTextureOptimizerTextureFormatPolicy.Automatic;
        [SerializeField] private AvatarTextureOptimizerTextureFormatPolicy _normalPolicy = AvatarTextureOptimizerTextureFormatPolicy.Automatic;
        [SerializeField] private AvatarTextureOptimizerTextureFormatPolicy _grayscalePolicy = AvatarTextureOptimizerTextureFormatPolicy.Automatic;

        public AvatarTextureOptimizerTextureFormatPolicy OpaquePolicy => _opaquePolicy;
        public AvatarTextureOptimizerTextureFormatPolicy TransparentPolicy => _transparentPolicy;
        public AvatarTextureOptimizerTextureFormatPolicy NormalPolicy => _normalPolicy;
        public AvatarTextureOptimizerTextureFormatPolicy GrayscalePolicy => _grayscalePolicy;

        public static AvatarTextureOptimizerTexturePipelineSettings CreateDefault()
        {
            return new AvatarTextureOptimizerTexturePipelineSettings();
        }

        public void Clamp()
        {
        }
    }

    public enum AvatarTextureOptimizerTargetPlatform
    {
        PC = 0,
        Android = 1,
        IOS = 2,
    }

    [Serializable]
    public sealed class AvatarTextureOptimizerPlatformOverrides
    {
        [SerializeField] private AvatarTextureOptimizerPlatformProfile _common = AvatarTextureOptimizerPlatformProfile.CreateDefault(AvatarTextureOptimizerTargetPlatform.PC, false);
        [SerializeField] private AvatarTextureOptimizerPlatformProfile _pc = AvatarTextureOptimizerPlatformProfile.CreateDefault(AvatarTextureOptimizerTargetPlatform.PC, false);
        [SerializeField] private AvatarTextureOptimizerPlatformProfile _android = AvatarTextureOptimizerPlatformProfile.CreateDefault(AvatarTextureOptimizerTargetPlatform.Android, false);
        [SerializeField] private AvatarTextureOptimizerPlatformProfile _ios = AvatarTextureOptimizerPlatformProfile.CreateDefault(AvatarTextureOptimizerTargetPlatform.IOS, false);

        public AvatarTextureOptimizerPlatformProfile Common => _common ??= AvatarTextureOptimizerPlatformProfile.CreateDefault(AvatarTextureOptimizerTargetPlatform.PC, false);
        public AvatarTextureOptimizerPlatformProfile PC => _pc ??= AvatarTextureOptimizerPlatformProfile.CreateDefault(AvatarTextureOptimizerTargetPlatform.PC, false);
        public AvatarTextureOptimizerPlatformProfile Android => _android ??= AvatarTextureOptimizerPlatformProfile.CreateDefault(AvatarTextureOptimizerTargetPlatform.Android, false);
        public AvatarTextureOptimizerPlatformProfile IOS => _ios ??= AvatarTextureOptimizerPlatformProfile.CreateDefault(AvatarTextureOptimizerTargetPlatform.IOS, false);

        public static AvatarTextureOptimizerPlatformOverrides CreateDefault()
        {
            return new AvatarTextureOptimizerPlatformOverrides();
        }

        public void Clamp()
        {
            Common.Clamp();
            PC.Clamp();
            Android.Clamp();
            IOS.Clamp();
        }
    }

    [Serializable]
    public sealed class AvatarTextureOptimizerPlatformProfile
    {
        [SerializeField] private AvatarTextureOptimizerTargetPlatform _platform;
        [SerializeField] private bool _overrideEnabled;
        [SerializeField] private int _maxAtlasSize = 8192;
        [SerializeField] private AvatarTextureOptimizerTexturePipelineSettings _textureSettings = AvatarTextureOptimizerTexturePipelineSettings.CreateDefault();

        public AvatarTextureOptimizerTargetPlatform Platform => _platform;
        public bool OverrideEnabled => _overrideEnabled;
        public int MaxAtlasSize => _maxAtlasSize;
        public AvatarTextureOptimizerTexturePipelineSettings TextureSettings => _textureSettings ??= AvatarTextureOptimizerTexturePipelineSettings.CreateDefault();

        public static AvatarTextureOptimizerPlatformProfile CreateDefault(AvatarTextureOptimizerTargetPlatform platform, bool overrideEnabled)
        {
            return new AvatarTextureOptimizerPlatformProfile
            {
                _platform = platform,
                _overrideEnabled = overrideEnabled,
                _maxAtlasSize = platform == AvatarTextureOptimizerTargetPlatform.PC ? 8192 : 4096,
                _textureSettings = AvatarTextureOptimizerTexturePipelineSettings.CreateDefault(),
            };
        }

        public void Clamp()
        {
            _maxAtlasSize = Mathf.Clamp(_maxAtlasSize, 64, _platform == AvatarTextureOptimizerTargetPlatform.PC ? 8192 : 4096);
            _textureSettings ??= AvatarTextureOptimizerTexturePipelineSettings.CreateDefault();
            _textureSettings.Clamp();
        }
    }
}
