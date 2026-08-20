// ============================================================================
// ATO - UV remapping
// ATO - UV 重映射
//
// Writes each atlased island's remapped UVs back into the mesh's UV channel
// (all vertices of the island, once). Rotation of the island's content is
// folded into the UV mapping so no tangent data is ever modified.
// AAO UV-channel evacuation (SkinnedMeshRenderer) is applied right before
// the write.
// 将每个已图集化岛的重映射 UV 写回网格 UV 通道（岛的全部顶点，各一次）。岛
// 内容旋转被折叠进 UV 映射，绝不改动切线数据。AAO UV 通道撤离
// （SkinnedMeshRenderer）在写入前应用。
// ============================================================================

#region

using System.Collections.Generic;
using net.fosa.AvatarTextureOptimizer.Editor.Analysis;
using net.fosa.AvatarTextureOptimizer.Editor.Core;
using net.fosa.AvatarTextureOptimizer.Editor.Interop;
using UnityEngine;

#endregion

namespace net.fosa.AvatarTextureOptimizer.Editor.Atlas
{
    public static class UVRemapper
    {
        /// <summary>Remaps all atlased islands into their meshes.
        /// 将所有已图集化岛重映射到网格。</summary>
        public static void Remap(ATOContext ctx)
        {
            var an = ctx.Analysis;
            var log = ctx.Log;
            if (an.PackedResult == null) return;

            var byMeshCh = new Dictionary<(Mesh mesh, int ch), List<ATOUVIsland>>();
            foreach (var island in an.Islands)
            {
                if (island.AtlasPage < 0) continue;
                if (island.NoRemap) continue; // keep original UVs  保持原 UV
                var key = (island.UVSet.Mesh, island.UVSet.Channel);
                if (!byMeshCh.TryGetValue(key, out var list))
                {
                    list = new List<ATOUVIsland>();
                    byMeshCh[key] = list;
                }
                list.Add(island);
            }

            foreach (var ((mesh, ch), islands) in byMeshCh)
            {
                ctx.Session.Check("Atlas 图集合成");

                // AAO evacuation for SkinnedMeshRenderer channels
                // SkinnedMeshRenderer 通道的 AAO 撤离
                var renderer = islands[0].UVSet.Renderer;
                if (renderer is SkinnedMeshRenderer smr && AAOInterop.Available)
                {
                    if (AAOInterop.IsTexCoordUsed(smr, ch))
                    {
                        int free = -1;
                        for (int m = 0; m < 8; m++)
                        {
                            if (m != ch && !AAOInterop.IsTexCoordUsed(smr, m))
                            {
                                free = m;
                                break;
                            }
                        }
                        if (free < 0 || !AAOInterop.TryRegisterEvacuation(smr, ch, free))
                        {
                            log.Warn(ATOLogMask.Atlas,
                                $"AAO evacuation failed for channel {ch} on \"{renderer.name}\" - " +
                                $"UV remap skipped for that channel (original UVs kept, textures " +
                                "remain whole-image). AAO 撤离失败，跳过该通道重映射。");
                            continue;
                        }
                        log.V(ATOLogMask.Atlas,
                            $"AAO evacuation: channel {ch} -> {free} on \"{renderer.name}\". AAO 通道撤离。");
                    }
                }

                var uvs = UVIslandExtractor.GetUVs(mesh, ch);
                if (uvs == null) continue;
                var copy = new Vector2[uvs.Length];
                System.Array.Copy(uvs, copy, uvs.Length);

                var done = new HashSet<int>();
                foreach (var island in islands)
                {
                    var page = an.PackedResult.Pages[island.AtlasPage];
                    float uvW = Mathf.Max(1e-6f, island.MaxUV.x - island.MinUV.x);
                    float uvH = Mathf.Max(1e-6f, island.MaxUV.y - island.MinUV.y);
                    float w0 = island.Rot90 == 1 ? island.AtlasH : island.AtlasW;
                    float h0 = island.Rot90 == 1 ? island.AtlasW : island.AtlasH;

                    foreach (int v in island.Triangles)
                    {
                        if (!done.Add(v)) continue;
                        var u = uvs[v];
                        float fx = Mathf.Clamp01((u.x - island.MinUV.x) / uvW);
                        float fy = Mathf.Clamp01((u.y - island.MinUV.y) / uvH);
                        float cx = fx * w0;
                        float cy = (1f - fy) * h0;
                        float px, py;
                        if (island.Rot90 == 1)
                        {
                            px = island.AtlasPos.x + (h0 - cy);
                            py = island.AtlasPos.y + cx;
                        }
                        else
                        {
                            px = island.AtlasPos.x + cx;
                            py = island.AtlasPos.y + cy;
                        }
                        copy[v] = new Vector2(
                            px / page.W,
                            1f - py / page.H);
                    }
                }
                mesh.SetUV(ch, copy);
            }
            log.Info(ATOLogMask.Atlas,
                $"UV remap done: {byMeshCh.Count} (mesh, channel) pairs. UV 重映射完成。");
        }
    }
}
