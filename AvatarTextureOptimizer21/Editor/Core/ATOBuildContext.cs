// ATO Build Context - Complete shared state across all passes
// ATO构建上下文 - 所有Pass之间的完整共享状态

using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using net.fosa.avatar_texture_optimizer.Runtime;

namespace net.fosa.avatar_texture_optimizer.Editor.Core
{
    public class ATOBuildContext
    {
        // === Configuration / 配置 ===
        public AvatarTextureOptimizerComponent Component { get; set; }
        public bool IsValid { get; set; } = true;
        public TargetPlatform EffectivePlatform { get; set; }

        // === Progress & Cancellation / 进度与取消 ===
        public CancellationTokenSource CancellationSource { get; set; }
        public volatile bool Cancelled;
        public string CurrentStage = "";
        public float CurrentProgress; // 0-1
        public Action<string, float> OnProgressChanged;

        public void ReportProgress(string stage, float progress)
        {
            CurrentStage = stage;
            CurrentProgress = progress;
            OnProgressChanged?.Invoke(stage, progress);
            if (Cancelled) throw new OperationCanceledException("[ATO] Build cancelled by user.");
        }

        // === Analysis Results / 分析结果 ===
        public List<RendererInfo> Renderers { get; set; } = new List<RendererInfo>();
        public Dictionary<UVKey, UVTextureMapping> UVTextureMap { get; set; }
            = new Dictionary<UVKey, UVTextureMapping>();
        public List<TextureInfo> AllTextures { get; set; } = new List<TextureInfo>();
        public HashSet<UnityEngine.Object> WhitelistObjects { get; set; } = new HashSet<UnityEngine.Object>();
        public HashSet<int> WhitelistedTextureIds { get; set; } = new HashSet<int>();

        // Whitelisted textures whose same-UV partners should skip atlas but do other opts
        // 白名单贴图的同UV伙伴（跳过图集化但参与其他优化）
        public HashSet<int> SameUVWhitelistPartners { get; set; } = new HashSet<int>();

        public AnimationAnalysisResult AnimationAnalysis { get; set; }
        public Dictionary<Material, ShaderAnalysisResult> ShaderAnalysisResults { get; set; }
            = new Dictionary<Material, ShaderAnalysisResult>();

        // === Processing Results / 处理结果 ===
        public List<UVIsland> AllIslands { get; set; } = new List<UVIsland>();
        public List<TextureTypeGroup> TextureTypeGroups { get; set; } = new List<TextureTypeGroup>();
        public List<UVGroup> UVGroups { get; set; } = new List<UVGroup>();
        public List<AtlasResult> Atlases { get; set; } = new List<AtlasResult>();
        public Dictionary<int, IslandQualityResult> IslandQualityResults { get; set; }
            = new Dictionary<int, IslandQualityResult>();

        // === Caches / 缓存 ===
        public Dictionary<int, Color[]> TexturePixelCache { get; set; } = new Dictionary<int, Color[]>();
        public Dictionary<int, bool[,]> RasterCache { get; set; } = new Dictionary<int, bool[,]>();
        public Dictionary<int, string> TextureHashCache { get; set; } = new Dictionary<int, string>();

        // === Application Results / 应用结果 ===
        public Dictionary<Mesh, Mesh> ModifiedMeshes { get; set; } = new Dictionary<Mesh, Mesh>();
        public List<Texture2D> GeneratedTextures { get; set; } = new List<Texture2D>();
        public Dictionary<Material, MaterialUpdate> MaterialUpdates { get; set; }
            = new Dictionary<Material, MaterialUpdate>();

        // Material slot merges: (renderer, oldSlotCount, newSlotMaterials)
        // 材质槽合并记录
        public List<MaterialSlotMerge> SlotMerges { get; set; } = new List<MaterialSlotMerge>();

        // Fallback textures (whitelisted but still get import setting optimization)
        // 降级贴图（白名单但仍获得导入设置优化）
        public List<Texture2D> FallbackTextures { get; set; } = new List<Texture2D>();

        // === Reporting / 报告 ===
        public List<ReportEntry> ReportEntries { get; set; } = new List<ReportEntry>();
        public Dictionary<string, double> StageTimings { get; set; } = new Dictionary<string, double>();
        public List<string> Warnings { get; set; } = new List<string>();

        public void AddWarning(string msg)
        {
            Warnings.Add(msg);
            ATOLog.Warning(msg);
        }
    }

    // === Data Structures / 数据结构 ===

    public class RendererInfo
    {
        public Renderer Renderer { get; set; }
        public Mesh SharedMesh { get; set; }
        public Material[] SharedMaterials { get; set; }
        public bool IsActive { get; set; }
        public bool IsEnabledByAnimation { get; set; } = true;
    }

    public struct UVKey : IEquatable<UVKey>
    {
        public int MeshInstanceId;
        public int UvChannel;
        public bool Equals(UVKey o) => MeshInstanceId == o.MeshInstanceId && UvChannel == o.UvChannel;
        public override bool Equals(object o) => o is UVKey k && Equals(k);
        public override int GetHashCode() => MeshInstanceId * 31 + UvChannel;
    }

    public class UVTextureMapping
    {
        public List<TextureUsage> TextureUsages { get; set; } = new List<TextureUsage>();
        public List<MaterialReference> MaterialReferences { get; set; } = new List<MaterialReference>();
    }

    public class TextureUsage
    {
        public Texture2D Texture { get; set; }
        public string ShaderPropertyName { get; set; }
        public TextureRole Role { get; set; }
        public Material SourceMaterial { get; set; }
        public bool FromAnimation { get; set; }
        public TransparencyMode TransparencyMode { get; set; }
        public float Cutoff { get; set; } = 0.5f;
    }

    public enum TextureRole
    {
        MainColor, NormalMap, Mask, Emission, Occlusion,
        Metallic, Roughness, AlphaMask, Detail, Other
    }

    public enum TransparencyMode
    {
        Opaque, Cutout, Blend, Premultiply, Additive
    }

    public class MaterialReference
    {
        public Renderer Renderer { get; set; }
        public int MaterialSlotIndex { get; set; }
        public Material Material { get; set; }
    }

    public class TextureInfo
    {
        public Texture2D Texture { get; set; }
        public Texture2D OriginalTexture { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public bool IsWhitelisted { get; set; }
        public TextureRole PrimaryRole { get; set; }
        public bool HasAlpha { get; set; }
        public bool IsNormalMap { get; set; }
        public bool IsGrayscale { get; set; }
        public bool IsLinear { get; set; }
        public TextureWrapMode WrapMode { get; set; }
        public FilterMode FilterMode { get; set; }
        // Import settings snapshot / 导入设置快照
        public bool HasMipMaps { get; set; }
        public bool SRGB { get; set; }
        public int MaxTextureSize { get; set; }
        public TextureImporterFormat ImportFormat { get; set; }
        public int InstanceId => Texture != null ? Texture.GetInstanceID() : 0;
    }

    public enum TextureImporterFormat
    {
        Automatic, BC7, BC5, BC4, BC1, BC3, DXT1, DXT5,
        ASTC_4x4, ASTC_6x6, ASTC_8x8, ASTC_12x12,
        ETC2_RGB, ETC2_RGBA8, PVRTC_RGB_4BPP, PVRTC_RGBA_4BPP,
        RGBA32, CrunchedBC7, CrunchedDXT5
    }

    public class AnimationAnalysisResult
    {
        public List<MaterialSwapInfo> MaterialSwaps { get; set; } = new List<MaterialSwapInfo>();
        public List<TexturePropertyChange> TextureChanges { get; set; } = new List<TexturePropertyChange>();
        public List<STTransformChange> STTransformChanges { get; set; } = new List<STTransformChange>();
        public List<RenderModeChange> RenderModeChanges { get; set; } = new List<RenderModeChange>();
        public Dictionary<Transform, float> MaxScales { get; set; } = new Dictionary<Transform, float>();
        public HashSet<GameObject> CanBeDisabled { get; set; } = new HashSet<GameObject>();
        // Animation-driven texture → original texture mapping (for type group merging)
        // 动画驱动的贴图→原始贴图映射（用于类型组合并）
        public Dictionary<Texture2D, Texture2D> AnimationTextureOriginalMap { get; set; }
            = new Dictionary<Texture2D, Texture2D>();
    }

    public class MaterialSwapInfo
    {
        public Renderer Renderer { get; set; }
        public int MaterialSlot { get; set; }
        public Material OriginalMaterial { get; set; }
        public List<Material> SwappedMaterials { get; set; } = new List<Material>();
    }

    public class TexturePropertyChange
    {
        public Material Material { get; set; }
        public string PropertyName { get; set; }
        public List<Texture2D> PossibleTextures { get; set; } = new List<Texture2D>();
    }

    public class STTransformChange
    {
        public Material Material { get; set; }
        public string PropertyName { get; set; }
        public bool HasOffsetChange, HasScaleChange, HasRotationChange;
    }

    public class RenderModeChange
    {
        public Material Material { get; set; }
        public List<TransparencyMode> PossibleModes { get; set; } = new List<TransparencyMode>();
        public List<float> PossibleCutoffs { get; set; } = new List<float>();
    }

    public class ShaderAnalysisResult
    {
        public Shader Shader { get; set; }
        public string ShaderName { get; set; }
        public bool IsLilToon, IsStandard, IsCompatible = true;
        public string IncompatibilityReason;
        public List<ShaderTextureProperty> TextureProperties { get; set; } = new List<ShaderTextureProperty>();
        public List<string> ActiveKeywords { get; set; } = new List<string>();
    }

    public class ShaderTextureProperty
    {
        public string PropertyName { get; set; }
        public TextureRole Role { get; set; }
        public bool HasSTTransform, IsDecalOrSpecial;
        public int UVChannel { get; set; }
    }

    public class UVIsland
    {
        public int Id { get; set; }
        public Mesh SourceMesh { get; set; }
        public int SubMeshIndex { get; set; }
        public int UvChannel { get; set; }
        public List<int> TriangleIndices { get; set; } = new List<int>();
        public List<Vector2> UVs { get; set; } = new List<Vector2>();
        public Vector2 BoundsMin, BoundsMax;
        public float UVArea, PhysicalArea;
        public Vector2 ScaleFactor = Vector2.one;
        // Anisotropic scale: first uniform, then per-axis refinement
        // 各向异性缩放：先均匀，后逐轴细化
        public Vector2 AnisotropicScale = Vector2.one;
        public int TargetAtlasIndex = -1;
        public List<Vector2> NewUVs;
        public bool IsPureColor;
        public Color PureColorValue;
        public int UVGroupId = -1;
        public int SourceTextureIndex = -1;
        public bool[,] RasterBitmask;
        public bool IsWhitelisted;
        // Skip atlas but participate in other opts (same UV as whitelisted)
        // 跳过图集但参与其他优化（与白名单同UV）
        public bool SkipAtlasOnly;
        public Dictionary<string, float> BlendShapeWeights { get; set; } = new Dictionary<string, float>();
        // Original triangle UV data (for precise rasterization)
        // 原始三角形UV数据（用于精确光栅化）
        public List<TriangleUV> TrianglesUV { get; set; } = new List<TriangleUV>();
    }

    public struct TriangleUV
    {
        public Vector2 V0, V1, V2;
    }

    public class TextureTypeGroup
    {
        public int Id;
        public TextureRole PrimaryRole;
        public bool HasNormalMap, HasMask, HasAlpha, IsLinear;
        public FilterMode FilterMode;
        public List<int> TextureIndices { get; set; } = new List<int>();
        public List<int> UVGroupIds { get; set; } = new List<int>();
        public string Signature; // Group signature key / 组签名键
    }

    public class UVGroup
    {
        public int Id;
        public List<int> IslandIds { get; set; } = new List<int>();
        public List<int> TextureIndices { get; set; } = new List<int>();
        public List<int> TypeGroupIds { get; set; } = new List<int>();
        public int MaxOriginalSize;
        // Barrel effect: final scale = max across all textures, capped by max original size
        // 木桶效应：最终缩放 = 所有贴图中的最大值，受最大原始尺寸钳制
        public float FinalScale = 1f;
        public Vector2 FinalAnisotropicScale = Vector2.one;
    }

    public class AtlasResult
    {
        public int Index;
        public string Name;
        public int Width, Height;
        public Texture2D AtlasTexture;
        public TextureRole AtlasRole;
        public int TypeGroupId;
        public List<PackedIsland> PackedIslands { get; set; } = new List<PackedIsland>();
        public float Utilization;
        public int SourceTextureCount, IslandCount;
    }

    public class PackedIsland
    {
        public int IslandId, X, Y, Width, Height;
        public bool Rotated; // 90° rotation applied / 应用了90°旋转
    }

    public class IslandQualityResult
    {
        public int IslandId;
        public float ScaleFactor = 1f;
        public Vector2 AnisotropicScale = Vector2.one;
        public bool PassedMS_SSIM, PassedSSIM, PassedDeltaE, PassedAlpha, PassedNormal, PassedGrayscale;
        public float WorstMetric;
        public string BottleneckMetric;
        public bool SkippedQualityCheck;
    }

    public class MaterialUpdate
    {
        public Material OriginalMaterial;
        public Dictionary<string, Texture2D> TextureReplacements { get; set; }
            = new Dictionary<string, Texture2D>();
        public bool ShouldDeduplicate;
        public Material DeduplicateTarget;
    }

    public class MaterialSlotMerge
    {
        public Renderer Renderer;
        public List<int> MergedSlots { get; set; } = new List<int>(); // Old slot indices
        public int TargetSlot; // The surviving slot index
        public Material MergedMaterial;
    }

    public class ReportEntry
    {
        public ReportSeverity Severity;
        public string Category, Message, MessageZh, Details, DetailsZh;
    }

    public enum ReportSeverity { Info, Warning, Error }
}
