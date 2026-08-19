// English: Mutable bake state shared across pipeline stages.
// 中文：贯穿各流水线阶段的可变烘焙状态。
using System;
using System.Collections.Generic;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using UnityEngine;
using UnityEngine.Rendering;
using Net.Fosa.AvatarTextureOptimizer;
using Net.Fosa.AvatarTextureOptimizer.API;
using Object = UnityEngine.Object;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    internal sealed class ATOState : IDisposable
    {
        public BuildContext Build;
        public AvatarTextureOptimizer Component;
        public ATOBuildPlatform Platform;
        public ATOPlatformSettings Settings;
        public ATOQualityParameters Quality;
        public ATOLogger Log;
        public ATOProgress Progress;
        public ATOReport Report = new ATOReport();
        public AnimatorServicesContext Anim;
        public ATOExtensionContext Ext = new ATOExtensionContext();

        public readonly HashSet<Object> WhitelistObjects = new HashSet<Object>();
        public readonly HashSet<Texture2D> WhitelistTextures = new HashSet<Texture2D>();
        public readonly HashSet<Texture2D> SkipAtlasTextures = new HashSet<Texture2D>();

        public readonly List<ATORendererInfo> Renderers = new List<ATORendererInfo>();
        public readonly Dictionary<Texture2D, Texture2D> TextureReplace = new Dictionary<Texture2D, Texture2D>();
        public readonly Dictionary<Material, Material> MaterialReplace = new Dictionary<Material, Material>();
        public readonly List<ATOTextureUse> Uses = new List<ATOTextureUse>();
        public readonly List<ATOIsland> Islands = new List<ATOIsland>();
        public readonly List<ATOUvGroup> UvGroups = new List<ATOUvGroup>();
        public readonly List<ATOAtlasResult> Atlases = new List<ATOAtlasResult>();

        public readonly ATOTextureCache Cache = new ATOTextureCache();
        public readonly HashSet<Object> Generated = new HashSet<Object>();

        public bool GenerateAtlases
        {
            get { return Settings != null && Settings.generateAtlases; }
        }

        public void Dispose()
        {
            Cache.Dispose();
        }
    }

    internal sealed class ATORendererInfo
    {
        public Renderer Renderer;
        public Mesh Mesh;
        public Material[] Materials;
        public bool AnimatedEnable;
        public bool AnimatedDisable;
        public Vector3 MaxAbsScale = Vector3.one;
        public bool AnySlotAnimatedIndependently;
        public readonly HashSet<int> AnimatedMaterialSlots = new HashSet<int>();
    }

    internal sealed class ATOTextureUse
    {
        public ATORendererInfo Renderer;
        public Material Material;
        public string Property;
        public Texture2D Texture;
        public int UvChannel;
        public ATOTextureSemantic Semantic;
        public ATOCompanionKind Companions;
        public ATOAlphaMode AlphaMode;
        public float Cutoff = 0.5f;
        public bool Linear;
        public FilterMode Filter;
        public TextureWrapMode Wrap;
        public bool Eligible = true;
        public string SkipReason;
        public ColorSpace ColorSpace;
    }

    internal sealed class ATOIsland
    {
        public int Id;
        public ATORendererInfo Renderer;
        public int Submesh;
        public int UvChannel;
        public Texture2D Source;
        public ATOTextureSemantic Semantic;
        public List<int> VertexIndices = new List<int>();
        public List<int> TriangleIndices = new List<int>();
        public Rect UvBounds;
        public Rect PixelBounds;
        public float WorldArea;
        public float UvArea;
        public Vector2 Scale = Vector2.one;
        public bool SolidColor;
        public Color Solid;
        public bool Eligible = true;
        public int PackX;
        public int PackY;
        public int PackW;
        public int PackH;
        public bool Rotated;
        public ATOAtlasResult Atlas;
        public Vector2 UvTranslate; // applied before scale, for [0,1] normalize
    }

    internal sealed class ATOUvGroup
    {
        public int Id;
        public readonly HashSet<Texture2D> Textures = new HashSet<Texture2D>();
        public readonly List<ATOIsland> Islands = new List<ATOIsland>();
        public ATOCompanionKind Companions;
        public bool Linear;
        public FilterMode Filter = FilterMode.Bilinear;
        public int MasterWidth;
        public int MasterHeight;
        public bool Packed;
        public bool Abandoned;
    }

    internal sealed class ATOAtlasResult
    {
        public string Name;
        public Texture2D Texture;
        public int Width;
        public int Height;
        public ATOTextureSemantic Semantic;
        public ATOCompanionKind TypeKey;
        public bool Linear;
        public FilterMode Filter;
        public float Utilization;
        public readonly List<Texture2D> Sources = new List<Texture2D>();
        public readonly List<ATOIsland> Islands = new List<ATOIsland>();
    }

    internal enum ColorSpaceKind
    {
        Srgb,
        Linear
    }
}
