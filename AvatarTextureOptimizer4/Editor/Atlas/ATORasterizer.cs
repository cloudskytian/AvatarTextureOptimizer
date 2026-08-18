// Avatar Texture Optimizer (ATO)
// 4px-granularity bitmask rasterizer + 90-degree-step rotation (transpose).
// Used by the BLF packer and the atlas builder.
// 4px 粒度位掩码光栅化 + 90 度步进旋转（转置）。供 BLF 装箱器与图集构建器使用。

using System.Collections.Generic;
using UnityEngine;

namespace NetFosa.ATO
{
    /// <summary>
    /// Rasterizes triangles (UV space) into a coarse cell grid.
    /// 把三角形（UV 空间）光栅化进粗粒度网格。
    /// </summary>
    public static class ATORasterizer
    {
        /// <summary>
        /// Rasterize triangles into a cell mask. Each cell is covered if its center lies
        /// inside any triangle (plus a one-cell dilation for safety).
        /// 把三角形光栅化为单元掩码。单元中心落在任一三角形内即覆盖（外加一圈膨胀以保证安全）。
        /// </summary>
        public static void Rasterize(Vector2[] uv, int[] tris, Vector2 bboxMin, Vector2 bboxSize,
            float refRes, int cellPx, out byte[] mask, out int cellsW, out int cellsH)
        {
            cellsW = Mathf.Max(1, Mathf.CeilToInt(bboxSize.x * refRes / cellPx));
            cellsH = Mathf.Max(1, Mathf.CeilToInt(bboxSize.y * refRes / cellPx));
            int n = cellsW * cellsH;
            mask = new byte[n];

            // Bucket triangles by cell for speed. / 按单元分桶三角形以加速。
            var buckets = new Dictionary<long, List<int>>();
            int triCount = tris.Length / 3;
            for (int t = 0; t < triCount; t++)
            {
                var a = UvToCell(uv[tris[t * 3]], bboxMin, refRes, cellPx);
                var b = UvToCell(uv[tris[t * 3 + 1]], bboxMin, refRes, cellPx);
                var c = UvToCell(uv[tris[t * 3 + 2]], bboxMin, refRes, cellPx);
                int x0 = Mathf.Clamp(Mathf.Min(a.x, Mathf.Min(b.x, c.x)) - 1, 0, cellsW - 1);
                int x1 = Mathf.Clamp(Mathf.Max(a.x, Mathf.Max(b.x, c.x)) + 1, 0, cellsW - 1);
                int y0 = Mathf.Clamp(Mathf.Min(a.y, Mathf.Min(b.y, c.y)) - 1, 0, cellsH - 1);
                int y1 = Mathf.Clamp(Mathf.Max(a.y, Mathf.Max(b.y, c.y)) + 1, 0, cellsH - 1);
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                    {
                        long key = (long)y << 32 | (uint)x;
                        if (!buckets.TryGetValue(key, out var list)) buckets[key] = list = new List<int>();
                        list.Add(t);
                    }
            }

            for (int cy = 0; cy < cellsH; cy++)
            {
                for (int cx = 0; cx < cellsW; cx++)
                {
                    // Cell center in UV space. / 单元中心对应的 UV。
                    float u = bboxMin.x + (cx + 0.5f) / cellsW * bboxSize.x;
                    float v = bboxMin.y + (cy + 0.5f) / cellsH * bboxSize.y;
                    long key = (long)cy << 32 | (uint)cx;
                    if (!buckets.TryGetValue(key, out var list)) continue;
                    foreach (var t in list)
                    {
                        var pa = uv[tris[t * 3]];
                        var pb = uv[tris[t * 3 + 1]];
                        var pc = uv[tris[t * 3 + 2]];
                        if (PointInTriangle(u, v, pa, pb, pc, 1e-5f))
                        {
                            mask[cy * cellsW + cx] = 1;
                            break;
                        }
                    }
                }
            }
        }

        private static Vector2Int UvToCell(Vector2 uv, Vector2 bboxMin, float refRes, int cellPx)
        {
            float fx = (uv.x - bboxMin.x) * refRes / cellPx;
            float fy = (uv.y - bboxMin.y) * refRes / cellPx;
            return new Vector2Int(Mathf.FloorToInt(fx), Mathf.FloorToInt(fy));
        }

        private static bool PointInTriangle(float u, float v, Vector2 a, Vector2 b, Vector2 c, float eps)
        {
            float d1 = Sign(u, v, a, b), d2 = Sign(u, v, b, c), d3 = Sign(u, v, c, a);
            bool neg = d1 < -eps || d2 < -eps || d3 < -eps;
            bool pos = d1 > eps || d2 > eps || d3 > eps;
            return !(neg && pos);
        }

        private static float Sign(float u, float v, Vector2 p1, Vector2 p2)
            => (u - p2.x) * (p1.y - p2.y) - (p1.x - p2.x) * (v - p2.y);

        /// <summary>Covered cell count. / 覆盖单元数。</summary>
        public static int CountCovered(byte[] mask)
        {
            int c = 0;
            for (int i = 0; i < mask.Length; i++) c += mask[i];
            return c;
        }

        /// <summary>
        /// Rotate a mask 90 degrees clockwise (transpose + mirror). Returns new dimensions.
        /// 把掩码顺时针旋转 90 度（转置 + 镜像）。返回新尺寸。
        /// </summary>
        public static void Rotate90(byte[] src, int w, int h, out byte[] dst, out int dw, out int dh)
        {
            dw = h; dh = w;
            dst = new byte[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    if (src[y * w + x] != 0)
                        dst[x * dw + (dw - 1 - y)] = 1; // cw: (x,y) -> (h-1-y, x)
        }

        /// <summary>Rotate a vector 90° clockwise (y-up), `times` times. / 把向量顺时针旋转 90°（y 向上），共 `times` 次。</summary>
        public static Vector2 RotateVecCw(Vector2 v, int times)
        {
            Vector2 r = v;
            for (int i = 0; i < (times & 3); i++) r = new Vector2(r.y, -r.x);
            return r;
        }

        /// <summary>True if two masks overlap at the given offset. / 判断两掩码在给定偏移处是否重叠。</summary>
        public static bool Overlaps(byte[] grid, int gw, int gh, byte[] m, int mw, int mh, int ox, int oy)
        {
            if (ox < 0 || oy < 0 || ox + mw > gw || oy + mh > gh) return true;
            for (int y = 0; y < mh; y++)
                for (int x = 0; x < mw; x++)
                    if (m[y * mw + x] != 0 && grid[(oy + y) * gw + (ox + x)] != 0)
                        return true;
            return false;
        }

        public static void Blit(byte[] grid, int gw, byte[] m, int mw, int mh, int ox, int oy)
        {
            for (int y = 0; y < mh; y++)
                for (int x = 0; x < mw; x++)
                    if (m[y * mw + x] != 0)
                        grid[(oy + y) * gw + (ox + x)] = 1;
        }
    }
}
