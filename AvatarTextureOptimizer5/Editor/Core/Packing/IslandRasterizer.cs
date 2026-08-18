// Copyright (c) fosa. Licensed under the MIT License.
// Rasterizes UV islands into 4px-granularity bitmasks used for shape-aware packing.
// Packing against the real island shape (not its bounding rect) is what recovers the空白
// area between concave islands.
// 将 UV 岛光栅化为 4px 粒度的位掩码，用于形状感知装箱。
// 按真实岛形状（而非包围矩形）装箱，正是回收凹形岛之间空白区域的关键。

using System;
using System.Collections.Generic;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Produces coverage bitmasks for UV islands at a fixed cell granularity.
    /// 以固定单元粒度为 UV 岛生成覆盖位掩码。
    /// </summary>
    public static class IslandRasterizer
    {
        /// <summary>
        /// Rasterization granularity in pixels. 4px matches the block size of every BC/DXT/ASTC
        /// format we emit, so island placement is always block aligned and lossless formats stay
        /// bit-exact.
        /// 光栅化粒度（像素）。4px 与我们输出的所有 BC/DXT/ASTC 格式的块大小一致，
        /// 因此岛的放置始终块对齐，无损格式可保持逐位精确。
        /// </summary>
        public const int CellSize = 4;

        /// <summary>
        /// Rasterizes an island's triangles into a bitmask sized to its packed dimensions.
        /// 将岛的三角形光栅化为与其打包尺寸匹配的位掩码。
        /// </summary>
        public static void Rasterize(
            UVIsland island, int[] triangles, Vector2[] uvs, int padding)
        {
            var w = Mathf.Max(1, island.PackedSize.x);
            var h = Mathf.Max(1, island.PackedSize.y);

            var cellsX = (w + CellSize - 1) / CellSize;
            var cellsY = (h + CellSize - 1) / CellSize;

            // Padding is expressed in pixels and dilates the mask so neighbouring islands cannot
            // bleed into one another during mip generation.
            // padding 以像素表示，会膨胀掩码，使相邻岛在生成 mip 时不会互相渗色。
            var padCells = (padding + CellSize - 1) / CellSize;
            cellsX += padCells * 2;
            cellsY += padCells * 2;

            var wordsPerRow = (cellsX + 63) / 64;
            var mask = new ulong[wordsPerRow * cellsY];

            var bounds = island.UVBounds;
            var invW = bounds.width > 1e-9f ? 1f / bounds.width : 0f;
            var invH = bounds.height > 1e-9f ? 1f / bounds.height : 0f;

            foreach (var t in island.Triangles)
            {
                var i0 = triangles[t * 3];
                var i1 = triangles[t * 3 + 1];
                var i2 = triangles[t * 3 + 2];
                if (i0 >= uvs.Length || i1 >= uvs.Length || i2 >= uvs.Length) continue;

                // Map UV into island-local cell space, offset by the padding ring.
                // 将 UV 映射到岛局部单元空间，并偏移 padding 环。
                var p0 = ToCell(uvs[i0], bounds, invW, invH, w, h, padCells);
                var p1 = ToCell(uvs[i1], bounds, invW, invH, w, h, padCells);
                var p2 = ToCell(uvs[i2], bounds, invW, invH, w, h, padCells);

                RasterizeTriangle(mask, wordsPerRow, cellsX, cellsY, p0, p1, p2);
            }

            if (padCells > 0)
            {
                mask = Dilate(mask, wordsPerRow, cellsX, cellsY, padCells);
            }

            island.CoverageMask = mask;
            island.MaskWidth = cellsX;
            island.MaskHeight = cellsY;
            island.CoveredCells = CountBits(mask);
        }

        private static Vector2 ToCell(
            Vector2 uv, Rect bounds, float invW, float invH, int w, int h, int padCells)
        {
            var localX = (uv.x - bounds.xMin) * invW * w;
            var localY = (uv.y - bounds.yMin) * invH * h;
            return new Vector2(localX / CellSize + padCells, localY / CellSize + padCells);
        }

        /// <summary>
        /// Conservative triangle rasterization: a cell is marked when the triangle touches it at
        /// all. Being conservative guarantees no texel of the island is ever left uncovered.
        /// 保守三角形光栅化：只要三角形接触到单元就标记。
        /// 保守策略保证岛的任何 texel 都不会遗漏覆盖。
        /// </summary>
        private static void RasterizeTriangle(
            ulong[] mask, int wordsPerRow, int cellsX, int cellsY, Vector2 a, Vector2 b, Vector2 c)
        {
            var minX = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(a.x, Mathf.Min(b.x, c.x))), 0, cellsX - 1);
            var maxX = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(a.x, Mathf.Max(b.x, c.x))), 0, cellsX - 1);
            var minY = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(a.y, Mathf.Min(b.y, c.y))), 0, cellsY - 1);
            var maxY = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(a.y, Mathf.Max(b.y, c.y))), 0, cellsY - 1);

            for (var y = minY; y <= maxY; y++)
            {
                for (var x = minX; x <= maxX; x++)
                {
                    // Test the cell as a box against the triangle.
                    // 将单元作为方框与三角形做相交测试。
                    if (TriangleTouchesBox(a, b, c, x, y, x + 1, y + 1))
                    {
                        SetBit(mask, wordsPerRow, x, y);
                    }
                }
            }
        }

        private static bool TriangleTouchesBox(
            Vector2 a, Vector2 b, Vector2 c, float x0, float y0, float x1, float y1)
        {
            // Any vertex inside the box.
            // 任一顶点位于方框内。
            if (PointInBox(a, x0, y0, x1, y1) ||
                PointInBox(b, x0, y0, x1, y1) ||
                PointInBox(c, x0, y0, x1, y1)) return true;

            // Box centre inside the triangle.
            // 方框中心位于三角形内。
            var cx = (x0 + x1) * 0.5f;
            var cy = (y0 + y1) * 0.5f;
            if (PointInTriangle(new Vector2(cx, cy), a, b, c)) return true;

            // Any box corner inside the triangle.
            // 任一方框角点位于三角形内。
            if (PointInTriangle(new Vector2(x0, y0), a, b, c) ||
                PointInTriangle(new Vector2(x1, y0), a, b, c) ||
                PointInTriangle(new Vector2(x0, y1), a, b, c) ||
                PointInTriangle(new Vector2(x1, y1), a, b, c)) return true;

            // Edge intersection.
            // 边相交。
            return EdgeCrossesBox(a, b, x0, y0, x1, y1) ||
                   EdgeCrossesBox(b, c, x0, y0, x1, y1) ||
                   EdgeCrossesBox(c, a, x0, y0, x1, y1);
        }

        private static bool PointInBox(Vector2 p, float x0, float y0, float x1, float y1) =>
            p.x >= x0 && p.x <= x1 && p.y >= y0 && p.y <= y1;

        private static bool PointInTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            var d1 = Sign(p, a, b);
            var d2 = Sign(p, b, c);
            var d3 = Sign(p, c, a);
            var hasNeg = d1 < 0 || d2 < 0 || d3 < 0;
            var hasPos = d1 > 0 || d2 > 0 || d3 > 0;
            return !(hasNeg && hasPos);
        }

        private static float Sign(Vector2 p1, Vector2 p2, Vector2 p3) =>
            (p1.x - p3.x) * (p2.y - p3.y) - (p2.x - p3.x) * (p1.y - p3.y);

        private static bool EdgeCrossesBox(
            Vector2 p, Vector2 q, float x0, float y0, float x1, float y1)
        {
            return SegmentsIntersect(p, q, new Vector2(x0, y0), new Vector2(x1, y0)) ||
                   SegmentsIntersect(p, q, new Vector2(x1, y0), new Vector2(x1, y1)) ||
                   SegmentsIntersect(p, q, new Vector2(x1, y1), new Vector2(x0, y1)) ||
                   SegmentsIntersect(p, q, new Vector2(x0, y1), new Vector2(x0, y0));
        }

        private static bool SegmentsIntersect(Vector2 p1, Vector2 p2, Vector2 p3, Vector2 p4)
        {
            var d1 = Sign(p3, p4, p1);
            var d2 = Sign(p3, p4, p2);
            var d3 = Sign(p1, p2, p3);
            var d4 = Sign(p1, p2, p4);
            return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) &&
                   ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
        }

        /// <summary>
        /// Dilates a mask by a number of cells, implementing the padding ring.
        /// 将掩码按指定单元数膨胀，实现 padding 环。
        /// </summary>
        private static ulong[] Dilate(
            ulong[] mask, int wordsPerRow, int cellsX, int cellsY, int radius)
        {
            var result = new ulong[mask.Length];
            Array.Copy(mask, result, mask.Length);

            for (var r = 0; r < radius; r++)
            {
                var step = new ulong[result.Length];
                Array.Copy(result, step, result.Length);

                for (var y = 0; y < cellsY; y++)
                {
                    for (var x = 0; x < cellsX; x++)
                    {
                        if (!GetBit(result, wordsPerRow, x, y)) continue;
                        if (x > 0) SetBit(step, wordsPerRow, x - 1, y);
                        if (x < cellsX - 1) SetBit(step, wordsPerRow, x + 1, y);
                        if (y > 0) SetBit(step, wordsPerRow, x, y - 1);
                        if (y < cellsY - 1) SetBit(step, wordsPerRow, x, y + 1);
                    }
                }

                result = step;
            }

            return result;
        }

        /// <summary>Sets a bit in a packed bitmask. / 在打包位掩码中置位。</summary>
        public static void SetBit(ulong[] mask, int wordsPerRow, int x, int y)
        {
            var idx = y * wordsPerRow + (x >> 6);
            if (idx < 0 || idx >= mask.Length) return;
            mask[idx] |= 1UL << (x & 63);
        }

        /// <summary>Reads a bit from a packed bitmask. / 从打包位掩码中读取位。</summary>
        public static bool GetBit(ulong[] mask, int wordsPerRow, int x, int y)
        {
            var idx = y * wordsPerRow + (x >> 6);
            if (idx < 0 || idx >= mask.Length) return false;
            return (mask[idx] & (1UL << (x & 63))) != 0;
        }

        /// <summary>Counts set bits. / 统计置位数量。</summary>
        public static int CountBits(ulong[] mask)
        {
            var count = 0;
            foreach (var w in mask)
            {
                var v = w;
                while (v != 0)
                {
                    v &= v - 1;
                    count++;
                }
            }

            return count;
        }

        /// <summary>
        /// Transposes a bitmask, which is exactly a 90 degree rotation of the island shape.
        /// The texels are rotated identically at composite time, so sampling stays equivalent;
        /// tangent data is never recomputed.
        /// 转置位掩码，等价于岛形状旋转 90 度。
        /// 合成时 texel 会同样旋转，因此采样保持等价；切线数据绝不重算。
        /// </summary>
        public static ulong[] Transpose(
            ulong[] mask, int width, int height, out int newWidth, out int newHeight)
        {
            newWidth = height;
            newHeight = width;

            var srcWords = (width + 63) / 64;
            var dstWords = (height + 63) / 64;
            var result = new ulong[dstWords * width];

            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    if (GetBit(mask, srcWords, x, y))
                    {
                        SetBit(result, dstWords, y, x);
                    }
                }
            }

            return result;
        }
    }
}
