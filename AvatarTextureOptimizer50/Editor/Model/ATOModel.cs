// -----------------------------------------------------------------------------
// ATOModel.cs — core data model shared by all pipeline stages.
// ATOModel.cs — 全部管线阶段共享的核心数据模型。
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace net.fosa.ato.editor
{
    /// <summary>Texture classification / 贴图分类。</summary>
    internal enum TexClass
    {
        AlbedoOpaque,   // sRGB color, no meaningful alpha / sRGB 颜色，无有效 alpha
        AlbedoAlpha,    // sRGB color with alpha / sRGB 颜色，含 alpha
        NormalMap,      // tangent-space normal / 切线空间法线
        GrayMask,       // linear data/mask / 线性数据或蒙版
    }

    /// <summary>Role of a texture inside a material relative to the slot's main texture.
    /// 贴图在材质中相对主色贴图的角色。</summary>
    internal enum TexRole
    {
        Main,       // the slot's primary albedo / 该材质槽的主色
        Normal,     // counterpart normal / 对应法线
        Gray,       // counterpart gray mask / 对应灰度蒙版
        ExtraColor, // other UV-sampled color maps (emission, shadow color, 2nd/3rd layers...)
                    // 其他按UV采样的颜色图（发光、影色、2/3层等）
    }

    /// <summary>Kind of a counterpart atlas layer / counterpart 图集层类型。</summary>
    internal enum LayerKind
    {
        Base,       // base color atlas / 主色图集
        Normal,     // normal counterpart / 法线层
        Gray,       // gray-mask counterpart / 灰度层
        ExtraColor, // extra color counterpart / 附加颜色层
        Variant,    // animated material-swap layer (same layout) / 动画换贴图层（同布局）
    }

    /// <summary>Import-settings snapshot used for dedup equality ("different import settings
    /// means different texture"). / 导入设置快照（“导入设置不同即视为不同贴图”）。</summary>
    internal sealed class ImportSnapshot : IEquatable<ImportSnapshot>
    {
        public int width, height;
        public bool sRGB;
        public bool mipmaps;
        public TextureWrapMode wrapMode;
        public FilterMode filterMode;
        public int aniso;
        public TextureImporterCompression compression;
        public string rawJson;   // full TextureImporterSettings JSON fallback / 完整设置 JSON 兜底

        public bool Equals(ImportSnapshot other)
        {
            if (ReferenceEquals(this, other)) return true;
            if (other is null) return false;
            return width == other.width && height == other.height && sRGB == other.sRGB
                   && mipmaps == other.mipmaps && wrapMode == other.wrapMode
                   && filterMode == other.filterMode && aniso == other.aniso
                   && compression == other.compression && rawJson == other.rawJson;
        }

        public override bool Equals(object obj) => Equals(obj as ImportSnapshot);
        public override int GetHashCode() => rawJson?.GetHashCode() ?? 0;
    }

    /// <summary>Linear-space RGBA pixel buffer (managed cache of one texture).
    /// 线性空间 RGBA 像素缓冲（单张贴图的托管缓存）。</summary>
    internal sealed class PixelBuffer
    {
        public Color32[] pixels; // linear-space / 线性空间
        public int width, height;
    }

    /// <summary>One unique texture (post-dedup) with all its usage info.
    /// 去重后的唯一贴图及其全部使用信息。</summary>
    internal sealed class TexInfo
    {
        public Texture2D source;          // original asset (or GPU-readable stand-in) / 原始资产
        public string assetPath = "";
        public string contentHash;        // content+import hash / 内容+导入设置哈希
        public ImportSnapshot importSnap;
        public PixelBuffer buffer;        // lazy linear pixels / 懒加载线性像素

        public int Width => importSnap?.width ?? source?.width ?? 0;
        public int Height => importSnap?.height ?? source?.height ?? 0;
        public bool IsSRGB => importSnap?.sRGB ?? true;

        public TexClass texClass = TexClass.AlbedoOpaque;
        public bool whitelisted;
        public readonly List<string> whitelistReasons = new List<string>();

        /// <summary>materials → property names referencing this texture / 引用该贴图的材质→属性名。</summary>
        public readonly Dictionary<Material, HashSet<string>> usedByMaterials =
            new Dictionary<Material, HashSet<string>>();

        /// <summary>All (uvGroup, role) usages / 全部 (UV组, 角色) 使用。</summary>
        public readonly List<(UvGroupInfo group, TexRole role)> usages =
            new List<(UvGroupInfo, TexRole)>();

        /// <summary>Alpha usages collected for strictest-mode evaluation (material → (mode, cutoffs)).
        /// Alpha 用途（材质→(模式, cutoff 集合)），供最严苛评估。</summary>
        public readonly Dictionary<Material, (AlphaMode mode, List<float> cutoffs)> alphaUsage =
            new Dictionary<Material, (AlphaMode, List<float>)>();

        public bool alphaContent;         // texture actually has varying alpha / 内容确实含变化 alpha

        // ---- results / 结果 ----
        /// <summary>Whole-texture optimized copy (for non-atlas paths). / 整图缩放结果（非图集路径）。</summary>
        public Texture2D wholeScaled;
        /// <summary>True when this texture got atlas-optimized somewhere. / 某处已图集化。</summary>
        public bool atlasified;

        public bool SkipOptimization => whitelisted;

        public void MarkWhitelist(string reason)
        {
            whitelisted = true;
            if (!whitelistReasons.Contains(reason)) whitelistReasons.Add(reason);
        }
    }

    internal enum AlphaMode
    {
        Opaque,
        Cutout,
        Blend,
    }

    /// <summary>One renderer under processing / 一个被处理的渲染器。</summary>
    internal sealed class RendererInfo
    {
        public Renderer renderer;
        public string path = "";
        public bool isSkinned;
        public Mesh mesh;

        public bool activeAtRest = true;                 // active state at collection / 采集时激活态
        public bool animatedActive;                      // animated enable/disable / 有动画启停
        public float scaleAreaFactor = 1f;               // max pairwise |s_a*s_b| of anim scale / 动画缩放最大两轴积
        public readonly Dictionary<string, float> blendshapeMax = new Dictionary<string, float>();

        /// <summary>slot → materials reachable (initial + animated swaps, deduped).
        /// 材质槽 → 可达材质（初始+动画切换，已去重）。</summary>
        public readonly List<HashSet<Material>> slotMaterials = new List<HashSet<Material>>();

        /// <summary>slot → the material present at rest (defines the atlas "base" layer;
        /// animated-in mains become variant layers). / 槽 → 静态材质（作为图集基础层；
        /// 动画换入的主色作为变体层）。</summary>
        public readonly List<Material> initialMaterial = new List<Material>();

        /// <summary>slot → animated material swap exists on this slot individually.
        /// 槽 → 该槽是否存在单独切换材质的动画（影响材质槽合并安全性）。</summary>
        public readonly HashSet<int> slotsWithSoloSwapAnimation = new HashSet<int>();

        public bool IsRelevant => activeAtRest || animatedActive;
    }

    /// <summary>A UV group = one renderer × one UV channel. All textures sampled through it
    /// must keep identical island geometry so every atlas layer stays aligned.
    /// UV组 = 渲染器 × UV 通道。经由它采样的所有贴图必须保持相同岛几何，使各图集层对齐。</summary>
    internal sealed class UvGroupInfo
    {
        public RendererInfo owner;
        public int channel;

        /// <summary>textures sampled via this UV (all roles, incl. animation variants).
        /// 经此 UV 采样的贴图（全部角色，含动画变体）。</summary>
        public readonly List<TexInfo> textures = new List<TexInfo>();

        public readonly List<IslandInfo> islands = new List<IslandInfo>();

        public bool eligibleForAtlas = true;
        public string ineligibilityReason = "";
        /// <summary>Set when this group's islands actually landed in atlases.
        /// 当本组的岛实际进入图集时置位。</summary>
        public bool atlasified;

        public readonly HashSet<Material> materials = new HashSet<Material>();

        /// <summary>UV wrap normalization applied (offset added to all UVs) / 应用的整体平移归一化。</summary>
        public Vector2 normalizationOffset = Vector2.zero;

        public void MarkIneligible(string reason)
        {
            eligibleForAtlas = false;
            if (string.IsNullOrEmpty(ineligibilityReason)) ineligibilityReason = reason;
        }
    }

    /// <summary>Binary island raster mask (4px cell granularity).
    /// 岛的二值光栅位掩码（4px 粒度）。</summary>
    internal sealed class IslandRaster
    {
        public int cellsW, cellsH;      // cell grid / 网格
        public ulong[] rows;            // row-major bitmask, row = cellsW bits / 行主序位掩码
        public const int Cell = 4;      // px per cell / 每格像素

        public IslandRaster Transposed()
        {
            var t = new IslandRaster { cellsW = cellsH, cellsH = cellsW, rows = new ulong[cellsH] };
            for (int y = 0; y < cellsH; y++)
            {
                ulong bits = rows[y];
                for (int x = 0; x < cellsW; x++)
                {
                    if ((bits & (1ul << x)) != 0)
                        t.rows[x] |= 1ul << y;
                }
            }

            return t;
        }

        public int PopCount()
        {
            int n = 0;
            foreach (var r in rows) n += math_popcount(r);
            return n;
        }

        private static int math_popcount(ulong v)
        {
            int n = 0;
            while (v != 0) { v &= v - 1; n++; }
            return n;
        }
    }

    /// <summary>One UV island / 一个 UV 岛。</summary>
    internal sealed class IslandInfo
    {
        public int id;
        public UvGroupInfo group;

        /// <summary>Triangles as vertex-index triples, grouped per submesh index.
        /// 三角形（顶点索引三元组），按子网格分组。</summary>
        public readonly List<(int subMesh, int i0, int i1, int i2)> triangles =
            new List<(int, int, int, int)>();

        public readonly List<int> vertexIndices = new List<int>(); // deduped / 去重顶点

        public Rect uvBounds;             // after normalization / 归一化后
        public Vector2 uvOffset;          // integer shift applied to raw UVs / 对原始UV施加的整数平移
        public float uvArea;
        public float worldArea;           // m², blendshape/scale-aware / 世界面积（含形态键/缩放）
        public bool wrapCrossing;         // crosses a wrap seam / 跨 wrap 缝

        /// <summary>Pixel size on the group's reference (largest) texture / 在组内参考（最大）贴图上的像素尺寸。</summary>
        public Vector2Int origSize;
        /// <summary>Decided scaled size (barrel-max across textures) / 决定的缩放尺寸（木桶最大）。</summary>
        public Vector2Int scaledSize;
        public bool pureColor;            // short-circuit flag / 纯色短路
        public bool losslessCopy;         // quality==1 → raw copy / 无损拷贝

        /// <summary>Textures this island samples (per its slot/materials) — union over materials.
        /// 本岛采样的贴图（按槽/材质并集）。</summary>
        public readonly List<(TexInfo tex, TexRole role)> sampledTextures =
            new List<(TexInfo, TexRole)>();

        /// <summary>Animated variant mains layered over this island (same-layout atlas).
        /// 本岛上的动画变体主贴图（同布局图集层）。</summary>
        public List<TexInfo> variants;

        /// <summary>Unit base texture (rest material main) set by the planner.
        /// 单元基础贴图（静态材质主色），由规划器设置。</summary>
        public TexInfo unitBase;

        public IslandRaster raster;       // at scaled size / 缩放后的光栅

        // ---- atlas placement / 图集放置 ----
        public int atlasId = -1;
        public RectInt cellRect;          // in cells / 单元格
        public bool rotated;

        /// <summary>Overlap-merged duplicates (they share the placement of this island).
        /// 重叠合并的重复岛（共用本岛的放置）。</summary>
        public readonly List<IslandInfo> mergedDuplicates = new List<IslandInfo>();

        public float DensityPxPerMeter =>
            worldArea > 1e-9f ? Mathf.Sqrt(Mathf.Max(1f, origSize.x * origSize.y) / worldArea) : float.MaxValue;
    }

    /// <summary>Signature of a texture type group / 类型组签名。</summary>
    internal readonly struct TypeGroupKey : IEquatable<TypeGroupKey>
    {
        public readonly bool hasNormal;
        public readonly bool hasGray;
        public readonly bool hasExtraColor;
        public readonly bool sRGB;
        public readonly FilterMode filter;

        public TypeGroupKey(bool hasNormal, bool hasGray, bool hasExtraColor, bool sRGB, FilterMode filter)
        {
            this.hasNormal = hasNormal;
            this.hasGray = hasGray;
            this.hasExtraColor = hasExtraColor;
            this.sRGB = sRGB;
            this.filter = filter;
        }

        public bool Equals(TypeGroupKey other) =>
            hasNormal == other.hasNormal && hasGray == other.hasGray &&
            hasExtraColor == other.hasExtraColor && sRGB == other.sRGB && filter == other.filter;

        public override bool Equals(object obj) => obj is TypeGroupKey k && Equals(k);
        public override int GetHashCode() => (hasNormal ? 1 : 0) | (hasGray ? 2 : 0) |
                                            (hasExtraColor ? 4 : 0) | (sRGB ? 8 : 0) |
                                            ((int)filter << 4);

        public override string ToString() =>
            $"N{(hasNormal ? 1 : 0)}G{(hasGray ? 1 : 0)}E{(hasExtraColor ? 1 : 0)}" +
            $"{(sRGB ? "srgb" : "lin")}/{filter}";
    }

    /// <summary>One atlas layer image (base or counterpart) / 一个图集层（主色或 counterpart）。</summary>
    internal sealed class AtlasLayer
    {
        public LayerKind kind;
        public TexInfo sourceTex;     // for variant/counterpart layers / 变体或 counterpart 的来源
        public Texture2D texture;     // final built texture / 最终生成的贴图
        public int width, height;
        public float usedRatio;       // occupied pixels / atlas pixels / 利用率
        public float scaleVsBase = 1f;// counterpart downscale ratio / counterpart 相对主图集缩放比
    }

    /// <summary>A packed atlas: layout + all layers sharing it.
    /// 一个打包完成的图集：布局 + 共享该布局的所有层。</summary>
    internal sealed class AtlasResult
    {
        public int id;
        public TypeGroupKey typeKey;
        public int width, height;
        public int padding;
        public readonly List<IslandInfo> islands = new List<IslandInfo>();
        public AtlasLayer baseLayer;
        public readonly List<AtlasLayer> layers = new List<AtlasLayer>(); // counterparts & variants
    }

    /// <summary>Atomic pack unit: one base texture with ALL its UV groups' islands.
    /// 装箱原子：一个基础贴图及其全部 UV 组的岛。</summary>
    internal sealed class PackUnit
    {
        public TexInfo baseTex;
        public TypeGroupKey typeKey;
        public readonly List<IslandInfo> islands = new List<IslandInfo>();
        public long rasterArea;                  // in cells / 光栅面积（格）
        /// <summary>Counterpart textures per kind (union across islands) / 各类型 counterpart 贴图。</summary>
        public readonly Dictionary<LayerKind, List<TexInfo>> counterparts =
            new Dictionary<LayerKind, List<TexInfo>>();
        /// <summary>Animated variant textures (same-layout layers) / 动画变体贴图（同布局层）。</summary>
        public readonly List<TexInfo> variants = new List<TexInfo>();
    }
}
