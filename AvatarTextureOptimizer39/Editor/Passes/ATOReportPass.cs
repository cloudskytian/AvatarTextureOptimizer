// Avatar Texture Optimizer (ATO)
// Bilingual comments: English + Simplified Chinese. 双语注释：英文 + 简体中文。

using System.Text;
using AvatarTextureOptimizer.Editor.Core;
using nadena.dev.ndmf;
using UnityEngine;

namespace AvatarTextureOptimizer.Editor.Passes
{
    /// <summary>
    /// Pass 10 — print the final report to the NDMF console: summary plus per-atlas
    /// details (source textures, island count, size, utilization, savings).
    ///
    /// Pass 10 —— 向 NDMF 控制台输出最终报告：总结 + 各图集细节（来源贴图、岛数、
    /// 尺寸、利用率、优化量）。
    /// </summary>
    public sealed class ATOReportPass : Pass<ATOReportPass>
    {
        public override string DisplayName => "ATO: Report / 报告";

        protected override void Execute(BuildContext context)
        {
            var state = context.GetState<ATOBuildState>();
            if (state.Component == null) return;

            var sb = new StringBuilder();
            sb.AppendLine("[ATO] ============ Bake report / 烘焙报告 ============");
            sb.AppendLine($"[ATO] Textures processed / 处理贴图: {state.Textures.Count}");
            sb.AppendLine($"[ATO] UV islands / UV 岛: {state.Islands.Count}");
            sb.AppendLine($"[ATO] Atlases generated / 生成图集: {state.GeneratedAtlases.Count}");
            sb.AppendLine($"[ATO] Skipped textures / 跳过贴图: {state.SkippedTextures.Count}");

            int islandCount = 0;
            long totalPixelArea = 0, totalAtlasArea = 0;
            foreach (var group in state.AtlasGroups)
            foreach (var atlas in group.Atlases)
            {
                foreach (var p in atlas.Placements)
                {
                    islandCount++;
                    totalPixelArea += (long)p.PixelW * p.PixelH;
                }
                totalAtlasArea += (long)atlas.Size * atlas.Size;
            }

            if (totalAtlasArea > 0)
            {
                double utilization = (double)totalPixelArea / totalAtlasArea * 100.0;
                sb.AppendLine($"[ATO] Islands packed / 装箱岛数: {islandCount}");
                sb.AppendLine($"[ATO] Atlas utilization / 图集利用率: {utilization:F1}%");
            }

            Debug.Log(sb.ToString().TrimEnd());

            // Details (verbose). 细节（详细级别）。
            if (ATOLog.Level >= 2)
            {
                int idx = 0;
                foreach (var group in state.AtlasGroups)
                foreach (var atlas in group.Atlases)
                {
                    long used = 0;
                    foreach (var p in atlas.Placements) used += (long)p.PixelW * p.PixelH;
                    double util = (double)used / ((long)atlas.Size * atlas.Size) * 100.0;
                    ATOLog.Verbose($"[ATO] Atlas #{idx++}: {atlas.Size}px, {atlas.Placements.Count} islands, " +
                                   $"utilization {util:F1}% (group {group.TypeGroupKey})");
                }
            }

            ATOLog.Info("ATO bake complete. / ATO 烘焙完成。");

            state.EndProgress();
        }
    }
}
