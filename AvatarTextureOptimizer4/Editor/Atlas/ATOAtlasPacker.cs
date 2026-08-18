// Avatar Texture Optimizer (ATO)
// Atlas packer: type-grouping, global BLF layout (normalized placement shared across all
// type-group atlases), candidate atlas pools (POT / experimental NPOT, square + rectangular,
// sorted by area then aspect ratio), and fallback for islands that cannot fit the largest atlas.
// 图集装箱器：类型分组、全局 BLF 布局（跨类型组共享归一化摆放）、候选图集池（POT / 实验性 NPOT，
// 正方形 + 非正方形，按面积、长宽比排序），以及装不进最大图集的岛的回退。
//
// Design notes / 设计说明：
//   - The global layout is computed once at reference resolution (maxAtlasSize); every UV space
//     gets a normalized placement + rotation. All type-group atlases of a space reuse that
//     placement, guaranteeing the same UV position across atlases (UV-group requirement).
//     全局布局在参考分辨率（maxAtlasSize）下一次算好；每个 UV 空间得到归一化摆放 + 旋转。
//     该空间的所有类型组图集复用同一摆放，从而保证同一 UV 在不同图集上位置一致（UV 组要求）。
//   - Per type group, the atlas size is the smallest candidate whose width/height can contain
//     every island of the group at its native (scaled) resolution, computed from the normalized
//     placement, so islands are never clipped.
//     每个类型组的图集尺寸 = 能容纳该组全部岛（原生缩放分辨率）的最小候选（由归一化摆放反推），
//     保证岛永不被截断。
//   - Normal-map spaces are rotation-locked to 0° (rotating a tangent-space normal map without
//     recomputing tangents breaks lighting). / 含法线的空间锁定 0° 旋转（切线不重算时旋转法线会破坏光照）。

using System.Collections.Generic;
using UnityEngine;

namespace NetFosa.ATO
{
    /// <summary>
    /// Type-group key: category + color space + filter mode + alpha + normal/mask companions.
    /// 类型组键：分类 + 色彩空间 + filterMode + alpha + 是否有法线/遮罩伴随。
    /// </summary>
    public readonly struct ATOTypeGroupKey : System.IEquatable<ATOTypeGroupKey>
    {
        public readonly ATOTextureCategory category;
        public readonly bool isSRGB;
        public readonly FilterMode filterMode;
        public readonly bool hasAlpha;
        public readonly bool hasNormalCompanion;
        public readonly bool hasMaskCompanion;

        public ATOTypeGroupKey(ATOTextureCategory c, bool srgb, FilterMode fm, bool alpha, bool normal, bool mask)
        {
            category = c; isSRGB = srgb; filterMode = fm; hasAlpha = alpha; hasNormalCompanion = normal; hasMaskCompanion = mask;
        }

        public bool Equals(ATOTypeGroupKey o) => category == o.category && isSRGB == o.isSRGB && filterMode == o.filterMode
            && hasAlpha == o.hasAlpha && hasNormalCompanion == o.hasNormalCompanion && hasMaskCompanion == o.hasMaskCompanion;
        public override bool Equals(object o) => o is ATOTypeGroupKey k && Equals(k);
        public override int GetHashCode() => ((int)category << 8) ^ (isSRGB ? 1 : 0) ^ ((int)filterMode << 2)
            ^ (hasAlpha ? 4 : 0) ^ (hasNormalCompanion ? 8 : 0) ^ (hasMaskCompanion ? 16 : 0);
    }

    /// <summary>
    /// Stage 6a: compute a normalized layout and per-group atlas records.
    /// 阶段 6a：计算归一化布局与各类型组图集记录。
    /// </summary>
    public static class ATOAtlasPacker
    {
        private sealed class SpaceData
        {
            public ATOUvSpace space;
            public Vector2[] uvs;
            public int[] tris;
            public byte[] mask;
            public int cellsW, cellsH;
            public int covered;
        }

        public static void Pack(ATOBuildContext build, ATOProgress progress)
        {
            var spaces = new List<ATOUvSpace>();
            foreach (var s in build.uvSpaces)
            {
                bool pinned = false;
                foreach (var t in s.textures) if (t.skipAllOptimization) pinned = true;
                if (pinned) continue;
                s.textures.RemoveAll(t => t.skipAllOptimization || t.texture == null);
                if (s.textures.Count == 0) continue;
                s.hasNormalTexture = false;
                foreach (var t in s.textures) if (t.Category == ATOTextureCategory.NormalMap) s.hasNormalTexture = true;
                spaces.Add(s);
            }

            progress.Begin(spaces.Count + 1);
            int refRes = build.profile.maxAtlasSize;
            int cellPx = ATOConstants.RasterCellSize;
            int refCells = refRes / cellPx;
            int padCells = Mathf.Max(1, Mathf.CeilToInt(Mathf.Max(build.profile.padding, Mathf.CeilToInt(refRes / 128f)) / cellPx));

            var spaceData = new List<SpaceData>();
            foreach (var s in spaces)
            {
                var sd = new SpaceData { space = s };
                BuildUnionGeometry(s, out sd.uvs, out sd.tris);
                ATORasterizer.Rasterize(sd.uvs, sd.tris, s.scaledMinUv, s.scaledSizeUv, refRes, cellPx,
                    out sd.mask, out sd.cellsW, out sd.cellsH);
                sd.covered = ATORasterizer.CountCovered(sd.mask);
                spaceData.Add(sd);
                progress.Advance(1);
            }

            // Sort by footprint area desc (largest first), then by max side length desc.
            // 按光栅化面积降序、再按边长降序排序。
            spaceData.Sort((a, b) =>
            {
                int c = b.covered.CompareTo(a.covered);
                if (c != 0) return c;
                return Mathf.Max(b.cellsW, b.cellsH).CompareTo(Mathf.Max(a.cellsW, a.cellsH));
            });

            // Global BLF layout into pages. / 全局 BLF 布局分页。
            var pages = new List<byte[]>();
            foreach (var sd in spaceData)
            {
                bool placed = false;
                for (int p = 0; p < pages.Count && !placed; p++)
                {
                    if (TryPlace(sd, pages[p], refCells, padCells, out int ox, out int oy, out int rot))
                    {
                        sd.space.pageIndex = p;
                        sd.space.rotation = rot;
                        sd.space.placementMinUv = new Vector2(ox * cellPx / (float)refRes, oy * cellPx / (float)refRes);
                        placed = true;
                    }
                }
                if (!placed)
                {
                    var grid = new byte[refCells * refCells];
                    pages.Add(grid);
                    if (TryPlace(sd, grid, refCells, padCells, out int ox, out int oy, out int rot))
                    {
                        sd.space.pageIndex = pages.Count - 1;
                        sd.space.rotation = rot;
                        sd.space.placementMinUv = new Vector2(ox * cellPx / (float)refRes, oy * cellPx / (float)refRes);
                    }
                    else
                    {
                        MarkFallback(build, sd.space);
                    }
                }
                progress.Advance(1);
            }

            // Per page, per type group: choose atlas sizes and create records. / 每页每类型组：选择尺寸并建记录。
            for (int p = 0; p < pages.Count; p++)
                AssignGroupAtlases(build, spaces, p);
        }

        /// <summary>Compute scaled footprints and concatenated union geometry. / 计算缩放足迹与拼接并集几何。</summary>
        private static void BuildUnionGeometry(ATOUvSpace space, out Vector2[] uvs, out int[] tris)
        {
            var uvList = new List<Vector2>();
            var triList = new List<int>();
            var min = new Vector2(float.MaxValue, float.MaxValue);
            var max = new Vector2(float.MinValue, float.MinValue);

            foreach (var isl in space.islands)
            {
                var ts = isl.TotalScale;
                var c = (isl.minUV + isl.maxUV) * 0.5f;
                int baseIdx = uvList.Count;
                for (int i = 0; i < isl.uv.Length; i++)
                    uvList.Add(c + (isl.uv[i] - c) * ts);
                for (int i = 0; i < isl.triangles.Length; i++)
                    triList.Add(baseIdx + isl.triangles[i]);

                var smin = c + (isl.minUV - c) * ts;
                var smax = c + (isl.maxUV - c) * ts;
                isl.scaledMinUv = smin;
                isl.scaledSizeUv = smax - smin;
                min = Vector2.Min(min, smin);
                max = Vector2.Max(max, smax);
            }
            space.scaledMinUv = min;
            space.scaledSizeUv = max - min;
            uvs = uvList.ToArray();
            tris = triList.ToArray();
        }

        private static bool TryPlace(SpaceData sd, byte[] grid, int refCells, int padCells, out int ox, out int oy, out int rot)
        {
            int rotCount = sd.space.hasNormalTexture ? 1 : 4;
            for (int r = 0; r < rotCount; r++)
            {
                byte[] m = sd.mask; int mw = sd.cellsW, mh = sd.cellsH;
                for (int i = 0; i < r; i++)
                {
                    ATORasterizer.Rotate90(m, mw, mh, out var rm, out var rw, out var rh);
                    m = rm; mw = rw; mh = rh;
                }
                for (int y = 0; y <= refCells - mh; y++)
                {
                    for (int x = 0; x <= refCells - mw; x++)
                    {
                        if (ATORasterizer.Overlaps(grid, refCells, refCells, m, mw, mh, x, y)) continue;
                        int px0 = Mathf.Max(0, x - padCells), py0 = Mathf.Max(0, y - padCells);
                        int px1 = Mathf.Min(refCells, x + mw + padCells), py1 = Mathf.Min(refCells, y + mh + padCells);
                        if (PaddedOverlaps(grid, refCells, px0, py0, px1, py1)) continue;
                        for (int yy = py0; yy < py1; yy++)
                            for (int xx = px0; xx < px1; xx++)
                                grid[yy * refCells + xx] = 1;
                        ox = x; oy = y; rot = r;
                        return true;
                    }
                }
            }
            ox = oy = 0; rot = 0;
            return false;
        }

        private static bool PaddedOverlaps(byte[] grid, int refCells, int x0, int y0, int x1, int y1)
        {
            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++)
                    if (grid[y * refCells + x] != 0) return true;
            return false;
        }

        private static void MarkFallback(ATOBuildContext build, ATOUvSpace space)
        {
            foreach (var t in space.textures)
            {
                t.fallbackNoAtlas = true;
                t.skipAllOptimization = true;
            }
            build.report.warnings.Add($"UV space on mesh {space.meshId} ch{space.uvChannel} cannot fit the largest atlas; skipped. / 网格 {space.meshId} 通道 {space.uvChannel} 的 UV 空间装不进最大图集，已跳过。");
            ATOLogger.Warn(build.report.warnings[build.report.warnings.Count - 1]);
        }

        private static void AssignGroupAtlases(ATOBuildContext build, List<ATOUvSpace> spaces, int page)
        {
            var groups = new Dictionary<ATOTypeGroupKey, List<ATOTextureRef>>();
            foreach (var s in spaces)
            {
                if (s.pageIndex != page || IsFallback(s)) continue;
                foreach (var t in s.textures)
                {
                    var key = GroupKeyFor(s, t);
                    if (!groups.TryGetValue(key, out var list)) groups[key] = list = new List<ATOTextureRef>();
                    if (!list.Contains(t)) list.Add(t);
                }
            }

            foreach (var kvp in groups)
                BuildAtlasForGroup(build, kvp.Key, kvp.Value, spaces, page);
        }

        private static bool IsFallback(ATOUvSpace s)
        {
            foreach (var t in s.textures) if (t.fallbackNoAtlas) return true;
            return false;
        }

        private static ATOTypeGroupKey GroupKeyFor(ATOUvSpace space, ATOTextureRef t)
        {
            bool hasNormal = false, hasMask = false;
            foreach (var other in space.textures)
            {
                if (other == t) continue;
                if (other.Category == ATOTextureCategory.NormalMap) hasNormal = true;
                if (other.Category == ATOTextureCategory.Mask) hasMask = true;
            }
            return new ATOTypeGroupKey(t.Category, t.isSRGB, t.filterMode, t.hasAlpha, hasNormal, hasMask);
        }

        private static void BuildAtlasForGroup(ATOBuildContext build, ATOTypeGroupKey key, List<ATOTextureRef> textures,
            List<ATOUvSpace> spaces, int page)
        {
            // Gather islands + compute required atlas dimensions so nothing is clipped.
            // 收集岛并计算所需图集尺寸，保证不被截断。
            var islands = new List<ATOIsland>();
            foreach (var s in spaces)
            {
                if (s.pageIndex != page || IsFallback(s)) continue;
                bool belongs = false;
                foreach (var t in s.textures) if (textures.Contains(t)) belongs = true;
                if (!belongs) continue;
                islands.AddRange(s.islands);
            }
            if (islands.Count == 0) return;

            float reqX = 1f, reqY = 1f;
            int maxTexDim = 1;
            foreach (var t in textures) maxTexDim = Mathf.Max(maxTexDim, Mathf.Max(t.width, t.height));
            reqX = Mathf.Max(reqX, maxTexDim);
            reqY = Mathf.Max(reqY, maxTexDim);

            foreach (var isl in islands)
            {
                float srcDim = SourceTextureDim(spaces, isl, key, textures);
                if (srcDim <= 0f) continue;
                // Conservative diagonal handles rotation (normal maps lock to 0°, others rotate). / 对角线保守处理旋转。
                float diag = Mathf.Max(isl.scaledSizeUv.x, isl.scaledSizeUv.y);
                float px = diag * srcDim;
                float denomX = Mathf.Max(1e-4f, 1f - isl.placementMinUv.x);
                float denomY = Mathf.Max(1e-4f, 1f - isl.placementMinUv.y);
                reqX = Mathf.Max(reqX, px / denomX);
                reqY = Mathf.Max(reqY, px / denomY);
            }

            int maxAtlas = build.profile.maxAtlasSize;
            if (reqX > maxAtlas || reqY > maxAtlas)
            {
                // Even a single texture cannot fit: fallback the whole group's spaces. / 单张贴图都装不下：整组兜底。
                foreach (var t in textures)
                {
                    t.fallbackNoAtlas = true;
                    t.skipAllOptimization = true;
                }
                build.report.warnings.Add($"Type group '{key.category}' needs {reqX}x{reqY} which exceeds the max atlas size; skipped. / 类型组 '{key.category}' 需要 {reqX}x{reqY} 超出最大图集尺寸，已跳过。");
                ATOLogger.Warn(build.report.warnings[build.report.warnings.Count - 1]);
                return;
            }

            // Pick from the candidate pool: smallest area, then closest to square. / 从候选池选取：面积最小、最接近正方形。
            var (aw, ah) = PickCandidate(build, reqX, reqY);
            var atlas = new ATOAtlas
            {
                name = $"{ATOConstants.AtlasNamePrefix}{key.category}_{(key.hasNormalCompanion ? "N" : "")}_{(key.hasMaskCompanion ? "M" : "")}_{aw}x{ah}",
                width = aw,
                height = ah,
                category = key.category,
                typeGroup = key.category,
                hasAlpha = key.hasAlpha,
            };

            foreach (var isl in islands)
            {
                isl.placed = true;
                isl.atlasIndex = build.atlases.Count;
                atlas.islands.Add(isl);
            }
            foreach (var t in textures)
                if (!atlas.sources.Contains(t))
                    atlas.sources.Add(t);

            atlas.islandCount = atlas.islands.Count;
            build.atlases.Add(atlas);
            build.report.atlasCount++;
            ATOLogger.Debug($"Atlas '{atlas.name}' ({aw}x{ah}) for {atlas.sources.Count} textures, {atlas.islandCount} islands.");
        }

        /// <summary>Source texture dimension (pixels) that this island is drawn from in this group. / 该岛在本组中绘制所用的源贴图尺寸（像素）。</summary>
        private static float SourceTextureDim(List<ATOUvSpace> spaces, ATOIsland isl, ATOTypeGroupKey key, List<ATOTextureRef> textures)
        {
            ATOUvSpace space = null;
            foreach (var s in spaces) if (s.meshId == isl.meshId && s.uvChannel == isl.uvChannel) { space = s; break; }
            if (space == null) return 0f;
            float dim = 0f;
            foreach (var t in space.textures)
                if (textures.Contains(t))
                    dim = Mathf.Max(dim, Mathf.Max(t.width, t.height));
            return dim;
        }

        /// <summary>
        /// Candidate atlas pool: POT sizes (64..max ×2) or NPOT (64..max step 64); square + rectangular;
        /// sorted by area ascending, then by long/short side ratio ascending (square first).
        /// 候选图集池：POT（64..max ×2）或 NPOT（64..max 步进 64）；含正方形与非正方形；
        /// 按面积升序、再按长边/短边比升序（最接近正方形优先）。
        /// </summary>
        private static (int w, int h) PickCandidate(ATOBuildContext build, float reqX, float reqY)
        {
            int max = build.profile.maxAtlasSize;
            var sides = new List<int>();
            if (build.profile.npotAtlas)
            {
                for (int s = ATOConstants.MinAtlasSize; s <= max; s += 64) sides.Add(s);
            }
            else
            {
                for (int s = ATOConstants.MinAtlasSize; s <= max; s *= 2) sides.Add(s);
            }
            if (sides.Count == 0) sides.Add(max);

            // Build candidates w>=h. / 生成候选 w>=h。
            var candidates = new List<(int w, int h)>();
            foreach (var w in sides)
                foreach (var h in sides)
                    if (h <= w) candidates.Add((w, h));

            candidates.Sort((a, b) =>
            {
                int areaCmp = (a.w * a.h).CompareTo(b.w * b.h);
                if (areaCmp != 0) return areaCmp;
                float rA = (float)a.w / a.h, rB = (float)b.w / b.h;
                return rA.CompareTo(rB);
            });

            foreach (var c in candidates)
                if (c.w >= reqX && c.h >= reqY)
                    return (c.w, c.h);

            return (max, max); // unreachable given the earlier size check / 前面的尺寸检查保证不会到这里
        }
    }
}
