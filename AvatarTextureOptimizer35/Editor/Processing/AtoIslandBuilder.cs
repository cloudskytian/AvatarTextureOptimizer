using System;
using System.Collections.Generic;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor
{
    /// <summary>
    /// Island extraction: union-find over triangles (shared edges), UV bbox, wrap normalization,
    /// overlap merging, blend-shape area factors, world-space sizes. /
    /// 岛提取：三角形并查集（共享边）、UV 包围盒、wrap 归一化、重叠合并、形态键面积系数、世界空间尺寸。
    /// </summary>
    internal static class AtoIslandBuilder
    {
        /// <summary>
        /// Build all islands of a mesh UV channel. / 构建网格 UV 通道的全部岛。
        /// </summary>
        /// <param name="uvs">UV array of the channel. / 该通道的 UV 数组。</param>
        /// <param name="triangles">Mesh triangle indices. / 网格三角形索引。</param>
        public static List<AtoIsland> Build(AtoUvGroup uvGroup, List<Vector2> uvs, int[] triangles)
        {
            var triCount = triangles.Length / 3;
            var parent = new int[triCount];
            for (var i = 0; i < triCount; i++) parent[i] = i;

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
                var ra = Find(a);
                var rb = Find(b);
                if (ra != rb) parent[ra] = rb;
            }

            // Shared-edge union-find: same vertex indices imply identical UVs. / 共享边并查集：相同顶点索引即相同 UV。
            var edgeMap = new Dictionary<(int, int), int>();
            for (var t = 0; t < triCount; t++)
            {
                for (var e = 0; e < 3; e++)
                {
                    var va = triangles[t * 3 + e];
                    var vb = triangles[t * 3 + (e + 1) % 3];
                    var key = va < vb ? (va, vb) : (vb, va);
                    if (edgeMap.TryGetValue(key, out var other))
                    {
                        Union(t, other);
                    }
                    else
                    {
                        edgeMap[key] = t;
                    }
                }
            }

            // Collect triangles per root. / 按根收集三角形。
            var islandTriangles = new Dictionary<int, List<int>>();
            for (var t = 0; t < triCount; t++)
            {
                var root = Find(t);
                if (!islandTriangles.TryGetValue(root, out var list))
                {
                    islandTriangles[root] = list = new List<int>();
                }
                list.Add(t);
            }

            var islands = new List<AtoIsland>();
            var index = 0;
            foreach (var list in islandTriangles.Values)
            {
                var island = new AtoIsland
                {
                    UvGroup = uvGroup,
                    Index = index++,
                    UvMin = new Vector2(float.MaxValue, float.MaxValue),
                    UvMax = new Vector2(float.MinValue, float.MinValue),
                };
                foreach (var t in list)
                {
                    for (var e = 0; e < 3; e++)
                    {
                        var v = triangles[t * 3 + e];
                        var uv = uvs[v];
                        island.UvMin = Vector2.Min(island.UvMin, uv);
                        island.UvMax = Vector2.Max(island.UvMax, uv);
                        island.Triangles.Add(v);
                    }
                }
                islands.Add(island);
            }
            return islands;
        }

        /// <summary>
        /// Compute the integer translation that moves the island's bbox into [0,1]², or null if
        /// the island spans more than one tile (repeat dependency → whitelist). /
        /// 计算把岛包围盒移入 [0,1]² 的整数平移；若岛跨 tile（依赖 repeat）返回 null（白名单）。
        /// </summary>
        public static Vector2Int? GetNormalizingTranslation(AtoIsland island)
        {
            var size = island.UvMax - island.UvMin;
            var translation = Vector2Int.zero;
            if (size.x > 1f + 1e-4f || size.y > 1f + 1e-4f) return null;

            var result = Vector2Int.zero;
            if (island.UvMin.x < 0f || island.UvMax.x > 1f)
            {
                result.x = -Mathf.FloorToInt(island.UvMin.x);
            }
            if (island.UvMin.y < 0f || island.UvMax.y > 1f)
            {
                result.y = -Mathf.FloorToInt(island.UvMin.y);
            }
            return result;
        }

        /// <summary>
        /// Compute the blend-shape area factor for the island: max of frame 0 and frame 100 areas,
        /// divided by the neutral area. Only frames 0 and 100 are considered (no combinations,
        /// no negatives, nothing beyond 100). / 计算岛的形态键面积系数：0 帧与 100 帧面积取最大值，
        /// 除以中性面积。仅考虑 0 与 100 两帧（不考虑组合、负数、超过 100）。
        /// </summary>
        public static float ComputeBlendShapeFactor(Mesh mesh, AtoIsland island, Vector3[] vertices)
        {
            if (mesh == null || mesh.blendShapeCount == 0) return 1f;

            var neutralArea = ComputeArea(vertices, island);
            if (neutralArea <= 1e-8f) return 1f;

            var maxFactor = 1f;
            var deltas = new Vector3[vertices.Length];
            var deltaNormals = new Vector3[vertices.Length];
            var deltaTangents = new Vector3[vertices.Length];

            for (var b = 0; b < mesh.blendShapeCount; b++)
            {
                // Find the frame with weight 100 (or the last frame as approximation). /
                // 找到权重 100 的帧（或最后帧作为近似）。
                var frameCount = mesh.GetBlendShapeFrameCount(b);
                var frameIndex = frameCount - 1;
                for (var f = 0; f < frameCount; f++)
                {
                    if (Mathf.Approximately(mesh.GetBlendShapeFrameWeight(b, f), 100f))
                    {
                        frameIndex = f;
                        break;
                    }
                }
                mesh.GetBlendShapeFrameVertices(b, frameIndex, deltas, deltaNormals, deltaTangents);
                var area = ComputeArea(vertices, island, deltas);
                var factor = Mathf.Max(area, neutralArea) / neutralArea;
                maxFactor = Mathf.Max(maxFactor, factor);
            }
            return maxFactor;
        }

        /// <summary>
        /// World-space size of the island (conservative: object bbox transformed × animated scale). /
        /// 岛的世界空间尺寸（保守：物体包围盒变换 × 动画缩放）。
        /// </summary>
        public static Vector2 ComputeWorldSize(Transform transform, AtoIsland island, Vector3[] vertices,
            Vector3 maxAnimatedScale)
        {
            var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            foreach (var vertexIndex in island.Triangles)
            {
                var p = vertices[vertexIndex];
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }

            // Conservative: transform all 8 bbox corners (handles rotation & non-uniform scale). /
            // 保守：变换包围盒 8 角点（处理旋转与非均匀缩放）。
            var worldMin = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var worldMax = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            var matrix = transform.localToWorldMatrix;
            for (var i = 0; i < 8; i++)
            {
                var corner = new Vector3(
                    (i & 1) == 0 ? min.x : max.x,
                    (i & 2) == 0 ? min.y : max.y,
                    (i & 4) == 0 ? min.z : max.z);
                var w = matrix.MultiplyPoint3x4(corner);
                worldMin = Vector3.Min(worldMin, w);
                worldMax = Vector3.Max(worldMax, w);
            }

            var size = worldMax - worldMin;
            return new Vector2(
                Mathf.Max(size.x, 1e-4f) * maxAnimatedScale.x,
                Mathf.Max(size.y, 1e-4f) * maxAnimatedScale.y);
        }

        /// <summary>
        /// Compute the triangle area sum (object space) for an island, optionally with blend-shape
        /// deltas applied. / 计算岛的三角形面积和（物体空间），可选应用形态键增量。
        /// </summary>
        private static float ComputeArea(Vector3[] vertices, AtoIsland island, Vector3[] deltas = null)
        {
            var area = 0f;
            for (var t = 0; t < island.Triangles.Count; t += 3)
            {
                var a = vertices[island.Triangles[t]];
                var b = vertices[island.Triangles[t + 1]];
                var c = vertices[island.Triangles[t + 2]];
                if (deltas != null)
                {
                    a += deltas[island.Triangles[t]];
                    b += deltas[island.Triangles[t + 1]];
                    c += deltas[island.Triangles[t + 2]];
                }
                area += Vector3.Cross(b - a, c - a).magnitude * 0.5f;
            }
            return area;
        }
    }
}
