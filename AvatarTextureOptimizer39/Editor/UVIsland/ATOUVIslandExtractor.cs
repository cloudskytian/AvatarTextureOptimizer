// Avatar Texture Optimizer (ATO)
// Bilingual comments: English + Simplified Chinese. 双语注释：英文 + 简体中文。

using System.Collections.Generic;
using UnityEngine;

namespace AvatarTextureOptimizer.Editor.UVIsland
{
    /// <summary>
    /// Extracts UV islands from a mesh. Islands are connected components of the triangle
    /// adjacency graph (triangles sharing a vertex index). Since Unity stores UVs per
    /// vertex, seam vertices are distinct vertex indices, so topology-based components
    /// exactly match UV islands.
    ///
    /// 从网格提取 UV 岛。岛是三角形邻接图（共享顶点索引）的连通分量。因 Unity 按顶点存储
    /// UV，缝处为不同顶点索引，故拓扑连通分量与 UV 岛一一对应。
    /// </summary>
    public static class ATOUVIslandExtractor
    {
        /// <summary>
        /// Extract islands for one submesh of a mesh.
        /// 提取网格某个子网格的岛。
        /// </summary>
        public static List<ATOUVIsland> Extract(Mesh mesh, int submesh)
        {
            var islands = new List<ATOUVIsland>();

            int[] tris = mesh.GetTriangles(submesh);
            int triCount = tris.Length / 3;
            var vertices = mesh.vertices;

            // Union-find over triangles. 三角形并查集。
            var parent = new int[triCount];
            for (int i = 0; i < triCount; i++) parent[i] = i;

            // vertex → triangles map. 顶点 → 三角形 映射。
            var vertToTris = new Dictionary<int, List<int>>();
            for (int t = 0; t < triCount; t++)
            {
                for (int k = 0; k < 3; k++)
                {
                    int v = tris[t * 3 + k];
                    if (!vertToTris.TryGetValue(v, out var list))
                    {
                        list = new List<int>();
                        vertToTris[v] = list;
                    }
                    list.Add(t);
                }
            }

            foreach (var kv in vertToTris)
            {
                var list = kv.Value;
                for (int i = 1; i < list.Count; i++)
                    Union(parent, Find(parent, list[0]), Find(parent, list[i]));
            }

            // Group triangles by root. 按根分组。
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

            foreach (var kv in groups)
            {
                var island = new ATOUVIsland { Triangles = kv.Value };
                ComputeBoundsAndArea(island, mesh, tris, vertices);
                islands.Add(island);
            }

            return islands;
        }

        private static void ComputeBoundsAndArea(ATOUVIsland island, Mesh mesh, int[] tris, Vector3[] vertices)
        {
            // Unique vertices. 唯一顶点。
            var vertSet = new HashSet<int>();
            foreach (var t in island.Triangles)
            {
                vertSet.Add(tris[t * 3]);
                vertSet.Add(tris[t * 3 + 1]);
                vertSet.Add(tris[t * 3 + 2]);
            }

            // UV bounds (per channel 0..7). UV 包围盒（0..7 通道）。
            island.UvBounds = new Rect[8];
            for (int ch = 0; ch < 8; ch++)
            {
                var uvs = new List<Vector2>();
                if (ch == 0) uvs = new List<Vector2>(mesh.uv);
                else if (ch == 1) uvs = new List<Vector2>(mesh.uv2);
                else
                {
                    var l = new List<Vector2>();
                    if (mesh.GetUVs(ch, l)) uvs = l;
                }

                if (uvs.Count == 0) continue;

                float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
                foreach (var v in vertSet)
                {
                    if (v >= uvs.Count) continue;
                    var uv = uvs[v];
                    if (uv.x < minX) minX = uv.x;
                    if (uv.y < minY) minY = uv.y;
                    if (uv.x > maxX) maxX = uv.x;
                    if (uv.y > maxY) maxY = uv.y;
                }
                island.UvBounds[ch] = new Rect(minX, minY, maxX - minX, maxY - minY);
            }

            // World-space area (base). 世界空间面积（基准）。
            float baseArea = 0f;
            foreach (var t in island.Triangles)
            {
                var a = vertices[tris[t * 3]];
                var b = vertices[tris[t * 3 + 1]];
                var c = vertices[tris[t * 3 + 2]];
                baseArea += Vector3.Cross(b - a, c - a).magnitude * 0.5f;
            }
            island.WorldArea = baseArea;
            island.MaxArea = baseArea;
            island.MaxAreaFactor = 1f;

            // Morph key max area (weight 0 vs 100). 形态键最大面积（0 vs 100）。
            int bsCount = mesh.blendShapeCount;
            if (bsCount > 0)
            {
                var delta = new Vector3[vertices.Length];
                for (int bs = 0; bs < bsCount; bs++)
                {
                    int frameCount = mesh.GetBlendShapeFrameCount(bs);
                    if (frameCount == 0) continue;
                    // Last frame is typically the max weight frame. 最后一帧通常为最大权重帧。
                    mesh.GetBlendShapeFrameVertices(bs, frameCount - 1, delta, null, null);
                    float morphArea = 0f;
                    foreach (var t in island.Triangles)
                    {
                        var a = vertices[tris[t * 3]] + delta[tris[t * 3]];
                        var b = vertices[tris[t * 3 + 1]] + delta[tris[t * 3 + 1]];
                        var c = vertices[tris[t * 3 + 2]] + delta[tris[t * 3 + 2]];
                        morphArea += Vector3.Cross(b - a, c - a).magnitude * 0.5f;
                    }
                    if (morphArea > island.MaxArea) island.MaxArea = morphArea;
                }
                if (baseArea > 1e-8f) island.MaxAreaFactor = island.MaxArea / baseArea;
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
            int ra = Find(parent, a), rb = Find(parent, b);
            if (ra != rb) parent[ra] = rb;
        }
    }

    /// <summary>
    /// A UV island with its triangles, per-channel UV bounds and world-area factors.
    /// 一个 UV 岛：三角形、各通道 UV 包围盒、世界面积系数。
    /// </summary>
    public sealed class ATOUVIsland
    {
        public List<int> Triangles;
        public Rect[] UvBounds;     // per UV channel. 各 UV 通道包围盒。
        public float WorldArea;     // base world area. 基准世界面积。
        public float MaxArea;       // max over morph keys. 形态键最大面积。
        public float MaxAreaFactor; // >=1. 面积放大系数。

        /// <summary>
        /// True if UV bounds of a channel straddle an integer tile boundary (needs wrap).
        /// 是否跨整数瓦片边界（需 wrap 采样）。
        /// </summary>
        public bool CrossesSeam(int channel)
        {
            var r = UvBounds[channel];
            return Mathf.FloorToInt(r.xMax) != Mathf.FloorToInt(r.xMin) ||
                   Mathf.FloorToInt(r.yMax) != Mathf.FloorToInt(r.yMin);
        }
    }
}
