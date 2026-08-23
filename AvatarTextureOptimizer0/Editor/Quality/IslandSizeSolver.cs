using System;
using System.Collections.Generic;
using System.Linq;
using Fosa.AvatarTextureOptimizer.Editor.Analysis;
using Fosa.AvatarTextureOptimizer.Editor.Atlas;
using Fosa.AvatarTextureOptimizer.Editor.Pipeline;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Editor.Quality
{
    internal sealed class IslandSizeSolver
    {
        public void Solve(AvatarAnalysis analysis, ATOOptimizationSettings settings)
        {
            var quality = settings.EffectiveQuality;
            using (var evaluator = quality.IsLosslessBypass ? null : new IslandQualityEvaluator())
            foreach (var group in analysis.UvGroups)
            {
                if (!group.AtlasSafe) continue;
                if (ExceedsResidentLimit(group))
                {
                    group.AtlasSafe = false;
                    analysis.Fallbacks.Add(new FallbackRecord(group?.Renderer?.Renderer,
                        "original or candidate island footprint exceeded the conservative resident-memory limit"));
                    continue;
                }
                evaluator?.ResetResourceLimit();
                foreach (var island in group.Islands)
                {
                    ATOProgress.Checkpoint("Solving UV-island quality bounds");
                    if (Solve(group, island, settings, quality, evaluator)) continue;
                    group.AtlasSafe = false;
                    analysis.Fallbacks.Add(new FallbackRecord(group?.Renderer?.Renderer,
                        evaluator != null && evaluator.ResourceLimitExceeded
                            ? "quality evaluation exceeded the conservative resident-memory limit"
                            : "quality thresholds cannot be met within the configured maximum pixel density"));
                    break;
                }
            }
        }

        private static bool ExceedsResidentLimit(UvGroupRecord group)
        {
            foreach (var island in group.Islands)
            {
                // The preserve-resolution branch skips the evaluator, so enforce its actual candidate allocation here.
                if (group.Renderer != null && group.Renderer.PreserveOriginalIslandResolution &&
                    (long)island.OriginalPixelBounds.x * island.OriginalPixelBounds.y >
                    IslandQualityEvaluator.MaximumResidentPixels) return true;
                foreach (var binding in group.Bindings)
                {
                    var width = Mathf.Max(1, Mathf.CeilToInt(island.UvBounds.width * binding.Texture.width));
                    var height = Mathf.Max(1, Mathf.CeilToInt(island.UvBounds.height * binding.Texture.height));
                    if ((long)width * height > IslandQualityEvaluator.MaximumResidentPixels) return true;
                }
            }
            return false;
        }

        private static bool Solve(UvGroupRecord group, UvIsland island, ATOOptimizationSettings settings,
            ATOQualitySettings quality, IslandQualityEvaluator evaluator)
        {
            var original = island.OriginalPixelBounds;
            var preserveOriginal = quality.IsLosslessBypass ||
                                   group.Renderer != null && group.Renderer.PreserveOriginalIslandResolution;
            var lower = preserveOriginal
                ? original
                : DensityLowerBound(island, (int)settings.minimumPixelDensity, original);
            var densityMaximum = preserveOriginal
                ? original
                : DensityLowerBound(island, (int)settings.maximumPixelDensity, original);
            var upper = Vector2Int.Min(original, densityMaximum);
            lower = Vector2Int.Min(lower, upper);

            // An atlas changes UV derivatives according to its integer content rectangle. When source mips exist,
            // only a shared integer power-of-two reduction maps every candidate LOD back to an exact source LOD
            // without changing material bias. Search that discrete safe set instead of producing an arbitrary binary
            // result that the final mip gate would inevitably reject.
            // 有源 mip 时只搜索可精确映射 LOD 的离散 POT 候选，避免连续二分结果在最终门禁整页回退。
            if (RequiresExactMipCandidates(group))
                return SolveExactMipCandidates(group, island, lower, upper, quality, evaluator);

            if (preserveOriginal)
            {
                // Relative bone motion (including constraints and physics) is not bounded by renderer/root scale curves.
                // Keeping the source footprint prevents an unproven density reduction while still allowing crop/atlas packing.
                // 骨骼相对运动不受 Renderer/根缩放曲线约束；保留源像素足迹，仅执行安全裁剪和图集重排。
                island.TargetPixelSize = original; island.Scale = Vector2.one; return true;
            }

            if (evaluator.Passes(group, island, Vector2Int.one, quality, out var pure) && pure)
            {
                island.PureColor = true; island.TargetPixelSize = Vector2Int.one;
                island.Scale = new Vector2(1f / original.x, 1f / original.y); return true;
            }

            // A hard maximum-density cap is never crossed silently: if it cannot satisfy quality, the group falls back unchanged.
            // Texture metrics over discrete resampling are not proven monotone in either dimension. Binary search is therefore
            // only a bounded candidate heuristic: every selected output is directly revalidated, and no interval inference
            // is allowed to become the safety proof. / 最大像素密度是硬上限；离散重采样质量并无逐轴单调性证明，
            // 二分仅用于限次寻找候选，最终输出必须由同一 evaluator 直接复核，否则回退到重新验证的上限或整组回退。
            if (!TrySolveContinuous(lower, upper,
                    candidate => Passes(group, island, candidate, quality, evaluator), out var best)) return false;
            island.TargetPixelSize = best;
            island.Scale = new Vector2((float)best.x / original.x, (float)best.y / original.y);
            return true;
        }

        private static bool SolveExactMipCandidates(UvGroupRecord group, UvIsland island,
            Vector2Int lower, Vector2Int upper, ATOQualitySettings quality, IslandQualityEvaluator evaluator)
        {
            foreach (var candidate in FindExactMipCandidates(group, island, lower, upper))
            {
                ATOProgress.Checkpoint("Solving exact atlas mip candidate");
                // The full Pipeline bypasses target quality 1 before analysis. Keep this internal solver total as well:
                // its lossless branch has lower == upper == original, so no GPU evaluator is needed here.
                if (evaluator != null && !Passes(group, island, candidate, quality, evaluator)) continue;
                island.TargetPixelSize = candidate;
                island.Scale = new Vector2((float)candidate.x / island.OriginalPixelBounds.x,
                    (float)candidate.y / island.OriginalPixelBounds.y);
                return true;
            }
            return false;
        }

        internal static bool RequiresExactMipCandidates(UvGroupRecord group) => group != null &&
            group.Bindings.Any(binding => binding.Texture != null && binding.Texture.mipmapCount > 1);

        internal static IReadOnlyList<Vector2Int> FindExactMipCandidates(UvGroupRecord group, UvIsland island,
            Vector2Int lower, Vector2Int upper)
        {
            var result = new List<Vector2Int>();
            if (group == null || island == null || lower.x <= 0 || lower.y <= 0 ||
                upper.x < lower.x || upper.y < lower.y) return result;
            var mipBindings = group.Bindings.Where(binding => binding.Texture != null &&
                binding.Texture.mipmapCount > 1).ToArray();
            if (mipBindings.Length == 0) return result;
            var first = mipBindings[0].Texture;
            var candidates = new HashSet<Vector2Int>();
            var footprintX = island.UvBounds.width * first.width;
            var footprintY = island.UvBounds.height * first.height;
            for (var offset = 0; offset < first.mipmapCount && offset < 31; offset++)
            {
                var divisor = 1L << offset;
                var candidate = new Vector2Int(
                    Mathf.Max(1, Mathf.RoundToInt(footprintX / divisor)),
                    Mathf.Max(1, Mathf.RoundToInt(footprintY / divisor)));
                if (candidate.x < lower.x || candidate.y < lower.y ||
                    candidate.x > upper.x || candidate.y > upper.y) continue;
                if (mipBindings.Any(binding => !AtlasTextureGenerator.TryGetExactSourceMipOffset(
                        binding.Texture, island, candidate, out _))) continue;
                candidates.Add(candidate);
            }
            result.AddRange(candidates.OrderBy(value => (long)value.x * value.y)
                .ThenBy(value => Mathf.Max(value.x, value.y)).ThenBy(value => value.x));
            return result;
        }

        internal static bool TrySolveContinuous(Vector2Int lower, Vector2Int upper,
            Func<Vector2Int, bool> passes, out Vector2Int best)
        {
            best = default;
            if (passes == null || lower.x <= 0 || lower.y <= 0 || upper.x < lower.x || upper.y < lower.y)
                return false;

            // The upper density limit is itself a candidate, never an inferred sentinel.
            if (!passes(upper)) return false;
            var selected = upper;
            var lowFactor = 0f;
            var highFactor = 1f;
            for (var iteration = 0; iteration < 10; iteration++)
            {
                var middle = (lowFactor + highFactor) * 0.5f;
                var candidate = new Vector2Int(Mathf.Max(lower.x, Mathf.CeilToInt(upper.x * middle)),
                    Mathf.Max(lower.y, Mathf.CeilToInt(upper.y * middle)));
                if (passes(candidate)) { selected = candidate; highFactor = middle; }
                else lowFactor = middle;
            }

            selected.x = SolveAxis(selected, lower.x, true, passes);
            selected.y = SolveAxis(selected, lower.y, false, passes);
            selected.x = SolveAxis(selected, lower.x, true, passes);

            // Postcondition: the exact dimensions handed to packing must pass now. This is deliberately redundant for a
            // deterministic evaluator and protects against non-monotone assumptions or a future stateful evaluator change.
            if (passes(selected))
            {
                best = selected;
                return true;
            }
            if (selected != upper && passes(upper))
            {
                best = upper;
                return true;
            }
            return false;
        }

        private static int SolveAxis(Vector2Int current, int lower, bool horizontal,
            Func<Vector2Int, bool> passes)
        {
            var low = lower;
            var high = horizontal ? current.x : current.y;
            var best = high;
            while (low <= high)
            {
                var middle = low + (high - low) / 2;
                var candidate = current;
                if (horizontal) candidate.x = middle;
                else candidate.y = middle;
                if (passes(candidate)) { best = middle; high = middle - 1; }
                else low = middle + 1;
            }
            return best;
        }

        private static bool Passes(UvGroupRecord group, UvIsland island, Vector2Int candidate,
            ATOQualitySettings quality, IslandQualityEvaluator evaluator)
        {
            candidate.x = Mathf.Clamp(candidate.x, 1, island.OriginalPixelBounds.x);
            candidate.y = Mathf.Clamp(candidate.y, 1, island.OriginalPixelBounds.y);
            return evaluator.Passes(group, island, candidate, quality, out _);
        }

        private static Vector2Int DensityLowerBound(UvIsland island, int pixelsPerMeter, Vector2Int upper)
        {
            var uvAspect = Mathf.Max(1e-6f, island.UvBounds.width / Mathf.Max(1e-6f, island.UvBounds.height));
            var widthMeters = Mathf.Sqrt(Mathf.Max(0f, island.SurfaceAreaSquareMeters) * uvAspect);
            var heightMeters = Mathf.Sqrt(Mathf.Max(0f, island.SurfaceAreaSquareMeters) / uvAspect);
            return new Vector2Int(Mathf.Clamp(Mathf.CeilToInt(widthMeters * pixelsPerMeter), 1, upper.x),
                Mathf.Clamp(Mathf.CeilToInt(heightMeters * pixelsPerMeter), 1, upper.y));
        }
    }
}
