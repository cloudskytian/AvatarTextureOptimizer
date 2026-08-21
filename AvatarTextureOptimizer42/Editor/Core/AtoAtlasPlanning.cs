using System;
using System.Collections.Generic;
using System.Linq;
using Net.Fosa.AvatarTextureOptimizer;
using UnityEditor;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Conservative atlas planning helpers used for dry-run planning.
    /// 用于干运行规划的保守图集规划辅助工具。
    /// </summary>
    internal static class AtoAtlasPlanning
    {
        private const int CellSize = 4;

        public static AvatarTextureOptimizerPlatformProfile ResolveActiveProfile(AvatarTextureOptimizer component)
        {
            var overrides = component.PlatformOverrides;
            switch (EditorUserBuildSettings.activeBuildTarget)
            {
                case BuildTarget.Android:
                    return overrides.Android.OverrideEnabled ? overrides.Android : overrides.Common;
                case BuildTarget.iOS:
                    return overrides.IOS.OverrideEnabled ? overrides.IOS : overrides.Common;
                default:
                    return overrides.PC.OverrideEnabled ? overrides.PC : overrides.Common;
            }
        }

        public static Vector2 EstimateSourcePixels(AtoUvGroupRecord uvGroup)
        {
            var width = 0.0f;
            var height = 0.0f;
            foreach (var usage in uvGroup.Usages)
            {
                if (usage.Texture is not Texture2D texture)
                {
                    continue;
                }

                width = Mathf.Max(width, texture.width * Mathf.Max(uvGroup.Span.x, 0.0f));
                height = Mathf.Max(height, texture.height * Mathf.Max(uvGroup.Span.y, 0.0f));
            }

            return new Vector2(Mathf.Max(width, 1.0f), Mathf.Max(height, 1.0f));
        }

        public static Vector2 EstimateTargetPixels(AtoUvGroupRecord uvGroup, AvatarTextureOptimizer component)
        {
            var sourcePixels = EstimateSourcePixels(uvGroup);
            var quality = component.Quality.Parameters.GlobalTargetQuality;
            var pixelDensityMin = Mathf.Max(128, component.General.MinimumPixelDensity);
            var pixelDensityMax = Mathf.Max(pixelDensityMin, component.General.MaximumPixelDensity);

            var worldArea = Mathf.Max(uvGroup.TotalObjectSpaceArea * Mathf.Max(1.0f, uvGroup.AnimatedAreaScaleFactor), 0.000001f);
            var targetEdge = Mathf.Sqrt(worldArea) * Mathf.Lerp(pixelDensityMin, pixelDensityMax, quality);
            var aspect = sourcePixels.y <= 0.0f ? 1.0f : sourcePixels.x / sourcePixels.y;
            var width = Mathf.Sqrt(targetEdge * targetEdge * aspect);
            var height = aspect <= 0.0f ? targetEdge : targetEdge / Mathf.Sqrt(aspect);

            width = Mathf.Clamp(width, 4.0f, sourcePixels.x);
            height = Mathf.Clamp(height, 4.0f, sourcePixels.y);
            return new Vector2(Mathf.Max(4.0f, width), Mathf.Max(4.0f, height));
        }

        public static List<AtoAtlasPlan> PlanAtlases(AtoTextureTypeGroupPlan typeGroup, Dictionary<string, AtoUvGroupRecord> uvGroups, AvatarTextureOptimizer component)
        {
            var atlasIndex = 0;
            return PlanSharedLayout(
                typeGroup.Members
                    .Select(member => uvGroups.TryGetValue(member.UvGroupKey, out var uvGroup) ? uvGroup : null)
                    .Where(uvGroup => uvGroup != null)
                    .GroupBy(uvGroup => uvGroup.Key)
                    .Select(group => group.First())
                    .ToList(),
                component,
                () => $"ATO_{typeGroup.MaterialProperty}_{typeGroup.Semantic}_{atlasIndex++:D3}");
        }

        public static List<AtoAtlasPlan> PlanSharedLayout(IReadOnlyCollection<AtoUvGroupRecord> uvGroups, AvatarTextureOptimizer component, Func<string> nameFactory = null)
        {
            var profile = ResolveActiveProfile(component);
            var requirements = uvGroups
                .Where(uvGroup => uvGroup != null)
                .Select(uvGroup => BuildRequirement(uvGroup, component))
                .OrderByDescending(req => req.CellWidth * req.CellHeight)
                .ThenByDescending(req => Mathf.Max(req.CellWidth, req.CellHeight))
                .ToList();

            var atlases = new List<AtoAtlasPlan>();
            if (requirements.Count == 0)
            {
                return atlases;
            }

            var candidates = BuildCandidates(component.General.ExperimentalNpotAtlasSizes, profile.MaxAtlasSize);
            var remaining = new List<PackedRequirement>(requirements);
            var atlasIndex = 0;
            while (remaining.Count > 0)
            {
                var packed = TryPackLargestPrefix(remaining, candidates, component.General.MinimumPadding);
                if (packed == null)
                {
                    var fallback = remaining[0];
                    var forced = new AtoAtlasPlan
                    {
                        Name = nameFactory != null ? nameFactory() : $"ATO_Shared_{atlasIndex++:D3}",
                        Width = Mathf.NextPowerOfTwo(Mathf.Max(64, fallback.PixelWidth + component.General.MinimumPadding * 2)),
                        Height = Mathf.NextPowerOfTwo(Mathf.Max(64, fallback.PixelHeight + component.General.MinimumPadding * 2)),
                        IslandCellSize = CellSize,
                        PaddingPixels = component.General.MinimumPadding,
                        EstimatedUtilization = 1.0f,
                    };
                    forced.Items.Add(new AtoAtlasItemPlan
                    {
                        UvGroupKey = fallback.UvGroupKey,
                        PixelX = component.General.MinimumPadding,
                        PixelY = component.General.MinimumPadding,
                        PixelWidth = fallback.PixelWidth,
                        PixelHeight = fallback.PixelHeight,
                        CellX = 0,
                        CellY = 0,
                        CellWidth = fallback.CellWidth,
                        CellHeight = fallback.CellHeight,
                    });
                    atlases.Add(forced);
                    remaining.RemoveAt(0);
                    continue;
                }

                packed.Name = nameFactory != null ? nameFactory() : $"ATO_Shared_{atlasIndex++:D3}";
                atlases.Add(packed);
                foreach (var item in packed.Items)
                {
                    remaining.RemoveAll(req => string.Equals(req.UvGroupKey, item.UvGroupKey, StringComparison.OrdinalIgnoreCase));
                }
            }

            return atlases;
        }

        private static PackedRequirement BuildRequirement(AtoUvGroupRecord uvGroup, AvatarTextureOptimizer component)
        {
            var estimated = EstimateTargetPixels(uvGroup, component);
            var padding = component.General.MinimumPadding;
            var pixelWidth = Mathf.Max(4, Mathf.CeilToInt(estimated.x));
            var pixelHeight = Mathf.Max(4, Mathf.CeilToInt(estimated.y));
            return new PackedRequirement
            {
                UvGroupKey = uvGroup.Key,
                PixelWidth = pixelWidth,
                PixelHeight = pixelHeight,
                CellWidth = Mathf.Max(1, Mathf.CeilToInt((pixelWidth + padding * 2) / (float)CellSize)),
                CellHeight = Mathf.Max(1, Mathf.CeilToInt((pixelHeight + padding * 2) / (float)CellSize)),
            };
        }

        private static List<Vector2Int> BuildCandidates(bool npot, int maxSize)
        {
            var lengths = new List<int>();
            if (npot)
            {
                for (var size = 64; size <= maxSize; size += 64)
                {
                    lengths.Add(size);
                }
            }
            else
            {
                for (var size = 64; size <= maxSize; size <<= 1)
                {
                    lengths.Add(size);
                }
            }

            return lengths
                .SelectMany(w => lengths.Select(h => new Vector2Int(w, h)))
                .OrderBy(v => v.x * v.y)
                .ThenBy(v => Mathf.Abs(v.x - v.y))
                .ToList();
        }

        private static AtoAtlasPlan TryPackLargestPrefix(List<PackedRequirement> requirements, List<Vector2Int> candidates, int paddingPixels)
        {
            for (var count = requirements.Count; count >= 1; count--)
            {
                var prefix = requirements.Take(count).ToList();
                var candidate = TryPackExact(prefix, candidates, paddingPixels);
                if (candidate != null)
                {
                    return candidate;
                }
            }

            return null;
        }

        private static AtoAtlasPlan TryPackExact(List<PackedRequirement> requirements, List<Vector2Int> candidates, int paddingPixels)
        {
            foreach (var candidate in candidates)
            {
                var cellWidth = Mathf.Max(1, candidate.x / CellSize);
                var cellHeight = Mathf.Max(1, candidate.y / CellSize);
                var occupancy = new bool[cellWidth, cellHeight];
                var items = new List<AtoAtlasItemPlan>();
                var packedAll = true;

                foreach (var requirement in requirements)
                {
                    if (!TryPlace(requirement, occupancy, out var cellX, out var cellY))
                    {
                        packedAll = false;
                        break;
                    }

                    Mark(occupancy, cellX, cellY, requirement.CellWidth, requirement.CellHeight);
                    items.Add(new AtoAtlasItemPlan
                    {
                        UvGroupKey = requirement.UvGroupKey,
                        CellX = cellX,
                        CellY = cellY,
                        CellWidth = requirement.CellWidth,
                        CellHeight = requirement.CellHeight,
                        PixelX = cellX * CellSize + paddingPixels,
                        PixelY = cellY * CellSize + paddingPixels,
                        PixelWidth = requirement.PixelWidth,
                        PixelHeight = requirement.PixelHeight,
                    });
                }

                if (!packedAll)
                {
                    continue;
                }

                var usedArea = items.Sum(item => item.PixelWidth * item.PixelHeight);
                var totalArea = candidate.x * candidate.y;
                var atlas = new AtoAtlasPlan
                {
                    Width = candidate.x,
                    Height = candidate.y,
                    IslandCellSize = CellSize,
                    PaddingPixels = paddingPixels,
                    EstimatedUtilization = totalArea <= 0 ? 0.0f : usedArea / (float)totalArea,
                };
                atlas.Items.AddRange(items);
                return atlas;
            }

            return null;
        }

        private static bool TryPlace(PackedRequirement requirement, bool[,] occupancy, out int cellX, out int cellY)
        {
            var maxX = occupancy.GetLength(0) - requirement.CellWidth;
            var maxY = occupancy.GetLength(1) - requirement.CellHeight;
            for (var y = 0; y <= maxY; y++)
            {
                for (var x = 0; x <= maxX; x++)
                {
                    if (Fits(occupancy, x, y, requirement.CellWidth, requirement.CellHeight))
                    {
                        cellX = x;
                        cellY = y;
                        return true;
                    }
                }
            }

            cellX = 0;
            cellY = 0;
            return false;
        }

        private static bool Fits(bool[,] occupancy, int x, int y, int width, int height)
        {
            for (var dy = 0; dy < height; dy++)
            {
                for (var dx = 0; dx < width; dx++)
                {
                    if (occupancy[x + dx, y + dy])
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static void Mark(bool[,] occupancy, int x, int y, int width, int height)
        {
            for (var dy = 0; dy < height; dy++)
            {
                for (var dx = 0; dx < width; dx++)
                {
                    occupancy[x + dx, y + dy] = true;
                }
            }
        }

        private sealed class PackedRequirement
        {
            public string UvGroupKey;
            public int PixelWidth;
            public int PixelHeight;
            public int CellWidth;
            public int CellHeight;
        }
    }
}
