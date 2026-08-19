// Triangle rasterization helpers shared by island analysis and the packer.
// 岛分析与装箱器共用的三角形光栅化工具。
using System.Collections.Generic;
using UnityEngine;

namespace net.fosa.ato.editor
{
    /// <summary>Simple bit grid. / 位网格。</summary>
    public sealed class BitGrid
    {
        public readonly int W, H;
        public readonly ulong[] Rows; // H rows of ceil(W/64) ulongs / 每行 ceil(W/64) 个 ulong
        public readonly int Stride;

        public BitGrid(int w, int h)
        {
            W = w; H = h;
            Stride = (w + 63) >> 6;
            Rows = new ulong[Stride * h];
        }

        public void Set(int x, int y)
        {
            if ((uint)x >= (uint)W || (uint)y >= (uint)H) return;
            Rows[y * Stride + (x >> 6)] |= 1UL << (x & 63);
        }

        public bool Get(int x, int y) =>
            (uint)x < (uint)W && (uint)y < (uint)H &&
            (Rows[y * Stride + (x >> 6)] & (1UL << (x & 63))) != 0;

        public int CountBits()
        {
            int c = 0;
            foreach (var v in Rows) c += PopCount(v);
            return c;
        }

        /// <summary>Portable popcount (.NET Standard 2.1 safe). / 可移植 popcount。</summary>
        public static int PopCount(ulong v)
        {
            v = v - ((v >> 1) & 0x5555555555555555UL);
            v = (v & 0x3333333333333333UL) + ((v >> 2) & 0x3333333333333333UL);
            v = (v + (v >> 4)) & 0x0F0F0F0F0F0F0F0FUL;
            return (int)((v * 0x0101010101010101UL) >> 56);
        }

        /// <summary>Dilate by r cells (chebyshev). / 切比雪夫距离 r 的膨胀。</summary>
        public BitGrid Dilate(int r)
        {
            if (r <= 0) return this;
            var outG = new BitGrid(W, H);
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    if (!Get(x, y)) continue;
                    for (int dy = -r; dy <= r; dy++)
                        for (int dx = -r; dx <= r; dx++)
                            outG.Set(x + dx, y + dy);
                }
            return outG;
        }

        /// <summary>Transposed copy (for 90° rotation). / 转置（旋转90°用）。</summary>
        public BitGrid Transpose()
        {
            var t = new BitGrid(H, W);
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                    if (Get(x, y)) t.Set(y, x);
            return t;
        }
    }

    public static class Raster
    {
        /// <summary>
        /// Rasterize UV triangles (in [0,1] island space mapped to grid) conservatively.
        /// 保守光栅化 UV 三角形（含边缘覆盖）。
        /// </summary>
        public static void FillTriangle(BitGrid g, Vector2 a, Vector2 b, Vector2 c)
        {
            float minX = Mathf.Min(a.x, Mathf.Min(b.x, c.x));
            float maxX = Mathf.Max(a.x, Mathf.Max(b.x, c.x));
            float minY = Mathf.Min(a.y, Mathf.Min(b.y, c.y));
            float maxY = Mathf.Max(a.y, Mathf.Max(b.y, c.y));
            int x0 = Mathf.Max(0, Mathf.FloorToInt(minX) - 1);
            int x1 = Mathf.Min(g.W - 1, Mathf.CeilToInt(maxX) + 1);
            int y0 = Mathf.Max(0, Mathf.FloorToInt(minY) - 1);
            int y1 = Mathf.Min(g.H - 1, Mathf.CeilToInt(maxY) + 1);
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    // conservative: test cell center and expand by half-cell / 保守：中心+半格扩展
                    var p = new Vector2(x + 0.5f, y + 0.5f);
                    if (PointNearTriangle(p, a, b, c, 0.8f)) g.Set(x, y);
                }
        }

        private static bool PointNearTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c, float pad)
        {
            float d1 = Cross(p, a, b), d2 = Cross(p, b, c), d3 = Cross(p, c, a);
            bool neg = d1 < 0 || d2 < 0 || d3 < 0, pos = d1 > 0 || d2 > 0 || d3 > 0;
            if (!(neg && pos)) return true; // inside / 在内部
            // near edge check / 边缘邻近判定
            return DistToSeg(p, a, b) <= pad || DistToSeg(p, b, c) <= pad || DistToSeg(p, c, a) <= pad;
        }

        private static float Cross(Vector2 p, Vector2 a, Vector2 b) =>
            (p.x - b.x) * (a.y - b.y) - (a.x - b.x) * (p.y - b.y);

        private static float DistToSeg(Vector2 p, Vector2 a, Vector2 b)
        {
            var ab = b - a;
            float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / Mathf.Max(ab.sqrMagnitude, 1e-12f));
            return (p - (a + ab * t)).magnitude;
        }

        /// <summary>Rasterize an island into a grid covering its bbox. / 按包围盒光栅化整岛。</summary>
        public static BitGrid RasterizeIsland(Island isl, Vector2[] uv, int[] indices, int cellsX, int cellsY)
        {
            var g = new BitGrid(cellsX, cellsY);
            var size = isl.BBoxMax - isl.BBoxMin;
            var scale = new Vector2(
                cellsX / Mathf.Max(size.x, 1e-9f),
                cellsY / Mathf.Max(size.y, 1e-9f));
            foreach (var t0 in isl.Triangles)
            {
                Vector2 A = (uv[indices[t0]] + isl.Shift - isl.BBoxMin) * scale;
                Vector2 B = (uv[indices[t0 + 1]] + isl.Shift - isl.BBoxMin) * scale;
                Vector2 C = (uv[indices[t0 + 2]] + isl.Shift - isl.BBoxMin) * scale;
                FillTriangle(g, A, B, C);
            }
            return g;
        }
    }
}
