// AvatarTextureOptimizer - MeshUvJobs
// EN: Burst jobs for UV island extraction: triangle rasterization, connected components, areas.
// CN: UV 岛提取的 Burst 作业：三角形光栅化、连通域、面积计算。
using System;
using System.Threading;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace net.fosa.avatar_texture_optimizer
{
    /// <summary>
    /// EN: Rasterizes triangle interiors into a bit grid (one bit per cell). Bias seals shared edges / T-junctions.
    /// CN: 将三角形内部光栅化到位网格（一格一位）。偏差用于密封共享边/T 型接缝。
    /// </summary>
    [BurstCompile]
    internal struct RasterizeUvJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float2> uvs;       // 顶点 UV（frac 空间）
        [ReadOnly] public NativeArray<int3> triangles;   // 三角形顶点索引
        [ReadOnly] public NativeArray<float2> triMin;    // 每三角形 uv 最小
        [ReadOnly] public NativeArray<float2> triMax;    // 每三角形 uv 最大
        public int grid;                                  // 网格边长
        public NativeArray<ulong> bits;                   // grid*grid/64

        public void Execute(int i)
        {
            int3 t = triangles[i];
            float2 a = uvs[t.x], b = uvs[t.y], c = uvs[t.z];
            float2 mn = triMin[i], mx = triMax[i];

            int x0 = Math.Max(0, (int)Math.Floor(mn.x * grid) - 1);
            int x1 = Math.Min(grid - 1, (int)Math.Ceiling(mx.x * grid) + 1);
            int y0 = Math.Max(0, (int)Math.Floor(mn.y * grid) - 1);
            int y1 = Math.Min(grid - 1, (int)Math.Ceiling(mx.y * grid) + 1);

            float twiceArea = Math.Abs((b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x));
            if (twiceArea < 1e-12f) return; // degenerate

            // EN: Bias of ~0.5 cell in edge-function units. Both windings accepted.
            // CN: 边函数单位下约半格偏差。两种绕向都接受。
            float bias = 1.0f / grid;
            for (int y = y0; y <= y1; y++)
            {
                float py = (y + 0.5f) / grid;
                for (int x = x0; x <= x1; x++)
                {
                    float px = (x + 0.5f) / grid;
                    float e1 = (b.x - a.x) * (py - a.y) - (b.y - a.y) * (px - a.x);
                    float e2 = (c.x - b.x) * (py - b.y) - (c.y - b.y) * (px - b.x);
                    float e3 = (a.x - c.x) * (py - c.y) - (a.y - c.y) * (px - c.x);
                    bool inside = (e1 >= -bias && e2 >= -bias && e3 >= -bias) ||
                                  (e1 <= bias && e2 <= bias && e3 <= bias);
                    if (!inside) continue;
                    int idx = y * grid + x;
                    bits[idx >> 6] |= 1UL << (idx & 63);
                }
            }
        }
    }

    /// <summary>
    /// EN: Scanline flood fill producing a component label per filled cell (labels are 1-based; 0 = empty).
    /// CN: 扫描线洪泛填充，为每个被填充的格子生成连通域标签（1 起；0 = 空）。
    /// </summary>
    [BurstCompile]
    internal struct FloodFillJob : IJob
    {
        [ReadOnly] public NativeArray<ulong> bits;
        public int grid;
        public NativeArray<int> labels;   // grid*grid, 0 = empty

        public void Execute()
        {
            var stack = new NativeList<int2>(1024, Allocator.Temp);
            int comp = 1;
            for (int y = 0; y < grid; y++)
            {
                int row = y * grid;
                for (int x = 0; x < grid; x++)
                {
                    int idx = row + x;
                    if (labels[idx] != 0) continue;
                    if ((bits[idx >> 6] & (1UL << (idx & 63))) == 0) continue;
                    stack.Clear();
                    stack.Add(new int2(x, y));
                    labels[idx] = comp;
                    int head = 0;
                    while (head < stack.Length)
                    {
                        int2 p = stack[head++];
                        for (int dy = -1; dy <= 1; dy++)
                        {
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                if (dx == 0 && dy == 0) continue;
                                int nx = p.x + dx, ny = p.y + dy;
                                if (nx < 0 || ny < 0 || nx >= grid || ny >= grid) continue;
                                int nidx = ny * grid + nx;
                                if (labels[nidx] != 0) continue;
                                if ((bits[nidx >> 6] & (1UL << (nidx & 63))) == 0) continue;
                                labels[nidx] = comp;
                                stack.Add(new int2(nx, ny));
                            }
                        }
                    }
                    comp++;
                }
            }
            stack.Dispose();
        }
    }

    /// <summary>
    /// EN: Triangle world-space areas; with optional blend-shape deltas returns the max of (base, deformed).
    /// CN: 三角形世界面积；带形态键增量时返回（基础、变形）中的最大值。
    /// </summary>
    [BurstCompile]
    internal struct TriangleAreaMaxJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float3> positions;
        [ReadOnly] public NativeArray<int3> triangles;
        [ReadOnly] public NativeArray<float3> deltaA;
        [ReadOnly] public NativeArray<float3> deltaB;
        [ReadOnly] public NativeArray<float3> deltaC;
        public NativeArray<float> areas;

        public void Execute(int i)
        {
            int3 t = triangles[i];
            float3 a = positions[t.x], b = positions[t.y], c = positions[t.z];
            float maxArea = TriArea(a, b, c);
            // EN: Note: an empty NativeArray still reports IsCreated==true — guard by length.
            // CN: 注意：空 NativeArray 的 IsCreated 仍为 true——用长度判断。
            if (deltaA.Length > 0)
            {
                float area = TriArea(a + deltaA[i], b + deltaB[i], c + deltaC[i]);
                if (area > maxArea) maxArea = area;
            }
            areas[i] = maxArea;
        }

        private static float TriArea(float3 a, float3 b, float3 c)
        {
            float3 cr = math.cross(b - a, c - a);
            return 0.5f * math.length(cr);
        }
    }

    /// <summary>
    /// EN: Assigns each triangle to a component via its centroid cell; accumulates island bounds & area per component.
    /// CN: 经质心格子把三角形归属到连通域；按连通域累计岛的包围盒与面积。
    /// </summary>
    [BurstCompile]
    internal struct CollectIslandStatsJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float2> uvs;
        [ReadOnly] public NativeArray<int3> triangles;
        [ReadOnly] public NativeArray<int> labels;
        [ReadOnly] public NativeArray<float2> triMin;
        [ReadOnly] public NativeArray<float2> triMax;
        [ReadOnly] public NativeArray<float> triArea;
        public int grid;
        public int baseLabel;                 // 已填充组件数 + 1（独立组件从此起）
        public NativeArray<int> nextLabel;    // 独立组件原子计数器（单元素）
        public NativeArray<float2> islandMin;
        public NativeArray<float2> islandMax;
        public NativeArray<float> islandArea;
        public NativeArray<int> islandTriCount;
        public NativeArray<int> triComponent;

        public void Execute(int tri)
        {
            int3 t = triangles[tri];
            float2 c = (uvs[t.x] + uvs[t.y] + uvs[t.z]) * (1f / 3f);
            int cx = (int)math.clamp(c.x * grid, 0, grid - 1);
            int cy = (int)math.clamp(c.y * grid, 0, grid - 1);
            int label = labels[cy * grid + cx];
            if (label == 0)
            {
                // EN: Tiny triangles may fall between cells: give them a fresh standalone component.
                // CN: 极小三角形可能落在格子间隙：分配独立组件。
                label = Interlocked.Increment(ref nextLabel[0]) + baseLabel - 1;
            }
            triComponent[tri] = label;
            float2 mn = islandMin[label];
            float2 mx = islandMax[label];
            islandMin[label] = new float2(math.min(mn.x, triMin[tri].x), math.min(mn.y, triMin[tri].y));
            islandMax[label] = new float2(math.max(mx.x, triMax[tri].x), math.max(mx.y, triMax[tri].y));
            islandArea[label] += triArea[tri];
            islandTriCount[label] += 1;
        }
    }
}
