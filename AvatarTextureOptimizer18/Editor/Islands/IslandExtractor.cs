using System.Collections.Generic;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Editor.Islands
{
    // UV 岛提取器：按（网格, UV 通道）提取连通岛、合并重叠岛、处理越界归一。
    // Island extractor: extracts connected islands per (mesh, UV channel), merges overlapping islands and normalizes out-of-bounds UVs.
    internal static class IslandExtractor
    {
        private const float Eps = 1e-6f;

        public static void Extract(ATOContext ctx, ATOReport.Stage stage)
        {
            ctx.entityByKey.Clear();
            ctx.islandEntities.Clear();
            int idGen = 0;
            int totalIslands = 0, merged = 0, normalized = 0;

            // 收集需要处理的（网格, 通道）键。Collect the (mesh, channel) keys to process.
            var keys = new HashSet<KeyValuePair<Mesh, int>>();
            foreach (var slot in ctx.slots)
            {
                foreach (var use in slot.uses)
                {
                    if (use.texture == null) continue;
                    keys.Add(new KeyValuePair<Mesh, int>(slot.mesh, use.uvChannel));
                }
            }

            foreach (var key in keys)
            {
                ctx.CheckCancelled();
                var list = ExtractForChannel(ctx, key.Key, key.Value, ref idGen, stage, ref totalIslands, ref merged, ref normalized);
                ctx.entityByKey[key] = list;
                ctx.islandEntities.AddRange(list);
            }

            stage.AddLine(string.Format(ATOLocalization.Tr("log.islandSummary"), totalIslands, merged, normalized));
        }

        // 提取某（网格, 通道）的全部岛。Extracts all islands of a (mesh, channel) pair.
        private static List<IslandEntity> ExtractForChannel(ATOContext ctx, Mesh mesh, int channel, ref int idGen,
            ATOReport.Stage stage, ref int totalIslands, ref int merged, ref int normalized)
        {
            var result = new List<IslandEntity>();
            if (mesh == null) return result;

            var uvs = new List<Vector2>();
            mesh.GetUVs(channel, uvs);
            int vertexCount = mesh.vertexCount;
            if (uvs.Count < vertexCount) return result; // 通道无数据。Channel has no data.

            // 1) 对每个子网格建立岛（并查集按共享 UV 边合并）。Build islands per submesh (union-find over shared UV edges).
            for (int sub = 0; sub < mesh.subMeshCount; sub++)
            {
                ctx.CheckCancelled();
                int[] tris;
                try
                {
                    tris = mesh.GetTriangles(sub);
                }
                catch (System.Exception)
                {
                    continue;
                }

                int triCount = tris.Length / 3;
                var parent = new int[triCount];
                for (int i = 0; i < triCount; i++) parent[i] = i;

                // 边哈希：键 = (minVtx,maxVtx)，值 = 所属三角形。Edge hash: key = (minVtx, maxVtx), value = owning triangle.
                var edges = new Dictionary<ulong, int>();
                for (int t = 0; t < triCount; t++)
                {
                    int a = tris[t * 3], b = tris[t * 3 + 1], c = tris[t * 3 + 2];
                    // 退化三角形跳过（UV 面积为零）。Skip degenerate triangles (zero UV area).
                    if (UvArea(uvs[a], uvs[b], uvs[c]) <= Eps) continue;
                    for (int e = 0; e < 3; e++)
                    {
                        int v0 = tris[t * 3 + e];
                        int v1 = tris[t * 3 + (e + 1) % 3];
                        ulong key = EdgeKey(v0, v1);
                        int other;
                        if (edges.TryGetValue(key, out other))
                        {
                            Union(parent, t, other);
                        }
                        else
                        {
                            edges[key] = t;
                        }
                    }
                }

                // 收集岛。Collect islands.
                var sets = new Dictionary<int, List<int>>();
                for (int t = 0; t < triCount; t++)
                {
                    int root = Find(parent, t);
                    List<int> list;
                    if (!sets.TryGetValue(root, out list))
                    {
                        list = new List<int>();
                        sets[root] = list;
                    }
                    list.Add(t);
                }

                foreach (var kv in sets)
                {
                    var entity = new IslandEntity
                    {
                        id = idGen++,
                        mesh = mesh,
                        uvChannel = channel,
                        submesh = sub
                    };
                    var verts = new HashSet<int>();
                    foreach (var t in kv.Value)
                    {
                        entity.triangles.Add(tris[t * 3]);
                        entity.triangles.Add(tris[t * 3 + 1]);
                        entity.triangles.Add(tris[t * 3 + 2]);
                        verts.Add(tris[t * 3]);
                        verts.Add(tris[t * 3 + 1]);
                        verts.Add(tris[t * 3 + 2]);
                    }
                    entity.vertices.AddRange(verts);

                    ComputeBounds(entity, uvs);
                    result.Add(entity);
                    totalIslands++;
                }
            }

            // 2) 越界归一化（跨子网格全局）。Out-of-bounds normalization.
            foreach (var e in result)
            {
                NormalizeOutOfBounds(ctx, e, uvs, stage, ref normalized);
            }

            // 3) 重叠岛合并（同通道、跨子网格，UV 包围盒相交 → 并）。Merge overlapping islands (same channel, bbox overlap → union).
            merged += MergeOverlapping(result);

            return result;
        }

        private static void ComputeBounds(IslandEntity e, List<Vector2> uvs)
        {
            float minU = float.MaxValue, minV = float.MaxValue, maxU = float.MinValue, maxV = float.MinValue;
            foreach (var v in e.vertices)
            {
                var uv = uvs[v];
                if (uv.x < minU) minU = uv.x;
                if (uv.y < minV) minV = uv.y;
                if (uv.x > maxU) maxU = uv.x;
                if (uv.y > maxV) maxV = uv.y;
            }
            e.uvMin = new Vector2(minU, minV);
            e.uvMax = new Vector2(maxU, maxV);
        }

        // 越界归一化：整数平移归一到 [0,1]（仅 Repeat wrap 安全；Clamp/Mirror/跨缝 → 白名单）。
        // Out-of-bounds normalization: integer translation into [0,1] (safe only for Repeat wrap; Clamp/Mirror/crossing-seam → whitelist).
        private static void NormalizeOutOfBounds(ATOContext ctx, IslandEntity e, List<Vector2> uvs, ATOReport.Stage stage, ref int normalized)
        {
            float minU = e.uvMin.x, minV = e.uvMin.y, maxU = e.uvMax.x, maxV = e.uvMax.y;
            bool outU = minU < -Eps || maxU > 1f + Eps;
            bool outV = minV < -Eps || maxV > 1f + Eps;
            if (!outU && !outV) return;

            float spanU = maxU - minU, spanV = maxV - minV;

            // 跨 wrap 缝（跨度 > 1）→ 无法归一，白名单。Crossing the wrap seam (span > 1) → unnormalizable, whitelist.
            if (spanU > 1f + Eps || spanV > 1f + Eps)
            {
                e.whitelistedFull = true;
                e.whitelistReason = "warn.island.crossSeam";
                stage.AddLine(string.Format(ATOLocalization.Tr("warn.island.crossSeam"), e.ToString()));
                return;
            }

            // 非 Repeat wrap → 整数平移不安全。Non-Repeat wrap → integer translation is unsafe.
            if (!RepeatSafe(ctx, e, outU, outV))
            {
                e.whitelistedFull = true;
                e.whitelistReason = "warn.island.nonRepeatWrap";
                stage.AddLine(string.Format(ATOLocalization.Tr("warn.island.nonRepeatWrap"), e.ToString()));
                return;
            }

            // 整数平移。Integer translation.
            float tx = outU ? -Mathf.Floor(minU) : 0f;
            float ty = outV ? -Mathf.Floor(minV) : 0f;
            if (minU + tx < -Eps || maxU + tx > 1f + Eps || minV + ty < -Eps || maxV + ty > 1f + Eps)
            {
                e.whitelistedFull = true;
                e.whitelistReason = "warn.island.crossSeam";
                stage.AddLine(string.Format(ATOLocalization.Tr("warn.island.crossSeam"), e.ToString()));
                return;
            }

            e.translation = new Vector2(tx, ty);
            e.uvMin += e.translation;
            e.uvMax += e.translation;
            normalized++;
        }

        // 该岛引用贴图的 wrap 模式是否允许整数平移（任一贴图非 Repeat 于越界轴 → 否）。
        // Whether integer translation is safe for all referencing textures (any non-Repeat wrap on an overflowing axis → no).
        private static bool RepeatSafe(ATOContext ctx, IslandEntity e, bool outU, bool outV)
        {
            // 使用关系尚未建立（提取阶段）：先检查所有槽位的贴图 wrap（按 mesh+channel 匹配槽位）。
            // Uses are not attached yet (extraction phase): check wrap modes of all slot textures matching this mesh+channel.
            foreach (var slot in ctx.slots)
            {
                if (slot.mesh != e.mesh) continue;
                foreach (var use in slot.uses)
                {
                    if (use.texture == null) continue;
                    var t = use.texture.source;
                    if (outU && t.wrapModeU != TextureWrapMode.Repeat) return false;
                    if (outV && t.wrapModeV != TextureWrapMode.Repeat) return false;
                }
            }
            return true;
        }

        // 重叠岛合并：UV 包围盒相交且归一平移一致 → 并（同贴图内重叠岛合并的保守实现：共享 UV 区域必须同一缩放）。
        // 平移量不同（跨 wrap 边界的两个岛实例）不合并，避免 UV 重写错位。
        // Merge overlapping islands: bbox overlap with identical normalization translation → union.
        // Different translations (two island instances across a wrap boundary) never merge, avoiding UV misplacement.
        private static int MergeOverlapping(List<IslandEntity> islands)
        {
            int n = islands.Count;
            var parent = new int[n];
            for (int i = 0; i < n; i++) parent[i] = i;
            for (int i = 0; i < n; i++)
            {
                for (int j = i + 1; j < n; j++)
                {
                    if (islands[i].whitelistedFull || islands[j].whitelistedFull) continue;
                    if (islands[i].translation != islands[j].translation) continue;
                    if (BBoxOverlap(islands[i], islands[j])) Union(parent, i, j);
                }
            }

            var groups = new Dictionary<int, List<IslandEntity>>();
            for (int i = 0; i < n; i++)
            {
                int root = Find(parent, i);
                List<IslandEntity> list;
                if (!groups.TryGetValue(root, out list))
                {
                    list = new List<IslandEntity>();
                    groups[root] = list;
                }
                list.Add(islands[i]);
            }

            int merged = 0;
            var keep = new List<IslandEntity>();
            foreach (var kv in groups)
            {
                var list = kv.Value;
                if (list.Count == 1)
                {
                    keep.Add(list[0]);
                    continue;
                }
                // 合并到第一个：合并三角形/顶点/包围盒。Merge into the first: merge triangles/vertices/bbox.
                var target = list[0];
                var vset = new HashSet<int>(target.vertices);
                foreach (var other in list)
                {
                    if (other == target) continue;
                    merged++;
                    target.triangles.AddRange(other.triangles);
                    foreach (var v in other.vertices) vset.Add(v);
                    target.uvMin = Vector2.Min(target.uvMin, other.uvMin);
                    target.uvMax = Vector2.Max(target.uvMax, other.uvMax);
                    target.worldArea += other.worldArea;
                    if (other.whitelistedFull)
                    {
                        target.whitelistedFull = true;
                        target.whitelistReason = other.whitelistReason;
                    }
                }
                target.vertices.Clear();
                target.vertices.AddRange(vset);
                keep.Add(target);
            }
            islands.Clear();
            islands.AddRange(keep);
            return merged;
        }

        private static bool BBoxOverlap(IslandEntity a, IslandEntity b)
        {
            return a.uvMin.x <= b.uvMax.x + Eps && b.uvMin.x <= a.uvMax.x + Eps
                && a.uvMin.y <= b.uvMax.y + Eps && b.uvMin.y <= a.uvMax.y + Eps;
        }

        private static float UvArea(Vector2 a, Vector2 b, Vector2 c)
        {
            return Mathf.Abs((b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x)) * 0.5f;
        }

        private static ulong EdgeKey(int v0, int v1)
        {
            if (v0 > v1) { int t = v0; v0 = v1; v1 = t; }
            return ((ulong)(uint)v0 << 32) | (uint)v1;
        }

        private static int Find(int[] parent, int i)
        {
            while (parent[i] != i)
            {
                parent[i] = parent[parent[i]];
                i = parent[i];
            }
            return i;
        }

        private static void Union(int[] parent, int a, int b)
        {
            int ra = Find(parent, a), rb = Find(parent, b);
            if (ra != rb) parent[rb] = ra;
        }
    }
}
