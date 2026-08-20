using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace net.fosa.ato.editor
{
    /// <summary>EN: Result of analysing one (mesh, submesh, uv channel). ZH: 对一个（网格, 子网格, UV 通道）的分析结果。</summary>
    public sealed class IslandSet
    {
        /// <summary>EN: The islands found. ZH: 找到的岛。</summary>
        public List<UVIsland> Islands = new List<UVIsland>();
        /// <summary>EN: True when the UV layout cannot be safely repacked. ZH: 该 UV 布局是否无法安全重排。</summary>
        public bool Unsafe;
        /// <summary>EN: Reason, for the warning log. ZH: 原因，用于警告日志。</summary>
        public string UnsafeReason;
    }

    /// <summary>
    /// EN: Splits a UV layout into islands, merges overlapping islands, normalises out-of-range UVs and
    ///     measures each island's real-world surface area (worst case across blend shapes and animated
    ///     scale) so a texel density can be derived.
    /// ZH: 把 UV 布局拆分成岛、合并重叠岛、归一化越界 UV，并测量每个岛的真实世界表面积
    ///     （取形态键与动画缩放的最坏情况），以便推导像素密度。
    /// </summary>
    public static class UVIslandBuilder
    {
        /// <summary>EN: Build the island set for one submesh/UV channel. ZH: 为一个子网格/UV 通道构建岛集合。</summary>
        public static IslandSet Build(Mesh mesh, int subMesh, int uvChannel, float worldScale, ATOLog log)
        {
            var set = new IslandSet();
            if (mesh == null || subMesh >= mesh.subMeshCount) return set;

            var uvList = new List<Vector2>();
            mesh.GetUVs(uvChannel, uvList);
            if (uvList.Count == 0)
            {
                set.Unsafe = true;
                set.UnsafeReason = $"mesh '{mesh.name}' has no UV{uvChannel}";
                return set;
            }

            var tris = mesh.GetTriangles(subMesh);
            if (tris.Length == 0) return set;

            var uv = uvList.ToArray();
            var verts = mesh.vertices;

            // ---- 1. Union-Find over vertex indices -------------------------------------------------
            // EN: Unity duplicates vertices across UV seams, so vertex-index connectivity IS UV
            //     connectivity. No epsilon matching is needed or wanted.
            // ZH: Unity 会在 UV 缝处复制顶点，因此顶点索引连通性就是 UV 连通性；无需也不应做 epsilon 匹配。
            var parent = new int[uv.Length];
            for (int i = 0; i < parent.Length; i++) parent[i] = i;

            int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
            void Union(int a, int b) { a = Find(a); b = Find(b); if (a != b) parent[a] = b; }

            for (int t = 0; t < tris.Length; t += 3)
            {
                Union(tris[t], tris[t + 1]);
                Union(tris[t + 1], tris[t + 2]);
            }

            var buckets = new Dictionary<int, List<int>>();
            for (int t = 0; t < tris.Length; t += 3)
            {
                var root = Find(tris[t]);
                if (!buckets.TryGetValue(root, out var l)) buckets[root] = l = new List<int>();
                l.Add(t);
            }

            var maxAreaScale = ComputeBlendShapeAreaScale(mesh, tris, verts);

            var raw = new List<UVIsland>(buckets.Count);
            foreach (var kv in buckets)
            {
                var island = new UVIsland { Triangles = kv.Value.ToArray() };
                ComputeBounds(island, tris, uv);
                raw.Add(island);
            }

            // ---- 2. Normalise out-of-range UVs ------------------------------------------------------
            foreach (var island in raw)
            {
                var fx = Mathf.FloorToInt(island.UvMin.x + 1e-5f);
                var fy = Mathf.FloorToInt(island.UvMin.y + 1e-5f);
                if (fx == 0 && fy == 0 && island.UvMax.x <= 1f + 1e-4f && island.UvMax.y <= 1f + 1e-4f) continue;

                // EN: Translating back into [0,1] is only valid when the island fits inside a single wrap
                //     tile. Otherwise it genuinely relies on repeat sampling and must be skipped.
                // ZH: 只有当岛完整落在单个 wrap 瓦片内时整体平移才成立；
                //     否则它确实依赖 repeat 采样，必须跳过。
                if (island.UvMax.x - fx > 1f + 1e-4f || island.UvMax.y - fy > 1f + 1e-4f)
                {
                    set.Unsafe = true;
                    set.UnsafeReason = $"UV{uvChannel} island spans a wrap seam ({island.UvMin} .. {island.UvMax})";
                    return set;
                }

                island.Wrap = new Vector2Int(fx, fy);
                island.UvMin -= new Vector2(fx, fy);
                island.UvMax -= new Vector2(fx, fy);
            }

            var merged = MergeOverlapping(raw);

            // ---- 3. Areas ----------------------------------------------------------------------------
            var scale2 = worldScale * worldScale;
            Parallel.For(0, merged.Count, i =>
            {
                var island = merged[i];
                double world = 0, uvArea = 0;
                foreach (var t in island.Triangles)
                {
                    int a = tris[t], b = tris[t + 1], c = tris[t + 2];
                    var triWorld = 0.5f * Vector3.Cross(verts[b] - verts[a], verts[c] - verts[a]).magnitude;
                    world += triWorld * maxAreaScale[t / 3] * scale2;
                    uvArea += Mathf.Abs(Cross(uv[b] - uv[a], uv[c] - uv[a])) * 0.5f;
                }
                island.WorldAreaM2 = (float)world;
                island.UvArea = (float)uvArea;
                island.Index = i;
            });

            set.Islands = merged;
            log.Trace($"'{mesh.name}' sub {subMesh} uv{uvChannel}: {raw.Count} raw islands -> {merged.Count} merged");
            return set;
        }

        private static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;

        private static void ComputeBounds(UVIsland island, int[] tris, Vector2[] uv)
        {
            var min = new Vector2(float.MaxValue, float.MaxValue);
            var max = new Vector2(float.MinValue, float.MinValue);
            foreach (var t in island.Triangles)
                for (int k = 0; k < 3; k++)
                {
                    var p = uv[tris[t + k]];
                    if (p.x < min.x) min.x = p.x;
                    if (p.y < min.y) min.y = p.y;
                    if (p.x > max.x) max.x = p.x;
                    if (p.y > max.y) max.y = p.y;
                }
            island.UvMin = min;
            island.UvMax = max;
        }

        /// <summary>
        /// EN: Per-triangle area multiplier capturing the worst case over blend shapes. Each shape is
        ///     evaluated at weight 0 and weight 100 only. Combinations, negative weights and weights
        ///     above 100 are deliberately not explored: the combinatorial blow-up would dominate bake
        ///     time for a negligible accuracy gain.
        /// ZH: 逐三角形的面积倍率，取形态键的最坏情况。每个形态键只在权重 0 与 100 求值。
        ///     刻意不枚举组合、负权重与超过 100 的权重：组合爆炸会主导烘焙耗时，精度收益可忽略。
        /// </summary>
        private static float[] ComputeBlendShapeAreaScale(Mesh mesh, int[] tris, Vector3[] verts)
        {
            var triCount = tris.Length / 3;
            var result = new float[triCount];
            for (int i = 0; i < triCount; i++) result[i] = 1f;

            var shapeCount = mesh.blendShapeCount;
            if (shapeCount == 0) return result;

            var vcount = mesh.vertexCount;
            var dv = new Vector3[vcount];
            var dn = new Vector3[vcount];
            var dt = new Vector3[vcount];
            var moved = new Vector3[vcount];

            for (int s = 0; s < shapeCount; s++)
            {
                var frames = mesh.GetBlendShapeFrameCount(s);
                if (frames == 0) continue;
                mesh.GetBlendShapeFrameVertices(s, frames - 1, dv, dn, dt);

                bool any = false;
                for (int v = 0; v < vcount; v++)
                {
                    moved[v] = verts[v] + dv[v];
                    if (!any && dv[v].sqrMagnitude > 1e-12f) any = true;
                }
                if (!any) continue;

                Parallel.For(0, triCount, i =>
                {
                    int t = i * 3;
                    int a = tris[t], b = tris[t + 1], c = tris[t + 2];
                    if (dv[a].sqrMagnitude < 1e-12f && dv[b].sqrMagnitude < 1e-12f && dv[c].sqrMagnitude < 1e-12f)
                        return;
                    var baseArea = Vector3.Cross(verts[b] - verts[a], verts[c] - verts[a]).magnitude;
                    if (baseArea < 1e-12f) return;
                    var shapeArea = Vector3.Cross(moved[b] - moved[a], moved[c] - moved[a]).magnitude;
                    var ratio = shapeArea / baseArea;
                    if (ratio > result[i]) result[i] = ratio;
                });
            }
            return result;
        }

        /// <summary>
        /// EN: Merge islands whose UV bounding boxes overlap; overlapping islands share texels and must
        ///     move together. Iterated to a fixed point because merging can create new overlaps.
        /// ZH: 合并 UV 包围盒重叠的岛；重叠的岛共享纹素，必须一起移动。
        ///     迭代到不动点，因为合并本身会产生新的重叠。
        /// </summary>
        private static List<UVIsland> MergeOverlapping(List<UVIsland> islands)
        {
            bool changed = true;
            var current = islands;
            while (changed && current.Count > 1)
            {
                changed = false;
                var result = new List<UVIsland>(current.Count);
                var consumed = new bool[current.Count];

                for (int i = 0; i < current.Count; i++)
                {
                    if (consumed[i]) continue;
                    var a = current[i];
                    consumed[i] = true;
                    for (int j = i + 1; j < current.Count; j++)
                    {
                        if (consumed[j]) continue;
                        var b = current[j];
                        if (!Overlaps(a, b)) continue;

                        var tri = new int[a.Triangles.Length + b.Triangles.Length];
                        Array.Copy(a.Triangles, tri, a.Triangles.Length);
                        Array.Copy(b.Triangles, 0, tri, a.Triangles.Length, b.Triangles.Length);
                        a = new UVIsland
                        {
                            Triangles = tri,
                            UvMin = Vector2.Min(a.UvMin, b.UvMin),
                            UvMax = Vector2.Max(a.UvMax, b.UvMax),
                            Wrap = a.Wrap,
                        };
                        consumed[j] = true;
                        changed = true;
                    }
                    result.Add(a);
                }
                current = result;
            }

            for (int i = 0; i < current.Count; i++) current[i].Index = i;
            return current;
        }

        private static bool Overlaps(UVIsland a, UVIsland b)
        {
            // EN: One-texel slack at 4096 px so islands that merely touch are not merged.
            // ZH: 按 4096 像素计的一纹素余量，避免仅相切的岛被合并。
            const float slack = 1f / 4096f;
            return a.UvMin.x < b.UvMax.x - slack && b.UvMin.x < a.UvMax.x - slack &&
                   a.UvMin.y < b.UvMax.y - slack && b.UvMin.y < a.UvMax.y - slack;
        }

        /// <summary>
        /// EN: Worst-case uniform world scale of a renderer, combining its current lossy scale with the
        ///     largest scale any animation can apply to it or to an ancestor.
        /// ZH: 渲染器的最坏情况均匀世界缩放，把当前 lossyScale 与动画可施加到它或其祖先的最大缩放合并。
        /// </summary>
        public static float WorstCaseWorldScale(Renderer r, Transform root, AnimationFacts anim)
        {
            var lossy = r.transform.lossyScale;
            float s = Mathf.Max(Mathf.Abs(lossy.x), Mathf.Max(Mathf.Abs(lossy.y), Mathf.Abs(lossy.z)));

            float animMul = 1f;
            var t = r.transform;
            while (t != null && t != root.parent)
            {
                var path = nadena.dev.ndmf.runtime.RuntimeUtil.RelativePath(root.gameObject, t.gameObject);
                if (path != null && anim.MaxAnimatedScale.TryGetValue(path, out var m))
                {
                    var cur = t.localScale;
                    float ratio = 1f;
                    if (Mathf.Abs(cur.x) > 1e-6f) ratio = Mathf.Max(ratio, Mathf.Abs(m.x) / Mathf.Abs(cur.x));
                    if (Mathf.Abs(cur.y) > 1e-6f) ratio = Mathf.Max(ratio, Mathf.Abs(m.y) / Mathf.Abs(cur.y));
                    if (Mathf.Abs(cur.z) > 1e-6f) ratio = Mathf.Max(ratio, Mathf.Abs(m.z) / Mathf.Abs(cur.z));
                    animMul *= Mathf.Max(1f, ratio);
                }
                t = t.parent;
            }
            return Mathf.Max(1e-4f, s * animMul);
        }
    }
}
