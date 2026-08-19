// Atlas Bin Packer - Triangle rasterization + BLF + rotation + normal map safety
// 图集装箱器 - 三角形光栅化 + BLF + 旋转 + 法线贴图安全

using System;
using System.Collections.Generic;
using System.Linq;
using net.fosa.avatar_texture_optimizer.Editor.Core;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.Editor.Atlas
{
    public static class AtlasBinPacker
    {
        private const int RASTER_GRAN = 4; // 4px granularity

        public static List<AtlasResult> PackAtlases(
            List<UVIsland> islands, List<TextureTypeGroup> typeGroups,
            ATOBuildContext atoCtx, int maxAtlasSize, int minPadding,
            bool enableNPOT, bool isMobile)
        {
            var results = new List<AtlasResult>();
            int effectiveMax = isMobile ? Mathf.Min(maxAtlasSize, 4096) : maxAtlasSize;

            // Group islands by type group
            var byGroup = new Dictionary<int, List<UVIsland>>();
            foreach (var island in islands)
            {
                if (island.IsWhitelisted || island.SkipAtlasOnly) continue;
                int gid = FindTypeGroup(island, typeGroups);
                if (!byGroup.ContainsKey(gid)) byGroup[gid] = new List<UVIsland>();
                byGroup[gid].Add(island);
            }

            int atlasIdx = 0;
            foreach (var kvp in byGroup)
            {
                int groupId = kvp.Key;
                var groupIslands = kvp.Value;

                // Rasterize all islands (with cache)
                foreach (var isl in groupIslands)
                {
                    if (isl.RasterBitmask == null)
                        RasterizeIslandTriangles(isl, atoCtx);
                }

                // Sort by rasterized area descending, then by longest edge descending
                groupIslands.Sort((a, b) =>
                {
                    int areaCmp = GetRasterArea(b).CompareTo(GetRasterArea(a));
                    if (areaCmp != 0) return areaCmp;
                    return GetLongestEdge(b).CompareTo(GetLongestEdge(a));
                });

                // Generate candidate pool
                var candidates = GeneratePool(effectiveMax, enableNPOT);

                // Total area check
                float totalArea = groupIslands.Sum(i => (float)GetRasterArea(i));
                var viable = candidates.Where(c => (long)c.W * c.H >= totalArea * 1.2f).ToList();
                if (viable.Count == 0) viable = candidates;

                // Sort candidates: area ascending, aspect ratio (most square first)
                viable.Sort((a, b) =>
                {
                    int ac = ((long)a.W * a.H).CompareTo((long)b.W * b.H);
                    if (ac != 0) return ac;
                    float ra = (float)Mathf.Max(a.W, a.H) / Mathf.Max(Mathf.Min(a.W, a.H), 1);
                    float rb = (float)Mathf.Max(b.W, b.H) / Mathf.Max(Mathf.Min(b.W, b.H), 1);
                    return ra.CompareTo(rb);
                });

                var remaining = new List<UVIsland>(groupIslands);
                while (remaining.Count > 0)
                {
                    bool packed = false;
                    foreach (var cand in viable)
                    {
                        int pad = Mathf.Max(minPadding, Mathf.CeilToInt((float)Mathf.Max(cand.W, cand.H) / 128f));
                        var bitmap = new AtlasBitmap(cand.W, cand.H);
                        var packedIslands = new List<PackedIsland>();
                        var failed = new List<UVIsland>();

                        foreach (var isl in remaining)
                        {
                            var pr = bitmap.TryPack(isl, pad);
                            if (pr != null) packedIslands.Add(pr);
                            else failed.Add(isl);
                        }

                        if (packedIslands.Count > 0)
                        {
                            float util = (float)packedIslands.Sum(p => p.Width * p.Height) / (cand.W * cand.H);
                            results.Add(new AtlasResult
                            {
                                Index = atlasIdx++,
                                Name = $"ATO_Atlas_{atlasIdx}_{cand.W}x{cand.H}",
                                Width = cand.W, Height = cand.H,
                                TypeGroupId = groupId,
                                PackedIslands = packedIslands,
                                IslandCount = packedIslands.Count,
                                Utilization = util
                            });
                            remaining = failed;
                            packed = true;
                            break;
                        }
                    }
                    if (!packed)
                    {
                        if (remaining.Count > 0)
                        {
                            atoCtx.AddWarning($"Island {remaining[0].Id} can't fit max atlas. Fallback to direct scale. / 岛{remaining[0].Id}无法装入最大图集，降级为直接缩放。");
                            remaining.RemoveAt(0);
                        }
                        else break;
                    }
                }
            }
            return results;
        }

        private static int FindTypeGroup(UVIsland island, List<TextureTypeGroup> groups)
        {
            foreach (var g in groups)
                if (g.UVGroupIds.Contains(island.UVGroupId)) return g.Id;
            return groups.Count > 0 ? groups[0].Id : -1;
        }

        /// <summary>
        /// Rasterize island using actual triangle shapes (not bounding box).
        /// 使用实际三角形形状光栅化岛（非包围盒）。
        /// </summary>
        private static void RasterizeIslandTriangles(UVIsland island, ATOBuildContext atoCtx)
        {
            if (island.TrianglesUV == null || island.TrianglesUV.Count == 0)
            {
                // Fallback to bounding box fill
                FallbackBBoxRaster(island);
                return;
            }

            // Determine pixel size based on scale factor
            float bbW = island.BoundsMax.x - island.BoundsMin.x;
            float bbH = island.BoundsMax.y - island.BoundsMin.y;
            int texSize = 1024; // Reference texture size
            if (island.SourceTextureIndex >= 0 && island.SourceTextureIndex < atoCtx.AllTextures.Count)
                texSize = Mathf.Max(atoCtx.AllTextures[island.SourceTextureIndex].Width,
                                    atoCtx.AllTextures[island.SourceTextureIndex].Height);

            float scaleFactor = Mathf.Max(island.ScaleFactor.x, island.ScaleFactor.y);
            int pixelW = Mathf.Max(RASTER_GRAN, Mathf.CeilToInt(bbW * scaleFactor * texSize));
            int pixelH = Mathf.Max(RASTER_GRAN, Mathf.CeilToInt(bbH * scaleFactor * texSize));
            int gridW = Mathf.Max(1, pixelW / RASTER_GRAN);
            int gridH = Mathf.Max(1, pixelH / RASTER_GRAN);

            var bitmask = new bool[gridW, gridH];

            float invBBW = bbW > 0 ? 1f / bbW : 1f;
            float invBBH = bbH > 0 ? 1f / bbH : 1f;

            // Rasterize each triangle using edge function method
            foreach (var tri in island.TrianglesUV)
            {
                // Normalize triangle to island local space [0,1]
                float ax = (tri.V0.x - island.BoundsMin.x) * invBBW;
                float ay = (tri.V0.y - island.BoundsMin.y) * invBBH;
                float bx = (tri.V1.x - island.BoundsMin.x) * invBBW;
                float by = (tri.V1.y - island.BoundsMin.y) * invBBH;
                float cx = (tri.V2.x - island.BoundsMin.x) * invBBW;
                float cy = (tri.V2.y - island.BoundsMin.y) * invBBH;

                // Convert to grid coordinates
                int minX = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(ax, bx, cx) * gridW));
                int maxX = Mathf.Min(gridW - 1, Mathf.CeilToInt(Mathf.Max(ax, bx, cx) * gridW));
                int minY = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(ay, by, cy) * gridH));
                int maxY = Mathf.Min(gridH - 1, Mathf.CeilToInt(Mathf.Max(ay, by, cy) * gridH));

                for (int gy = minY; gy <= maxY; gy++)
                {
                    for (int gx = minX; gx <= maxX; gx++)
                    {
                        // Center of grid cell in normalized space
                        float px = (gx + 0.5f) / gridW;
                        float py = (gy + 0.5f) / gridH;

                        if (PointInTriangle(px, py, ax, ay, bx, by, cx, cy))
                            bitmask[gx, gy] = true;
                    }
                }
            }

            island.RasterBitmask = bitmask;

            // Cache the rasterization result
            atoCtx.RasterCache[island.Id] = bitmask;
        }

        private static bool PointInTriangle(float px, float py,
            float ax, float ay, float bx, float by, float cx, float cy)
        {
            float d1 = (px - bx) * (ay - by) - (ax - bx) * (py - by);
            float d2 = (px - cx) * (by - cy) - (bx - cx) * (py - cy);
            float d3 = (px - ax) * (cy - ay) - (cx - ax) * (py - ay);
            bool hasNeg = (d1 < 0) || (d2 < 0) || (d3 < 0);
            bool hasPos = (d1 > 0) || (d2 > 0) || (d3 > 0);
            return !(hasNeg && hasPos);
        }

        private static void FallbackBBoxRaster(UVIsland island)
        {
            float bbW = island.BoundsMax.x - island.BoundsMin.x;
            float bbH = island.BoundsMax.y - island.BoundsMin.y;
            int gw = Mathf.Max(1, Mathf.CeilToInt(bbW * 256));
            int gh = Mathf.Max(1, Mathf.CeilToInt(bbH * 256));
            var bm = new bool[gw, gh];
            for (int y = 0; y < gh; y++) for (int x = 0; x < gw; x++) bm[x, y] = true;
            island.RasterBitmask = bm;
        }

        private static int GetRasterArea(UVIsland island)
        {
            if (island.RasterBitmask == null) return 0;
            int count = 0;
            int w = island.RasterBitmask.GetLength(0), h = island.RasterBitmask.GetLength(1);
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) if (island.RasterBitmask[x, y]) count++;
            return count;
        }

        private static int GetLongestEdge(UVIsland island)
        {
            if (island.RasterBitmask == null) return 0;
            return Mathf.Max(island.RasterBitmask.GetLength(0), island.RasterBitmask.GetLength(1));
        }

        private static List<(int W, int H)> GeneratePool(int maxSize, bool npot)
        {
            var pool = new List<(int, int)>();
            var seen = new HashSet<string>();

            if (npot)
            {
                for (int s = 64; s <= maxSize; s += 64)
                {
                    for (int h = 64; h <= s; h += 64)
                    {
                        float ratio = (float)s / h;
                        if (ratio <= 2f && seen.Add($"{s}x{h}"))
                        {
                            pool.Add((s, h));
                            if (s != h && seen.Add($"{h}x{s}"))
                                pool.Add((h, s));
                        }
                    }
                }
            }
            else
            {
                for (int s = 64; s <= maxSize; s *= 2)
                {
                    pool.Add((s, s));
                    for (int h = 64; h < s; h *= 2)
                    {
                        pool.Add((s, h));
                        pool.Add((h, s));
                    }
                }
            }
            return pool;
        }
    }

    /// <summary>
    /// Bitmap atlas for shape-based raster packing.
    /// 基于形状的光栅装箱位图图集。
    /// </summary>
    public class AtlasBitmap
    {
        private bool[,] _occ;
        private int _w, _h;

        public AtlasBitmap(int w, int h)
        {
            _w = w; _h = h;
            _occ = new bool[w / 4, h / 4];
        }

        /// <summary>
        /// Try packing with 0° and 90° rotation.
        /// Normal maps: rotation transposes the bitmask but tangent data stays unchanged.
        /// 尝试0°和90°旋转装箱。法线贴图：旋转转置位掩码但切线数据保持不变。
        /// </summary>
        public PackedIsland TryPack(UVIsland island, int padding)
        {
            if (island.RasterBitmask == null) return null;

            int bw = island.RasterBitmask.GetLength(0) * 4;
            int bh = island.RasterBitmask.GetLength(1) * 4;

            for (int rot = 0; rot < 2; rot++)
            {
                int tw = rot == 0 ? bw : bh;
                int th = rot == 0 ? bh : bw;
                int pw = tw + padding * 2;
                int ph = th + padding * 2;
                if (pw > _w || ph > _h) continue;

                // Bottom-Left-Fill scan
                for (int y = 0; y <= _h - ph; y += 4)
                {
                    for (int x = 0; x <= _w - pw; x += 4)
                    {
                        if (CanPlace(island.RasterBitmask, x + padding, y + padding, rot == 1))
                        {
                            Place(island.RasterBitmask, x + padding, y + padding, rot == 1);
                            return new PackedIsland
                            {
                                IslandId = island.Id,
                                X = x + padding, Y = y + padding,
                                Width = tw, Height = th,
                                Rotated = rot == 1
                            };
                        }
                    }
                }
            }
            return null;
        }

        private bool CanPlace(bool[,] bm, int px, int py, bool rotated)
        {
            int bw = bm.GetLength(0), bh = bm.GetLength(1);
            for (int by = 0; by < bh; by++)
            {
                for (int bx = 0; bx < bw; bx++)
                {
                    bool occ = rotated ? bm[by, bw - 1 - bx] : bm[bx, by];
                    if (!occ) continue;
                    int gx = (px + bx * 4) / 4;
                    int gy = (py + by * 4) / 4;
                    if (gx < 0 || gx >= _occ.GetLength(0) || gy < 0 || gy >= _occ.GetLength(1))
                        return false;
                    if (_occ[gx, gy]) return false;
                }
            }
            return true;
        }

        private void Place(bool[,] bm, int px, int py, bool rotated)
        {
            int bw = bm.GetLength(0), bh = bm.GetLength(1);
            for (int by = 0; by < bh; by++)
            {
                for (int bx = 0; bx < bw; bx++)
                {
                    bool occ = rotated ? bm[by, bw - 1 - bx] : bm[bx, by];
                    if (!occ) continue;
                    int gx = (px + bx * 4) / 4;
                    int gy = (py + by * 4) / 4;
                    if (gx >= 0 && gx < _occ.GetLength(0) && gy >= 0 && gy < _occ.GetLength(1))
                        _occ[gx, gy] = true;
                }
            }
        }
    }
}
