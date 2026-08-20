// AvatarTextureOptimizer - MeshWriter
// EN: Rebuilds meshes whose UV channels were remapped: vertices are split per island (a vertex shared by several
// islands gets one copy per island with that island's remapped UV), blend shapes are rebuilt through the new->old
// index map, and the mesh is written to the renderer as a fresh asset (original assets untouched).
// CN: 重建被重映射 UV 通道的网格：顶点按岛拆分（跨岛共享顶点为每个岛复制一份并重映射 UV），
//     形态键经 新→旧 索引映射重建，网格作为全新资产写回渲染器（原资产不受影响）。
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace net.fosa.avatar_texture_optimizer
{
    public static class MeshWriter
    {
        /// <summary>EN: Rebuilds all meshes that have remapped UV groups. / CN: 重建所有存在重映射 UV 组的网格。</summary>
        public static void RebuildAll(AtoBuildState state)
        {
            var done = new HashSet<Mesh>();
            foreach (var g in state.UvGroups)
            {
                if (g.whitelisted || g.layout == null || g.mesh == null || done.Contains(g.mesh)) continue;
                done.Add(g.mesh);
                bool anyRemap = false;
                foreach (var island in g.islands)
                    if (island.hasRemap) { anyRemap = true; break; }
                if (!anyRemap) continue;
                RebuildMesh(state, g);
            }
        }

        private static void RebuildMesh(AtoBuildState state, UvGroup group)
        {
            var mesh = group.mesh;
            var renderer = group.renderer;
            var data = group.islands.Count > 0 ? group.islands[0].owner : null;
            if (data == null) return;
            int vertexCount = mesh.vertexCount;

            // EN: All optimized channels of this mesh (each is its own UvGroup).
            // CN: 该网格的全部已优化通道（各自为独立 UV 组）。
            var optimized = new List<UvGroup>();
            foreach (var g in state.UvGroups)
                if (g.mesh == mesh && !g.whitelisted && g.layout != null) optimized.Add(g);
            if (optimized.Count == 0) return;

            // EN: triangle → island per optimized channel.
            // CN: 每优化通道的 三角形 → 岛 映射。
            var triIsland = new Dictionary<int, Dictionary<int, Island>>();
            foreach (var g in optimized)
            {
                var map = new Dictionary<int, Island>();
                foreach (var island in g.islands)
                    foreach (var t in island.triangles)
                        map[t] = island;
                triIsland[g.channel] = map;
            }

            var positions = mesh.vertices;
            var normals = mesh.normals;
            var tangents = mesh.tangents;
            var colors = mesh.colors;
            var uvChannels = new List<Vector2>[8];
            for (int c = 0; c < 8; c++)
            {
                if (mesh.uvCount > c)
                {
                    uvChannels[c] = new List<Vector2>(vertexCount);
                    mesh.GetUVs(c, uvChannels[c]);
                }
            }

            var newPos = new List<Vector3>(vertexCount);
            var newNorm = new List<Vector3>(vertexCount);
            var newTan = new List<Vector4>(vertexCount);
            var newCol = new List<Color>(vertexCount);
            var newUvs = new List<Vector2>[8];
            for (int c = 0; c < 8; c++) newUvs[c] = uvChannels[c] != null ? new List<Vector2>(vertexCount) : null;
            var newToOld = new List<int>(vertexCount);
            var vertexKey = new Dictionary<long, int>(vertexCount * 2);

            // EN: Key = old vertex index + island context per optimized channel (islandsForTri).
            // CN: 键 = 旧顶点索引 + 每优化通道的岛上下文（islandsForTri）。
            long Key(int oldIdx, Dictionary<int, Island>[] islandsPerChannel)
            {
                long k = oldIdx;
                for (int i = 0; i < optimized.Count; i++)
                {
                    var ch = optimized[i];
                    var island = islandsPerChannel[i] != null && islandsPerChannel[i].TryGetValue(oldIdx, out var isl)
                        ? isl : null;
                    k = k * 7919 + (island != null ? island.id : -100003);
                }
                return k;
            }

            // EN: Remapped UV for a vertex in a given channel & island (or original when no island/remap).
            // CN: 顶点在某通道与岛中的重映射 UV（无岛或未重映射时用原始值）。
            Vector2 RemappedUv(int oldIdx, int ch, Island island, List<Vector2> src)
            {
                Vector2 uv = src[oldIdx];
                if (island != null && island.hasRemap)
                {
                    float lx = (uv.x - island.tile.x - island.fracRect.x) / Mathf.Max(1e-6f, island.fracRect.width);
                    float ly = (uv.y - island.tile.y - island.fracRect.y) / Mathf.Max(1e-6f, island.fracRect.height);
                    uv = new Vector2(
                        island.remapRect.x + Mathf.Clamp01(lx) * island.remapRect.width,
                        island.remapRect.y + Mathf.Clamp01(ly) * island.remapRect.height);
                }
                return uv;
            }

            // EN: Iterate GLOBAL triangles (islands are keyed globally); collect output per submesh.
            // CN: 按全局三角形迭代（岛按全局键）；输出按子网格收集。
            int globalTriCount = data.allTriangles.Length / 3;
            var triSubmesh = new int[globalTriCount];
            {
                int gi = 0;
                for (int s = 0; s < data.submeshTriangles.Length; s++)
                    for (int i = 0; i < data.submeshTriangles[s].Length; i += 3)
                        triSubmesh[gi++] = s;
            }
            var subOut = new List<int>[data.submeshTriangles.Length];
            for (int s = 0; s < subOut.Length; s++) subOut[s] = new List<int>();

            for (int t = 0; t < globalTriCount; t++)
            {
                int s = triSubmesh[t];
                int a = data.allTriangles[t * 3], b = data.allTriangles[t * 3 + 1], c = data.allTriangles[t * 3 + 2];
                var islandsPerChannel = new Dictionary<int, Island>[optimized.Count];
                for (int i = 0; i < optimized.Count; i++)
                {
                    var map = triIsland[optimized[i].channel];
                    islandsPerChannel[i] = map.TryGetValue(t, out var isl) ? new Dictionary<int, Island> { [a] = isl, [b] = isl, [c] = isl } : null;
                }
                // EN: Same island for all three corners of one triangle; keep one lookup per channel.
                // CN: 同一三角形的三个角点属于同一岛；每通道只查一次。
                for (int corner = 0; corner < 3; corner++)
                {
                    int oldIdx = corner == 0 ? a : (corner == 1 ? b : c);
                    long key = Key(oldIdx, islandsPerChannel);
                    if (vertexKey.TryGetValue(key, out int ni))
                    {
                        subOut[s].Add(ni);
                        continue;
                    }
                    int vidx = newPos.Count;
                    vertexKey[key] = vidx;
                    newToOld.Add(oldIdx);
                    newPos.Add(positions.Length > oldIdx ? positions[oldIdx] : Vector3.zero);
                    newNorm.Add(normals != null && normals.Length > oldIdx ? normals[oldIdx] : Vector3.up);
                    newTan.Add(tangents != null && tangents.Length > oldIdx ? tangents[oldIdx] : new Vector4(1, 0, 0, 1));
                    newCol.Add(colors != null && colors.Length > oldIdx ? colors[oldIdx] : Color.white);
                    for (int ci = 0; ci < optimized.Count; ci++)
                    {
                        int ch = optimized[ci].channel;
                        if (uvChannels[ch] == null) continue;
                        var island = islandsPerChannel[ci] != null ? islandsPerChannel[ci][oldIdx] : null;
                        newUvs[ch].Add(RemappedUv(oldIdx, ch, island, uvChannels[ch]));
                    }
                    // EN: Non-optimized channels are copied per new vertex.
                    // CN: 未优化通道按新顶点拷贝。
                    for (int ch = 0; ch < 8; ch++)
                    {
                        if (uvChannels[ch] == null || newUvs[ch] == null) continue;
                        bool optimizedChannel = false;
                        foreach (var g in optimized)
                            if (g.channel == ch) { optimizedChannel = true; break; }
                        if (!optimizedChannel) newUvs[ch].Add(uvChannels[ch][oldIdx]);
                    }
                    subOut[s].Add(vidx);
                }
            }

            // EN: Build the new mesh asset.
            // CN: 构建新网格资产。
            var newMesh = new Mesh { name = $"ATO_{mesh.name}" };
            if (newPos.Count > 65535) newMesh.indexFormat = IndexFormat.UInt32;
            newMesh.SetVertices(newPos);
            newMesh.SetNormals(newNorm);
            if (newTan.Count > 0) newMesh.SetTangents(newTan);
            if (newCol.Count > 0) newMesh.SetColors(newCol);
            for (int ch = 0; ch < 8; ch++)
            {
                if (newUvs[ch] != null && newUvs[ch].Count == newPos.Count) newMesh.SetUVs(ch, newUvs[ch]);
            }
            newMesh.subMeshCount = subOut.Length;
            for (int s = 0; s < subOut.Length; s++)
            {
                newMesh.SetTriangles(subOut[s], s);
            }
            if (mesh.bindposes.Length > 0) newMesh.bindposes = mesh.bindposes;

            // EN: Rebuild blend shapes: each new vertex receives the delta of its source old vertex.
            // CN: 重建形态键：每个新顶点接收其源旧顶点的增量。
            if (data.hasBlendShapes && mesh.blendShapeCount > 0 && newToOld.Count > 0)
            {
                var deltaV = new Vector3[vertexCount];
                var deltaN = new Vector3[vertexCount];
                var deltaT = new Vector3[vertexCount];
                var outV = new Vector3[newToOld.Count];
                var outN = new Vector3[newToOld.Count];
                var outT = new Vector3[newToOld.Count];
                for (int shape = 0; shape < mesh.blendShapeCount; shape++)
                {
                    string name = mesh.GetBlendShapeName(shape);
                    int frames = mesh.GetBlendShapeFrameCount(shape);
                    for (int f = 0; f < frames; f++)
                    {
                        float weight = mesh.GetBlendShapeFrameWeight(shape, f);
                        mesh.GetBlendShapeFrameVertices(shape, f, deltaV, deltaN, deltaT);
                        for (int i = 0; i < outV.Length; i++) { outV[i] = Vector3.zero; outN[i] = Vector3.zero; outT[i] = Vector3.zero; }
                        for (int ni = 0; ni < newToOld.Count; ni++)
                        {
                            int oldIdx = newToOld[ni];
                            if (oldIdx < 0 || oldIdx >= vertexCount) continue;
                            outV[ni] = deltaV[oldIdx];
                            outN[ni] = deltaN[oldIdx];
                            outT[ni] = deltaT[oldIdx];
                        }
                        newMesh.AddBlendShapeFrame(name, weight, outV, outN, outT);
                    }
                }
            }

            newMesh.RecalculateBounds();
            for (int ci = 0; ci < optimized.Count; ci++)
            {
                int ch = optimized[ci].channel;
                if (ch < 8 && newMesh.uvCount > ch)
                    newMesh.RecalculateUVDistributionMetrics(ch);
            }
            try { MeshUtility.SetMeshCompression(newMesh, MeshUtility.GetMeshCompression(mesh)); }
            catch (System.Exception) { }

            // EN: Assign to every renderer that uses this mesh (shared mesh assets!).
            // CN: 赋给使用该网格的全部渲染器（网格资产可能被共用）。
            var targets = group.renderers.Count > 0 ? group.renderers : new List<Renderer> { renderer };
            foreach (var r in targets)
            {
                if (r is SkinnedMeshRenderer smr)
                {
                    smr.sharedMesh = newMesh;
                }
                else if (r is MeshRenderer mr)
                {
                    var mf = r.GetComponent<MeshFilter>();
                    if (mf != null && mf.sharedMesh == mesh) mf.sharedMesh = newMesh;
                }
            }

            // EN: NDMF must not recompute UV distribution for this mesh (done above).
            // CN: NDMF 不得重算该网格的 UV 分布（上面已计算）。
            state.Ctx.SetEnableUVDistributionRecalculation(newMesh, false);

            AtoLog.Detail($"Mesh {mesh.name}: {vertexCount} -> {newPos.Count} vertices, UVs remapped");
        }
    }
}
