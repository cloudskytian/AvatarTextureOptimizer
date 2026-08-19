// AvatarTextureOptimizer
// File: Editor/Model/UVIsland.cs
//
// A connected component of triangles in UV space (with overlapping triangles
// merged). Islands are the atomic unit of quality scaling and atlas packing.
//
// UV 空间中三角形的连通分量（重叠三角形已合并）。岛是质量缩放与图集
// 装箱的原子单位。

using System.Collections.Generic;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor.model
{
    /// <summary>
    /// One UV island: a set of triangles sharing connectivity in UV space.
    /// 一个 UV 岛：UV 空间中共享连通性的一组三角形。
    /// </summary>
    public sealed class UVIsland
    {
        /// <summary>Mesh vertex indices belonging to this island. / 属于该岛的网格顶点索引。</summary>
        public readonly List<int> Vertices = new List<int>();

        /// <summary>Submesh index this island belongs to (triangle indices point into that submesh's index buffer). / 该岛所属的子网格索引（三角形索引指向该子网格的索引缓冲）。</summary>
        public int SubmeshIndex;

        /// <summary>Triangle indices (into the submesh's index buffer) belonging to this island. / 属于该岛的三角形索引（子网格索引缓冲中的索引）。</summary>
        public readonly List<int> Triangles = new List<int>();

        /// <summary>The UV coordinates of the island vertices (original space, before normalization). / 岛顶点的 UV 坐标（原始空间，归一化前）。</summary>
        public readonly List<Vector2> UVs = new List<Vector2>();

        /// <summary>Bounding box in original UV space. / 原始 UV 空间中的包围盒。</summary>
        public Rect BoundsUV;

        /// <summary>Bounding box in the texture's pixel space (rounded outwards). / 贴图像素空间中的包围盒（向外取整）。</summary>
        public RectInt PixelBounds;

        /// <summary>Island centroid in UV space (used for stable ordering). / UV 空间中的岛质心（用于稳定排序）。</summary>
        public Vector2 Centroid;

        /// <summary>Rasterized pixel area (after quality scaling, before packing). / 光栅化像素面积（质量缩放后、装箱前）。</summary>
        public long RasterAreaPixels;

        /// <summary>Short side of the pixel bounding box of the ORIGINAL island. / 原岛像素包围盒的短边。</summary>
        public int OriginalShortSide;

        /// <summary>True when the island is solid-colored (shortcut-able). / 是否为纯色岛（可短路）。</summary>
        public bool IsSolidColor;

        /// <summary>Pixel density of the original island in px/m, if the world scale is known. / 原岛的像素密度 px/m（若已知世界缩放）。</summary>
        public float PixelDensityPPM = -1f;

        /// <summary>
        /// Whether the island's UVs are inside [0,1] or can be normalized by a
        /// whole-box translation without crossing a wrap seam.
        /// 岛的 UV 是否在 [0,1] 内，或能否通过整体平移归一化而不跨 wrap 缝。
        /// </summary>
        public bool Normalizable = true;

        /// <summary>Computed bounding-box area in pixels (AABB, not rasterized). / 像素包围盒面积（AABB，非光栅化）。</summary>
        public long BoundsAreaPixels => (long)PixelBounds.width * PixelBounds.height;

        /// <summary>
        /// Whole-box translation that normalizes the island's UVs into [0,1]
        /// (computed when the island is out of bounds but within one wrap cell).
        /// The applier must subtract this offset from the island's vertex UVs.
        /// 将岛的 UV 整体平移到 [0,1] 的偏移量（越界但处于单个 wrap 单元内时
        /// 计算）。应用器必须从岛的顶点 UV 中减去该偏移。
        /// </summary>
        public Vector2 NormalizeOffset;

        /// <summary>
        /// Final pixel rect of the island inside the atlas (after quality
        /// scaling + normalization). / 质量缩放与归一化后岛在图集内的最终像素矩形。
        /// </summary>
        public RectInt ScaledRect;

        /// <summary>Rasterized shape mask (set by the packer prepass). / 光栅化形状掩码（装箱预处理阶段设置）。</summary>
        public atlas.RasterMask Raster;

        /// <summary>True when the island is placed rotated 90° in the atlas. / 岛在图集中是否被旋转 90 度放置。</summary>
        public bool RotatedInAtlas;

        public override string ToString() => $"UVIsland({BoundsUV}, px{BoundsAreaPixels})";
    }

    /// <summary>
    /// A UV group: one UV space (renderer slot + channel) plus ALL textures
    /// that share it — whether from a texture type group (normal/mask) or from
    /// animation switches. All textures of a UV group must occupy identical
    /// positions in their respective atlases so a UV shared between a normal
    /// material and a non-normal material keeps sampling the same place.
    ///
    /// 一个 UV 组：一个 UV 空间（渲染器槽位 + 通道）加上共享它的所有贴图——
    /// 无论来自贴图类型组（法线/蒙版）还是动画切换。UV 组的所有贴图在各自
    /// 图集中的位置必须一致，以确保 UV 同时被有法线与无法线贴图的材质引用时
    /// 采样到同一位置。
    /// </summary>
    public sealed class UVGroup
    {
        /// <summary>The UV space this group represents. / 该组代表的 UV 空间。</summary>
        public UVSpaceKey Space;

        /// <summary>Mesh backing this UV space (for island rasterization). / 支撑该 UV 空间的网格（用于岛光栅化）。</summary>
        public Mesh Mesh;

        /// <summary>The UV channel data of the group's channel. / 该组通道的 UV 数据。</summary>
        public List<Vector2> UVChannelData;

        /// <summary>Index buffer of the group's submesh. / 该组子网格的索引缓冲。</summary>
        public int[] SubmeshIndices;

        /// <summary>All islands of this UV space. / 该 UV 空间的所有岛。</summary>
        public List<UVIsland> Islands = new List<UVIsland>();

        /// <summary>All textures mapped onto this UV space. / 映射到该 UV 空间的所有贴图。</summary>
        public List<TextureUsage> Textures = new List<TextureUsage>();

        /// <summary>Where this group lands in the atlas (in pixels), shared by all textures of the group. / 该组在图集中的位置（像素），组内所有贴图共用。</summary>
        public RectInt AtlasRect;

        /// <summary>The atlas index this group was packed into (-1 = not atlasized). / 该组被装箱到的图集索引（-1=未图集化）。</summary>
        public int AtlasIndex = -1;

        /// <summary>The final (quality-scaled) per-island pixel rects in the atlas. / 最终（质量缩放后）各岛在图集中的像素矩形。</summary>
        public readonly Dictionary<UVIsland, RectInt> IslandRects = new Dictionary<UVIsland, RectInt>();

        /// <summary>Maximum target scale among member textures (bucket effect: take the largest). / 成员贴图中最大的目标缩放（木桶效应：取最大）。</summary>
        public float MaxTargetScale = 1f;

        /// <summary>The maximum original texture size among member textures (used to clamp growth). / 成员贴图中最大的原贴图尺寸（用于钳制增长）。</summary>
        public int MaxOriginalTextureSize = 0;

        /// <summary>True when this group is excluded from atlasization but still gets whole-texture scaling. / 是否跳过图集化但保留整图缩放。</summary>
        public bool SkippedAtlas = false;

        /// <summary>True when this group is fully whitelisted (no optimization at all). / 是否完全白名单（不做任何优化）。</summary>
        public bool Whitelisted = false;

        /// <summary>Description for logs. / 供日志使用的描述。</summary>
        public override string ToString() => $"UVGroup({Space}, islands={Islands.Count}, textures={Textures.Count})";
    }
}
