// ============================================================================
// AvatarTextureOptimizer (net.fosa.avatar-texture-optimizer)
// UV/IslandExtractor.cs — UV 岛提取 / UV island extraction
//
// 需求:
//  - 按网格拓扑连通性聚簇（共享顶点索引即共享 UV）。
//  - 同贴图内重叠岛合并（保守: AABB 相交即合并，绝不导致采样错误）。
//  - UV 越界: 可整体平移归一到 [0,1]（不跨 wrap 缝）→ 归一化重映射；
//    越界且跨缝依赖 repeat 采样 → 视作白名单 + warning。
//  - 形态键面积: 每个形态键仅取 0 与 100 的二者最大值，不考虑组合。
//  - 动画缩放面积: 按最大缩放时的面积算。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor
{
    /// <summary>
    /// UV 岛提取器 / UV island extractor.
    /// </summary>
    public static class IslandExtractor
    {
        /// <summary>
        /// 对单个 UV 组提取岛 / Extract islands for one UV group.
        /// </summary>
        public static void ExtractGroup(UVGroup group, ATOComponent cfg, AnimationData anim,
            List<Renderer> referencingRenderers)
        {
            var mesh = group.mesh;
            var uvs = new List<Vector2>();
            mesh.GetUVs(group.uvChannel, uvs);
            if (uvs.Count == 0) return;

            var triangles = mesh.triangles; // 全部子网格的三角形 / all submesh triangles
            int triCount = triangles.Length / 3;

            // 1. 并查集聚簇（共享边的三角形连通） / union-find over shared edges
            var parent = new int[triCount];
            for (int i = 0; i < triCount; i++) parent[i] = i;
            int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
            void Union(int a, int b) { int ra = Find(a), rb = Find(b); if (ra != rb) parent[ra] = rb; }

            var edgeOwner = new Dictionary<(int, int), int>();
            for (int t = 0; t < triCount; t++)
            {
                int i0 = triangles[t * 3], i1 = triangles[t * 3 + 1], i2 = triangles[t * 3 + 2];
                var e01 = EdgeKey(i0, i1);
                var e12 = EdgeKey(i1, i2);
                var e20 = EdgeKey(i2, i0);
                TryUnion(t, e01, edgeOwner, Union);
                TryUnion(t, e12, edgeOwner, Union);
                TryUnion(t, e20, edgeOwner, Union);
            }

            // 2. 按根分组 / group by root
            var groups = new Dictionary<int, List<int>>();
            for (int t = 0; t < triCount; t++)
            {
                int r = Find(t);
                if (!groups.TryGetValue(r, out var list))
                {
                    list = new List<int>();
                    groups[r] = list;
                }
                list.Add(t);
            }

            // 3. 世界面积（形态键 0/100 取最大） / world area (blendshape max of 0/100)
            var vertices = mesh.vertices;
            var worldArea = ComputeWorldAreas(mesh, vertices, triangles, triCount);

            // 4. 动画缩放因子（引用该网格的渲染器取最大） / max animated scale factor
            float scaleFactor = 1f;
            foreach (var r in referencingRenderers)
            {
                float f = 1f;
                Vector3 s = r.transform.localScale;
                f = Mathf.Max(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z));
                if (anim.maxScale.TryGetValue(r.transform, out var ms))
                {
                    f = Mathf.Max(f, Mathf.Max(Mathf.Abs(ms.x), Mathf.Abs(ms.y), Mathf.Abs(ms.z)));
                }
                scaleFactor = Mathf.Max(scaleFactor, f);
            }
            float areaFactor = scaleFactor * scaleFactor;

            // 5. 构建岛 / build islands
            var islands = new List<Island>();
            foreach (var kv in groups)
            {
                var island = new Island { group = group, triangles = kv.Value };
                // UV 包围盒 / UV bbox
                float minU = float.MaxValue, minV = float.MaxValue, maxU = float.MinValue, maxV = float.MinValue;
                double uvArea = 0;
                foreach (var t in kv.Value)
                {
                    int i0 = triangles[t * 3], i1 = triangles[t * 3 + 1], i2 = triangles[t * 3 + 2];
                    var a = uvs[i0]; var b = uvs[i1]; var c = uvs[i2];
                    minU = Mathf.Min(minU, a.x, b.x, c.x);
                    minV = Mathf.Min(minV, a.y, b.y, c.y);
                    maxU = Mathf.Max(maxU, a.x, b.x, c.x);
                    maxV = Mathf.Max(maxV, a.y, b.y, c.y);
                    uvArea += Math.Abs(Cross(a, b, c)) * 0.5;
                }
                island.uvMin = new Vector2(minU, minV);
                island.uvMax = new Vector2(maxU, maxV);
                island.uvArea = (float)uvArea;

                // 越界处理 / out-of-bounds handling
                var shift = Vector2.zero;
                bool oob = minU < 0f || minV < 0f || maxU > 1f || maxV > 1f;
                if (oob)
                {
                    int fU0 = (int)Math.Floor(minU), fU1 = (int)Math.Floor(maxU);
                    int fV0 = (int)Math.Floor(minV), fV1 = (int)Math.Floor(maxV);
                    if (fU0 == fU1 && fV0 == fV1)
                    {
                        // 可整体平移归一 / translatable into [0,1]
                        shift = new Vector2(-fU0, -fV0);
                        island.uvMin += shift;
                        island.uvMax += shift;
                        island.shift = shift;
                    }
                    else
                    {
                        // 跨 wrap 缝 → 白名单 / crosses wrap seam → whitelist
                        group.whitelisted = true;
                        group.whitelistReason = "oob-cross-seam";
                        Log.Warning(LogFmt.Warn(LogKeys.OobRepeat, group.mesh.name));
                        return; // 该组直接整组走整图缩放 / whole group falls back to whole-texture scaling
                    }
                }

                // 世界面积 / world area (per triangle max, × scale factor²)
                double wa = 0;
                foreach (var t in kv.Value)
                {
                    wa += worldArea[t];
                }
                island.worldArea = (float)(wa * areaFactor);

                // 密度尺寸需求 / density size requirements
                float sqrtA = Mathf.Sqrt(Mathf.Max(island.worldArea, 1e-8f));
                island.densityLo = cfg.minPixelDensity * sqrtA;
                island.densityHi = cfg.maxPixelDensity * sqrtA;

                // 原像素短边（组内最大原尺寸） / original short side
                island.origShortSide = group.maxOriginalShortSide;

                islands.Add(island);
            }

            // 6. 重叠岛合并（AABB 相交保守合并） / merge overlapping islands (conservative AABB)
            MergeOverlapping(islands);

            group.islands = islands;
        }

        private static void TryUnion(int t, (int, int) edge, Dictionary<(int, int), int> edgeOwner, Action<int, int> union)
        {
            if (edgeOwner.TryGetValue(edge, out var owner))
            {
                union(t, owner);
            }
            else
            {
                edgeOwner[edge] = t;
            }
        }

        private static (int, int) EdgeKey(int a, int b) => a < b ? (a, b) : (b, a);

        private static float Cross(Vector2 a, Vector2 b, Vector2 c)
        {
            return (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);
        }

        /// <summary>
        /// 计算每个三角形的最大面积（基础 vs 各形态键 100）/
        /// Per-triangle max area (base vs each blendshape at 100).
        /// </summary>
        private static float[] ComputeWorldAreas(Mesh mesh, Vector3[] vertices, int[] triangles, int triCount)
        {
            var areas = new float[triCount];

            // 基础面积 / base areas
            Parallel.For(0, triCount, t =>
            {
                int i0 = triangles[t * 3], i1 = triangles[t * 3 + 1], i2 = triangles[t * 3 + 2];
                areas[t] = TriArea(vertices[i0], vertices[i1], vertices[i2]);
            });


            // 形态键（仅 0 与 100，取最大；多键不组合）/ blendshapes (0 & 100 only, max; no combos)
            int shapeCount = mesh.blendShapeCount;
            if (shapeCount == 0) return areas;

            var baseVerts = vertices;
            var delta = new Vector3[vertices.Length];
            for (int s = 0; s < shapeCount; s++)
            {
                int frame = FindFrameAt100(mesh, s);
                if (frame < 0) continue;
                Array.Clear(delta, 0, delta.Length);
                mesh.GetBlendShapeFrameVertices(s, frame, delta, null, null);

                Parallel.For(0, triCount, t =>
                {
                    int i0 = triangles[t * 3], i1 = triangles[t * 3 + 1], i2 = triangles[t * 3 + 2];
                    var a = baseVerts[i0] + delta[i0];
                    var b = baseVerts[i1] + delta[i1];
                    var c = baseVerts[i2] + delta[i2];
                    float area = TriArea(a, b, c);
                    if (area > areas[t]) areas[t] = area;
                });
            }
            return areas;
        }

        private static int FindFrameAt100(Mesh mesh, int shape)
        {
            int count = mesh.GetBlendShapeFrameCount(shape);
            if (count == 0) return -1;
            for (int i = 0; i < count; i++)
            {
                if (Mathf.Approximately(mesh.GetBlendShapeFrameWeight(shape, i), 100f)) return i;
            }
            // 无 100 帧 → 取权重最大的一帧 / no 100-frame → frame with max weight
            int best = 0; float bestW = -1;
            for (int i = 0; i < count; i++)
            {
                float w = mesh.GetBlendShapeFrameWeight(shape, i);
                if (w > bestW) { bestW = w; best = i; }
            }
            return best;
        }

        private static float TriArea(Vector3 a, Vector3 b, Vector3 c)
        {
            var ab = b - a;
            var ac = c - a;
            var cross = Vector3.Cross(ab, ac);
            return cross.magnitude * 0.5f;
        }

        /// <summary>
        /// 保守重叠合并：AABB 相交的岛合并为一个 / Conservative overlap merge: AABB-intersecting islands merge.
        /// </summary>
        private static void MergeOverlapping(List<Island> islands)
        {
            if (islands.Count < 2) return;
            bool merged = true;
            while (merged)
            {
                merged = false;
                for (int i = 0; i < islands.Count && !merged; i++)
                {
                    for (int j = i + 1; j < islands.Count && !merged; j++)
                    {
                        var a = islands[i];
                        var b = islands[j];
                        if (AabbOverlap(a, b))
                        {
                            // 合并 b 进 a / merge b into a
                            a.triangles.AddRange(b.triangles);
                            a.uvMin = Vector2.Min(a.uvMin, b.uvMin);
                            a.uvMax = Vector2.Max(a.uvMax, b.uvMax);
                            a.uvArea += b.uvArea;
                            a.worldArea = Mathf.Max(a.worldArea, b.worldArea); // 合并后取保守值 / conservative
                            islands.RemoveAt(j);
                            merged = true;
                        }
                    }
                }
            }
        }

        private static bool AabbOverlap(Island a, Island b)
        {
            return a.uvMin.x <= b.uvMax.x && a.uvMax.x >= b.uvMin.x &&
                   a.uvMin.y <= b.uvMax.y && a.uvMax.y >= b.uvMin.y;
        }
    }
}
