// SPDX-License-Identifier: MIT
// EN: Core data model shared by every stage of the pipeline.
// ZH: 管线各阶段共用的核心数据模型。

using System;
using System.Collections.Generic;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// EN: Identifies one UV stream of one sub mesh. Islands are built per key, and every texture sampled
    ///     through this key must share the exact same atlas layout.
    /// ZH: 标识某个子网格的某一路 UV。UV 岛按此键构建，所有经由该键采样的贴图必须共享完全相同的图集布局。
    /// </summary>
    public readonly struct ATOUVKey : IEquatable<ATOUVKey>
    {
        public readonly Mesh Mesh;
        public readonly int SubMesh;
        public readonly int UVChannel;

        public ATOUVKey(Mesh mesh, int subMesh, int uvChannel)
        {
            Mesh = mesh;
            SubMesh = subMesh;
            UVChannel = uvChannel;
        }

        public bool Equals(ATOUVKey other) =>
            ReferenceEquals(Mesh, other.Mesh) && SubMesh == other.SubMesh && UVChannel == other.UVChannel;

        public override bool Equals(object obj) => obj is ATOUVKey o && Equals(o);

        public override int GetHashCode()
        {
            var h = Mesh != null ? Mesh.GetInstanceID() : 0;
            return (h * 397) ^ (SubMesh * 31 + UVChannel);
        }

        public override string ToString() =>
            $"{(Mesh != null ? Mesh.name : "<null>")}#sm{SubMesh}/uv{UVChannel}";
    }

    /// <summary>
    /// EN: A single reference of a texture from one material property.
    /// ZH: 某个材质属性对一张贴图的一次引用。
    /// </summary>
    public sealed class ATOTextureUsage
    {
        public Material Material;
        public string PropertyName;
        public ATOTextureRole Role;
        public ATOAlphaMode AlphaMode;
        public float Cutoff = 0.5f;
        public int UVChannel;
        public bool[] UsedChannels = { true, true, true, true };

        public override string ToString() => $"{(Material ? Material.name : "<null>")}.{PropertyName}";
    }

    /// <summary>
    /// EN: Everything the pipeline knows about one *source* texture (after content deduplication).
    /// ZH: 管线对一张“源贴图”（内容去重之后）掌握的全部信息。
    /// </summary>
    public sealed class ATOTextureInfo
    {
        public Texture2D Source;
        public int Width;
        public int Height;
        public bool SRGB;
        public FilterMode Filter;
        public TextureWrapMode Wrap;
        public int AnisoLevel;

        /// <summary>EN: Strictest role across all usages. ZH: 所有引用中最严格的角色。</summary>
        public ATOTextureRole Role = ATOTextureRole.ColorOpaque;

        /// <summary>EN: Strictest alpha mode across all usages. ZH: 所有引用中最严格的 alpha 模式。</summary>
        public ATOAlphaMode AlphaMode = ATOAlphaMode.Opaque;

        /// <summary>EN: All cutoff thresholds this texture is evaluated against. ZH: 需要逐一评估的所有 cutoff 阈值。</summary>
        public readonly List<float> Cutoffs = new List<float>();

        /// <summary>EN: Channels that are actually read by any shader. ZH: 实际被着色器读取的通道。</summary>
        public readonly bool[] UsedChannels = { false, false, false, false };

        /// <summary>EN: True when the texture must not be modified at all. ZH: 完全不允许修改时为 true。</summary>
        public bool Whitelisted;

        /// <summary>EN: True when atlasing is impossible but rescaling is still allowed. ZH: 无法图集化但仍可缩放时为 true。</summary>
        public bool AtlasBlocked;

        /// <summary>EN: Reason string for the report. ZH: 报告里显示的原因。</summary>
        public string BlockReason;

        public readonly List<ATOTextureUsage> Usages = new List<ATOTextureUsage>();

        /// <summary>EN: UV keys sampling this texture. ZH: 采样该贴图的 UV 键。</summary>
        public readonly HashSet<ATOUVKey> UVKeys = new HashSet<ATOUVKey>();

        /// <summary>EN: Result of the pipeline: the texture that replaces the source. ZH: 管线结果：替换源贴图的新贴图。</summary>
        public Texture2D Result;

        /// <summary>EN: Class signature used for type grouping. ZH: 用于类型分组的类签名。</summary>
        public ATOTextureClass Class => new ATOTextureClass(Role, SRGB, Filter, Wrap);

        public long OriginalByteSize => (long)Width * Height * 4;

        public override string ToString() => Source != null ? Source.name : "<null texture>";
    }

    /// <summary>
    /// EN: Textures that may share one atlas: same role, colour space, filter and wrap mode.
    /// ZH: 可以共用一张图集的贴图类别：角色、色彩空间、过滤模式与 wrap 模式都相同。
    /// </summary>
    public readonly struct ATOTextureClass : IEquatable<ATOTextureClass>
    {
        public readonly ATOTextureRole Role;
        public readonly bool SRGB;
        public readonly FilterMode Filter;
        public readonly TextureWrapMode Wrap;

        public ATOTextureClass(ATOTextureRole role, bool srgb, FilterMode filter, TextureWrapMode wrap)
        {
            Role = role;
            SRGB = srgb;
            Filter = filter;
            Wrap = wrap;
        }

        public bool Equals(ATOTextureClass other) =>
            Role == other.Role && SRGB == other.SRGB && Filter == other.Filter && Wrap == other.Wrap;

        public override bool Equals(object obj) => obj is ATOTextureClass o && Equals(o);

        public override int GetHashCode() => ((int)Role * 31 + (SRGB ? 1 : 0)) * 31 * 31 + (int)Filter * 31 + (int)Wrap;

        public override string ToString() => $"{Role}/{(SRGB ? "sRGB" : "linear")}/{Filter}/{Wrap}";
    }

    /// <summary>
    /// EN: One UV island: a connected set of triangles in UV space.
    /// ZH: 一个 UV 岛：UV 空间中相互连通的三角形集合。
    /// </summary>
    public sealed class ATOIsland
    {
        public ATOUVKey Key;
        public int Index;

        /// <summary>EN: Triangle indices (into the sub mesh triangle list). ZH: 三角形索引（子网格三角形列表内）。</summary>
        public int[] Triangles;

        /// <summary>EN: Vertex indices used by the island. ZH: 岛使用到的顶点索引。</summary>
        public int[] Vertices;

        /// <summary>EN: UV bounding box in [0,1] space after wrap normalisation. ZH: wrap 归一化后位于 [0,1] 空间的 UV 包围盒。</summary>
        public Rect Bounds;

        /// <summary>EN: Integer offset removed while normalising the island into [0,1]. ZH: 归一化到 [0,1] 时减掉的整数偏移。</summary>
        public Vector2 WrapOffset;

        /// <summary>EN: World space surface area (max over blend shapes / animated scale). ZH: 世界空间面积（形态键/动画缩放的最大值）。</summary>
        public float WorldArea;

        /// <summary>EN: UV area in [0,1]^2 units. ZH: [0,1]^2 单位下的 UV 面积。</summary>
        public float UVArea;

        /// <summary>EN: Quality driven scale factor per axis, 1 = untouched. ZH: 质量驱动的双轴缩放系数，1 表示不变。</summary>
        public Vector2 Scale = Vector2.one;

        /// <summary>EN: Island size in source pixels before scaling. ZH: 缩放前岛在源贴图中的像素尺寸。</summary>
        public Vector2Int SourcePixelSize;

        /// <summary>EN: Island size in atlas pixels after scaling. ZH: 缩放后岛在图集中的像素尺寸。</summary>
        public Vector2Int TargetPixelSize;

        /// <summary>EN: Set when the island is a flat colour and can be shrunk immediately. ZH: 纯色岛标记，可直接缩到最小。</summary>
        public bool IsFlatColor;

        /// <summary>EN: Placement result. ZH: 装箱结果。</summary>
        public ATOPlacement Placement;

        /// <summary>EN: Islands merged into this one (fully overlapping duplicates). ZH: 被合并进来的完全重叠岛。</summary>
        public List<ATOIsland> Merged;

        public override string ToString() => $"{Key}#i{Index} {Bounds} area={UVArea:F5}";
    }

    /// <summary>
    /// EN: Where an island ended up inside an atlas.
    /// ZH: 岛在图集中的最终落点。
    /// </summary>
    public struct ATOPlacement
    {
        public int AtlasIndex;
        public int X;
        public int Y;
        public bool Rotated;
        public int Width;
        public int Height;
        public bool Valid;
    }

    /// <summary>
    /// EN: A connected component of the (UV key ↔ texture) graph. Every texture inside shares the layout.
    /// ZH: (UV 键 ↔ 贴图) 图的一个连通分量。其中所有贴图共享同一套布局。
    /// </summary>
    public sealed class ATOUVGroup
    {
        public int Id;
        public readonly List<ATOUVKey> Keys = new List<ATOUVKey>();
        public readonly List<ATOTextureInfo> Textures = new List<ATOTextureInfo>();
        public readonly List<ATOIsland> Islands = new List<ATOIsland>();

        /// <summary>EN: Distinct texture classes present in this group. ZH: 该组内出现的不同贴图类别。</summary>
        public readonly HashSet<ATOTextureClass> Classes = new HashSet<ATOTextureClass>();

        /// <summary>EN: Rasterised area in atlas pixels, used for the packing order. ZH: 光栅化后的像素面积，用于装箱排序。</summary>
        public long RasterArea;

        /// <summary>EN: True when the whole group must skip atlasing. ZH: 整个组跳过图集化时为 true。</summary>
        public bool AtlasBlocked;

        public override string ToString() => $"UVGroup#{Id} keys={Keys.Count} tex={Textures.Count} islands={Islands.Count}";
    }

    /// <summary>
    /// EN: One generated atlas.
    /// ZH: 一张生成出来的图集。
    /// </summary>
    public sealed class ATOAtlas
    {
        public int Index;
        public ATOTextureClass Class;
        public int Width;
        public int Height;

        /// <summary>EN: Size of the shared layout space (before class scaling). ZH: 共享布局空间的尺寸（类别缩放前）。</summary>
        public int LayoutWidth;

        /// <summary>EN: Size of the shared layout space (before class scaling). ZH: 共享布局空间的尺寸（类别缩放前）。</summary>
        public int LayoutHeight;

        /// <summary>EN: Scale relative to the layout resolution (&lt;= 1). ZH: 相对布局分辨率的缩放（&lt;= 1）。</summary>
        public float ClassScale = 1f;

        public bool HasAlpha;
        public readonly List<ATOTextureInfo> Sources = new List<ATOTextureInfo>();
        public readonly List<ATOIsland> Islands = new List<ATOIsland>();
        public Texture2D Result;
        public string Name;
        public double Utilisation;

        public override string ToString() => $"{Name} {Width}x{Height} {Class} util={Utilisation:P1}";
    }

    /// <summary>
    /// EN: Aggregated statistics for the final report.
    /// ZH: 供最终报告使用的统计信息。
    /// </summary>
    public sealed class ATOStatistics
    {
        public int TexturesConsidered;
        public int TexturesOptimised;
        public int TexturesWhitelisted;
        public int IslandsPacked;
        public int AtlasCount;
        public long OriginalBytes;
        public long ResultBytes;
        public int MaterialsDeduplicated;
        public int TexturesDeduplicated;
        public int MeshesRewritten;

        public double SavedPercent => OriginalBytes <= 0 ? 0 : 100.0 * (OriginalBytes - ResultBytes) / OriginalBytes;
    }
}
