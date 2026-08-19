// ============================================================================
// AvatarTextureOptimizer (net.fosa.avatar-texture-optimizer)
// Packing/BitmaskRasterizer.cs — Burst 光栅位掩码 / Burst rasterization into bitmasks
//
// 需求: 图集装箱采用 Unity Burst 光栅位掩码（4px 粒度光栅化）；装箱直接用岛形状（非矩形）。
// 实现: 把岛三角形映射到岛局部像素坐标(finalW×finalH)，以 4px 块为粒度做点-三角形包含测试。
// ============================================================================
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor
{
    /// <summary>
    /// Burst 岛光栅化作业：把岛三角形栅格化为 4px 粒度位掩码（1 bit = 4×4 像素块）。
    /// Burst island rasterization: triangles → 4px-granularity bitmask (1 bit = 4×4 px block).
    /// </summary>
    [BurstCompile]
    internal struct RasterizeIslandJob : IJobParallelFor
    {
        /// <summary>三角形顶点（每三角形 3 个 float2，岛局部像素坐标）/ triangle vertices (3 float2 per tri, island-local px)</summary>
        [ReadOnly] public NativeArray<float2> triVerts;
        /// <summary>三角形数量 / triangle count</summary>
        public int triCount;
        /// <summary>位掩码（每块 1 bit）/ bitmask (1 bit per block)</summary>
        public NativeArray<ulong> mask;
        /// <summary>块宽高（= ceil(finalW/4), ceil(finalH/4)）/ block dims</summary>
        public int bw, bh;

        public void Execute(int blockIndex)
        {
            int by = blockIndex / bw;
            int bx = blockIndex % bw;
            // 块中心（岛局部像素坐标，1px 采样偏移保证覆盖）/ block center in island-local px
            float cx = bx * 4f + 2f;
            float cy = by * 4f + 2f;

            for (int t = 0; t < triCount; t++)
            {
                var a = triVerts[t * 3];
                var b = triVerts[t * 3 + 1];
                var c = triVerts[t * 3 + 2];
                if (PointInTri(cx, cy, a, b, c))
                {
                    int word = blockIndex >> 6;
                    int bit = blockIndex & 63;
                    mask[word] |= 1UL << bit;
                    return;
                }
            }
        }

        private static bool PointInTri(float px, float py, float2 a, float2 b, float2 c)
        {
            float d1 = Sign(px, py, a, b);
            float d2 = Sign(px, py, b, c);
            float d3 = Sign(px, py, c, a);
            bool neg = (d1 < 0) || (d2 < 0) || (d3 < 0);
            bool pos = (d1 > 0) || (d2 > 0) || (d3 > 0);
            return !(neg && pos);
        }

        private static float Sign(float px, float py, float2 a, float2 b)
        {
            return (px - b.x) * (a.y - b.y) - (a.x - b.x) * (py - b.y);
        }
    }

    /// <summary>
    /// 位掩码光栅化器 / Bitmask rasterizer.
    /// </summary>
    public static class BitmaskRasterizer
    {
        /// <summary>粒度（像素）/ granularity (px)</summary>
        public const int Granularity = 4;

        /// <summary>
        /// 光栅化一个岛为位掩码 / Rasterize one island into a bitmask.
        /// </summary>
        /// <param name="uvs">网格 UV（全部顶点）/ mesh UVs (all vertices)</param>
        /// <param name="triangles">岛三角形（全局索引）/ island triangles (global indices)</param>
        /// <param name="uvMin">岛 UV 包围盒 / island UV bbox</param>
        /// <param name="uvMax">岛 UV 包围盒 / island UV bbox</param>
        /// <param name="finalW">最终像素宽 / final pixel width</param>
        /// <param name="finalH">最终像素高 / final pixel height</param>
        /// <returns>位掩码（word 数组）、块宽高 / bitmask words, block dims</returns>
        public static (ulong[] words, int bw, int bh) Rasterize(List<Vector2> uvs, int[] meshTriangles,
            List<int> islandTriangles, Vector2 uvMin, Vector2 uvMax, int finalW, int finalH)
        {
            int bw = Mathf.Max(1, (int)Mathf.Ceil(finalW / (float)Granularity));
            int bh = Mathf.Max(1, (int)Mathf.Ceil(finalH / (float)Granularity));
            int wordCount = ((bw * bh) + 63) / 64;
            var words = new ulong[wordCount];

            float uw = Mathf.Max(1e-6f, uvMax.x - uvMin.x);
            float uh = Mathf.Max(1e-6f, uvMax.y - uvMin.y);

            var triVerts = new NativeArray<float2>(islandTriangles.Count * 3, Allocator.TempJob);
            try
            {
                // 岛三角形索引 → 网格三角形 → 顶点索引 → UV /
                // island triangle indices → mesh triangles → vertex indices → UVs
                for (int i = 0; i < islandTriangles.Count; i++)
                {
                    int t = islandTriangles[i];
                    for (int k = 0; k < 3; k++)
                    {
                        int vi = meshTriangles[t * 3 + k];
                        var uv = uvs[vi];
                        float lx = (uv.x - uvMin.x) / uw * finalW;
                        float ly = (uv.y - uvMin.y) / uh * finalH;
                        triVerts[i * 3 + k] = new float2(lx, ly);
                    }
                }

                var mask = new NativeArray<ulong>(wordCount, Allocator.TempJob);
                try
                {
                    var job = new RasterizeIslandJob
                    {
                        triVerts = triVerts,
                        triCount = islandTriangles.Count,
                        mask = mask,
                        bw = bw,
                        bh = bh,
                    };
                    job.Schedule(bw * bh, 64).Complete();

                    mask.CopyTo(words);
                }
                finally
                {
                    mask.Dispose();
                }
            }
            finally
            {
                triVerts.Dispose();
            }
            return (words, bw, bh);
        }

        /// <summary>
        /// 位掩码面积（置位数量）/ bitmask area (popcount).
        /// </summary>
        public static long Area(ulong[] words)
        {
            long area = 0;
            foreach (var w in words)
            {
                ulong v = w;
                while (v != 0) { area += (long)(v & 1); v >>= 1; }
            }
            return area;
        }

        /// <summary>
        /// 90° 旋转位掩码（转置+翻转 → 顺时针90°）/
        /// Rotate bitmask 90° clockwise (transpose + flip).
        /// </summary>
        public static ulong[] Rotate90(ulong[] words, int bw, int bh)
        {
            // 旋转后: 新尺寸 = bh × bw / rotated dims = bh × bw
            int nbw = bh, nbh = bw;
            var rotated = new ulong[((nbw * nbh) + 63) / 64];
            for (int y = 0; y < bh; y++)
            {
                for (int x = 0; x < bw; x++)
                {
                    if (GetBit(words, x, y, bw))
                    {
                        // 顺时针: (x,y) → (ny, nbx-1-x) 其中 nbx=bh / clockwise: (x,y) → (y, bh-1-x) in new coords
                        int nx = y;
                        int ny = nbh - 1 - x;
                        SetBit(rotated, nx, ny, nbw);
                    }
                }
            }
            return rotated;
        }

        private static bool GetBit(ulong[] words, int x, int y, int bw)
        {
            int idx = y * bw + x;
            return (words[idx >> 6] & (1UL << (idx & 63))) != 0;
        }

        private static void SetBit(ulong[] words, int x, int y, int bw)
        {
            int idx = y * bw + x;
            words[idx >> 6] |= 1UL << (idx & 63);
        }
    }
}
