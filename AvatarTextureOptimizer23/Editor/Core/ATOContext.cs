using System;
using System.Collections.Generic;
using nadena.dev.ndmf;
using UnityEngine;
using UnityEngine.Rendering;
using FOSA.AvatarTextureOptimizer;

namespace FOSA.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Shared bake state. Lives in BuildContext.GetState and is disposed at the end of the pass.
    /// 烘焙共享状态。放在 BuildContext.GetState 里，pass 结束时释放。
    /// </summary>
    internal sealed class ATOContext : IDisposable
    {
        public BuildContext Build;
        public AvatarTextureOptimizer Component;
        public ATOResolvedSettings Settings;
        public ATOLog Log = new ATOLog();
        public ATOProgress Progress;
        public ATOReportData Report = new ATOReportData();

        public readonly HashSet<Texture2D> WhitelistedTextures = new HashSet<Texture2D>();
        public readonly HashSet<Texture2D> SkipAtlasTextures = new HashSet<Texture2D>();
        public readonly List<ATORendererInfo> Renderers = new List<ATORendererInfo>();
        public readonly List<ATOTextureUse> Uses = new List<ATOTextureUse>();
        public readonly Dictionary<Texture2D, Texture2D> TextureRemap = new Dictionary<Texture2D, Texture2D>();
        public readonly Dictionary<Material, Material> MaterialRemap = new Dictionary<Material, Material>();
        public readonly Dictionary<Mesh, Mesh> MeshRemap = new Dictionary<Mesh, Mesh>();
        public readonly List<ATOUvGroup> UvGroups = new List<ATOUvGroup>();
        public readonly List<ATOTypeGroup> TypeGroups = new List<ATOTypeGroup>();
        public readonly List<string> Warnings = new List<string>();
        public readonly Dictionary<Texture2D, ATODecodedTexture> DecodeCache = new Dictionary<Texture2D, ATODecodedTexture>();

        public string TempFolder;
        public bool Canceled;

        public void Dispose()
        {
            foreach (var kv in DecodeCache)
            {
                kv.Value?.Dispose();
            }
            DecodeCache.Clear();
            ATOGpuUtil.ReleaseAll();
        }

        public void WarnWhitelist(Texture2D tex, string reason)
        {
            if (tex == null) return;
            WhitelistedTextures.Add(tex);
            var msg = $"Whitelist {tex.name}: {reason}";
            Warnings.Add(msg);
            Log.Warn(msg);
            ATOLoc.Report(ErrorSeverity.Information, "ato.warn.whitelist", tex, reason);
        }
    }

    internal sealed class ATORendererInfo
    {
        public Renderer Renderer;
        public Mesh Mesh;
        public bool IsSkinned;
        public bool EnabledNow;
        public bool EnabledByAnimation;
        public float MaxWorldScale = 1f;
        public Material[] SharedMaterials;
        public List<ATOIsland> Islands = new List<ATOIsland>();
    }

    internal sealed class ATOTextureUse
    {
        public ATOTextureSlotInfo Slot;
        public ATORendererInfo Renderer;
        public int UvGroupId = -1;
        public int TypeGroupId = -1;
    }

    /// <summary>
    /// One UV island on one mesh / submesh / UV channel. Shape comes from the mesh, not the texture.
    /// 某一个网格/子网格/UV 通道上的一个岛。形状来自网格，不是贴图。
    /// </summary>
    internal sealed class ATOIsland
    {
        public int Id;
        public ATORendererInfo Renderer;
        public int Submesh;
        public int UvChannel;
        public int[] TriangleIndices; // indices into mesh triangles (index-of-index / 3)
        public Vector2 UvMin;
        public Vector2 UvMax;
        public Vector2 UvSize => UvMax - UvMin;
        public float WorldArea;
        public float WorldShortSide;
        public bool OverlapsMerged;

        public int OriginalPixelW;
        public int OriginalPixelH;
        public int ScaledW;
        public int ScaledH;
        public bool SolidColor;
        public Color Solid;

        public ATOBitmask Mask;
        public int PackedX;
        public int PackedY;
        public bool Rotated;
        public bool Packed;
    }

    internal sealed class ATOUvGroup
    {
        public int Id;
        public readonly HashSet<Texture2D> Textures = new HashSet<Texture2D>();
        public readonly List<ATOIsland> Islands = new List<ATOIsland>();
        public readonly List<ATOTextureUse> Uses = new List<ATOTextureUse>();
        public bool SkipAtlas;
        public bool HasAlternates;
        public Vector2Int LayoutSize;
        public string FailReason;
    }

    internal sealed class ATOTypeGroup
    {
        public int Id;
        public ATOTypeKey Key;
        public readonly List<ATOUvGroup> UvGroups = new List<ATOUvGroup>();
        public readonly List<ATOAtlasResult> Atlases = new List<ATOAtlasResult>();
    }

    internal struct ATOTypeKey : IEquatable<ATOTypeKey>
    {
        public bool HasNormal;
        public bool HasMask;
        public ColorSpace ColorSpace;
        public FilterMode Filter;

        public bool Equals(ATOTypeKey other)
        {
            return HasNormal == other.HasNormal && HasMask == other.HasMask &&
                   ColorSpace == other.ColorSpace && Filter == other.Filter;
        }

        public override bool Equals(object obj) => obj is ATOTypeKey other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                var h = HasNormal.GetHashCode();
                h = (h * 397) ^ HasMask.GetHashCode();
                h = (h * 397) ^ (int)ColorSpace;
                h = (h * 397) ^ (int)Filter;
                return h;
            }
        }

        public override string ToString() =>
            $"N={(HasNormal ? 1 : 0)} M={(HasMask ? 1 : 0)} CS={ColorSpace} F={Filter}";
    }

    internal sealed class ATOAtlasResult
    {
        public Texture2D Source;
        public Texture2D Atlas;
        public ATOTextureCategory Category;
        public int Width;
        public int Height;
        public int Padding;
        public float Utilization;
        public int IslandCount;
        public long OriginalBytes;
        public long AtlasBytes;
        public bool FallbackNoAtlas;
        public string Name;
    }

    internal sealed class ATODecodedTexture : IDisposable
    {
        public Texture2D Source;
        public int Width;
        public int Height;
        public Color[] Pixels;
        public bool Linear;
        public bool HasAlpha;
        public bool IsNormal;
        public bool Disposed;

        public void Dispose()
        {
            Pixels = null;
            Disposed = true;
        }
    }

    internal sealed class ATOReportData
    {
        public int RendererCount;
        public int TextureIn;
        public int TextureOut;
        public int IslandCount;
        public int AtlasCount;
        public int WhitelistCount;
        public int SkippedAtlas;
        public long BytesIn;
        public long BytesOut;
        public double TotalMs;
        public readonly List<string> AtlasLines = new List<string>();
        public readonly List<string> Warnings = new List<string>();
    }

    internal struct ATOBitmask : IDisposable
    {
        public int Width;
        public int Height;
        public ulong[] Bits; // row-major, 64 bits per ulong
        public int Stride;   // ulongs per row

        public static ATOBitmask Allocate(int w, int h)
        {
            var stride = (w + 63) >> 6;
            return new ATOBitmask
            {
                Width = w,
                Height = h,
                Stride = stride,
                Bits = new ulong[stride * h]
            };
        }

        public bool this[int x, int y]
        {
            get
            {
                if ((uint)x >= (uint)Width || (uint)y >= (uint)Height) return false;
                var i = y * Stride + (x >> 6);
                return (Bits[i] & (1UL << (x & 63))) != 0;
            }
            set
            {
                if ((uint)x >= (uint)Width || (uint)y >= (uint)Height) return;
                var i = y * Stride + (x >> 6);
                var bit = 1UL << (x & 63);
                if (value) Bits[i] |= bit;
                else Bits[i] &= ~bit;
            }
        }

        public int PopCount()
        {
            var n = 0;
            if (Bits == null) return 0;
            for (int i = 0; i < Bits.Length; i++)
                n += CountBits(Bits[i]);
            return n;
        }

        public ATOBitmask Transpose()
        {
            var t = Allocate(Height, Width);
            for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                if (this[x, y]) t[y, x] = true;
            }
            return t;
        }

        public void Dispose()
        {
            Bits = null;
        }

        private static int CountBits(ulong v)
        {
            v -= (v >> 1) & 0x5555555555555555UL;
            v = (v & 0x3333333333333333UL) + ((v >> 2) & 0x3333333333333333UL);
            v = (v + (v >> 4)) & 0x0F0F0F0F0F0F0F0FUL;
            return (int)((v * 0x0101010101010101UL) >> 56);
        }
    }
}
