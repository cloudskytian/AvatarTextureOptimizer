using System;
using System.Collections.Generic;
using NetFosa.AvatarTextureOptimizer.Editor.Analysis;
using NetFosa.AvatarTextureOptimizer.Editor.Logging;
using UnityEngine;

namespace NetFosa.AvatarTextureOptimizer.Editor.UV
{
    /// <summary>
    /// UV 岛提取器：按 UV 空间邻接（三角形共享 UV 顶点）并查集合并出连通岛；
    /// 处理越界平移归一（不跨 wrap 缝）、同组重叠岛合并。
    /// 多通道 UV：每个 UV 通道独立调用。
    /// </summary>
    public static class UvIslandExtractor
    {
        private const float UvEpsilon = 1e-5f;

        public static List<UvIsland> Extract(UvGroup group, ATOLogger logger)
        {
            var mesh = group.mesh;
            var channel = group.uvChannel;
            var slot = group.slotIndex;

            var uvs = GetUvArray(mesh, channel);
            if (uvs == null || uvs.Length == 0)
            {
                group.failed = true;
                group.failReason = $"mesh '{mesh.name}' has no UV channel {channel}";
                return new List<UvIsland>();
            }

            var triangles = mesh.GetTriangles(slot);
            if (triangles == null || triangles.Length == 0) return new List<UvIsland>();

            int triCount = triangles.Length / 3;
            var parent = new int[triCount];
            for (int i = 0; i < triCount; i++) parent[i] = i;

            // 建立边 → 三角形映射（量化 UV 坐标）
            var edgeMap = new Dictionary<(long, long, long, long), int>();
            // 量化：fixed-point 1e5
            for (int t = 0; t < triCount; t++)
            {
                int i0 = triangles[t * 3];
                int i1 = triangles[t * 3 + 1];
                int i2 = triangles[t * 3 + 2];
                AddEdge(t, i0, i1, uvs, edgeMap, parent);
                AddEdge(t, i1, i2, uvs, edgeMap, parent);
                AddEdge(t, i2, i0, uvs, edgeMap, parent);
            }

            // 收集连通分量
            var islandOfTri = new Dictionary<int, UvIsland>();
            var islands = new List<UvIsland>();
            var bounds = new Rect[triCount];
            for (int t = 0; t < triCount; t++)
            {
                int i0 = triangles[t * 3];
                int i1 = triangles[t * 3 + 1];
                int i2 = triangles[t * 3 + 2];
                var b = new Rect(
                    Mathf.Min(uvs[i0].x, Mathf.Min(uvs[i1].x, uvs[i2].x)),
                    Mathf.Min(uvs[i0].y, Mathf.Min(uvs[i1].y, uvs[i2].y)),
                    0f, 0f);
                b.width = Mathf.Max(uvs[i0].x, Mathf.Max(uvs[i1].x, uvs[i2].x)) - b.x;
                b.height = Mathf.Max(uvs[i0].y, Mathf.Max(uvs[i1].y, uvs[i2].y)) - b.y;
                bounds[t] = b;

                int root = Find(parent, t);
                if (!islandOfTri.TryGetValue(root, out var island))
                {
                    island = new UvIsland
                    {
                        id = islands.Count,
                        group = group,
                        uvBounds = bounds[t],
                        needsNormalize = false,
                    };
                    islandOfTri[root] = island;
                    islands.Add(island);
                }
                else
                {
                    island.uvBounds = Union(island.uvBounds, bounds[t]);
                }
                island.triangleIndices.Add(t * 3);
                island.triangleIndices.Add(t * 3 + 1);
                island.triangleIndices.Add(t * 3 + 2);
            }

            // 越界归一化检测（不跨缝才允许平移）
            foreach (var island in islands)
            {
                bool ok = true;
                float minU = float.MaxValue, minV = float.MaxValue;
                foreach (var idx in island.triangleIndices)
                {
                    var uv = uvs[triangles[idx]];
                    minU = Mathf.Min(minU, uv.x);
                    minV = Mathf.Min(minV, uv.y);
                }
                float offsetU = Mathf.Floor(minU);
                float offsetV = Mathf.Floor(minV);
                foreach (var idx in island.triangleIndices)
                {
                    var uv = uvs[triangles[idx]];
                    float nu = uv.x - offsetU;
                    float nv = uv.y - offsetV;
                    if (nu < -UvEpsilon || nu > 1f + UvEpsilon || nv < -UvEpsilon || nv > 1f + UvEpsilon)
                    {
                        ok = false;
                        break;
                    }
                }
                if (!ok)
                {
                    // 跨 wrap 缝 → 白名单跳过并警告
                    group.failed = true;
                    group.failReason = $"island #{island.id} UVs cross a wrap seam (relies on repeat sampling); treated as whitelist";
                    logger.Warn(group.failReason);
                    continue;
                }
                if (offsetU != 0f || offsetV != 0f)
                {
                    island.needsNormalize = true;
                    island.normalizedOffset = new Vector2(offsetU, offsetV);
                    island.uvBounds = new Rect(island.uvBounds.x - offsetU, island.uvBounds.y - offsetV,
                        island.uvBounds.width, island.uvBounds.height);
                }
            }

            // 同组内重叠岛合并（AABB 相交即合并；覆盖镜像 UV 等场景）
            islands = MergeOverlapping(islands);

            // 合并后重新分配全局唯一 id（质量评估的区域缓存以 (texture, islandId) 为键）
            for (int i = 0; i < islands.Count; i++) islands[i].id = i;

            return islands;
        }

        private static void AddEdge(int tri, int va, int vb, Vector2[] uvs,
            Dictionary<(long, long, long, long), int> edgeMap, int[] parent)
        {
            var a = uvs[va];
            var b = uvs[vb];
            long qa1 = Mathf.RoundToInt(a.x / UvEpsilon);
            long qa2 = Mathf.RoundToInt(a.y / UvEpsilon);
            long qb1 = Mathf.RoundToInt(b.x / UvEpsilon);
            long qb2 = Mathf.RoundToInt(b.y / UvEpsilon);
            var key = NormalizeKey(qa1, qa2, qb1, qb2);

            if (edgeMap.TryGetValue(key, out int other))
            {
                Union(parent, tri, other);
            }
            else
            {
                edgeMap[key] = tri;
            }
        }

        private static (long, long, long, long) NormalizeKey(long a1, long a2, long b1, long b2)
        {
            // 无向边：排序保证 (a)<(b)
            if (a1 < b1 || (a1 == b1 && a2 < b2)) return (a1, a2, b1, b2);
            return (b1, b2, a1, a2);
        }

        private static int Find(int[] parent, int i)
        {
            while (parent[i] != i)
            {
                parent[i] = parent[parent[i]];
                i = parent[i];
            }
            return i;
        }

        private static void Union(int[] parent, int a, int b)
        {
            int ra = Find(parent, a);
            int rb = Find(parent, b);
            if (ra != rb) parent[ra] = rb;
        }

        private static Rect Union(Rect a, Rect b)
        {
            float x = Mathf.Min(a.x, b.x);
            float y = Mathf.Min(a.y, b.y);
            float xMax = Mathf.Max(a.xMax, b.xMax);
            float yMax = Mathf.Max(a.yMax, b.yMax);
            return new Rect(x, y, xMax - x, yMax - y);
        }

        private static List<UvIsland> MergeOverlapping(List<UvIsland> islands)
        {
            if (islands.Count <= 1) return islands;

            // O(n²) 对每组足够；n 通常不大
            var merged = new List<UvIsland>();
            var consumed = new bool[islands.Count];
            for (int i = 0; i < islands.Count; i++)
            {
                if (consumed[i]) continue;
                var acc = islands[i];
                for (int j = i + 1; j < islands.Count; j++)
                {
                    if (consumed[j]) continue;
                    var b = islands[j];
                    if (acc.uvBounds.Overlaps(b.uvBounds, true))
                    {
                        // 合并
                        acc.uvBounds = Union(acc.uvBounds, b.uvBounds);
                        acc.triangleIndices.AddRange(b.triangleIndices);
                        consumed[j] = true;
                    }
                }
                merged.Add(acc);
            }
            return merged;
        }

        public static Vector2[] GetUvArray(Mesh mesh, int channel)
        {
            switch (channel)
            {
                case 0: return mesh.uv;
                case 1: return mesh.uv2;
                case 2: return mesh.uv3;
                case 3: return mesh.uv4;
                case 4: return mesh.uv5;
                case 5: return mesh.uv6;
                case 6: return mesh.uv7;
                case 7: return mesh.uv8;
                default: return null;
            }
        }
    }
}
