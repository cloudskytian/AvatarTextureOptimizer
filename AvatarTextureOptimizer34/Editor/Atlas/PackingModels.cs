// AvatarTextureOptimizer - PackingModels
// EN: Packing data models: template layout (per UV group), blocks (per texture), atlas layouts.
// CN: 装箱数据模型：模板布局（每 UV 组）、块（每贴图）、图集布局。
using System.Collections.Generic;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer
{
    /// <summary>EN: One island's placement inside a template layout. / CN: 一个岛在模板布局中的位置。</summary>
    public sealed class TemplateEntry
    {
        public Island island;
        public int x, y;          // 单元（4px）
        public int w, h;          // 单元
        public int rotation;      // 旋转象限（0/90/180/270）
    }

    /// <summary>
    /// EN: Template layout of one UV group: the island rects shared by every texture instance of the group
    /// (same UV maps to the same position across atlases).
    /// CN: 一个 UV 组的模板布局：组内每个贴图实例共享的岛矩形（同一 UV 在所有图集同位）。
    /// </summary>
    public sealed class TemplateLayout
    {
        public UvGroup group;
        public readonly List<TemplateEntry> entries = new List<TemplateEntry>();
        public int cellsW, cellsH;   // 布局包围盒（单元）
    }

    /// <summary>EN: A texture's block inside an atlas: its template scaled by the type scale. / CN: 贴图在图集中的块：模板按类型缩放。</summary>
    public sealed class AtlasBlock
    {
        public TextureRef tex;
        public TemplateLayout layout;
        public float scale;          // 类型均匀缩放（整体布局）
        public int x, y;             // 在图集中的位置（单元）
        public int w, h;             // 尺寸（单元）
        public long areaCells;
    }

    /// <summary>EN: Result of packing: atlases + per-island rects for baking. / CN: 装箱结果：图集 + 供烘焙的岛矩形。</summary>
    public sealed class PackingResult
    {
        public readonly List<PackedAtlas> atlases = new List<PackedAtlas>();
    }

    /// <summary>EN: One packed atlas (per type group + usage). / CN: 一个装箱图集（每类型组 + 用途）。</summary>
    public sealed class PackedAtlas
    {
        public TypeGroup group;
        public TextureUsage usage;
        public int width, height;              // 像素
        public int cellsW, cellsH;             // 单元（= width/4）
        public CellMask occ;                   // 占用位掩码（装箱中）
        public readonly List<PackedIsland> islands = new List<PackedIsland>();
        public long usedCells;
        public int sourceTextureCount;
        public string Name;

        public void Dispose() { occ?.Dispose(); occ = null; }
    }

    /// <summary>EN: Island placement within a packed atlas (pixel rect + source info). / CN: 岛在装箱图集中的位置（像素矩形 + 来源）。</summary>
    public sealed class PackedIsland
    {
        public Island island;
        public TextureRef tex;
        public Rect rect;            // 图集内像素矩形（内容区）
        public int rotation;         // 旋转象限（0/90/180/270）
        public float scaleX, scaleY; // 相对原贴图像素
        public int padPx;
    }
}
