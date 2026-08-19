// Stage 6: island-shape raster bin packing with Burst bitmask BLF scan, candidate atlas pool.
// 阶段6：岛形光栅装箱（Burst 位掩码 BLF 全扫描）+ 候选图集池。
using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace net.fosa.ato.editor
{
    public static class PackStage
    {
        public const int Cell = 4; // raster granularity px / 光栅粒度

        public static void Run(AtoContext ctx)
        {
            using (AtoLog.Time("PackStage", (l, ms) => ctx.Stats.StageTimes.Add((l, ms))))
            {
                AtoProgress.BeginStage(AtoL10n.Tr("stage.pack"));
                BuildPackUnits(ctx);
                if (!ctx.Settings.generateAtlas) return;

                // queues per type group / 按类型组分队列
                var queues = ctx.PackUnits.Where(u => !u.GaveUp)
                    .GroupBy(u => u.TypeGroupKey)
                    .ToDictionary(g => g.Key, g => g.OrderByDescending(u => u.RasterArea)
                        .ThenByDescending(u => u.Islands.Count == 0 ? 0 :
                            u.Islands.Max(i => Math.Max(i.RasterSize.x, i.RasterSize.y))).ToList());

                int atlasIndex = 0;
                foreach (var q in queues)
                {
                    var remaining = q.Value;
                    while (remaining.Count > 0)
                    {
                        AtoProgress.Step(0.5f, $"type group {q.Key}, {remaining.Count} units left");
                        var packed = PackOneAtlas(ctx, remaining, atlasIndex, q.Key, out var spilled);
                        if (packed == 0 && spilled.Count > 0)
                        {
                            // single unit too large even for max atlas / 单元过大放弃图集化
                            var giveUp = spilled[0];
                            giveUp.GaveUp = true;
                            spilled.RemoveAt(0);
                            nadena.dev.ndmf.ErrorReport.ReportError(AtoL10n.Localizer,
                                nadena.dev.ndmf.ErrorSeverity.Information, "warn.island_too_big",
                                giveUp.Textures.FirstOrDefault()?.Tex?.name ?? "?");
                        }
                        else atlasIndex++;
                        remaining = spilled;
                    }
                }
                AtoLog.Info($"packing complete: {atlasIndex} atlas layouts");
            }
        }

        // ---- pack unit construction / 装箱单元构建 ----
        private static void BuildPackUnits(AtoContext ctx)
        {
            // Disqualify mappings containing whitelisted textures: their co-textures fall back to
            // whole-texture scaling. / 含白名单贴图的映射整体退化为整图缩放。
            var badMappings = new HashSet<MappingKey>();
            foreach (var kv in ctx.MappingTextures)
                if (kv.Value.Any(t => t.Whitelisted) || !ctx.Islands.ContainsKey(kv.Key))
                    badMappings.Add(kv.Key);

            var eligible = ctx.Textures.Values
                .Where(t => !t.Whitelisted && t.Mappings.Count > 0 && ctx.Settings.generateAtlas &&
                            t.Mappings.All(m => !badMappings.Contains(m)))
                .ToList();

            // union-find over shared mappings / 依共享映射的并查集
            var unitOf = new Dictionary<TexInfo, int>();
            var parent = new List<int>();
            int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
            var mappingOwner = new Dictionary<MappingKey, int>();
            foreach (var t in eligible)
            {
                int id = parent.Count;
                parent.Add(id);
                unitOf[t] = id;
                foreach (var m in t.Mappings)
                {
                    if (mappingOwner.TryGetValue(m, out var other))
                    {
                        int a = Find(id), b = Find(other);
                        if (a != b) parent[b] = a;
                    }
                    else mappingOwner[m] = id;
                }
            }

            var units = new Dictionary<int, PackUnit>();
            foreach (var t in eligible)
            {
                int root = Find(unitOf[t]);
                if (!units.TryGetValue(root, out var u))
                {
                    units[root] = u = new PackUnit { Id = ctx.PackUnits.Count + units.Count };
                }
                u.Textures.Add(t);
                t.PackUnitId = u.Id;
                foreach (var m in t.Mappings)
                    if (!u.Mappings.Contains(m)) u.Mappings.Add(m);
            }

            foreach (var u in units.Values)
            {
                bool anyNormal = u.Textures.Any(t => t.Role == TexRole.Normal || t.CompanionNormal);
                bool anyMask = u.Textures.Any(t => t.Role == TexRole.Gray || t.CompanionMask);
                bool srgb = u.Textures.Any(t => t.SRGB && t.Role == TexRole.Color);
                var filter = u.Textures.Select(t => t.Filter).GroupBy(f => f)
                    .OrderByDescending(g => g.Count()).First().Key;
                u.TypeGroupKey = $"n{(anyNormal ? 1 : 0)}_m{(anyMask ? 1 : 0)}_s{(srgb ? 1 : 0)}_f{(int)filter}";

                foreach (var m in u.Mappings)
                    foreach (var isl in ctx.Islands[m])
                    {
                        u.Islands.Add(isl);
                        u.RasterArea += (long)isl.RasterSize.x * isl.RasterSize.y;
                    }
                ctx.PackUnits.Add(u);
            }
            AtoLog.Info($"pack units: {ctx.PackUnits.Count} " +
                        $"({ctx.PackUnits.Sum(u => u.Textures.Count)} textures, {ctx.PackUnits.Sum(u => u.Islands.Count)} islands)");
        }

        // ---- candidate pool / 候选图集池 ----
        internal static List<Vector2Int> CandidatePool(AtoContext ctx)
        {
            int maxEdge = ctx.MaxAtlasSize;
            var sizes = new List<int>();
            if (ctx.Settings.experimentalNpot)
                for (int s = 64; s <= maxEdge; s += 64) sizes.Add(s);
            else
                for (int s = 64; s <= maxEdge; s <<= 1) sizes.Add(s);

            var pool = new List<Vector2Int>();
            foreach (var w in sizes)
                foreach (var h in sizes)
                    pool.Add(new Vector2Int(w, h));
            // area asc, then aspect (long/short) asc: closest-to-square first / 面积升序、越接近正方形越优先
            pool.Sort((a, b) =>
            {
                long areaA = (long)a.x * a.y, areaB = (long)b.x * b.y;
                if (areaA != areaB) return areaA.CompareTo(areaB);
                float aspectA = Mathf.Max(a.x, a.y) / (float)Mathf.Min(a.x, a.y);
                float aspectB = Mathf.Max(b.x, b.y) / (float)Mathf.Min(b.x, b.y);
                return aspectA.CompareTo(aspectB);
            });
            return pool;
        }

        internal static int PaddingFor(AtoContext ctx, Vector2Int atlas)
        {
            int pad = Mathf.CeilToInt(Mathf.Max(atlas.x, atlas.y) / 128f);
            pad = Mathf.Max(pad, 4);
            pad = Mathf.Max(pad, (int)ctx.Settings.minPadding);
            return pad;
        }

        // ---- pack one atlas / 装一张图集 ----
        private static int PackOneAtlas(AtoContext ctx, List<PackUnit> remaining, int atlasIndex,
            string typeKey, out List<PackUnit> spilled)
        {
            long totalArea = remaining.Sum(u => u.RasterArea);
            var pool = CandidatePool(ctx).Where(c => (long)c.x * c.y >= totalArea).ToList();
            bool allowRotate = !typeKey.StartsWith("n1"); // normal maps: no rotation (tangent safety) / 法线不旋转

            // try to fit ALL units / 先尝试整队列装入
            foreach (var cand in pool)
            {
                if (TryPack(ctx, remaining, cand, atlasIndex, allowRotate, commit: true))
                {
                    spilled = new List<PackUnit>();
                    return remaining.Count;
                }
            }

            // fall back: greedy fill max atlas, spill the rest / 兜底：最大图集贪心装填，剩余溢出
            var max = new Vector2Int(ctx.MaxAtlasSize, ctx.MaxAtlasSize);
            var packer = new GridPacker(max, PaddingFor(ctx, max));
            spilled = new List<PackUnit>();
            int packed = 0;
            foreach (var u in remaining)
            {
                if (packer.TryPlaceUnit(ctx, u, atlasIndex, allowRotate)) packed++;
                else spilled.Add(u);
            }
            if (packed > 0) AtoLog.Debugf($"atlas #{atlasIndex}: greedy packed {packed}, spilled {spilled.Count}");
            return packed;
        }

        private static bool TryPack(AtoContext ctx, List<PackUnit> units, Vector2Int size,
            int atlasIndex, bool allowRotate, bool commit)
        {
            var packer = new GridPacker(size, PaddingFor(ctx, size));
            foreach (var u in units)
                if (!packer.TryPlaceUnit(ctx, u, atlasIndex, allowRotate))
                {
                    // rollback placements on failure / 失败回滚
                    foreach (var uu in units)
                        foreach (var isl in uu.Islands)
                            if (isl.PlacedAtlas == atlasIndex) isl.PlacedAtlas = -1;
                    return false;
                }
            if (commit)
                foreach (var u in units)
                {
                    foreach (var t in u.Textures) t.AtlasIndex = atlasIndex;
                    u.AtlasSize = size;
                }
            return true;
        }
    }

    /// <summary>Bitmask grid packer with Burst BLF scan. / Burst BLF 位掩码装箱器。</summary>
    public sealed class GridPacker
    {
        private readonly int _w, _h, _stride, _padCells;
        private readonly Vector2Int _size;
        private ulong[] _grid;

        public GridPacker(Vector2Int size, int paddingPx)
        {
            _size = size;
            _w = size.x / PackStage.Cell;
            _h = size.y / PackStage.Cell;
            _stride = (_w + 63) >> 6;
            _grid = new ulong[_stride * _h];
            _padCells = Mathf.CeilToInt(paddingPx / 2f / PackStage.Cell);
        }

        public bool TryPlaceUnit(AtoContext ctx, PackUnit unit, int atlasIndex, bool allowRotate)
        {
            // atomic: all islands of the unit or none / 原子操作：整单元全放或全不放
            var placed = new List<(Island isl, BitGrid stamped)>();
            // big islands first inside unit / 单元内大岛优先
            foreach (var isl in unit.Islands.OrderByDescending(i => (long)i.RasterSize.x * i.RasterSize.y))
            {
                if (!TryPlaceIsland(ctx, isl, atlasIndex, allowRotate, out var stamped))
                {
                    foreach (var (p, g) in placed) { Unstamp(g, p.PlacePos.x / PackStage.Cell, p.PlacePos.y / PackStage.Cell); p.PlacedAtlas = -1; }
                    return false;
                }
                placed.Add((isl, stamped));
            }
            foreach (var t in unit.Textures) t.AtlasIndex = atlasIndex;
            unit.AtlasSize = _size;
            return true;
        }

        private bool TryPlaceIsland(AtoContext ctx, Island isl, int atlasIndex, bool allowRotate, out BitGrid stampedGrid)
        {
            stampedGrid = null;
            var mask = RasterMask(ctx, isl);
            var pos = FindBLF(mask.grid, mask.w, mask.h);
            BitGrid rotated = null; (int x, int y) posR = (-1, -1);
            if (allowRotate)
            {
                rotated = mask.grid.Transpose();
                posR = FindBLF(rotated, mask.h, mask.w);
            }

            bool useRot = false;
            (int x, int y) chosen = pos;
            if (pos.y < 0 && posR.y < 0) return false;
            if (pos.y < 0 || (posR.y >= 0 && (posR.y < pos.y || (posR.y == pos.y && posR.x < pos.x))))
            { chosen = posR; useRot = true; }

            var g = useRot ? rotated : mask.grid;
            Stamp(g, chosen.x, chosen.y);
            stampedGrid = g;
            isl.PlacedAtlas = atlasIndex;
            isl.Rotated = useRot;
            isl.PlacePos = new Vector2Int(chosen.x * PackStage.Cell, chosen.y * PackStage.Cell);
            return true;
        }

        private (BitGrid grid, int w, int h) RasterMask(AtoContext ctx, Island isl)
        {
            int cw = Mathf.Max(1, Mathf.CeilToInt(isl.RasterSize.x / (float)PackStage.Cell));
            int ch = Mathf.Max(1, Mathf.CeilToInt(isl.RasterSize.y / (float)PackStage.Cell));
            var data = IslandStage.UvCache[isl.Key];
            var g = Raster.RasterizeIsland(isl, data.Uv, data.Indices, cw, ch);
            g = g.Dilate(_padCells);
            return (g, cw, ch);
        }

        private void Unstamp(BitGrid mask, int px, int py)
        {
            for (int y = 0; y < mask.H; y++)
                for (int x = 0; x < mask.W; x++)
                    if (mask.Get(x, y)) ClearCell(px + x, py + y);
        }

        private void Stamp(BitGrid mask, int px, int py)
        {
            for (int y = 0; y < mask.H; y++)
                for (int x = 0; x < mask.W; x++)
                    if (mask.Get(x, y)) SetCell(px + x, py + y);
        }

        private void SetCell(int x, int y)
        {
            if ((uint)x >= (uint)_w || (uint)y >= (uint)_h) return;
            _grid[y * _stride + (x >> 6)] |= 1UL << (x & 63);
        }

        private void ClearCell(int x, int y)
        {
            if ((uint)x >= (uint)_w || (uint)y >= (uint)_h) return;
            _grid[y * _stride + (x >> 6)] &= ~(1UL << (x & 63));
        }

        /// <summary>Bottom-left-first full scan via Burst job. / Burst 全扫描 BLF。</summary>
        private (int x, int y) FindBLF(BitGrid mask, int mw, int mh)
        {
            if (mw > _w || mh > _h) return (-1, -1);
            var atlasArr = new NativeArray<ulong>(_grid, Allocator.TempJob);
            var maskArr = new NativeArray<ulong>(mask.Rows, Allocator.TempJob);
            var result = new NativeArray<int>(2, Allocator.TempJob);
            var job = new BlfScanJob
            {
                Atlas = atlasArr, AtlasW = _w, AtlasH = _h, AtlasStride = _stride,
                Mask = maskArr, MaskW = mw, MaskH = mh, MaskStride = mask.Stride,
                Result = result
            };
            job.Run();
            var r = (result[0], result[1]);
            atlasArr.Dispose(); maskArr.Dispose(); result.Dispose();
            return r;
        }
    }

    [BurstCompile(CompileSynchronously = false)]
    public struct BlfScanJob : IJob
    {
        [ReadOnly] public NativeArray<ulong> Atlas;
        public int AtlasW, AtlasH, AtlasStride;
        [ReadOnly] public NativeArray<ulong> Mask;
        public int MaskW, MaskH, MaskStride;
        public NativeArray<int> Result; // x,y or -1,-1

        public void Execute()
        {
            Result[0] = -1; Result[1] = -1;
            int maxY = AtlasH - MaskH, maxX = AtlasW - MaskW;
            for (int y = 0; y <= maxY; y++)
            {
                for (int x = 0; x <= maxX; x++)
                {
                    if (Fits(x, y)) { Result[0] = x; Result[1] = y; return; }
                }
            }
        }

        private bool Fits(int px, int py)
        {
            for (int my = 0; my < MaskH; my++)
            {
                int ay = py + my;
                for (int mw = 0; mw < MaskStride; mw++)
                {
                    ulong bits = Mask[my * MaskStride + mw];
                    if (bits == 0) continue;
                    int baseBit = px + (mw << 6);
                    int word = baseBit >> 6, shift = baseBit & 63;
                    ulong a = Atlas[ay * AtlasStride + word] >> shift;
                    if (shift != 0 && word + 1 < AtlasStride)
                        a |= Atlas[ay * AtlasStride + word + 1] << (64 - shift);
                    if ((a & bits) != 0) return false;
                }
            }
            return true;
        }
    }
}
