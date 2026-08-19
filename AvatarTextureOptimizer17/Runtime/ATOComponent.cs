// ============================================================================
// AvatarTextureOptimizer (net.fosa.avatar-texture-optimizer)
// ATOComponent.cs — 挂载于 Avatar 的配置组件 / Avatar-root configuration component
//
// 规则 (来自需求):
//  - 用户可在 Avatar 上加一个组件优化整个 Avatar。
//  - 一个 Avatar 及其子级上只允许挂载一个组件（[DisallowMultipleComponent] + 构建期校验）。
//  - 挂载对象上必须存在 VRCAvatarDescriptor（[RequireComponent] + 构建期校验）。
//  - 本组件仅承载配置，不执行任何运行时逻辑；烘焙完成后会从成品上移除自身。
// ============================================================================
using System.Collections.Generic;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace net.fosa.avatar_texture_optimizer
{
    /// <summary>
    /// AvatarTextureOptimizer 配置组件 /
    /// Configuration component for AvatarTextureOptimizer.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(VRCAvatarDescriptor))]
    [AddComponentMenu("Avatar Texture Optimizer/ATO Optimizer")]
    [HelpURL("https://github.com/net-fosa/avatar-texture-optimizer")]
    public sealed class ATOComponent : MonoBehaviour
    {
        // ---- 主开关 / Main toggles ------------------------------------------------

        /// <summary>
        /// 是否生成图集（默认勾选）。不勾选则不生成图集、不剔除未使用 UV、不重排 UV，
        /// 直接缩放整张贴图并进行其他优化。/
        /// Whether to generate atlases (default on). When off: no atlasing, no unused-UV trimming,
        /// no UV repacking — the whole texture is scaled and other optimizations still apply.
        /// </summary>
        [Header("General")] // 通用
        public bool generateAtlases = true;

        /// <summary>
        /// 目标质量挡位。切换挡位时具体参数值随之变化。/
        /// Target quality preset. Switching preset updates concrete parameter values.
        /// </summary>
        public QualityPreset qualityPreset = QualityPreset.Balanced;

        /// <summary>
        /// 自定义挡位参数（仅 Custom 挡位生效，默认全 1 = 近无损）。/
        /// Custom preset parameters (only used when qualityPreset == Custom; defaults = near-lossless).
        /// </summary>
        public QualityTargets customQuality = new QualityTargets
        {
            msSsim = 1f,
            maxDeltaE = 0f,
            minAlphaCutoutIoU = 1f,
            maxAlphaBlendRmse = 0f,
            maxNormalAngleDeg = 0f,
            maxGrayRmse = 0f,
        };

        // ---- 像素密度 / Pixel density --------------------------------------------

        /// <summary>
        /// 最小像素密度(px/m)。默认 2048。可选 512/1024/2048/4096/8192。/
        /// Minimum texel density in pixels per meter (default 2048; options 512..8192).
        /// </summary>
        public int minPixelDensity = 2048;

        /// <summary>
        /// 最大像素密度(px/m)。默认 4096。可选 512/1024/2048/4096/8192。/
        /// Maximum texel density in pixels per meter (default 4096; options 512..8192).
        /// </summary>
        public int maxPixelDensity = 4096;

        // ---- 图集 / Atlas ---------------------------------------------------------

        /// <summary>
        /// 图集岛间距最小 padding 挡位(px)：4/8/16/32/64，默认 4。
        /// 实际 padding = max(ceil(候选图集最大边长/128), 最小padding挡位)。
        /// Atlas island padding option (px): 4/8/16/32/64, default 4.
        /// Actual padding = max(ceil(candidate atlas max edge/128), this option).
        /// </summary>
        [Header("Atlas")] // 图集
        public int paddingOption = 4;

        /// <summary>
        /// 实验性 NPOT（非 2 的幂）分辨率选项。默认关闭。
        /// 开启后候选图集边长按 64 步进生成；已通过 MipStreaming 与 Crunch 验证，
        /// 但会剔除不支持的压缩格式（如 iOS PVRTC——本工具不支持 PVRTC，故默认剔除）。/
        /// Experimental NPOT option (off by default). When on, candidate edges step by 64.
        /// </summary>
        public bool experimentalNpot = false;

        /// <summary>
        /// 是否对图集启用 Crunch 压缩（平台支持时）/
        /// Whether to use Crunch compression for atlases when the platform supports it.
        /// </summary>
        public bool crunch = false;

        // ---- Mipmap / MipStreaming（绑定开关见 CategoryImportSettings.mipmaps）-----

        // ---- 导入设置（每分类全局默认；平台 override 可覆盖） / import settings ----

        /// <summary>不透明贴图导入设置 / opaque import settings</summary>
        [Header("Import")] // 导入
        public CategoryImportSettings opaqueImport = new CategoryImportSettings();

        /// <summary>透明贴图导入设置 / transparent import settings</summary>
        public CategoryImportSettings transparentImport = new CategoryImportSettings();

        /// <summary>法线贴图导入设置 / normal import settings</summary>
        public CategoryImportSettings normalImport = new CategoryImportSettings();

        /// <summary>灰度贴图导入设置 / grayscale import settings</summary>
        public CategoryImportSettings grayscaleImport = new CategoryImportSettings();

        /// <summary>
        /// 获取某分类在某平台的导入设置（平台 override 优先，其次全局）/
        /// Get import settings for a category on a platform (override first, then global).
        /// </summary>
        public CategoryImportSettings ImportFor(TextureCategory category, ATOPlatform platform)
        {
            if (platformOverrideEnabled)
            {
                var ov = OverrideFor(platform);
                if (ov != null)
                {
                    switch (category)
                    {
                        case TextureCategory.Opaque: if (ov.opaque != null) return ov.opaque; break;
                        case TextureCategory.Transparent: if (ov.transparent != null) return ov.transparent; break;
                        case TextureCategory.Normal: if (ov.normal != null) return ov.normal; break;
                        case TextureCategory.Grayscale: if (ov.grayscale != null) return ov.grayscale; break;
                    }
                }
            }
            switch (category)
            {
                case TextureCategory.Opaque: return opaqueImport;
                case TextureCategory.Transparent: return transparentImport;
                case TextureCategory.Normal: return normalImport;
                default: return grayscaleImport;
            }
        }

        // ---- 平台 / Platform -------------------------------------------------------

        /// <summary>
        /// 是否启用平台覆盖（默认按当前构建平台推断）。勾选后显示对应平台折叠区。/
        /// Whether platform override is enabled (default value reads the current build target).
        /// </summary>
        [Header("Platform")] // 平台
        public bool platformOverrideEnabled = false;

        /// <summary>平台覆盖配置（PC/Android/iOS） / Per-platform override configs</summary>
        public PlatformOverrideConfig pc = new PlatformOverrideConfig();
        public PlatformOverrideConfig android = new PlatformOverrideConfig();
        public PlatformOverrideConfig ios = new PlatformOverrideConfig();

        // ---- 白名单 / Whitelist -----------------------------------------------------

        /// <summary>
        /// 白名单，不限制对象类型（网格/材质/贴图/动画/游戏对象等）。
        /// 白名单内对象引用的全部贴图都跳过所有优化（包括后续参数优化）；
        /// 同 UV 的其他贴图跳过图集化，但参与整图缩放与导入参数优化。/
        /// Whitelist of arbitrary objects (meshes/materials/textures/clips/GameObjects...).
        /// Textures referenced by whitelisted objects skip ALL optimization.
        /// </summary>
        [Header("Whitelist")] // 白名单
        public List<Object> whitelist = new List<Object>();

        // ---- 界面 / UI ---------------------------------------------------------------

        /// <summary>i18n 语言（Auto 跟随 ndmf 当前语言，回退英文） / UI language</summary>
        [Header("Localization")] // 本地化
        public ATOLanguage language = ATOLanguage.Auto;

        /// <summary>详细日志开关（[ATO] 前缀，含每一步耗时/来源/岛数等） / Verbose logging toggle</summary>
        public bool verboseLogging = false;

        /// <summary>
        /// 获取当前生效的质量目标参数（预设或自定义）/
        /// Returns the effective quality targets (preset table or custom).
        /// </summary>
        public QualityTargets EffectiveQuality()
        {
            switch (qualityPreset)
            {
                case QualityPreset.Balanced: return QualityTargets.Balanced();
                case QualityPreset.Quality: return QualityTargets.HighQuality();
                case QualityPreset.Performance: return QualityTargets.Performance();
                case QualityPreset.NearLossless: return QualityTargets.NearLossless();
                default: return customQuality;
            }
        }

        /// <summary>
        /// 当前构建目标平台（默认值读取）/
        /// Platform inferred from the current build target.
        /// </summary>
        public ATOPlatform DefaultPlatform()
        {
#if UNITY_STANDALONE
            return ATOPlatform.PC;
#elif UNITY_ANDROID
            return ATOPlatform.Android;
#elif UNITY_IOS
            return ATOPlatform.iOS;
#else
            return ATOPlatform.PC;
#endif
        }

        /// <summary>
        /// 获取某平台的覆盖配置（可能为 null，表示用通用设置）/
        /// Gets the override config for a platform (may be null → use global settings).
        /// </summary>
        public PlatformOverrideConfig OverrideFor(ATOPlatform p)
        {
            switch (p)
            {
                case ATOPlatform.Android: return android;
                case ATOPlatform.iOS: return ios;
                default: return pc;
            }
        }
    }
}
