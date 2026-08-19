// ATO — Avatar Texture Optimizer
// BLF atlas packer. Packs each texture type group's UV groups into one or more atlases
// using bitmask rasterization (4px cells) and a full-scan bottom-left-fill placement with
// 90° rotation steps (disabled for normal maps — tangent data is never recomputed).
// BLF 图集装箱器。用位掩码光栅化（4px 单元）与全扫描 bottom-left-fill 放置、90° 旋转步进
// （法线贴图禁用旋转——绝不重算法线切线数据）把各贴图类型组的 UV 组装入一个或多个图集。
//
// Packing strategy (CLAUDE.md #16): queue units by rasterized area desc; discard candidates
// smaller than the queue's total rasterized area; try candidates ascending by area (nearest
// square first); a unit that cannot fit the remaining space of the largest atlas is moved to
// a new queue; a unit that cannot fit an empty largest atlas is dropped from atlasing.
// 装箱策略（CLAUDE.md #16）：按光栅化面积降序排队；丢弃小于队列总面积需求的候选；
// 按面积升序（越接近正方形越优先）尝试候选；装不下最大图集剩余空间的单元另开新队列；
// 单独都装不进空的最大图集的单元放弃图集化。

using System.Collections.Generic;
using UnityEngine;
using net.fosa.ato;

namespace net.fosa.ato.editor
{
    /// <summary>
    /// One packable unit: a UV group (single texture + its UV group) with a sort proxy.
    /// 一个可装箱单元：一个 UV 组（单张贴图 + 其 UV 组），带排序代理面积。
    /// </summary>
    public class ATOPackedUnit
    {
        public ATOUVGroup group;
        public double areaProxy;
    }

    /// <summary>
    /// A completed queue pack: the units, their placement layout and the atlas size.
    /// 一个完成的队列装箱：单元、放置布局与图集尺寸。
    /// </summary>
    public class ATOPackResult
    {
        public List<ATOUVGroup> units = new List<ATOUVGroup>();
        public List<ATOPackedIsland> layout = new List<ATOPackedIsland>();
        public int size;
    }

    /// <summary>
    /// The BLF atlas packer. BLF 图集装箱器。
    /// </summary>
    public static class AtlasPacker
    {
        /// <summary>
        /// Pack all texture type groups into queue results. Dropped UV groups are returned.
        /// 把所有贴图类型组装箱为队列结果，返回被放弃图集化的 UV 组。
        /// </summary>
        public static List<ATOPackResult> Pack(ATOBuildContext bc, ATOAnalysisResult result, List<ATOUVGroup> dropped)
        {
            var results = new List<ATOPackResult>();
            int maxEdge = AtlasPool.MaxEdgeFor(bc.Platform);

            foreach (var typeGroup in result.typeGroups)
            {
                PackTypeGroup(bc, result, typeGroup, maxEdge, results, dropped);
            }
            return results;
        }

        private static void PackTypeGroup(ATOBuildContext bc, ATOAnalysisResult result, ATOTextureTypeGroup typeGroup,
            int maxEdge, List<ATOPackResult> results, List<ATOUVGroup> dropped)
        {
            // Build units: UV groups whose main color usage belongs to this type group.
            // 构建单元：主色用途属于该类型组的 UV 组。
            var units = new List<ATOPackedUnit>();
            foreach (var usage in typeGroup.colorUsages)
            {
                var group = FindGroup(result, usage.renderer, usage.slotIndex, usage.uvChannel);
                if (group == null || group.whitelisted) continue;
                if (units.Exists(u => u.group == group)) continue;
                units.Add(new ATOPackedUnit { group = group, areaProxy = AreaProxy(group) });
            }
            if (units.Count == 0) return;

            units.Sort((a, b) => b.areaProxy.CompareTo(a.areaProxy));

            int padding = AtlasPool.EffectivePadding(maxEdge, result.settings.islandPadding);

            // Quality was evaluated against each island's SOURCE texture resolution; to keep the
            // evaluated density, the atlas edge must be at least the largest source texture edge.
            // 质量是以各岛的源贴图分辨率为基准评估的；为保持评估密度，图集边长必须 ≥ 最大源贴图边长。
            int minSourceEdge = MaxSourceEdge(units);

            // Queues (split) processing. 队列（拆分）处理。
            var remaining = new List<ATOPackedUnit>(units);
            while (remaining.Count > 0)
            {
                bc.ThrowIfCancelled();
                // Try the smallest candidate that fits ALL remaining units. 先试能装下全部单元的最小候选。
                int chosen = -1;
                var candidates = AtlasPool.Candidates(maxEdge, result.settings.npotAtlas);
                candidates.RemoveAll(s => s < minSourceEdge);
                foreach (var size in candidates)
                {
                    if (TryPackAll(bc, remaining, size, padding, out var layout))
                    {
                        chosen = size;
                        results.Add(new ATOPackResult { units = Groups(remaining), layout = layout, size = size });
                        remaining.Clear();
                        break;
                    }
                }
                if (chosen != -1) continue;

                // No candidate fits all → greedily fill the largest, move leftovers to a new queue.
                // 没有候选能装下全部 → 用最大尺寸贪心填充，剩余单元进入新队列。
                var (packed, leftover, layout) = GreedyPack(bc, remaining, maxEdge, padding);
                if (packed.Count == 0)
                {
                    // First unit alone cannot fit → drop the whole UV group from atlasing.
                    // 首个单元单独也装不下 → 放弃该 UV 组的图集化。
                    var unit = remaining[0];
                    remaining.RemoveAt(0);
                    dropped.Add(unit.group);
                    ATOLog.Warn(ATOI18n.T(ATOI18nKeys.WarnCannotFitAtlas, FirstTextureName(unit.group)));
                    continue;
                }
                results.Add(new ATOPackResult { units = Groups(packed), layout = layout, size = maxEdge });
                remaining = leftover;
            }
        }

        private static ATOUVGroup FindGroup(ATOAnalysisResult result, Renderer r, int slot, int channel)
        {
            foreach (var g in result.uvGroups)
                if (g.renderer == r && g.slotIndex == slot && g.uvChannel == channel) return g;
            return null;
        }

        private static int MaxSourceEdge(List<ATOPackedUnit> units)
        {
            int edge = 0;
            foreach (var unit in units)
            foreach (var u in unit.group.usages)
            {
                if (u.texture == null) continue;
                edge = Mathf.Max(edge, u.texture.width, u.texture.height);
            }
            return Mathf.Max(1, edge);
        }

        private static double AreaProxy(ATOUVGroup group)
        {
            double area = 0;
            foreach (var island in group.islands)
                area += (double)island.uvArea * island.scaleX * island.scaleY;
            return area;
        }

        private static string FirstTextureName(ATOUVGroup group)
        {
            foreach (var u in group.usages)
                if (u.texture != null) return u.texture.name;
            return "?";
        }

        private static List<ATOUVGroup> Groups(List<ATOPackedUnit> units)
        {
            var list = new List<ATOUVGroup>();
            foreach (var u in units) list.Add(u.group);
            return list;
        }

        // ---- packing attempts -------------------------------------------------

        private static bool TryPackAll(ATOBuildContext bc, List<ATOPackedUnit> units, int size, int padding, out List<ATOPackedIsland> layout)
        {
            layout = new List<ATOPackedIsland>();
            int cells = size / 4;
            var atlas = new BitMask(cells, cells);
            var placed = new List<ATOPackedIsland>();
            foreach (var unit in units)
            {
                if (!PlaceUnit(bc, unit, size, padding, atlas, placed)) return false;
            }
            layout = placed;
            return true;
        }

        private static (List<ATOPackedUnit> packed, List<ATOPackedUnit> leftover, List<ATOPackedIsland> layout) GreedyPack(
            ATOBuildContext bc, List<ATOPackedUnit> units, int size, int padding)
        {
            var packed = new List<ATOPackedUnit>();
            var leftover = new List<ATOPackedUnit>();
            int cells = size / 4;
            var atlas = new BitMask(cells, cells);
            var placed = new List<ATOPackedIsland>();
            foreach (var unit in units)
            {
                if (PlaceUnit(bc, unit, size, padding, atlas, placed)) packed.Add(unit);
                else leftover.Add(unit);
            }
            return (packed, leftover, placed);
        }

        /// <summary>Place all islands of one unit into the atlas; false if any island does not fit. 放置一个单元的全部岛；任一放不下返回 false。</summary>
        private static bool PlaceUnit(ATOBuildContext bc, ATOPackedUnit unit, int size, int padding, BitMask atlas, List<ATOPackedIsland> placed)
        {
            bool allowRotation = !HasNormalMap(unit.group);
            int padCells = Mathf.Max(1, Mathf.CeilToInt(padding / 4f));
            var placements = new List<ATOPackedIsland>();

            foreach (var island in unit.group.islands)
            {
                if (island.scaledUV == null || island.scaledUV.Length == 0) continue;
                int gridW = Mathf.Max(1, Mathf.CeilToInt(island.bounds.width * island.scaleX * size / 4f));
                int gridH = Mathf.Max(1, Mathf.CeilToInt(island.bounds.height * island.scaleY * size / 4f));

                var key = (island, size);
                if (!bc.RasterCache.TryGetValue(key, out var mask))
                {
                    mask = IslandRasterizer.Rasterize(island, gridW, gridH);
                    bc.RasterCache[key] = mask;
                }
                mask = mask.Dilate(padCells);

                bool placedOk = false;
                int bestRot = 0, bestX = 0, bestY = 0;
                int rotCount = allowRotation ? 4 : 1;
                for (int rot = 0; rot < rotCount && !placedOk; rot++)
                {
                    var rotMask = rot == 0 ? mask : mask.Rotate(rot);
                    if (FullScanBLF(atlas, rotMask, out int ox, out int oy))
                    {
                        rotMask.BlitInto(atlas, ox, oy);
                        placedOk = true; bestRot = rot; bestX = ox; bestY = oy;
                    }
                }
                if (!placedOk) return false;

                int px = bestX * 4 + padCells * 4;
                int py = bestY * 4 + padCells * 4;
                placements.Add(new ATOPackedIsland
                {
                    island = island,
                    offset = new Vector2Int(px, py),
                    size = new Vector2Int(gridW * 4, gridH * 4),
                    rotationSteps = bestRot,
                });
            }
            placed.AddRange(placements);
            return true;
        }

        private static bool HasNormalMap(ATOUVGroup group)
        {
            foreach (var u in group.usages)
                if (u.kind == ATOTextureKind.NormalMap) return true;
            return false;
        }

        /// <summary>Full-scan bottom-left-fill: first position (bottom-left) where the mask fits. 全扫描 BLF：自左下起第一个可放位置。</summary>
        private static bool FullScanBLF(BitMask atlas, BitMask mask, out int ox, out int oy)
        {
            int maxX = atlas.Width - mask.Width;
            int maxY = atlas.Height - mask.Height;
            for (int y = 0; y <= maxY; y++)
            for (int x = 0; x <= maxX; x++)
            {
                if (!OverlapsAt(atlas, mask, x, y)) { ox = x; oy = y; return true; }
            }
            ox = oy = -1;
            return false;
        }

        private static bool OverlapsAt(BitMask atlas, BitMask mask, int ox, int oy)
        {
            for (int y = 0; y < mask.Height; y++)
            for (int x = 0; x < mask.Width; x++)
            {
                if (!mask.Get(x, y)) continue;
                if (atlas.Get(x + ox, y + oy)) return true;
            }
            return false;
        }
    }
}
