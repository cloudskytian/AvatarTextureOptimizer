// Avatar Texture Optimizer / 头像贴图优化器
// Central usage model: textures, usages, UV groups, islands.
// 核心使用模型：贴图、用途、UV 组、UV 岛。

using System;
using System.Collections.Generic;
using UnityEngine;

namespace FOSA.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Registry entry for one physical Texture2D (post-dedup).
    /// 一张物理 Texture2D（去重后）的登记表项。
    /// </summary>
    public sealed class ATOTextureEntry
    {
        public Texture2D texture;
        public string assetPath;
        public int width, height;
        public bool sRGB;                 // importer sRGB flag / 导入器 sRGB 标志
        public FilterMode filterMode;
        public TextureWrapMode wrapModeU = TextureWrapMode.Repeat;
        public TextureWrapMode wrapModeV = TextureWrapMode.Repeat;
        public bool isNormalMap;          // importer NormalMap type / 导入器法线类型
        public bool alphaIsTransparency;
        public bool mipmapsEnabled;
        public bool streamingMipmaps;
        public TextureFormat format;
        public string contentHash;        // filled by dedup / 去重阶段填充
        public string importSignature;    // import-settings signature / 导入设置签名
        public ATOExcludeReason exclusion = ATOExcludeReason.None;
        public string exclusionNote;
        public ATOTextureCategory category = ATOTextureCategory.Opaque; // classified later / 后续分类
        public bool hasRealAlpha;         // pixel content check / 像素内容检查
        public long sourceBytes;          // encoded source size / 源文件体积
        public bool pureColor;            // solid color detection / 纯色检测
        public Color pureColorValue;

        /// <summary>True when freely optimizable. / 可自由优化。</summary>
        public bool Optimizable => exclusion == ATOExcludeReason.None;

        /// <summary>Import-settings fingerprint used as dedup gate. / 去重门槛用的导入设置指纹。</summary>
        public string ImportSignature() => importSignature;
    }

    /// <summary>
    /// One concrete usage of a texture: a material slot on a renderer/submesh
    /// sampling it through a specific UV channel.
    /// 一次具体用途：某渲染器某子网格的材质槽经某 UV 通道采样该贴图。
    /// </summary>
    public sealed class ATOUsage
    {
        public ATOTextureEntry texture;
        public ATORole role = ATORole.Unknown;
        public string propertyName;
        public int usedChannels = 0xF;
        public Renderer renderer;
        public string rendererPath;          // avatar-relative path / 相对 Avatar 的路径
        public int submeshIndex;
        public int materialSlot;             // material slot on the renderer / 渲染器槽位
        public int uvChannel;
        public Material material;            // analyzed material (original) / 材质（原始）
        public ATORenderMode renderMode = ATORenderMode.Opaque;
        public float cutoff = 0.5f;
        public ATOExcludeReason exclusion = ATOExcludeReason.None;
        public string note;
        public bool fromAnimation;           // discovered via material swap / 来自动画切换
        /// <summary>Back-reference to the owning UV group (set during grouping). / 所属 UV 组反向引用（分组时填充）。</summary>
        public ATOUVGroup group;

        public bool Optimizable => exclusion == ATOExcludeReason.None && texture != null && texture.Optimizable;

        /// <summary>
        /// Owning UV group; falls back to a full model scan when the back-ref is stale.
        /// 所属 UV 组；反向引用失效时回退为全模型扫描兜底。
        /// </summary>
        public ATOUVGroup GroupOf(ATOUsageModel model)
        {
            if (group != null && group.usages.Contains(this)) return group;
            if (model != null)
            {
                foreach (var g in model.uvGroups)
                {
                    if (g.usages.Contains(this))
                    {
                        group = g;
                        return g;
                    }
                }
            }
            return null;
        }

        /// <summary>A texture renders at most this strictly across all usages. / 取最严格指标时按用途逐一评估。</summary>
        public ATORenderMode StrictestMode => renderMode;
    }

    /// <summary>
    /// Shared island geometry for a UV group. All group textures use identical
    /// placement, so the geometry is texture-independent.
    /// UV 组共享的岛几何。组内全部贴图位置一致，因此几何与贴图无关。
    /// </summary>
    public sealed class ATOIsland
    {
        public int index;
        /// <summary>Island-local baked UVs (normalized into [0,1], per-island offsets applied). / 岛局部烘焙 UV（已归一到 [0,1]，逐岛平移已应用）。</summary>
        public Vector2[] bakedUVs;
        /// <summary>Original mesh vertex ids parallel to bakedUVs. / 与 bakedUVs 平行的原始网格顶点 ID。</summary>
        public int[] origVertexIds;
        /// <summary>Triangles as index triples into bakedUVs. / 以 bakedUVs 索引的三角形三元组。</summary>
        public int[] localTriangles;
        /// <summary>Counts of source triangles (statistics). / 源三角形数量（统计）。</summary>
        public int sourceTriangleCount;
        public Vector2 uvMin, uvMax;         // normalized [0..1] uv bounds / 归一化 [0..1] 包围盒
        public float uvArea;                 // 0..1 of full texture / 占整张贴图面积比例
        public float worldArea;              // m^2 without renderer factors / 未乘渲染器系数的真实面积
        // anisotropy (PCA over uv-space triangle area) / 各向异性（UV 空间三角形面积的主成分）
        public Vector2 axisMajor, axisMinor;
        public float lenMajor, lenMinor;
        // per-texture raster state filled during quality/packing / 质量与装箱阶段按贴图填充
    }

    /// <summary>
    /// A UV group: all textures sharing one (mesh, submesh, channel) UV layout.
    /// Textures here are located identically in any atlas.
    /// UV 组：共享同一（网格、子网格、UV 通道）UV 布局的全部贴图；图集内位置一致。
    /// </summary>
    public sealed class ATOUVGroup
    {
        public Mesh mesh;
        public int submesh;
        public int uvChannel;
        public readonly List<ATOUsage> usages = new List<ATOUsage>();
        public readonly List<ATOIsland> islands = new List<ATOIsland>();

        /// <summary>Max world-area factor from renderers (scale^2 with animation/blendshape factors). / 渲染器面积系数最大值（含动画缩放与形态键）。</summary>
        public float areaFactor = 1f;

        /// <summary>
        /// Blocked from atlas generation (seam-crossing UV islands, AAO channel
        /// conflict with no free channel, ...). Whole-texture scaling may still be
        /// possible; groups with zero islands are hard-whitelisted (never touched).
        /// 被阻止进入图集（UV 岛跨缝、AAO 通道冲突且无空闲通道等）。整图缩放仍可能进行；
        /// 岛数为 0 的组为硬白名单（绝不触碰）。
        /// </summary>
        public bool IsAtlasBlocked { get; private set; }

        /// <summary>Human-readable reason for the atlas block. / 图集阻塞的人类可读原因。</summary>
        public string AtlasBlockReason { get; private set; }

        /// <summary>Mark the group as atlas-blocked with a reason. / 以指定原因标记图集阻塞。</summary>
        public void SetAtlasBlocked(string reason)
        {
            IsAtlasBlocked = true;
            AtlasBlockReason = string.IsNullOrEmpty(reason) ? "blocked / 已阻塞" : reason;
        }

        /// <summary>
        /// Final pipeline disposition of this group ("atlas", "standalone:&lt;tag&gt;",
        /// "whitelist: ...", ...). Always non-null by report time (pipeline fills a
        /// default for untouched groups) so every group is traceable.
        /// 该组在管线中的最终处置（"atlas"、"standalone:&lt;tag&gt;"、
        /// "whitelist: ..." 等）。报告生成前保证非空（未触碰组由管线填默认值），
        /// 使每个组都可追溯。
        /// </summary>
        public string FinalDisposition { get; set; }

        /// <summary>Roles present among the group's (non-excluded) textures. / 组内（未排除）贴图的角色集合。</summary>
        public HashSet<ATORole> RolesPresent()
        {
            var set = new HashSet<ATORole>();
            foreach (var u in usages)
                if (u.Optimizable) set.Add(u.role);
            return set;
        }

        /// <summary>Group type-group signature (roles + colorspace + filterMode). / 类型组签名（角色+色彩空间+过滤模式）。</summary>
        public string TypeGroupSignature()
        {
            var roles = new SortedSet<int>();
            foreach (var u in usages)
                if (u.Optimizable) roles.Add((int)u.role);
            var filter = -2; // mixed marker / 混合标记
            var srgb = -2;
            foreach (var u in usages)
            {
                if (!u.Optimizable) continue;
                int f = (int)u.texture.filterMode;
                filter = filter == -2 ? f : (filter == f ? f : -1);
                int s = u.texture.sRGB ? 1 : 0;
                srgb = srgb == -2 ? s : (srgb == s ? s : -1);
            }
            return $"r[{string.Join(",", roles)}]|f{filter}|s{srgb}";
        }

        /// <summary>All optimizable textures in this group (distinct). / 组内全部可优化贴图（去重）。</summary>
        public IEnumerable<ATOTextureEntry> OptimizableTextures()
        {
            var seen = new HashSet<ATOTextureEntry>();
            foreach (var u in usages)
                if (u.Optimizable && seen.Add(u.texture)) yield return u.texture;
        }

        /// <summary>All textures (distinct) regardless of exclusion. / 组内全部贴图（去重，不看排除）。</summary>
        public IEnumerable<ATOTextureEntry> AllTextures()
        {
            var seen = new HashSet<ATOTextureEntry>();
            foreach (var u in usages)
                if (u.texture != null && seen.Add(u.texture)) yield return u.texture;
        }
    }

    /// <summary>
    /// Renderer record: static & animation-driven surface scaling data.
    /// 渲染器记录：静态与动画驱动的表面缩放数据。
    /// </summary>
    public sealed class ATORendererRecord
    {
        public Renderer renderer;
        public string path;
        public Mesh mesh;
        public bool isSkinned;
        public Vector3 staticScale = Vector3.one;
        public Vector3 animatedScaleMax = Vector3.one; // from animation scan / 动画扫描的最大缩放
        public float blendshapeFactor = 1f;            // max per-vertex displacement factor / 形态键最大位移系数
        public bool activeAnimated;                    // enable/disable animated / 受启停动画影响
        public List<Material> staticMaterials = new List<Material>();
        public Dictionary<int, HashSet<Material>> animatedSlotMaterials = new Dictionary<int, HashSet<Material>>();
        public Dictionary<string, ATOFloatRange> animatedFloats = new Dictionary<string, ATOFloatRange>();
        public HashSet<string> animatedPropNames = new HashSet<string>();
        public bool stAnimated;                        // any ST animated -> whitelist affected / ST 被动画 -> 影响贴图白名单

        /// <summary>Overall max surface area factor for this renderer. / 渲染器的综合最大表面积系数。</summary>
        public float MaxAreaFactor()
        {
            float sx = Mathf.Max(Mathf.Abs(staticScale.x * animatedScaleMax.x), 1e-6f);
            float sy = Mathf.Max(Mathf.Abs(staticScale.y * animatedScaleMax.y), 1e-6f);
            float sz = Mathf.Max(Mathf.Abs(staticScale.z * animatedScaleMax.z), 1e-6f);
            // two largest components dominate surface area / 两个最大分量主导表面积
            float a = Mathf.Max(sx, Mathf.Max(sy, sz));
            float b = Mathf.Min(sx + sy + sz - a - Mathf.Min(sx, Mathf.Min(sy, sz)), a);
            return a * b * Mathf.Max(1f, blendshapeFactor);
        }
    }

    /// <summary>
    /// Whole-avatar usage model. The single source of truth for the pipeline.
    /// 全 Avatar 使用模型。整条管线的唯一事实来源。
    /// </summary>
    public sealed class ATOUsageModel
    {
        public readonly List<ATORendererRecord> renderers = new List<ATORendererRecord>();
        public readonly Dictionary<Texture2D, ATOTextureEntry> textures = new Dictionary<Texture2D, ATOTextureEntry>();
        public readonly List<ATOUsage> usages = new List<ATOUsage>();
        public readonly List<ATOUVGroup> uvGroups = new List<ATOUVGroup>();
        public ATOAnimationData animation;
        public readonly HashSet<Texture2D> whitelistedTextures = new HashSet<Texture2D>();
        public readonly List<string> notes = new List<string>();
        /// <summary>Original -> representative texture dedup map. / 原始 -> 代表贴图的去重映射。</summary>
        public readonly Dictionary<Texture2D, Texture2D> textureDedupMap = new Dictionary<Texture2D, Texture2D>();
        public readonly ATOBuildReport report = new ATOBuildReport();

        /// <summary>Get-or-create a texture entry. / 取或创建贴图表项。</summary>
        public ATOTextureEntry EntryFor(Texture2D tex)
        {
            if (textures.TryGetValue(tex, out var e)) return e;
            e = new ATOTextureEntry { texture = tex };
            textures[tex] = e;
            return e;
        }
    }
}
