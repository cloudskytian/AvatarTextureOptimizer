// Core analysis data model shared across pipeline stages.
// / 流水线各阶段共享的核心分析数据模型。

using System.Collections.Generic;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor.analysis
{
    /// <summary>Texture role classification. / 贴图用途分类。</summary>
    public enum TextureRole
    {
        MainColor,   // 主色贴图 / main color
        Normal,      // 法线贴图 / normal map
        Mask,        // 蒙版/灰度贴图 / mask / grayscale
        Other,       // 其他（无法归类时按主色处理）/ other (treated like main color)
    }

    /// <summary>How a single material slot binds a texture. / 单个材质槽对一张贴图的绑定。</summary>
    public sealed class TexBinding
    {
        public Material Material;
        public string PropertyName;      // shader property, e.g. _MainTex / 着色器属性名
        public TextureRole Role;
        public Texture2D Texture;
        public int UvChannel;            // UV channel used by the shader for this texture (normally 0) / 该贴图使用的 UV 通道
        public bool TransparentBlend;    // render mode is Blend / 渲染模式为 Blend
        public bool TransparentCutout;   // render mode is Cutout / 渲染模式为 Cutout
        public float Cutoff;             // _Cutoff value for cutout / Cutout 模式的 _Cutoff 阈值
        public bool Animated;            // set by an animation clip / 由动画剪辑设置
    }

    /// <summary>A renderer (mesh) that we process. / 我们处理的一个渲染器（网格）。</summary>
    public sealed class MeshUsage
    {
        public Renderer Renderer;
        public Mesh Mesh;
        public bool Skinned;
        public Transform Transform;
        public float MaxAnimatedScale = 1f;   // max scale factor over animations / 动画最大缩放
        public readonly List<MeshSlot> Slots = new List<MeshSlot>();
        public bool EditorOnly;
        public bool AnimatedActive;            // enabled or animated-enabled / 被启用或动画启用
    }

    /// <summary>One material slot (submesh). / 一个材质槽（子网格）。</summary>
    public sealed class MeshSlot
    {
        public int SubMeshIndex;
        public Material Material;
        public readonly List<TexBinding> Bindings = new List<TexBinding>();
    }

    /// <summary>
    /// Deduplicated texture record. Identity = pixel content + import settings (both must match).
    /// / 去重后的贴图记录。身份 = 像素内容 + 导入设置（两者都相同才视为同一张）。
    /// </summary>
    public sealed class TexRecord
    {
        public Texture2D Texture;         // representative asset / 代表资产
        public int Width, Height;
        public bool HasAlpha;
        public bool IsNormalMap;          // importer says normal map / 导入器标记为法线贴图
        public bool IsSrgb = true;        // importer sRGB flag / 导入器 sRGB 标记
        public FilterMode FilterMode = FilterMode.Bilinear;
        public string Fingerprint;        // content hash + import fingerprint / 内容哈希 + 导入指纹
        public readonly List<TexBinding> Bindings = new List<TexBinding>(); // all usages / 所有使用处
        public bool Whitelisted;
        public bool Skipped;              // falls back to whole-texture scaling / 回退为整图缩放
        public string SkipReason;
        public float WholeScale = 1f;     // whole-texture scale (fallback / no-atlas path) / 整图缩放（回退/无图集路径）
        // Results filled by later stages / 由后续阶段填充的结果
        public Texture2D ResultTexture;   // atlas or scaled texture assigned to materials / 分配给材质的最终贴图
        public string ResultName;
    }

    /// <summary>A UV island (connected UV region of triangles). / 一个 UV 岛（三角形组成的连通 UV 区域）。</summary>
    public sealed class Island
    {
        public int Id;
        public int UvGroupId;
        public int UvChannel;
        public MeshData Owner;            // mesh data (UVs) for this island / 该岛的网格数据
        public readonly List<int> Triangles = new List<int>();  // triangle indices into mesh.triangles / 三角形索引
        public Vector2 Min, Max;          // UV bounds / UV 包围盒
        public bool Mirrored;             // winding reversed (mirrored UV) / 镜像（绕序反转）
        public float WorldArea;           // world-space area of the island triangles (for px/m) / 世界空间面积
        public float WorldSize;           // sqrt(WorldArea) in meters / 世界尺寸（米）
        public float OrigLongSidePx;      // long side of the UV bbox in source texels / UV 包围盒在原贴图上的长边像素
        public float OrigShortSidePx;     // short side / 短边像素
        public float DensityScaleMin;     // floor from min px/m / 最小像素密度决定的缩放下限
        public float DensityScaleMax;     // cap from max px/m (<=1) / 最大像素密度决定的缩放上限
        public float GroupScale;          // bucketed final scale for this island / 木桶效应后的最终缩放
        // Results / 结果
        public Rect ScaledRect;           // rect in source texture space after scaling / 缩放后在原贴图空间中的矩形
        public int AtlasX, AtlasY;        // atlas placement (pixels, top-left) / 图集位置
        public int AtlasW, AtlasH;
        public bool Rotated90;            // packed rotated by 90° / 装箱时旋转 90°
        public int AtlasIndex;            // atlas id / 图集 id
    }

    /// <summary>
    /// A UV group: all textures that share the same mesh UV coordinates must stay aligned across atlases.
    /// / UV 组：共享同一网格 UV 坐标的所有贴图，在不同图集上的位置必须一致。
    /// </summary>
    public sealed class UVGroup
    {
        public int Id;
        public MeshUsage Mesh;
        public int UvChannel;
        public readonly List<Island> Islands = new List<Island>();
        public readonly List<GroupTexture> Textures = new List<GroupTexture>();
        public bool Whitelisted;            // contains whitelisted involvement -> skip atlas for the group's other textures / 涉及白名单
        public float GroupScale = 1f;       // bucketing result: max scale across textures / 木桶效应后的组缩放
        public bool AllPureColor;           // all textures are pure color -> shortcut scale / 全部为纯色
        // Layout result / 布局结果
        public Rect[] LayoutRects;          // per-island atlas-space rects (0..1), shared by all atlases of this group / 每个岛在图集中的 UV 矩形
        public bool LayoutRotated;          // per-island rotation flag (bit array not needed: use island.Rotated90) / 旋转标记（使用 island.Rotated90）
    }

    /// <summary>One texture inside a UV group. / UV 组中的一张贴图。</summary>
    public sealed class GroupTexture
    {
        public TexRecord Record;
        public TextureRole Role;                              // primary role / 主用途
        public readonly List<TextureRole> Roles = new List<TextureRole>(); // all roles / 全部用途
        public float RequiredScale = 1f;                      // per-texture ideal scale / 该贴图理想缩放
        public bool PureColor;
        public bool SkipScaling;                              // quality==1 / 近无损跳过
        public string TypeGroupKey;                           // role+colorspace+filterMode group key / 类型组键
        public Texture2D SourceTexture;                       // texture to sample islands from (after dedup) / 采样源贴图
    }

    /// <summary>Result of the whole analysis. / 整个分析的结果。</summary>
    public sealed class AnalysisResult
    {
        public readonly List<MeshUsage> Meshes = new List<MeshUsage>();
        public readonly List<TexRecord> Textures = new List<TexRecord>();
        public readonly List<UVGroup> UvGroups = new List<UVGroup>();
        public readonly List<string> Warnings = new List<string>();
        public int WhitelistedTextureCount;
        public AnimationFacts Facts;
    }
}
