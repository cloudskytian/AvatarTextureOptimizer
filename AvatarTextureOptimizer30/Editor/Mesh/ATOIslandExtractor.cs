// ATOIslandExtractor.cs — UV 岛提取 / UV island extraction.
// 说明：从网格的每个 UV 通道提取连通岛（共享同 UV 边的三角形合并），计算：
//  - UV 包围盒与归一化平移（越界但可整体平移归一到 [0,1]；跨 wrap 缝则标记不可处理 → 按白名单跳过并 warning）
//  - UV 空间面积与最大世界面积（含实例缩放、动画最大缩放、形态键 0/100 取最大；不考虑排列组合/负数/超 100）
//  - 同贴图内重叠岛合并（仅当存在同一张贴图同时引用两岛时合并）
// Note: extracts connected islands from each UV channel (triangles sharing equal-UV edges merge), computing:
//  - UV bbox & normalizing translation (out-of-range but translatable → normalized into [0,1]; wrap-seam crossing → whitelist + warning)
//  - UV area and max world area (incl. instance scale, max animated scale, blendshapes at 0 & 100 — max of the two; no combos/negatives/>100)
//  - overlapping-island merging per texture (only when a single texture references both islands)

using System;
using System.Collections.Generic;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer
{
    /// <summary>UV 岛提取器。/ UV island extractor.</summary>
    internal static class ATOIslandExtractor
    {
        private const float UvEpsilon = 1e-5f; // UV 相等容差 / UV equality tolerance

        /// <summary>
        /// 提取一个（网格 × UV 通道）的全部岛。若该通道无数据返回空列表。
        /// Extract all islands of a (mesh × UV channel). Returns an empty list when the channel has no data.
        /// </summary>
        public static List<ATOIsland> Extract(Mesh mesh, int channel, List<ATORendererInfo> instances, out bool wrapIssue)
        {
            wrapIssue = false;
            var result = new List<ATOIsland>();

            var uvs = new List<Vector2>();
            mesh.GetUVs(channel, uvs);
            if (uvs.Count == 0) return result;

            var tris = mesh.triangles;
            if (tris.Length < 3) return result;
            var triCount = tris.Length / 3;

            // ---- 并查集：共享同 UV 边 → 同岛 / union-find: shared equal-UV edge → same island ----
            var parent = new int[triCount];
            for (int i = 0; i < triCount; i++) parent[i] = i;

            int Find(int x)
            {
                while (parent[x] != x)
                {
                    parent[x] = parent[parent[x]];
                    x = parent[x];
                }
                return x;
            }
            void Union(int a, int b)
            {
                var ra = Find(a);
                var rb = Find(b);
                if (ra != rb) parent[ra] = rb;
            }

            var edgeMap = new Dictionary<long, int>();
            for (int t = 0; t < triCount; t++)
            {
                for (int e = 0; e < 3; e++)
                {
                    var i0 = tris[t * 3 + e];
                    var i1 = tris[t * 3 + (e + 1) % 3];
                    var key = EdgeKey(uvs[i0], uvs[i1]);
                    if (edgeMap.TryGetValue(key, out var other))
                        Union(t, other);
                    else
                        edgeMap[key] = t;
                }
            }

            // ---- 按岛分组 / group triangles by island ----
            var islandTris = new Dictionary<int, List<int>>();
            for (int t = 0; t < triCount; t++)
            {
                var root = Find(t);
                if (!islandTris.TryGetValue(root, out var list))
                {
                    list = new List<int>();
                    islandTris[root] = list;
                }
                list.Add(t);
            }

            // ---- 构建岛数据 / build island data ----
            int id = 0;
            foreach (var kv in islandTris)
            {
                var island = new ATOIsland
                {
                    id = id++,
                    mesh = mesh,
                    channel = channel,
                    triangles = kv.Value,
                };

                // 顶点集合 / vertex set
                var verts = new HashSet<int>();
                var minU = float.MaxValue;
                var minV = float.MaxValue;
                var maxU = float.MinValue;
                var maxV = float.MinValue;
                foreach (var t in kv.Value)
                {
                    for (int e = 0; e < 3; e++)
                    {
                        var vi = tris[t * 3 + e];
                        verts.Add(vi);
                        var uv = uvs[vi];
                        if (uv.x < minU) minU = uv.x;
                        if (uv.y < minV) minV = uv.y;
                        if (uv.x > maxU) maxU = uv.x;
                        if (uv.y > maxV) maxV = uv.y;
                    }
                }

                // 越界检查：整体平移可归一到 [0,1] 才处理；跨 wrap 缝 → 标记不可处理 /
                // out-of-range check: process only when translatable into [0,1]; wrap-seam crossing → unprocessable
                var spanU = maxU - minU;
                var spanV = maxV - minV;
                if (spanU > 1f + UvEpsilon || spanV > 1f + UvEpsilon)
                {
                    wrapIssue = true;
                    island.wrapIssue = true;
                    island.translation = Vector2.zero;
                }
                else
                {
                    var tx = -Mathf.Floor(minU);
                    var ty = -Mathf.Floor(minV);
                    // 平移后仍需在 [0,1] 内 / after translation it must still fit in [0,1]
                    if (minU + tx < -UvEpsilon || maxU + tx > 1f + UvEpsilon ||
                        minV + ty < -UvEpsilon || maxV + ty > 1f + UvEpsilon)
                    {
                        wrapIssue = true;
                        island.wrapIssue = true;
                        island.translation = Vector2.zero;
                    }
                    else
                    {
                        island.translation = new Vector2(tx, ty);
                    }
                }

                island.uvMin = new Vector2(minU, minV);
                island.uvMax = new Vector2(maxU, maxV);

                // UV 面积（平移不变）/ UV area (translation-invariant)
                var area = 0f;
                foreach (var t in kv.Value)
                {
                    var a = uvs[tris[t * 3 + 0]];
                    var b = uvs[tris[t * 3 + 1]];
                    var c = uvs[tris[t * 3 + 2]];
                    area += Mathf.Abs((b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x)) * 0.5f;
                }
                island.uvArea = area;

                // 世界面积：实例 × 动画缩放 × 形态键（0/100 取最大）/ world area: instances × anim scale × morphs (max of 0/100)
                island.worldAreaMax = ComputeMaxWorldArea(mesh, island.triangles, tris, instances);

                result.Add(island);
            }

            // 面积降序（装箱顺序预备）/ sort by area desc (packing order prep)
            result.Sort((a, b) => b.uvArea.CompareTo(a.uvArea));
            return result;
        }

        /// <summary>UV 边键（有序端点、量化容差）。/ UV edge key (ordered endpoints, quantized tolerance).</summary>
        private static long EdgeKey(Vector2 a, Vector2 b)
        {
            var qa = Quantize(a);
            var qb = Quantize(b);
            if (qa < qb) return (qa << 32) ^ qb;
            return (qb << 32) ^ qa;
        }

        private static uint Quantize(Vector2 uv)
        {
            var u = (uint)Mathf.RoundToInt(uv.x / UvEpsilon);
            var v = (uint)Mathf.RoundToInt(uv.y / UvEpsilon);
            return u * 7919u + v * 1543u; // 组合哈希（碰撞概率极低，提取后还会校验）/ combined hash (collision chance negligible; validated downstream)
        }

        /// <summary>
        /// 计算岛的最大世界面积：遍历全部实例，取 max(实例面积 × 动画缩放系数)；
        /// 形态键：每个形态键仅取 0 与 100 时的面积，取二者最大值。
        /// Compute max world area: max over instances of (instance area × animated scale factor);
        /// blendshapes: area at weight 0 and 100 only, take the max of the two.
        /// </summary>
        private static float ComputeMaxWorldArea(Mesh mesh, List<int> islandTris, int[] tris, List<ATORendererInfo> instances)
        {
            if (instances == null || instances.Count == 0) return 0f;

            // 每实例（本地面积 × 实例缩放与动画缩放）/ per-instance local area × instance & animated scale
            float maxArea = 0f;
            foreach (var inst in instances)
            {
                if (inst.mesh != mesh) continue;
                var scale = ComputeInstanceAreaScale(inst.renderer.transform);
                var localArea = ComputeLocalArea(mesh, islandTris, tris, null);
                var area = localArea * scale * inst.maxAnimScaleFactor;
                if (area > maxArea) maxArea = area;
            }

            // 形态键：仅 0 与 100 / blendshapes: only 0 and 100
            if (mesh.blendShapeCount > 0)
            {
                var baseVerts = mesh.vertices;
                var delta = new Vector3[mesh.vertexCount];
                for (int b = 0; b < mesh.blendShapeCount; b++)
                {
                    // 取最高权重帧（通常即 100）/ take the highest-weight frame (usually 100)
                    var frameCount = mesh.GetBlendShapeFrameCount(b);
                    if (frameCount == 0) continue;
                    var lastFrame = frameCount - 1;
                    var frameWeight = mesh.GetBlendShapeFrameWeight(b, lastFrame);
                    mesh.GetBlendShapeFrameVertices(b, lastFrame, delta, null, null);
                    var w = frameWeight > 0f ? frameWeight / 100f : 1f;
                    var localArea = ComputeLocalArea(mesh, islandTris, tris, (i) => baseVerts[i] + delta[i] * w);
                    foreach (var inst in instances)
                    {
                        if (inst.mesh != mesh) continue;
                        var scale = ComputeInstanceAreaScale(inst.renderer.transform);
                        var area = localArea * scale * inst.maxAnimScaleFactor;
                        if (area > maxArea) maxArea = area;
                    }
                }
            }
            return maxArea;
        }

        /// <summary>本地面积（可带形态键增量）。/ Local area (optionally with blendshape deltas).</summary>
        private static float ComputeLocalArea(Mesh mesh, List<int> islandTris, int[] tris, Func<int, Vector3> vertGetter)
        {
            if (vertGetter == null)
            {
                var verts0 = mesh.vertices;
                vertGetter = (i) => verts0[i];
            }
            float area = 0f;
            foreach (var t in islandTris)
            {
                var a = vertGetter(tris[t * 3 + 0]);
                var b = vertGetter(tris[t * 3 + 1]);
                var c = vertGetter(tris[t * 3 + 2]);
                area += Vector3.Cross(b - a, c - a).magnitude * 0.5f;
            }
            return area;
        }

        /// <summary>实例面积缩放系数（保守：取三轴两两乘积最大值）。/ Instance area scale (conservative: max pairwise axis product).</summary>
        private static float ComputeInstanceAreaScale(Transform t)
        {
            var s = t.lossyScale;
            var ax = Mathf.Abs(s.x);
            var ay = Mathf.Abs(s.y);
            var az = Mathf.Abs(s.z);
            return Mathf.Max(ax * ay, Mathf.Max(ax * az, ay * az));
        }

        /// <summary>
        /// 同贴图内重叠岛合并：两岛（归一化后）包围盒相交，且存在同一张贴图同时引用两岛 → 合并。
        /// Merge overlapping islands within the same texture: bboxes (normalized) intersect AND some texture references both.
        /// </summary>
        public static List<ATOIsland> MergeOverlappingIslands(List<ATOIsland> islands,
            Func<ATOIsland, ATOIsland, bool> shareTexture)
        {
            var result = new List<ATOIsland>();
            var mergedSet = new HashSet<ATOIsland>();

            for (int i = 0; i < islands.Count; i++)
            {
                var a = islands[i];
                if (mergedSet.Contains(a)) continue;

                var group = new List<ATOIsland> { a };
                var bboxMin = a.uvMin + a.translation;
                var bboxMax = a.uvMax + a.translation;

                bool grew = true;
                while (grew)
                {
                    grew = false;
                    for (int j = i + 1; j < islands.Count; j++)
                    {
                        var b = islands[j];
                        if (mergedSet.Contains(b) || group.Contains(b)) continue;
                        var bMin = b.uvMin + b.translation;
                        var bMax = b.uvMax + b.translation;
                        var overlaps = bMin.x <= bboxMax.x && bMax.x >= bboxMin.x &&
                                       bMin.y <= bboxMax.y && bMax.y >= bboxMin.y;
                        if (!overlaps) continue;
                        if (!shareTexture(a, b)) continue;
                        group.Add(b);
                        bboxMin = Vector2.Min(bboxMin, bMin);
                        bboxMax = Vector2.Max(bboxMax, bMax);
                        grew = true;
                    }
                }

                if (group.Count == 1)
                {
                    result.Add(a);
                    continue;
                }

                // 合并岛：uvMin/uvMax 直接存"归一化后"的包围盒，translation=0（已是规范坐标）/
                // merged island: uvMin/uvMax store the normalized bbox directly, translation=0 (already canonical)
                var merged = new ATOIsland
                {
                    id = a.id,
                    mesh = a.mesh,
                    channel = a.channel,
                    triangles = new List<int>(),
                    merged = true,
                    mergedChildren = group,
                    uvMin = bboxMin,
                    uvMax = bboxMax,
                    translation = Vector2.zero,
                    uvArea = 0f,
                };
                foreach (var g in group)
                {
                    merged.triangles.AddRange(g.triangles);
                    merged.uvArea += g.uvArea;
                    merged.worldAreaMax = Mathf.Max(merged.worldAreaMax, g.worldAreaMax);
                    mergedSet.Add(g);
                }
                mergedSet.Add(merged);
                result.Add(merged);
            }
            return result;
        }
    }
}
