// English: In-memory analysis model: UV groups, type groups, islands, references.
// 中文：分析阶段内存模型：UV 组、类型组、岛、引用。
using System;
using System.Collections.Generic;
using net.fosa.ato;
using UnityEngine;

namespace net.fosa.ato.editor
{
    public sealed class AtoTexKey : IEquatable<AtoTexKey>
    {
        public Texture2D Texture;
        public string ImporterFingerprint;
        public bool Whitelisted;

        public bool Equals(AtoTexKey other) =>
            other != null && Texture == other.Texture && ImporterFingerprint == other.ImporterFingerprint;
        public override bool Equals(object obj) => Equals(obj as AtoTexKey);
        public override int GetHashCode() =>
            (Texture ? Texture.GetInstanceID() : 0) * 397 ^ (ImporterFingerprint ?? "").GetHashCode();
    }

    public sealed class AtoIsland
    {
        public int MeshId;
        public Mesh Mesh;
        public Renderer Renderer;
        public int Submesh;
        public int UvChannel;
        public int IslandIndex;
        public Vector2 Min, Max; // in original UV
        public int[] Triangles;  // local triangle indices into mesh
        public int[] Vertices;
        public float WorldArea;
        public bool SolidColor;
        public Color Solid;
        public float ScaleU = 1f, ScaleV = 1f;
        public RectInt PixelRect; // on source texture
        public bool SkipAtlas;
        public Vector2[] UvTris; // 3 * triangleCount in UV space after normalize
    }

    public sealed class AtoUvBinding
    {
        public Renderer Renderer;
        public Mesh Mesh;
        public int Submesh;
        public int UvChannel;
        public Material Material;
        public string PropertyName;
        public Texture2D Texture;
        public AtoTextureClass Class;
        public AtoAlphaMode AlphaMode;
        public float Cutoff;
        public bool Animated;
        public bool Eligible = true;
        public string IneligibleReason;
    }

    public sealed class AtoUvGroup
    {
        public int Id;
        public Renderer Renderer;
        public Mesh Mesh;
        public int Submesh;
        public int UvChannel;
        public readonly List<AtoUvBinding> Bindings = new List<AtoUvBinding>();
        public readonly List<AtoIsland> Islands = new List<AtoIsland>();
        public bool Whitelisted;
        public bool SkipAtlasOnly;
    }

    public sealed class AtoTypeGroup
    {
        public string Key; // colorspace|filter|hasNormal|hasMask|...
        public TextureImporterType UnityType;
        public bool Linear;
        public FilterMode Filter;
        public bool HasNormal;
        public bool HasMask;
        public readonly List<Texture2D> Textures = new List<Texture2D>();
        public readonly List<AtoUvGroup> UvGroups = new List<AtoUvGroup>();
    }

    public sealed class AtoBakeReport
    {
        public long TotalMs;
        public int Islands;
        public int Atlases;
        public int TexturesIn;
        public int TexturesOut;
        public long BytesIn;
        public long BytesOut;
        public readonly List<string> Details = new List<string>();
        public readonly List<string> Warnings = new List<string>();

        public void Add(string s)
        {
            Details.Add(s);
            AtoLog.VerboseInfo(s);
        }
    }
}
