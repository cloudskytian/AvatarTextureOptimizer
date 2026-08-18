// Avatar Texture Optimizer (ATO)
// UV island extraction: per (renderer, channel) via triangle adjacency flood fill,
// out-of-bounds normalization, wrap-seam detection, and same-submesh overlap merge.
// UV 岛提取：按 (渲染器, 通道) 用三角形邻接洪泛填充，处理越界归一、wrap 缝检测与同子网格重叠岛合并。

using System.Collections.Generic;
using UnityEngine;

namespace NetFosa.ATO
{
    /// <summary>
    /// Stage 4: build UV islands and UV spaces. / 阶段 4：构建 UV 岛与 UV 空间。
    /// </summary>
    public static class ATOIslandBuilder
    {
        private struct Edge : System.IEquatable<Edge>
        {
            public int a, b;
            public Edge(int x, int y) { a = x < y ? x : y; b = x < y ? y : x; }
            public bool Equals(Edge o) => a == o.a && b == o.b;
            public override int GetHashCode() => a * 73856093 ^ b * 19349663;
        }

        public static void BuildAll(ATOBuildContext build, ATOProgress progress)
        {
            int workItems = 0;
            foreach (var rr in build.renderers)
                if (rr.EffectiveEnabled) workItems += rr.usedUvChannels.Count;
            progress.Begin(workItems);

            int nextIslandId = 0;
            foreach (var rr in build.renderers)
            {
                if (!rr.EffectiveEnabled) continue;
                foreach (var channel in rr.usedUvChannels)
                {
                    BuildForChannel(build, rr, channel, ref nextIslandId);
                    progress.Advance(1, $"{rr.renderer.name} ch{channel}");
                    progress.ThrowIfCancelled();
                }
            }

            build.report.islandCount = nextIslandId;
            ATOLogger.Info($"Extracted {nextIslandId} UV islands across {build.uvSpaces.Count} UV spaces.");
        }

        private static void BuildForChannel(ATOBuildContext build, ATORendererRef rr, int channel, ref int nextIslandId)
        {
            var mesh = rr.sourceMesh;
            if (!ATOMeshUvAccessor.TryGetUv(mesh, channel, out var meshUv)) return;

            var space = new ATOUvSpace { meshId = rr.rendererId, uvChannel = channel, usable = true };
            var islands = space.islands;

            int subMeshCount = mesh.subMeshCount;
            for (int sub = 0; sub < subMeshCount; sub++)
            {
                var subTris = mesh.GetTriangles(sub);
                if (subTris.Length == 0) continue;

                // Edge -> adjacent triangle list. / 边 -> 相邻三角形列表。
                var edgeMap = new Dictionary<Edge, List<int>>();
                for (int t = 0; t < subTris.Length / 3; t++)
                {
                    int i0 = subTris[t * 3], i1 = subTris[t * 3 + 1], i2 = subTris[t * 3 + 2];
                    AddEdge(edgeMap, new Edge(i0, i1), t);
                    AddEdge(edgeMap, new Edge(i1, i2), t);
                    AddEdge(edgeMap, new Edge(i2, i0), t);
                }

                int triCount = subTris.Length / 3;
                var visited = new bool[triCount];
                for (int seed = 0; seed < triCount; seed++)
                {
                    if (visited[seed]) continue;
                    // Flood fill. / 洪泛填充。
                    var triList = new List<int>();
                    var stack = new Stack<int>();
                    stack.Push(seed);
                    visited[seed] = true;
                    while (stack.Count > 0)
                    {
                        int t = stack.Pop();
                        triList.Add(t);
                        int i0 = subTris[t * 3], i1 = subTris[t * 3 + 1], i2 = subTris[t * 3 + 2];
                        foreach (var e in new[] { new Edge(i0, i1), new Edge(i1, i2), new Edge(i2, i0) })
                        {
                            if (!edgeMap.TryGetValue(e, out var nbrs)) continue;
                            foreach (var n in nbrs)
                                if (!visited[n]) { visited[n] = true; stack.Push(n); }
                        }
                    }

                    islands.Add(MakeIsland(mesh, meshUv, subTris, triList, channel, sub, rr.rendererId, ref nextIslandId));
                }
            }

            if (islands.Count == 0) return;

            // Merge same-submesh overlapping islands. / 合并同子网格重叠岛。
            MergeOverlapping(islands);

            // Normalize out-of-bounds islands. / 归一越界岛。
            ValidateAndNormalize(build, space, rr);

            if (space.usable && islands.Count > 0)
                build.uvSpaces.Add(space);
            else if (!space.usable)
            {
                // Space is treated as whitelist: its textures skip optimization. / 该空间按白名单处理：其贴图跳过优化。
                foreach (var t in space.textures) t.skipAllOptimization = true;
                build.report.warnings.Add($"UV space '{rr.renderer.name}' ch{channel} skipped: {space.unusableReason} / UV 空间 '{rr.renderer.name}' 通道{channel} 跳过：{space.unusableReason}");
                ATOLogger.Warn(build.report.warnings[build.report.warnings.Count - 1]);
            }
        }

        private static void AddEdge(Dictionary<Edge, List<int>> map, Edge e, int tri)
        {
            if (!map.TryGetValue(e, out var list)) map[e] = list = new List<int>();
            list.Add(tri);
        }

        private static ATOIsland MakeIsland(Mesh mesh, Vector2[] meshUv, int[] subTris, List<int> triList,
            int channel, int subMesh, int meshId, ref int nextIslandId)
        {
            // Collect unique vertices in deterministic order. / 按确定顺序收集唯一顶点。
            var vertexSet = new HashSet<int>();
            foreach (var t in triList)
                for (int k = 0; k < 3; k++) vertexSet.Add(subTris[t * 3 + k]);

            var localVertices = new List<int>(vertexSet);
            var indexMap = new Dictionary<int, int>();
            for (int i = 0; i < localVertices.Count; i++) indexMap[localVertices[i]] = i;

            var uv = new Vector2[localVertices.Count];
            for (int i = 0; i < localVertices.Count; i++) uv[i] = meshUv[localVertices[i]];

            var tris = new int[triList.Count * 3];
            for (int t = 0; t < triList.Count; t++)
                for (int k = 0; k < 3; k++) tris[t * 3 + k] = indexMap[subTris[triList[t] * 3 + k]];

            var isl = new ATOIsland
            {
                islandId = nextIslandId++,
                meshId = meshId,
                uvChannel = channel,
                subMesh = subMesh,
                uv = uv,
                triangles = tris,
                localVertices = localVertices.ToArray(),
            };
            RecomputeBounds(isl);
            return isl;
        }

        private static void RecomputeBounds(ATOIsland isl)
        {
            var min = new Vector2(float.MaxValue, float.MaxValue);
            var max = new Vector2(float.MinValue, float.MinValue);
            float area = 0f;
            for (int t = 0; t < isl.triangles.Length / 3; t++)
            {
                var a = isl.uv[isl.triangles[t * 3]];
                var b = isl.uv[isl.triangles[t * 3 + 1]];
                var c = isl.uv[isl.triangles[t * 3 + 2]];
                min = Vector2.Min(min, Vector2.Min(a, Vector2.Min(b, c)));
                max = Vector2.Max(max, Vector2.Max(a, Vector2.Max(b, c)));
                area += Mathf.Abs(Cross(b - a, c - a)) * 0.5f;
            }
            isl.minUV = min; isl.maxUV = max; isl.areaUv = area;
        }

        private static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;

        private static void MergeOverlapping(List<ATOIsland> islands)
        {
            bool mergedAny = true;
            while (mergedAny)
            {
                mergedAny = false;
                for (int i = 0; i < islands.Count && !mergedAny; i++)
                {
                    for (int j = i + 1; j < islands.Count; j++)
                    {
                        var a = islands[i]; var b = islands[j];
                        if (a.subMesh != b.subMesh) continue;
                        if (!BoundsOverlap(a, b)) continue;
                        MergeInto(a, b);
                        islands.RemoveAt(j);
                        mergedAny = true;
                        break;
                    }
                }
            }
        }

        private static bool BoundsOverlap(ATOIsland a, ATOIsland b)
        {
            return a.minUV.x <= b.maxUV.x && a.maxUV.x >= b.minUV.x && a.minUV.y <= b.maxUV.y && a.maxUV.y >= b.minUV.y;
        }

        private static void MergeInto(ATOIsland dst, ATOIsland src)
        {
            var offset = dst.uv.Length;
            var newUv = new Vector2[dst.uv.Length + src.uv.Length];
            dst.uv.CopyTo(newUv, 0);
            for (int i = 0; i < src.uv.Length; i++) newUv[offset + i] = src.uv[i];
            dst.uv = newUv;

            var newVerts = new int[dst.localVertices.Length + src.localVertices.Length];
            dst.localVertices.CopyTo(newVerts, 0);
            src.localVertices.CopyTo(newVerts, dst.localVertices.Length);
            dst.localVertices = newVerts;

            var newTris = new int[dst.triangles.Length + src.triangles.Length];
            dst.triangles.CopyTo(newTris, 0);
            for (int i = 0; i < src.triangles.Length; i++) newTris[dst.triangles.Length + i] = src.triangles[i] + offset;
            dst.triangles = newTris;

            RecomputeBounds(dst);
        }

        private static void ValidateAndNormalize(ATOBuildContext build, ATOUvSpace space, ATORendererRef rr)
        {
            // Textures bound to this space (primary usage resolution). / 绑定到该空间的贴图（按主用解析）。
            foreach (var tr in build.textures)
                foreach (var u in tr.usages)
                    if (u.renderer == rr && u.uvChannel == space.uvChannel && !space.textures.Contains(tr))
                        space.textures.Add(tr);

            bool anyRepeat = space.textures.Count > 0;
            foreach (var t in space.textures)
                if (t.wrapMode != TextureWrapMode.Repeat) anyRepeat = false;

            const float eps = 1e-4f;
            foreach (var isl in space.islands)
            {
                if (isl.minUV.x < -eps || isl.minUV.y < -eps || isl.maxUV.x > 1f + eps || isl.maxUV.y > 1f + eps)
                {
                    isl.outOfBounds = true;
                    var span = isl.maxUV - isl.minUV;
                    if (span.x > 1f + eps || span.y > 1f + eps)
                    {
                        // Crosses the wrap seam -> needs repeat sampling -> whitelist. / 跨 wrap 缝 -> 依赖 repeat -> 白名单。
                        space.usable = false;
                        space.unusableReason = "island crosses the wrap seam (repeat sampling required) / 岛跨 wrap 缝（依赖 repeat 采样）";
                        return;
                    }
                    if (!anyRepeat)
                    {
                        space.usable = false;
                        space.unusableReason = "out-of-bounds UV with non-repeat wrap mode / 越界 UV 且 wrap 模式非 Repeat";
                        return;
                    }
                }
            }

            // Translate out-of-bounds (non-seam-crossing) islands into [0,1]. / 把越界（未跨缝）岛平移回 [0,1]。
            foreach (var isl in space.islands)
            {
                if (!isl.outOfBounds) continue;
                var offset = new Vector2(-Mathf.Floor(isl.minUV.x + eps), -Mathf.Floor(isl.minUV.y + eps));
                for (int i = 0; i < isl.uv.Length; i++) isl.uv[i] += offset;
                isl.normalized = true;
                isl.normalizationOffset = offset;
                RecomputeBounds(isl);
                ATOLogger.Debug($"Normalized island {isl.islandId} by offset {offset}");
            }
        }
    }
}
