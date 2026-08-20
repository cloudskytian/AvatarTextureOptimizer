using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// A renderer we may process. / 可能处理的渲染器。
    /// </summary>
    public sealed class RendererRef
    {
        public Renderer Renderer;
        public Mesh Mesh;
        public bool IsSkinned;
        public Material[] SharedMaterials;
        public int UvChannelCount;
        public float MaxScaleMul = 1f;
        public bool EnabledOrAnimatedOn;
        public string Path = "";
    }

    /// <summary>
    /// One material slot on a renderer, possibly with animation swaps. / 渲染器上的一个材质槽（含动画切换）。
    /// </summary>
    public sealed class MaterialSlotRef
    {
        public RendererRef Owner;
        public int SlotIndex;
        public readonly List<Material> Materials = new List<Material>();
        public bool SlotAnimatedIndependently;
    }

    /// <summary>
    /// Texture binding from a material slot + property onto a mesh UV channel.
    /// 材质槽+属性到网格 UV 通道的贴图绑定。
    /// </summary>
    public sealed class TextureBinding
    {
        public Texture2D Texture;
        public Material Material;
        public RendererRef Owner;
        public int SlotIndex;
        public string PropertyName;
        public TextureUsageKind Usage;
        public int UvChannel;
        public AlphaEvalMode AlphaMode;
        public float Cutoff;
        public ColorSpace ColorSpace;
        public FilterMode FilterMode;
        public bool IsWhitelisted;
        public bool SkipAtlas;
        public bool Eligible = true;
        public string IneligibleReason;
    }

    /// <summary>
    /// Connected UV island on one mesh UV channel. / 单个网格 UV 通道上的连通 UV 岛。
    /// </summary>
    public sealed class UvIsland
    {
        public Mesh Mesh;
        public RendererRef Owner;
        public int UvChannel;
        public int Submesh;
        public List<int> VertexIndices = new List<int>();
        public List<int> TriangleIndices = new List<int>();
        public Vector2 UvMin;
        public Vector2 UvMax;
        public Vector2 UvTranslate; // integer-ish wrap normalize
        public float WorldArea;
        public float UvArea;
        public int OrigPixelW;
        public int OrigPixelH;
        public bool SolidColor;
        public bool Anisotropic;
        public Vector2 Scale = Vector2.one; // quality scale relative to original island px
        public int PackedX, PackedY, PackedW, PackedH;
        public bool Rotated90;
        public int AtlasId = -1;
        /// <summary>4px bitmask of the island shape in original texture space (cropped bbox). / 原贴图空间裁剪包围盒的 4px 位掩码。</summary>
        public Bitmask2D Shape;
        public Color32 SolidColorValue;
    }

    /// <summary>
    /// Bitmask at 4px granularity. / 4px 粒度位掩码。
    /// </summary>
    public sealed class Bitmask2D
    {
        public int Width;
        public int Height;
        public ulong[] Words; // row-major, 64-bit words along X

        public int WordsPerRow => (Width + 63) / 64;

        public static Bitmask2D Create(int w, int h)
        {
            var m = new Bitmask2D { Width = Math.Max(1, w), Height = Math.Max(1, h) };
            m.Words = new ulong[m.WordsPerRow * m.Height];
            return m;
        }

        public void Set(int x, int y)
        {
            if ((uint)x >= (uint)Width || (uint)y >= (uint)Height) return;
            var row = y * WordsPerRow;
            Words[row + (x >> 6)] |= 1UL << (x & 63);
        }

        public bool Get(int x, int y)
        {
            if ((uint)x >= (uint)Width || (uint)y >= (uint)Height) return false;
            var row = y * WordsPerRow;
            return (Words[row + (x >> 6)] & (1UL << (x & 63))) != 0;
        }

        public int CountBits()
        {
            int n = 0;
            for (int i = 0; i < Words.Length; i++) n += PopCount(Words[i]);
            return n;
        }

        public Bitmask2D Rotated90()
        {
            // Transpose + reverse rows = 90° CW. / 转置并反向行 = 顺时针 90°。
            var r = Create(Height, Width);
            for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                if (Get(x, y)) r.Set(Height - 1 - y, x);
            }
            return r;
        }

        public static int PopCount(ulong v)
        {
            v -= (v >> 1) & 0x5555555555555555UL;
            v = (v & 0x3333333333333333UL) + ((v >> 2) & 0x3333333333333333UL);
            v = (v + (v >> 4)) & 0x0F0F0F0F0F0F0F0FUL;
            return (int)((v * 0x0101010101010101UL) >> 56);
        }
    }

    /// <summary>
    /// Textures that must share UV layout. / 必须共享 UV 布局的贴图集合。
    /// </summary>
    public sealed class UvGroup
    {
        public int Id;
        public readonly HashSet<Texture2D> Textures = new HashSet<Texture2D>();
        public readonly List<UvIsland> Islands = new List<UvIsland>();
        public readonly List<TextureBinding> Bindings = new List<TextureBinding>();
        public bool SkipAtlas;
        public bool Whitelisted;
    }

    /// <summary>
    /// Type group key: companions + color space + filter. / 类型组键：伴随贴图 + 色彩空间 + filter。
    /// </summary>
    public struct TypeGroupKey : IEquatable<TypeGroupKey>
    {
        public bool HasNormal;
        public bool HasMask;
        public ColorSpace ColorSpace;
        public FilterMode Filter;
        public TextureUsageKind PrimaryUsage;

        public bool Equals(TypeGroupKey other) =>
            HasNormal == other.HasNormal && HasMask == other.HasMask &&
            ColorSpace == other.ColorSpace && Filter == other.Filter &&
            PrimaryUsage == other.PrimaryUsage;

        public override bool Equals(object obj) => obj is TypeGroupKey o && Equals(o);
        public override int GetHashCode() => HashCode.Combine(HasNormal, HasMask, (int)ColorSpace, (int)Filter, (int)PrimaryUsage);
        public override string ToString() => $"{PrimaryUsage}|n={HasNormal}|m={HasMask}|{ColorSpace}|{Filter}";
    }

    public sealed class TypeGroup
    {
        public TypeGroupKey Key;
        public readonly List<Texture2D> Textures = new List<Texture2D>();
        public readonly List<UvGroup> UvGroups = new List<UvGroup>();
    }

    public sealed class AtlasResult
    {
        public int Id;
        public int Width, Height;
        public Texture2D Texture;
        public TypeGroupKey Key;
        public readonly List<UvIsland> Islands = new List<UvIsland>();
        public readonly List<Texture2D> Sources = new List<Texture2D>();
        public float Utilization;
        public bool HasAlpha;
        public TextureUsageKind Usage;
        public FilterMode Filter;
        public ColorSpace ColorSpace;
    }

    public sealed class AtoReportData
    {
        public int SourceTextures;
        public int OutputTextures;
        public int AtlasCount;
        public int IslandCount;
        public long VramBefore;
        public long VramAfter;
        public readonly List<string> Details = new List<string>();
        public readonly List<string> Warnings = new List<string>();
        public readonly Dictionary<string, long> StageMs = new Dictionary<string, long>();
    }
}
