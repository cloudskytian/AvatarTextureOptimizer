using System;
using System.Collections.Generic;
using UnityEngine;
using FOSA.AvatarTextureOptimizer;

namespace FOSA.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Type-group queues → candidate pool → BLF. UV groups with animation alternates get their own queue.
    /// 类型组队列 → 候选池 → BLF。带动画备选贴图的 UV 组单独成队。
    /// </summary>
    internal static class ATOAtlasPipeline
    {
        public static void Run(ATOContext ctx)
        {
            var pool = ATOBLFPacker.BuildCandidatePool(ctx.Settings);
            var maxEdge = ctx.Settings.MaxAtlasEdge;
            var maxArea = maxEdge * maxEdge;

            foreach (var tg in ctx.TypeGroups)
            {
                ctx.Progress.ThrowIfCanceled();
                var units = new List<ATOUvGroup>(tg.UvGroups);
                units.Sort((a, b) => RasterArea(b).CompareTo(RasterArea(a)));

                var queues = new List<List<ATOUvGroup>>();
                foreach (var unit in units)
                {
                    if (unit.SkipAtlas || ctx.WhitelistedTexturesOverlaps(unit))
                    {
                        unit.SkipAtlas = true;
                        ctx.Report.SkippedAtlas++;
                        continue;
                    }

                    EnsureMasks(ctx, unit);
                    var area = RasterArea(unit);
                    if (area <= 0)
                    {
                        unit.SkipAtlas = true;
                        continue;
                    }

                    // Single unit cannot fit the largest atlas. / 单个单位连最大图集都塞不下。
                    if (area > maxArea || !CanFitMax(unit, maxEdge, ctx.Settings.minPadding))
                    {
                        unit.SkipAtlas = true;
                        unit.FailReason = "cannot fit max atlas";
                        ctx.Report.SkippedAtlas++;
                        ctx.Log.Warn($"UVGroup {unit.Id} cannot fit {maxEdge} atlas, skip atlasing.");
                        ATOLoc.Report(nadena.dev.ndmf.ErrorSeverity.NonFatal, "ato.warn.no_fit", unit.Id.ToString());
                        continue;
                    }

                    var placed = false;
                    if (!unit.HasAlternates)
                    {
                        foreach (var q in queues)
                        {
                            if (q.Count > 0 && q[0].HasAlternates) continue;
                            if (CanFitQueue(q, unit, maxEdge, ctx.Settings.minPadding))
                            {
                                q.Add(unit);
                                placed = true;
                                break;
                            }
                        }
                    }

                    if (!placed)
                    {
                        queues.Add(new List<ATOUvGroup> { unit });
                    }
                }

                foreach (var q in queues)
                {
                    ctx.Progress.ThrowIfCanceled();
                    PackQueue(ctx, tg, q, pool);
                }
            }

            ctx.Report.AtlasCount = CountAtlases(ctx);
            ctx.Log.Info($"Atlases generated: {ctx.Report.AtlasCount}");
        }

        private static int CountAtlases(ATOContext ctx)
        {
            var n = 0;
            foreach (var tg in ctx.TypeGroups) n += tg.Atlases.Count;
            return n;
        }

        private static bool CanFitMax(ATOUvGroup unit, int maxEdge, ATOMinPadding minPad)
        {
            var items = BuildItems(unit);
            return ATOBLFPacker.Pack(items, maxEdge, maxEdge, ATOBLFPacker.PaddingFor(maxEdge, minPad), out _);
        }

        private static bool CanFitQueue(List<ATOUvGroup> q, ATOUvGroup extra, int maxEdge, ATOMinPadding minPad)
        {
            var items = new List<ATOBLFPacker.Item>();
            foreach (var g in q) items.AddRange(BuildItems(g));
            items.AddRange(BuildItems(extra));
            return ATOBLFPacker.Pack(items, maxEdge, maxEdge, ATOBLFPacker.PaddingFor(maxEdge, minPad), out _);
        }

        private static void PackQueue(ATOContext ctx, ATOTypeGroup tg, List<ATOUvGroup> q, List<(int w, int h)> pool)
        {
            var items = new List<ATOBLFPacker.Item>();
            foreach (var g in q) items.AddRange(BuildItems(g));
            long need = 0;
            foreach (var it in items) need += it.Area;

            foreach (var cand in pool)
            {
                if ((long)cand.w * cand.h < need) continue;
                var pad = ATOBLFPacker.PaddingFor(Math.Max(cand.w, cand.h), ctx.Settings.minPadding);
                if (!ATOBLFPacker.Pack(items, cand.w, cand.h, pad, out var places)) continue;

                ApplyPlacements(q, places);
                foreach (var g in q) g.LayoutSize = new Vector2Int(cand.w, cand.h);
                ComposeAtlases(ctx, tg, q, cand.w, cand.h, pad, places);
                ctx.Log.Info($"Packed type {tg.Id} queue n={q.Count} islands={items.Count} → {cand.w}x{cand.h} pad={pad}");
                return;
            }

            foreach (var g in q)
            {
                g.SkipAtlas = true;
                g.FailReason = "no candidate fit";
                ctx.Report.SkippedAtlas++;
            }
        }

        private static void ApplyPlacements(List<ATOUvGroup> q, List<ATOBLFPacker.Placement> places)
        {
            foreach (var p in places)
            {
                p.Island.Packed = true;
                p.Island.PackedX = p.X;
                p.Island.PackedY = p.Y;
                p.Island.Rotated = p.Rotated;
                p.Island.ScaledW = p.PixelW;
                p.Island.ScaledH = p.PixelH;
            }
        }

        private static void ComposeAtlases(
            ATOContext ctx, ATOTypeGroup tg, List<ATOUvGroup> q,
            int aw, int ah, int pad, List<ATOBLFPacker.Placement> places)
        {
            // One atlas per unique source texture (animation alternates).
            // 每张源贴图一张图集（动画备选各自一张）。
            var sources = new HashSet<Texture2D>();
            foreach (var g in q)
            foreach (var t in g.Textures)
                sources.Add(t);

            foreach (var src in sources)
            {
                if (src == null) continue;
                if (ctx.WhitelistedTextures.Contains(src)) continue;
                var cat = GuessCategory(ctx, src);
                var atlas = ATOAtlasComposer.Compose(ctx, src, cat, aw, ah, pad, q);
                if (atlas == null) continue;
                tg.Atlases.Add(atlas);
                ctx.TextureRemap[src] = atlas.Atlas;
                ctx.Report.AtlasLines.Add(
                    $"{atlas.Name}  {aw}x{ah}  util={atlas.Utilization:P1}  from={src.name}  islands={atlas.IslandCount}");
                ctx.Log.Detail($"Atlas {atlas.Name} src={src.name} {aw}x{ah} util={atlas.Utilization:P1} islands={atlas.IslandCount}");
            }

            // Secondary atlas downscale if the whole type is below main-color demand.
            // 若该类型整体质量需求低于主色，则整张副图集降分辨率。
            // Layout stays; UVs are normalized so this is safe.
            // layout 不变；UV 是归一化的，因此安全。
        }

        private static ATOTextureCategory GuessCategory(ATOContext ctx, Texture2D src)
        {
            foreach (var use in ctx.Uses)
            {
                if (use.Slot.texture == src) return use.Slot.category;
            }
            return ATOTextureUtil.IsNormalImporter(src) ? ATOTextureCategory.Normal : ATOTextureCategory.OpaqueAlbedo;
        }

        private static void EnsureMasks(ATOContext ctx, ATOUvGroup g)
        {
            foreach (var island in g.Islands)
            {
                if (island.Mask.Bits != null) continue;
                island.Mask = ATORasterizer.Rasterize(island, Math.Max(1, island.ScaledW), Math.Max(1, island.ScaledH));
            }
        }

        private static List<ATOBLFPacker.Item> BuildItems(ATOUvGroup g)
        {
            var list = new List<ATOBLFPacker.Item>(g.Islands.Count);
            foreach (var island in g.Islands)
            {
                if (island.Mask.Bits == null)
                    island.Mask = ATORasterizer.Rasterize(island, Math.Max(1, island.ScaledW), Math.Max(1, island.ScaledH));
                var item = new ATOBLFPacker.Item
                {
                    Island = island,
                    Mask = island.Mask,
                    MaskRot = island.Mask.Transpose(),
                    PixelW = Math.Max(1, island.ScaledW),
                    PixelH = Math.Max(1, island.ScaledH)
                };
                item.Area = item.Mask.PopCount();
                list.Add(item);
            }
            return list;
        }

        private static long RasterArea(ATOUvGroup g)
        {
            long a = 0;
            foreach (var island in g.Islands)
            {
                if (island.Mask.Bits != null) a += island.Mask.PopCount();
                else a += Math.Max(1, island.ScaledW) * Math.Max(1, island.ScaledH) / 16;
            }
            return a;
        }
    }

    internal static class ATOContextWhitelistExt
    {
        public static bool WhitelistedTexturesOverlaps(this ATOContext ctx, ATOUvGroup g)
        {
            foreach (var t in g.Textures)
                if (t != null && ctx.WhitelistedTextures.Contains(t)) return true;
            return false;
        }
    }
}
