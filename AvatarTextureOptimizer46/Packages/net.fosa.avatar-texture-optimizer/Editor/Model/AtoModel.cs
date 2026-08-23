// SPDX-License-Identifier: MIT
// EN: Data model shared across the whole pipeline.
// ZH: 贯穿整个管线的数据模型。

using System;
using System.Collections.Generic;
using Net.Fosa.AvatarTextureOptimizer.Api;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor.Model
{
    /// <summary>
    /// EN: Identifies one UV layout region: a specific sub mesh of a specific mesh, read through a
    ///     specific UV channel. This is the smallest unit whose UVs can be rewritten independently.
    /// ZH: 标识一个 UV 布局区域：某个网格的某个子网格，通过某个 UV 通道读取。
    ///     这是可以独立重写 UV 的最小单元。
    /// </summary>
    public readonly struct UvSlot : IEquatable<UvSlot>
    {
        /// <summary>EN: The mesh asset. ZH: 网格资产。</summary>
        public readonly Mesh Mesh;
        /// <summary>EN: Sub mesh index. ZH: 子网格索引。</summary>
        public readonly int SubMesh;
        /// <summary>EN: UV channel index (0..7). ZH: UV 通道索引（0..7）。</summary>
        public readonly int Channel;

        /// <summary>EN: Creates a slot. ZH: 创建一个槽。</summary>
        public UvSlot(Mesh mesh, int subMesh, int channel)
        {
            Mesh = mesh;
            SubMesh = subMesh;
            Channel = channel;
        }

        /// <inheritdoc/>
        public bool Equals(UvSlot other) => ReferenceEquals(Mesh, other.Mesh) && SubMesh == other.SubMesh && Channel == other.Channel;
        /// <inheritdoc/>
        public override bool Equals(object obj) => obj is UvSlot o && Equals(o);
        /// <inheritdoc/>
        public override int GetHashCode() => ((Mesh != null ? Mesh.GetInstanceID() : 0) * 397 ^ SubMesh) * 397 ^ Channel;
        /// <inheritdoc/>
        public override string ToString() => $"{(Mesh != null ? Mesh.name : "<null>")}#{SubMesh}.uv{Channel}";
    }

    /// <summary>
    /// EN: One concrete "material slot uses this texture" fact, including everything needed to pick the
    ///     strictest quality requirement later.
    /// ZH: 一条具体的“材质槽使用了该贴图”的事实，包含之后挑选最严格质量要求所需的一切信息。
    /// </summary>
    public sealed class TextureUsage
    {
        /// <summary>EN: The referencing material. ZH: 引用它的材质。</summary>
        public Material Material;
        /// <summary>EN: Shader property name. ZH: 着色器属性名。</summary>
        public string PropertyName;
        /// <summary>EN: The UV slot it is sampled through. ZH: 采样所经过的 UV 槽。</summary>
        public UvSlot Slot;
        /// <summary>EN: Renderer that owns the material slot. ZH: 拥有该材质槽的渲染器。</summary>
        public Renderer Renderer;
        /// <summary>EN: Material slot index on the renderer. ZH: 渲染器上的材质槽索引。</summary>
        public int MaterialSlotIndex;
        /// <summary>EN: Semantic kind as reported by the analyzer. ZH: 分析器报告的语义分类。</summary>
        public AtoTextureKind Kind;
        /// <summary>EN: Alpha handling of the referencing material. ZH: 引用材质的 alpha 处理方式。</summary>
        public AtoAlphaMode AlphaMode;
        /// <summary>EN: Alpha cutoff of the referencing material. ZH: 引用材质的裁剪阈值。</summary>
        public float Cutoff;
        /// <summary>EN: True when this usage came from an animation rather than the scene state. ZH: 该引用来自动画而非场景状态时为 true。</summary>
        public bool FromAnimation;
    }

    /// <summary>
    /// EN: Everything ATO knows about one texture asset.
    /// ZH: ATO 关于某一张贴图资产所知道的一切。
    /// </summary>
    public sealed class TextureEntry
    {
        /// <summary>EN: The texture asset. ZH: 贴图资产。</summary>
        public Texture2D Texture;
        /// <summary>EN: All places it is used. ZH: 它被使用的所有位置。</summary>
        public readonly List<TextureUsage> Usages = new List<TextureUsage>();
        /// <summary>EN: Why it is skipped, if it is. ZH: 若被跳过，跳过的原因。</summary>
        public AtoSkipReason SkipReason = AtoSkipReason.None;
        /// <summary>EN: Extra human readable detail for the skip. ZH: 跳过原因的附加可读说明。</summary>
        public string SkipDetail;
        /// <summary>EN: Effective semantic kind, after merging every usage and inspecting pixels. ZH: 合并所有引用并检查像素后的最终语义分类。</summary>
        public AtoTextureKind Kind = AtoTextureKind.ColorOpaque;
        /// <summary>EN: Whether the source asset is imported as sRGB. ZH: 源资产是否以 sRGB 导入。</summary>
        public bool SRgb = true;
        /// <summary>EN: Filter mode of the source asset. ZH: 源资产的过滤模式。</summary>
        public FilterMode FilterMode = FilterMode.Bilinear;
        /// <summary>EN: Anisotropic level of the source asset. ZH: 源资产的各向异性等级。</summary>
        public int AnisoLevel = 1;
        /// <summary>EN: Wrap mode of the source asset. ZH: 源资产的 wrap 模式。</summary>
        public TextureWrapMode WrapMode = TextureWrapMode.Repeat;
        /// <summary>EN: Whether the source asset has mipmaps. ZH: 源资产是否有 Mipmap。</summary>
        public bool HasMipmaps = true;
        /// <summary>EN: RGBA channel mask actually consumed across all usages. ZH: 所有引用中实际使用的 RGBA 通道掩码。</summary>
        public int UsedChannelMask;
        /// <summary>EN: True when at least one texel has alpha below 1. ZH: 至少有一个像素 alpha 小于 1 时为 true。</summary>
        public bool HasAlpha;
        /// <summary>EN: True when every texel is the same colour. ZH: 所有像素颜色一致时为 true。</summary>
        public bool IsSolidColor;
        /// <summary>EN: The solid colour when <see cref="IsSolidColor"/> is true. ZH: 当 <see cref="IsSolidColor"/> 为 true 时的纯色值。</summary>
        public Color SolidColor;
        /// <summary>EN: Group this texture belongs to. ZH: 该贴图所属的组。</summary>
        public UvGroup Group;
        /// <summary>EN: The optimized replacement, filled in by the atlas/scale passes. ZH: 优化后的替代品，由图集/缩放阶段填充。</summary>
        public Texture2D Result;

        /// <summary>EN: True when the texture takes part in optimization. ZH: 该贴图参与优化时为 true。</summary>
        public bool IsOptimizable => SkipReason == AtoSkipReason.None;

        /// <summary>EN: Source width in texels. ZH: 源宽度（像素）。</summary>
        public int Width => Texture != null ? Texture.width : 0;
        /// <summary>EN: Source height in texels. ZH: 源高度（像素）。</summary>
        public int Height => Texture != null ? Texture.height : 0;
    }

    /// <summary>
    /// EN: A set of textures that must share one island layout, because they are sampled through the
    ///     same UV slots. This is the "UV group" from the specification: an island lives at the same
    ///     place in every atlas produced for the group.
    /// ZH: 必须共享同一套岛布局的贴图集合，因为它们通过相同的 UV 槽采样。
    ///     这就是规格中的“UV 组”：一个岛在该组产出的每张图集中位置都相同。
    /// </summary>
    public sealed class UvGroup
    {
        /// <summary>EN: Stable index used in logs. ZH: 日志中使用的稳定索引。</summary>
        public int Index;
        /// <summary>EN: Member textures. ZH: 成员贴图。</summary>
        public readonly List<TextureEntry> Textures = new List<TextureEntry>();
        /// <summary>EN: UV slots feeding this group. ZH: 供给该组的 UV 槽。</summary>
        public readonly HashSet<UvSlot> Slots = new HashSet<UvSlot>();
        /// <summary>EN: The UV channel every member is sampled through. ZH: 所有成员采样所用的 UV 通道。</summary>
        public int Channel;
        /// <summary>EN: Reference resolution the island layout is expressed in. ZH: 岛布局所基于的参考分辨率。</summary>
        public Vector2Int ReferenceSize;
        /// <summary>EN: Islands, computed once in the group's reference texture space. ZH: 在该组参考贴图空间中一次性计算出的岛。</summary>
        public List<UvIsland> Islands = new List<UvIsland>();
        /// <summary>EN: Set when the whole group had to be skipped. ZH: 整组被跳过时设置。</summary>
        public AtoSkipReason SkipReason = AtoSkipReason.None;

        /// <summary>EN: True when the group is still eligible for atlasing. ZH: 该组仍可参与图集化时为 true。</summary>
        public bool IsOptimizable => SkipReason == AtoSkipReason.None;
    }

    /// <summary>
    /// EN: A connected region of texture space used by the mesh, together with its chosen scale and its
    ///     final placement inside an atlas.
    /// ZH: 网格所使用的一块连通的贴图空间区域，及其选定的缩放与在图集中的最终位置。
    /// </summary>
    public sealed class UvIsland
    {
        /// <summary>EN: Index inside the owning group. ZH: 在所属组内的索引。</summary>
        public int Index;
        /// <summary>EN: Bounding box in reference texel space (inclusive min, exclusive max). ZH: 参考像素空间中的包围盒（min 含，max 不含）。</summary>
        public RectInt Bounds;
        /// <summary>EN: Coverage mask at 4 texel granularity, row major, width = ceil(Bounds.w/4). ZH: 4 像素粒度的覆盖掩码，行主序，宽度为 ceil(Bounds.w/4)。</summary>
        public bool[] Mask;
        /// <summary>EN: Mask width in cells. ZH: 掩码宽度（单元数）。</summary>
        public int MaskWidth;
        /// <summary>EN: Mask height in cells. ZH: 掩码高度（单元数）。</summary>
        public int MaskHeight;
        /// <summary>EN: Number of covered cells; the packing sort key. ZH: 被覆盖的单元数，装箱排序的键。</summary>
        public int CoveredCells;
        /// <summary>EN: Chosen non uniform scale in [0,1]. ZH: 选定的非均匀缩放，取值 [0,1]。</summary>
        public Vector2 Scale = Vector2.one;
        /// <summary>EN: Size in texels after scaling. ZH: 缩放后的像素尺寸。</summary>
        public Vector2Int ScaledSize;
        /// <summary>EN: Index of the atlas the island landed in, or -1. ZH: 岛落入的图集索引，未落入时为 -1。</summary>
        public int AtlasIndex = -1;
        /// <summary>EN: Placement inside the atlas, in atlas texels. ZH: 在图集中的位置，单位为图集像素。</summary>
        public Vector2Int AtlasOrigin;
        /// <summary>EN: True when the island was rotated by 90 degrees while packing. ZH: 装箱时旋转 90 度则为 true。</summary>
        public bool Rotated;
        /// <summary>EN: True when the island is a single flat colour and can shortcut to the minimum size. ZH: 该岛为单一纯色、可直接短路到最小尺寸时为 true。</summary>
        public bool SolidColor;
        /// <summary>EN: World space surface area in square meters, used for the pixel density clamp. ZH: 世界空间表面积（平方米），用于像素密度钳制。</summary>
        public float WorldAreaM2;
    }

    /// <summary>
    /// EN: A finished atlas plus the bookkeeping used by the report.
    /// ZH: 一张完成的图集及其报告所需的记录信息。
    /// </summary>
    public sealed class AtlasResult
    {
        /// <summary>EN: Atlas index. ZH: 图集索引。</summary>
        public int Index;
        /// <summary>EN: Atlas dimensions. ZH: 图集尺寸。</summary>
        public Vector2Int Size;
        /// <summary>EN: Group this atlas belongs to. ZH: 该图集所属的组。</summary>
        public UvGroup Group;
        /// <summary>EN: Source textures baked into this atlas, in packing order. ZH: 按装箱顺序烘焙进该图集的源贴图。</summary>
        public readonly List<TextureEntry> Sources = new List<TextureEntry>();
        /// <summary>EN: Fraction of the atlas covered by island pixels, in [0,1]. ZH: 图集中被岛像素覆盖的比例，取值 [0,1]。</summary>
        public float Utilization;
        /// <summary>EN: The generated texture. ZH: 生成的贴图。</summary>
        public Texture2D Texture;
    }
}
