// IslandRasterizer.cs
// Rasterizes island triangles into a bbox-local bit/byte coverage grid at configurable
// cell size (packing uses 4px cells; quality uses 1px). Provides dilation and 90°
// transposition (bitmask) for the packer. / 将岛三角形光栅化为 bbox 局部覆盖网格
// (装箱用4px格;质量用1px),提供膨胀与90°转置(位掩码)供装箱器使用。
// Copyright (c) 2026 fosa. Licensed under the MIT License.

using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace net.fosa.ato
{
    /// <summary>Bit-packed coverage mask over an integer grid. / 整数网格上的位打包覆盖掩码。</summary>
    internal sealed class IslandRasterMask
    {
        internal int W, H;              // grid cells / 格数
        internal ulong[] Words;         // row-major bits, LSB = leftmost / 行主序,最低位在最左
        internal float CellSize;        // pixels per cell in mask space / 掩码空间的每格像素数
        /// <summary>Byte coverage (same grid), used by quality jobs. / 字节覆盖(同网格),质量作业用。</summary>
        internal byte[] Bytes;
        /// <summary>Bbox origin in texture pixels. / 纹理像素空间 bbox 原点。</summary>
        internal int OriginX, OriginY;
        /// <summary>Bbox size in texture pixels. / 纹理像素空间 bbox 尺寸。</summary>
        internal int PixelW, PixelH;
        /// <summary>Content offset within a dilated mask (cells). / 膨胀掩码内的内容偏移(格)。</summary>
        internal int ContentOffset;

        internal bool Get(int x, int y)
        {
            if (x < 0 || y < 0 || x >= W || y >= H) return false;
            return (Words[y * WordStride() + (x >> 6)] & (1ul << (x & 63))) != 0;
        }

        internal int WordStride() => (W + 63) / 64;

        internal long SetCount()
        {
            long c = 0;
            foreach (var w in Words) c += BitCount.Popcount(w);
            return c;
        }
    }

    // Unity 2022 has System.Numerics.BitOperations; guard with a tiny shim to avoid
    // dependency surprises. / Unity 2022 自带 BitOperations,这里做小垫片防意外。
    internal static class BitCount
    {
        internal static long Popcount(ulong v)
        {
            v = v - ((v >> 1) & 0x5555555555555555ul);
            v = (v & 0x3333333333333333ul) + ((v >> 2) & 0x3333333333333333ul);
            v = (v + (v >> 4)) & 0x0F0F0F0F0F0F0F0Ful;
            return (long)((v * 0x0101010101010101ul) >> 56);
        }
    }

    internal static class IslandRasterizer
    {
        /// <summary>
        /// Rasterize island triangles into a coverage mask over the pixel bbox.
        /// / 将岛三角形光栅化到像素 bbox 的覆盖掩码。
        /// </summary>
        internal static IslandRasterMask RasterizePixels(Vector2[] uvs, List<int> triangles, Rect uvBounds,
            int texW, int texH, int cellPx)
        {
            int gw = Mathf.Max(1, Mathf.CeilToInt(uvBounds.width * texW / cellPx));
            int gh = Mathf.Max(1, Mathf.CeilToInt(uvBounds.height * texH / cellPx));
            int px = Mathf.Clamp(Mathf.FloorToInt(uvBounds.xMin * texW), 0, texW - 1);
            int py = Mathf.Clamp(Mathf.FloorToInt(uvBounds.yMin * texH), 0, texH - 1);
            var shell = new IslandRasterMask
            {
                W = gw, H = gh, CellSize = cellPx,
                OriginX = px, OriginY = py,
                PixelW = Mathf.Max(1, Mathf.CeilToInt(uvBounds.width * texW)),
                PixelH = Mathf.Max(1, Mathf.CeilToInt(uvBounds.height * texH)),
            };

            var bytes = new NativeArray<byte>(gw * gh, Allocator.TempJob);
            var tri = new NativeArray<int>(triangles.ToArray(), Allocator.TempJob);
            var uvArr = new NativeArray<Vector2>(uvs, Allocator.TempJob);
            try
            {
                var job = new RasterizeJob
                {
                    Uvs = uvArr,
                    Triangles = tri,
                    TriCount = tri.Length / 3,
                    BoundsMin = new float2(uvBounds.xMin, uvBounds.yMin),
                    BoundsSize = new float2(uvBounds.width, uvBounds.height),
                    TexW = texW, TexH = texH, CellPx = cellPx,
                    Gw = gw, Gh = gh,
                    Coverage = bytes,
                };
                job.Schedule().Complete();
                return Pack(bytes, gw, gh, cellPx, shell);
            }
            finally
            {
                bytes.Dispose();
                tri.Dispose();
                uvArr.Dispose();
            }
        }

        private static IslandRasterMask Pack(NativeArray<byte> bytes, int gw, int gh, float cellPx, IslandRasterMask shell)
        {
            var stride = (gw + 63) / 64;
            var words = new ulong[gh * stride];
            var byteArr = new byte[gw * gh];
            for (int y = 0; y < gh; y++)
            for (int x = 0; x < gw; x++)
            {
                if (bytes[y * gw + x] == 0) continue;
                words[y * stride + (x >> 6)] |= 1ul << (x & 63);
                byteArr[y * gw + x] = 1;
            }
            shell.Words = words;
            shell.Bytes = byteArr;
            return shell;
        }

        /// <summary>
        /// Dilate by whole cells (chebyshev) on an EXPANDED grid: result dims grow by 2*cells,
        /// original content sits at offset (cells, cells). / 按格膨胀(切比雪夫),网格扩边:
        /// 结果尺寸增加 2*cells,原内容位于偏移 (cells,cells)。
        /// </summary>
        internal static IslandRasterMask Dilate(IslandRasterMask m, int cells)
        {
            if (cells <= 0) return m;
            if (cells >= 63) cells = 63;

            int nw = m.W + cells * 2, nh = m.H + cells * 2;
            int nstride = (nw + 63) / 64;

            // copy into expanded grid / 拷入扩边网格
            var grid = new ulong[nh * nstride];
            int ostride = m.WordStride();
            for (int y = 0; y < m.H; y++)
            for (int x = 0; x < m.W; x++)
            {
                if ((m.Words[y * ostride + (x >> 6)] & (1ul << (x & 63))) == 0) continue;
                int nx = x + cells, ny = y + cells;
                grid[ny * nstride + (nx >> 6)] |= 1ul << (nx & 63);
            }

            // horizontal dilation / 水平膨胀
            var hbuf = new ulong[grid.Length];
            for (int y = 0; y < nh; y++)
            {
                int row = y * nstride;
                for (int i = 0; i < nstride; i++)
                {
                    ulong v = grid[row + i];
                    ulong acc = v;
                    for (int s = 1; s <= cells; s++)
                    {
                        ulong fromLeft = i > 0 ? grid[row + i - 1] >> (64 - s) : 0;
                        ulong fromRight = i + 1 < nstride ? grid[row + i + 1] << (64 - s) : 0;
                        acc |= (v << s) | fromLeft | (v >> s) | fromRight;
                    }
                    hbuf[row + i] = acc;
                }
            }

            // vertical dilation / 垂直膨胀
            var dst = new ulong[grid.Length];
            for (int y = 0; y < nh; y++)
            {
                int row = y * nstride;
                int r0 = Mathf.Max(0, y - cells), r1 = Mathf.Min(nh - 1, y + cells);
                for (int r = r0; r <= r1; r++)
                {
                    int srow = r * nstride;
                    for (int i = 0; i < nstride; i++) dst[row + i] |= hbuf[srow + i];
                }
            }

            var res = new IslandRasterMask { W = nw, H = nh, Words = dst, CellSize = m.CellSize };
            res.ContentOffset = cells;
            return res;
        }

        /// <summary>Transpose (rotate 90°) a mask for rotated placements. / 转置(旋转90°)掩码。</summary>
        internal static IslandRasterMask Transpose(IslandRasterMask m)
        {
            var stride = m.WordStride();
            var nw = m.H;
            var nh = m.W;
            var nstride = (nw + 63) / 64;
            var dst = new ulong[nh * nstride];
            for (int y = 0; y < m.H; y++)
            for (int x = 0; x < m.W; x++)
            {
                if ((m.Words[y * stride + (x >> 6)] & (1ul << (x & 63))) == 0) continue;
                dst[x * nstride + (y >> 6)] |= 1ul << (y & 63);
            }
            return new IslandRasterMask { W = nw, H = nh, Words = dst, CellSize = m.CellSize };
        }
    }

    /// <summary>Triangle rasterization with conservative edge tests. / 保守边测试三角形光栅化。</summary>
    [BurstCompile]
    internal struct RasterizeJob : IJob
    {
        [ReadOnly] public NativeArray<Vector2> Uvs;
        [ReadOnly] public NativeArray<int> Triangles;
        public int TriCount;
        public float2 BoundsMin, BoundsSize;
        public int TexW, TexH, CellPx, Gw, Gh;
        [WriteOnly] public NativeArray<byte> Coverage;

        public void Execute()
        {
            for (int t = 0; t < TriCount; t++)
            {
                var a = Uvs[Triangles[t * 3]];
                var b = Uvs[Triangles[t * 3 + 1]];
                var c = Uvs[Triangles[t * 3 + 2]];
                var pa = ToGrid(a); var pb = ToGrid(b); var pc = ToGrid(c);

                float minx = math.min(pa.x, math.min(pb.x, pc.x));
                float maxx = math.max(pa.x, math.max(pb.x, pc.x));
                float miny = math.min(pa.y, math.min(pb.y, pc.y));
                float maxy = math.max(pa.y, math.max(pb.y, pc.y));

                int x0 = math.clamp((int)math.floor(minx), 0, Gw - 1);
                int x1 = math.clamp((int)math.ceil(maxx), 0, Gw - 1);
                int y0 = math.clamp((int)math.floor(miny), 0, Gh - 1);
                int y1 = math.clamp((int)math.ceil(maxy), 0, Gh - 1);

                for (int y = y0; y <= y1; y++)
                {
                    for (int x = x0; x <= x1; x++)
                    {
                        // conservative cell test: cell center ± half cell / 保守格测试:格中心±半格
                        float cx0 = x - 0.5f, cx1 = x + 0.5f, cy0 = y - 0.5f, cy1 = y + 0.5f;
                        if (BoxTouchesTriangle(cx0, cy0, cx1, cy1, pa, pb, pc))
                            Coverage[y * Gw + x] = 1;
                    }
                }
            }
        }

        private float2 ToGrid(Vector2 uv)
        {
            return new float2(
                (uv.x - BoundsMin.x) * TexW / CellPx,
                (uv.y - BoundsMin.y) * TexH / CellPx);
        }

        private static bool BoxTouchesTriangle(float x0, float y0, float x1, float y1,
            float2 a, float2 b, float2 c)
        {
            // SAT: box vs triangle / 分离轴测试
            // triangle edges / 三角形边
            if (EdgeOut(a, b, x0, y0, x1, y1)) return false;
            if (EdgeOut(b, c, x0, y0, x1, y1)) return false;
            if (EdgeOut(c, a, x0, y0, x1, y1)) return false;
            // box axes / 盒轴
            float tminx = math.min(a.x, math.min(b.x, c.x));
            float tmaxx = math.max(a.x, math.max(b.x, c.x));
            float tminy = math.min(a.y, math.min(b.y, c.y));
            float tmaxy = math.max(a.y, math.max(b.y, c.y));
            if (tmaxx < x0 || tminx > x1 || tmaxy < y0 || tminy > y1) return false;
            return true;
        }

        private static bool EdgeOut(float2 p, float2 q, float x0, float y0, float x1, float y1)
        {
            // separating axis = normal of edge pq / 分离轴 = pq 边法线
            float nx = -(q.y - p.y), ny = q.x - p.x;
            float pmin = math.min(nx * x0, nx * x1) + math.min(ny * y0, ny * y1);
            float pmax = math.max(nx * x0, nx * x1) + math.max(ny * y0, ny * y1);
            float dp = nx * p.x + ny * p.y;
            float dq = nx * q.x + ny * q.y;
            float emin = math.min(dp, dq), emax = math.max(dp, dq);
            return pmax < emin || pmin > emax;
        }
    }
}
