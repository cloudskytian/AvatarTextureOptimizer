// Avatar Texture Optimizer (ATO)
// Bilingual comments: English + Simplified Chinese. 双语注释：英文 + 简体中文。

using System.Collections.Generic;
using UnityEngine;

namespace AvatarTextureOptimizer.Editor.UVIsland
{
    /// <summary>
    /// Rasterizes UV triangles into a per-texel coverage mask within a bounding box.
    /// Used both for quality evaluation (which texels the island actually covers) and for
    /// atlas packing (bit-mask rasterization at 4px granularity).
    ///
    /// 将 UV 三角形光栅化为包围盒内的逐 texel 覆盖掩码。用于质量评估（岛实际覆盖哪些
    /// texel）与图集装箱（4px 粒度位掩码光栅化）。
    /// </summary>
    public static class ATOTriangleRasterizer
    {
        /// <summary>
        /// Rasterize island triangles into a coverage mask for a bounding box of
        /// (width x height) texels. UV coordinates are given per-vertex for the channel.
        ///
        /// 将岛三角形光栅化为 (width x height) 包围盒的覆盖掩码。UV 按通道逐顶点给出。
        /// </summary>
        public static bool[] Rasterize(Vector2[] uvs, int[] triangles, IReadOnlyList<int> islandTris,
            Rect uvBounds, int width, int height)
        {
            var mask = new bool[width * height];
            if (width <= 0 || height <= 0) return mask;

            float invW = width / uvBounds.width;
            float invH = height / uvBounds.height;

            foreach (var t in islandTris)
            {
                int i0 = triangles[t * 3];
                int i1 = triangles[t * 3 + 1];
                int i2 = triangles[t * 3 + 2];

                Vector2 a = ToPixel(uvs[i0], uvBounds, invW, invH);
                Vector2 b = ToPixel(uvs[i1], uvBounds, invW, invH);
                Vector2 c = ToPixel(uvs[i2], uvBounds, invW, invH);

                RasterizeTriangle(mask, width, height, a, b, c);
            }

            return mask;
        }

        private static Vector2 ToPixel(Vector2 uv, Rect bounds, float invW, float invH)
        {
            return new Vector2((uv.x - bounds.xMin) * invW, (uv.y - bounds.yMin) * invH);
        }

        private static void RasterizeTriangle(bool[] mask, int w, int h, Vector2 a, Vector2 b, Vector2 c)
        {
            float minX = Mathf.Floor(Mathf.Min(a.x, Mathf.Min(b.x, c.x)));
            float maxX = Mathf.Ceil(Mathf.Max(a.x, Mathf.Max(b.x, c.x)));
            float minY = Mathf.Floor(Mathf.Min(a.y, Mathf.Min(b.y, c.y)));
            float maxY = Mathf.Ceil(Mathf.Max(a.y, Mathf.Max(b.y, c.y)));

            int x0 = Mathf.Clamp((int)minX, 0, w - 1);
            int x1 = Mathf.Clamp((int)maxX, 0, w - 1);
            int y0 = Mathf.Clamp((int)minY, 0, h - 1);
            int y1 = Mathf.Clamp((int)maxY, 0, h - 1);

            // Edge functions. 边函数。
            float area = Edge(a, b, c);
            if (Mathf.Abs(area) < 1e-8f) return;

            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    // Sample at pixel center. 采样像素中心。
                    Vector2 p = new Vector2(x + 0.5f, y + 0.5f);
                    float w0 = Edge(b, c, p) / area;
                    float w1 = Edge(c, a, p) / area;
                    float w2 = Edge(a, b, p) / area;

                    // Inside (or on edge) if all weights are non-negative.
                    // 所有权重非负即在内部（或边上）。
                    if (w0 >= -1e-4f && w1 >= -1e-4f && w2 >= -1e-4f)
                        mask[y * w + x] = true;
                }
            }
        }

        private static float Edge(Vector2 a, Vector2 b, Vector2 p)
        {
            return (b.x - a.x) * (p.y - a.y) - (b.y - a.y) * (p.x - a.x);
        }
    }
}
