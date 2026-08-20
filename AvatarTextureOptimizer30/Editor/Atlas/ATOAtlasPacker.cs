// ATOAtlasPacker.cs — 图集装箱器 / Atlas packer.
// 说明：按贴图类型组装箱。装箱步骤（依据需求）：
//  - 贴图按（质量缩放、剔除后）所有岛的光栅化总面积降序排序形成贴图队列
//  - 候选图集池：默认 2 的 n 次幂边长（64 ~ 8192，移动端 4096）；实验性 NPOT 以 64 为步进；
//    按面积从小到大、长边/短边升序（最接近正方形优先）排序
//  - 每张贴图及其所属 UV 组为原子操作刚性装箱；先尝试已有箱（队列复用），装不下则开新箱（选第一个能装下的候选）
//  - 岛形状光栅化装箱（非矩形装箱）；旋转 90 度步进（位掩码转置）
//  - 同一 UV 组在不同箱/不同角色图集上的位置保持一致（归一化布局），保证共享 UV 不出错
//  - padding = max(用户挡位, ceil(图集最大边长/128))
//  - 单个贴图无法装入最大图集 → 放弃该贴图整个 UV 组的图集化，报 warning（进入整图路径）
// Note: bins per texture type group. Textures are sorted by rasterized island area desc; candidate pools are
// powers of two (64~8192, mobile 4096) or NPOT steps of 64 (experimental), sorted by area asc then aspect asc;
// each texture+its UV group is packed rigidly as one atomic item, reusing existing bins (queues) first;
// island SHAPES are rasterized (not rect packing); rotations in 90° steps via bit transpose;
// shared-UV islands keep identical normalized placements across bins and role atlases;
// padding = max(user option, ceil(max atlas side / 128)); a texture that cannot fit the largest atlas falls back
// to the whole-texture path with a warning.

using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer
{
    /// <summary>装箱项（含位掩码缓存）。/ Pack item (with bitmask caches).</summary>
    internal sealed class ATOPackItem : IDisposable
    {
        public ATOItem item;                                     // 原子项 / the atomic item
        public Dictionary<ATOIsland, RectInt> localRects = new Dictionary<ATOIsland, RectInt>(); // 项内岛矩形（px，4px 对齐）/ island rects within the item (px, 4px aligned)
        public int cellW;                                        // 基础掩码宽（格）/ base mask width (cells)
        public int cellH;                                        // 基础掩码高（格）/ base mask height (cells)
        public NativeArray<ulong> baseMask;                      // 基础掩码（未旋转未膨胀）/ base mask (unrotated, undilated)
        public long areaCells;                                   // 面积（格）/ area (cells)
        public bool failed;                                      // 装箱失败（整图路径）/ failed to pack (whole-texture path)
        public ATOBin bin;                                       // 所在箱 / the bin
        public int placedRot;                                    // 放置旋转 / placed rotation
        public int placedX, placedY;                             // 放置位置（格，未膨胀基准偏移）/ placed position (cells)

        public void Dispose()
        {
            if (baseMask.IsCreated) baseMask.Dispose();
        }
    }

    /// <summary>候选图集尺寸。/ Candidate atlas size.</summary>
    internal readonly struct ATOCandidate : IComparable<ATOCandidate>
    {
        public readonly int w;
        public readonly int h;
        public ATOCandidate(int w, int h)
        {
            this.w = w;
            this.h = h;
        }
        public long Area => (long)w * h;
        public double Aspect => (double)Mathf.Max(w, h) / Mathf.Min(w, h);
        public int CompareTo(ATOCandidate other)
        {
            var a = Area.CompareTo(other.Area);
            if (a != 0) return a;
            return Aspect.CompareTo(other.Aspect);
        }
    }

    /// <summary>装箱结果。/ Packing results.</summary>
    internal sealed class ATOPackResult
    {
        public List<ATOPackItem> failedItems = new List<ATOPackItem>(); // 未装箱（整图路径）/ unpacked (whole-texture path)
        public List<ATOBin> bins = new List<ATOBin>();                  // 全部箱 / all bins
    }

    /// <summary>图集装箱器。/ Atlas packer.</summary>
    internal sealed class ATOAtlasPacker
    {
        private readonly int _maxSide;
        private readonly bool _npot;
        private readonly int _minPadding;
        private readonly List<ATOCandidate> _pool; // 预排序候选池 / pre-sorted candidate pool

        public ATOAtlasPacker(int maxSide, bool npot, int minPadding)
        {
            _maxSide = maxSide;
            _npot = npot;
            _minPadding = minPadding;
            _pool = BuildPool();
        }

        /// <summary>生成候选图集池（排序）。/ Build the candidate pool (sorted).</summary>
        private List<ATOCandidate> BuildPool()
        {
            var sizes = new List<int>();
            if (_npot)
            {
                for (int s = 64; s <= _maxSide; s += 64) sizes.Add(s);
            }
            else
            {
                for (int s = 64; s <= _maxSide; s *= 2) sizes.Add(s);
            }
            var pool = new List<ATOCandidate>();
            foreach (var w in sizes)
                foreach (var h in sizes)
                    pool.Add(new ATOCandidate(w, h));
            pool.Sort();
            return pool;
        }

        /// <summary>当前候选的 padding（px）。/ Effective padding (px) for a candidate.</summary>
        public int PaddingFor(int w, int h)
        {
            var side = Mathf.Max(w, h);
            return Mathf.Max(_minPadding, Mathf.CeilToInt(side / 128f));
        }

        /// <summary>
        /// 对一个类型组执行装箱。items 需按面积降序预排序。
        /// Pack one type group. Items must be pre-sorted by area desc.
        /// </summary>
        public ATOPackResult Pack(ATOTypeGroup group, List<ATOPackItem> sortedItems)
        {
            var result = new ATOPackResult();
            var layout = group.layout; // island → placement（组内共享）/ shared within the group

            foreach (var packItem in sortedItems)
            {
                if (packItem.failed) continue;

                // 1. 尝试放入已有箱（队列复用）/ try existing bins (queue reuse)
                bool placed = false;
                foreach (var bin in group.bins)
                {
                    if (TryPlaceInBin(packItem, bin, layout))
                    {
                        placed = true;
                        break;
                    }
                }
                if (placed) continue;

                // 2. 开新箱：按候选池顺序，取第一个能装下的候选 / open a new bin: first candidate in pool order that fits
                foreach (var candidate in _pool)
                {
                    // 面积单位换算：1 格 = 4×4 px / area unit conversion: 1 cell = 4×4 px
                    if (candidate.Area < packItem.areaCells * 16) continue;
                    var bin = new ATOBin
                    {
                        group = group,
                        width = candidate.w,
                        height = candidate.h,
                        occupancy = new ATOBitmask((candidate.w + 3) / 4, (candidate.h + 3) / 4, Allocator.TempJob),
                    };
                    if (TryPlaceInBin(packItem, bin, layout))
                    {
                        group.bins.Add(bin);
                        result.bins.Add(bin);
                        placed = true;
                        break;
                    }
                    bin.occupancy.Dispose();
                }
                if (placed) continue;

                // 3. 无法装入任何候选 → 放弃该贴图整个 UV 组的图集化（整图路径 + warning）/
                //    cannot fit any candidate → give up atlasing for this texture's whole UV group (whole-texture path + warning)
                packItem.failed = true;
                result.failedItems.Add(packItem);
                ATOLog.Warning($"Texture '{packItem.item.texture.name}' cannot fit into any atlas candidate; falling back to whole-texture path. (贴图无法装入任何候选图集，改用整图路径)");
            }

            // 4. 计算各箱的角色缩放系数 / compute per-bin role scale factors
            foreach (var bin in group.bins)
            {
                ComputeRoleFactors(bin, layout);
            }
            return result;
        }

        /// <summary>尝试将项放入箱。/ Try to place an item into a bin.</summary>
        private bool TryPlaceInBin(ATOPackItem packItem, ATOBin bin, Dictionary<ATOIsland, ATOPlacement> layout)
        {
            var pad = PaddingFor(bin.width, bin.height);
            // 两侧各膨胀 k 格 → 岛间距离 ≈ 2×4k ≈ pad（4px 粒度下取 [pad, pad+8)）/ dilating k cells per side gives gap ≈ 2×4k ≈ pad
            var padCells = Mathf.Max(1, Mathf.CeilToInt(pad / 8f));

            // 箱格式档案（色彩空间/过滤模式不同 → 不同箱；首项设定档案）/ bin format profile (color space / filter mode differ → different bins; first item sets the profile)
            var info = packItem.item.info;
            if (bin.items.Count > 0)
            {
                if (bin.isSRGB != info.isSRGB || bin.filterMode != info.filterMode) return false;
            }
            else
            {
                bin.isSRGB = info.isSRGB;
                bin.filterMode = info.filterMode;
            }

            // 布局已定的岛（同一 UV 组在不同箱中的位置必须一致）/ islands whose placement is already decided
            var pinned = new List<(ATOIsland island, Vector2Int posPx, int rot)>();
            int forcedRot = -1;
            bool conflict = false;
            foreach (var kv in packItem.localRects)
            {
                var island = kv.Key;
                if (!layout.TryGetValue(island, out var placement)) continue;
                var posPx = new Vector2Int(
                    Mathf.RoundToInt(placement.min.x * bin.width / 4f) * 4,
                    Mathf.RoundToInt(placement.min.y * bin.height / 4f) * 4);
                if (forcedRot >= 0 && forcedRot != placement.rotation) conflict = true;
                forcedRot = placement.rotation;
                pinned.Add((island, posPx, placement.rotation));
            }

            // 允许的旋转（与已定布局一致；未定则 0/90/180/270）/ allowed rotations (consistent with pinned; otherwise all four)
            var rotations = new List<int>();
            if (forcedRot >= 0)
            {
                if (!conflict) rotations.Add(forcedRot);
            }
            else
            {
                rotations.Add(0);
                rotations.Add(1);
                rotations.Add(2);
                rotations.Add(3);
            }

            foreach (var rot in rotations)
            {
                // 生成该旋转 + 膨胀的掩码 / build the rotated + dilated mask
                var mask = GetMask(packItem, rot, padCells, out int maskW, out int maskH);
                var maskStride = (maskW + 63) / 64;
                // 掩码与岛矩形的偏移（膨胀导致）/ offset of island rects due to dilation
                var localRectsRot = ComputeRotatedRects(packItem, rot, padCells);

                int cx = 0, cy = 0;
                bool ok;
                if (pinned.Count > 0)
                {
                    // 由已定岛求平移 / solve translation from pinned islands
                    ok = SolvePinnedTranslation(packItem, localRectsRot, pinned, bin, out cx, out cy);
                    if (!ok) continue;
                    ok = cx >= 0 && cy >= 0 &&
                         ATOBitmaskOps.FitsAt(bin.occupancy.bits, bin.occupancy.stride, bin.occupancy.cellsW, bin.occupancy.cellsH,
                             mask, maskStride, maskW, maskH, cx, cy);
                }
                else
                {
                    // 全扫描 BLF / full-scan BLF
                    ok = ATOBitmaskOps.TryPlaceBlf(bin.occupancy.bits, bin.occupancy.stride, bin.occupancy.cellsW, bin.occupancy.cellsH,
                        mask, maskStride, maskW, maskH, out cx, out cy);
                }

                if (!ok) continue;

                // 放置成功：写入占用与布局 / placed: stamp occupancy and record layout
                ATOBitmaskOps.Stamp(bin.occupancy.bits, bin.occupancy.stride, mask, maskStride, maskW, maskH, cx, cy);
                packItem.bin = bin;
                packItem.placedRot = rot;
                packItem.placedX = cx;
                packItem.placedY = cy;
                bin.items.Add(packItem.item);

                foreach (var kv in localRectsRot)
                {
                    var island = kv.Key;
                    var rect = kv.Value;
                    var px = (cx + rect.x) * 4;
                    var py = (cy + rect.y) * 4;
                    var normMin = new Vector2(px / (float)bin.width, py / (float)bin.height);
                    var normSize = new Vector2(rect.width * 4f / bin.width, rect.height * 4f / bin.height);
                    if (!layout.TryGetValue(island, out var placement))
                    {
                        placement = new ATOPlacement();
                        layout[island] = placement;
                    }
                    placement.min = normMin;
                    placement.size = normSize;
                    placement.rotation = rot;
                    placement.bin = bin;
                }
                return true;
            }
            return false;
        }

        /// <summary>由已定岛求解唯一平移（各岛约束必须一致）。/ Solve the unique translation from pinned islands (must all agree).</summary>
        private static bool SolvePinnedTranslation(ATOPackItem packItem,
            Dictionary<ATOIsland, RectInt> localRectsRot,
            List<(ATOIsland island, Vector2Int posPx, int rot)> pinned, ATOBin bin,
            out int cx, out int cy)
        {
            cx = 0;
            cy = 0;
            bool first = true;
            foreach (var (island, posPx, rot) in pinned)
            {
                var rect = localRectsRot[island];
                // 位置以岛矩形左上角对齐（4px 粒度）/ align by the island rect top-left (4px granularity)
                var tx = (posPx.x / 4) - rect.x;
                var ty = (posPx.y / 4) - rect.y;
                if (first)
                {
                    cx = tx;
                    cy = ty;
                    first = false;
                }
                else if (cx != tx || cy != ty)
                {
                    return false; // 约束冲突 / conflicting constraints
                }
            }
            return !first;
        }

        /// <summary>计算旋转后（含膨胀偏移）的岛矩形（格坐标）。/ Compute rotated island rects (cell coords) incl. dilation offset.</summary>
        private static Dictionary<ATOIsland, RectInt> ComputeRotatedRects(ATOPackItem packItem, int rot, int padCells)
        {
            var result = new Dictionary<ATOIsland, RectInt>();
            int w0 = packItem.cellW;
            int h0 = packItem.cellH;
            foreach (var kv in packItem.localRects)
            {
                var r = kv.Value; // px → cells / px to cells
                int x = r.x / 4, y = r.y / 4, w = Mathf.CeilToInt(r.width / 4f), h = Mathf.CeilToInt(r.height / 4f);
                int nx, ny, nw, nh;
                switch (rot & 3)
                {
                    case 1: nw = h; nh = w; nx = h0 - y - h; ny = x; break;
                    case 2: nw = w; nh = h; nx = w0 - x - w; ny = h0 - y - h; break;
                    case 3: nw = h; nh = w; nx = y; ny = w0 - x - w; break;
                    default: nw = w; nh = h; nx = x; ny = y; break;
                }
                result[kv.Key] = new RectInt(nx + padCells, ny + padCells, nw, nh);
            }
            return result;
        }

        /// <summary>获取（缓存）旋转 + 膨胀掩码。/ Get (cached) rotated + dilated mask.</summary>
        private NativeArray<ulong> GetMask(ATOPackItem packItem, int rot, int padCells, out int maskW, out int maskH)
        {
            // 缓存键 / cache key
            var key = (packItem, rot, padCells);
            if (_maskCache.TryGetValue(key, out var cached))
            {
                maskW = cached.w;
                maskH = cached.h;
                return cached.bits;
            }

            ATOBitmask rotated;
            switch (rot & 3)
            {
                case 1: rotated = ATOBitmaskOps.Rotate90(packItem.baseMask.ToBitmask(packItem.cellW, packItem.cellH)); break;
                case 2: rotated = ATOBitmaskOps.Rotate90(ATOBitmaskOps.Rotate90(packItem.baseMask.ToBitmask(packItem.cellW, packItem.cellH))); break;
                case 3: rotated = ATOBitmaskOps.Rotate90(ATOBitmaskOps.Rotate90(ATOBitmaskOps.Rotate90(packItem.baseMask.ToBitmask(packItem.cellW, packItem.cellH)))); break;
                default: rotated = packItem.baseMask.ToBitmask(packItem.cellW, packItem.cellH); break;
            }

            var dilated = Dilate(rotated, padCells);
            maskW = dilated.cellsW;
            maskH = dilated.cellsH;
            _maskCache[key] = (dilated.bits, maskW, maskH);
            return dilated.bits;
        }

        private readonly Dictionary<(ATOPackItem, int, int), (NativeArray<ulong> bits, int w, int h)> _maskCache = new Dictionary<(ATOPackItem, int, int), (NativeArray<ulong>, int, int)>();

        /// <summary>掩码膨胀 k 格（8 邻域迭代）。/ Dilate a mask by k cells (8-neighborhood iterations).</summary>
        private ATOBitmask Dilate(ATOBitmask src, int k)
        {
            if (k <= 0) return src;
            var cur = src;
            for (int iter = 0; iter < k; iter++)
            {
                var next = new ATOBitmask(cur.cellsW + 2, cur.cellsH + 2, Allocator.TempJob);
                for (int y = 0; y < cur.cellsH; y++)
                {
                    var srcRow = y * cur.stride;
                    var dstRow0 = y * next.stride;
                    var dstRow1 = (y + 1) * next.stride;
                    var dstRow2 = (y + 2) * next.stride;
                    for (int x = 0; x < cur.cellsW; x++)
                    {
                        if ((cur.bits[srcRow + (x >> 6)] & (1UL << (x & 63))) == 0) continue;
                        for (int dy = 0; dy <= 2; dy++)
                            for (int dx = 0; dx <= 2; dx++)
                            {
                                var nx = x + dx;
                                next.bits[(dstRow0 + dy * next.stride) + (nx >> 6)] |= 1UL << (nx & 63);
                            }
                    }
                }
                if (cur != src) cur.Dispose();
                cur = next;
            }
            return cur;
        }

        /// <summary>计算箱内各角色缩放系数（木桶：取全部岛的最小比值）。/ Compute per-bin role scale factors (barrel: min ratio across islands).</summary>
        private static void ComputeRoleFactors(ATOBin bin, Dictionary<ATOIsland, ATOPlacement> layout)
        {
            float minNU = 1f, minNV = 1f, minMU = 1f, minMV = 1f;
            bool hasNormal = false, hasMask = false;

            // ref → island 映射 / ref → island map
            var refToIsland = new Dictionary<ATOIslandRef, ATOIsland>();
            foreach (var island in layout.Keys)
                foreach (var r in island.refs)
                    refToIsland[r] = island;

            foreach (var item in bin.items)
            {
                foreach (var r in item.refs)
                {
                    if (!refToIsland.TryGetValue(r, out var island)) continue;
                    var baseW = Mathf.Max(1f, island.baseSizeU);
                    var baseH = Mathf.Max(1f, island.baseSizeV);
                    if (r.category == ATOScaleCategory.Normal)
                    {
                        hasNormal = true;
                        minNU = Mathf.Min(minNU, island.normalSizeU / baseW);
                        minNV = Mathf.Min(minNV, island.normalSizeV / baseH);
                    }
                    else if (r.category == ATOScaleCategory.Mask)
                    {
                        hasMask = true;
                        minMU = Mathf.Min(minMU, island.maskSizeU / baseW);
                        minMV = Mathf.Min(minMV, island.maskSizeV / baseH);
                    }
                }
            }
            bin.normalScaleU = hasNormal ? Mathf.Clamp(minNU, 0.001f, 1f) : 1f;
            bin.normalScaleV = hasNormal ? Mathf.Clamp(minNV, 0.001f, 1f) : 1f;
            bin.maskScaleU = hasMask ? Mathf.Clamp(minMU, 0.001f, 1f) : 1f;
            bin.maskScaleV = hasMask ? Mathf.Clamp(minMV, 0.001f, 1f) : 1f;
        }

        /// <summary>释放全部掩码缓存。/ Release all mask caches.</summary>
        public void Dispose()
        {
            foreach (var kv in _maskCache)
            {
                if (kv.Value.bits.IsCreated) kv.Value.bits.Dispose();
            }
            _maskCache.Clear();
        }
    }

    /// <summary>NativeArray 位掩码的包装扩展。/ Wrapper extensions for NativeArray bitmasks.</summary>
    internal static class ATOBitmaskExtensions
    {
        /// <summary>包装 NativeArray 为 ATOBitmask（浅包装，不复制）。/ Wrap a NativeArray as an ATOBitmask (shallow, no copy).</summary>
        public static ATOBitmask ToBitmask(this NativeArray<ulong> bits, int cellsW, int cellsH)
        {
            return ATOBitmask.Wrap(bits, cellsW, cellsH);
        }
    }
}
