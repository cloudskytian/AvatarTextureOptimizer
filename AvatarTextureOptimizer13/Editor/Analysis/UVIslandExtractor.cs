// ATO — Avatar Texture Optimizer
// UV island extraction: builds connected triangle groups in UV space (per channel),
// normalizes out-of-bounds UVs when a pure integer translation fits [0,1] without
// crossing a wrap seam, and merges overlapping islands.
// UV 岛提取：在 UV 空间（逐通道）构建连通的三角形组；当纯整数平移可放入 [0,1] 且不跨
// wrap 缝时归一化越界 UV；合并重叠岛。

using System.Collections.Generic;
using UnityEngine;

namespace net.fosa.ato.editor
{
    /// <summary>
    /// Result of island extraction for one mesh+channel. 某网格某通道的岛提取结果。
    /// </summary>
    public class ATOIslandExtractResult
    {
        public List<ATOIsland> islands = new List<ATOIsland>();
        /// <summary>True when UVs could not be normalized (cross-seam repeat). 无法归一化（跨缝 repeat）时为 true。</summary>
        public bool cannotNormalize;
    }

    /// <summary>
    /// Extracts UV islands from a mesh. 从网格提取 UV 岛。
    /// </summary>
    public static class UVIslandExtractor
    {
        private const float Eps = 1e-4f;
        private const float Quant = 1e6f;

        /// <summary>
        /// Extract islands for the given UV channel and submesh (material slot).
        /// <paramref name="areaScaleFactor"/> multiplies the world-space area (animated scale).
        /// 提取给定 UV 通道与子网格（材质槽）的岛。<paramref name="areaScaleFactor"/> 乘以世界空间面积（动画缩放）。
        /// </summary>
        public static ATOIslandExtractResult Extract(Mesh mesh, int channel, float areaScaleFactor, int submeshIndex)
        {
            var result = new ATOIslandExtractResult();
            if (mesh == null) { result.cannotNormalize = true; return result; }

            var uvs = new List<Vector2>();
            mesh.GetUVs(channel, uvs);
            int[] tris = mesh.triangles;
            Vector3[] verts = mesh.vertices;

            if (tris == null || tris.Length == 0 || uvs.Count == 0)
            {
                return result; // no UVs → nothing
            }

            // Restrict to the submesh's triangle range. 限定到子网格的三角形范围。
            int triStart = 0, triEnd = tris.Length / 3;
            if (submeshIndex >= 0 && submeshIndex < mesh.subMeshCount)
            {
                var sm = mesh.GetSubMesh(submeshIndex);
                triStart = sm.indexStart / 3;
                triEnd = (sm.indexStart + sm.indexCount) / 3;
            }
            triStart = Mathf.Max(0, triStart);
            triEnd = Mathf.Min(tris.Length / 3, triEnd);

            // Quantize UVs for stable edge keys. 量化 UV 以获得稳定的边键。
            var quvs = new Vector2[uvs.Count];
            for (int i = 0; i < uvs.Count; i++) quvs[i] = Quantize(uvs[i]);

            // Build edge → triangle adjacency. 构建边 → 三角形邻接。
            var edgeMap = new Dictionary<(long, long), List<int>>();
            int triCount = triEnd;
            for (int t = triStart; t < triEnd; t++)
            {
                int i0 = tris[t * 3], i1 = tris[t * 3 + 1], i2 = tris[t * 3 + 2];
                AddEdge(edgeMap, t, quvs[i0], quvs[i1]);
                AddEdge(edgeMap, t, quvs[i1], quvs[i2]);
                AddEdge(edgeMap, t, quvs[i2], quvs[i0]);
            }

            // Flood fill islands. 洪泛填充岛。
            var visited = new bool[triCount];
            for (int t = triStart; t < triEnd; t++)
            {
                if (visited[t]) continue;
                var islandTris = new List<int>();
                var stack = new Stack<int>();
                stack.Push(t);
                visited[t] = true;
                while (stack.Count > 0)
                {
                    int cur = stack.Pop();
                    islandTris.Add(cur);
                    int a = tris[cur * 3], b = tris[cur * 3 + 1], c = tris[cur * 3 + 2];
                    foreach (var (e0, e1) in new[] { (quvs[a], quvs[b]), (quvs[b], quvs[c]), (quvs[c], quvs[a]) })
                    {
                        var key = MakeKey(e0, e1);
                        if (edgeMap.TryGetValue(key, out var neighbors))
                        {
                            foreach (var n in neighbors)
                            {
                                if (!visited[n]) { visited[n] = true; stack.Push(n); }
                            }
                        }
                    }
                }

                var island = BuildIsland(mesh, islandTris, uvs, verts, areaScaleFactor);
                if (island != null) result.islands.Add(island);
            }

            // Normalize out-of-bounds UVs. 归一化越界 UV。
            bool allNormalized = true;
            foreach (var island in result.islands)
            {
                if (!TryNormalize(island.originalUV, out var normalized))
                {
                    allNormalized = false;
                    break;
                }
                island.originalUV = normalized;
            }
            result.cannotNormalize = !allNormalized;

            // Merge overlapping islands. 合并重叠岛。
            result.islands = MergeOverlapping(result.islands);

            return result;
        }

        private static void AddEdge(Dictionary<(long, long), List<int>> map, int tri, Vector2 a, Vector2 b)
        {
            var key = MakeKey(a, b);
            if (!map.TryGetValue(key, out var list)) { list = new List<int>(); map[key] = list; }
            if (list.Count == 0 || list[list.Count - 1] != tri) list.Add(tri);
        }

        private static (long, long) MakeKey(Vector2 a, Vector2 b)
        {
            long pa = Pack(a), pb = Pack(b);
            return pa <= pb ? (pa, pb) : (pb, pa);
        }

        private static long Pack(Vector2 v)
        {
            uint x = (uint)(int)Mathf.RoundToInt(v.x * Quant);
            uint y = (uint)(int)Mathf.RoundToInt(v.y * Quant);
            return ((long)x << 32) | y;
        }

        private static Vector2 Quantize(Vector2 v)
        {
            return new Vector2(Mathf.RoundToInt(v.x * Quant), Mathf.RoundToInt(v.y * Quant));
        }

        private static ATOIsland BuildIsland(Mesh mesh, List<int> islandTris, List<Vector2> uvs, Vector3[] verts, float areaScaleFactor)
        {
            // Collect unique vertex indices. 收集唯一顶点下标。
            int[] tris = mesh.triangles;
            var vertSet = new HashSet<int>();
            foreach (var t in islandTris)
            {
                vertSet.Add(tris[t * 3]);
                vertSet.Add(tris[t * 3 + 1]);
                vertSet.Add(tris[t * 3 + 2]);
            }
            var vertList = new List<int>(vertSet);
            var origUV = new Vector2[vertList.Count];
            var vertexToLocal = new Dictionary<int, int>();
            for (int i = 0; i < vertList.Count; i++)
            {
                vertexToLocal[vertList[i]] = i;
                origUV[i] = uvs[vertList[i]];
            }
            // Per-triangle island-local vertex indices (for rasterization). 每三角形岛本地顶点下标（用于光栅化）。
            var triangleUV = new List<int>(islandTris.Count * 3);
            foreach (var t in islandTris)
            {
                triangleUV.Add(vertexToLocal[tris[t * 3]]);
                triangleUV.Add(vertexToLocal[tris[t * 3 + 1]]);
                triangleUV.Add(vertexToLocal[tris[t * 3 + 2]]);
            }

            // UV-space bounds + area, world area. UV 空间包围盒+面积、世界面积。
            Rect bounds = new Rect(origUV[0].x, origUV[0].y, 0, 0);
            float uvArea = 0f, worldArea = 0f;
            foreach (var t in islandTris)
            {
                int a = tris[t * 3], b = tris[t * 3 + 1], c = tris[t * 3 + 2];
                Vector2 ua = uvs[a], ub = uvs[b], uc = uvs[c];
                bounds.xMin = Mathf.Min(bounds.xMin, ua.x, ub.x, uc.x);
                bounds.xMax = Mathf.Max(bounds.xMax, ua.x, ub.x, uc.x);
                bounds.yMin = Mathf.Min(bounds.yMin, ua.y, ub.y, uc.y);
                bounds.yMax = Mathf.Max(bounds.yMax, ua.y, ub.y, uc.y);
                uvArea += Mathf.Abs(TriArea2D(ua, ub, uc));
                if (verts != null && a < verts.Length && b < verts.Length && c < verts.Length)
                    worldArea += Mathf.Abs(TriArea3D(verts[a], verts[b], verts[c]));
            }

            var island = new ATOIsland
            {
                triangles = islandTris,
                vertexIndices = vertList,
                triangleUV = triangleUV,
                originalUV = origUV,
                bounds = bounds,
                uvArea = uvArea,
                worldArea = worldArea * Mathf.Max(areaScaleFactor, 1e-6f),
            };
            return island;
        }

        private static float TriArea2D(Vector2 a, Vector2 b, Vector2 c)
        {
            return 0.5f * ((b.x - a.x) * (c.y - a.y) - (c.x - a.x) * (b.y - a.y));
        }

        private static float TriArea3D(Vector3 a, Vector3 b, Vector3 c)
        {
            return 0.5f * Vector3.Cross(b - a, c - a).magnitude;
        }

        /// <summary>
        /// Normalize an island's UVs into [0,1] via integer translation, if it does not cross a wrap seam.
        /// 若岛不跨 wrap 缝，则通过整数平移将其 UV 归一化到 [0,1]。
        /// </summary>
        public static bool TryNormalize(Vector2[] uv, out Vector2[] normalized)
        {
            normalized = uv;
            if (uv == null || uv.Length == 0) return true;
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            foreach (var v in uv)
            {
                minX = Mathf.Min(minX, v.x); maxX = Mathf.Max(maxX, v.x);
                minY = Mathf.Min(minY, v.y); maxY = Mathf.Max(maxY, v.y);
            }
            // Already inside [0,1]. 已在 [0,1] 内。
            if (minX >= -Eps && minY >= -Eps && maxX <= 1f + Eps && maxY <= 1f + Eps) return true;

            // If the island is wider than one tile, it must cross a seam → cannot normalize safely.
            // 若岛宽超过一个瓦片，必然跨缝 → 无法安全归一化。
            if (maxX - minX > 1f + Eps || maxY - minY > 1f + Eps) return false;

            int tx = Mathf.FloorToInt(minX);
            int ty = Mathf.FloorToInt(minY);
            var result = new Vector2[uv.Length];
            for (int i = 0; i < uv.Length; i++)
            {
                float nx = uv[i].x - tx;
                float ny = uv[i].y - ty;
                if (nx < -Eps || ny < -Eps || nx > 1f + Eps || ny > 1f + Eps)
                {
                    // Translation did not fit → seam crossing. 平移后仍越界 → 跨缝。
                    return false;
                }
                result[i] = new Vector2(nx, ny);
            }
            normalized = result;
            return true;
        }

        /// <summary>
        /// Merge islands whose UV bounds overlap (same texture region). 合并 UV 包围盒重叠的岛（同一贴图区域）。
        /// </summary>
        private static List<ATOIsland> MergeOverlapping(List<ATOIsland> islands)
        {
            var result = new List<ATOIsland>();
            foreach (var island in islands)
            {
                ATOIsland target = null;
                foreach (var existing in result)
                {
                    if (RectsOverlap(existing.bounds, island.bounds))
                    {
                        target = existing;
                        break;
                    }
                }
                if (target == null)
                {
                    result.Add(island);
                }
                else
                {
                    // Merge triangles, UVs and recompute bounds/area. 合并三角形、UV 并重算包围盒/面积。
                    var mergedUVs = new List<Vector2>(target.originalUV);
                    var offset = mergedUVs.Count;
                    mergedUVs.AddRange(island.originalUV);
                    target.triangles.AddRange(island.triangles);
                    target.originalUV = mergedUVs.ToArray();
                    target.bounds = Union(target.bounds, island.bounds);
                    target.uvArea += island.uvArea;
                    target.worldArea = Mathf.Max(target.worldArea, island.worldArea);
                }
            }
            return result;
        }

        private static bool RectsOverlap(Rect a, Rect b)
        {
            return a.xMin < b.xMax && b.xMin < a.xMax && a.yMin < b.yMax && b.yMin < a.yMax;
        }

        private static Rect Union(Rect a, Rect b)
        {
            float xMin = Mathf.Min(a.xMin, b.xMin), yMin = Mathf.Min(a.yMin, b.yMin);
            float xMax = Mathf.Max(a.xMax, b.xMax), yMax = Mathf.Max(a.yMax, b.yMax);
            return new Rect(xMin, yMin, xMax - xMin, yMax - yMin);
        }
    }
}
