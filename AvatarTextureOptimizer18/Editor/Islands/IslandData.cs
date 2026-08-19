using System.Collections.Generic;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Editor.Islands
{
    // 单个 UV 岛实体：某网格某 UV 通道上的一个连通 UV 区域。
    // One UV island entity: a connected UV region on one UV channel of a mesh.
    public sealed class IslandEntity
    {
        // 全局唯一 ID。Globally unique id.
        public int id;
        public Mesh mesh;
        public int uvChannel;
        public int submesh;

        // 三角形（全局顶点索引三元组）。Triangles (global vertex-index triples).
        public readonly List<int> triangles = new List<int>();
        // 使用的顶点（去重）。Used vertices (unique).
        public readonly List<int> vertices = new List<int>();

        // 归一化后的 UV 包围盒。UV bounding box after normalization.
        public Vector2 uvMin, uvMax;
        // 越界归一化所用的整数平移。Integer translation used for out-of-bounds normalization.
        public Vector2 translation;

        // 世界面积（平方米，已含动画缩放与形态键因子）。World area in m² (incl. animated scale and blend-shape factors).
        public float worldArea;

        // 该岛上的贴图使用（一个岛可能对应多张贴图/多份材质）。Texture uses on this island.
        public readonly List<IslandUse> uses = new List<IslandUse>();

        // 越界且无法归一 → 白名单跳过（跨 wrap 缝 / Clamp / Mirror）。Out-of-bounds and unnormalizable → whitelisted.
        public bool whitelistedFull;
        public string whitelistReason;

        // ---- 缩放结果（由质量阶段写入）。Scaling results (written by the quality stage). ----
        public float scaleX = 1f, scaleY = 1f;
        public bool skipQuality;      // 目标质量=1 或纯色短路。Quality=1 or pure-color shortcut.
        public bool pureColor;
        public int pureColorSizePx;   // 纯色岛短路尺寸。Pure-color shortcut size.

        // ---- 装箱结果（由装箱阶段写入）。Packing results (written by the packing stage). ----
        public int typeGroupId = -1;
        public int rotation;          // 0/1/2/3（90°步进）。0/1/2/3 (90° steps).
        public Vector2Int rectPosPx;  // 图集内像素位置。Pixel position in the atlas.
        public Vector2Int rectSizePx; // 放置矩形像素尺寸。Placed rect pixel size.
        public int atlasId = -1;      // 所在图集。Owning atlas.
        public string atlasKind;      // 图集类别（Color/Normal/Gray/AlphaColor）。Atlas kind.
        // 密度缩放上限（防浪费；各向异性搜索上界）。Density scale cap (anti-waste; anisotropic search upper bound).
        public float densityCap = 1f;

        // 放弃图集化（fallback）。Gave up atlasing (fallback).
        public bool noAtlasFallback;
        public string fallbackReason;
        // 放置时的 padding（像素）。Padding in pixels at placement time.
        public int paddingPx;

        // 各轴像素跨度（按引用贴图取最大分辨率下的值）。Per-axis pixel span (in the largest referencing texture).
        public int pixelWidth, pixelHeight;

        public override string ToString()
        {
            return string.Format("Island#{0} {1}.ch{2}.sub{3} tris={4}", id, mesh != null ? mesh.name : "?", uvChannel, submesh, triangles.Count / 3);
        }
    }

    // 岛上的一个贴图使用（贴图 → 该岛区域的映射）。One texture use on an island.
    public sealed class IslandUse
    {
        public Analysis.TextureEntry texture;
        public Analysis.ATOTextureKind kind;
        public bool sRGB;
        public FilterMode filterMode;
        public Analysis.ATOAlphaMode alphaMode;
        public float cutoff;
        // 该使用的质量缩放结果（缩放后由岛取木桶最小值）。Quality scale of this use (island takes the bucket minimum).
        public float useScale = 1f;
        // 是否来自动画切换。Whether it comes from an animated swap.
        public bool animatedSwap;
        // 白名单级别（该使用）。Whitelist level of this use.
        public Analysis.ATOWhitelistLevel whitelistLevel = Analysis.ATOWhitelistLevel.None;
        public string whitelistReason;
        // 该使用的替换贴图（图集；由图集构建阶段写入）。Replacement texture of this use (atlas; written by the atlas stage).
        public Texture2D replacementTexture;
        public Packing.AtlasPlan replacementAtlas;
    }

    // 图集类别：决定装箱队列与图集资产。Atlas kind: drives packing queues and atlas assets.
    public enum AtlasKind
    {
        OpaqueColor = 0,
        AlphaColor = 1,
        Normal = 2,
        Grayscale = 3
    }

    // 类型组：岛的贴图种类多重集 + sRGB + filterMode 完全一致的岛集合。
    // Type group: islands whose texture-kind multiset + sRGB + filterMode are identical.
    public sealed class TypeGroup
    {
        public int id;
        // 排序后的种类签名（如 "AC,NC"）。Sorted kind signature.
        public readonly List<AtlasKind> kinds = new List<AtlasKind>();
        public readonly List<IslandEntity> islands = new List<IslandEntity>();
        // 每个类别一个图集序列（装箱结果）。Atlas sequences per kind (packing results).
        public readonly Dictionary<AtlasKind, List<Packing.AtlasPlan>> atlases = new Dictionary<AtlasKind, List<Packing.AtlasPlan>>();
    }
}
