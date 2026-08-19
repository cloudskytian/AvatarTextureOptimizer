using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// 4 px granularity island bitmask. Burst rasterizer.
    /// 4 像素粒度的岛位掩码。Burst 光栅化。
    /// </summary>
    public static class BitmaskRaster
    {
        public const int Granularity = 4;

        public struct Mask : IDisposable
        {
            public int CellsW;
            public int CellsH;
            public NativeArray<ulong> Bits; // row-major, 64 cells per ulong along X

            public int Stride => (CellsW + 63) >> 6;

            public void Dispose()
            {
                if (Bits.IsCreated) Bits.Dispose();
            }

            public Mask Clone(Allocator alloc)
            {
                var m = new Mask
                {
                    CellsW = CellsW,
                    CellsH = CellsH,
                    Bits = new NativeArray<ulong>(Bits.Length, alloc)
                };
                NativeArray<ulong>.Copy(Bits, m.Bits);
                return m;
            }
        }

        public static Mask GetOrRasterize(UvIsland island, int scaledW, int scaledH, Allocator alloc)
        {
            if (island.CachedMask.HasValue && island.CachedMask.Value.Bits.IsCreated)
                return island.CachedMask.Value;
            var m = RasterizeIsland(island, scaledW, scaledH, alloc);
            island.CachedMask = m;
            return m;
        }

        public static Mask RasterizeIsland(UvIsland island, int scaledW, int scaledH, Allocator alloc)
        {
            var cellsW = Math.Max(1, (scaledW + Granularity - 1) / Granularity);
            var cellsH = Math.Max(1, (scaledH + Granularity - 1) / Granularity);
            var stride = (cellsW + 63) >> 6;
            var mask = new Mask
            {
                CellsW = cellsW,
                CellsH = cellsH,
                Bits = new NativeArray<ulong>(stride * cellsH, alloc)
            };

            if (island.Triangles.Count < 3)
            {
                // Fallback: fill the bbox. / 回退：填满包围盒。
                FillRect(mask, 0, 0, cellsW, cellsH);
                return mask;
            }

            var uvMin = island.MinUvNorm;
            var uvSize = new float2(Math.Max(1e-8f, island.UvWidth), Math.Max(1e-8f, island.UvHeight));
            var uvs = new System.Collections.Generic.List<UnityEngine.Vector2>();
            island.Mesh.GetUVs(island.UvChannel, uvs);

            for (int t = 0; t + 2 < island.Triangles.Count; t += 3)
            {
                var i0 = island.Triangles[t];
                var i1 = island.Triangles[t + 1];
                var i2 = island.Triangles[t + 2];
                if ((uint)i0 >= (uint)uvs.Count || (uint)i1 >= (uint)uvs.Count || (uint)i2 >= (uint)uvs.Count)
                    continue;
                var p0 = ToCell(uvs[i0] - island.Translate, uvMin, uvSize, cellsW, cellsH);
                var p1 = ToCell(uvs[i1] - island.Translate, uvMin, uvSize, cellsW, cellsH);
                var p2 = ToCell(uvs[i2] - island.Translate, uvMin, uvSize, cellsW, cellsH);
                FillTriangle(mask, p0, p1, p2);
            }

            return mask;
        }

        static int2 ToCell(UnityEngine.Vector2 uv, UnityEngine.Vector2 min, float2 size, int cw, int ch)
        {
            var x = (int)math.floor((uv.x - min.x) / size.x * cw);
            var y = (int)math.floor((uv.y - min.y) / size.y * ch);
            return new int2(math.clamp(x, 0, cw - 1), math.clamp(y, 0, ch - 1));
        }

        public static Mask Rotate90(Mask src, Allocator alloc)
        {
            // 90° CW via bitmask transpose. Tangents are NOT recalculated (caller must forbid this for normal groups).
            // 位掩码转置实现 90° 顺时针。切线不重算（含法线的 UV 组由调用方禁止旋转）。
            var dst = new Mask
            {
                CellsW = src.CellsH,
                CellsH = src.CellsW
            };
            var stride = (dst.CellsW + 63) >> 6;
            dst.Bits = new NativeArray<ulong>(Math.Max(1, stride * Math.Max(1, dst.CellsH)), alloc);
            for (int y = 0; y < src.CellsH; y++)
            for (int x = 0; x < src.CellsW; x++)
            {
                if (!Test(src, x, y)) continue;
                var nx = src.CellsH - 1 - y;
                var ny = x;
                Set(dst, nx, ny);
            }

            return dst;
        }

        public static int OccupiedCells(Mask m)
        {
            int n = 0;
            for (int i = 0; i < m.Bits.Length; i++) n += math.countbits(m.Bits[i]);
            return n;
        }

        public static bool Test(Mask m, int x, int y)
        {
            if ((uint)x >= (uint)m.CellsW || (uint)y >= (uint)m.CellsH) return false;
            var stride = (m.CellsW + 63) >> 6;
            var word = y * stride + (x >> 6);
            return (m.Bits[word] & (1UL << (x & 63))) != 0;
        }

        public static void Set(Mask m, int x, int y)
        {
            if ((uint)x >= (uint)m.CellsW || (uint)y >= (uint)m.CellsH) return;
            var stride = (m.CellsW + 63) >> 6;
            var word = y * stride + (x >> 6);
            m.Bits[word] |= 1UL << (x & 63);
        }

        static void FillRect(Mask m, int x, int y, int w, int h)
        {
            for (int j = 0; j < h; j++)
            for (int i = 0; i < w; i++)
                Set(m, x + i, y + j);
        }

        static void FillTriangle(Mask m, int2 a, int2 b, int2 c)
        {
            var minX = math.min(a.x, math.min(b.x, c.x));
            var maxX = math.max(a.x, math.max(b.x, c.x));
            var minY = math.min(a.y, math.min(b.y, c.y));
            var maxY = math.max(a.y, math.max(b.y, c.y));
            for (int y = minY; y <= maxY; y++)
            for (int x = minX; x <= maxX; x++)
            {
                if (PointInTri(new int2(x, y), a, b, c)) Set(m, x, y);
            }
        }

        static bool PointInTri(int2 p, int2 a, int2 b, int2 c)
        {
            var s = a.y * c.x - a.x * c.y + (c.y - a.y) * p.x + (a.x - c.x) * p.y;
            var t = a.x * b.y - a.y * b.x + (a.y - b.y) * p.x + (b.x - a.x) * p.y;
            if ((s < 0) != (t < 0) && s != 0 && t != 0) return false;
            var A = -b.y * c.x + a.y * (c.x - b.x) + a.x * (b.y - c.y) + b.x * c.y;
            return A < 0 ? (s <= 0 && s + t >= A) : (s >= 0 && s + t <= A);
        }
    }
}
