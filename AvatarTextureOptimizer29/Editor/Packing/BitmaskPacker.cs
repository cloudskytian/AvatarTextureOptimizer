// BLF bitmask packing: full scan bottom-left-fill over 4px cell grids, 90° rotation
// (mask transpose), per-type-group open-atlas queues, atomic unit = packing component,
// candidate selection per spec (area floor from remaining queue, smallest first).
// BLF 位掩码装箱：4px 单元全网扫描，90° 旋转（掩码转置），按类型组的开放式图集队列，
// 原子单元=装箱分量，候选选择按需求书（以剩余队列面积为下限，从小到大）。
//
// Trials always run on a cloned occupancy mask; success commits the clone.
// 尝试永远在克隆的占用掩码上进行；成功后提交克隆。

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace net.fosa.ato.editor
{
    internal class Placement
    {
        internal UvIsland island;
        internal RectInt rect;      // visible pixel rect on page / 页面可见像素矩形
        internal bool rotated;
    }

    internal class AtlasLayout
    {
        internal int pageW, pageH, padding;
        internal string typeGroupKey;
        internal bool srgb;
        internal readonly List<Placement> placements = new List<Placement>();
        internal readonly HashSet<Texture2D> textures = new HashSet<Texture2D>();
        internal BitMask used;
        // secondary page scales (normal/mask may shrink) / 次要页面缩放
        internal float normalPageScale = 1f, maskPageScale = 1f;
        internal int normalW, normalH, maskW, maskH;
    }

    internal static class BitmaskPacker
    {
        internal const int CellSize = 4; // 4px granularity / 4px 粒度

        internal static void Pack(AtoSession s)
        {
            using var _ = ATOLog.Scope("PackAtlases");

            // island footprints (max across textures per axis; spec barrel effect)
            // 岛足印（逐轴取各贴图最大；需求书木桶效应）
            var footprints = new Dictionary<UvIsland, Vector2Int>();
            foreach (var pc in s.components)
            foreach (var isl in pc.islands)
            {
                int w = 1, h = 1;
                foreach (var t in isl.textures)
                    if (isl.scaledSize.TryGetValue(t, out var sz))
                    {
                        w = Mathf.Max(w, sz.x);
                        h = Mathf.Max(h, sz.y);
                    }
                footprints[isl] = new Vector2Int(w, h);
            }

            var units = s.components
                .Where(pc => !pc.fallbackNoAtlas)
                .OrderByDescending(pc => pc.islands.Sum(i => (long)footprints[i].x * footprints[i].y))
                .ToList();

            var openAtlases = new Dictionary<string, List<AtlasLayout>>();
            s.atlases.Clear();
            var maskCache = new Dictionary<(UvIsland, int), BitMask>();
            int index = 0;

            foreach (var pc in units)
            {
                Progress.Report("packing", index / (float)Mathf.Max(1, units.Count), $"component {index + 1}/{units.Count}");
                index++;
                if (pc.textures.Count == 0 || pc.islands.Count == 0) continue;

                string key = TypeGroupKey(pc);
                if (!openAtlases.TryGetValue(key, out var queue)) openAtlases[key] = queue = new List<AtlasLayout>();

                AtlasLayout placed = null;
                // reuse open queues first (spec) / 先复用已开队列
                foreach (var atlas in queue)
                {
                    var trial = TryPlaceAll(atlas, pc, footprints, maskCache, atlas.padding);
                    if (trial != null)
                    {
                        atlas.used = trial.used;
                        atlas.placements.AddRange(trial.placements);
                        placed = atlas;
                        break;
                    }
                }

                if (placed == null)
                {
                    // remaining area in this queue (incl. self) / 队列剩余总面积（含自身）
                    long remaining = units
                        .Where(u => TypeGroupKey(u) == key && !u.placedInAtlas)
                        .Sum(u => u.islands.Sum(i => (long)footprints[i].x * footprints[i].y));
                    long floor = Mathf.Clamp(remaining, 64 * 64, (long)int.MaxValue);

                    foreach (var cand in CandidatePool.Candidates(s.settings.experimentalNpot,
                                 (int)floor, s.platform))
                    {
                        int padding = PaddingFor(Mathf.Max(cand.x, cand.y), s.settings.minPadding);
                        var layout = new AtlasLayout
                        {
                            pageW = cand.x, pageH = cand.y, padding = padding,
                            typeGroupKey = key, srgb = pc.srgb,
                            used = new BitMask(cand.x / CellSize, cand.y / CellSize),
                        };
                        var trial = TryPlaceAll(layout, pc, footprints, maskCache, padding);
                        if (trial != null)
                        {
                            layout.used = trial.used;
                            layout.placements.AddRange(trial.placements);
                            queue.Add(layout);
                            s.atlases.Add(layout);
                            placed = layout;
                            break;
                        }

                        if (cand.x >= CandidatePool.MaxEdge(s.platform) &&
                            cand.y >= CandidatePool.MaxEdge(s.platform))
                            break; // max candidate reached / 已到最大候选
                    }
                }

                if (placed != null)
                {
                    pc.placedInAtlas = true;
                    foreach (var t in pc.textures) placed.textures.Add(t);
                }
                else
                {
                    // give up atlas for the whole component (spec) / 放弃整分量图集化
                    pc.fallbackNoAtlas = true;
                    pc.fallbackReason = "oversize";
                    foreach (var t in pc.textures)
                    {
                        s.texInfos[t].forceNoAtlas = true;
                        s.warnings.Add(string.Format(ATOL10n.Get("warn.oversize"), t.name));
                    }
                }
            }

            foreach (var atlas in s.atlases) ComputeSecondaryScales(s, atlas, footprints);

            ATOLog.Info($"packing: {s.atlases.Count} atlases over {units.Count} components, " +
                        $"{s.components.Count(c => c.fallbackNoAtlas)} fallback");
        }

        // ------------------------------------------------------------------
        internal static int PaddingFor(int maxEdge, int minPadding) =>
            Mathf.Max(minPadding, Mathf.CeilToInt(maxEdge / 128f));

        internal static string TypeGroupKey(PackingComponent pc) =>
            $"{(pc.srgb ? "s" : "l")}|{(int)pc.filterMode}|{(pc.hasNormal ? "N" : "-")}{(pc.hasMask ? "M" : "-")}";

        /// <summary>Island mask at footprint size with symmetric padding ring.
        /// 岛掩码：足印尺寸 + 对称 padding 环。</summary>
        internal static BitMask IslandMask(UvIsland isl, Vector2Int footprint, int padding,
            Dictionary<(UvIsland, int), BitMask> cache)
        {
            if (cache != null && cache.TryGetValue((isl, padding), out var hit)) return hit;

            int dilate = Mathf.Max(1, Mathf.CeilToInt(padding * 0.5f / CellSize));
            int gw = Mathf.Max(1, (footprint.x + CellSize - 1) / CellSize + 2 * dilate);
            int gh = Mathf.Max(1, (footprint.y + CellSize - 1) / CellSize + 2 * dilate);

            var uvs = new List<float>();
            foreach (var g in isl.groups)
            {
                var uvArr = new List<Vector2>();
                g.ri.mesh.GetUVs(g.channel, uvArr);
                if (uvArr.Count == 0) continue;
                var tris = g.ri.mesh.triangles;
                foreach (var t in g.triangles)
                    for (int k = 0; k < 3; k++)
                    {
                        var p = uvArr[tris[t * 3 + k]];
                        float lx = (p.x - isl.uvBounds.xMin) / Mathf.Max(1e-9f, isl.uvBounds.width);
                        float ly = (p.y - isl.uvBounds.yMin) / Mathf.Max(1e-9f, isl.uvBounds.height);
                        uvs.Add(lx);
                        uvs.Add(ly);
                    }
            }

            var rows = new Unity.Collections.NativeArray<ulong>(
                gh * BitMask.WordsPerRow(gw), Unity.Collections.Allocator.TempJob);
            var triArr = new Unity.Collections.NativeArray<float>(
                uvs.ToArray(), Unity.Collections.Allocator.TempJob);
            var job = new RasterizeJob
            {
                triUvs = triArr,
                triCount = uvs.Count / 6,
                gw = gw, gh = gh, dilateCells = dilate,
                rows = rows,
            };
            job.Schedule().Complete();
            triArr.Dispose();

            var mask = new BitMask(gw, gh);
            rows.CopyTo(mask.Rows);
            rows.Dispose();
            if (cache != null) cache[(isl, padding)] = mask;
            return mask;
        }

        private class TrialResult
        {
            internal BitMask used;
            internal List<Placement> placements;
        }

        /// <summary>Atomic all-islands placement trial on a cloned mask.
        /// 在克隆掩码上对全岛做原子试放。</summary>
        private static TrialResult TryPlaceAll(AtlasLayout atlas, PackingComponent pc,
            Dictionary<UvIsland, Vector2Int> footprints,
            Dictionary<(UvIsland, int), BitMask> maskCache, int padding)
        {
            var used = CloneMask(atlas.used);
            var added = new List<Placement>();

            foreach (var isl in pc.islands.OrderByDescending(i => (long)footprints[i].x * footprints[i].y))
            {
                var fp = footprints[isl];
                var mask = IslandMask(isl, fp, padding, maskCache);
                var spot = FindPlacement(used, atlas, mask, fp);
                if (spot == null) return null; // trial mask discarded / 弃置试放掩码

                Place(used, mask, spot.cellX, spot.cellY);
                added.Add(new Placement { island = isl, rect = spot.rect, rotated = spot.rotated });
            }

            return new TrialResult { used = used, placements = added };
        }

        private class Spot
        {
            internal RectInt rect;
            internal int cellX, cellY;
            internal bool rotated;
        }

        private static Spot FindPlacement(BitMask used, AtlasLayout atlas, BitMask mask, Vector2Int fp)
        {
            var a = TryOrientation(used, atlas, mask, fp, false);
            var b = TryOrientation(used, atlas, mask.Transposed(),
                new Vector2Int(fp.y, fp.x), true, ringOf: a?.ring ?? 0);
            if (a == null && b == null) return null;
            if (a == null) return b;
            if (b == null) return a;
            return a.rect.yMin <= b.rect.yMin ? a : b; // lower first (BLF) / 更低优先
        }

        private static Spot TryOrientation(BitMask used, AtlasLayout atlas, BitMask mask,
            Vector2Int fp, bool rotated, int ringOf = 0)
        {
            int gw = used.Gw, gh = used.Gh;
            int mw = mask.Gw, mh = mask.Gh;
            if (mw > gw || mh > gh) return null;

            int ring = Mathf.Max(1, Mathf.CeilToInt(atlas.padding * 0.5f / CellSize));
            int wprUsed = BitMask.WordsPerRow(gw);
            int wprMask = BitMask.WordsPerRow(mw);

            for (int py = 0; py <= gh - mh; py++)
                for (int px = 0; px <= gw - mw; px++)
                {
                    if (Overlap(used.Rows, wprUsed, gw, mask.Rows, wprMask, mw, mh, px, py)) continue;
                    int visX = (px + ring) * CellSize, visY = (py + ring) * CellSize;
                    int visW = rotated ? fp.y : fp.x;
                    int visH = rotated ? fp.x : fp.y;
                    return new Spot
                    {
                        rect = new RectInt(visX, visY, visW, visH),
                        cellX = px, cellY = py, rotated = rotated,
                    };
                }

            return null;
        }

        /// <summary>mask AND used-shifted == 0 test. / 平移后按位与测试。</summary>
        internal static bool Overlap(ulong[] used, int wprUsed, int gw, ulong[] mask, int wprMask,
            int mw, int mh, int px, int py)
        {
            int wordOff = px >> 6, bitOff = px & 63;
            for (int y = 0; y < mh; y++)
            {
                int rowBase = (py + y) * wprUsed + wordOff;
                for (int w = 0; w < wprMask; w++)
                {
                    ulong m = mask[y * wprMask + w];
                    if (m == 0) continue;
                    ulong u = used[rowBase + w] >> bitOff;
                    if (bitOff > 0 && rowBase + w + 1 < used.Length)
                        u |= used[rowBase + w + 1] << (64 - bitOff);
                    if ((u & m) != 0) return true;
                }
            }
            return false;
        }

        private static void Place(BitMask used, BitMask mask, int px, int py)
        {
            int wprUsed = BitMask.WordsPerRow(used.Gw);
            int wprMask = BitMask.WordsPerRow(mask.Gw);
            int wordOff = px >> 6, bitOff = px & 63;
            for (int y = 0; y < mask.Gh; y++)
                for (int w = 0; w < wprMask; w++)
                {
                    ulong m = mask[y * wprMask + w];
                    if (m == 0) continue;
                    used.Rows[(py + y) * wprUsed + wordOff + w] |= m << bitOff;
                    if (bitOff > 0 && (py + y) * wprUsed + wordOff + w + 1 < used.Rows.Length)
                        used.Rows[(py + y) * wprUsed + wordOff + w + 1] |= m >> (64 - bitOff);
                }
        }

        private static BitMask CloneMask(BitMask src)
        {
            var dst = new BitMask(src.Gw, src.Gh);
            Array.Copy(src.Rows, dst.Rows, src.Rows.Length);
            return dst;
        }

        // ------------------------------------------------------------------
        private static void ComputeSecondaryScales(AtoSession s, AtlasLayout atlas,
            Dictionary<UvIsland, Vector2Int> footprints)
        {
            float colorReq = 0f, normalReq = 0f, maskReq = 0f;
            foreach (var p in atlas.placements)
            {
                var fp = footprints[p.island];
                foreach (var t in p.island.textures)
                {
                    if (!s.texInfos.TryGetValue(t, out var ti)) continue;
                    var orig = new Vector2Int(
                        Mathf.Max(1, Mathf.RoundToInt(p.island.uvBounds.width * t.width)),
                        Mathf.Max(1, Mathf.RoundToInt(p.island.uvBounds.height * t.height)));
                    float req = Mathf.Max(fp.x / Mathf.Max(1f, orig.x), fp.y / Mathf.Max(1f, orig.y));
                    switch (ti.category)
                    {
                        case AtoTexCategory.Normal: normalReq = Mathf.Max(normalReq, req); break;
                        case AtoTexCategory.Gray: maskReq = Mathf.Max(maskReq, req); break;
                        default: colorReq = Mathf.Max(colorReq, req); break;
                    }
                }
            }

            atlas.normalPageScale = SecondaryScale(normalReq / Mathf.Max(colorReq, 1e-4f), atlas.padding);
            atlas.maskPageScale = SecondaryScale(maskReq / Mathf.Max(colorReq, 1e-4f), atlas.padding);
            atlas.normalW = Mathf.Max(64, Mathf.RoundToInt(atlas.pageW * atlas.normalPageScale));
            atlas.normalH = Mathf.Max(64, Mathf.RoundToInt(atlas.pageH * atlas.normalPageScale));
            atlas.maskW = Mathf.Max(64, Mathf.RoundToInt(atlas.pageW * atlas.maskPageScale));
            atlas.maskH = Mathf.Max(64, Mathf.RoundToInt(atlas.pageH * atlas.maskPageScale));
        }

        private static float SecondaryScale(float ratio, int padding)
        {
            if (ratio >= 0.75f) return 1f;
            float scale = ratio >= 0.375f ? 0.5f : ratio >= 0.1875f ? 0.25f : 0.125f;
            if (padding * scale < 4f) scale = Mathf.Min(1f, 4f / padding);
            return scale;
        }
    }
}
