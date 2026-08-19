using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace FOSA.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Builds UV islands from mesh topology. Multi-channel. Blendshapes at 0 and 100 only.
    /// Overlapping islands on the same texture are merged. Out-of-range UVs that can be
    /// translated as a whole into [0,1] are remapped; wrap-dependent UVs become whitelist.
    /// 从网格拓扑提取 UV 岛。多通道。形态键只取 0 和 100。
    /// 同贴图重叠岛合并。可整体平移进 [0,1] 的越界 UV 会归一；依赖 wrap 的则进白名单。
    /// </summary>
    internal static class ATOIslandExtractor
    {
        public static void Run(ATOContext ctx)
        {
            int nextId = 1;
            foreach (var ri in ctx.Renderers)
            {
                if (ri.Mesh == null) continue;
                ExtractRenderer(ctx, ri, ref nextId);
            }
            ctx.Report.IslandCount = CountIslands(ctx);
            ctx.Log.Info($"Islands: {ctx.Report.IslandCount}");
        }

        private static int CountIslands(ATOContext ctx)
        {
            var n = 0;
            foreach (var ri in ctx.Renderers) n += ri.Islands.Count;
            return n;
        }

        private static void ExtractRenderer(ATOContext ctx, ATORendererInfo ri, ref int nextId)
        {
            var mesh = ri.Mesh;
            var usedChannels = new HashSet<int>();
            foreach (var use in ctx.Uses)
            {
                if (use.Renderer != ri) continue;
                if (use.Slot.texture == null) continue;
                if (ctx.WhitelistedTextures.Contains(use.Slot.texture)) continue;
                usedChannels.Add(Mathf.Clamp(use.Slot.uvChannel, 0, 7));
            }

            foreach (var ch in usedChannels)
            {
                var uvs = GetUv(mesh, ch);
                if (uvs == null || uvs.Length == 0)
                {
                    ctx.Log.Warn($"Mesh '{mesh.name}' has no UV{ch}");
                    continue;
                }

                if (!TryNormalizeUvs(uvs, out var remapped, out var reason))
                {
                    foreach (var use in ctx.Uses)
                    {
                        if (use.Renderer == ri && use.Slot.uvChannel == ch && use.Slot.texture != null)
                            ctx.WarnWhitelist(use.Slot.texture, $"UV{ch} {reason} on {mesh.name}");
                    }
                    continue;
                }

                if (remapped)
                {
                    ctx.Log.Detail($"Normalized UV{ch} on '{mesh.name}' into [0,1]");
                    // Persist remapped UVs onto a mesh clone later in Apply; stash on renderer via a temp mesh copy.
                    // 归一后的 UV 在 Apply 阶段写回克隆网格；这里先改工作副本。
                    EnsureWorkingMesh(ctx, ri);
                    SetUv(ri.Mesh, ch, uvs);
                }

                var worldScale = ri.MaxWorldScale;
                for (int sm = 0; sm < mesh.subMeshCount; sm++)
                {
                    var tris = mesh.GetTriangles(sm);
                    if (tris == null || tris.Length < 3) continue;
                    var islands = BuildIslands(ctx, ri, sm, ch, tris, uvs, worldScale, ref nextId);
                    MergeOverlapping(islands);
                    ri.Islands.AddRange(islands);
                    ctx.Log.Detail($"  {ri.Renderer.name} sm={sm} uv{ch}: {islands.Count} islands");
                }
            }
        }

        private static void EnsureWorkingMesh(ATOContext ctx, ATORendererInfo ri)
        {
            if (ctx.MeshRemap.TryGetValue(ri.Mesh, out var existing) && existing != null)
            {
                ri.Mesh = existing;
                return;
            }
            var clone = UnityEngine.Object.Instantiate(ri.Mesh);
            clone.name = ri.Mesh.name + "_ATO";
            ctx.Build.AssetSaver.SaveAsset(clone);
            ObjectRegistrySafe(ri.Mesh, clone);
            ctx.MeshRemap[ri.Mesh] = clone;
            ri.Mesh = clone;
            if (ri.IsSkinned) ((SkinnedMeshRenderer)ri.Renderer).sharedMesh = clone;
            else
            {
                var mf = ri.Renderer.GetComponent<MeshFilter>();
                if (mf != null) mf.sharedMesh = clone;
            }
        }

        private static void ObjectRegistrySafe(UnityEngine.Object a, UnityEngine.Object b)
        {
            try { nadena.dev.ndmf.ObjectRegistry.RegisterReplacedObject(a, b); }
            catch { /* registry may be inactive in tests / 测试里可能没有 registry */ }
        }

        internal static bool TryNormalizeUvs(Vector2[] uvs, out bool remapped, out string reason)
        {
            remapped = false;
            reason = null;
            if (uvs == null || uvs.Length == 0) { reason = "empty"; return false; }

            var min = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
            var max = new Vector2(float.NegativeInfinity, float.NegativeInfinity);
            foreach (var uv in uvs)
            {
                if (float.IsNaN(uv.x) || float.IsNaN(uv.y) || float.IsInfinity(uv.x) || float.IsInfinity(uv.y))
                {
                    reason = "contains NaN/Inf";
                    return false;
                }
                min = Vector2.Min(min, uv);
                max = Vector2.Max(max, uv);
            }

            var size = max - min;
            if (size.x > 1.0001f || size.y > 1.0001f)
            {
                reason = $"crosses wrap seam (bbox {min}..{max})";
                return false;
            }

            // Already inside [0,1]. / 已在 [0,1] 内。
            if (min.x >= -1e-5f && min.y >= -1e-5f && max.x <= 1.0001f && max.y <= 1.0001f)
                return true;

            // Translate as a whole so min >= 0 and max <= 1. / 整体平移使 min>=0 且 max<=1。
            var shift = new Vector2(
                min.x < 0f ? -min.x : (max.x > 1f ? 1f - max.x : 0f),
                min.y < 0f ? -min.y : (max.y > 1f ? 1f - max.y : 0f));

            // If we only need to pull into range and size <= 1, a single translation works.
            // 只要尺寸 ≤ 1，一次平移就能拉回区间。
            for (int i = 0; i < uvs.Length; i++) uvs[i] += shift;
            remapped = true;
            return true;
        }

        private static List<ATOIsland> BuildIslands(
            ATOContext ctx, ATORendererInfo ri, int sm, int ch,
            int[] tris, Vector2[] uvs, float worldScale, ref int nextId)
        {
            var triCount = tris.Length / 3;
            var uf = new int[triCount];
            for (int i = 0; i < triCount; i++) uf[i] = i;

            int Find(int x) { while (uf[x] != x) { uf[x] = uf[uf[x]]; x = uf[x]; } return x; }
            void Union(int a, int b) { a = Find(a); b = Find(b); if (a != b) uf[b] = a; }

            // Edge key: quantized UV endpoints. / 边键：量化后的 UV 端点。
            var edgeToTri = new Dictionary<long, int>(triCount * 2);
            for (int t = 0; t < triCount; t++)
            {
                var i0 = tris[t * 3];
                var i1 = tris[t * 3 + 1];
                var i2 = tris[t * 3 + 2];
                Connect(i0, i1, t);
                Connect(i1, i2, t);
                Connect(i2, i0, t);
            }

            void Connect(int a, int b, int tri)
            {
                var key = EdgeKey(uvs[a], uvs[b]);
                if (edgeToTri.TryGetValue(key, out var other)) Union(tri, other);
                else edgeToTri[key] = tri;
            }

            var groups = new Dictionary<int, List<int>>();
            for (int t = 0; t < triCount; t++)
            {
                var r = Find(t);
                if (!groups.TryGetValue(r, out var list))
                {
                    list = new List<int>();
                    groups[r] = list;
                }
                list.Add(t);
            }

            var verts = ri.Mesh.vertices;
            var delta0 = new Vector3[verts.Length];
            var delta100 = EvaluateBlendMax(ri.Mesh, verts);

            var islands = new List<ATOIsland>(groups.Count);
            foreach (var kv in groups)
            {
                var island = new ATOIsland
                {
                    Id = nextId++,
                    Renderer = ri,
                    Submesh = sm,
                    UvChannel = ch,
                    TriangleIndices = kv.Value.ToArray(),
                    UvMin = new Vector2(float.PositiveInfinity, float.PositiveInfinity),
                    UvMax = new Vector2(float.NegativeInfinity, float.NegativeInfinity)
                };

                double area0 = 0, area100 = 0;
                foreach (var t in kv.Value)
                {
                    var a = tris[t * 3];
                    var b = tris[t * 3 + 1];
                    var c = tris[t * 3 + 2];
                    island.UvMin = Vector2.Min(island.UvMin, uvs[a]);
                    island.UvMin = Vector2.Min(island.UvMin, uvs[b]);
                    island.UvMin = Vector2.Min(island.UvMin, uvs[c]);
                    island.UvMax = Vector2.Max(island.UvMax, uvs[a]);
                    island.UvMax = Vector2.Max(island.UvMax, uvs[b]);
                    island.UvMax = Vector2.Max(island.UvMax, uvs[c]);
                    area0 += TriangleArea(verts[a], verts[b], verts[c]);
                    if (delta100 != null)
                    {
                        area100 += TriangleArea(verts[a] + delta100[a], verts[b] + delta100[b], verts[c] + delta100[c]);
                    }
                }

                var worldArea = (float)Math.Max(area0, area100) * worldScale * worldScale;
                island.WorldArea = Mathf.Max(worldArea, 1e-12f);
                island.WorldShortSide = Mathf.Sqrt(island.WorldArea);
                islands.Add(island);
            }
            return islands;
        }

        /// <summary>
        /// Per-vertex max displacement between weight 0 and the frame closest to 100 (clamped, no extrapolation).
        /// 顶点在权重 0 与最接近 100 的帧之间的最大位移（钳制，不做外推）。
        /// </summary>
        private static Vector3[] EvaluateBlendMax(Mesh mesh, Vector3[] baseVerts)
        {
            var count = mesh.blendShapeCount;
            if (count == 0) return null;
            var acc = new Vector3[baseVerts.Length];
            var d = new Vector3[baseVerts.Length];
            var nrm = new Vector3[baseVerts.Length];
            var tan = new Vector3[baseVerts.Length];
            var any = false;
            for (int s = 0; s < count; s++)
            {
                var frames = mesh.GetBlendShapeFrameCount(s);
                if (frames <= 0) continue;
                var best = 0;
                var bestW = mesh.GetBlendShapeFrameWeight(s, 0);
                for (int f = 1; f < frames; f++)
                {
                    var w = mesh.GetBlendShapeFrameWeight(s, f);
                    if (w <= 100.0001f && w >= bestW) { best = f; bestW = w; }
                }
                mesh.GetBlendShapeFrameVertices(s, best, d, nrm, tan);
                for (int i = 0; i < acc.Length; i++) acc[i] += d[i];
                any = true;
            }
            return any ? acc : null;
        }

        private static void MergeOverlapping(List<ATOIsland> islands)
        {
            bool changed = true;
            while (changed)
            {
                changed = false;
                for (int i = 0; i < islands.Count; i++)
                {
                    for (int j = i + 1; j < islands.Count; j++)
                    {
                        if (!Overlaps(islands[i], islands[j])) continue;
                        islands[i] = Merge(islands[i], islands[j]);
                        islands.RemoveAt(j);
                        changed = true;
                        break;
                    }
                    if (changed) break;
                }
            }
        }

        private static bool Overlaps(ATOIsland a, ATOIsland b)
        {
            if (a.UvChannel != b.UvChannel || a.Submesh != b.Submesh) return false;
            return a.UvMin.x <= b.UvMax.x && a.UvMax.x >= b.UvMin.x &&
                   a.UvMin.y <= b.UvMax.y && a.UvMax.y >= b.UvMin.y;
        }

        private static ATOIsland Merge(ATOIsland a, ATOIsland b)
        {
            var tris = new int[a.TriangleIndices.Length + b.TriangleIndices.Length];
            Array.Copy(a.TriangleIndices, 0, tris, 0, a.TriangleIndices.Length);
            Array.Copy(b.TriangleIndices, 0, tris, a.TriangleIndices.Length, b.TriangleIndices.Length);
            a.TriangleIndices = tris;
            a.UvMin = Vector2.Min(a.UvMin, b.UvMin);
            a.UvMax = Vector2.Max(a.UvMax, b.UvMax);
            a.WorldArea += b.WorldArea;
            a.WorldShortSide = Mathf.Sqrt(a.WorldArea);
            a.OverlapsMerged = true;
            return a;
        }

        private static long EdgeKey(Vector2 a, Vector2 b)
        {
            Quant(a, out var ax, out var ay);
            Quant(b, out var bx, out var by);
            // Order-independent. / 与方向无关。
            if (ax > bx || (ax == bx && ay > by))
            {
                var tx = ax; ax = bx; bx = tx;
                var ty = ay; ay = by; by = ty;
            }
            unchecked
            {
                ulong k = (ushort)ax;
                k = (k << 16) | (ushort)ay;
                k = (k << 16) | (ushort)bx;
                k = (k << 16) | (ushort)by;
                return (long)k;
            }
        }

        private static void Quant(Vector2 v, out int x, out int y)
        {
            x = Mathf.RoundToInt(v.x * 4096f);
            y = Mathf.RoundToInt(v.y * 4096f);
        }

        private static float TriangleArea(Vector3 a, Vector3 b, Vector3 c)
        {
            return Vector3.Cross(b - a, c - a).magnitude * 0.5f;
        }

        public static Vector2[] GetUv(Mesh mesh, int channel)
        {
            var list = new List<Vector2>(mesh.vertexCount);
            mesh.GetUVs(channel, list);
            return list.Count == 0 ? null : list.ToArray();
        }

        public static void SetUv(Mesh mesh, int channel, Vector2[] uvs)
        {
            mesh.SetUVs(channel, uvs);
        }
    }
}
