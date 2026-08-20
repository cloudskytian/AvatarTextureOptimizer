// ============================================================================
// ATO analysis data model
// ATO 分析数据模型
//
// Pure in-memory plan objects (no Unity mutation). Built by AnalysisStage,
// consumed by later stages.
// 纯内存 PLAN 对象（不改动 Unity）。由 AnalysisStage 构建，供后续阶段消费。
// ============================================================================

#region

using System.Collections.Generic;
using UnityEngine;

#endregion

namespace net.fosa.AvatarTextureOptimizer.Editor.Analysis
{
    /// <summary>One deduplicated texture instance with identity info.
    /// 一个去重后的贴图实例及身份标识。</summary>
    public sealed class ATOTextureRef
    {
        public int Id;
        public Texture2D Texture;
        /// <summary>Importer settings signature (different settings = different
        /// texture). 导入设置签名（设置不同=不同贴图）。</summary>
        public string ImportSignature;
        /// <summary>Lazy pixel content hash. 惰性像素内容哈希。</summary>
        public string ContentHash;
        public int Width;
        public int Height;
        public bool sRGB;

        // Whitelist 白名单
        public bool Whitelisted;
        public string WhitelistReason;

        // Type-group signature fields (filled from referencing materials)
        // 类型组签名字段（由引用材质填充）
        public bool HasNormal;
        public bool HasMask;
        public bool HasEmission;

        /// <summary>True when this texture must skip ATLASING only (it shares
        /// a UV with a whitelisted texture); it still gets whole-image
        /// scaling + import optimization.
        /// 仅跳过图集化（与白名单贴图同 UV）；仍参与整图缩放+导入优化。</summary>
        public bool AtlasDisabled;

        /// <summary>Materials currently referencing this texture.
        /// 当前引用该贴图的材质。</summary>
        public readonly List<Material> ReferringMaterials = new();
    }

    /// <summary>Per-material analysis result.
    /// 每个材质的分析结果。</summary>
    public sealed class ATOMaterialInfo
    {
        public Material Material;
        public Api.ATOShaderAnalysis Analysis;
        /// <summary>Strictest alpha mode across static + animated values:
        /// 0=opaque 1=cutout 2=blend 3=premultiply.
        /// 静态+动画取值的最严格透明模式。</summary>
        public int AlphaMode;
        /// <summary>Range of animated/static cutoff values (strictest = min
        /// for cutout). 裁剪阈值范围（裁剪取最小=最严）。</summary>
        public float CutoffMin = 0.5f, CutoffMax = 0.5f;
        public float SubpassCutoffMin = 0.5f, SubpassCutoffMax = 0.5f;
        public bool Whitelisted;
        public string WhitelistReason;

        /// <summary>property name -> assigned texture (non-null only).
        /// 属性名 -> 贴图（仅非空）。</summary>
        public readonly Dictionary<string, Texture2D> Textures = new();
        /// <summary>property name -> property analysis (from the analyzer).
        /// 属性名 -> 属性分析。</summary>
        public readonly Dictionary<string, Api.ATOShaderTextureRef> PropertyRefs = new();
    }

    /// <summary>One renderer + submesh + UV channel with its islands.
    /// 单个渲染器+子网格+UV 通道及其岛。</summary>
    public sealed class ATOMeshUVSet
    {
        public Renderer Renderer;
        public Mesh Mesh;
        public int Submesh;
        public int Channel;
        public int MaterialSlot;
        public Material Material;
        public bool IsSkinned;
        /// <summary>Islands of this UV set. 该 UV 集合的岛。</summary>
        public readonly List<ATOUVIsland> Islands = new();
        /// <summary>Meters per UV unit for this channel (bounds/uvExtent).
        /// 该通道每 UV 单位的世界长度。</summary>
        public float MetersPerUV;
        /// <summary>Max animated local-scale area factor (x*y) for this
        /// renderer's transform chain contribution. 动画最大局部缩放面积系数。</summary>
        public float MaxScaleArea = 1f;
        /// <summary>Shape-key area factor: max area at shape keys 0 and 100.
        /// 形态键面积系数：形态键 0 与 100 时面积最大值。</summary>
        public float ShapeKeyArea = 1f;
    }

    /// <summary>One UV island (connected triangle set) in one texture's UV
    /// space (normalized after wrap-safe shifting).
    /// 单个 UV 岛（连通三角集合），位于某贴图 UV 空间（可安全平移归一化后）。</summary>
    public sealed class ATOUVIsland
    {
        public int Id;
        public ATOMeshUVSet UVSet;
        /// <summary>Anchor texture ref id (primary albedo). 锚贴图 id。</summary>
        public int TexRefId;
        /// <summary>All texture refs sampled by this island's triangles
        /// (albedo + normal + mask + emission of the slot material).
        /// 该岛三角形采样的全部贴图（槽位材质的主色+法线+蒙版+自发光）。</summary>
        public readonly List<int> SampledTextureIds = new();
        /// <summary>Triangles (indices into UVSet.Mesh) of this island.
        /// 岛内三角形（UVSet.Mesh 顶点索引）。</summary>
        public int[] Triangles;
        /// <summary>UV bounding box AFTER normalization shift (in [0,1]).
        /// 归一化平移后的 UV 包围盒（在 [0,1] 内）。</summary>
        public Vector2 MinUV, MaxUV;
        /// <summary>Shift applied to bring the island into [0,1]
        /// (source UV = stored UV + ShiftUV). 归一化平移量（源 UV = 存储 UV + 平移量）。</summary>
        public Vector2 ShiftUV;
        /// <summary>UV-space area (0..1 units^2). UV 面积。</summary>
        public float UVArea;
        /// <summary>World-space area estimate (m^2). 世界面积估算。</summary>
        public float WorldArea;

        // Quality stage output 质量阶段输出
        /// <summary>Final island pixel size (0,0) until scaled.
        /// 岛最终像素尺寸（缩放前为 0）。</summary>
        public int TargetW, TargetH;
        public bool IsPureColor;
        public float ScaledW, ScaledH; // UV-space size after scaling 缩放后 UV 尺寸

        /// <summary>True when this island's UV group contains a whitelisted
        /// texture: UVs must NOT be remapped (the whitelisted texture keeps
        /// its original mapping); its non-whitelisted textures fall back to
        /// whole-image scaling.
        /// UV 组含白名单贴图：UV 不得重映射（白名单贴图保持原映射）；其非白
        /// 名单贴图回退整图缩放。</summary>
        public bool NoRemap;

        // Packing output 装箱输出
        public int UVGroup;
        public int ClusterId; // overlap cluster within same texture 同贴图重叠簇
        /// <summary>Atlas placement (page index, pixel pos, size, rot).
        /// 图集摆放（页索引、像素位置、尺寸、旋转）。</summary>
        public int AtlasPage = -1;
        public Vector2 AtlasPos;
        public int AtlasW, AtlasH;
        public int Rot90;
    }

    /// <summary>UV group: islands/textures sharing one UV region. All of their
    /// atlas pages must share an identical NORMALIZED layout for this region.
    /// UV 组：共享同一 UV 区域的岛/贴图。它们所在各图集页必须对此区域保持完全
    /// 一致的归一化布局。</summary>
    public sealed class ATOUVGroup
    {
        public int Id;
        /// <summary>Anchor island (largest area). 锚岛（面积最大）。</summary>
        public ATOUVIsland Anchor;
        /// <summary>All islands in the group (across meshes). 组内全部岛。</summary>
        public readonly List<ATOUVIsland> Islands = new();
        /// <summary>Texture refs involved. 涉及的贴图。</summary>
        public readonly List<int> TextureIds = new();
        /// <summary>Normalized bbox of the anchor (in [0,1] UV space).
        /// 锚岛归一化包围盒。</summary>
        public Vector2 MinUV, MaxUV;
        /// <summary>Layout scale: atlas pixels per UV unit (quality stage
        /// output, barrel-effect minimum over members).
        /// 布局比例：每 UV 单位的图集像素（质量阶段输出，木桶效应最小值）。</summary>
        public float LayoutKx, LayoutKy;
        /// <summary>Type-group ids involved (may be >1 when a UV group spans
        /// type groups; then involved type groups share page size).
        /// 涉及类型组 id（跨类型组时相关类型组共享图集尺寸）。</summary>
        public readonly List<int> TypeGroupIds = new();
    }

    /// <summary>Texture type group: albedo textures sharing (colorSpace,
    /// filterMode, hasNormal, hasMask, hasEmission).
    /// 贴图类型组：共享 (色彩空间, 过滤模式, 是否有法线/蒙版/自发光) 的主色贴图。</summary>
    public sealed class ATOTexTypeGroup
    {
        public int Id;
        public bool sRGB;
        public FilterMode Filter;
        public bool HasNormal;
        public bool HasMask;
        public bool HasEmission;
        /// <summary>Albedo texture refs in the group. 组内主色贴图。</summary>
        public readonly List<int> TextureIds = new();
        /// <summary>Texture ref -> which special textures it carries
        /// (role -> texref id). 贴图 -> 其携带的特殊贴图（角色->贴图 id）。</summary>
        public readonly Dictionary<int, Dictionary<Api.ATOTextureRole, int>> SpecialTextures = new();
        /// <summary>Chosen page size (width, height). 选定图集尺寸。</summary>
        public int PageW, PageH;
    }

    /// <summary>Aggregate analysis result for one build.
    /// 单次构建的聚合分析结果。</summary>
    public sealed class ATOAnalysis
    {
        public readonly Dictionary<int, ATOTextureRef> Textures = new();
        public readonly Dictionary<Material, ATOMaterialInfo> Materials = new();
        public readonly List<ATOMeshUVSet> MeshUVSets = new();
        public readonly List<ATOUVIsland> Islands = new();
        public readonly List<ATOUVGroup> UVGroups = new();
        public readonly List<ATOTexTypeGroup> TypeGroups = new();

        // Dedup: original texture -> chosen representative texture ref id
        // 去重：原贴图 -> 选中的代表贴图
        public readonly Dictionary<Texture, int> TextureDedupMap = new();
        /// <summary>Original material -> replacement material (dedup).
        /// 原材质 -> 替代表达材质（去重）。</summary>
        public readonly Dictionary<Material, Material> MaterialDedupMap = new();

        public int TextureCount;
        public int MaterialCount;
        public int IslandCount;
        public int WhitelistedTextureCount;

        /// <summary>Quality stage output: (islandId, texRefId) -> target
        /// pixel size. 质量阶段输出：(岛id, 贴图id) -> 目标像素尺寸。</summary>
        public readonly Dictionary<(int island, int tex), (int w, int h)> IslandScales = new();
        /// <summary>Pure-color (islandId, texRefId) pairs. 纯色岛。</summary>
        public readonly HashSet<(int island, int tex)> PureColorIslands = new();
        /// <summary>Texture refs that got whole-image scaling (no-atlas mode
        /// or dropped groups): texref -> scale factor.
        /// 整图缩放（无图集模式或放弃图集化）：贴图 -> 缩放系数。</summary>
        public readonly Dictionary<int, float> WholeTextureScales = new();

        /// <summary>Packing output (stage 3). 装箱输出（阶段 3）。</summary>
        public net.fosa.AvatarTextureOptimizer.Editor.Packing.ATOPackResult PackedResult;

        /// <summary>Whole-image scaled textures (stage 4): texref -> new tex.
        /// 整图缩放贴图（阶段4）：贴图 -> 新贴图。</summary>
        public readonly Dictionary<int, Texture2D> ScaledTextures = new();

        /// <summary>Final texture per (material, property) after atlas +
        /// scaling. 图集+缩放后每 (材质, 属性) 的最终贴图。</summary>
        public readonly Dictionary<(Material mat, string prop), Texture2D> FinalTextures = new();

        /// <summary>Slot remap per renderer (dedup/merge plan, stage 6).
        /// 每渲染器槽重映射（去重/合并计划，阶段6）。</summary>
        public readonly Dictionary<Renderer, Dictionary<int, int>> SlotRemap = new();
        /// <summary>Per-renderer apply plans (stage 6 output).
        /// 每渲染器应用计划（阶段6输出）。</summary>
        public readonly Dictionary<Renderer, Dedup.ATORendererPlan> RendererPlans = new();
        /// <summary>Import plans: texture -> settings (stage 5 output).
        /// 导入计划：贴图 -> 设置（阶段5输出）。</summary>
        public readonly Dictionary<Texture2D, Import.ATOImportPlan> ImportPlans = new();

        /// <summary>Gets the texture ref for a Texture2D (after dedup).
        /// 取贴图（去重后）的引用。</summary>
        public ATOTextureRef GetTextureRef(Texture2D tex)
        {
            if (!TextureDedupMap.TryGetValue(tex, out int id)) return null;
            return Textures.TryGetValue(id, out var r) ? r : null;
        }
    }
}
