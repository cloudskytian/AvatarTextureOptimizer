// Copyright (c) fosa. Licensed under the MIT License.
// Core data model shared across the analysis, quality, packing and output stages.
// 分析、质量、装箱与输出各阶段共用的核心数据模型。

using System;
using System.Collections.Generic;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Identifies one UV stream on one renderer: the mesh plus the UV channel index.
    /// Multi-channel UVs are split out and treated as independent UV streams, exactly as
    /// required by the specification.
    /// 标识某个渲染器上的一条 UV 流：网格 + UV 通道索引。
    /// 按需求规格，多通道 UV 会被拆分出来当作独立的 UV 流处理。
    /// </summary>
    public readonly struct UVStreamKey : IEquatable<UVStreamKey>
    {
        /// <summary>The renderer owning the mesh. / 拥有该网格的渲染器。</summary>
        public readonly Renderer Renderer;

        /// <summary>UV channel index, 0-7. / UV 通道索引，0-7。</summary>
        public readonly int Channel;

        /// <summary>Creates a key. / 创建键。</summary>
        public UVStreamKey(Renderer renderer, int channel)
        {
            Renderer = renderer;
            Channel = channel;
        }

        /// <inheritdoc />
        public bool Equals(UVStreamKey other) =>
            ReferenceEquals(Renderer, other.Renderer) && Channel == other.Channel;

        /// <inheritdoc />
        public override bool Equals(object obj) => obj is UVStreamKey o && Equals(o);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            var h = Renderer != null ? Renderer.GetInstanceID() : 0;
            return (h * 397) ^ Channel;
        }

        /// <inheritdoc />
        public override string ToString() =>
            $"{(Renderer != null ? Renderer.name : "<null>")}#uv{Channel}";
    }

    /// <summary>
    /// How a specific material slot references a specific texture. A single texture may be
    /// referenced many times with different alpha modes and cutoffs; the strictest requirement
    /// across all references wins.
    /// 某个材质槽引用某张贴图的方式。同一张贴图可能被多次引用且透明模式与 cutoff 不同，
    /// 最终取所有引用中要求最严苛者。
    /// </summary>
    public sealed class TextureUsage
    {
        /// <summary>The material doing the referencing. / 发起引用的材质。</summary>
        public Material Material;

        /// <summary>Shader property name, e.g. _MainTex. / 着色器属性名，例如 _MainTex。</summary>
        public string PropertyName;

        /// <summary>Semantic role inferred from the shader. / 从着色器推断出的语义角色。</summary>
        public TextureCategory Category;

        /// <summary>Alpha handling of the referencing material. / 引用材质的 alpha 处理方式。</summary>
        public AlphaMode AlphaMode;

        /// <summary>Alpha cutoff when <see cref="AlphaMode" /> is Cutout. / AlphaMode 为 Cutout 时的裁剪阈值。</summary>
        public float Cutoff = 0.5f;

        /// <summary>Whether the sampled data is sRGB encoded. / 采样数据是否为 sRGB 编码。</summary>
        public bool IsSRGB = true;

        /// <summary>Which colour channels are actually consumed by the shader. / 着色器实际消费的颜色通道。</summary>
        public ChannelMask UsedChannels = ChannelMask.All;
    }

    /// <summary>
    /// Bit flags describing which colour channels carry meaningful data.
    /// 描述哪些颜色通道承载有效数据的位标志。
    /// </summary>
    [Flags]
    public enum ChannelMask
    {
        /// <summary>No channels. / 无通道。</summary>
        None = 0,

        /// <summary>Red. / 红。</summary>
        R = 1,

        /// <summary>Green. / 绿。</summary>
        G = 2,

        /// <summary>Blue. / 蓝。</summary>
        B = 4,

        /// <summary>Alpha. / 透明度。</summary>
        A = 8,

        /// <summary>RGB without alpha. / 不含 alpha 的 RGB。</summary>
        RGB = R | G | B,

        /// <summary>All four channels. / 全部四个通道。</summary>
        All = R | G | B | A,
    }

    /// <summary>
    /// A texture together with everything the pipeline learned about it.
    /// 一张贴图及管线针对它收集到的全部信息。
    /// </summary>
    public sealed class TextureInfo
    {
        /// <summary>The source texture asset. / 源贴图资产。</summary>
        public Texture2D Texture;

        /// <summary>Every place this texture is referenced from. / 该贴图的所有引用点。</summary>
        public readonly List<TextureUsage> Usages = new List<TextureUsage>();

        /// <summary>Excluded from all optimization. / 排除在所有优化之外。</summary>
        public bool Whitelisted;

        /// <summary>Reason the texture was whitelisted, for reporting. / 被列入白名单的原因，用于报告。</summary>
        public string WhitelistReason;

        /// <summary>Effective category after merging all usages. / 合并所有引用后的最终分类。</summary>
        public TextureCategory Category = TextureCategory.OpaqueColor;

        /// <summary>Strictest alpha mode across all usages. / 所有引用中最严苛的 alpha 模式。</summary>
        public AlphaMode StrictestAlphaMode = AlphaMode.Opaque;

        /// <summary>All distinct cutoff values this texture is tested against. / 该贴图被测试过的所有不同 cutoff 值。</summary>
        public readonly HashSet<float> Cutoffs = new HashSet<float>();

        /// <summary>Union of channels used across all references. / 所有引用使用通道的并集。</summary>
        public ChannelMask UsedChannels = ChannelMask.None;

        /// <summary>True when the decoded pixels contain non-opaque alpha. / 解码后像素含非不透明 alpha 时为 true。</summary>
        public bool HasAlphaContent;

        /// <summary>Colour space of the imported asset. / 导入资产的色彩空间。</summary>
        public bool IsSRGB = true;

        /// <summary>Filter mode of the imported asset. / 导入资产的过滤模式。</summary>
        public FilterMode FilterMode = FilterMode.Bilinear;

        /// <summary>Width of the imported asset in pixels. / 导入资产的宽度（像素）。</summary>
        public int Width;

        /// <summary>Height of the imported asset in pixels. / 导入资产的高度（像素）。</summary>
        public int Height;

        /// <summary>Returns the strictest cutoff, i.e. the one demanding the most alpha fidelity. / 返回最严苛的 cutoff，即对 alpha 保真度要求最高者。</summary>
        public float StrictestCutoff
        {
            get
            {
                // A cutoff nearer 0.5 sits on the steepest part of typical alpha ramps and is the
                // most sensitive to resampling error, so we treat it as the strictest.
                // 越接近 0.5 的 cutoff 位于 alpha 斜坡最陡处，对重采样误差最敏感，视为最严苛。
                var best = 0.5f;
                var bestDist = float.MaxValue;
                foreach (var c in Cutoffs)
                {
                    var d = Mathf.Abs(c - 0.5f);
                    if (d < bestDist)
                    {
                        bestDist = d;
                        best = c;
                    }
                }

                return best;
            }
        }
    }

    /// <summary>
    /// A connected UV island: the triangles that share UV connectivity, plus its bounds.
    /// 一个连通的 UV 岛：共享 UV 连通性的三角形集合及其包围盒。
    /// </summary>
    public sealed class UVIsland
    {
        /// <summary>Index of this island inside its stream. / 该岛在所属流中的索引。</summary>
        public int Index;

        /// <summary>Triangle indices belonging to this island. / 属于该岛的三角形索引。</summary>
        public readonly List<int> Triangles = new List<int>();

        /// <summary>Vertex indices touched by this island. / 该岛涉及的顶点索引。</summary>
        public readonly List<int> Vertices = new List<int>();

        /// <summary>UV-space bounds before any transformation. / 变换前的 UV 空间包围盒。</summary>
        public Rect UVBounds;

        /// <summary>Integer translation applied to bring an out-of-range island back into [0,1]. / 将越界岛平移回 [0,1] 所施加的整数平移。</summary>
        public Vector2Int NormalizationOffset;

        /// <summary>Source pixel rect in the original texture. / 在原贴图中的源像素矩形。</summary>
        public RectInt SourceRect;

        /// <summary>Chosen scale on each axis after the quality search. / 质量搜索后各轴选定的缩放比例。</summary>
        public Vector2 Scale = Vector2.one;

        /// <summary>Final size in atlas pixels. / 在图集中的最终像素尺寸。</summary>
        public Vector2Int PackedSize;

        /// <summary>Final position in atlas pixels. / 在图集中的最终像素位置。</summary>
        public Vector2Int PackedPosition;

        /// <summary>True when the island was rotated 90 degrees during packing. / 装箱时旋转 90 度则为 true。</summary>
        public bool Rotated;

        /// <summary>Index of the atlas this island landed in, or -1. / 该岛所在图集的索引，未装箱为 -1。</summary>
        public int AtlasIndex = -1;

        /// <summary>True when every texel in the island is the same colour. / 岛内所有 texel 颜色相同时为 true。</summary>
        public bool IsSolidColor;

        /// <summary>The uniform colour when <see cref="IsSolidColor" /> is true. / IsSolidColor 为 true 时的统一颜色。</summary>
        public Color SolidColor;

        /// <summary>World-space surface area driving texel density clamps. / 用于像素密度钳制的世界空间表面积。</summary>
        public float WorldArea;

        /// <summary>Rasterized coverage mask at 4px granularity. / 4px 粒度的光栅化覆盖位掩码。</summary>
        public ulong[] CoverageMask;

        /// <summary>Width of <see cref="CoverageMask" /> in 4px cells. / CoverageMask 的宽度（4px 单元数）。</summary>
        public int MaskWidth;

        /// <summary>Height of <see cref="CoverageMask" /> in 4px cells. / CoverageMask 的高度（4px 单元数）。</summary>
        public int MaskHeight;

        /// <summary>Number of covered 4px cells, used for area-descending sort. / 被覆盖的 4px 单元数，用于面积降序排序。</summary>
        public int CoveredCells;
    }

    /// <summary>
    /// A set of textures that must share an identical UV layout because they are addressed by
    /// the same UV coordinates. This is what prevents a normal map and a colour map from being
    /// packed to different positions.
    /// 一组必须共享完全相同 UV 布局的贴图，因为它们由同一套 UV 坐标寻址。
    /// 这正是防止法线贴图与颜色贴图被打包到不同位置的机制。
    /// </summary>
    public sealed class UVGroup
    {
        /// <summary>Stable identifier. / 稳定标识符。</summary>
        public int Id;

        /// <summary>UV streams feeding this group. / 供给该组的 UV 流。</summary>
        public readonly List<UVStreamKey> Streams = new List<UVStreamKey>();

        /// <summary>Textures that must share the layout. / 必须共享布局的贴图。</summary>
        public readonly List<TextureInfo> Textures = new List<TextureInfo>();

        /// <summary>Islands, shared by every texture in the group. / 岛集合，组内所有贴图共享。</summary>
        public readonly List<UVIsland> Islands = new List<UVIsland>();

        /// <summary>Whitelisted groups skip atlasing entirely. / 白名单组完全跳过图集化。</summary>
        public bool Whitelisted;

        /// <summary>Reason for skipping, surfaced in the report. / 跳过原因，会出现在报告中。</summary>
        public string SkipReason;

        /// <summary>
        /// The texture-type signature, e.g. "color+normal+mask". Groups only merge into the same
        /// atlas queue when their signatures match, which is what stops a normal atlas from
        /// wasting 9/10 of its area.
        /// 贴图类型签名，例如 "color+normal+mask"。只有签名一致的组才会并入同一装箱队列，
        /// 这正是避免法线图集浪费 9/10 面积的机制。
        /// </summary>
        public string TypeSignature = string.Empty;

        /// <summary>Largest original texture size in the group, clamping the packed size. / 组内最大原始贴图尺寸，用于钳制打包尺寸。</summary>
        public int MaxOriginalSize;

        /// <summary>
        /// Triangle indices of the source mesh, cached so the packing stage can rasterize the
        /// true island shape instead of falling back to its bounding box.
        /// 源网格的三角形索引，缓存以便装箱阶段能够光栅化岛的真实形状，
        /// 而不是退化为其包围盒。
        /// </summary>
        public int[] SourceTriangles;

        /// <summary>UV coordinates of the source mesh for the group's channel. / 源网格在该组通道上的 UV 坐标。</summary>
        public Vector2[] SourceUVs;
    }

    /// <summary>
    /// A candidate atlas size considered by the packer.
    /// 装箱器考虑的候选图集尺寸。
    /// </summary>
    public readonly struct AtlasCandidate
    {
        /// <summary>Candidate width in pixels. / 候选宽度（像素）。</summary>
        public readonly int Width;

        /// <summary>Candidate height in pixels. / 候选高度（像素）。</summary>
        public readonly int Height;

        /// <summary>Creates a candidate. / 创建候选项。</summary>
        public AtlasCandidate(int width, int height)
        {
            Width = width;
            Height = height;
        }

        /// <summary>Total pixel area. / 总像素面积。</summary>
        public long Area => (long)Width * Height;

        /// <summary>Aspect ratio, long side over short side, 1.0 when square. / 长宽比（长边/短边），正方形为 1.0。</summary>
        public float AspectRatio =>
            Width >= Height ? (float)Width / Height : (float)Height / Width;

        /// <inheritdoc />
        public override string ToString() => $"{Width}x{Height}";
    }

    /// <summary>
    /// The result of packing one texture-type queue into one atlas.
    /// 将一个贴图类型队列装入一张图集的结果。
    /// </summary>
    public sealed class AtlasResult
    {
        /// <summary>Index of this atlas among all generated atlases. / 该图集在所有生成图集中的索引。</summary>
        public int Index;

        /// <summary>Final atlas width. / 图集最终宽度。</summary>
        public int Width;

        /// <summary>Final atlas height. / 图集最终高度。</summary>
        public int Height;

        /// <summary>UV groups placed into this atlas. / 放入该图集的 UV 组。</summary>
        public readonly List<UVGroup> Groups = new List<UVGroup>();

        /// <summary>Texture-type signature shared by all groups here. / 此处所有组共享的贴图类型签名。</summary>
        public string TypeSignature = string.Empty;

        /// <summary>Fraction of atlas area actually covered by islands, 0-1. / 图集中实际被岛覆盖的面积占比，0-1。</summary>
        public float Utilization;

        /// <summary>Padding applied between islands. / 岛间使用的间距。</summary>
        public int Padding;
    }
}
