// ============================================================================
// MeshUtils.cs — 网格与 UV 工具 / Mesh and UV utilities
// (EN) Reads mesh UV channels, extracts UV islands via union-find over shared
//      vertices, and computes world-space triangle areas (for pixel density).
// (ZH) 读取网格 UV 通道、通过共享顶点并查集提取 UV 岛，并计算世界空间三角面积
//      （用于像素密度）。
// ============================================================================

using System.Collections.Generic;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer
{
    public static class ATOMeshUtils
    {
        /// <summary>(EN) Get the shared mesh of a renderer. (ZH) 获取渲染器的共享网格。</summary>
        public static Mesh GetMesh(Renderer renderer)
        {
            if (renderer is SkinnedMeshRenderer smr) return smr.sharedMesh;
            if (renderer is MeshRenderer mr)
            {
                var mf = mr.GetComponent<MeshFilter>();
                return mf != null ? mf.sharedMesh : null;
            }
            return null;
        }

        /// <summary>(EN) Which UV channels the mesh has data for. (ZH) 网格中有数据的 UV 通道。</summary>
        public static bool[] GetUvChannelPresence(Mesh mesh)
        {
            var present = new bool[8];
            if (mesh == null) return present;
            var tmp = new List<Vector2>();
            for (int c = 0; c < 8; c++)
            {
                mesh.GetUVs(c, tmp);
                present[c] = tmp.Count > 0;
            }
            return present;
        }

        /// <summary>(EN) Extract UV islands for a UV channel across all submeshes. (ZH) 跨全部子网格提取某 UV 通道的岛。</summary>
        public static List<ATOUVIsland> ExtractIslands(Mesh mesh, int channel)
        {
            var result = new List<ATOUVIsland>();
            if (mesh == null) return result;

            var uvs = new List<Vector2>();
            mesh.GetUVs(channel, uvs);
            if (uvs.Count == 0) return result;

            // 收集全部子网格三角形并记录其子网格 / gather all triangles with submesh index
            var allTris = new List<int>();
            var triToSubmesh = new List<int>();
            var triBuffer = new List<int>();
            for (int sub = 0; sub < mesh.subMeshCount; sub++)
            {
                mesh.GetTriangles(triBuffer, sub);
                for (int i = 0; i < triBuffer.Count; i++)
                {
                    allTris.Add(triBuffer[i]);
                    triToSubmesh.Add(sub);
                }
            }

            int triCount = allTris.Count / 3;

            // 顶点 → 三角形映射 / vertex -> triangles map
            var vertToTris = new Dictionary<int, List<int>>();
            for (int t = 0; t < triCount; t++)
            {
                for (int k = 0; k < 3; k++)
                {
                    int v = allTris[t * 3 + k];
                    if (!vertToTris.TryGetValue(v, out var list))
                    {
                        list = new List<int>();
                        vertToTris[v] = list;
                    }
                    list.Add(t);
                }
            }

            // 并查集 / union-find over triangles
            var dsu = new DSU(triCount);
            foreach (var list in vertToTris.Values)
            {
                for (int i = 1; i < list.Count; i++)
                    dsu.Union(list[0], list[i]);
            }

            // 按根分组 / group by root
            var islands = new Dictionary<int, ATOUVIsland>();
            for (int t = 0; t < triCount; t++)
            {
                int root = dsu.Find(t);
                if (!islands.TryGetValue(root, out var island))
                {
                    island = new ATOUVIsland { UvChannel = channel };
                    islands[root] = island;
                }
                island.Triangles.Add(t);
                island.TriangleVerts.Add(allTris[t * 3 + 0]);
                island.TriangleVerts.Add(allTris[t * 3 + 1]);
                island.TriangleVerts.Add(allTris[t * 3 + 2]);
                island.TriangleUVs.Add(uvs[allTris[t * 3 + 0]]);
                island.TriangleUVs.Add(uvs[allTris[t * 3 + 1]]);
                island.TriangleUVs.Add(uvs[allTris[t * 3 + 2]]);
                island.Submeshes.Add(triToSubmesh[t]);
            }

            // 计算包围盒与世界面积 / compute bounds and world area
            var verts = mesh.vertices;
            foreach (var island in islands.Values)
            {
                ComputeIslandGeometry(island, allTris, uvs, verts, channel);
                result.Add(island);
            }

            return result;
        }

        private static void ComputeIslandGeometry(ATOUVIsland island, List<int> tris, List<Vector2> uvs, Vector3[] verts, int channel)
        {
            Vector2 min = new Vector2(float.MaxValue, float.MaxValue);
            Vector2 max = new Vector2(float.MinValue, float.MinValue);
            float area = 0f;

            foreach (var t in island.Triangles)
            {
                int i0 = tris[t * 3 + 0], i1 = tris[t * 3 + 1], i2 = tris[t * 3 + 2];
                var uv0 = uvs[i0], uv1 = uvs[i1], uv2 = uvs[i2];

                min = Vector2.Min(min, Vector2.Min(uv0, Vector2.Min(uv1, uv2)));
                max = Vector2.Max(max, Vector2.Max(uv0, Vector2.Max(uv1, uv2)));

                // 世界空间面积 / world-space area (triangle cross product / 2)
                var p0 = verts[i0], p1 = verts[i1], p2 = verts[i2];
                area += Vector3.Cross(p1 - p0, p2 - p0).magnitude * 0.5f;
            }

            island.Bounds = new Rect(min.x, min.y, max.x - min.x, max.y - min.y);
            island.WorldArea = area;
        }

        /// <summary>(EN) Set island pixel dimensions from source texture resolution. (ZH) 由源贴图分辨率设置岛像素尺寸。</summary>
        public static void SetIslandPixelSize(ATOUVIsland island, int texWidth, int texHeight)
        {
            island.PixelWidth = Mathf.Max(1, Mathf.RoundToInt(island.Bounds.width * texWidth));
            island.PixelHeight = Mathf.Max(1, Mathf.RoundToInt(island.Bounds.height * texHeight));
        }

        /// <summary>(EN) Max world area of the island over all blendshapes (each at 100 vs 0).
        ///     Takes per-blendshape max only (no combinations), per project spec.
        /// (ZH) 该岛在所有形态键下的最大世界面积（各形态键 0 vs 100 取最大，不考虑组合）。</summary>
        public static float ComputeMaxBlendShapeArea(Mesh mesh, ATOUVIsland island)
        {
            if (mesh == null || mesh.blendShapeCount == 0) return island.WorldArea;

            float maxArea = island.WorldArea;
            var baseVerts = mesh.vertices;

            for (int bs = 0; bs < mesh.blendShapeCount; bs++)
            {
                var deltaVerts = new Vector3[mesh.vertexCount];
                var dn = new Vector3[mesh.vertexCount];
                var dt = new Vector3[mesh.vertexCount];
                mesh.GetBlendShapeFrameVertices(bs, mesh.GetBlendShapeFrameCount(bs) - 1, deltaVerts, dn, dt);

                float area = 0f;
                for (int k = 0; k < island.TriangleVerts.Count; k += 3)
                {
                    int i0 = island.TriangleVerts[k], i1 = island.TriangleVerts[k + 1], i2 = island.TriangleVerts[k + 2];
                    var p0 = baseVerts[i0] + deltaVerts[i0];
                    var p1 = baseVerts[i1] + deltaVerts[i1];
                    var p2 = baseVerts[i2] + deltaVerts[i2];
                    area += Vector3.Cross(p1 - p0, p2 - p0).magnitude * 0.5f;
                }
                maxArea = Mathf.Max(maxArea, area);
            }

            return maxArea;
        }

        // ---------------------------------------------------------------------
        // 并查集 / Disjoint Set Union
        // ---------------------------------------------------------------------
        private sealed class DSU
        {
            private readonly int[] _parent;
            private readonly int[] _rank;

            public DSU(int n)
            {
                _parent = new int[n];
                _rank = new int[n];
                for (int i = 0; i < n; i++) _parent[i] = i;
            }

            public int Find(int x)
            {
                while (_parent[x] != x)
                {
                    _parent[x] = _parent[_parent[x]];
                    x = _parent[x];
                }
                return x;
            }

            public void Union(int a, int b)
            {
                int ra = Find(a), rb = Find(b);
                if (ra == rb) return;
                if (_rank[ra] < _rank[rb]) { _parent[ra] = rb; }
                else if (_rank[ra] > _rank[rb]) { _parent[rb] = ra; }
                else { _parent[rb] = ra; _rank[ra]++; }
            }
        }
    }
}
