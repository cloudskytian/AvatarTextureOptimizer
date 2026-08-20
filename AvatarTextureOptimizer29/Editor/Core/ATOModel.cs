// Shared pipeline data model. / 流水线共享数据模型。
// The AtoSession is threaded through every stage. / AtoSession 贯穿全部阶段。

using System;
using System.Collections.Generic;
using nadena.dev.ndmf;
using UnityEngine;

namespace net.fosa.ato.editor
{
    /// <summary>Alpha handling of a material usage. / 材质用途的透明模式。</summary>
    internal enum AlphaMode { Opaque, Cutout, Blend }

    /// <summary>Semantic class of a texture property. / 贴图属性语义类别。</summary>
    internal enum TexKind { Color, Normal, GrayMask, Special }

    /// <summary>One texture usage site (renderer slot x shader property). / 一次贴图引用。</summary>
    internal class TexUse
    {
        internal Renderer renderer;
        internal int slot;              // material slot == submesh index; -1 = all slots (animated swap) / -1=全槽（动画切换）
        internal Material material;
        internal string prop;           // shader texture property / 着色器纹理属性名
        internal Texture2D texture;
        internal TexKind kind;
        internal int uvChannel;         // mesh uv channel / 网格UV通道
        internal AlphaMode alpha;       // for color textures / 主色贴图的透明模式
        internal float cutoff = 0.5f;
        internal bool stTransformed;    // own ST or uvMain transformed / 存在UV变换
        internal bool specialUse;       // decal/matcap/parallax... / 特殊用途
    }

    /// <summary>Renderer + its material slots (static + animated). / 渲染器与其材质槽。</summary>
    internal class RendererInfo
    {
        internal Renderer renderer;
        internal Mesh mesh;
        internal bool skinned;
        internal string path;                 // path from avatar root / 相对根路径
        internal bool animatedEnabled;        // may be activated by animation / 可能被动画启用
        internal readonly List<Material[]> slotMaterials = new List<Material[]>(); // per slot: static + animated variants / 每槽：静态+动画变体
        /// <summary>Max scale factor from animated ancestor scales (per axis, >= 1). / 动画缩放因子。</summary>
        internal Vector3 animatedScaleFactor = Vector3.one;
        /// <summary>Original UV data per channel (pre-rewrite, for AAO evacuation). / 原UV数据（AAO疏散用）。</summary>
        internal readonly Dictionary<int, Vector2[]> originalUvBackup = new Dictionary<int, Vector2[]>();
    }

    /// <summary>Aggregate per-texture info. / 每贴图聚合信息。</summary>
    internal class TexInfo
    {
        internal Texture2D texture;
        internal readonly List<TexUse> uses = new List<TexUse>();
        internal bool whitelisted;
        internal string whiteReason;
        internal bool eligibleForAtlas;      // not whitelist & has at least one clean mesh-UV usage / 可图集化
        internal bool forceNoAtlas;          // shares UV with whitelist/cross-seam texture -> whole-image only / 强制非图集
        internal AtoTexCategory category = AtoTexCategory.Opaque;
        internal bool hasAlphaContent;       // any alpha < 0.99 / 是否含透明内容
        internal bool isGrayscaleContent;    // R≈G≈B everywhere / 内容是否灰度
        internal readonly HashSet<byte> usedChannels = new HashSet<byte>(); // for masks / 蒙版使用的通道
        internal readonly HashSet<byte> contentChannels = new HashSet<byte>(); // channels that actually vary / 实际有变化的通道
        internal Texture2D dedupTarget;      // canonical instance after dedup / 去重后的规范实例
    }

    /// <summary>One mesh+channel triangle group inside an island. / 岛内一个网格+通道的三角形组。</summary>
    internal class IslandGroup
    {
        internal RendererInfo ri;
        internal int channel;
        internal int[] triangles;             // global triangle indices of this mesh / 该网格的全局三角形索引
        internal readonly HashSet<Texture2D> textures = new HashSet<Texture2D>();
    }

    /// <summary>A UV island across all its usages and textures (the UV-group unit).
    /// UV 岛：跨网格使用集与贴图集（即 UV 组单元）。</summary>
    internal class UvIsland
    {
        internal int id;
        internal readonly List<IslandGroup> groups = new List<IslandGroup>();
        internal Rect uvBounds;              // normalized source UV space / 归一后源UV空间包围盒
        internal float uvArea;               // triangle-covered UV area (not bbox) / 三角形覆盖UV面积
        internal float worldArea;            // edit-time world area incl. blendshape & scale factors / 世界面积（含因子）
        internal readonly HashSet<Texture2D> textures = new HashSet<Texture2D>(); // UV group textures / UV组贴图
        // quality results per texture: island pixel size in that texture / 每贴图缩放后像素尺寸
        internal readonly Dictionary<Texture2D, Vector2Int> scaledSize = new Dictionary<Texture2D, Vector2Int>();
        internal bool pureColor;
        // per-texture normalized bounds correction is identity: all textures share the island's
        // normalized UV region (UV-group requirement). / 岛内全部贴图共享归一UV区域（UV组要求）。
        internal float crossSeam;            // >0 if some triangle spans > 1 in UV (whitelist) / 跨缝标记
    }

    /// <summary>Connected component of the texture-island bipartite graph = atomic packing unit.
    /// 纹理↔岛二部图连通分量 = 装箱原子单元。</summary>
    internal class PackingComponent
    {
        internal int id;
        internal readonly List<UvIsland> islands = new List<UvIsland>();
        internal readonly HashSet<Texture2D> textures = new HashSet<Texture2D>();
        internal bool fallbackNoAtlas;   // oversized / cross-seam / whitelist-shared -> whole-image scaling / 回退整图缩放
        internal string fallbackReason;
        internal bool placedInAtlas;
        // type group signature / 类型组签名
        internal bool srgb = true;
        internal FilterMode filterMode = FilterMode.Bilinear;
        internal bool hasNormal, hasMask;
    }

    /// <summary>Everything the pipeline knows. / 流水线全量状态。</summary>
    internal class AtoSession
    {
        internal BuildContext ctx;
        internal AvatarTextureOptimizer component;
        internal AtoPlatform platform;
        internal AtoPlatformSettings settings;    // effective (override resolved) / 生效设置
        internal AtoQualityParams quality;        // effective params / 生效质量参数
        internal bool qualityIsOne;               // preset NearLossless && quality==1 semantics: skip scaling / 跳过缩放
        internal readonly List<RendererInfo> renderers = new List<RendererInfo>();
        internal readonly Dictionary<Texture2D, TexInfo> texInfos = new Dictionary<Texture2D, TexInfo>();
        internal readonly List<UvIsland> islands = new List<UvIsland>();
        internal readonly List<PackingComponent> components = new List<PackingComponent>();
        internal readonly List<string> warnings = new List<string>();
        internal readonly List<AtlasLayout> atlases = new List<AtlasLayout>(); // filled by packer / 装箱结果
        // (mesh, channel) -> integer translate applied during island normalization
        // (网格,通道) -> 岛归一化时的整数平移量
        internal readonly Dictionary<(Mesh, int), Vector2> uvOffsets = new Dictionary<(Mesh, int), Vector2>();
        // renderers whose meshes were rewritten + which channels / 被重写网格与其通道
        internal readonly Dictionary<Renderer, HashSet<int>> rewrittenChannels = new Dictionary<Renderer, HashSet<int>>();
        internal AnimationData anim;                       // filled by AnimationAnalyzer / 动画分析结果
        internal readonly Dictionary<string, AnimatedMatProps> matAnim = new Dictionary<string, AnimatedMatProps>();
        internal readonly Dictionary<Texture2D, Texture2D> textureDedupMap = new Dictionary<Texture2D, Texture2D>();
        internal readonly Dictionary<Material, Material> materialCloneMap = new Dictionary<Material, Material>();
        internal int atlasedTextures, atlasedMaterials; // counters for report / 报告计数
    }
}
