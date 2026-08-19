// SPDX-License-Identifier: MIT
// AvatarTextureOptimizer (ATO) - UV island extraction, normalisation and rasterisation.
// AvatarTextureOptimizer (ATO) - UV 岛提取、归一化与光栅化。

using System;
using System.Collections.Generic;
using Net.Fosa.AvatarTextureOptimizer.Editor.Core;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor.MeshOps
{
    /// <summary>
    /// EN: A connected set of triangles in UV space that will be moved/scaled as one unit.
    /// ZH: UV 空间中的一组连通三角形，作为一个整体被移动/缩放。
    /// </summary>
    public sealed class UVIsland
    {
        /// <summary>EN: Indices into the owning <see cref="UVIslandSet.Triangles"/> array. ZH: 指向所属 <see cref="UVIslandSet.Triangles"/> 的索引。</summary>
        public List<int> TriangleIds = new List<int>();

        /// <summary>EN: UV-space bounds AFTER integer normalisation. ZH: 整数归一化之后的 UV 空间包围盒。</summary>
        public float2 Min, Max;

        /// <summary>EN: Integer tile offset that was subtracted during normalisation. ZH: 归一化时减去的整数瓦片偏移。</summary>
        public int2 TileOffset;

        /// <summary>EN: Bounding-box size in source-texture pixels. ZH: 包围盒在源贴图上的像素尺寸。</summary>
        public int PixelWidth, PixelHeight;

        /// <summary>EN: Sum of world-space triangle areas (max over blendshape / scale variants).
        ///     ZH: 世界空间三角形面积之和（在形态键 / 缩放变体中取最大）。</summary>
        public float WorldArea;

        /// <summary>EN: Sum of UV-space triangle areas. ZH: UV 空间三角形面积之和。</summary>
        public float UvArea;

        /// <summary>
        /// EN: The island's footprint in the atlas, in pixels. This is a property of the *UV group*, not of
        ///     any single texture: every texture that shares this UV must occupy exactly the same slot so
        ///     that one set of rewritten UVs is valid for all of its parallel atlases.
        /// ZH: 岛在图集中的占位（像素）。它属于 *UV 组* 而不属于任何单张贴图：
        ///     共享该 UV 的每张贴图都必须占据完全相同的位置，
        ///     这样一份重写后的 UV 才能同时对它的所有平行图集有效。
        /// </summary>
        public int TargetWidth, TargetHeight;

        /// <summary>EN: Placement in the final atlas, in pixels. ZH: 在最终图集中的像素位置。</summary>
        public int2 AtlasOrigin;

        /// <summary>EN: True if the island was rotated 90 degrees during packing. ZH: 装箱时是否旋转了 90 度。</summary>
        public bool Rotated;

        /// <summary>EN: True once the island has been placed in an atlas family. ZH: 岛已被放入某个图集族时为 true。</summary>
        public bool Placed;

        /// <summary>EN: 4px-granularity coverage mask, row-major, width = ceil(w/4). ZH: 4 像素粒度覆盖掩码，行主序，宽 = ceil(w/4)。</summary>
        public RasterMask Mask;

        public int ScaledWidth => Mathf.Max(1, TargetWidth > 0 ? TargetWidth : PixelWidth);
        public int ScaledHeight => Mathf.Max(1, TargetHeight > 0 ? TargetHeight : PixelHeight);
    }

    /// <summary>
    /// EN: A bit-packed coverage mask at 4 pixel granularity. Rows are stored as ulong words so the
    ///     bottom-left-fill scan can test 64 cells (=256 px) per instruction.
    /// ZH: 4 像素粒度的位压缩覆盖掩码。每行以 ulong 存储，使 BLF 扫描一次指令即可测试 64 个格子（=256 像素）。
    /// </summary>
    public struct RasterMask : IDisposable
    {
        public int CellsX, CellsY;
        public int WordsPerRow;
        public NativeArray<ulong> Bits;

        public bool IsCreated => Bits.IsCreated;

        public static RasterMask Create(int cellsX, int cellsY, Allocator allocator)
        {
            var m = new RasterMask
            {
                CellsX = math.max(1, cellsX),
                CellsY = math.max(1, cellsY),
            };
            m.WordsPerRow = (m.CellsX + 63) / 64;
            m.Bits = new NativeArray<ulong>(m.WordsPerRow * m.CellsY, allocator, NativeArrayOptions.ClearMemory);
            return m;
        }

        public bool Get(int x, int y)
        {
            if ((uint)x >= (uint)CellsX || (uint)y >= (uint)CellsY) return false;
            return (Bits[y * WordsPerRow + (x >> 6)] & (1UL << (x & 63))) != 0;
        }

        public void Set(int x, int y)
        {
            if ((uint)x >= (uint)CellsX || (uint)y >= (uint)CellsY) return;
            int i = y * WordsPerRow + (x >> 6);
            Bits[i] = Bits[i] | (1UL << (x & 63));
        }

        /// <summary>EN: Number of set cells. ZH: 已置位的格子数。</summary>
        public int Popcount()
        {
            int total = 0;
            for (int i = 0; i < Bits.Length; i++) total += math.countbits(Bits[i]);
            return total;
        }

        /// <summary>EN: Transposed copy, i.e. the island rotated by 90 degrees. ZH: 转置副本，即旋转 90 度后的岛。</summary>
        public RasterMask Transpose(Allocator allocator)
        {
            var r = Create(CellsY, CellsX, allocator);
            for (int y = 0; y < CellsY; y++)
            for (int x = 0; x < CellsX; x++)
                if (Get(x, y)) r.Set(y, x);
            return r;
        }

        /// <summary>EN: Dilate by <paramref name="cells"/> in all directions (used for padding).
        ///     ZH: 向各方向膨胀 <paramref name="cells"/> 格（用于 padding）。</summary>
        public RasterMask Dilate(int cells, Allocator allocator)
        {
            if (cells <= 0)
            {
                var copy = Create(CellsX, CellsY, allocator);
                copy.Bits.CopyFrom(Bits);
                return copy;
            }

            var r = Create(CellsX + cells * 2, CellsY + cells * 2, allocator);
            for (int y = 0; y < CellsY; y++)
            for (int x = 0; x < CellsX; x++)
            {
                if (!Get(x, y)) continue;
                for (int dy = -cells; dy <= cells; dy++)
                for (int dx = -cells; dx <= cells; dx++)
                    r.Set(x + cells + dx, y + cells + dy);
            }
            return r;
        }

        public void Dispose()
        {
            if (Bits.IsCreated) Bits.Dispose();
        }
    }

    /// <summary>
    /// EN: The islands of one (mesh, submesh-group, uv-channel, texture-size) combination.
    /// ZH: 某个 (网格, 子网格组, UV 通道, 贴图尺寸) 组合下的全部 UV 岛。
    /// </summary>
    public sealed class UVIslandSet : IDisposable
    {
        /// <summary>EN: Flattened triangle list: 3 vertex indices per triangle. ZH: 扁平三角形列表，每个三角形 3 个顶点索引。</summary>
        public int[] Triangles;

        /// <summary>EN: UVs indexed by mesh vertex index. ZH: 按网格顶点索引排列的 UV。</summary>
        public Vector2[] Uv;

        public List<UVIsland> Islands = new List<UVIsland>();

        /// <summary>EN: Set when any island crosses a wrap seam and therefore cannot be handled.
        ///     ZH: 存在跨 wrap 缝的岛（因而无法处理）时置位。</summary>
        public bool HasCrossSeamIsland;

        public void Dispose()
        {
            foreach (var i in Islands)
            {
                if (i.Mask.IsCreated) i.Mask.Dispose();
            }
            Islands.Clear();
        }
    }

    public static class UVIslandBuilder
    {
        /// <summary>
        /// EN: Quantisation used to weld UV vertices that are "the same point". 1/65536 of UV space is far
        ///     below any meaningful texel on an 8K texture, so this never welds distinct points.
        /// ZH: 用于把“同一个点”的 UV 顶点焊接在一起的量化精度。1/65536 的 UV 空间远小于 8K 贴图上的一个纹素，
        ///     因此绝不会误焊接不同的点。
        /// </summary>
        private const float WeldQuant = 65536f;

        /// <summary>
        /// EN: Build islands for a set of triangles.
        /// ZH: 为一组三角形构建 UV 岛。
        /// </summary>
        /// <param name="triangles">EN: flattened triangle indices. ZH: 扁平三角形索引。</param>
        /// <param name="uv">EN: UV array indexed by vertex. ZH: 按顶点索引的 UV 数组。</param>
        /// <param name="texWidth">EN: source texture width. ZH: 源贴图宽度。</param>
        /// <param name="texHeight">EN: source texture height. ZH: 源贴图高度。</param>
        public static UVIslandSet Build(int[] triangles, Vector2[] uv, int texWidth, int texHeight)
        {
            var set = new UVIslandSet { Triangles = triangles, Uv = uv };
            int triCount = triangles.Length / 3;
            if (triCount == 0) return set;

            // ---- 1. Weld UV vertices and union-find over triangles / 焊接 UV 顶点并对三角形做并查集 ----
            var vertexKeyToRoot = new Dictionary<long, int>(triCount * 3);
            var parent = new int[triCount];
            for (int i = 0; i < triCount; i++) parent[i] = i;

            int Find(int x)
            {
                while (parent[x] != x)
                {
                    parent[x] = parent[parent[x]];
                    x = parent[x];
                }
                return x;
            }

            void Union(int a, int b)
            {
                a = Find(a); b = Find(b);
                if (a != b) parent[b] = a;
            }

            for (int t = 0; t < triCount; t++)
            {
                for (int k = 0; k < 3; k++)
                {
                    var p = uv[triangles[t * 3 + k]];
                    long key = QuantKey(p);
                    if (vertexKeyToRoot.TryGetValue(key, out var other)) Union(other, t);
                    else vertexKeyToRoot[key] = t;
                }
            }

            // ---- 2. Group triangles by root / 按并查集根分组 ----
            var groups = new Dictionary<int, UVIsland>();
            for (int t = 0; t < triCount; t++)
            {
                int root = Find(t);
                if (!groups.TryGetValue(root, out var island))
                {
                    island = new UVIsland
                    {
                        Min = new float2(float.MaxValue, float.MaxValue),
                        Max = new float2(float.MinValue, float.MinValue),
                    };
                    groups[root] = island;
                }
                island.TriangleIds.Add(t);

                for (int k = 0; k < 3; k++)
                {
                    var p = uv[triangles[t * 3 + k]];
                    island.Min = math.min(island.Min, new float2(p.x, p.y));
                    island.Max = math.max(island.Max, new float2(p.x, p.y));
                }
            }

            // ---- 3. Normalise out-of-range islands / 归一化越界岛 ----
            foreach (var island in groups.Values)
            {
                if (!TryNormalise(island, out var offset))
                {
                    set.HasCrossSeamIsland = true;
                    continue;
                }
                island.TileOffset = offset;
                island.Min -= (float2)offset;
                island.Max -= (float2)offset;

                island.PixelWidth = Mathf.Max(1, Mathf.CeilToInt((island.Max.x - island.Min.x) * texWidth));
                island.PixelHeight = Mathf.Max(1, Mathf.CeilToInt((island.Max.y - island.Min.y) * texHeight));
                island.UvArea = ComputeUvArea(island, triangles, uv);

                set.Islands.Add(island);
            }

            ATOLog.Trace($"island build: {triCount} tris -> {set.Islands.Count} islands" +
                         (set.HasCrossSeamIsland ? " (cross-seam detected)" : ""));
            return set;
        }

        private static long QuantKey(Vector2 p)
        {
            long x = (long)math.round(p.x * WeldQuant);
            long y = (long)math.round(p.y * WeldQuant);
            return (x << 32) ^ (y & 0xFFFFFFFFL);
        }

        /// <summary>
        /// EN: An island can be normalised iff it lies entirely inside a single integer UV tile; then a pure
        ///     integer translation brings it into [0,1] without changing repeat-sampling results.
        /// ZH: 当且仅当岛完全位于同一个整数 UV 瓦片内时可以归一化；
        ///     此时一个纯整数平移即可把它移入 [0,1]，且不改变 repeat 采样结果。
        /// </summary>
        private static bool TryNormalise(UVIsland island, out int2 offset)
        {
            const float eps = 1e-5f;
            int ox = Mathf.FloorToInt(island.Min.x + eps);
            int oy = Mathf.FloorToInt(island.Min.y + eps);
            offset = new int2(ox, oy);

            // EN: max must stay within the same tile (allowing an exact touch of the upper edge).
            // ZH: 最大值必须仍在同一瓦片内（允许恰好贴到上边界）。
            if (island.Max.x - ox > 1f + eps) return false;
            if (island.Max.y - oy > 1f + eps) return false;
            if (island.Min.x - ox < -eps) return false;
            if (island.Min.y - oy < -eps) return false;
            return true;
        }

        private static float ComputeUvArea(UVIsland island, int[] tris, Vector2[] uv)
        {
            float area = 0f;
            foreach (var t in island.TriangleIds)
            {
                var a = uv[tris[t * 3 + 0]];
                var b = uv[tris[t * 3 + 1]];
                var c = uv[tris[t * 3 + 2]];
                area += Mathf.Abs((b.x - a.x) * (c.y - a.y) - (c.x - a.x) * (b.y - a.y)) * 0.5f;
            }
            return area;
        }

        // ---- Rasterisation / 光栅化 --------------------------------------------------------------

        /// <summary>
        /// EN: Rasterise an island's triangles into a 4px-granularity coverage mask, in the island's own
        ///     local (scaled) pixel space. This is the mask the packer uses; the same masks are cached and
        ///     reused for the final blit, so no triangle is rasterised twice.
        /// ZH: 将岛的三角形光栅化为 4 像素粒度的覆盖掩码，坐标位于岛自身的（已缩放）局部像素空间。
        ///     装箱器使用该掩码；同一批掩码会被缓存并在最终 blit 时复用，因此没有三角形会被光栅化两次。
        /// </summary>
        public static RasterMask Rasterise(UVIsland island, int[] tris, Vector2[] uv,
            int texWidth, int texHeight, int cellSize = 4)
        {
            int w = island.ScaledWidth, h = island.ScaledHeight;
            int cellsX = (w + cellSize - 1) / cellSize;
            int cellsY = (h + cellSize - 1) / cellSize;
            var mask = RasterMask.Create(cellsX, cellsY, Allocator.Persistent);

            // EN: Map island-local UV space directly onto the target footprint.
            // ZH: 把岛的局部 UV 空间直接映射到目标占位上。
            float spanX = Mathf.Max(1e-8f, island.Max.x - island.Min.x);
            float spanY = Mathf.Max(1e-8f, island.Max.y - island.Min.y);
            float sx = w / spanX / cellSize;
            float sy = h / spanY / cellSize;

            var verts = new NativeArray<float2>(island.TriangleIds.Count * 3, Allocator.TempJob,
                NativeArrayOptions.UninitializedMemory);
            for (int i = 0; i < island.TriangleIds.Count; i++)
            {
                int t = island.TriangleIds[i];
                for (int k = 0; k < 3; k++)
                {
                    var p = uv[tris[t * 3 + k]];
                    var local = new float2(p.x - island.TileOffset.x - island.Min.x,
                        p.y - island.TileOffset.y - island.Min.y);
                    verts[i * 3 + k] = new float2(local.x * sx, local.y * sy);
                    // EN: texWidth/texHeight are unused here on purpose: the footprint already encodes them.
                    // ZH: 此处刻意不使用 texWidth/texHeight：目标占位中已经包含了它们的信息。
                }
            }

            var job = new RasteriseJob
            {
                Verts = verts,
                CellsX = mask.CellsX,
                CellsY = mask.CellsY,
                WordsPerRow = mask.WordsPerRow,
                Bits = mask.Bits,
            };
            job.Run();
            verts.Dispose();
            return mask;
        }

        /// <summary>
        /// EN: Conservative triangle rasterisation: a cell is covered if the triangle touches it at all.
        ///     Conservative (rather than centre-sampled) coverage is required because the atlas blit must
        ///     never clip a partially-covered texel.
        /// ZH: 保守三角形光栅化：只要三角形接触到某个格子就算覆盖。
        ///     必须使用保守覆盖（而非中心采样），否则图集 blit 可能裁掉部分覆盖的纹素。
        /// </summary>
        [BurstCompile(FloatPrecision.Standard, FloatMode.Fast)]
        private struct RasteriseJob : IJob
        {
            [ReadOnly] public NativeArray<float2> Verts;
            public int CellsX, CellsY, WordsPerRow;
            [NativeDisableParallelForRestriction] public NativeArray<ulong> Bits;

            public void Execute()
            {
                int triCount = Verts.Length / 3;
                for (int t = 0; t < triCount; t++)
                {
                    var a = Verts[t * 3 + 0];
                    var b = Verts[t * 3 + 1];
                    var c = Verts[t * 3 + 2];

                    int minX = (int)math.floor(math.min(a.x, math.min(b.x, c.x)));
                    int maxX = (int)math.ceil(math.max(a.x, math.max(b.x, c.x)));
                    int minY = (int)math.floor(math.min(a.y, math.min(b.y, c.y)));
                    int maxY = (int)math.ceil(math.max(a.y, math.max(b.y, c.y)));

                    minX = math.max(minX, 0); minY = math.max(minY, 0);
                    maxX = math.min(maxX, CellsX - 1); maxY = math.min(maxY, CellsY - 1);

                    for (int y = minY; y <= maxY; y++)
                    for (int x = minX; x <= maxX; x++)
                    {
                        if (!CellOverlapsTriangle(x, y, a, b, c)) continue;
                        int i = y * WordsPerRow + (x >> 6);
                        Bits[i] = Bits[i] | (1UL << (x & 63));
                    }
                }
            }

            private static bool CellOverlapsTriangle(int cx, int cy, float2 a, float2 b, float2 c)
            {
                float2 lo = new float2(cx, cy);
                float2 hi = lo + new float2(1f, 1f);

                // EN: Separating-axis test against the three edges plus the two box axes.
                // ZH: 针对三条边加两条包围盒轴的分离轴测试。
                if (math.max(a.x, math.max(b.x, c.x)) < lo.x) return false;
                if (math.min(a.x, math.min(b.x, c.x)) > hi.x) return false;
                if (math.max(a.y, math.max(b.y, c.y)) < lo.y) return false;
                if (math.min(a.y, math.min(b.y, c.y)) > hi.y) return false;

                if (EdgeSeparates(a, b, c, lo, hi)) return false;
                if (EdgeSeparates(b, c, a, lo, hi)) return false;
                if (EdgeSeparates(c, a, b, lo, hi)) return false;
                return true;
            }

            private static bool EdgeSeparates(float2 p0, float2 p1, float2 other, float2 lo, float2 hi)
            {
                float2 n = new float2(-(p1.y - p0.y), p1.x - p0.x);
                float dOther = math.dot(n, other - p0);
                if (math.abs(dOther) < 1e-12f) return false;
                float sign = dOther > 0f ? -1f : 1f;

                // EN: If every box corner is strictly on the far side of the edge from the third vertex,
                //     the axis separates them.
                // ZH: 如果包围盒的四个角都严格落在与第三个顶点相反的一侧，则该轴是分离轴。
                for (int i = 0; i < 4; i++)
                {
                    float2 corner = new float2((i & 1) == 0 ? lo.x : hi.x, (i & 2) == 0 ? lo.y : hi.y);
                    if (math.dot(n, corner - p0) * sign < 0f) return false;
                }
                return true;
            }
        }

        /// <summary>
        /// EN: Merge islands whose 4px masks actually intersect. Overlapping islands must move together,
        ///     otherwise the shared texels would be duplicated inconsistently in the atlas.
        /// ZH: 合并 4 像素掩码真正相交的岛。重叠的岛必须一起移动，
        ///     否则共享的纹素会在图集中被不一致地复制。
        /// </summary>
        public static void MergeOverlapping(UVIslandSet set, int texWidth, int texHeight)
        {
            var list = set.Islands;
            int n = list.Count;
            if (n < 2) return;

            var parent = new int[n];
            for (int i = 0; i < n; i++) parent[i] = i;

            int Find(int x)
            {
                while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
                return x;
            }

            // EN: Only test pairs whose bounding boxes overlap - O(n^2) on boxes is cheap, raster tests are not.
            // ZH: 只测试包围盒重叠的对——盒子层面的 O(n^2) 很便宜，而光栅测试很贵。
            var masks = new RasterMask[n];
            for (int i = 0; i < n; i++)
            {
                for (int j = i + 1; j < n; j++)
                {
                    if (list[i].Max.x <= list[j].Min.x || list[j].Max.x <= list[i].Min.x) continue;
                    if (list[i].Max.y <= list[j].Min.y || list[j].Max.y <= list[i].Min.y) continue;

                    if (!masks[i].IsCreated) masks[i] = RasteriseAbsolute(list[i], set, texWidth, texHeight);
                    if (!masks[j].IsCreated) masks[j] = RasteriseAbsolute(list[j], set, texWidth, texHeight);

                    if (MasksIntersect(masks[i], masks[j])) parent[Find(j)] = Find(i);
                }
            }

            for (int i = 0; i < n; i++) if (masks[i].IsCreated) masks[i].Dispose();

            var merged = new Dictionary<int, UVIsland>();
            foreach (var idx in EnumerateRange(n))
            {
                int root = Find(idx);
                if (root == idx)
                {
                    merged[root] = list[idx];
                    continue;
                }
                var target = merged.TryGetValue(root, out var m) ? m : (merged[root] = list[root]);
                target.TriangleIds.AddRange(list[idx].TriangleIds);
                target.Min = math.min(target.Min, list[idx].Min);
                target.Max = math.max(target.Max, list[idx].Max);
                target.UvArea += list[idx].UvArea;
            }

            if (merged.Count != n)
            {
                ATOLog.Debug_($"merged {n - merged.Count} overlapping island(s)");
                set.Islands = new List<UVIsland>(merged.Values);
                foreach (var island in set.Islands)
                {
                    island.PixelWidth = Mathf.Max(1, Mathf.CeilToInt((island.Max.x - island.Min.x) * texWidth));
                    island.PixelHeight = Mathf.Max(1, Mathf.CeilToInt((island.Max.y - island.Min.y) * texHeight));
                }
            }
        }

        private static IEnumerable<int> EnumerateRange(int n)
        {
            for (int i = 0; i < n; i++) yield return i;
        }

        /// <summary>EN: Rasterise in whole-texture coordinates for overlap testing. ZH: 在整张贴图坐标下光栅化，用于重叠测试。</summary>
        private static RasterMask RasteriseAbsolute(UVIsland island, UVIslandSet set, int texW, int texH)
        {
            var saveMin = island.Min;
            var saveMax = island.Max;
            var saveTw = island.TargetWidth;
            var saveTh = island.TargetHeight;

            island.Min = new float2(0f, 0f);
            island.Max = new float2(1f, 1f);
            island.TargetWidth = texW;
            island.TargetHeight = texH;
            var m = Rasterise(island, set.Triangles, set.Uv, texW, texH);

            island.Min = saveMin;
            island.Max = saveMax;
            island.TargetWidth = saveTw;
            island.TargetHeight = saveTh;
            return m;
        }

        private static bool MasksIntersect(RasterMask a, RasterMask b)
        {
            int rows = math.min(a.CellsY, b.CellsY);
            int words = math.min(a.WordsPerRow, b.WordsPerRow);
            for (int y = 0; y < rows; y++)
            for (int w = 0; w < words; w++)
                if ((a.Bits[y * a.WordsPerRow + w] & b.Bits[y * b.WordsPerRow + w]) != 0) return true;
            return false;
        }
    }
}
