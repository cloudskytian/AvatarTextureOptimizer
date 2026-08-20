// ============================================================================
// ATO - PackStage implementation (stage 3)
// ATO - PackStage 实现（阶段 3）
//
// Per type group  每个类型组：
//   1. build one "composite" per UV group (raster at 4px in layout px,
//      derived from the group's K = pixels per UV unit);
//   2. merge composites that share a texture (all islands of one texture
//      must land on one page) into stack items;
//   3. candidate pool loop per spec: discard candidates whose area < queue
//      total; try from smallest (most-square wins ties); a partially filled
//      page is kept and the remainder starts a new page; a texture that
//      doesn't fit even the largest atlas is abandoned from atlasing
//      (whole-image path) with a warning.
// ============================================================================

#region

using System.Collections.Generic;
using System.Numerics;
using nadena.dev.ndmf;
using net.fosa.AvatarTextureOptimizer.Editor.Analysis;
using net.fosa.AvatarTextureOptimizer.Editor.Core;
using net.fosa.AvatarTextureOptimizer.Editor.Quality;
using UnityEngine;

#endregion

namespace net.fosa.AvatarTextureOptimizer.Editor.Packing
{
    public static class PackStageImpl
    {
        public static void Execute(ATOContext ctx, BuildContext context)
        {
            var c = ctx.Component;
            var log = ctx.Log;
            var an = ctx.Analysis;
            if (an == null) return;

            if (!c.GenerateAtlas)
            {
                log.Info(ATOLogMask.Packing,
                    "atlas generation disabled - no packing. 图集生成已禁用 - 不装箱。");
                return;
            }

            var pool = AtlasPool.BuildPool(c.UseNPOT);
            var result = new ATOPackResult();
            an.PackedResult = result;

            int minPadding = Mathf.Max(4, c.MinPadding);

            foreach (var tg in an.TypeGroups)
            {
                ctx.Session.Check("Pack 装箱");
                ctx.Session.SetProgress((float) (an.TypeGroups.IndexOf(tg) + 1) / an.TypeGroups.Count);
                PackTypeGroup(ctx, tg, pool, result, minPadding, log);
            }

            FinalizeIslandPlacements(an, result);

            long totalArea = 0, usedArea = 0;
            foreach (var p in result.Pages)
            {
                totalArea += (long) p.W * p.H;
                usedArea += p.UsedArea;
            }
            log.Info(ATOLogMask.Packing,
                $"pack done: {result.Pages.Count} pages ({usedArea}/{totalArea} px used, " +
                $"{(totalArea == 0 ? 0 : usedArea * 100f / totalArea):F1}% utilized), " +
                $"{result.Abandoned.Count} abandoned texture pairs. 装箱完成。");
        }

        // ------------------------------------------------------------------
        private sealed class GroupComposite
        {
            public ATOUVGroup Group;
            public BigInteger Mask;
            public int W, H; // px, multiple of 4  像素（4 的倍数）
            public int Cells;
            /// <summary>Per island: local rect in this composite (px).
            /// 每岛：本复合内的本地矩形（px）。</summary>
            public readonly Dictionary<ATOUVIsland, (int x, int y, int w, int h)> IslandRects = new();
        }

        private sealed class StackItem
        {
            public readonly List<GroupComposite> Groups = new();
            public readonly List<int> TextureIds = new();
            public BigInteger Mask;
            public int W, H, Cells;
            public BLFPacker.Placement? Placement;
            public int PageX, PageY, PageRot;
        }

        private static void PackTypeGroup(
            ATOContext ctx, ATOTexTypeGroup tg, List<ATOPoolEntry> pool,
            ATOPackResult result, int minPadding, ATOLog log)
        {
            var an = ctx.Analysis;

            // 1. composites per relevant UV group  按相关 UV 组构建复合
            var composites = new List<GroupComposite>();
            var texToGroups = new Dictionary<int, List<GroupComposite>>();

            foreach (var uvGroup in an.UVGroups)
            {
                if (!uvGroup.TypeGroupIds.Contains(tg.Id)) continue;
                if (uvGroup.Islands.Count > 0 && uvGroup.Islands[0].NoRemap) continue;
                // UV 组保持原 UV（含白名单贴图）不参与装箱
                var comp = BuildComposite(ctx, tg, uvGroup);
                if (comp == null) continue;
                composites.Add(comp);
                foreach (var island in uvGroup.Islands)
                {
                    foreach (var tid in island.SampledTextureIds)
                    {
                        if (!IsInTypeGroup(tg, tid)) continue;
                        var tref = an.Textures[tid];
                        if (tref.AtlasDisabled) continue; // whole-image path 整图路径
                        if (!texToGroups.TryGetValue(tid, out var list))
                        {
                            list = new List<GroupComposite>();
                            texToGroups[tid] = list;
                        }
                        if (!list.Contains(comp)) list.Add(comp);
                    }
                }
            }

            if (composites.Count == 0) return;

            // 2. merge composites sharing a texture (union-find)
            //    合并共享贴图的复合（并查集）
            var uf = new Dictionary<GroupComposite, GroupComposite>();
            foreach (var comp in composites) uf[comp] = comp;
            GroupComposite Root(GroupComposite g)
            {
                while (!ReferenceEquals(uf[g], g))
                {
                    uf[g] = uf[uf[g]];
                    g = uf[g];
                }
                return g;
            }
            foreach (var list in texToGroups.Values)
            {
                for (int i = 1; i < list.Count; i++)
                {
                    var ra = Root(list[0]);
                    var rb = Root(list[i]);
                    if (!ReferenceEquals(ra, rb)) uf[ra] = rb;
                }
            }

            var itemGroups = new List<List<GroupComposite>>();
            var itemByRoot = new Dictionary<GroupComposite, List<GroupComposite>>();
            foreach (var comp in composites)
            {
                var root = Root(comp);
                if (!itemByRoot.TryGetValue(root, out var list))
                {
                    list = new List<GroupComposite>();
                    itemByRoot[root] = list;
                    itemGroups.Add(list);
                }
                list.Add(comp);
            }

            // 3. packing loop per spec:
            //    - queue sorted by rasterized total area desc (side desc ties)
            //    - padding = max(minPadding, ceil(maxEdge/128), 4)
            //    - discard candidates whose area < queue total
            //    - first candidate that fits the WHOLE queue wins
            //    - if even the largest can't fit all: what fits stays, the
            //      rest opens a new page; a single texture that fits nothing
            //      is abandoned (whole-image path) with a warning.
            //    按规范装箱：队列面积降序（边长降序次级）；padding =
            //    max(最小值, ceil(最大边/128), 4)；丢弃面积小于队列总量的候
            //    选；首个能装下整个队列的候选胜出；最大图集装不下全部时已装
            //    部分保留、剩余开新页；单贴图装不进任何图集则放弃图集化
            //    （改整图缩放）并告警。
            // initial queue: items sorted by rasterized total area desc
            // (tie: side desc); pad value doesn't affect cell counts.
            // 初始队列：条目按光栅化总面积降序（平手边长降序）；间距不影响
            // 单元数。
            var queue = new List<StackItem>(BuildStackItems(itemGroups, 4));
            int itemTotal = queue.Count;
            int pageIndexCounter = 0;
            while (queue.Count > 0)
            {
                ctx.Session.Check("Pack 装箱");
                ctx.Session.SetProgress(1f - (float) queue.Count / itemTotal);

                // rasterized total area (cells * 16 px^2)  光栅化总面积
                long queueArea = 0;
                foreach (var it in queue) queueArea += (long) it.Cells * 16;

                // discard candidates smaller than the queue total; sort:
                // area asc, most square first  丢弃小于队列总量的候选
                var candidates = new List<ATOPoolEntry>();
                foreach (var cand in pool)
                {
                    if (cand.Area >= queueArea) candidates.Add(cand);
                }
                if (candidates.Count == 0) candidates.Add(pool[pool.Count - 1]);

                ATOPoolEntry winner = null;
                List<StackItem> winnerPlaced = null;
                var winnerItems = new List<StackItem>();
                foreach (var cand in candidates)
                {
                    int pad = PaddingFor(cand, minPadding);
                    var candItems = BuildStackItems(itemGroups, pad);
                    // align queue items to the rebuilt item instances
                    // 将队列条目对齐到重建的条目实例
                    var (placed, _) = TryPackQueue(RebuildQueue(queue, candItems), cand, pad);
                    if (placed.Count == queue.Count)
                    {
                        // first candidate that holds the whole queue wins
                        // 首个装下整个队列的候选胜出
                        winner = cand;
                        winnerPlaced = placed;
                        winnerItems = candItems;
                        break;
                    }
                }

                if (winner != null)
                {
                    CreatePage(result, tg, winner, winnerPlaced, winnerItems,
                        PaddingFor(winner, minPadding), pageIndexCounter++);
                    queue.Clear();
                    continue;
                }

                // even the largest candidate can't hold everything
                // 最大候选也装不下全部
                var largest = pool[pool.Count - 1];
                int padL = PaddingFor(largest, minPadding);
                var largestItems = BuildStackItems(itemGroups, padL);
                var (placedL, remainingL) = TryPackQueue(RebuildQueue(queue, largestItems), largest, padL);
                if (placedL.Count == 0)
                {
                    // single texture doesn't fit even the largest atlas ->
                    // abandon atlasing for its UV group 单贴图装不进最大图集
                    var first = queue[0];
                    foreach (var g in first.Groups)
                    {
                        foreach (var tid in g.Group.TextureIds)
                        {
                            if (IsInTypeGroup(tg, tid)) result.Abandoned.Add((g.Group.Id, tid));
                        }
                    }
                    log.Warn(ATOLogMask.Packing,
                        $"UV group #{first.Groups[0].Group.Id} doesn't fit even the largest " +
                        $"candidate ({largest.W}x{largest.H}) - atlasing abandoned for its textures " +
                        "(whole-image scaling applied instead). 最大候选图集装不下，放弃图集化（改整图缩放）。");
                    queue.RemoveAt(0);
                    continue;
                }
                CreatePage(result, tg, largest, placedL, largestItems, padL, pageIndexCounter++);
                queue = remainingL;
            }
        }

        /// <summary>padding = max(minPadding, ceil(maxEdge/128), 4).
        /// 按规范计算 padding。</summary>
        private static int PaddingFor(ATOPoolEntry cand, int minPadding)
        {
            int edge = Math.Max(cand.W, cand.H);
            return Math.Max(Math.Max(minPadding, 4), (edge + 127) / 128);
        }

        /// <summary>Builds stack items with a specific inter-item padding.
        /// 以指定间距构建堆叠条目。</summary>
        private static List<StackItem> BuildStackItems(List<List<GroupComposite>> itemGroups, int pad)
        {
            var result = new List<StackItem>();
            foreach (var groupList in itemGroups)
            {
                var item = new StackItem();
                foreach (var g in groupList)
                {
                    item.Groups.Add(g);
                    foreach (var tid in g.Group.TextureIds)
                    {
                        if (!item.TextureIds.Contains(tid)) item.TextureIds.Add(tid);
                    }
                }
                int cursorY = 0;
                foreach (var g in groupList)
                {
                    OrMask(item, g.Mask, g.W / 4, g.H / 4, 0, cursorY / 4,
                        ref item.Mask, ref item.W, ref item.H, ref item.Cells);
                    cursorY += g.H + pad;
                }
                if (groupList.Count > 0) item.H = cursorY - pad;
                item.W = Math.Max(item.W, 4);
                item.H = Math.Max(item.H, 4);
                result.Add(item);
            }
            result.Sort((a, b) =>
            {
                int c = b.Cells.CompareTo(a.Cells);
                if (c != 0) return c;
                return (b.W + b.H).CompareTo(a.W + a.H);
            });
            return result;
        }

        /// <summary>Maps the current queue (old instances) onto the rebuilt
        /// item instances (same group lists, new masks).
        /// 将当前队列（旧实例）映射到重建的条目实例（同组合、新掩码）。</summary>
        private static List<StackItem> RebuildQueue(List<StackItem> queue, List<StackItem> built)
        {
            // match by the first group reference  以首个组引用匹配
            var map = new Dictionary<GroupComposite, StackItem>();
            foreach (var b in built)
            {
                if (b.Groups.Count > 0 && !map.ContainsKey(b.Groups[0])) map[b.Groups[0]] = b;
            }
            var result = new List<StackItem>();
            foreach (var q in queue)
            {
                if (q.Groups.Count > 0 && map.TryGetValue(q.Groups[0], out var b))
                {
                    result.Add(b);
                }
            }
            return result;
        }

        // ------------------------------------------------------------------
        private static (List<StackItem> placed, List<StackItem> remaining) TryPackQueue(
            List<StackItem> queue, ATOPoolEntry cand, int pad)
        {
            int pageW = cand.W, pageH = cand.H;
            var occupied = new bool[(pageW / 4) * (pageH / 4)];
            MarkBorder(occupied, pageW / 4, pageH / 4, Math.Max(1, (pad + 3) / 4));

            var placed = new List<StackItem>();
            var remaining = new List<StackItem>();
            foreach (var it in queue)
            {
                var p = BLFPacker.TryPlace(pageW, pageH, occupied, pageW / 4, pageH / 4,
                    it.Mask, it.W, it.H, it.Cells, out _);
                if (p == null)
                {
                    remaining.Add(it);
                }
                else
                {
                    it.Placement = p;
                    placed.Add(it);
                }
            }
            return (placed, remaining);
        }

        private static void CreatePage(
            ATOPackResult result, ATOTexTypeGroup tg, ATOPoolEntry cand,
            List<StackItem> placed, List<StackItem> items, int pad, int index)
        {
            var page = new ATOPackedPage
            {
                TypeGroupId = tg.Id,
                W = cand.W,
                H = cand.H,
            };
            long used = 0;
            foreach (var it in placed)
            {
                var pl = it.Placement.Value;
                it.PageX = pl.X;
                it.PageY = pl.Y;
                it.PageRot = pl.Rot90;
                var packed = new ATOPackedItem
                {
                    X = pl.X,
                    Y = pl.Y,
                    Rot90 = pl.Rot90,
                    MaskW = pl.Rot90 == 1 ? it.H : it.W,
                    MaskH = pl.Rot90 == 1 ? it.W : it.H,
                };
                int subY = 0;
                foreach (var g in it.Groups)
                {
                    packed.UVGroups.Add(g.Group);
                    packed.SubItems.Add((g.Group, 0, subY));
                    page.IslandCount += g.IslandRects.Count;
                    subY += g.H + pad;
                }
                page.Items.Add(packed);
                used += (long) it.W * it.H;
            }
            page.UsedArea = used;
            result.Pages.Add(page);
            tg.PageW = cand.W;
            tg.PageH = cand.H;
        }

        // ------------------------------------------------------------------
        /// <summary>Maps packed items back to per-island atlas rects.
        /// 将已装条目映射回每岛的图集矩形。</summary>
        public static void FinalizeIslandPlacements(ATOAnalysis an, ATOPackResult result)
        {
            for (int pi = 0; pi < result.Pages.Count; pi++)
            {
                var page = result.Pages[pi];
                foreach (var item in page.Items)
                {
                    int itemW = item.MaskW, itemH = item.MaskH; // page-space dims 页内尺寸
                    foreach (var (group, lx0, ly0) in item.SubItems)
                    {
                        foreach (var (island, (gx, gy, w, h)) in group.IslandRects)
                        {
                            int px, py, rw, rh;
                            if (item.Rot90 == 1)
                            {
                                // 90°: item local (lx,ly,w,h) in (W,H) ->
                                // (H - ly - h, lx, h, w) in (H,W)
                                // 90° 旋转映射
                                px = itemH - ly0 - gy - h;
                                py = lx0 + gx;
                                rw = h;
                                rh = w;
                            }
                            else
                            {
                                px = lx0 + gx;
                                py = ly0 + gy;
                                rw = w;
                                rh = h;
                            }
                            island.AtlasPage = pi;
                            island.AtlasPos = new Vector2(item.X + px, item.Y + py);
                            island.AtlasW = rw;
                            island.AtlasH = rh;
                            island.Rot90 = item.Rot90;
                        }
                    }
                }
            }
        }

        // ------------------------------------------------------------------
        /// <summary>Builds the 4px raster composite of one UV group in layout
        /// px. 构建单个 UV 组在布局像素下的 4px 光栅复合。</summary>
        private static GroupComposite BuildComposite(ATOContext ctx, ATOTexTypeGroup tg, ATOUVGroup uvGroup)
        {
            var an = ctx.Analysis;
            float kx = uvGroup.LayoutKx;
            float ky = uvGroup.LayoutKy;
            if (kx <= 0f) kx = 1f;
            if (ky <= 0f) ky = 1f;

            var comp = new GroupComposite { Group = uvGroup };
            int maxX = 0, maxY = 0;
            var rectList = new List<(ATOUVIsland island, int x, int y, int w, int h)>();

            foreach (var island in uvGroup.Islands)
            {
                bool hasRelevant = false;
                foreach (var tid in island.SampledTextureIds)
                {
                    if (!IsInTypeGroup(tg, tid)) continue;
                    if (an.Textures[tid].AtlasDisabled) continue;
                    hasRelevant = true;
                    break;
                }
                if (!hasRelevant) continue;

                float uvW = island.MaxUV.x - island.MinUV.x;
                float uvH = island.MaxUV.y - island.MinUV.y;
                int w = Mathf.Max(4, Mathf.RoundToInt(uvW * kx));
                int h = Mathf.Max(4, Mathf.RoundToInt(uvH * ky));
                int gx = Mathf.RoundToInt((island.MinUV.x - uvGroup.MinUV.x) * kx);
                int gy = Mathf.RoundToInt((island.MinUV.y - uvGroup.MinUV.y) * ky);
                rectList.Add((island, gx, gy, w, h));
                maxX = Math.Max(maxX, gx + w);
                maxY = Math.Max(maxY, gy + h);
            }
            if (rectList.Count == 0) return null;

            // round up to 4px grid  向上取整到 4px 网格
            int W = (maxX + 3) / 4 * 4;
            int H = (maxY + 3) / 4 * 4;
            int mw = W / 4, mh = H / 4;
            var mask = new bool[mw * mh];
            int cells = 0;

            foreach (var (island, gx, gy, w, h) in rectList)
            {
                comp.IslandRects[island] = (gx, gy, w, h);
                var local = CoverageRasterizer.Rasterize(island, w, h);
                int ox = gx / 4, oy = gy / 4;
                for (int ly = 0; ly < h; ly++)
                {
                    for (int lx = 0; lx < w; lx++)
                    {
                        if (local[ly * w + lx] == 0) continue;
                        int cx = ox + lx / 4, cy = oy + ly / 4;
                        if (cx >= mw || cy >= mh) continue;
                        int ci = cy * mw + cx;
                        if (!mask[ci])
                        {
                            mask[ci] = true;
                            cells++;
                        }
                    }
                }
            }

            var bigMask = BigInteger.Zero;
            for (int cy = 0; cy < mh; cy++)
            {
                for (int cx = 0; cx < mw; cx++)
                {
                    if (mask[cy * mw + cx]) bigMask |= BigInteger.One << (cy * mw + cx);
                }
            }
            comp.Mask = bigMask;
            comp.W = W;
            comp.H = H;
            comp.Cells = cells;
            return comp;
        }

        // ------------------------------------------------------------------
        private static void OrMask(StackItem item, BigInteger gMask, int gMw, int gMh,
            int ox, int oy, ref BigInteger mask, ref int W, ref int H, ref int cells)
        {
            int newW = Math.Max(W, ox * 4 + gMw * 4);
            int newH = Math.Max(H, oy * 4 + gMh * 4);
            int mw = (newW + 3) / 4, mh = (newH + 3) / 4;
            var oldMask = new bool[mw * mh];
            int oldMw = Math.Max(1, W / 4), oldMh = Math.Max(1, H / 4);
            for (int cy = 0; cy < oldMh; cy++)
            {
                for (int cx = 0; cx < oldMw; cx++)
                {
                    if ((mask >> (cy * oldMw + cx)) != 0) oldMask[cy * mw + cx] = true;
                }
            }
            for (int cy = 0; cy < gMh; cy++)
            {
                for (int cx = 0; cx < gMw; cx++)
                {
                    if ((gMask >> (cy * gMw + cx)) == 0) continue;
                    int t = (oy + cy) * mw + (ox + cx);
                    if (!oldMask[t])
                    {
                        oldMask[t] = true;
                        cells++;
                    }
                }
            }
            var m = BigInteger.Zero;
            for (int i = 0; i < mw * mh; i++)
            {
                if (oldMask[i]) m |= BigInteger.One << i;
            }
            mask = m;
            W = newW;
            H = newH;
        }

        private static void MarkBorder(bool[] occ, int w, int h, int pad)
        {
            if (pad <= 0) return;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    if (x < pad || x >= w - pad || y < pad || y >= h - pad) occ[y * w + x] = true;
                }
            }
        }

        private static bool IsInTypeGroup(ATOTexTypeGroup tg, int tid)
        {
            if (tg.TextureIds.Contains(tid)) return true;
            foreach (var dict in tg.SpecialTextures.Values)
            {
                if (dict.ContainsValue(tid)) return true;
            }
            return false;
        }
    }
}
