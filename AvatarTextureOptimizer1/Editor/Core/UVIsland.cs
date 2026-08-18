// UVIsland.cs / UVIsland.cs
// Represents a single connected UV island on a mesh, plus its mapping to a texture slot.
// 表示网格上的单个连通UV岛及其到贴图槽位的映射。

using System.Collections.Generic;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.Editor.Core
{
    /// <summary>
    /// A connected UV island (a set of triangles that are connected via shared UV-edges, forming one "patch" on the source texture).
    /// 一个连通UV岛（通过共享UV边连接的一组三角面，在源贴图上形成一个"补丁"）。
    /// </summary>
    public class UVIsland
    {
        /// <summary>Source mesh / 源网格</summary>
        public Mesh SourceMesh;
        /// <summary>Renderer entry (holds WorkingMesh reference) / Renderer条目（持有WorkingMesh引用）</summary>
        public RendererEntry RendererEntry;
        /// <summary>Renderer this island comes from / 来源渲染器</summary>
        public Renderer Renderer;
        /// <summary>UV channel index (0..7) / UV通道索引 (0..7)</summary>
        public int UVChannel;
        /// <summary>Material slot index on the renderer / 渲染器上的材质槽索引</summary>
        public int MaterialSlot;
        /// <summary>Submesh index on the mesh / 网格上的子网格索引</summary>
        public int SubmeshIndex;
        /// <summary>Triangle vertex indices (GLOBAL vertex indices into the mesh) — flattened list of (i0,i1,i2) triples
        /// 三角形顶点索引（网格中的全局顶点索引）——展平的(i0,i1,i2)三元组列表</summary>
        public List<int> Triangles = new();
        /// <summary>Triangle LOCAL indices within the submesh (index into GetTriangles(submesh) array / 3)
        /// 子网格内三角面的局部索引（GetTriangles(submesh)数组/3的索引）</summary>
        public List<int> TriangleLocalIndices = new();
        /// <summary>Bounding box in source UV space / 源UV空间包围盒</summary>
        public Rect BoundsUV;
        /// <summary>Estimated world-space area after blendshape and animation scaling / 考虑形态键和动画缩放后的世界空间面积估算</summary>
        public float WorldArea;
        /// <summary>Original pixel bounding box size (w,h) in source texture / 源贴图上的原始像素包围盒大小(w,h)</summary>
        public Vector2Int OriginalPixelSize;
        /// <summary>Source texture (from material slot) at the time of analysis / 分析时该材质槽上的源贴图</summary>
        public Texture2D SourceTexture;
        /// <summary>Texture descriptor key / 贴图描述符键</summary>
        public TextureDescriptor SourceDescriptor;
        /// <summary>Required pixel density (pixels per meter) / 所需像素密度(px/m)</summary>
        public float RequiredPixelDensity;
        /// <summary>Whether this island is treated as whitelist / 是否被视为白名单</summary>
        public bool IsWhitelisted;
        /// <summary>Whether the island is on a transparent/cutout material / 是否在透明/Cutout材质上</summary>
        public bool IsAlpha;
        /// <summary>Cutoff value if this is a cutout / 如果是cutout的cutoff阈值</summary>
        public float Cutoff;
        /// <summary>Whether the UV source needs tangent rotation preservation (normal maps) / UV源是否需要保持切线旋转（法线贴图）</summary>
        public bool NeedsNormalRotation;

        /// <summary>After packing: position in atlas UV space / 装箱后在图集UV空间中的位置</summary>
        public Rect AtlasRect;
        /// <summary>After packing: atlas this island belongs to / 装箱后所属图集</summary>
        public Atlas.AtlasTexture AssignedAtlas;
        /// <summary>After packing: whether the island is rotated 90 degrees / 装箱后是否旋转了90度</summary>
        public bool Rotated;
        /// <summary>After quality scaling: target pixel size / 质量缩放后的目标像素尺寸</summary>
        public Vector2Int ScaledPixelSize;

        public override string ToString()
        {
            return $"UVIsland(mesh={SourceMesh?.name}, uv{UVChannel}, tris={Triangles.Count/3}, srcTex={SourceTexture?.name})";
        }
    }
}
