// ============================================================================
// ATOPacker.cs — 位掩码光栅化 + BLF 装箱 / bitmask rasterization + BLF packing
// (EN) Rasterizes islands into 4px-granularity bitmasks (triangle fill) and
//      packs them into a candidate atlas via Bottom-Left-Fill with 90° rotation
//      (bitmask transpose). CPU reference implementation; Burst acceleration is
//      layered on in a later pass (the rasterization is Burst-friendly).
// (ZH) 将岛光栅化为 4px 粒度位掩码（三角形填充），并通过带 90° 旋转（位掩码转置）
//      的 Bottom-Left-Fill 装箱到候选图集。CPU 参考实现；光栅化适合后续 Burst 加速。
// ============================================================================

using System;
using System.Collections.Generic;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer
{
    public static class ATOPacker
    {
        /// <summary>(EN) Rasterization granularity (pixels per mask cell). (ZH) 光栅化粒度（每掩码单元的像素数）。</summary>
        public const int Granularity = 4;

        // ---------------------------------------------------------------------
        // 候选图集池 / candidate atlas pool
        // ---------------------------------------------------------------------
        /// <summary>(EN) Generate candidate atlas edge lengths. (ZH) 生成候选图集边长列表。</summary>
        public static List<int> BuildCandidatePool(ATOAtlasSettings atlas, bool mobile)
        {
            int maxEdge = mobile ? atlas.maxAtlasSizeMobile : atlas.maxAtlasSizePC;
            var pool = new List<int>();
            if (atlas.allowNPot)
            {
                for (int e = 64; e <= maxEdge; e += 64) pool.Add(e);
            }
            else
            {
                for (int e = 64; e <= maxEdge; e *= 2) pool.Add(e);
            }
            return pool;
        }

        /// <summary>(EN) Build candidate atlas rects (allow non-square, near-square preferred). (ZH) 生成候选图集矩形（允许非正方形，近正方形优先）。</summary>
        public static List<(int w, int h)> BuildCandidateRects(List<int> pool, long minAreaCells)
        {
            var rects = new List<(int, int)>();
            foreach (int a in pool)
                foreach (int b in pool)
                {
                    long area = (long)a * b;
                    if (area < minAreaCells) continue;
                    rects.Add((a, b));
                }
            // 面积升序，长边/短边比升序（近正方形优先）/ area asc, aspect asc (near-square first)
            rects.Sort((x, y) =>
            {
                long ax = (long)x.Item1 * x.Item2, ay = (long)y.Item1 * y.Item2;
                if (ax != ay) return ax.CompareTo(ay);
                float rx = (float)Mathf.Max(x.Item1, x.Item2) / Mathf.Min(x.Item1, x.Item2);
                float ry = (float)Mathf.Max(y.Item1, y.Item2) / Mathf.Min(y.Item1, y.Item2);
                return rx.CompareTo(ry);
            });
            return rects;
        }

        // ---------------------------------------------------------------------
        // 光栅化 / rasterization
        // ---------------------------------------------------------------------
        /// <summary>(EN) Rasterize an island into a 4px-granularity bitmask. (ZH) 将岛光栅化为 4px 粒度位掩码。</summary>
        public static void Rasterize(ATOUVIsland island, int pixelW, int pixelH)
        {
            int mw = Mathf.Max(1, Mathf.CeilToInt(pixelW / (float)Granularity));
            int mh = Mathf.Max(1, Mathf.CeilToInt(pixelH / (float)Granularity));
            var mask = new bool[mw * mh];

            // 岛局部空间映射：raw UV → [0,1] 归一化 → 掩码坐标
            Vector2 size = island.Bounds.size;
            Vector2 min = island.Bounds.min;

            int triCount = island.TriangleUVs.Count / 3;

            // Burst 加速路径 / Burst-accelerated path
#if UNITY_BURST
            if (ATOBurst.Available && triCount >= 32)
            {
                var verts = new Unity.Mathematics.float2[triCount * 3];
                for (int t = 0; t < triCount; t++)
                {
                    verts[t * 3 + 0] = ToMaskF2(island.TriangleUVs[t * 3 + 0], island.Translation, min, size, mw, mh);
                    verts[t * 3 + 1] = ToMaskF2(island.TriangleUVs[t * 3 + 1], island.Translation, min, size, mw, mh);
                    verts[t * 3 + 2] = ToMaskF2(island.TriangleUVs[t * 3 + 2], island.Translation, min, size, mw, mh);
                }
                var bytes = new byte[mw * mh];
                ATOBurst.Rasterize(verts, bytes, mw, mh);
                for (int i = 0; i < bytes.Length; i++) mask[i] = bytes[i] != 0;
                island.RasterizedMask = mask;
                island.RasterW = mw;
                island.RasterH = mh;
                return;
            }
#endif

            // CPU 参考实现 / CPU reference implementation
            for (int t = 0; t < triCount; t++)
            {
                var a = ToMask(island.TriangleUVs[t * 3 + 0], island.Translation, min, size, mw, mh);
                var b = ToMask(island.TriangleUVs[t * 3 + 1], island.Translation, min, size, mw, mh);
                var c = ToMask(island.TriangleUVs[t * 3 + 2], island.Translation, min, size, mw, mh);
                FillTriangle(a, b, c, mask, mw, mh);
            }

            island.RasterizedMask = mask;
            island.RasterW = mw;
            island.RasterH = mh;
        }

        private static Unity.Mathematics.float2 ToMaskF2(Vector2 raw, Vector2 translation, Vector2 min, Vector2 size, int mw, int mh)
        {
            float nx = (raw.x + translation.x - min.x) / Mathf.Max(1e-6f, size.x);
            float ny = (raw.y + translation.y - min.y) / Mathf.Max(1e-6f, size.y);
            return new Unity.Mathematics.float2(nx * mw, ny * mh);
        }

        private static Vector2 ToMask(Vector2 raw, Vector2 translation, Vector2 min, Vector2 size, int mw, int mh)
        {
            float nx = (raw.x + translation.x - min.x) / Mathf.Max(1e-6f, size.x);
            float ny = (raw.y + translation.y - min.y) / Mathf.Max(1e-6f, size.y);
            return new Vector2(nx * mw, ny * mh);
        }

        private static void FillTriangle(Vector2 a, Vector2 b, Vector2 c, bool[] mask, int mw, int mh)
        {
            int minX = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(a.x, Mathf.Min(b.x, c.x))), 0, mw - 1);
            int maxX = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(a.x, Mathf.Max(b.x, c.x))), 0, mw - 1);
            int minY = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(a.y, Mathf.Min(b.y, c.y))), 0, mh - 1);
            int maxY = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(a.y, Mathf.Max(b.y, c.y))), 0, mh - 1);

            float area = Edge(a, b, c);
            if (Mathf.Abs(area) < 1e-6f) return;

            for (int y = minY; y <= maxY; y++)
                for (int x = minX; x <= maxX; x++)
                {
                    var p = new Vector2(x + 0.5f, y + 0.5f);
                    float w0 = Edge(b, c, p), w1 = Edge(c, a, p), w2 = Edge(a, b, p);
                    bool inside = area > 0 ? (w0 >= 0 && w1 >= 0 && w2 >= 0) : (w0 <= 0 && w1 <= 0 && w2 <= 0);
                    if (inside) mask[y * mw + x] = true;
                }
        }

        private static float Edge(Vector2 p0, Vector2 p1, Vector2 p) =>
            (p.x - p0.x) * (p1.y - p0.y) - (p.y - p0.y) * (p1.x - p0.x);

        // ---------------------------------------------------------------------
        // 装箱 / packing (BLF with rotation)
        // ---------------------------------------------------------------------
        /// <summary>(EN) Pack islands into an atlas of (aw, ah) pixels. Returns placements (cell coords). (ZH) 将岛装箱到 (aw,ah) 像素图集，返回位置（掩码单元坐标）。</summary>
        public static bool TryPack(List<ATOUVIsland> islands, int aw, int ah, int paddingCells, out List<ATOUVIsland> placed)
        {
            placed = new List<ATOUVIsland>();
            int awc = aw / Granularity, ahc = ah / Granularity;
            var used = new bool[awc * ahc];

            foreach (var island in islands)
            {
                int bw = island.RasterW, bh = island.RasterH;
                bool placedOk = false;

                // 尝试 4 种旋转 / try 4 rotations
                for (int rot = 0; rot < 4 && !placedOk; rot++)
                {
                    var (mask, mw, mh) = RotateMask(island, rot);
                    for (int y = 0; y + mh + paddingCells <= ahc && !placedOk; y++)
                        for (int x = 0; x + mw + paddingCells <= awc && !placedOk; x++)
                        {
                            if (CanPlace(used, awc, ahc, mask, mw, mh, x, y, paddingCells))
                            {
                                Place(used, awc, mask, mw, mh, x, y, paddingCells);
                                island.RasterX = x;
                                island.RasterY = y;
                                island.Rotated90 = (rot == 1 || rot == 3);
                                placedOk = true;
                            }
                        }
                }

                if (!placedOk) return false;
                placed.Add(island);
            }
            return true;
        }

        private static (bool[] mask, int w, int h) RotateMask(ATOUVIsland island, int rot)
        {
            if (rot == 0) return (island.RasterizedMask, island.RasterW, island.RasterH);
            int w = island.RasterW, h = island.RasterH;
            // 转置 / transpose
            var src = island.RasterizedMask;
            if (rot == 1 || rot == 3)
            {
                var t = new bool[w * h];
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                        t[x * h + y] = src[y * w + x];
                return (t, h, w);
            }
            // rot == 2: 180° (翻转)/ flip both
            var f = new bool[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    f[y * w + x] = src[(h - 1 - y) * w + (w - 1 - x)];
            return (f, w, h);
        }

        private static bool CanPlace(bool[] used, int awc, int ahc, bool[] mask, int mw, int mh, int px, int py, int pad)
        {
            for (int y = 0; y < mh; y++)
                for (int x = 0; x < mw; x++)
                {
                    if (!mask[y * mw + x]) continue;
                    for (int dy = -pad; dy <= pad; dy++)
                        for (int dx = -pad; dx <= pad; dx++)
                        {
                            int ux = px + x + dx, uy = py + y + dy;
                            if (ux < 0 || uy < 0 || ux >= awc || uy >= ahc) return false;
                            if (used[uy * awc + ux]) return false;
                        }
                }
            return true;
        }

        private static void Place(bool[] used, int awc, bool[] mask, int mw, int mh, int px, int py, int pad)
        {
            for (int y = 0; y < mh; y++)
                for (int x = 0; x < mw; x++)
                {
                    if (!mask[y * mw + x]) continue;
                    for (int dy = -pad; dy <= pad; dy++)
                        for (int dx = -pad; dx <= pad; dx++)
                        {
                            int ux = px + x + dx, uy = py + y + dy;
                            if (ux < 0 || uy < 0 || ux >= awc || uy >= ahc) continue;
                            used[uy * awc + ux] = true;
                        }
                }
        }

        /// <summary>(EN) Total rasterized cell area of islands. (ZH) 岛光栅化后的总掩码单元面积。</summary>
        public static long TotalCellArea(List<ATOUVIsland> islands)
        {
            long area = 0;
            foreach (var i in islands)
            {
                long a = 0;
                foreach (var b in i.RasterizedMask) if (b) a++;
                area += a;
            }
            return area;
        }
    }
}
