// SPDX-License-Identifier: MIT
// EN: Triangle rasterisation helpers: per texel coverage masks for quality evaluation and 4 px granularity
//     bit masks for the atlas packer.
// ZH: 三角形光栅化辅助：用于质量评估的逐纹素覆盖掩码，以及供图集装箱使用的 4px 粒度位掩码。

using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// EN: A 4 px granularity occupancy mask of one island, stored as one bit per cell in 64 bit words.
    /// ZH: 某个岛的 4px 粒度占用掩码，以 64 位字按位存储，每格 1 位。
    /// </summary>
    public sealed class ATORasterMask
    {
        public const int CellSize = 4;

        /// <summary>EN: Width in cells. ZH: 以格为单位的宽度。</summary>
        public int CellWidth;

        /// <summary>EN: Height in cells. ZH: 以格为单位的高度。</summary>
        public int CellHeight;

        /// <summary>EN: Row major bit words, <see cref="WordsPerRow"/> words per row. ZH: 行优先的位字，每行 <see cref="WordsPerRow"/> 个。</summary>
        public ulong[] Bits;

        public int WordsPerRow;

        /// <summary>EN: Number of set cells. ZH: 被占用的格子数。</summary>
        public int FilledCells;

        /// <summary>EN: Occupied area in pixels. ZH: 以像素计的占用面积。</summary>
        public long PixelArea => (long)FilledCells * CellSize * CellSize;

        public bool Get(int x, int y)
        {
            if (x < 0 || y < 0 || x >= CellWidth || y >= CellHeight) return false;
            return (Bits[y * WordsPerRow + (x >> 6)] & (1UL << (x & 63))) != 0;
        }

        public void Set(int x, int y)
        {
            if (x < 0 || y < 0 || x >= CellWidth || y >= CellHeight) return;
            var idx = y * WordsPerRow + (x >> 6);
            var bit = 1UL << (x & 63);
            if ((Bits[idx] & bit) == 0)
            {
                Bits[idx] |= bit;
                FilledCells++;
            }
        }

        public static ATORasterMask Create(int cellWidth, int cellHeight)
        {
            var wpr = (cellWidth + 63) / 64;
            return new ATORasterMask
            {
                CellWidth = cellWidth,
                CellHeight = cellHeight,
                WordsPerRow = wpr,
                Bits = new ulong[wpr * Math.Max(1, cellHeight)],
            };
        }

        /// <summary>
        /// EN: Returns the mask rotated by 90 degrees (bit mask transpose + flip).
        /// ZH: 返回旋转 90 度后的掩码（位掩码转置 + 翻转）。
        /// </summary>
        public ATORasterMask Rotate90()
        {
            var r = Create(CellHeight, CellWidth);
            for (var y = 0; y < CellHeight; y++)
            for (var x = 0; x < CellWidth; x++)
                if (Get(x, y))
                    r.Set(CellHeight - 1 - y, x);
            return r;
        }

        /// <summary>
        /// EN: Dilates the mask by <paramref name="cells"/> cells in every direction (padding).
        /// ZH: 把掩码在各方向上膨胀 <paramref name="cells"/> 格（用于 padding）。
        /// </summary>
        public ATORasterMask Dilate(int cells)
        {
            if (cells <= 0) return this;

            var r = Create(CellWidth + cells * 2, CellHeight + cells * 2);
            for (var y = 0; y < CellHeight; y++)
            for (var x = 0; x < CellWidth; x++)
            {
                if (!Get(x, y)) continue;
                for (var dy = -cells; dy <= cells; dy++)
                for (var dx = -cells; dx <= cells; dx++)
                    r.Set(x + cells + dx, y + cells + dy);
            }

            return r;
        }
    }

    /// <summary>
    /// EN: Rasterisation entry points.
    /// ZH: 光栅化入口。
    /// </summary>
    public static class ATORaster
    {
        /// <summary>
        /// EN: Rasterises the island into a per texel coverage mask covering the island bounding box.
        ///     Coverage is conservative: a texel is covered when its centre or any of its corners is inside.
        /// ZH: 把岛光栅化成覆盖其包围盒的逐纹素掩码。采用保守策略：纹素中心或任一角点落在三角形内即视为覆盖。
        /// </summary>
        public static NativeArray<byte> RasterizeCoverage(Vector2[] uv, int[] triangleIndices, int[] islandTriangles,
            RectInt pixelRect, int textureWidth, int textureHeight, Allocator allocator)
        {
            var w = Mathf.Max(1, pixelRect.width);
            var h = Mathf.Max(1, pixelRect.height);
            var mask = new NativeArray<byte>(w * h, allocator, NativeArrayOptions.ClearMemory);

            foreach (var t in islandTriangles)
            {
                var a = ToPixel(uv[triangleIndices[t * 3]], textureWidth, textureHeight) - pixelRect.min;
                var b = ToPixel(uv[triangleIndices[t * 3 + 1]], textureWidth, textureHeight) - pixelRect.min;
                var c = ToPixel(uv[triangleIndices[t * 3 + 2]], textureWidth, textureHeight) - pixelRect.min;
                FillTriangle(a, b, c, w, h, (x, y) => mask[y * w + x] = 1);
            }

            return mask;
        }

        /// <summary>
        /// EN: Rasterises the island at 4 px granularity for the packer.
        /// ZH: 以 4px 粒度光栅化岛，供装箱器使用。
        /// </summary>
        public static ATORasterMask RasterizeMask(Vector2[] uv, int[] triangleIndices, int[] islandTriangles,
            Rect uvBounds, int pixelWidth, int pixelHeight)
        {
            var cellW = Mathf.Max(1, Mathf.CeilToInt(pixelWidth / (float)ATORasterMask.CellSize));
            var cellH = Mathf.Max(1, Mathf.CeilToInt(pixelHeight / (float)ATORasterMask.CellSize));
            var mask = ATORasterMask.Create(cellW, cellH);

            var scaleX = pixelWidth / Mathf.Max(1e-6f, uvBounds.width);
            var scaleY = pixelHeight / Mathf.Max(1e-6f, uvBounds.height);

            foreach (var t in islandTriangles)
            {
                var a = ToCell(uv[triangleIndices[t * 3]], uvBounds, scaleX, scaleY);
                var b = ToCell(uv[triangleIndices[t * 3 + 1]], uvBounds, scaleX, scaleY);
                var c = ToCell(uv[triangleIndices[t * 3 + 2]], uvBounds, scaleX, scaleY);
                FillTriangle(a, b, c, cellW, cellH, mask.Set);
            }

            // EN: A degenerate island (zero area in UV) still needs at least one cell.
            // ZH: 退化的岛（UV 面积为 0）也至少需要占一个格子。
            if (mask.FilledCells == 0) mask.Set(0, 0);

            return mask;
        }

        private static Vector2 ToCell(Vector2 uv, Rect bounds, float scaleX, float scaleY)
        {
            return new Vector2((uv.x - bounds.xMin) * scaleX / ATORasterMask.CellSize,
                (uv.y - bounds.yMin) * scaleY / ATORasterMask.CellSize);
        }

        private static Vector2 ToPixel(Vector2 uv, int width, int height) =>
            new Vector2(uv.x * width, uv.y * height);

        private static Vector2 ToPixel(Vector2 uv, int width, int height, Vector2Int _) =>
            ToPixel(uv, width, height);

        /// <summary>
        /// EN: Conservative half-space triangle fill with a one cell margin so thin triangles never vanish.
        /// ZH: 保守的半空间三角形填充，附带 1 格外扩，保证细长三角形不会消失。
        /// </summary>
        private static void FillTriangle(Vector2 a, Vector2 b, Vector2 c, int width, int height,
            Action<int, int> plot)
        {
            var minX = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(a.x, Mathf.Min(b.x, c.x))) - 1, 0, width - 1);
            var maxX = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(a.x, Mathf.Max(b.x, c.x))) + 1, 0, width - 1);
            var minY = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(a.y, Mathf.Min(b.y, c.y))) - 1, 0, height - 1);
            var maxY = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(a.y, Mathf.Max(b.y, c.y))) + 1, 0, height - 1);

            for (var y = minY; y <= maxY; y++)
            for (var x = minX; x <= maxX; x++)
            {
                if (BoxIntersectsTriangle(x, y, a, b, c)) plot(x, y);
            }
        }

        private static bool BoxIntersectsTriangle(int x, int y, Vector2 a, Vector2 b, Vector2 c)
        {
            // EN: Sample the texel centre and its four corners. ZH: 采样纹素中心与四个角点。
            if (PointInTriangle(new Vector2(x + 0.5f, y + 0.5f), a, b, c)) return true;
            if (PointInTriangle(new Vector2(x, y), a, b, c)) return true;
            if (PointInTriangle(new Vector2(x + 1f, y), a, b, c)) return true;
            if (PointInTriangle(new Vector2(x, y + 1f), a, b, c)) return true;
            if (PointInTriangle(new Vector2(x + 1f, y + 1f), a, b, c)) return true;

            // EN: Also catch triangles smaller than a texel. ZH: 同时处理小于一个纹素的三角形。
            var minX = Mathf.Min(a.x, Mathf.Min(b.x, c.x));
            var maxX = Mathf.Max(a.x, Mathf.Max(b.x, c.x));
            var minY = Mathf.Min(a.y, Mathf.Min(b.y, c.y));
            var maxY = Mathf.Max(a.y, Mathf.Max(b.y, c.y));
            return maxX >= x && minX <= x + 1f && maxY >= y && minY <= y + 1f &&
                   (maxX - minX < 1f || maxY - minY < 1f);
        }

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

        /// <summary>
        /// EN: Pixel rectangle of an island inside a texture, clamped to the texture and never empty.
        /// ZH: 岛在贴图内的像素矩形，会被钳制在贴图范围内且不会为空。
        /// </summary>
        public static RectInt IslandPixelRect(Rect uvBounds, int textureWidth, int textureHeight)
        {
            var x0 = Mathf.Clamp(Mathf.FloorToInt(uvBounds.xMin * textureWidth), 0, textureWidth - 1);
            var y0 = Mathf.Clamp(Mathf.FloorToInt(uvBounds.yMin * textureHeight), 0, textureHeight - 1);
            var x1 = Mathf.Clamp(Mathf.CeilToInt(uvBounds.xMax * textureWidth), x0 + 1, textureWidth);
            var y1 = Mathf.Clamp(Mathf.CeilToInt(uvBounds.yMax * textureHeight), y0 + 1, textureHeight);
            return new RectInt(x0, y0, x1 - x0, y1 - y0);
        }

        /// <summary>
        /// EN: Counts how many texels of the coverage mask are set.
        /// ZH: 统计覆盖掩码中被置位的纹素数量。
        /// </summary>
        public static int CountCoverage(NativeArray<byte> coverage)
        {
            var n = 0;
            for (var i = 0; i < coverage.Length; i++)
                if (coverage[i] != 0)
                    n++;
            return n;
        }
    }
}
