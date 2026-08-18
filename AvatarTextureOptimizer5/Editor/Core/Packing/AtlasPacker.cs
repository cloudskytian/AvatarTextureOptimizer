// Copyright (c) fosa. Licensed under the MIT License.
// Shape-aware atlas packing: candidate atlas pool + full-scan bottom-left-fill over 4px
// rasterized bitmasks, with 90 degree rotation via bitmask transpose.
// 形状感知图集装箱：候选图集池 + 基于 4px 光栅位掩码的全扫描 BLF，通过位掩码转置实现 90 度旋转。

using System;
using System.Collections.Generic;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// An occupancy grid for one atlas, tracking which 4px cells are taken.
    /// 单个图集的占用网格，记录哪些 4px 单元已被占用。
    /// </summary>
    public sealed class OccupancyGrid
    {
        private readonly ulong[] _bits;
        private readonly int _wordsPerRow;

        /// <summary>Grid width in cells. / 网格宽度（单元数）。</summary>
        public int Width { get; }

        /// <summary>Grid height in cells. / 网格高度（单元数）。</summary>
        public int Height { get; }

        /// <summary>Number of occupied cells. / 已占用单元数。</summary>
        public int Occupied { get; private set; }

        /// <summary>Creates an empty grid. / 创建空网格。</summary>
        public OccupancyGrid(int widthCells, int heightCells)
        {
            Width = Mathf.Max(1, widthCells);
            Height = Mathf.Max(1, heightCells);
            _wordsPerRow = (Width + 63) / 64;
            _bits = new ulong[_wordsPerRow * Height];
        }

        /// <summary>
        /// Tests whether a mask can be placed with its origin at the given cell.
        /// 测试掩码能否以给定单元为原点放置。
        /// </summary>
        public bool CanPlace(ulong[] mask, int maskW, int maskH, int ox, int oy)
        {
            if (ox < 0 || oy < 0 || ox + maskW > Width || oy + maskH > Height) return false;

            var maskWords = (maskW + 63) / 64;
            for (var y = 0; y < maskH; y++)
            {
                var rowBase = y * maskWords;
                for (var wi = 0; wi < maskWords; wi++)
                {
                    var word = mask[rowBase + wi];
                    if (word == 0) continue;

                    // Test each set bit against the grid. Shifting the whole word would be
                    // faster but needs careful handling of the 64-bit boundary; correctness
                    // first, and the bit loop only visits cells that are actually occupied.
                    // 逐个测试置位的位。整字移位更快但需要谨慎处理 64 位边界；
                    // 正确性优先，且位循环只访问实际被占用的单元。
                    while (word != 0)
                    {
                        var bit = TrailingZeroCount(word);
                        word &= word - 1;
                        var mx = wi * 64 + bit;
                        if (mx >= maskW) continue;
                        if (GetBit(ox + mx, oy + y)) return false;
                    }
                }
            }

            return true;
        }

        /// <summary>
        /// Burst-accelerated placement search over the whole grid.
        /// 在整个网格上进行 Burst 加速的放置搜索。
        /// </summary>
        public bool TryFindPositionBurst(
            ulong[] mask, int maskW, int maskH, out int x, out int y)
        {
            return BurstPackKernel.FindPosition(
                _bits, _wordsPerRow, Width, Height, mask, maskW, maskH, out x, out y);
        }

        /// <summary>
        /// Scalar reference implementation of the placement search. Retained as the correctness
        /// oracle the Burst kernel is validated against.
        /// 放置搜索的标量参考实现。作为验证 Burst 内核正确性的基准而保留。
        /// </summary>
        public bool TryFindPositionScalar(
            ulong[] mask, int maskW, int maskH, out int x, out int y)
        {
            for (var yy = 0; yy + maskH <= Height; yy++)
            {
                for (var xx = 0; xx + maskW <= Width; xx++)
                {
                    if (!CanPlace(mask, maskW, maskH, xx, yy)) continue;
                    x = xx;
                    y = yy;
                    return true;
                }
            }

            x = 0;
            y = 0;
            return false;
        }

        /// <summary>
        /// Marks a mask as occupied at the given origin.
        /// 将掩码在给定原点处标记为已占用。
        /// </summary>
        public void Place(ulong[] mask, int maskW, int maskH, int ox, int oy)
        {
            var maskWords = (maskW + 63) / 64;
            for (var y = 0; y < maskH; y++)
            {
                var rowBase = y * maskWords;
                for (var wi = 0; wi < maskWords; wi++)
                {
                    var word = mask[rowBase + wi];
                    while (word != 0)
                    {
                        var bit = TrailingZeroCount(word);
                        word &= word - 1;
                        var mx = wi * 64 + bit;
                        if (mx >= maskW) continue;
                        SetBit(ox + mx, oy + y);
                    }
                }
            }
        }

        /// <summary>
        /// Counts trailing zero bits. Implemented with a de Bruijn sequence because
        /// System.Numerics.BitOperations is not available in Unity's netstandard2.1 profile.
        /// 统计末尾零位数。使用 de Bruijn 序列实现，因为 Unity 的 netstandard2.1 配置中
        /// 没有 System.Numerics.BitOperations。
        /// </summary>
        private static int TrailingZeroCount(ulong value)
        {
            if (value == 0) return 64;
            return DeBruijnPositions[((ulong)((long)value & -(long)value) * DeBruijnSequence) >> 58];
        }

        private const ulong DeBruijnSequence = 0x37E84A99DAE458FUL;

        private static readonly int[] DeBruijnPositions =
        {
            0, 1, 17, 2, 18, 50, 3, 57, 47, 19, 22, 51, 29, 4, 33, 58,
            15, 48, 20, 27, 25, 23, 52, 41, 54, 30, 38, 5, 43, 34, 59, 8,
            63, 16, 49, 56, 46, 21, 28, 32, 14, 26, 24, 40, 53, 37, 42, 7,
            62, 55, 45, 31, 13, 39, 36, 6, 61, 44, 12, 35, 60, 11, 10, 9,
        };

        private bool GetBit(int x, int y)
        {
            if (x < 0 || y < 0 || x >= Width || y >= Height) return true; // out of bounds = blocked
            return (_bits[y * _wordsPerRow + (x >> 6)] & (1UL << (x & 63))) != 0;
        }

        private void SetBit(int x, int y)
        {
            if (x < 0 || y < 0 || x >= Width || y >= Height) return;
            var idx = y * _wordsPerRow + (x >> 6);
            var bit = 1UL << (x & 63);
            if ((_bits[idx] & bit) == 0)
            {
                _bits[idx] |= bit;
                Occupied++;
            }
        }

        /// <summary>Fraction of cells occupied. / 单元占用比例。</summary>
        public float Utilization => (float)Occupied / (Width * (long)Height);
    }

    /// <summary>
    /// One island queued for packing, together with its owning group.
    /// 一个待装箱的岛及其所属组。
    /// </summary>
    public sealed class PackItem
    {
        /// <summary>The island being placed. / 被放置的岛。</summary>
        public UVIsland Island;

        /// <summary>The UV group that must stay together. / 必须保持在一起的 UV 组。</summary>
        public UVGroup Group;
    }

    /// <summary>
    /// Packs islands into atlases using a candidate pool and shape-aware placement.
    /// 使用候选池与形状感知放置将岛装入图集。
    /// </summary>
    public sealed class AtlasPacker
    {
        private readonly ATOLogger _log;

        /// <summary>Creates a packer. / 创建装箱器。</summary>
        public AtlasPacker(ATOLogger log)
        {
            _log = log;
        }

        /// <summary>
        /// Builds the candidate atlas pool. Power-of-two sides are used by default; the
        /// experimental NPOT mode steps by 64, which keeps every candidate a multiple of 4 and
        /// therefore compatible with block-compressed formats and crunch.
        /// 构建候选图集池。默认使用 2 的幂边长；实验性 NPOT 模式以 64 步进，
        /// 使每个候选都是 4 的倍数，因而兼容块压缩格式与 crunch。
        /// </summary>
        public List<AtlasCandidate> BuildCandidatePool(int maxSide, bool allowNpot)
        {
            const int minSide = 64;
            var sides = new List<int>();

            if (allowNpot)
            {
                for (var s = minSide; s <= maxSide; s += 64) sides.Add(s);
            }
            else
            {
                for (var s = minSide; s <= maxSide; s *= 2) sides.Add(s);
            }

            var pool = new List<AtlasCandidate>();
            foreach (var w in sides)
            {
                foreach (var h in sides)
                {
                    // Restrict to a sane aspect ratio; extremely elongated atlases waste memory
                    // and stress the hardware's texture cache.
                    // 限制在合理的长宽比内；极端狭长的图集浪费内存且加重硬件纹理缓存压力。
                    var ratio = w >= h ? (float)w / h : (float)h / w;
                    if (ratio > 8f) continue;
                    pool.Add(new AtlasCandidate(w, h));
                }
            }

            // Area ascending, then most-square first, exactly as specified.
            // 按面积升序，其次最接近正方形优先，与需求完全一致。
            pool.Sort((a, b) =>
            {
                var c = a.Area.CompareTo(b.Area);
                if (c != 0) return c;
                return a.AspectRatio.CompareTo(b.AspectRatio);
            });

            return pool;
        }

        /// <summary>
        /// Packs one queue of groups sharing a texture-type signature into as many atlases as
        /// needed. Every island of a group is placed into the same atlas as an atomic unit, so a
        /// group is never split across atlases.
        /// 将共享贴图类型签名的一个组队列装入所需数量的图集。
        /// 组内所有岛作为原子单元放入同一图集，因此组绝不会被拆分到不同图集。
        /// </summary>
        public List<AtlasResult> PackQueue(
            List<UVGroup> groups,
            List<AtlasCandidate> pool,
            int padding,
            Func<bool> cancellationCheck)
        {
            var results = new List<AtlasResult>();
            if (groups == null || groups.Count == 0) return results;

            // Area descending: large groups first, which is the classic heuristic for
            // minimising fragmentation.
            // 面积降序：大组优先，这是最小化碎片的经典启发式。
            var remaining = new List<UVGroup>(groups);
            remaining.Sort((a, b) => TotalCells(b).CompareTo(TotalCells(a)));

            while (remaining.Count > 0)
            {
                if (cancellationCheck != null && cancellationCheck()) break;

                var required = 0L;
                foreach (var g in remaining) required += TotalCells(g);

                // Discard candidates that provably cannot hold the remaining area.
                // 丢弃明显无法容纳剩余面积的候选项。
                var viable = new List<AtlasCandidate>();
                foreach (var c in pool)
                {
                    var cells = (long)(c.Width / IslandRasterizer.CellSize) *
                                (c.Height / IslandRasterizer.CellSize);
                    if (cells >= required) viable.Add(c);
                }

                // If nothing holds everything, fall back to the largest candidate and let the
                // overflow start a new atlas on the next iteration.
                // 若没有候选能装下全部，则回退到最大候选，溢出部分在下一轮开新图集。
                if (viable.Count == 0 && pool.Count > 0)
                {
                    viable.Add(pool[pool.Count - 1]);
                }

                AtlasResult best = null;
                List<UVGroup> placedGroups = null;

                foreach (var candidate in viable)
                {
                    if (cancellationCheck != null && cancellationCheck()) break;

                    var grid = new OccupancyGrid(
                        candidate.Width / IslandRasterizer.CellSize,
                        candidate.Height / IslandRasterizer.CellSize);

                    var placed = new List<UVGroup>();
                    var allFit = true;

                    foreach (var group in remaining)
                    {
                        if (!TryPlaceGroup(grid, group, candidate))
                        {
                            allFit = false;
                            continue; // try smaller groups into the leftover space
                                      // 继续尝试把更小的组塞进剩余空间
                        }

                        placed.Add(group);
                    }

                    if (placed.Count == 0) continue;

                    best = new AtlasResult
                    {
                        Width = candidate.Width,
                        Height = candidate.Height,
                        Utilization = grid.Utilization,
                        Padding = padding,
                    };
                    best.Groups.AddRange(placed);
                    placedGroups = placed;

                    // First candidate that fits everything wins, per the specification.
                    // 依据需求，第一个能装下全部的候选直接作为成品。
                    if (allFit) break;
                }

                if (best == null || placedGroups == null || placedGroups.Count == 0)
                {
                    // Nothing could be placed at all: give up on these groups.
                    // 完全无法放置：放弃这些组。
                    foreach (var g in remaining)
                    {
                        g.SkipReason = "island does not fit the largest candidate atlas";
                        _log?.Warning(
                            $"UV group {g.Id} could not be atlased: {g.SkipReason}");
                    }

                    break;
                }

                best.Index = results.Count;
                best.TypeSignature = placedGroups[0].TypeSignature;
                foreach (var g in placedGroups)
                {
                    foreach (var island in g.Islands) island.AtlasIndex = best.Index;
                    remaining.Remove(g);
                }

                results.Add(best);
                _log?.Detail(
                    $"Atlas #{best.Index} {best.Width}x{best.Height} " +
                    $"groups={best.Groups.Count} utilization={best.Utilization:P1}");
            }

            return results;
        }

        /// <summary>
        /// Attempts to place every island of a group. All-or-nothing: a partial placement is
        /// rolled back by discarding the trial grid at the call site.
        /// 尝试放置组内所有岛。全有或全无：部分放置会由调用方丢弃试验网格来回滚。
        /// </summary>
        private bool TryPlaceGroup(OccupancyGrid grid, UVGroup group, AtlasCandidate candidate)
        {
            // Sort islands within the group: rasterized area descending, then long side
            // descending, matching the specified ordering.
            // 组内岛排序：光栅化面积降序，其次长边降序，与需求指定顺序一致。
            var islands = new List<UVIsland>(group.Islands);
            islands.Sort((a, b) =>
            {
                var c = b.CoveredCells.CompareTo(a.CoveredCells);
                if (c != 0) return c;
                var la = Mathf.Max(a.MaskWidth, a.MaskHeight);
                var lb = Mathf.Max(b.MaskWidth, b.MaskHeight);
                return lb.CompareTo(la);
            });

            var placements = new List<(UVIsland island, int x, int y, bool rot)>();

            foreach (var island in islands)
            {
                if (island.CoverageMask == null) return false;

                if (!FindPosition(grid, island, out var px, out var py, out var rotated))
                {
                    return false;
                }

                placements.Add((island, px, py, rotated));

                // Commit immediately so later islands see this one.
                // 立即提交，使后续岛能看到该岛的占用。
                var mw = rotated ? island.MaskHeight : island.MaskWidth;
                var mh = rotated ? island.MaskWidth : island.MaskHeight;
                var mask = rotated
                    ? IslandRasterizer.Transpose(
                        island.CoverageMask, island.MaskWidth, island.MaskHeight, out _, out _)
                    : island.CoverageMask;
                grid.Place(mask, mw, mh, px, py);
            }

            foreach (var (island, x, y, rot) in placements)
            {
                island.PackedPosition = new Vector2Int(
                    x * IslandRasterizer.CellSize, y * IslandRasterizer.CellSize);
                island.Rotated = rot;
            }

            return true;
        }

        /// <summary>
        /// Full-scan bottom-left-fill with a 90 degree rotation trial. Scanning bottom-up and
        /// left-to-right yields the classic BLF packing quality without a skyline heuristic's
        /// blind spots.
        /// 带 90 度旋转试验的全扫描 BLF。自下而上、自左而右扫描可获得经典 BLF 的装箱质量，
        /// 且没有 skyline 启发式的盲区。
        /// </summary>
        private static bool FindPosition(
            OccupancyGrid grid, UVIsland island, out int px, out int py, out bool rotated)
        {
            px = py = 0;
            rotated = false;

            var w = island.MaskWidth;
            var h = island.MaskHeight;
            var mask = island.CoverageMask;

            var bestX = int.MaxValue;
            var bestY = int.MaxValue;
            var found = false;

            // Orientation 0. The Burst kernel scans all rows in parallel and reduces to the
            // bottom-left-most fit, which is bit-identical to the scalar scan below.
            // 方向 0。Burst 内核并行扫描所有行并归约出最下最左的可放置位置，
            // 与下方标量扫描的结果位完全一致。
            if (grid.TryFindPositionBurst(mask, w, h, out var bx0, out var by0))
            {
                bestX = bx0;
                bestY = by0;
                found = true;
                rotated = false;
            }

            // Orientation 90: transpose the bitmask. Texels are rotated to match at composite
            // time and tangents are never recomputed, so sampling remains equivalent.
            // 方向 90：转置位掩码。合成时 texel 同步旋转且切线绝不重算，因此采样保持等价。
            var tMask = IslandRasterizer.Transpose(mask, w, h, out var tw, out var th);
            if (grid.TryFindPositionBurst(tMask, tw, th, out var bx1, out var by1))
            {
                // Prefer the strictly lower position; on a tie prefer the leftmost. Rotation is
                // only chosen when it genuinely improves placement, keeping results stable.
                // 优先选择严格更低的位置；平局时选择最左。
                // 只有旋转确实能改善放置时才采用，从而保持结果稳定。
                if (!found || by1 < bestY || (by1 == bestY && bx1 < bestX))
                {
                    bestX = bx1;
                    bestY = by1;
                    found = true;
                    rotated = true;
                }
            }

            px = bestX == int.MaxValue ? 0 : bestX;
            py = bestY == int.MaxValue ? 0 : bestY;
            return found;
        }

        private static long TotalCells(UVGroup group)
        {
            long sum = 0;
            foreach (var i in group.Islands) sum += i.CoveredCells;
            return sum;
        }
    }
}
