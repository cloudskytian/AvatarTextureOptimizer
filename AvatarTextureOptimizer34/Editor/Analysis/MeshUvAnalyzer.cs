// AvatarTextureOptimizer - MeshUvAnalyzer
// EN: Extracts UV islands from renderer meshes (multi-channel, blend shapes, animation scale, wrap detection).
// CN: 从渲染器网格提取 UV 岛（多通道、形态键、动画缩放、wrap 检测）。
using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer
{
    /// <summary>
    /// EN: Builds MeshUvData for every optimized renderer. Island extraction is Burst-driven.
    /// CN: 为每个被优化的渲染器构建 MeshUvData。岛提取由 Burst 驱动。
    /// </summary>
    public static class MeshUvAnalyzer
    {
        public const int Grid = 1024; // 连通性网格分辨率（连接性用；岛边界来自真实几何）

        /// <summary>EN: Analyzes UVs of one renderer mesh. / CN: 分析一个渲染器网格的 UV。</summary>
        public static void Analyze(AtoBuildState state, Renderer renderer, AnimationData anim, bool skip)
        {
            Mesh mesh = GetMesh(renderer);
            if (mesh == null) return;

            var uvChannels = EnumerateUsedChannels(mesh);
            if (uvChannels.Count == 0) return;

            // EN: Effective scale = product of max scales along the transform chain (parent bones included;
            // overestimation is safe: it prevents blur, never artifacts).
            // CN: 有效缩放 = 变换链上各级最大缩放的乘积（含父骨骼；高估是安全的：防糊而非出错）。
            float maxScale = 1f;
            if (anim != null)
            {
                var t = renderer.transform;
                while (t != null)
                {
                    if (anim.maxScale.TryGetValue(t.gameObject, out float s)) maxScale *= s;
                    t = t.parent;
                }
            }

            foreach (int channel in uvChannels)
            {
                var data = AnalyzeChannel(state, renderer, mesh, channel, maxScale, skip);
                if (data != null) state.MeshUvData.Add(data);
            }
        }

        private static Mesh GetMesh(Renderer r)
        {
            if (r is SkinnedMeshRenderer smr) return smr.sharedMesh;
            if (r is MeshRenderer mr)
            {
                var mf = r.GetComponent<MeshFilter>();
                return mf != null ? mf.sharedMesh : null;
            }
            return null;
        }

        private static List<int> EnumerateUsedChannels(Mesh mesh)
        {
            var list = new List<int>();
            for (int i = 0; i < 8; i++)
            {
                if (mesh.uvCount > i && mesh.GetUVDimension(i) >= 2) list.Add(i);
            }
            return list;
        }

        private static MeshUvData AnalyzeChannel(AtoBuildState state, Renderer renderer, Mesh mesh, int channel,
            float maxScale, bool skip)
        {
            var uvs = new Vector2[mesh.vertexCount];
            mesh.GetUVs(channel, new List<Vector2>()).CopyTo(uvs, 0);
            var positions = mesh.vertices;
            if (positions.Length == 0 || uvs.Length == 0) return null;

            // EN: Combine submesh triangles; keep submesh index per triangle (material slot).
            // CN: 合并子网格三角形；记录每三角形的子网格索引（材质槽）。
            var allTris = new List<int>();
            var submeshStarts = new List<int>();
            int submeshCount = mesh.subMeshCount;
            for (int s = 0; s < submeshCount; s++)
            {
                submeshStarts.Add(allTris.Count);
                allTris.AddRange(mesh.GetTriangles(s));
            }
            submeshStarts.Add(allTris.Count);
            if (allTris.Count == 0) return null;

            int triCount = allTris.Count / 3;

            // EN: Per-triangle data. Wrap detection: a triangle is repeat-dependent when it spans a wrap seam,
            // i.e. floor(min) != floor(max) on any axis (covers both "range > 1" and "straddles the seam").
            // CN: 每三角形数据。Wrap 检测：三角形跨越 wrap 接缝（任一轴 floor(min)!=floor(max)）即依赖 repeat。
            var triMin = new NativeArray<float2>(triCount, Allocator.TempJob);
            var triMax = new NativeArray<float2>(triCount, Allocator.TempJob);
            var triLocalUv = new NativeArray<float2>(uvs.Length, Allocator.TempJob);
            var triangles = new NativeArray<int3>(triCount, Allocator.TempJob);
            bool wrapCrossing = false;

            for (int t = 0; t < triCount; t++)
            {
                int i0 = allTris[t * 3], i1 = allTris[t * 3 + 1], i2 = allTris[t * 3 + 2];
                triangles[t] = new int3(i0, i1, i2);
                float2 a = new float2(uvs[i0].x, uvs[i0].y);
                float2 b = new float2(uvs[i1].x, uvs[i1].y);
                float2 c = new float2(uvs[i2].x, uvs[i2].y);
                float2 mn = math.min(math.min(a, b), c);
                float2 mx = math.max(math.max(a, b), c);
                if (math.floor(mn.x) != math.floor(mx.x) || math.floor(mn.y) != math.floor(mx.y))
                    wrapCrossing = true;
                // EN: Local frac space per triangle (valid: no triangle crosses a seam when !wrapCrossing, and a
                // vertex shared by triangles in the same tile gets the same local value).
                // CN: 每三角形局部 frac 空间（无跨缝时有效；同平铺块内共享顶点局部值一致）。
                float2 f = new float2(math.floor(mn.x), math.floor(mn.y));
                triLocalUv[i0] = a - f;
                triLocalUv[i1] = b - f;
                triLocalUv[i2] = c - f;
                triMin[t] = LocalMin(a, b, c, mn);
                triMax[t] = LocalMax(a, b, c, mx);
            }

            var bits = new NativeArray<ulong>((Grid * Grid + 63) / 64, Allocator.TempJob);
            var labels = new NativeArray<int>(Grid * Grid, Allocator.TempJob);

            var raster = new RasterizeUvJob
            {
                uvs = triLocalUv, triangles = triangles, triMin = triMin, triMax = triMax,
                grid = Grid, bits = bits
            }.Schedule(triCount, 64);
            var fill = new FloodFillJob { bits = bits, grid = Grid, labels = labels }.Schedule(raster);
            fill.Complete();
            raster.Complete();

            // EN: World area with blend shapes (each shape at its 100-weight frame; take max across shapes).
            // CN: 含形态键的世界面积（每个形态键取 100 权重帧；跨形态键取最大）。
            var triArea = new NativeArray<float>(triCount, Allocator.TempJob);
            bool hasBlends = mesh.blendShapeCount > 0 && !skip;
            if (hasBlends)
            {
                var posArr = new NativeArray<float3>(positions.Length, Allocator.TempJob);
                for (int i = 0; i < positions.Length; i++) posArr[i] = positions[i];
                var deltaA = new NativeArray<float3>(triCount, Allocator.TempJob);
                var deltaB = new NativeArray<float3>(triCount, Allocator.TempJob);
                var deltaC = new NativeArray<float3>(triCount, Allocator.TempJob);
                var shapeArea = new NativeArray<float>(triCount, Allocator.TempJob);
                var vDelta = new Vector3[mesh.vertexCount];

                for (int shape = 0; shape < mesh.blendShapeCount; shape++)
                {
                    int frames = mesh.GetBlendShapeFrameCount(shape);
                    if (frames == 0) continue;
                    // EN: Take the frame nearest weight 100 (spec: only 0 and 100 considered).
                    // CN: 取最接近权重 100 的帧（按需求仅考虑 0 与 100）。
                    int frame = frames - 1;
                    float w0 = mesh.GetBlendShapeFrameWeight(shape, 0);
                    float w1 = mesh.GetBlendShapeFrameWeight(shape, frame);
                    if (w1 > 100f) { for (int f = frames - 1; f >= 0; f--) if (mesh.GetBlendShapeFrameWeight(shape, f) <= 100f) { frame = f; break; } }
                    mesh.GetBlendShapeFrameVertices(shape, frame, vDelta, null, null);
                    for (int t = 0; t < triCount; t++)
                    {
                        int3 tr = triangles[t];
                        deltaA[t] = vDelta[tr.x]; deltaB[t] = vDelta[tr.y]; deltaC[t] = vDelta[tr.z];
                    }
                    new TriangleAreaMaxJob
                    {
                        positions = posArr, triangles = triangles, deltaA = deltaA, deltaB = deltaB, deltaC = deltaC,
                        areas = shapeArea
                    }.Schedule(triCount, 64).Complete();
                    for (int t = 0; t < triCount; t++) triArea[t] = math.max(triArea[t], shapeArea[t]);
                }
                posArr.Dispose(); deltaA.Dispose(); deltaB.Dispose(); deltaC.Dispose(); shapeArea.Dispose();
                // EN: Without blend shapes the max-area job would double the base area; guard by only using it when hasBlends.
                // CN: 无形态键时该作业会重复计算基础面积；仅在存在形态键时使用。
            }
            else
            {
                var posArr = new NativeArray<float3>(positions.Length, Allocator.TempJob);
                for (int i = 0; i < positions.Length; i++) posArr[i] = positions[i];
                var empty = new NativeArray<float3>(0, Allocator.TempJob);
                new TriangleAreaMaxJob
                {
                    positions = posArr, triangles = triangles, deltaA = empty, deltaB = empty, deltaC = empty,
                    areas = triArea
                }.Schedule(triCount, 64).Complete();
                posArr.Dispose(); empty.Dispose();
            }

            // EN: Consolidate components into islands.
            // CN: 汇总连通域为岛。
            int maxLabel = 0;
            for (int i = 0; i < labels.Length; i++) if (labels[i] > maxLabel) maxLabel = labels[i];
            int capacity = maxLabel + triCount + 1;
            var islandMin = new NativeArray<float2>(capacity, Allocator.TempJob);
            var islandMax = new NativeArray<float2>(capacity, Allocator.TempJob);
            var islandArea = new NativeArray<float>(capacity, Allocator.TempJob);
            var islandTriCount = new NativeArray<int>(capacity, Allocator.TempJob);
            var nextLabel = new NativeArray<int>(1, Allocator.TempJob);
            var triComponent = new NativeArray<int>(triCount, Allocator.TempJob);
            for (int i = 0; i < capacity; i++)
            {
                islandMin[i] = new float2(float.MaxValue, float.MaxValue);
                islandMax[i] = new float2(float.MinValue, float.MinValue);
            }
            var collect = new CollectIslandStatsJob
            {
                uvs = triLocalUv, triangles = triangles, labels = labels, triMin = triMin, triMax = triMax,
                triArea = triArea, grid = Grid, baseLabel = maxLabel + 1, nextLabel = nextLabel,
                islandMin = islandMin, islandMax = islandMax, islandArea = islandArea,
                islandTriCount = islandTriCount, triComponent = triComponent
            }.Schedule(triCount, 64);
            collect.Complete();

            int compCount = maxLabel + nextLabel[0];
            var comps = new Dictionary<int, Island>(compCount);
            var data = new MeshUvData
            {
                mesh = mesh, renderer = renderer, channel = channel, uvs = uvs,
                positions = positions, normals = mesh.normals, tangents = mesh.tangents,
                colors = mesh.colors, hasBlendShapes = hasBlends, maxAnimationScale = maxScale,
                whitelisted = skip || wrapCrossing
            };
            data.submeshTriangles = new int[submeshCount][];
            for (int s = 0; s < submeshCount; s++)
            {
                int start = submeshStarts[s], end = submeshStarts[s + 1];
                var arr = new int[end - start];
                for (int i = 0; i < arr.Length; i++) arr[i] = allTris[start + i];
                data.submeshTriangles[s] = arr;
            }
            data.allTriangles = allTris.ToArray();

            for (int t = 0; t < triCount; t++)
            {
                int comp = triComponent[t];
                if (!comps.TryGetValue(comp, out var island))
                {
                    island = new Island
                    {
                        id = comp,
                        fracRect = new Rect(islandMin[comp].x, islandMin[comp].y,
                            Mathf.Max(0, islandMax[comp].x - islandMin[comp].x),
                            Mathf.Max(0, islandMax[comp].y - islandMin[comp].y)),
                        uvArea = islandArea[comp],
                        worldAreaM2 = islandArea[comp] * maxScale * maxScale,
                        owner = data
                    };
                    // EN: Tile = floor of raw min (from any member triangle's raw uvs).
                    // CN: 平铺块 = 原始最小值的 floor（取自成员三角形的原始 UV）。
                    int rawTri = t;
                    float2 rawMin = RawTriMin(uvs, triangles[rawTri]);
                    island.tile = new Vector2Int(Mathf.FloorToInt(rawMin.x), Mathf.FloorToInt(rawMin.y));
                    comps[comp] = island;
                    data.islands.Add(island);
                }
                island.triangles.Add(t);
                // EN: Material slot from submesh index (triangle range lookup).
                // CN: 由子网格索引得到材质槽。
                int slot = 0;
                for (int s = 0; s < submeshCount; s++)
                {
                    if (t >= submeshStarts[s] / 3 && t < submeshStarts[s + 1] / 3) { slot = s; break; }
                }
                if (!island.materialSlots.Contains(slot)) island.materialSlots.Add(slot);
            }

            triMin.Dispose(); triMax.Dispose(); triLocalUv.Dispose(); triangles.Dispose();
            bits.Dispose(); labels.Dispose(); triArea.Dispose();
            islandMin.Dispose(); islandMax.Dispose(); islandArea.Dispose(); islandTriCount.Dispose();
            nextLabel.Dispose(); triComponent.Dispose();

            AtoLog.Detail($"Mesh {mesh.name} ch{channel}: {data.islands.Count} islands" +
                          (wrapCrossing ? " (WRAP-CROSSING -> whitelist)" : ""));
            return data;
        }

        private static float2 LocalMin(float2 a, float2 b, float2 c, float2 rawMin)
        {
            float2 fa = new float2(a.x - math.floor(rawMin.x), a.y - math.floor(rawMin.y));
            float2 fb = new float2(b.x - math.floor(rawMin.x), b.y - math.floor(rawMin.y));
            float2 fc = new float2(c.x - math.floor(rawMin.x), c.y - math.floor(rawMin.y));
            return math.min(math.min(fa, fb), fc);
        }

        private static float2 LocalMax(float2 a, float2 b, float2 c, float2 rawMax)
        {
            float2 fa = new float2(a.x - math.floor(rawMax.x), a.y - math.floor(rawMax.y));
            float2 fb = new float2(b.x - math.floor(rawMax.x), b.y - math.floor(rawMax.y));
            float2 fc = new float2(c.x - math.floor(rawMax.x), c.y - math.floor(rawMax.y));
            return math.max(math.max(fa, fb), fc);
        }

        private static float2 RawTriMin(Vector2[] uvs, int3 tri)
        {
            return new float2(
                math.min(uvs[tri.x].x, math.min(uvs[tri.y].x, uvs[tri.z].x)),
                math.min(uvs[tri.x].y, math.min(uvs[tri.y].y, uvs[tri.z].y)));
        }
    }
}
