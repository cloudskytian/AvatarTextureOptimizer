// SPDX-License-Identifier: MIT
// EN: Builds UV islands in texture space, merging overlapping regions automatically.
// ZH: 在贴图空间中构建 UV 岛，并自动合并重叠区域。

using System;
using System.Collections.Generic;
using Net.Fosa.AvatarTextureOptimizer.Editor.Core;
using Net.Fosa.AvatarTextureOptimizer.Editor.Model;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor.Meshes
{
    /// <summary>
    /// EN: One triangle of one sub mesh, expressed in the group's reference texture space.
    /// ZH: 某个子网格的一个三角形，以该组的参考贴图空间表示。
    /// </summary>
    public struct SourceTriangle
    {
        /// <summary>EN: The UV slot this triangle came from. ZH: 该三角形来自的 UV 槽。</summary>
        public UvSlot Slot;
        /// <summary>EN: Triangle index inside the sub mesh. ZH: 在子网格内的三角形索引。</summary>
        public int TriangleIndex;
        /// <summary>EN: UV coordinates in [0,1] texture space. ZH: [0,1] 贴图空间中的 UV 坐标。</summary>
        public Vector2 UvA, UvB, UvC;
        /// <summary>EN: World space area in square meters at the largest configuration. ZH: 最大形态下的世界空间面积（平方米）。</summary>
        public float WorldArea;
        /// <summary>EN: Assigned island index, filled in by the builder. ZH: 由构建器填充的所属岛索引。</summary>
        public int IslandIndex;
    }

    /// <summary>
    /// EN: Extracts islands by conservatively rasterizing every triangle into a 4 texel grid and running
    ///     an 8-connected flood fill. Overlapping islands merge naturally because they share cells, which
    ///     is exactly the behaviour required for textures shared by several meshes.
    /// ZH: 将每个三角形保守光栅化到 4 像素网格并执行八连通洪水填充来提取岛。
    ///     重叠的岛因为共享单元而自然合并——这正是多个网格共用一张贴图时所需的行为。
    /// </summary>
    public static class UvIslandBuilder
    {
        private const string Stage = "Islands";
        /// <summary>EN: Rasterization granularity in texels, as specified. ZH: 规格规定的光栅化粒度（像素）。</summary>
        public const int CellSize = 4;

        /// <summary>
        /// EN: Builds the island set for a group.
        /// ZH: 为一个组构建岛集合。
        /// </summary>
        /// <param name="triangles">EN: All triangles feeding the group. Modified in place to record island indices. ZH: 供给该组的所有三角形。会就地修改以记录岛索引。</param>
        /// <param name="referenceSize">EN: Texture space resolution the UVs are measured against. ZH: 用于衡量 UV 的贴图空间分辨率。</param>
        /// <returns>EN: The islands, ordered by descending covered area. ZH: 按覆盖面积降序排列的岛。</returns>
        public static List<UvIsland> Build(SourceTriangle[] triangles, Vector2Int referenceSize)
        {
            int gw = Mathf.Max(1, Mathf.CeilToInt(referenceSize.x / (float)CellSize));
            int gh = Mathf.Max(1, Mathf.CeilToInt(referenceSize.y / (float)CellSize));

            var coverage = new NativeArray<byte>(gw * gh, Allocator.TempJob, NativeArrayOptions.ClearMemory);
            var rasterTris = new NativeArray<RasterTriangle>(triangles.Length, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            try
            {
                float sx = referenceSize.x / (float)CellSize;
                float sy = referenceSize.y / (float)CellSize;
                for (int i = 0; i < triangles.Length; i++)
                {
                    var t = triangles[i];
                    rasterTris[i] = new RasterTriangle
                    {
                        A = new float2(t.UvA.x * sx, t.UvA.y * sy),
                        B = new float2(t.UvB.x * sx, t.UvB.y * sy),
                        C = new float2(t.UvC.x * sx, t.UvC.y * sy),
                    };
                }

                var job = new ConservativeRasterJob
                {
                    Triangles = rasterTris,
                    GridWidth = gw,
                    GridHeight = gh,
                    Coverage = coverage,
                };
                job.Schedule(triangles.Length, 64).Complete();

                var labels = FloodFill(coverage, gw, gh, out int islandCount);
                var islands = BuildIslands(labels, coverage, gw, gh, islandCount, referenceSize);

                // EN: Assign each triangle to the island containing its centroid cell; the raster
                //     coverage of a single triangle is connected, so this is unambiguous.
                // ZH: 将每个三角形分配给包含其重心单元的岛；单个三角形的光栅覆盖是连通的，
                //     因此这一分配没有歧义。
                for (int i = 0; i < triangles.Length; i++)
                {
                    var rt = rasterTris[i];
                    var c = (rt.A + rt.B + rt.C) / 3f;
                    int cx = Mathf.Clamp((int)c.x, 0, gw - 1);
                    int cy = Mathf.Clamp((int)c.y, 0, gh - 1);
                    int label = labels[cy * gw + cx];
                    if (label < 0)
                    {
                        // EN: Rare: the centroid cell was not covered (very thin sliver). Search the
                        //     bounding box for any covered cell instead.
                        // ZH: 罕见情况：重心单元未被覆盖（极细长的三角形）。改为在包围盒中查找任一被覆盖单元。
                        label = FindAnyLabel(labels, gw, gh, rt);
                    }
                    triangles[i].IslandIndex = label;
                    if (label >= 0) islands[label].WorldAreaM2 += triangles[i].WorldArea;
                }

                labels.Dispose();
                AtoLog.Debug_(Stage, $"{triangles.Length} triangles -> {islands.Count} islands on a {gw}x{gh} cell grid");
                return islands;
            }
            finally
            {
                coverage.Dispose();
                rasterTris.Dispose();
            }
        }

        private static int FindAnyLabel(NativeArray<int> labels, int gw, int gh, RasterTriangle t)
        {
            int x0 = Mathf.Clamp((int)math.floor(math.min(t.A.x, math.min(t.B.x, t.C.x))), 0, gw - 1);
            int x1 = Mathf.Clamp((int)math.ceil(math.max(t.A.x, math.max(t.B.x, t.C.x))), 0, gw - 1);
            int y0 = Mathf.Clamp((int)math.floor(math.min(t.A.y, math.min(t.B.y, t.C.y))), 0, gh - 1);
            int y1 = Mathf.Clamp((int)math.ceil(math.max(t.A.y, math.max(t.B.y, t.C.y))), 0, gh - 1);
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                    if (labels[y * gw + x] >= 0) return labels[y * gw + x];
            return -1;
        }

        /// <summary>
        /// EN: Iterative 8-connected flood fill. Iterative on purpose: recursion would blow the stack on
        ///     an 8K texture.
        /// ZH: 迭代式八连通洪水填充。刻意使用迭代：在 8K 贴图上递归会爆栈。
        /// </summary>
        private static NativeArray<int> FloodFill(NativeArray<byte> coverage, int gw, int gh, out int islandCount)
        {
            var labels = new NativeArray<int>(gw * gh, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            for (int i = 0; i < labels.Length; i++) labels[i] = -1;

            var stack = new Stack<int>(1024);
            int next = 0;
            for (int start = 0; start < labels.Length; start++)
            {
                if (coverage[start] == 0 || labels[start] >= 0) continue;
                int label = next++;
                labels[start] = label;
                stack.Push(start);

                while (stack.Count > 0)
                {
                    int idx = stack.Pop();
                    int x = idx % gw, y = idx / gw;
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        int ny = y + dy;
                        if (ny < 0 || ny >= gh) continue;
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = x + dx;
                            if (nx < 0 || nx >= gw) continue;
                            int nidx = ny * gw + nx;
                            if (coverage[nidx] == 0 || labels[nidx] >= 0) continue;
                            labels[nidx] = label;
                            stack.Push(nidx);
                        }
                    }
                }
            }

            islandCount = next;
            return labels;
        }

        private static List<UvIsland> BuildIslands(NativeArray<int> labels, NativeArray<byte> coverage,
            int gw, int gh, int count, Vector2Int referenceSize)
        {
            var minX = new int[count]; var minY = new int[count];
            var maxX = new int[count]; var maxY = new int[count];
            var cells = new int[count];
            for (int i = 0; i < count; i++) { minX[i] = int.MaxValue; minY[i] = int.MaxValue; maxX[i] = -1; maxY[i] = -1; }

            for (int y = 0; y < gh; y++)
            {
                for (int x = 0; x < gw; x++)
                {
                    int l = labels[y * gw + x];
                    if (l < 0) continue;
                    if (x < minX[l]) minX[l] = x;
                    if (y < minY[l]) minY[l] = y;
                    if (x > maxX[l]) maxX[l] = x;
                    if (y > maxY[l]) maxY[l] = y;
                    cells[l]++;
                }
            }

            var islands = new List<UvIsland>(count);
            for (int l = 0; l < count; l++)
            {
                int cw = maxX[l] - minX[l] + 1;
                int ch = maxY[l] - minY[l] + 1;
                var island = new UvIsland
                {
                    Index = l,
                    MaskWidth = cw,
                    MaskHeight = ch,
                    Mask = new bool[cw * ch],
                    CoveredCells = cells[l],
                    Bounds = new RectInt(
                        minX[l] * CellSize, minY[l] * CellSize,
                        Mathf.Min(cw * CellSize, referenceSize.x - minX[l] * CellSize),
                        Mathf.Min(ch * CellSize, referenceSize.y - minY[l] * CellSize)),
                };
                islands.Add(island);
            }

            for (int y = 0; y < gh; y++)
            {
                for (int x = 0; x < gw; x++)
                {
                    int l = labels[y * gw + x];
                    if (l < 0) continue;
                    var isl = islands[l];
                    isl.Mask[(y - minY[l]) * isl.MaskWidth + (x - minX[l])] = true;
                }
            }

            return islands;
        }
    }
}
