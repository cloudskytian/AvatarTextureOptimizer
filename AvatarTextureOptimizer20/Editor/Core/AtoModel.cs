// Central data model shared by all pipeline stages. / 全流水线共享的核心数据模型。
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace net.fosa.ato.editor
{
    /// <summary>Semantic role of a texture use. / 贴图用途角色。</summary>
    public enum TexRole
    {
        Color = 0,     // sRGB color (main/emission/matcap-like color content) / 颜色类
        Normal = 1,    // tangent-space normal map / 法线
        Gray = 2       // linear data / mask / grayscale, evaluated per used channel / 灰度蒙版
    }

    /// <summary>Alpha handling of a material referencing a texture. / 引用材质的透明模式。</summary>
    public enum AlphaMode { Opaque = 0, Cutout = 1, Blend = 2 }

    /// <summary>(Mesh, uv channel) is the unit whose islands must share one layout everywhere.
    /// (网格, UV通道) 是必须全局共享同一布局的最小单元。</summary>
    public readonly struct MappingKey : IEquatable<MappingKey>
    {
        public readonly Mesh Mesh;
        public readonly int Channel;
        public MappingKey(Mesh mesh, int channel) { Mesh = mesh; Channel = channel; }
        public bool Equals(MappingKey o) => Mesh == o.Mesh && Channel == o.Channel;
        public override bool Equals(object o) => o is MappingKey k && Equals(k);
        public override int GetHashCode() => (Mesh ? Mesh.GetInstanceID() : 0) * 397 ^ Channel;
        public override string ToString() => $"{(Mesh ? Mesh.name : "null")}:uv{Channel}";
    }

    /// <summary>One material-property use of a texture. / 贴图的一次材质属性引用。</summary>
    public class TexUse
    {
        public Material Material;
        public string Property;
        public TexRole Role;
        public int UvChannel;
        public AlphaMode Alpha;        // effective alpha mode / 有效透明模式
        public float Cutoff = 0.5f;
        public byte UsedChannels = 0xF; // bitmask RGBA for gray role / 灰度使用通道掩码
        public bool FromAnimation;     // discovered via animation swap / 来自动画切换
        public Renderer Renderer;      // slot owner (null when animation-only) / 材质槽宿主
        public int SlotIndex = -1;
    }

    /// <summary>Aggregated per-texture info. / 单张贴图的聚合信息。</summary>
    public class TexInfo
    {
        public Texture2D Tex;
        public bool SRGB;
        public TexRole Role = TexRole.Color;     // dominant role, strictest wins / 主导角色
        public byte UsedChannels;                // union over gray uses / 灰度通道并集
        public bool HasAlphaContent;             // actual alpha < 1 present / 实际存在透明像素
        public readonly List<TexUse> Uses = new List<TexUse>();
        public readonly HashSet<MappingKey> Mappings = new HashSet<MappingKey>();
        public readonly Dictionary<MappingKey, ulong> SubmeshMask = new Dictionary<MappingKey, ulong>();

        // Companion roles present anywhere this texture's materials also sample.
        // 该贴图所在材质是否伴随法线/蒙版（用于贴图类型组）。
        public bool CompanionNormal, CompanionMask;

        public bool Whitelisted;
        public string WhitelistReason;

        // Alpha evaluation requirements (strictest across all uses). / 最严苛透明评估要求。
        public bool AnyCutout, AnyBlend, AnyOpaqueUse;
        public readonly List<float> Cutoffs = new List<float>();

        public FilterMode Filter => Tex ? Tex.filterMode : FilterMode.Bilinear;

        // ---- results / 处理结果 ----
        public int PackUnitId = -1;
        public int AtlasIndex = -1;              // -1 = not atlased / 未图集化
        public float WholeScale = 1f;            // non-atlas whole-texture scale / 整图缩放系数
        public Texture2D Output;                 // atlas or rescaled texture / 输出贴图
        public Rect OutputUvRect = new Rect(0, 0, 1, 1);

        public string TypeGroupKey =>
            $"n{(CompanionNormal ? 1 : 0)}_m{(CompanionMask ? 1 : 0)}_s{(SRGB ? 1 : 0)}_f{(int)Filter}_r{(int)Role}";
    }

    /// <summary>A UV island (possibly merged overlapping islands). / UV 岛（可含合并的重叠岛）。</summary>
    public class Island
    {
        public MappingKey Key;
        public List<int> Triangles = new List<int>(); // triangle start indices into mesh index list
        public ulong SubmeshMask;                     // which submeshes contribute / 涉及的子网格
        public Vector2 BBoxMin, BBoxMax;              // normalized UV space, after normalization shift
        public Vector2 Shift;                         // applied wrap shift / 已应用的越界平移
        public float WorldAreaMax;                    // max world-space area (blendshape & anim scale) / 最大真实面积
        public float UvArea;                          // uv-space area / UV面积

        // per-texture quality result: chosen scale (x,y) relative to source pixels
        // 逐贴图质量结果：相对原始像素的缩放
        public readonly Dictionary<TexInfo, Vector2> Scale = new Dictionary<TexInfo, Vector2>();
        public Vector2 GroupScale = Vector2.one;      // barrel-effect final scale for group / 木桶效应后的组内最终缩放
        public bool IsSolid;                          // solid color short-circuit / 纯色短路
        public bool Skipped;                          // too small etc. / 忽略质量评估

        // Packing / 装箱
        public int PlacedAtlas = -1;
        public Vector2Int PlacePos;                   // pixel pos in atlas / 图集内像素位置
        public bool Rotated;                          // rotated 90° / 旋转90度
        public Vector2Int RasterSize;                 // final pixel size in atlas / 图集内像素尺寸
        public Vector2Int SrcPixelMin, SrcPixelSize;  // source pixel rect (per widest texture) / 源像素矩形
    }

    /// <summary>Atomic packing unit: textures connected via shared mappings. / 原子装箱单元。</summary>
    public class PackUnit
    {
        public int Id;
        public string TypeGroupKey;
        public readonly List<TexInfo> Textures = new List<TexInfo>();
        public readonly List<MappingKey> Mappings = new List<MappingKey>();
        public readonly List<Island> Islands = new List<Island>();
        public long RasterArea; // sum of island raster areas / 光栅化总面积
        public bool GaveUp;     // could not fit largest atlas / 无法装入最大图集
        public Vector2Int AtlasSize; // committed atlas size / 已确定的图集尺寸
    }

    /// <summary>One produced atlas (per type-group per role). / 生成的一张图集。</summary>
    public class AtlasResult
    {
        public string Name;
        public TexRole Role;
        public bool SRGB;
        public bool HasAlpha;
        public int Width, Height;
        public Texture2D Texture;
        public readonly List<TexInfo> Sources = new List<TexInfo>();
        public long UsedPixels;
        public float Utilization => Width * Height == 0 ? 0 : (float)UsedPixels / (Width * (long)Height);
    }

    /// <summary>Per-renderer scan record. / 渲染器扫描记录。</summary>
    public class RendererInfo
    {
        public Renderer Renderer;
        public Mesh Mesh;
        public bool ActiveOrAnimated;
        public readonly List<Material[]> MaterialVariants = new List<Material[]>(); // slot arrays incl. animation variants
        public float MaxAnimScale = 1f;      // max animated scale factor / 动画最大缩放
        public float BlendshapeAreaFactor = 1f; // max blendshape area inflation / 形态键面积放大

        // Animation-driven material property findings. / 动画对材质属性的影响。
        public bool AnimatedStUnsafe;            // animated ST/scroll/angle -> unsafe / 动画UV变换
        public string AnimatedStProperty;
        public bool AnimatedAlphaModeChanges;    // rendering mode animated / 动画改渲染模式
        public readonly HashSet<float> AnimatedCutoffs = new HashSet<float>(); // animated cutoff values
    }

    /// <summary>Statistics for the final report. / 最终报告统计。</summary>
    public class AtoStats
    {
        public readonly List<(string label, long ms)> StageTimes = new List<(string, long)>();
        public readonly List<string> Details = new List<string>();
        public readonly List<AtlasResult> Atlases = new List<AtlasResult>();
        public int TexturesSeen, TexturesDeduped, TexturesWhitelisted, TexturesAtlased, TexturesScaled;
        public int IslandCount, MaterialsCloned, MaterialsMerged, MeshesRewritten;
        public long OriginalPixels, FinalPixels;
        public bool Cancelled;
    }

    /// <summary>Shared bake context. / 共享烘焙上下文。</summary>
    public class AtoContext : IDisposable
    {
        public nadena.dev.ndmf.BuildContext Ndmf;
        public AvatarTextureOptimizer Settings;
        public AtoQualityParams Quality;
        public AtoPlatform Platform;
        public AtoPlatformOverride PlatformOverride;

        public readonly List<RendererInfo> Renderers = new List<RendererInfo>();
        public readonly Dictionary<Texture2D, TexInfo> Textures = new Dictionary<Texture2D, TexInfo>();
        public readonly Dictionary<Texture2D, Texture2D> DedupMap = new Dictionary<Texture2D, Texture2D>();
        public readonly Dictionary<MappingKey, List<Island>> Islands = new Dictionary<MappingKey, List<Island>>();
        public readonly Dictionary<MappingKey, List<TexInfo>> MappingTextures = new Dictionary<MappingKey, List<TexInfo>>();
        public readonly List<PackUnit> PackUnits = new List<PackUnit>();
        public readonly List<AtlasResult> Atlases = new List<AtlasResult>();
        public readonly HashSet<UnityEngine.Object> WhitelistObjects = new HashSet<UnityEngine.Object>();
        public readonly HashSet<Texture2D> WhitelistTextures = new HashSet<Texture2D>();
        public readonly List<AnimationClip> Clips = new List<AnimationClip>();
        public readonly AtoStats Stats = new AtoStats();

        public TexturePixels Pixels;   // decoded pixel cache / 解码像素缓存

        public int MaxAtlasSize => Platform == AtoPlatform.PC ? 8192 : 4096;

        public TexInfo GetOrAddTex(Texture2D t)
        {
            if (!Textures.TryGetValue(t, out var info))
            {
                info = new TexInfo { Tex = t, SRGB = GraphicsFormatUtilityIsSrgb(t) };
                Textures[t] = info;
            }
            return info;
        }

        private static bool GraphicsFormatUtilityIsSrgb(Texture2D t)
        {
            try { return UnityEngine.Experimental.Rendering.GraphicsFormatUtility.IsSRGBFormat(t.graphicsFormat); }
            catch { return true; }
        }

        public void Dispose()
        {
            Pixels?.Dispose();
            Pixels = null;
        }
    }
}
