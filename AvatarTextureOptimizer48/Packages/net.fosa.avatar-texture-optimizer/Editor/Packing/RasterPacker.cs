// Atlas packing: Burst rasterized island bitmasks (4px granularity) + full-scan bottom-left
// (skyline) packing with 90-degree rotation via mask transpose.
// / 图集装箱：Burst 光栅化岛位掩码（4px 粒度）+ 全扫描左下（skyline）装箱，旋转 90° 通过掩码转置实现。
// Islands are packed by their exact rasterized shape, not by bounding rectangles.
// / 岛按光栅化后的实际形状装箱，而非包围矩形。

using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using net.fosa.avatar_texture_optimizer.editor.analysis;
using net.fosa.avatar_texture_optimizer.editor.pipeline;

namespace net.fosa.avatar_texture_optimizer.editor.packing
{
    /// <summary>Result of placing one island. / 单个岛的放置结果。</summary>
    public struct Placement
    {
        public int X, Y;          // in atlas pixels / 图集像素坐标
        public bool Rotated90;
    }

    /// <summary>An item to pack: island + target pixel size. / 待装箱项：岛 + 目标像素尺寸。</summary>
    public struct PackItem
    {
        public Island Island;
        public int W, H;          // target pixel size / 目标像素尺寸
    }

    /// <summary>
    /// Rasterizes island shapes into 4px bitmasks with a Burst job. / 用 Burst 任务把岛形状光栅化为 4px 位掩码。
    /// </summary>
    [BurstCompile]
    public struct RasterizeIslandsJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float> UvX0, UvY0, UvX1, UvY1, UvX2, UvY2;   // per triangle UVs / 每三角形 UV
        [ReadOnly] public NativeArray<int> TriOffset, TriCount;                      // per island: triangle range / 每岛的三角形范围
        [ReadOnly] public NativeArray<float> MinX, MinY, SizeX, SizeY;              // per island: bbox / 每岛包围盒
        [ReadOnly] public NativeArray<int> GridW, GridH;                            // per island: grid size (4px cells) / 每岛网格尺寸
        [WriteOnly] public NativeArray<byte> Masks;                                 // flattened bitmasks / 平铺位掩码
        [ReadOnly] public NativeArray<int> MaskStride;                              // bytes per row / 每行字节数

        private static int BitIndex(int x, int y, int stride) => y * stride + (x >> 3);
        private static byte BitMask(int x) => (byte)(1 << (x & 7));

        public void Execute(int i)
        {
            int gw = GridW[i], gh = GridH[i], stride = MaskStride[i];
            int byteCount = gh * stride;
            for (int b = 0; b < byteCount; b++) Masks[i * byteCount + b] = 0;

            float minX = MinX[i], minY = MinY[i];
            float sx = SizeX[i] > 1e-6f ? gw / SizeX[i] : 0f;
            float sy = SizeY[i] > 1e-6f ? gh / SizeY[i] : 0f;

            for (int t = 0; t < TriCount[i]; t++)
            {
                int tri = TriOffset[i] + t;
                float ax = (UvX0[tri] - minX) * sx, ay = (UvY0[tri] - minY) * sy;
                float bx = (UvX1[tri] - minX) * sx, by = (UvY1[tri] - minY) * sy;
                float cx = (UvX2[tri] - minX) * sx, cy = (UvY2[tri] - minY) * sy;

                int minGx = math.max(0, (int)math.floor(math.min(ax, math.min(bx, cx))));
                int maxGx = math.min(gw - 1, (int)math.ceil(math.max(ax, math.max(bx, cx))));
                int minGy = math.max(0, (int)math.floor(math.min(ay, math.min(by, cy))));
                int maxGy = math.min(gh - 1, (int)math.ceil(math.max(ay, math.max(by, cy))));

                for (int gy = minGy; gy <= maxGy; gy++)
                {
                    float py = gy + 0.5f;
                    for (int gx = minGx; gx <= maxGx; gx++)
                    {
                        float px = gx + 0.5f;
                        if (PointInTriangle(px, py, ax, ay, bx, by, cx, cy))
                        {
                            int idx = i * byteCount + BitIndex(gx, gy, stride);
                            Masks[idx] = (byte)(Masks[idx] | BitMask(gx));
                        }
                    }
                }
            }
        }

        private static bool PointInTriangle(float px, float py, float ax, float ay, float bx, float by, float cx, float cy)
        {
            float d1 = (bx - ax) * (py - ay) - (by - ay) * (px - ax);
            float d2 = (cx - bx) * (py - by) - (cy - by) * (px - bx);
            float d3 = (ax - cx) * (py - cy) - (ay - cy) * (px - cx);
            bool hasNeg = d1 < 0 || d2 < 0 || d3 < 0;
            bool hasPos = d1 > 0 || d2 > 0 || d3 > 0;
            return !(hasNeg && hasPos);
        }
    }

    /// <summary>
    /// Skyline bottom-left packer with 90-degree rotation. / skyline 左下装箱器，支持 90° 旋转。
    /// </summary>
    public static class RasterPacker
    {
        /// <summary>
        /// Rasterize islands to 4px bitmasks (parallel, Burst). / 并行光栅化岛为 4px 位掩码。
        /// Returns per-item mask data (grid w/h in cells, stride, byte[]). / 返回每项的掩码数据。
        /// </summary>
        public static List<MaskData> Rasterize(List<PackItem> items)
        {
            var result = new List<MaskData>(items.Count);
            if (items.Count == 0) return result;

            int triTotal = 0;
            foreach (var it in items) triTotal += it.Island.Triangles.Count;

            var uvx0 = new NativeArray<float>(triTotal, Allocator.TempJob);
            var uvy0 = new NativeArray<float>(triTotal, Allocator.TempJob);
            var uvx1 = new NativeArray<float>(triTotal, Allocator.TempJob);
            var uvy1 = new NativeArray<float>(triTotal, Allocator.TempJob);
            var uvx2 = new NativeArray<float>(triTotal, Allocator.TempJob);
            var uvy2 = new NativeArray<float>(triTotal, Allocator.TempJob);
            var triOff = new NativeArray<int>(items.Count, Allocator.TempJob);
            var triCnt = new NativeArray<int>(items.Count, Allocator.TempJob);
            var minX = new NativeArray<float>(items.Count, Allocator.TempJob);
            var minY = new NativeArray<float>(items.Count, Allocator.TempJob);
            var sizeX = new NativeArray<float>(items.Count, Allocator.TempJob);
            var sizeY = new NativeArray<float>(items.Count, Allocator.TempJob);
            var gridW = new NativeArray<int>(items.Count, Allocator.TempJob);
            var gridH = new NativeArray<int>(items.Count, Allocator.TempJob);
            var strideArr = new NativeArray<int>(items.Count, Allocator.TempJob);

            int totalBytes = 0;
            int triCursor = 0;
            for (int i = 0; i < items.Count; i++)
            {
                var iso = items[i].Island;
                int gw = Mathf.Max(1, Mathf.CeilToInt(items[i].W / 4f));
                int gh = Mathf.Max(1, Mathf.CeilToInt(items[i].H / 4f));
                int stride = (gw + 7) / 8;
                gridW[i] = gw; gridH[i] = gh; strideArr[i] = stride;
                triOff[i] = triCursor;
                triCnt[i] = iso.Triangles.Count;
                minX[i] = iso.Min.x; minY[i] = iso.Min.y;
                sizeX[i] = iso.Max.x - iso.Min.x;
                sizeY[i] = iso.Max.y - iso.Min.y;
                foreach (var t in iso.Triangles)
                {
                    var md = iso.Owner;
                    var uv = md.Uv;
                    var tris = md.Triangles;
                    uvx0[triCursor] = uv[tris[t * 3]].x; uvy0[triCursor] = uv[tris[t * 3]].y;
                    uvx1[triCursor] = uv[tris[t * 3 + 1]].x; uvy1[triCursor] = uv[tris[t * 3 + 1]].y;
                    uvx2[triCursor] = uv[tris[t * 3 + 2]].x; uvy2[triCursor] = uv[tris[t * 3 + 2]].y;
                    triCursor++;
                }
                totalBytes += gh * stride;
            }

            var masks = new NativeArray<byte>(totalBytes, Allocator.TempJob);

            var job = new RasterizeIslandsJob
            {
                UvX0 = uvx0, UvY0 = uvy0, UvX1 = uvx1, UvY1 = uvy1, UvX2 = uvx2, UvY2 = uvy2,
                TriOffset = triOff, TriCount = triCnt,
                MinX = minX, MinY = minY, SizeX = sizeX, SizeY = sizeY,
                GridW = gridW, GridH = gridH, MaskStride = strideArr, Masks = masks,
            };
            job.Schedule(items.Count, 16).Complete();

            int cursor = 0;
            for (int i = 0; i < items.Count; i++)
            {
                int gw = gridW[i], gh = gridH[i], stride = strideArr[i];
                var data = new byte[gh * stride];
                for (int b = 0; b < data.Length; b++) data[b] = masks[cursor + b];
                cursor += data.Length;
                result.Add(new MaskData { GridW = gw, GridH = gh, Stride = stride, Bits = data });
            }

            uvx0.Dispose(); uvy0.Dispose(); uvx1.Dispose(); uvy1.Dispose(); uvx2.Dispose(); uvy2.Dispose();
            triOff.Dispose(); triCnt.Dispose(); minX.Dispose(); minY.Dispose();
            sizeX.Dispose(); sizeY.Dispose(); gridW.Dispose(); gridH.Dispose();
            strideArr.Dispose(); masks.Dispose();

            return result;
        }

        /// <summary>Bitmask data of one island. / 一个岛的位掩码数据。</summary>
        public sealed class MaskData
        {
            public int GridW, GridH, Stride;
            public byte[] Bits;

            /// <summary>Transposed mask (90° rotation). / 转置掩码（90° 旋转）。</summary>
            public MaskData Transpose()
            {
                var t = new MaskData { GridW = GridH, GridH = GridW, Stride = (GridH + 7) / 8, Bits = new byte[GridW * ((GridH + 7) / 8)] };
                for (int y = 0; y < GridH; y++)
                {
                    for (int x = 0; x < GridW; x++)
                    {
                        if (Get(x, y)) t.Set(y, x);
                    }
                }
                return t;
            }

            public bool Get(int x, int y)
            {
                int byteIdx = y * Stride + (x >> 3);
                if (byteIdx < 0 || byteIdx >= Bits.Length) return false;
                return (Bits[byteIdx] & (1 << (x & 7))) != 0;
            }

            public void Set(int x, int y)
            {
                int byteIdx = y * Stride + (x >> 3);
                if (byteIdx < 0 || byteIdx >= Bits.Length) return;
                Bits[byteIdx] |= (byte)(1 << (x & 7));
            }
        }

        /// <summary>
        /// Pack items into a canvas using skyline BLF with 90° rotation.
        /// Returns false if the canvas is too small. / 用 skyline BLF 装箱；画布过小返回 false。
        /// </summary>
        public static bool TryPack(List<PackItem> items, List<MaskData> masks, int canvasSize, int padding,
            out Dictionary<int, Placement> placements)
        {
            placements = new Dictionary<int, Placement>();
            int grid = canvasSize / 4;
            if (grid <= 0) return false;
            int padCells = Mathf.Max(1, padding / 4);

            // skyline: highest occupied cell per column / 每列的最高占用格
            var skyline = new int[grid];
            // occupancy as bitset for shape tests / 形状检测用位集
            int stride = (grid + 7) / 8;
            var occ = new byte[grid * stride];

            // Sort: area desc, then long edge desc / 排序：面积降序，边长降序
            var order = new List<int>(items.Count);
            for (int i = 0; i < items.Count; i++) order.Add(i);
            order.Sort((a, b) =>
            {
                long areaA = (long)items[a].W * items[a].H;
                long areaB = (long)items[b].W * items[b].H;
                if (areaA != areaB) return areaB.CompareTo(areaA);
                int edgeA = Mathf.Max(items[a].W, items[a].H);
                int edgeB = Mathf.Max(items[b].W, items[b].H);
                return edgeB.CompareTo(edgeA);
            });

            foreach (var idx in order)
            {
                var item = items[idx];
                bool placed = false;
                // Try both orientations (original and 90-degree transposed). / 尝试两种朝向（原朝向与 90° 转置）。
                var orientations = new[] { masks[idx], masks[idx].Transpose() };
                for (int attempt = 0; attempt < orientations.Length && !placed; attempt++)
                {
                    var cur = orientations[attempt];
                    int gw = cur.GridW, gh = cur.GridH;
                    if (gw + padCells > grid || gh + padCells > grid) continue;

                    for (int x = 0; x <= grid - gw - padCells; x++)
                    {
                        // candidate y from skyline / skyline 给出候选 y
                        int y = 0;
                        for (int dx = 0; dx < gw + padCells; dx++) y = Mathf.Max(y, skyline[x + dx]);
                        while (y + gh + padCells <= grid)
                        {
                            if (ShapeFits(cur, occ, x, y, grid, stride))
                            {
                                Place(cur, occ, x, y, grid, stride);
                                for (int dx = 0; dx < gw + padCells; dx++)
                                    skyline[x + dx] = y + gh + padCells;
                                placements[idx] = new Placement
                                {
                                    X = x * 4,
                                    Y = y * 4,
                                    Rotated90 = attempt == 1,
                                };
                                placed = true;
                                break;
                            }
                            y++;
                        }
                        if (placed) break;
                    }
                }

                if (!placed) return false;
            }
            return true;
        }

        private static bool ShapeFits(MaskData mask, byte[] occ, int x, int y, int grid, int stride)
        {
            for (int gy = 0; gy < mask.GridH; gy++)
            {
                for (int gx = 0; gx < mask.GridW; gx++)
                {
                    if (!mask.Get(gx, gy)) continue;
                    int ox = x + gx, oy = y + gy;
                    if (ox >= grid || oy >= grid) return false;
                    if ((occ[oy * stride + (ox >> 3)] & (1 << (ox & 7))) != 0) return false;
                }
            }
            return true;
        }

        private static void Place(MaskData mask, byte[] occ, int x, int y, int grid, int stride)
        {
            for (int gy = 0; gy < mask.GridH; gy++)
            {
                for (int gx = 0; gx < mask.GridW; gx++)
                {
                    if (!mask.Get(gx, gy)) continue;
                    int ox = x + gx, oy = y + gy;
                    occ[oy * stride + (ox >> 3)] |= (byte)(1 << (ox & 7));
                }
            }
        }
    }
}
