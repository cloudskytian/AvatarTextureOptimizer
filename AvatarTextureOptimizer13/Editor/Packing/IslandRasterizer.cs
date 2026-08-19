// ATO — Avatar Texture Optimizer
// Rasterizes an island's triangles (scaled UVs) into a bitmask grid and computes the
// rasterized area used for queue ordering (CLAUDE.md #15/#16).
// 将岛的三角形（缩放后 UV）光栅化为位掩码网格，并计算用于队列排序的光栅化面积（CLAUDE.md #15/#16）。

using UnityEngine;

namespace net.fosa.ato.editor
{
    /// <summary>
    /// Island rasterization into a cell grid. 岛光栅化为单元网格。
    /// </summary>
    public static class IslandRasterizer
    {
        /// <summary>
        /// Rasterize an island into a grid of (gridW x gridH) cells covering its scaled UV bbox.
        /// 将岛光栅化为覆盖其缩放 UV 包围盒的 (gridW x gridH) 单元网格。
        /// </summary>
        public static BitMask Rasterize(ATOIsland island, int gridW, int gridH)
        {
            var mask = new BitMask(gridW, gridH);
            if (island.scaledUV == null || island.scaledUV.Length == 0) return mask;

            Vector2 min = island.bounds.min;
            Vector2 size = new Vector2(
                island.bounds.width * island.scaleX,
                island.bounds.height * island.scaleY);
            float invCellX = size.x > 1e-9f ? gridW / size.x : 0f;
            float invCellY = size.y > 1e-9f ? gridH / size.y : 0f;

            int n = island.scaledUV.Length;
            var cellUV = new Vector2[n];
            for (int i = 0; i < n; i++)
            {
                cellUV[i] = new Vector2((island.scaledUV[i].x - min.x) * invCellX,
                                        (island.scaledUV[i].y - min.y) * invCellY);
            }

            // Rasterize each triangle by filling cells whose center lies inside it.
            // 对每个三角形，填充中心位于其内的单元。
            int triCount = island.triangleUV.Count / 3;
            for (int t = 0; t < triCount; t++)
            {
                int i0 = island.triangleUV[t * 3];
                int i1 = island.triangleUV[t * 3 + 1];
                int i2 = island.triangleUV[t * 3 + 2];
                if (i0 >= n || i1 >= n || i2 >= n) continue;
                RasterizeTriangle(mask, cellUV[i0], cellUV[i1], cellUV[i2], gridW, gridH);
            }
            return mask;
        }

        private static void RasterizeTriangle(BitMask mask, Vector2 a, Vector2 b, Vector2 c, int w, int h)
        {
            int minX = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(a.x, b.x, c.x) - 1f));
            int maxX = Mathf.Min(w - 1, Mathf.CeilToInt(Mathf.Max(a.x, b.x, c.x) + 1f));
            int minY = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(a.y, b.y, c.y) - 1f));
            int maxY = Mathf.Min(h - 1, Mathf.CeilToInt(Mathf.Max(a.y, b.y, c.y) + 1f));

            const float eps = 1e-4f;
            for (int y = minY; y <= maxY; y++)
            for (int x = minX; x <= maxX; x++)
            {
                var p = new Vector2(x + 0.5f, y + 0.5f);
                // Signed areas relative to each edge; inside iff all same sign (either winding).
                // 各边有向面积；同号（任一绕序）即在内。
                float w0 = Cross(b, c, p);
                float w1 = Cross(c, a, p);
                float w2 = Cross(a, b, p);
                bool inside = (w0 >= -eps && w1 >= -eps && w2 >= -eps) ||
                              (w0 <= eps && w1 <= eps && w2 <= eps);
                if (inside) mask.Set(x, y);
            }
        }

        private static float Cross(Vector2 o, Vector2 a, Vector2 b)
        {
            return (a.x - o.x) * (b.y - o.y) - (a.y - o.y) * (b.x - o.x);
        }

        /// <summary>Rasterized cell count (area proxy). 光栅化单元数（面积代理）。</summary>
        public static int CountBits(BitMask mask)
        {
            int count = 0;
            for (int y = 0; y < mask.Height; y++)
            for (int x = 0; x < mask.Width; x++)
                if (mask.Get(x, y)) count++;
            return count;
        }
    }
}
