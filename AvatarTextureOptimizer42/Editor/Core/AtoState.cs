using System;
using System.Collections.Generic;
using System.Diagnostics;
using Net.Fosa.AvatarTextureOptimizer;
using nadena.dev.ndmf;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Mutable build session state stored in NDMF BuildContext.
    /// 存储在 NDMF BuildContext 中的可变构建会话状态。
    /// </summary>
    internal sealed class AtoSessionState
    {
        public AvatarTextureOptimizer Component;
        public bool Enabled;
        public bool Abort;
        public bool Cancelled;
        public readonly Stopwatch TotalTimer = Stopwatch.StartNew();
        public readonly AtoBuildReport Report = new AtoBuildReport();
        public readonly Dictionary<string, Material> MaterialRewriteMap = new Dictionary<string, Material>(StringComparer.OrdinalIgnoreCase);
        public AtoScanResult ScanResult = new AtoScanResult();
        public AtoBuildPlan Plan = new AtoBuildPlan();
    }

    /// <summary>
    /// Build report model emitted to logs.
    /// 输出到日志中的构建报告模型。
    /// </summary>
    internal sealed class AtoBuildReport
    {
        public int RendererCount;
        public int MaterialSlotCount;
        public int MaterialCount;
        public int TextureCandidateCount;
        public int UniqueTextureCount;
        public int AnimationClipCount;
        public int WhitelistHitCount;
        public int UnsupportedCount;
        public int PotentialDuplicateGroupCount;
        public int UvIslandCount;
        public int PlannedAtlasCount;
        public int ExecutedTextureCount;
        public int ExecutedAtlasCount;
        public int ExecutedMeshCount;
        public int ExecutedMaterialCount;
        public long TextureSourceBytes;
        public readonly List<string> DetailLines = new List<string>();
        public readonly List<string> WarningLines = new List<string>();
        public readonly Dictionary<string, double> StageTimesMs = new Dictionary<string, double>();

        public void AddDetail(string line)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                DetailLines.Add(line);
            }
        }

        public void AddWarning(string line)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                WarningLines.Add(line);
            }
        }
    }

    /// <summary>
    /// Raw analysis result before optimization.
    /// 优化前的原始分析结果。
    /// </summary>
    internal sealed class AtoScanResult
    {
        public readonly List<AtoRendererRecord> Renderers = new List<AtoRendererRecord>();
        public readonly List<AtoTextureUsageRecord> TextureUsages = new List<AtoTextureUsageRecord>();
        public readonly List<AtoAnimationClipRecord> AnimationClips = new List<AtoAnimationClipRecord>();
        public readonly List<AtoDuplicateTextureGroup> DuplicateGroups = new List<AtoDuplicateTextureGroup>();
        public readonly List<AtoUvGroupRecord> UvGroups = new List<AtoUvGroupRecord>();
    }

    internal sealed class AtoRendererRecord
    {
        public Renderer Renderer;
        public string Path;
        public bool ActiveSelf;
        public bool ActiveInHierarchy;
        public bool RendererEnabled;
        public bool PotentiallyActive = true;
        public int MaterialSlotCount;
        public bool IsSkinnedMeshRenderer;
        public Mesh SharedMesh;
        public Vector3 LossyScale;
        public float AnimatedAreaScaleFactor = 1.0f;
    }

    internal enum AtoTextureSemantic
    {
        Unknown = 0,
        Color = 1,
        Normal = 2,
        Mask = 3,
        Grayscale = 4,
    }

    internal enum AtoTextureDecision
    {
        Candidate = 0,
        ExplicitWhitelist = 1,
        SafeFallback = 2,
    }

    internal sealed class AtoTextureUsageRecord
    {
        public Object SourceObject;
        public Renderer Renderer;
        public Material Material;
        public Texture Texture;
        public string RendererPath;
        public string MaterialPath;
        public string TexturePath;
        public string MaterialProperty;
        public string UvGroupKey;
        public Vector2 Scale;
        public Vector2 Offset;
        public TextureWrapMode WrapModeU;
        public TextureWrapMode WrapModeV;
        public FilterMode FilterMode;
        public int MaterialSlotIndex;
        public int UvChannel;
        public bool IsTexture2D;
        public bool IsAnimatedProperty;
        public bool IsAnimatedSt;
        public bool IsAnimatedMaterialReference;
        public bool IsPotentiallyActive = true;
        public bool IsWhitelisted;
        public bool UsesIdentityTransform;
        public bool MayOverflowUvRange;
        public bool HasUvData;
        public bool UvInUnitSquare;
        public bool UvCanTranslateIntoUnitSquare;
        public AtoTextureSemantic Semantic;
        public AtoTextureDecision Decision;
        public string DecisionReason;
        public string ImporterFingerprint;
        public string ContentFingerprint;
        public long SourceBytes;
    }

    internal sealed class AtoAnimationClipRecord
    {
        public AnimationClip Clip;
        public string AssetPath;
        public int CurveBindingCount;
        public int ObjectReferenceBindingCount;
        public int MaterialBindingCount;
        public int ActivationBindingCount;
        public readonly HashSet<string> AnimatedMaterialProperties = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> AnimatedRendererPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    internal sealed class AtoDuplicateTextureGroup
    {
        public string Fingerprint;
        public readonly List<AtoTextureUsageRecord> Members = new List<AtoTextureUsageRecord>();
    }

    internal sealed class AtoUvGroupRecord
    {
        public string Key;
        public Renderer Renderer;
        public Mesh Mesh;
        public int MaterialSlotIndex;
        public int UvChannel;
        public bool HasData;
        public Vector2 Min;
        public Vector2 Max;
        public Vector2 Span;
        public Vector2 Translation;
        public bool InUnitSquareAlready;
        public bool CanTranslateIntoUnitSquare;
        public float TotalObjectSpaceArea;
        public float TotalUvArea;
        public float AnimatedAreaScaleFactor = 1.0f;
        public readonly List<AtoUvIslandRecord> Islands = new List<AtoUvIslandRecord>();
        public readonly List<AtoTextureUsageRecord> Usages = new List<AtoTextureUsageRecord>();
    }

    internal sealed class AtoUvIslandRecord
    {
        public int Index;
        public int TriangleCount;
        public Vector2 Min;
        public Vector2 Max;
        public Vector2 Size;
        public float ObjectSpaceArea;
        public float UvArea;
        public readonly List<AtoUvTriangleRecord> Triangles = new List<AtoUvTriangleRecord>();
    }

    internal struct AtoUvTriangleRecord
    {
        public Vector2 A;
        public Vector2 B;
        public Vector2 C;

        public AtoUvTriangleRecord(Vector2 a, Vector2 b, Vector2 c)
        {
            A = a;
            B = b;
            C = c;
        }
    }

    internal sealed class AtoBuildPlan
    {
        public readonly List<AtoUvGroupPlan> UvGroupPlans = new List<AtoUvGroupPlan>();
        public readonly List<AtoTextureTypeGroupPlan> TextureTypeGroups = new List<AtoTextureTypeGroupPlan>();
    }

    internal sealed class AtoUvGroupPlan
    {
        public string Key;
        public int CandidateCount;
        public int FallbackCount;
        public int WhitelistCount;
        public int IslandCount;
        public Vector2 EstimatedSourcePixels;
        public Vector2 EstimatedTargetPixels;
    }

    internal sealed class AtoTextureTypeGroupPlan
    {
        public string Key;
        public string MaterialProperty;
        public AtoTextureSemantic Semantic;
        public FilterMode FilterMode;
        public TextureWrapMode WrapModeU;
        public TextureWrapMode WrapModeV;
        public readonly List<AtoTextureUsageRecord> Members = new List<AtoTextureUsageRecord>();
        public readonly List<AtoAtlasPlan> Atlases = new List<AtoAtlasPlan>();
    }

    internal sealed class AtoAtlasPlan
    {
        public string Name;
        public int Width;
        public int Height;
        public int IslandCellSize;
        public int PaddingPixels;
        public float EstimatedUtilization;
        public readonly List<AtoAtlasItemPlan> Items = new List<AtoAtlasItemPlan>();
    }

    internal sealed class AtoAtlasItemPlan
    {
        public string UvGroupKey;
        public int PixelX;
        public int PixelY;
        public int PixelWidth;
        public int PixelHeight;
        public int CellX;
        public int CellY;
        public int CellWidth;
        public int CellHeight;
    }
}
