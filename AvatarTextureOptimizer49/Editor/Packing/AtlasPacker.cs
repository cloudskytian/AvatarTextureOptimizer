using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>An island placed inside an atlas. / 已放入图集的岛。</summary>
    internal class PlacedIsland
    {
        internal UvGroup Group;
        internal UvIsland Island;
        internal int X, Y;          // pixel position (bottom-left) / 像素位置（左下）
        internal bool Rotated90;
        internal IslandRaster RasterUsed;
        /// <summary>Island footprint in atlas pixels. / 岛在图集中的像素尺寸。</summary>
        internal int IslandW, IslandH;
        /// <summary>Atlas dimensions in pixels. / 图集像素尺寸。</summary>
        internal Vector2Int AtlasDims;
    }

    /// <summary>One packed atlas layout. / 一张已装箱图集的布局。</summary>
    internal class AtlasLayout
    {
        internal TypeGroup TypeGroup;
        internal int Width, Height;
        internal readonly List<PlacedIsland> Placed = new List<PlacedIsland>();
        internal int OccupiedCells;

        /// <summary>Fraction of atlas area covered by (undilated) island cells. / 岛（未膨胀）面积利用率。</summary>
        internal float Utilization
        {
            get
            {
                float cells = (Width / (float)IslandRaster.Cell) * (Height / (float)IslandRaster.Cell);
                return cells > 0f ? OccupiedCells / cells : 0f;
            }
        }
    }

    /// <summary>
    /// Shape-rasterized bin packing per spec:
    /// - queue sorted by rasterized area desc / 队列按光栅面积降序
    /// - atomic unit = one UV group (all textures share the layout) / 原子 = UV组
    /// - candidate atlas pool (POT 64..max by default; experimental NPOT in 64px steps);
    ///   candidates smaller than the queue's total raster area are dropped; sorted by area asc
    /// then squareness asc / 候选图集池，面积升序+近正方形优先
    /// - full-scan bottom-left-fill with 90° rotation (bitmask transpose) / 全扫描 BLF + 90°旋转
    /// - first candidate that fits the whole queue wins; overflow opens a new queue; an atom that
    ///   cannot fit the largest atlas aborts that group's atlasing / 首个能装下的候选即成品，溢出另开队列
    /// NOT rectangle packing — island shapes are rasterized bitmasks. / 形状光栅化装箱，非矩形装箱。
    /// </summary>
    internal class AtlasPacker
    {
        private const int MinSide = 64;

        internal class Result
        {
            internal readonly List<AtlasLayout> Atlases = new List<AtlasLayout>();
            /// <summary>Groups that could not be atlased (too large). / 无法图集化的组。</summary>
            internal readonly List<(UvGroup, string)> Failed = new List<(UvGroup, string)>();
        }

        private readonly Dictionary<(UvGroup, UvIsland), (int w, int h)> _sizes =
            new Dictionary<(UvGroup, UvIsland), (int w, int h)>();
        private readonly Dictionary<((UvGroup, UvIsland), int), IslandRaster> _dilated =
            new Dictionary<((UvGroup, UvIsland), int), IslandRaster>();
        private readonly Dictionary<(UvGroup, UvIsland), int> _undilatedCells =
            new Dictionary<(UvGroup, UvIsland), int>();

        /// <summary>Register an island's layout size (from quality scaling). / 注册岛的布局尺寸（来自质量缩放）。</summary>
        internal void SetFinalSize(UvGroup group, UvIsland island, int w, int h) =>
            _sizes[(group, island)] = (w, h);

        private (int, int) SizeOf(UvGroup g, UvIsland i) =>
            _sizes.TryGetValue((g, i), out var s) && s.w > 0 && s.h > 0 ? s : (4, 4);

        private IslandRaster Dilated(UvGroup g, UvIsland i, int dilateCells)
        {
            var key = ((g, i), dilateCells);
            if (_dilated.TryGetValue(key, out var r)) return r;
            var size = SizeOf(g, i);
            r = IslandRaster.Rasterize(i, g, size.Item1, size.Item2, dilateCells);
            r.Rotate90(); // precompute transpose / 预计算转置
            _dilated[key] = r;
            return r;
        }

        private int UndilatedCells(UvGroup g, UvIsland i)
        {
            if (_undilatedCells.TryGetValue((g, i), out var c)) return c;
            var size = SizeOf(g, i);
            var r = IslandRaster.Rasterize(i, g, size.Item1, size.Item2, 0);
            _undilatedCells[(g, i)] = r.CellCount;
            return r.CellCount;
        }

        // ------------------------------------------------------------------ entry
        internal Result Pack(TypeGroup tg, IReadOnlyList<(UvGroup group, int rasterArea)> queue,
            bool npot, int maxSide, int minPadding, Action<long> progress)
        {
            var result = new Result();
            if (queue.Count == 0) return result;

            var remaining = new LinkedList<(UvGroup, int)>(queue);

            while (remaining.Count > 0)
            {
                progress?.Invoke(result.Atlases.Count);

                long queueArea = remaining.Sum(q => q.Item2);
                var candidates = BuildCandidatePool(queueArea, npot, maxSide);
                if (candidates.Count == 0) candidates = new List<(int w, int h)> { (maxSide, maxSide) };

                AtlasLayout layout = null;
                var leftover = new List<(UvGroup, int)>();

                foreach (var size in candidates)
                {
                    layout = TryBuildAtlas(tg, size.w, size.h, minPadding, remaining, leftover);
                    if (layout != null) break;
                }

                if (layout == null)
                    layout = TryBuildAtlas(tg, maxSide, maxSide, minPadding, remaining, leftover);

                if (layout == null)
                {
                    // single atom does not fit the largest atlas → give up this group's atlasing
                    // 单体装不进最大图集 → 放弃该组图集化（调用方降级为整图缩放并警告）
                    var atom = remaining.First.Value;
                    result.Failed.Add((atom.Item1, $"atom too large for {maxSide}px atlas / 单体超过 {maxSide}px 图集上限"));
                    remaining.RemoveFirst();
                    continue;
                }

                result.Atlases.Add(layout);

                remaining.Clear();
                foreach (var item in leftover) remaining.AddLast(item);
            }

            return result;
        }

        /// <summary>Try to fill one atlas; atoms that do not fit go to `leftover`. / 尝试装一张图集，装不下的进剩余列表。</summary>
        private AtlasLayout TryBuildAtlas(TypeGroup tg, int w, int h, int minPadding,
            LinkedList<(UvGroup, int)> atoms, List<(UvGroup, int)> leftover)
        {
            // padding: ceil(max side/128), clamped to user minimum / 间距：max(4, ceil(边/128))
            int padding = Mathf.Max(minPadding, Mathf.CeilToInt(Mathf.Max(w, h) / 128f));
            int dilateCells = Mathf.Max(1, padding / IslandRaster.Cell);

            var layout = new AtlasLayout { TypeGroup = tg, Width = w, Height = h };
            int cellsW = w / IslandRaster.Cell, cellsH = h / IslandRaster.Cell;
            int words = (cellsW + 63) / 64;
            var occupancy = new ulong[cellsH * words];

            bool any = false;
            foreach (var atom in atoms)
            {
                var group = atom.Item1;
                var placements = new List<PlacedIsland>();
                var backup = (ulong[])occupancy.Clone();
                bool atomOk = true;

                // islands: rasterized area desc, then max side desc / 岛按面积降序、边长降序
                var islands = group.islands
                    .OrderByDescending(i => UndilatedCells(group, i))
                    .ThenByDescending(i => Mathf.Max(i.uvBounds.width, i.uvBounds.height))
                    .ToList();

                foreach (var island in islands)
                {
                    var size = SizeOf(group, island);
                    var raster = Dilated(group, island, dilateCells);

                    var (px, py, rotated) = FindPosition(occupancy, cellsW, cellsH, words, raster);
                    if (px < 0)
                    {
                        atomOk = false;
                        break;
                    }

                    var used = rotated ? raster.Rotate90() : raster;
                    Stamp(occupancy, words, used, px / IslandRaster.Cell, py / IslandRaster.Cell);
                    placements.Add(new PlacedIsland
                    {
                        Group = group, Island = island, X = px, Y = py, Rotated90 = rotated,
                        RasterUsed = used, IslandW = size.Item1, IslandH = size.Item2,
                        AtlasDims = new Vector2Int(w, h),
                    });
                }

                if (atomOk && placements.Count > 0)
                {
                    layout.Placed.AddRange(placements);
                    layout.OccupiedCells += group.islands.Sum(i => UndilatedCells(group, i));
                    any = true;
                }
                else
                {
                    occupancy = backup;
                    leftover.Add(atom);
                }
            }

            return any ? layout : null;
        }

        // ------------------------------------------------------------------ BLF search
        /// <summary>Full-scan bottom-left-fill with both orientations. / 全扫描 BLF（含90°旋转）。</summary>
        private static (int, int, bool) FindPosition(ulong[] occupancy, int cellsW, int cellsH,
            int words, IslandRaster r0)
        {
            int bestX = -1, bestY = -1;
            bool bestRot = false;
            long bestScore = long.MaxValue;

            foreach (var (raster, rotated) in new[] { (r0, false), (r0.Rotate90(), true) })
            {
                if (raster.CellsW > cellsW || raster.CellsH > cellsH) continue;
                var (x, y) = Scan(occupancy, cellsW, cellsH, words, raster);
                if (x < 0) continue;
                long score = (long)y * cellsW + x;
                if (score < bestScore) { bestScore = score; bestX = x; bestY = y; bestRot = rotated; }
            }

            return bestX >= 0 ? (bestX * IslandRaster.Cell, bestY * IslandRaster.Cell, bestRot)
                              : (-1, -1, false);
        }

        private static (int, int) Scan(ulong[] occupancy, int cellsW, int cellsH, int words,
            IslandRaster r)
        {
            int rWords = (r.CellsW + 63) / 64;
            for (int y = 0; y + r.CellsH <= cellsH; y++)
            {
                for (int x = 0; x + r.CellsW <= cellsW; x++)
                {
                    if (!Overlaps(occupancy, words, r, rWords, x, y)) return (x, y);
                }
            }
            return (-1, -1);
        }

        private static bool Overlaps(ulong[] occupancy, int occWords, IslandRaster r, int rWords,
            int x, int y)
        {
            int shift = x & 63;
            int wordOffset = x >> 6;
            for (int row = 0; row < r.CellsH; row++)
            {
                int occRowBase = (y + row) * occWords + wordOffset;
                int rRowBase = row * rWords;
                for (int w = 0; w < rWords; w++)
                {
                    ulong rv = r.Bits[rRowBase + w];
                    if (rv == 0) continue;
                    ulong left = rv << shift;
                    ulong right = shift == 0 ? 0UL : rv >> (64 - shift);
                    int occIdx = occRowBase + w;
                    if (left != 0 && occIdx >= 0 && occIdx < occupancy.Length && (occupancy[occIdx] & left) != 0)
                        return true;
                    if (right != 0 && occIdx + 1 < occupancy.Length && (occupancy[occIdx + 1] & right) != 0)
                        return true;
                }
            }
            return false;
        }

        private static void Stamp(ulong[] occupancy, int words, IslandRaster r, int cx, int cy)
        {
            int rWords = (r.CellsW + 63) / 64;
            int shift = cx & 63;
            int wordOffset = cx >> 6;
            for (int row = 0; row < r.CellsH; row++)
            {
                int occRowBase = (cy + row) * words + wordOffset;
                int rRowBase = row * rWords;
                for (int w = 0; w < rWords; w++)
                {
                    ulong rv = r.Bits[rRowBase + w];
                    if (rv == 0) continue;
                    ulong left = rv << shift;
                    ulong right = shift == 0 ? 0UL : rv >> (64 - shift);
                    int occIdx = occRowBase + w;
                    if (left != 0 && occIdx >= 0 && occIdx < occupancy.Length) occupancy[occIdx] |= left;
                    if (right != 0 && occIdx + 1 >= 0 && occIdx + 1 < occupancy.Length)
                        occupancy[occIdx + 1] |= right;
                }
            }
        }

        // ------------------------------------------------------------------ candidate pool
        /// <summary>Candidates with area ≥ need, area asc then squareness asc. / 候选池排序。</summary>
        internal static List<(int w, int h)> BuildCandidatePool(long needPixelArea, bool npot, int maxSide)
        {
            var sizes = new List<int>();
            if (npot)
            {
                for (int s = MinSide; s <= maxSide; s += 64) sizes.Add(s);
            }
            else
            {
                for (int s = MinSide; s <= maxSide; s <<= 1) sizes.Add(s);
            }

            var pool = new List<(int w, int h)>();
            foreach (var w in sizes)
                foreach (var h in sizes)
                    if ((long)w * h >= needPixelArea)
                        pool.Add((w, h));

            pool.Sort((a, b) =>
            {
                long areaA = (long)a.w * a.h, areaB = (long)b.w * b.h;
                if (areaA != areaB) return areaA.CompareTo(areaB);
                float arA = (float)Mathf.Max(a.w, a.h) / Mathf.Min(a.w, a.h);
                float arB = (float)Mathf.Max(b.w, b.h) / Mathf.Min(b.w, b.h);
                return arA.CompareTo(arB);
            });

            return pool;
        }
    }
}
