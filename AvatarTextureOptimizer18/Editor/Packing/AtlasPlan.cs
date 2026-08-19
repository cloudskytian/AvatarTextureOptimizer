using System.Collections.Generic;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Editor.Packing
{
    // 位掩码：4px 粒度光栅化占用图。每行 stride 个 ulong（每 ulong 64 位，LSB 优先）。
    // Bit mask: 4px-granularity occupancy bitmap. stride ulongs per row (64 bits each, LSB first).
    public struct BitMask
    {
        public int w, h;      // 单元格尺寸。Cell dimensions.
        public int stride;    // 每行 ulong 数。Ulongs per row.
        public ulong[] rows;  // h × stride。h × stride.

        public static BitMask Allocate(int w, int h)
        {
            int stride = (w + 63) >> 6;
            return new BitMask { w = w, h = h, stride = stride, rows = new ulong[h * stride] };
        }

        public bool Get(int x, int y)
        {
            if (x < 0 || y < 0 || x >= w || y >= h) return false;
            return (rows[y * stride + (x >> 6)] & (1UL << (x & 63))) != 0;
        }

        public void Set(int x, int y)
        {
            if (x < 0 || y < 0 || x >= w || y >= h) return;
            rows[y * stride + (x >> 6)] |= 1UL << (x & 63);
        }

        // 已占用单元格计数。Occupied cell count.
        public int PopCount()
        {
            int count = 0;
            foreach (var r in rows)
            {
                ulong v = r;
                while (v != 0) { v &= v - 1; count++; }
            }
            return count;
        }

        // 该掩码能否放置于 (x,y)（掩码须已包含 padding 膨胀）。Whether the mask fits at (x,y) (mask must already include padding dilation).
        public bool CanPlace(in BitMask atlas, int x, int y)
        {
            int x1 = x + w, y1 = y + h;
            if (x < 0 || y < 0 || x1 > atlas.w || y1 > atlas.h) return false;

            int shift = x & 63;
            int wordX = x >> 6;
            for (int ry = 0; ry < h; ry++)
            {
                int ay = (y + ry) * atlas.stride + wordX;
                ulong row = rows[ry * stride];
                ulong overlap = atlas.rows[ay] & (row << shift);
                if (shift > 0 && wordX + 1 < atlas.stride)
                {
                    overlap |= atlas.rows[ay + 1] & (row >> (64 - shift));
                }
                if (overlap != 0) return false;
            }
            return true;
        }

        // 放置（写入占用；掩码须已包含 padding 膨胀）。Places (writes occupancy; mask must already include padding dilation).
        public void Place(ref BitMask atlas, int x, int y)
        {
            for (int py = 0; py < h; py++)
            {
                int ay = (y + py) * atlas.stride;
                int wordX = x >> 6;
                int shift = x & 63;
                for (int wI = 0; wI < stride; wI++)
                {
                    ulong row = rows[py * stride + wI];
                    if (row == 0) continue;
                    if (wordX + wI < atlas.stride)
                    {
                        atlas.rows[ay + wordX + wI] |= row << shift;
                    }
                    if (shift > 0 && wordX + wI + 1 < atlas.stride)
                    {
                        atlas.rows[ay + wordX + wI + 1] |= row >> (64 - shift);
                    }
                }
            }
        }

        // 向四周膨胀 k 圈（用于 padding 边框）。Dilates by k cells on all sides (for the padding ring).
        public BitMask Dilate(int k)
        {
            if (k <= 0) return this;
            var r = Allocate(w + k * 2, h + k * 2);
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    if (!Get(x, y)) continue;
                    for (int dy = -k; dy <= k; dy++)
                    {
                        for (int dx = -k; dx <= k; dx++)
                        {
                            r.Set(x + k + dx, y + k + dy);
                        }
                    }
                }
            }
            return r;
        }

        // 旋转 90°（逆时针）。Rotate 90° CCW.
        public BitMask Rotate90()
        {
            var r = Allocate(h, w);
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    if (Get(x, y)) r.Set(h - 1 - y, x);
                }
            }
            return r;
        }

        // 掩码内容包围盒（像素单位）。Content bounding box (in pixels).
        public void PixelBounds(out int minX, out int minY, out int maxX, out int maxY)
        {
            minX = int.MaxValue; minY = int.MaxValue; maxX = int.MinValue; maxY = int.MinValue;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    if (Get(x, y))
                    {
                        if (x * 4 < minX) minX = x * 4;
                        if (y * 4 < minY) minY = y * 4;
                        if (x * 4 + 4 > maxX) maxX = x * 4 + 4;
                        if (y * 4 + 4 > maxY) maxY = y * 4 + 4;
                    }
                }
            }
            if (minX > maxX) { minX = 0; minY = 0; maxX = 0; maxY = 0; }
        }
    }

    // 装箱计划：一张成品图集（一个类别）。Packing plan: one finished atlas (one kind).
    public sealed class AtlasPlan
    {
        public int id;
        public Islands.AtlasKind kind;
        public int width, height;         // 像素尺寸。Size in pixels.
        public Islands.TypeGroup group;
        public readonly List<Islands.IslandEntity> islands = new List<Islands.IslandEntity>();
        public BitMask occupancy;
        public float utilization;
        public int paddingPx;

        // 构建阶段填充。Filled by the atlas-build stage.
        public Texture2D texture;
        public string assetPath;

        public override string ToString()
        {
            return string.Format("Atlas#{0} {1} {2}x{3} islands={4}", id, kind, width, height, islands.Count);
        }
    }
}
