using System;
using System.Collections.Generic;
using NetFosa.AvatarTextureOptimizer.Editor.Analysis;
using NetFosa.AvatarTextureOptimizer.Editor.Logging;
using NetFosa.AvatarTextureOptimizer.Editor.UV;
using UnityEngine;
using NetFosa.AvatarTextureOptimizer;

namespace NetFosa.AvatarTextureOptimizer.Editor.Atlas
{
    /// <summary>一次装箱的单个岛放置结果。</summary>
    public struct PackPlacement
    {
        public UvIsland island;
        public int cellX;
        public int cellY;
        public bool rotated;
        public int maskW; // 放置的格尺寸（含旋转）
        public int maskH;
        public int cells; // 光栅化占用格数（利用率统计）
    }

    /// <summary>装箱输出：一个成品图集。</summary>
    public sealed class AtlasResult
    {
        public int width;
        public int height;
        public List<PackPlacement> placements = new List<PackPlacement>();
        public long usedCells;
        public long totalCells;
        public List<string> sources = new List<string>(); // 贴图来源名
        public ATOTextureCategory category;
        public ATOColorSpace colorSpace;
        public TextureTypeGroup typeGroup;
        /// <summary>岛 → 来源贴图（写出时使用）。</summary>
        public Dictionary<UvIsland, TextureInfo> islandTextures = new Dictionary<UvIsland, TextureInfo>();
    }

    /// <summary>
    /// 装箱器（BLF）：
    /// - 4px 粒度光栅位掩码（Burst 光栅化）
    /// - 全扫描 BLF + 光栅化面积降序 + 边长降序 + 90° 旋转步进（位掩码转置；法线类型组不旋转——旋转会破坏切线方向）
    /// - 贴图按光栅化总面积降序排队；队列以"单张贴图及其 UV 组"为原子操作
    /// - 候选图集池：面积升序、长宽比升序，第一个能装下全部岛的作为成品图集
    /// - 单张贴图无法装入最大图集 → 放弃其整个 UV 组的图集化（返回 fallback）
    /// </summary>
    public sealed class BinPacker
    {
        private readonly CandidatePool _pool;
        private readonly bool _useBurst;
        private readonly ATOLogger _logger;

        public BinPacker(CandidatePool pool, bool useBurst, ATOLogger logger)
        {
            _pool = pool;
            _useBurst = useBurst;
            _logger = logger;
        }

        public class QueueItem
        {
            public TextureInfo texture;
            public List<(UvIsland island, Rect uvRect)> islands = new List<(UvIsland, Rect)>();
            public double uvArea;
        }

        /// <summary>
        /// 装箱一个类型组的全部贴图（每贴图的全部岛）。返回成品图集列表；
        /// fallbackTextures 为放弃图集化的贴图（其整个 UV 组走整图缩放）。
        /// </summary>
        public List<AtlasResult> PackTypeGroup(TextureTypeGroup typeGroup,
            Dictionary<TextureInfo, List<(UvIsland island, Rect uvRect)>> islandsByTexture,
            List<TextureInfo> fallbackTextures, int minPadding)
        {
            var results = new List<AtlasResult>();
            if (islandsByTexture == null || islandsByTexture.Count == 0) return results;

            // 贴图按光栅化总面积降序
            var textures = new List<TextureInfo>(islandsByTexture.Keys);
            textures.Sort((a, b) =>
            {
                double aa = RasterizedArea(a, islandsByTexture[a]);
                double ba = RasterizedArea(b, islandsByTexture[b]);
                int c = ba.CompareTo(aa);
                return c;
            });

            // ---- 建立贴图队列 ----
            var queues = new List<List<QueueItem>>();
            foreach (var tex in textures)
            {
                var items = islandsByTexture[tex];
                if (items == null || items.Count == 0) continue;
                double area = 0;
                foreach (var (isl, rect) in items) area += rect.width * rect.height;
                if (area <= 0) continue;

                var qi = new QueueItem { texture = tex, uvArea = area };
                qi.islands.AddRange(items);

                bool placed = false;
                foreach (var q in queues)
                {
                    if (QueueCanFit(q, qi))
                    {
                        q.Add(qi);
                        placed = true;
                        break;
                    }
                }
                if (!placed)
                {
                    var nq = new List<QueueItem> { qi };
                    queues.Add(nq);
                }
            }

            // ---- 每个队列选候选图集并装箱 ----
            foreach (var queue in queues)
            {
                double totalArea = 0;
                foreach (var qi in queue) totalArea += qi.uvArea;

                AtlasResult built = null;
                foreach (var candidate in _pool.Entries)
                {
                    // 面积下限（UV 空间总量不可能超过 1 个图集）：超限直接跳过该候选
                    if (totalArea > 1.0) break;

                    var attempt = TryPack(candidate.width, candidate.height, queue, typeGroup, minPadding);
                    if (attempt != null)
                    {
                        built = new AtlasResult
                        {
                            width = candidate.width,
                            height = candidate.height,
                            placements = attempt,
                            category = CategoryFor(typeGroup),
                            colorSpace = typeGroup.colorSpace,
                            typeGroup = typeGroup,
                        };
                        foreach (var qi in queue)
                        {
                            built.sources.Add(qi.texture.texture != null ? qi.texture.texture.name : "?");
                        }
                        // 记录岛布局
                        foreach (var p in attempt)
                        {
                            p.island.rotated90 = p.rotated;
                            p.island.atlasPosUV = new Vector2(
                                p.cellX * 4f / candidate.width,
                                p.cellY * 4f / candidate.height);
                            p.island.layoutAssigned = true;
                        }
                        break;
                    }
                }

                if (built != null)
                {
                    built.totalCells = (long)(built.width / 4) * (built.height / 4);
                    long used = 0;
                    foreach (var p in built.placements) used += p.cells;
                    built.usedCells = used;
                    results.Add(built);
                }
                else
                {
                    // 队列无法装入任何候选 → 逐贴图尝试单独装入最大图集，失败的进 fallback
                    foreach (var qi in queue)
                    {
                        if (!CanFitSingleInMax(qi, typeGroup, minPadding))
                        {
                            fallbackTextures.Add(qi.texture);
                            foreach (var (isl, _) in qi.islands)
                            {
                                isl.failed = true;
                                isl.failReason = "cannot fit into the largest candidate atlas; whole-texture scale fallback";
                            }
                            _logger.Warn($"[ATO] Texture '{qi.texture.texture?.name}' cannot be atlased (too large / fragmented); using whole-texture scaling fallback.");
                        }
                        else
                        {
                            // 能单独装入最大图集 → 以最大图集为其成品（小队列）
                            var attempt = TryPack(_pool.MaxSide, _pool.MaxSide, new List<QueueItem> { qi }, typeGroup, minPadding);
                            if (attempt != null)
                            {
                                var r = new AtlasResult
                                {
                                    width = _pool.MaxSide,
                                    height = _pool.MaxSide,
                                    placements = attempt,
                                    category = CategoryFor(typeGroup),
                                    colorSpace = typeGroup.colorSpace,
                                    typeGroup = typeGroup,
                                };
                                r.sources.Add(qi.texture.texture != null ? qi.texture.texture.name : "?");
                                r.totalCells = (long)(_pool.MaxSide / 4) * (_pool.MaxSide / 4);
                                long used = 0;
                                foreach (var p in attempt) used += p.cells;
                                r.usedCells = used;
                                foreach (var p in attempt)
                                {
                                    p.island.rotated90 = p.rotated;
                                    p.island.atlasPosUV = new Vector2(p.cellX * 4f / _pool.MaxSide, p.cellY * 4f / _pool.MaxSide);
                                    p.island.layoutAssigned = true;
                                }
                                results.Add(r);
                            }
                            else
                            {
                                fallbackTextures.Add(qi.texture);
                            }
                        }
                    }
                }
            }

            return results;
        }

        private static ATOTextureCategory CategoryFor(TextureTypeGroup tg)
        {
            switch (tg.baseKind)
            {
                case ATOUsageKind.Normal: return ATOTextureCategory.Normal;
                case ATOUsageKind.GrayMask: return ATOTextureCategory.GrayMask;
                case ATOUsageKind.MainAlpha: return ATOTextureCategory.MainTransparent;
                default: return ATOTextureCategory.MainOpaque;
            }
        }

        private static double RasterizedArea(TextureInfo tex, List<(UvIsland island, Rect uvRect)> islands)
        {
            double area = 0;
            foreach (var (_, rect) in islands) area += rect.width * rect.height;
            return area;
        }

        private static bool QueueCanFit(List<QueueItem> q, QueueItem item)
        {
            double total = item.uvArea;
            foreach (var qi in q) total += qi.uvArea;
            return total <= 0.95;
        }

        // 掩码缓存：key = (island, atlasW, atlasH)
        private readonly Dictionary<(UvIsland, int, int), RasterMask> _maskCache = new Dictionary<(UvIsland, int, int), RasterMask>();

        private RasterMask GetMask(UvIsland island, Rect uvRect, int atlasW, int atlasH, int padCells, bool includePad)
        {
            var key = (island, atlasW, atlasH);
            if (!_maskCache.TryGetValue(key, out var mask))
            {
                var uvArray = UV.UvIslandExtractor.GetUvArray(island.group.mesh, island.group.uvChannel);
                var slotTris = island.group.mesh.GetTriangles(island.group.slotIndex);
                // 内容尺寸 = rectUV × 图集尺寸（与最终写出内容一致）
                int contentW = Math.Max(1, Mathf.RoundToInt(uvRect.width * atlasW));
                int contentH = Math.Max(1, Mathf.RoundToInt(uvRect.height * atlasH));
                mask = RasterMask.RasterizeIsland(island.triangleIndices, uvArray, slotTris, island.uvBounds,
                    island.normalizedOffset, contentW, contentH, atlasW, atlasH, _useBurst);
                _maskCache[key] = mask;
            }
            // 注意：缓存的一律为原始形状掩码；padding 膨胀每次现算，避免缓存污染
            if (includePad && padCells > 0) return Dilate(mask, padCells);
            return mask;
        }

        /// <summary>膨胀掩码（向四周扩 padCells 格，用于 padding 预留）。</summary>
        private static RasterMask Dilate(RasterMask mask, int padCells)
        {
            int gw = mask.GridW, gh = mask.GridH;
            var result = new RasterMask(gw, gh);
            for (int y = 0; y < gh; y++)
            {
                for (int x = 0; x < gw; x++)
                {
                    if (!mask.GetCell(x, y)) continue;
                    for (int dy = -padCells; dy <= padCells; dy++)
                    {
                        int ny = y + dy;
                        if (ny < 0 || ny >= gh) continue;
                        for (int dx = -padCells; dx <= padCells; dx++)
                        {
                            int nx = x + dx;
                            if (nx < 0 || nx >= gw) continue;
                            result.SetCellRawPublic(nx, ny);
                        }
                    }
                }
            }
            return result;
        }

        private bool CanFitSingleInMax(QueueItem qi, TextureTypeGroup typeGroup, int minPadding)
        {
            return TryPack(_pool.MaxSide, _pool.MaxSide, new List<QueueItem> { qi }, typeGroup, minPadding) != null;
        }

        private static bool FitsAt(RasterMask grid, RasterMask mask, int x, int y)
        {
            for (int yy = 0; yy < mask.GridH; yy++)
            {
                int ay = y + yy;
                if (ay < 0 || ay >= grid.GridH) return false;
                for (int xx = 0; xx < mask.GridW; xx++)
                {
                    if (!mask.GetCell(xx, yy)) continue;
                    if (grid.GetCell(x + xx, ay)) return false;
                }
            }
            return true;
        }

        /// <summary>
        /// 尝试把队列全部贴图装入候选图集。成功返回放置列表，失败返回 null。
        /// 原子操作：单张贴图及其 UV 组的所有岛必须一次性装入。
        /// </summary>
        private List<PackPlacement> TryPack(int atlasW, int atlasH, List<QueueItem> queue, TextureTypeGroup typeGroup, int minPadding)
        {
            int padPx = CandidatePool.ComputePadding(Math.Max(atlasW, atlasH), minPadding);
            int padCells = Math.Max(1, padPx / 4);

            var grid = new RasterMask(atlasW / 4, atlasH / 4);
            var placements = new List<PackPlacement>();
            // 旋转仅允许：非法线类型组 + 正方形图集（非正方形图集下 UV 旋转与像素旋转不一致）
            bool allowRotate = typeGroup.baseKind != ATOUsageKind.Normal && atlasW == atlasH;

            // 全部岛排序：光栅化面积降序、边长降序
            var allItems = new List<(UvIsland island, Rect rect)>();
            foreach (var qi in queue)
                foreach (var (isl, rect) in qi.islands)
                    allItems.Add((isl, rect));

            allItems.Sort((a, b) =>
            {
                double aa = a.rect.width * a.rect.height;
                double ba = b.rect.width * b.rect.height;
                int c = ba.CompareTo(aa);
                if (c != 0) return c;
                float al = Mathf.Max(a.rect.width, a.rect.height);
                float bl = Mathf.Max(b.rect.width, b.rect.height);
                return bl.CompareTo(al);
            });

            foreach (var (island, rect) in allItems)
            {
                var mask = GetMask(island, rect, atlasW, atlasH, padCells, true);
                bool placed = false;

                // 旋转规则：仅当该岛所属 UV 组内没有任何法线贴图时允许旋转
                // （网格 UV 是组内共用的；旋转会改变采样方向，法线组不旋转，因此整组不得旋转）
                bool islandCanRotate = allowRotate && !GroupContainsNormal(island);
                var rotations = new List<(RasterMask m, bool rot)>();
                if (island.layoutAssigned)
                {
                    // 固定位置：必须与已分配（主图集）的旋转一致，否则网格 UV 与内容不匹配
                    if (island.rotated90)
                        rotations.Add((mask.Transposed(), true));
                    else
                        rotations.Add((mask, false));
                }
                else
                {
                    rotations.Add((mask, false));
                    if (islandCanRotate) rotations.Add((mask.Transposed(), true));
                }

                foreach (var (m, rot) in rotations)
                {
                    if (island.layoutAssigned)
                    {
                        // 固定位置（UV 组跨图集一致性）：按已分配 UV 位置换算格坐标
                        int cx = Mathf.RoundToInt(island.atlasPosUV.x * atlasW / 4f);
                        int cy = Mathf.RoundToInt(island.atlasPosUV.y * atlasH / 4f);
                        if (FitsAt(grid, m, cx, cy))
                        {
                            grid.TryPlace(m, cx, cy);
                            var raw = GetMask(island, rect, atlasW, atlasH, padCells, false);
                            placements.Add(new PackPlacement { island = island, cellX = cx, cellY = cy, rotated = rot, maskW = m.GridW, maskH = m.GridH, cells = raw.OccupiedCells() });
                            placed = true;
                            break;
                        }
                        continue;
                    }

                    for (int y = 0; y <= grid.GridH - m.GridH && !placed; y++)
                    {
                        for (int x = 0; x <= grid.GridW - m.GridW && !placed; x++)
                        {
                            if (FitsAt(grid, m, x, y))
                            {
                                grid.TryPlace(m, x, y);
                                var raw = GetMask(island, rect, atlasW, atlasH, padCells, false);
                                placements.Add(new PackPlacement { island = island, cellX = x, cellY = y, rotated = rot, maskW = m.GridW, maskH = m.GridH, cells = raw.OccupiedCells() });
                                placed = true;
                            }
                        }
                    }
                    if (placed) break;
                }

                if (!placed) return null; // 该候选失败
            }

            return placements;
        }

        private static bool GroupContainsNormal(UvIsland island)
        {
            if (island.group == null) return false;
            foreach (var t in island.group.textures)
            {
                if (t.info == null) continue;
                if (t.info.typeGroup != null && t.info.typeGroup.baseKind == ATOUsageKind.Normal) return true;
                if (t.info.usages != null)
                {
                    foreach (var u in t.info.usages)
                    {
                        if (u.kind == ATOUsageKind.Normal) return true;
                    }
                }
            }
            return false;
        }
    }
}
