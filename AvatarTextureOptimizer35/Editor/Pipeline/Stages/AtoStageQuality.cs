using System;
using System.Linq;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor
{
    /// <summary>
    /// Stage: target-quality scaling. / 阶段：目标质量缩放。
    /// For every non-whitelisted UV group: per-island per-texture binary search (uniform, then
    /// per-axis anisotropic refinement), wooden-barrel final rect, density band, solid shortcut,
    /// near-lossless skip. / 对每个非白名单 UV 组：逐岛逐贴图二分搜索（先均匀后逐轴细化），木桶效应
    /// 定最终矩形，密度带、纯色短路、近无损跳过。
    /// </summary>
    internal sealed class AtoStageQuality : IAtoStage
    {
        public string I18nKey => "quality";

        public void Run(AtoContext ctx)
        {
            var settings = ctx.State.Settings;
            var thresholds = settings.GetThresholds();
            var nearLossless = settings.IsNearLossless();
            var evaluator = new AtoQualityEvaluator(ctx);

            var groupIndex = 0;
            foreach (var uvGroup in ctx.UvGroups)
            {
                ctx.State.SetProgress($"quality for {uvGroup.DisplayName}",
                    (float)groupIndex / Mathf.Max(1, ctx.UvGroups.Count));
                ctx.State.ThrowIfCancelled();

                if (uvGroup.Whitelisted)
                {
                    // Whitelisted groups: no island scaling (co-UV textures get whole-texture scaling later). /
                    // 白名单组：不做岛缩放（同 UV 贴图稍后整图缩放）。
                    foreach (var island in uvGroup.Islands)
                    {
                        island.FinalUvMin = island.UvMin + new Vector2(island.NormalizationTranslation.x, island.NormalizationTranslation.y);
                        island.FinalUvMax = island.UvMax + new Vector2(island.NormalizationTranslation.x, island.NormalizationTranslation.y);
                    }
                    continue;
                }

                foreach (var island in uvGroup.Islands)
                {
                    ctx.State.ThrowIfCancelled();
                    AtoIslandScaler.ScaleIsland(ctx, uvGroup, island, evaluator, thresholds, nearLossless);
                }
                groupIndex++;
            }

            AtoLog.Info($"[ATO] quality: scaled {ctx.UvGroups.Count(u => !u.Whitelisted)} UV group(s)" +
                        (nearLossless ? " (near-lossless: no scaling)" : ""));
        }
    }
}
