// ============================================================================
// AvatarTextureOptimizer (net.fosa.avatar-texture-optimizer)
// Core/Model.cs — 核心数据模型 / Core data model
//
// 关键关系 (Coder1/Coder2 共识):
//   Mesh + UV 通道 → UVGroup(该 UV 采样的全部贴图, 含动画切换)
//   UVGroup 的贴图按 TextureFamily(类型组) 归类；一个 UVGroup 可横跨多个 Family
//   (例如同一 UV 同时被有法线/无法线材质引用 → 主色族 + 法线族)。
//   Island 属于 UVGroup；同一个岛在所有 Family 图集中的矩形位置必须完全一致
//   (保证 UV 切换材质后采样内容不变)。缩放按 UV 组内木桶效应取最大尺寸。
// ============================================================================
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor
{
    /// <summary>
    /// 贴图角色 / Texture role.
    /// </summary>
    public enum TextureRole
    {
        /// <summary>主色（反照率） / Main color (albedo)</summary>
        MainColor,
        /// <summary>法线 / Normal map</summary>
        Normal,
        /// <summary>蒙版（混合/溶解/阴影等灰度用途） / Mask (blend/dissolve/shadow etc.)</summary>
        Mask,
        /// <summary>自发光 / Emission</summary>
        Emission,
        /// <summary>其他（保守优化） / Other (conservative)</summary>
        Other,
    }

    /// <summary>
    /// 单个贴图引用（材质属性 → 贴图）/ A single texture reference (material property → texture).
    /// </summary>
    public sealed class TextureRef
    {
        /// <summary>去重后的规范贴图 / Canonical texture after dedup</summary>
        public Texture2D source;

        /// <summary>着色器属性名 / Shader property name</summary>
        public string property;

        /// <summary>角色 / Role</summary>
        public TextureRole role;

        /// <summary>压缩分类（由角色+alpha 推导） / Compression category (derived)</summary>
        public TextureCategory category;

        /// <summary>UV 通道（0..7）/ UV channel</summary>
        public int uvChannel;

        /// <summary>材质存在非单位 ST 变换 → 白名单 / Non-identity ST transform on the material → whitelist</summary>
        public bool hasSTTransform;

        /// <summary>贴花或特殊采样（非网格UV、POM 等）→ 白名单 / Decal or special sampling → whitelist</summary>
        public bool isDecalOrSpecial;

        /// <summary>动画中存在对该属性的修改（切换贴图/ST 等）/ Animated (texture swap or ST anim)</summary>
        public bool animated;

        /// <summary>综合白名单标记 / Combined whitelist flag</summary>
        public bool whitelisted;

        /// <summary>白名单原因（用于 warning）/ Whitelist reason (for warnings)</summary>
        public string whitelistReason;

        /// <summary>源贴图是否使用 alpha（惰性缓存） / Whether source uses alpha (lazy)</summary>
        public bool hasAlpha;

        /// <summary>源贴图色彩空间 / Source colorspace (sRGB)</summary>
        public bool sRGB;

        /// <summary>源贴图过滤模式 / Source filter mode</summary>
        public FilterMode filterMode;

        /// <summary>该贴图在哪些材质槽被引用（用于 alpha 模式/Cutoff 最严苛评估）/
        /// Material slots referencing this texture (for strictest alpha-mode/cutoff evaluation)</summary>
        public List<MaterialSlotRef> referencingSlots = new List<MaterialSlotRef>();

        /// <summary>来源贴图像素内存估计（优化量统计用）/ Estimated source bytes</summary>
        public long sourceBytes;

        /// <summary>目标贴图像素内存估计（优化量统计用）/ Estimated target bytes</summary>
        public long targetBytes;
    }

    /// <summary>
    /// 材质槽引用（renderer × slotIndex）/ Material slot reference.
    /// </summary>
    public sealed class MaterialSlotRef
    {
        public Renderer renderer;
        public int slotIndex;
        public Material material;
        public Mesh mesh;
        public List<TextureRef> textures = new List<TextureRef>();
        public bool whitelisted;
        public bool isSkinned => renderer is SkinnedMeshRenderer;

        /// <summary>主材质的透明模式与裁剪阈值 / Main material's alpha mode &amp; cutoff</summary>
        public AlphaMode alphaMode = AlphaMode.Opaque;
        public float cutoff = 0.5f;

        /// <summary>动画切换材质的透明模式（最严苛评估用）/ Alpha modes of animation-swapped materials</summary>
        public List<(AlphaMode mode, float cutoff)> extraAlphaModes = new List<(AlphaMode, float)>();
    }

    /// <summary>
    /// UV 组：同一 (mesh, uvChannel) 上所有贴图的集合（含动画切换）/
    /// UV group: all textures sampled by one (mesh, uvChannel).
    /// </summary>
    public sealed class UVGroup
    {
        public Mesh mesh;
        public int uvChannel;
        public List<TextureRef> textures = new List<TextureRef>();
        public List<Island> islands = new List<Island>();

        /// <summary>白名单/无法重排（跨缝、动画ST 等）→ 不图集化 / Whitelisted → no repack</summary>
        public bool whitelisted;
        public string whitelistReason;

        /// <summary>组内所有贴图的原尺寸最大短边（木桶效应上限） / Max original short side across textures</summary>
        public int maxOriginalShortSide;

        /// <summary>该组对应的 Family 集合（key → family）/ Families of this group</summary>
        public Dictionary<string, TextureFamily> families = new Dictionary<string, TextureFamily>();

        /// <summary>分组级别的最终岛尺寸是否已解析 / Whether group-level final sizes resolved</summary>
        public bool sizesResolved;

        /// <summary>该组的岛是否需要缩放（质量!=1 且非纯色短路） / Whether islands of this group need scaling</summary>
        public bool needsScaling = true;
    }

    /// <summary>
    /// 贴图类型组（family）：同一图集家族 / Texture family: same atlas family.
    /// key 形如 "MainColor|sRGB|Bilinear|N"（N=是否含法线对应）。
    /// </summary>
    public sealed class TextureFamily
    {
        public string key;
        public TextureRole role;
        public TextureCategory category;
        public bool sRGB;
        public FilterMode filterMode;
        /// <summary>主色族是否含法线对应（决定是否镜像法线图集） / Whether this main-color family has normal counterparts</summary>
        public bool hasNormalCounterpart;

        public List<UVGroup> groups = new List<UVGroup>();
        public List<AtlasResult> atlases = new List<AtlasResult>();

        /// <summary>该族中参与装箱的贴图（统计用）/ All textures in this family</summary>
        public HashSet<Texture2D> sources = new HashSet<Texture2D>();
    }

    /// <summary>
    /// 岛：UV 空间连通区域 / Island: connected UV region.
    /// </summary>
    public sealed class Island
    {
        /// <summary>所属 UV 组 / Owning UV group</summary>
        public UVGroup group;

        /// <summary>覆盖的全局三角形索引 / Covered global triangle indices</summary>
        public List<int> triangles = new List<int>();

        /// <summary>UV 包围盒（归一化，允许负值；OOB 平移后归一到 [0,1]）/ UV bounds</summary>
        public Vector2 uvMin;
        public Vector2 uvMax;

        /// <summary>越界平移量（OOB 归一用）/ OOB shift applied for normalization</summary>
        public Vector2 shift;

        /// <summary>UV 面积（三角形面积和）/ UV-space area</summary>
        public float uvArea;

        /// <summary>世界面积（形态键 0/100 取最大、动画缩放取最大）/ World area</summary>
        public float worldArea;

        /// <summary>像素密度需求区间（px/m）/ Density requirement range</summary>
        public float densityLo;
        public float densityHi;

        /// <summary>原贴图像素包围盒（短边）/ Original pixel bounding box short side</summary>
        public int origShortSide;

        /// <summary>原始像素包围盒（宽高，按组内最大原尺寸）/ Original pixel bbox</summary>
        public int origW, origH;

        /// <summary>是否纯色（短路缩小用）/ Whether pure color</summary>
        public bool pureColor;

        /// <summary>最终尺寸（组级木桶效应后）/ Final size after group bucket effect</summary>
        public int finalW, finalH;

        /// <summary>纹理级目标尺寸（每个纹理自己的质量缩放结果）/
        /// Per-texture target sizes: [textureInstanceId] → (w,h,skipScaled,pureColor)</summary>
        public Dictionary<int, TexTarget> texTargets = new Dictionary<int, TexTarget>();

        /// <summary>装箱结果 / Packing result</summary>
        public int atlasX, atlasY;
        public bool rotated;
        public AtlasResult atlas;

        /// <summary>是否已装箱 / Whether packed</summary>
        public bool packed;

        /// <summary>包围盒（含padding后的像素矩形，供网格UV重映射）/ Final atlas rect (px, includes padding margin for remap)</summary>
        public RectInt finalRect;

        // ---- 装箱缓存（光栅化结果, 4px 粒度）/ packing cache (rasterized, 4px granularity) ----
        /// <summary>形状位掩码（finalW×finalH 的光栅化） / shape bitmask (rasterized at finalW×finalH)</summary>
        public ulong[] shapeMask;
        /// <summary>位掩码块宽高 / bitmask block dims</summary>
        public int maskBw, maskBh;
        /// <summary>光栅化面积（块数）/ rasterized area (blocks)</summary>
        public long rasterArea;

        /// <summary>该岛所属 UV 组是否含法线（禁止旋转）/ whether the group contains a normal (no rotation)</summary>
        public bool noRotation;
    }

    /// <summary>
    /// 单个纹理对单个岛的缩放目标 / Per-texture scaling target for an island.
    /// </summary>
    public struct TexTarget
    {
        /// <summary>目标像素宽高 / Target pixel size</summary>
        public int w, h;

        /// <summary>该纹理在近无损模式（不重采样原样拷贝）/ Near-lossless (copy as-is)</summary>
        public bool nearLossless;

        /// <summary>该岛对该纹理为纯色（短路到最小） / Pure color for this texture</summary>
        public bool pureColor;
    }

    /// <summary>
    /// 图集结果（同时充当装箱"队列槽"） / Atlas result (also acts as the packing "queue slot").
    /// </summary>
    public sealed class AtlasResult
    {
        /// <summary>名称（ATO_ 开头）/ Name (starts with ATO_)</summary>
        public string name;

        /// <summary>成品纹理 / Final texture</summary>
        public Texture2D texture;

        public int width, height;

        /// <summary>利用率（岛位+padding / 总位）/ Utilization</summary>
        public float utilization;

        /// <summary>包含的岛 / Islands in this atlas</summary>
        public List<Island> islands = new List<Island>();

        /// <summary>
        /// 内容归属: 贴图 → 该图集中属于它的岛（同一 UV 组的多个贴图共享同一组 rect）/
        /// Content ownership: texture → islands belonging to it (multiple textures of one UV group share rects).
        /// </summary>
        public Dictionary<TextureRef, List<Island>> content = new Dictionary<TextureRef, List<Island>>();

        /// <summary>所属 family / Owning family</summary>
        public TextureFamily family;

        /// <summary>来源贴图（日志用）/ Source textures (for logging)</summary>
        public HashSet<Texture2D> sources = new HashSet<Texture2D>();

        /// <summary>来源像素总数（原）/ Total source pixels</summary>
        public long sourcePixels;

        /// <summary>目标像素总数 / Total target pixels</summary>
        public long targetPixels;

        // ---- 装箱状态（队列槽）/ packing state (queue slot) ----
        /// <summary>4px 块粒度位掩码 / block-level bitmask</summary>
        public ulong[] mask;
        public int bw, bh;
        public long usedBlocks;
        public long totalBlocks;
        /// <summary>当前尺寸是否已达上限（无法再增长）/ whether the slot is at max size</summary>
        public bool atMax;
    }

    /// <summary>
    /// 一个被处理的网格实例（重写 UV 的最小单位）/
    /// A mesh instance to rewrite (smallest rewrite unit).
    /// </summary>
    public sealed class MeshRewrite
    {
        public Mesh sourceMesh;
        public Mesh resultMesh;
        /// <summary>被重写的 UV 通道 / Rewritten UV channels</summary>
        public HashSet<int> channels = new HashSet<int>();
        /// <summary>引用该网格的渲染器（材质槽白名单状态一致才能共享结果）/
        /// Renderers referencing this mesh (shared only when slot whitelist consistent)</summary>
        public List<Renderer> renderers = new List<Renderer>();
    }
}
