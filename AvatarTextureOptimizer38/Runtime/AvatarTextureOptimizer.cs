using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;
#if ATO_NDMF
using nadena.dev.ndmf;
#endif
#if ATO_VRCSDK3
using VRC.SDK3.Avatars.Components;
#endif

namespace Net.Fosa.AvatarTextureOptimizer
{
    /// <summary>
    /// Root component. One per avatar, must sit on the VRCAvatarDescriptor object.
    /// 根组件。每个 Avatar 只能有一个，且必须挂在带 VRCAvatarDescriptor 的物体上。
    /// Implements INDMFEditorOnly so VRC upload strips leftovers; the NDMF pass also DestroyImmediate's it.
    /// 实现 INDMFEditorOnly 以便上传剥离；NDMF Pass 结束时也会 DestroyImmediate。
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("FOSA/Avatar Texture Optimizer")]
    [HelpURL("https://github.com/fosa/avatar-texture-optimizer")]
    public sealed class AvatarTextureOptimizer :
        MonoBehaviour
#if ATO_NDMF
        , INDMFEditorOnly
#endif
    {
        public const string LogPrefix = "[ATO]";
        public const string GeneratedAssetFolder = "Assets/_ATO_Generated";
        public const string AtlasNamePrefix = "ATO_";
        public const string PackageName = "net.fosa.avatar-texture-optimizer";

        [Header("Atlas / 图集")]
        [Tooltip("Generate atlases. Off = scale whole textures, keep UVs, skip unused-UV crop.\n生成图集。关闭则只缩放整张贴图，不剔除未使用 UV、不重排 UV。")]
        public bool generateAtlas = true;

        [Tooltip("Experimental NPOT atlas sizes (64 px steps). Disables PVRTC etc.\n实验性非 2 次幂图集（64px 步进）。会剔除不支持的压缩如 PVRTC。")]
        public bool experimentalNpot = false;

        [Tooltip("Minimum island padding. Actual padding is max(this, ceil(maxSide/128)) clamped to >=4.\n最小岛间距。实际 padding = max(本值, ceil(长边/128)) 且不小于 4。")]
        public AtlasPaddingPreset minPadding = AtlasPaddingPreset.Px4;

        [Header("Quality / 质量")]
        public QualityPreset qualityPreset = QualityPreset.High;

        [Tooltip("Edited when preset is Custom, or overwritten when switching non-Custom presets.\n自定义挡位下由用户改；切换到其它挡位时会被覆盖。")]
        public QualityParameters qualityParameters = default;

        [Tooltip("Minimum pixel density (px/m). / 最小像素密度（像素/米）。")]
        public PixelDensityStep minPixelDensity = PixelDensityStep.Px2048;

        [Tooltip("Maximum pixel density (px/m). / 最大像素密度（像素/米）。")]
        public PixelDensityStep maxPixelDensity = PixelDensityStep.Px4096;

        [Header("Dedup / 去重")]
        [Tooltip("Deduplicate identical materials after optimize. / 优化后合并内容与参数完全相同的材质。")]
        public bool deduplicateMaterials = true;

        [Tooltip("Deduplicate identical textures/atlases after optimize. / 优化后合并内容与参数完全相同的贴图/图集。")]
        public bool deduplicateTextures = true;

        [Header("Whitelist / 白名单")]
        [Tooltip("Any referenced Texture2D under these objects skips ALL optimization.\n这些对象引用到的全部 Texture2D 跳过所有优化。")]
        public List<Object> whitelist = new List<Object>();

        [Header("Platform / 平台")]
        [Tooltip("Override settings per platform (Unity-style). Default reads current build target.\n按平台覆盖参数。默认读取当前构建平台。")]
        public bool enablePlatformOverride = false;

        public AtoBuildPlatform defaultPlatform = AtoBuildPlatform.PC;

        public PlatformTextureSettings pcSettings;
        public PlatformTextureSettings androidSettings;
        public PlatformTextureSettings iosSettings;

        [Header("Language / 语言")]
        public AtoLanguageMode language = AtoLanguageMode.Auto;

        [Header("Debug / 调试")]
        [Tooltip("Verbose [ATO] logs for advanced users. / 高级用户详细日志。")]
        public bool verboseLog = false;

        [SerializeField, HideInInspector]
        private bool _initialized;

        [SerializeField, HideInInspector]
        private QualityPreset _lastAppliedPreset;

        private void Reset()
        {
            InitDefaults();
        }

        private void OnEnable()
        {
            if (!_initialized) InitDefaults();
        }

        private void OnValidate()
        {
            if (!_initialized) InitDefaults();

            // Sync quality fields when preset changes (except Custom). / 挡位变化时同步参数（自定义除外）。
            if (qualityPreset != QualityPreset.Custom && qualityPreset != _lastAppliedPreset)
            {
                qualityParameters = QualityParameters.ForPreset(qualityPreset);
                _lastAppliedPreset = qualityPreset;
            }
            else if (qualityPreset == QualityPreset.Custom)
            {
                _lastAppliedPreset = QualityPreset.Custom;
            }

            if ((int)minPixelDensity > (int)maxPixelDensity)
            {
                maxPixelDensity = minPixelDensity;
            }
        }

        /// <summary>
        /// Initialize default settings. / 初始化默认设置。
        /// </summary>
        public void InitDefaults()
        {
            generateAtlas = true;
            experimentalNpot = false;
            minPadding = AtlasPaddingPreset.Px4;
            qualityPreset = QualityPreset.High;
            qualityParameters = QualityParameters.High();
            _lastAppliedPreset = QualityPreset.High;
            minPixelDensity = PixelDensityStep.Px2048;
            maxPixelDensity = PixelDensityStep.Px4096;
            deduplicateMaterials = true;
            deduplicateTextures = true;
            enablePlatformOverride = false;
            defaultPlatform = DetectEditorPlatform();
            pcSettings = PlatformTextureSettings.DefaultPc();
            androidSettings = PlatformTextureSettings.DefaultAndroid();
            iosSettings = PlatformTextureSettings.DefaultIos();
            language = AtoLanguageMode.Auto;
            verboseLog = false;
            if (whitelist == null) whitelist = new List<Object>();
            _initialized = true;
        }

        /// <summary>
        /// Active quality parameters for the current preset. / 当前挡位对应的质量参数。
        /// </summary>
        public QualityParameters ActiveQuality
        {
            get
            {
                if (qualityPreset == QualityPreset.Custom) return qualityParameters;
                return QualityParameters.ForPreset(qualityPreset);
            }
        }

        public PlatformTextureSettings ActivePlatformSettings(AtoBuildPlatform platform)
        {
            if (!enablePlatformOverride)
            {
                switch (platform)
                {
                    case AtoBuildPlatform.Android: return PlatformTextureSettings.DefaultAndroid();
                    case AtoBuildPlatform.iOS: return PlatformTextureSettings.DefaultIos();
                    default: return PlatformTextureSettings.DefaultPc();
                }
            }

            switch (platform)
            {
                case AtoBuildPlatform.Android: return androidSettings;
                case AtoBuildPlatform.iOS: return iosSettings;
                default: return pcSettings;
            }
        }

        public static AtoBuildPlatform DetectEditorPlatform()
        {
#if UNITY_EDITOR
            switch (UnityEditor.EditorUserBuildSettings.activeBuildTarget)
            {
                case UnityEditor.BuildTarget.Android:
                    return AtoBuildPlatform.Android;
                case UnityEditor.BuildTarget.iOS:
                    return AtoBuildPlatform.iOS;
                default:
                    return AtoBuildPlatform.PC;
            }
#else
            return AtoBuildPlatform.PC;
#endif
        }

        /// <summary>
        /// True if this GameObject has a VRCAvatarDescriptor. / 是否存在 VRCAvatarDescriptor。
        /// </summary>
        public bool HasAvatarDescriptor
        {
            get
            {
#if ATO_VRCSDK3
                return GetComponent<VRCAvatarDescriptor>() != null;
#else
                return GetComponent("VRCAvatarDescriptor") != null;
#endif
            }
        }
    }
}
