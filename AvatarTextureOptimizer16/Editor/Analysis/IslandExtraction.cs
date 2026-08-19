using System;
using System.Collections.Generic;
using UnityEngine;

namespace AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Extracts connected UV islands from a mesh for a given UV channel. / 从网格指定 UV 通道提取连通 UV 岛。
    /// Uses union-find over triangles sharing UV-space edges. / 用并查集对共享 UV 空间边的三角形分组。
    /// Also computes local-space area (for pixel density) and normalized UVs (for rasterization).
    /// 同时计算本地空间面积（用于像素密度）与归一化 UV（用于光栅化）。
    /// </summary>
    public static class IslandExtraction
    {
        /// <summary>
        /// Extract islands for one UV channel across all submeshes. / 提取某 UV 通道跨全部子网格的岛。
        /// </summary>
        public static List<UvIsland> Extract(Mesh mesh, int uvChannel)
        {
            var result = new List<UvIsland>();
            if (mesh == null) return result;

            Vector2[] uvs = GetUVs(mesh, uvChannel);
            if (uvs == null || uvs.Length == 0) return result;

            Vector3[] verts = mesh.vertices;
            bool hasVerts = verts != null && verts.Length > 0;

            // gather all triangles / 收集全部三角形
            int triCount = 0;
            for (int s = 0; s < mesh.subMeshCount; s++) triCount += (int)mesh.GetIndexCount(s) / 3;
            if (triCount == 0) return result;

            int[] parent = new int[triCount];
            int[] triSubmesh = new int[triCount];
            for (int i = 0; i < triCount; i++) parent[i] = i;

            int[] triVerts = new int[triCount * 3];
            int cursor = 0;
            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                var indices = mesh.GetIndices(s);
                for (int i = 0; i < indices.Length; i++)
                {
                    triVerts[cursor] = indices[i];
                    triSubmesh[cursor / 3] = s;
                    cursor++;
                }
            }

            var edgeMap = new Dictionary<ulong, int>();

            for (int t = 0; t < triCount; t++)
            {
                int a = triVerts[t * 3], b = triVerts[t * 3 + 1], c = triVerts[t * 3 + 2];
                if (a >= uvs.Length || b >= uvs.Length || c >= uvs.Length) continue;
                // union only within the same submesh / 仅在相同 submesh 内合并
                AddEdge(edgeMap, uvs[a], uvs[b], t, triSubmesh[t], parent, triSubmesh);
                AddEdge(edgeMap, uvs[b], uvs[c], t, triSubmesh[t], parent, triSubmesh);
                AddEdge(edgeMap, uvs[c], uvs[a], t, triSubmesh[t], parent, triSubmesh);
            }

            var byRoot = new Dictionary<int, List<int>>();
            for (int t = 0; t < triCount; t++)
            {
                int r = Find(parent, t);
                if (!byRoot.TryGetValue(r, out var list)) { list = new List<int>(); byRoot[r] = list; }
                list.Add(t);
            }

            int islandIndex = 0;
            foreach (var kv in byRoot)
            {
                var island = new UvIsland { islandIndex = islandIndex++, uvChannel = uvChannel, submesh = triSubmesh[kv.Value[0]] };
                island.triangleIndices.AddRange(kv.Value);

                var min = new Vector2(float.MaxValue, float.MaxValue);
                var max = new Vector2(float.MinValue, float.MinValue);
                bool anyOutOfRange = false;

                foreach (var t in kv.Value)
                    for (int e = 0; e < 3; e++)
                    {
                        var uv = uvs[triVerts[t * 3 + e]];
                        min = Vector2.Min(min, uv);
                        max = Vector2.Max(max, uv);
                        if (uv.x < 0f || uv.x > 1f || uv.y < 0f || uv.y > 1f) anyOutOfRange = true;
                    }

                island.bounds = new Rect(min.x, min.y, max.x - min.x, max.y - min.y);

                // UV area + local-space area / UV 面积 + 本地空间面积
                float uvArea = 0f, localArea = 0f;
                foreach (var t in kv.Value)
                {
                    int ia = triVerts[t * 3], ib = triVerts[t * 3 + 1], ic = triVerts[t * 3 + 2];
                    var u0 = uvs[ia]; var u1 = uvs[ib]; var u2 = uvs[ic];
                    uvArea += Mathf.Abs((u1.x - u0.x) * (u2.y - u0.y) - (u2.x - u0.x) * (u1.y - u0.y)) * 0.5f;

                    if (hasVerts && ia < verts.Length && ib < verts.Length && ic < verts.Length)
                        localArea += TriangleArea3D(verts[ia], verts[ib], verts[ic]);
                }
                island.area = uvArea;
                island.localArea = localArea;

                // store flattened UV + normalized UV (local 0..1) / 存储展平 UV + 归一化 UV
                float bw = Mathf.Max(0.0001f, max.x - min.x);
                float bh = Mathf.Max(0.0001f, max.y - min.y);
                foreach (var t in kv.Value)
                    for (int e = 0; e < 3; e++)
                    {
                        var uv = uvs[triVerts[t * 3 + e]];
                        island.uvCoordinates.Add(uv);
                        island.normalizedUV.Add(new Vector2((uv.x - min.x) / bw, (uv.y - min.y) / bh));
                    }

                // out-of-range: if spanning multiple tiles in either axis it crosses a wrap seam.
                // 越界：任一轴跨多个 tile 即跨 wrap 缝。
                island.outOfRangeNeedsRepeat = anyOutOfRange &&
                    ((max.x - min.x) > 1f || (max.y - min.y) > 1f);

                result.Add(island);
            }

            return result;
        }

        private static float TriangleArea3D(Vector3 a, Vector3 b, Vector3 c)
        {
            return Vector3.Cross(b - a, c - a).magnitude * 0.5f;
        }

        private static void AddEdge(Dictionary<ulong, int> map, Vector2 a, Vector2 b, int tri, int submesh, int[] parent, int[] triSubmesh)
        {
            ulong key = EdgeKey(a, b);
            if (map.TryGetValue(key, out int other))
            {
                if (triSubmesh[other] == submesh) Union(parent, tri, other);
            }
            else map[key] = tri;
        }

        private static ulong EdgeKey(Vector2 a, Vector2 b)
        {
            if (a.x > b.x || (a.x == b.x && a.y > b.y)) { var t = a; a = b; b = t; }
            uint ax = (uint)(a.x * 4096f);
            uint ay = (uint)(a.y * 4096f);
            uint bx = (uint)(b.x * 4096f);
            uint by = (uint)(b.y * 4096f);
            return ((ulong)ax << 48) | ((ulong)ay << 32) | ((ulong)bx << 16) | by;
        }

        private static int Find(int[] parent, int i)
        {
            while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; }
            return i;
        }

        private static void Union(int[] parent, int a, int b)
        {
            int ra = Find(parent, a), rb = Find(parent, b);
            if (ra != rb) parent[rb] = ra;
        }

        public static Vector2[] GetUVs(Mesh mesh, int channel)
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

        public static void SetUVs(Mesh mesh, int channel, Vector2[] uvs)
        {
            switch (channel)
            {
                case 0: mesh.uv = uvs; break;
                case 1: mesh.uv2 = uvs; break;
                case 2: mesh.uv3 = uvs; break;
                case 3: mesh.uv4 = uvs; break;
                case 4: mesh.uv5 = uvs; break;
                case 5: mesh.uv6 = uvs; break;
                case 6: mesh.uv7 = uvs; break;
                case 7: mesh.uv8 = uvs; break;
            }
        }
    }
}
