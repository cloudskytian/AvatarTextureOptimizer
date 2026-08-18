// Avatar Texture Optimizer (ATO)
// Core data model shared across pipeline stages.
// 管线各阶段共享的核心数据模型。

using System;
using System.Collections.Generic;
using UnityEngine;

namespace NetFosa.ATO
{
    /// <summary>
    /// Transparency mode of a material reference (affects alpha metric choice).
    /// 材质引用的透明模式（决定 alpha 指标选择）。
    /// </summary>
    public enum ATOAlphaMode
    {
        Opaque = 0,  // no alpha usage / 不使用 alpha
        Cutout = 1,  // alpha-tested clip / alpha 测试裁剪
        Blend = 2    // alpha blended / alpha 混合
    }

    /// <summary>
    /// One material+property usage of a texture. / 贴图在某材质某属性上的一处使用。
    /// </summary>
    public sealed class ATOTextureUsage
    {
        public Material material;
        public string propertyName;
        public ATOTextureCategory category;
        public int uvChannel;
        public ATOAlphaMode alphaMode = ATOAlphaMode.Opaque;
        public float cutoff = 0.5f;            // cutout threshold / cutout 阈值
        public bool fromAnimation;             // introduced by an animation swap / 来自动画切换
        public bool materialSwappedViaAnimation; // this material slot is animated / 该材质槽被动画切换
        public ATORendererRef renderer;        // owning renderer (may be null for material-path animation) / 所属渲染器（材质路径动画时可为空）
        public bool stDisqualified;            // ST vector is not identity or animated / ST 向量非单位或被动画修改
    }

    /// <summary>
    /// A canonical texture reference (after dedup). / 去重后的规范贴图引用。
    /// </summary>
    public sealed class ATOTextureRef
    {
        public Texture2D texture;         // canonical asset used everywhere / 统一使用的规范资产
        public Texture2D sourceAsset;     // original asset before dedup / 去重前的原始资产
        public string assetPath;
        public int width, height;
        public bool hasAlpha;             // actual alpha content / 实际是否含 alpha 通道内容
        public bool isSRGB;               // color space / 色彩空间
        public FilterMode filterMode;
        public TextureWrapMode wrapMode;
        public string importFingerprint;  // import-settings fingerprint / 导入设置指纹
        public bool isWhitelisted;        // dedup result inherits whitelist / 去重结果继承白名单
        public bool skipAllOptimization;  // fully skipped (whitelisted or ineligible) / 完全跳过优化

        public readonly List<ATOTextureUsage> usages = new List<ATOTextureUsage>();

        // Whole-texture scaling (no-atlas mode, or UV-mate of a whitelisted texture).
        // 整图缩放（无图集模式，或白名单贴图的同 UV 贴图）。
        public bool wholeTextureScale;
        public float wholeScale = 1f;

        // Fallback: island cannot fit even the largest atlas; keep it in place (scaled UVs).
        // 兜底：岛连最大图集都装不下；保持原位（缩放后的 UV），贴图保持原尺寸。
        public bool fallbackNoAtlas;

        /// <summary>Effective category: the strictest among all usages. / 有效分类：所有使用中最严格者。</summary>
        public ATOTextureCategory Category
        {
            get
            {
                if (usages.Count == 0) return ATOTextureCategory.Other;
                var c = usages[0].category;
                foreach (var u in usages)
                {
                    if (u.category == ATOTextureCategory.NormalMap) return ATOTextureCategory.NormalMap;
                    if (u.category == ATOTextureCategory.MainColor && c != ATOTextureCategory.NormalMap) c = ATOTextureCategory.MainColor;
                    if (u.category == ATOTextureCategory.Mask && c != ATOTextureCategory.NormalMap && c != ATOTextureCategory.MainColor) c = ATOTextureCategory.Mask;
                    if (u.category == ATOTextureCategory.Grayscale && c == ATOTextureCategory.Other) c = ATOTextureCategory.Grayscale;
                }
                return c;
            }
        }

        /// <summary>True when any usage requires normal-map handling. / 任一使用要求法线处理时返回真。</summary>
        public bool IsNormal => Category == ATOTextureCategory.NormalMap;

        public int Area => width * height;
    }

    /// <summary>
    /// A UV island extracted from a mesh's UV channel. / 从网格某 UV 通道提取出的 UV 岛。
    /// </summary>
    public sealed class ATOIsland
    {
        public int islandId;
        public int meshId;
        public int uvChannel;
        public int subMesh;              // source sub-mesh (same submesh => same material) / 来源子网格（同子网格 => 同材质）

        public Vector2[] uv;             // local per-island-vertex UV / 岛内逐顶点 UV（局部索引）
        public int[] triangles;          // triangle indices into local vertices / 指向局部顶点的三角形索引
        public int[] localVertices;      // mesh vertex index per local vertex / 局部顶点对应的网格顶点索引

        public Vector2 minUV, maxUV;     // bounds in UV space / UV 空间包围盒
        public float areaUv;             // island area in UV space / UV 空间面积
        public float areaTexel;          // area in texels at original texture resolution / 原贴图分辨率下的纹素面积

        public bool outOfBounds;         // bounds outside [0,1] / 包围盒超出 [0,1]
        public bool crossesWrapSeam;     // crosses the wrap seam and needs repeat sampling / 跨越 wrap 缝且依赖 repeat 采样
        public bool normalized;          // whether we applied a translation to normalize / 是否已做平移归一
        public Vector2 normalizationOffset;

        // --- results ---
        public float uniformScale = 1f;       // uniform scale applied / 均匀缩放比例
        public Vector2 anisotropicScale = Vector2.one; // extra per-axis refine / 额外双轴细化
        public bool pureColor;                // island is a solid color / 岛为纯色
        public Color32 pureColorValue;
        public bool scalingSkipped;           // targetQuality == 1 or pure color / 因目标质量=1或纯色跳过缩放

        // Atlas placement (if atlased). / 图集摆放（若进入图集）。
        public bool placed;
        public int atlasIndex = -1;
        public Vector2 placementMinUv;   // normalized bottom-left in the atlas / 图集内归一化左下角
        public int rotation;             // 0/90/180/270 / 旋转角度
        public Vector2 scaledMinUv;      // scaled footprint min (UV units) / 缩放后足迹左下（UV 单位）
        public Vector2 scaledSizeUv;     // scaled footprint size (UV units) / 缩放后足迹尺寸（UV 单位）

        /// <summary>Bounds in UV space. / UV 空间包围盒。</summary>
        public Vector2 Size => maxUV - minUV;

        /// <summary>Total per-axis scale (uniform × anisotropic). / 各轴总缩放（均匀 × 各向异性）。</summary>
        public Vector2 TotalScale => new Vector2(uniformScale * anisotropicScale.x, uniformScale * anisotropicScale.y);
    }

    /// <summary>
    /// A UV space of one mesh+channel: the set of islands and the textures bound to it.
    /// This is the "UV group" level: all textures sharing this UV space must be placed
    /// at identical positions in their respective atlases.
    /// 某个网格+通道的 UV 空间：一组岛及其绑定的全部贴图。这是"UV 组"层级——
    /// 共享该 UV 空间的全部贴图必须在其各自图集中放在相同位置。
    /// </summary>
    public sealed class ATOUvSpace
    {
        public int meshId;
        public int uvChannel;
        public readonly List<ATOIsland> islands = new List<ATOIsland>();
        public readonly List<ATOTextureRef> textures = new List<ATOTextureRef>();
        public bool usable;                       // false => treat as whitelist (e.g. wrap-seam repeat) / false => 视作白名单
        public string unusableReason;

        // Atlas layout (global normalized placement). / 图集布局（全局归一化摆放）。
        public bool hasNormalTexture;             // any normal-map texture => rotation locked to 0 / 含法线贴图 => 锁定 0 旋转
        public int pageIndex;                     // layout page / 布局分页
        public Vector2 placementMinUv;            // normalized bottom-left of the space union / 空间并集的归一化左下角
        public int rotation;                      // 0/90/180/270 / 旋转角度
        public Vector2 scaledMinUv;               // scaled union bbox min (UV units) / 缩放后并集包围盒左下（UV 单位）
        public Vector2 scaledSizeUv;              // scaled union bbox size (UV units) / 缩放后并集包围盒尺寸（UV 单位）
    }

    /// <summary>
    /// A renderer instance on the avatar with its material slots. / Avatar 上的一个渲染器实例及其材质槽。
    /// </summary>
    public sealed class ATORendererRef
    {
        public int rendererId;
        public Renderer renderer;
        public bool isSkinned;
        public string path;               // transform path relative to avatar root / 相对 Avatar 根的变换路径
        public Mesh workingMesh;          // cloned mesh we will mutate / 将要修改的克隆网格
        public Mesh sourceMesh;
        public Material[] slots;          // current material slots / 当前材质槽
        public bool enabled;
        public bool animatedEnabled;      // enabled/disabled via animation / 被动画启用/禁用
        public readonly HashSet<int> usedUvChannels = new HashSet<int>();

        /// <summary>Whether this renderer should be processed at all. / 该渲染器是否参与处理。</summary>
        public bool EffectiveEnabled => enabled || animatedEnabled;
    }

    /// <summary>
    /// A generated atlas. / 生成的图集。
    /// </summary>
    public sealed class ATOAtlas
    {
        public string name;
        public Texture2D texture;
        public int width, height;
        public ATOTextureCategory category;   // type group of this atlas / 图集所属类型组
        public ATOTextureCategory typeGroup;  // broader type-group id (see AtlasPacker) / 更宽的类型组 id
        public bool hasAlpha;
        public float utilization;             // used-cell area / total area / 利用率
        public readonly List<ATOTextureRef> sources = new List<ATOTextureRef>();
        public readonly List<ATOIsland> islands = new List<ATOIsland>();
        public int islandCount;
        public readonly List<(Material material, string property)> references = new List<(Material, string)>();
    }

    /// <summary>
    /// Per-island quality result (for logs/report). / 逐岛质量结果（用于日志/报告）。
    /// </summary>
    public sealed class ATOIslandQualityResult
    {
        public int islandId;
        public float worstMetric;      // value of the limiting metric / 限制性指标的值
        public string limitingMetric;  // e.g. "MS-SSIM" / 例如 "MS-SSIM"
        public int originalTexels;
        public int scaledTexels;
    }

    /// <summary>
    /// Mapping used to rewrite animation references after dedup/merge.
    /// 去重/合并后用于改写动画引用的映射。
    /// </summary>
    public sealed class ATOAnimationRemap
    {
        public readonly Dictionary<Texture, Texture> textureRemap = new Dictionary<Texture, Texture>();
        public readonly Dictionary<Material, Material> materialRemap = new Dictionary<Material, Material>();
        // When a material must be cloned per renderer: original -> (rendererId -> clone).
        public readonly Dictionary<Material, Dictionary<int, Material>> materialCloneByRenderer = new Dictionary<Material, Dictionary<int, Material>>();
        // rendererId -> (oldSlotIndex -> newSlotIndex or -1 if removed)
        public readonly Dictionary<int, Dictionary<int, int>> slotRemap = new Dictionary<int, Dictionary<int, int>>();
    }
}
