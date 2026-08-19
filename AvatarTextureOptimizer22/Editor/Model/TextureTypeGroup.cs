// AvatarTextureOptimizer
// File: Editor/Model/TextureTypeGroup.cs
//
// Texture type groups: textures that must be packed together into shared
// atlas(es) because they carry special companion textures (normal maps, masks).
// Example: when 10 main-color textures are atlased but only 1 of them has a
// normal map, a naive setup would waste 9/10 of the normal atlas. Textures
// with special companions are grouped by (type set, color space, filter mode)
// so every atlas is used efficiently. A texture used in both normal and
// non-normal materials is classified into the normal group.
//
// 贴图类型组：必须共同装箱成共享图集的贴图，因为它们带有特殊伴随贴图
// （法线贴图、蒙版）。例如：10 张主色贴图生成图集但仅 1 张有法线贴图时，
// 朴素方案会浪费法线图集 9/10 的面积。带特殊伴随贴图的贴图按（类型集合、
// 色彩空间、过滤模式）分组，使每张图集都被高效利用。同时存在于有法线与
// 无法线材质中的贴图归入有法线的类型组。

using System.Collections.Generic;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor.model
{
    /// <summary>
    /// Which special companion textures a source texture carries.
    /// 源贴图携带哪些特殊伴随贴图。
    /// </summary>
    [System.Flags]
    public enum CompanionFlags
    {
        None = 0,
        Normal = 1,   // 有对应法线贴图 / has a normal-map companion
        Mask = 2,     // 有对应蒙版贴图 / has a mask companion
    }

    /// <summary>
    /// A set of textures that must be packed into the same atlas(es).
    /// 必须被装箱进同一（批）图集的一组贴图。
    /// </summary>
    public sealed class TextureTypeGroup
    {
        /// <summary>Unique index of the type group. / 类型组的唯一索引。</summary>
        public int Index;

        /// <summary>Companion signature of this group (Normal/Mask presence). / 该组的伴随签名（是否存在法线/蒙版）。</summary>
        public CompanionFlags Companions;

        /// <summary>Color space shared by all members (sRGB vs linear). / 所有成员共享的色彩空间（sRGB 或线性）。</summary>
        public bool IsSRGB = true;

        /// <summary>Filter mode shared by all members. / 所有成员共享的过滤模式。</summary>
        public FilterMode FilterMode = FilterMode.Bilinear;

        /// <summary>All textures in this group (a texture appears in exactly one type group). / 组内所有贴图（一张贴图只属于一个类型组）。</summary>
        public readonly List<Texture2D> Textures = new List<Texture2D>();

        /// <summary>Whether any member has an alpha channel (drives transparent import settings). / 是否有成员带 alpha 通道（决定透明导入参数）。</summary>
        public bool HasAlpha;

        /// <summary>Sum of the rasterized areas of all member islands (for packing order). / 所有成员岛光栅化面积之和（用于装箱顺序）。</summary>
        public long TotalRasterArea;

        /// <summary>Description for logs. / 供日志使用的描述。</summary>
        public override string ToString() =>
            $"TypeGroup[{Index}] ({(IsSRGB ? "sRGB" : "linear")}/{FilterMode}, companions={Companions}, textures={Textures.Count})";
    }

    /// <summary>
    /// One generated atlas texture asset (or planned entry before creation).
    /// 一张已生成的图集贴图资产（或在创建前的计划条目）。
    /// </summary>
    public sealed class AtlasEntry
    {
        /// <summary>Unique index of this atlas (assigned when added). / 图集的唯一索引（加入时赋值）。</summary>
        public int Index;

        /// <summary>The canonical layout index this atlas mirrors. / 该图集镜像的规范布局索引。</summary>
        public int LayoutIndex = -1;

        /// <summary>Which type group this atlas serves. / 该图集服务的类型组。</summary>
        public TextureTypeGroup TypeGroup;

        /// <summary>Width of the atlas in pixels. / 图集宽度（像素）。</summary>
        public int Width;

        /// <summary>Height of the atlas in pixels. / 图集高度（像素）。</summary>
        public int Height;

        /// <summary>Packing padding used for this atlas. / 该图集使用的装箱 padding。</summary>
        public int Padding;

        /// <summary>Actual texture asset (created later in the bake). / 实际贴图资产（烘焙后期创建）。</summary>
        public Texture2D Texture;

        /// <summary>Source textures and their island counts (for the report). / 来源贴图及其岛数量（供报告使用）。</summary>
        public readonly Dictionary<Texture2D, int> Sources = new Dictionary<Texture2D, int>();

        /// <summary>Rasterized used area (before padding) for utilization computation. / 光栅化已用面积（padding 前），用于计算利用率。</summary>
        public long UsedArea;

        /// <summary>The name starts with ATO_. / 图集名称以 ATO_ 开头。</summary>
        public string Name => $"ATO_{TypeGroup.Index}_{Index}_{Width}x{Height}";

        /// <summary>0~1 utilization. / 0~1 利用率。</summary>
        public float Utilization => (Width > 0 && Height > 0) ? (float)((double)UsedArea / ((long)Width * Height)) : 0f;
    }
}
