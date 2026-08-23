using System.Collections.Generic;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>One UV island (a connected set of triangles in UV space). / 一个 UV 岛（UV 空间连通三角形集合）。</summary>
    internal class UvIsland
    {
        internal int id;
        /// <summary>Owning group backreference. / 所属UV组反向引用。</summary>
        internal UvGroup Group;
        /// <summary>Triangle indices (into the extractor's flat arrays), 3 per triangle. / 三角形索引（每3个一组）。</summary>
        internal readonly List<int> triangles = new List<int>();
        /// <summary>UV bounds after translation into [0,1]. / 平移归一后的 UV 包围盒。</summary>
        internal Rect uvBounds;
        /// <summary>Translation applied to reach [0,1] (integer tile shift). / 归一时应用的整数平移。</summary>
        internal Vector2 uvOffset;
        /// <summary>Max world area (blendshapes 0/100 + animation scale). / 最大世界面积（形态键0/100+动画缩放）。</summary>
        internal float worldArea;
        /// <summary>Islands with identical quantized shape merged into this layout island. / 形状完全一致而被合并的岛。</summary>
        internal readonly List<UvIsland> mergedIslands = new List<UvIsland>();
        /// <summary>Shape hash for overlap merging. / 重叠合并用形状哈希。</summary>
        internal ulong shapeHash;

        /// <summary>Total triangle count incl. merged duplicates. / 含合并副本的三角形总数。</summary>
        internal int TotalTriangleCount => triangles.Count / 3 + MergedTriangleCount();

        private int MergedTriangleCount()
        {
            var n = 0;
            foreach (var m in mergedIslands) n += m.triangles.Count / 3;
            return n;
        }
    }

    /// <summary>
    /// A UV group: one mesh + one UV channel, and every texture reachable through it (base
    /// materials, swapped materials, texture swaps). All its textures share one island layout in
    /// every atlas that hosts them. / UV组：一个网格的一个UV通道及其可达的全部贴图；
    /// 其所有贴图在所有图集中共享同一套岛布局。
    /// </summary>
    internal class UvGroup
    {
        internal Mesh mesh;
        internal int channel;
        internal readonly List<UvIsland> islands = new List<UvIsland>();
        /// <summary>texture → storage category (normal &gt; color &gt; mask, see processor). / 贴图 → 存储类别。</summary>
        internal readonly Dictionary<Texture2D, TexCategory> textures = new Dictionary<Texture2D, TexCategory>();
        /// <summary>texture → every role it is used as (all evaluated, strictest wins). / 贴图的全部用途（全部评估）。</summary>
        internal readonly Dictionary<Texture2D, HashSet<TexCategory>> usageCategories =
            new Dictionary<Texture2D, HashSet<TexCategory>>();
        /// <summary>All alpha combos to evaluate for textures of this group. / 本组贴图需评估的全部透明组合。</summary>
        internal readonly HashSet<(AlphaMode mode, float cutoff)> alphaCandidates =
            new HashSet<(AlphaMode, float)>();
        internal bool atlasEligible = true;
        internal string ineligibleReason;
        /// <summary>Max area factor over renderers using this mesh. / 使用该网格的渲染器的最大面积因子。</summary>
        internal float areaFactor = 1f;
        /// <summary>Primary renderer (for blendshape evaluation). / 代表渲染器（形态键评估用）。</summary>
        internal RendererInfo primaryRenderer;

        internal string Key => UvGroupKey(mesh, channel);
        internal static string UvGroupKey(Mesh m, int ch) => m.GetInstanceID() + ":" + ch;
    }
}
