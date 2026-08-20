// CoreDataModel.cs
// Core data structures for the ATO pipeline: UV islands, texture references,
// UV groups, texture type groups, and animation tracking.
// ATO 管线的核心数据结构：UV 岛、贴图引用、UV 组、贴图类型组、动画跟踪。
//
// Copyright (c) 2024 fosa. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Core
{
    /// <summary>
    /// A single UV island: a connected group of UV triangles within a mesh on a specific UV channel.
    /// Maps to a rectangular region of a texture.
    /// 单个 UV 岛：网格中某个 UV 通道上的一组连通 UV 三角形。
    /// </summary>
    internal sealed class UVIsland
    {
        /// <summary>Unique ID within the pipeline. / 管线内唯一 ID。</summary>
        internal int Id;

        /// <summary>The mesh this island belongs to. / 此岛所属网格。</summary>
        internal Mesh SourceMesh;

        /// <summary>The renderer this island belongs to. / 此岛所属渲染器。</summary>
        internal Renderer SourceRenderer;

        /// <summary>UV channel (0-7). / UV 通道（0-7）。</summary>
        internal int UVChannel;

        /// <summary>Material slot index on the renderer. / 渲染器上的材质槽索引。</summary>
        internal int MaterialSlot;

        /// <summary>Bounding box in UV space [0,1]. / UV 空间 [0,1] 中的包围盒。</summary>
        internal Rect UVBounds;

        /// <summary>Bounding box in texture pixel space. / 贴图像素空间中的包围盒。</summary>
        internal Rect PixelBounds;

        /// <summary>List of triangle indices (into the mesh). / 三角形索引列表。</summary>
        internal List<int> TriangleIndices = new List<int>();

        /// <summary>Whether the island touches the UV wrap seam (requires whitelist). / 岛是否触及 UV 环绕缝（需要白名单）。</summary>
        internal bool CrossesWrapSeam;

        /// <summary>Scaled bounds after quality evaluation. / 质量评估后缩放的包围盒。</summary>
        internal Rect ScaledPixelBounds;

        /// <summary>Final placement in the atlas. / 图集中的最终位置。</summary>
        internal Rect AtlasPlacement;

        /// <summary>Rotation applied during packing (0 or 90). / 装箱时应用的旋转（0 或 90）。</summary>
        internal int Rotation;

        /// <summary>The rasterized bitmask for bin packing. / 用于装箱的光栅化位掩码。</summary>
        internal ulong[] RasterBitmask;

        /// <summary>Granularity used for rasterization. / 光栅化使用的粒度。</summary>
        internal int RasterGranularity;

        /// <summary>Island area in pixels (from rasterization). / 岛面积（像素，来自光栅化）。</summary>
        internal long RasterArea;

        /// <summary>Original texture this island came from. / 此岛来源的原始贴图。</summary>
        internal Texture2D SourceTexture;

        /// <summary>UV group this island belongs to. / 此岛所属的 UV 组。</summary>
        internal UVGroup UVGroup;

        /// <summary>Texture type group this island belongs to. / 此岛所属的贴图类型组。</summary>
        internal TextureTypeGroup TypeGroup;

        /// <summary>Whether this island was merged from overlapping islands. / 此岛是否由重叠岛合并而来。</summary>
        internal bool WasMerged;

        /// <summary>Anisotropic scale factors for fine-tuning. / 各向异性缩放因子。</summary>
        internal Vector2 AnisotropicScale = Vector2.one;

        /// <summary>Computed pixel density (pixels per meter on the mesh). / 计算的像素密度。</summary>
        internal float PixelDensity;

        public override string ToString() => $"Island#{Id} ch{UVChannel} mesh={SourceMesh?.name} tex={SourceTexture?.name}";
    }

    /// <summary>
    /// A UV group: all textures that share the same UV coordinates.
    /// Guarantees identical UV placement across all atlas maps.
    /// UV 组：共享相同 UV 坐标的所有贴图。保证在所有图集上的 UV 位置相同。
    /// </summary>
    internal sealed class UVGroup
    {
        internal int Id;

        /// <summary>Islands that belong to this UV group (same UV region). / 属于此 UV 组的岛。</summary>
        internal List<UVIsland> Islands = new List<UVIsland>();

        /// <summary>All textures referenced by this UV group (color, normal, mask, animation-switched). / 此 UV 组引用的所有贴图。</summary>
        internal HashSet<Texture2D> AllTextures = new HashSet<Texture2D>();

        /// <summary>The maximum original texture dimension (caps the group's target size). / 最大原始贴图尺寸。</summary>
        internal int MaxOriginalDimension;

        /// <summary>Computed target dimension after quality scaling (wood-barrel effect: max across textures). / 质量缩放后计算的目标尺寸（木桶效应）。</summary>
        internal int TargetDimension;

        /// <summary>Whether this group is on the whitelist (skip optimization). / 此组是否在白名单中。</summary>
        internal bool IsWhitelisted;

        /// <summary>Whether to generate atlas for this group. / 是否为此组生成图集。</summary>
        internal bool GenerateAtlas = true;
    }

    /// <summary>
    /// A texture type group: textures with the same combination of companion maps
    /// (normal, mask), color space, and filter mode. Grouped to maximize atlas utilization.
    /// 贴图类型组：具有相同配套贴图（法线、蒙版）、色彩空间和 filterMode 组合的贴图。
    /// </summary>
    internal sealed class TextureTypeGroup
    {
        internal int Id;

        /// <summary>Does this group have normal maps? / 此组是否有法线贴图？</summary>
        internal bool HasNormal;
        /// <summary>Does this group have mask maps? / 此组是否有蒙版贴图？</summary>
        internal bool HasMask;
        /// <summary>Color space of this group. / 此组的色彩空间。</summary>
        internal ColorSpace ColorSpace;
        /// <summary>Filter mode. / 过滤模式。</summary>
        internal FilterMode FilterMode;

        /// <summary>Primary (color) textures in this group. / 此组中的主色贴图。</summary>
        internal List<Texture2D> PrimaryTextures = new List<Texture2D>();

        /// <summary>Companion normal textures (keyed by primary texture). / 配套法线贴图。</summary>
        internal Dictionary<Texture2D, Texture2D> NormalMaps = new Dictionary<Texture2D, Texture2D>();

        /// <summary>Companion mask textures (keyed by primary texture). / 配套蒙版贴图。</summary>
        internal Dictionary<Texture2D, Texture2D> MaskMaps = new Dictionary<Texture2D, Texture2D>();

        /// <summary>UV groups contained in this type group. / 此类型组包含的 UV 组。</summary>
        internal List<UVGroup> UVGroups = new List<UVGroup>();

        /// <summary>All islands across all textures in this group (ready for packing). / 此组所有贴图的全部岛。</summary>
        internal List<UVIsland> AllIslands = new List<UVIsland>();

        /// <summary>Assigned atlas(es). / 分配的图集。</summary>
        internal List<GeneratedAtlas> Atlases = new List<GeneratedAtlas>();

        public string Signature => $"N{(HasNormal ? 1 : 0)}M{(HasMask ? 1 : 0)}C{(int)ColorSpace}F{(int)FilterMode}";
    }

    /// <summary>
    /// A generated atlas texture and its metadata.
    /// 生成的图集贴图及其元数据。
    /// </summary>
    internal sealed class GeneratedAtlas
    {
        internal Texture2D Texture;
        internal string Name;
        internal int Width;
        internal int Height;
        internal TextureCategory Category;
        internal float Utilization; // 0-1
        internal List<UVIsland> PlacedIslands = new List<UVIsland>();
        internal long RasterAreaTotal;
        internal long TotalArea;
        internal bool IsNPOT;

        // Companion atlas (e.g., normal map atlas paired with this color atlas)
        // 配套图集（例如与此主色图集配对的法线图集）
        internal GeneratedAtlas CompanionNormal;
        internal GeneratedAtlas CompanionMask;
    }

    /// <summary>
    /// Information about a material slot on a renderer, including animation-referenced
    /// materials and textures.
    /// 渲染器上材质槽的信息，包括动画引用的材质和贴图。
    /// </summary>
    internal sealed class MaterialSlotInfo
    {
        internal Renderer Renderer;
        internal int SlotIndex;
        internal Material CurrentMaterial;
        internal List<Material> AnimationMaterials = new List<Material>();
        internal List<Texture2D> AllReferencedTextures = new List<Texture2D>();

        // Whether this slot is enabled or animated
        internal bool IsEnabled = true;
        internal bool IsAnimatedEnabled;

        /// <summary>All materials (current + animation-switched) for this slot. / 此槽的所有材质。</summary>
        internal IEnumerable<Material> AllMaterials
        {
            get
            {
                if (CurrentMaterial != null) yield return CurrentMaterial;
                foreach (var m in AnimationMaterials)
                    if (m != null && m != CurrentMaterial)
                        yield return m;
            }
        }
    }

    /// <summary>
    /// Tracks texture references discovered during scanning, including which material property
    /// references them and in what category (color, normal, mask).
    /// 扫描期间发现的贴图引用，包括引用它们的材质属性和类别。
    /// </summary>
    internal sealed class TextureReference
    {
        internal Texture2D Texture;
        internal TextureCategory Category;
        internal string PropertyName;
        internal Material Material;
        internal int RendererId;
        internal int SlotIndex;
        internal int UVChannel;

        // Import settings hash for dedup
        internal string ImportHash;

        // ST/transform tracking
        internal bool HasSTTransform;
        internal Vector4 STOffsetScale = new Vector4(1, 1, 0, 0);

        // Alpha mode tracking
        internal AlphaMode AlphaMode = AlphaMode.Opaque;
        internal float Cutoff = 0.5f;
    }

    internal enum AlphaMode
    {
        Opaque,
        Cutout,
        Blend,
        TransClipping
    }

    /// <summary>
    /// Complete scan result of the avatar.
    /// Avatar 的完整扫描结果。
    /// </summary>
    internal sealed class AvatarScanResult
    {
        internal List<Renderer> Renderers = new List<Renderer>();
        internal List<MaterialSlotInfo> MaterialSlots = new List<MaterialSlotInfo>();
        internal Dictionary<Texture2D, TextureReference> TextureReferences = new Dictionary<Texture2D, TextureReference>();
        internal HashSet<Object> WhitelistedObjects = new HashSet<Object>();
        internal HashSet<Texture2D> WhitelistedTextures = new HashSet<Texture2D>();
        internal List<string> Warnings = new List<string>();
        internal Dictionary<Texture2D, Texture2D> DedupMapping = new Dictionary<Texture2D, Texture2D>();
    }
}
