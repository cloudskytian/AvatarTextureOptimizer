using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace net.fosa.ato.editor
{
    /// <summary>
    /// EN: Normalised semantic slot a texture occupies inside a material. Two textures belong to the
    ///     same slot when they play the same role, regardless of which shader property carries them.
    ///     The slot set of a UV group is what forms a texture type group, so that a normal-map atlas is
    ///     never generated for UV groups that have no normal map (which would waste most of the atlas).
    /// ZH: 贴图在材质中所占据的、经过归一化的语义槽位。只要作用相同，两张贴图就属于同一槽位，
    ///     与承载它们的着色器属性名无关。UV 组的槽位集合决定了贴图类型组的划分，
    ///     从而避免为没有法线贴图的 UV 组生成法线图集（那会浪费掉图集的绝大部分）。
    /// </summary>
    public enum TextureSlot
    {
        /// <summary>EN: Base colour / albedo. ZH: 基础色 / 反照率。</summary>
        Color = 0,
        /// <summary>EN: Tangent space normal map. ZH: 切线空间法线贴图。</summary>
        Normal = 1,
        /// <summary>EN: Emission colour. ZH: 自发光颜色。</summary>
        Emission = 2,
        /// <summary>EN: Single/multi channel mask (alpha mask, smoothness, metallic, AO, matcap mask...).
        /// ZH: 单/多通道蒙版（alpha 蒙版、光滑度、金属度、AO、matcap 蒙版等）。</summary>
        Mask = 3,
        /// <summary>EN: Anything we recognised as UV-sampled but could not classify further.
        /// ZH: 识别为经 UV 采样、但无法进一步分类的贴图。</summary>
        Other = 4,
    }

    /// <summary>
    /// EN: Everything we need to know about one source Texture2D, resolved once and cached.
    /// ZH: 关于一张源 Texture2D 的全部所需信息，只解析一次并缓存。
    /// </summary>
    public sealed class AtoTexture
    {
        /// <summary>EN: The original asset. ZH: 原始资产。</summary>
        public Texture2D Source;

        /// <summary>EN: Width in pixels as imported (the "effective" size, not the file size).
        /// ZH: 导入后的像素宽度（"有效"尺寸，而非文件尺寸）。</summary>
        public int Width;

        /// <summary>EN: Height in pixels as imported. ZH: 导入后的像素高度。</summary>
        public int Height;

        /// <summary>EN: True when the texture is sampled as sRGB. ZH: 是否以 sRGB 方式采样。</summary>
        public bool SRGB;

        /// <summary>EN: Import filter mode. Part of the type-group key. ZH: 导入的过滤模式，属于类型组键的一部分。</summary>
        public FilterMode Filter;

        /// <summary>EN: Import wrap mode. ZH: 导入的循环模式。</summary>
        public TextureWrapMode Wrap;

        /// <summary>EN: Anisotropic level. ZH: 各向异性级别。</summary>
        public int AnisoLevel;

        /// <summary>EN: True when at least one pixel has alpha &lt; 1. ZH: 是否至少有一个像素的 alpha &lt; 1。</summary>
        public bool HasAlpha;

        /// <summary>EN: True when every pixel is the same colour. Enables the solid-colour short circuit.
        /// ZH: 是否所有像素颜色一致。用于纯色短路优化。</summary>
        public bool IsSolid;

        /// <summary>EN: The single colour, valid only when <see cref="IsSolid"/>. ZH: 纯色值，仅当 IsSolid 为真时有效。</summary>
        public Color SolidColor;

        /// <summary>EN: Which RGBA channels actually carry information. Used for data textures.
        /// ZH: 实际承载信息的 RGBA 通道，用于数据贴图。</summary>
        public bool4Mask UsedChannels;

        /// <summary>EN: Classification driving format choice and quality metric. ZH: 决定格式选择与质量度量的分类。</summary>
        public TextureClass Class;

        /// <summary>EN: Content hash over the decoded pixels plus the import signature, for deduplication.
        /// ZH: 解码后像素 + 导入签名的内容哈希，用于去重。</summary>
        public Hash128 ContentHash;

        /// <summary>EN: True when this texture must not be modified in any way. ZH: 是否完全禁止修改该贴图。</summary>
        public bool Whitelisted;

        /// <summary>EN: The texture this one was merged into by input deduplication, or null.
        /// ZH: 输入去重时该贴图被合并到的目标贴图，若无则为 null。</summary>
        public AtoTexture DedupTarget;

        /// <summary>EN: Follow the dedup chain to the surviving representative. ZH: 沿去重链找到最终留存的代表。</summary>
        public AtoTexture Representative
        {
            get
            {
                var t = this;
                while (t.DedupTarget != null) t = t.DedupTarget;
                return t;
            }
        }

        /// <summary>EN: Readable identity for logs. ZH: 便于日志阅读的标识。</summary>
        public override string ToString() =>
            Source != null ? $"{Source.name}({Width}x{Height},{Class}{(SRGB ? ",sRGB" : "")})" : "<null>";
    }

    /// <summary>
    /// EN: Serialisable four-channel boolean mask. Unity's bool4 lives in Unity.Mathematics which we
    ///     do not want to force into the runtime assembly, so we keep a tiny local struct.
    /// ZH: 可序列化的四通道布尔掩码。Unity 的 bool4 位于 Unity.Mathematics，
    ///     我们不想把它强加给运行时程序集，因此保留一个极小的本地结构体。
    /// </summary>
    [Serializable]
    public struct bool4Mask
    {
        /// <summary>EN: Red channel used. ZH: 使用红通道。</summary>
        public bool R;
        /// <summary>EN: Green channel used. ZH: 使用绿通道。</summary>
        public bool G;
        /// <summary>EN: Blue channel used. ZH: 使用蓝通道。</summary>
        public bool B;
        /// <summary>EN: Alpha channel used. ZH: 使用 alpha 通道。</summary>
        public bool A;

        /// <summary>EN: Number of used channels. ZH: 被使用的通道数量。</summary>
        public int Count => (R ? 1 : 0) + (G ? 1 : 0) + (B ? 1 : 0) + (A ? 1 : 0);

        /// <summary>EN: Union of two masks. ZH: 两个掩码的并集。</summary>
        public static bool4Mask operator |(bool4Mask a, bool4Mask b) =>
            new bool4Mask { R = a.R | b.R, G = a.G | b.G, B = a.B | b.B, A = a.A | b.A };

        /// <summary>EN: Readable form for logs. ZH: 便于日志阅读的形式。</summary>
        public override string ToString() => $"{(R ? "R" : "-")}{(G ? "G" : "-")}{(B ? "B" : "-")}{(A ? "A" : "-")}";
    }

    /// <summary>
    /// EN: One concrete reference of a texture by a material property, with everything the quality
    ///     algorithm needs. A texture referenced by several materials produces several usages, and the
    ///     strictest of them wins.
    /// ZH: 某个材质属性对一张贴图的一次具体引用，携带质量算法所需的全部信息。
    ///     一张贴图被多个材质引用时会产生多条引用记录，取其中最严苛者。
    /// </summary>
    public sealed class TextureUsage
    {
        /// <summary>EN: The material doing the referencing. ZH: 发起引用的材质。</summary>
        public Material Material;

        /// <summary>EN: Shader property name, e.g. _MainTex. ZH: 着色器属性名，例如 _MainTex。</summary>
        public string PropertyName;

        /// <summary>EN: The referenced texture. ZH: 被引用的贴图。</summary>
        public AtoTexture Texture;

        /// <summary>EN: Normalised slot. ZH: 归一化后的槽位。</summary>
        public TextureSlot Slot;

        /// <summary>EN: UV channel the shader samples this property with (0..7). ZH: 着色器采样该属性所用的 UV 通道（0..7）。</summary>
        public int UvChannel;

        /// <summary>EN: Alpha treatment of the referencing material. ZH: 引用材质对 alpha 的处理方式。</summary>
        public AlphaMode AlphaMode;

        /// <summary>EN: Alpha cutoff of the referencing material, when Cutout. ZH: Cutout 时引用材质的 alpha 阈值。</summary>
        public float Cutoff;
    }

    /// <summary>
    /// EN: Identifies a piece of geometry that shares one UV layout: a renderer, one submesh, one UV
    ///     channel. Multi-channel UVs are split out and treated as independent UVs exactly as specified.
    /// ZH: 标识共享同一 UV 布局的一块几何体：一个渲染器、一个子网格、一个 UV 通道。
    ///     多通道 UV 会被拆出来当作独立 UV 处理，与需求一致。
    /// </summary>
    public readonly struct MeshBinding : IEquatable<MeshBinding>
    {
        /// <summary>EN: Owning renderer. ZH: 所属渲染器。</summary>
        public readonly Renderer Renderer;
        /// <summary>EN: Submesh / material slot index. ZH: 子网格 / 材质槽索引。</summary>
        public readonly int SubMesh;
        /// <summary>EN: UV channel index. ZH: UV 通道索引。</summary>
        public readonly int UvChannel;

        /// <summary>EN: Construct a binding. ZH: 构造一个绑定。</summary>
        public MeshBinding(Renderer renderer, int subMesh, int uvChannel)
        {
            Renderer = renderer;
            SubMesh = subMesh;
            UvChannel = uvChannel;
        }

        /// <inheritdoc/>
        public bool Equals(MeshBinding other) =>
            ReferenceEquals(Renderer, other.Renderer) && SubMesh == other.SubMesh && UvChannel == other.UvChannel;

        /// <inheritdoc/>
        public override bool Equals(object obj) => obj is MeshBinding b && Equals(b);

        /// <inheritdoc/>
        public override int GetHashCode() =>
            ((Renderer != null ? Renderer.GetInstanceID() : 0) * 397 ^ SubMesh) * 397 ^ UvChannel;

        /// <inheritdoc/>
        public override string ToString() =>
            $"{(Renderer != null ? Renderer.name : "<null>")}#{SubMesh}.uv{UvChannel}";
    }

    /// <summary>
    /// EN: A set of geometry that shares one UV layout together with every texture bound to it,
    ///     including textures introduced only by animation. All textures inside a UV group MUST end up
    ///     at the same position inside their respective atlases; that invariant is what prevents a UV
    ///     from being referenced simultaneously by a with-normal and a without-normal material and
    ///     ending up with two incompatible layouts.
    /// ZH: 共享同一 UV 布局的一组几何体，以及绑定到它的全部贴图（包含仅由动画引入的贴图）。
    ///     同一 UV 组内的所有贴图必须落在各自图集中的相同位置；正是这个不变量避免了
    ///     一个 UV 同时被"有法线"与"无法线"的材质引用时产生两套互不兼容的布局。
    /// </summary>
    public sealed class UVGroup
    {
        /// <summary>EN: Stable index used in logs. ZH: 用于日志的稳定编号。</summary>
        public int Id;

        /// <summary>EN: Geometry sharing this UV layout. ZH: 共享该 UV 布局的几何体。</summary>
        public readonly List<MeshBinding> Bindings = new List<MeshBinding>();

        /// <summary>EN: Textures bound to this UV, keyed by slot. Several textures per slot occur when
        /// animation swaps materials or textures. ZH: 按槽位索引、绑定到该 UV 的贴图。
        /// 当动画切换材质或贴图时，同一槽位可能有多张贴图。</summary>
        public readonly Dictionary<TextureSlot, List<AtoTexture>> Textures =
            new Dictionary<TextureSlot, List<AtoTexture>>();

        /// <summary>EN: Every usage record touching this UV group. ZH: 涉及该 UV 组的所有引用记录。</summary>
        public readonly List<TextureUsage> Usages = new List<TextureUsage>();

        /// <summary>EN: The islands of this UV layout. ZH: 该 UV 布局的岛。</summary>
        public readonly List<UVIsland> Islands = new List<UVIsland>();

        /// <summary>EN: True when anything forces this group out of atlasing. ZH: 是否有任何原因导致该组不参与图集化。</summary>
        public bool SkipAtlas;

        /// <summary>EN: True when the group is fully whitelisted and must not be touched at all.
        /// ZH: 该组是否完全白名单化、不允许任何修改。</summary>
        public bool FullyWhitelisted;

        /// <summary>EN: Human readable reason for <see cref="SkipAtlas"/>, shown in the report.
        /// ZH: <see cref="SkipAtlas"/> 的可读原因，展示在报告中。</summary>
        public string SkipReason;

        /// <summary>EN: The size, in pixels, the group's layout is authored against. This is the largest
        /// original size across the group, per the bucket-effect rule. ZH: 该组布局所基于的像素尺寸。
        /// 按木桶效应规则，取组内最大的原始尺寸。</summary>
        public Vector2Int LayoutSize;

        /// <summary>EN: Slots present in this group; forms the type-group signature.
        /// ZH: 该组存在的槽位，构成类型组签名。</summary>
        public IEnumerable<TextureSlot> Slots => Textures.Where(kv => kv.Value.Count > 0).Select(kv => kv.Key);

        /// <summary>EN: Add a texture to a slot, ignoring duplicates. ZH: 向槽位添加贴图，自动忽略重复。</summary>
        public void AddTexture(TextureSlot slot, AtoTexture tex)
        {
            if (tex == null) return;
            if (!Textures.TryGetValue(slot, out var list))
                Textures[slot] = list = new List<AtoTexture>();
            if (!list.Contains(tex)) list.Add(tex);
        }

        /// <inheritdoc/>
        public override string ToString() =>
            $"UVGroup#{Id}[{Bindings.Count} bindings, {Textures.Sum(kv => kv.Value.Count)} textures]";
    }

    /// <summary>
    /// EN: One connected component of a UV layout, i.e. a UV island. Islands are the atomic unit of both
    ///     quality-driven rescaling and shape-aware packing.
    /// ZH: UV 布局中的一个连通分量，即一个 UV 岛。岛既是质量驱动缩放的原子单位，
    ///     也是形状感知装箱的原子单位。
    /// </summary>
    public sealed class UVIsland
    {
        /// <summary>EN: Index inside the owning UV group. ZH: 在所属 UV 组内的编号。</summary>
        public int Index;

        /// <summary>EN: Owning UV group. ZH: 所属 UV 组。</summary>
        public UVGroup Group;

        /// <summary>EN: Triangle indices (into the submesh's index buffer, in triples) that belong here.
        /// ZH: 属于该岛的三角形索引（以三个一组的形式索引子网格的索引缓冲）。</summary>
        public int[] Triangles;

        /// <summary>EN: UV-space bounding box, min corner. ZH: UV 空间包围盒最小角。</summary>
        public Vector2 UvMin;

        /// <summary>EN: UV-space bounding box, max corner. ZH: UV 空间包围盒最大角。</summary>
        public Vector2 UvMax;

        /// <summary>EN: Integer translation applied to bring an out-of-range island back into [0,1].
        /// ZH: 为把越界岛整体平移归一到 [0,1] 而施加的整数平移量。</summary>
        public Vector2Int Wrap;

        /// <summary>EN: Solved scale on U. 1 means "kept at original resolution". ZH: 求解出的 U 轴缩放，1 表示保持原分辨率。</summary>
        public float ScaleU = 1f;

        /// <summary>EN: Solved scale on V. ZH: 求解出的 V 轴缩放。</summary>
        public float ScaleV = 1f;

        /// <summary>EN: Surface area of this island in avatar world metres squared, worst case across
        /// blend shapes and animated scale. ZH: 该岛在 Avatar 世界空间中的面积（平方米），
        /// 取形态键与动画缩放的最坏情况。</summary>
        public float WorldAreaM2;

        /// <summary>EN: Area in UV space, used to derive texel density. ZH: UV 空间面积，用于推导像素密度。</summary>
        public float UvArea;

        /// <summary>EN: True when every texel the island covers is a single colour in every bound texture.
        /// ZH: 该岛覆盖的所有纹素在每张绑定贴图中是否都为同一颜色。</summary>
        public bool IsSolid;

        /// <summary>EN: Final packed rectangle inside the atlas, in pixels. ZH: 最终在图集内的像素矩形。</summary>
        public RectInt PackedRect;

        /// <summary>EN: True when the island was rotated 90 degrees during packing. ZH: 装箱时该岛是否旋转了 90 度。</summary>
        public bool PackedRotated;

        /// <summary>EN: Index of the atlas this island landed in, or -1. ZH: 该岛所落入的图集索引，未装箱为 -1。</summary>
        public int AtlasIndex = -1;

        /// <summary>EN: Pixel-space size at the solved scale, before padding. ZH: 按求解缩放后的像素尺寸，不含 padding。</summary>
        public Vector2Int ScaledSize;

        /// <summary>EN: 4 px granularity coverage bitmask used by the packer, row major.
        /// ZH: 装箱器使用的 4 像素粒度覆盖位掩码，行主序。</summary>
        public RasterMask Mask;

        /// <inheritdoc/>
        public override string ToString() =>
            $"Island#{Group?.Id}.{Index}[{ScaledSize.x}x{ScaledSize.y} s=({ScaleU:F3},{ScaleV:F3})]";
    }
}
