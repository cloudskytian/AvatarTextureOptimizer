// ============================================================================
// ATO - QualityStage implementation (stage 2)
// ATO - QualityStage 实现（阶段 2）
//
// Atlas mode  图集模式：
//   per UV group:
//     1. every (island, sampled texture) finds its largest passing uniform
//        scale by binary search (budget = density clamped, never upscales);
//        pure-color islands shortcut to min(4, short side);
//     2. group scale = min of member scales (barrel effect);
//     3. biaxial refinement (x then y) while all members still pass;
//     4. targets assigned.
//   quality == 1 (lossless): all scaling skipped, islands copied.
// No-atlas mode  无图集模式：
//   every texture is whole-image scaled by the same binary search evaluated
//   over all its island regions.
// ============================================================================

#region

using System.Collections.Generic;
using nadena.dev.ndmf;
using net.fosa.AvatarTextureOptimizer.Editor.Analysis;
using net.fosa.AvatarTextureOptimizer.Editor.Core;
using UnityEngine;

#endregion

namespace net.fosa.AvatarTextureOptimizer.Editor.Quality
{
    public static class QualityStageImpl
    {
        public static void Execute(ATOContext ctx, BuildContext context)
        {
            var c = ctx.Component;
            var log = ctx.Log;
            var an = ctx.Analysis;
            if (an == null) return;

            float quality = ATOQualityTierMap.GetQuality(c.QualityTier, c.CustomQuality);
            var params0 = c.QualityTier == ATOQualityTier.Custom
                ? c.CustomParams.Clone()
                : ATOQualityParams.FromQuality(quality);
            bool lossless = quality >= 0.999f;

            int minDensity = c.MinDensity;
            int maxDensity = c.MaxDensity;
            if (minDensity > maxDensity) (minDensity, maxDensity) = (maxDensity, minDensity);

            var decoder = new RegionDecoder(an);
            try
            {
                if (c.GenerateAtlas)
                {
                    int gi = 0;
                    foreach (var group in an.UVGroups)
                    {
                        ctx.Session.Check("Quality 质量缩放");
                        ctx.Session.SetProgress((float) gi / an.UVGroups.Count);
                        gi++;
                        if (group.Islands.Count > 0 && group.Islands[0].NoRemap) continue;
                        // UV 组含白名单 -> 保持原 UV，走整图缩放路径
                        ProcessUVGroup(ctx, group, params0, lossless, minDensity, maxDensity, decoder);
                        // release this group's regions  释放本组区域
                        foreach (var island in group.Islands)
                        {
                            foreach (var tid in island.SampledTextureIds)
                            {
                                decoder.Dispose((island.Id, tid));
                            }
                        }
                    }
                    // atlas-disabled textures (share UV with whitelist) get
                    // whole-image scaling  图集禁用贴图走整图缩放
                    foreach (var (tid, tref) in an.Textures)
                    {
                        if (tref.Whitelisted || !tref.AtlasDisabled) continue;
                        if (an.WholeTextureScales.ContainsKey(tid)) continue;
                        ctx.Session.Check("Quality 质量缩放");
                        ProcessWholeTexture(ctx, tid, params0, lossless, minDensity, maxDensity, decoder);
                    }
                }
                else
                {
                    int ti = 0;
                    foreach (var (tid, tref) in an.Textures)
                    {
                        if (tref.Whitelisted) continue;
                        ctx.Session.Check("Quality 质量缩放");
                        ctx.Session.SetProgress((float) ti / an.Textures.Count);
                        ti++;
                        ProcessWholeTexture(ctx, tid, params0, lossless, minDensity, maxDensity, decoder);
                    }
                }
            }
            finally
            {
                decoder.DisposeAll();
            }

            log.Info(ATOLogMask.Quality,
                $"quality done: {an.IslandScales.Count} island scales, " +
                $"{an.WholeTextureScales.Count} whole-texture scales (lossless={lossless}). 质量缩放完成。");
        }

        // ------------------------------------------------------------------
        private static void ProcessUVGroup(
            ATOContext ctx, ATOUVGroup group, ATOQualityParams p, bool lossless,
            int minDensity, int maxDensity, RegionDecoder decoder)
        {
            var an = ctx.Analysis;

            // (island, tex) -> allowed (passing, within budget) pixel size
            // (岛, 贴图) -> 允许（达标且在预算内）的像素尺寸
            var allowed = new Dictionary<(int, int), (int w, int h)>();
            var pure = new HashSet<(int, int)>();

            foreach (var island in group.Islands)
            {
                foreach (var tid in island.SampledTextureIds)
                {
                    var info = island.UVSet.Material != null && an.Materials.TryGetValue(island.UVSet.Material, out var mi) ? mi : null;
                    int alphaMode = info != null ? info.AlphaMode : 0;
                    float cutoff = info != null ? info.CutoffMin : 0.5f;
                    var category = CategoryOf(an, island, tid, alphaMode);

                    var region = decoder.Decode(island, tid);
                    int origW = region.W, origH = region.H;
                    var key = (island.Id, tid);

                    if (lossless)
                    {
                        allowed[key] = (origW, origH);
                        continue;
                    }

                    byte[] coverage = CoverageRasterizer.Rasterize(island, origW, origH);

                    if (IsPureColor(region, coverage))
                    {
                        pure.Add(key);
                        int t = Mathf.Min(4, Mathf.Min(origW, origH));
                        allowed[key] = (t, t);
                        continue;
                    }

                    var (aw, ah) = BinarySearchAllowed(ctx, island, tid, p, minDensity, maxDensity, region, coverage, category, alphaMode, cutoff);
                    allowed[key] = (aw, ah);
                }
            }

            // barrel effect: group layout scale K (px per UV unit)
            // 木桶效应：组布局比例 K（每 UV 单位像素）
            float kx = float.MaxValue, ky = float.MaxValue;
            foreach (var island in group.Islands)
            {
                foreach (var tid in island.SampledTextureIds)
                {
                    var key = (island.Id, tid);
                    if (pure.Contains(key)) continue;
                    var (aw, ah) = allowed[key];
                    float uvW = Mathf.Max(1e-6f, island.MaxUV.x - island.MinUV.x);
                    float uvH = Mathf.Max(1e-6f, island.MaxUV.y - island.MinUV.y);
                    kx = Mathf.Min(kx, aw / uvW);
                    ky = Mathf.Min(ky, ah / uvH);
                }
            }
            if (lossless)
            {
                // lossless: K = min of members' original px per UV (barrel).
                // Members may be (slightly) resampled to keep the shared
                // normalized layout; never upscaled beyond 2x original.
                // 近无损：K = 成员原始每 UV 像素最小值（木桶）；允许为保持共
                // 享归一化布局而（轻微）重采样，放大不超过 2 倍原始。
                float kxMin = float.MaxValue, kyMin = float.MaxValue;
                foreach (var island in group.Islands)
                {
                    foreach (var tid in island.SampledTextureIds)
                    {
                        if (pure.Contains((island.Id, tid))) continue;
                        var region = decoder.Decode(island, tid);
                        kxMin = Mathf.Min(kxMin, region.W / Mathf.Max(1e-6f, island.MaxUV.x - island.MinUV.x));
                        kyMin = Mathf.Min(kyMin, region.H / Mathf.Max(1e-6f, island.MaxUV.y - island.MinUV.y));
                    }
                }
                kx = float.IsFinite(kxMin) ? kxMin : 1f;
                ky = float.IsFinite(kyMin) ? kyMin : 1f;
                group.LayoutKx = kx;
                group.LayoutKy = ky;
                AssignTargets(an, group, kx, ky, pure, allowed, decoder, lossless);
                return;
            }
            if (kx == float.MaxValue) { kx = 1f; ky = 1f; }

            // biaxial refinement  双轴细化
            kx = RefineAxis(ctx, group, kx, ky, 0, p, decoder, pure);
            ky = RefineAxis(ctx, group, kx, ky, 1, p, decoder, pure);
            group.LayoutKx = kx;
            group.LayoutKy = ky;
            AssignTargets(an, group, kx, ky, pure, allowed, decoder, lossless);
        }

        /// <summary>Assigns final pixel targets = UV size * K (pure color ->
        /// min(4, short side)). 分配最终像素目标 = UV 尺寸 * K（纯色 -> min(4, 短边)）。</summary>
        private static void AssignTargets(
            ATOAnalysis an, ATOUVGroup group, float kx, float ky,
            HashSet<(int, int)> pure, Dictionary<(int, int), (int w, int h)> allowed,
            RegionDecoder decoder, bool lossless)
        {
            foreach (var island in group.Islands)
            {
                foreach (var tid in island.SampledTextureIds)
                {
                    var key = (island.Id, tid);
                    var region = decoder.Decode(island, tid);
                    if (pure.Contains(key))
                    {
                        var (w, h) = allowed[key];
                        an.IslandScales[key] = (w, h);
                        an.PureColorIslands.Add(key);
                    }
                    else
                    {
                        float uvW = island.MaxUV.x - island.MinUV.x;
                        float uvH = island.MaxUV.y - island.MinUV.y;
                        int wMax = lossless ? region.W * 2 : region.W;
                        int hMax = lossless ? region.H * 2 : region.H;
                        int w = Mathf.Clamp(Mathf.RoundToInt(uvW * kx), 4, wMax);
                        int h = Mathf.Clamp(Mathf.RoundToInt(uvH * ky), 4, hMax);
                        an.IslandScales[key] = (w, h);
                    }
                }
            }
        }

        /// <summary>Biaxial refinement of one K axis while all non-pure
        /// members still pass. 单轴双轴细化：所有非纯成员仍达标时提升 K 轴。</summary>
        private static float RefineAxis(
            ATOContext ctx, ATOUVGroup group, float kx, float ky, int axis,
            ATOQualityParams p, RegionDecoder decoder, HashSet<(int, int)> pure)
        {
            var an = ctx.Analysis;
            // upper bound: min budget cap of members  上界：成员预算上限最小值
            float hi = float.MaxValue;
            foreach (var island in group.Islands)
            {
                foreach (var tid in island.SampledTextureIds)
                {
                    var key = (island.Id, tid);
                    if (pure.Contains(key)) continue;
                    var region = decoder.Decode(island, tid);
                    hi = Mathf.Min(hi, (axis == 0 ? region.W : region.H) /
                                       Mathf.Max(1e-6f, axis == 0 ? island.MaxUV.x - island.MinUV.x : island.MaxUV.y - island.MinUV.y));
                }
            }
            if (!float.IsFinite(hi) || hi <= (axis == 0 ? kx : ky)) return axis == 0 ? kx : ky;

            float lo = axis == 0 ? kx : ky;
            for (int i = 0; i < 12 && hi - lo > 1e-3f; i++)
            {
                float mid = (lo + hi) * 0.5f;
                var (tx, ty) = axis == 0 ? (mid, ky) : (kx, mid);
                if (GroupPassesK(ctx, group, tx, ty, p, decoder, pure)) lo = mid;
                else hi = mid;
            }
            return lo;
        }

        // ------------------------------------------------------------------
        private static bool GroupPassesK(
            ATOContext ctx, ATOUVGroup group, float kx, float ky, ATOQualityParams p,
            RegionDecoder decoder, HashSet<(int, int)> pure)
        {
            var an = ctx.Analysis;
            foreach (var island in group.Islands)
            {
                foreach (var tid in island.SampledTextureIds)
                {
                    var key = (island.Id, tid);
                    if (pure.Contains(key)) continue;
                    var region = decoder.Decode(island, tid);
                    int w = Mathf.Clamp(Mathf.RoundToInt((island.MaxUV.x - island.MinUV.x) * kx), 4, region.W);
                    int h = Mathf.Clamp(Mathf.RoundToInt((island.MaxUV.y - island.MinUV.y) * ky), 4, region.H);
                    if (w >= region.W && h >= region.H) continue; // original = always passes
                    // 原尺寸 = 恒过
                    byte[] coverage = CoverageRasterizer.Rasterize(island, region.W, region.H);
                    var info = an.Materials.TryGetValue(island.UVSet.Material, out var mi) ? mi : null;
                    int alphaMode = info != null ? info.AlphaMode : 0;
                    float cutoff = info != null ? info.CutoffMin : 0.5f;
                    var category = CategoryOf(an, island, tid, alphaMode);
                    var scaled = Bilinear.Resample(region.RGBA, region.W, region.H, w, h);
                    var up = Bilinear.Resample(scaled, w, h, region.W, region.H);
                    if (!Metrics.Evaluate(region.RGBA, region.W, region.H, coverage, up, region.W, region.H,
                            category, alphaMode, cutoff, p, out _))
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        // ------------------------------------------------------------------
        /// <summary>Binary search for the largest (w,h) within the density
        /// budget that passes all metrics. 二分搜索预算内全部达标的最大 (w,h)。</summary>
        private static (int w, int h) BinarySearchAllowed(
            ATOContext ctx, ATOUVIsland island, int tid, ATOQualityParams p,
            int minDensity, int maxDensity, ATORegion region, byte[] coverage,
            ATOTextureCategory category, int alphaMode, float cutoff)
        {
            int origW = region.W, origH = region.H;
            // density budget  密度预算
            float factors = island.UVSet.MaxScaleArea * island.UVSet.ShapeKeyArea;
            float wBudget = (island.MaxUV.x - island.MinUV.x) * island.UVSet.MetersPerUV * maxDensity * factors;
            float hBudget = (island.MaxUV.y - island.MinUV.y) * island.UVSet.MetersPerUV * maxDensity * factors;
            int wMax = Mathf.Clamp(Mathf.RoundToInt(Mathf.Min(origW, wBudget)), 4, origW);
            int hMax = Mathf.Clamp(Mathf.RoundToInt(Mathf.Min(origH, hBudget)), 4, origH);
            if (wMax >= origW && hMax >= origH) return (origW, origH); // original fits 原尺寸在预算内

            // first check minimum  先检查最小值
            if (!PassAt(region, coverage, category, alphaMode, cutoff, p, 4, 4))
            {
                // even minimum fails - accept minimum with warning
                // 最小值都不达标 - 接受最小值并警告
                ctx.Log.Warn(ATOLogMask.Quality,
                    $"island #{island.Id} tex #{tid}: quality fails even at 4px - using minimum. 4px 均不达标，取最小值。");
                return (4, 4);
            }
            int lo = 4, hi = wMax;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) / 2;
                int midH = Mathf.Max(4, Mathf.RoundToInt(mid * (float) hMax / wMax));
                if (PassAt(region, coverage, category, alphaMode, cutoff, p, mid, midH)) lo = mid;
                else hi = mid;
            }
            int hAt = Mathf.Max(4, Mathf.RoundToInt(lo * (float) hMax / wMax));
            return (lo, hAt);
        }

        private static bool PassAt(
            ATORegion region, byte[] coverage, ATOTextureCategory category,
            int alphaMode, float cutoff, ATOQualityParams p, int w, int h)
        {
            w = Mathf.Clamp(w, 4, region.W);
            h = Mathf.Clamp(h, 4, region.H);
            if (w >= region.W && h >= region.H) return true; // original 原尺寸
            var scaled = Bilinear.Resample(region.RGBA, region.W, region.H, w, h);
            var up = Bilinear.Resample(scaled, w, h, region.W, region.H);
            return Metrics.Evaluate(region.RGBA, region.W, region.H, coverage, up, region.W, region.H,
                category, alphaMode, cutoff, p, out _);
        }

        // ------------------------------------------------------------------
        private static void ProcessWholeTexture(
            ATOContext ctx, int tid, ATOQualityParams p, bool lossless,
            int minDensity, int maxDensity, RegionDecoder decoder)
        {
            var an = ctx.Analysis;
            var tref = an.Textures[tid];

            // gather islands of this texture  收集该贴图的岛
            var islands = new List<ATOUVIsland>();
            foreach (var island in an.Islands)
            {
                if (island.SampledTextureIds.Contains(tid)) islands.Add(island);
            }
            if (islands.Count == 0)
            {
                an.WholeTextureScales[tid] = 1f;
                return;
            }

            if (lossless)
            {
                an.WholeTextureScales[tid] = 1f;
                return;
            }

            // density budget from the largest island's mesh  密度预算取最大岛的网格
            float metersPerUV = 0f;
            float factors = 1f;
            foreach (var island in islands)
            {
                metersPerUV = Mathf.Max(metersPerUV, island.UVSet.MetersPerUV);
                factors = Mathf.Max(factors, island.UVSet.MaxScaleArea * island.UVSet.ShapeKeyArea);
            }
            float uvSpan = tref.Texture.width > 0 ? 1f : 1f; // whole texture spans its own UVs
            // 整图缩放上限：密度预算  整图缩放上限：密度预算
            float budget = uvSpan * metersPerUV * maxDensity * factors;
            float sMax = Mathf.Clamp01(budget / tref.Texture.width);
            if (sMax >= 1f)
            {
                an.WholeTextureScales[tid] = 1f;
                return;
            }

            // binary search on whole scale  整图缩放二分
            float lo = 0.015625f, hi = sMax;
            if (!WholePasses(ctx, islands, tid, p, 0.015625f, decoder))
            {
                an.WholeTextureScales[tid] = lo;
                ctx.Log.Warn(ATOLogMask.Quality,
                    $"texture #{tid} ({tref.Texture.name}) fails at minimum scale - using minimum. 最小缩放均不达标。");
                return;
            }
            for (int i = 0; i < 14 && hi - lo > 1e-4f; i++)
            {
                float mid = (lo + hi) * 0.5f;
                if (WholePasses(ctx, islands, tid, p, mid, decoder)) lo = mid;
                else hi = mid;
            }
            an.WholeTextureScales[tid] = lo;
        }

        private static bool WholePasses(
            ATOContext ctx, List<ATOUVIsland> islands, int tid, ATOQualityParams p,
            float s, RegionDecoder decoder)
        {
            var an = ctx.Analysis;
            foreach (var island in islands)
            {
                var info = an.Materials.TryGetValue(island.UVSet.Material, out var mi) ? mi : null;
                int alphaMode = info != null ? info.AlphaMode : 0;
                float cutoff = info != null ? info.CutoffMin : 0.5f;
                var category = CategoryOf(an, island, tid, alphaMode);
                var region = decoder.Decode(island, tid);
                int w = Mathf.Clamp(Mathf.RoundToInt(region.W * s), 4, region.W);
                int h = Mathf.Clamp(Mathf.RoundToInt(region.H * s), 4, region.H);
                if (w >= region.W && h >= region.H) continue;
                byte[] coverage = CoverageRasterizer.Rasterize(island, region.W, region.H);
                var scaled = Bilinear.Resample(region.RGBA, region.W, region.H, w, h);
                var up = Bilinear.Resample(scaled, w, h, region.W, region.H);
                if (!Metrics.Evaluate(region.RGBA, region.W, region.H, coverage, up, region.W, region.H,
                        category, alphaMode, cutoff, p, out _))
                {
                    return false;
                }
            }
            return true;
        }

        // ------------------------------------------------------------------
        /// <summary>Category of one texture as used by one island: from the
        /// role in the island's material + the material's alpha mode.
        /// 单个贴图在单个岛上使用的类别：来自岛材质中的角色 + 材质透明模式。</summary>
        public static ATOTextureCategory CategoryOf(ATOAnalysis an, ATOUVIsland island, int tid, int alphaMode)
        {
            var mat = island.UVSet.Material;
            if (mat != null && an.Materials.TryGetValue(mat, out var info))
            {
                foreach (var (prop, pref) in info.PropertyRefs)
                {
                    if (!info.Textures.TryGetValue(prop, out var tex)) continue;
                    if (!(tex is Texture2D t2d)) continue;
                    if (!an.TextureDedupMap.TryGetValue(t2d, out var did)) continue;
                    if (did != tid) continue;
                    switch (pref.Role)
                    {
                        case Api.ATOTextureRole.Normal: return ATOTextureCategory.Normal;
                        case Api.ATOTextureRole.Mask: return ATOTextureCategory.Gray;
                        case Api.ATOTextureRole.Emission:
                        case Api.ATOTextureRole.Albedo:
                        case Api.ATOTextureRole.Utility:
                            return alphaMode == 0 ? ATOTextureCategory.Opaque : ATOTextureCategory.Transparent;
                    }
                }
            }
            return alphaMode == 0 ? ATOTextureCategory.Opaque : ATOTextureCategory.Transparent;
        }

        private static bool IsPureColor(ATORegion region, byte[] coverage)
        {
            float sr = 0, sg = 0, sb = 0, sa = 0;
            int n = 0;
            int step = Mathf.Max(1, region.W * region.H / 2048);
            for (int i = 0; i < region.W * region.H; i++)
            {
                if (coverage[i] == 0 || i % step != 0) continue;
                int o = i * 4;
                sr += region.RGBA[o];
                sg += region.RGBA[o + 1];
                sb += region.RGBA[o + 2];
                sa += region.RGBA[o + 3];
                n++;
            }
            if (n < 4) return false;
            sr /= n;
            sg /= n;
            sb /= n;
            sa /= n;
            for (int i = 0; i < region.W * region.H; i++)
            {
                if (coverage[i] == 0 || i % step != 0) continue;
                int o = i * 4;
                if (Mathf.Abs(region.RGBA[o] - sr) > 1e-3f ||
                    Mathf.Abs(region.RGBA[o + 1] - sg) > 1e-3f ||
                    Mathf.Abs(region.RGBA[o + 2] - sb) > 1e-3f ||
                    Mathf.Abs(region.RGBA[o + 3] - sa) > 1e-3f)
                {
                    return false;
                }
            }
            return true;
        }

    }
}
