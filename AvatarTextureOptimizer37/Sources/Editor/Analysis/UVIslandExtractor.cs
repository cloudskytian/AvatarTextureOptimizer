// ============================================================================
// ATO - UV island extraction
// ATO - UV 岛提取
//
// For each (mesh, submesh, UV channel):
//   - extract connected UV islands (triangle-edge UV adjacency, eps 1e-4);
//   - normalize out-of-range islands: a whole island may be translated into
//     [0,1] iff its extent is <= 1 per axis (no wrap seam); islands whose
//     extent > 1 (relying on repeat sampling) mark the texture as
//     unprocessable (whitelist + warning);
//   - merge islands of the SAME texture whose normalized bboxes overlap
//     (they sample the same pixels and must share one atlas region);
//   - group islands across textures that share a UV region (UV groups).
// ============================================================================

#region

using System.Collections.Generic;
using net.fosa.AvatarTextureOptimizer.Editor.Core;
using UnityEngine;

#endregion

namespace net.fosa.AvatarTextureOptimizer.Editor.Analysis
{
    public static class UVIslandExtractor
    {
        private const float Eps = 1e-4f;

        /// <summary>Extracts islands for one (mesh, submesh, channel).
        /// Islands are triangle-level connected components: two triangles
        /// join when they share a vertex pair whose UVs are continuous
        /// (equal). Single isolated triangles form their own island.
        /// 提取单个 (网格,子网格,通道) 的岛。岛是三角形级连通域：两三角共享
        /// 顶点对且该处 UV 连续（相等）时合并。孤立单三角自成岛。
        /// </summary>
        public static List<ATOUVIsland> Extract(Mesh mesh, int submesh, int channel,
            ATOMeshUVSet uvSet, out bool repeatWrapping)
        {
            repeatWrapping = false;
            var result = new List<ATOUVIsland>();

            var uvs = GetUVs(mesh, channel);
            if (uvs == null) return result;

            int[] tris = mesh.GetTriangles(submesh);
            int triCount = tris.Length / 3;
            if (triCount == 0) return result;

            // union-find over triangles  三角并查集
            var parent = new int[triCount];
            for (int i = 0; i < triCount; i++) parent[i] = i;

            int Find(int i)
            {
                while (parent[i] != i)
                {
                    parent[i] = parent[parent[i]];
                    i = parent[i];
                }
                return i;
            }

            // index triangles by shared vertex pair  按共享顶点对索引三角
            var byPair = new Dictionary<(int, int), List<int>>();
            void AddToPair(int v1, int v2, int ti)
            {
                var key = v1 < v2 ? (v1, v2) : (v2, v1);
                if (!byPair.TryGetValue(key, out var list))
                {
                    list = new List<int>();
                    byPair[key] = list;
                }
                list.Add(ti);
            }
            for (int t = 0; t < triCount; t++)
            {
                int a = tris[t * 3], b = tris[t * 3 + 1], c = tris[t * 3 + 2];
                AddToPair(a, b, t);
                AddToPair(b, c, t);
                AddToPair(a, c, t);
            }

            // connect triangles with continuous UVs on the shared pair
            // UV 连续的共享顶点对上的三角互相连接
            foreach (var (key, list) in byPair)
            {
                if (list.Count < 2) continue;
                // connectivity is transitive within the pair group: connect
                // every triangle whose UVs at the shared pair equal the
                // first triangle's (chain through equal-UV triangles).
                // 组内传递连接：与首三角共享对 UV 相等的三角全部连接
                var root = list[0];
                for (int i = 1; i < list.Count; i++)
                {
                    var a1 = tris[root * 3];
                    var a2 = tris[root * 3 + 1];
                    var a3 = tris[root * 3 + 2];
                    var b1 = tris[list[i] * 3];
                    var b2 = tris[list[i] * 3 + 1];
                    var b3 = tris[list[i] * 3 + 2];

                    Vector2 UVof(int v, Vector2 u1, Vector2 u2, Vector2 u3)
                    {
                        if (v == a1) return u1;
                        if (v == a2) return u2;
                        if (v == a3) return u3;
                        return uvs[v];
                    }
                    Vector2 UVofB(int v, Vector2 v1, Vector2 v2, Vector2 v3)
                    {
                        if (v == b1) return v1;
                        if (v == b2) return v2;
                        if (v == b3) return v3;
                        return uvs[v];
                    }

                    Vector2 ua = UVof(key.Item1, uvs[a1], uvs[a2], uvs[a3]);
                    Vector2 ua2 = UVof(key.Item2, uvs[a1], uvs[a2], uvs[a3]);
                    Vector2 ub = UVofB(key.Item1, uvs[b1], uvs[b2], uvs[b3]);
                    Vector2 ub2 = UVofB(key.Item2, uvs[b1], uvs[b2], uvs[b3]);

                    if (Mathf.Approximately(ua.x, ub.x) && Mathf.Approximately(ua.y, ub.y) &&
                        Mathf.Approximately(ua2.x, ub2.x) && Mathf.Approximately(ua2.y, ub2.y))
                    {
                        parent[Find(list[i])] = Find(root);
                    }
                }
            }

            // components  组件
            var compTriangles = new Dictionary<int, List<int>>();
            for (int t = 0; t < triCount; t++)
            {
                int r = Find(t);
                if (!compTriangles.TryGetValue(r, out var list))
                {
                    list = new List<int>();
                    compTriangles[r] = list;
                }
                list.Add(t);
            }

            foreach (var list in compTriangles.Values)
            {
                // bbox over all component triangle UVs  组件全部三角 UV 包围盒
                var min = new Vector2(float.MaxValue, float.MaxValue);
                var max = new Vector2(float.MinValue, float.MinValue);
                foreach (var t in list)
                {
                    for (int i = 0; i < 3; i++)
                    {
                        var uv = uvs[tris[t * 3 + i]];
                        min = Vector2.Min(min, uv);
                        max = Vector2.Max(max, uv);
                    }
                }

                var extent = max - min;
                if (extent.x < 1e-6f && extent.y < 1e-6f) continue; // degenerate 退化
                if (extent.x > 1f + 1e-3f || extent.y > 1f + 1e-3f)
                {
                    repeatWrapping = true;
                    continue; // island spans wrap seam 岛跨 wrap 缝
                }

                var shift = -min; // translate into [0,1] 平移进 [0,1]

                var triList = new int[list.Count * 3];
                for (int i = 0; i < list.Count; i++)
                {
                    int t = list[i];
                    triList[i * 3] = tris[t * 3];
                    triList[i * 3 + 1] = tris[t * 3 + 1];
                    triList[i * 3 + 2] = tris[t * 3 + 2];
                }

                float uvArea = extent.x * extent.y;
                result.Add(new ATOUVIsland
                {
                    UVSet = uvSet,
                    Triangles = triList,
                    MinUV = Vector2.zero,
                    MaxUV = extent,
                    ShiftUV = shift,
                    UVArea = uvArea,
                    WorldArea = uvArea * uvSet.MetersPerUV * uvSet.MetersPerUV
                                * uvSet.MaxScaleArea * uvSet.ShapeKeyArea,
                });
            }

            return result;
        }

        /// <summary>Merges islands of the same texture whose normalized
        /// bboxes overlap (union-find). Returns cluster id per island (the
        /// first island's cluster id == its own index in input order when
        /// alone).
        /// 合并同一贴图中归一化包围盒重叠的岛（并查集）。返回每岛的簇 id。</summary>
        public static Dictionary<ATOUVIsland, int> MergeOverlaps(
            IEnumerable<ATOUVIsland> islandsOfTexture)
        {
            var list = new List<ATOUVIsland>(islandsOfTexture);
            var parent = new int[list.Count];
            for (int i = 0; i < list.Count; i++) parent[i] = i;

            int Find(int i)
            {
                while (parent[i] != i)
                {
                    parent[i] = parent[parent[i]];
                    i = parent[i];
                }
                return i;
            }

            for (int i = 0; i < list.Count; i++)
            {
                for (int j = i + 1; j < list.Count; j++)
                {
                    var a = list[i];
                    var b = list[j];
                    // bbox overlap (normalized space) 包围盒重叠（归一化空间）
                    if (a.MinUV.x <= b.MaxUV.x + Eps && b.MinUV.x <= a.MaxUV.x + Eps &&
                        a.MinUV.y <= b.MaxUV.y + Eps && b.MinUV.y <= a.MaxUV.y + Eps)
                    {
                        parent[Find(i)] = Find(j);
                    }
                }
            }

            var result = new Dictionary<ATOUVIsland, int>();
            foreach (var island in list)
            {
                int idx = list.IndexOf(island);
                result[island] = Find(idx);
            }
            return result;
        }

        /// <summary>Groups islands (across textures) that share a UV region.
        /// Quantized bbox key at 1/256.
        /// 跨贴图共享 UV 区域的岛分组（1/256 量化包围盒键）。</summary>
        public static List<ATOUVGroup> BuildUVGroups(List<ATOUVIsland> islands,
            List<ATOUVGroup> outGroups)
        {
            var keyMap = new Dictionary<(int, int, int, int), ATOUVGroup>();
            var groupIslandIndex = new Dictionary<ATOUVIsland, ATOUVGroup>();

            foreach (var island in islands)
            {
                int qx0 = Mathf.RoundToInt(island.MinUV.x * 256f);
                int qy0 = Mathf.RoundToInt(island.MinUV.y * 256f);
                int qx1 = Mathf.RoundToInt(island.MaxUV.x * 256f);
                int qy1 = Mathf.RoundToInt(island.MaxUV.y * 256f);
                var key = (qx0, qy0, qx1, qy1);

                if (!keyMap.TryGetValue(key, out var group))
                {
                    group = new ATOUVGroup { Id = outGroups.Count, MinUV = island.MinUV, MaxUV = island.MaxUV };
                    keyMap[key] = group;
                    outGroups.Add(group);
                }

                group.Islands.Add(island);
                island.UVGroup = group.Id;
                groupIslandIndex[island] = group;

                if (group.Anchor == null || island.UVArea > group.Anchor.UVArea)
                {
                    group.Anchor = island;
                    group.MinUV = island.MinUV;
                    group.MaxUV = island.MaxUV;
                }
            }
            return outGroups;
        }

        public static Vector2[] GetUVs(Mesh mesh, int channel)
        {
            switch (channel)
            {
                case 0: return mesh.uv;
                case 1: return mesh.uv2;
                case 2: return mesh.uv3;
                case 3: return mesh.uv4;
                default: return null;
            }
        }

        /// <summary>UV extent of a mesh channel (max per-axis span).
        /// 网格某通道 UV 跨度（各轴最大范围）。</summary>
        public static Vector2 UVExtent(Mesh mesh, int channel)
        {
            var uvs = GetUVs(mesh, channel);
            if (uvs == null || uvs.Length == 0) return Vector2.zero;
            var min = uvs[0];
            var max = uvs[0];
            for (int i = 1; i < uvs.Length; i++)
            {
                min = Vector2.Min(min, uvs[i]);
                max = Vector2.Max(max, uvs[i]);
            }
            return max - min;
        }
    }
}
