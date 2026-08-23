using System;
using System.Collections.Generic;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Editor.Core
{
    /// <summary>EN: Material transparency modes relevant to alpha quality. ZH: 与 Alpha 质量相关的材质透明模式。</summary>
    internal enum AlphaMode { Opaque, Cutout, Blend }

    /// <summary>EN: One alpha evaluation constraint. ZH: 一条 Alpha 评估约束。</summary>
    internal readonly struct AlphaConstraint
    {
        public readonly AlphaMode Mode;
        public readonly float Cutoff;
        public AlphaConstraint(AlphaMode mode, float cutoff) { Mode = mode; Cutoff = cutoff; }
    }

    /// <summary>EN: One conservative material/texture reference. ZH: 一条保守的材质/贴图引用记录。</summary>
    internal sealed class TextureUsage
    {
        public Material Material;
        public string PropertyName;
        public Texture2D Texture;
        public TextureSemantic Semantic;
        public int UvChannel;
        public int UsedChannelMask = 0xF;
        public readonly List<AlphaConstraint> AlphaConstraints = new List<AlphaConstraint>();
        public bool IsSrgb;
        public FilterMode FilterMode;
        public bool IsAnimated;
        public bool Protected;
        public string UnsafeReason;
        public readonly HashSet<Renderer> Renderers = new HashSet<Renderer>();

        public bool Safe => string.IsNullOrEmpty(UnsafeReason);
    }

    /// <summary>EN: A material that may be assigned statically or by animation. ZH: 可由静态或动画赋值的材质。</summary>
    internal sealed class MaterialRecord
    {
        public Material Original;
        public Material Working;
        public readonly List<TextureUsage> Usages = new List<TextureUsage>();
        public readonly HashSet<RendererSlot> Slots = new HashSet<RendererSlot>();
        public bool Whitelisted;
    }

    /// <summary>EN: Stable renderer material-slot identity. ZH: 稳定的 Renderer 材质槽标识。</summary>
    internal readonly struct RendererSlot : IEquatable<RendererSlot>
    {
        public readonly Renderer Renderer;
        public readonly int Slot;
        public RendererSlot(Renderer renderer, int slot) { Renderer = renderer; Slot = slot; }
        public bool Equals(RendererSlot other) => Renderer == other.Renderer && Slot == other.Slot;
        public override bool Equals(object obj) => obj is RendererSlot other && Equals(other);
        public override int GetHashCode() => ((Renderer != null ? Renderer.GetInstanceID() : 0) * 397) ^ Slot;
        public override string ToString() => $"{(Renderer != null ? Renderer.name : "<null>")}[{Slot}]";
    }

    /// <summary>EN: One analyzed renderer and all materials reachable per slot. ZH: 一个已分析 Renderer 及每槽位可达的全部材质。</summary>
    internal sealed class RendererRecord
    {
        public Renderer Renderer;
        public Mesh SourceMesh;
        public Mesh WorkingMesh;
        public float MaximumAreaScale = 1f;
        public readonly Dictionary<int, HashSet<Material>> PossibleMaterials = new Dictionary<int, HashSet<Material>>();
    }

    /// <summary>EN: One triangle belonging to a UV island. ZH: 属于某 UV 岛的一个三角形。</summary>
    internal readonly struct IslandTriangle
    {
        public readonly int A;
        public readonly int B;
        public readonly int C;
        public IslandTriangle(int a, int b, int c) { A = a; B = b; C = c; }
    }

    /// <summary>EN: Connected, overlap-merged UV island. ZH: 连通且已合并重叠关系的 UV 岛。</summary>
    internal sealed class UvIsland
    {
        public int Id;
        public int UvGroupId;
        public readonly List<IslandTriangle> Triangles = new List<IslandTriangle>();
        public Rect UvBounds;
        public Rect NormalizedBounds;
        public Vector2 IntegerTranslation;
        public Vector2 Scale = Vector2.one;
        public Vector2Int SourcePixelSize;
        public Vector2Int MinimumDensityPixelSize;
        public Vector2Int MaximumDensityPixelSize;
        public Vector2Int TargetPixelSize;
        public bool IsPureColor;
        public Color PureColor;
        public RasterMask Raster;
        public AtlasPlacement Placement;
        public float ModelArea;
    }

    /// <summary>EN: All textures that must observe identical UV coordinates. ZH: 必须使用完全相同 UV 坐标的全部贴图集合。</summary>
    internal sealed class UvGroup
    {
        public int Id;
        public RendererRecord Renderer;
        public int SubMesh;
        public int UvChannel;
        public readonly HashSet<Material> Materials = new HashSet<Material>();
        public readonly List<TextureUsage> Usages = new List<TextureUsage>();
        public readonly List<UvIsland> Islands = new List<UvIsland>();
        public Vector2 IntegerTranslation;
        public bool Whitelisted;
        public string FallbackReason;
    }

    /// <summary>EN: Four-pixel rasterized island silhouette. ZH: 以四像素粒度光栅化的岛轮廓。</summary>
    internal sealed class RasterMask
    {
        public int Width;
        public int Height;
        public int Stride;
        public ulong[] Rows;
        public int SetBitCount;
        public RasterMask Rotated;
    }

    /// <summary>EN: Final shape-packing placement. ZH: 最终形状装箱落点。</summary>
    internal readonly struct AtlasPlacement
    {
        public readonly int AtlasIndex;
        public readonly int X;
        public readonly int Y;
        public readonly bool Rotated;
        public readonly int PixelWidth;
        public readonly int PixelHeight;
        public AtlasPlacement(int atlasIndex, int x, int y, bool rotated, int pixelWidth, int pixelHeight)
        { AtlasIndex = atlasIndex; X = x; Y = y; Rotated = rotated; PixelWidth = pixelWidth; PixelHeight = pixelHeight; }
    }

    /// <summary>EN: A same-sampler/same-special-map-signature atlas family. ZH: 采样器和特殊贴图签名一致的图集族。</summary>
    internal sealed class TextureTypeGroup
    {
        public string Key;
        public readonly List<UvGroup> UvGroups = new List<UvGroup>();
        public readonly List<List<UvGroup>> PackingAtoms = new List<List<UvGroup>>();
        public readonly List<AtlasLayout> Layouts = new List<AtlasLayout>();
    }

    /// <summary>EN: One generated replacement layer with context needed for animated curves. ZH: 带动画曲线上下文的一层生成替换贴图。</summary>
    internal sealed class AnimatedLayerMapping
    {
        public Texture2D Source;
        public string PropertyName;
        public readonly HashSet<Renderer> Renderers = new HashSet<Renderer>();
    }

    internal sealed class GeneratedTextureLayer
    {
        public Texture2D Output;
        public string PropertyName;
        public TextureSemantic Semantic;
        public int AtlasIndex;
        public TextureTypeGroup TypeGroup;
        public readonly HashSet<Texture2D> Sources = new HashSet<Texture2D>();
        public readonly Dictionary<Material, HashSet<string>> AssignedProperties = new Dictionary<Material, HashSet<string>>();
        public readonly List<AnimatedLayerMapping> AnimatedMappings = new List<AnimatedLayerMapping>();
        public readonly HashSet<Renderer> Renderers = new HashSet<Renderer>();
    }

    /// <summary>EN: Shared dimensions and placements across every semantic atlas in a family. ZH: 类型组内所有语义图集共享的尺寸与落点。</summary>
    internal sealed class AtlasLayout
    {
        public int Index;
        public int Width;
        public int Height;
        public int Padding;
        public readonly List<UvIsland> Islands = new List<UvIsland>();
        public long OccupiedRasterPixels;
        public float Utilization => Width <= 0 || Height <= 0 ? 0f : (float)OccupiedRasterPixels / (Width * Height);
    }

    /// <summary>EN: Immutable analysis output consumed by processing stages. ZH: 供处理阶段使用的不可变分析输出。</summary>
    internal sealed class BuildPlan
    {
        public Fosa.AvatarTextureOptimizer.AvatarTextureOptimizer Component;
        public OptimizerPlatform Platform;
        public PlatformProfile Profile;
        public readonly List<RendererRecord> Renderers = new List<RendererRecord>();
        public readonly Dictionary<Material, MaterialRecord> Materials = new Dictionary<Material, MaterialRecord>();
        public readonly List<UvGroup> UvGroups = new List<UvGroup>();
        public readonly List<TextureTypeGroup> TypeGroups = new List<TextureTypeGroup>();
        public readonly Dictionary<Texture2D, Texture2D> TextureReplacements = new Dictionary<Texture2D, Texture2D>();
        public readonly List<GeneratedTextureLayer> GeneratedLayers = new List<GeneratedTextureLayer>();
        public readonly Dictionary<Material, Material> MaterialReplacements = new Dictionary<Material, Material>();
        public readonly HashSet<Texture2D> ProtectedTextures = new HashSet<Texture2D>();
        public readonly Dictionary<(SkinnedMeshRenderer renderer, int originalChannel), int> AaoEvacuations =
            new Dictionary<(SkinnedMeshRenderer, int), int>();
        public readonly HashSet<Renderer> AaoBlockedRenderers = new HashSet<Renderer>();
    }
}
