// ATOModels.cs
// Core data model of the analysis graph: texture usages, mesh islands, UV groups,
// type groups and atlas layers. / 分析图核心模型:贴图用途、网格岛、UV组、类型组、图集层。
// Copyright (c) 2026 fosa. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace net.fosa.ato
{
    /// <summary>Functional role of a texture in a material. / 贴图在材质中的功能角色。</summary>
    internal enum TexRole
    {
        /// <summary>sRGB color texture (albedo, emission color, shadow color...). / sRGB 颜色贴图。</summary>
        Color = 0,
        /// <summary>Tangent-space normal map. / 切线空间法线贴图。</summary>
        Normal = 1,
        /// <summary>Grayscale/data mask; evaluated per used channel. / 灰度/数据蒙版;按使用通道评估。</summary>
        Mask = 2,
    }

    /// <summary>Alpha handling mode of a referencing material. / 引用材质的透明处理模式。</summary>
    internal enum AlphaMode
    {
        Opaque = 0,
        Cutout = 1,
        Blend = 2,
    }

    /// <summary>One way one texture is used by one material on one mesh region. / 一张贴图被一个材质在一片网格区域使用的一次记录。</summary>
    internal sealed class TextureUsage
    {
        internal Texture2D Texture;
        internal TexRole Role;
        /// <summary>Mesh UV channel this usage samples. / 该用途采样的网格 UV 通道。</summary>
        internal int UvChannel;
        /// <summary>Shader property name. / 着色器属性名。</summary>
        internal string PropertyName;
        /// <summary>Material providing this usage (may be an animation variant). / 提供该用途的材质(可为动画变体)。</summary>
        internal Material Material;
        /// <summary>True when any ST/scroll/rotate/decal transform exists (→ whitelist). / 存在任何 UV 变换时为真(→白名单)。</summary>
        internal bool HasTransform;
        /// <summary>True when sampled with UV not tied to mesh (matcap, LUT, screen...). / 非网格 UV 采样(matcap/LUT/屏幕)。</summary>
        internal bool NonMeshUv;
        /// <summary>Channels actually read by the shader (Mask role only). / 着色器实际读取的通道(仅 Mask 角色)。</summary>
        internal byte UsedChannels; // bit0=R,1=G,2=B,3=A
        /// <summary>Material alpha mode for this usage. / 该用途下材质的透明模式。</summary>
        internal AlphaMode Alpha;
        /// <summary>Cutoff value when Alpha==Cutout (keyframe values merged strictly). / Cutout 模式下的阈值(动画取最严)。</summary>
        internal float Cutoff;
        /// <summary>All cutoff values to evaluate (animated cutoffs). / 需要逐一评估的全部阈值(含动画)。</summary>
        internal float[] MultiCutoffs;
        /// <summary>Animation changes render mode → Blend metrics also required. / 动画修改渲染模式→同时要求 Blend 指标。</summary>
        internal bool BlendAlsoRequired;
        /// <summary>Whether the source texture is imported as sRGB. / 源贴图是否按 sRGB 导入。</summary>
        internal bool Srgb;
        internal FilterMode Filter;
    }

    /// <summary>A UV island of one (mesh, submesh, uv channel). / 一个(网格,子网格,UV通道)上的 UV 岛。</summary>
    internal sealed class UvIsland
    {
        internal int Id;
        /// <summary>Vertex indices (into mesh arrays). / 顶点索引(网格数组)。</summary>
        internal int[] Vertices;
        /// <summary>Triangle vertex-index triples belonging to this island. / 属于本岛的三角形顶点索引。</summary>
        internal List<int> Triangles = new List<int>();
        /// <summary>UV bounds (already normalized). / UV 包围盒(已归一化)。</summary>
        internal Rect UvBounds;
        /// <summary>Approximate world-space area (m²) incl. blendshape/scale maxima. / 近似世界面积(米²,含形态键/缩放最大值)。</summary>
        internal float WorldArea;
        /// <summary>Pixel coverage bit grid over the island bbox (1 bit per cell, set by rasterizer). / 岛 bbox 上的像素覆盖位网格。</summary>
        internal IslandRasterMask PixelMask;
        /// <summary>All zeros → pure color island. / 全零 → 纯色岛。</summary>
        internal bool IsPureColor;
        internal Color32 PureColor;
    }

    /// <summary>Islands of one (mesh, submesh, uvChannel). / 一个(网格,子网格,UV通道)的岛集合。</summary>
    internal sealed class IslandSetData
    {
        internal Mesh Mesh;
        internal int SubMesh;
        internal int Channel;
        internal List<UvIsland> Islands = new List<UvIsland>();
        /// <summary>UV normalization offset applied (integer shift into [0,1]). / 已应用的 UV 归一化偏移。</summary>
        internal Vector2 NormalizeOffset;
        /// <summary>Islands blocked by whitelist/unprocessed textures sharing the UV → no atlasing. / 被白名单/未处理贴图共享 UV 的岛→不图集化。</summary>
        internal bool BlockedByWhitelist;
        /// <summary>Unresolvable UVs (cross-wrap, tiled) → skip. / 无法处理的 UV(跨 wrap/平铺)→跳过。</summary>
        internal bool Unusable;
        internal string UnusableReason;
        /// <summary>Normalized UVs (mesh-vertex indexed). / 归一化后的 UV(按网格顶点索引)。</summary>
        internal Vector2[] NormalizedUvs;
    }

    /// <summary>A texture + its accumulated strictness across all usages. / 一张贴图及其所有用途的最严要求汇总。</summary>
    internal sealed class TextureNode
    {
        internal Texture2D Tex;
        internal int InstanceId;
        internal List<TextureUsage> Usages = new List<TextureUsage>();
        /// <summary>Merged strictest alpha requirements (per referencing material). / 合并后的最严 alpha 要求。</summary>
        internal List<AlphaRequirement> AlphaRequirements = new List<AlphaRequirement>();
        /// <summary>Any usage whitelisted → whole texture is whitelisted. / 任一用途进白名单→整张贴图白名单。</summary>
        internal bool Whitelisted;
        internal bool WhitelistReasonRecorded;
        /// <summary>Primary role = the most restrictive usage role. / 主角色=最严格的用途角色。</summary>
        internal TexRole PrimaryRole;
        /// <summary>True if any usage pairs this texture with a normal map. / 任一用途与本贴图配对法线时为真。</summary>
        internal bool HasNormalCompanion;
        /// <summary>True if any usage pairs this texture with a mask. / 任一用途与本贴图配对蒙版时为真。</summary>
        internal bool HasMaskCompanion;
        /// <summary>Whitelist contamination: skip atlasing, use whole-texture scaling. / 白名单污染:跳过图集化,改用整图缩放。</summary>
        internal bool NoAtlas;
        /// <summary>All islands were placed into atlases. / 全部岛已放入图集。</summary>
        internal bool Atlased;
        internal bool Srgb;         // import setting / 导入设置
        internal FilterMode Filter;
        /// <summary>Color layer assignment within its component (variants separated). / 所在组件内的颜色分层(变体分离)。</summary>
        internal int ColorLayer;
        /// <summary>All islands covered by this texture. / 本贴图覆盖的全部岛。</summary>
        internal List<IslandRef> IslandRefs = new List<IslandRef>();
    }

    /// <summary>Connected component of the island↔texture bipartite graph. / 岛↔贴图二部图的连通分量。</summary>
    internal sealed class UvGroup
    {
        internal int Id;
        /// <summary>Islands in this component. / 分量内的岛。</summary>
        internal List<IslandRef> Islands = new List<IslandRef>();
        /// <summary>Textures in this component. / 分量内的贴图。</summary>
        internal List<TextureNode> Textures = new List<TextureNode>();
        /// <summary>Signature: which parallel layers exist. / 签名:存在哪些平行层。</summary>
        internal UvGroupSignature Signature;
        /// <summary>True when any island has multiple textures (animation variants). / 任一岛有多贴图(动画变体)时为真。</summary>
        internal bool HasVariants;
        /// <summary>Dedup replacement (set when identical content). / 去重替换目标。</summary>
        internal Texture2D DedupTarget;
        internal bool Deduped;
        /// <summary>Final per-island scale decisions. / 最终逐岛缩放决策。</summary>
        internal List<IslandScaleDecision> ScaleDecisions;
        /// <summary>Packing outcome. / 装箱结果。</summary>
        internal bool Packed;
        internal bool PackFailed;
        /// <summary>Fell back to standalone scaling due to whitelist contamination. / 因白名单污染回退整图缩放。</summary>
        internal bool FallbackWhitelist;
    }

    /// <summary>Type-group signature: parallel-layer kinds that exist. / 类型组签名:存在的平行层种类。</summary>
    internal sealed class UvGroupSignature : IEquatable<UvGroupSignature>
    {
        internal bool HasColor;
        internal bool HasNormal;
        internal bool HasMask;
        internal bool ColorSrgb;
        internal FilterMode ColorFilter;
        internal bool AnyLinearColor; // linear color texture exists → separate color layer / 存在线性颜色贴图→独立颜色层

        public bool Equals(UvGroupSignature other) => other != null &&
            HasColor == other.HasColor && HasNormal == other.HasNormal && HasMask == other.HasMask &&
            ColorSrgb == other.ColorSrgb && ColorFilter == other.ColorFilter &&
            AnyLinearColor == other.AnyLinearColor;
        public override int GetHashCode() =>
            (HasColor, HasNormal, HasMask, ColorSrgb, (int)ColorFilter, AnyLinearColor).GetHashCode();
        public override string ToString() =>
            $"color{(ColorSrgb ? ":srgb" : ":lin")}/f{(int)ColorFilter}{(HasNormal ? "/n" : "")}{(HasMask ? "/m" : "")}{(AnyLinearColor ? "/lin-color" : "")}";
    }

    /// <summary>Per-island quality scale decision. / 单岛质量缩放决策。</summary>
    internal struct IslandScaleDecision
    {
        /// <summary>Island reference. / 岛引用。</summary>
        internal int SetId, IslandId;
        /// <summary>Chosen scale per axis (≤1), relative to the group's LARGEST texture. / 各轴缩放(≤1),以组内最大贴图为基准。</summary>
        internal float Sx, Sy;
        /// <summary>Reference (largest) texture dims the scales relate to. / 缩放所参照的最大贴图尺寸。</summary>
        internal int RefW, RefH;
        /// <summary>Scale used by binary search progress. / 二分搜索进度。</summary>
        internal int SearchSteps;
        /// <summary>Short-circuit reason. / 短路原因。</summary>
        internal string Note;
    }

    /// <summary>Everything the pipeline knows about one avatar. / 管线对单个 Avatar 的全部认知。</summary>
    internal sealed class ATOBuildData
    {
        internal BuildContext Ctx;
        internal AvatarTextureOptimizer Component;
        internal PlatformProfile EffectiveProfile;
        internal ATOPlatform Platform;

        // --- Analysis / 分析 ---
        internal AnimationDatabase Animations;
        internal List<RendererRecord> Renderers = new List<RendererRecord>();
        internal Dictionary<Texture2D, TextureNode> TextureNodes = new Dictionary<Texture2D, TextureNode>();
        internal List<IslandSetData> IslandSets = new List<IslandSetData>();
        /// <summary>(setId, islandId) → texture list. / 岛→贴图列表。</summary>
        internal Dictionary<long, List<TextureNode>> IslandTextures = new Dictionary<long, List<TextureNode>>();
        internal List<UvGroup> UvGroups = new List<UvGroup>();
        internal HashSet<Texture2D> WhitelistedTextures => _whiteTex;
        private readonly HashSet<Texture2D> _whiteTex = new HashSet<Texture2D>();
        /// <summary>Dedup map texture → canonical representative. / 去重映射。</summary>
        internal Dictionary<Texture2D, Texture2D> TextureDedupMap = new Dictionary<Texture2D, Texture2D>();
        /// <summary>Textures that failed some analysis step and stay as-is. / 分析失败保持原样的贴图。</summary>
        internal HashSet<Texture2D> SkippedTextures = new HashSet<Texture2D>();

        // --- Quality / 质量 ---
        /// <summary>Per-island minimum passing uniform scale per texture (barrel-merged later). / 逐贴图逐岛的最小通过缩放。</summary>
        internal Dictionary<long, float> IslandMinScale = new Dictionary<long, float>();

        // --- Packing / 装箱 ---
        internal List<AtlasPlan> AtlasPlans = new List<AtlasPlan>();
        /// <summary>Source texture → its atlas. / 源贴图→图集。</summary>
        internal Dictionary<Texture2D, AtlasPlan> AtlasByTexture = new Dictionary<Texture2D, AtlasPlan>();

        // --- Bake / 烘焙 ---
        internal Dictionary<Texture2D, Texture2D> TextureReplacements = new Dictionary<Texture2D, Texture2D>();
        /// <summary>Standalone scaled textures (no-atlas mode). / 独立缩放贴图(无图集模式)。</summary>
        internal Dictionary<Texture2D, Texture2D> StandaloneBaked = new Dictionary<Texture2D, Texture2D>();
        /// <summary>Materials cloned by ATO (original → clone). / ATO 克隆的材质。</summary>
        internal Dictionary<Material, Material> MaterialClones = new Dictionary<Material, Material>();
        internal Dictionary<Mesh, Mesh> MeshClones = new Dictionary<Mesh, Mesh>();
        /// <summary>Material slot merges per renderer: old index → new index. / 材质槽合并映射。</summary>
        internal Dictionary<Renderer, int[]> SlotRemaps;
        internal long OriginalPixelCount;
        internal long OptimizedPixelCount;

        // --- Report / 报告 ---
        internal List<string> ReportLines = new List<string>();
        internal List<string> ReportDetails = new List<string>();
        internal int Warnings;

        internal static long Key(int setId, int islandId) => ((long)setId << 32) | (uint)islandId;
    }

    /// <summary>One renderer under processing. / 一个正在处理的渲染器记录。</summary>
    internal sealed class RendererRecord
    {
        internal Renderer Renderer;
        internal string Path;
        internal bool InitiallyActive;
        internal bool AnimatedActive;
        /// <summary>Slot index → materials (current first, then animation swaps). / 槽位→材质(当前材质在前,后为动画切换)。</summary>
        internal Dictionary<int, List<Material>> SlotMaterials = new Dictionary<int, List<Material>>();
        /// <summary>Max animated scale factor (area multiplier). / 动画最大缩放因子(面积乘子)。</summary>
        internal float MaxScaleFactor = 1f;
        /// <summary>Mesh used. / 使用的网格。</summary>
        internal Mesh Mesh;
    }


    /// <summary>One output atlas (one parallel layer of one atlas family). / 一张输出图集(一个图集族的一个平行层)。</summary>
    internal sealed class AtlasPlan
    {
        internal string Name;
        internal int Width, Height;
        internal TexRole Role;
        internal bool Srgb;
        internal FilterMode Filter;
        /// <summary>Layer index inside the family (variants). / 族内层索引(变体)。</summary>
        internal int LayerIndex;
        /// <summary>Family id: components sharing layout. / 族 id:共享布局的组件集合。</summary>
        internal int FamilyId;
        /// <summary>Islands placed: rect + source texture + island ref. / 已放置的岛。</summary>
        internal List<PlacedIsland> Placed = new List<PlacedIsland>();
        internal Texture2D Baked;
        /// <summary>Utilization = covered cells / total cells. / 利用率。</summary>
        internal float Utilization;
        /// <summary>Overall aux scale (≤1, e.g. downscaled normal atlas). / 辅助层整体缩放(≤1)。</summary>
        internal float AuxScale = 1f;
        /// <summary>Alpha present in content. / 内容是否含 alpha。</summary>
        internal bool HasAlpha;
    }

    /// <summary>An island placed into an atlas. / 放入图集的一个岛。</summary>
    internal sealed class PlacedIsland
    {
        internal int SetId, IslandId;
        internal Texture2D Source;
        /// <summary>Padded placement rect in atlas pixels. / 图集像素坐标放置矩形。</summary>
        internal RectInt Rect;
        /// <summary>Placement scale (island content scale inside rect). / 放置缩放。</summary>
        internal float Sx, Sy;
        /// <summary>Rotated 90°. / 旋转 90°。</summary>
        internal bool Rotated;
        /// <summary>UV bounds in the SOURCE texture. / 源贴图内 UV 包围盒。</summary>
        internal Rect SourceUvBounds;
        /// <summary>Normalized rect in the atlas (0..1). / 图集内归一化矩形(0..1)。</summary>
        internal Rect RectN;
    }

    /// <summary>Alpha strictness entry. / alpha 最严要求条目。</summary>
    internal sealed class AlphaRequirement
    {
        internal AlphaMode Mode; internal float Cutoff;
        internal AlphaRequirement(AlphaMode m, float c) { Mode = m; Cutoff = c; }
    }

    /// <summary>Reference to one island. / 指向一个岛的引用。</summary>
    internal struct IslandRef : IEquatable<IslandRef>
    {
        internal int SetId, IslandId;
        internal IslandRef(int setId, int islandId) { SetId = setId; IslandId = islandId; }
        internal long Key => ATOBuildData.Key(SetId, IslandId);
        public bool Equals(IslandRef other) => SetId == other.SetId && IslandId == other.IslandId;
        public override bool Equals(object obj) => obj is IslandRef r && Equals(r);
        public override int GetHashCode() => (SetId * 397) ^ IslandId;
    }

    /// <summary>Animated texture swap entry. / 动画贴图切换条目。</summary>
    internal sealed class TexSwapEntry
    {
        internal string Prop; internal Texture2D Tex;
        internal TexSwapEntry(string prop, Texture2D tex) { Prop = prop; Tex = tex; }
    }

    /// <summary>Animation facts gathered from all clips. / 从全部动画片段收集到的事实。</summary>
    internal sealed class AnimationDatabase
    {
        /// <summary>Renderer path → slot → swapped materials (with clip). / 渲染器路径→槽位→切换材质。</summary>
        internal Dictionary<string, Dictionary<int, List<Material>>> MaterialSwaps =
            new Dictionary<string, Dictionary<int, List<Material>>>();
        /// <summary>Renderer path → texture-swap entries. / 路径→贴图切换条目。</summary>
        internal Dictionary<string, List<TexSwapEntry>> TextureSwaps = new Dictionary<string, List<TexSwapEntry>>();
        /// <summary>Paths of objects whose active state is animated. / active 被动画驱动的路径。</summary>
        internal HashSet<string> AnimatedActivePaths = new HashSet<string>();
        /// <summary>Renderer path → slot → {float prop → keyframe values}. / 槽位→浮点属性→关键帧值。</summary>
        internal Dictionary<string, Dictionary<int, Dictionary<string, float[]>>> MaterialFloatKeyframes =
            new Dictionary<string, Dictionary<int, Dictionary<string, float[]>>>();
        /// <summary>Path → max animated scale product. / 路径→最大动画缩放乘积。</summary>
        internal Dictionary<string, float> MaxScaleByPath = new Dictionary<string, float>();
        /// <summary>Renderer path → slot indices with any material animation. / 有材质动画的槽位。</summary>
        internal HashSet<(string, int)> AnimatedSlots = new HashSet<(string, int)>();
        /// <summary>Blendshape curves: path → (shape name → [min,max]). / 形态键曲线。</summary>
        internal Dictionary<string, Dictionary<string, float[]>> BlendshapeCurves = new Dictionary<string, Dictionary<string, float[]>>();
    }
}
