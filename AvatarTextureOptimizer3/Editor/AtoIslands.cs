// English: Extract UV islands, merge overlaps, detect wrap-cross, normalize translatable UVs.
// 中文：提取 UV 岛、合并重叠、检测跨缝、可整体平移的越界 UV 归一化。
using System;
using System.Collections.Generic;
using UnityEngine;

namespace net.fosa.ato.editor
{
    public static class AtoIslands
    {
        public struct ExtractResult
        {
            public List<AtoIsland> Islands;
            public bool CrossesWrap;
            public bool Normalized;
            public Vector2 Translate;
        }

        public static ExtractResult Extract(Mesh mesh, int submesh, int uvChannel, float worldArea)
        {
            var r = new ExtractResult { Islands = new List<AtoIsland>(), Translate = Vector2.zero };
            if (mesh == null || uvChannel < 0 || uvChannel > 7) return r;
            var uvs = new List<Vector2>();
            mesh.GetUVs(uvChannel, uvs);
            if (uvs == null || uvs.Count == 0) return r;

            int[] tris;
            try { tris = mesh.GetTriangles(submesh); }
            catch { return r; }
            if (tris == null || tris.Length < 3) return r;

            // Detect wrap-cross vs globally translatable
            float minU = float.PositiveInfinity, minV = float.PositiveInfinity;
            float maxU = float.NegativeInfinity, maxV = float.NegativeInfinity;
            var used = new bool[uvs.Count];
            for (int i = 0; i < tris.Length; i++) used[tris[i]] = true;
            for (int i = 0; i < uvs.Count; i++)
            {
                if (!used[i]) continue;
                minU = Mathf.Min(minU, uvs[i].x); maxU = Mathf.Max(maxU, uvs[i].x);
                minV = Mathf.Min(minV, uvs[i].y); maxV = Mathf.Max(maxV, uvs[i].y);
            }
            float spanU = maxU - minU, spanV = maxV - minV;
            bool outOf01 = minU < -1e-4f || minV < -1e-4f || maxU > 1f + 1e-4f || maxV > 1f + 1e-4f;
            if (outOf01 && (spanU > 1f + 1e-3f || spanV > 1f + 1e-3f))
            {
                r.CrossesWrap = true;
                return r;
            }
            Vector2 translate = Vector2.zero;
            if (outOf01)
            {
                translate = new Vector2(-Mathf.Floor(minU), -Mathf.Floor(minV));
                for (int i = 0; i < uvs.Count; i++)
                    if (used[i]) uvs[i] += translate;
                r.Normalized = true;
                r.Translate = translate;
            }

            int triCount = tris.Length / 3;
            var parent = new int[triCount];
            for (int i = 0; i < triCount; i++) parent[i] = i;
            int Find(int a) { while (parent[a] != a) { parent[a] = parent[parent[a]]; a = parent[a]; } return a; }
            void Union(int a, int b) { a = Find(a); b = Find(b); if (a != b) parent[b] = a; }

            // Edge map in UV space (quantized)
            var edge = new Dictionary<long, int>();
            long Key(int a, int b)
            {
                int lo = Math.Min(a, b), hi = Math.Max(a, b);
                return ((long)lo << 32) | (uint)hi;
            }
            for (int t = 0; t < triCount; t++)
            {
                int i0 = tris[t * 3], i1 = tris[t * 3 + 1], i2 = tris[t * 3 + 2];
                int[] vs = { i0, i1, i2 };
                for (int e = 0; e < 3; e++)
                {
                    var k = Key(vs[e], vs[(e + 1) % 3]);
                    if (edge.TryGetValue(k, out var ot)) Union(t, ot);
                    else edge[k] = t;
                }
            }

            var groups = new Dictionary<int, List<int>>();
            for (int t = 0; t < triCount; t++)
            {
                int f = Find(t);
                if (!groups.TryGetValue(f, out var list)) { list = new List<int>(); groups[f] = list; }
                list.Add(t);
            }

            int idx = 0;
            foreach (var kv in groups)
            {
                var island = new AtoIsland
                {
                    Mesh = mesh,
                    Submesh = submesh,
                    UvChannel = uvChannel,
                    IslandIndex = idx++,
                    WorldArea = worldArea,
                    Min = new Vector2(float.PositiveInfinity, float.PositiveInfinity),
                    Max = new Vector2(float.NegativeInfinity, float.NegativeInfinity)
                };
                var vset = new HashSet<int>();
                var tlist = new List<int>();
                foreach (var t in kv.Value)
                {
                    tlist.Add(t);
                    for (int k = 0; k < 3; k++)
                    {
                        int vi = tris[t * 3 + k];
                        vset.Add(vi);
                        var uv = uvs[vi];
                        island.Min = Vector2.Min(island.Min, uv);
                        island.Max = Vector2.Max(island.Max, uv);
                    }
                }
                island.Triangles = tlist.ToArray();
                var va = new int[vset.Count];
                vset.CopyTo(va);
                island.Vertices = va;
                var uvtris = new List<Vector2>(tlist.Count * 3);
                foreach (var t in tlist)
                {
                    uvtris.Add(uvs[tris[t * 3]]);
                    uvtris.Add(uvs[tris[t * 3 + 1]]);
                    uvtris.Add(uvs[tris[t * 3 + 2]]);
                }
                island.UvTris = uvtris.ToArray();
                r.Islands.Add(island);
            }

            MergeOverlapping(r.Islands);
            return r;
        }

        private static void MergeOverlapping(List<AtoIsland> islands)
        {
            bool changed = true;
            while (changed)
            {
                changed = false;
                for (int i = 0; i < islands.Count; i++)
                for (int j = i + 1; j < islands.Count; j++)
                {
                    if (!Overlap(islands[i], islands[j])) continue;
                    islands[i] = Merge(islands[i], islands[j]);
                    islands.RemoveAt(j);
                    changed = true;
                    break;
                }
            }
        }

        private static bool Overlap(AtoIsland a, AtoIsland b)
        {
            return a.Min.x < b.Max.x && a.Max.x > b.Min.x && a.Min.y < b.Max.y && a.Max.y > b.Min.y;
        }

        private static AtoIsland Merge(AtoIsland a, AtoIsland b)
        {
            a.Min = Vector2.Min(a.Min, b.Min);
            a.Max = Vector2.Max(a.Max, b.Max);
            var ts = new List<int>(a.Triangles); ts.AddRange(b.Triangles);
            a.Triangles = ts.ToArray();
            var vs = new HashSet<int>(a.Vertices);
            foreach (var v in b.Vertices) vs.Add(v);
            var va = new int[vs.Count]; vs.CopyTo(va); a.Vertices = va;
            var uvs = new List<Vector2>();
            if (a.UvTris != null) uvs.AddRange(a.UvTris);
            if (b.UvTris != null) uvs.AddRange(b.UvTris);
            a.UvTris = uvs.ToArray();
            return a;
        }

        public static float MeshWorldArea(Renderer r, Mesh mesh, int submesh, AtoAnimInfo anim)
        {
            if (mesh == null || r == null) return 0f;
            var verts = mesh.vertices;
            int[] tris;
            try { tris = mesh.GetTriangles(submesh); } catch { return 0f; }
            float area = AreaOf(verts, tris);

            if (mesh is Mesh && r is SkinnedMeshRenderer smr && mesh.blendShapeCount > 0)
            {
                var deltaV = new Vector3[verts.Length];
                var deltaN = new Vector3[verts.Length];
                var deltaT = new Vector3[verts.Length];
                float maxArea = area;
                for (int i = 0; i < mesh.blendShapeCount; i++)
                {
                    int frames = mesh.GetBlendShapeFrameCount(i);
                    if (frames <= 0) continue;
                    // 0 and 100 only
                    mesh.GetBlendShapeFrameVertices(i, frames - 1, deltaV, deltaN, deltaT);
                    var v100 = new Vector3[verts.Length];
                    for (int k = 0; k < verts.Length; k++) v100[k] = verts[k] + deltaV[k];
                    maxArea = Mathf.Max(maxArea, AreaOf(v100, tris));
                }
                area = maxArea;
            }

            float scaleMul = 1f;
            if (anim != null && anim.MaxLossyScaleMul.TryGetValue(r, out var m)) scaleMul = Mathf.Max(1f, m);
            var ls = r.transform.lossyScale;
            float s = Mathf.Max(Mathf.Abs(ls.x), Mathf.Abs(ls.y), Mathf.Abs(ls.z)) * scaleMul;
            return area * s * s;
        }

        private static float AreaOf(Vector3[] v, int[] tris)
        {
            double a = 0;
            for (int i = 0; i + 2 < tris.Length; i += 3)
            {
                var ab = v[tris[i + 1]] - v[tris[i]];
                var ac = v[tris[i + 2]] - v[tris[i]];
                a += Vector3.Cross(ab, ac).magnitude * 0.5f;
            }
            return (float)a;
        }
    }
}
