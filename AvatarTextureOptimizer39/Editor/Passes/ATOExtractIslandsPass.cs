// Avatar Texture Optimizer (ATO)
// Bilingual comments: English + Simplified Chinese. 双语注释：英文 + 简体中文。

using System.Collections.Generic;
using System.Linq;
using AvatarTextureOptimizer.Editor.Core;
using AvatarTextureOptimizer.Editor.UVIsland;
using nadena.dev.ndmf;
using UnityEngine;

namespace AvatarTextureOptimizer.Editor.Passes
{
    /// <summary>
    /// Pass 5 — extract UV islands per (renderer, submesh, UV channel), normalize
    /// out-of-bounds-but-translatable UVs into [0,1], and whitelist seam-crossing islands.
    ///
    /// Pass 5 —— 按 (渲染器, 子网格, UV 通道) 提取 UV 岛；将越界但可平移的 UV 归一到
    /// [0,1]；跨缝岛列入白名单。
    /// </summary>
    public sealed class ATOExtractIslandsPass : Pass<ATOExtractIslandsPass>
    {
        public override string DisplayName => "ATO: Extract UV islands / 提取 UV 岛";

        protected override void Execute(BuildContext context)
        {
            var state = context.GetState<ATOBuildState>();
            if (state.Component == null) return;
            state.BeginStage("Extract UV islands / 提取 UV 岛");

            using var _ = ATOLog.Time("Extract islands");

            foreach (var kv in state.SubmeshBindings)
            {
                var (renderer, submesh) = kv.Key;
                var bindings = kv.Value;
                if (bindings.Count == 0) continue;

                var mesh = renderer is SkinnedMeshRenderer smr ? smr.sharedMesh
                    : renderer is MeshRenderer mr ? mr.GetComponent<MeshFilter>()?.sharedMesh
                    : null;
                if (mesh == null) continue;

                var islands = ATOUVIslandExtractor.Extract(mesh, submesh);

                // Group bindings by UV channel. 按 UV 通道分组绑定。
                var byChannel = bindings.GroupBy(b => b.UVChannel);

                foreach (var chGroup in byChannel)
                {
                    int channel = chGroup.Key;
                    var textureRecs = chGroup.Select(b => b.Texture).Distinct()
                        .Where(t => state.Textures.ContainsKey(t))
                        .Select(t => state.Textures[t]).ToList();

                    // Merge overlapping islands within the same texture/channel. 合并同通道重叠岛。
                    var mergedIslands = ATOOverlapMerger.Merge(islands, channel);

                    foreach (var island in mergedIslands)
                    {
                        var bounds = island.UvBounds[channel];
                        if (bounds.width <= 0 || bounds.height <= 0) continue;

                        if (island.CrossesSeam(channel))
                        {
                            ATOLog.Warning($"Island on {renderer.name} crosses a UV seam (channel {channel}); " +
                                           $"whitelisting its textures. / UV 跨缝，贴图列入白名单。");
                            foreach (var t in textureRecs) state.SkippedTextures.Add(t.Texture);
                            continue;
                        }

                        // Normalize by integer tile offset. 按整数瓦片偏移归一。
                        int ox = Mathf.FloorToInt(bounds.xMin);
                        int oy = Mathf.FloorToInt(bounds.yMin);

                        state.Islands.Add(new ATOUVIslandEntry
                        {
                            Renderer = renderer,
                            SubMeshIndex = submesh,
                            UVChannel = channel,
                            Island = island,
                            NormalizedBounds = new Rect(bounds.xMin - ox, bounds.yMin - oy, bounds.width, bounds.height),
                            OffsetTileX = ox,
                            OffsetTileY = oy,
                            Textures = textureRecs,
                        });
                    }
                }
            }

            ATOLog.Info($"Extracted {state.Islands.Count} UV island entries. / 提取到 {state.Islands.Count} 个 UV 岛条目。");
        }
    }
}
