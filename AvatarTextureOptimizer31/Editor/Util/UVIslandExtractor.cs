// UVIslandExtractor.cs
// Extracts connected UV islands from a mesh's UV channel data.
// Uses a union-find structure for efficient island merging of overlapping islands.
// 从网格 UV 通道数据中提取连通 UV 岛，使用并查集合并重叠岛。
//
// Copyright (c) 2024 fosa. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Util
{
    /// <summary>
    /// Extracts UV islands from a mesh. An island is a maximal set of triangles
    /// whose UV coordinates overlap or are connected in UV space.
    /// 从网格中提取 UV 岛。
    /// </summary>
    internal static class UVIslandExtractor
    {
        internal struct ExtractedIsland
        {
            internal List<int> TriangleIndices;
            internal Rect UVBounds;        // in [0,1] UV space
            internal Rect PixelBounds;     // in texture pixel space
            internal int UVChannel;
            internal bool CrossesWrapSeam;
        }

        /// <summary>
        /// Extracts UV islands from a mesh for a given UV channel and material submesh.
        /// 从网格中提取指定 UV 通道和子网格的 UV 岛。
        /// </summary>
        internal static List<ExtractedIsland> Extract(Mesh mesh, int uvChannel, int submeshIndex,
            Vector2 textureSize)
        {
            var result = new List<ExtractedIsland>();
            if (mesh == null) return result;

            int texW = Mathf.Max(1, (int)textureSize.x);
            int texH = Mathf.Max(1, (int)textureSize.y);

            // Get UV data for the specified channel
            List<Vector2> uvs = new List<Vector2>();
            mesh.GetUVs(uvChannel, uvs);
            if (uvs.Count == 0)
            {
                // Try UV0 if the requested channel is empty
                if (uvChannel == 0)
                {
                    mesh.GetUVs(0, uvs);
                    if (uvs.Count == 0) return result;
                }
                else return result;
            }

            // Get the triangles for the specified submesh
            int[] triangles = GetSubmeshTriangles(mesh, submeshIndex);
            if (triangles == null || triangles.Length == 0) return result;

            // Build a spatial hash to find overlapping triangles in UV space
            // Then use union-find to merge connected triangles
            int triCount = triangles.Length / 3;

            // Union-find
            int[] parent = new int[triCount];
            for (int i = 0; i < triCount; i++) parent[i] = i;

            // Compute each triangle's UV bounding box
            var triBounds = new Rect[triCount];
            var triBoundsNormalized = new Rect[triCount];
            var crossesSeam = new bool[triCount];

            for (int t = 0; t < triCount; t++)
            {
                int i0 = triangles[t * 3];
                int i1 = triangles[t * 3 + 1];
                int i2 = triangles[t * 3 + 2];

                if (i0 >= uvs.Count || i1 >= uvs.Count || i2 >= uvs.Count) continue;

                var u0 = uvs[i0];
                var u1 = uvs[i1];
                var u2 = uvs[i2];

                float minU = Mathf.Min(u0.x, u1.x, u2.x);
                float maxU = Mathf.Max(u0.x, u1.x, u2.x);
                float minV = Mathf.Min(u0.y, u1.y, u2.y);
                float maxV = Mathf.Max(u0.y, u1.y, u2.y);

                // Check for wrap seam crossing
                crossesSeam[t] = (maxU - minU > 0.5f) || (maxV - minV > 0.5f);

                if (crossesSeam[t])
                {
                    // Triangle spans more than half the UV range → likely wraps around
                    triBounds[t] = new Rect(0, 0, 1, 1);
                    triBoundsNormalized[t] = new Rect(0, 0, 1, 1);
                }
                else
                {
                    triBounds[t] = Rect.MinMaxRect(minU, minV, maxU, maxV);
                    triBoundsNormalized[t] = Rect.MinMaxRect(minU, minV, maxU, maxV);
                }
            }

            // Spatial hash for overlap detection
            const float cellSize = 0.0625f; // 1/16 grid
            var spatialHash = new Dictionary<long, List<int>>();

            for (int t = 0; t < triCount; t++)
            {
                if (crossesSeam[t]) continue;

                var b = triBounds[t];
                int minCX = Mathf.FloorToInt(b.xMin / cellSize);
                int maxCX = Mathf.FloorToInt(b.xMax / cellSize);
                int minCY = Mathf.FloorToInt(b.yMin / cellSize);
                int maxCY = Mathf.FloorToInt(b.yMax / cellSize);

                for (int cx = minCX; cx <= maxCX; cx++)
                {
                    for (int cy = minCY; cy <= maxCY; cy++)
                    {
                        long key = (long)cx * 100000 + cy;
                        if (!spatialHash.TryGetValue(key, out var list))
                        {
                            list = new List<int>();
                            spatialHash[key] = list;
                        }
                        list.Add(t);
                    }
                }
            }

            // Find overlapping triangle pairs and union them
            foreach (var kvp in spatialHash)
            {
                var list = kvp.Value;
                for (int i = 0; i < list.Count; i++)
                {
                    for (int j = i + 1; j < list.Count; j++)
                    {
                        if (RectsOverlap(triBounds[list[i]], triBounds[list[j]]))
                        {
                            Union(parent, list[i], list[j]);
                        }
                    }
                }
            }

            // Group triangles by root
            var groups = new Dictionary<int, List<int>>();
            for (int t = 0; t < triCount; t++)
            {
                int root = Find(parent, t);
                if (!groups.TryGetValue(root, out var list))
                {
                    list = new List<int>();
                    groups[root] = list;
                }
                list.Add(t);
            }

            // Build islands
            foreach (var kvp in groups)
            {
                var triIndices = new List<int>();
                float minU = float.MaxValue, maxU = float.MinValue;
                float minV = float.MaxValue, maxV = float.MinValue;
                bool anyCrossesSeam = false;

                foreach (var t in kvp.Value)
                {
                    triIndices.Add(t * 3);
                    triIndices.Add(t * 3 + 1);
                    triIndices.Add(t * 3 + 2);

                    var b = triBounds[t];
                    if (!crossesSeam[t])
                    {
                        minU = Mathf.Min(minU, b.xMin);
                        maxU = Mathf.Max(maxU, b.xMax);
                        minV = Mathf.Min(minV, b.yMin);
                        maxV = Mathf.Max(maxV, b.yMax);
                    }
                    if (crossesSeam[t]) anyCrossesSeam = true;
                }

                if (minU == float.MaxValue)
                {
                    minU = 0; maxU = 1; minV = 0; maxV = 1;
                }

                // Clamp to [0,1]
                float clampedMinU = Mathf.Clamp01(minU);
                float clampedMaxU = Mathf.Clamp01(maxU);
                float clampedMinV = Mathf.Clamp01(minV);
                float clampedMaxV = Mathf.Clamp01(maxV);

                var uvBounds = Rect.MinMaxRect(clampedMinU, clampedMinV, clampedMaxU, clampedMaxV);

                // Compute pixel bounds
                int pxMin = Mathf.FloorToInt(uvBounds.xMin * texW);
                int pxMax = Mathf.CeilToInt(uvBounds.xMax * texW);
                int pyMin = Mathf.FloorToInt(uvBounds.yMin * texH);
                int pyMax = Mathf.CeilToInt(uvBounds.yMax * texH);
                var pixelBounds = Rect.MinMaxRect(pxMin, pyMin, pxMax, pyMax);

                result.Add(new ExtractedIsland
                {
                    TriangleIndices = triIndices,
                    UVBounds = uvBounds,
                    PixelBounds = pixelBounds,
                    UVChannel = uvChannel,
                    CrossesWrapSeam = anyCrossesSeam
                });
            }

            return result;
        }

        internal static int[] GetSubmeshTriangles(Mesh mesh, int submeshIndex)
        {
            if (submeshIndex < 0 || submeshIndex >= mesh.subMeshCount) return null;
            return mesh.GetTriangles(submeshIndex);
        }

        /// <summary>
        /// Checks if the UV coordinates can be normalized to [0,1] by a single translation
        /// (no wrap seam crossing). Returns the offset needed, or null if not possible.
        /// 检查 UV 是否可通过整体平移归一到 [0,1]。
        /// </summary>
        internal static Vector2? TryNormalizeUVBounds(List<Vector2> uvs)
        {
            if (uvs.Count == 0) return null;

            float minU = float.MaxValue, maxU = float.MinValue;
            float minV = float.MaxValue, maxV = float.MinValue;

            foreach (var uv in uvs)
            {
                minU = Mathf.Min(minU, uv.x);
                maxU = Mathf.Max(maxU, uv.x);
                minV = Mathf.Min(minV, uv.y);
                maxV = Mathf.Max(maxV, uv.y);
            }

            // If the range exceeds 1.0, it wraps around → not normalizable by translation alone
            if (maxU - minU > 1.0f || maxV - minV > 1.0f)
                return null;

            // Compute integer translation to bring everything into [0,1]
            float offsetX = -Mathf.Floor(minU);
            float offsetY = -Mathf.Floor(minV);

            // Verify
            if (minU + offsetX >= 0 && maxU + offsetX <= 1.0f + 1e-6f &&
                minV + offsetY >= 0 && maxV + offsetY <= 1.0f + 1e-6f)
            {
                return new Vector2(offsetX, offsetY);
            }

            return null;
        }

        // Union-Find helpers
        private static int Find(int[] parent, int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]]; // path compression
                x = parent[x];
            }
            return x;
        }

        private static void Union(int[] parent, int a, int b)
        {
            int ra = Find(parent, a);
            int rb = Find(parent, b);
            if (ra != rb) parent[ra] = rb;
        }

        private static bool RectsOverlap(Rect a, Rect b)
        {
            return a.xMax > b.xMin && b.xMax > a.xMin &&
                   a.yMax > b.yMin && b.yMax > a.yMin;
        }
    }
}
