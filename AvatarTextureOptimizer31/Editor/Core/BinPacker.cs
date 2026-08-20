// BinPacker.cs
// Phase 7: Packs rasterized UV islands into candidate atlases.
// Algorithm: Full-scan BLF (bottom-left-fill) with rasterization-based collision,
// area-descending sort, 90° rotation stepping, and candidate atlas pool.
// 阶段7：将光栅化的 UV 岛装箱到候选图集中。
//
// Copyright (c) 2024 fosa. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using Fosa.AvatarTextureOptimizer.Packing;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Core
{
    /// <summary>
    /// Packs UV islands into atlases using full-scan BLF packing with bitmask collision.
    /// Generates candidate atlas pools (POT or NPOT), selects the best-fitting,
    /// and falls back to per-texture optimization if packing fails.
    /// 使用全扫描 BLF 装箱将 UV 岛打包成图集。
    /// </summary>
    internal sealed class BinPacker
    {
        private readonly List<TextureTypeGroup> _typeGroups;
        private readonly ATOComponent _component;
        private readonly AdvancedSettings _settings;
        private readonly ATOLogger _log;

        internal BinPacker(List<TextureTypeGroup> typeGroups, ATOComponent component,
            AdvancedSettings settings, ATOLogger log)
        {
            _typeGroups = typeGroups;
            _component = component;
            _settings = settings;
            _log = log;
        }

        internal List<GeneratedAtlas> Execute()
        {
            var allAtlases = new List<GeneratedAtlas>();
            int paddingPx = (int)_component._padding;

            foreach (var tg in _typeGroups)
            {
                if (tg.AllIslands.Count == 0) continue;

                // Sort islands by rasterized area descending
                var sortedIslands = tg.AllIslands
                    .OrderByDescending(i => i.RasterArea)
                    .ThenByDescending(i => Mathf.Max(i.ScaledPixelBounds.width, i.ScaledPixelBounds.height))
                    .ToList();

                // Group islands by source texture (to ensure all islands from
                // the same texture end up in the same atlas)
                var textureQueues = new Dictionary<Texture2D, List<UVIsland>>();
                foreach (var island in sortedIslands)
                {
                    if (!textureQueues.TryGetValue(island.SourceTexture, out var list))
                    {
                        list = new List<UVIsland>();
                        textureQueues[island.SourceTexture] = list;
                    }
                    list.Add(island);
                }

                // Build a sorted queue: total area per texture descending
                var textureQueue = textureQueues
                    .Select(kvp => (tex: kvp.Key, islands: kvp.Value, area: kvp.Value.Sum(i => i.RasterArea)))
                    .OrderByDescending(x => x.area)
                    .ToList();

                // Try to pack all textures into the best-fitting atlas
                var atlases = PackTextureGroup(tg, textureQueue, paddingPx);
                allAtlases.AddRange(atlases);
            }

            _log.Info($"Packed {allAtlases.Count} atlases from {_typeGroups.Count} type groups.");
            foreach (var atlas in allAtlases)
            {
                _log.Verbose($"  {atlas.Name}: {atlas.Width}×{atlas.Height}, util={atlas.Utilization * 100:F1}%, {atlas.PlacedIslands.Count} islands");
            }

            return allAtlases;
        }

        private List<GeneratedAtlas> PackTextureGroup(TextureTypeGroup tg,
            List<(Texture2D tex, List<UVIsland> islands, long area)> textureQueue,
            int paddingPx)
        {
            var result = new List<GeneratedAtlas>();
            var remaining = new List<(Texture2D tex, List<UVIsland> islands, long area)>(textureQueue);

            while (remaining.Count > 0)
            {
                // Compute total area of all remaining islands
                long totalArea = remaining.Sum(x => x.area);

                // Generate candidate atlas pool
                var candidates = GenerateCandidatePool(totalArea);

                if (candidates.Count == 0)
                {
                    // Largest atlas can't fit even the smallest texture group
                    foreach (var (tex, islands, area) in remaining)
                    {
                        _log.Warning($"Cannot fit texture '{tex?.name}' islands into any atlas. " +
                            "Falling back to individual texture optimization. / 无法将贴图装箱到图集中，回退到单独贴图优化。");
                        // Mark for individual processing (no atlas)
                        foreach (var island in islands)
                            island.AtlasPlacement = new Rect(0, 0, 1, 1); // identity
                    }
                    break;
                }

                // Try each candidate from most-square to least-square
                bool packed = false;
                foreach (var candidate in candidates)
                {
                    var atlas = TryPackInto(tg, remaining, candidate, paddingPx);
                    if (atlas != null)
                    {
                        result.Add(atlas);
                        // Remove successfully packed islands
                        var packedTextures = atlas.PlacedIslands.Select(i => i.SourceTexture).ToHashSet();
                        remaining.RemoveAll(x => packedTextures.Contains(x.tex));
                        packed = true;
                        break;
                    }
                }

                if (!packed)
                {
                    // Couldn't pack even the first texture into the largest atlas
                    // Split: try the first texture alone
                    if (remaining.Count > 0)
                    {
                        var first = remaining[0];
                        var singleCandidate = GenerateCandidatePool(first.area);
                        var atlas = singleCandidate.Count > 0
                            ? TryPackInto(tg, new List<(Texture2D, List<UVIsland>, long)> { first },
                                singleCandidate[0], paddingPx)
                            : null;

                        if (atlas != null)
                        {
                            result.Add(atlas);
                            remaining.RemoveAt(0);
                        }
                        else
                        {
                            // Give up on this texture
                            _log.Warning($"Cannot pack texture '{first.tex?.name}'. Skipping atlas for it.");
                            foreach (var island in first.islands)
                                island.AtlasPlacement = new Rect(0, 0, 1, 1);
                            remaining.RemoveAt(0);
                        }
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Generates candidate atlas dimensions sorted by closest-to-square first.
        /// POT mode: powers of 2 from 64 to max.
        /// NPOT mode: multiples of 64 up to max.
        /// 生成候选图集尺寸，按最接近正方形优先排序。
        /// </summary>
        private List<(int w, int h)> GenerateCandidatePool(long requiredArea)
        {
            var candidates = new List<(int w, int h)>();
            int minSize = 64;
            int maxSize = GetMaxAtlasSize();

            if (_component._useNPOT)
            {
                // NPOT: step by 64
                for (int w = minSize; w <= maxSize; w += 64)
                {
                    for (int h = minSize; h <= maxSize; h += 64)
                    {
                        long area = (long)w * h;
                        if (area >= requiredArea)
                            candidates.Add((w, h));
                    }
                }
            }
            else
            {
                // POT: powers of 2
                for (int w = minSize; w <= maxSize; w *= 2)
                {
                    for (int h = minSize; h <= maxSize; h *= 2)
                    {
                        long area = (long)w * h;
                        if (area >= requiredArea)
                            candidates.Add((w, h));
                    }
                }
            }

            // Sort by area ascending, then by aspect ratio (longest/shortest) ascending
            return candidates
                .OrderBy(c => (long)c.w * c.h)
                .ThenBy(c => (float)Mathf.Max(c.w, c.h) / Mathf.Min(c.w, c.h))
                .ToList();
        }

        private int GetMaxAtlasSize()
        {
            // Default to PC max; platform overrides handled elsewhere
            return 8192;
        }

        /// <summary>
        /// Attempts to pack a set of texture queues into a single atlas using full-scan BLF.
        /// Returns null if packing fails.
        /// 尝试使用全扫描 BLF 将纹理队列打包到单个图集中。
        /// </summary>
        private GeneratedAtlas TryPackInto(TextureTypeGroup tg,
            List<(Texture2D tex, List<UVIsland> islands, long area)> textureQueue,
            (int w, int h) candidate, int paddingPx)
        {
            int atlasW = candidate.w;
            int atlasH = candidate.h;
            int granularity = _settings.rasterGranularity;

            int rasterW = atlasW / granularity;
            int rasterH = atlasH / granularity;
            int atlasWordsPerRow = (rasterW + 63) / 64;
            var atlasBitmask = new ulong[rasterH * atlasWordsPerRow];
            Array.Clear(atlasBitmask, 0, atlasBitmask.Length);

            var placedIslands = new List<UVIsland>();
            long totalRasterArea = 0;

            foreach (var (tex, islands, _) in textureQueue)
            {
                bool allPlaced = true;

                foreach (var island in islands)
                {
                    var placement = TryPlaceIsland(island, atlasBitmask, rasterW, rasterH,
                        atlasW, atlasH, granularity, paddingPx);

                    if (placement.HasValue)
                    {
                        island.AtlasPlacement = placement.Value;
                        island.Rotation = placement.Value.width < 0 ? 90 : 0; // hack to signal rotation
                        placedIslands.Add(island);
                        totalRasterArea += island.RasterArea;

                        // Stamp into atlas bitmask
                        StampIsland(island, atlasBitmask, rasterW, rasterH,
                            Mathf.RoundToInt(placement.Value.x / (float)granularity),
                            Mathf.RoundToInt(placement.Value.y / (float)granularity));
                    }
                    else
                    {
                        allPlaced = false;
                        break;
                    }
                }

                if (!allPlaced)
                {
                    // This texture's islands don't all fit → fail the whole atlas
                    return null;
                }
            }

            float utilization = (float)totalRasterArea / (rasterW * rasterH);

            var atlasName = $"ATO_Atlas_{tg.Id}_{tg.Atlases.Count}_{atlasW}x{atlasH}";

            return new GeneratedAtlas
            {
                Name = atlasName,
                Width = atlasW,
                Height = atlasH,
                Utilization = utilization,
                PlacedIslands = placedIslands,
                RasterAreaTotal = totalRasterArea,
                TotalArea = rasterW * rasterH,
                IsNPOT = _component._useNPOT,
                Category = tg.HasNormal ? TextureCategory.Normal : TextureCategory.Color
            };
        }

        /// <summary>
        /// Full-scan BLF placement: scans from bottom-left, finds first non-colliding position.
        /// Tries 0° and 90° rotation (for color textures only, not normal maps).
        /// 全扫描 BLF 放置：从左下角扫描，找到第一个不碰撞的位置。
        /// </summary>
        private Rect? TryPlaceIsland(UVIsland island, ulong[] atlasBitmask,
            int rasterW, int rasterH, int atlasW, int atlasH,
            int granularity, int paddingPx)
        {
            int islandRasterW = Mathf.CeilToInt(island.ScaledPixelBounds.width / (float)granularity);
            int islandRasterH = Mathf.CeilToInt(island.ScaledPixelBounds.height / (float)granularity);
            int padRaster = Mathf.Max(1, paddingPx / granularity);

            // Try 0° rotation
            var placement = ScanBLF(island.RasterBitmask, islandRasterW, islandRasterH,
                atlasBitmask, rasterW, rasterH, padRaster);
            if (placement.HasValue)
            {
                return new Rect(
                    placement.Value.x * granularity,
                    placement.Value.y * granularity,
                    island.ScaledPixelBounds.width,
                    island.ScaledPixelBounds.height
                );
            }

            // Try 90° rotation (only for color textures, not normal maps - tangent data)
            if (island.TypeGroup == null || !island.TypeGroup.HasNormal)
            {
                var (transposed, tW, tH) = IslandRasterizer.Transpose(island.RasterBitmask, islandRasterW, islandRasterH);
                var rotatedPlacement = ScanBLF(transposed, tW, tH,
                    atlasBitmask, rasterW, rasterH, padRaster);
                if (rotatedPlacement.HasValue)
                {
                    island.Rotation = 90;
                    return new Rect(
                        rotatedPlacement.Value.x * granularity,
                        rotatedPlacement.Value.y * granularity,
                        island.ScaledPixelBounds.height,  // swapped
                        island.ScaledPixelBounds.width
                    );
                }
            }

            return null;
        }

        private (int x, int y)? ScanBLF(ulong[] islandBitmask, int islandW, int islandH,
            ulong[] atlasBitmask, int atlasW, int atlasH, int padding)
        {
            if (islandBitmask == null || islandBitmask.Length == 0) return null;
            if (islandW > atlasW || islandH > atlasH) return null;

            for (int y = 0; y <= atlasH - islandH; y++)
            {
                for (int x = 0; x <= atlasW - islandW; x++)
                {
                    if (!IslandRasterizer.CheckCollision(islandBitmask, islandW, islandH,
                        atlasBitmask, atlasW, atlasH, x, y, 1))
                    {
                        // Check padding margin
                        if (CheckPaddingClear(islandBitmask, islandW, islandH,
                            atlasBitmask, atlasW, atlasH, x, y, padding))
                        {
                            return (x, y);
                        }
                    }
                }
            }
            return null;
        }

        private bool CheckPaddingClear(ulong[] islandBitmask, int islandW, int islandH,
            ulong[] atlasBitmask, int atlasW, int atlasH, int x, int y, int padding)
        {
            if (padding <= 0) return true;
            // Check the padded border around the island is clear
            int paddedW = islandW + 2 * padding;
            int paddedH = islandH + 2 * padding;
            int startX = x - padding;
            int startY = y - padding;

            int atlasWordsPerRow = (atlasW + 63) / 64;

            for (int ry = 0; ry < paddedH; ry++)
            {
                int ay = startY + ry;
                if (ay < 0 || ay >= atlasH) continue;
                for (int rx = 0; rx < paddedW; rx++)
                {
                    int ax = startX + rx;
                    if (ax < 0 || ax >= atlasW) continue;

                    // Only check the border ring (skip interior which is the island itself)
                    bool isBorder = ry < padding || ry >= paddedH - padding ||
                                    rx < padding || rx >= paddedW - padding;
                    if (!isBorder) continue;

                    int word = ay * atlasWordsPerRow + ax / 64;
                    int bit = ax % 64;
                    if (word < atlasBitmask.Length && (atlasBitmask[word] & (1UL << bit)) != 0)
                        return false;
                }
            }
            return true;
        }

        private void StampIsland(UVIsland island, ulong[] atlasBitmask, int rasterW, int rasterH,
            int offsetX, int offsetY)
        {
            int islandRasterW = Mathf.CeilToInt(island.ScaledPixelBounds.width / (float)island.RasterGranularity);
            int islandRasterH = Mathf.CeilToInt(island.ScaledPixelBounds.height / (float)island.RasterGranularity);

            IslandRasterizer.Stamp(island.RasterBitmask, islandRasterW, islandRasterH,
                atlasBitmask, rasterW, rasterH, offsetX, offsetY);
        }
    }
}
