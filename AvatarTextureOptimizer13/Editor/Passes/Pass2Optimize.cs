// ATO — Avatar Texture Optimizer
// Pass 2 — optimize: scales every island (or whole texture, when atlas generation is off)
// to the target quality using binary search, with density clamping and solid-color /
// lossless shortcuts.
// Pass 2——优化：用二分搜索把每个岛（或整张贴图，不生成图集时）缩放到目标质量，
// 带密度钳制与纯色/近无损捷径。

using System.Collections.Generic;
using UnityEngine;
using net.fosa.ato;

namespace net.fosa.ato.editor
{
    /// <summary>
    /// Pass 2 — optimization. Pass 2——优化。
    /// </summary>
    public class Pass2Optimize : ATOBasePass<Pass2Optimize>
    {
        protected override void Process(ATOBuildContext bc, nadena.dev.ndmf.BuildContext context)
        {
            var result = bc.Result;
            if (result == null || !result.didAnything) return;

            var refs = new Dictionary<Texture2D, ATOTextureRef>();
            foreach (var tr in result.textures) refs[tr.texture] = tr;

            if (result.settings.generateAtlas)
            {
                RunStage(bc, ATOI18nKeys.StageOptimize, CountIslands(result), () =>
                {
                    int done = 0;
                    foreach (var group in result.uvGroups)
                    {
                        bc.ThrowIfCancelled();
                        if (group.whitelisted) continue;
                        if (group.hasWhitelistMember)
                        {
                            // Partial whitelist: skip atlas, whole-texture scale the non-whitelisted members.
                            // 部分白名单：跳过图集化，对非白名单成员做整图缩放。
                            foreach (var u in group.usages)
                            {
                                if (u.whitelisted || u.texture == null) continue;
                                if (refs.TryGetValue(u.texture, out var tr) && !tr.whitelisted)
                                    UVIslandScaler.ScaleWholeTexture(bc, tr, result.settings);
                            }
                            continue;
                        }
                        foreach (var island in group.islands)
                        {
                            bc.ThrowIfCancelled();
                            UVIslandScaler.ScaleIsland(bc, group, island, refs, result.settings);
                            done++;
                        }
                        bc.ReportProgress(done);
                    }
                    bc.Report.AddDetail($"[Optimize] scaled {done} islands across {result.uvGroups.Count} UV groups.");
                });
            }
            else
            {
                RunStage(bc, ATOI18nKeys.StageOptimize, result.textures.Count, () =>
                {
                    int done = 0;
                    foreach (var tr in result.textures)
                    {
                        bc.ThrowIfCancelled();
                        if (tr.whitelisted) continue;
                        UVIslandScaler.ScaleWholeTexture(bc, tr, result.settings);
                        done++;
                        bc.ReportProgress(done);
                    }
                    bc.Report.AddDetail($"[Optimize] scaled {done} whole textures (atlas disabled).");
                });
            }

            bc.ClearCaches(); // decoded pixels no longer needed for now. 解码像素暂不再需要。
        }

        private static int CountIslands(ATOAnalysisResult result)
        {
            int count = 0;
            foreach (var g in result.uvGroups) count += g.islands.Count;
            return Mathf.Max(1, count);
        }

        protected override void ReleaseResources(ATOBuildContext bc)
        {
            bc.ClearCaches();
        }
    }
}
