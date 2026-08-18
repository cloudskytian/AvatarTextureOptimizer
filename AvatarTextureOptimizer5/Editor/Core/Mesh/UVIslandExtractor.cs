// Copyright (c) fosa. Licensed under the MIT License.
// Extracts connected UV islands, normalises out-of-range UVs and merges overlapping islands.
// 提取连通 UV 岛，归一化越界 UV，并合并重叠岛。

using System;
using System.Collections.Generic;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Outcome of attempting to normalise a mesh's UVs into the [0,1] range.
    /// 尝试将网格 UV 归一化到 [0,1] 范围的结果。
    /// </summary>
    public enum UVNormalizationResult
    {
        /// <summary>Already inside [0,1]. / 已经位于 [0,1] 内。</summary>
        AlreadyInRange,

        /// <summary>Shifted into range by an integer translation. / 通过整数平移移入范围内。</summary>
        Normalized,

        /// <summary>Crosses a wrap seam and cannot be handled safely. / 跨越 wrap 缝，无法安全处理。</summary>
        CrossesSeam,
    }

    /// <summary>
    /// Builds UV islands from mesh topology. Two triangles belong to the same island when they
    /// share an edge whose UV coordinates match on both sides; a UV discontinuity therefore
    /// splits islands exactly where the texture atlas must also split them.
    /// 依据网格拓扑构建 UV 岛。当两个三角形共享的边在两侧 UV 坐标一致时属于同一岛；
    /// 因此 UV 不连续处正好是图集也必须切分之处。
    /// </summary>
    public static class UVIslandExtractor
    {
        /// <summary>
        /// Extracts islands for one UV channel of a mesh.
        /// 提取网格某个 UV 通道的岛。
        /// </summary>
        /// <param name="triangles">Triangle index buffer. / 三角形索引缓冲。</param>
        /// <param name="uvs">UV coordinates per vertex. / 每顶点的 UV 坐标。</param>
        /// <param name="log">Optional logger. / 可选日志器。</param>
        public static List<UVIsland> Extract(int[] triangles, Vector2[] uvs, ATOLogger log = null)
        {
            var islands = new List<UVIsland>();
            if (triangles == null || uvs == null || triangles.Length < 3) return islands;

            var triCount = triangles.Length / 3;

            // Union-find over triangles. Triangles are merged when they share a UV-continuous
            // edge, which is the standard definition of a UV shell.
            // 对三角形做并查集。共享 UV 连续边的三角形被合并，这是 UV 壳的标准定义。
            var parent = new int[triCount];
            for (var i = 0; i < triCount; i++) parent[i] = i;

            // Map each directed UV-space edge to a triangle so we can find neighbours in O(n).
            // 将每条有向 UV 空间边映射到三角形，从而以 O(n) 找到邻接关系。
            var edgeMap = new Dictionary<UVEdgeKey, int>(triCount * 3);

            for (var t = 0; t < triCount; t++)
            {
                var i0 = triangles[t * 3];
                var i1 = triangles[t * 3 + 1];
                var i2 = triangles[t * 3 + 2];

                if (i0 >= uvs.Length || i1 >= uvs.Length || i2 >= uvs.Length) continue;

                TryLinkEdge(edgeMap, parent, uvs[i0], uvs[i1], t);
                TryLinkEdge(edgeMap, parent, uvs[i1], uvs[i2], t);
                TryLinkEdge(edgeMap, parent, uvs[i2], uvs[i0], t);
            }

            // Gather triangles by root.
            // 按根节点收集三角形。
            var byRoot = new Dictionary<int, UVIsland>();
            for (var t = 0; t < triCount; t++)
            {
                var root = Find(parent, t);
                if (!byRoot.TryGetValue(root, out var island))
                {
                    island = new UVIsland();
                    byRoot[root] = island;
                }

                island.Triangles.Add(t);
            }

            // Compute bounds and vertex sets.
            // 计算包围盒与顶点集合。
            foreach (var kv in byRoot)
            {
                var island = kv.Value;
                var verts = new HashSet<int>();
                var min = new Vector2(float.MaxValue, float.MaxValue);
                var max = new Vector2(float.MinValue, float.MinValue);

                foreach (var t in island.Triangles)
                {
                    for (var k = 0; k < 3; k++)
                    {
                        var vi = triangles[t * 3 + k];
                        if (vi >= uvs.Length) continue;
                        verts.Add(vi);
                        var uv = uvs[vi];
                        min = Vector2.Min(min, uv);
                        max = Vector2.Max(max, uv);
                    }
                }

                island.Vertices.AddRange(verts);
                island.UVBounds = new Rect(min.x, min.y, max.x - min.x, max.y - min.y);
                island.Index = islands.Count;
                islands.Add(island);
            }

            log?.Detail($"Extracted {islands.Count} UV islands from {triCount} triangles");
            return islands;
        }

        private static void TryLinkEdge(
            Dictionary<UVEdgeKey, int> edgeMap, int[] parent, Vector2 a, Vector2 b, int tri)
        {
            var key = new UVEdgeKey(a, b);
            if (edgeMap.TryGetValue(key, out var other))
            {
                Union(parent, tri, other);
            }
            else
            {
                edgeMap[key] = tri;
            }
        }

        private static int Find(int[] parent, int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }

            return x;
        }

        private static void Union(int[] parent, int a, int b)
        {
            var ra = Find(parent, a);
            var rb = Find(parent, b);
            if (ra != rb) parent[rb] = ra;
        }

        /// <summary>
        /// An undirected UV-space edge, quantised so that floating point noise does not split
        /// islands that are visually continuous.
        /// 无向 UV 空间边，经过量化以避免浮点噪声将视觉上连续的岛切开。
        /// </summary>
        private readonly struct UVEdgeKey : IEquatable<UVEdgeKey>
        {
            private readonly long _a;
            private readonly long _b;

            public UVEdgeKey(Vector2 p, Vector2 q)
            {
                var ka = Quantize(p);
                var kb = Quantize(q);

                // Order-independent so both winding directions hash the same.
                // 与顺序无关，使两种绕序哈希一致。
                if (ka <= kb)
                {
                    _a = ka;
                    _b = kb;
                }
                else
                {
                    _a = kb;
                    _b = ka;
                }
            }

            private static long Quantize(Vector2 v)
            {
                // 1e-6 UV units: far below one texel of an 8192px texture (1.2e-4).
                // 1e-6 UV 单位：远小于 8192px 贴图的一个 texel（1.2e-4）。
                var x = (long)Mathf.Round(v.x * 1000000f);
                var y = (long)Mathf.Round(v.y * 1000000f);
                return (x << 32) ^ (y & 0xFFFFFFFFL);
            }

            public bool Equals(UVEdgeKey other) => _a == other._a && _b == other._b;

            public override bool Equals(object obj) => obj is UVEdgeKey o && Equals(o);

            public override int GetHashCode() => (_a.GetHashCode() * 397) ^ _b.GetHashCode();
        }

        /// <summary>
        /// Attempts to translate an island into [0,1] by an integer offset. Islands that span a
        /// wrap boundary rely on repeat sampling and cannot be atlased, so they are rejected.
        /// 尝试以整数偏移将岛平移进 [0,1]。跨越 wrap 边界的岛依赖 repeat 采样、无法图集化，因此被拒绝。
        /// </summary>
        public static UVNormalizationResult TryNormalize(UVIsland island, out Vector2Int offset)
        {
            offset = Vector2Int.zero;
            var b = island.UVBounds;

            // Allow a small epsilon so UVs sitting exactly on 0 or 1 are not misclassified.
            // 留出小量容差，避免恰好位于 0 或 1 的 UV 被误判。
            const float eps = 1e-5f;

            if (b.xMin >= -eps && b.xMax <= 1f + eps &&
                b.yMin >= -eps && b.yMax <= 1f + eps)
            {
                return UVNormalizationResult.AlreadyInRange;
            }

            // An island wider than one full tile inherently crosses a seam.
            // 宽度超过一整个 tile 的岛必然跨缝。
            if (b.width > 1f + eps || b.height > 1f + eps)
            {
                return UVNormalizationResult.CrossesSeam;
            }

            var ox = -Mathf.FloorToInt(b.xMin + eps);
            var oy = -Mathf.FloorToInt(b.yMin + eps);

            var nxMin = b.xMin + ox;
            var nxMax = b.xMax + ox;
            var nyMin = b.yMin + oy;
            var nyMax = b.yMax + oy;

            if (nxMin < -eps || nxMax > 1f + eps || nyMin < -eps || nyMax > 1f + eps)
            {
                // Still outside after the shift, so the island straddles a tile boundary.
                // 平移后仍在范围外，说明该岛横跨 tile 边界。
                return UVNormalizationResult.CrossesSeam;
            }

            offset = new Vector2Int(ox, oy);
            return UVNormalizationResult.Normalized;
        }

        /// <summary>
        /// Merges islands whose UV bounds overlap within the same texture. Overlapping islands
        /// intentionally share texels, so they must be packed as a single unit or the shared
        /// pixels would be duplicated inconsistently.
        /// 合并同一贴图内 UV 包围盒重叠的岛。重叠岛有意共享 texel，
        /// 必须作为单一单元装箱，否则共享像素会被不一致地复制。
        /// </summary>
        public static List<UVIsland> MergeOverlapping(List<UVIsland> islands, ATOLogger log = null)
        {
            if (islands == null || islands.Count <= 1) return islands;

            var parent = new int[islands.Count];
            for (var i = 0; i < parent.Length; i++) parent[i] = i;

            for (var i = 0; i < islands.Count; i++)
            {
                for (var j = i + 1; j < islands.Count; j++)
                {
                    if (islands[i].UVBounds.Overlaps(islands[j].UVBounds))
                    {
                        Union(parent, i, j);
                    }
                }
            }

            var groups = new Dictionary<int, UVIsland>();
            var seeded = new HashSet<int>();
            for (var i = 0; i < islands.Count; i++)
            {
                var root = Find(parent, i);
                if (!groups.TryGetValue(root, out var merged))
                {
                    merged = new UVIsland { Index = groups.Count };
                    groups[root] = merged;
                }

                merged.Triangles.AddRange(islands[i].Triangles);
                merged.Vertices.AddRange(islands[i].Vertices);

                // Seed the bounds from the first island in the group, then grow by union.
                // 用组内第一个岛初始化包围盒，之后逐个并集扩展。
                if (seeded.Add(root))
                {
                    merged.UVBounds = islands[i].UVBounds;
                }
                else
                {
                    merged.UVBounds = Union(merged.UVBounds, islands[i].UVBounds);
                }
            }

            var result = new List<UVIsland>(groups.Values);
            for (var i = 0; i < result.Count; i++) result[i].Index = i;

            if (log != null && result.Count != islands.Count)
            {
                log.Detail($"Merged {islands.Count} islands into {result.Count} (overlap merge)");
            }

            return result;
        }

        /// <summary>Returns the smallest rect containing both inputs. / 返回同时包含两个输入的最小矩形。</summary>
        public static Rect Union(Rect a, Rect b)
        {
            var xMin = Mathf.Min(a.xMin, b.xMin);
            var yMin = Mathf.Min(a.yMin, b.yMin);
            var xMax = Mathf.Max(a.xMax, b.xMax);
            var yMax = Mathf.Max(a.yMax, b.yMax);
            return new Rect(xMin, yMin, xMax - xMin, yMax - yMin);
        }
    }
}
