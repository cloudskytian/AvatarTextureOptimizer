using System;
using System.Collections.Generic;
using System.Linq;
using Fosa.AvatarTextureOptimizer.Editor.API;
using Fosa.AvatarTextureOptimizer.Editor.Core;
using Fosa.AvatarTextureOptimizer.Editor.Reporting;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Editor.Quality
{
    /// <summary>EN: Uniform binary search followed by independent X/Y refinement under UV-group bottle-neck quality. ZH: 在 UV 组木桶质量约束下先均匀二分，再独立细化 X/Y。</summary>
    internal static class IslandQualityScaler
    {
        public static void Scale(BuildPlan plan, BuildProgress progress, AtoBuildReport report)
        {
            if (plan.Profile.quality.IsExact)
            {
                report.Log("Quality is exactly 1; island resampling was skipped.");
                return;
            }

            var work = plan.UvGroups.Where(x => x.Usages.Any(u => !u.Protected))
                .SelectMany(x => x.Islands.Select(i => (group: x, island: i))).ToList();
            using (var evaluator = new GpuIslandQualityEvaluator())
            {
                for (var index = 0; index < work.Count; index++)
                {
                    progress.Report("Searching island quality / 搜索 UV 岛质量", index, Math.Max(1, work.Count));
                    var item = work[index];
                    ScaleOne(item.group, item.island, plan.Profile.quality, evaluator, report);
                }
            }
        }

        private static void ScaleOne(UvGroup group, UvIsland island, QualityThresholds threshold,
            GpuIslandQualityEvaluator evaluator, AtoBuildReport report)
        {
            var cache = new Dictionary<(int texture, int width, int height, string property), QualityResult>();
            var source = island.SourcePixelSize;
            var densityLow = Min(island.MinimumDensityPixelSize, source);
            var densityHigh = Min(island.MaximumDensityPixelSize, source);

            var sourceResults = EvaluateAll(group, island, source, evaluator, cache);
            if (sourceResults.All(x => x.result.IsPureColor))
            {
                island.IsPureColor = true;
                island.PureColor = Color.white;
                island.TargetPixelSize = new Vector2Int(Mathf.Min(4, source.x), Mathf.Min(4, source.y));
                return;
            }

            var high = densityHigh;
            if (!Passes(EvaluateAll(group, island, high, evaluator, cache), threshold, group, island, high))
            {
                high = source;
                report.Warn($"Island {island.Id} required more than maximum pixel density to satisfy quality; original-size safety fallback was allowed.", group.Renderer.Renderer);
            }
            if (!Passes(EvaluateAll(group, island, high, evaluator, cache), threshold, group, island, high))
            {
                island.TargetPixelSize = source;
                report.Warn($"Island {island.Id} did not pass even at source size due to sampling uncertainty; no resampling will be performed.", group.Renderer.Renderer);
                return;
            }

            var low = densityLow;
            var best = high;
            if (Passes(EvaluateAll(group, island, low, evaluator, cache), threshold, group, island, low)) best = low;
            else
            {
                var lo = 0f; var hi = 1f;
                for (var iteration = 0; iteration < 10; iteration++)
                {
                    var middle = (lo + hi) * 0.5f;
                    var candidate = LerpSize(low, high, middle);
                    if (Passes(EvaluateAll(group, island, candidate, evaluator, cache), threshold, group, island, candidate)) { best = candidate; hi = middle; }
                    else lo = middle;
                }
            }

            // EN: Refine each axis while retaining the other, reducing anisotropic waste.
            // ZH: 保持另一轴不变分别细化每个轴，减少各向异性浪费。
            best.x = RefineAxis(0, densityLow.x, best.x, best, group, island, threshold, evaluator, cache);
            best.y = RefineAxis(1, densityLow.y, best.y, best, group, island, threshold, evaluator, cache);
            island.TargetPixelSize = new Vector2Int(Mathf.Clamp(best.x, 1, source.x), Mathf.Clamp(best.y, 1, source.y));
            island.Scale = new Vector2((float)island.TargetPixelSize.x / source.x, (float)island.TargetPixelSize.y / source.y);
        }

        private static int RefineAxis(int axis, int minimum, int maximum, Vector2Int fixedSize, UvGroup group,
            UvIsland island, QualityThresholds threshold, GpuIslandQualityEvaluator evaluator,
            Dictionary<(int texture, int width, int height, string property), QualityResult> cache)
        {
            var low = minimum; var high = maximum; var best = maximum;
            while (low <= high)
            {
                var middle = low + (high - low) / 2;
                var candidate = fixedSize;
                if (axis == 0) candidate.x = middle; else candidate.y = middle;
                if (Passes(EvaluateAll(group, island, candidate, evaluator, cache), threshold, group, island, candidate)) { best = middle; high = middle - 1; }
                else low = middle + 1;
            }
            return best;
        }

        private static List<(TextureUsage usage, QualityResult result)> EvaluateAll(UvGroup group, UvIsland island,
            Vector2Int target, GpuIslandQualityEvaluator evaluator,
            Dictionary<(int texture, int width, int height, string property), QualityResult> cache)
        {
            var output = new List<(TextureUsage, QualityResult)>();
            foreach (var usage in group.Usages.Where(x => !x.Protected))
            {
                var key = (usage.Texture.GetInstanceID(), target.x, target.y, usage.PropertyName);
                if (!cache.TryGetValue(key, out var result))
                {
                    result = evaluator.Evaluate(usage, group, island, target);
                    cache[key] = result;
                }
                output.Add((usage, result));
            }
            return output;
        }

        private static bool Passes(IEnumerable<(TextureUsage usage, QualityResult result)> values,
            QualityThresholds threshold, UvGroup group, UvIsland island, Vector2Int target)
        {
            var evaluated = values.ToList();
            foreach (var value in evaluated)
                if (!value.result.Passes(value.usage.Semantic, threshold)) return false;
            var sourceUv = new Rect(island.UvBounds.x - group.IntegerTranslation.x,
                island.UvBounds.y - group.IntegerTranslation.y, island.UvBounds.width, island.UvBounds.height);
            foreach (var constraint in AtoExtensionRegistry.Get<IAtoIslandQualityConstraint>())
            foreach (var value in evaluated)
            {
                try
                {
                    if (!constraint.Accept(value.usage.Texture, sourceUv, target, value.usage.Semantic, threshold)) return false;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[ATO] Custom quality constraint {constraint.GetType().FullName} failed: {ex.Message}");
                    return false;
                }
            }
            return true;
        }

        private static Vector2Int LerpSize(Vector2Int a, Vector2Int b, float t)
        {
            return new Vector2Int(Mathf.Clamp(Mathf.CeilToInt(Mathf.Lerp(a.x, b.x, t)), 1, b.x),
                Mathf.Clamp(Mathf.CeilToInt(Mathf.Lerp(a.y, b.y, t)), 1, b.y));
        }

        private static Vector2Int Min(Vector2Int a, Vector2Int b) => new Vector2Int(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y));
    }
}
