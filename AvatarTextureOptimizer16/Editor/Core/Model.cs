using System;
using System.Collections.Generic;
using UnityEngine;

namespace AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Texture category, used for compression format selection and type grouping.
    /// 贴图分类，用于压缩格式选择与类型分组。
    /// </summary>
    public enum ATOTextureCategory
    {
        OpaqueColor,
        TransparentColor,
        Normal,
        Gray,
    }

    /// <summary>
    /// Special-map relationship flags for a color texture. / 主色贴图的特殊贴图关系标记。
    /// </summary>
    [Flags]
    public enum ATOSpecialFlags
    {
        None = 0,
        HasNormal = 1,
        HasMask = 2,
    }

    /// <summary>
    /// A unique texture after pixel+import deduplication. / 去重后的唯一贴图。
    /// </summary>
    public sealed class TextureEntry
    {
        public Texture2D texture;
        /// <summary>CPU-readable copy for pixel operations (created lazily). / 用于像素操作的 CPU 可读副本（惰性创建）。</summary>
        public Texture2D readable;
        public int width;
        public int height;
        public long pixelHash;             // content hash / 内容哈希
        public string importSignature;     // import settings signature / 导入设置签名
        public ATOTextureCategory category;
        public bool hasAlpha;
        public bool whitelisted;
        public bool isLinear;              // color space (false = sRGB) / 色彩空间（false=sRGB）
        public FilterMode filterMode;
        public ATOSpecialFlags specialFlags;   // for color textures / 主色贴图的特殊关系

        /// <summary>Normal map encoding: 0=DXT5nm, 1=BC5, 2=RGB. / 法线贴图编码：0=DXT5nm, 1=BC5, 2=RGB。</summary>
        public int normalEncoding = 0;

        /// <summary>Bitmask of channels used by a gray texture (R=1,G=2,B=4,A=8). / 灰度贴图使用通道位掩码。</summary>
        public int grayChannelMask = 7;

        /// <summary>Materials referencing this texture, with property + UV channel. / 引用此贴图的材质（含属性与 UV 通道）。</summary>
        public readonly List<TextureReference> references = new List<TextureReference>();

        public string DisplayName => texture != null ? texture.name : "<null>";

        public override string ToString() => DisplayName;
    }

    /// <summary>
    /// A single (material, property, uv channel) reference to a texture. / 对贴图的单个（材质、属性、UV通道）引用。
    /// </summary>
    public sealed class TextureReference
    {
        public Material material;
        public string propertyName;
        public int uvChannel;
        public Vector4 st = new Vector4(1, 1, 0, 0); // scale.xy, offset.zw
        public bool stIsIdentity = true;
    }

    /// <summary>
    /// A connected UV island within one mesh + UV channel. / 某网格某 UV 通道内的一个连通 UV 岛。
    /// </summary>
    public sealed class UvIsland
    {
        public int islandIndex;
        public int uvChannel;
        public int submesh;                                     // material slot index / 材质槽索引
        public List<int> triangleIndices = new List<int>();   // mesh triangles / 网格三角形索引
        public Rect bounds;                                    // UV bounds (min,size) / UV 包围盒
        public float area;                                     // UV area / UV 面积
        public float localArea;                                // local-space area (m²) / 本地空间面积（米²）
        public bool outOfRangeNeedsRepeat;                     // crossed wrap seam / 跨 wrap 缝

        /// <summary>Flattened UV coordinates of island triangles. / 岛三角形的展平 UV 坐标。</summary>
        public List<Vector2> uvCoordinates = new List<Vector2>();

        /// <summary>UV coordinates normalized to the island bounds ([0,1] local). / 归一化到岛包围盒的局部 UV（[0,1]）。</summary>
        public List<Vector2> normalizedUV = new List<Vector2>();
    }

    /// <summary>
    /// A UV group: one mesh island plus all textures sampling it. All member textures share the
    /// same unified UV-space scale (worst-case "barrel" rule) and must be placed at identical
    /// UV positions across their respective atlases.
    /// UV 组：一个网格岛 + 采样它的所有贴图。成员贴图共享统一的 UV 空间缩放（木桶取最严），
    /// 且必须在各自图集中占据相同的 UV 位置。
    /// </summary>
    public sealed class UvGroup
    {
        public string id;
        public Renderer renderer;
        public Mesh sourceMesh;
        public UvIsland island;                                 // shared island / 共享岛
        public readonly List<TextureEntry> textures = new List<TextureEntry>();

        /// <summary>Unified UV-space scale applied to all member textures (worst case wins). / 统一 UV 缩放（木桶取最严）。</summary>
        public Vector2 scale = Vector2.one;

        /// <summary>Placement of each member texture's island in its atlas. / 各成员贴图的岛在图集中的放置。</summary>
        public readonly Dictionary<TextureEntry, AtlasPlacedIsland> placements =
            new Dictionary<TextureEntry, AtlasPlacedIsland>();

        /// <summary>Whether the group has a normal map among its textures. / 组内是否含法线贴图。</summary>
        public bool HasNormal { get { foreach (var t in textures) if (t.category == ATOTextureCategory.Normal) return true; return false; } }
    }

    /// <summary>
    /// A placed island inside a generated atlas. / 生成图集内一个已放置的岛。
    /// </summary>
    public sealed class AtlasPlacedIsland
    {
        public UvIsland island;
        public Rect dstRect;            // destination rect in atlas UV (0..1) / 图集 UV 中的目标矩形（0..1）
        public int rotation;            // 0/90/180/270 / 旋转角
        public TextureEntry source;     // source texture / 来源贴图
    }

    /// <summary>
    /// A generated atlas. / 一个生成的图集。
    /// </summary>
    public sealed class AtlasResult
    {
        public string name;                        // ATO_... / 以 ATO_ 开头
        public Texture2D texture;
        public int width;
        public int height;
        public ATOTextureCategory category;
        public bool hasAlpha;
        public List<AtlasPlacedIsland> islands = new List<AtlasPlacedIsland>();
        public List<TextureEntry> sources = new List<TextureEntry>();

        /// <summary>
        /// Atlas fill ratio = placed island area / atlas area (in UV space). / 利用率 = 已放岛面积 / 图集面积（UV 空间）。
        /// </summary>
        public float Utilization
        {
            get
            {
                if (width <= 0 || height <= 0) return 0f;
                float used = 0f;
                foreach (var i in islands) used += i.dstRect.width * i.dstRect.height;
                return used;
            }
        }
    }

    /// <summary>
    /// A single entry in the final report. / 最终报告中的单条记录。
    /// </summary>
    public sealed class ATOReportEntry
    {
        public string stage;          // stage name / 阶段名
        public long elapsedMs;        // elapsed time / 耗时
        public string summary;        // summary line / 概览行
        public List<string> details = new List<string>();  // foldable details / 折叠细节
    }

    /// <summary>
    /// Texture category extension helpers. / 贴图分类扩展助手。
    /// </summary>
    public static class ATOTextureCategoryExtensions
    {
        public static bool IsColor(this ATOTextureCategory c) =>
            c == ATOTextureCategory.OpaqueColor || c == ATOTextureCategory.TransparentColor;
    }
}
