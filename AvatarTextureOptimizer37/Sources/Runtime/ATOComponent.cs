// ============================================================================
// ATO (Avatar Texture Optimizer) - Main Component
// ATO（Avatar 贴图优化器）- 主组件
//
// Attach exactly ONE instance to a GameObject that also carries a
// VRCAvatarDescriptor (usually the avatar root). At NDMF build time the ATO
// plugin reads this component and applies texture optimization to the whole
// avatar.
// 在同时挂载了 VRCAvatarDescriptor 的对象（通常是 Avatar 根节点）上挂载且仅
// 挂载一个本组件。NDMF 构建时 ATO 插件读取该组件并对整个 Avatar 执行贴图
// 优化。
//
// The component is automatically removed from the processed avatar after a
// successful NDMF bake/build.
// 烘焙/构建成功后，本组件会从成品 Avatar 上自动移除。
// ============================================================================

#region

using System;
using System.Collections.Generic;
using UnityEngine;

#endregion

namespace net.fosa.AvatarTextureOptimizer
{
    /// <summary>
    /// Main ATO configuration component.
    /// ATO 主配置组件。
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("ATO/Avatar Texture Optimizer")]
    public class ATOComponent : MonoBehaviour
    {
        // ------------------------------------------------------------------
        // Master switch 总开关
        // ------------------------------------------------------------------
        [Tooltip("Master switch. When disabled the ATO build pass does nothing. " +
                 "总开关。禁用时 ATO 构建通道不做任何处理。")]
        [SerializeField] private bool active = true;

        public bool Active => active;

        // ------------------------------------------------------------------
        // Quality 质量
        // ------------------------------------------------------------------
        [Tooltip("Quality tier. Tiers change the underlying metric thresholds " +
                 "(see Advanced). Custom exposes raw parameters (default = all " +
                 "1.0 ≈ lossless). " +
                 "质量档位。档位变化时底层指标阈值联动变化（见高级选项）。" +
                 "自定义档暴露原始参数（默认全 1.0 ≈ 近无损）。")]
        [SerializeField] private ATOQualityTier qualityTier = ATOQualityTier.Medium;

        public ATOQualityTier QualityTier => qualityTier;

        [Tooltip("Quality value used by the Custom tier (0..1, 1 ≈ lossless). " +
                 "自定义档使用的质量值（0~1，1≈近无损）。")]
        [SerializeField, Range(0f, 1f)] private float customQuality = 1f;

        public float CustomQuality => customQuality;

        [Tooltip("Explicit metric thresholds for the Custom tier. These values " +
                 "are not overwritten when switching tiers. " +
                 "自定义档的显式指标阈值。切换档位时不会被覆盖。")]
        [SerializeField] private ATOQualityParams customParams = new ATOQualityParams(1f);

        public ATOQualityParams CustomParams => customParams;

        [Tooltip("Show advanced quality parameters (read-only preview of the " +
                 "thresholds derived from the current tier). " +
                 "显示高级质量参数（当前档位推导出的阈值预览，只读）。")]
        [SerializeField] private bool showAdvancedQuality = false;

        // ------------------------------------------------------------------
        // Pixel density 像素密度
        // ------------------------------------------------------------------
        [Tooltip("Minimum pixels-per-meter used to size UV islands. " +
                 "UV 岛尺寸估算使用的最小像素密度（px/m）。")]
        [SerializeField] private int minDensity = 2048;

        [Tooltip("Maximum pixels-per-meter used to size UV islands. " +
                 "UV 岛尺寸估算使用的最大像素密度（px/m）。")]
        [SerializeField] private int maxDensity = 4096;

        public int MinDensity => minDensity;
        public int MaxDensity => maxDensity;

        // ------------------------------------------------------------------
        // Atlas 图集
        // ------------------------------------------------------------------
        [Tooltip("Generate atlases. When disabled, unused UVs are NOT removed " +
                 "and UVs are NOT rearranged; whole textures are scaled " +
                 "directly instead. " +
                 "是否生成图集。关闭时不剔除未使用 UV、不重排 UV，直接缩放整张贴图。")]
        [SerializeField] private bool generateAtlas = true;

        public bool GenerateAtlas => generateAtlas;

        [Tooltip("Minimum island padding in pixels (atlas padding is " +
                 "max(ceil(maxAtlasEdge/128), this value)). " +
                 "岛间最小间距（px）。实际 padding = max(ceil(图集最大边长/128), 本值)。")]
        [SerializeField] private int minPadding = 4;

        public int MinPadding => minPadding;

        [Tooltip("Experimental: allow non-power-of-two atlas resolutions " +
                 "(step 64). Automatically removes compression formats that " +
                 "cannot be NPOT (e.g. PVRTC on iOS). Verified compatible with " +
                 "MipStreaming and Crunch. " +
                 "实验性：允许非 2 的幂图集分辨率（步进 64）。会自动剔除不支持 " +
                 "NPOT 的压缩格式（如 iOS 的 PVRTC）。已验证兼容 MipStreaming " +
                 "与 Crunch。")]
        [SerializeField] private bool useNPOT = false;

        public bool UseNPOT => useNPOT;

        // ------------------------------------------------------------------
        // Mipmaps / Mip Streaming  Mipmap / MipStreaming（绑定关系）
        // ------------------------------------------------------------------
        // VRChat requires mip streaming whenever mipmaps are enabled, so a
        // single switch per category controls both.
        // VRChat 要求开启 Mipmap 时必须开启 MipStreaming，因此每类贴图用单一开
        // 关同时控制两者。
        [Tooltip("Mipmaps + MipStreaming for opaque textures/atlas pages. " +
                 "不透明贴图/图集页的 Mipmap + MipStreaming。")]
        [SerializeField] private bool mipsOpaque = true;
        [Tooltip("Mipmaps + MipStreaming for transparent textures/atlas pages. " +
                 "透明贴图/图集页的 Mipmap + MipStreaming。")]
        [SerializeField] private bool mipsTransparent = true;
        [Tooltip("Mipmaps + MipStreaming for normal maps/atlas pages. " +
                 "法线贴图/图集页的 Mipmap + MipStreaming。")]
        [SerializeField] private bool mipsNormal = true;
        [Tooltip("Mipmaps + MipStreaming for grayscale textures/atlas pages. " +
                 "灰度贴图/图集页的 Mipmap + MipStreaming。")]
        [SerializeField] private bool mipsGray = true;

        public bool MipsOpaque => mipsOpaque;
        public bool MipsTransparent => mipsTransparent;
        public bool MipsNormal => mipsNormal;
        public bool MipsGray => mipsGray;

        // ------------------------------------------------------------------
        // Compression formats 压缩格式（安全枚举，由编辑器按平台/通道过滤）
        // ------------------------------------------------------------------
        [Tooltip("Compression format for opaque textures/atlas pages (alpha-less when the page has no alpha). " +
                 "不透明贴图/图集页压缩格式（图集页无 alpha 时按无 alpha 处理）。")]
        [SerializeField] private ATOFormatChoice formatOpaque = ATOFormatChoice.Auto;
        [Tooltip("Compression format for transparent textures/atlas pages. " +
                 "透明贴图/图集页压缩格式。")]
        [SerializeField] private ATOFormatChoice formatTransparent = ATOFormatChoice.Auto;
        [Tooltip("Compression format for normal maps/atlas pages. " +
                 "法线贴图/图集页压缩格式。")]
        [SerializeField] private ATOFormatChoice formatNormal = ATOFormatChoice.Auto;
        [Tooltip("Compression format for grayscale textures/atlas pages. " +
                 "灰度贴图/图集页压缩格式。")]
        [SerializeField] private ATOFormatChoice formatGray = ATOFormatChoice.Auto;

        public ATOFormatChoice FormatOpaque => formatOpaque;
        public ATOFormatChoice FormatTransparent => formatTransparent;
        public ATOFormatChoice FormatNormal => formatNormal;
        public ATOFormatChoice FormatGray => formatGray;

        // ------------------------------------------------------------------
        // Dedup switches 去重开关
        // ------------------------------------------------------------------
        [Tooltip("Deduplicate identical materials (content + parameters) after " +
                 "optimization. Merged opaque sub-meshes of the same mesh also " +
                 "merge their material slots; animation references are remapped. " +
                 "优化后对内容+参数完全相同的材质去重。同网格上合并的不透明子网" +
                 "格同时合并材质槽，并同步重映射动画引用。")]
        [SerializeField] private bool dedupMaterials = true;

        [Tooltip("Deduplicate identical textures/atlas pages (content + import " +
                 "settings). " +
                 "优化后对内容+导入设置完全相同的贴图/图集页去重（贴图和图集共用" +
                 "此开关）。")]
        [SerializeField] private bool dedupTextures = true;

        public bool DedupMaterials => dedupMaterials;
        public bool DedupTextures => dedupTextures;

        // ------------------------------------------------------------------
        // Whitelist 白名单
        // ------------------------------------------------------------------
        [Tooltip("Objects (mesh, material, texture, animator, game object, " +
                 "component, ...) whose referenced textures skip ALL " +
                 "optimization (including import parameters). Textures sharing " +
                 "a UV with a whitelisted texture skip atlasing but keep the " +
                 "other optimizations. " +
                 "白名单对象（网格、材质、贴图、动画、游戏对象、组件等）。白名单" +
                 "对象引用的全部贴图跳过所有优化（含导入参数）。与其同 UV 的其它" +
                 "贴图跳过图集化，但参与整图缩放与导入参数优化。")]
        [SerializeField] private List<UnityEngine.Object> whitelist = new();

        public IReadOnlyList<UnityEngine.Object> Whitelist => whitelist;

        // ------------------------------------------------------------------
        // Platform overrides 平台 Override
        // ------------------------------------------------------------------
        [Tooltip("Enable per-platform overrides. When checked, the overrides of " +
                 "the current build platform (PC/Android/iOS) restrict " +
                 "platform-limited parameters such as compression formats. " +
                 "Defaults follow the current build platform. " +
                 "启用按平台 Override。勾选后当前构建平台（PC/Android/iOS）的 " +
                 "覆盖项会对受平台限制的可自定义参数（如压缩格式）生效，默认值读" +
                 "取当前构建平台。")]
        [SerializeField] private bool platformOverride = false;

        public bool PlatformOverride => platformOverride;

        [SerializeField] private PlatformOverrideSettings pc = new();
        [SerializeField] private PlatformOverrideSettings android = new();
        [SerializeField] private PlatformOverrideSettings ios = new();

        public PlatformOverrideSettings PCOVERRIDE => pc;
        public PlatformOverrideSettings AndroidOverride => android;
        public PlatformOverrideSettings IOSOverride => ios;

        /// <summary>Per-platform parameter overrides.
        /// 每平台的参数覆盖。</summary>
        [Serializable]
        [System.Serializable]
        public class PlatformOverrideSettings
        {
            [Tooltip("Format for opaque textures on this platform. " +
                     "本平台不透明贴图格式。")]
            public ATOFormatChoice formatOpaque = ATOFormatChoice.Auto;
            [Tooltip("Format for transparent textures on this platform. " +
                     "本平台透明贴图格式。")]
            public ATOFormatChoice formatTransparent = ATOFormatChoice.Auto;
            [Tooltip("Format for normal maps on this platform. " +
                     "本平台法线贴图格式。")]
            public ATOFormatChoice formatNormal = ATOFormatChoice.Auto;
            [Tooltip("Format for grayscale textures on this platform. " +
                     "本平台灰度贴图格式。")]
            public ATOFormatChoice formatGray = ATOFormatChoice.Auto;

            [Tooltip("Mipmaps + MipStreaming (opaque). 本平台不透明 Mipmap。")]
            public bool mipsOpaque = true;
            [Tooltip("Mipmaps + MipStreaming (transparent). 本平台透明 Mipmap。")]
            public bool mipsTransparent = true;
            [Tooltip("Mipmaps + MipStreaming (normal). 本平台法线 Mipmap。")]
            public bool mipsNormal = true;
            [Tooltip("Mipmaps + MipStreaming (gray). 本平台灰度 Mipmap。")]
            public bool mipsGray = true;

            [Tooltip("Allow NPOT atlases on this platform. 本平台允许 NPOT 图集。")]
            public bool useNPOT = false;
        }

        // ------------------------------------------------------------------
        // i18n 国际化
        // ------------------------------------------------------------------
        [Tooltip("UI language. \"auto\" follows the NDMF language selection; " +
                 "otherwise pick one of the loaded i18n/*.json languages. " +
                 "Missing keys fall back to English. " +
                 "界面语言。auto 跟随 NDMF 语言选择；否则从 i18n/*.json 已加载" +
                 "语言中选择。缺失键回退英文。")]
        [SerializeField] private string languageOverride = "auto";

        public string LanguageOverride => languageOverride;

        // ------------------------------------------------------------------
        // Debug logging 调试日志
        // ------------------------------------------------------------------
        [Tooltip("Enable verbose [ATO] logging to the Unity console (advanced " +
                 "users). 开启详细 [ATO] 日志（高级用户）。")]
        [SerializeField] private bool verboseLogging = false;

        public bool VerboseLogging => verboseLogging;

        [Tooltip("Log categories. 日志类别。")]
        [SerializeField] private ATOLogMask logMask =
            ATOLogMask.Analysis | ATOLogMask.Quality | ATOLogMask.Packing |
            ATOLogMask.Atlas | ATOLogMask.Import | ATOLogMask.Dedup;

        public ATOLogMask LogMask => logMask;
    }
}
