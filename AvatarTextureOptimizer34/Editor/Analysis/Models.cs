// AvatarTextureOptimizer - Models
// EN: Core data models for the analysis pipeline.
// CN: 分析管线的核心数据模型。
using System.Collections.Generic;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer
{
    /// <summary>EN: Texture usage category. / CN: 贴图用途分类。</summary>
    public enum TextureUsage
    {
        Albedo = 0,   // 主色（含 emission/gradation 等 sRGB 彩色贴图）
        Normal = 1,   // 法线
        GrayMask = 2, // 灰度/蒙版
    }

    /// <summary>EN: Effective render mode of a material (with animation in mind). / CN: 材质有效渲染模式（考虑动画）。</summary>
    public enum RenderMode
    {
        Opaque = 0,
        Cutout = 1,
        Blend = 2
    }

    /// <summary>EN: A material usage site: render modes + cutoffs accumulated from the material itself and all animations
    /// (the evaluator takes the strictest combination).
    /// CN: 材质使用点：从材质本身与全部动画累积渲染模式与 Cutoff（评估器取最严苛组合）。</summary>
    public sealed class MaterialUsage
    {
        public Material material;
        public bool animated;                                   // 有动画修改
        public bool hasNormalRef;                               // 材质引用法线贴图
        public bool hasMaskRef;                                 // 材质引用灰度/蒙版贴图
        public readonly HashSet<RenderMode> modes = new HashSet<RenderMode>();
        public readonly List<float> cutoffs = new List<float>();
        public readonly HashSet<string> animatedProperties = new HashSet<string>();

        public void AddMode(RenderMode m) => modes.Add(m);
        public void AddCutoff(float c) { if (c > 0.01f) cutoffs.Add(c); }

        /// <summary>EN: Strictest mode (Blend > Cutout > Opaque). / CN: 最严苛模式。</summary>
        public RenderMode StrictestMode
        {
            get
            {
                if (modes.Contains(RenderMode.Blend)) return RenderMode.Blend;
                if (modes.Contains(RenderMode.Cutout)) return RenderMode.Cutout;
                return RenderMode.Opaque;
            }
        }
    }

    /// <summary>EN: Where a texture is used on a mesh: renderer + material slot. / CN: 贴图在网格上的使用点：渲染器 + 材质槽。</summary>
    public struct MeshUsage
    {
        public Mesh mesh;
        public Renderer renderer;
        public int slot;
    }

    /// <summary>
    /// EN: One texture usage instance (deduplicated identity + usage info + referencing materials).
    /// CN: 一个贴图使用实例（去重身份 + 用途信息 + 引用材质）。
    /// </summary>
    public sealed class TextureRef
    {
        public Texture2D texture;
        public string propertyName = "";      // 着色器属性名
        public TextureUsage usage;            // 主要用途（多种用途时取最严苛）
        public bool sRGB;                     // 色彩空间
        public FilterMode filterMode;
        public int width, height;
        public bool whitelisted;              // 完全跳过（含导入参数）
        public bool skipAtlas;                // 跳过图集化，但允许整图缩放 + 导入参数优化
        public bool specialUv;                // 特殊 UV 用途（matcap 等）→ 等同白名单
        public bool animated;                 // 出现在动画切换中
        public int uvChannel;                 // 网格 UV 通道
        public readonly List<MeshUsage> meshUsages = new List<MeshUsage>();
        public readonly List<MaterialUsage> materials = new List<MaterialUsage>();
        public readonly Dictionary<MaterialUsage, TextureUsage> usageByMaterial =
            new Dictionary<MaterialUsage, TextureUsage>();
        public readonly List<UvGroup> uvGroups = new List<UvGroup>();
        public TypeGroup typeGroup;
        public float originalBytes;           // 原始内存估算（报告用）
        public float wholeScale = 1f;         // 整图缩放（无图集模式 / 跳图集贴图）
        public readonly Dictionary<UvGroup, float> typeScale = new Dictionary<UvGroup, float>(); // 每 UV 组的类型均匀缩放

        public bool HasAlphaRequirement
        {
            get
            {
                foreach (var m in materials)
                    if (m.StrictestMode != RenderMode.Opaque) return true;
                return false;
            }
        }
    }

    /// <summary>
    /// EN: A UV island: connected triangle group on one mesh UV channel (overlaps merged by rasterization).
    /// CN: 一个 UV 岛：网格某 UV 通道上的连通三角形组（重叠经光栅化合并）。
    /// </summary>
    public sealed class Island
    {
        public int id;
        public Rect fracRect;                 // 岛在 frac 空间的 UV 矩形 [min,max]
        public Vector2Int tile;               // 岛所在平铺块 floor(min)
        public float uvArea;                  // frac 空间面积
        public float worldAreaM2;             // 世界面积（形态键 0/100 最大 + 动画最大缩放）
        public List<int> triangles = new List<int>();   // 全局三角形索引（mesh 的 triangle 数组下标）
        public List<int> materialSlots = new List<int>(); // 引用的材质槽
        public MeshUvData owner;              // 所属网格 UV 数据（覆盖率掩码用）
        public bool pureColor;                // 纯色岛（质量阶段判定）
        public Rect remapRect;                // 图集 UV 空间中的新矩形（装箱后填充）
        public bool hasRemap;                 // 是否已重映射
        // 质量阶段结果（per texture instance，由 QualityEvaluator 填充）
        public readonly Dictionary<TextureRef, IslandScale> scales = new Dictionary<TextureRef, IslandScale>();
        public float templateW, templateH;    // 模板尺寸（UV 组木桶最大）
    }

    /// <summary>EN: Per-(island,texture) scaling result. / CN: 每个 (岛, 贴图) 的缩放结果。</summary>
    public sealed class IslandScale
    {
        public float scaleX = 1f, scaleY = 1f;   // 相对原尺寸
        public int targetW, targetH;             // 目标像素尺寸（缩放后）
        public int shortSidePx;                  // 岛在原始贴图上的包围盒短边（SSIM 回退判定用）
        public bool skip;                        // 近无损跳过缩放
        public bool pureColorShortcut;           // 纯色短路
        public bool fitFailed;                   // 二分未达标（保持原样）
    }

    /// <summary>
    /// EN: UV group: all textures sampling the same (mesh, channel) share one template layout so that the same UV
    /// maps to the same position across every atlas (prevents albedo/normal atlas misalignment).
    /// CN: UV 组：采样同一 (网格, 通道) 的全部贴图共享一份模板布局，保证同一 UV 在所有图集上的位置一致。
    /// </summary>
    public sealed class UvGroup
    {
        public Mesh mesh;
        public Renderer renderer;                  // 首个渲染器（锚）
        public readonly List<Renderer> renderers = new List<Renderer>(); // 全部使用该网格的渲染器
        public int channel;
        public Vector2 uvShift;                 // 整体平移量（归一化用）
        public bool wrapCrossing;               // 跨 wrap 接缝 → 白名单
        public bool whitelisted;                // 整通道白名单
        public readonly List<Island> islands = new List<Island>();
        public readonly List<TextureRef> textures = new List<TextureRef>();
        public TemplateLayout layout;           // 装箱模板（生成后可用）
        public float layoutScale = 1f;          // 模板到网格 UV 的缩放（布局尺寸/模板像素）
        public Vector2 layoutOrigin;            // 模板原点（uv 空间）
    }

    /// <summary>
    /// EN: Type group: textures that share atlas generation (usage-set signature + colorspace + filterMode).
    /// Members pack into the same atlas pools so normal/mask atlases have similar utilization.
    /// CN: 类型组：共享图集生成的贴图集合（用途集合签名 + 色彩空间 + filterMode）。
    /// </summary>
    public sealed class TypeGroup
    {
        public bool hasNormalMember;   // 组内存在法线贴图
        public bool hasMaskMember;     // 组内存在灰度/蒙版贴图
        public bool sRGB;              // 主色色彩空间
        public FilterMode filterMode;
        public readonly List<TextureRef> textures = new List<TextureRef>();
        public readonly List<Atlas> atlases = new List<Atlas>();
        public readonly System.Collections.Generic.Dictionary<TextureUsage, float> usageScale =
            new System.Collections.Generic.Dictionary<TextureUsage, float>(); // 每用途统一缩放
        public int totalAreaPx;        // 光栅化总面积（排序用）
        public string Name => $"G{(hasNormalMember ? "N" : "")}{(hasMaskMember ? "M" : "")}{(sRGB ? "sRGB" : "Lin")}F{(int)filterMode}";
    }

    /// <summary>EN: A generated atlas asset. / CN: 生成的图集资产。</summary>
    public sealed class Atlas
    {
        public string name;
        public int width, height;
        public TextureUsage usage;                 // 图集用途
        public TypeGroup group;
        public Texture2D asset;                    // 生成资产
        public readonly List<AtlasIsland> islands = new List<AtlasIsland>();
        public int usedAreaPx;
        public float Utilization => width * height > 0 ? usedAreaPx / (float)(width * height) : 0f;
        public int sourceTextureCount;
    }

    /// <summary>EN: Island placement inside an atlas (legacy info model; PackedIsland is the active one). / CN: 岛在图集中的位置（旧信息模型；PackedIsland 为现行模型）。</summary>
    public sealed class AtlasIsland
    {
        public Island island;
        public TextureRef tex;         // 所属贴图实例
        public Rect rect;              // 图集内像素矩形
        public int rotation;           // 旋转象限（0/90/180/270）
        public float scaleX, scaleY;   // 相对原尺寸
    }

    /// <summary>EN: One mesh + one UV channel analysis result. / CN: 一个网格 + 一个 UV 通道的分析结果。</summary>
    public sealed class MeshUvData
    {
        public Mesh mesh;
        public Renderer renderer;
        public int channel;
        public Vector2[] uvs;              // 顶点 UV
        public int[][] submeshTriangles;   // 每材质槽三角形
        public int[] allTriangles;         // 合并后的三角形索引（全局）
        public Vector3[] positions;
        public Vector3[] normals;
        public Vector4[] tangents;
        public Color[] colors;
        public bool hasBlendShapes;
        public float maxAnimationScale = 1f;   // 动画最大缩放
        public readonly List<Island> islands = new List<Island>();
        public bool whitelisted;
    }

    /// <summary>EN: Animation analysis result. / CN: 动画分析结果。</summary>
    public sealed class AnimationData
    {
        public readonly List<AnimationClip> clips = new List<AnimationClip>();
        public readonly List<AnimatorControllerRef> controllers = new List<AnimatorControllerRef>();
        // 渲染模式/Cutoff 等被动画修改的材质属性（取最严苛）
        public readonly Dictionary<Material, MaterialUsage> materialUsage = new Dictionary<Material, MaterialUsage>();
        // 动画切换的贴图引用：(renderer, slotIndex, propertyName) → 贴图列表
        public readonly Dictionary<(Renderer, int, string), HashSet<Texture2D>> animatedTextureProps =
            new Dictionary<(Renderer, int, string), HashSet<Texture2D>>();
        // 材质资产上的贴图属性切换：material → (propertyName → 贴图列表)
        public readonly Dictionary<Material, Dictionary<string, HashSet<Texture2D>>> animatedMaterialAssetTextures =
            new Dictionary<Material, Dictionary<string, HashSet<Texture2D>>>();
        // 动画切换的材质引用：slot → 材质列表
        public readonly Dictionary<(Renderer, int), HashSet<Material>> animatedMaterials =
            new Dictionary<(Renderer, int), HashSet<Material>>();
        // 被动画启用的渲染器
        public readonly HashSet<Renderer> animatedEnabled = new HashSet<Renderer>();
        // 最大缩放 (GameObject → maxScale)（分析器沿祖先链查找）
        public readonly Dictionary<GameObject, float> maxScale = new Dictionary<GameObject, float>();
        // 有 ST 动画的属性：(renderer, slot, property)
        public readonly HashSet<(Renderer, int, string)> stAnimated = new HashSet<(Renderer, int, string)>();
        // 单个槽位被单独切换（阻止槽合并）
        public readonly HashSet<(Renderer, int)> individuallyAnimatedSlots = new HashSet<(Renderer, int)>();
    }

    /// <summary>EN: AnimatorController reference (runtime controller + its layers' clips). / CN: 动画控制器引用。</summary>
    public sealed class AnimatorControllerRef
    {
        public RuntimeAnimatorController controller;
        public readonly List<AnimationClip> clips = new List<AnimationClip>();
    }
}
