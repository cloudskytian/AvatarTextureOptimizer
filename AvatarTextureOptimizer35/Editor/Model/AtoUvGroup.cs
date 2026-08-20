using System;
using System.Collections.Generic;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor
{
    /// <summary>
    /// One UV group: a (mesh, UV channel) whose UV coordinates are shared by all its texture slots
    /// (including textures swapped in by animations). / 一个 UV 组：（网格, UV通道）的一份 UV，被其全部贴图槽
    /// （含动画切换进来的贴图）共享。
    ///
    /// CORE INVARIANT (Coder consensus): every island has exactly ONE UV rect (after quality scaling).
    /// That rect is identical in EVERY atlas that contains the island. The mesh has one UV array per
    /// channel, so all atlases must agree. Per-type-group savings are realized by choosing a SMALLER
    /// ATLAS RESOLUTION for textures whose quality demand is lower than the main texture — the shared
    /// UV rect is preserved, only the atlas's pixel resolution differs. /
    /// 核心不变式（Coder 共识）：每个岛只有一个（质量缩放后的）UV 矩形；该矩形在所有包含此岛的图集中完全一致。
    /// 网格每通道只有一个 UV 数组，因此所有图集必须一致。“类型组节省”通过为质量需求低于主色的贴图选择
    /// 更小的图集分辨率实现 —— 共享 UV 矩形不变，只有图集的像素分辨率不同。
    /// </summary>
    public sealed class AtoUvGroup
    {
        /// <summary>The renderer. / 渲染器。</summary>
        public Renderer Renderer;

        /// <summary>The mesh being processed (original). / 被处理的（原始）网格。</summary>
        public Mesh Mesh;

        /// <summary>UV channel index. / UV 通道索引。</summary>
        public int Channel;

        /// <summary>All texture slots using this UV. / 使用该 UV 的全部贴图槽。</summary>
        public List<AtoTextureSlot> Slots = new List<AtoTextureSlot>();

        /// <summary>All islands of this channel. / 该通道的全部岛。</summary>
        public List<AtoIsland> Islands = new List<AtoIsland>();

        /// <summary>Type groups this UV group participates in. / 该 UV 组参与的类型组。</summary>
        public HashSet<AtoTypeGroup> TypeGroups = new HashSet<AtoTypeGroup>();

        /// <summary>Max animated object scale (per axis, from animation analysis). / 动画导致的最大物体缩放（逐轴）。</summary>
        public Vector3 MaxAnimatedScale = Vector3.one;

        /// <summary>Blend shape induced area factor (max of frame 0 / frame 100). / 形态键导致的面积系数（0/100 帧取最大）。</summary>
        public float BlendShapeAreaFactor = 1f;

        /// <summary>Whether this UV group is whitelisted (no atlasing, no UV rewrite). / 是否白名单（不图集化、不改 UV）。</summary>
        public bool Whitelisted;

        /// <summary>Whitelist reason. / 白名单原因。</summary>
        public string WhitelistReason;

        /// <summary>Whether atlasing was skipped for this group (too large etc.). / 是否放弃图集化（过大等）。</summary>
        public bool AtlasSkipped;

        public string DisplayName => $"{Renderer.name}#uv{Channel}";

        /// <summary>
        /// Get the island's original pixel size on a given texture (before scaling). /
        /// 获取岛在给定贴图上的原始像素尺寸（缩放前）。
        /// </summary>
        public Vector2Int GetSourcePixelSize(AtoIsland island, Texture2D texture)
        {
            var w = Mathf.Max(1, Mathf.RoundToInt((island.UvMax.x - island.UvMin.x) * texture.width));
            var h = Mathf.Max(1, Mathf.RoundToInt((island.UvMax.y - island.UvMin.y) * texture.height));
            return new Vector2Int(w, h);
        }
    }

    /// <summary>
    /// One UV island. / 一个 UV 岛。
    /// </summary>
    public sealed class AtoIsland
    {
        /// <summary>Owning UV group. / 所属 UV 组。</summary>
        public AtoUvGroup UvGroup;

        /// <summary>Index within the group. / 组内索引。</summary>
        public int Index;

        /// <summary>Original UV bounding box (after wrap normalization). / 原始 UV 包围盒（wrap 归一后）。</summary>
        public Vector2 UvMin;
        public Vector2 UvMax;

        /// <summary>Vertex indices of the island's triangles (3 per triangle). / 岛三角形对应的顶点索引（每三角形 3 个）。</summary>
        public List<int> Triangles = new List<int>();

        /// <summary>
        /// Integer UV translation that normalized this island into [0,1] (applied at mesh rewrite). /
        /// 把该岛归一进 [0,1] 的整数 UV 平移（网格重写时应用）。
        /// </summary>
        public Vector2Int NormalizationTranslation;

        /// <summary>
        /// Final UV rect (shared by ALL atlases containing this island). Set by the quality stage. /
        /// 最终 UV 矩形（被所有包含此岛的图集共享）。由质量阶段设置。
        /// </summary>
        public Vector2 FinalUvMin;
        public Vector2 FinalUvMax;

        /// <summary>Whether the island is a solid color (short-circuit scale). / 是否纯色岛（短路缩放）。</summary>
        public bool IsSolid;

        /// <summary>Pixel density (px/m) on the source textures (per axis, worst). / 源贴图上的像素密度（px/m，逐轴最差）。</summary>
        public Vector2 SourceDensity;

        /// <summary>World-space size of the island (per axis, incl. animated scale). / 岛的世界空间尺寸（逐轴，含动画缩放）。</summary>
        public Vector2 WorldSize = Vector2.one;

        /// <summary>Blend-shape area factor (max of frame 0 / frame 100). / 形态键面积系数（0/100 帧取最大）。</summary>
        public float BlendShapeFactor = 1f;

        /// <summary>
        /// Per-texture quality scale (0..1]: the scale at which THIS texture's quality thresholds are
        /// exactly met for this island. The wooden barrel (s = min over textures) decides the final
        /// UV rect; looser textures' headroom (s_i^t / s_i) allows their atlases to be smaller. /
        /// 逐贴图质量缩放（0..1]：该贴图在此岛的质量阈值恰好达标时的缩放。木桶效应（s = 各贴图取最小）决定
        /// 最终 UV 矩形；较宽松贴图的余量（s_i^t / s_i）允许其图集整体更小。
        /// </summary>
        public Dictionary<Texture2D, Vector2> PerTextureScale = new Dictionary<Texture2D, Vector2>();
    }

    /// <summary>
    /// Placement of one island: the UV rect (origin + size) is SHARED across all atlases containing
    /// the island; each atlas derives its pixel rect by (u×W, v×H). Rotation rotates both the content
    /// and the rect consistently; tangent-data atlases never rotate. /
    /// 一个岛的放置：UV 矩形（原点+尺寸）在所有包含此岛的图集间共享；各图集按 (u×W, v×H) 推导像素矩形。
    /// 旋转同时作用于内容与矩形；含切线数据的图集绝不旋转。
    /// </summary>
    public sealed class AtoPlacedIsland
    {
        public AtoIsland Island;

        /// <summary>Shared UV origin (u, v) in [0,1]². / 共享 UV 原点 (u,v) ∈ [0,1]²。</summary>
        public Vector2 UvOrigin;

        /// <summary>Rotation step applied (0..3 × 90°). / 旋转步进（0..3 × 90°）。</summary>
        public int Rotation;

        /// <summary>
        /// Compute the pixel rect of this island inside an atlas of size (w, h). /
        /// 计算该岛在尺寸 (w,h) 图集中的像素矩形。
        /// </summary>
        public RectInt GetPixelRect(int atlasWidth, int atlasHeight)
        {
            var uvSize = Island.FinalUvMax - Island.FinalUvMin;
            var x = Mathf.FloorToInt(UvOrigin.x * atlasWidth);
            var y = Mathf.FloorToInt(UvOrigin.y * atlasHeight);
            var pw = Mathf.Max(1, Mathf.RoundToInt(uvSize.x * atlasWidth));
            var ph = Mathf.Max(1, Mathf.RoundToInt(uvSize.y * atlasHeight));
            // 90°/270° rotations swap the axes. / 90°/270° 旋转交换宽高。
            if ((Rotation & 1) == 1) (pw, ph) = (ph, pw);
            return new RectInt(x, y, pw, ph);
        }

        /// <summary>Pixel size at an atlas size (rotation-aware). / 在给定图集尺寸下的像素大小（考虑旋转）。</summary>
        public Vector2Int GetPixelSize(int atlasWidth, int atlasHeight)
        {
            var uvSize = Island.FinalUvMax - Island.FinalUvMin;
            var pw = Mathf.Max(1, Mathf.RoundToInt(uvSize.x * atlasWidth));
            var ph = Mathf.Max(1, Mathf.RoundToInt(uvSize.y * atlasHeight));
            if ((Rotation & 1) == 1) (pw, ph) = (ph, pw);
            return new Vector2Int(pw, ph);
        }
    }
}
