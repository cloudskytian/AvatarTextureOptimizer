// ATO — Avatar Texture Optimizer
// The per-avatar component. Add one (and only one) per avatar subtree.
// The actual processing happens inside the NDMF build passes (Editor layer); this
// component only holds the serialized user configuration.
// 每个 Avatar 上挂载一个（且仅一个）的组件。
// 真正的处理发生在 NDMF 构建 Pass（编辑器层）中；本组件只保存用户的序列化配置。

using System.Collections.Generic;
using UnityEngine;

namespace net.fosa.ato
{
    /// <summary>
    /// Optimizes the avatar's textures: establishes mesh-UV ↔ texture mappings, scales UV
    /// islands to the target quality, strips unused UV area and repacks islands into atlases.
    /// 优化 Avatar 贴图：建立网格 UV ↔ 贴图映射，按目标质量缩放 UV 岛、剔除未使用 UV 区域并重组为图集。
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("ATO/Avatar Texture Optimizer")]
    [HelpURL("https://github.com/fosa/avatar-texture-optimizer")]
    public class AvatarTextureOptimizer : MonoBehaviour
    {
        [Header("General 通用")]
        [Tooltip("Enable optimization. 启用优化。")]
        public bool enable = true;

        [Tooltip("Target quality preset. 目标质量挡位。")]
        public ATOQualityPreset qualityPreset = ATOQualityPreset.Balanced;

        [Tooltip("Generate atlases. When disabled, no atlas / unused-UV stripping / UV re-arrangement happens; textures are only scaled plus other optimizations. 生成图集。关闭时不生成图集、不剔除未使用 UV、不重排 UV，仅缩放贴图并进行其他优化。")]
        public bool generateAtlas = true;

        [Header("Density 像素密度")]
        [Tooltip("Min pixel density (px/m). Presets: 512 / 1024 / 2048 / 4096 / 8192. 最小像素密度（px/m）。挡位：512 / 1024 / 2048 / 4096 / 8192。")]
        public float minPixelDensity = 2048f;

        [Tooltip("Max pixel density (px/m). 最大像素密度（px/m）。")]
        public float maxPixelDensity = 4096f;

        [Header("Atlas 图集")]
        [Tooltip("Island padding in pixels (4 / 8 / 16 / 32 / 64). 岛间距（px）。")]
        [Range(4, 64)] public int islandPadding = 4;

        [Tooltip("Experimental NPOT atlas sizes. 实验性 NPOT 图集尺寸。")]
        public bool npotAtlas = false;

        [Header("Dedup & Streaming 去重与流式")]
        [Tooltip("Deduplicate materials after optimization. 优化后对材质去重。")]
        public bool dedupMaterials = true;

        [Tooltip("Deduplicate textures / atlases after optimization. 优化后对贴图 / 图集去重。")]
        public bool dedupTextures = true;

        [Tooltip("Mipmaps + MipStreaming (bound together; VRChat requires MipStreaming when Mipmaps are on). Mipmap 与 MipStreaming（绑定；VRChat 要求开启 Mipmap 时必须开启 MipStreaming）。")]
        public bool mipmapsEnabled = true;

        [Header("Compression 压缩")]
        public ATOCompressionSettings compression = new ATOCompressionSettings();

        [Header("Whitelist 白名单")]
        [Tooltip("Objects listed here (meshes / materials / textures / animations) have all of their textures skipped from every optimization. 列在此处的对象（网格/材质/贴图/动画）所引用的全部贴图将跳过所有优化。")]
        public List<Object> whitelist = new List<Object>();

        [Header("Platform Overrides 平台覆盖")]
        [Tooltip("Per-platform overrides (PC / Android / iOS). 各平台覆盖（PC / Android / iOS）。")]
        public ATOPlatformSettings[] platformOverrides = new ATOPlatformSettings[]
        {
            new ATOPlatformSettings { platform = ATOPlatform.PC },
            new ATOPlatformSettings { platform = ATOPlatform.Android },
            new ATOPlatformSettings { platform = ATOPlatform.iOS },
        };

        [Header("Localization & Logging 本地化与日志")]
        [Tooltip("Language: Auto follows NDMF language, or pick a specific one. 语言：Auto 跟随 NDMF 语言，或手动指定。")]
        public string language = "auto";

        [Tooltip("Verbose [ATO] logging. 详细 [ATO] 日志。")]
        public bool verboseLogging = true;

        [Header("Advanced — Quality Parameters (preset = Custom) 高级——质量参数（自定义挡位）")]
        [Tooltip("Custom quality parameters, used when qualityPreset is Custom. Defaults to all-1 (near lossless). 自定义质量参数（挡位为 Custom 时生效），默认全 1（近无损）。")]
        public ATOQualityParameters customParameters = ATOQualityParameters.Lossless();

        /// <summary>Resolve effective parameters for the given preset. 解析给定挡位的有效参数。</summary>
        public ATOQualityParameters EffectiveParameters => ATOQualityParameters.For(qualityPreset, customParameters);

        /// <summary>Resolve the platform settings for the given platform, applying its override when enabled. 解析给定平台的设置（含覆盖）。</summary>
        public ATOPlatformSettings PlatformSettingsFor(ATOPlatform platform)
        {
            foreach (var s in platformOverrides)
            {
                if (s != null && s.platform == platform) return s;
            }
            // Fallback: first entry or a fresh default. 回退：第一项或新建默认。
            return platformOverrides != null && platformOverrides.Length > 0
                ? platformOverrides[0]
                : new ATOPlatformSettings { platform = platform };
        }

        /// <summary>
        /// Effective settings for a platform, resolving base vs override.
        /// 解析某平台的有效设置（基础 vs 覆盖）。
        /// </summary>
        public ATOEffectiveSettings EffectiveSettingsFor(ATOPlatform platform)
        {
            var ps = PlatformSettingsFor(platform);
            var eff = new ATOEffectiveSettings();
            if (ps != null && ps.overrideEnabled)
            {
                eff.enable = enable;
                eff.qualityPreset = ps.qualityPreset;
                eff.customParameters = ps.customParameters;
                eff.generateAtlas = ps.generateAtlas;
                eff.islandPadding = ps.islandPadding;
                eff.minPixelDensity = ps.minPixelDensity;
                eff.maxPixelDensity = ps.maxPixelDensity;
                eff.npotAtlas = ps.npotAtlas;
                eff.dedupMaterials = ps.dedupMaterials;
                eff.dedupTextures = ps.dedupTextures;
                eff.mipmapsEnabled = ps.mipmapsEnabled;
                eff.compression = ps.compression;
            }
            else
            {
                eff.enable = enable;
                eff.qualityPreset = qualityPreset;
                eff.customParameters = customParameters;
                eff.generateAtlas = generateAtlas;
                eff.islandPadding = islandPadding;
                eff.minPixelDensity = minPixelDensity;
                eff.maxPixelDensity = maxPixelDensity;
                eff.npotAtlas = npotAtlas;
                eff.dedupMaterials = dedupMaterials;
                eff.dedupTextures = dedupTextures;
                eff.mipmapsEnabled = mipmapsEnabled;
                eff.compression = compression;
            }
            eff.parameters = ATOQualityParameters.For(eff.qualityPreset, eff.customParameters);
            return eff;
        }
    }

    /// <summary>
    /// A resolved, immutable-in-spirit snapshot of the effective settings for one build.
    /// 一次构建中解析出的有效设置快照。
    /// </summary>
    public class ATOEffectiveSettings
    {
        public bool enable = true;
        public ATOQualityPreset qualityPreset = ATOQualityPreset.Balanced;
        public ATOQualityParameters customParameters = ATOQualityParameters.Lossless();
        public ATOQualityParameters parameters;
        public bool generateAtlas = true;
        public int islandPadding = 4;
        public float minPixelDensity = 2048f;
        public float maxPixelDensity = 4096f;
        public bool npotAtlas = false;
        public bool dedupMaterials = true;
        public bool dedupTextures = true;
        public bool mipmapsEnabled = true;
        public ATOCompressionSettings compression = new ATOCompressionSettings();
    }
}
