using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace net.fosa.ato
{
    // ============================================================================
    // 流水线数据模型 / Pipeline data model.
    // 所有收集、分析、装箱、应用阶段共享的数据结构 / Shared data structures for the
    // collect -> analyze -> scale -> pack -> apply stages.
    // ============================================================================

    /// <summary>贴图类别 / Texture category. 决定指标算法、类型组与压缩选项 / Drives metrics, type groups and compression options.</summary>
    public enum ATOTextureCategory
    {
        /// <summary>主色贴图 / Main color texture (MS-SSIM + ΔE + alpha)</summary>
        Color = 0,

        /// <summary>法线贴图 / Normal map (angle error + p95)</summary>
        Normal = 1,

        /// <summary>蒙版等特殊纹理 / Mask & other special textures (grayscale-style, uses linear RMSE on used channels)</summary>
        Mask = 2,

        /// <summary>灰度贴图 / Grayscale texture (linear RMSE per used channel, worst channel wins)</summary>
        Grayscale = 3
    }

    /// <summary>贴图跳过级别 / How much optimization a texture skips.</summary>
    public enum ATOSkip
    {
        /// <summary>不跳过 / No skipping.</summary>
        None = 0,

        /// <summary>完全跳过(白名单): 不缩放、不图集化、不改导入参数 / Full skip (whitelist): no scaling, no atlasing, no import changes.</summary>
        Full = 1,

        /// <summary>仅跳过图集化: 参与整图缩放与导入参数优化 / Atlas-only skip: still whole-texture scaled and import-optimized.</summary>
        AtlasOnly = 2
    }

    /// <summary>跳过原因(用于日志) / Skip reasons (for logging).</summary>
    public enum ATOSkipReason
    {
        None = 0,
        Whitelist,
        WhitelistSharedUV,   // 同UV存在白名单贴图 / shares UV with a whitelisted texture
        StTransform,         // 材质或动画修改 ST / ST modified by material or animation
        UnknownShaderUsage,  // 着色器用途无法确认 / shader usage cannot be confirmed
        WrapCrossSeam,       // UV 越界且跨 wrap 缝 / out-of-bounds UV crossing the wrap seam
        SpecialUsage,        // 贴花/matcap/灯光记忆图等特殊用途 / decal, matcap, light-memory map, etc.
        NonReadable          // 无法读取像素(理论上不应发生) / pixels unreadable (shouldn't happen)
    }

    /// <summary>贴图信息 / Information about one collected texture.</summary>
    public sealed class ATOTextureInfo
    {
        public Texture2D source;            // 原贴图资产 / original texture asset
        public string assetPath;            // 资产路径 / asset path
        public int width, height;
        public bool sRGB;                   // 色彩空间 / color space
        public FilterMode filterMode;
        public TextureWrapMode wrapU, wrapV;
        public bool mipmapEnabled;
        public string importerKey;          // 导入设置指纹 / importer settings fingerprint
        public string contentHash;          // 像素内容哈希 / pixel content hash
        public ATOTextureCategory category = ATOTextureCategory.Color;
        public int uvChannel;                   // 该贴图经网格UV采样的通道(默认0) / mesh UV channel used by this texture (default 0)
        public ATOSkip skip = ATOSkip.None;
        public ATOSkipReason skipReason = ATOSkipReason.None;
        public string skipDetail;           // 人类可读原因 / human-readable detail
        public ATOTextureInfo dedupOf;      // 去重后指向代表贴图 / points to the representative after dedup
        public ATOTypeGroup group;          // 类型组 / assigned type group (null when skipped)
        public readonly List<ATOTextureRef> refs = new List<ATOTextureRef>();
        public readonly List<ATOIsland> islands = new List<ATOIsland>(); // 引用该贴图的岛 / islands using this texture
        public Texture2D readable;          // 构建期可读拷贝(用完即销毁) / temporary readable copy (destroyed after use)
        public bool hasAlpha;               // 是否含有效 alpha 通道 / whether alpha channel carries information
        public int usedChannels = 0b1111;   // 灰度贴图被使用通道位掩码 / used-channel bitmask for grayscale (R|G|B|A)
        public bool readableOwned;          // 可读拷贝是否为本工具创建(需销毁) / whether the readable copy is owned (must be destroyed)
        public bool isStandaloneResult;     // 最终是否为独立贴图(非图集) / final output is standalone (not an atlas)
        public float wholeScale = 1f;       // 整图缩放比例(非图集模式/AtlasOnly) / whole-texture scale (no-atlas mode / AtlasOnly)
        public Texture2D result;            // 最终输出贴图 / final output texture
        public string outputHash;           // 输出像素哈希(最终去重用) / output pixel hash (for final dedup)
    }

    /// <summary>贴图引用位置 / A reference to a texture.</summary>
    public sealed class ATOTextureRef
    {
        public Renderer renderer;              // 网格渲染器(材质槽引用) / renderer for material-slot refs
        public int slotIndex = -1;             // 材质槽下标 / material slot index
        public Material material;              // 材质属性引用 / material property reference
        public string property;                // 属性名 / property name (e.g. _MainTex)
        public AnimationClip clip;             // 动画引用 / animation clip reference (may be null)
        public EditorCurveBinding binding;     // 动画绑定 / curve binding (valid when clip != null)
    }

    /// <summary>类型组 / Texture type group. 图集按类型组生成 / Atlases are generated per type group.</summary>
    public sealed class ATOTypeGroup
    {
        public string key;                     // 组键 / group key (category|sRGB|filter)
        public ATOTextureCategory category;
        public bool sRGB;
        public FilterMode filterMode;
        public readonly List<ATOTextureInfo> textures = new List<ATOTextureInfo>();
        public readonly List<ATOAtlas> atlases = new List<ATOAtlas>();
    }

    /// <summary>UV 岛 / A UV island. 同一 UV 对应的所有贴图构成一个 UV 组 / All textures sharing this UV form one UV group.</summary>
    public sealed class ATOIsland
    {
        public ATOMeshInfo owner;              // 所属网格 / owning mesh
        public int channel;                    // UV 通道 / UV channel (0-3)
        public int[] triangles;                // 岛内三角形(全局索引) / triangles of this island (global indices)
        public Vector2[] uvs;                  // 岛内顶点UV(可能已归一) / island vertex UVs (possibly normalized)
        public Rect uvBounds;                  // 归一化包围盒 / normalized bounds
        public bool normalized;                // 是否做过越界平移归一 / whether out-of-bounds UVs were shift-normalized
        public float worldArea;                // 最大世界面积(m², 含形态键/缩放动画) / max world area in m² (incl. blendshapes & scale animation)
        public double origPixelsPerM;          // 原始物理像素密度(主贴图) / original physical texel density of the main texture
        public readonly List<ATOTextureInfo> textures = new List<ATOTextureInfo>(); // UV 组内全部贴图 / all textures in the UV group
        public readonly Dictionary<ATOTextureInfo, ATOIslandTexture> perTexture = new Dictionary<ATOTextureInfo, ATOIslandTexture>();
        public bool atlasCandidate = true;     // 是否参与图集 / participates in atlasing
        public int vertexCount;                // 岛内顶点数 / vertex count of this island
    }

    /// <summary>岛在某张贴图上的数据 / Per-texture island data.</summary>
    public sealed class ATOIslandTexture
    {
        public ATOTextureInfo texture;
        public Rect pixelRect;                 // 贴图像素矩形 / pixel rect on the texture
        public Vector2 scale = Vector2.one;    // 质量缩放(≤1, UV组共享后) / quality scale (≤1, after UV-group sharing)
        public Vector2 individualScale = Vector2.one; // UV组共享前该贴图岛自身的质量缩放 / this texture's own quality scale before UV-group sharing
        public bool solidColor;                // 纯色岛 / solid-color island
        public Color32 solid;
        public int targetWidth, targetHeight;  // 缩放后像素尺寸 / resized pixel size
        public double densityScale;            // 密度约束缩放 / density-constraint scale
        public bool resampleSkipped;           // 原样拷贝(质量=1或纯色短路) / copied as-is (quality=1 or solid shortcut)

        // 装箱结果 / packing result
        public ATOAtlas atlas;                 // 所属图集 / assigned atlas (null = standalone)
        public Rect atlasRect;                 // 图集内像素矩形 / pixel rect inside the atlas
        public int rotation;                   // 旋转步数(0/1/2/3 = 0/90/180/270) / rotation steps
        public Rect standaloneRect;            // 独立贴图重排矩形 / standalone repack rect
    }

    /// <summary>图集 / An atlas.</summary>
    public sealed class ATOAtlas
    {
        public ATOTypeGroup group;
        public int width, height;
        public string name;                    // ATO_ 开头 / prefixed with ATO_
        public readonly List<ATOPlacement> placements = new List<ATOPlacement>();
        public Texture2D result;               // 最终贴图资产 / final texture asset
        public bool hasAlpha;
        public float utilization;              // 利用率 / utilization (0..1)
        public long sourcePixels;              // 原贴图像素总量 / total source pixels
        public string outputHash;              // 输出像素哈希(最终去重用) / output pixel hash (for final dedup)
    }

    /// <summary>图集内的一个摆放 / One placement inside an atlas.</summary>
    public sealed class ATOPlacement
    {
        public ATOIsland island;
        public int rotation;
        public Rect normRect;                  // 归一化矩形(图集空间) / normalized rect (atlas space)
        public Rect cellRect;                  // 4px 粒度格子矩形(最终尺寸) / 4px-granularity cell rect (final size)
    }

    /// <summary>网格信息 / Mesh info.</summary>
    public sealed class ATOMeshInfo
    {
        public Renderer renderer;              // 渲染器 / the renderer (SMR or MR)
        public Mesh mesh;                      // 原网格 / original mesh
        public Mesh working;                   // 克隆网格(UV将被改写) / cloned mesh (UVs will be rewritten)
        public Material[] slots;               // 材质槽快照 / material slot snapshot
        public readonly List<ATOIsland> islands = new List<ATOIsland>(); // 全部通道全部岛 / all islands on all channels
        public readonly Dictionary<int, List<Vector2>> newUVs = new Dictionary<int, List<Vector2>>(); // channel -> new UVs
        public bool hasBlendShapes;
        public List<(string name, Vector3[] delta)> blendShapeDeltas; // 形态键位移 / blendshape deltas
        public float animatedScaleFactor = 1f; // 动画缩放最大倍数 / max animated scale multiplier
    }

    // ============================================================================
    // 动画分析结果 / Animation analysis results.
    // ============================================================================
    public sealed class ATOAnimAnalysis
    {
        public readonly List<AnimationClip> clips = new List<AnimationClip>();

        // 材质槽切换绑定: renderer -> slot -> bindings / material slot switching bindings
        public readonly Dictionary<Renderer, Dictionary<int, List<EditorCurveBinding>>> slotBindings =
            new Dictionary<Renderer, Dictionary<int, List<EditorCurveBinding>>>();

        // 渲染器启用动画 / renderer enabled animations
        public readonly Dictionary<Renderer, List<EditorCurveBinding>> enabledBindings =
            new Dictionary<Renderer, List<EditorCurveBinding>>();

        // 物体启用动画(m_IsActive) / GameObject active animations
        public readonly Dictionary<GameObject, List<EditorCurveBinding>> activeBindings =
            new Dictionary<GameObject, List<EditorCurveBinding>>();

        // 贴图属性动画: binding -> clip / texture property animations
        public readonly Dictionary<EditorCurveBinding, AnimationClip> texturePropBindings =
            new Dictionary<EditorCurveBinding, AnimationClip>();

        // ST 动画 / ST animations (_MainTex_ST.x ...)
        public readonly List<EditorCurveBinding> stBindings = new List<EditorCurveBinding>();

        // Cutoff 动画 / cutoff animations
        public readonly List<EditorCurveBinding> cutoffBindings = new List<EditorCurveBinding>();

        // 渲染模式动画(关键字/_Mode/_SrcBlend 等) / render-mode animations (keywords, _Mode, blend props)
        public readonly List<EditorCurveBinding> renderModeBindings = new List<EditorCurveBinding>();

        // 缩放动画: transform -> bindings / scale animations
        public readonly Dictionary<Transform, List<EditorCurveBinding>> scaleBindings =
            new Dictionary<Transform, List<EditorCurveBinding>>();

        // 形态键动画: renderer -> shape名 -> bindings / blendshape animations
        public readonly Dictionary<Renderer, Dictionary<string, List<EditorCurveBinding>>> blendShapeBindings =
            new Dictionary<Renderer, Dictionary<string, List<EditorCurveBinding>>>();

        // 收集到的全部材质(含动画切换出的) / all materials referenced (incl. via animation)
        public readonly HashSet<Material> allMaterials = new HashSet<Material>();
    }

    // ============================================================================
    // 解析后的配置 / Resolved configuration (global + platform override).
    // ============================================================================
    public sealed class ATOConfig
    {
        public BuildTarget platform;
        public bool enableAtlas;
        public int minPadding;
        public bool enableNPOT;
        public ATOQualityPreset qualityPreset;
        public ATOQualityParameters quality;   // 挡位解析后的参数 / resolved preset parameters
        public float minDensity, maxDensity;
        public bool enableMipmaps;
        public ATOCompressionFormat opaqueFormat, transparentFormat, normalFormat, grayscaleFormat;
        public int maxAtlasSize;               // 当前平台最大图集边长 / max atlas size on this platform
        public bool dedupMaterials, dedupTextures, mergeOpaqueSlots;
        public bool debugLogging;
        public List<Object> whitelist;
        public bool cancelled;

        /// <summary>质量挡位参数表(依据学术/业内研究设定, 见 CLAUDE.md) / Preset parameter table (see CLAUDE.md for rationale).</summary>
        public static ATOQualityParameters PresetParams(ATOQualityPreset preset)
        {
            switch (preset)
            {
                case ATOQualityPreset.Lossless:
                    return new ATOQualityParameters
                    {
                        msSsim = 1f, deltaE2000 = 0f, alphaIoU = 1f, alphaRmse = 0f,
                        normalAngleMean = 0f, normalAngleP95 = 0f, grayscaleRmse = 0f
                    };
                case ATOQualityPreset.High:
                    return new ATOQualityParameters
                    {
                        msSsim = 0.99f, deltaE2000 = 1.5f, alphaIoU = 0.98f, alphaRmse = 0.01f,
                        normalAngleMean = 1.0f, normalAngleP95 = 2.0f, grayscaleRmse = 0.004f
                    };
                case ATOQualityPreset.Medium:
                    return new ATOQualityParameters
                    {
                        msSsim = 0.97f, deltaE2000 = 3.0f, alphaIoU = 0.95f, alphaRmse = 0.02f,
                        normalAngleMean = 2.0f, normalAngleP95 = 4.0f, grayscaleRmse = 0.008f
                    };
                case ATOQualityPreset.Low:
                    return new ATOQualityParameters
                    {
                        msSsim = 0.94f, deltaE2000 = 6.0f, alphaIoU = 0.90f, alphaRmse = 0.04f,
                        normalAngleMean = 4.0f, normalAngleP95 = 8.0f, grayscaleRmse = 0.016f
                    };
                case ATOQualityPreset.Custom:
                default:
                    return null; // 由组件字段提供 / provided by the component field
            }
        }

        /// <summary>该类别是否等效近无损(跳过缩放) / Whether the preset is effectively lossless for this category.</summary>
        public bool IsLosslessFor(ATOTextureCategory cat)
        {
            if (qualityPreset == ATOQualityPreset.Lossless) return true;
            switch (cat)
            {
                case ATOTextureCategory.Color:
                    return quality.msSsim >= 1f && quality.deltaE2000 <= 0f
                           && quality.alphaIoU >= 1f && quality.alphaRmse <= 0f;
                case ATOTextureCategory.Normal:
                    return quality.normalAngleMean <= 0f && quality.normalAngleP95 <= 0f;
                case ATOTextureCategory.Mask:
                case ATOTextureCategory.Grayscale:
                    return quality.grayscaleRmse <= 0f;
                default:
                    return false;
            }
        }

        /// <summary>从组件 + 当前构建平台解析配置 / Resolve configuration from the component and current build target.</summary>
        public static ATOConfig Resolve(AvatarTextureOptimizer c, BuildTarget platform)
        {
            var cfg = new ATOConfig
            {
                platform = platform,
                enableAtlas = c.enableAtlas,
                minPadding = AvatarTextureOptimizer.ClampPadding(c.minPadding),
                enableNPOT = c.enableNPOT,
                qualityPreset = c.qualityPreset,
                minDensity = c.minTexelDensity,
                maxDensity = c.maxTexelDensity,
                enableMipmaps = c.enableMipmaps,
                opaqueFormat = c.opaqueFormat,
                transparentFormat = c.transparentFormat,
                normalFormat = c.normalFormat,
                grayscaleFormat = c.grayscaleFormat,
                dedupMaterials = c.dedupMaterials,
                dedupTextures = c.dedupTextures,
                mergeOpaqueSlots = c.mergeOpaqueSlots,
                debugLogging = c.debugLogging,
                whitelist = new List<Object>(c.whitelist ?? new List<Object>())
            };

            // 平台 override / platform overrides
            ATOPlatformSettings ps = null;
            if (platform == BuildTarget.Android) ps = c.android;
            else if (platform == BuildTarget.iOS) ps = c.ios;
            else ps = c.windows;

            if (ps != null && ps.overrideEnabled)
            {
                if (ps.overrideQuality) cfg.qualityPreset = ps.qualityPreset;
                if (ps.overrideDensity)
                {
                    cfg.minDensity = ps.minTexelDensity;
                    cfg.maxDensity = ps.maxTexelDensity;
                }

                if (ps.overrideAtlas) cfg.enableAtlas = ps.enableAtlas;
                if (ps.overridePadding) cfg.minPadding = AvatarTextureOptimizer.ClampPadding(ps.minPadding);
                if (ps.overrideCompression)
                {
                    cfg.opaqueFormat = ps.opaqueFormat;
                    cfg.transparentFormat = ps.transparentFormat;
                    cfg.normalFormat = ps.normalFormat;
                    cfg.grayscaleFormat = ps.grayscaleFormat;
                }

                if (ps.overrideMipmaps) cfg.enableMipmaps = ps.enableMipmaps;
            }

            cfg.maxAtlasSize = (ps != null && ps.overrideMaxAtlasSize) ? ps.maxAtlasSize
                : (platform == BuildTarget.Android || platform == BuildTarget.iOS ? 4096 : 8192);
            cfg.maxAtlasSize = Mathf.Clamp(cfg.maxAtlasSize, 64, 8192);

            // 质量参数解析 / resolve preset parameters
            var preset = PresetParams(cfg.qualityPreset);
            cfg.quality = preset ?? (c.customQuality != null ? c.customQuality.Clone() : new ATOQualityParameters());

            // 密度钳制 / density sanity
            if (cfg.minDensity <= 0) cfg.minDensity = 2048f;
            if (cfg.maxDensity < cfg.minDensity) cfg.maxDensity = cfg.minDensity;

            return cfg;
        }
    }

    // ============================================================================
    // 构建状态(NDMF context state) / Build state stored in the NDMF context.
    // ============================================================================
    public sealed class ATOBuildState
    {
        public ATOConfig config;
        public AvatarTextureOptimizer component;
        public readonly List<ATOMeshInfo> meshes = new List<ATOMeshInfo>();
        public readonly List<ATOTextureInfo> textures = new List<ATOTextureInfo>();
        public readonly Dictionary<Texture2D, ATOTextureInfo> byTexture = new Dictionary<Texture2D, ATOTextureInfo>();
        public readonly List<ATOTextureInfo> outputTextures = new List<ATOTextureInfo>(); // 最终贴图(用于最终去重) / final textures (for final dedup)
        public readonly List<ATOMaterialInfo> materialInfos = new List<ATOMaterialInfo>();
        public readonly Dictionary<Material, ATOMaterialInfo> byMaterial = new Dictionary<Material, ATOMaterialInfo>();
        public readonly List<Texture2D> tempDisposables = new List<Texture2D>(); // 需销毁的临时可读贴图 / temporary readable textures to destroy
        public ATOAnimAnalysis anim;
        public ATOPipelineContext pipelineContext = new ATOPipelineContext();
        public long skippedFull, skippedAtlasOnly, islandCount, atlasCount;
        public double totalSourcePixels, totalOutputPixels;
        public bool hasAAO;
    }

    /// <summary>材质信息 / Material info (for dedup & slot merging).</summary>
    public sealed class ATOMaterialInfo
    {
        public Material original;
        public Material current;               // 处理中的材质(可能是克隆) / material being processed (possibly cloned)
        public readonly List<ATOTextureRef> slotRefs = new List<ATOTextureRef>(); // 材质槽引用 / material slot refs
        public bool animated;                  // 被动画引用 / referenced by animation
        public bool opaque;                    // 是否不透明 / opaque or not
    }
}
