// MeshAnalysis.cs - UV island extraction with Unity Burst. / 使用 Unity Burst 的 UV 岛提取。
// Steps / 步骤:
//  1. weld vertices by quantized UV position (same channel) / 按量化UV焊接顶点
//  2. union-find triangles sharing welded UV vertices / 共享焊接点的三角形并查集
//  3. per-island integer shift into [0,1] (repeat-safe); bbox > 1 or straddling seam -> wrapped -> whitelist
//     每岛整数平移归一到[0,1]（repeat安全）；包围盒>1或跨缝->wrapped->白名单
//  4. merge overlapping islands (mirror layouts) / 合并重叠岛（镜像摆法）
//  5. world area = max over blendshape weights {0,100} and animated scale / 世界面积取形态键0/100与动画缩放的最大值
using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using Fosa.ATO.Editor.Core;
using System.Linq;

namespace Fosa.ATO.Editor.Analysis
{
    public static class MeshAnalysis
    {
        private const float WeldEps = 1e-4f; // quantize step 1e-4 in UV space / UV空间量化步长

        /// <summary>Extract islands of one (mesh, channel) over the given submesh triangle list. / 提取一个(网格,通道)在给定三角形集合上的岛。</summary>
        public static List<Island> Extract(Mesh mesh, int channel, Renderer renderer, AvatarScan scan, string rendererPath)
        {
            using (ATOLog.Scope($"Islands:{mesh.name}#uv{channel}"))
            {
                // multi-channel aware read / 支持多通道读取
                var uvList = new List<Vector2>(mesh.vertexCount);
                mesh.GetUVs(channel, uvList);
                if (uvList.Count != mesh.vertexCount)
                {
                    if (channel == 0 && uvList.Count == 0) { /* mesh without UV0: nothing to do / 无UV0网格：跳过 */ }
                    else ATOLog.Warn($"UV channel {channel} of {mesh.name} is not 2D or missing; skipped / UV通道非2D或缺失，已跳过");
                    return new List<Island>();
                }
                var uvs = new NativeArray<Vector2>(uvList.ToArray(), Allocator.TempJob);
                var bases = new NativeArray<Vector3>(mesh.vertices, Allocator.TempJob);
                var tris = new NativeArray<int>(TrianglesOf(mesh), Allocator.TempJob);
                var outIsland = new NativeArray<int>(mesh.vertexCount, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
                var outTriIsland = new NativeArray<int>(tris.Length / 3, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
                var outCount = new NativeArray<int>(1, Allocator.TempJob);
                var job = new IslandJob { Uv = uvs.Reinterpret<float2>(8), Pos = bases.Reinterpret<float3>(12), Tris = tris, IslandOfVertex = outIsland, IslandOfTriangle = outTriIsland, IslandCountBuf = outCount };
                job.Schedule().Complete();

                int islandCount = outCount[0];
                var islands = new List<Island>();
                for (int i = 0; i < islandCount; i++) islands.Add(new Island { id = i });
                var verts = new List<int>[islandCount];
                for (int v = 0; v < outIsland.Length; v++)
                {
                    int isl = outIsland[v];
                    if (isl < 0) continue;
                    if (verts[isl] == null) verts[isl] = new List<int>();
                    verts[isl].Add(v);
                }
                for (int t = 0; t < outTriIsland.Length; t++)
                {
                    int isl = outTriIsland[t];
                    if (isl < 0) continue;
                    islands[isl].triangles = AppendTri(islands[isl].triangles, tris, t);
                }

                // bboxes + shift / 包围盒与平移
                for (int i = 0; i < islands.Count; i++)
                {
                    var isl = islands[i];
                    if (isl.triangles == null || isl.triangles.Length == 0) { islands.RemoveAt(i--); continue; }
                    isl.vertices = verts[i]?.ToArray() ?? Array.Empty<int>();
                    ComputeBBox(isl, uvs);
                }

                // wrapped check & shift normalization (post-weld, mod-1 semantics) / 跨缝检查与平移归一
                foreach (var isl in islands)
                {
                    Vector2 size = isl.uvMax - isl.uvMin;
                    if (size.x > 1f + WeldEps || size.y > 1f + WeldEps) { isl.wrapped = true; continue; }
                    Vector2 shift = new Vector2(-Mathf.Floor(isl.uvMin.x + 1e-5f), -Mathf.Floor(isl.uvMin.y + 1e-5f));
                    // bring min into [0,1) / 将min移入[0,1)
                    if (isl.uvMax.x + shift.x > 1f + WeldEps || isl.uvMax.y + shift.y > 1f + WeldEps) { isl.wrapped = true; continue; }
                    isl.uvShift = shift;
                    isl.uvMin += shift; isl.uvMax += shift;
                }

                // merge overlapping island bboxes (same texture area, e.g. mirrored) / 合并包围盒重叠的岛
                MergeOverlapping(islands);

                // world area incl. blendshapes & animated scale / 世界面积（含形态键与动画缩放）
                ComputeWorldAreas(islands, mesh, bases, renderer, scan, rendererPath);

                uvs.Dispose(); bases.Dispose(); tris.Dispose(); outIsland.Dispose(); outTriIsland.Dispose(); outCount.Dispose();
                ATOLog.Detail($"mesh={mesh.name} uv{channel}: {islands.Count} islands, wrapped={islands.Count(x => x.wrapped)}");
                return islands;
            }
        }

        private static int[] TrianglesOf(Mesh mesh)
        {
            // all submeshes concatenated / 全部子网格拼接
            int total = 0;
            for (int s = 0; s < mesh.subMeshCount; s++) total += (int)mesh.GetIndexCount(s);
            var all = new int[total];
            int o = 0;
            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                var part = mesh.GetTriangles(s);
                part.CopyTo(all, o); o += part.Length;
            }
            return all;
        }

        private static int[] AppendTri(int[] cur, NativeArray<int> tris, int t)
        {
            int b = t * 3;
            int n = cur?.Length ?? 0;
            var arr = new int[n + 3];
            if (n > 0) Array.Copy(cur, 0, arr, 0, n);
            arr[n] = tris[b]; arr[n + 1] = tris[b + 1]; arr[n + 2] = tris[b + 2];
            return arr;
        }

        private static void ComputeBBox(Island isl, NativeArray<Vector2> uvs)
        {
            var min = new Vector2(float.MaxValue, float.MaxValue); var max = new Vector2(float.MinValue, float.MinValue);
            foreach (var v in isl.vertices)
            {
                var uv = uvs[v];
                if (uv.x < min.x) min.x = uv.x; if (uv.y < min.y) min.y = uv.y;
                if (uv.x > max.x) max.x = uv.x; if (uv.y > max.y) max.y = uv.y;
            }
            isl.uvMin = min; isl.uvMax = max;
        }

        /// <summary>Merge islands whose (post-shift) bboxes intersect. / 合并（平移后）包围盒相交的岛。</summary>
        private static void MergeOverlapping(List<Island> islands)
        {
            bool merged = true;
            while (merged)
            {
                merged = false;
                for (int i = 0; i < islands.Count && !merged; i++)
                    for (int j = i + 1; j < islands.Count && !merged; j++)
                    {
                        var a = islands[i]; var b = islands[j];
                        if (a.wrapped || b.wrapped) continue;
                        if (a.uvMin.x <= b.uvMax.x && b.uvMin.x <= a.uvMax.x &&
                            a.uvMin.y <= b.uvMax.y && b.uvMin.y <= a.uvMax.y)
                        {
                            a.uvMin = Vector2.Min(a.uvMin, b.uvMin); a.uvMax = Vector2.Max(a.uvMax, b.uvMax);
                            a.vertices = Concat(a.vertices, b.vertices);
                            a.triangles = Concat(a.triangles, b.triangles);
                            islands.RemoveAt(j); merged = true;
                        }
                    }
            }
        }

        private static T[] Concat<T>(T[] a, T[] b)
        {
            if (a == null || a.Length == 0) return b;
            if (b == null || b.Length == 0) return a;
            var r = new T[a.Length + b.Length];
            Array.Copy(a, 0, r, 0, a.Length); Array.Copy(b, 0, r, a.Length, b.Length);
            return r;
        }

        // ------------------------------------------------------------------
        // World area / 世界面积
        // ------------------------------------------------------------------

        private static void ComputeWorldAreas(List<Island> islands, Mesh mesh, NativeArray<Vector3> basePos, Renderer r, AvatarScan scan, string rendererPath)
        {
            float scale = 1f;
            if (r != null)
            {
                var ls = r.transform.lossyScale;
                scale = Mathf.Max(Mathf.Max(ls.x, ls.y), ls.z);
                scale *= AnimationScanner.MaxScaleOnChain(scan, scan.root.transform, r);
            }
            using (ATOLog.Scope("WorldArea"))
            {
                var pos = new NativeArray<Vector3>(basePos.ToArray(), Allocator.TempJob);
                var jobAreas = new NativeArray<float>(islands.Count, Allocator.TempJob);
                var trisFlat = FlatTriangles(islands);
                var triIsland = TriIslandMap(islands);
                new AreaJob { Pos = pos.Reinterpret<float3>(12), Tris = trisFlat, TriIsland = triIsland, Areas = jobAreas, UniformScale = scale }
                    .Schedule().Complete();

                // blendshape 0/100 max per shape / 每个形态键取0/100最大
                int shapeCount = mesh.blendShapeCount;
                if (shapeCount > 0)
                {
                    var deltas = new NativeArray<Vector3>(mesh.vertexCount, Allocator.TempJob);
                    for (int s = 0; s < shapeCount; s++)
                    {
                        var d = new Vector3[mesh.vertexCount];
                        var dummy = new Vector3[mesh.vertexCount];
                        int frame = mesh.GetBlendShapeFrameCount(s) - 1; // heaviest frame / 最重帧
                        mesh.GetBlendShapeFrameVertices(s, frame, d, dummy, null);
                        deltas.CopyFrom(d);
                        new ShapeAreaJob { Base = pos.Reinterpret<float3>(12), Delta = deltas.Reinterpret<float3>(12), Tris = trisFlat, TriIsland = triIsland, Areas = jobAreas, UniformScale = scale }
                            .Schedule().Complete();
                    }
                    deltas.Dispose();
                }
                for (int i = 0; i < islands.Count; i++) islands[i].worldAreaM2 = jobAreas[i];
                pos.Dispose(); jobAreas.Dispose(); trisFlat.Dispose(); triIsland.Dispose();
            }
        }

        private static NativeArray<int> FlatTriangles(List<Island> islands)
        {
            int n = 0; foreach (var i in islands) n += i.triangles?.Length ?? 0;
            var a = new NativeArray<int>(n, Allocator.TempJob);
            int o = 0;
            foreach (var i in islands) { if (i.triangles == null) continue; for (int k = 0; k < i.triangles.Length; k++) a[o + k] = i.triangles[k]; o += i.triangles.Length; }
            return a;
        }

        private static NativeArray<int> TriIslandMap(List<Island> islands)
        {
            int n = 0; foreach (var i in islands) n += (i.triangles?.Length ?? 0) / 3;
            var a = new NativeArray<int>(n, Allocator.TempJob);
            int o = 0;
            for (int idx = 0; idx < islands.Count; idx++)
            {
                int tc = (islands[idx].triangles?.Length ?? 0) / 3;
                for (int k = 0; k < tc; k++) a[o + k] = idx;
                o += tc;
            }
            return a;
        }

        // ------------------------------------------------------------------
        // Burst jobs / Burst作业
        // ------------------------------------------------------------------

        /// <summary>Weld by quantized UV + union-find + island labeling. / 量化UV焊接+并查集+岛标号。</summary>
        [BurstCompile]
        private struct IslandJob : IJob
        {
            public NativeArray<float2> Uv;
            [ReadOnly] public NativeArray<float3> Pos;
            [ReadOnly] public NativeArray<int> Tris;
            public NativeArray<int> IslandOfVertex;
            public NativeArray<int> IslandOfTriangle;
            public NativeArray<int> IslandCountBuf; // [0] = island count on completion / 完成后[0]=岛数

            public void Execute()
            {
                int vc = IslandOfVertex.Length;
                // quantized key per vertex / 每顶点量化键
                var keys = new NativeArray<long>(vc, Allocator.Temp);
                for (int i = 0; i < vc; i++)
                {
                    long qx = (long)math.round(Uv[i].x / WeldEps);
                    long qy = (long)math.round(Uv[i].y / WeldEps);
                    keys[i] = (qx + 0x40000000) * 0x20000000L + (qy + 0x40000000);
                }
                // union-find over welded keys via hashing / 通过哈希对焊接键并查集
                var parent = new NativeArray<int>(vc, Allocator.Temp);
                for (int i = 0; i < vc; i++) parent[i] = i;
                var table = new NativeParallelHashMap<long, int>(vc, Allocator.Temp);
                for (int i = 0; i < vc; i++)
                {
                    if (table.TryGetValue(keys[i], out int first)) Union(parent, first, i);
                    else table[keys[i]] = i;
                }
                // triangles join by their welded verts (same vertex => same uv so vertex union suffices)
                // 三角形通过焊接顶点连接（同顶点=>同UV，顶点并查集足够）
                for (int t = 0; t < Tris.Length; t += 3)
                {
                    Union(parent, Tris[t], Tris[t + 1]);
                    Union(parent, Tris[t], Tris[t + 2]);
                }
                // compact island ids / 压缩岛编号
                for (int i = 0; i < vc; i++) IslandOfVertex[i] = -1;
                var remap = new NativeArray<int>(vc, Allocator.Temp);
                for (int i = 0; i < vc; i++) remap[i] = -1;
                int count = 0;
                for (int i = 0; i < vc; i++)
                {
                    int root = Find(parent, i);
                    if (remap[root] < 0) remap[root] = count++;
                    IslandOfVertex[i] = remap[root];
                }
                for (int t = 0; t < IslandOfTriangle.Length; t++) IslandOfTriangle[t] = IslandOfVertex[Tris[t * 3]];
                IslandCountBuf[0] = count;
                keys.Dispose(); parent.Dispose(); table.Dispose(); remap.Dispose();
            }

            private int Find(NativeArray<int> p, int i)
            { while (p[i] != i) { p[i] = p[p[i]]; i = p[i]; } return i; }

            private void Union(NativeArray<int> p, int a, int b)
            { a = Find(p, a); b = Find(p, b); if (a != b) p[b] = a; }
        }

        /// <summary>Accumulate max triangle area per island. / 累计每岛最大三角形面积。</summary>
        [BurstCompile]
        private struct AreaJob : IJob
        {
            [ReadOnly] public NativeArray<float3> Pos;
            [ReadOnly] public NativeArray<int> Tris;
            [ReadOnly] public NativeArray<int> TriIsland;
            public NativeArray<float> Areas;
            public float UniformScale;

            public void Execute()
            {
                for (int i = 0; i < Areas.Length; i++) Areas[i] = 0f;
                float s2 = UniformScale * UniformScale;
                for (int t = 0; t < Tris.Length; t += 3)
                {
                    float3 a = Pos[Tris[t]], b = Pos[Tris[t + 1]], c = Pos[Tris[t + 2]];
                    float area = 0.5f * math.length(math.cross(b - a, c - a)) * s2;
                    int isl = TriIsland[t / 3];
                    if (area > Areas[isl]) Areas[isl] = area;
                }
            }
        }

        /// <summary>Blendshape@100 area, keep max. / 形态键@100面积，保留最大。</summary>
        [BurstCompile]
        private struct ShapeAreaJob : IJob
        {
            [ReadOnly] public NativeArray<float3> Base;
            [ReadOnly] public NativeArray<float3> Delta;
            [ReadOnly] public NativeArray<int> Tris;
            [ReadOnly] public NativeArray<int> TriIsland;
            public NativeArray<float> Areas;
            public float UniformScale;

            public void Execute()
            {
                float s2 = UniformScale * UniformScale;
                for (int t = 0; t < Tris.Length; t += 3)
                {
                    int i0 = Tris[t], i1 = Tris[t + 1], i2 = Tris[t + 2];
                    float3 a = Base[i0] + Delta[i0], b = Base[i1] + Delta[i1], c = Base[i2] + Delta[i2];
                    float area = 0.5f * math.length(math.cross(b - a, c - a)) * s2;
                    int isl = TriIsland[t / 3];
                    if (area > Areas[isl]) Areas[isl] = area;
                }
            }
        }
    }
}
