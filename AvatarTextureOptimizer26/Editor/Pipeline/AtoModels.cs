using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Net.Fosa.AvatarTextureOptimizer;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    public readonly struct AtoUvKey : IEquatable<AtoUvKey>
    {
        public readonly Renderer Renderer;
        public readonly int Submesh;
        public readonly int UvChannel;

        public AtoUvKey(Renderer renderer, int submesh, int uvChannel)
        {
            Renderer = renderer;
            Submesh = submesh;
            UvChannel = uvChannel;
        }

        public bool Equals(AtoUvKey other) =>
            Renderer == other.Renderer && Submesh == other.Submesh && UvChannel == other.UvChannel;

        public override bool Equals(object obj) => obj is AtoUvKey o && Equals(o);
        public override int GetHashCode() => HashCode.Combine(Renderer, Submesh, UvChannel);
        public override string ToString() =>
            $"{(Renderer != null ? Renderer.name : "null")}[{Submesh}].uv{UvChannel}";
    }

    public sealed class AtoTextureUse
    {
        public Texture2D Texture;
        public Material Material;
        public string Property;
        public AtoTextureKind Kind;
        public AtoAlphaMode AlphaMode;
        public float Cutoff;
        public bool IsSrgb;
        public FilterMode Filter;
        public int UvChannel;
        public bool HasNormalCompanion;
        public bool HasMaskCompanion;
        public bool Whitelisted;
        public bool SkipAtlas; // same-UV companion of a whitelist texture
        public int UsedGrayChannels; // bit0=R ... bit3=A
    }

    public sealed class AtoIsland
    {
        public AtoUvKey Uv;
        public int Id;
        public List<int> Triangles = new List<int>(); // index into submesh triangle array (3 per tri)
        public Rect UvRect; // in 0-1 after normalize
        public Vector2 UvTranslate; // applied to original uvs
        public float WorldArea;
        public int OrigW;
        public int OrigH;
        public int TargetW;
        public int TargetH;
        public bool SolidColor;
        public Color32 Solid;
        public bool Rotated90;
        public Vector2Int AtlasPos; // cell or pixel
        public int AtlasIndex = -1;
        public NativeMaskRef Mask;
    }

    public struct NativeMaskRef
    {
        public int CellsW;
        public int CellsH;
        public ulong[] Bits; // row-major cells, 1 bit per cell packed in ulong
    }

    public sealed class AtoUvGroup
    {
        public int Id;
        public AtoUvKey Key;
        public List<AtoTextureUse> Textures = new List<AtoTextureUse>();
        public List<AtoIsland> Islands = new List<AtoIsland>();
        public bool Whitelisted;
        public bool SkipAtlas;
        public bool FailedAtlas;
        public Vector2Int PackedSize;
    }

    public sealed class AtoTypeGroup
    {
        public int Id;
        public bool HasNormal;
        public bool HasMask;
        public bool IsSrgb;
        public FilterMode Filter;
        public List<AtoUvGroup> UvGroups = new List<AtoUvGroup>();
        public List<AtoAtlas> Atlases = new List<AtoAtlas>();
    }

    public sealed class AtoAtlas
    {
        public int Id;
        public string Name;
        public AtoTextureKind Kind;
        public int Width;
        public int Height;
        public Texture2D Texture;
        public List<AtoIsland> Islands = new List<AtoIsland>();
        public List<Texture2D> Sources = new List<Texture2D>();
        public float Utilization;
        public long OrigBytes;
        public long NewBytes;
    }

    public sealed class AtoReportData
    {
        public int Renderers;
        public int Materials;
        public int TexturesIn;
        public int TexturesOut;
        public int Islands;
        public int Atlases;
        public int Whitelisted;
        public int Warnings;
        public long OrigPixels;
        public long NewPixels;
        public readonly List<string> Details = new List<string>();
        public readonly List<string> AtlasLines = new List<string>();
    }
}
