using System;
using System.Collections.Generic;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor
{
    /// <summary>
    /// CPU rasterizer for island coverage masks (managed; a Burst-accelerated variant can replace
    /// the inner loops later). Produces occupancy masks at arbitrary resolutions — used for the
    /// 4px-granularity packing masks and the native-resolution quality sampling masks. /
    /// 岛覆盖掩码的 CPU 光栅化器（托管实现；内层循环后续可替换为 Burst 版本）。生成任意分辨率的
    /// 占用掩码 —— 用于 4px 粒度装箱掩码与原生分辨率质量采样掩码。
    /// </summary>
    internal static class AtoRasterizer
    {
        /// <summary>
        /// Rasterize a list of triangles (in UV space) into a byte mask. / 把一组三角形（UV 空间）光栅化为字节掩码。
        /// </summary>
        /// <param name="uvs">UV array of the whole mesh channel. / 整网格通道的 UV 数组。</param>
        /// <param name="triangles">Triangle indices (into uvs). / 三角形索引（指向 uvs）。</param>
        /// <param name="uvMin">The UV rectangle to map to the mask. / 映射到掩码的 UV 矩形（最小角）。</param>
        /// <param name="uvMax">The UV rectangle to map to the mask. / 映射到掩码的 UV 矩形（最大角）。</param>
        /// <param name="width">Mask width (pixels). / 掩码宽（像素）。</param>
        /// <param name="height">Mask height (pixels). / 掩码高（像素）。</param>
        /// <param name="mask">Output occupancy mask (byte 0/1), size width*height. / 输出占用掩码（0/1），尺寸 width*height。</param>
        public static void Rasterize(List<Vector2> uvs, List<int> triangles, Vector2 uvMin, Vector2 uvMax,
            int width, int height, byte[] mask, Vector2 uvOffset = default)
        {
            Array.Clear(mask, 0, mask.Length);
            var invSize = new Vector2(width / Mathf.Max(1e-6f, uvMax.x - uvMin.x),
                height / Mathf.Max(1e-6f, uvMax.y - uvMin.y));

            for (var t = 0; t < triangles.Count; t += 3)
            {
                var i0 = triangles[t];
                var i1 = triangles[t + 1];
                var i2 = triangles[t + 2];
                var p0 = UvToPixel(uvs[i0] + uvOffset, uvMin, invSize);
                var p1 = UvToPixel(uvs[i1] + uvOffset, uvMin, invSize);
                var p2 = UvToPixel(uvs[i2] + uvOffset, uvMin, invSize);

                // Bounding box. / 包围盒。
                var minX = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(p0.x, Mathf.Min(p1.x, p2.x))));
                var maxX = Mathf.Min(width - 1, Mathf.CeilToInt(Mathf.Max(p0.x, Mathf.Max(p1.x, p2.x))));
                var minY = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(p0.y, Mathf.Min(p1.y, p2.y))));
                var maxY = Mathf.Min(height - 1, Mathf.CeilToInt(Mathf.Max(p0.y, Mathf.Max(p1.y, p2.y))));
                if (minX > maxX || minY > maxY) continue;

                // Edge functions. / 边函数。
                var area = Edge(p0, p1, p2);
                if (Mathf.Abs(area) < 1e-9f) continue; // degenerate. / 退化三角形。

                for (var y = minY; y <= maxY; y++)
                {
                    for (var x = minX; x <= maxX; x++)
                    {
                        var pixel = new Vector2(x + 0.5f, y + 0.5f);
                        var w0 = Edge(p1, p2, pixel);
                        var w1 = Edge(p2, p0, pixel);
                        var w2 = Edge(p0, p1, pixel);
                        var inside = (area > 0 && w0 >= 0 && w1 >= 0 && w2 >= 0) ||
                                     (area < 0 && w0 <= 0 && w1 <= 0 && w2 <= 0);
                        if (inside) mask[y * width + x] = 1;
                    }
                }
            }
        }

        private static Vector2 UvToPixel(Vector2 uv, Vector2 uvMin, Vector2 invSize) =>
            new Vector2((uv.x - uvMin.x) * invSize.x, (uv.y - uvMin.y) * invSize.y);

        private static float Edge(Vector2 a, Vector2 b, Vector2 c) =>
            (c.x - a.x) * (b.y - a.y) - (c.y - a.y) * (b.x - a.x);

        /// <summary>
        /// Compute the coverage count of a mask (number of set pixels). / 计算掩码覆盖像素数。
        /// </summary>
        public static int CountPixels(byte[] mask)
        {
            var count = 0;
            for (var i = 0; i < mask.Length; i++) count += mask[i];
            return count;
        }

        /// <summary>
        /// Test whether two masks overlap (any common set pixel). / 测试两个掩码是否重叠（存在共同置位像素）。
        /// </summary>
        public static bool Overlaps(byte[] a, byte[] b)
        {
            var len = Mathf.Min(a.Length, b.Length);
            for (var i = 0; i < len; i++)
            {
                if (a[i] != 0 && b[i] != 0) return true;
            }
            return false;
        }
    }
}
