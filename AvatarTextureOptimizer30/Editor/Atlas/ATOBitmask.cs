// ATOBitmask.cs — 4px 粒度位掩码（Burst）/ 4px-granularity bitmask (Burst).
// 说明：图集占用与岛形状以 4px 粒度光栅化为位掩码（每 4×4px = 1 bit）：
//  - 光栅化：保守光栅化三角形（单元 AABB 与三角形 SAT 相交判据）
//  - 旋转：90 度步进的位掩码转置（法线贴图切线数据保持原样、绝不重算）
//  - 放置：全扫描 BLF（自底向上、自左向右）与定点放置测试
// Note: atlas occupancy and island shapes are rasterized into bitmasks at 4px granularity (1 bit = 4×4 px):
// conservative triangle rasterization (cell-AABB vs triangle SAT test), 90°-step rotations via bit transpose
// (normal-map tangent data stays as-is — never recomputed), full-scan BLF placement and pinned placement tests.

using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer
{
    /// <summary>位掩码网格（每 4px 一格）。/ Bitmask grid (one cell per 4 px).</summary>
    public sealed class ATOBitmask : IDisposable
    {
        public int cellsW;                  // 宽（格）/ width in cells
        public int cellsH;                  // 高（格）/ height in cells
        public int stride;                  // 每行 ulong 数 / ulongs per row
        public NativeArray<ulong> bits;     // 位数据（行优先）/ bits (row-major)
        private bool _ownsBuffer;           // 是否拥有缓冲（浅包装不拥有，Dispose 不释放）/ whether this owns the buffer (shallow wrappers don't)

        private ATOBitmask() { }

        public ATOBitmask(int cellsW, int cellsH, Allocator alloc = Allocator.TempJob)
        {
            this.cellsW = cellsW;
            this.cellsH = cellsH;
            stride = (cellsW + 63) / 64;
            bits = new NativeArray<ulong>(stride * cellsH, alloc, NativeArrayOptions.ClearMemory);
            _ownsBuffer = true;
        }

        /// <summary>浅包装（不拥有缓冲）。/ Shallow wrapper (does not own the buffer).</summary>
        public static ATOBitmask Wrap(NativeArray<ulong> bits, int cellsW, int cellsH)
        {
            return new ATOBitmask
            {
                cellsW = cellsW,
                cellsH = cellsH,
                stride = (cellsW + 63) / 64,
                bits = bits,
                _ownsBuffer = false,
            };
        }

        public int PixelSizeW => cellsW * 4;
        public int PixelSizeH => cellsH * 4;

        public void Dispose()
        {
            if (_ownsBuffer && bits.IsCreated) bits.Dispose();
        }

        public long CountBits()
        {
            long total = 0;
            for (int i = 0; i < bits.Length; i++)
            {
                var v = bits[i];
                // popcount / 位计数
                v = v - ((v >> 1) & 0x5555555555555555UL);
                v = (v & 0x3333333333333333UL) + ((v >> 2) & 0x3333333333333333UL);
                v = (v + (v >> 4)) & 0x0F0F0F0F0F0F0F0FUL;
                total += (long)((v * 0x0101010101010101UL) >> 56);
            }
            return total;
        }
    }

    /// <summary>位掩码操作（Burst 作业）。/ Bitmask operations (Burst jobs).</summary>
    internal static class ATOBitmaskOps
    {
        // ================= 光栅化 / rasterization =================

        /// <summary>三角形数据结构（图集像素空间）。/ Triangle data (atlas pixel space).</summary>
        public struct Tri
        {
            public float2 a, b, c;
        }

        /// <summary>光栅化任务。/ Rasterization job.</summary>
        [BurstCompile]
        private struct RasterizeJob : IJob
        {
            public NativeArray<ulong> bits;
            public int stride;
            public int cellsW;
            public int cellsH;
            [ReadOnly] public NativeArray<Tri> tris;

            public void Execute()
            {
                foreach (var tri in tris)
                {
                    // 三角形 AABB（像素空间 → 格空间）/ triangle AABB (pixel → cell space)
                    var minP = math.min(math.min(tri.a, tri.b), tri.c);
                    var maxP = math.max(math.max(tri.a, tri.b), tri.c);
                    var x0 = math.clamp((int)math.floor(minP.x / 4f), 0, cellsW - 1);
                    var x1 = math.clamp((int)math.floor(maxP.x / 4f), 0, cellsW - 1);
                    var y0 = math.clamp((int)math.floor(minP.y / 4f), 0, cellsH - 1);
                    var y1 = math.clamp((int)math.floor(maxP.y / 4f), 0, cellsH - 1);
                    for (int cy = y0; cy <= y1; cy++)
                    {
                        for (int cx = x0; cx <= x1; cx++)
                        {
                            if (CellTriOverlap(cx * 4, cy * 4, tri))
                            {
                                var bit = 1UL << (cx & 63);
                                bits[cy * stride + (cx >> 6)] |= bit;
                            }
                        }
                    }
                }
            }

            /// <summary>单元 AABB 与三角形 SAT 相交。/ Cell AABB vs triangle SAT overlap test.</summary>
            private static bool CellTriOverlap(float cellX, float cellY, Tri tri)
            {
                // 单元角点 / cell corners
                float2 c0 = new float2(cellX, cellY);
                float2 c1 = new float2(cellX + 4, cellY);
                float2 c2 = new float2(cellX + 4, cellY + 4);
                float2 c3 = new float2(cellX, cellY + 4);

                // 分离轴：三角形边法线 / separating axes: triangle edge normals
                if (SeparatedByAxis(tri.a, tri.b, tri.c, c0, c1, c2, c3)) return false;
                if (SeparatedByAxis(tri.b, tri.c, tri.a, c0, c1, c2, c3)) return false;
                if (SeparatedByAxis(tri.c, tri.a, tri.b, c0, c1, c2, c3)) return false;
                // 分离轴：单元边（AABB 轴）/ separating axes: cell (AABB) axes
                if (SeparatedByAxis(c0, c1, c2, c3, tri.a, tri.b, tri.c)) return false;
                if (SeparatedByAxis(c0, c3, c2, c1, tri.a, tri.b, tri.c)) return false;
                return true;
            }

            private static bool SeparatedByAxis(float2 p1, float2 p2, float2 p3,
                float2 q1, float2 q2, float2 q3, float2 q4)
            {
                var edge = p2 - p1;
                var axis = new float2(-edge.y, edge.x);
                if (math.lengthsq(axis) < 1e-12f) return false;
                float minP = math.min(math.min(math.dot(axis, p1), math.dot(axis, p2)), math.dot(axis, p3));
                float maxP = math.max(math.max(math.dot(axis, p1), math.dot(axis, p2)), math.dot(axis, p3));
                float minQ = math.min(math.min(math.dot(axis, q1), math.dot(axis, q2)), math.min(math.dot(axis, q3), math.dot(axis, q4)));
                float maxQ = math.max(math.max(math.dot(axis, q1), math.dot(axis, q2)), math.max(math.dot(axis, q3), math.dot(axis, q4)));
                return maxP < minQ || maxQ < minP;
            }
        }

        /// <summary>将三角形集合光栅化进位掩码（保守）。/ Rasterize triangles into a bitmask (conservative).</summary>
        public static void Rasterize(ATOBitmask mask, List<Tri> tris)
        {
            var arr = new NativeArray<Tri>(tris.ToArray(), Allocator.TempJob);
            try
            {
                var job = new RasterizeJob
                {
                    bits = mask.bits,
                    stride = mask.stride,
                    cellsW = mask.cellsW,
                    cellsH = mask.cellsH,
                    tris = arr,
                };
                job.Run();
            }
            finally
            {
                arr.Dispose();
            }
        }

        /// <summary>填充矩形（4px 对齐）。/ Fill a rectangle (4px aligned).</summary>
        [BurstCompile]
        private struct FillRectJob : IJob
        {
            public NativeArray<ulong> bits;
            public int stride;
            public int x0, y0, x1, y1; // 格坐标（含）/ cell coords (inclusive)

            public void Execute()
            {
                for (int y = y0; y <= y1; y++)
                {
                    var row = y * stride;
                    for (int x = x0; x <= x1; x++)
                        bits[row + (x >> 6)] |= 1UL << (x & 63);
                }
            }
        }

        public static void FillRect(ATOBitmask mask, int cellX0, int cellY0, int cellX1, int cellY1)
        {
            var job = new FillRectJob
            {
                bits = mask.bits,
                stride = mask.stride,
                x0 = math.max(0, cellX0),
                y0 = math.max(0, cellY0),
                x1 = math.min(mask.cellsW - 1, cellX1),
                y1 = math.min(mask.cellsH - 1, cellY1),
            };
            job.Run();
        }

        // ================= 旋转（位掩码转置）/ rotation (bit transpose) =================

        [BurstCompile]
        private struct TransposeJob : IJob
        {
            [ReadOnly] public NativeArray<ulong> src;
            public int srcW;
            public int srcH;
            public int srcStride;
            [WriteOnly] public NativeArray<ulong> dst; // 新宽=srcH，新高=srcW / new width=srcH, new height=srcW
            public int dstStride;

            public void Execute()
            {
                for (int y = 0; y < srcH; y++)
                {
                    var row = y * srcStride;
                    for (int x = 0; x < srcW; x++)
                    {
                        if ((src[row + (x >> 6)] & (1UL << (x & 63))) == 0) continue;
                        // 90° 顺时针转置：新 (nx, ny) = (srcH-1-y, x) / 90° CW transpose: new (nx, ny) = (srcH-1-y, x)
                        var nx = srcH - 1 - y;
                        var ny = x;
                        dst[ny * dstStride + (nx >> 6)] |= 1UL << (nx & 63);
                    }
                }
            }
        }

        /// <summary>90° 顺时针旋转后的新位掩码。/ New bitmask rotated 90° clockwise.</summary>
        public static ATOBitmask Rotate90(ATOBitmask src, Allocator alloc = Allocator.TempJob)
        {
            var dst = new ATOBitmask(src.cellsH, src.cellsW, alloc);
            var job = new TransposeJob
            {
                src = src.bits,
                srcW = src.cellsW,
                srcH = src.cellsH,
                srcStride = src.stride,
                dst = dst.bits,
                dstStride = dst.stride,
            };
            job.Run();
            return dst;
        }

        // ================= 放置测试 / placement tests =================

        /// <summary>定点放置测试（item 左上角在格坐标 x,y）。/ Pinned placement test (item top-left at cell x,y).</summary>
        public static bool FitsAt(NativeArray<ulong> occupancy, int occStride, int occW, int occH,
            NativeArray<ulong> item, int itemStride, int itemW, int itemH, int x, int y)
        {
            if (x < 0 || y < 0 || x + itemW > occW || y + itemH > occH) return false;
            for (int row = 0; row < itemH; row++)
            {
                var occRow = (y + row) * occStride;
                var itemRow = row * itemStride;
                var shift = x & 63;
                var lane = x >> 6;
                for (int l = 0; l < itemStride; l++)
                {
                    var v = item[itemRow + l];
                    if (v == 0) continue;
                    var lo = v << shift;
                    var hi = shift == 0 ? 0UL : v >> (64 - shift);
                    if ((occupancy[occRow + lane + l] & lo) != 0) return false;
                    if (hi != 0 && lane + l + 1 < occStride && (occupancy[occRow + lane + l + 1] & hi) != 0) return false;
                }
            }
            return true;
        }

        /// <summary>
        /// 全扫描 BLF 放置：自底向上、自左向右扫描，返回第一个可行位置。
        /// Full-scan BLF placement: bottom-to-top, left-to-right; returns the first feasible position.
        /// </summary>
        public static bool TryPlaceBlf(NativeArray<ulong> occupancy, int occStride, int occW, int occH,
            NativeArray<ulong> item, int itemStride, int itemW, int itemH, out int x, out int y)
        {
            for (int cy = 0; cy + itemH <= occH; cy++)
            {
                for (int cx = 0; cx + itemW <= occW; cx++)
                {
                    if (FitsAt(occupancy, occStride, occW, occH, item, itemStride, itemW, itemH, cx, cy))
                    {
                        x = cx;
                        y = cy;
                        return true;
                    }
                }
            }
            x = 0;
            y = 0;
            return false;
        }

        /// <summary>将 item 掩码写入占用掩码。/ Stamp the item mask into the occupancy mask.</summary>
        [BurstCompile]
        private struct StampJob : IJob
        {
            public NativeArray<ulong> occupancy;
            public int occStride;
            [ReadOnly] public NativeArray<ulong> item;
            public int itemStride;
            public int itemW;
            public int itemH;
            public int x;
            public int y;

            public void Execute()
            {
                for (int row = 0; row < itemH; row++)
                {
                    var occRow = (y + row) * occStride;
                    var itemRow = row * itemStride;
                    var shift = x & 63;
                    var lane = x >> 6;
                    for (int l = 0; l < itemStride; l++)
                    {
                        var v = item[itemRow + l];
                        if (v == 0) continue;
                        occupancy[occRow + lane + l] |= v << shift;
                        if (shift != 0 && lane + l + 1 < occStride) occupancy[occRow + lane + l + 1] |= v >> (64 - shift);
                    }
                }
            }
        }

        public static void Stamp(NativeArray<ulong> occupancy, int occStride,
            NativeArray<ulong> item, int itemStride, int itemW, int itemH, int x, int y)
        {
            var job = new StampJob
            {
                occupancy = occupancy,
                occStride = occStride,
                item = item,
                itemStride = itemStride,
                itemW = itemW,
                itemH = itemH,
                x = x,
                y = y,
            };
            job.Run();
        }
    }
}
