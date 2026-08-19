using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace NetFosa.AvatarTextureOptimizer.Editor.Atlas
{
    /// <summary>
    /// 图集栅格位掩码（4px 粒度）。每行用 UInt64 位掩码存储，支持快速重叠检测与放置。
    /// 岛形状光栅化使用 Burst 作业（三角形 → 4px 格），带非 Burst 兜底。
    /// </summary>
    public sealed class RasterMask
    {
        public int GridW { get; }
        public int GridH { get; }
        public int CellSize => 4;

        private ulong[] _rows;
        private readonly int _wordsPerRow;

        public RasterMask(int gridW, int gridH)
        {
            GridW = gridW;
            GridH = gridH;
            _wordsPerRow = (gridW + 63) / 64;
            _rows = new ulong[gridH * _wordsPerRow];
        }

        /// <summary>某格是否被占用。</summary>
        public bool GetCell(int x, int y)
        {
            if (x < 0 || x >= GridW || y < 0 || y >= GridH) return true;
            int word = x >> 6;
            ulong bit = 1UL << (x & 63);
            return (_rows[y * _wordsPerRow + word] & bit) != 0;
        }

        /// <summary>放置 mask（其原点在 (ox,oy)），若全部格可用则占用并返回 true。</summary>
        public bool TryPlace(RasterMask mask, int ox, int oy)
        {
            for (int y = 0; y < mask.GridH; y++)
            {
                int ay = oy + y;
                if (ay < 0 || ay >= GridH) return false;
                for (int w = 0; w < mask._wordsPerRow; w++)
                {
                    ulong mw = mask._rows[y * mask._wordsPerRow + w];
                    if (mw == 0) continue;
                    int aw = w + (ox >> 6);
                    if (aw >= _wordsPerRow) return false;
                    int shift = ox & 63;
                    ulong maskWord = mw;
                    if (shift > 0)
                    {
                        // 跨字偏移
                        ulong a = mw << shift;
                        if (a != 0)
                        {
                            if (aw >= _wordsPerRow) return false;
                            if ((_rows[ay * _wordsPerRow + aw] & a) != 0) return false;
                        }
                        ulong b = shift == 0 ? 0 : (mw >> (64 - shift));
                        if (b != 0)
                        {
                            if (aw + 1 >= _wordsPerRow) return false;
                            if ((_rows[ay * _wordsPerRow + aw + 1] & b) != 0) return false;
                        }
                    }
                    else
                    {
                        if ((_rows[ay * _wordsPerRow + aw] & mw) != 0) return false;
                    }
                }
            }
            // 占用
            for (int y = 0; y < mask.GridH; y++)
            {
                int ay = oy + y;
                for (int w = 0; w < mask._wordsPerRow; w++)
                {
                    ulong mw = mask._rows[y * mask._wordsPerRow + w];
                    if (mw == 0) continue;
                    int aw = w + (ox >> 6);
                    int shift = ox & 63;
                    if (shift > 0)
                    {
                        _rows[ay * _wordsPerRow + aw] |= mw << shift;
                        if (aw + 1 < _wordsPerRow) _rows[ay * _wordsPerRow + aw + 1] |= mw >> (64 - shift);
                    }
                    else
                    {
                        _rows[ay * _wordsPerRow + aw] |= mw;
                    }
                }
            }
            return true;
        }

        /// <summary>行占用数（用于计算实际利用率）。</summary>
        public int OccupiedCells()
        {
            int count = 0;
            for (int i = 0; i < _rows.Length; i++)
            {
                count += CountBits(_rows[i]);
            }
            return count;
        }

        private static int CountBits(ulong v)
        {
            int c = 0;
            while (v != 0) { v &= v - 1; c++; }
            return c;
        }

        /// <summary>转置（90° 旋转的位掩码）。</summary>
        public RasterMask Transposed()
        {
            var t = new RasterMask(GridH, GridW);
            for (int y = 0; y < GridH; y++)
            {
                for (int x = 0; x < GridW; x++)
                {
                    if (GetCell(x, y)) t.SetCellRaw(y, x);
                }
            }
            return t;
        }

        public void SetCellRaw(int x, int y)
        {
            if (x < 0 || x >= GridW || y < 0 || y >= GridH) return;
            int word = x >> 6;
            ulong bit = 1UL << (x & 63);
            _rows[y * _wordsPerRow + word] |= bit;
        }

        /// <summary>公开设置格（供 Dilate 等使用）。</summary>
        public void SetCellRawPublic(int x, int y) => SetCellRaw(x, y);

        // ==================================================================
        // 岛形状光栅化（Burst，带兜底）
        // ==================================================================

        /// <summary>
        /// 把岛的三角形栅格化到 4px 网格。uvBounds 为岛在 UV 空间的 AABB（已归一化 0..1），
        /// offset 为越界岛平移量（顶点 UV 需先减去）。contentW/H 为内容在目标图集中的像素尺寸
        /// （= rectUV × 图集尺寸），掩码按内容尺寸栅格化，保证掩码与最终内容区域精确一致。
        /// atlasW/H 为目标图集像素尺寸。
        /// </summary>
        public static RasterMask RasterizeIsland(System.Collections.Generic.List<int> triIndices,
            Vector2[] uvArray, int[] slotTris, Rect uvBounds, Vector2 offset, int contentW, int contentH,
            int atlasW, int atlasH, bool useBurst)
        {
            int cellW = Math.Max(1, contentW / 4);
            int cellH = Math.Max(1, contentH / 4);
            var mask = new RasterMask(cellW, cellH);

            if (triIndices == null || triIndices.Count < 3) return mask;

            // 收集三角形 UV（先减去平移量，再归一化到岛 AABB 内 0..1，再映射到内容格）
            int triCount = triIndices.Count / 3;
            var tris = new float2[triCount * 3];
            for (int t = 0; t < triCount; t++)
            {
                int i0 = slotTris[triIndices[t * 3]];
                int i1 = slotTris[triIndices[t * 3 + 1]];
                int i2 = slotTris[triIndices[t * 3 + 2]];
                tris[t * 3] = ToContentCell(uvArray[i0] - offset, uvBounds, contentW, contentH);
                tris[t * 3 + 1] = ToContentCell(uvArray[i1] - offset, uvBounds, contentW, contentH);
                tris[t * 3 + 2] = ToContentCell(uvArray[i2] - offset, uvBounds, contentW, contentH);
            }

            if (useBurst)
            {
                using var triArray = new NativeArray<float2>(tris, Allocator.TempJob);
                using var occupied = new NativeArray<int>(cellW * cellH, Allocator.TempJob, NativeArrayOptions.ClearMemory);
                var job = new RasterizeJob
                {
                    Tris = triArray,
                    TriCount = triCount,
                    GridW = cellW,
                    GridH = cellH,
                    Occupied = occupied,
                };
                job.Schedule(occupied.Length, 64).Complete();
                for (int i = 0; i < occupied.Length; i++)
                {
                    if (occupied[i] > 0) mask.SetCellRaw(i % cellW, i / cellW);
                }
            }
            else
            {
                RasterizeFallback(tris, triCount, cellW, cellH, mask);
            }
            return mask;
        }

        private static void RasterizeFallback(float2[] tris, int triCount, int cellW, int cellH, RasterMask mask)
        {
            // 三角形坐标已在内容像素单位（0..contentW）；单元中心 = (x+0.5)
            for (int t = 0; t < triCount; t++)
            {
                var a = tris[t * 3]; var b = tris[t * 3 + 1]; var c = tris[t * 3 + 2];
                int x0 = Mathf.Clamp((int)Mathf.Floor(Mathf.Min(a.x, Mathf.Min(b.x, c.x))), 0, cellW - 1);
                int x1 = Mathf.Clamp((int)Mathf.Ceil(Mathf.Max(a.x, Mathf.Max(b.x, c.x))) + 1, 0, cellW);
                int y0 = Mathf.Clamp((int)Mathf.Floor(Mathf.Min(a.y, Mathf.Min(b.y, c.y))), 0, cellH - 1);
                int y1 = Mathf.Clamp((int)Mathf.Ceil(Mathf.Max(a.y, Mathf.Max(b.y, c.y))) + 1, 0, cellH);
                for (int y = y0; y < y1; y++)
                {
                    for (int x = x0; x < x1; x++)
                    {
                        float cx = x + 0.5f;
                        float cy = y + 0.5f;
                        if (PointInTriangle(new float2(cx, cy), a, b, c)) mask.SetCellRaw(x, y);
                    }
                }
            }
        }

        /// <summary>把 UV 映射为内容网格单元坐标（0..contentW / 0..contentH）。</summary>
        private static float2 ToContentCell(Vector2 uv, Rect bounds, int contentW, int contentH)
        {
            float u = bounds.width > 1e-6f ? (uv.x - bounds.x) / bounds.width : 0f;
            float v = bounds.height > 1e-6f ? (uv.y - bounds.y) / bounds.height : 0f;
            return new float2(Mathf.Clamp01(u) * contentW, Mathf.Clamp01(v) * contentH);
        }

        private static bool PointInTriangle(float2 p, float2 a, float2 b, float2 c)
        {
            float d1 = Cross(p - a, b - a);
            float d2 = Cross(p - b, c - b);
            float d3 = Cross(p - c, a - c);
            bool neg = d1 < 0 || d2 < 0 || d3 < 0;
            bool pos = d1 > 0 || d2 > 0 || d3 > 0;
            return !(neg && pos);
        }

        private static float Cross(float2 a, float2 b) => a.x * b.y - a.y * b.x;

        [BurstCompile]
        private struct RasterizeJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float2> Tris;
            public int TriCount;
            public int GridW;
            public int GridH;
            public NativeArray<int> Occupied;

            public void Execute(int index)
            {
                int x = index % GridW;
                int y = index / GridW;
                // 三角形坐标在内容像素单位；单元中心 = (x+0.5)
                float cx = x + 0.5f;
                float cy = y + 0.5f;
                var p = new float2(cx, cy);
                int hit = 0;
                for (int t = 0; t < TriCount; t++)
                {
                    var a = Tris[t * 3];
                    var b = Tris[t * 3 + 1];
                    var c = Tris[t * 3 + 2];
                    if (PointInTriangle(p, a, b, c)) { hit++; break; }
                }
                Occupied[index] = hit;
            }

            private static float Cross(float2 a, float2 b) => a.x * b.y - a.y * b.x;

            private static bool PointInTriangle(float2 p, float2 a, float2 b, float2 c)
            {
                float d1 = Cross(p - a, b - a);
                float d2 = Cross(p - b, c - b);
                float d3 = Cross(p - c, a - c);
                bool neg = d1 < 0 || d2 < 0 || d3 < 0;
                bool pos = d1 > 0 || d2 > 0 || d3 > 0;
                return !(neg && pos);
            }
        }
    }
}
