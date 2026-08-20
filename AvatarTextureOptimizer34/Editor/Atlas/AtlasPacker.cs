// AvatarTextureOptimizer - AtlasPacker
// EN: Template layout packing (per UV group) + atlas block packing (per type group queue) with Burst-rasterized
// 4px masks, BLF (bottom-left fill) scanning with coarse-grid acceleration, 90-degree rotation steps, and the
// candidate atlas pool (POT / experimental NPOT).
// CN: 模板布局装箱（每 UV 组）+ 图集块装箱（每类型组队列），使用 Burst 光栅 4px 掩码、
//     带粗网格加速的 BLF（左下填充）扫描、90 度旋转步进、候选图集池（POT / 实验性 NPOT）。
using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer
{
    public static class AtlasPacker
    {
        public const int Cell = 4;                    // 4px 粒度
        public const int CoarseFactor = 8;            // 粗网格 = 8 单元（32px）
        public const int MinAtlasSize = 64;

        /// <summary>EN: Entry point. / CN: 入口。</summary>
        public static PackingResult Pack(AtoBuildState state)
        {
            var result = new PackingResult();
            var profile = state.Profile;
            int padPx = Mathf.Max(4, profile.padding);
            int maxSize = profile.maxAtlasSize;
#if UNITY_ANDROID || UNITY_IOS
            maxSize = Mathf.Min(maxSize, 4096);
#endif
            if (state.Platform == AtoPlatform.Android || state.Platform == AtoPlatform.iOS)
                maxSize = Mathf.Min(maxSize, 4096);

            // ------------------------------------------------------------ 1. 模板
            foreach (var g in state.UvGroups)
            {
                if (state.Cancelled) break;
                if (g.whitelisted) continue;
                g.layout = BuildTemplate(state, g, padPx);
            }

            // ------------------------------------------------------------ 2. 块
            var blocks = new List<AtlasBlock>();
            foreach (var tref in state.Textures)
            {
                if (state.Cancelled) break;
                if (tref.whitelisted || tref.specialUv || tref.skipAtlas) continue;
                foreach (var g in tref.uvGroups)
                {
                    if (g.layout == null) continue;
                    // EN: Uniform scale for the whole (type group, usage): guarantees identical uv rects across
                    // the albedo & normal atlases of the same UV group (spec: 满足最小 padding 的前提下可缩放).
                    // CN: (类型组, 用途) 的统一缩放：保证同一 UV 组在主色/法线图集中的 uv 矩形一致。
                    float sT = 1f;
                    if (tref.typeGroup != null &&
                        tref.typeGroup.usageScale.TryGetValue(tref.usage, out float u))
                        sT = u;
                    var block = new AtlasBlock { tex = tref, layout = g.layout, scale = sT };
                    foreach (var e in g.layout.entries)
                    {
                        block.areaCells += (long)Math.Max(1, Math.Round(e.w * sT)) * Math.Max(1, Math.Round(e.h * sT));
                    }
                    blocks.Add(block);
                }
            }
            blocks.Sort((a, b) => b.areaCells.CompareTo(a.areaCells));

            // ------------------------------------------------------------ 3. 队列
            var queues = new Dictionary<(TypeGroup, TextureUsage), List<AtlasBlock>>();
            foreach (var b in blocks)
            {
                var key = (b.tex.typeGroup, b.tex.usage);
                if (!queues.TryGetValue(key, out var q)) queues[key] = q = new List<AtlasBlock>();
                q.Add(b);
            }

            // ------------------------------------------------------------ 4. 装箱
            var candidates = BuildCandidates(maxSize, profile.experimentalNpot);
            int maxCells = maxSize / Cell;
            int minPadCells = Mathf.Max(1, 4 / Cell);

            // EN: Process queues by total area descending (spec: 光栅化总面积降序).
            // CN: 按光栅化总面积降序处理队列（按需求）。
            var orderedQueues = new List<KeyValuePair<(TypeGroup, TextureUsage), List<AtlasBlock>>>(queues);
            orderedQueues.Sort((a, b) =>
            {
                long aa = 0, ba = 0;
                foreach (var bl in a.Value) aa += bl.areaCells;
                foreach (var bl in b.Value) ba += bl.areaCells;
                return ba.CompareTo(aa);
            });
            foreach (var kv in orderedQueues)
            {
                if (state.Cancelled) break;
                var queue = kv.Value;
                result.atlases.AddRange(PlaceQueue(state, queue, kv.Key.Item1, kv.Key.Item2, candidates,
                    maxCells, padPx, minPadCells));
            }

            AtoLog.Detail($"Packed {result.atlases.Count} atlases");
            return result;
        }

        // ===================================================================== 模板

        private static TemplateLayout BuildTemplate(AtoBuildState state, UvGroup g, int padPx)
        {
            // EN: Islands with a template size (bucket max across participating textures).
            // CN: 有模板尺寸（参与贴图的木桶最大）的岛。
            var entries = new List<(Island island, int w, int h)>();
            long totalCells = 0;
            foreach (var island in g.islands)
            {
                if (island.templateW <= 0 || island.templateH <= 0) continue;
                int w = Math.Max(1, (island.templateW + Cell - 1) / Cell);
                int h = Math.Max(1, (island.templateH + Cell - 1) / Cell);
                entries.Add((island, w, h));
                totalCells += (long)w * h;
            }
            if (entries.Count == 0) return null;

            entries.Sort((a, b) =>
            {
                long aa = (long)a.w * a.h, ba = (long)b.w * b.h;
                if (aa != ba) return ba.CompareTo(aa);
                int ae = Math.Max(a.w, a.h), be = Math.Max(b.w, b.h);
                return be.CompareTo(ae);
            });

            // EN: Growing container until everything fits (BLF, 4 rotations).
            // CN: 容器倍增直至全部装下（BLF，4 种旋转）。
            int side = Math.Max(MinAtlasSize / Cell,
                (int)Math.Ceiling(Math.Sqrt(totalCells * 1.15)));
            int padCells = Math.Max(1, (padPx + Cell - 1) / Cell);

            var layout = new TemplateLayout { group = g };
            while (true)
            {
                layout.entries.Clear();
                if (TryPackTemplate(entries, side, padCells, layout))
                {
                    layout.cellsW = side;
                    layout.cellsH = side;
                    return layout;
                }
                side *= 2;
                if (side > 4096 * 2) return null; // 安全上限
            }
        }

        private static bool TryPackTemplate(List<(Island island, int w, int h)> entries, int side, int padCells,
            TemplateLayout layout)
        {
            var occ = new CellMask(side, side);
            bool ok = true;
            foreach (var (island, w, h) in entries)
            {
                var mask = IslandRasterizer.Rasterize(island, w * Cell, h * Cell, padCells * Cell, false);
                bool placed = false;
                // EN: 4 rotations: 0 / 90 (transpose) / 180 / 270.
                // CN: 4 种旋转：0 / 90（转置）/ 180 / 270。
                var orientations = BuildOrientations(mask, w, h);
                for (int o = 0; o < 4 && !placed; o++)
                {
                    var (om, ow, oh) = orientations[o];
                    int x0 = 0, y0 = 0;
                    if (FindPosition(occ, om, ow, oh, side, side, out x0, out y0))
                    {
                        Place(occ, om, ow, oh, x0, y0);
                        layout.entries.Add(new TemplateEntry
                        {
                            island = island, x = x0, y = y0, w = ow, h = oh,
                            rotation = o * 90
                        });
                        placed = true;
                        break;
                    }
                }
                mask.Dispose();
                foreach (var (m, _, _) in orientations) if (m != null && m != mask) m.Dispose();
                if (!placed) { ok = false; break; }
            }
            occ.Dispose();
            return ok;
        }

        private static (CellMask, int, int)[] BuildOrientations(CellMask src, int w, int h)
        {
            // EN: Orientations of the CONTENT (w,h) — the mask itself includes the padding border, so all
            // transforms use the mask's own dimensions.
            // CN: 内容（w,h）的朝向——掩码本身含 padding 边框，故全部变换使用掩码自身尺寸。
            int mw = src.cellsW, mh = src.cellsH;
            var list = new (CellMask, int, int)[4];
            list[0] = (src, w, h);
            var r90 = new CellMask(mh, mw);
            RunTranspose(src, mw, mh, r90);
            list[1] = (r90, h, w);
            var r180 = FlipBoth(src, mw, mh);
            list[2] = (r180, w, h);
            var r270 = FlipBoth(r90, mh, mw);
            list[3] = (r270, h, w);
            return list;
        }

        private static void RunTranspose(CellMask src, int w, int h, CellMask dst)
        {
            new TransposeMaskJob
            {
                src = src.bits, srcW = w, srcH = h, dst = dst.bits, dstW = h, dstH = w
            }.Schedule().Complete();
        }

        /// <summary>EN: Applies a quarter-turn (0/90/180/270) to a mask (uses the mask's own dimensions, which
        /// include the padding border). / CN: 对掩码施加象限旋转（0/90/180/270；使用掩码自身尺寸，含 padding 边框）。</summary>
        private static CellMask Orient(CellMask src, int quarters)
        {
            int mw = src.cellsW, mh = src.cellsH;
            switch ((quarters % 360 + 360) % 360)
            {
                case 0: return src;
                case 90:
                {
                    var dst = new CellMask(mh, mw);
                    RunTranspose(src, mw, mh, dst);
                    return dst;
                }
                case 180: return FlipBoth(src, mw, mh);
                default:
                {
                    var t = new CellMask(mh, mw);
                    RunTranspose(src, mw, mh, t);
                    var dst = FlipBoth(t, mh, mw);
                    t.Dispose();
                    return dst;
                }
            }
        }

        private static CellMask FlipBoth(CellMask src, int w, int h)
        {
            var dst = new CellMask(w, h);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    if (src.Get(x, y)) dst.bits[((h - 1 - y) * w + (w - 1 - x)) >> 6] |=
                        1UL << (((h - 1 - y) * w + (w - 1 - x)) & 63);
            return dst;
        }

        // ===================================================================== 队列装箱

        private static List<PackedAtlas> PlaceQueue(AtoBuildState state, List<AtlasBlock> queue, TypeGroup group,
            TextureUsage usage, List<int> candidatesPx, int maxCells, int padPx, int minPadCells)
        {
            var result = new List<PackedAtlas>();
            long totalArea = 0;
            foreach (var b in queue) totalArea += b.areaCells;

            // EN: Candidates with area >= remaining total area, sorted by area asc (spec), aspect asc.
            // CN: 面积 ≥ 剩余总面积的候选，按面积升序（按需求）、长宽比升序排序。
            var cand = new List<int>();
            foreach (var c in candidatesPx)
            {
                int cc = c / Cell;
                if ((long)cc * cc >= totalArea) cand.Add(c);
            }
            if (cand.Count == 0) cand.AddRange(candidatesPx);

            // EN: Try candidates in order; the first that fits ALL blocks becomes the atlas (spec).
            // CN: 依序尝试候选；第一个能装下全部块的即成品图集。
            foreach (var c in cand)
            {
                var atl = TryPlaceAll(queue, group, usage, c / Cell, padPx, minPadCells);
                if (atl != null)
                {
                    result.Add(atl);
                    return result;
                }
            }

            // EN: Fallback: incremental packing with multiple atlases (natural growth).
            // CN: 回退：多图集增量装箱（自然增长）。
            PackedAtlas current = null;
            foreach (var block in queue)
            {
                if (current == null) current = NewAtlas(group, usage, maxCells);
                if (!TryPlaceBlock(current, block, padPx, minPadCells))
                {
                    // EN: Try opening a new atlas; if the block still fails, it exceeds the largest atlas.
                    // CN: 尝试新开图集；若仍失败，说明超过最大图集。
                    var fresh = NewAtlas(group, usage, maxCells);
                    if (!TryPlaceBlock(fresh, block, padPx, minPadCells))
                    {
                        fresh.Dispose();
                        AtoLog.Warn(string.Format(I18n.T("warn.atlas.failed"),
                            block.tex.texture != null ? block.tex.texture.name : "?"));
                        continue;
                    }
                    result.Add(current);
                    current = fresh;
                }
            }
            if (current != null) result.Add(current);
            // EN: Downsize every atlas to the smallest candidate that contains its used bbox (spec: 候选图集池).
            // CN: 把每个图集收缩到能容纳已用包围盒的最小候选（按需求：候选图集池）。
            foreach (var a in result) FinalizeSize(a, candidatesPx);
            return result;
        }

        /// <summary>EN: Shrinks an atlas to the smallest candidate >= its used bbox. / CN: 把图集收缩到 >= 已用包围盒的最小候选。</summary>
        private static void FinalizeSize(PackedAtlas atlas, List<int> candidatesPx)
        {
            int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
            foreach (var pi in atlas.islands)
            {
                int x0 = (int)pi.rect.x, y0 = (int)pi.rect.y;
                int x1 = (int)(pi.rect.x + pi.rect.width), y1 = (int)(pi.rect.y + pi.rect.height);
                if (x0 < minX) minX = x0;
                if (y0 < minY) minY = y0;
                if (x1 > maxX) maxX = x1;
                if (y1 > maxY) maxY = y1;
            }
            if (maxX < 0) return;
            int need = Mathf.Max(maxX, maxY);
            int best = atlas.width;
            foreach (var c in candidatesPx)
            {
                if (c >= need && c < best) best = c;
            }
            if (best < atlas.width)
            {
                atlas.width = best;
                atlas.height = best;
                atlas.cellsW = best / Cell;
                atlas.cellsH = best / Cell;
                AtoLog.Detail($"Atlas {atlas.Name} downsized to {best}x{best}");
            }
        }

        private static PackedAtlas TryPlaceAll(List<AtlasBlock> blocks, TypeGroup group, TextureUsage usage,
            int cells, int padPx, int minPadCells)
        {
            var atlas = new PackedAtlas { group = group, usage = usage, cellsW = cells, cellsH = cells,
                width = cells * Cell, height = cells * Cell };
            var occ = new CellMask(cells, cells);
            foreach (var block in blocks)
            {
                if (!TryPlaceBlockInner(atlas, occ, block, padPx, minPadCells))
                {
                    occ.Dispose();
                    return null;
                }
            }
            occ.Dispose();
            return atlas;
        }

        private static PackedAtlas NewAtlas(TypeGroup group, TextureUsage usage, int maxCells)
        {
            return new PackedAtlas { group = group, usage = usage, cellsW = maxCells, cellsH = maxCells,
                width = maxCells * Cell, height = maxCells * Cell };
        }

        private static bool TryPlaceBlock(PackedAtlas atlas, AtlasBlock block, int padPx, int minPadCells)
        {
            if (atlas.occ == null) atlas.occ = new CellMask(atlas.cellsW, atlas.cellsH);
            return TryPlaceBlockInner(atlas, atlas.occ, block, padPx, minPadCells);
        }

        private static bool TryPlaceBlockInner(PackedAtlas atlas, CellMask occ, AtlasBlock block, int padPx,
            int minPadCells)
        {
            // EN: Rasterize each island mask at the scaled size (layout * block scale), with scaled padding,
            // positioned at its scaled layout offset.
            // CN: 按缩放尺寸（布局 × 块缩放）光栅化每个岛掩码，padding 随缩放，并按缩放后的布局偏移放置。
            int padCells = Math.Max(minPadCells, (int)Math.Round(padPx / 4f * block.scale));
            float s = block.scale;

            var scaled = new List<(CellMask mask, int w, int h, TemplateEntry e, int ox, int oy)>();
            foreach (var e in block.layout.entries)
            {
                int w = Math.Max(1, (int)Math.Round(e.w * s));
                int h = Math.Max(1, (int)Math.Round(e.h * s));
                int lx = (int)Math.Round(e.x * s);
                int ly = (int)Math.Round(e.y * s);
                // EN: Rasterize unrotated, then apply the quarter-turn transform to the mask.
                // CN: 先按 0 度光栅化，再对掩码施加象限旋转变换。
                var mask = IslandRasterizer.Rasterize(e.island, w * Cell, h * Cell, padCells * Cell, false);
                var oriented = Orient(mask, e.rotation);
                if (oriented != mask) mask.Dispose();
                scaled.Add((oriented, w, h, e, lx, ly));
            }

            // EN: Union mask over the whole scaled layout (each island at its layout offset + padding border).
            // CN: 整个缩放布局的并集掩码（各岛在其布局偏移 + padding 边框处）。
            int lw = Math.Max(1, (int)Math.Round(block.layout.cellsW * s));
            int lh = Math.Max(1, (int)Math.Round(block.layout.cellsH * s));
            int bw = lw + padCells * 2;
            int bh = lh + padCells * 2;
            var union = new CellMask(bw, bh);
            foreach (var (mask, w, h, _, lx, ly) in scaled)
            {
                for (int y = 0; y < h + padCells * 2; y++)
                {
                    for (int x = 0; x < w + padCells * 2; x++)
                    {
                        if (!mask.Get(x, y)) continue;
                        int gx = lx + x, gy = ly + y;
                        if (gx < 0 || gy < 0 || gx >= bw || gy >= bh) continue;
                        union.bits[(gy * bw + gx) >> 6] |= 1UL << ((gy * bw + gx) & 63);
                    }
                }
            }

            if (!FindPosition(occ, union, bw, bh, atlas.cellsW, atlas.cellsH, out int ox, out int oy))
            {
                union.Dispose();
                foreach (var (m, _, _, _, _, _) in scaled) m.Dispose();
                return false;
            }

            // EN: Place all islands: translated mask into occupancy + record pixel rects (content only, padding
            // is reserved around it).
            // CN: 放置所有岛：掩码平移进占用表 + 记录像素矩形（仅内容区，padding 在其周围保留）。
            foreach (var (mask, w, h, e, lx, ly) in scaled)
            {
                int bx = ox + lx, by = oy + ly;
                for (int y = 0; y < h + padCells * 2; y++)
                {
                    for (int x = 0; x < w + padCells * 2; x++)
                    {
                        if (!mask.Get(x, y)) continue;
                        int gx = bx + x, gy = by + y;
                        if (gx < 0 || gy < 0 || gx >= atlas.cellsW || gy >= atlas.cellsH) continue;
                        occ.bits[(gy * atlas.cellsW + gx) >> 6] |= 1UL << ((gy * atlas.cellsW + gx) & 63);
                        atlas.usedCells++;
                    }
                }
                var scale = block.tex.scaleAt(e.island, s);
                atlas.islands.Add(new PackedIsland
                {
                    island = e.island,
                    tex = block.tex,
                    rect = new Rect((bx + padCells) * Cell, (by + padCells) * Cell, w * Cell, h * Cell),
                    rotation = e.rotation,
                    scaleX = scale.x,
                    scaleY = scale.y,
                    padPx = padCells * Cell
                });
                atlas.sourceTextureCount++;
            }
            union.Dispose();
            foreach (var (m, _, _, _, _, _) in scaled) m.Dispose();
            return true;
        }

        // ===================================================================== 位置搜索

        /// <summary>
        /// EN: BLF position search with coarse-grid acceleration: scan coarse cells in (y,x) order, then fine
        /// offsets within the first fitting coarse cell.
        /// CN: 带粗网格加速的 BLF 位置搜索：按 (y,x) 顺序扫描粗单元，再在首个可用的粗单元内细扫偏移。
        /// </summary>
        private static bool FindPosition(CellMask occ, CellMask mask, int w, int h, int atlasW, int atlasH,
            out int ox, out int oy)
        {
            ox = 0; oy = 0;
            if (w > atlasW || h > atlasH) return false;

            int cw = (atlasW + CoarseFactor - 1) / CoarseFactor;
            int ch = (atlasH + CoarseFactor - 1) / CoarseFactor;
            var coarse = new CellMask(cw, ch);
            // EN: Coarse mask of the island (OR of 8x8 fine cells).
            // CN: 岛粗掩码（8x8 细单元相或）。
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    if (mask.Get(x, y))
                        coarse.bits[((y / CoarseFactor) * cw + (x / CoarseFactor)) >> 6] |=
                            1UL << (((y / CoarseFactor) * cw + (x / CoarseFactor)) & 63);
            int cwMask = (w + CoarseFactor - 1) / CoarseFactor;
            int chMask = (h + CoarseFactor - 1) / CoarseFactor;

            int maxCy = atlasH - h;
            int maxCx = atlasW - w;

            // EN: Scan coarse cells in BLF order.
            // CN: 按 BLF 顺序扫描粗单元。
            for (int cy = 0; cy <= ch - chMask && cy * CoarseFactor <= maxCy; cy++)
            {
                for (int cx = 0; cx <= cw - cwMask && cx * CoarseFactor <= maxCx; cx++)
                {
                    if (CoarseFits(occ, coarse, cwMask, chMask, cx, cy, cw))
                    {
                        // EN: Fine scan within this coarse cell (preserves BLF order).
                        // CN: 在该粗单元内细扫（保持 BLF 顺序）。
                        int fy0 = cy * CoarseFactor, fx0 = cx * CoarseFactor;
                        int fy1 = Math.Min(atlasH - h, fy0 + CoarseFactor - 1);
                        int fx1 = Math.Min(atlasW - w, fx0 + CoarseFactor - 1);
                        for (int y = fy0; y <= fy1; y++)
                        {
                            for (int x = fx0; x <= fx1; x++)
                            {
                                if (Fits(occ, mask, w, h, x, y))
                                {
                                    ox = x; oy = y;
                                    coarse.Dispose();
                                    return true;
                                }
                            }
                        }
                    }
                }
            }
            coarse.Dispose();
            return false;
        }

        private static bool CoarseFits(CellMask occ, CellMask coarse, int wc, int hc, int cx, int cy, int cw)
        {
            for (int y = 0; y < hc; y++)
            {
                for (int x = 0; x < wc; x++)
                {
                    if (!coarse.Get(x, y)) continue;
                    if (occ.Get(cx + x, cy + y)) return false;
                }
            }
            return true;
        }

        private static bool Fits(CellMask occ, CellMask mask, int w, int h, int x, int y)
        {
            for (int yy = 0; yy < h; yy++)
            {
                for (int xx = 0; xx < w; xx++)
                {
                    if (!mask.Get(xx, yy)) continue;
                    if (occ.Get(x + xx, y + yy)) return false;
                }
            }
            return true;
        }

        private static void Place(CellMask occ, CellMask mask, int w, int h, int x, int y)
        {
            for (int yy = 0; yy < h; yy++)
                for (int xx = 0; xx < w; xx++)
                    if (mask.Get(xx, yy))
                        occ.bits[((y + yy) * occ.cellsW + (x + xx)) >> 6] |=
                            1UL << (((y + yy) * occ.cellsW + (x + xx)) & 63);
        }

        // ===================================================================== 候选池

        /// <summary>EN: Candidate atlas sizes (px). POT by default; NPOT = 64px steps (experimental). / CN: 候选图集尺寸（px）。默认 POT；NPOT = 64px 步进（实验性）。</summary>
        public static List<int> BuildCandidates(int maxSize, bool npot)
        {
            var list = new List<int>();
            if (npot)
            {
                for (int s = MinAtlasSize; s <= maxSize; s += 64)
                {
                    if (!list.Contains(s)) list.Add(s);
                }
            }
            else
            {
                for (int s = MinAtlasSize; s <= maxSize; s *= 2)
                {
                    if (!list.Contains(s)) list.Add(s);
                }
            }
            list.Sort();
            return list;
        }
    }

    /// <summary>EN: Extension: per-island scale within a block. / CN: 扩展：块内岛缩放。</summary>
    public static class AtlasBlockExt
    {
        public static Vector2 scaleAt(this TextureRef tref, Island island, float blockScale)
        {
            if (island.scales.TryGetValue(tref, out var s))
                return new Vector2(Mathf.Max(1e-3f, s.scaleX * blockScale), Mathf.Max(1e-3f, s.scaleY * blockScale));
            return new Vector2(blockScale, blockScale);
        }
    }
}
