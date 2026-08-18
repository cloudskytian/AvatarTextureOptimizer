using System.Collections.Generic;
using UnityEngine;
using Net.Fosa.AvatarTextureOptimizer;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// UV ↔ texture correspondence after animation merge.
    /// 动画合并后的 UV 与贴图对应关系。
    /// </summary>
    public sealed class AtoGraph
    {
        public readonly List<AtoBinding> Bindings = new List<AtoBinding>();
        public readonly HashSet<Texture2D> EligibleTextures = new HashSet<Texture2D>();
        public readonly HashSet<Texture2D> WhitelistedTextures = new HashSet<Texture2D>();
        public readonly Dictionary<Texture2D, AtoTypeGroupKey> TypeGroup = new Dictionary<Texture2D, AtoTypeGroupKey>();
        public readonly Dictionary<int, AtoUvGroup> UvGroups = new Dictionary<int, AtoUvGroup>();
        public readonly List<Renderer> Renderers = new List<Renderer>();
    }

    public sealed class AtoBinding
    {
        public Renderer Renderer;
        public Mesh Mesh;
        public int Submesh;
        public int MaterialSlot;
        public Material Material;
        public string Property;
        public Texture2D Texture;
        public int UvChannel;
        public AtoTextureRole Role;
        public AtoBlendMode Blend;
        public float Cutoff;
        public bool Eligible;
        public string SkipReason;
    }

    public sealed class AtoUvGroup
    {
        public int Id;
        public readonly HashSet<Texture2D> Textures = new HashSet<Texture2D>();
        public readonly List<AtoBinding> Bindings = new List<AtoBinding>();
    }

    /// <summary>
    /// Type group: companion maps + color space + filter. / 类型组：伴随贴图 + 色彩空间 + filter。
    /// </summary>
    public struct AtoTypeGroupKey : System.IEquatable<AtoTypeGroupKey>
    {
        public bool HasNormal;
        public bool HasMask;
        public bool Srgb;
        public FilterMode Filter;

        public bool Equals(AtoTypeGroupKey o) =>
            HasNormal == o.HasNormal && HasMask == o.HasMask && Srgb == o.Srgb && Filter == o.Filter;

        public override bool Equals(object obj) => obj is AtoTypeGroupKey k && Equals(k);
        public override int GetHashCode() =>
            (HasNormal ? 1 : 0) | (HasMask ? 2 : 0) | (Srgb ? 4 : 0) | ((int)Filter << 3);

        public override string ToString() =>
            $"n={(HasNormal ? 1 : 0)} m={(HasMask ? 1 : 0)} srgb={(Srgb ? 1 : 0)} f={Filter}";
    }
}
