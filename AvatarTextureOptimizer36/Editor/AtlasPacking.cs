using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Editor
{
    internal sealed class AtlasPlacement
    {
        public IslandRecord Island;
        public int X;
        public int Y;
        public int ContentWidth;
        public int ContentHeight;
        public int PackedWidth;
        public int PackedHeight;
        public bool Rotated;
        public int Padding;
        public int AtlasWidth;
        public int AtlasHeight;
    }

    internal sealed class AtlasPackingResult
    {
        public readonly List<AtlasPlacement> Placements = new List<AtlasPlacement>();
        public int Width;
        public int Height;
        public int Padding;
        public float OccupiedMaskArea;

        public float Utilization => Width <= 0 || Height <= 0 ? 0f : Mathf.Clamp01(OccupiedMaskArea / (Width * Height));
    }

    /// <summary>
    /// 4-pixel raster mask full-scan BLF packer with optional 90-degree rotation. / 4px 光栅位掩码、全扫描 BLF、可选 90 度旋转的装箱器。
    /// </summary>
    internal static class AtlasPacker
    {
        public static AtlasPackingResult TryPack(IList<IslandRecord> islands, int maxAtlasSize, int minimumSize,
            bool npot, int minimumPadding, int granularity, ATOLogger logger, ATOProgress progress = null)
        {
            if (islands == null || islands.Count == 0) return null;
            maxAtlasSize = Mathf.Clamp(maxAtlasSize, 64, 8192);
            minimumSize = Mathf.Clamp(minimumSize, 64, maxAtlasSize);
            granularity = 4;
            List<AtlasCandidate> candidates = BuildCandidates(maxAtlasSize, minimumSize, npot);
            List<IslandRecord> ordered = islands.OrderByDescending(EstimateArea)
                .ThenByDescending(EstimateLongSide).ToList();
            for (int candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
            {
                AtlasCandidate candidate = candidates[candidateIndex];
                int padding = Mathf.Max(4, minimumPadding, Mathf.CeilToInt(Mathf.Max(candidate.Width, candidate.Height) / 128f));
                long lowerBound = 0;
                for (int islandIndex = 0; islandIndex < ordered.Count; islandIndex++)
                {
                    lowerBound += (long)(ordered[islandIndex].OutputWidth + padding * 2) *
                                  (ordered[islandIndex].OutputHeight + padding * 2);
                }
                if ((long)candidate.Width * candidate.Height < lowerBound) continue;
                progress?.CheckCancellation();
                AtlasPackingResult result = TryCandidate(ordered, candidate.Width, candidate.Height, padding, granularity, progress);
                if (result != null)
                {
                    logger.Detail("Packed " + islands.Count + " islands into " + candidate.Width + "x" + candidate.Height +
                                  " padding=" + padding + ". / 装箱成功。");
                    return result;
                }
            }
            return null;
        }

        private static AtlasPackingResult TryCandidate(IList<IslandRecord> islands, int atlasWidth, int atlasHeight,
            int padding, int granularity, ATOProgress progress)
        {
            int cellsWide = Mathf.CeilToInt(atlasWidth / (float)granularity);
            int cellsHigh = Mathf.CeilToInt(atlasHeight / (float)granularity);
            BitArray occupied = new BitArray(cellsWide * cellsHigh);
            AtlasPackingResult result = new AtlasPackingResult
            {
                Width = atlasWidth,
                Height = atlasHeight,
                Padding = padding
            };

            for (int i = 0; i < islands.Count; i++)
            {
                IslandRecord island = islands[i];
                int contentWidth = Mathf.Max(1, island.OutputWidth);
                int contentHeight = Mathf.Max(1, island.OutputHeight);
                RasterMask normal = RasterMask.FromIsland(island, contentWidth, contentHeight, granularity, padding, false);
                RasterMask rotated = RasterMask.FromIsland(island, contentWidth, contentHeight, granularity, padding, true);
                progress?.CheckCancellation();
                AtlasPlacement placement = ScanBLF(occupied, cellsWide, cellsHigh, normal, rotated, island,
                    contentWidth, contentHeight, padding, atlasWidth, atlasHeight, granularity, progress);
                if (placement == null) return null;
                result.Placements.Add(placement);
                result.OccupiedMaskArea += normal.FilledCount * granularity * granularity;
            }
            return result;
        }

        private static AtlasPlacement ScanBLF(BitArray occupied, int cellsWide, int cellsHigh, RasterMask normal,
            RasterMask rotated, IslandRecord island, int contentWidth, int contentHeight, int padding, int atlasWidth,
            int atlasHeight, int granularity, ATOProgress progress)
        {
            RasterMask[] masks = { normal, rotated };
            for (int orientation = 0; orientation < masks.Length; orientation++)
            {
                RasterMask mask = masks[orientation];
                if (mask.Width > cellsWide || mask.Height > cellsHigh) continue;
                for (int y = 0; y <= cellsHigh - mask.Height; y++)
                {
                    if ((y & 15) == 0) progress?.CheckCancellation();
                    for (int x = 0; x <= cellsWide - mask.Width; x++)
                    {
                        if (Overlaps(occupied, cellsWide, cellsHigh, mask, x, y)) continue;
                        Stamp(occupied, cellsWide, mask, x, y);
                        bool rotatedFlag = orientation == 1;
                        int packedContentWidth = rotatedFlag ? contentHeight : contentWidth;
                        int packedContentHeight = rotatedFlag ? contentWidth : contentHeight;
                        return new AtlasPlacement
                        {
                            Island = island,
                            X = x * granularity + padding,
                            Y = y * granularity + padding,
                            ContentWidth = packedContentWidth,
                            ContentHeight = packedContentHeight,
                            PackedWidth = mask.Width * granularity,
                            PackedHeight = mask.Height * granularity,
                            Rotated = rotatedFlag,
                            Padding = padding,
                            AtlasWidth = atlasWidth,
                            AtlasHeight = atlasHeight
                        };
                    }
                }
            }
            return null;
        }

        private static bool Overlaps(BitArray occupied, int cellsWide, int cellsHigh, RasterMask mask, int offsetX,
            int offsetY)
        {
            for (int y = 0; y < mask.Height; y++)
                for (int x = 0; x < mask.Width; x++)
                    if (mask.Bits[y * mask.Width + x] && occupied[(offsetY + y) * cellsWide + offsetX + x]) return true;
            return false;
        }

        private static void Stamp(BitArray occupied, int cellsWide, RasterMask mask, int offsetX, int offsetY)
        {
            for (int y = 0; y < mask.Height; y++)
                for (int x = 0; x < mask.Width; x++)
                    if (mask.Bits[y * mask.Width + x]) occupied[(offsetY + y) * cellsWide + offsetX + x] = true;
        }

        private static List<AtlasCandidate> BuildCandidates(int maxSize, int minimumSize, bool npot)
        {
            List<int> dimensions = new List<int>();
            if (npot)
            {
                for (int size = minimumSize; size <= maxSize; size += 64) dimensions.Add(size);
                if (dimensions.Count == 0 || dimensions[dimensions.Count - 1] != maxSize) dimensions.Add(maxSize);
            }
            else
            {
                int size = 64;
                while (size < minimumSize) size <<= 1;
                while (size <= maxSize)
                {
                    dimensions.Add(size);
                    size <<= 1;
                }
                if (dimensions.Count == 0) dimensions.Add(maxSize);
            }

            List<AtlasCandidate> candidates = new List<AtlasCandidate>();
            for (int i = 0; i < dimensions.Count; i++)
                for (int j = 0; j < dimensions.Count; j++)
                    candidates.Add(new AtlasCandidate(dimensions[i], dimensions[j]));
            candidates.Sort((a, b) =>
            {
                long areaA = (long)a.Width * a.Height;
                long areaB = (long)b.Width * b.Height;
                int area = areaA.CompareTo(areaB);
                if (area != 0) return area;
                float aspectA = Mathf.Abs(Mathf.Log(a.Width / (float)a.Height));
                float aspectB = Mathf.Abs(Mathf.Log(b.Width / (float)b.Height));
                return aspectA.CompareTo(aspectB);
            });
            return candidates;
        }

        private static float EstimateArea(IslandRecord island)
        {
            return Mathf.Max(1f, island.OutputWidth * island.OutputHeight);
        }

        private static float EstimateLongSide(IslandRecord island)
        {
            return Mathf.Max(island.OutputWidth, island.OutputHeight);
        }

        private readonly struct AtlasCandidate
        {
            public readonly int Width;
            public readonly int Height;
            public AtlasCandidate(int width, int height)
            {
                Width = width;
                Height = height;
            }
        }
    }

    internal sealed class RasterMask
    {
        public readonly int Width;
        public readonly int Height;
        public readonly BitArray Bits;
        public int FilledCount { get; private set; }

        private RasterMask(int width, int height)
        {
            Width = width;
            Height = height;
            Bits = new BitArray(width * height);
        }

        public static RasterMask FromIsland(IslandRecord island, int contentWidth, int contentHeight, int granularity,
            int padding, bool rotated)
        {
            int pixelWidth = rotated ? contentHeight : contentWidth;
            int pixelHeight = rotated ? contentWidth : contentHeight;
            int padCells = Mathf.CeilToInt(padding / (float)granularity);
            int width = Mathf.CeilToInt(pixelWidth / (float)granularity) + padCells * 2;
            int height = Mathf.CeilToInt(pixelHeight / (float)granularity) + padCells * 2;
            RasterMask mask = new RasterMask(Mathf.Max(1, width), Mathf.Max(1, height));
            for (int triangleIndex = 0; triangleIndex < island.Triangles.Count; triangleIndex++)
            {
                IslandTriangle triangle = island.Triangles[triangleIndex];
                Vector2 a = Local(triangle.UVA, island, contentWidth, contentHeight, rotated);
                Vector2 b = Local(triangle.UVB, island, contentWidth, contentHeight, rotated);
                Vector2 c = Local(triangle.UVC, island, contentWidth, contentHeight, rotated);
                int minX = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(a.x, Mathf.Min(b.x, c.x)) / granularity) + padCells, 0, mask.Width - 1);
                int maxX = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(a.x, Mathf.Max(b.x, c.x)) / granularity) + padCells, 0, mask.Width - 1);
                int minY = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(a.y, Mathf.Min(b.y, c.y)) / granularity) + padCells, 0, mask.Height - 1);
                int maxY = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(a.y, Mathf.Max(b.y, c.y)) / granularity) + padCells, 0, mask.Height - 1);
                for (int y = minY; y <= maxY; y++)
                    for (int x = minX; x <= maxX; x++)
                    {
                        Vector2 point = new Vector2((x - padCells + 0.5f) * granularity,
                            (y - padCells + 0.5f) * granularity);
                        if (PointInTriangle(point, a, b, c)) Set(x, y);
                    }
            }
            if (mask.FilledCount == 0) mask.Set(Mathf.Clamp(padCells, 0, mask.Width - 1), Mathf.Clamp(padCells, 0, mask.Height - 1));
            Dilate(mask, padCells);
            return mask;
        }

        private static Vector2 Local(Vector2 uv, IslandRecord island, int contentWidth, int contentHeight, bool rotated)
        {
            Vector2 normalized = uv + island.UVTranslation;
            float u = island.UVBounds.width <= 1e-8f ? 0.5f : Mathf.InverseLerp(island.UVBounds.xMin, island.UVBounds.xMax, normalized.x);
            float v = island.UVBounds.height <= 1e-8f ? 0.5f : Mathf.InverseLerp(island.UVBounds.yMin, island.UVBounds.yMax, normalized.y);
            if (rotated) return new Vector2((1f - v) * contentHeight, u * contentWidth);
            return new Vector2(u * contentWidth, v * contentHeight);
        }

        private void Set(int x, int y)
        {
            if (x < 0 || x >= Width || y < 0 || y >= Height) return;
            int index = y * Width + x;
            if (Bits[index]) return;
            Bits[index] = true;
            FilledCount++;
        }

        private static void Dilate(RasterMask mask, int radius)
        {
            if (radius <= 0) return;
            BitArray original = (BitArray)mask.Bits.Clone();
            for (int y = 0; y < mask.Height; y++)
                for (int x = 0; x < mask.Width; x++)
                {
                    bool found = false;
                    for (int dy = -radius; dy <= radius && !found; dy++)
                        for (int dx = -radius; dx <= radius; dx++)
                        {
                            int sx = x + dx;
                            int sy = y + dy;
                            if (sx >= 0 && sy >= 0 && sx < mask.Width && sy < mask.Height && original[sy * mask.Width + sx])
                            {
                                found = true;
                                break;
                            }
                        }
                    if (found) mask.Set(x, y);
                }
        }

        private static bool PointInTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float d1 = Sign(p, a, b);
            float d2 = Sign(p, b, c);
            float d3 = Sign(p, c, a);
            bool hasNegative = d1 < 0f || d2 < 0f || d3 < 0f;
            bool hasPositive = d1 > 0f || d2 > 0f || d3 > 0f;
            return !(hasNegative && hasPositive);
        }

        private static float Sign(Vector2 p1, Vector2 p2, Vector2 p3)
        {
            return (p1.x - p3.x) * (p2.y - p3.y) - (p2.x - p3.x) * (p1.y - p3.y);
        }
    }
}
