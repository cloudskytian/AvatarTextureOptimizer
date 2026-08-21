using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Builds a conservative execution plan from the analyzed safe subset.
    /// 从已分析的安全子集生成保守执行计划。
    /// </summary>
    internal static class AtoPlanner
    {
        public static AtoBuildPlan BuildPlan(AtoSessionState session)
        {
            var plan = new AtoBuildPlan();
            var uvGroupsByKey = session.ScanResult.UvGroups.ToDictionary(group => group.Key, StringComparer.OrdinalIgnoreCase);

            foreach (var sharedChannel in session.ScanResult.UvGroups
                         .GroupBy(group => $"{group.Renderer?.GetInstanceID() ?? 0}|uv{group.UvChannel}", StringComparer.OrdinalIgnoreCase)
                         .Where(group => group.Count() > 1)
                         .Take(32))
            {
                session.Report.AddWarning($"Shared UV channel fallback: renderer UV channel is referenced by multiple material slots and cannot be safely whole-channel remapped in the current milestone. Groups={string.Join(", ", sharedChannel.Select(g => g.Key))}.");
            }

            foreach (var uvGroup in session.ScanResult.UvGroups)
            {
                var sourcePixels = AtoAtlasPlanning.EstimateSourcePixels(uvGroup);
                var densityTarget = AtoAtlasPlanning.EstimateTargetPixels(uvGroup, session.Component);
                var qualityTarget = AtoQualityEvaluator.EstimateMinimumTargetPixels(uvGroup, session.Component);
                var uvPlan = new AtoUvGroupPlan
                {
                    Key = uvGroup.Key,
                    CandidateCount = uvGroup.Usages.Count(usage => usage.Decision == AtoTextureDecision.Candidate),
                    FallbackCount = uvGroup.Usages.Count(usage => usage.Decision == AtoTextureDecision.SafeFallback),
                    WhitelistCount = uvGroup.Usages.Count(usage => usage.Decision == AtoTextureDecision.ExplicitWhitelist),
                    IslandCount = uvGroup.Islands.Count,
                    EstimatedSourcePixels = sourcePixels,
                    EstimatedTargetPixels = Vector2.Min(sourcePixels, Vector2.Max(densityTarget, qualityTarget)),
                };
                plan.UvGroupPlans.Add(uvPlan);
            }

            var groups = session.ScanResult.TextureUsages
                .Where(usage => usage.Decision == AtoTextureDecision.Candidate)
                .GroupBy(BuildTextureTypeKey)
                .OrderByDescending(group => group.Count())
                .ToArray();

            foreach (var group in groups)
            {
                var first = group.First();
                var textureTypeGroup = new AtoTextureTypeGroupPlan
                {
                    Key = group.Key,
                    MaterialProperty = first.MaterialProperty,
                    Semantic = first.Semantic,
                    FilterMode = first.FilterMode,
                    WrapModeU = first.WrapModeU,
                    WrapModeV = first.WrapModeV,
                };
                textureTypeGroup.Members.AddRange(group);
                textureTypeGroup.Atlases.AddRange(AtoAtlasPlanning.PlanAtlases(textureTypeGroup, uvGroupsByKey, session.Component));
                plan.TextureTypeGroups.Add(textureTypeGroup);
            }

            session.Report.PlannedAtlasCount = plan.TextureTypeGroups.Sum(group => group.Atlases.Count);

            foreach (var uvGroupPlan in plan.UvGroupPlans.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase).Take(32))
            {
                session.Report.AddDetail(
                    $"UV plan: {uvGroupPlan.Key} | candidate={uvGroupPlan.CandidateCount} | fallback={uvGroupPlan.FallbackCount} | whitelist={uvGroupPlan.WhitelistCount} | islands={uvGroupPlan.IslandCount} | sourcePx={uvGroupPlan.EstimatedSourcePixels} | targetPx={uvGroupPlan.EstimatedTargetPixels}.");
            }

            foreach (var typeGroup in plan.TextureTypeGroups.Take(32))
            {
                session.Report.AddDetail(
                    $"Type group: {typeGroup.Key} | semantic={typeGroup.Semantic} | members={typeGroup.Members.Count} | atlases={typeGroup.Atlases.Count}.");
                foreach (var atlas in typeGroup.Atlases.Take(8))
                {
                    session.Report.AddDetail(
                        $"Atlas plan: {atlas.Name ?? "<pending-name>"} | size={atlas.Width}x{atlas.Height} | items={atlas.Items.Count} | util={atlas.EstimatedUtilization:P1}.");
                }
            }

            return plan;
        }

        private static string BuildTextureTypeKey(AtoTextureUsageRecord usage)
        {
            return string.Join("|",
                usage.MaterialProperty,
                usage.Semantic,
                usage.FilterMode,
                usage.WrapModeU,
                usage.WrapModeV,
                usage.UvChannel);
        }
    }
}
