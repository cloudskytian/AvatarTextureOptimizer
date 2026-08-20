using System;
using System.Collections.Generic;
using UnityEngine;
using Net.Fosa.AvatarTextureOptimizer;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Builds UV islands per submesh / UV channel, merges overlaps, normalizes translatable out-of-range UVs.
    /// 按子网格/UV 通道构建岛，合并重叠，并把可整体平移的越界 UV 归一到 [0,1]。
    /// </summary>
    public static class AtoIslandExtractor
    {
        private const float Eps = 1e-5f;

        public struct ExtractResult
        {
            public List<AtoIsland> Islands;
            public bool WrapCross;
            public Vector2 Translate;
        }

        public static ExtractResult Extract(Mesh mesh, int submesh, int uvChannel, int texW, int texH,
            float worldAreaScale, BlendShapeArea blend)
        {
            var r = new ExtractResult { Islands = new List<AtoIsland>() };
            if (mesh == null || submesh < 0 || submesh >= mesh.subMeshCount) return r;

            var uvs = new List<Vector2>();
            mesh.GetUVs(uvChannel, uvs);
            if (uvs == null || uvs.Count == 0) return r;

            var tris = mesh.GetTriangles(submesh);
            if (tris == null || tris.Length < 3) return r;

            var verts = mesh.vertices;
            ApplyBlendMax(mesh, verts, blend);

            var triCount = tris.Length / 3;
            var parent = new int[triCount];
            for (var i = 0; i < triCount; i++) parent[i] = i;
            int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
            void Union(int a, int b) { a = Find(a); b = Find(b); if (a != b) parent[b] = a; }

            var edge = new Dictionary<long, int>();
            long Key(int a, int b) => a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;

            for (var t = 0; t < triCount; t++)
            {
                var i0 = tris[t * 3];
                var i1 = tris[t * 3 + 1];
                var i2 = tris[t * 3 + 2];
                TryEdge(i0, i1, t);
                TryEdge(i1, i2, t);
                TryEdge(i2, i0, t);
            }

            void TryEdge(int a, int b, int tri)
            {
                var k = Key(a, b);
                if (edge.TryGetValue(k, out var other))
                {
                    if (UvSame(uvs[a], uvs[a]) && SameUvEdge(uvs, a, b, other, tris))
                        Union(tri, other);
                }
                else edge[k] = tri;
            }

            // Rebuild edge union more carefully: two triangles share a UV-space edge if they share two UV positions.
            // 更严谨：两三角若共享两条 UV 位置则合并。
            var uvEdge = new Dictionary<(UvQ, UvQ), List<int>>();
            for (var t = 0; t < triCount; t++)
            {
                AddUvEdge(t, tris[t * 3], tris[t * 3 + 1]);
                AddUvEdge(t, tris[t * 3 + 1], tris[t * 3 + 2]);
                AddUvEdge(t, tris[t * 3 + 2], tris[t * 3]);
            }

            void AddUvEdge(int tri, int a, int b)
            {
                var qa = Quant(uvs[a]);
                var qb = Quant(uvs[b]);
                var key = qa.GetHashCode() < qb.GetHashCode() || (qa.GetHashCode() == qb.GetHashCode() && qa.X < qb.X)
                    ? (qa, qb) : (qb, qa);
                if (!uvEdge.TryGetValue(key, out var list))
                    uvEdge[key] = list = new List<int>();
                foreach (var o in list) Union(tri, o);
                list.Add(tri);
            }

            var groups = new Dictionary<int, List<int>>();
            for (var t = 0; t < triCount; t++)
            {
                var f = Find(t);
                if (!groups.TryGetValue(f, out var list)) groups[f] = list = new List<int>();
                list.Add(t);
            }

            foreach (var g in groups.Values)
            {
                var island = new AtoIsland { Triangles = g };
                float minU = float.MaxValue, minV = float.MaxValue, maxU = float.MinValue, maxV = float.MinValue;
                var area = 0f;
                foreach (var t in g)
                {
                    var i0 = tris[t * 3]; var i1 = tris[t * 3 + 1]; var i2 = tris[t * 3 + 2];
                    Accumulate(uvs[i0]); Accumulate(uvs[i1]); Accumulate(uvs[i2]);
                    area += TriangleArea(verts[i0], verts[i1], verts[i2]);
                }
                void Accumulate(Vector2 uv)
                {
                    if (uv.x < minU) minU = uv.x; if (uv.y < minV) minV = uv.y;
                    if (uv.x > maxU) maxU = uv.x; if (uv.y > maxV) maxV = uv.y;
                }
                island.UvRect = Rect.MinMaxRect(minU, minV, maxU, maxV);
                island.WorldArea = area * worldAreaScale;
                island.OrigW = texW;
                island.OrigH = texH;
                r.Islands.Add(island);
            }

            // Wrap / normalize. / 越界与跨缝。
            float gminU = float.MaxValue, gminV = float.MaxValue, gmaxU = float.MinValue, gmaxV = float.MinValue;
            foreach (var isl in r.Islands)
            {
                gminU = Mathf.Min(gminU, isl.UvRect.xMin);
                gminV = Mathf.Min(gminV, isl.UvRect.yMin);
                gmaxU = Mathf.Max(gmaxU, isl.UvRect.xMax);
                gmaxV = Mathf.Max(gmaxV, isl.UvRect.yMax);
            }

            var spanU = gmaxU - gminU;
            var spanV = gmaxV - gminV;
            if (spanU > 1f + 1e-3f || spanV > 1f + 1e-3f)
            {
                // Any island wider than 1 uses repeat. / 单岛宽度超过 1 依赖 Repeat。
                r.WrapCross = true;
            }
            else
            {
                // Translate so min goes into [0,1) if the whole set fits.
                // 若整体能装进单位方，则平移到 [0,1)。
                var tU = 0f; var tV = 0f;
                if (gminU < -1e-4f || gmaxU > 1f + 1e-4f) tU = -Mathf.Floor(gminU);
                if (gminV < -1e-4f || gmaxV > 1f + 1e-4f) tV = -Mathf.Floor(gminV);
                r.Translate = new Vector2(tU, tV);
                foreach (var isl in r.Islands)
                {
                    isl.UvTranslate = r.Translate;
                    var rr = isl.UvRect;
                    isl.UvRect = new Rect(rr.x + tU, rr.y + tV, rr.width, rr.height);
                    if (isl.UvRect.xMin < -1e-3f || isl.UvRect.yMin < -1e-3f ||
                        isl.UvRect.xMax > 1f + 1e-3f || isl.UvRect.yMax > 1f + 1e-3f)
                        r.WrapCross = true;
                }
            }

            MergeOverlaps(r.Islands);
            return r;
        }

        private static void MergeOverlaps(List<AtoIsland> islands)
        {
            var changed = true;
            while (changed)
            {
                changed = false;
                for (var i = 0; i < islands.Count; i++)
                for (var j = i + 1; j < islands.Count; j++)
                {
                    if (!islands[i].UvRect.Overlaps(islands[j].UvRect, true)) continue;
                    islands[i].Triangles.AddRange(islands[j].Triangles);
                    islands[i].UvRect = UnionRect(islands[i].UvRect, islands[j].UvRect);
                    islands[i].WorldArea += islands[j].WorldArea;
                    islands.RemoveAt(j);
                    changed = true;
                    break;
                }
                if (changed) break;
            }
            if (changed) MergeOverlaps(islands);
        }

        private static Rect UnionRect(Rect a, Rect b)
        {
            var x0 = Mathf.Min(a.xMin, b.xMin);
            var y0 = Mathf.Min(a.yMin, b.yMin);
            var x1 = Mathf.Max(a.xMax, b.xMax);
            var y1 = Mathf.Max(a.yMax, b.yMax);
            return Rect.MinMaxRect(x0, y0, x1, y1);
        }

        private static bool UvSame(Vector2 a, Vector2 b) =>
            Mathf.Abs(a.x - b.x) < Eps && Mathf.Abs(a.y - b.y) < Eps;

        private static bool SameUvEdge(List<Vector2> uvs, int a, int b, int otherTri, int[] tris)
        {
            return true;
        }

        private static UvQ Quant(Vector2 uv) =>
            new UvQ { X = (int)Math.Round(uv.x * 1024f * 16f), Y = (int)Math.Round(uv.y * 1024f * 16f) };

        private struct UvQ : IEquatable<UvQ>
        {
            public int X, Y;
            public bool Equals(UvQ other) => X == other.X && Y == other.Y;
            public override bool Equals(object obj) => obj is UvQ o && Equals(o);
            public override int GetHashCode() => HashCode.Combine(X, Y);
        }

        private static float TriangleArea(Vector3 a, Vector3 b, Vector3 c) =>
            Vector3.Cross(b - a, c - a).magnitude * 0.5f;

        public struct BlendShapeArea
        {
            public bool Any;
            public Vector3[] MaxDelta;
        }

        public static BlendShapeArea BuildBlendMax(Mesh mesh)
        {
            var r = new BlendShapeArea();
            if (mesh == null || mesh.blendShapeCount == 0) return r;
            var vcount = mesh.vertexCount;
            var acc = new Vector3[vcount];
            var dV = new Vector3[vcount];
            var dN = new Vector3[vcount];
            var dT = new Vector3[vcount];
            for (var s = 0; s < mesh.blendShapeCount; s++)
            {
                var frames = mesh.GetBlendShapeFrameCount(s);
                if (frames <= 0) continue;
                // Only weight 0 (zero delta) and 100 (last frame if weight==100, else interpolate).
                // 只取 0 与 100。
                var lastW = mesh.GetBlendShapeFrameWeight(s, frames - 1);
                mesh.GetBlendShapeFrameVertices(s, frames - 1, dV, dN, dT);
                var scale = lastW <= 1e-3f ? 0f : 100f / lastW;
                for (var i = 0; i < vcount; i++)
                {
                    var d = dV[i] * scale;
                    // Max of 0 and 100 per-axis abs accumulation of magnitude: take larger displacement vector length per vertex.
                    // 每顶点在 0 与 100 之间取位移更大者（不对形态键做排列组合）。
                    if (d.sqrMagnitude > acc[i].sqrMagnitude) acc[i] = d;
                }
            }
            r.Any = true;
            r.MaxDelta = acc;
            return r;
        }

        private static void ApplyBlendMax(Mesh mesh, Vector3[] verts, BlendShapeArea blend)
        {
            if (!blend.Any || blend.MaxDelta == null) return;
            var n = Math.Min(verts.Length, blend.MaxDelta.Length);
            for (var i = 0; i < n; i++) verts[i] += blend.MaxDelta[i];
        }
    }
}
