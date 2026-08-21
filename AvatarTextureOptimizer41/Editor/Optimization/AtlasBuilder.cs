using System;
using System.Collections.Generic;
using System.Linq;
using Net.Fosa.AvatarTextureOptimizer.Pure;
using UnityEngine;

// Atlas builder: forms bucket (kind x colorspace x filter) groups, lays out each UVGroup once at the
// reference resolution, then assembles per-(bucket, texture) atlases that replicate the group's
// normalized layout so the same UV maps to the same position in every atlas. Groups that cannot be
// assembled fall back to whole-texture scaling.
// 图集装配器：形成桶（种类×色彩空间×过滤）组，每个 UV 组在参考分辨率上布局一次，再按 (桶, 贴图)
// 装配图集并复制组的归一化布局，使同一 UV 在所有图集的位置一致。无法装配的组回退为整图缩放。

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    public static class AtlasBuilder
    {
        public sealed class GroupPlan
        {
            public UVGroup Group;
            public GroupLayout Layout;
            public Vector2Int MaxDemand;                       // wooden-bucket max across uses. 跨引用木桶最大值。
            public Dictionary<UVIsland, Vector2Int> Demand;    // per island max across uses. 每岛跨引用最大值。
            public bool Feasible = true;
            public string FallbackReason = "";
        }

        /// <summary>
        /// Builds atlas definitions into ctx.Atlases and marks ctx-level fallbacks.
        /// ctx.UVGroups must already have use.IslandScaleFactors filled.
        /// 将图集定义构建到 ctx.Atlases 并标记回退。
        /// </summary>
        public static void Build(ATOBuildContext ctx, ATOSettingsData data, ATOCancellation cancel, ATOBuildReport report)
        {
            if (!data.generateAtlas)
            {
                ATOLog.Info("atlas generation disabled; whole-texture scaling path");
                return;
            }

            int dMax = MaxAtlasDim(ctx.Platform);
            var candidateDims = AtoAtlasSizes.Candidates(dMax, data.atlasSizeMode == AtlasSizeMode.PowerOfTwo);
            int pad = Mathf.Max(Mathf.CeilToInt(dMax / 128f), data.minPadding);

            // ---- Pass 1: per-group feasibility, demands, layouts. 第一遍：组可行性、需求、布局。----
            var plans = new List<GroupPlan>();
            int gIdx = 0;
            foreach (var group in ctx.UVGroups)
            {
                cancel.ThrowIfCancelled($"Atlas layout (group {gIdx + 1}/{ctx.UVGroups.Count})", gIdx / (float)Math.Max(1, ctx.UVGroups.Count));
                gIdx++;
                // A group with ANY whitelisted/skip use cannot be atlased: the shared UV must not be remapped
                // (its other textures skip atlasing and fall back to whole-texture scaling, per spec).
                // 含任何白名单/跳过引用的组不能图集化：共享 UV 不得被重映射（其他贴图按规格回退整图缩放）。
                if (!group.AnyOptimizable || group.Uses.Any(u => u.Skip)) continue;

                var plan = new GroupPlan { Group = group, Demand = new Dictionary<UVIsland, Vector2Int>() };
                bool ok = true;
                foreach (var island in group.Islands)
                {
                    Vector2Int demand = Vector2Int.one;
                    foreach (var use in group.Uses)
                    {
                        if (use.Skip) continue;
                        if (!use.IslandScaleFactors.TryGetValue(island, out var s))
                        {
                            ok = false; break;
                        }
                        // Round up to 4px granularity. 向上取整到 4px 粒度。
                        demand.x = Mathf.Max(demand.x, AlignUp(Mathf.RoundToInt(s.x)));
                        demand.y = Mathf.Max(demand.y, AlignUp(Mathf.RoundToInt(s.y)));
                    }
                    if (!ok) break;
                    demand.x = Mathf.Clamp(demand.x, 4, dMax);
                    demand.y = Mathf.Clamp(demand.y, 4, dMax);
                    plan.Demand[island] = demand;
                    plan.MaxDemand = Vector2Int.Max(plan.MaxDemand, demand);
                }
                if (!ok)
                {
                    plan.Feasible = false;
                    plan.FallbackReason = "island scale factors incomplete";
                    plans.Add(plan);
                    continue;
                }
                if (plan.MaxDemand.x > dMax || plan.MaxDemand.y > dMax)
                {
                    plan.Feasible = false;
                    plan.FallbackReason = $"island demand {plan.MaxDemand} exceeds max atlas {dMax}";
                    plans.Add(plan);
                    continue;
                }

                // Layout at reference resolution. 参考分辨率布局。
                var items = new List<PackItem>();
                foreach (var island in group.Islands)
                {
                    var demand = plan.Demand[island];
                    var mask = RasterizeIsland(island, demand.x, demand.y);
                    items.Add(new PackItem { Mask = mask, Tag = island });
                }
                var layout = AtoGroupLayout.Layout(items, dMax, pad);
                if (!layout.Success)
                {
                    plan.Feasible = false;
                    plan.FallbackReason = "group does not fit reference atlas";
                    plans.Add(plan);
                    continue;
                }
                plan.Layout = layout;
                plans.Add(plan);
            }

            // ---- Pass 2: global group placement (one shared normalized layout). 第二遍：全局组摆放（共享归一化布局）。----
            // All atlased groups share ONE normalized universe so the same mesh UV maps to the same
            // position in every atlas. Group macro rects are packed at D_max via rect BLF; groups that
            // do not fit are dropped (fallback), largest first.
            // 所有图集化组共享一个归一化空间，保证同一网格 UV 在所有图集位置一致。
            // 组宏观矩形在 D_max 上做矩形 BLF；装不下的组被丢弃（回退），先丢最大的。
            var feasible = plans.Where(p => p.Feasible).ToList();
            feasible.Sort((a, b) => (b.Layout.BoundsUV.w * b.Layout.BoundsUV.h).CompareTo(a.Layout.BoundsUV.w * a.Layout.BoundsUV.h));
            var globalOrigins = new Dictionary<UVGroup, Vector2>();
            {
                var rects = new List<AtoRectI>();
                var order = new List<GroupPlan>();
                foreach (var plan in feasible)
                {
                    rects.Add(new AtoRectI(0, 0,
                        Mathf.Max(1, Mathf.CeilToInt(plan.Layout.BoundsUV.w * dMax)),
                        Mathf.Max(1, Mathf.CeilToInt(plan.Layout.BoundsUV.h * dMax))));
                    order.Add(plan);
                }
                var placements = new List<AtoRectI>();
                if (!AtoRectBLF.TryPack(rects, dMax, dMax, pad, placements))
                {
                    // Drop the largest groups until the rest fit. 丢弃最大的组直到剩余能装下。
                    while (rects.Count > 0)
                    {
                        rects.RemoveAt(0);
                        order.RemoveAt(0);
                        placements.Clear();
                        if (AtoRectBLF.TryPack(rects, dMax, dMax, pad, placements)) break;
                    }
                    for (int i = order.Count; i < feasible.Count; i++)
                    {
                        feasible[i].Feasible = false;
                        feasible[i].FallbackReason = "global group layout overflow";
                    }
                    feasible = feasible.Take(order.Count).ToList();
                }
                for (int i = 0; i < order.Count; i++)
                    globalOrigins[order[i].Group] = new Vector2((float)placements[i].x / dMax, (float)placements[i].y / dMax);
            }

            // ---- Pass 3: bucket membership. 第三遍：桶归属。----
            // (bucket, texture) -> groups that contain that texture in that bucket.
            var bucketTextureGroups = new Dictionary<(AtlasBucketKey, Texture2D), List<GroupPlan>>();
            foreach (var plan in feasible)
            {
                foreach (var use in plan.Group.Uses)
                {
                    if (use.Skip) continue;
                    var key = (BucketOf(use), use.Texture);
                    if (!bucketTextureGroups.TryGetValue(key, out var tl)) { tl = new List<GroupPlan>(); bucketTextureGroups[key] = tl; }
                    if (!tl.Contains(plan)) tl.Add(plan);
                }
            }

            // ---- Pass 4: create one atlas per (bucket, texture). 第四遍：每个 (桶, 贴图) 一张图集。----
            var useAtlas = new Dictionary<TextureUse, AtlasDefinition>();
            foreach (var kv in bucketTextureGroups)
            {
                var bucket = kv.Key.Item1;
                var tex = kv.Key.Item2;

                // Choose the smallest candidate dim D so every island of this texture renders at or above
                // its own demand (pixel size = normSize*D, normSize = maxDemand/dMax).
                // 选择最小候选边长 D，使该贴图每个岛的实际像素（normSize×D，normSize=maxDemand/dMax）≥ 自身需求。
                int minDim = 1;
                foreach (var plan in kv.Value)
                {
                    foreach (var island in plan.Group.Islands)
                    {
                        Vector2Int texDemand = Vector2Int.one;
                        foreach (var use in plan.Group.Uses)
                        {
                            if (use.Skip || use.Texture != tex) continue;
                            if (!BucketOf(use).Equals(bucket)) continue; // only this bucket's demand. 只计本桶需求。
                            if (use.IslandScaleFactors.TryGetValue(island, out var s))
                            {
                                texDemand.x = Mathf.Max(texDemand.x, Mathf.RoundToInt(s.x));
                                texDemand.y = Mathf.Max(texDemand.y, Mathf.RoundToInt(s.y));
                            }
                        }
                        float nx = (float)plan.Demand[island].x / dMax;
                        float ny = (float)plan.Demand[island].y / dMax;
                        if (nx > 1e-6f) minDim = Mathf.Max(minDim, Mathf.CeilToInt(texDemand.x / nx));
                        if (ny > 1e-6f) minDim = Mathf.Max(minDim, Mathf.CeilToInt(texDemand.y / ny));
                    }
                }
                int dim = AtoAtlasSizes.SmallestAtLeast(candidateDims, minDim);
                if (dim < 0) dim = Mathf.Clamp(minDim, 64, dMax);

                var atlas = new AtlasDefinition { Bucket = bucket, Width = dim, Height = dim };
                foreach (var plan in kv.Value)
                {
                    var origin = globalOrigins[plan.Group];
                    foreach (var island in plan.Group.Islands)
                    {
                        var nr = plan.Layout.IslandRects[island];
                        bool rotated = plan.Layout.Rotations.TryGetValue(island, out bool r) && r;
                        float fx = origin.x + nr.x, fy = origin.y + nr.y;
                        int px = Mathf.Clamp(Mathf.RoundToInt(fx * dim), 0, dim - 1);
                        int py = Mathf.Clamp(Mathf.RoundToInt(fy * dim), 0, dim - 1);
                        int pw = Mathf.Max(1, Mathf.RoundToInt(nr.w * dim));
                        int ph = Mathf.Max(1, Mathf.RoundToInt(nr.h * dim));
                        if (px + pw > dim) pw = dim - px;
                        if (py + ph > dim) ph = dim - py;
                        atlas.IslandRects[island] = new RectInt(px, py, pw, ph);
                        island.Rotation = rotated ? 1 : 0;
                        island.NormalizedRect = new Rect(fx, fy, nr.w, nr.h);
                        atlas.IslandPixelArea += (long)pw * ph;
                    }
                }
                atlas.AtlasPixelArea = (long)dim * dim;
                atlas.Utilization = atlas.AtlasPixelArea > 0 ? (float)((double)atlas.IslandPixelArea / atlas.AtlasPixelArea) : 0f;

                foreach (var plan in kv.Value)
                {
                    foreach (var use in plan.Group.Uses)
                    {
                        if (use.Skip || use.Texture != tex) continue;
                        if (BucketOf(use).Equals(bucket) && !atlas.PropertyForUse.ContainsKey(use))
                        {
                            atlas.PropertyForUse[use] = use.PropertyName;
                            if (!atlas.SourceTextures.Contains(tex)) atlas.SourceTextures.Add(tex);
                            useAtlas[use] = atlas;
                        }
                    }
                }
                ctx.Atlases.Add(atlas);
            }

            // ---- Fallback groups: mark their uses un-atlased. 回退组：标记其引用不图集化。----
            foreach (var plan in plans)
            {
                if (!plan.Feasible)
                {
                    ATOLog.Warn($"UV group on {plan.Group.Renderer?.name} slot {plan.Group.SlotIndex} ch{plan.Group.Channel}: atlas skipped ({plan.FallbackReason}); falls back to whole-texture scaling");
                    foreach (var use in plan.Group.Uses)
                    {
                        if (useAtlas.ContainsKey(use)) useAtlas.Remove(use);
                    }
                }
            }

            ctx.UseAtlas = useAtlas;
            report.AtlasCount = ctx.Atlases.Count;
            report.TotalAtlasUtilization = ctx.Atlases.Sum(a => a.Utilization);
            foreach (var a in ctx.Atlases)
                ATOLog.Info($"atlas {a.Width}x{a.Height} {a.Bucket}: {a.IslandRects.Count} islands, utilization {a.Utilization * 100f:F1}%, sources: {string.Join(", ", a.SourceTextures.ConvertAll(t => t.name))}");
        }

        public static int MaxAtlasDim(ATOPlatform platform) => platform == ATOPlatform.PC ? 8192 : 4096;

        private static int AlignUp(int v) => (v + 3) / 4 * 4;

        private static BitMask RasterizeIsland(UVIsland island, int w, int h)
        {
#if ATO_BURST_AVAILABLE
            return BurstPacking.Rasterize(island, w, h);
#else
            return AtoRaster.RasterizeTriangles(island.UVs, island.TriangleArrayIndices,
                island.BoundsMin.x, island.BoundsMin.y, island.BoundsMax.x, island.BoundsMax.y, w, h);
#endif
        }

        /// <summary>Atlas bucket key of a use. 引用的图集桶 key。</summary>
        public static AtlasBucketKey BucketOf(TextureUse use)
        {
            bool linear = use.Class == TextureClass.Normal || !TextureDecodeCache.IsSRGB(use.Texture);
            return new AtlasBucketKey
            {
                Class = use.Class,
                LinearSpace = linear,
                Filter = ToFilter(use.Texture != null ? use.Texture.filterMode : FilterMode.Bilinear),
            };
        }

        private static ATOFilterMode ToFilter(FilterMode fm)
        {
            switch (fm)
            {
                case FilterMode.Point: return ATOFilterMode.Point;
                case FilterMode.Trilinear: return ATOFilterMode.Trilinear;
                default: return ATOFilterMode.Bilinear;
            }
        }
    }
}
