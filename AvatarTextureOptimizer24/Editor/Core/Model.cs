// ============================================================================
// Model.cs — 核心数据模型 / Core data model
// (EN) Shared types used across all pipeline stages: textures, renderers,
//      material slots, UV islands, UV groups, and texture type groups.
// (ZH) 供全部管线阶段共享的类型：贴图、渲染器、材质槽、UV 岛、UV 组、类型组。
// ============================================================================

using System;
using System.Collections.Generic;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer
{
    // -------------------------------------------------------------------------
    // 贴图用途 / texture usage (what role a texture plays in a shader)
    // -------------------------------------------------------------------------
    public enum ATOTextureUsage
    {
        /// <summary>(EN) Base/albedo color texture. (ZH) 主色/反照率贴图。</summary>
        MainColor = 0,
        /// <summary>(EN) Normal map. (ZH) 法线贴图。</summary>
        NormalMap = 1,
        /// <summary>(EN) Mask / grayscale single-purpose texture. (ZH) 蒙版/灰度贴图。</summary>
        Mask = 2,
        /// <summary>(EN) Grayscale data texture (linear-space, channel-specific). (ZH) 灰度数据贴图（线性空间）。</summary>
        Grayscale = 3,
        /// <summary>(EN) Unclassified / special purpose — treated as whitelist. (ZH) 未分类/特殊用途——按白名单处理。</summary>
        Other = 4,
    }

    // -------------------------------------------------------------------------
    // 贴图引用 / texture reference (identity = asset + import signature + pixels)
    // -------------------------------------------------------------------------
    public class ATOTextureRef
    {
        public Texture2D Texture;
        public string ImportSignature;   // 导入设置签名 / import settings signature
        public string PixelSignature;    // 像素内容签名 / pixel content signature
        public ATOTextureUsage Usage = ATOTextureUsage.Other;
        public ATOTextureClass Classification = ATOTextureClass.Opaque;
        public bool Whitelisted;

        // 整图缩放（无图集模式）/ whole-texture scale (no-atlas mode)
        public float WholeScaleX = 1f;
        public float WholeScaleY = 1f;

        /// <summary>(EN) Full dedup identity. (ZH) 完整去重标识。</summary>
        public string DedupIdentity => ImportSignature + "|" + PixelSignature;

        public override string ToString() => Texture != null ? Texture.name : "(null)";
    }

    // -------------------------------------------------------------------------
    // 材质槽内的单个贴图引用 / a single texture reference within a material slot
    // -------------------------------------------------------------------------
    public class ATOSlotTexture
    {
        public ATOTextureRef Ref;
        public string PropertyName;      // shader 属性名 / shader property name
        public int UvChannel;            // 使用的 UV 通道 / UV channel used
        public bool HasTransform;        // 存在 ST 缩放/平移/旋转 / has ST scale/offset/rotation
        public bool SpecialPurpose;      // 特殊用途（贴花等）/ special purpose (decal etc.)

        public bool SafeToOptimize => !HasTransform && !SpecialPurpose && !Ref.Whitelisted;
    }

    // -------------------------------------------------------------------------
    // 材质槽 / a material slot on a renderer
    // -------------------------------------------------------------------------
    public class ATOSlot
    {
        public int SlotIndex;
        public Material Material;
        public List<ATOSlotTexture> Textures = new List<ATOSlotTexture>();

        // 动画分析结果 / animation analysis results
        public bool AnimatedRenderMode;                 // 渲染模式/Cutoff 被动画修改 / render mode or cutoff animated
        public float MinCutoff = 0.5f;                  // 动画中出现的最严苛 Cutoff / strictest cutoff seen in animation
        public List<Material> SwitchedMaterials = new List<Material>(); // 动画切换进来的材质 / materials switched in via animation

        /// <summary>(EN) Find a slot-texture by property name and UV channel. (ZH) 按属性名与 UV 通道查找槽内贴图。</summary>
        public ATOSlotTexture Find(string propertyName, int uvChannel)
        {
            foreach (var t in Textures)
                if (t.PropertyName == propertyName && t.UvChannel == uvChannel) return t;
            return null;
        }
    }

    // -------------------------------------------------------------------------
    // 渲染器信息 / renderer info (SkinnedMeshRenderer or MeshRenderer)
    // -------------------------------------------------------------------------
    public class ATORendererInfo
    {
        public Renderer Renderer;
        public Mesh Mesh;
        public bool IsSkinned;
        public bool EnabledByDefault;
        public bool EnabledByAnimation;
        public List<ATOSlot> Slots = new List<ATOSlot>();

        /// <summary>(EN) Which UV channels are present in the mesh. (ZH) 网格中存在的 UV 通道。</summary>
        public bool[] UvChannelPresent = new bool[8];

        /// <summary>(EN) Max animated scale (per-axis) for area computation. (ZH) 动画最大缩放（逐轴），用于面积计算。</summary>
        public Vector3 AnimScale = Vector3.one;

        public override string ToString() => Renderer != null ? Renderer.name : "(null)";
    }

    // -------------------------------------------------------------------------
    // UV 岛 / a UV island (connected component of triangles sharing vertices)
    // -------------------------------------------------------------------------
    public class ATOUVIsland
    {
        public int UvChannel;
        public List<int> Triangles = new List<int>();   // mesh triangle indices (global, across submeshes)
        public List<int> TriangleVerts = new List<int>(); // per-triangle vertex indices (3 per triangle)
        public List<Vector2> TriangleUVs = new List<Vector2>(); // per-triangle UVs (raw, 3 per triangle)
        public Rect Bounds;                             // UV-space bounding box
        public float WorldArea;                         // world-space surface area (unscaled)
        public int PixelWidth;                          // pixel width at source resolution
        public int PixelHeight;                         // pixel height at source resolution
        public HashSet<int> Submeshes = new HashSet<int>(); // submeshes this island touches
        public Vector2 Translation = Vector2.zero;      // UV normalization offset (out-of-bounds handling)
        public bool CrossesWrapSeam;                    // true if island spans >1 in UV → whitelist
        public float MaxAreaScale = 1f;                 // max animation scale factor (for area)
        public float MaxBlendArea;                      // max world area under blendshapes (100)
        public bool HasUnsafeReference;                 // referenced by whitelisted/ST-transform/special texture → skip atlas

        // 引用该岛的贴图 / textures referencing this island
        public List<ATOTextureRef> ReferencingTextures = new List<ATOTextureRef>();

        // 质量缩放结果 / quality-scaling result (filled in quality stage)
        public float ScaleX = 1f;
        public float ScaleY = 1f;
        public bool SkipScaling;                        // quality target == 1
        public int ScaledPixelW = 1;                    // scaled pixel size (filled in pack stage)
        public int ScaledPixelH = 1;

        // 用于装箱的位掩码 / bitmask for packing (filled in pack stage)
        public bool[] RasterizedMask;
        public int RasterW, RasterH;
        public int RasterX, RasterY;                    // placement in atlas
        public bool Rotated90;

        /// <summary>(EN) Short side of the bounding box in pixels. (ZH) 包围盒短边（像素）。</summary>
        public int ShortSidePx => Mathf.Min(PixelWidth, PixelHeight);
    }

    // -------------------------------------------------------------------------
    // UV 组 / UV group: the same UV (renderer + channel + island set) referenced
    // by multiple textures. All textures in a UV group must land at the SAME
    // position across their atlases so UVs stay consistent.
    // (ZH) UV 组：同一 UV（渲染器+通道+岛集合）被多张贴图引用。组内所有贴图
    //      在其图集上的位置必须一致，保证 UV 一致。
    // -------------------------------------------------------------------------
    public class ATOUVGroup
    {
        public string Key;                      // mesh + channel + texture-set identity
        public Mesh Mesh;                       // 共享网格（岛按网格去重）/ shared mesh (islands deduped per mesh)
        public List<ATORendererInfo> Renderers = new List<ATORendererInfo>(); // 使用该网格的渲染器
        public int UvChannel;
        public List<ATOUVIsland> Islands = new List<ATOUVIsland>();
        public List<ATOTextureRef> Textures = new List<ATOTextureRef>(); // 共享该 UV 的贴图
        public ATOTextureProfile Profile;       // 档案（法线/蒙版/色彩空间/filterMode）

        // 质量缩放结果 / quality-scaling result (filled in quality stage)
        public float ScaleX = 1f;
        public float ScaleY = 1f;
    }

    /// <summary>(EN) Texture profile used to derive the type-group key. (ZH) 用于推导类型组键的贴图档案。</summary>
    public struct ATOTextureProfile
    {
        public bool HasNormalMap;
        public bool HasMaskMap;
        public bool Srgb;
        public FilterMode FilterMode;

        public string ToKey()
        {
            return (HasNormalMap ? "N" : "-") + (HasMaskMap ? "M" : "-") + (Srgb ? "S" : "L") + "_" + FilterMode;
        }
    }

    // -------------------------------------------------------------------------
    // 贴图类型组 / texture type group: textures that should be atlased together
    // because they share the same "special" characteristics (normal/mask presence,
    // color space, filter mode). Prevents wasted atlas space.
    // (ZH) 贴图类型组：因共享相同“特殊”特征（法线/蒙版存在、色彩空间、filterMode）
    //      而应一起装箱的贴图，避免图集空间浪费。
    // -------------------------------------------------------------------------
    public class ATOTextureTypeGroup
    {
        public string Key;                       // 组合键 / combination key
        public ATOTextureUsage PrimaryUsage;
        public bool HasNormalMap;
        public bool HasMaskMap;
        public bool Srgb;
        public FilterMode FilterMode;
        public List<ATOTextureRef> Textures = new List<ATOTextureRef>();
    }

    // -------------------------------------------------------------------------
    // 白名单判定结果 / whitelist decision
    // -------------------------------------------------------------------------
    public static class ATOWhitelist
    {
        /// <summary>(EN) Objects that skip ALL optimization. (ZH) 跳过所有优化的对象。</summary>
        public static HashSet<UnityEngine.Object> Set = new HashSet<UnityEngine.Object>();

        /// <summary>(EN) Check if an object (mesh/material/texture/animation) is whitelisted. (ZH) 判断对象是否在白名单内。</summary>
        public static bool Contains(UnityEngine.Object obj)
        {
            return obj != null && Set.Contains(obj);
        }

        /// <summary>(EN) Check if a texture is referenced by any whitelisted object. (ZH) 判断贴图是否被白名单对象引用。</summary>
        public static bool TextureWhitelisted(Texture2D tex, ATORendererInfo renderer)
        {
            if (tex == null) return true;
            if (Contains(tex)) return true;
            if (renderer != null && Contains(renderer.Renderer)) return true;
            if (renderer != null && renderer.Renderer != null && Contains(renderer.Renderer.gameObject)) return true;
            return false;
        }
    }
}
