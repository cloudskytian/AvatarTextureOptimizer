using System;
using System.Collections.Generic;
using UnityEngine;

// Core data model shared by analysis / optimization / baking.
// 分析/优化/烘焙共享的核心数据模型。

namespace Net.Fosa.AvatarTextureOptimizer
{
    /// <summary>
    /// A UV island: a connected group of triangles sampling one region of a texture on one UV channel.
    /// UV 岛：同一 UV 通道上采样贴图同一区域的三角形连通组。
    /// </summary>
    public sealed class UVIsland
    {
        /// <summary>Indices of the mesh triangles belonging to this island. 属于该岛的三角形索引。</summary>
        public List<int> TriangleIndices = new List<int>();
        /// <summary>UV min corner (original UV space, after normalization to [0,1] when possible). 原 UV 包围盒最小角。</summary>
        public Vector2 BoundsMin;
        /// <summary>UV max corner. 原 UV 包围盒最大角。</summary>
        public Vector2 BoundsMax;
        /// <summary>True if the original UV was translated so it fits [0,1] (no wrap crossing). 是否做过整体平移归一。</summary>
        public bool WasTranslated;
        /// <summary>True if this island crosses a wrap seam and cannot be normalized. 是否跨 wrap 缝（无法归一）。</summary>
        public bool CrossesWrap;
        /// <summary>Original pixel-space bounding size of the island at the source texture resolution. 原贴图像素包围盒尺寸。</summary>
        public Vector2Int OrigPixelSize;
        /// <summary>Source texture (after dedup) that defined this island's sampling. 定义该岛采样的源贴图（去重后）。</summary>
        public Texture2D SourceTexture;
        /// <summary>UV channel on the mesh. 网格上的 UV 通道。</summary>
        public int Channel;
        /// <summary>Mesh UV array (absolute, this channel) for rasterization. 网格 UV 数组（绝对坐标，该通道），用于光栅化。</summary>
        public float[] UVs;
        /// <summary>Triangle indices of this island into the mesh triangle array. 指向网格三角形数组的岛三角形索引。</summary>
        public int[] TriangleArrayIndices;

        /// <summary>World-space size (meters) of the island on the mesh, worst case across morph keys & scale anims. 岛在网格上的世界尺寸（米），取形态键与缩放动画最差情况。</summary>
        public Vector2 WorldSizeMeters;

        /// <summary>Scaled pixel size after quality scaling (atlas space), per atlas bucket later filled by scaler. 质量缩放后的像素尺寸（图集空间）。</summary>
        public Vector2Int ScaledPixelSize;
        /// <summary>True when this island was found to be pure color (short-circuited). 是否纯色（短路）。</summary>
        public bool IsPureColor;

        /// <summary>Normalized rect (position+size) decided by the shared UVGroup layout. 归一化矩形（由 UV 组共享布局决定）。</summary>
        public Rect NormalizedRect;
        /// <summary>90-degree rotation (0/1/2/3). 90°旋转（0/1/2/3）。</summary>
        public int Rotation;

        public Vector2 SizeUV => BoundsMax - BoundsMin;
        public float ShortEdge => Mathf.Min(OrigPixelSize.x, OrigPixelSize.y);
        public bool IsTiny => ShortEdge < 11f; // below 11px short edge: quality metrics ignored. 短边<11px：忽略质量指标。

        public override string ToString() => $"Island[{Channel}]({TriangleIndices.Count} tris, bbox {OrigPixelSize.x}x{OrigPixelSize.y})";
    }

    /// <summary>
    /// One use of a texture by a material property on a renderer slot (+ optional animation states).
    /// 一个材质属性在某个渲染器槽位上对贴图的一次引用（含可能的动画状态）。
    /// </summary>
    public sealed class TextureUse
    {
        public Texture2D Texture;
        public TextureKind Kind;
        public TextureClass Class;
        /// <summary>Shader property name (e.g. _MainTex). 着色器属性名。</summary>
        public string PropertyName;
        /// <summary>UV channel sampled by this use (0..7). 该引用采样的 UV 通道。</summary>
        public int UVChannel;
        /// <summary>True if the material's ST (scale/offset/rotation) for this property is identity and never animated. 该属性 ST 是否为恒等且无动画。</summary>
        public bool HasIdentityST;
        /// <summary>True if any animation can change this property's texture. 是否有动画可切换该贴图。</summary>
        public bool AnimatedTexture;
        /// <summary>Worst-case alpha evaluation set across all referencing materials/animation states. 所有引用材质/动画状态的最严苛 alpha 评估集。</summary>
        public AlphaMode AlphaMode = AlphaMode.Opaque;
        public float Cutoff = 0.5f;
        /// <summary>True if the texture use is whitelisted / must be skipped. 是否白名单/跳过。</summary>
        public bool Skip;
        /// <summary>Reason for skipping (for warnings). 跳过原因（用于 warning）。</summary>
        public string SkipReason;
        /// <summary>The material (pre-optimization) that referenced this texture; may be an animated candidate. 引用该贴图的材质（优化前，可能是动画候选）。</summary>
        public Material Material;
        /// <summary>The material slot index on the renderer. 渲染器上的材质槽位索引。</summary>
        public int SlotIndex;
        /// <summary>Target quality scale factor assigned by the scaler for this (use, island) pair, keyed by island. 缩放器为 (use, island) 分配的目标缩放（按岛索引）。</summary>
        public Dictionary<UVIsland, Vector2> IslandScaleFactors = new Dictionary<UVIsland, Vector2>();
    }

    /// <summary>
    /// A UV group: all textures that share the same UV space (same renderer+slot+channel+islands).
    /// UV 组：共享同一 UV 空间（同一渲染器+槽位+通道+岛集合）的全部贴图。
    /// All members must map to identical normalized atlas positions.
    /// 所有成员必须映射到相同的归一化图集位置。
    /// </summary>
    public sealed class UVGroup
    {
        public Renderer Renderer;
        public int SlotIndex;
        public int Channel;
        public List<UVIsland> Islands = new List<UVIsland>();
        public List<TextureUse> Uses = new List<TextureUse>();
        /// <summary>True if at least one member use is not whitelisted (candidate for atlasing). 是否有成员可图集化。</summary>
        public bool AnyOptimizable;

        /// <summary>Normalized layout region occupied by this group inside an atlas. 该组在图集中的归一化布局区域。</summary>
        public Rect LayoutRectUV;

        public override string ToString() => $"UVGroup({Renderer?.name}, slot{SlotIndex}, ch{Channel}, {Islands.Count} islands, {Uses.Count} uses)";
    }

    /// <summary>
    /// Atlas bucket key: textures that can share one atlas.
    /// 图集桶 key：可共享同一图集的贴图集合。
    /// </summary>
    public struct AtlasBucketKey : IEquatable<AtlasBucketKey>
    {
        public TextureClass Class;
        public bool LinearSpace;   // false = sRGB. 是否线性空间。
        public ATOFilterMode Filter;

        public bool Equals(AtlasBucketKey other) => Class == other.Class && LinearSpace == other.LinearSpace && Filter == other.Filter;
        public override bool Equals(object obj) => obj is AtlasBucketKey other && Equals(other);
        public override int GetHashCode() => (int)Class * 397 ^ (LinearSpace ? 1 : 0) ^ ((int)Filter << 3);
        public override string ToString() => $"{Class}|{(LinearSpace ? "Linear" : "sRGB")}|{Filter}";
    }

    /// <summary>
    /// One generated atlas.
    /// 一张生成的图集。
    /// </summary>
    public sealed class AtlasDefinition
    {
        public AtlasBucketKey Bucket;
        public Texture2D AtlasTexture;
        public int Width;
        public int Height;
        /// <summary>Material property name that should sample this atlas (per UVGroup member use). 采样该图集的材质属性名。</summary>
        public Dictionary<TextureUse, string> PropertyForUse = new Dictionary<TextureUse, string>();
        /// <summary>Placed islands: island -> pixel rect inside this atlas. 已放置岛：岛 → 图集内像素矩形。</summary>
        public Dictionary<UVIsland, RectInt> IslandRects = new Dictionary<UVIsland, RectInt>();
        /// <summary>Occupied (non-empty) ratio, 0..1, for the report. 利用率（报告用）。</summary>
        public float Utilization;
        /// <summary>Total pixel area of islands. 岛总面积（像素）。</summary>
        public long IslandPixelArea;
        /// <summary>Atlas pixel area. 图集像素面积。</summary>
        public long AtlasPixelArea;
        /// <summary>Source textures contributing to this atlas (report). 来源贴图（报告用）。</summary>
        public List<Texture2D> SourceTextures = new List<Texture2D>();
    }

    /// <summary>
    /// Global per-bake context shared across pipeline stages.
    /// 跨管线阶段共享的烘焙上下文。
    /// </summary>
    public sealed class ATOBuildContext
    {
        public ATOSettings Settings;
        public ATOPlatform Platform;
        public List<UVGroup> UVGroups = new List<UVGroup>();
        public List<AtlasDefinition> Atlases = new List<AtlasDefinition>();
        /// <summary>Renderer -> newly remapped mesh (only for renderers whose UVs changed). 渲染器 → 重映射网格。</summary>
        public Dictionary<Renderer, Mesh> NewMeshes = new Dictionary<Renderer, Mesh>();
        /// <summary>Renderer -> original (pre-remap) mesh, for AAO UV evacuation. 渲染器 → 原（重映射前）网格，供 AAO UV 转移。</summary>
        public Dictionary<Renderer, Mesh> OriginalMeshes = new Dictionary<Renderer, Mesh>();
        /// <summary>Original texture -> replacement (atlas or scaled), for reference rewriting. 原贴图 → 替代贴图。</summary>
        public Dictionary<Texture2D, Texture2D> TextureReplacement = new Dictionary<Texture2D, Texture2D>();
        /// <summary>Materials whose texture references were rewritten. 已重写贴图引用的材质。</summary>
        public HashSet<Material> TouchedMaterials = new HashSet<Material>();
        /// <summary>use -> its atlas (for baking & property assignment). 引用 → 其图集（烘焙与属性赋值用）。</summary>
        public Dictionary<TextureUse, AtlasDefinition> UseAtlas = new Dictionary<TextureUse, AtlasDefinition>();
        /// <summary>(material, property) pairs already assigned an atlas; whole-texture rewrite must skip them. 已赋图集的 (材质, 属性) 对；整图缩放重写须跳过。</summary>
        public HashSet<(Material, string)> AtlasAssignedProps = new HashSet<(Material, string)>();
        /// <summary>Uses that fell back to whole-texture scaling (not atlased). 回退到整图缩放的引用（未图集化）。</summary>
        public HashSet<TextureUse> WholeTextureUses = new HashSet<TextureUse>();
    }
}
