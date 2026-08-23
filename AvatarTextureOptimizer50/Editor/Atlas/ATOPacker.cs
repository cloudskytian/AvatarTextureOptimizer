// -----------------------------------------------------------------------------
// ATOPacker.cs — bitmask raster BLF packing with candidate atlas pool.
// ATOPacker.cs —— 位掩码光栅 BLF 装箱与候选图集池。
//
// Per spec: rasterized island masks (4px cells), full-scan bottom-left-fill with
// 90° rotation (bitmask transpose), candidate pool filtered by total raster area,
// ordered by (area asc, squareness), atomic unit = texture + all its UV groups.
// 按规格：岛光栅位掩码（4px 格）、全扫描 BLF、90°旋转（位掩码转置）、按光栅总面积
// 过滤候选池并按（面积升序、接近正方形优先）排序、原子单位=贴图×其全部UV组。
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace net.fosa.ato.editor
{
    internal static class ATOPacker
    {
        /// <summary>Pack all units of ONE type group into one or more atlases.
        /// 将一个类型组的全部装箱单元装入一个或多个图集。</summary>
        public static List<AtlasResult> PackTypeGroup(List<PackUnit> units, TypeGroupKey key,
            ATOSettings s, ATOBuildState st)
        {
            var results = new List<AtlasResult>();

            // 0. rasters (cached on islands) + queue order / 光栅缓存与队列排序
            foreach (var u in units)
            foreach (var isl in u.islands)
                if (isl.raster == null)
                    isl.raster = ATOIslands.Rasterize(isl, isl.scaledSize.x, isl.scaledSize.y);

            var queue = units
                .Select(u => (u, area: u.islands.Sum(i => i.raster.PopCount())))
                .OrderByDescending(x => x.area)
                .ThenByDescending(x => x.u.islands.Max(i => Mathf.Max(i.scaledSize.x, i.scaledSize.y)))
                .Select(x => x.u)
                .ToList();

            PackQueue(queue, s, st, results);
            foreach (var r in results) r.typeKey = key;
            return results;
        }

        private static void PackQueue(List<PackUnit> queue, ATOSettings s, ATOBuildState st,
            List<AtlasResult> results)
        {
            while (queue.Count > 0)
            {
                long totalCells = queue.Sum(u => u.islands.Sum(i => i.raster.PopCount()));
                int maxEdge = s.maxAtlasSize;

                // find smallest unit that can never fit → drop to whole-scale fallback
                // 找出永远放不下的单元 → 整图缩放回退
                var oversized = queue.Where(u => u.islands.Any(i =>
                        Mathf.CeilToInt(i.scaledSize.x / 4f) + 2 > maxEdge / 4 ||
                        Mathf.CeilToInt(i.scaledSize.y / 4f) + 2 > maxEdge / 4))
                    .ToList();
                foreach (var big in oversized)
                {
                    st.report.AddWarning(
                        $"Texture '{big.baseTex.source.name}' island exceeds max atlas {maxEdge}px " +
                        "→ whole-texture fallback / 单岛超过最大图集→整图缩放回退");
                    queue.Remove(big);
                    big.baseTex.atlasified = false; // caller sends it to whole-scale / 由调用方转整图缩放
                }

                if (queue.Count == 0) break;

                AtlasResult atlas = null;
                foreach (var cand in Candidates(s, totalCells, maxEdge))
                {
                    var trial = TryPlace(queue, cand.w, cand.h, s, results.Count);
                    if (trial.fullFit)
                    {
                        atlas = trial.atlas;
                        break;
                    }
                }

                if (atlas == null)
                {
                    // No candidate fits everything → fill the largest, overflow to next queue
                    // 无候选可全装 → 用最大图集填充，溢出到下一队列
                    var (partial, leftover) = TryPlacePartial(queue, maxEdge, maxEdge, s, results.Count);
                    atlas = partial;
                    queue = leftover; // next iteration packs the rest / 下一轮装剩余
                }
                else
                {
                    queue = new List<PackUnit>();
                }

                if (atlas != null && atlas.islands.Count > 0) results.Add(atlas);
                else if (atlas == null) break; // safety / 安全阀
            }
        }

        // ================================================================= //
        // Candidate pool / 候选图集池
        // ================================================================= //

        private static IEnumerable<(int w, int h)> Candidates(ATOSettings s, long totalCellArea, int maxEdge)
        {
            var list = new List<(int w, int h)>();
            long totalPxArea = totalCellArea * IslandRaster.Cell * IslandRaster.Cell;
            long totalPx = totalPxArea;

            if (s.npotAtlases)
            {
                for (int w = 64; w <= maxEdge; w += 64)
                for (int h = 64; h <= maxEdge; h += 64)
                    if ((long)w * h >= totalPx) list.Add((w, h));
            }
            else
            {
                for (int w = 64; w <= maxEdge; w <<= 1)
                for (int h = 64; h <= maxEdge; h <<= 1)
                    if ((long)w * h >= totalPx) list.Add((w, h));
            }

            // area asc, then squareness (long/short asc) / 面积升序，其后长宽比升序
            return list.OrderBy(c => (long)c.w * c.h)
                .ThenBy(c => c.w >= c.h ? (float)c.w / c.h : (float)c.h / c.w);
        }

        // ================================================================= //
        // Placement / 放置
        // ================================================================= //

        private sealed class Trial
        {
            public AtlasResult atlas;
            public bool fullFit;
        }

        private static Trial TryPlace(List<PackUnit> queue, int w, int h, ATOSettings s, int atlasId)
        {
            var t = Place(queue, w, h, s, atlasId, out var placed, out var failed);
            return new Trial { atlas = t, fullFit = failed.Count == 0 && placed.Count == queue.Count };
        }

        private static (AtlasResult, List<PackUnit>) TryPlacePartial(List<PackUnit> queue,
            int w, int h, ATOSettings s, int atlasId)
        {
            var t = Place(queue, w, h, s, atlasId, out var placed, out var failed);
            return (t, failed);
        }

        /// <summary>Greedy bottom-left placement into a w×h atlas. Units that fail remain
        /// in `failed` (smaller units continue to be tried, per spec).
        /// 贪心 BLF 放置进 w×h 图集。放不下的单元留在 failed（按规格继续尝试更小的）。</summary>
        private static AtlasResult Place(List<PackUnit> queue, int w, int h, ATOSettings s,
            int atlasId, out List<PackUnit> placed, out List<PackUnit> failed)
        {
            placed = new List<PackUnit>();
            failed = new List<PackUnit>();

            int padding = Mathf.Max(4, Mathf.CeilToInt(Mathf.Max(w, h) / 128f), s.minPadding);
            int padCells = Mathf.Max(1, Mathf.RoundToInt(padding / (2f * IslandRaster.Cell)));

            int gw = w / IslandRaster.Cell, gh = h / IslandRaster.Cell;
            var free = new ulong[gh]; // occupancy bitmap / 占用位图

            var atlas = new AtlasResult
            {
                id = atlasId,
                width = w,
                height = h,
                padding = padding,
            };

            foreach (var unit in queue)
            {
                var placements = new List<(IslandInfo isl, RectInt rect, bool rot)>();
                bool ok = true;

                foreach (var isl in unit.islands.OrderByDescending(i => i.raster.PopCount()))
                {
                    var inflated = ATOIslands.DilateN(isl.raster, padCells);
                    var inflatedT = inflated.Transposed();
                    if (!TryBlf(free, gw, gh, inflated, inflatedT, out var rect, out var rot))
                    {
                        ok = false;
                        break;
                    }

                    placements.Add((isl, rect, rot));
                }

                if (!ok)
                {
                    failed.Add(unit); // continue with smaller ones / 继续尝试更小的
                    continue;
                }

                foreach (var (isl, rect, rot) in placements)
                {
                    var inflated = ATOIslands.DilateN(isl.raster, padCells);
                    var inflatedT = inflated.Transposed();
                    Stamp(free, rot ? inflatedT : inflated, rect.x, rect.y);

                    // content rect = inflated rect inset by padCells / 内容矩形=膨胀矩形内缩
                    isl.atlasId = atlas.id;
                    isl.cellRect = new RectInt(rect.x + padCells, rect.y + padCells,
                        (rot ? inflated.cellsH : inflated.cellsW) - 2 * padCells,
                        (rot ? inflated.cellsW : inflated.cellsH) - 2 * padCells);
                    isl.rotated = rot;
                    atlas.islands.Add(isl);

                    foreach (var dup in isl.mergedDuplicates)
                    {
                        dup.atlasId = atlas.id;
                        dup.cellRect = isl.cellRect;
                        dup.rotated = rot;
                    }
                }

                placed.Add(unit);
            }

            return atlas;
        }

        /// <summary>Full-scan bottom-left-fit test. / 全扫描 BLF 测试。</summary>
        private static bool TryBlf(ulong[] free, int gw, int gh, IslandRaster mask,
            IslandRaster maskT, out RectInt rect, out bool rotated)
        {
            rect = default;
            rotated = false;

            var m = mask;
            if (m.cellsW <= gw && m.cellsH <= gh)
            {
                for (int y = 0; y + m.cellsH <= gh; y++)
                for (int x = 0; x + m.cellsW <= gw; x++)
                {
                    if (Fits(free, m, x, y))
                    {
                        rect = new RectInt(x, y, m.cellsW, m.cellsH);
                        return true;
                    }
                }
            }

            var t = maskT;
            if (t.cellsW <= gw && t.cellsH <= gh)
            {
                for (int y = 0; y + t.cellsH <= gh; y++)
                for (int x = 0; x + t.cellsW <= gw; x++)
                {
                    if (Fits(free, t, x, y))
                    {
                        rect = new RectInt(x, y, t.cellsW, t.cellsH);
                        rotated = true;
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool Fits(ulong[] free, IslandRaster m, int ox, int oy)
        {
            for (int y = 0; y < m.cellsH; y++)
            {
                ulong bits = m.rows[y] << ox;
                if ((free[y + oy] & bits) != 0) return false;
            }

            return true;
        }

        private static void Stamp(ulong[] free, IslandRaster m, int ox, int oy)
        {
            for (int y = 0; y < m.cellsH; y++)
                free[y + oy] |= m.rows[y] << ox;
        }
    }
}
