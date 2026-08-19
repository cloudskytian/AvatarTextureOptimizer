// IslandExtractor.cs
// UV island segmentation: union-find over quantized UV positions, per-island integer
// normalization, overlap merging, world-area estimation with blendshape/scale maxima.
// UV 岛分割:量化 UV 位置并查集、逐岛整数归一化、重叠合并、含形态键/缩放最大值的世界面积估算。
// Copyright (c) 2026 fosa. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace net.fosa.ato
{
    internal static class IslandExtractor
    {
        private const float Quant = 1e-5f; // UV position quantization / UV 位置量化

        internal sealed class ExtractResult
        {
            internal List<UvIsland> Islands = new List<UvIsland>();
            internal bool Normalized;
            internal Vector2 NormalizeOffset;
            internal bool Unusable;
            internal string UnusableReason;
            /// <summary>UV array after per-island normalization (mesh-vertex indexed). / 逐岛归一化后的 UV 数组(按网格顶点索引)。</summary>
            internal Vector2[] NormalizedUvs;
        }

        internal static ExtractResult Extract(Mesh mesh, int submesh, int channel, RendererRecord rec, AnimationDatabase anims)
        {
            var result = new ExtractResult();

            var uvsList = new List<Vector2>();
            mesh.GetUVs(channel, uvsList);
            if (uvsList.Count == 0) { result.Unusable = true; result.UnusableReason = $"UV{channel} empty"; return result; }
            var uvs = uvsList.ToArray();

            var tris = mesh.GetTriangles(submesh);
            if (tris.Length == 0) { result.Unusable = true; result.UnusableReason = "submesh empty"; return result; }

            // ---------------- Union-find over quantized UV positions / 量化 UV 位置并查集 ----------------
            var posToId = new Dictionary<long, int>(tris.Length);
            var uf = new AtoUnionFind();

            for (int t = 0; t < tris.Length; t += 3)
            {
                int a = PosId(uf, posToId, uvs[tris[t]]);
                int b = PosId(uf, posToId, uvs[tris[t + 1]]);
                int c = PosId(uf, posToId, uvs[tris[t + 2]]);
                uf.Union(a, b); uf.Union(b, c);
            }

            // ---------------- Group vertices & triangles / 分组顶点与三角形 ----------------
            var islandOfVertex = new int[uvs.Length];
            for (int v = 0; v < uvs.Length; v++) islandOfVertex[v] = -1;
            var islands = new Dictionary<int, UvIsland>();
            for (int t = 0; t < tris.Length; t += 3)
            {
                int root = uf.Find(posToId[QuantKey(uvs[tris[t]])]);
                if (!islands.TryGetValue(root, out var isl))
                {
                    islands[root] = isl = new UvIsland { Id = islands.Count };
                }
                isl.Triangles.Add(tris[t]); isl.Triangles.Add(tris[t + 1]); isl.Triangles.Add(tris[t + 2]);
            }
            for (int v = 0; v < uvs.Length; v++)
            {
                if (!posToId.TryGetValue(QuantKey(uvs[v]), out int id)) continue;
                var root = uf.Find(id);
                if (islands.TryGetValue(root, out var isl))
                {
                    islandOfVertex[v] = isl.Id;
                }
            }

            // ---------------- Per-island normalization & validity / 逐岛归一化与有效性 ----------------
            var byId = new List<UvIsland>();
            foreach (var kv in islands) byId.Add(kv.Value);
            foreach (var isl in byId)
            {
                // bounds from triangles' vertices / 由三角形顶点求包围盒
                float minx = float.MaxValue, miny = float.MaxValue, maxx = float.MinValue, maxy = float.MinValue;
                foreach (var vi in isl.Triangles)
                {
                    var uv = uvs[vi];
                    if (uv.x < minx) minx = uv.x; if (uv.x > maxx) maxx = uv.x;
                    if (uv.y < miny) miny = uv.y; if (uv.y > maxy) maxy = uv.y;
                }
                // Island spans > 1 tile → relies on repeat inside itself → unusable. / 岛自身跨块→依赖 repeat→不可用
                if (maxx - minx > 1f + 1e-4f || maxy - miny > 1f + 1e-4f)
                {
                    result.Unusable = true;
                    result.UnusableReason = $"island spans >1 tile (wrap sampling), uv range ({maxx - minx:F2},{maxy - miny:F2})";
                    return result;
                }
                // Integer translate into [0,1] / 整数平移归一到 [0,1]
                float ox = Mathf.Floor(minx + 1e-4f), oy = Mathf.Floor(miny + 1e-4f);
                if (ox != 0f || oy != 0f)
                {
                    for (int i = 0; i < isl.Triangles.Count; i++)
                        uvs[isl.Triangles[i]] = new Vector2(uvs[isl.Triangles[i]].x - ox, uvs[isl.Triangles[i]].y - oy);
                    minx -= ox; maxx -= ox; miny -= oy; maxy -= oy;
                    result.Normalized = true;
                }
                isl.UvBounds = Rect.MinMaxRect(minx, miny, maxx, maxy);
            }

            // ---------------- Overlap merge (conservative bbox) / 重叠合并(保守包围盒) ----------------
            MergeOverlapping(byId);

            // ---------------- Vertices per island / 每岛顶点集合 ----------------
            foreach (var isl in byId)
                isl.Vertices = new int[0]; // filled after merge by pass below / 合并后统一填充
            {
                var vertsByIsl = new Dictionary<int, List<int>>();
                for (int v = 0; v < uvs.Length; v++)
                {
                    int islId = islandOfVertex[v];
                    if (islId < 0) continue;
                    // island may have been merged; find representative / 岛可能已合并;找代表
                    int rep = _mergeMap.TryGetValue(islId, out var r) ? r : islId;
                    if (!vertsByIsl.TryGetValue(rep, out var list)) vertsByIsl[rep] = list = new List<int>();
                    list.Add(v);
                }
                foreach (var kv in vertsByIsl)
                {
                    var isl = byId.Find(i => i.Id == kv.Key);
                    if (isl != null) isl.Vertices = kv.Value.ToArray();
                }
            }

            // ---------------- World area (blendshape max × scale max) / 世界面积(形态键最大×缩放最大) ----------------
            ComputeWorldAreas(mesh, byId, rec);
            foreach (var isl in byId) isl.WorldArea *= rec.MaxScaleFactor;

            // Reassign ids 0..n-1 / 重排编号
            var final = new List<UvIsland>();
            foreach (var isl in byId)
                if (isl.Vertices != null && isl.Vertices.Length > 0) final.Add(isl);
            for (int i = 0; i < final.Count; i++) final[i].Id = i;
            result.Islands = final;
            result.NormalizedUvs = uvs;

            ATOLog.V($"island extract: mesh '{mesh.name}' sub{submesh} uv{channel}: {final.Count} islands");
            return result;
        }

        private static Dictionary<int, int> _mergeMap = new Dictionary<int, int>();

        private static void MergeOverlapping(List<UvIsland> islands)
        {
            _mergeMap.Clear();
            var boxes = new Rect[islands.Count];
            for (int i = 0; i < islands.Count; i++) boxes[i] = islands[i].UvBounds;

            // union-find over island ids / 岛 id 并查集
            var uf = new AtoUnionFind();

            // Sort by minx for sweep / 按 minx 扫描
            var order = new List<int>();
            for (int i = 0; i < islands.Count; i++) order.Add(i);
            order.Sort((a, b) => boxes[a].xMin.CompareTo(boxes[b].xMin));
            for (int ii = 0; ii < order.Count; ii++)
            {
                var A = boxes[order[ii]];
                for (int jj = ii + 1; jj < order.Count; jj++)
                {
                    var j = order[jj];
                    if (boxes[j].xMin > A.xMax) break; // sorted by xMin / 已按 xMin 排序
                    var B = boxes[j];
                    if (A.Overlaps(B)) uf.Union(order[ii], j);
                }
            }

            // Merge groups / 合并组
            var groups = new Dictionary<int, List<UvIsland>>();
            for (int i = 0; i < islands.Count; i++)
            {
                int root = uf.Find(i);
                _mergeMap[islands[i].Id] = root;
                if (!groups.TryGetValue(root, out var g)) groups[root] = g = new List<UvIsland>();
                g.Add(islands[i]);
            }
            foreach (var g in groups.Values)
            {
                if (g.Count <= 1) continue;
                var merged = g[0];
                for (int i = 1; i < g.Count; i++)
                {
                    merged.Triangles.AddRange(g[i].Triangles);
                    merged.UvBounds = UnionRect(merged.UvBounds, g[i].UvBounds);
                    g[i].Triangles = null; // mark removed / 标记移除
                }
            }
            islands.RemoveAll(i => i.Triangles == null);
        }

        private static Rect UnionRect(Rect a, Rect b) => Rect.MinMaxRect(
            Mathf.Min(a.xMin, b.xMin), Mathf.Min(a.yMin, b.yMin),
            Mathf.Max(a.xMax, b.xMax), Mathf.Max(a.yMax, b.yMax));

        private static int PosId(AtoUnionFind uf, Dictionary<long, int> posToId, Vector2 uv)
        {
            var k = QuantKey(uv);
            if (!posToId.TryGetValue(k, out int id))
            {
                id = uf.Add();
                posToId[k] = id;
            }
            return id;
        }

        private static long QuantKey(Vector2 uv)
        {
            long x = (long)Mathf.Round(uv.x / Quant);
            long y = (long)Mathf.Round(uv.y / Quant);
            return (x << 32) ^ (y & 0xFFFFFFFF);
        }

        // ------------------------------------------------------------------ //
        // World area / 世界面积
        // ------------------------------------------------------------------ //
        private static void ComputeWorldAreas(Mesh mesh, List<UvIsland> islands, RendererRecord rec)
        {
            var baseVerts = mesh.vertices;
            var l2w = rec.Renderer.transform.localToWorldMatrix;

            // Base pose areas / 基础姿态面积
            AccumulateAreas(islands, baseVerts, l2w, weight: 1f);

            // Blendshape maxima: shape@100 vs base, per shape (no combinations). / 形态键最大值:逐形态键 100 与 0 取大(不做组合)。
            if (rec.Renderer is SkinnedMeshRenderer && mesh.blendShapeCount > 0)
            {
                var deltas = new Vector3[baseVerts.Length];
                var scratch = new Vector3[baseVerts.Length];
                for (int s = 0; s < mesh.blendShapeCount; s++)
                {
                    int lastFrame = mesh.GetBlendShapeFrameCount(s) - 1;
                    if (lastFrame < 0) continue;
                    mesh.GetBlendShapeFrameVertices(s, lastFrame, deltas, null, null);
                    bool moved = false;
                    for (int i = 0; i < scratch.Length; i++)
                    {
                        scratch[i] = baseVerts[i] + deltas[i];
                        if (deltas[i].sqrMagnitude > 1e-12f) moved = true;
                    }
                    if (!moved) continue;
                    AccumulateAreas(islands, scratch, l2w, weight: -1f); // measure only / 仅测量
                    // replace area if larger / 更大面积
                    for (int i = 0; i < islands.Count; i++)
                    {
                        float a = _scratchAreas[i];
                        if (a > islands[i].WorldArea) islands[i].WorldArea = a;
                    }
                }
            }
        }

        private static float[] _scratchAreas = new float[0];

        private static void AccumulateAreas(List<UvIsland> islands, Vector3[] verts, Matrix4x4 l2w, float weight)
        {
            if (_scratchAreas == null || _scratchAreas.Length < islands.Count)
                _scratchAreas = new float[islands.Count];
            for (int i = 0; i < islands.Count; i++) _scratchAreas[i] = 0f;
            for (int i = 0; i < islands.Count; i++)
            {
                var tris = islands[i].Triangles;
                double area = 0;
                for (int t = 0; t < tris.Count; t += 3)
                {
                    var pa = l2w.MultiplyPoint3x4(verts[tris[t]]);
                    var pb = l2w.MultiplyPoint3x4(verts[tris[t + 1]]);
                    var pc = l2w.MultiplyPoint3x4(verts[tris[t + 2]]);
                    area += TriangleArea(pa, pb, pc);
                }
                _scratchAreas[i] = (float)area;
                if (weight > 0) islands[i].WorldArea = (float)area;
            }
        }

        private static float TriangleArea(Vector3 a, Vector3 b, Vector3 c) =>
            Vector3.Cross(b - a, c - a).magnitude * 0.5f;
    }
}
