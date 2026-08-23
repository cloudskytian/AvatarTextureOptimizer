using System;
using System.Collections.Generic;
using System.Linq;
using Fosa.AvatarTextureOptimizer.Editor.Analysis;
using Fosa.AvatarTextureOptimizer.Editor.Pipeline;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Editor.Atlas
{
    internal sealed class ShapeAtlasPacker
    {
        // Pull-push uses multiple half-float surfaces. Keep each page within a conservative resident GPU budget;
        // additional groups spill to unlimited further pages instead of risking an editor GPU allocation failure.
        internal const long MaximumAtlasPixels = 4096L * 4096L;

        public AtlasPlan Build(AvatarAnalysis analysis, ATOOptimizationSettings settings)
        {
            var plan = new AtlasPlan(); var nextPage = 0;
            var padding = (int)settings.minimumPadding;
            var maximumAtlasSize = EffectiveMaximumAtlasSize(settings.maximumAtlasSize, SystemInfo.maxTextureSize);
            foreach (var group in analysis.UvGroups.Where(value => value.AtlasSafe))
            {
                ATOProgress.Checkpoint("Packing UV group " + group.Id);
                if (!AtlasLayoutAnalyzer.TryCreate(group, out var layout, out var failure))
                {
                    Reject(analysis, group, failure); continue;
                }
                plan.GroupLayouts[group] = layout;
                var accepted = false;
                foreach (var page in plan.Pages.Where(value => value.LayoutSignature == layout.Signature).ToArray())
                {
                    var groups = page.Groups.Concat(new[] { group }).ToList();
                    if (!TryFindSmallest(groups, padding, maximumAtlasSize, settings.experimentalNpot,
                            out var size, out var placements)) continue;
                    page.Size = size; page.Groups.Add(group); page.Placements.Clear(); page.Placements.AddRange(placements);
                    accepted = true; break;
                }
                if (accepted) continue;
                if (!TryFindSmallest(new List<UvGroupRecord> { group }, padding, maximumAtlasSize,
                        settings.experimentalNpot, out var pageSize, out var pagePlacements))
                {
                    Reject(analysis, group,
                        "UV group cannot fit the configured/device atlas size or conservative GPU memory budget");
                    continue;
                }
                var created = new AtlasPage { Id = nextPage++, Size = pageSize, LayoutSignature = layout.Signature };
                created.Groups.Add(group); created.Placements.AddRange(pagePlacements); plan.Pages.Add(created);
            }
            return plan;
        }

        private static bool TryFindSmallest(List<UvGroupRecord> groups, int padding, int maximumAtlasSize,
            bool experimentalNpot, out Vector2Int selected, out List<AtlasPlacement> placements)
        {
            if (maximumAtlasSize <= 0 || groups.Count == 0 || groups.Any(value => value.Islands.Count == 0))
            { selected = default; placements = null; return false; }
            foreach (var island in groups.SelectMany(value => value.Islands))
            {
                var width = Align4(island.TargetPixelSize.x + padding * 2);
                var height = Align4(island.TargetPixelSize.y + padding * 2);
                if (width > maximumAtlasSize || height > maximumAtlasSize ||
                    (long)width * height > MaximumAtlasPixels)
                { selected = default; placements = null; return false; }
            }
            var shapes = groups.SelectMany(group => group.Islands.Select(island => PackingShape.Build(group, island, padding)))
                .OrderByDescending(value => OccupiedArea(value)).ThenByDescending(value => Math.Max(value.Width, value.Height)).ToList();
            foreach (var candidate in CandidateSizes(shapes, maximumAtlasSize, experimentalNpot))
            {
                ATOProgress.Checkpoint("Trying atlas candidate " + candidate);
                if (!TryPack(shapes, candidate, padding, out placements)) continue;
                selected = candidate; return true;
            }
            selected = default; placements = null; return false;
        }

        private static IEnumerable<Vector2Int> CandidateSizes(List<PackingShape> shapes, int maximum, bool experimentalNpot)
        {
            var values = new HashSet<Vector2Int>();
            var powers = new List<int>();
            for (var value = 32; value <= maximum; value *= 2) powers.Add(value);
            if (powers.Count == 0 || powers[powers.Count - 1] != maximum && IsPowerOfTwo(maximum)) powers.Add(maximum);
            foreach (var width in powers) foreach (var height in powers) values.Add(new Vector2Int(width, height));
            if (experimentalNpot)
            {
                var alignedMaximum = Mathf.Max(4, maximum & ~3);
                var widths = shapes.Select(value => Align4(value.Width)).Concat(new[] { alignedMaximum }).Distinct().ToArray();
                var heights = shapes.Select(value => Align4(value.Height)).Concat(new[] { alignedMaximum }).Distinct().ToArray();
                var area = shapes.Sum(value => (long)OccupiedArea(value));
                var side = Align4(Mathf.CeilToInt(Mathf.Sqrt(Mathf.Min(area, int.MaxValue))));
                widths = widths.Concat(new[] { side, Align4(side * 3 / 2) }).Where(value => value <= maximum).Distinct().ToArray();
                heights = heights.Concat(new[] { side, Align4(side * 3 / 2) }).Where(value => value <= maximum).Distinct().ToArray();
                foreach (var width in widths) foreach (var height in heights) values.Add(new Vector2Int(width, height));
            }
            return values.Where(size => (long)size.x * size.y <= MaximumAtlasPixels &&
                    shapes.All(shape => (shape.Width <= size.x && shape.Height <= size.y) ||
                    (shape.Height <= size.x && shape.Width <= size.y)))
                .OrderBy(size => (long)size.x * size.y).ThenBy(size => Math.Max(size.x, size.y)).ThenBy(size => size.x);
        }

        private static bool TryPack(List<PackingShape> shapes, Vector2Int pageSize, int padding, out List<AtlasPlacement> placements)
        {
            var occupancy = new byte[(pageSize.x * pageSize.y + 3) / 4];
            placements = new List<AtlasPlacement>(shapes.Count);
            foreach (var original in shapes)
            {
                var normalFound = TryPlace(original, occupancy, pageSize, out var normalX, out var normalY);
                var rotatedShape = original.Rotated();
                var rotatedFound = TryPlace(rotatedShape, occupancy, pageSize, out var rotatedX, out var rotatedY);
                if (!normalFound && !rotatedFound) { placements = null; return false; }
                var rotated = rotatedFound && (!normalFound || rotatedY < normalY ||
                    rotatedY == normalY && rotatedX < normalX ||
                    rotatedY == normalY && rotatedX == normalX && rotatedShape.Height < original.Height);
                var used = rotated ? rotatedShape : original; var x = rotated ? rotatedX : normalX; var y = rotated ? rotatedY : normalY;
                Commit(used, occupancy, pageSize.x, x, y);
                var target = original.Island.TargetPixelSize;
                placements.Add(new AtlasPlacement
                {
                    Group = original.Group, Island = original.Island, Rotated = rotated,
                    PaddedRect = new RectInt(x, y, used.Width, used.Height),
                    // Alignment slack is appended to the unrotated right/top edges. After clockwise rotation,
                    // the former top slack moves to the left, so the rotated content origin is not always exactly padding.
                    ContentRect = new RectInt(x + ContentOffsetX(used, target, padding, rotated), y + padding,
                        rotated ? target.y : target.x,
                        rotated ? target.x : target.y)
                });
            }
            return true;
        }

        private static bool TryPlace(PackingShape shape, byte[] occupancy, Vector2Int page,
            out int foundX, out int foundY)
        {
            for (var y = 0; y + shape.Height <= page.y; y += 4)
            {
                if ((y & 255) == 0) ATOProgress.Checkpoint("Searching atlas placement");
                for (var x = 0; x + shape.Width <= page.x; x += 4)
                {
                    if (Collides(shape, occupancy, page.x, x, y)) continue;
                    foundX = x; foundY = y; return true;
                }
            }
            foundX = foundY = 0; return false;
        }

        private static bool Collides(PackingShape shape, byte[] occupancy, int pageWidth, int offsetX, int offsetY)
        {
            for (var y = 0; y < shape.Height; y++)
            {
                if ((y & 255) == 0) ATOProgress.Checkpoint("Testing shape collision");
                for (var x = 0; x < shape.Width; x++)
                {
                    if (!shape.IsSet(x, y)) continue;
                    var destination = (y + offsetY) * pageWidth + x + offsetX;
                    if ((occupancy[destination >> 2] & (1 << (destination & 3))) != 0) return true;
                }
            }
            return false;
        }

        private static void Commit(PackingShape shape, byte[] occupancy, int pageWidth, int offsetX, int offsetY)
        {
            for (var y = 0; y < shape.Height; y++)
            {
                if ((y & 255) == 0) ATOProgress.Checkpoint("Committing atlas placement");
                for (var x = 0; x < shape.Width; x++)
                {
                    if (!shape.IsSet(x, y)) continue;
                    var destination = (y + offsetY) * pageWidth + x + offsetX;
                    occupancy[destination >> 2] |= (byte)(1 << (destination & 3));
                }
            }
        }

        private static int OccupiedArea(PackingShape shape)
        {
            var count = 0;
            foreach (var value in shape.Bits) for (var bit = 0; bit < 4; bit++) if ((value & (1 << bit)) != 0) count++;
            return count;
        }

        private static void Reject(AvatarAnalysis analysis, UvGroupRecord group, string reason)
        {
            group.AtlasSafe = false; analysis.Fallbacks.Add(new FallbackRecord(group?.Renderer?.Renderer, reason));
        }

        internal static int ContentOffsetX(PackingShape used, Vector2Int target, int padding, bool rotated) =>
            rotated ? used.Width - padding - target.y : padding;

        internal static int EffectiveMaximumAtlasSize(int configured, int deviceMaximum)
        {
            if (configured <= 0 || deviceMaximum <= 0) return 0;
            return Math.Min(configured, deviceMaximum);
        }

        private static int Align4(int value) => (value + 3) & ~3;
        private static bool IsPowerOfTwo(int value) => value > 0 && (value & (value - 1)) == 0;
    }
}
