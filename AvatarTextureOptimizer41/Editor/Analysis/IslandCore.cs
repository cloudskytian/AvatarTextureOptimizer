using System;
using System.Collections.Generic;

// Pure C# UV-island extraction. NO Unity dependencies — compiles in Unity and in the dotnet test harness.
// 纯 C# UV 岛提取。不依赖 Unity —— 可在 Unity 与 dotnet 单测中编译。

namespace Net.Fosa.AvatarTextureOptimizer.Pure
{
    /// <summary>
    /// A UV island as extracted from mesh UVs.
    /// 从网格 UV 提取的 UV 岛。
    /// </summary>
    public sealed class Island
    {
        /// <summary>Triangle indices (into the caller's triangle list). 三角形索引（指向调用方的三角形列表）。</summary>
        public List<int> Triangles = new List<int>();
        public float MinU = float.MaxValue, MinV = float.MaxValue, MaxU = float.MinValue, MaxV = float.MinValue;
        /// <summary>True if the island crosses a wrap seam / cannot be normalized; treat as whitelist. 是否跨 wrap 缝/无法归一（视为白名单）。</summary>
        public bool CrossesWrap;
        /// <summary>Translation that maps this island into [0,1] (valid when !CrossesWrap). 将其归一到 [0,1] 的平移（!CrossesWrap 时有效）。</summary>
        public float TranslateU, TranslateV;

        public float WidthUV => MaxU - MinU;
        public float HeightUV => MaxV - MinV;
        public bool FitsUnitSquare => !CrossesWrap;
    }

    public static class IslandCore
    {
        /// <summary>
        /// UV weld tolerance: positions closer than this are considered the same texel location.
        /// UV 焊接容差：小于该距离视为同一纹素位置（≈1/4096 纹理）。
        /// </summary>
        public const float WeldEpsilon = 1f / 4096f;

        /// <summary>
        /// Extracts islands from mesh UVs (one UV channel).
        /// Triangles are connected when they share a vertex whose UVs weld to the same position.
        /// uv: 2 floats per vertex. tris: 3 indices per triangle.
        /// 从网格 UV 提取岛（单通道）。三角形共享焊接 UV 位置即连通。
        /// </summary>
        public static List<Island> Extract(float[] uv, int[] tris, int vertexCount)
        {
            int triCount = tris.Length / 3;
            var parent = new int[triCount];
            for (int i = 0; i < triCount; i++) parent[i] = i;

            // Union-find. 并查集。
            int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
            void Union(int a, int b) { int ra = Find(a), rb = Find(b); if (ra != rb) parent[rb] = ra; }

            // Map welded UV cell -> list of triangle indices incident to that cell.
            // 焊接 UV 单元 → 该单元相关的三角形索引列表。
            var cellToTris = new Dictionary<long, List<int>>();
            var vertToTri = new List<List<int>>();
            for (int v = 0; v < vertexCount; v++) vertToTri.Add(new List<int>());

            for (int t = 0; t < triCount; t++)
            {
                for (int k = 0; k < 3; k++)
                {
                    int vi = tris[t * 3 + k];
                    if (vi < 0 || vi >= vertexCount) continue;
                    long cell = Cell(uv[vi * 2], uv[vi * 2 + 1]);
                    if (!cellToTris.TryGetValue(cell, out var list)) { list = new List<int>(); cellToTris[cell] = list; }
                    list.Add(t);
                    vertToTri[vi].Add(t);
                }
            }

            // Every cell unions all its triangles; shared welded position => connected.
            // 每个单元联合其全部三角形：共享焊接位置即连通。
            foreach (var kv in cellToTris)
            {
                var list = kv.Value;
                for (int i = 1; i < list.Count; i++) Union(list[0], list[i]);
            }

            // Group triangles by root. 按根分组。
            var groups = new Dictionary<int, Island>();
            for (int t = 0; t < triCount; t++)
            {
                int r = Find(t);
                if (!groups.TryGetValue(r, out var island)) { island = new Island(); groups[r] = island; }
                island.Triangles.Add(t);
            }

            foreach (var island in groups.Values)
            {
                ComputeBoundsAndFlags(island, uv, tris, vertexCount);
            }
            return new List<Island>(groups.Values);
        }

        private static long Cell(float u, float v)
        {
            int x = (int)Math.Floor(u / WeldEpsilon);
            int y = (int)Math.Floor(v / WeldEpsilon);
            return ((long)x << 32) ^ (uint)y;
        }

        private static void ComputeBoundsAndFlags(Island island, float[] uv, int[] tris, int vertexCount)
        {
            island.MinU = float.MaxValue; island.MinV = float.MaxValue;
            island.MaxU = float.MinValue; island.MaxV = float.MinValue;
            bool wraps = false;
            foreach (int t in island.Triangles)
            {
                int i0 = tris[t * 3], i1 = tris[t * 3 + 1], i2 = tris[t * 3 + 2];
                if (i0 >= vertexCount || i1 >= vertexCount || i2 >= vertexCount) { wraps = true; continue; }
                float u0 = uv[i0 * 2], v0 = uv[i0 * 2 + 1];
                float u1 = uv[i1 * 2], v1 = uv[i1 * 2 + 1];
                float u2 = uv[i2 * 2], v2 = uv[i2 * 2 + 1];
                // Track bounds. 记录包围盒。
                island.MinU = Math.Min(island.MinU, Math.Min(u0, Math.Min(u1, u2)));
                island.MinV = Math.Min(island.MinV, Math.Min(v0, Math.Min(v1, v2)));
                island.MaxU = Math.Max(island.MaxU, Math.Max(u0, Math.Max(u1, u2)));
                island.MaxV = Math.Max(island.MaxV, Math.Max(v0, Math.Max(v1, v2)));
                // Detect wrap seam: any UV edge jumping > 0.5 in one axis means the triangle samples across the seam.
                // 检测跨缝：任一条 UV 边在某轴跳变 > 0.5 表示三角形跨缝采样。
                if (Math.Abs(u0 - u1) > 0.5f || Math.Abs(u1 - u2) > 0.5f || Math.Abs(u2 - u0) > 0.5f ||
                    Math.Abs(v0 - v1) > 0.5f || Math.Abs(v1 - v2) > 0.5f || Math.Abs(v2 - v0) > 0.5f)
                    wraps = true;
            }

            // Span > 1 in any axis cannot be translated into [0,1] without wrapping. 任轴跨度>1 无法平移归一。
            if (island.MaxU - island.MinU > 1f + 1e-4f || island.MaxV - island.MinV > 1f + 1e-4f)
                wraps = true;

            island.CrossesWrap = wraps;
            if (!wraps)
            {
                // Translate so the island sits in [0,1]. 平移使其落入 [0,1]。
                island.TranslateU = -island.MinU;
                island.TranslateV = -island.MinV;
            }
        }

        /// <summary>
        /// Merges islands whose UV AABBs overlap (conservative: may merge adjacent islands, which is safe).
        /// Returns a new list. 合并 UV 包围盒重叠的岛（保守策略：可能合并相邻岛，安全）。返回新列表。
        /// </summary>
        public static List<Island> MergeOverlapping(List<Island> islands, float toleranceUV = 1e-3f)
        {
            var result = new List<Island>();
            var merged = new bool[islands.Count];
            for (int i = 0; i < islands.Count; i++)
            {
                if (merged[i]) continue;
                var acc = islands[i];
                merged[i] = true;
                bool grew = true;
                while (grew)
                {
                    grew = false;
                    for (int j = 0; j < islands.Count; j++)
                    {
                        if (merged[j]) continue;
                        var o = islands[j];
                        if (AabbOverlap(acc, o, toleranceUV))
                        {
                            acc = MergeTwo(acc, o);
                            merged[j] = true;
                            grew = true;
                        }
                    }
                }
                result.Add(acc);
            }
            return result;
        }

        private static bool AabbOverlap(Island a, Island b, float tol)
            => a.MinU <= b.MaxU + tol && b.MinU <= a.MaxU + tol &&
               a.MinV <= b.MaxV + tol && b.MinV <= a.MaxV + tol;

        private static Island MergeTwo(Island a, Island b)
        {
            var m = new Island
            {
                MinU = Math.Min(a.MinU, b.MinU),
                MinV = Math.Min(a.MinV, b.MinV),
                MaxU = Math.Max(a.MaxU, b.MaxU),
                MaxV = Math.Max(a.MaxV, b.MaxV),
                CrossesWrap = a.CrossesWrap || b.CrossesWrap,
            };
            m.Triangles.AddRange(a.Triangles);
            m.Triangles.AddRange(b.Triangles);
            if (!m.CrossesWrap)
            {
                m.TranslateU = -m.MinU;
                m.TranslateV = -m.MinV;
            }
            return m;
        }

        /// <summary>
        /// UV-space area of an island (sum of triangle areas, absolute values). 岛的 UV 面积（三角形面积之和的绝对值）。
        /// </summary>
        public static float UVArea(Island island, float[] uv)
        {
            float area = 0;
            foreach (int t in island.Triangles)
            {
                int i0 = t * 3, i1 = t * 3 + 1, i2 = t * 3 + 2;
                if (i2 + 1 >= uv.Length / 2) continue;
                float ax = uv[i0 * 2], ay = uv[i0 * 2 + 1];
                float bx = uv[i1 * 2], by = uv[i1 * 2 + 1];
                float cx = uv[i2 * 2], cy = uv[i2 * 2 + 1];
                area += Math.Abs((bx - ax) * (cy - ay) - (by - ay) * (cx - ax)) * 0.5f;
            }
            return area;
        }
    }
}
