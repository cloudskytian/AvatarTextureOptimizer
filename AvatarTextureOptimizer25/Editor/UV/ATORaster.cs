// Avatar Texture Optimizer / 头像贴图优化器
// Integer-grid span rasterizer for UV triangles. Used by island overlap
// detection and later by the packing bitmask raster (4px granularity).
// UV 三角形的整格扫描线光栅器。用于岛重叠检测与后续的装箱位掩码（4px 粒度）。
//
// The rasterizer is midpoint-rule conservative: a cell is covered when its
// center is inside the triangle. This matches GPU-style sampling closely
// enough for coverage statistics and packing.
// 光栅采用中心点规则：格心落在三角形内即覆盖。与 GPU 采样近似一致，
// 对覆盖统计与装箱足够精确。

using System;
using System.Collections.Generic;
using UnityEngine;

namespace FOSA.AvatarTextureOptimizer.Editor
{
    /// <summary>Cell-grid raster utilities. / 单元格光栅工具。</summary>
    public static class ATORaster
    {
        /// <summary>
        /// Rasterize one triangle (normalized UV) into a grid, invoking
        /// <paramref name="emit"/> for each covered cell (x,y).
        /// 将一个三角形（归一化 UV）光栅化进网格，逐覆盖格回调 emit(x,y)。
        /// </summary>
        public static void RasterTriangle(Vector2 a, Vector2 b, Vector2 c, int width, int height, Action<int, int> emit)
        {
            // skip degenerate triangles / 跳过退化三角形
            var area2 = (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);
            if (Mathf.Abs(area2) < 1e-12f) return;

            float minXf = Mathf.Min(a.x, Mathf.Min(b.x, c.x));
            float minYf = Mathf.Min(a.y, Mathf.Min(b.y, c.y));
            float maxXf = Mathf.Max(a.x, Mathf.Max(b.x, c.x));
            float maxYf = Mathf.Max(a.y, Mathf.Max(b.y, c.y));

            int x0 = Mathf.Clamp(Mathf.FloorToInt(minXf * width), 0, width - 1);
            int x1 = Mathf.Clamp(Mathf.CeilToInt(maxXf * width) - 1, 0, width - 1);
            int y0 = Mathf.Clamp(Mathf.FloorToInt(minYf * height), 0, height - 1);
            int y1 = Mathf.Clamp(Mathf.CeilToInt(maxYf * height) - 1, 0, height - 1);

            float inv = 1f / area2;
            for (int y = y0; y <= y1; y++)
            {
                float cy = (y + 0.5f) / height;
                for (int x = x0; x <= x1; x++)
                {
                    float cx = (x + 0.5f) / width;
                    // Barycentric / 重心坐标
                    float w0 = ((b.x - cx) * (c.y - cy) - (b.y - cy) * (c.x - cx)) * inv;
                    float w1 = ((c.x - cx) * (a.y - cy) - (c.y - cy) * (a.x - cx)) * inv;
                    float w2 = 1f - w0 - w1;
                    if (w0 >= -1e-6f && w1 >= -1e-6f && w2 >= -1e-6f) emit(x, y);
                }
            }
        }

        /// <summary>
        /// Rasterize a triangle in PIXEL space into a boolean mask.
        /// 在像素空间将三角形光栅化进布尔掩码。
        /// </summary>
        public static void RasterTrianglePx(Vector2 a, Vector2 b, Vector2 c, bool[] mask, int width, int height)
        {
            Vector2 an = new Vector2(a.x / width, a.y / height);
            Vector2 bn = new Vector2(b.x / width, b.y / height);
            Vector2 cn = new Vector2(c.x / width, c.y / height);
            RasterTriangle(an, bn, cn, width, height, (x, y) => mask[y * width + x] = true);
        }
    }

    /// <summary>
    /// Compact bit-mask over a cell grid (used by the packer).
    /// 装箱用的紧凑位掩码网格。
    /// </summary>
    public sealed class ATOBitMask
    {
        public readonly int width, height;
        public readonly ulong[] bits;
        private int _wordRowLen;

        public ATOBitMask(int width, int height)
        {
            this.width = width;
            this.height = height;
            _wordRowLen = (width + 63) / 64;
            bits = new ulong[_wordRowLen * height];
        }

        /// <summary>Clone into a new mask. / 克隆为新掩码。</summary>
        public ATOBitMask CloneCroppedRect(int x, int y, int w, int h)
        {
            var m = new ATOBitMask(w, h);
            for (int yy = 0; yy < h; yy++)
            for (int xx = 0; xx < w; xx++)
                if (Get(x + xx, y + yy)) m.Set(xx, yy, true);
            return m;
        }

        public bool Get(int x, int y)
        {
            if (x < 0 || y < 0 || x >= width || y >= height) return false;
            int idx = y * _wordRowLen + (x >> 6);
            return (bits[idx] & (1UL << (x & 63))) != 0;
        }

        public void Set(int x, int y, bool v)
        {
            if (x < 0 || y < 0 || x >= width || y >= height) return;
            int idx = y * _wordRowLen + (x >> 6);
            if (v) bits[idx] |= (1UL << (x & 63));
            else bits[idx] &= ~(1UL << (x & 63));
        }

        /// <summary>Number of covered cells. / 覆盖格数量。</summary>
        public long CountBits()
        {
            long n = 0;
            foreach (var w in bits) n += PopCount64(w);
            return n;
        }

        /// <summary>Bounding box of covered cells (inclusive). / 覆盖格的包围盒（含端点）。</summary>
        public bool TryGetBounds(out int minX, out int minY, out int maxX, out int maxY)
        {
            minX = int.MaxValue; minY = int.MaxValue; maxX = -1; maxY = -1;
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                if (Get(x, y))
                {
                    if (x < minX) minX = x;
                    if (y < minY) minY = y;
                    if (x > maxX) maxX = x;
                    if (y > maxY) maxY = y;
                }
            return maxX >= 0;
        }

        /// <summary>90-degree clockwise rotation (bit transpose based). / 顺时针旋转 90 度（基于位转置）。</summary>
        public ATOBitMask Rotate90CW()
        {
            var m = new ATOBitMask(height, width);
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                if (Get(x, y)) m.Set(height - 1 - y, x, true);
            return m;
        }

        private static int PopCount64(ulong v)
        {
            v = v - ((v >> 1) & 0x5555555555555555UL);
            v = (v & 0x3333333333333333UL) + ((v >> 2) & 0x3333333333333333UL);
            return (int)((((v + (v >> 4)) & 0x0F0F0F0F0F0F0F0FUL) * 0x0101010101010101UL) >> 56);
        }
    }
}
