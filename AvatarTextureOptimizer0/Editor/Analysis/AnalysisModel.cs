using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Editor.Analysis
{
    [Flags]
    internal enum ATOTextureChannels : byte
    {
        None = 0,
        R = 1 << 0,
        G = 1 << 1,
        B = 1 << 2,
        A = 1 << 3,
        Rgb = R | G | B,
        Rgba = R | G | B | A
    }

    internal sealed class AvatarAnalysis
    {
        public readonly List<RendererRecord> Renderers = new List<RendererRecord>();
        public readonly List<UvGroupRecord> UvGroups = new List<UvGroupRecord>();
        public readonly List<TextureTypeGroupRecord> TextureTypeGroups = new List<TextureTypeGroupRecord>();
        public readonly Dictionary<Texture2D, Texture2D> CanonicalTextures = new Dictionary<Texture2D, Texture2D>();
        public readonly HashSet<Texture2D> WhitelistedTextures = new HashSet<Texture2D>();
        public readonly List<FallbackRecord> Fallbacks = new List<FallbackRecord>();
        public long InputTexturePixels;
        public IEnumerable<TextureBindingRecord> TextureBindings => Renderers.SelectMany(renderer => renderer.Slots).SelectMany(slot => slot.Bindings);
    }

    internal sealed class RendererRecord
    {
        public Renderer Renderer;
        public Mesh Mesh;
        public string Path;
        public float MaximumAreaScale = 1f;
        // Bone-relative deformation has no general finite area bound (constraints/physics may act outside clips).
        public bool PreserveOriginalIslandResolution;
        public readonly List<MaterialSlotRecord> Slots = new List<MaterialSlotRecord>();
    }

    internal sealed class MaterialSlotRecord
    {
        public int Slot;
        public bool AtlasUnsafe;
        public readonly HashSet<Material> Materials = new HashSet<Material>();
        public readonly List<TextureBindingRecord> Bindings = new List<TextureBindingRecord>();
    }

    internal sealed class TextureBindingRecord
    {
        public RendererRecord Renderer;
        public MaterialSlotRecord Slot;
        public Material Material;
        public string PropertyName;
        public Texture2D Texture;
        public Texture2D OriginalTexture;
        public ATOTextureKind Kind;
        public int UvChannel;
        public ATOAlphaMode AlphaMode;
        public bool EvaluateCutout;
        public bool EvaluateBlend;
        public bool EvaluatePackedChannels;
        // None means legacy/extension-unspecified and is conservatively interpreted as RGBA by the evaluator.
        public ATOTextureChannels UsedChannels;
        public float Cutoff;
        public float[] Cutoffs = Array.Empty<float>();
        public bool Whitelisted;
        public bool AtlasSafe;
        public string UnsafeReason;
        public string ImportSignature;
        public bool IsInitialValue;
        public bool IsAnimatedValue;
    }

    internal sealed class UvGroupRecord
    {
        public int Id;
        public RendererRecord Renderer;
        public MaterialSlotRecord Slot;
        public int UvChannel;
        public bool AtlasSafe = true;
        public readonly List<TextureBindingRecord> Bindings = new List<TextureBindingRecord>();
        public readonly List<UvIsland> Islands = new List<UvIsland>();
        public string TypeGroupKey;
    }


    internal readonly struct TextureTypeKey : System.IEquatable<TextureTypeKey>
    {
        public readonly ATOTextureKind Kind;
        public readonly bool Srgb;
        public readonly FilterMode FilterMode;
        public readonly int AnisoLevel;
        public readonly float MipMapBias;
        public TextureTypeKey(ATOTextureKind kind, bool srgb, FilterMode filterMode, int anisoLevel, float mipMapBias)
        {
            Kind = kind; Srgb = srgb; FilterMode = filterMode; AnisoLevel = anisoLevel; MipMapBias = mipMapBias;
        }
        public bool Equals(TextureTypeKey other) => Kind == other.Kind && Srgb == other.Srgb &&
            FilterMode == other.FilterMode && AnisoLevel == other.AnisoLevel && MipMapBias.Equals(other.MipMapBias);
        public override bool Equals(object obj) => obj is TextureTypeKey other && Equals(other);
        public override int GetHashCode()
        {
            unchecked
            {
                var hash = (((int)Kind * 397) ^ Srgb.GetHashCode()) * 397 ^ (int)FilterMode;
                return ((hash * 397) ^ AnisoLevel) * 397 ^ MipMapBias.GetHashCode();
            }
        }
    }

    internal sealed class TextureTypeGroupRecord
    {
        public int Id;
        public TextureTypeKey Key;
        public readonly List<TextureBindingRecord> Bindings = new List<TextureBindingRecord>();
    }

    internal sealed class UvIsland
    {
        public int Id;
        public int UvGroupId;
        public readonly List<int> TriangleIndices = new List<int>();
        public Rect UvBounds;
        public Vector2 IntegerNormalization;
        public float SurfaceAreaSquareMeters;
        public Vector2Int OriginalPixelBounds;
        public Vector2Int TargetPixelSize;
        public Vector2 Scale = Vector2.one;
        public bool Rotated;
        public bool PureColor;
    }

    internal readonly struct FallbackRecord
    {
        public readonly UnityEngine.Object Subject;
        public readonly string Reason;
        public FallbackRecord(UnityEngine.Object subject, string reason) { Subject = subject; Reason = reason; }
    }

    internal readonly struct ShaderTextureInfo
    {
        public readonly string PropertyName;
        public readonly ATOTextureKind Kind;
        public readonly int UvChannel;
        public readonly bool Safe;
        public readonly string Reason;
        public readonly ATOSurfaceAlphaUsage SurfaceAlphaUsage;
        public readonly ATOTextureChannels UsedChannels;

        public ShaderTextureInfo(string propertyName, ATOTextureKind kind, int uvChannel, bool safe, string reason,
            ATOSurfaceAlphaUsage surfaceAlphaUsage, ATOTextureChannels usedChannels)
        {
            PropertyName = propertyName; Kind = kind; UvChannel = uvChannel; Safe = safe; Reason = reason;
            SurfaceAlphaUsage = surfaceAlphaUsage; UsedChannels = usedChannels;
        }
    }
}
