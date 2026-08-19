// SPDX-License-Identifier: MIT
// AvatarTextureOptimizer (ATO) - Shape-aware atlas packing (raster bitmask + BLF + rotation).
// AvatarTextureOptimizer (ATO) - 形状感知图集装箱（光栅位掩码 + BLF + 旋转）。

using System;
using System.Collections.Generic;
using System.Linq;
using Net.Fosa.AvatarTextureOptimizer.Editor.Analysis;
using Net.Fosa.AvatarTextureOptimizer.Editor.Core;
using Net.Fosa.AvatarTextureOptimizer.Editor.MeshOps;
using Net.Fosa.AvatarTextureOptimizer.Editor.Quality;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor.Atlas
{
    /// <summary>
    /// EN: A candidate atlas size. The pool is generated once per build and shared by every queue.
    /// ZH: 一个候选图集尺寸。整个构建只生成一次候选池，并被所有队列共享。
    /// </summary>
    public readonly struct AtlasCandidate
    {
        public readonly int Width, Height;

        public AtlasCandidate(int w, int h) { Width = w; Height = h; }

        public long Area => (long)Width * Height;
        public float Aspect => (float)Mathf.Max(Width, Height) / Mathf.Min(Width, Height);
        public override string ToString() => $"{Width}x{Height}";
    }

    /// <summary>
    /// EN: One packed atlas: which textures went in, where every island landed, and the usage statistics
    ///     the final report prints.
    /// ZH: 一个已装箱的图集：包含了哪些贴图、每个岛落在哪里，以及最终报告要打印的利用率统计。
    /// </summary>
    public sealed class AtlasPlan
    {
        public int Index;
        public int Width, Height;
        public int Padding;
        public string TypeGroupKey;

        /// <summary>EN: Islands placed in this atlas. ZH: 放入该图集的岛。</summary>
        public readonly List<IslandPlan> Islands = new List<IslandPlan>();

        /// <summary>EN: Distinct source textures contributing to this atlas. ZH: 贡献到该图集的不同源贴图。</summary>
        public readonly HashSet<TextureUsage> Sources = new HashSet<TextureUsage>();

        /// <summary>EN: Covered cells / total cells. ZH: 已覆盖格子数 / 总格子数。</summary>
        public float Utilisation;

        public override string ToString() =>
            $"ATO atlas #{Index} {Width}x{Height} src={Sources.Count} islands={Islands.Count} util={Utilisation:P1}";
    }

    public static class AtlasPacker
    {
        public const int CellSize = 4;

        /// <summary>
        /// EN: Build the candidate pool. Power-of-two by default (64..max); with the experimental NPOT
        ///     option, 64 px steps instead. Non-square candidates are allowed and preferred in order of
        ///     "closest to square first" among equal areas.
        /// ZH: 构建候选图集池。默认使用 2 的 n 次幂（64..max）；勾选实验性 NPOT 时改为 64px 步进。
        ///     允许非正方形候选，在面积相同的情况下越接近正方形越优先。
        /// </summary>
        public static List<AtlasCandidate> BuildCandidatePool(int maxSize, bool npot)
        {
            var sizes = new List<int>();
            if (npot)
            {
                for (int s = 64; s <= maxSize; s += 64) sizes.Add(s);
            }
            else
            {
                for (int s = 64; s <= maxSize; s *= 2) sizes.Add(s);
            }

            var pool = new List<AtlasCandidate>();
            foreach (var w in sizes)
            foreach (var h in sizes)
            {
                // EN: Extremely elongated atlases waste mip chains and confuse compressors.
                // ZH: 极端狭长的图集会浪费 mip 链并影响压缩器。
                if ((float)Mathf.Max(w, h) / Mathf.Min(w, h) > 8f) continue;
                pool.Add(new AtlasCandidate(w, h));
            }

            pool.Sort((a, b) =>
            {
                int c = a.Area.CompareTo(b.Area);
                if (c != 0) return c;
                return a.Aspect.CompareTo(b.Aspect);
            });

            ATOLog.Debug_($"candidate pool: {pool.Count} entries (max {maxSize}, npot={npot})");
            return pool;
        }

        /// <summary>
        /// EN: Padding for a candidate: ceil(maxEdge / 128), clamped up to the configured minimum.
        /// ZH: 候选图集的 padding：向上取整的 最大边长/128，并向上钳制到配置的最小值。
        /// </summary>
        public static int PaddingFor(AtlasCandidate candidate, int minPadding)
        {
            int p = Mathf.CeilToInt(Mathf.Max(candidate.Width, candidate.Height) / 128f);
            return Mathf.Max(p, minPadding);
        }

        /// <summary>
        /// EN: A UV group as seen by the packer: the shared island geometry plus the parallel texture
        ///     layers that must all receive the identical layout.
        /// ZH: 装箱器眼中的一个 UV 组：共享的岛几何，加上必须获得完全相同布局的若干平行贴图层。
        /// </summary>
        public sealed class PackGroup
        {
            public int UvGroupId;
            public readonly List<UVIsland> Islands = new List<UVIsland>();

            /// <summary>EN: layer key -&gt; island plans of that layer. ZH: 层键 -&gt; 该层的岛计划。</summary>
            public readonly Dictionary<string, List<IslandPlan>> Layers =
                new Dictionary<string, List<IslandPlan>>(StringComparer.Ordinal);

            /// <summary>EN: Sorted layer keys; groups with the same signature share atlas families.
            ///     ZH: 排序后的层键；签名相同的组共享图集族。</summary>
            public string Signature;

            public long FootprintCells(int minPadding)
            {
                long total = 0;
                int pad = (minPadding + CellSize - 1) / CellSize;
                foreach (var i in Islands)
                {
                    int cx = (i.ScaledWidth + CellSize - 1) / CellSize + pad * 2;
                    int cy = (i.ScaledHeight + CellSize - 1) / CellSize + pad * 2;
                    total += (long)cx * cy;
                }
                return total;
            }
        }

        /// <summary>
        /// EN: Build the packer's work items from the usage graph. One item per UV group, because a UV
        ///     group is the smallest thing that can be placed without breaking the "same UV, same slot in
        ///     every parallel atlas" invariant.
        /// ZH: 从关系图构建装箱器的工作项。每个 UV 组一项，
        ///     因为 UV 组是在不破坏「同一 UV 在每张平行图集上位置相同」不变量的前提下能放置的最小单位。
        /// </summary>
        public static List<PackGroup> BuildPackGroups(
            IReadOnlyDictionary<int, List<TextureUsage>> uvGroups,
            Dictionary<TextureUsage, List<IslandPlan>> plansByTexture)
        {
            var result = new List<PackGroup>();

            foreach (var kv in uvGroups)
            {
                var group = new PackGroup { UvGroupId = kv.Key };
                var seenIslands = new HashSet<UVIsland>();

                // EN: Two textures with the same role on the same UV are *alternatives* (an animation
                //     swaps between them), so they cannot share an atlas slot. Each one becomes its own
                //     variant layer, which the family then emits as a separate parallel atlas.
                // ZH: 同一 UV 上角色相同的两张贴图是**互斥的备选**（由动画在它们之间切换），
                //     因此不能共用同一个图集位置。每一张各成为一个变体层，
                //     由图集族输出为一张独立的平行图集。
                var variantCounter = new Dictionary<string, int>(StringComparer.Ordinal);

                foreach (var usage in kv.Value.OrderBy(u => u.Texture != null ? u.Texture.name : "",
                             StringComparer.Ordinal))
                {
                    if (usage.Excluded) continue;
                    if (!plansByTexture.TryGetValue(usage, out var plans)) continue;

                    var role = usage.TypeGroupKey ?? "default";
                    variantCounter.TryGetValue(role, out var variant);
                    variantCounter[role] = variant + 1;

                    var layerKey = variant == 0 ? role : $"{role}#{variant}";
                    if (!group.Layers.TryGetValue(layerKey, out var layer))
                        group.Layers[layerKey] = layer = new List<IslandPlan>();

                    foreach (var plan in plans)
                    {
                        layer.Add(plan);
                        if (seenIslands.Add(plan.Island)) group.Islands.Add(plan.Island);
                    }
                }

                if (group.Islands.Count == 0 || group.Layers.Count == 0) continue;

                var keys = new List<string>(group.Layers.Keys);
                keys.Sort(StringComparer.Ordinal);
                group.Signature = string.Join("&&", keys);
                result.Add(group);
            }

            ATOLog.Debug_($"pack groups: {result.Count}");
            return result;
        }

        /// <summary>
        /// EN: Pack every UV group into atlas families. Each family produces one atlas per layer, all with
        ///     identical dimensions and identical island placement, which is exactly what lets a single set
        ///     of rewritten UVs address a colour atlas and its companion normal/mask atlases at once.
        /// ZH: 把所有 UV 组装入图集族。每个族按层各生成一张图集，尺寸与岛的位置完全一致，
        ///     这正是「一份重写后的 UV 能同时寻址彩色图集及其配套的法线/蒙版图集」的实现方式。
        /// </summary>
        public static List<AtlasPlan> PackAll(List<PackGroup> groups, List<AtlasCandidate> pool,
            int minPadding, ATOProgress progress, ref int atlasCounter)
        {
            var atlases = new List<AtlasPlan>();

            // EN: Groups with the same layer signature can live in the same family.
            // ZH: 层签名相同的组可以共处同一个族。
            var bySignature = new Dictionary<string, List<PackGroup>>(StringComparer.Ordinal);
            foreach (var g in groups)
            {
                if (!bySignature.TryGetValue(g.Signature, out var list))
                    bySignature[g.Signature] = list = new List<PackGroup>();
                list.Add(g);
            }

            foreach (var kv in bySignature)
            {
                // EN: Largest footprint first - the classic first-fit-decreasing ordering.
                // ZH: 占位面积最大者优先——经典的 FFD 排序。
                var queue = kv.Value.OrderByDescending(g => g.FootprintCells(minPadding)).ToList();

                int guard = 0;
                while (queue.Count > 0 && guard++ < 8192)
                {
                    progress?.ThrowIfCancelled();

                    long needed = queue.Sum(g => g.FootprintCells(minPadding));
                    var viable = pool
                        .Where(c => (long)(c.Width / CellSize) * (c.Height / CellSize) >= needed)
                        .ToList();
                    if (viable.Count == 0) viable = new List<AtlasCandidate> { pool[pool.Count - 1] };

                    List<PackGroup> placed = null;
                    AtlasCandidate chosen = default;
                    bool complete = false;

                    foreach (var candidate in viable)
                    {
                        progress?.ThrowIfCancelled();
                        if (!TryPackFamily(candidate, queue, minPadding, out placed, out _)) continue;
                        if (placed.Count != queue.Count) continue;
                        chosen = candidate;
                        complete = true;
                        break;
                    }

                    float utilisation;
                    if (!complete)
                    {
                        chosen = viable[viable.Count - 1];
                        if (!TryPackFamily(chosen, queue, minPadding, out placed, out utilisation) ||
                            placed.Count == 0)
                        {
                            // EN: A single UV group does not fit even the largest atlas - give up on it.
                            // ZH: 连单个 UV 组都装不进最大图集——放弃它。
                            var victim = queue[0];
                            var sample = victim.Layers.Values.First().First();
                            ATOReportUtil.Warn("ATO:warn:island_too_large", sample.Texture.Texture);
                            queue.RemoveAt(0);
                            continue;
                        }
                    }
                    else
                    {
                        TryPackFamily(chosen, queue, minPadding, out placed, out utilisation);
                    }

                    int padding = PaddingFor(chosen, minPadding);

                    // EN: Emit one atlas per layer, all sharing the family's geometry.
                    // ZH: 每层输出一张图集，共享该族的几何布局。
                    var layerKeys = new SortedSet<string>(StringComparer.Ordinal);
                    foreach (var g in placed) foreach (var k in g.Layers.Keys) layerKeys.Add(k);

                    foreach (var layerKey in layerKeys)
                    {
                        var plan = new AtlasPlan
                        {
                            Index = atlasCounter++,
                            Width = chosen.Width,
                            Height = chosen.Height,
                            Padding = padding,
                            TypeGroupKey = layerKey,
                            Utilisation = utilisation,
                        };

                        foreach (var g in placed)
                        {
                            if (!g.Layers.TryGetValue(layerKey, out var layer)) continue;
                            foreach (var islandPlan in layer)
                            {
                                islandPlan.AtlasIndex = plan.Index;
                                plan.Islands.Add(islandPlan);
                                plan.Sources.Add(islandPlan.Texture);
                            }
                        }

                        if (plan.Islands.Count == 0) continue;
                        atlases.Add(plan);
                        ATOLog.Info($"packed {plan} layer='{layerKey}' " +
                                    $"sources=[{string.Join(", ", plan.Sources.Select(s => s.Texture.name))}]");
                    }

                    foreach (var g in placed) queue.Remove(g);
                }
            }

            return atlases;
        }

        /// <summary>
        /// EN: Place as many whole UV groups as possible into one candidate. Placement is written onto the
        ///     shared islands, so it is automatically identical for every layer of the group.
        /// ZH: 把尽可能多的完整 UV 组放入一个候选图集。位置写在共享的岛上，
        ///     因此该组的每一层自动获得完全相同的布局。
        /// </summary>
        private static bool TryPackFamily(AtlasCandidate candidate, List<PackGroup> queue, int minPadding,
            out List<PackGroup> placedGroups, out float utilisation)
        {
            placedGroups = new List<PackGroup>();
            utilisation = 0f;

            int padding = PaddingFor(candidate, minPadding);
            int padCells = (padding + CellSize - 1) / CellSize;
            int cellsX = candidate.Width / CellSize;
            int cellsY = candidate.Height / CellSize;
            if (cellsX <= 0 || cellsY <= 0) return false;

            var occupancy = RasterMask.Create(cellsX, cellsY, Allocator.Temp);
            try
            {
                foreach (var group in queue)
                {
                    // EN: Order islands by rasterised area, then longest edge - classic BLF ordering.
                    // ZH: 按光栅化面积降序、再按最长边降序排列岛——经典 BLF 排序。
                    var ordered = group.Islands
                        .OrderByDescending(i => (long)i.ScaledWidth * i.ScaledHeight)
                        .ThenByDescending(i => Mathf.Max(i.ScaledWidth, i.ScaledHeight))
                        .ToList();

                    var snapshot = new NativeArray<ulong>(occupancy.Bits, Allocator.Temp);
                    var placements = new List<(UVIsland island, int2 origin, bool rotated)>();
                    bool allFit = true;

                    foreach (var island in ordered)
                    {
                        if (!PlaceIsland(island, occupancy, padCells, out var origin, out var rotated))
                        {
                            allFit = false;
                            break;
                        }
                        placements.Add((island, origin, rotated));
                    }

                    if (!allFit)
                    {
                        occupancy.Bits.CopyFrom(snapshot);
                        snapshot.Dispose();
                        continue; // EN: try the next, smaller group. ZH: 尝试下一个更小的组。
                    }
                    snapshot.Dispose();

                    foreach (var (island, origin, rotated) in placements)
                    {
                        island.AtlasOrigin = origin * CellSize;
                        island.Rotated = rotated;
                        island.Placed = true;
                    }
                    placedGroups.Add(group);
                }

                if (placedGroups.Count == 0) return false;
                utilisation = (float)occupancy.Popcount() / (cellsX * cellsY);
                return true;
            }
            finally
            {
                occupancy.Dispose();
            }
        }

        private static bool PlaceIsland(UVIsland island, RasterMask occupancy, int padCells,
            out int2 origin, out bool rotated)
        {
            origin = default;
            rotated = false;

            var baseMask = island.Mask;
            if (!baseMask.IsCreated) return false;

            var padded = baseMask.Dilate(padCells, Allocator.Temp);
            RasterMask transposed = default;

            try
            {
                if (ScanFit(padded, occupancy, out origin))
                {
                    Stamp(padded, occupancy, origin);
                    // EN: Dilate() offsets the island content by +padCells inside the padded mask, so the
                    //     island's own origin is the placement origin plus that offset.
                    // ZH: Dilate() 使岛内容在填充掩码内偏移了 +padCells，
                    //     因此岛自身的原点等于放置原点加上该偏移。
                    origin += new int2(padCells, padCells);
                    rotated = false;
                    return true;
                }

                transposed = padded.Transpose(Allocator.Temp);
                if (ScanFit(transposed, occupancy, out origin))
                {
                    Stamp(transposed, occupancy, origin);
                    origin += new int2(padCells, padCells);
                    rotated = true;
                    return true;
                }

                return false;
            }
            finally
            {
                padded.Dispose();
                if (transposed.IsCreated) transposed.Dispose();
            }
        }

        private static bool ScanFit(RasterMask shape, RasterMask occupancy, out int2 origin)
        {
            origin = default;
            int maxX = occupancy.CellsX - shape.CellsX;
            int maxY = occupancy.CellsY - shape.CellsY;
            if (maxX < 0 || maxY < 0) return false;

            for (int y = 0; y <= maxY; y++)
            for (int x = 0; x <= maxX; x++)
            {
                if (Overlaps(shape, occupancy, x, y)) continue;
                origin = new int2(x, y);
                return true;
            }
            return false;
        }

        private static bool Overlaps(RasterMask shape, RasterMask occupancy, int ox, int oy)
        {
            for (int y = 0; y < shape.CellsY; y++)
            {
                int oy2 = oy + y;
                for (int w = 0; w < shape.WordsPerRow; w++)
                {
                    ulong bits = shape.Bits[y * shape.WordsPerRow + w];
                    if (bits == 0) continue;

                    int baseX = w * 64 + ox;
                    // EN: The shifted word may straddle two occupancy words.
                    // ZH: 移位后的字可能横跨两个占用字。
                    int wordIndex = baseX >> 6;
                    int shift = baseX & 63;

                    ulong lowMask = bits << shift;
                    if ((occupancy.Bits[oy2 * occupancy.WordsPerRow + wordIndex] & lowMask) != 0) return true;

                    if (shift != 0 && wordIndex + 1 < occupancy.WordsPerRow)
                    {
                        ulong highMask = bits >> (64 - shift);
                        if (highMask != 0 &&
                            (occupancy.Bits[oy2 * occupancy.WordsPerRow + wordIndex + 1] & highMask) != 0) return true;
                    }
                    else if (shift != 0)
                    {
                        // EN: Bits would fall outside the atlas.
                        // ZH: 有位会落在图集之外。
                        if ((bits >> (64 - shift)) != 0) return true;
                    }
                }
            }
            return false;
        }

        private static void Stamp(RasterMask shape, RasterMask occupancy, int2 origin)
        {
            for (int y = 0; y < shape.CellsY; y++)
            for (int x = 0; x < shape.CellsX; x++)
            {
                if (shape.Get(x, y)) occupancy.Set(origin.x + x, origin.y + y);
            }
        }
    }
}
