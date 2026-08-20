using System.Collections.Generic;
using UnityEngine;

namespace Fosa.Ato.Editor.Pipeline
{
    /// <summary>Identifies a specific material slot on a specific renderer. / 标识某渲染器上的某个材质槽。</summary>
    internal readonly struct MaterialSlotRef
    {
        public readonly Renderer Renderer;
        public readonly int SlotIndex;
        public MaterialSlotRef(Renderer r, int i) { Renderer = r; SlotIndex = i; }
        public override int GetHashCode() => (Renderer?.GetHashCode() ?? 0) * 397 ^ SlotIndex;
        public override bool Equals(object o) => o is MaterialSlotRef s && s.Renderer == Renderer && s.SlotIndex == SlotIndex;
    }

    /// <summary>A mesh UV channel reference (channel 0..7, treated as independent UV sets). / 一个网格 UV 通道。</summary>
    internal readonly struct UvChannelRef
    {
        public readonly Mesh Mesh;
        public readonly int Channel;
        public readonly int SubMesh;
        public UvChannelRef(Mesh m, int ch, int sub) { Mesh = m; Channel = ch; SubMesh = sub; }
    }

    /// <summary>How a texture uses alpha / 贴图的透明模式。</summary>
    internal enum TexAlphaMode { Opaque, Blend, Cutout }

    /// <summary>
    /// A single texture usage: a Texture2D plus how it is used (type, color space, filter, alpha,
    /// cutoff, channels used). Import settings differ => different TextureUsage even for same pixels.
    /// 单个贴图使用记录：贴图本身 + 使用方式（类型、色彩空间、filter、透明模式、cutoff、使用通道）。
    /// 导入设置不同即视为不同的 TextureUsage（即使像素相同）。
    /// </summary>
    internal sealed class TextureUsage
    {
        public Texture2D Texture;
        public int ImportHash;       // hash of relevant import settings / 导入设置哈希
        public TextureKind Kind;
        public bool SRGB;
        public FilterMode Filter;
        public TexAlphaMode Alpha;
        public float Cutoff = 0.5f;
        public int ChannelsUsedMask = 0b1111; // which channels actually hold data / 实际含数据的通道
        public bool HasAlphaChannel;
        // Transparency requirements across all referencing materials take the strictest.
        // 跨所有引用材质取最严格要求
        public bool Whitelisted;      // fully skipped (referenced by whitelist) / 完全跳过
        public bool AtlasAllowed = true; // may be atlased; false => scale-only / 允许图集化
        public string ShaderPropertyName;

        public string Key => $"{Texture?.GetInstanceID()}:{ImportHash}:{ShaderPropertyName}";
    }

    /// <summary>A triangle cluster (UV island) within a source texture. / 源贴图内的一个三角形簇（UV 岛）。</summary>
    internal sealed class Island
    {
        public int Id;
        public Texture2D SourceTexture;
        public TextureUsage SourceUsage;
        public UvChannelRef Uv;
        public List<int> Triangles = new();
        public Rect UvBox;            // UV-space bbox / UV 包围盒
        public Vector2 SizePx;        // size in source texture pixels / 源贴图上的像素尺寸
        public float WorldArea;       // world-space area (max over blendshapes & anim scale) / 世界面积
        public bool OverlapsOther;    // merged from overlapping islands / 与其他岛重叠并已合并
        public Vector2 TargetSizePx;  // after quality+density clamp / 质量+密度钳制后的目标尺寸
        public Matrix2x3 UvToPx;      // UV -> source pixel transform (for rasterization) / UV 到像素变换
        public bool SolidColor;       // detected solid/near-solid / 纯色或近纯色
        public bool IsAnimated;
    }

    /// <summary>
    /// A UV group: all islands that share the same UV identity across all maps in a type group
    /// (main+normal+mask+animation-switch maps). They MUST land at identical positions in every
    /// atlas of the group to prevent cross-map misalignment. The resize bucket is the max required
    /// size across maps (wooden bucket), capped at the group's largest original size.
    /// UV 组：在类型组内共享同一 UV 身份的所有岛（主色+法线+蒙版+动画切换贴图）。它们在组内每个图集
    /// 上的位置必须完全一致，防止错位；尺寸取所有贴图中的最大需求（木桶效应），不超过组内最大原尺寸。
    /// </summary>
    internal sealed class UvGroup
    {
        public int Id;
        public List<Island> Islands = new();
        public List<TextureUsage> Maps = new();
        public Vector2 BucketSizePx;       // chosen target / 选定目标尺寸
        public Vector2 MaxOriginalSizePx;  // upper bound / 上限
        public TextureKind DominantKind;
    }

    /// <summary>
    /// A texture type group keyed by (special-map presence signature, colorSpace, filterMode).
    /// Textures in the same group share atlas(es) and UV alignment. If a sub-map type (e.g. masks)
    /// in the group has a lower overall quality need, its atlas may be scaled down past min padding.
    /// 贴图类型组：按（特殊贴图存在特征、色彩空间、filterMode）分组。同组共享图集与 UV 对齐。
    /// 若组内某类贴图（如蒙版）整体质量需求更低，其图集可在满足最小 padding 下缩小。
    /// </summary>
    internal sealed class TypeGroup
    {
        public int Signature;
        public bool SRGB;
        public FilterMode Filter;
        public List<TextureUsage> Textures = new();
        public List<UvGroup> UvGroups = new();
        public bool HasNormal;
        public bool HasMask;
        public bool HasEmission;
    }

    /// <summary>A placed island in atlas pixel space. / 图集中放置好的岛。</summary>
    internal struct PlacedIsland
    {
        public Island Island;
        public UvGroup Group;
        public RectInt PixelRect;   // in atlas coordinates / 图集像素坐标
        public bool Rotated;        // 90° rotation (bitmask transpose) / 旋转 90°
    }

    /// <summary>A produced atlas (or scaled standalone fallback). / 产出的图集（或缩放后的独立贴图）。</summary>
    internal sealed class AtlasResult
    {
        public string Name;
        public int Width, Height;
        public Texture2D Texture;
        public List<PlacedIsland> Placements = new();
        public TypeGroup Group;
        public TextureKind Kind;
        public float Utilization;
        public long SourceBytes;
        public long OutputBytes;
        public bool FallbackStandalone; // not atlased, just scaled / 未图集化，仅缩放
    }

    // Minimal 2x3 matrix (2D affine) for UV<->pixel mapping / 简易 2x3 仿射矩阵
    internal struct Matrix2x3
    {
        public float m00, m01, m02, m10, m11, m12;
        public Vector2 Transform(Vector2 v) =>
            new(m00 * v.x + m01 * v.y + m02, m10 * v.x + m11 * v.y + m12);
        public static Matrix2x3 Identity => new() { m00 = 1, m11 = 1 };
    }
}
