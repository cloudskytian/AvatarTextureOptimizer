using System;
using System.Collections.Generic;
using System.Linq;
using Fosa.AvatarTextureOptimizer.Editor.Core;
using Fosa.AvatarTextureOptimizer.Editor.Reporting;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Editor.Atlas
{
    /// <summary>EN: Candidate-pool, full-scan BLF shape packer using raster bitmasks. ZH: 使用光栅位掩码的候选池全扫描 BLF 形状装箱器。</summary>
    internal static class ShapeAtlasPacker
    {
        private readonly struct Candidate
        {
            public readonly int Width, Height;
            public Candidate(int width, int height) { Width = width; Height = height; }
            public long Area => (long)Width * Height;
            public float Aspect => (float)Math.Max(Width, Height) / Math.Min(Width, Height);
        }

        private sealed class PackedItem
        {
            public UvIsland Island;
            public RasterMask Normal;
            public RasterMask Rotated;
        }

        public static void Pack(BuildPlan plan, BuildProgress progress, AtoBuildReport report)
        {
            for (var index = 0; index < plan.TypeGroups.Count; index++)
            {
                progress.Report("Packing raster island shapes / 光栅岛形状装箱", index, Math.Max(1, plan.TypeGroups.Count));
                PackGroup(plan.TypeGroups[index], plan.Profile, report);
            }
        }

        private static void PackGroup(TextureTypeGroup group, PlatformProfile profile, AtoBuildReport report)
        {
            foreach (var uvGroup in group.UvGroups)
            foreach (var island in uvGroup.Islands)
                island.Raster = RasterMaskBuilder.Build(uvGroup, island);

            var candidates = GenerateCandidates(profile.maximumAtlasSize, profile.experimentalNpotAtlases)
                .OrderBy(x => x.Area).ThenBy(x => x.Aspect).ThenBy(x => x.Width).ToList();
            var maximum = new Candidate(profile.maximumAtlasSize, profile.maximumAtlasSize);
            var pending = group.PackingAtoms
                .OrderByDescending(AtomArea).ThenByDescending(x => x.SelectMany(g => g.Islands).Max(i => Math.Max(i.TargetPixelSize.x, i.TargetPixelSize.y)))
                .ToList();

            while (pending.Count > 0)
            {
                var queue = new List<List<UvGroup>>();
                foreach (var atom in pending.ToList())
                {
                    var trial = queue.Concat(new[] { atom }).SelectMany(x => x).SelectMany(x => x.Islands).ToList();
                    if (TryPack(maximum, trial, profile.minimumPadding, -1, false, out _)) queue.Add(atom);
                }

                if (queue.Count == 0)
                {
                    var failed = pending[0]; pending.RemoveAt(0);
                    foreach (var uvGroup in failed)
                    {
                        uvGroup.Whitelisted = true;
                        uvGroup.FallbackReason = "One texture/UV atom does not fit the maximum atlas";
                    }
                    report.Warn($"One texture/UV atom could not fit {profile.maximumAtlasSize}px and will use non-atlas fallback.");
                    continue;
                }

                var queueIslands = queue.SelectMany(x => x).SelectMany(x => x.Islands).ToList();
                var baseArea = queueIslands.Sum(x => (long)x.Raster.SetBitCount * RasterMaskBuilder.Granularity * RasterMaskBuilder.Granularity);
                AtlasLayout chosen = null;
                foreach (var candidate in candidates.Where(x => x.Area >= baseArea))
                {
                    if (TryPack(candidate, queueIslands, profile.minimumPadding, group.Layouts.Count, true, out chosen)) break;
                }
                if (chosen == null)
                {
                    // EN: The max-square preflight succeeded; reaching this branch means an internal packing inconsistency.
                    // ZH: 最大正方形预检已成功；到达此分支表示内部装箱不一致。
                    foreach (var atom in queue) foreach (var uvGroup in atom) { uvGroup.Whitelisted = true; uvGroup.FallbackReason = "Packing consistency fallback"; }
                    report.Warn("Atlas candidate selection was inconsistent; affected atoms use fallback.");
                }
                else group.Layouts.Add(chosen);

                foreach (var atom in queue) pending.Remove(atom);
            }
        }

        private static long AtomArea(IEnumerable<UvGroup> atom)
        {
            return atom.SelectMany(x => x.Islands).Sum(x => (long)x.TargetPixelSize.x * x.TargetPixelSize.y);
        }

        private static bool TryPack(Candidate candidate, IReadOnlyList<UvIsland> islands, MinimumPadding minimumPadding,
            int atlasIndex, bool commitPlacement, out AtlasLayout layout)
        {
            layout = null;
            var requestedPadding = Mathf.Max((int)minimumPadding, Mathf.CeilToInt(Math.Max(candidate.Width, candidate.Height) / 128f));
            var paddingCells = Mathf.CeilToInt(requestedPadding / (float)RasterMaskBuilder.Granularity);
            var actualPadding = paddingCells * RasterMaskBuilder.Granularity;
            var items = islands.Select(x => new PackedItem
            {
                Island = x,
                Normal = RasterMaskBuilder.Pad(x.Raster, paddingCells),
                Rotated = RasterMaskBuilder.Pad(x.Raster.Rotated, paddingCells),
            }).OrderByDescending(x => x.Normal.SetBitCount)
              .ThenByDescending(x => Math.Max(x.Normal.Width, x.Normal.Height)).ToList();

            var cellsWidth = Mathf.CeilToInt(candidate.Width / (float)RasterMaskBuilder.Granularity);
            var cellsHeight = Mathf.CeilToInt(candidate.Height / (float)RasterMaskBuilder.Granularity);
            var occupancy = RasterMaskBuilder.Create(cellsWidth, cellsHeight);
            var placements = new List<(PackedItem item, int x, int y, bool rotated)>();
            foreach (var item in items)
            {
                if (!FindBottomLeft(occupancy, item, out var x, out var y, out var rotated)) return false;
                var mask = rotated ? item.Rotated : item.Normal;
                Commit(occupancy, mask, x, y);
                placements.Add((item, x, y, rotated));
            }
            if (!commitPlacement) return true;

            layout = new AtlasLayout
            {
                Index = atlasIndex,
                Width = candidate.Width,
                Height = candidate.Height,
                Padding = actualPadding,
                OccupiedRasterPixels = islands.Sum(x => (long)x.Raster.SetBitCount *
                    RasterMaskBuilder.Granularity * RasterMaskBuilder.Granularity),
            };
            foreach (var placement in placements)
            {
                var island = placement.item.Island;
                var originX = placement.x * RasterMaskBuilder.Granularity + actualPadding;
                var originY = placement.y * RasterMaskBuilder.Granularity + actualPadding;
                island.Placement = new AtlasPlacement(atlasIndex, originX, originY, placement.rotated,
                    placement.rotated ? island.TargetPixelSize.y : island.TargetPixelSize.x,
                    placement.rotated ? island.TargetPixelSize.x : island.TargetPixelSize.y);
                layout.Islands.Add(island);
            }
            return true;
        }

        private static bool FindBottomLeft(RasterMask occupancy, PackedItem item, out int resultX, out int resultY, out bool rotated)
        {
            for (var y = 0; y < occupancy.Height; y++)
            for (var x = 0; x < occupancy.Width; x++)
            {
                if (CanPlace(occupancy, item.Normal, x, y)) { resultX = x; resultY = y; rotated = false; return true; }
                if (item.Rotated.Width != item.Normal.Width || item.Rotated.Height != item.Normal.Height)
                    if (CanPlace(occupancy, item.Rotated, x, y)) { resultX = x; resultY = y; rotated = true; return true; }
            }
            resultX = resultY = 0; rotated = false; return false;
        }

        private static bool CanPlace(RasterMask occupancy, RasterMask mask, int x, int y)
        {
            if (x + mask.Width > occupancy.Width || y + mask.Height > occupancy.Height) return false;
            for (var my = 0; my < mask.Height; my++)
            for (var mx = 0; mx < mask.Width; mx++)
                if (RasterMaskBuilder.Get(mask, mx, my) && RasterMaskBuilder.Get(occupancy, x + mx, y + my)) return false;
            return true;
        }

        private static void Commit(RasterMask occupancy, RasterMask mask, int x, int y)
        {
            for (var my = 0; my < mask.Height; my++)
            for (var mx = 0; mx < mask.Width; mx++)
                if (RasterMaskBuilder.Get(mask, mx, my)) RasterMaskBuilder.Set(occupancy, x + mx, y + my);
        }

        private static IEnumerable<Candidate> GenerateCandidates(int maximum, bool npot)
        {
            var sides = new List<int>();
            if (npot) for (var side = 64; side <= maximum; side += 64) sides.Add(side);
            else for (var side = 64; side <= maximum; side <<= 1) sides.Add(side);
            foreach (var width in sides) foreach (var height in sides) yield return new Candidate(width, height);
        }
    }
}
