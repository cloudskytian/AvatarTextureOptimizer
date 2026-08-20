using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Fosa.AvatarTextureOptimizer;

namespace Fosa.AvatarTextureOptimizer.Editor
{
    internal sealed class BuildSnapshot : IDisposable
    {
        public readonly GameObject Root;
        public readonly TexturePixelCache PixelCache = new TexturePixelCache(256L * 1024L * 1024L);
        public readonly List<RendererRecord> Renderers = new List<RendererRecord>();
        public readonly List<MaterialUse> MaterialUses = new List<MaterialUse>();
        public readonly List<TextureAssetInfo> Textures = new List<TextureAssetInfo>();
        public readonly List<IslandRecord> Islands = new List<IslandRecord>();
        public readonly Dictionary<Texture2D, TextureAssetInfo> TextureMap = new Dictionary<Texture2D, TextureAssetInfo>();
        public readonly Dictionary<Renderer, RendererAnimationInfo> AnimationInfo = new Dictionary<Renderer, RendererAnimationInfo>();

        public BuildSnapshot(GameObject root)
        {
            Root = root;
        }

        public void AddTexture(TextureAssetInfo info)
        {
            if (info == null || info.Source == null) return;
            if (!TextureMap.ContainsKey(info.Source))
            {
                TextureMap.Add(info.Source, info);
                Textures.Add(info);
            }
        }

        public void Dispose()
        {
            PixelCache.Dispose();
        }
    }

    internal sealed class RendererRecord
    {
        public Renderer Renderer;
        public Mesh SourceMesh;
        public Mesh WorkingMesh;
        public bool SkipAll;
        public bool IsSkinned;
        public readonly HashSet<int> UnsafeUVChannels = new HashSet<int>();
        public readonly HashSet<int> RegisteredAAOChannels = new HashSet<int>();
        public float AnimationAreaScale = 1f;
        public readonly List<MaterialUse> Materials = new List<MaterialUse>();

        public string Path(BuildSnapshot snapshot)
        {
            return ATOUnityPaths.RelativePath(snapshot.Root.transform, Renderer != null ? Renderer.transform : null);
        }
    }

    internal sealed class MaterialUse
    {
        public RendererRecord Owner;
        public int Slot;
        public Material SourceMaterial;
        public Material WorkingMaterial;
        public bool SkipAll;
        public bool SkipAtlas;
        public bool HasAnimatedMaterialSwitch;
        public bool HasAnimatedTextureTransform;
        public bool ShaderRecognized;
        public bool Cutout;
        public bool Blend;
        public float Cutoff = 0.5f;
        public readonly List<TextureReference> References = new List<TextureReference>();
        public readonly List<Material> AnimationVariants = new List<Material>();
        public readonly List<IslandRecord> Islands = new List<IslandRecord>();

        public TextureReference MainReference
        {
            get
            {
                for (int i = 0; i < References.Count; i++)
                    if (References[i].Category == ATOTextureCategory.Opaque || References[i].Category == ATOTextureCategory.Transparent)
                        return References[i];
                return References.Count == 0 ? null : References[0];
            }
        }
    }

    internal sealed class TextureReference
    {
        public string PropertyName;
        public TextureAssetInfo Texture;
        public ATOTextureCategory Category;
        public int UVChannel;
        public bool IsPrimary;
        public bool IsWhitelisted;
        public bool IsAnimatedVariant;
        public bool AtlasAssigned;
        public Texture2D OptimizedTexture;
        public string TypeGroupKey;
    }

    internal sealed class TextureAssetInfo
    {
        public Texture2D Source;
        public Texture2D Optimized;
        public bool IsWhitelisted;
        public bool HasAlpha;
        public bool IsNormal;
        public bool IsGrayscale;
        public int Width;
        public int Height;
        public FilterMode FilterMode;
        public TextureWrapMode WrapMode;
        public bool SRGB;
        public string TypeGroupKey;
        public TextureImportFingerprint Fingerprint;
        public readonly List<TextureReference> References = new List<TextureReference>();

        public string DisplayName => Source == null ? "<null>" : Source.name;

        public ATOTextureCategory Category
        {
            get
            {
                if (IsNormal) return ATOTextureCategory.Normal;
                if (IsGrayscale) return ATOTextureCategory.Grayscale;
                return HasAlpha ? ATOTextureCategory.Transparent : ATOTextureCategory.Opaque;
            }
        }
    }

    internal readonly struct TextureImportFingerprint : IEquatable<TextureImportFingerprint>
    {
        public readonly int Width;
        public readonly int Height;
        public readonly TextureWrapMode WrapMode;
        public readonly FilterMode FilterMode;
        public readonly bool Mipmap;
        public readonly bool Streaming;
        public readonly bool SRGB;
        public readonly TextureImporterCompression Compression;
        public readonly int MaxSize;
        public readonly string AssetPath;

        public TextureImportFingerprint(int width, int height, TextureWrapMode wrapMode, FilterMode filterMode, bool mipmap,
            bool streaming, bool srgb, TextureImporterCompression compression, int maxSize, string assetPath)
        {
            Width = width;
            Height = height;
            WrapMode = wrapMode;
            FilterMode = filterMode;
            Mipmap = mipmap;
            Streaming = streaming;
            SRGB = srgb;
            Compression = compression;
            MaxSize = maxSize;
            AssetPath = assetPath ?? string.Empty;
        }

        public bool Equals(TextureImportFingerprint other)
        {
            return Width == other.Width && Height == other.Height && WrapMode == other.WrapMode &&
                   FilterMode == other.FilterMode && Mipmap == other.Mipmap && Streaming == other.Streaming &&
                   SRGB == other.SRGB && Compression == other.Compression && MaxSize == other.MaxSize;
        }

        public override bool Equals(object obj)
        {
            return obj is TextureImportFingerprint && Equals((TextureImportFingerprint)obj);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = Width;
                hash = hash * 31 + Height;
                hash = hash * 31 + (int)WrapMode;
                hash = hash * 31 + (int)FilterMode;
                hash = hash * 31 + (Mipmap ? 1 : 0);
                hash = hash * 31 + (Streaming ? 1 : 0);
                hash = hash * 31 + (SRGB ? 1 : 0);
                hash = hash * 31 + (int)Compression;
                hash = hash * 31 + MaxSize;
                return hash;
            }
        }

        public static bool operator ==(TextureImportFingerprint left, TextureImportFingerprint right) => left.Equals(right);
        public static bool operator !=(TextureImportFingerprint left, TextureImportFingerprint right) => !left.Equals(right);
    }

    internal sealed class RendererAnimationInfo
    {
        public bool HasAnimatedEnable;
        public bool HasAnimatedMaterialSwitch;
        public bool HasAnimatedTextureTransform;
        public readonly List<Material> MaterialVariants = new List<Material>();
        public readonly HashSet<string> SourceClips = new HashSet<string>();
        public float MaxScaleX = 1f;
        public float MaxScaleY = 1f;
        public float MaxScaleZ = 1f;

        public float MaxAreaScale => Mathf.Max(1f, Mathf.Abs(MaxScaleX * MaxScaleY * MaxScaleZ));
    }

    internal sealed class IslandRecord
    {
        public MaterialUse Material;
        public int SubMesh;
        public int UVChannel;
        public string TypeGroupKey;
        public readonly List<IslandTriangle> Triangles = new List<IslandTriangle>();
        public Rect UVBounds;
        public float OriginalUVArea;
        public float SurfaceArea;
        public float UniformScale = 1f;
        public Vector2 AxisScale = Vector2.one;
        public Vector2 AtlasOffset;
        public int AtlasIndex = -1;
        public bool SkipQuality;
        public bool SkipAtlas;
        public bool PureColor;
        public bool NormalizedByTranslation;
        public Vector2 UVTranslation;
        public int OutputWidth;
        public int OutputHeight;

        public TextureAssetInfo PrimaryTexture => Material == null || Material.MainReference == null
            ? null
            : Material.MainReference.Texture;

        public Vector2 TransformUV(Vector2 uv)
        {
            Vector2 normalized = uv + UVTranslation;
            Vector2 pivot = UVBounds.center;
            Vector2 transformed = pivot + Vector2.Scale(normalized - pivot, AxisScale * UniformScale);
            return transformed + AtlasOffset;
        }
    }

    internal readonly struct IslandTriangle
    {
        public readonly int A;
        public readonly int B;
        public readonly int C;
        public readonly Vector2 UVA;
        public readonly Vector2 UVB;
        public readonly Vector2 UVC;
        public readonly float Area;

        public IslandTriangle(int a, int b, int c, Vector2 uvA, Vector2 uvB, Vector2 uvC, float area)
        {
            A = a;
            B = b;
            C = c;
            UVA = uvA;
            UVB = uvB;
            UVC = uvC;
            Area = area;
        }
    }

    internal sealed class AtlasFamily
    {
        public string Key;
        public int UVChannel;
        public readonly List<IslandRecord> Islands = new List<IslandRecord>();
        public readonly List<TextureAssetInfo> Channels = new List<TextureAssetInfo>();
        public int Width;
        public int Height;
        public Texture2D AtlasTexture;
        public readonly Dictionary<TextureAssetInfo, Texture2D> AtlasBySource = new Dictionary<TextureAssetInfo, Texture2D>();
    }

    internal static class ATOUnityPaths
    {
        public static string RelativePath(Transform root, Transform child)
        {
            if (root == null || child == null) return string.Empty;
            if (child == root) return string.Empty;
            List<string> names = new List<string>();
            Transform current = child;
            while (current != null && current != root)
            {
                names.Add(current.name);
                current = current.parent;
            }
            if (current != root) return child.name;
            names.Reverse();
            return string.Join("/", names.ToArray());
        }
    }
}
