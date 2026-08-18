using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// 4px-granularity bitmask raster + full-scan BLF + 90° rotate (transpose).
    /// 4px 粒度位掩码光栅 + 全扫描 BLF + 90 度转置旋转。法线切线不重算。
    /// </summary>
    public static class AtoBitmaskPacker
    {
        public const int Granule = 4;

        public static void Rasterize(AtoIsland isl, Mesh mesh)
        {
            int dw = Mathf.Max(1, Mathf.RoundToInt(isl.PixelBounds.width * isl.ScaleU));
            int dh = Mathf.Max(1, Mathf.RoundToInt(isl.PixelBounds.height * isl.ScaleV));
            isl.RasterW = (dw + Granule - 1) / Granule;
            isl.RasterH = (dh + Granule - 1) / Granule;
            int words = (isl.RasterW + 63) / 64;
            isl.Mask = new ulong[Math.Max(1, words * isl.RasterH)];

            var uv = AtoUvUtil.Normalize(AtoUvUtil.GetUv(mesh, isl.UvChannel), out _);
            var tris = mesh.GetTriangles(isl.Submesh);
            float u0 = isl.UvBounds.xMin, v0 = isl.UvBounds.yMin;
            float uw = Mathf.Max(isl.UvBounds.width, 1e-6f);
            float vh = Mathf.Max(isl.UvBounds.height, 1e-6f);

            if (isl.Triangles.Count >= 8)
            {
                var nuv = new NativeArray<float2>(uv.Length, Allocator.TempJob);
                for (int i = 0; i < uv.Length; i++) nuv[i] = new float2(uv[i].x, uv[i].y);
                var ntri = new NativeArray<int>(isl.Triangles.Count * 3, Allocator.TempJob);
                int p = 0;
                foreach (var t in isl.Triangles)
                {
                    ntri[p++] = tris[t * 3];
                    ntri[p++] = tris[t * 3 + 1];
                    ntri[p++] = tris[t * 3 + 2];
                }
                var nmask = new NativeArray<ulong>(isl.Mask.Length, Allocator.TempJob);
                var job = new AtoRasterJob
                {
                    RasterW = isl.RasterW,
                    RasterH = isl.RasterH,
                    Words = words,
                    Granule = Granule,
                    U0 = u0, V0 = v0, Uw = uw, Vh = vh, Dw = dw, Dh = dh,
                    Uv = nuv, Tris = ntri, Mask = nmask
                };
                job.Schedule().Complete();
                nmask.CopyTo(isl.Mask);
                nuv.Dispose(); ntri.Dispose(); nmask.Dispose();
                return;
            }

            foreach (var t in isl.Triangles)
            {
                var p0 = uv[tris[t * 3]];
                var p1 = uv[tris[t * 3 + 1]];
                var p2 = uv[tris[t * 3 + 2]];
                RasterTri(isl, ToPix(p0, u0, v0, uw, vh, dw, dh),
                    ToPix(p1, u0, v0, uw, vh, dw, dh),
                    ToPix(p2, u0, v0, uw, vh, dw, dh), words);
            }
        }

        static Vector2 ToPix(Vector2 uv, float u0, float v0, float uw, float vh, int dw, int dh)
        {
            return new Vector2((uv.x - u0) / uw * dw, (uv.y - v0) / vh * dh);
        }

        static void RasterTri(AtoIsland isl, Vector2 a, Vector2 b, Vector2 c, int words)
        {
            int minX = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(a.x, Mathf.Min(b.x, c.x)) / Granule), 0, isl.RasterW - 1);
            int maxX = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(a.x, Mathf.Max(b.x, c.x)) / Granule), 0, isl.RasterW - 1);
            int minY = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(a.y, Mathf.Min(b.y, c.y)) / Granule), 0, isl.RasterH - 1);
            int maxY = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(a.y, Mathf.Max(b.y, c.y)) / Granule), 0, isl.RasterH - 1);
            for (int y = minY; y <= maxY; y++)
            for (int x = minX; x <= maxX; x++)
            {
                var p = new Vector2((x + 0.5f) * Granule, (y + 0.5f) * Granule);
                if (Inside(p, a, b, c))
                    Set(isl.Mask, words, x, y);
            }
        }

        static bool Inside(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float s = Sign(p, a, b), t = Sign(p, b, c), u = Sign(p, c, a);
            return (s >= 0 && t >= 0 && u >= 0) || (s <= 0 && t <= 0 && u <= 0);
        }

        static float Sign(Vector2 p, Vector2 a, Vector2 b) =>
            (p.x - b.x) * (a.y - b.y) - (a.x - b.x) * (p.y - b.y);

        static void Set(ulong[] m, int words, int x, int y)
        {
            int i = y * words + x / 64;
            if ((uint)i < (uint)m.Length) m[i] |= 1UL << (x & 63);
        }

        public static ulong[] Transpose(AtoIsland isl)
        {
            int w = isl.RasterW, h = isl.RasterH;
            int words = (h + 63) / 64;
            var o = new ulong[Math.Max(1, words * w)];
            int sw = (w + 63) / 64;
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * sw + x / 64;
                if ((isl.Mask[i] & (1UL << (x & 63))) != 0)
                    o[x * words + y / 64] |= 1UL << (y & 63);
            }
            return o;
        }

        public static bool TryBlf(ulong[] atlas, int aw, int ah, ulong[] spr, int sw, int sh, int padCells, out int ox, out int oy)
        {
            ox = oy = 0;
            int aWords = (aw + 63) / 64;
            int sWords = (sw + 63) / 64;
            int maxX = aw - sw - padCells;
            int maxY = ah - sh - padCells;
            if (maxX < 0 || maxY < 0) return false;
            for (int y = 0; y <= maxY; y++)
            {
                for (int x = 0; x <= maxX; x++)
                {
                    if (!Overlaps(atlas, aWords, aw, spr, sWords, sw, sh, x, y, padCells))
                    {
                        ox = x; oy = y;
                        Stamp(atlas, aWords, spr, sWords, sw, sh, x, y);
                        return true;
                    }
                }
            }
            return false;
        }

        static bool Overlaps(ulong[] a, int awords, int aw, ulong[] s, int swords, int sw, int sh, int ox, int oy, int pad)
        {
            for (int y = 0; y < sh; y++)
            {
                int ay = oy + y;
                for (int x = 0; x < sw; x++)
                {
                    int si = y * swords + x / 64;
                    if ((s[si] & (1UL << (x & 63))) == 0) continue;
                    for (int py = -pad; py <= pad; py++)
                    for (int px = -pad; px <= pad; px++)
                    {
                        int ax = ox + x + px;
                        int ay2 = ay + py;
                        if (ax < 0 || ay2 < 0) continue;
                        int ai = ay2 * awords + ax / 64;
                        if ((uint)ai >= (uint)a.Length) continue;
                        if ((a[ai] & (1UL << (ax & 63))) != 0) return true;
                    }
                }
            }
            return false;
        }

        static void Stamp(ulong[] a, int awords, ulong[] s, int swords, int sw, int sh, int ox, int oy)
        {
            for (int y = 0; y < sh; y++)
            for (int x = 0; x < sw; x++)
            {
                int si = y * swords + x / 64;
                if ((s[si] & (1UL << (x & 63))) == 0) continue;
                int ax = ox + x, ay = oy + y;
                a[ay * awords + ax / 64] |= 1UL << (ax & 63);
            }
        }

        public static int OccupiedArea(AtoIsland isl)
        {
            int n = 0;
            foreach (var w in isl.Mask) n += BitCount(w);
            return n * Granule * Granule;
        }

        static int BitCount(ulong v)
        {
            int c = 0;
            while (v != 0) { v &= v - 1; c++; }
            return c;
        }
    }
}
