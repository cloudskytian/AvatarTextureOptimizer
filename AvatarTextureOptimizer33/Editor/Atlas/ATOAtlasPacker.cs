// SPDX-License-Identifier: MIT
// EN: Shape aware atlas packer: 4 px raster bit masks, height map guided bottom-left-fill with a full
//     bitmask collision test, area/edge descending order, optional 90 degree rotation and a candidate pool.
// ZH: 基于形状的图集装箱器：4px 光栅位掩码、由高度图引导的 BLF 全扫描 + 位掩码碰撞检测、
//     面积/边长降序、可选 90 度旋转，以及候选图集池。

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// EN: One atomic packing unit: a whole UV group (a texture plus everything sharing its UVs).
    /// ZH: 一个原子装箱单元：整个 UV 组（一张贴图以及与其共享 UV 的一切）。
    /// </summary>
    public sealed class ATOPackUnit
    {
        public ATOUVGroup Group;
        public readonly List<ATOPackItem> Items = new List<ATOPackItem>();
        public long RasterArea;

        public override string ToString() => $"unit({Group}) items={Items.Count} area={RasterArea}";
    }

    /// <summary>
    /// EN: One island with its rasterised mask, in both orientations.
    /// ZH: 一个岛及其两个朝向的光栅掩码。
    /// </summary>
    public sealed class ATOPackItem
    {
        public ATOIsland Island;
        public ATORasterMask Mask;
        public ATORasterMask MaskRotated;
        public long Area => Mask.PixelArea;
        public int LongEdge => Mathf.Max(Mask.CellWidth, Mask.CellHeight);
    }

    /// <summary>
    /// EN: A candidate atlas size.
    /// ZH: 一个候选图集尺寸。
    /// </summary>
    public readonly struct ATOCandidate
    {
        public readonly int Width;
        public readonly int Height;

        public ATOCandidate(int w, int h)
        {
            Width = w;
            Height = h;
        }

        public long Area => (long)Width * Height;
        public float Ratio => Mathf.Max(Width, Height) / (float)Mathf.Min(Width, Height);
        public override string ToString() => $"{Width}x{Height}";
    }

    /// <summary>
    /// EN: Result of packing one queue.
    /// ZH: 单个队列的装箱结果。
    /// </summary>
    public sealed class ATOPackResult
    {
        public ATOCandidate Size;
        public readonly List<ATOPackUnit> Units = new List<ATOPackUnit>();

        /// <summary>EN: Placements are only applied once the result is accepted. ZH: 只有结果被采纳后才会应用落点。</summary>
        public readonly List<(ATOIsland island, ATOPlacement placement)> Placements =
            new List<(ATOIsland, ATOPlacement)>();

        public long UsedPixels;

        /// <summary>EN: Writes the placements into the islands. ZH: 把落点写回岛。</summary>
        public void Apply()
        {
            foreach (var (island, placement) in Placements) island.Placement = placement;
        }
        public double Utilisation => Size.Area == 0 ? 0 : (double)UsedPixels / Size.Area;
    }

    /// <summary>
    /// EN: The packer.
    /// ZH: 装箱器。
    /// </summary>
    public sealed class ATOAtlasPacker
    {
        private readonly ATOLog _log;
        private readonly bool _allowRotation;
        private readonly bool _allowNPOT;
        private readonly int _maxSize;

        /// <summary>EN: Upper bound on full pack attempts per atlas. ZH: 每张图集的完整装箱尝试次数上限。</summary>
        private const int MaxCandidateAttempts = 8;

        public ATOAtlasPacker(ATOLog log, bool allowRotation, bool allowNPOT, int maxSize)
        {
            _log = log;
            _allowRotation = allowRotation;
            _allowNPOT = allowNPOT;
            _maxSize = Mathf.Clamp(maxSize, 64, 8192);
        }

        /// <summary>
        /// EN: Builds the candidate pool: powers of two, or 64 px steps when NPOT is enabled.
        ///     Non square candidates are allowed; the closer to square, the earlier it is tried.
        /// ZH: 构建候选图集池：2 的幂，或启用 NPOT 时按 64px 步进。允许非正方形，越接近正方形越优先。
        /// </summary>
        public List<ATOCandidate> BuildCandidatePool()
        {
            var sizes = new List<int>();
            if (_allowNPOT)
            {
                for (var s = 64; s <= _maxSize; s += 64) sizes.Add(s);
            }
            else
            {
                for (var s = 64; s <= _maxSize; s *= 2) sizes.Add(s);
            }

            var pool = new List<ATOCandidate>();
            foreach (var w in sizes)
            foreach (var h in sizes)
            {
                var ratio = Mathf.Max(w, h) / (float)Mathf.Min(w, h);
                if (ratio > 4f) continue; // EN: extreme shapes waste memory. ZH: 过于极端的形状浪费显存。
                pool.Add(new ATOCandidate(w, h));
            }

            pool.Sort((a, b) =>
            {
                var c = a.Area.CompareTo(b.Area);
                if (c != 0) return c;
                return a.Ratio.CompareTo(b.Ratio);
            });

            _log.Trace("pack", $"candidate pool: {pool.Count} entries, max {_maxSize}, npot={_allowNPOT}");
            return pool;
        }

        /// <summary>
        /// EN: Packs one queue of units into as many atlases as needed.
        /// ZH: 把一个队列的单元装进所需数量的图集。
        /// </summary>
        public List<ATOPackResult> PackQueue(List<ATOPackUnit> units, List<ATOCandidate> pool,
            Func<ATOCandidate, int> paddingForCandidate, Action<ATOPackUnit> onUnpackable)
        {
            var results = new List<ATOPackResult>();
            var remaining = new List<ATOPackUnit>(units);

            // EN: Rasterised area descending, then by longest edge. ZH: 光栅面积降序，其次按最长边。
            remaining.Sort((a, b) =>
            {
                var c = b.RasterArea.CompareTo(a.RasterArea);
                if (c != 0) return c;
                return LongestEdge(b).CompareTo(LongestEdge(a));
            });

            var guard = 0;
            while (remaining.Count > 0)
            {
                if (guard++ > 4096)
                {
                    _log.Error("pack", "packing did not converge, aborting this queue");
                    break;
                }

                var totalArea = 0L;
                foreach (var u in remaining) totalArea += u.RasterArea;

                ATOPackResult best = null;
                var attempts = 0;

                foreach (var candidate in pool)
                {
                    if (candidate.Area < totalArea) continue;
                    if (attempts++ >= MaxCandidateAttempts) break;

                    var attempt = TryPack(remaining, candidate, paddingForCandidate(candidate), true);
                    if (attempt == null) continue;

                    best = attempt;
                    break;
                }

                if (best == null)
                {
                    // EN: Nothing fits everything: fill the largest candidate greedily.
                    // ZH: 没有候选能装下全部：用最大候选做贪心填充。
                    var largest = new ATOCandidate(_maxSize, _maxSize);
                    best = TryPack(remaining, largest, paddingForCandidate(largest), false);

                    if (best == null || best.Units.Count == 0)
                    {
                        // EN: Even a single unit does not fit -> give up on this unit.
                        // ZH: 连单个单元都装不下 -> 放弃该单元。
                        var victim = remaining[0];
                        remaining.RemoveAt(0);
                        onUnpackable?.Invoke(victim);
                        _log.Warning("pack", $"{victim} does not fit into {_maxSize}x{_maxSize}, atlasing skipped");
                        continue;
                    }
                }

                best.Apply();
                results.Add(best);
                foreach (var u in best.Units) remaining.Remove(u);

                _log.Info("pack",
                    $"atlas {results.Count}: {best.Size} units={best.Units.Count} utilisation={best.Utilisation:P1}");
            }

            return results;
        }

        private static int LongestEdge(ATOPackUnit unit)
        {
            var e = 0;
            foreach (var item in unit.Items) e = Mathf.Max(e, item.LongEdge);
            return e;
        }

        /// <summary>
        /// EN: Attempts to pack the units into one atlas. When <paramref name="requireAll"/> is true the
        ///     attempt fails unless every unit fits.
        /// ZH: 尝试把单元装入一张图集。<paramref name="requireAll"/> 为 true 时必须全部装下才算成功。
        /// </summary>
        private ATOPackResult TryPack(List<ATOPackUnit> units, ATOCandidate size, int paddingPixels, bool requireAll)
        {
            var cellsW = size.Width / ATORasterMask.CellSize;
            var cellsH = size.Height / ATORasterMask.CellSize;
            if (cellsW <= 0 || cellsH <= 0) return null;

            var grid = new Grid(cellsW, cellsH);
            var result = new ATOPackResult { Size = size };
            var paddingCells = Mathf.Max(1, Mathf.CeilToInt(paddingPixels / (float)ATORasterMask.CellSize));

            foreach (var unit in units)
            {
                var snapshot = grid.Snapshot();
                var placed = new List<(ATOIsland island, ATOPlacement placement)>();
                var ok = true;

                // EN: Inside a unit, islands are placed area descending then longest edge descending.
                // ZH: 单元内部，岛按面积降序、其次按最长边降序放置。
                var items = new List<ATOPackItem>(unit.Items);
                items.Sort((a, b) =>
                {
                    var c = b.Area.CompareTo(a.Area);
                    if (c != 0) return c;
                    return b.LongEdge.CompareTo(a.LongEdge);
                });

                foreach (var item in items)
                {
                    if (!TryPlace(grid, item, paddingCells, out var placement))
                    {
                        ok = false;
                        break;
                    }

                    placed.Add((item.Island, placement));
                }

                if (!ok)
                {
                    grid.Restore(snapshot);
                    if (requireAll) return null;
                    continue;
                }

                foreach (var (island, placement) in placed)
                {
                    result.Placements.Add((island, placement));
                    result.UsedPixels += (long)placement.Width * placement.Height;
                }

                result.Units.Add(unit);
            }

            if (requireAll && result.Units.Count != units.Count) return null;
            return result.Units.Count == 0 ? null : result;
        }

        private bool TryPlace(Grid grid, ATOPackItem item, int paddingCells, out ATOPlacement placement)
        {
            placement = default;

            var masks = new List<(ATORasterMask mask, bool rotated)> { (item.Mask, false) };
            if (_allowRotation && item.MaskRotated != null) masks.Add((item.MaskRotated, true));

            var bestX = -1;
            var bestY = int.MaxValue;
            ATORasterMask bestMask = null;
            var bestRotated = false;

            foreach (var (mask, rotated) in masks)
            {
                var padded = mask.Dilate(paddingCells);
                if (padded.CellWidth > grid.Width || padded.CellHeight > grid.Height) continue;

                if (!grid.FindBottomLeft(padded, out var x, out var y)) continue;

                if (y < bestY || (y == bestY && x < bestX) || bestX < 0)
                {
                    bestX = x;
                    bestY = y;
                    bestMask = padded;
                    bestRotated = rotated;
                }
            }

            if (bestMask == null) return false;

            grid.Occupy(bestMask, bestX, bestY);

            var cell = ATORasterMask.CellSize;
            placement = new ATOPlacement
            {
                X = (bestX + paddingCells) * cell,
                Y = (bestY + paddingCells) * cell,
                Width = (bestMask.CellWidth - paddingCells * 2) * cell,
                Height = (bestMask.CellHeight - paddingCells * 2) * cell,
                Rotated = bestRotated,
                Valid = true,
            };
            return true;
        }

        /// <summary>
        /// EN: Occupancy grid with a per column height map used to skip impossible positions.
        /// ZH: 带有逐列高度图的占用网格，用于跳过不可能的位置。
        /// </summary>
        private sealed class Grid
        {
            public readonly int Width;
            public readonly int Height;
            private readonly int _wordsPerRow;
            private readonly ulong[] _bits;
            private readonly int[] _columnHeight;

            public Grid(int width, int height)
            {
                Width = width;
                Height = height;
                _wordsPerRow = (width + 63) / 64;
                _bits = new ulong[_wordsPerRow * height];
                _columnHeight = new int[width];
            }

            public (ulong[] bits, int[] heights) Snapshot() => ((ulong[])_bits.Clone(), (int[])_columnHeight.Clone());

            public void Restore((ulong[] bits, int[] heights) snapshot)
            {
                Array.Copy(snapshot.bits, _bits, _bits.Length);
                Array.Copy(snapshot.heights, _columnHeight, _columnHeight.Length);
            }

            /// <summary>
            /// EN: Full scan bottom-left-fill: for every x the height map gives the lowest plausible y,
            ///     then an exact bit mask test confirms (and walks upwards on overhangs).
            /// ZH: BLF 全扫描：对每个 x 由高度图给出最低可能的 y，再用精确位掩码检测确认（遇到悬空则上移）。
            /// </summary>
            public bool FindBottomLeft(ATORasterMask mask, out int outX, out int outY)
            {
                outX = 0;
                outY = 0;

                var columns = Width - mask.CellWidth + 1;
                if (columns <= 0) return false;

                // EN: The scan is read only on the grid, so every column can be probed in parallel.
                // ZH: 扫描只读取网格，因此每一列都可以并行探测。
                var bestYPerColumn = new int[columns];

                Parallel.For(0, columns, x =>
                {
                    var y = 0;
                    for (var c = x; c < x + mask.CellWidth; c++) y = Math.Max(y, _columnHeight[c] - mask.CellHeight);
                    if (y < 0) y = 0;

                    while (y + mask.CellHeight <= Height)
                    {
                        if (!Collides(mask, x, y))
                        {
                            bestYPerColumn[x] = y;
                            return;
                        }

                        y++;
                    }

                    bestYPerColumn[x] = int.MaxValue;
                });

                var bestX = -1;
                var bestY = int.MaxValue;
                for (var x = 0; x < columns; x++)
                {
                    if (bestYPerColumn[x] >= bestY) continue;
                    bestY = bestYPerColumn[x];
                    bestX = x;
                    if (bestY == 0) break; // EN: cannot do better. ZH: 不可能更好了。
                }

                if (bestX < 0 || bestY == int.MaxValue) return false;
                outX = bestX;
                outY = bestY;
                return true;
            }

            private bool Collides(ATORasterMask mask, int x, int y)
            {
                for (var r = 0; r < mask.CellHeight; r++)
                {
                    var gridRow = (y + r) * _wordsPerRow;
                    var maskRow = r * mask.WordsPerRow;

                    for (var w = 0; w < mask.WordsPerRow; w++)
                    {
                        var word = mask.Bits[maskRow + w];
                        if (word == 0) continue;

                        // EN: Shift the mask word into the grid's word alignment. ZH: 把掩码字对齐到网格字。
                        var bitOffset = x + w * 64;
                        var wordIndex = bitOffset >> 6;
                        var shift = bitOffset & 63;

                        if (wordIndex >= _wordsPerRow) return true;
                        if ((_bits[gridRow + wordIndex] & (word << shift)) != 0) return true;

                        if (shift != 0 && wordIndex + 1 < _wordsPerRow)
                        {
                            if ((_bits[gridRow + wordIndex + 1] & (word >> (64 - shift))) != 0) return true;
                        }
                        else if (shift != 0)
                        {
                            // EN: Bits would fall outside the atlas. ZH: 位会越出图集边界。
                            if ((word >> (64 - shift)) != 0) return true;
                        }
                    }
                }

                return false;
            }

            public void Occupy(ATORasterMask mask, int x, int y)
            {
                for (var r = 0; r < mask.CellHeight; r++)
                {
                    var gridRow = (y + r) * _wordsPerRow;
                    var maskRow = r * mask.WordsPerRow;

                    for (var w = 0; w < mask.WordsPerRow; w++)
                    {
                        var word = mask.Bits[maskRow + w];
                        if (word == 0) continue;

                        var bitOffset = x + w * 64;
                        var wordIndex = bitOffset >> 6;
                        var shift = bitOffset & 63;

                        if (wordIndex < _wordsPerRow) _bits[gridRow + wordIndex] |= word << shift;
                        if (shift != 0 && wordIndex + 1 < _wordsPerRow)
                            _bits[gridRow + wordIndex + 1] |= word >> (64 - shift);
                    }
                }

                for (var c = x; c < x + mask.CellWidth && c < Width; c++)
                    _columnHeight[c] = Math.Max(_columnHeight[c], y + mask.CellHeight);
            }
        }
    }
}
