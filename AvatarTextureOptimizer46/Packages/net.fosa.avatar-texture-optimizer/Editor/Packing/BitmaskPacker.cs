// SPDX-License-Identifier: MIT
// EN: Shape aware packing using a 4 texel granularity occupancy bit mask.
// ZH: 使用 4 像素粒度占用位掩码的形状感知装箱。

using System;
using System.Collections.Generic;
using Net.Fosa.AvatarTextureOptimizer.Editor.Core;
using Net.Fosa.AvatarTextureOptimizer.Editor.Model;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor.Packing
{
    /// <summary>
    /// EN: A placement request: an island's scaled shape mask plus its identity.
    /// ZH: 一次放置请求：岛缩放后的形状掩码及其标识。
    /// </summary>
    public sealed class PackItem
    {
        /// <summary>EN: The island being placed. ZH: 被放置的岛。</summary>
        public UvIsland Island;
        /// <summary>EN: Occupancy mask at cell granularity, already dilated by padding. ZH: 单元粒度的占用掩码，已按 padding 膨胀。</summary>
        public bool[] Mask;
        /// <summary>EN: Mask width in cells. ZH: 掩码宽度（单元数）。</summary>
        public int Width;
        /// <summary>EN: Mask height in cells. ZH: 掩码高度（单元数）。</summary>
        public int Height;
        /// <summary>EN: Number of set cells, the primary sort key. ZH: 被置位的单元数，主排序键。</summary>
        public int Area;
    }

    /// <summary>
    /// EN: Bottom-left-first shape packer. It scans every candidate cell of the atlas and takes the
    ///     lowest, then leftmost position where the island's mask does not collide with what is already
    ///     placed. Because collision is tested against the actual shape rather than a bounding rectangle,
    ///     concave islands nest into each other.
    /// ZH: 左下优先（BLF）形状装箱器。它扫描图集的每一个候选单元，取岛掩码与已放置内容不冲突的
    ///     最低、其次最左的位置。由于碰撞检测针对实际形状而非包围矩形，凹形的岛可以互相嵌套。
    /// </summary>
    public sealed class BitmaskPacker
    {
        private const string Stage = "Pack";

        private readonly bool[] _occupied;
        private readonly int _cellsX;
        private readonly int _cellsY;

        /// <summary>EN: Atlas width in cells. ZH: 图集宽度（单元数）。</summary>
        public int CellsX => _cellsX;
        /// <summary>EN: Atlas height in cells. ZH: 图集高度（单元数）。</summary>
        public int CellsY => _cellsY;
        /// <summary>EN: Number of cells occupied so far. ZH: 目前已占用的单元数。</summary>
        public int OccupiedCells { get; private set; }

        /// <summary>EN: Creates a packer for an atlas of the given cell dimensions. ZH: 为给定单元尺寸的图集创建装箱器。</summary>
        public BitmaskPacker(int cellsX, int cellsY)
        {
            _cellsX = cellsX;
            _cellsY = cellsY;
            _occupied = new bool[cellsX * cellsY];
        }

        /// <summary>
        /// EN: Attempts to place an item. Returns true and fills in the island's placement on success.
        ///     Rotation by 90 degrees is attempted as a second option; the mask is transposed, which is
        ///     exactly the transform the blit shader applies, so the two can never disagree.
        /// ZH: 尝试放置一个条目。成功时返回 true 并填写岛的放置信息。
        ///     会把旋转 90 度作为第二选项；掩码转置正是 blit 着色器所应用的变换，因此两者不可能不一致。
        /// </summary>
        public bool TryPlace(PackItem item, bool allowRotation)
        {
            if (TryPlaceMask(item.Mask, item.Width, item.Height, out int x, out int y))
            {
                Commit(item.Mask, item.Width, item.Height, x, y);
                item.Island.AtlasOrigin = new Vector2Int(x, y);
                item.Island.Rotated = false;
                return true;
            }

            if (allowRotation)
            {
                var rotated = Transpose(item.Mask, item.Width, item.Height);
                if (TryPlaceMask(rotated, item.Height, item.Width, out x, out y))
                {
                    Commit(rotated, item.Height, item.Width, x, y);
                    item.Island.AtlasOrigin = new Vector2Int(x, y);
                    item.Island.Rotated = true;
                    return true;
                }
            }

            return false;
        }

        private bool TryPlaceMask(bool[] mask, int mw, int mh, out int outX, out int outY)
        {
            outX = outY = 0;
            if (mw > _cellsX || mh > _cellsY) return false;

            for (int y = 0; y <= _cellsY - mh; y++)
            {
                for (int x = 0; x <= _cellsX - mw; x++)
                {
                    if (Fits(mask, mw, mh, x, y))
                    {
                        outX = x;
                        outY = y;
                        return true;
                    }
                }
            }
            return false;
        }

        private bool Fits(bool[] mask, int mw, int mh, int ox, int oy)
        {
            for (int my = 0; my < mh; my++)
            {
                int rowBase = (oy + my) * _cellsX + ox;
                int maskBase = my * mw;
                for (int mx = 0; mx < mw; mx++)
                {
                    if (!mask[maskBase + mx]) continue;
                    if (_occupied[rowBase + mx]) return false;
                }
            }
            return true;
        }

        private void Commit(bool[] mask, int mw, int mh, int ox, int oy)
        {
            for (int my = 0; my < mh; my++)
            {
                int rowBase = (oy + my) * _cellsX + ox;
                int maskBase = my * mw;
                for (int mx = 0; mx < mw; mx++)
                {
                    if (!mask[maskBase + mx]) continue;
                    if (!_occupied[rowBase + mx])
                    {
                        _occupied[rowBase + mx] = true;
                        OccupiedCells++;
                    }
                }
            }
        }

        /// <summary>
        /// EN: Captures the current occupancy so a speculative placement can be rolled back exactly.
        ///     Used when a whole UV group is placed atomically: if any island of the group fails, every
        ///     island of that group must be undone.
        /// ZH: 捕获当前占用状态，使推测性放置可以被精确回滚。
        ///     用于整组原子放置：若组内任一岛失败，该组的所有岛都必须撤销。
        /// </summary>
        public (bool[] cells, int count) Snapshot() => ((bool[])_occupied.Clone(), OccupiedCells);

        /// <summary>EN: Restores a snapshot taken by <see cref="Snapshot"/>. ZH: 恢复由 <see cref="Snapshot"/> 捕获的快照。</summary>
        public void Restore((bool[] cells, int count) snapshot)
        {
            Array.Copy(snapshot.cells, _occupied, _occupied.Length);
            OccupiedCells = snapshot.count;
        }

        /// <summary>EN: Transposes a mask, which is the 90 degree rotation used by the blit. ZH: 转置掩码，即 blit 所使用的 90 度旋转。</summary>
        public static bool[] Transpose(bool[] mask, int w, int h)
        {
            var result = new bool[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    if (mask[y * w + x])
                        result[x * h + (h - 1 - y)] = true;
            return result;
        }

        /// <summary>
        /// EN: Rescales an island's original cell mask to its scaled size and dilates it by the padding,
        ///     producing the mask actually used for collision.
        /// ZH: 将岛的原始单元掩码重采样到其缩放后尺寸并按 padding 膨胀，
        ///     得到真正用于碰撞检测的掩码。
        /// </summary>
        public static PackItem BuildItem(UvIsland island, int cellSize, int paddingTexels)
        {
            int targetW = Mathf.Max(1, Mathf.CeilToInt(island.ScaledSize.x / (float)cellSize));
            int targetH = Mathf.Max(1, Mathf.CeilToInt(island.ScaledSize.y / (float)cellSize));
            int pad = Mathf.CeilToInt(paddingTexels / (float)cellSize);

            var scaled = new bool[targetW * targetH];
            for (int y = 0; y < targetH; y++)
            {
                int sy0 = Mathf.FloorToInt(y * island.MaskHeight / (float)targetH);
                int sy1 = Mathf.Max(sy0 + 1, Mathf.CeilToInt((y + 1) * island.MaskHeight / (float)targetH));
                for (int x = 0; x < targetW; x++)
                {
                    int sx0 = Mathf.FloorToInt(x * island.MaskWidth / (float)targetW);
                    int sx1 = Mathf.Max(sx0 + 1, Mathf.CeilToInt((x + 1) * island.MaskWidth / (float)targetW));
                    bool any = false;
                    for (int sy = sy0; sy < sy1 && !any; sy++)
                        for (int sx = sx0; sx < sx1 && !any; sx++)
                            if (sy < island.MaskHeight && sx < island.MaskWidth && island.Mask[sy * island.MaskWidth + sx])
                                any = true;
                    scaled[y * targetW + x] = any;
                }
            }

            // EN: Dilate by the padding radius so neighbours can never touch.
            // ZH: 按 padding 半径膨胀，使相邻的岛永远不会接触。
            int dw = targetW + pad * 2;
            int dh = targetH + pad * 2;
            var dilated = new bool[dw * dh];
            int area = 0;
            for (int y = 0; y < targetH; y++)
            {
                for (int x = 0; x < targetW; x++)
                {
                    if (!scaled[y * targetW + x]) continue;
                    for (int dy = -pad; dy <= pad; dy++)
                    {
                        int ny = y + pad + dy;
                        if (ny < 0 || ny >= dh) continue;
                        for (int dx = -pad; dx <= pad; dx++)
                        {
                            int nx = x + pad + dx;
                            if (nx < 0 || nx >= dw) continue;
                            if (!dilated[ny * dw + nx]) { dilated[ny * dw + nx] = true; area++; }
                        }
                    }
                }
            }

            return new PackItem
            {
                Island = island,
                Mask = dilated,
                Width = dw,
                Height = dh,
                Area = area,
            };
        }

        /// <summary>
        /// EN: Sorts items the way the specification requires: rasterized area descending, then longest
        ///     edge descending, so the hardest pieces are placed first.
        /// ZH: 按规格要求排序：光栅化面积降序，其次最长边降序，
        ///     使最难放置的部分优先放置。
        /// </summary>
        public static void SortItems(List<PackItem> items)
        {
            items.Sort((a, b) =>
            {
                int byArea = b.Area.CompareTo(a.Area);
                if (byArea != 0) return byArea;
                int byEdge = Mathf.Max(b.Width, b.Height).CompareTo(Mathf.Max(a.Width, a.Height));
                if (byEdge != 0) return byEdge;
                return a.Island.Index.CompareTo(b.Island.Index);
            });
        }
    }
}
