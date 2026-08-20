using System.Collections.Generic;
using UnityEngine;

namespace Fosa.ATO.Editor
{
    // =====================================================================
    // 数据模型。Data model for the whole pipeline.
    // =====================================================================

    /// <summary>贴图在某个材质槽上的一处引用（含用途分类）。A single texture use on a material slot.</summary>
    public class ATOTextureSlot
    {
        public Renderer renderer;         // 所属渲染器 / owning renderer
        public int materialSlotIndex;     // 材质槽索引 / material slot index
        public Material material;         // 材质 / the material (cloned during build)
        public string propertyName;       // 着色器属性名 / shader property name
        public ATOTextureType type;       // 用途分类 / classified texture type
        public int uvChannel;             // 采样的 UV 通道 / UV channel used
        public Texture2D texture;         // 原始贴图资产 / original texture asset
        public Vector4 st;                // 属性上的 ST（必须为 (1,1,0,0) 才处理）/ property ST
        public bool isNormalMap;          // 是否为法线贴图 / is it a normal map (decoded specially)
    }

    /// <summary>去重后的贴图条目。Deduplicated texture entry.</summary>
    public class ATOTextureEntry
    {
        public Texture2D texture;         // 规范贴图 / canonical texture
        public string importKey;          // 导入设置签名 / import settings signature
        public bool whitelisted;          // 白名单标志 / whitelist flag
        public bool hasAlpha;             // 是否有 alpha 通道 / has alpha channel
        public bool sRGB;                 // 色彩空间 / color space
        public FilterMode filterMode;     // 过滤模式 / filter mode
        public bool mipmaps;              // 是否启用 mipmap / mipmap enabled
        public int width, height;         // 像素尺寸 / pixel size
        public ATOTextureEntry canonicalOf; // 去重后指向规范条目 / points to canonical if deduped
        public List<ATOTextureSlot> slots = new List<ATOTextureSlot>(); // 引用它的槽 / referencing slots

        public bool IsDuplicate => canonicalOf != null && canonicalOf != this;
        public ATOTextureEntry Canonical => canonicalOf ?? this;
    }

    /// <summary>
    /// UV 组：同一 UV（同一渲染器 + 材质槽 + UV 通道）被采样的所有贴图。
    /// 保证这些贴图在不同图集上位置一致。
    /// UV group: all textures sampled by the same UV (renderer + slot + channel).
    /// </summary>
    public class ATOUVGroup
    {
        public Renderer renderer;
        public int materialSlotIndex;
        public int uvChannel;
        public List<ATOTextureSlot> slots = new List<ATOTextureSlot>();

        public string Key => $"{renderer.GetInstanceID()}:{materialSlotIndex}:{uvChannel}";
    }

    /// <summary>
    /// 贴图类型组：同类型（含色彩空间/过滤模式）的贴图共同生成图集。
    /// Texture type group: same type (+color space/filter) share atlas(es).
    /// </summary>
    public class ATOTextureTypeGroup
    {
        public ATOTextureType type;
        public bool sRGB;
        public FilterMode filterMode;
        public List<ATOTextureEntry> textures = new List<ATOTextureEntry>();

        public string Key => $"{(int)type}:{sRGB}:{(int)filterMode}";
    }

    /// <summary>UV 岛。A UV island (connected set of triangles).</summary>
    public class ATOIsland
    {
        public ATOUVGroup uvGroup;
        public ATOTextureEntry texture;
        public Mesh mesh;                 // 网格 / mesh
        public int[] triangles;           // 三角形索引 / triangle indices
        public Vector2[] uv;              // 该岛的 UV 坐标（局部，相对岛包围盒左下角）/ island-local UVs
        public Rect bounds;               // 原始 UV 包围盒（在贴图 UV 空间）/ original UV bounds
        public float worldArea;           // 世界空间面积（含形态键/缩放的最大值）/ max world area (morph/scale)
        public bool isSolidColor;         // 是否纯色岛 / solid-color island
        public Color solidColor;
        public bool skipScale;            // 目标质量为 1 时跳过缩放 / skip scaling when target quality == 1
        public ATOTextureType type;       // 用途分类 / texture type (from the slot)
        public bool isNormalMap;          // 是否法线贴图 / normal-map flag

        // 装箱结果 / packing result
        public Vector2 packedUv;          // 图集中的 UV 左下角 / atlas UV bottom-left
        public Vector2 packedScale;       // 缩放 / scale applied
        public Rect packedRect;           // 图集 UV 矩形 / atlas UV rect
        public ATOTextureEntry atlas;     // 被装入的图集 / atlas it was packed into
    }

    /// <summary>图集（输出）。An output atlas.</summary>
    public class ATOAtlas
    {
        public ATOTextureTypeGroup group;
        public int width, height;
        public List<ATOIsland> islands = new List<ATOIsland>();
        public Texture2D texture;         // 生成的图集贴图 / generated atlas texture
        public string name;               // ATO_ 开头 / starts with ATO_
        internal bool[] _grid;            // 装箱占位网格（cell 单位）/ packing occupancy grid (cells)
    }

    /// <summary>整个构建的共享状态。Whole-build shared state.</summary>
    public class ATOBuildData
    {
        public AvatarTextureOptimizer component;
        public List<Renderer> renderers = new List<Renderer>();
        public List<ATOTextureSlot> allSlots = new List<ATOTextureSlot>();
        public Dictionary<Texture2D, ATOTextureEntry> entriesByTexture = new Dictionary<Texture2D, ATOTextureEntry>();
        public List<ATOTextureEntry> entries = new List<ATOTextureEntry>();
        public Dictionary<string, ATOUVGroup> uvGroups = new Dictionary<string, ATOUVGroup>();
        public List<ATOIsland> allIslands = new List<ATOIsland>();
        public List<ATOTextureTypeGroup> typeGroups = new List<ATOTextureTypeGroup>();
        public List<ATOAtlas> atlases = new List<ATOAtlas>();
        public HashSet<Object> whitelistSet = new HashSet<Object>();
    }
}
