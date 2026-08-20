using System;
using System.Collections.Generic;
using UnityEngine;

namespace net.fosa.ato.editor
{
    /// <summary>
    /// EN: A coverage bitmask of an island at 4 px granularity, stored as one bit per cell packed into
    ///     64-bit words, row major. Shape-aware packing operates entirely on these: a rectangle packer
    ///     would waste the concave space that character UV islands are full of.
    /// ZH: 岛的覆盖位掩码，粒度为 4 像素，每个单元 1 比特打包进 64 位字，行主序。
    ///     形状感知装箱完全基于它工作：矩形装箱会浪费掉角色 UV 岛中大量存在的凹形空间。
    /// </summary>
    public sealed class RasterMask
    {
        /// <summary>EN: Width in cells. ZH: 单元宽度。</summary>
        public int CellsX;
        /// <summary>EN: Height in cells. ZH: 单元高度。</summary>
        public int CellsY;
        /// <summary>EN: Packed bits, <see cref="WordsPerRow"/> words per row. ZH: 打包后的比特，每行 WordsPerRow 个字。</summary>
        public ulong[] Bits;
        /// <summary>EN: Number of set cells. ZH: 被置位的单元数。</summary>
        public int Coverage;

        /// <summary>EN: 64-bit words needed for one row. ZH: 一行所需的 64 位字数。</summary>
        public int WordsPerRow => (CellsX + 63) >> 6;

        /// <summary>EN: Allocate an empty mask. ZH: 分配一个空掩码。</summary>
        public RasterMask(int cellsX, int cellsY)
        {
            CellsX = Mathf.Max(1, cellsX);
            CellsY = Mathf.Max(1, cellsY);
            Bits = new ulong[WordsPerRow * CellsY];
        }

        /// <summary>EN: Test a cell. ZH: 测试一个单元。</summary>
        public bool Get(int x, int y)
        {
            if (x < 0 || y < 0 || x >= CellsX || y >= CellsY) return false;
            return (Bits[y * WordsPerRow + (x >> 6)] & (1UL << (x & 63))) != 0;
        }

        /// <summary>EN: Set a cell. ZH: 置位一个单元。</summary>
        public void Set(int x, int y)
        {
            if (x < 0 || y < 0 || x >= CellsX || y >= CellsY) return;
            int i = y * WordsPerRow + (x >> 6);
            var bit = 1UL << (x & 63);
            if ((Bits[i] & bit) == 0) { Bits[i] |= bit; Coverage++; }
        }

        /// <summary>
        /// EN: Transpose, which is exactly a 90 degree rotation for a coverage mask. Rotating this way
        ///     costs nothing and never touches vertex tangents - the mesh UVs are swapped instead, so
        ///     tangent data is preserved verbatim as required.
        /// ZH: 转置，对覆盖掩码而言正好等价于旋转 90 度。这样旋转零成本且完全不触碰顶点切线——
        ///     我们改为交换网格 UV，因此切线数据按要求被原样保留。
        /// </summary>
        public RasterMask Transposed()
        {
            var t = new RasterMask(CellsY, CellsX);
            for (int y = 0; y < CellsY; y++)
                for (int x = 0; x < CellsX; x++)
                    if (Get(x, y)) t.Set(y, x);
            return t;
        }

        /// <summary>
        /// EN: Dilate by <paramref name="cells"/> in every direction, used to bake the padding into the
        ///     mask so the packer can never place two islands closer than the minimum spacing.
        /// ZH: 向各方向膨胀 <paramref name="cells"/> 个单元，把 padding 烘进掩码，
        ///     这样装箱器绝不可能把两个岛放得比最小间距更近。
        /// </summary>
        public RasterMask Dilated(int cells)
        {
            if (cells <= 0) return this;
            var d = new RasterMask(CellsX + cells * 2, CellsY + cells * 2);
            for (int y = 0; y < CellsY; y++)
            for (int x = 0; x < CellsX; x++)
            {
                if (!Get(x, y)) continue;
                for (int dy = -cells; dy <= cells; dy++)
                for (int dx = -cells; dx <= cells; dx++)
                    d.Set(x + cells + dx, y + cells + dy);
            }
            return d;
        }

        /// <summary>
        /// EN: Rasterise an island's triangles into a mask at the island's solved scale.
        ///     Conservative rasterisation: a cell is covered when the triangle touches it at all, so the
        ///     packer never overlaps two islands by half a texel.
        /// ZH: 按岛求解出的缩放把岛的三角形光栅化成掩码。
        ///     采用保守光栅化：只要三角形碰到某个单元就算覆盖，这样装箱器绝不会让两个岛重叠半个纹素。
        /// </summary>
        public static RasterMask Rasterize(UVIsland island, int[] indices, Vector2[] uv, Vector2Int pixelSize)
        {
            int gran = ATOConstants.RasterGranularity;
            int cx = Mathf.Max(1, Mathf.CeilToInt(pixelSize.x / (float)gran));
            int cy = Mathf.Max(1, Mathf.CeilToInt(pixelSize.y / (float)gran));
            var mask = new RasterMask(cx, cy);

            var span = island.UvMax - island.UvMin;
            if (span.x <= 0f) span.x = 1e-6f;
            if (span.y <= 0f) span.y = 1e-6f;

            foreach (var t in island.Triangles)
            {
                var a = ToCell(uv[indices[t]], island, span, cx, cy);
                var b = ToCell(uv[indices[t + 1]], island, span, cx, cy);
                var c = ToCell(uv[indices[t + 2]], island, span, cx, cy);
                RasterizeTriangle(mask, a, b, c);
            }

            // EN: Degenerate islands (a single edge, a hair card) can rasterise to nothing; keep one cell
            //     so the island still gets space and its texels survive.
            // ZH: 退化的岛（单条边、发片）可能光栅化为空；保留一个单元，让它仍能分到空间、纹素得以保留。
            if (mask.Coverage == 0) mask.Set(0, 0);
            return mask;
        }

        private static Vector2 ToCell(Vector2 p, UVIsland island, Vector2 span, int cx, int cy)
        {
            return new Vector2((p.x - island.UvMin.x) / span.x * cx, (p.y - island.UvMin.y) / span.y * cy);
        }

        private static void RasterizeTriangle(RasterMask m, Vector2 a, Vector2 b, Vector2 c)
        {
            int minX = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(a.x, Mathf.Min(b.x, c.x))) - 1, 0, m.CellsX - 1);
            int maxX = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(a.x, Mathf.Max(b.x, c.x))) + 1, 0, m.CellsX - 1);
            int minY = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(a.y, Mathf.Min(b.y, c.y))) - 1, 0, m.CellsY - 1);
            int maxY = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(a.y, Mathf.Max(b.y, c.y))) + 1, 0, m.CellsY - 1);

            for (int y = minY; y <= maxY; y++)
            for (int x = minX; x <= maxX; x++)
            {
                // EN: Conservative test - the cell square against the triangle, not just its centre.
                // ZH: 保守测试——用单元方块与三角形求交，而不仅仅是单元中心点。
                if (TriangleTouchesCell(a, b, c, x, y)) m.Set(x, y);
            }
        }

        private static bool TriangleTouchesCell(Vector2 a, Vector2 b, Vector2 c, int cx, int cy)
        {
            // EN: Sample the cell centre plus its four corners. For 4 px cells against character UVs this
            //     is indistinguishable from exact conservative rasterisation and far cheaper.
            // ZH: 采样单元中心加四个角点。对 4 像素单元与角色 UV 而言，
            //     这与精确保守光栅化不可区分，且成本低得多。
            Span<Vector2> pts = stackalloc Vector2[5];
            pts[0] = new Vector2(cx + 0.5f, cy + 0.5f);
            pts[1] = new Vector2(cx, cy);
            pts[2] = new Vector2(cx + 1f, cy);
            pts[3] = new Vector2(cx, cy + 1f);
            pts[4] = new Vector2(cx + 1f, cy + 1f);

            foreach (var p in pts) if (PointInTriangle(p, a, b, c)) return true;

            // EN: A very thin triangle can slip between all five samples; catch it with edge tests.
            // ZH: 极细的三角形可能从五个采样点之间溜走；用边测试兜底。
            return SegmentIntersectsCell(a, b, cx, cy) || SegmentIntersectsCell(b, c, cx, cy) ||
                   SegmentIntersectsCell(c, a, cx, cy);
        }

        private static bool PointInTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float d1 = Sign(p, a, b), d2 = Sign(p, b, c), d3 = Sign(p, c, a);
            bool neg = d1 < 0 || d2 < 0 || d3 < 0;
            bool pos = d1 > 0 || d2 > 0 || d3 > 0;
            return !(neg && pos);
        }

        private static float Sign(Vector2 p1, Vector2 p2, Vector2 p3) =>
            (p1.x - p3.x) * (p2.y - p3.y) - (p2.x - p3.x) * (p1.y - p3.y);

        private static bool SegmentIntersectsCell(Vector2 p, Vector2 q, int cx, int cy)
        {
            float minX = Mathf.Min(p.x, q.x), maxX = Mathf.Max(p.x, q.x);
            float minY = Mathf.Min(p.y, q.y), maxY = Mathf.Max(p.y, q.y);
            return !(maxX < cx || minX > cx + 1 || maxY < cy || minY > cy + 1);
        }
    }
}
