// Avatar Texture Optimizer / 头像贴图优化器
// Atlas planning: type groups -> unit queues -> candidate pool filtering ->
// BLF (bottom-left-fill) full-scan bitmask packing with 90-degree rotation
// steps, atomically per UV group.
// 图集规划：贴图类型组 -> 贴图队列 -> 候选池过滤 -> 全扫描 BLF 位掩码装箱
// （90 度步进旋转，以 UV 组为原子单位）。
//
// Rotation safety note (team consensus): rotating an island by exactly 90deg
// together with its UVs preserves the point-sample mapping per texel, so
// normal-map tangent data does NOT need recomputation; rotation is allowed for
// all roles with an exact pixel permutation.
// 旋转安全性说明（团队共识）：岛与其 UV 同步旋转 90 度时逐纹素采样映射保持
// 不变，法线切线数据无需重算；配合精确像素重排，所有角色均可旋转。

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace FOSA.AvatarTextureOptimizer.Editor
{
    /// <summary>A unit to place: one UV group (all its islands, all its roles move together). / 装箱单元：一个 UV 组（全部岛、全部角色一起移动）。</summary>
    public sealed class ATOPackUnit
    {
        public ATOUVGroup group;
        public string typeGroupKey;
        public readonly List<(ATOIsland island, int w, int h, long areaUv, bool rotatable)> islandSizes = new List<(ATOIsland, int, int, long, bool)>();
        public long totalUvArea;      // sum of islands' scaled UV area (ratio-applied) / 缩放后 UV 面积合计
        public int id;
    }

    /// <summary>Placement of one island in one atlas. / 单个岛在一张图集中的摆放。</summary>
    public struct ATOPlacedIsland
    {
        public ATOPackUnit unit;
        public ATOIsland island;
        public int x, y, w, h;
        public bool rotated90;
    }

    /// <summary>Plan of one atlas (placements for one type group layer set). / 单张图集规划（某贴图类型组一层的摆放结果）。</summary>
    public sealed class ATOAtlasPlan
    {
        public int width, height;
        public string typeGroupKey;
        public readonly List<ATOPlacedIsland> islands = new List<ATOPlacedIsland>();
        public readonly HashSet<ATOTextureEntry> sourceTextures = new HashSet<ATOTextureEntry>();
        public long coveredUvArea;
        public float utilization;
        public int setIndex; // which atlas within the type group / 在类型组内的序号
    }

    /// <summary>
    /// Plans atlases for all UV groups.
    /// 为全部 UV 组规划图集。
    /// </summary>
    public sealed class ATOAtlasPlanner
    {
        private readonly AvatarTextureOptimizer _settings;
        private readonly ATOPlatform _platform;
        private readonly int _userMinPadding;
        private readonly Dictionary<(ATOIsland, int, int, int), ATOBitMask> _rasterCache = new Dictionary<(ATOIsland, int, int, int), ATOBitMask>();

        public ATOAtlasPlanner(AvatarTextureOptimizer settings, ATOPlatform platform)
        {
            _settings = settings;
            _platform = platform;
            _userMinPadding = (int)settings.minAtlasPadding;
        }

        private int MaxAtlasSize()
        {
            var ov = PlatformOf(_settings, _platform);
            if (ov != null && ov.enabled && ov.maxAtlasSize > 0)
            {
                int cap = _platform == ATOPlatform.PC ? ATOConsts.MaxAtlasPC : ATOConsts.MaxAtlasMobile;
                return Mathf.Clamp(ov.maxAtlasSize, ATOConsts.MinAtlasEdge, cap);
            }
            return _platform == ATOPlatform.PC ? ATOConsts.MaxAtlasPC : ATOConsts.MaxAtlasMobile;
        }

        private static ATOPlatformOverride PlatformOf(AvatarTextureOptimizer s, ATOPlatform p) => s.OverrideFor(p);

        /// <summary>Build candidate atlas sizes for the current platform/options. / 按平台与选项生成候选图集尺寸池。</summary>
        public List<(int w, int h)> BuildCandidatePool()
        {
            int max = MaxAtlasSize();
            int min = ATOConsts.MinAtlasEdge;
            var sizes = new List<(int, int)>();
            if (!_settings.experimentalNPOT)
            {
                var pot = new List<int>();
                for (int s = min; s <= max; s *= 2) pot.Add(s);
                foreach (var w in pot)
                foreach (var h in pot)
                    sizes.Add((w, h));
            }
            else
            {
                // Experimental NPOT: 64px steps with limited aspect variants.
                // 实验性 NPOT：64px 步进 + 受限宽高比变体。
                for (int s = min; s <= max; s += 64)
                {
                    sizes.Add((s, s));
                    foreach (var (aw, ah) in new[] { (2, 1), (1, 2), (4, 1), (1, 4) })
                    {
                        int w = s * aw, h = s * ah;
                        if (w <= max && h <= max) sizes.Add((w, h));
                    }
                }
            }
            // Sort: area asc, then long/short ratio asc (square first). / 排序：面积升序，再长宽比升序（近方优先）。
            sizes.Sort((a, b) =>
            {
                long aa = (long)a.Item1 * a.Item2, bb = (long)b.Item1 * b.Item2;
                if (aa != bb) return aa.CompareTo(bb);
                float ra = (float)Mathf.Max(a.Item1, a.Item2) / Mathf.Min(a.Item1, a.Item2);
                float rb = (float)Mathf.Max(b.Item1, b.Item2) / Mathf.Min(b.Item1, b.Item2);
                return ra.CompareTo(rb);
            });
            return sizes;
        }

        /// <summary>
        /// Plan all atlases. Quality ratios are per-group per-island vectors.
        /// 规划全部图集。质量比例为每组每岛向量。
        /// </summary>
        public List<ATOAtlasPlan> Plan(
            List<ATOUVGroup> groups,
            Dictionary<ATOUVGroup, Dictionary<ATOIsland, Vector2>> ratios,
            Func<ATOUVGroup, bool> groupAllowedInAtlas,
            out List<ATOUVGroup> fallbackGroups)
        {
            // 1) build units grouped by type-group key / 按贴图类型组键分组构建单元
            var byTypeGroup = new Dictionary<string, List<ATOPackUnit>>();
            int uid = 0;
            foreach (var g in groups)
            {
                if (!groupAllowedInAtlas(g)) continue;
                if (g.islands.Count == 0) continue;
                if (!g.OptimizableTextures().Any()) continue;

                var key = g.TypeGroupSignature();
                if (!byTypeGroup.TryGetValue(key, out var list))
                {
                    list = new List<ATOPackUnit>();
                    byTypeGroup[key] = list;
                }
                var unit = new ATOPackUnit { group = g, typeGroupKey = key, id = uid++ };
                var rmap = ratios != null && ratios.TryGetValue(g, out var r) ? r : null;
                foreach (var isl in g.islands)
                {
                    var ratio = rmap != null && rmap.TryGetValue(isl, out var rv) ? rv : Vector2.one;
                    // pixel size relative to a 1-normalized atlas (multiply by candidate side later)
                    // 相对 1 归一化图集的像素尺寸（稍后乘以候选边长）
                    float uw = Mathf.Max(1e-6f, (isl.uvMax.x - isl.uvMin.x) * ratio.x);
                    float uh = Mathf.Max(1e-6f, (isl.uvMax.y - isl.uvMin.y) * ratio.y);
                    long areaUv = (long)(uw * uh * 1e12); // fixed-scale area metric / 定标面积量
                    unit.islandSizes.Add((isl, FasterInt(uw), FasterInt(uh), areaUv, true));
                    unit.totalUvArea += areaUv;
                }
                list.Add(unit);
            }

            var plans = new List<ATOAtlasPlan>();
            fallbackGroups = new List<ATOUVGroup>();
            var pool = BuildCandidatePool();
            if (pool.Count == 0)
            {
                fallbackGroups.AddRange(groups);
                return plans;
            }
            var maxCandidate = pool[pool.Count - 1];

            foreach (var kv in byTypeGroup)
            {
                // Sort the queue: rasterized area desc (approx by uv area) / 队列排序：光栅化面积降序（以 UV 面积近似）
                var queue = kv.Value.OrderByDescending(u => u.totalUvArea).ToList();
                PlanQueue(queue, pool, maxCandidate, kv.Key, plans, fallbackGroups);
            }
            return plans;
        }

        private static int FasterInt(float v) => Mathf.CeilToInt(v * 4096f);

        private void PlanQueue(
            List<ATOPackUnit> queue, List<(int w, int h)> pool, (int w, int h) maxCandidate,
            string typeGroupKey, List<ATOAtlasPlan> plans, List<ATOUVGroup> fallbackGroups)
        {
            int setIndex = 0;
            var remaining = new List<ATOPackUnit>(queue);
            while (remaining.Count > 0)
            {
                // total area needed for the remaining units (at max candidate scale) / 剩余单元总面积（以最大候选计）
                var candidates = pool;
                ATOAtlasPlan plan = null;
                List<ATOPackUnit> leftover = null;

                foreach (var cand in candidates)
                {
                    if (!CandidateBigEnough(cand, remaining)) continue;
                    var outcome = TryPack(remaining, cand.w, cand.h, out leftover);
                    if (outcome != null)
                    {
                        plan = outcome;
                        plan.width = cand.w;
                        plan.height = cand.h;
                        break;
                    }
                }

                if (plan == null)
                {
                    // Even the largest atlas cannot hold the first unit alone?
                    // 最大图集也放不下队列中最大单元本身？
                    var biggest = remaining.OrderByDescending(u => u.totalUvArea).First();
                    var alone = new List<ATOPackUnit> { biggest };
                    if (TryPack(alone, maxCandidate.w, maxCandidate.h, out _) == null)
                    {
                        fallbackGroups.Add(biggest.group);
                        remaining.Remove(biggest);
                        ATOLog.Warn(ATOLoc.T("ato:atlas.dropgroup", biggest.group.mesh?.name, biggest.group.submesh));
                        continue;
                    }
                    // Otherwise pack what fits into the largest atlas.
                    // 否则往最大图集装到装不下为止。
                    plan = PackGreedyInto(remaining, maxCandidate.w, maxCandidate.h, out leftover);
                    plan.width = maxCandidate.w;
                    plan.height = maxCandidate.h;
                }
                else if (leftover != null)
                {
                    // queue continues with the smaller leftover / 队列剩余继续
                }

                plan.typeGroupKey = typeGroupKey;
                plan.setIndex = setIndex++;
                plans.Add(plan);
                foreach (var isl in plan.islands)
                {
                    foreach (var tex in isl.unit.group.OptimizableTextures())
                        plan.sourceTextures.Add(tex);
                }

                if (leftover == null || leftover.Count == 0) break;
                remaining = leftover;
            }
        }

        private bool CandidateBigEnough((int w, int h) cand, List<ATOPackUnit> units)
        {
            // Approximate area model (uv-area fraction * atlas px). / 近似面积模型（UV 面积比例 * 图集像素）。
            double need = 0;
            foreach (var u in units)
            {
                foreach (var isl in u.islandSizes)
                {
                    double wpx = isl.w / 4096.0 * cand.w;
                    double hpx = isl.h / 4096.0 * cand.h;
                    need += wpx * hpx * 0.7; // raster coverage ~ 70% of bbox average-ish / 近似
                }
            }
            return need <= (double)cand.w * cand.h * 0.98;
        }

        /// <summary>Pack as many units as possible into one atlas, return plan + leftovers. / 往单张图集尽可能装，返回规划+剩余。</summary>
        private ATOAtlasPlan PackGreedyInto(List<ATOPackUnit> units, int w, int h, out List<ATOPackUnit> leftover)
        {
            var plan = new ATOAtlasPlan();
            var occupancy = new ATOBitMask(w / ATOConsts.RasterGranularity, h / ATOConsts.RasterGranularity);
            leftover = new List<ATOPackUnit>();
            foreach (var unit in units)
            {
                if (TryPackUnit(unit, w, h, occupancy, plan))
                {
                    // placed / 已放入
                }
                else
                {
                    leftover.Add(unit);
                }
            }
            FinalizePlan(plan, w, h);
            return plan.islands.Count > 0 ? plan : null;
        }

        /// <summary>Attempt to pack ALL units into one atlas; null on failure. / 尝试将全部单元装入一张图集；失败返回 null。</summary>
        private ATOAtlasPlan TryPack(List<ATOPackUnit> units, int w, int h, out List<ATOPackUnit> leftover)
        {
            leftover = null;
            var plan = new ATOAtlasPlan();
            var occupancy = new ATOBitMask(w / ATOConsts.RasterGranularity, h / ATOConsts.RasterGranularity);
            foreach (var unit in units)
            {
                if (!TryPackUnit(unit, w, h, occupancy, plan))
                {
                    leftover = new List<ATOPackUnit>(units);
                    return null;
                }
            }
            FinalizePlan(plan, w, h);
            return plan;
        }

        private void FinalizePlan(ATOAtlasPlan plan, int w, int h)
        {
            plan.width = w;
            plan.height = h;
            long used = 0;
            foreach (var isl in plan.islands) used += (long)isl.w * isl.h;
            plan.utilization = w > 0 && h > 0 ? Mathf.Clamp01((float)((double)plan.coveredUvArea / ((double)w * h))) : 0f;
        }

        private int PaddingFor(int atlasMaxSide)
        {
            int derived = Mathf.CeilToInt(atlasMaxSide / 128f);
            return Mathf.Max(derived, _userMinPadding);
        }

        /// <summary>Try placing every island of a unit (atomic) with rotation alternatives. / 尝试原子化放入单元的全部岛（含旋转变体）。</summary>
        private bool TryPackUnit(ATOPackUnit unit, int w, int h, ATOBitMask occupancy, ATOAtlasPlan plan)
        {
            int pad = PaddingFor(Mathf.Max(w, h));
            int padExtend = Mathf.Max(1, (pad + 1) / 2);

            var placements = new List<ATOPlacedIsland>(unit.islandSizes.Count);
            // island rects in atlas pixels / 图集像素岛矩形
            var rects = new List<(ATOIsland isl, int w, int h)>(unit.islandSizes.Count);
            foreach (var entry in unit.islandSizes)
            {
                // FasterInt encodes uv spans at 1/4096 fixed point. / FasterInt 以 1/4096 定点编码 UV 跨度。
                int wpx = Mathf.Max(1, Mathf.CeilToInt(entry.w / 4096f * w));
                int hpx = Mathf.Max(1, Mathf.CeilToInt(entry.h / 4096f * h));
                rects.Add((entry.island, wpx, hpx));
            }
            // order: raster area desc, then longer side desc / 光栅面积降序 + 长边降序
            var order = rects
                .OrderByDescending(r => (long)r.w * r.h)
                .ThenByDescending(r => Mathf.Max(r.w, r.h))
                .ToList();

            var tryMasks = new List<(ATOBitMask mask, bool rotated, int contentOffset)>();
            var placedRects = new List<(ATOIsland isl, int x, int y, int w, int h, bool rot)>();

            foreach (var r in order)
            {
                var mask = RasterMask(r.isl, r.w, r.h, padExtend, out int contentOff);
                tryMasks.Clear();
                tryMasks.Add((mask, false, contentOff));
                var rot = mask.Rotate90CW();
                // Rotating the dilated mask moves the content offset corner.
                // 旋转膨胀掩码会移动内容偏移角。
                tryMasks.Add((rot, true, contentOff));

                bool placed = false;
                foreach (var (m, rotated, off) in tryMasks)
                {
                    if (PlaceBLF(occupancy, m, out int px, out int py))
                    {
                        int contentX = (px + off) * ATOConsts.RasterGranularity;
                        int contentY = (py + off) * ATOConsts.RasterGranularity;
                        placedRects.Add((r.isl, contentX, contentY, Mathf.Min(r.w, w), Mathf.Min(r.h, h), rotated));
                        Stamp(occupancy, m, px, py);
                        placements.Add(new ATOPlacedIsland
                        {
                            unit = unit,
                            island = r.isl,
                            x = contentX,
                            y = contentY,
                            w = rotated ? r.h : r.w,
                            h = rotated ? r.w : r.h,
                            rotated90 = rotated,
                        });
                        placed = true;
                        break;
                    }
                }
                if (!placed) return false;
            }

            foreach (var pr in placedRects)
            {
                plan.coveredUvArea += (long)pr.w * pr.h;
            }
            plan.islands.AddRange(placements);
            return true;
        }

        /// <summary>
        /// Bottom-left-first scan placement with word-accelerated tests.
        /// 自底向左的全扫描摆放（字级加速测试）。
        /// </summary>
        private static bool PlaceBLF(ATOBitMask occupancy, ATOBitMask item, out int outX, out int outY)
        {
            int W = occupancy.width, H = occupancy.height;
            int w = item.width, h = item.height;
            outX = -1;
            outY = -1;
            if (w > W || h > H) return false;
            for (int y = 0; y <= H - h; y++)
            {
                for (int x = 0; x <= W - w; x++)
                {
                    if (Fits(occupancy, item, x, y))
                    {
                        outX = x;
                        outY = y;
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// Bit-parallel overlap test: each item row (64 cells per word) is ANDed
        /// against the shifted occupancy row. ~64x faster than per-cell probing,
        /// which matters for 2048x2048-cell BLF scans.
        /// 位并行重叠测试：逐项行（每 ulong 64 格）与移位后的占用行按位与。
        /// 比逐格探测快约 64 倍——2048x2048 格的 BLF 全扫描靠它才不超时。
        /// </summary>
        private static bool Fits(ATOBitMask occ, ATOBitMask item, int ox, int oy)
        {
            int occWords = (occ.width + 63) >> 6;
            int itemWords = (item.width + 63) >> 6;
            int shift = ox & 63;
            int wordOff = ox >> 6;
            var ob = occ.bits;
            var ib = item.bits;
            for (int iy = 0; iy < item.height; iy++)
            {
                int oBase = (oy + iy) * occWords + wordOff;
                int iBase = iy * itemWords;
                if (shift == 0)
                {
                    for (int iw = 0; iw < itemWords; iw++)
                    {
                        if ((ob[oBase + iw] & ib[iBase + iw]) != 0UL) return false;
                    }
                }
                else
                {
                    for (int iw = 0; iw < itemWords; iw++)
                    {
                        ulong a = ob[oBase + iw] >> shift;
                        int hi = oBase + iw + 1;
                        // guard the row end: beyond-row bits are 0 (row width padded)
                        if (hi % occWords != 0 && hi < ob.Length)
                            a |= ob[hi] << (64 - shift);
                        if ((a & ib[iBase + iw]) != 0UL) return false;
                    }
                }
            }
            return true;
        }

        private static void Stamp(ATOBitMask occ, ATOBitMask item, int ox, int oy)
        {
            for (int iy = 0; iy < item.height; iy++)
            for (int ix = 0; ix < item.width; ix++)
                if (item.Get(ix, iy)) occ.Set(ox + ix, oy + iy, true);
        }

        /// <summary>
        /// Raster (and cache) an island at target pixel size translated into
        /// cell-grid granularity, already dilated for padding. The content origin
        /// inside the dilated mask is written to <paramref name="contentOffsetCells"/>.
        /// 光栅化（并缓存）目标像素尺寸的岛（含 padding 膨胀），转成单元格粒度。
        /// 内容原点在膨胀掩码内的偏移写入 <paramref name="contentOffsetCells"/>。
        /// </summary>
        private ATOBitMask RasterMask(ATOIsland isl, int wpx, int hpx, int dilatePx, out int contentOffsetCells)
        {
            int cw = Mathf.Max(1, Mathf.CeilToInt((float)wpx / ATOConsts.RasterGranularity));
            int ch = Mathf.Max(1, Mathf.CeilToInt((float)hpx / ATOConsts.RasterGranularity));
            int dil = Mathf.Max(0, Mathf.RoundToInt((float)dilatePx / ATOConsts.RasterGranularity));
            contentOffsetCells = dil;
            var key = (isl, wpx, hpx, dil);
            if (_rasterCache.TryGetValue(key, out var cached)) return cached;
            if (_rasterCache.Count > 2048) _rasterCache.Clear(); // memory valve / 内存阀门

            // normalized island-space raster / 归一化岛空间光栅
            int up = 8; // supersample factor per cell for coverage accuracy / 每格超采样保证覆盖精度
            var mask = new ATOBitMask(cw * up, ch * up);
            float minX = isl.uvMin.x, minY = isl.uvMin.y;
            float spanX = Mathf.Max(1e-8f, isl.uvMax.x - isl.uvMin.x);
            float spanY = Mathf.Max(1e-8f, isl.uvMax.y - isl.uvMin.y);
            for (int t = 0; t < isl.localTriangles.Length; t += 3)
            {
                Vector2 a = Norm(isl.bakedUVs[isl.localTriangles[t]], minX, minY, spanX, spanY);
                Vector2 b = Norm(isl.bakedUVs[isl.localTriangles[t + 1]], minX, minY, spanX, spanY);
                Vector2 c = Norm(isl.bakedUVs[isl.localTriangles[t + 2]], minX, minY, spanX, spanY);
                ATORaster.RasterTriangle(a, b, c, cw * up, ch * up, (x, y) => mask.Set(x, y, true));
            }
            // downsample to cell grid (any covered supersample covers the cell)
            // 降采样到单元格（任一超采样覆盖即覆盖该格）
            var cell = new ATOBitMask(cw, ch);
            for (int y = 0; y < ch; y++)
            for (int x = 0; x < cw; x++)
            {
                bool any = false;
                for (int gy = 0; gy < up && !any; gy++)
                for (int gx = 0; gx < up && !any; gx++)
                    if (mask.Get(x * up + gx, y * up + gy)) any = true;
                if (any) cell.Set(x, y, true);
            }

            // padding dilation in cells / 单元级 padding 膨胀
            for (int i = 0; i < dil; i++) cell = Dilate(cell);

            _rasterCache[key] = cell;
            return cell;
        }

        private static Vector2 Norm(Vector2 uv, float minX, float minY, float spanX, float spanY)
        {
            return new Vector2((uv.x - minX) / spanX, (uv.y - minY) / spanY);
        }

        private static ATOBitMask Dilate(ATOBitMask src)
        {
            var dst = new ATOBitMask(src.width, src.height);
            for (int y = 0; y < src.height; y++)
            for (int x = 0; x < src.width; x++)
            {
                if (!src.Get(x, y)) continue;
                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                    dst.Set(x + dx, y + dy, true);
            }
            return dst;
        }
    }
}
