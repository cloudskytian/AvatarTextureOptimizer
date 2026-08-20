// ATODataModel.cs — 核心数据模型 / Core data model.
// 说明：定义管线中各阶段共享的数据结构（材质槽、贴图用途、UV 岛、贴图引用、类型组、箱、布局）。
// Note: shared data structures used across pipeline stages (slots, texture usages, UV islands,
// island refs, type groups, bins, layout placements).

using System;
using System.Collections.Generic;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer
{
    // ============================================================
    // 贴图角色（类型组依据）/ Texture roles (basis for type groups)
    // ============================================================
    /// <summary>
    /// 贴图角色位掩码。Main=主色贴图；Normal=法线贴图；Mask=蒙版/数据贴图；Color=其他颜色贴图（发光/MatCap 等，质量策略同主色）。
    /// Texture role bitmask. Main=albedo; Normal=normal map; Mask=mask/data; Color=other color textures (emission/MatCap; same quality policy as Main).
    /// </summary>
    [Flags]
    public enum ATORole
    {
        Main = 1 << 0,
        Normal = 1 << 1,
        Mask = 1 << 2,
        Color = 1 << 3,
    }

    /// <summary>
    /// 透明度使用方式位标志（对质量评估方式有决定性影响）。多个模式同时置位表示"动画可能修改渲染模式，取最严苛评估"。
    /// Alpha usage bit flags (decides the quality evaluation method). Multiple bits mean the render mode may be animated — evaluate all, strictest wins.
    /// </summary>
    [Flags]
    public enum ATOAlphaUsage
    {
        Opaque = 0,  // 不透明（无额外检查）/ Opaque (no extra checks)
        Cutout = 1,  // 裁剪（clip 后轮廓 IoU）/ Cutout (post-clip silhouette IoU)
        Blend = 2,   // 混合（线性预乘 alpha RMSE）/ Blend (linear premultiplied-alpha RMSE)
    }

    /// <summary>贴图质量求解结果缓存键的类别 / category of a solved scale (for cache keys).</summary>
    public enum ATOScaleCategory
    {
        Base,      // 主色/颜色角色（基础空间）/ Main/Color role (base space)
        Normal,    // 法线 / Normal
        Mask,      // 蒙版 / Mask
        Grayscale, // 灰度 / Grayscale
    }

    // ============================================================
    // 贴图用途 / Texture usage
    // ============================================================
    /// <summary>
    /// 一次贴图引用：某材质某属性以某角色、某 UV 通道、某透明度模式引用某贴图。
    /// One texture reference: a material property referencing a texture with a role, UV channel and alpha mode.
    /// </summary>
    public sealed class ATOTextureUsage
    {
        public Texture2D texture;               // 贴图 / the texture
        public ATORole role;                    // 角色 / role
        public int uvChannel;                   // 采样 UV 通道 / sampled UV channel
        public Material material;               // 引用材质 / referencing material
        public string propertyName;             // 属性名 / property name
        public Vector4 st = new Vector4(1, 1, 0, 0); // ST 平移/缩放/旋转 / scale-translate
        public ATOAlphaUsage alphaUsage = ATOAlphaUsage.Opaque; // 透明度模式位标志 / alpha mode bit flags
        public float[] cutoffSamples = new[] { 0.5f };        // Cutout 阈值采样（当前值 + 动画取值，逐一评估）/ cutout threshold samples (current + animated values, all evaluated)
        public bool isSRGB = true;              // 贴图是否 sRGB（决定色彩空间/类型组）/ whether the texture is sRGB (color space / type group)
        public bool animatedST;                 // ST 是否被动画修改 / whether ST is animated
        public bool animatedCutoff;             // Cutoff 是否被动画修改 / whether cutoff is animated
        public bool whitelisted;                // 是否白名单 / whitelisted
        public string whitelistReason;          // 白名单原因（供报告）/ whitelist reason (for reporting)
        public string shaderName;               // 材质着色器名（报告用）/ shader name (for reporting)

        /// <summary>贴图角色类别（用于质量求解与图集角色划分）/ The scale category for this role.</summary>
        public ATOScaleCategory Category
        {
            get
            {
                switch (role)
                {
                    case ATORole.Normal: return ATOScaleCategory.Normal;
                    case ATORole.Mask: return ATOScaleCategory.Mask;
                    default: return ATOScaleCategory.Base;
                }
            }
        }

        public ATOTextureUsage Clone()
        {
            return (ATOTextureUsage)MemberwiseClone();
        }
    }

    // ============================================================
    // 渲染器与材质槽 / Renderers and slots
    // ============================================================
    /// <summary>渲染器信息（含全部可能的材质槽内容）/ Renderer info (incl. all possible slot contents).</summary>
    public sealed class ATORendererInfo
    {
        public Renderer renderer;                    // 渲染器 / the renderer
        public Mesh mesh;                            // 共享网格 / shared mesh
        public bool skinned;                         // 是否 SkinnedMeshRenderer
        public bool editorOnly;                      // EditorOnly（跳过）/ EditorOnly (skip)
        public bool mayBeEnabled = true;             // 是否可能被启用（含动画启用）/ may be enabled (incl. via animation)
        public List<List<Material>> slots = new List<List<Material>>(); // 每槽全部可能材质 / all possible materials per slot
        public float maxAnimScaleFactor = 1f;        // 动画最大缩放导致的面积放大系数 / area amplification from max animated scale
        public string path;                          // 相对路径（日志用）/ relative path (for logs)

        /// <summary>该渲染器所有材质槽上的全部贴图用途。/ All texture usages across all slots of this renderer.</summary>
        public List<ATOTextureUsage> usages = new List<ATOTextureUsage>();
    }

    // ============================================================
    // 贴图信息 / Texture info
    // ============================================================
    /// <summary>贴图信息（含用途列表与去重结果）/ Texture info (usages + dedup result).</summary>
    public sealed class ATOTextureInfo
    {
        public Texture2D texture;                    // 贴图 / the texture
        public int width;                            // 宽度 / width
        public int height;                           // 高度 / height
        public bool isSRGB;                          // sRGB 色彩空间 / sRGB color space
        public FilterMode filterMode;                // 过滤模式 / filter mode
        public bool whitelisted;                     // 白名单 / whitelisted
        public string whitelistReason;               // 白名单原因 / reason
        public List<ATOTextureUsage> usages = new List<ATOTextureUsage>(); // 全部用途 / all usages
        public ATOTextureInfo dedupTarget;           // 去重目标（自身为 null）/ dedup target (null if self)
        public int uniqueHash;                       // 内容+导入参数哈希 / content+import hash
    }

    // ============================================================
    // UV 岛 / UV islands
    // ============================================================
    /// <summary>
    /// 一个 UV 岛（网格 + UV 通道 + 三角形集合）。
    /// An UV island (mesh + UV channel + triangle set).
    /// </summary>
    public sealed class ATOIsland
    {
        public int id;                               // 全局唯一 / globally unique
        public Mesh mesh;                            // 网格 / mesh
        public int channel;                          // UV 通道 / UV channel
        public List<int> triangles = new List<int>();// 三角形索引 / triangle indices
        public Vector2 uvMin, uvMax;                 // 原始 UV 包围盒（含越界）/ original UV bbox (may be out of [0,1])
        public Vector2 translation;                  // 归一到 [0,1] 的整体平移（整数）/ integral translation normalizing into [0,1]
        public float uvArea;                         // UV 空间面积 / UV-space area
        public float worldAreaMax = 0f;              // 最大世界面积（含实例缩放/形态键/动画缩放）/ max world area (instances/morphs/anim scale)
        public List<ATOIslandRef> refs = new List<ATOIslandRef>(); // 引用本岛的全部贴图 / textures referencing this island
        public bool anyWhitelistedRef;               // 是否有白名单引用（则该岛跳过图集化）/ any whitelisted ref (island skips atlasing)
        public bool wrapIssue;                       // UV 跨 wrap 缝（无法归一）/ UV crosses the wrap seam (cannot normalize)
        public List<ATOIsland> mergedChildren;       // 若为合并岛：被合并的子岛 / merged sub-islands when merged
        public bool merged;                          // 是否为合并岛 / whether this is a merged island
        // 各角色求解尺寸（木桶聚合结果，装箱用）/ per-role solved sizes (barrel-aggregated, used by packing)
        public float baseSizeU;                      // 基础（主色/颜色）U 尺寸 px / base (main/color) U size px
        public float baseSizeV;                      // 基础 V 尺寸 / base V size
        public float normalSizeU;                    // 法线 U 尺寸 / normal U size
        public float normalSizeV;                    // 法线 V 尺寸 / normal V size
        public float maskSizeU;                      // 蒙版 U 尺寸 / mask U size
        public float maskSizeV;                      // 蒙版 V 尺寸 / mask V size
    }

    /// <summary>
    /// 岛上一份贴图引用（按贴图+角色聚合多个材质用途）及其求解结果。
    /// One texture reference on an island (aggregates multiple material usages by texture+role) and its solved result.
    /// </summary>
    public sealed class ATOIslandRef
    {
        public Texture2D texture;                    // 贴图 / the texture
        public ATORole role;                         // 角色 / role
        public ATOScaleCategory category;            // 类别 / category
        public List<ATOTextureUsage> usages = new List<ATOTextureUsage>(); // 聚合的用途（逐一评估、取最严）/ aggregated usages (all evaluated, strictest wins)
        public RectInt cropRect;                     // 裁剪像素矩形（归一后）/ crop pixel rect (after normalization)
        public Vector2 cropOffset;                   // 相对岛布局矩形的偏移（合并岛用）/ offset from island layout rect (for merged islands)
        public bool whitelisted;                     // 白名单 / whitelisted
        public string whitelistReason;               // 白名单原因 / reason
        public float nativeWidth;                    // 原生像素宽 / native pixel width
        public float nativeHeight;                   // 原生像素高 / native pixel height
        // 求解结果（相对原生尺寸的比例，≤1）/ solved scales (relative to native, ≤1)
        public float solvedScaleU = 1f;              // U 轴 / U axis
        public float solvedScaleV = 1f;              // V 轴 / V axis
        public bool solved;                          // 是否已求解 / solved
        public bool pureColor;                       // 纯色岛短路 / solid-color short-circuit applied
        public bool losslessCopy;                    // 近无损原样拷贝 / near-lossless plain copy
        // 装箱结果 / packing results
        public ATOBin bin;                           // 所在箱 / the bin
        public Vector2 layoutMin;                    // 归一化布局位置（基础图集空间）/ normalized layout position (base atlas space)
        public Vector2 layoutSize;                   // 归一化布局尺寸 / normalized layout size
        public int layoutRotation;                   // 布局旋转（90 度步进）/ layout rotation (90° steps)
    }

    // ============================================================
    // 类型组、箱、布局 / Type groups, bins, layout
    // ============================================================
    /// <summary>
    /// 贴图类型组：特殊贴图类型组合（法线/蒙版）、色彩空间、过滤模式等共同构成组键；
    /// 同组贴图共享同一套归一化布局，生成一份或多份图集（箱）。
    /// Type group: the combination of special texture types (normal/mask), color space, filter mode forms the group key;
    /// textures in a group share one normalized layout and produce one or more atlases (bins).
    /// </summary>
    public sealed class ATOTypeGroup
    {
        public int id;                               // 组 ID / group id
        public string key;                           // 组键（报告用）/ key (for reporting)
        public HashSet<ATOTextureInfo> textures = new HashSet<ATOTextureInfo>(); // 组内贴图 / textures in group
        public List<ATOBin> bins = new List<ATOBin>(); // 图集箱 / atlas bins
        public Dictionary<ATOIsland, ATOPlacement> layout = new Dictionary<ATOIsland, ATOPlacement>(); // 岛→布局 / island → placement
        public bool hasNormal;                       // 组内含法线 / has normal maps
        public bool hasMask;                         // 组内含蒙版 / has mask maps
    }

    /// <summary>岛的归一化布局位置与旋转。/ Normalized layout placement and rotation of an island.</summary>
    public sealed class ATOPlacement
    {
        public Vector2 min;      // 归一化位置 / normalized position
        public Vector2 size;     // 归一化尺寸 / normalized size
        public int rotation;     // 0/90/180/270
        public ATOBin bin;       // 首次放置的箱 / the bin where it was first placed
    }

    /// <summary>
    /// 一个图集箱（成品图集）：固定尺寸、占用位掩码、已放置项、各角色缩放系数与最终图集贴图。
    /// One atlas bin (a finished atlas): fixed dimensions, occupancy bitmask, placed items, per-role scale factors and final atlas textures.
    /// </summary>
    public sealed class ATOBin
    {
        public ATOTypeGroup group;                   // 所属组 / owning group
        public int width;                            // 宽（基础空间，px）/ width (base space, px)
        public int height;                           // 高（基础空间，px）/ height (base space, px)
        public ATOBitmask occupancy;                 // 占用位掩码（4px 粒度）/ occupancy bitmask (4px granularity)
        public List<ATOItem> items = new List<ATOItem>(); // 已放置项 / placed items
        public float normalScaleU = 1f;              // 法线图集 U 缩放系数（≤1）/ normal atlas U scale factor (≤1)
        public float normalScaleV = 1f;              // 法线图集 V 缩放系数 / normal atlas V scale factor
        public float maskScaleU = 1f;                // 蒙版图集 U 缩放系数 / mask atlas U scale factor
        public float maskScaleV = 1f;                // 蒙版图集 V 缩放系数 / mask atlas V scale factor
        public bool isSRGB;                          // 色彩空间（主色）/ color space (base role)
        public FilterMode filterMode;                // 过滤模式（取组内最高）/ filter mode (highest in group)
        public bool hasAlpha;                        // 是否含 alpha（决定透明/不透明分类）/ contains alpha (transparent vs opaque)
        public Dictionary<ATORole, Texture2D> atlases = new Dictionary<ATORole, Texture2D>(); // 成品图集 / finished atlases
    }

    /// <summary>
    /// 装箱原子项：单张贴图及其全部岛（刚性整体装箱）。
    /// Packing atomic item: a single texture with all of its islands (packed as one rigid unit).
    /// </summary>
    public sealed class ATOItem
    {
        public Texture2D texture;                    // 贴图 / the texture
        public List<ATOIslandRef> refs = new List<ATOIslandRef>(); // 该贴图的全部岛引用 / all island refs of this texture
        public long area;                            // 光栅化总面积（4px 单元）/ rasterized area (4px cells)
        public ATOTextureInfo info;                  // 贴图信息 / texture info
    }
}
