// Island extraction: builds connected UV islands from mesh triangles (union-find over shared UV edges),
// merges overlapping islands within a texture, and computes world areas incl. blendshape extremes.
// / 岛提取：基于共享 UV 边并查集构建连通 UV 岛；合并同贴图内的重叠岛；计算含形态键极值的世界面积。

using System.Collections.Generic;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor.analysis
{
    /// <summary>
    /// Extracts UV islands for one (mesh, uv channel). / 为单个（网格, UV 通道）提取 UV 岛。
    /// </summary>
    public static class IslandExtractor
    {
        private const float UvEps = 1e-4f;

        private sealed class UnionFind
        {
            private readonly int[] _p;
            public UnionFind(int n) { _p = new int[n]; for (int i = 0; i < n; i++) _p[i] = i; }
            public int Find(int x)
            {
                while (_p[x] != x) { _p[x] = _p[_p[x]]; x = _p[x]; }
                return x;
            }
            public void Union(int a, int b) { int ra = Find(a), rb = Find(b); if (ra != rb) _p[ra] = rb; }
        }

        /// <summary>Extract islands for a mesh channel. / 提取某网格通道的岛。</summary>
        public static List<Island> Extract(MeshData md, int uvGroupId)
        {
            var uv = md.Uv;
            var tris = md.Triangles;
            int triCount = tris.Length / 3;
            var uf = new UnionFind(triCount);

            // Edge -> triangle map for connectivity / 边 -> 三角形 映射
            var edgeMap = new Dictionary<(long, long), int>();
            for (int t = 0; t < triCount; t++)
            {
                int i0 = tris[t * 3], i1 = tris[t * 3 + 1], i2 = tris[t * 3 + 2];
                AddEdge(edgeMap, uf, t, uv[i0], uv[i1]);
                AddEdge(edgeMap, uf, t, uv[i1], uv[i2]);
                AddEdge(edgeMap, uf, t, uv[i2], uv[i0]);
            }

            // Group triangles into islands / 三角形分组为岛
            var islandOfTri = new int[triCount];
            var islands = new List<Island>();
            var triToIsland = new Dictionary<int, int>(); // root tri -> island idx / 根三角形 -> 岛索引
            for (int t = 0; t < triCount; t++)
            {
                int root = uf.Find(t);
                if (!triToIsland.TryGetValue(root, out int idx))
                {
                    idx = islands.Count;
                    triToIsland[root] = idx;
                    var iso = new Island
                    {
                        Id = idx,
                        UvGroupId = uvGroupId,
                        UvChannel = md.UvChannel,
                        Owner = md,
                        Min = Vector2.one * float.MaxValue,
                        Max = Vector2.one * float.MinValue
                    };
                    islands.Add(iso);
                }
                islandOfTri[t] = idx;
                var iso2 = islands[idx];
                iso2.Triangles.Add(t);
            }

            // Compute bounds, orientation, world area (base + blendshape extremes) / 计算包围盒、绕序、世界面积
            for (int i = 0; i < islands.Count; i++)
            {
                ComputeIslandMetrics(islands[i]);
            }

            return islands;
        }

        private static void AddEdge(Dictionary<(long, long), int> edgeMap, UnionFind uf, int tri,
            Vector2 a, Vector2 b)
        {
            var key = EdgeKey(a, b);
            if (edgeMap.TryGetValue(key, out int other))
            {
                uf.Union(tri, other);
            }
            else
            {
                edgeMap[key] = tri;
            }
        }

        private static (long, long) EdgeKey(Vector2 a, Vector2 b)
        {
            long ka = ((long)Mathf.RoundToInt(a.x / UvEps) << 20) | (Mathf.RoundToInt(a.y / UvEps) & 0xFFFFF);
            long kb = ((long)Mathf.RoundToInt(b.x / UvEps) << 20) | (Mathf.RoundToInt(b.y / UvEps) & 0xFFFFF);
            return ka < kb ? (ka, kb) : (kb, ka);
        }

        private static void ComputeIslandMetrics(Island iso)
        {
            var md = iso.Owner;
            var uv = md.Uv;
            var tris = md.Triangles;
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            double signedArea2 = 0;

            foreach (var t in iso.Triangles)
            {
                var a = uv[tris[t * 3]];
                var b = uv[tris[t * 3 + 1]];
                var c = uv[tris[t * 3 + 2]];
                minX = Mathf.Min(minX, Mathf.Min(a.x, Mathf.Min(b.x, c.x)));
                minY = Mathf.Min(minY, Mathf.Min(a.y, Mathf.Min(b.y, c.y)));
                maxX = Mathf.Max(maxX, Mathf.Max(a.x, Mathf.Max(b.x, c.x)));
                maxY = Mathf.Max(maxY, Mathf.Max(a.y, Mathf.Max(b.y, c.y)));
                signedArea2 += (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);
            }

            iso.Min = new Vector2(minX, minY);
            iso.Max = new Vector2(maxX, maxY);
            iso.Mirrored = signedArea2 < 0;

            // World area: base pose + max over blendshapes at weight 100 / 世界面积：基础姿态与各形态键极值的最大值
            iso.WorldArea = ComputeWorldArea(iso, null);
            if (md.HasBlendShapes && md.MaxVertexDelta > 1e-5f)
            {
                var deltas = md.BlendShapeDeltas;
                for (int b = 0; b < deltas.Length; b++)
                {
                    if (deltas[b] == null) continue;
                    iso.WorldArea = Mathf.Max(iso.WorldArea, ComputeWorldArea(iso, deltas[b]));
                }
            }
            iso.WorldSize = Mathf.Sqrt(iso.WorldArea);
        }

        private static float ComputeWorldArea(Island iso, Vector3[] delta)
        {
            var md = iso.Owner;
            var verts = md.Vertices;
            var tris = md.Triangles;
            var ltw = md.LocalToWorld;
            double area = 0;
            foreach (var t in iso.Triangles)
            {
                int i0 = tris[t * 3], i1 = tris[t * 3 + 1], i2 = tris[t * 3 + 2];
                Vector3 p0 = ltw.MultiplyPoint3x4(delta != null ? verts[i0] + delta[i0] : verts[i0]);
                Vector3 p1 = ltw.MultiplyPoint3x4(delta != null ? verts[i1] + delta[i1] : verts[i1]);
                Vector3 p2 = ltw.MultiplyPoint3x4(delta != null ? verts[i2] + delta[i2] : verts[i2]);
                area += Vector3.Cross(p1 - p0, p2 - p0).magnitude * 0.5;
            }
            return (float)area;
        }

        /// <summary>
        /// Merge overlapping islands within the same texture/group. Two islands merge if any triangle of one
        /// overlaps any triangle of the other in UV space. / 合并同贴图/同组内重叠的岛：任一三角形在 UV 空间相交即合并。
        /// </summary>
        public static void MergeOverlaps(List<Island> islands)
        {
            if (islands.Count < 2) return;

            bool changed = true;
            int guard = 0;
            while (changed && guard++ < 32)
            {
                changed = false;
                for (int i = 0; i < islands.Count && !changed; i++)
                {
                    var a = islands[i];
                    if (a == null || a.Triangles.Count == 0) continue;
                    for (int j = i + 1; j < islands.Count; j++)
                    {
                        var b = islands[j];
                        if (b == null || b.Triangles.Count == 0) continue;
                        if (!RectsOverlap(a, b)) continue;
                        if (IslandsOverlap(a, b))
                        {
                            a.Triangles.AddRange(b.Triangles);
                            b.Triangles.Clear();
                            changed = true;
                            break;
                        }
                    }
                }
                if (changed)
                {
                    islands.RemoveAll(x => x == null || x.Triangles.Count == 0);
                    for (int i = 0; i < islands.Count; i++) ComputeIslandMetrics(islands[i]);
                }
            }
        }

        private static bool RectsOverlap(Island a, Island b)
        {
            return a.Min.x <= b.Max.x && b.Min.x <= a.Max.x && a.Min.y <= b.Max.y && b.Min.y <= a.Max.y;
        }

        private static bool IslandsOverlap(Island a, Island b)
        {
            for (int pass = 0; pass < 2; pass++)
            {
                var outer = pass == 0 ? a : b;
                var inner = pass == 0 ? b : a;
                int maxTris = Mathf.Min(outer.Triangles.Count, 256);
                for (int s = 0; s < maxTris; s++)
                {
                    int t = outer.Triangles[s];
                    // centroid + edge midpoints / 重心 + 三边中点
                    var (p0, p1, p2) = TriUvs(outer, t);
                    if (PointInAnyTriangle(inner, (p0 + p1 + p2) / 3f)) return true;
                    if (PointInAnyTriangle(inner, (p0 + p1) * 0.5f)) return true;
                    if (PointInAnyTriangle(inner, (p1 + p2) * 0.5f)) return true;
                    if (PointInAnyTriangle(inner, (p2 + p0) * 0.5f)) return true;
                }
            }
            return false;
        }

        private static (Vector2, Vector2, Vector2) TriUvs(Island iso, int tri)
        {
            var md = iso.Owner;
            var tris = md.Triangles;
            var uv = md.Uv;
            return (uv[tris[tri * 3]], uv[tris[tri * 3 + 1]], uv[tris[tri * 3 + 2]]);
        }

        private static bool PointInAnyTriangle(Island iso, Vector2 p)
        {
            int maxTris = Mathf.Min(iso.Triangles.Count, 1024);
            for (int s = 0; s < maxTris; s++)
            {
                var (a, b, c) = TriUvs(iso, iso.Triangles[s]);
                if (PointInTriangle(p, a, b, c)) return true;
            }
            return false;
        }

        private static bool PointInTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float d1 = Cross(p, a, b), d2 = Cross(p, b, c), d3 = Cross(p, c, a);
            bool hasNeg = d1 < 0 || d2 < 0 || d3 < 0;
            bool hasPos = d1 > 0 || d2 > 0 || d3 > 0;
            return !(hasNeg && hasPos);
        }

        private static float Cross(Vector2 p, Vector2 a, Vector2 b)
        {
            return (b.x - a.x) * (p.y - a.y) - (b.y - a.y) * (p.x - a.x);
        }
    }
}
