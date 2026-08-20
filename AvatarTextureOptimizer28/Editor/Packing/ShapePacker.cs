using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace net.fosa.ato.editor
{
    /// <summary>
    /// EN: One atomic packing unit: a whole UV group. Every island of the group must land in the same
    ///     atlas, which is what guarantees that all textures sharing that UV stay consistent.
    /// ZH: 一个原子装箱单位：一整个 UV 组。组内所有岛必须落在同一张图集里，
    ///     这正是保证共享该 UV 的所有贴图保持一致的前提。
    /// </summary>
    public sealed class PackUnit
    {
        /// <summary>EN: The group being packed. ZH: 被装箱的组。</summary>
        public UVGroup Group;
        /// <summary>EN: Padded masks, one per island, in the same order as <see cref="UVGroup.Islands"/>.
        /// ZH: 已加 padding 的掩码，每个岛一个，顺序与 UVGroup.Islands 一致。</summary>
        public RasterMask[] Masks;
        /// <summary>EN: Total covered cells, used for the descending area sort. ZH: 覆盖单元总数，用于面积降序排序。</summary>
        public int Coverage;
        /// <summary>EN: Longest island side in cells, used as the secondary sort key. ZH: 最长岛边（单元数），作为次级排序键。</summary>
        public int LongestSide;
    }

    /// <summary>EN: One produced atlas layout. ZH: 产出的一个图集布局。</summary>
    public sealed class AtlasLayout
    {
        /// <summary>EN: Chosen atlas size. ZH: 选定的图集尺寸。</summary>
        public AtlasCandidate Size;
        /// <summary>EN: Padding used, in pixels. ZH: 使用的 padding，单位像素。</summary>
        public int Padding;
        /// <summary>EN: Groups packed into this atlas. ZH: 装入该图集的组。</summary>
        public readonly List<UVGroup> Groups = new List<UVGroup>();
        /// <summary>EN: Occupied cells over total cells. ZH: 已占用单元数 / 总单元数。</summary>
        public float Utilisation;
    }

    /// <summary>
    /// EN: Shape-aware bin packer.
    ///
    ///     Placement strategy, in the order the specification requires:
    ///       * Units are ordered by rasterised coverage descending, then by longest side descending.
    ///       * For each queue we compute the total covered area, discard every candidate atlas smaller
    ///         than that, and then walk the remaining candidates from smallest area / most square
    ///         upwards, taking the first one that holds the entire queue.
    ///       * Inside an atlas each island is placed by a full-scan bottom-left-fill over the 4 px cell
    ///         grid, testing the real coverage mask rather than a bounding rectangle, and optionally the
    ///         90 degree rotation obtained by transposing the mask.
    ///       * A group is atomic: if any of its islands fails to place, the whole group is rolled back.
    ///
    /// ZH: 形状感知装箱器。
    ///
    ///     放置策略，顺序完全遵循需求：
    ///       * 单位按光栅化覆盖面积降序排序，再按最长边降序。
    ///       * 对每个队列计算覆盖总面积，丢弃所有小于该面积的候选图集，
    ///         然后从"面积最小 / 最接近正方形"开始向上遍历剩余候选，取第一个能装下整个队列的。
    ///       * 图集内部对每个岛在 4 像素单元网格上做全扫描 BLF 放置，
    ///         测试的是真实覆盖掩码而非包围矩形，并可选地测试通过转置得到的 90 度旋转。
    ///       * 组是原子的：只要其中任一岛放置失败，整组回滚。
    /// </summary>
    public sealed class ShapePacker
    {
        private readonly ATOLog _log;
        private readonly ATOProgress _progress;
        private readonly bool _allowRotation;
        private readonly int _paddingPx;
        private readonly int _paddingCells;

        /// <summary>EN: Construct with the fixed padding the masks were dilated by. ZH: 用掩码膨胀时所用的固定 padding 构造。</summary>
        public ShapePacker(ATOLog log, ATOProgress progress, bool allowRotation, int paddingPx)
        {
            _log = log;
            _progress = progress;
            _allowRotation = allowRotation;
            _paddingPx = paddingPx;
            _paddingCells = Mathf.CeilToInt(paddingPx / (float)ATOConstants.RasterGranularity);
        }

        /// <summary>
        /// EN: Pack a texture type group's units into as many atlases as needed. Units that cannot fit
        ///     the largest candidate even alone are marked <see cref="UVGroup.SkipAtlas"/> and reported.
        /// ZH: 把一个贴图类型组的单位装进所需数量的图集。
        ///     即使单独也装不进最大候选图集的单位会被标记 SkipAtlas 并上报。
        /// </summary>
        public List<AtlasLayout> Pack(List<PackUnit> units, List<AtlasCandidate> pool)
        {
            var layouts = new List<AtlasLayout>();
            if (units.Count == 0 || pool.Count == 0) return layouts;

            var largest = pool[pool.Count - 1];

            // EN: Reject units that can never fit, before they poison the queue logic.
            // ZH: 在污染队列逻辑之前，先剔除永远装不下的单位。
            var queue = new List<PackUnit>();
            foreach (var u in units)
            {
                if (FitsAlone(u, largest)) { queue.Add(u); continue; }
                u.Group.SkipAtlas = true;
                u.Group.SkipReason = $"does not fit the largest candidate atlas ({largest}) even alone";
                _log.Warn(ATOLocalizer.Tr("ato.warn.tooLargeForAtlas",
                    u.Group.Textures.Values.SelectMany(v => v).FirstOrDefault()?.Source?.name ?? u.Group.ToString()));
            }

            // EN: Coverage descending, then longest side descending.
            // ZH: 覆盖面积降序，再按最长边降序。
            queue.Sort((a, b) =>
            {
                int c = b.Coverage.CompareTo(a.Coverage);
                return c != 0 ? c : b.LongestSide.CompareTo(a.LongestSide);
            });

            while (queue.Count > 0)
            {
                _progress.ThrowIfCancelled();

                long needed = queue.Sum(u => (long)u.Coverage);
                AtlasLayout produced = null;

                foreach (var cand in pool)
                {
                    long cells = cand.Area / (ATOConstants.RasterGranularity * ATOConstants.RasterGranularity);
                    if (cells < needed) continue;

                    var attempt = TryPackAll(queue, cand, out var placedAll);
                    if (attempt != null && placedAll) { produced = attempt; break; }
                }

                if (produced != null)
                {
                    layouts.Add(produced);
                    _log.Detail($"Atlas {layouts.Count}: {produced.Size} padding={produced.Padding}px " +
                                $"groups={produced.Groups.Count} utilisation={produced.Utilisation * 100f:F1}%");
                    break;
                }

                // EN: Nothing holds the whole queue. Fill the largest atlas greedily and start a new
                //     queue with whatever is left, exactly as the specification describes.
                // ZH: 没有候选能装下整个队列。按需求所述，用最大图集贪心填充，剩下的另开一个队列。
                var partial = TryPackAll(queue, largest, out _);
                if (partial == null || partial.Groups.Count == 0)
                {
                    foreach (var u in queue)
                    {
                        u.Group.SkipAtlas = true;
                        u.Group.SkipReason = "packer made no progress";
                    }
                    break;
                }

                layouts.Add(partial);
                _log.Detail($"Atlas {layouts.Count}: {partial.Size} padding={partial.Padding}px " +
                            $"groups={partial.Groups.Count} utilisation={partial.Utilisation * 100f:F1}% (queue split)");

                var placed = new HashSet<UVGroup>(partial.Groups);
                queue = queue.Where(u => !placed.Contains(u.Group)).ToList();
            }

            return layouts;
        }

        private bool FitsAlone(PackUnit u, AtlasCandidate cand)
        {
            int cx = cand.Width / ATOConstants.RasterGranularity;
            int cy = cand.Height / ATOConstants.RasterGranularity;
            foreach (var m in u.Masks)
            {
                bool ok = (m.CellsX <= cx && m.CellsY <= cy) ||
                          (_allowRotation && m.CellsY <= cx && m.CellsX <= cy);
                if (!ok) return false;
            }
            return true;
        }

        private AtlasLayout TryPackAll(List<PackUnit> units, AtlasCandidate cand, out bool placedAll)
        {
            int cx = cand.Width / ATOConstants.RasterGranularity;
            int cy = cand.Height / ATOConstants.RasterGranularity;
            var occupancy = new RasterMask(cx, cy);
            var layout = new AtlasLayout { Size = cand, Padding = _paddingPx };
            placedAll = true;

            foreach (var unit in units)
            {
                _progress.ThrowIfCancelled();
                var snapshot = (ulong[])occupancy.Bits.Clone();
                int snapshotCoverage = occupancy.Coverage;
                bool ok = true;

                for (int i = 0; i < unit.Masks.Length; i++)
                {
                    if (!Place(occupancy, unit.Masks[i], unit.Group.Islands[i])) { ok = false; break; }
                }

                if (ok)
                {
                    layout.Groups.Add(unit.Group);
                }
                else
                {
                    occupancy.Bits = snapshot;
                    occupancy.Coverage = snapshotCoverage;
                    foreach (var isl in unit.Group.Islands) isl.AtlasIndex = -1;
                    placedAll = false;
                }
            }

            layout.Utilisation = occupancy.Coverage / (float)(cx * cy);
            return layout.Groups.Count > 0 ? layout : null;
        }

        /// <summary>
        /// EN: Full-scan bottom-left-fill. We scan rows from the bottom and columns from the left, taking
        ///     the first position where the padded mask does not collide. This is O(cells) per island in
        ///     the worst case but the word-level collision test keeps the constant tiny, and unlike a
        ///     skyline heuristic it never leaves unreachable holes.
        /// ZH: 全扫描 BLF。从下往上扫行、从左往右扫列，取第一个加了 padding 的掩码不发生碰撞的位置。
        ///     最坏情况下每个岛是 O(单元数)，但字级碰撞测试让常数非常小；
        ///     而且与 skyline 启发式不同，它绝不会留下不可达的空洞。
        /// </summary>
        private bool Place(RasterMask occ, RasterMask mask, UVIsland island)
        {
            var orientations = _allowRotation
                ? new[] { (mask, false), (mask.Transposed(), true) }
                : new[] { (mask, false) };

            int bestY = int.MaxValue, bestX = int.MaxValue;
            RasterMask bestMask = null;
            bool bestRot = false;

            foreach (var (m, rotated) in orientations)
            {
                if (m.CellsX > occ.CellsX || m.CellsY > occ.CellsY) continue;
                for (int y = 0; y + m.CellsY <= occ.CellsY; y++)
                {
                    if (y > bestY) break;
                    for (int x = 0; x + m.CellsX <= occ.CellsX; x++)
                    {
                        if (y == bestY && x >= bestX) break;
                        if (!Collides(occ, m, x, y))
                        {
                            bestY = y; bestX = x; bestMask = m; bestRot = rotated;
                            break;
                        }
                    }
                }
            }

            if (bestMask == null) return false;

            Stamp(occ, bestMask, bestX, bestY);

            // EN: The mask we placed was pre-dilated by the padding, so the real island starts one
            //     padding ring inside it and keeps exactly its solved pixel size.
            // ZH: 我们放置的掩码已按 padding 预膨胀，因此真实的岛从内缩一圈 padding 处开始，
            //     并精确保持它求解出的像素尺寸。
            int g = ATOConstants.RasterGranularity;
            island.PackedRotated = bestRot;
            island.AtlasIndex = 0;                 // EN: filled in by the caller. ZH: 由调用方回填。
            int w = bestRot ? island.ScaledSize.y : island.ScaledSize.x;
            int h = bestRot ? island.ScaledSize.x : island.ScaledSize.y;
            island.PackedRect = new RectInt(
                bestX * g + _paddingCells * g,
                bestY * g + _paddingCells * g,
                Mathf.Max(1, w),
                Mathf.Max(1, h));
            return true;
        }

        private static bool Collides(RasterMask occ, RasterMask m, int ox, int oy)
        {
            int shift = ox & 63;
            int wordOffset = ox >> 6;
            int mw = m.WordsPerRow;
            int ow = occ.WordsPerRow;

            for (int y = 0; y < m.CellsY; y++)
            {
                int mrow = y * mw;
                int orow = (oy + y) * ow;
                for (int w = 0; w < mw; w++)
                {
                    ulong bits = m.Bits[mrow + w];
                    if (bits == 0) continue;

                    int oi = orow + wordOffset + w;
                    if (oi < orow + ow && (occ.Bits[oi] & (bits << shift)) != 0) return true;
                    if (shift != 0)
                    {
                        int oi2 = oi + 1;
                        if (oi2 < orow + ow && (occ.Bits[oi2] & (bits >> (64 - shift))) != 0) return true;
                    }
                }
            }
            return false;
        }

        private static void Stamp(RasterMask occ, RasterMask m, int ox, int oy)
        {
            for (int y = 0; y < m.CellsY; y++)
            for (int x = 0; x < m.CellsX; x++)
                if (m.Get(x, y)) occ.Set(ox + x, oy + y);
        }
    }
}
