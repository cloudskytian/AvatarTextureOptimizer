using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Extracts UV islands from a mesh+channel: UV-space connected components with quantized
    /// vertices, integer-tile translation into [0,1] (wrap-crossing islands are reported so the
    /// caller can whitelist them), identical-shape island merging, and world-area estimation with
    /// blendshape(0/100 max) and animation-scale factors. / 提取 UV 岛：量化顶点连通分量、
    /// 整数平移归一到[0,1]（跨缝岛上报白名单）、同形岛合并、形态键0/100与动画缩放的最大面积估算。
    /// </summary>
    internal static class UVIslandExtractor
    {
        private const float QuantEps = 1e-5f; // UV quantization epsilon / 量化精度

        internal class Extraction
        {
            internal UvGroup Group;
            /// <summary>Groups that must be whitelisted due to unresolvable UVs. / 需白名单的跨缝等异常。</summary>
            internal readonly List<(UvIsland island, string reason)> ProblemIslands =
                new List<(UvIsland, string)>();
        }

        internal static Extraction Extract(Mesh mesh, int channel, RendererInfo primary, float areaFactor)
        {
            var group = new UvGroup { mesh = mesh, channel = channel, primaryRenderer = primary, areaFactor = areaFactor };
            var ex = new Extraction { Group = group };

            var uvs = new List<Vector2>();
            mesh.GetUVs(channel, uvs);
            if (uvs.Count == 0)
            {
                group.atlasEligible = false;
                group.ineligibleReason = "no UV channel data / 无UV数据";
                return ex;
            }

            var verts = new List<Vector3>();
            mesh.GetVertices(verts);

            // Flat triangle list with submesh ids. / 展开三角形并记录子网格。
            var tris = new List<int>();
            var triSubmesh = new List<int>();
            for (int sub = 0; sub < mesh.subMeshCount; sub++)
            {
                var idx = mesh.GetIndices(sub, false);
                for (int i = 0; i < idx.Length; i += 3)
                {
                    tris.Add(idx[i]); tris.Add(idx[i + 1]); tris.Add(idx[i + 2]);
                    triSubmesh.Add(sub);
                }
            }

            // ---- union-find over quantized UV positions / 量化UV位置并查集 ----
            var posIndex = new Dictionary<long, int>();
            var dsu = new int[tris.Count]; // per corner
            for (int i = 0; i < dsu.Length; i++) dsu[i] = i;
            int[] rank = new int[tris.Count];

            int Find(int x)
            {
                while (dsu[x] != x) { dsu[x] = dsu[dsu[x]]; x = dsu[x]; }
                return x;
            }
            void Union(int a, int b)
            {
                a = Find(a); b = Find(b);
                if (a == b) return;
                if (rank[a] < rank[b]) (a, b) = (b, a);
                dsu[b] = a;
                if (rank[a] == rank[b]) rank[a]++;
            }

            bool PackUv(Vector2 uv, out long key)
            {
                float qu = uv.x / QuantEps, qv = uv.y / QuantEps;
                if (float.IsInfinity(qu) || float.IsNaN(qu) || Math.Abs(qu) > 1_000_000f || Math.Abs(qv) > 1_000_000f)
                {
                    key = 0;
                    return false;
                }
                long iu = (long)Mathf.Round(qu), iv = (long)Mathf.Round(qv);
                key = (iu << 22) + iv; // ±4M range fits / 范围内可打包
                return true;
            }

            for (int i = 0; i < tris.Count; i++)
            {
                if (!PackUv(uvs[tris[i]], out var k)) { /* degenerate uv / 退化UV */ }
                if (posIndex.TryGetValue(k, out var other)) Union(i, other);
                else posIndex[k] = i;
            }

            // ---- collect islands by root / 按根节点收集岛 ----
            var byRoot = new Dictionary<int, UvIsland>();
            for (int t = 0; t < tris.Count / 3; t++)
            {
                int r = Find(t * 3);
                if (!byRoot.TryGetValue(r, out var island))
                {
                    island = new UvIsland { id = byRoot.Count, Group = group };
                    byRoot[r] = island;
                }
                island.triangles.Add(tris[t * 3]);
                island.triangles.Add(tris[t * 3 + 1]);
                island.triangles.Add(tris[t * 3 + 2]);
            }

            // ---- bounds, normalization, world area / 包围盒、归一化、世界面积 ----
            var shapeGroups = new Dictionary<ulong, UvIsland>();
            foreach (var island in byRoot.Values)
            {
                if (!NormalizeAndBound(island, uvs, out var reason))
                {
                    ex.ProblemIslands.Add((island, reason));
                    continue;
                }

                island.worldArea = ComputeWorldArea(island, mesh, verts, primary) * Mathf.Max(areaFactor, 1e-6f);
                island.shapeHash = ComputeShapeHash(island, uvs);

                // identical-shape merge / 同形岛合并
                if (shapeGroups.TryGetValue(island.shapeHash, out var master))
                {
                    master.mergedIslands.Add(island);
                    master.worldArea = Mathf.Max(master.worldArea, island.worldArea);
                }
                else
                {
                    shapeGroups[island.shapeHash] = island;
                    group.islands.Add(island);
                }
            }

            if (ex.ProblemIslands.Count > 0)
            {
                group.atlasEligible = false;
                group.ineligibleReason = ex.ProblemIslands[0].reason;
            }

            ATOLog.Verbose($"UV islands: mesh '{mesh.name}' ch{channel}: {group.islands.Count} layout islands, " +
                           $"{ex.ProblemIslands.Count} problem islands");
            return ex;
        }

        /// <summary>
        /// Translate island into [0,1] when it fits inside one wrap tile; false when it crosses a
        /// wrap seam (needs repeat sampling → whitelist). / 岛可整体平移进[0,1]则归一；跨缝返回false。
        /// </summary>
        private static bool NormalizeAndBound(UvIsland island, List<Vector2> uvs, out string reason)
        {
            reason = null;
            float minU = float.MaxValue, minV = float.MaxValue, maxU = float.MinValue, maxV = float.MinValue;
            foreach (var vi in island.triangles)
            {
                var uv = uvs[vi];
                minU = Mathf.Min(minU, uv.x); maxU = Mathf.Max(maxU, uv.x);
                minV = Mathf.Min(minV, uv.y); maxV = Mathf.Max(maxV, uv.y);
            }

            const float eps = 1e-4f;
            float w = maxU - minU, h = maxV - minV;
            if (w > 1f + eps || h > 1f + eps)
            {
                reason = $"island crosses wrap seam (bbox {w:F2}x{h:F2}) / 岛跨wrap缝";
                return false;
            }

            // Shift by integer tiles so bounds fall into [0,1]. / 整数平移入[0,1]。
            float tu = -Mathf.Floor(minU), tv = -Mathf.Floor(minV);
            // if min is inside [0,1) already, floor(min)>=0 → shift 0 / 已在[0,1)则不平移
            island.uvOffset = new Vector2(tu, tv);
            island.uvBounds = new Rect(minU + tu, minV + tv, w, h);
            // UV exactly at 1.0 (single tile edge) still fine for bounds / 上界=1时仍视为在[0,1]内
            return true;
        }

        private static float ComputeWorldArea(UvIsland island, Mesh mesh, List<Vector3> verts, RendererInfo primary)
        {
            // Base triangle areas, then each blendshape at its max frame alone (0/100 max, no
            // combinations to avoid combinatorial explosion). / 基础面积 + 每个形态键单独最大帧面积。
            float area = SumTriangleArea(island.triangles, verts);

            var smr = primary?.smr;
            if (smr != null && mesh.blendShapeCount > 0)
            {
                var baseVerts = new Vector3[verts.Count];
                for (int i = 0; i < verts.Count; i++) baseVerts[i] = verts[i];
                var deltas = new Vector3[verts.Count];
                var tmpD = new Vector3[verts.Count];
                var tmpN = new Vector3[verts.Count];
                var tmpT = new Vector3[verts.Count];

                for (int s = 0; s < mesh.blendShapeCount; s++)
                {
                    int frame = mesh.GetBlendShapeFrameCount(s) - 1; // highest weight frame / 最大权重帧
                    mesh.GetBlendShapeFrameVertices(s, frame, tmpD, tmpN, tmpT);
                    float maxW = mesh.GetBlendShapeFrameWeight(s, frame);
                    if (maxW <= 0f) continue;
                    float scale = Mathf.Clamp01(100f / maxW); // normalize to 100 / 归一到100

                    for (int i = 0; i < verts.Count; i++) deltas[i] = baseVerts[i] + tmpD[i] * scale;
                    float shapeArea = SumTriangleArea(island.triangles, deltas);
                    area = Mathf.Max(area, shapeArea);
                }
            }

            return area;
        }

        private static float SumTriangleArea(List<int> tris, List<Vector3> verts)
        {
            float area = 0f;
            for (int i = 0; i < tris.Count; i += 3)
            {
                var a = verts[tris[i]];
                var b = verts[tris[i + 1]];
                var c = verts[tris[i + 2]];
                area += Vector3.Cross(b - a, c - a).magnitude * 0.5f;
            }
            return area;
        }

        /// <summary>Hash of the island's quantized triangle shape (translation-invariant). / 平移不变的量化形状哈希。</summary>
        private static ulong ComputeShapeHash(UvIsland island, List<Vector2> uvs)
        {
            ulong hash = 1469598103934665603UL;
            var points = new List<(long, long, long, long, long, long)>();

            float minU = float.MaxValue, minV = float.MaxValue;
            foreach (var vi in island.triangles)
            {
                minU = Mathf.Min(minU, uvs[vi].x);
                minV = Mathf.Min(minV, uvs[vi].y);
            }

            for (int i = 0; i < island.triangles.Count; i += 3)
            {
                long q(int vi, int axis)
                {
                    float v = axis == 0 ? uvs[vi].x - minU : uvs[vi].y - minV;
                    return (long)Mathf.Round(v / QuantEps);
                }
                points.Add((q(island.triangles[i], 0), q(island.triangles[i], 1),
                    q(island.triangles[i + 1], 0), q(island.triangles[i + 1], 1),
                    q(island.triangles[i + 2], 0), q(island.triangles[i + 2], 1)));
            }

            foreach (var p in points.OrderBy(x => x, Comparer<(long, long, long, long, long, long)>.Default))
            {
                hash = (hash ^ (ulong)p.Item1) * 1099511628211UL;
                hash = (hash ^ (ulong)p.Item2) * 1099511628211UL;
                hash = (hash ^ (ulong)p.Item3) * 1099511628211UL;
                hash = (hash ^ (ulong)p.Item4) * 1099511628211UL;
                hash = (hash ^ (ulong)p.Item5) * 1099511628211UL;
                hash = (hash ^ (ulong)p.Item6) * 1099511628211UL;
            }

            hash = (hash ^ (ulong)points.Count) * 1099511628211UL;
            return hash;
        }
    }
}
