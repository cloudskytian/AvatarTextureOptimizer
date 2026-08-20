// Avatar Texture Optimizer (ATO)
// Bilingual comments: English + Simplified Chinese. 双语注释：英文 + 简体中文。

using System;
using System.Collections.Generic;
using AvatarTextureOptimizer.Editor.Atlas;
using AvatarTextureOptimizer.Editor.UVIsland;
using UnityEngine;

namespace AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Root data structures shared across all passes. Kept in the NDMF BuildContext via
    /// <c>context.GetState&lt;ATOBuildState&gt;()</c>.
    ///
    /// 跨 Pass 共享的根数据结构。通过 context.GetState&lt;ATOBuildState&gt;() 保存在
    /// NDMF BuildContext 中。
    /// </summary>
    public class ATOBuildState
    {
        /// <summary>The active component driving this build. 驱动本次构建的组件。</summary>
        public ATOAvatarTextureOptimizer Component;

        /// <summary>Resolved effective quality thresholds. 解析后的有效质量阈值。</summary>
        public ATOQualityThresholds Quality;

        /// <summary>Resolved effective platform. 解析后的有效平台。</summary>
        public ATOPlatform Platform = ATOPlatform.PC;

        /// <summary>Effective max atlas edge for this platform. 当前平台图集最大边长。</summary>
        public int MaxAtlasEdge = 8192;

        /// <summary>Effective allow-NPOT flag. 当前是否允许 NPOT。</summary>
        public bool AllowNPOT = false;

        /// <summary>Effective minimum padding (texels). 有效最小 padding（texel）。</summary>
        public int MinPadding = 4;

        /// <summary>
        /// Set of whitelisted objects (expanded). Textures referenced by these are skipped.
        /// 展开后的白名单对象集合。其引用的贴图跳过优化。
        /// </summary>
        public HashSet<UnityEngine.Object> Whitelist = new HashSet<UnityEngine.Object>();

        /// <summary>
        /// Set of textures that must skip ALL optimization (whitelist-propagated or unsafe).
        /// 需要跳过所有优化的贴图集合（白名单传播或判定为不安全）。
        /// </summary>
        public HashSet<Texture2D> SkippedTextures = new HashSet<Texture2D>();

        /// <summary>Collected renderers eligible for optimization. 收集到的可优化渲染器。</summary>
        public List<Renderer> EligibleRenderers = new List<Renderer>();

        /// <summary>All discovered texture records (per unique texture). 发现的全部贴图记录。</summary>
        public Dictionary<Texture2D, ATOTextureRecord> Textures = new Dictionary<Texture2D, ATOTextureRecord>();

        /// <summary>All discovered material records. 发现的材质记录。</summary>
        public Dictionary<Material, ATOMaterialRecord> Materials = new Dictionary<Material, ATOMaterialRecord>();

        /// <summary>
        /// Per (renderer, submesh) texture bindings, used to build UV sets.
        /// 按 (渲染器, 子网格) 的贴图绑定，用于构建 UV 组。
        /// </summary>
        public Dictionary<(Renderer, int), List<ATOTextureBinding>> SubmeshBindings =
            new Dictionary<(Renderer, int), List<ATOTextureBinding>>();

        /// <summary>All extracted islands. 提取出的全部岛。</summary>
        public List<ATOUVIslandEntry> Islands = new List<ATOUVIslandEntry>();

        /// <summary>Packed atlas groups (per type group). 装箱后的图集组（按类型组）。</summary>
        public List<ATOAtlasGroupResult> AtlasGroups = new List<ATOAtlasGroupResult>();

        /// <summary>Generated atlases: index → Texture2D (filled in regenerate pass). 生成的图集。</summary>
        public List<Texture2D> GeneratedAtlases = new List<Texture2D>();

        /// <summary>Original texture → its atlas texture (atlas mode). 原贴图 → 其图集（图集模式）。</summary>
        public Dictionary<Texture2D, Texture2D> TextureToAtlas = new Dictionary<Texture2D, Texture2D>();

        /// <summary>Original texture → its replacement (whole-texture scaling mode). 原贴图 → 替换（整图缩放模式）。</summary>
        public Dictionary<Texture2D, Texture2D> TextureRemap = new Dictionary<Texture2D, Texture2D>();

        /// <summary>Whether the build was cancelled. 是否已取消。</summary>
        public volatile bool Cancelled = false;

        /// <summary>Progress + cancellation reporting. 进度与取消报告。</summary>
        public ATOProgress Progress;

        public void ThrowIfCancelled()
        {
            if (Cancelled) throw new OperationCanceledException("ATO build cancelled by user. / 用户取消了 ATO 烘焙。");
        }

        /// <summary>Initialize progress reporting for a bake. 初始化烘焙进度报告。</summary>
        public void InitProgress(string avatarName, int totalStages)
        {
            if (Progress == null)
            {
                Progress = new ATOProgress(this, avatarName);
                Progress.SetTotalStages(totalStages);
            }
        }

        /// <summary>Begin a named stage (no-op if progress is disabled). 开始命名阶段。</summary>
        public void BeginStage(string name) => Progress?.BeginStage(name);

        /// <summary>Dispose progress reporting. 释放进度报告。</summary>
        public void EndProgress() => Progress?.Dispose();
    }

    /// <summary>
    /// Record for a unique texture discovered during analysis.
    /// 分析阶段发现的单张唯一贴图的记录。
    /// </summary>
    public class ATOTextureRecord
    {
        public Texture2D Texture;
        public ATOTextureCategory Category = ATOTextureCategory.Albedo;
        public bool IsSrgb;
        public FilterMode FilterMode;
        public TextureWrapMode WrapMode;
        public int Width, Height;
        public bool HasAlpha;
        public bool HasMipmaps;

        /// <summary>True if this texture is exempt from all optimization. 是否豁免所有优化。</summary>
        public bool SkipAll;

        /// <summary>
        /// Original import settings signature (dedup key). 原始导入设置签名（去重键）。
        /// </summary>
        public string ImportSignature;

        /// <summary>Cached decoded pixels (linear space where applicable). 缓存的解码像素。</summary>
        public Color[] Pixels;

        /// <summary>Cached raw pixels for hashing/alpha detection. 缓存的原始像素。</summary>
        public Color32[] Pixels32;

        /// <summary>Content + import hash for dedup. 用于去重的内容+导入哈希。</summary>
        public byte[] ContentHash;

        /// <summary>Path of the source asset ("" if runtime texture). 源资产路径（运行时贴图为空）。</summary>
        public string AssetPath;

        /// <summary>
        /// Type-group key for atlas grouping: (category, isSrgb, filterMode).
        /// 图集类型组键：(类别, 是否sRGB, filterMode)。
        /// </summary>
        public string TypeGroupKey => $"{(int)Category}|{(IsSrgb ? 1 : 0)}|{(int)FilterMode}";
    }

    /// <summary>
    /// Record for a material discovered during analysis, plus every (property → texture)
    /// binding that qualifies. 材质记录 + 每个符合条件的 (属性 → 贴图) 绑定。
    /// </summary>
    public class ATOMaterialRecord
    {
        public Material Material;
        public Renderer Renderer;
        public int SubMeshIndex;

        /// <summary>
        /// Texture bindings: property name → texture, with category and UV channel.
        /// 贴图绑定：属性名 → 贴图，含类别与 UV 通道。
        /// </summary>
        public List<ATOTextureBinding> Bindings = new List<ATOTextureBinding>();
    }

    /// <summary>
    /// A single (material property → texture) binding with its semantic category and
    /// UV channel. 单个（材质属性 → 贴图）绑定，含语义类别与 UV 通道。
    /// </summary>
    public class ATOTextureBinding
    {
        public string PropertyName;
        public Texture2D Texture;
        public ATOTextureCategory Category;
        public int UVChannel;
    }

    /// <summary>
    /// An extracted island bound to its renderer/submesh/UV-channel and the textures that
    /// sample it. 绑定到渲染器/子网格/UV 通道及其采样贴图的岛条目。
    /// </summary>
    public class ATOUVIslandEntry
    {
        public Renderer Renderer;
        public int SubMeshIndex;
        public int UVChannel;
        public ATOUVIsland Island;
        public Rect NormalizedBounds;   // UV bounds after normalization. 归一化后的 UV 包围盒。
        public int OffsetTileX, OffsetTileY; // integer tile offset removed. 移除的整数瓦片偏移。
        public List<ATOTextureRecord> Textures = new List<ATOTextureRecord>();

        /// <summary>Uniform scale applied (1.0 = original). 均匀缩放（1.0=原始）。</summary>
        public float UniformScale = 1.0f;

        /// <summary>Anisotropic scale per axis (1.0 = original). 双轴各向异性缩放。</summary>
        public Vector2 AnisoScale = Vector2.one;

        /// <summary>
        /// True when this island shares its UV with a whitelisted texture → it skips
        /// atlas-ization (but still participates in whole-texture scaling).
        /// 当该岛与白名单贴图共享 UV 时为 true → 跳过图集化（但仍参与整图缩放）。
        /// </summary>
        public bool SkipAtlas;
    }
}
