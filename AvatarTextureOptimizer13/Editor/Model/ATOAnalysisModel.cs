// ATO — Avatar Texture Optimizer
// Data model shared by all pipeline stages: texture usages, UV groups, texture type
// groups, UV islands, packed atlases, animation state and the analysis result.
// 各管线阶段共用的数据模型：贴图用途、UV 组、贴图类型组、UV 岛、装箱图集、动画状态与分析结果。

using System.Collections.Generic;
using UnityEngine;
using net.fosa.ato;

namespace net.fosa.ato.editor
{
    /// <summary>
    /// One usage of a texture on a material slot.
    /// 一张贴图在某个材质槽上的一次用途。
    /// </summary>
    public class ATOTextureUsage
    {
        public Texture2D texture;
        public ATOTextureKind kind = ATOTextureKind.Color;
        /// <summary>Shader property name (e.g. _MainTex). 着色器属性名。</summary>
        public string propertyName;
        /// <summary>UV channel (0..7). UV 通道（0..7）。</summary>
        public int uvChannel;
        /// <summary>True when this is the primary color texture of the slot. 是否为该槽的主色贴图。</summary>
        public bool isMainColor;
        /// <summary>Owning material. 所属材质。</summary>
        public Material material;
        /// <summary>Owning renderer. 所属渲染器。</summary>
        public Renderer renderer;
        /// <summary>Material slot index on the renderer. 渲染器上的材质槽索引。</summary>
        public int slotIndex;
        /// <summary>Non-identity ST (tiling/offset) present. 存在非单位 ST（平铺/偏移）。</summary>
        public bool hasNonIdentityST;
        /// <summary>Scroll/rotate animation present (lilToon _X_ScrollRotate or similar). 存在滚动/旋转。</summary>
        public bool hasScrollRotate;
        /// <summary>Used as decal / parallax / other transform-dependent usage. 用作贴花/视差等依赖变换的用途。</summary>
        public bool isSpecialUsage;
        /// <summary>Whether the owning material is whitelisted. 所属材质是否白名单。</summary>
        public bool materialWhitelisted;
        /// <summary>Whether this usage is whitelisted (any whitelist rule hit). 该用途是否白名单（命中任意白名单规则）。</summary>
        public bool whitelisted;
        /// <summary>Replacement texture (atlas or scaled) to write back. 要回写的替代贴图（图集或缩放后）。</summary>
        public Texture2D replacement;

        /// <summary>True when the texture is still eligible for UV/atlas optimization. 该贴图是否仍符合 UV/图集优化条件。</summary>
        public bool IsEligible => !whitelisted && !materialWhitelisted &&
                                  !hasNonIdentityST && !hasScrollRotate && !isSpecialUsage;
    }

    /// <summary>
    /// Shared kind helpers. 共享类别辅助。
    /// </summary>
    public static class ATOKindUtil
    {
        /// <summary>Normalize a usage kind to an atlas kind. 将用途类别归一化为图集类别。</summary>
        public static ATOTextureKind Normalize(ATOTextureKind kind)
        {
            switch (kind)
            {
                case ATOTextureKind.Mask:
                case ATOTextureKind.Grayscale:
                    return ATOTextureKind.Mask;
                case ATOTextureKind.Other:
                    return ATOTextureKind.Color;
                default:
                    return kind;
            }
        }
    }

    /// <summary>
    /// A UV group: every texture sampled with the same UV coordinates (same renderer, same
    /// channel, same transform), including textures introduced by animation swaps.
    /// All members must be packed with an identical island layout.
    /// UV 组：以相同 UV 坐标采样的全部贴图（同渲染器、同通道、同变换），包括动画切换引入的贴图。
    /// 组内所有成员必须以完全一致的岛布局装箱。
    /// </summary>
    public class ATOUVGroup
    {
        /// <summary>Stable id for logs. 日志用的稳定 id。</summary>
        public int id;
        public Renderer renderer;
        /// <summary>UV channel this group covers. 该组覆盖的 UV 通道。</summary>
        public int uvChannel;
        /// <summary>All usages (primary + animation-introduced) in this group. 组内全部用途（主用 + 动画引入）。</summary>
        public List<ATOTextureUsage> usages = new List<ATOTextureUsage>();
        /// <summary>Material slot index (submesh) this group covers. 该组覆盖的材质槽（子网格）下标。</summary>
        public int slotIndex;
        /// <summary>Whether the group was whitelisted (all members). 该组是否白名单（全部成员）。</summary>
        public bool whitelisted;
        /// <summary>Whether the group has at least one whitelisted member (skip atlas, whole-texture scale the rest). 该组是否有白名单成员（跳过图集化，其余贴图整图缩放）。</summary>
        public bool hasWhitelistMember;
        /// <summary>World-space area scale factor from animated mesh scale (>= 1). 动画缩放的面积因子（>= 1）。</summary>
        public float areaScaleFactor = 1f;
        /// <summary>UV islands of this group (extracted from the slot's submesh). 该组的 UV 岛（从槽的子网格提取）。</summary>
        public List<ATOIsland> islands = new List<ATOIsland>();
    }

    /// <summary>
    /// Texture type group: textures sharing the same "special map signature" so that the
    /// atlases generated for them (and their normals / masks) do not waste space.
    /// 贴图类型组：共享相同"特殊贴图签名"的贴图，使为它们（及其法线/蒙版）生成的图集不浪费空间。
    /// </summary>
    public class ATOTextureTypeGroup
    {
        /// <summary>Grouping key (serialized signature). 分组键（序列化签名）。</summary>
        public string key;
        public bool hasNormalMap;
        public bool hasMask;
        public bool linearColorSpace;
        public FilterMode filterMode;
        /// <summary>Main-color usages belonging to this group. 属于该组的主色用途。</summary>
        public List<ATOTextureUsage> colorUsages = new List<ATOTextureUsage>();

        public static string BuildKey(bool hasNormalMap, bool hasMask, bool linear, FilterMode filter)
        {
            return $"N{(hasNormalMap ? 1 : 0)}M{(hasMask ? 1 : 0)}L{(linear ? 1 : 0)}F{(int)filter}";
        }
    }

    /// <summary>
    /// A UV island extracted from a mesh for one UV channel.
    /// 从网格的某一 UV 通道提取出的 UV 岛。
    /// </summary>
    public class ATOIsland
    {
        /// <summary>Triangles (indices into the mesh's triangle array) forming the island. 构成岛的三角形（网格三角形数组的下标）。</summary>
        public List<int> triangles = new List<int>();
        /// <summary>Mesh vertex indices belonging to this island (for write-back). 该岛对应的网格顶点下标（用于回写）。</summary>
        public List<int> vertexIndices = new List<int>();
        /// <summary>Per-triangle island-local vertex indices (3 per triangle, into originalUV/scaledUV). 每三角形 3 个岛本地顶点下标（指向 originalUV/scaledUV）。</summary>
        public List<int> triangleUV = new List<int>();
        /// <summary>Original per-vertex UVs of this island (already normalized to [0,1] if applicable). 该岛逐顶点原始 UV（如适用已归一化到 [0,1]）。</summary>
        public Vector2[] originalUV;
        /// <summary>UV-space bounds. UV 空间包围盒。</summary>
        public Rect bounds;
        /// <summary>UV-space area (fraction of the texture). UV 空间面积（占贴图比例）。</summary>
        public float uvArea;
        /// <summary>Approximate world-space area (for density clamping). 近似世界空间面积（用于密度钳制）。</summary>
        public float worldArea;
        /// <summary>True when the island is solid color and can be shrunk to minimum. 纯色岛，可缩到最小。</summary>
        public bool solidColor;
        /// <summary>True when quality==1 and the island is copied as-is. 质量=1 时原样拷贝。</summary>
        public bool losslessSkip;
        /// <summary>Resulting uniform scale (1 = unchanged). 结果均匀缩放（1=不变）。</summary>
        public float uniformScale = 1f;
        /// <summary>Resulting per-axis scales (after anisotropic refinement). 双轴细化后的结果缩放。</summary>
        public float scaleX = 1f, scaleY = 1f;
        /// <summary>Scaled UVs ready for packing / rewriting. 用于装箱/回写的缩放后 UV。</summary>
        public Vector2[] scaledUV;
    }

    /// <summary>
    /// A unique texture entity after content+import-settings dedup.
    /// 按内容+导入设置去重后的唯一贴图实体。
    /// </summary>
    public class ATOTextureRef
    {
        public Texture2D texture;
        /// <summary>Dedup key (content hash + import settings). 去重键（内容哈希 + 导入设置）。</summary>
        public string dedupKey;
        /// <summary>Usages referencing this texture. 引用该贴图的全部用途。</summary>
        public List<ATOTextureUsage> usages = new List<ATOTextureUsage>();
        /// <summary>True when the dedup source contained a whitelisted entry. 去重来源含白名单时为 true。</summary>
        public bool whitelisted;
        /// <summary>Resolved alpha mode across all referencing materials (strictest). 所有引用材质中最严格的透明模式。</summary>
        public ATOAlphaMode alphaMode = ATOAlphaMode.Opaque;
        /// <summary>Resolved cutoff (strictest). 最严格 Cutoff。</summary>
        public float cutoff = 0.5f;
        /// <summary>Whole-texture scale (used when atlas generation is disabled). 整图缩放（不生成图集时使用）。</summary>
        public float wholeTextureScale = 1f;
    }

    /// <summary>
    /// A packed island placed inside an atlas. 放入图集的一个已装箱岛。
    /// </summary>
    public class ATOPackedIsland
    {
        public ATOIsland island;
        /// <summary>Pixel offset within the atlas. 图集内的像素偏移。</summary>
        public Vector2Int offset;
        /// <summary>Pixel size of the island inside the atlas (without padding). 岛在图集内的像素尺寸（不含 padding）。</summary>
        public Vector2Int size;
        /// <summary>Rotation in 90° steps (0..3). 90° 步进旋转（0..3）。</summary>
        public int rotationSteps;
        /// <summary>Whether the rasterization was transposed (rotation). 光栅化是否已转置（旋转）。</summary>
        public bool transposed;
    }

    /// <summary>
    /// A generated atlas (one per texture-kind per type-group queue). 生成的图集（每种贴图类别每队列一个）。
    /// </summary>
    public class ATOAtlas
    {
        public string name;             // ATO_...
        public ATOTextureKind kind;
        public int size;                // edge length in px
        public bool npot;
        public Texture2D texture;
        /// <summary>Whether the atlas contains alpha content (drives compression + alpha source). 图集是否含 alpha 内容（决定压缩与 alpha 来源）。</summary>
        public bool transparent;
        /// <summary>UV groups (units) merged into this atlas. 合并进该图集的 UV 组（单元）。</summary>
        public List<ATOUVGroup> units = new List<ATOUVGroup>();
        /// <summary>Islands placed in this atlas. 放入该图集的岛。</summary>
        public List<ATOPackedIsland> packed = new List<ATOPackedIsland>();
        /// <summary>Source textures contributing to this atlas (for the report). 该图集的来源贴图（用于报告）。</summary>
        public List<Texture2D> sources = new List<Texture2D>();
        /// <summary>Utilization ratio 0..1. 利用率 0..1。</summary>
        public float utilization;
    }

    /// <summary>
    /// Animation analysis results affecting texture/UV optimization.
    /// 影响贴图/UV 优化的动画分析结果。
    /// </summary>
    public class ATOAnimationState
    {
        /// <summary>Renderers whose enable/disable state is animated. 启用/禁用状态被动画修改的渲染器。</summary>
        public HashSet<Renderer> animatedEnableRenderers = new HashSet<Renderer>();
        /// <summary>Renderers whose scale is animated (max scale factor). 缩放被动画修改的渲染器（最大缩放因子）。</summary>
        public Dictionary<Renderer, float> animatedScaleFactors = new Dictionary<Renderer, float>();
        /// <summary>Material slots whose material reference is animated. 材质引用被动画修改的材质槽。</summary>
        public HashSet<(Renderer, int)> animatedMaterialSlots = new HashSet<(Renderer, int)>();
        /// <summary>Texture properties animated (property name per material). 被动画修改的贴图属性。</summary>
        public HashSet<(Material, string)> animatedTextureProps = new HashSet<(Material, string)>();
        /// <summary>Blend shapes animated (per renderer). 被动画修改的形态键。</summary>
        public Dictionary<Renderer, HashSet<int>> animatedBlendShapes = new Dictionary<Renderer, HashSet<int>>();
        /// <summary>Animated render mode / cutoff properties. 渲染模式/Cutoff 被动画修改。</summary>
        public HashSet<Material> animatedRenderMode = new HashSet<Material>();
    }

    /// <summary>
    /// The full analysis result, carried between passes via the NDMF context state.
    /// 完整分析结果，通过 NDMF 上下文状态在 Pass 间传递。
    /// </summary>
    public class ATOAnalysisResult
    {
        public AvatarTextureOptimizer component;
        public ATOEffectiveSettings settings;

        public List<ATOTextureUsage> allUsages = new List<ATOTextureUsage>();
        public List<ATOUVGroup> uvGroups = new List<ATOUVGroup>();
        public List<ATOTextureTypeGroup> typeGroups = new List<ATOTextureTypeGroup>();
        public List<ATOTextureRef> textures = new List<ATOTextureRef>();
        public ATOAnimationState animation = new ATOAnimationState();

        /// <summary>Islands per renderer per UV channel. 各渲染器各 UV 通道的岛。</summary>
        public Dictionary<Renderer, Dictionary<int, List<ATOIsland>>> islandsByRenderer = new Dictionary<Renderer, Dictionary<int, List<ATOIsland>>>();

        /// <summary>Final atlases produced. 产生的最终图集。</summary>
        public List<ATOAtlas> atlases = new List<ATOAtlas>();

        /// <summary>Whether any processing actually happened. 是否实际发生了处理。</summary>
        public bool didAnything;

        /// <summary>Estimated texture memory before/after (bytes), for the report. 报告用的优化前后贴图内存（字节）。</summary>
        public long bytesBefore;
        public long bytesAfter;

        public IEnumerable<Renderer> AllRenderers
        {
            get
            {
                var set = new HashSet<Renderer>();
                foreach (var u in allUsages) if (u.renderer != null) set.Add(u.renderer);
                foreach (var r in islandsByRenderer.Keys) set.Add(r);
                return set;
            }
        }
    }
}
