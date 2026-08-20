using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using Net.Fosa.AvatarTextureOptimizer;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// 4px Burst raster + full-scan BLF + candidate atlas pool.
    /// 4px Burst 光栅 + 全扫描 BLF + 候选图集池。
    /// Packing uses island shapes, not rectangles. / 按岛形状装箱，不用矩形。
    /// Atomic unit: one texture + its UV group. / 原子单位：一张贴图及其 UV 组。
    /// </summary>
    public static class AtoAtlasPacker
    {
        public const int Cell = 4;

        public static List<(int w, int h)> CandidatePool(AtoContext ctx)
        {
            var max = AtoPlatformUtil.MaxAtlasEdge(ctx.Platform);
            var list = new List<(int w, int h)>();
            if (ctx.Settings.experimentalNpot)
            {
                for (var s = 64; s <= max; s += 64)
                for (var t = 64; t <= max; t += 64)
                    list.Add((s, t));
            }
            else
            {
                for (var s = 64; s <= max; s <<= 1)
                for (var t = 64; t <= max; t <<= 1)
                    list.Add((s, t));
            }
            return list;
        }

        public static int PaddingFor(int maxEdge, int minPad)
        {
            return Mathf.Max(minPad, Mathf.CeilToInt(maxEdge / 128f));
        }

        public static NativeMaskRef RasterIsland(AtoContext ctx, Mesh mesh, int submesh, int uvChannel,
            AtoIsland isl, int texW, int texH)
        {
            var cellsW = Mathf.Max(1, Mathf.CeilToInt(texW / (float)Cell));
            var cellsH = Mathf.Max(1, Mathf.CeilToInt(texH / (float)Cell));
            var cells = new NativeArray<byte>(cellsW * cellsH, Allocator.TempJob);
            var tris = mesh.GetTriangles(submesh);
            var uvs = new List<Vector2>();
            mesh.GetUVs(uvChannel, uvs);
            var n = isl.Triangles.Count;
            var a = new NativeArray<float2>(n, Allocator.TempJob);
            var b = new NativeArray<float2>(n, Allocator.TempJob);
            var c = new NativeArray<float2>(n, Allocator.TempJob);
            for (var i = 0; i < n; i++)
            {
                var t = isl.Triangles[i];
                var i0 = tris[t * 3]; var i1 = tris[t * 3 + 1]; var i2 = tris[t * 3 + 2];
                a[i] = new float2(uvs[i0].x + isl.UvTranslate.x, uvs[i0].y + isl.UvTranslate.y);
                b[i] = new float2(uvs[i1].x + isl.UvTranslate.x, uvs[i1].y + isl.UvTranslate.y);
                c[i] = new float2(uvs[i2].x + isl.UvTranslate.x, uvs[i2].y + isl.UvTranslate.y);
            }
            new AtoRasterTrisJob
            {
                UvA = a, UvB = b, UvC = c, CellsW = cellsW, CellsH = cellsH,
                TexW = texW, TexH = texH, CellPx = Cell, Cells = cells
            }.Schedule(n, 8).Complete();
            a.Dispose(); b.Dispose(); c.Dispose();

            var bits = new ulong[(cellsW * cellsH + 63) / 64];
            for (var i = 0; i < cellsW * cellsH; i++)
                if (cells[i] != 0) bits[i >> 6] |= 1UL << (i & 63);
            cells.Dispose();
            return new NativeMaskRef { CellsW = cellsW, CellsH = cellsH, Bits = bits };
        }

        public static NativeMaskRef CropToIsland(NativeMaskRef full, AtoIsland isl, int texW, int texH)
        {
            var x0 = Mathf.Clamp(Mathf.FloorToInt(isl.UvRect.xMin * texW / Cell), 0, full.CellsW - 1);
            var y0 = Mathf.Clamp(Mathf.FloorToInt(isl.UvRect.yMin * texH / Cell), 0, full.CellsH - 1);
            var x1 = Mathf.Clamp(Mathf.CeilToInt(isl.UvRect.xMax * texW / Cell), 0, full.CellsW);
            var y1 = Mathf.Clamp(Mathf.CeilToInt(isl.UvRect.yMax * texH / Cell), 0, full.CellsH);
            var w = Mathf.Max(1, x1 - x0);
            var h = Mathf.Max(1, y1 - y0);
            var bits = new ulong[(w * h + 63) / 64];
            for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                if (Get(full, x0 + x, y0 + y))
                    Set(bits, w, x, y, true);
            }
            return new NativeMaskRef { CellsW = w, CellsH = h, Bits = bits };
        }

        public static NativeMaskRef Transpose(NativeMaskRef m)
        {
            var bits = new ulong[(m.CellsW * m.CellsH + 63) / 64];
            var r = new NativeMaskRef { CellsW = m.CellsH, CellsH = m.CellsW, Bits = bits };
            for (var y = 0; y < m.CellsH; y++)
            for (var x = 0; x < m.CellsW; x++)
                if (Get(m, x, y)) Set(r.Bits, r.CellsW, y, m.CellsW - 1 - x, true);
            return r;
        }

        public static int OccupiedCells(NativeMaskRef m)
        {
            var n = 0;
            if (m.Bits == null) return 0;
            foreach (var b in m.Bits) n += CountBits(b);
            return n;
        }

        private static int CountBits(ulong v)
        {
            var c = 0;
            while (v != 0) { c++; v &= v - 1; }
            return c;
        }

        public static bool Get(NativeMaskRef m, int x, int y)
        {
            if ((uint)x >= (uint)m.CellsW || (uint)y >= (uint)m.CellsH) return false;
            var i = y * m.CellsW + x;
            return (m.Bits[i >> 6] & (1UL << (i & 63))) != 0;
        }

        public static void Set(ulong[] bits, int w, int x, int y, bool v)
        {
            var i = y * w + x;
            if (v) bits[i >> 6] |= 1UL << (i & 63);
            else bits[i >> 6] &= ~(1UL << (i & 63));
        }

        public struct Place
        {
            public bool Ok;
            public int X, Y; // cells
            public bool Rot90;
        }

        public static Place FindPlace(byte[] atlas, int aw, int ah, NativeMaskRef mask, NativeMaskRef mask90, int padCells)
        {
            var p = Try(atlas, aw, ah, mask, false, padCells);
            if (p.Ok) return p;
            return Try(atlas, aw, ah, mask90, true, padCells);
        }

        private static Place Try(byte[] atlas, int aw, int ah, NativeMaskRef m, bool rot, int pad)
        {
            var pw = m.CellsW + pad;
            var ph = m.CellsH + pad;
            if (pw > aw || ph > ah) return default;
            for (var y = 0; y <= ah - ph; y++)
            for (var x = 0; x <= aw - pw; x++)
            {
                if (Fits(atlas, aw, ah, m, x, y, pad))
                    return new Place { Ok = true, X = x, Y = y, Rot90 = rot };
            }
            return default;
        }

        private static bool Fits(byte[] atlas, int aw, int ah, NativeMaskRef m, int ox, int oy, int pad)
        {
            for (var y = 0; y < m.CellsH; y++)
            for (var x = 0; x < m.CellsW; x++)
            {
                if (!Get(m, x, y)) continue;
                if (atlas[(oy + y) * aw + (ox + x)] != 0) return false;
            }
            return true;
        }

        public static void Stamp(byte[] atlas, int aw, NativeMaskRef m, int ox, int oy)
        {
            for (var y = 0; y < m.CellsH; y++)
            for (var x = 0; x < m.CellsW; x++)
            {
                if (!Get(m, x, y)) continue;
                atlas[(oy + y) * aw + (ox + x)] = 1;
            }
        }

        /// <summary>
        /// Sort candidate sizes: drop those smaller than needed area, then area asc, then long/short asc (square first).
        /// 候选尺寸排序：丢掉面积不够的，再按面积升序、长宽比升序（越接近正方形越优先）。
        /// </summary>
        public static List<(int w, int h)> SortCandidates(List<(int w, int h)> pool, int neededPixels)
        {
            var list = new List<(int w, int h)>();
            foreach (var c in pool)
                if ((long)c.w * c.h >= neededPixels) list.Add(c);
            list.Sort((a, b) =>
            {
                var aa = (long)a.w * a.h; var ba = (long)b.w * b.h;
                var c = aa.CompareTo(ba);
                if (c != 0) return c;
                var ra = Ratio(a.w, a.h); var rb = Ratio(b.w, b.h);
                return ra.CompareTo(rb);
            });
            return list;
        }

        private static float Ratio(int w, int h)
        {
            var lo = Mathf.Min(w, h); var hi = Mathf.Max(w, h);
            return lo == 0 ? 999 : hi / (float)lo;
        }
    }
}
