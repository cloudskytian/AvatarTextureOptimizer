using System.Collections.Generic;
using UnityEngine;

// Mesh replacement: clones meshes and remaps UVs of atlased groups to their shared normalized atlas
// rects. A vertex shared by triangles of different groups on the same channel cannot be remapped
// consistently -> the channel falls back to whole-texture scaling with a warning.
// 网格替换：克隆网格并把图集化组的 UV 重映射到其共享归一化图集矩形。
// 同一通道上被不同组三角形共享的顶点无法一致重映射 → 该通道回退整图缩放并警告。

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    public static class MeshReplacer
    {
        /// <summary>
        /// Remaps UVs of atlased groups and assigns cloned meshes to renderers.
        /// 重映射图集化组的 UV，并将克隆网格赋给渲染器。
        /// </summary>
        public static void Remap(ATOBuildContext ctx, ATOCancellation cancel)
        {
            // Group atlased groups by renderer. 按渲染器聚合图集化组。
            var byRenderer = new Dictionary<Renderer, List<UVGroup>>();
            foreach (var group in ctx.UVGroups)
            {
                bool atlased = false;
                foreach (var use in group.Uses)
                    if (!use.Skip && ctx.UseAtlas.ContainsKey(use)) { atlased = true; break; }
                if (!atlased) continue;
                if (!byRenderer.TryGetValue(group.Renderer, out var list)) { list = new List<UVGroup>(); byRenderer[group.Renderer] = list; }
                list.Add(group);
            }

            int idx = 0;
            foreach (var kv in byRenderer)
            {
                cancel.ThrowIfCancelled($"Remapping mesh UVs ({idx + 1}/{byRenderer.Count})", idx / (float)byRenderer.Count);
                var renderer = kv.Key;
                var groups = kv.Value;
                Mesh src = renderer is SkinnedMeshRenderer smr ? smr.sharedMesh : renderer.GetComponent<MeshFilter>()?.sharedMesh;
                if (src == null) continue;

                var clone = UnityEngine.Object.Instantiate(src);
                clone.name = "ATO_" + src.name;
                ctx.OriginalMeshes[renderer] = src;
                bool changed = false;

                // Per channel: compute new UVs. 每通道计算新 UV。
                var channels = new HashSet<int>();
                foreach (var g in groups) channels.Add(g.Channel);
                foreach (int channel in channels)
                {
                    var channelGroups = groups.FindAll(g => g.Channel == channel);
                    if (channelGroups.Count == 0) continue;

                    var uvList = new List<Vector2>(src.vertexCount);
                    src.GetUVs(channel, uvList);
                    if (uvList.Count == 0) continue;
                    var newUvs = new Vector2[src.vertexCount];
                    System.Array.Copy(uvList.ToArray(), newUvs, src.vertexCount);

                    // vertex -> assigned (group, island) transform. 顶点 → 分配的（组, 岛）变换。
                    var assignment = new (UVGroup group, UVIsland island, Rect normRect, int rotation)[src.vertexCount];
                    bool conflict = false;
                    foreach (var g in channelGroups)
                    {
                        foreach (var island in g.Islands)
                        {
                            foreach (int t in island.TriangleIndices)
                            {
                                for (int k = 0; k < 3; k++)
                                {
                                    int vi = t * 3 + k;
                                    if (vi >= src.vertexCount) continue;
                                    if (assignment[vi].group != null && assignment[vi].group != g)
                                    {
                                        conflict = true;
                                        break;
                                    }
                                    assignment[vi] = (g, island, island.NormalizedRect, island.Rotation);
                                }
                                if (conflict) break;
                            }
                            if (conflict) break;
                        }
                        if (conflict) break;
                    }

                    if (conflict)
                    {
                        ATOLog.Warn($"mesh {src.name} renderer {renderer.name} ch{channel}: a vertex is shared by multiple UV groups; channel falls back to whole-texture scaling");
                        continue;
                    }

                    // Apply transform to each vertex. 对每个顶点应用变换。
                    bool anyMapped = false;
                    for (int vi = 0; vi < src.vertexCount; vi++)
                    {
                        var a = assignment[vi];
                        if (a.group == null) continue;
                        var g = a.group;
                        var island = a.island;
                        var r = a.normRect;
                        var uv = uvList[vi];
                        // Island-local UV (translated into the island rect). 岛局部 UV（平移到岛矩形）。
                        float lu = (uv.x - island.BoundsMin.x) / Mathf.Max(1e-6f, island.SizeUV.x);
                        float lv = (uv.y - island.BoundsMin.y) / Mathf.Max(1e-6f, island.SizeUV.y);
                        float nu, nv;
                        if (island.Rotation == 1)
                        {
                            nu = r.x + (1f - lv) * r.width;
                            nv = r.y + lu * r.height;
                        }
                        else
                        {
                            nu = r.x + lu * r.width;
                            nv = r.y + lv * r.height;
                        }
                        newUvs[vi] = new Vector2(nu, nv);
                        anyMapped = true;
                    }
                    if (anyMapped)
                    {
                        clone.SetUVs(channel, newUvs);
                        changed = true;
                    }
                }

                if (changed)
                {
                    if (renderer is SkinnedMeshRenderer s) s.sharedMesh = clone;
                    else renderer.GetComponent<MeshFilter>().sharedMesh = clone;
                    ctx.NewMeshes[renderer] = clone;
                    ATOLog.VerboseLog($"remapped mesh {clone.name} for {renderer.name}");
                }
                else
                {
                    UnityEngine.Object.DestroyImmediate(clone);
                }
                idx++;
            }

            // AAO UV-usage compatibility: evacuate original UVs of modified channels.
            // AAO UV 使用兼容：转移被修改通道的原始 UV。
            AAOCompatBridge.EvacuateModifiedChannels(ctx);
        }
    }
}
