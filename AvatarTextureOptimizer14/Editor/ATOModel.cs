// ATOModel — shared data model for all pipeline stages / 所有流水线阶段共享的数据模型
// Consensus notes (Coder-A/B): UV group = (renderer, submesh, channel); a *super group* is the
// connected component of UV groups linked by shared textures, so "all islands from one texture end
// up in one atlas" and "same UV lands identically in every atlas" both hold atomically.<br>
// 共识：UV组=(渲染器,子网格,UV通道)；“超组”是共享贴图连通的UV组集合，保证同贴图所有岛同图集、同UV跨图集同位。
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Fosa.ATO.Editor
{
    /// <summary>Texture semantic class within a type group / 类型组内的贴图语义类别。</summary>
    internal enum TexClass { Albedo = 0, Normal = 1, Mask = 2 }
    internal enum AlphaMode { Opaque = 0, Cutout = 1, Blend = 2 }

    /// <summary>One material's reference to one texture through one property. / 某材质通过某属性对贴图的一次引用。</summary>
    internal sealed class MaterialTextureRef
    {
        internal Material material;      // material referencing / 引用材质
        internal string property;        // texture property name / 贴图属性名
        internal TexClass cls;           // semantic class / 语义类别
        internal int uvChannel;          // UV channel sampled / 采样UV通道
        internal AlphaMode alphaMode;    // for Albedo / 用于主色
        internal float cutoff = 0.5f;    // max cutoff (incl. animation) / 最大Cutoff（含动画）
        internal int maskChannelMask = 0xF; // for Mask: which RGBA channels used / 蒙版使用通道位掩码
        internal readonly List<Texture2D> textures = new List<Texture2D>(); // textures over animation states / 动画各状态贴图
    }

    /// <summary>Deduplicated texture unit with import-settings snapshot. / 去重后的贴图单位（含导入设置快照）。</summary>
    internal sealed class TextureInfo
    {
        internal Texture2D source;
        internal string dedupKey;
        internal int width, height;
        internal bool sRGB;
        internal bool isNormalMap;
        internal FilterMode filterMode = FilterMode.Bilinear;
        internal TextureWrapMode wrapMode = TextureWrapMode.Repeat;
        internal bool mipmapEnabled = true, mipStreaming;
        internal int maxTextureSize = 2048;
        internal string compressionKey = "";   // capture of TextureImporter compression / 压缩设置摘要
        internal bool alphaIsTransparency;
        internal TypeGroupKey typeKey;              // 贴图粒度类型键（Stage4 计算）/ per-texture type key (Stage4)
        internal bool whitelisted;             // 白名单贴图：跳过所有优化
        internal readonly List<string> whitelistReasons = new List<string>();
        internal readonly HashSet<TexClass> classes = new HashSet<TexClass>();

        internal void MarkWhitelist(string reason)
        {
            whitelisted = true;
            if (!whitelistReasons.Contains(reason)) whitelistReasons.Add(reason);
        }

        internal long ApproxBytes => (long)width * height * 4;
    }

    /// <summary>
    /// Type-group key: class-set mask + color-space bucket + filter bucket (max quality wins within group).<br/>
    /// 类型组键：类别集合 + 色彩空间桶 + filterMode 桶（组内取最高质量，避免同UV组被拆散）。
    /// </summary>
    internal struct TypeGroupKey : IEquatable<TypeGroupKey>
    {
        internal int classMask;      // bit(Albedo)|bit(Normal)|bit(Mask) / 类别位掩码
        internal bool albedoSRGB;    // albedo plane color space / 主色平面色彩空间
        internal int filterBucket;   // 0 point,1 bilinear,2 trilinear / 取组内最高

        internal static int ClassBit(TexClass c) => 1 << (int)c;

        public bool Equals(TypeGroupKey other) => classMask == other.classMask && albedoSRGB == other.albedoSRGB && filterBucket == other.filterBucket;
        public override bool Equals(object obj) => obj is TypeGroupKey k && Equals(k);
        public override int GetHashCode() => (classMask << 4) ^ (albedoSRGB ? 2 : 0) ^ filterBucket;
        public override string ToString() => $"mask={classMask} srgb={albedoSRGB} filter={filterBucket}";
    }

    /// <summary>An extracted UV island (triangles sharing UV continuity). / 一个提取出的 UV 岛。</summary>
    internal sealed class Island
    {
        internal PackingGroup group;         // owning super group / 所属超组
        internal UVSlotKey slot;             // (renderer, submesh, channel)
        internal readonly List<int> triIndices = new List<int>(); // triangle indices (3 ints per tri) of the slot's triangles

        // Original UV mapping / 原始UV
        internal Vector2 uvMin, uvMax;       // raw UV bounds / 原始UV包围盒
        internal Vector2Int tileOffset;      // integer tile for [0,1] normalization / 归一化整数平移
        internal Vector2 nMin, nMax;         // normalized bounds in [0,1] after shift / 平移后的归一包围盒

        internal float worldAreaMax;         // max world area (blendshape 0/100 & anim scale) / 最大世界面积（形态键0/100+动画缩放）
        internal Vector2 reqScale = Vector2.one; // final quality scale per axis / 各轴最终质量缩放
        internal Vector2Int unifiedSize;     // wood-barrel unified px size across classes / 木桶效应统一尺寸(px)
        internal bool skipScale;             // quality==1 → copy as-is / 质量为1时跳过缩放
        internal bool solidColor;            // near-constant color island / 纯色岛

        // Per-texture quality results / 逐贴图质量结果
        internal Dictionary<TextureInfo, Vector2Int> perTextureTarget;


        internal Vector2 NormalizedSpan => nMax - nMin;
    }

    /// <summary>Identity of a (renderer, submesh, uv channel) slot. / (渲染器,子网格,UV通道) 槽标识。</summary>
    internal struct UVSlotKey : IEquatable<UVSlotKey>
    {
        internal Renderer renderer; internal int submesh; internal int channel;
        public bool Equals(UVSlotKey other) => renderer == other.renderer && submesh == other.submesh && channel == other.channel;
        public override bool Equals(object obj) => obj is UVSlotKey k && Equals(k);
        public override int GetHashCode() => (renderer != null ? renderer.GetHashCode() : 0) ^ (submesh << 8) ^ channel;
    }

    /// <summary>
    /// Super group: connected component of slots sharing textures; the packing atom.<br/>
    /// 超组：通过共享贴图连通的槽集合，即装箱原子单位（对应需求中的“贴图及其所属UV组”）。
    /// </summary>
    internal sealed class PackingGroup
    {
        internal int id;
        internal readonly List<UVSlotKey> slots = new List<UVSlotKey>();
        internal readonly List<Island> islands = new List<Island>();
        internal readonly List<TextureInfo> textures = new List<TextureInfo>(); // deduped sources / 组内去重贴图
        internal readonly Dictionary<TexClass, HashSet<TextureInfo>> texturesByClass = new Dictionary<TexClass, HashSet<TextureInfo>>();
        internal readonly List<MaterialTextureRef> refs = new List<MaterialTextureRef>();
        internal TypeGroupKey typeKey;
        internal bool whitelisted;          // 组内存在白名单贴图 → 跳过图集化
        internal bool atlasAbandoned;       // 单组超出最大图集 → 放弃图集化
        internal readonly Dictionary<TexClass, Vector2Int> maxSrcByClass = new Dictionary<TexClass, Vector2Int>(); // 各类型最大原尺寸

        // Alpha strictness per class (strictest of any referencing material) / 各类型透明度最严
        internal AlphaMode strictestAlpha = AlphaMode.Opaque;
        internal float strictestCutoff = 0.5f;
    }

    /// <summary>Global island placement record (for cross-atlas co-location). / 岛全局放置记录（跨图集共位用）。</summary>
    internal sealed class IslandPlacement
    {
        internal RectInt rect;
        internal bool rotated;
        internal int padPx;
    }

    /// <summary>One generated atlas (shared layout; one texture plane per class). / 一个图集（共享布局，每类别一个贴图平面）。</summary>
    internal sealed class AtlasDef
    {
        internal sealed class Entry
        {
            internal Island island;
            internal RectInt rect;
            internal bool rotated;
            internal TextureInfo tex;   // which texture's pixels go here / 该处渲染哪张贴图的像素
        }
        internal readonly List<Entry> entries = new List<Entry>();

        internal int width, height;
        internal int padding;
        internal TypeGroupKey key;
        internal readonly List<PackingGroup> groups = new List<PackingGroup>();
        internal readonly List<Island> islands = new List<Island>();
        internal readonly Dictionary<TexClass, PlaneOut> planes = new Dictionary<TexClass, PlaneOut>();

        internal sealed class PlaneOut
        {
            internal TexClass cls;
            internal Vector2 scale = Vector2.one; // whole-plane downscale (≤1) / 整平面缩放（省体积）
            internal bool hasAlpha;
            internal Texture2D texture;
            internal string assetPath;
            internal long sourceBytes;    // sum of source textures in this plane / 源贴图体积和
        }

        internal float Utilization => width > 0 && height > 0 && entries.Count > 0
            ? entries.Sum(e => (float)e.rect.width * e.rect.height) / ((float)width * height) : 0f;
    }

    /// <summary>Resolved immutable settings snapshot. / 解析后的不可变设置快照。</summary>
    internal sealed class ATOSettingsSnap
    {
        internal bool generateAtlas;
        internal ATOQualityPreset preset;
        internal ATOQualityThresholds thresholds;
        internal int minDensity, maxDensity, minPadding;
        internal bool allowNPOT;
        internal bool dedupTextures, dedupMaterials, verbose;
        internal ATOMipSettings mips;
        internal ATOPlatformOverride pc, android, ios;
        internal string language;

        internal bool Lossless => preset == ATOQualityPreset.Lossless;

        internal ATOPlatformOverride Override(ATOPlatform p) => p switch
        {
            ATOPlatform.PC => pc, ATOPlatform.Android => android, _ => ios,
        };

        internal static ATOSettingsSnap From(AvatarTextureOptimizer c) => new ATOSettingsSnap
        {
            generateAtlas = c.generateAtlas,
            preset = c.qualityPreset,
            thresholds = c.Thresholds,
            minDensity = c.minPixelDensity, maxDensity = c.maxPixelDensity,
            minPadding = c.minPadding,
            allowNPOT = c.allowNPOT,
            dedupTextures = c.dedupTextures, dedupMaterials = c.dedupMaterials,
            verbose = c.verboseLogging,
            mips = c.mipSettings, pc = c.pcOverride, android = c.androidOverride, ios = c.iosOverride,
            language = c.languageOverride,
        };
    }

    /// <summary>Animation-scan outcome per renderer node. / 动画扫描结果（逐渲染器）。</summary>
    internal sealed class RendererAnimState
    {
        internal bool disabledAlways = true;   // never active in any observed state / 所有状态均禁用
        internal Vector3 maxAnimScale = Vector3.one; // max abs scale incl. animation / 最大缩放（含动画）
    }

    /// <summary>
    /// Shared pipeline context passed across stages. Also hosts simple registries for extensions.<br/>
    /// 阶段间共享的流水线上下文，同时承载第三方扩展注册表。
    /// </summary>
    internal sealed class ATOPipeContext
    {
        internal ATOSettingsSnap settings;
        internal List<TextureInfo> textures = new List<TextureInfo>();
        internal List<PackingGroup> groups = new List<PackingGroup>();
        internal List<Island> islands = new List<Island>();
        internal List<AtlasDef> atlases = new List<AtlasDef>();
        internal readonly Dictionary<UVSlotKey, List<MaterialTextureRef>> slotRefs = new Dictionary<UVSlotKey, List<MaterialTextureRef>>();
        internal readonly Dictionary<Renderer, RendererAnimState> rendererStates = new Dictionary<Renderer, RendererAnimState>();
        internal readonly Dictionary<Material, Material> materialReplacements = new Dictionary<Material, Material>(); // orig -> new
        internal readonly Dictionary<Texture2D, Texture2D> textureReplacements = new Dictionary<Texture2D, Texture2D>();
        internal readonly Dictionary<Mesh, Mesh> meshReplacements = new Dictionary<Mesh, Mesh>();
        internal readonly Dictionary<Texture2D, TextureInfo> infoOf = new Dictionary<Texture2D, TextureInfo>();
        internal readonly Dictionary<UVSlotKey, bool> aaoChannelUsed = new Dictionary<UVSlotKey, bool>();   // AAO 使用该UV通道
        internal readonly Dictionary<UVSlotKey, int[]> slotTriangles = new Dictionary<UVSlotKey, int[]>();  // 槽三角形(子网格)
        internal readonly Dictionary<UVSlotKey, List<Island>> slotIslands = new Dictionary<UVSlotKey, List<Island>>();
        internal readonly Dictionary<TextureInfo, Vector2> wholeTextureScale = new Dictionary<TextureInfo, Vector2>(); // 非图集整图缩放
        internal readonly Dictionary<Island, IslandPlacement> islandPlacement = new Dictionary<Island, IslandPlacement>(); // 岛全局放置登记
        internal readonly Dictionary<(TextureInfo, TexClass), AtlasDef.PlaneOut> atlasPlaneOf = new Dictionary<(TextureInfo, TexClass), AtlasDef.PlaneOut>(); // 贴图→图集平面
        internal readonly Dictionary<TextureInfo, Texture2D> wholeTexReplacement = new Dictionary<TextureInfo, Texture2D>(); // 整图缩放置换
        internal readonly HashSet<UVSlotKey> skipSlots = new HashSet<UVSlotKey>(); // fallback：跳过重映射的槽（AAO疏散失败等）
        internal readonly HashSet<(TextureInfo, TexClass)> blockedTex = new HashSet<(TextureInfo, TexClass)>(); // 因槽跳过而禁止替换的(贴图,类型) / skip-latched replacement blocks
        internal readonly List<string> warnings = new List<string>(); // 汇总告警（报告用）

        internal void CancelCheck(StageProgress p, string label, float f)
        {
            if (p.Cancelled(label, f)) throw new OperationCanceledException(label);
        }
    }

    /// <summary>Progress display abstraction with cancellation. / 进度显示与取消。</summary>
    internal sealed class StageProgress
    {
        public string Title = "Avatar Texture Optimizer";
        public bool Cancelled(string label, float f)
        {
            return UnityEditor.EditorUtility.DisplayCancelableProgressBar(Title, label, Mathf.Clamp01(f));
        }
        public void Clear() => UnityEditor.EditorUtility.ClearProgressBar();
    }
}
