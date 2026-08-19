using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using Fosa.AvatarTextureOptimizer.Editor.Islands;

namespace Fosa.AvatarTextureOptimizer.Editor.Quality
{
    // 质量缩放编排器：为每个 UV 岛/贴图使用确定缩放系数。
    // - 均匀二分搜索（Burst 并行逐迭代）；各向异性：先均匀达标，再双轴独立二分细化（仅图集化岛）。
    // - 密度钳制：> max 密度 → 缩放上限（防浪费）；< min 密度 → 下限 1（防发糊）。
    // - 全岛纯色（含 alpha 一致）在目标质量 < 1 时短路缩到 min(4, 短边)。
    // - 该贴图类型目标质量为 1（无损）→ 该使用不贡献缩小；全岛无损 → 跳过缩放原样拷贝。
    // Quality scaling orchestrator: determines the scale factor per island/texture use.
    // - Uniform binary search (Burst-parallel per iteration); anisotropic: uniform first, then per-axis refinement (atlased islands only).
    // - Density clamps: > max → shrink cap (anti-waste); < min → floor 1 (anti-blur).
    // - All-pure islands (incl. uniform alpha) shortcut to min(4, short side) when target quality < 1.
    // - Lossless kind (target quality = 1) → no shrink contribution from that use; all-lossless islands copy as-is.
    internal static class IslandScaler
    {
        private const int Iterations = 12;
        private const float MinScale = 1f / 256f;

        // 单个评估使用（托管侧）。One evaluation use (managed side).
        private sealed class EvalUse
        {
            public IslandEntity island;
            public IslandUse use;
            public int evalIndex;
            public float floor, cap;   // 密度范围。Density range.
            public float lo, hi;       // 二分区间。Bisection interval.
            public float resultScale = 1f;
            public bool fixedScale;    // 纯色/无损/范围冲突直接确定。Fixed by pure-color/lossless/range conflict.
        }

        public static void Scale(ATOContext ctx, ATOReport.Stage stage)
        {
            bool atlasMode = ctx.settings.generateAtlas;
            var metrics = ctx.metrics;
            var cache = new TextureCache();

            try
            {
                // 0) 世界面积（含动画缩放与形态键 0/100 因子）。World areas (incl. animated scale and blend-shape factors).
                WorldArea.ResetCache();
                foreach (var e in ctx.islandEntities)
                {
                    ctx.CheckCancelled();
                    float area;
                    Vector2 extents;
                    WorldArea.ComputeIslandArea(ctx, e, out area, out extents);
                    e.worldArea = math.max(area, 1e-6f);
                }

                // 1) 收集评估使用并加载贴图（去重链解析为规范条目）。Collect eval uses and load textures (dedup chain → canonical).
                var evals = new List<EvalUse>();
                int evalIndex = 0;
                foreach (var e in ctx.islandEntities)
                {
                    if (e.whitelistedFull) continue;
                    foreach (var u in e.uses)
                    {
                        if (u.whitelistLevel == Analysis.ATOWhitelistLevel.Full) continue;
                        var entry = ResolveCanonical(u.texture);
                        if (entry == null) continue;
                        u.texture = entry;
                        evals.Add(new EvalUse { island = e, use = u, evalIndex = evalIndex++ });
                        cache.Load(entry, NeedsPremultiply(entry));
                    }
                }

                int total = evals.Count;
                var evalData = new NativeArray<UseEvalData>(total, Allocator.TempJob);
                var scales = new NativeArray<float>(total, Allocator.TempJob);
                var margins = new NativeArray<float>(total, Allocator.TempJob);
                var active = new NativeArray<int>(total, Allocator.TempJob);

                try
                {
                    FillEvalData(evals, cache, evalData);

                    // 2) 无损 / 密度范围。Lossless / density range.
                    int losslessCount = 0;
                    foreach (var eu in evals)
                    {
                        ctx.CheckCancelled();
                        var e = eu.island;
                        var u = eu.use;
                        var entry = u.texture;

                        // 无损 → 该使用不贡献缩小（原样拷贝由应用阶段处理）。Lossless → no shrink contribution (copy as-is at apply).
                        if (IsKindLossless(u.kind, entry.worstAlphaMode, metrics))
                        {
                            eu.fixedScale = true;
                            eu.resultScale = 1f;
                            losslessCount++;
                            continue;
                        }

                        // 密度钳制。Density clamps.
                        float pw = math.max(1f, (e.uvMax.x - e.uvMin.x) * entry.width);
                        float ph = math.max(1f, (e.uvMax.y - e.uvMin.y) * entry.height);
                        float density = math.sqrt(pw * ph) / math.sqrt(e.worldArea);
                        float cap = density > ctx.settings.maxDensityPxPerMeter ? ctx.settings.maxDensityPxPerMeter / density : 1f;
                        float floor = density < ctx.settings.minDensityPxPerMeter ? 1f : math.min(1f, ctx.settings.minDensityPxPerMeter / density);
                        eu.cap = math.clamp(cap, MinScale, 1f);
                        eu.floor = math.clamp(floor, MinScale, 1f);
                        eu.lo = eu.floor;
                        eu.hi = eu.cap;
                    }

                    // 3) 岛级纯色短路（全部使用均纯色，仅图集化贴图；目标质量 < 1）。
                    // Island-level pure-color shortcut (all uses pure; atlased textures only; target quality < 1).
                    int pureCount = 0;
                    foreach (var e in ctx.islandEntities)
                    {
                        if (e.whitelistedFull || e.noAtlasFallback || e.typeGroupId < 0) continue;
                        bool allPure = true;
                        float minPureScale = 1f;
                        foreach (var u in e.uses)
                        {
                            if (u.whitelistLevel == Analysis.ATOWhitelistLevel.Full) continue;
                            var entry = ResolveCanonical(u.texture);
                            if (entry == null) { allPure = false; break; }
                            if (IsNoAtlasTexture(entry) || !atlasMode) { allPure = false; break; }
                            if (IsKindLossless(u.kind, entry.worstAlphaMode, metrics)) { allPure = false; break; }
                            if (!IsPureColor(cache, entry, e)) { allPure = false; break; }
                            int shortSide = math.min(e.pixelWidth, e.pixelHeight);
                            int target = math.min(ATOConstants.PureColorMinSize, math.max(1, shortSide));
                            float s = shortSide <= target ? 1f : (float)target / shortSide;
                            minPureScale = math.min(minPureScale, s);
                        }
                        if (allPure)
                        {
                            e.pureColor = true;
                            e.skipQuality = true;
                            e.scaleX = minPureScale;
                            e.scaleY = minPureScale;
                            foreach (var eu in evals)
                            {
                                if (eu.island == e)
                                {
                                    eu.fixedScale = true;
                                    eu.resultScale = 1f; // 使用级不设缩小；岛级统一短路。Use-level unchanged; island-level shortcut applies.
                                }
                            }
                            pureCount++;
                        }
                    }

                    // 4) 岛级范围冲突：floor > cap → 取 floor（质量优先）。Island-level conflict: floor > cap → floor wins (quality first).
                    foreach (var e in ctx.islandEntities)
                    {
                        float f = 0f, c = 1f;
                        foreach (var eu in evals)
                        {
                            if (eu.island != e || eu.fixedScale) continue;
                            f = math.max(f, eu.floor);
                            c = math.min(c, eu.cap);
                        }
                        if (f > c)
                        {
                            foreach (var eu in evals)
                            {
                                if (eu.island == e && !eu.fixedScale)
                                {
                                    eu.fixedScale = true;
                                    eu.resultScale = f;
                                }
                            }
                            ATOLog.Debug(string.Format("岛密度范围冲突 / density range conflict: {0}, scale={1:F3}", e, f));
                        }
                        e.densityCap = c;
                    }

                    // 5) 均匀二分搜索（并行逐迭代）。Uniform binary search (parallel per iteration).
                    int activeCount = 0;
                    foreach (var eu in evals)
                    {
                        if (!eu.fixedScale) active[activeCount++] = eu.evalIndex;
                    }
                    var m = ToBurstMetrics(metrics);
                    for (int it = 0; it < Iterations && activeCount > 0; it++)
                    {
                        ctx.CheckCancelled();
                        foreach (var eu in evals)
                        {
                            if (!eu.fixedScale) scales[eu.evalIndex] = (eu.lo + eu.hi) * 0.5f;
                        }
                        var job = new ActiveEvalJob
                        {
                            active = active.GetSubArray(0, activeCount),
                            pool = cache.Pool,
                            uses = evalData,
                            scales = scales,
                            m = m,
                            margins = margins
                        };
                        job.Schedule(activeCount, 32).Complete();

                        bool converged = true;
                        foreach (var eu in evals)
                        {
                            if (eu.fixedScale) continue;
                            float margin = margins[eu.evalIndex];
                            float mid = (eu.lo + eu.hi) * 0.5f;
                            if (margin <= 1f) eu.lo = mid;
                            else eu.hi = mid;
                            if (eu.hi - eu.lo > 1f / 256f) converged = false;
                        }
                        if (converged) break;
                    }

                    foreach (var eu in evals)
                    {
                        if (!eu.fixedScale) eu.resultScale = eu.lo;
                        eu.use.useScale = eu.resultScale;
                    }

                    // 6) 各向异性细化（图集化岛、非纯色、非全无损）：先均匀达标，再双轴独立二分。
                    // Anisotropic refinement (atlased, non-pure, not-all-lossless islands): uniform first, then per-axis bisection.
                    if (atlasMode) AnisotropicRefine(ctx, cache, evals, evalData, m, stage);

                    // 7) 岛级结果汇总。Island-level result summary.
                    foreach (var e in ctx.islandEntities)
                    {
                        if (e.whitelistedFull || e.noAtlasFallback) continue;
                        if (e.skipQuality) continue; // 纯色已设置。Pure-color already set.
                        if (e.typeGroupId < 0) continue;
                        float minU = 1f;
                        bool anyLossy = false;
                        foreach (var u in e.uses)
                        {
                            if (u.whitelistLevel == Analysis.ATOWhitelistLevel.Full) continue;
                            minU = math.min(minU, u.useScale);
                            if (u.useScale < 1f) anyLossy = true;
                        }
                        if (!anyLossy)
                        {
                            // 全无损 → 跳过缩放原样拷贝。All lossless → skip scaling, copy as-is.
                            e.skipQuality = true;
                            e.scaleX = 1f;
                            e.scaleY = 1f;
                        }
                        else
                        {
                            // 各向异性结果下限 = 均匀结果（木桶），上限 = 密度上限（防浪费）。
                            // Anisotropic result: lower bound = uniform result (bucket), upper bound = density cap (anti-waste).
                            e.scaleX = math.clamp(e.scaleX, minU, math.max(minU, e.densityCap));
                            e.scaleY = math.clamp(e.scaleY, minU, math.max(minU, e.densityCap));
                        }
                    }

                    // 8) 整图缩放目标：为所有规范贴图计算（是否应用由 Fallback 阶段按
                    // 无图集模式 / NoAtlas / 装箱失败回退 判定）。Whole-texture scale targets:
                    // computed for every canonical texture; applicability is decided later by the fallback stage.
                    foreach (var entry in ctx.textures)
                    {
                        var canon = ResolveCanonical(entry);
                        if (canon == null || canon.whitelistLevel == Analysis.ATOWhitelistLevel.Full) continue;
                        float minScale = 1f;
                        bool any = false;
                        foreach (var eu in evals)
                        {
                            if (eu.use.texture != canon) continue;
                            any = true;
                            minScale = math.min(minScale, eu.resultScale);
                        }
                        if (any) canon.wholeTextureScale = minScale;
                    }

                    stage.AddLine(string.Format(ATOLocalization.Tr("log.scaleSummary"), total, pureCount, losslessCount));
                }
                finally
                {
                    evalData.Dispose();
                    scales.Dispose();
                    margins.Dispose();
                    active.Dispose();
                }
            }
            finally
            {
                cache.Dispose();
            }
        }

        // 各向异性细化。Anisotropic refinement.
        private static void AnisotropicRefine(ATOContext ctx, TextureCache cache, List<EvalUse> evals,
            NativeArray<UseEvalData> evalData, BurstMetrics m, ATOReport.Stage stage)
        {
            // 按岛分组（仅图集化、非纯色、非全无损）。Group by island (atlased, non-pure, not-all-lossless).
            var byIsland = new Dictionary<IslandEntity, List<EvalUse>>();
            foreach (var eu in evals)
            {
                var e = eu.island;
                if (e.typeGroupId < 0 || e.pureColor || e.skipQuality) continue;
                if (eu.fixedScale && eu.resultScale >= 1f) continue; // 无损使用不参与。Lossless uses don't participate.
                List<EvalUse> list;
                if (!byIsland.TryGetValue(e, out list))
                {
                    list = new List<EvalUse>();
                    byIsland[e] = list;
                }
                list.Add(eu);
            }

            var islands = new List<IslandEntity>(byIsland.Keys);
            int n = islands.Count;
            if (n == 0) return;

            // 紧凑使用数组。Compact use arrays.
            int cursor = 0;
            var useStart = new NativeArray<int>(n, Allocator.TempJob);
            var useCount = new NativeArray<int>(n, Allocator.TempJob);
            for (int i = 0; i < n; i++)
            {
                var list = byIsland[islands[i]];
                useStart[i] = cursor;
                useCount[i] = list.Count;
                cursor += list.Count;
            }
            var compactData = new NativeArray<UseEvalData>(cursor, Allocator.TempJob);
            int cc = 0;
            for (int i = 0; i < n; i++)
            {
                foreach (var eu in byIsland[islands[i]])
                {
                    compactData[cc++] = evalData[eu.evalIndex];
                }
            }
            var scalesX = new NativeArray<float>(n, Allocator.TempJob);
            var scalesY = new NativeArray<float>(n, Allocator.TempJob);
            var islandMargins = new NativeArray<float>(n, Allocator.TempJob);
            var loX = new NativeArray<float>(n, Allocator.TempJob);
            var hiX = new NativeArray<float>(n, Allocator.TempJob);
            var loY = new NativeArray<float>(n, Allocator.TempJob);
            var hiY = new NativeArray<float>(n, Allocator.TempJob);

            try
            {
                // 初始：每岛均匀结果（各使用中的最小值）。Initial: uniform result per island (min over its uses).
                for (int i = 0; i < n; i++)
                {
                    var e = islands[i];
                    float minU = 1f;
                    foreach (var eu in byIsland[e]) minU = math.min(minU, eu.resultScale);
                    e.scaleX = minU;
                    e.scaleY = minU;
                    loX[i] = minU;
                    hiX[i] = e.densityCap;
                    loY[i] = minU;
                    hiY[i] = e.densityCap;
                }

                // X 轴二分。X-axis bisection.
                for (int it = 0; it < Iterations; it++)
                {
                    ctx.CheckCancelled();
                    for (int i = 0; i < n; i++)
                    {
                        scalesX[i] = (loX[i] + hiX[i]) * 0.5f;
                        scalesY[i] = islands[i].scaleY;
                    }
                    RunAniso(cache, compactData, useStart, useCount, scalesX, scalesY, m, islandMargins, n);
                    for (int i = 0; i < n; i++)
                    {
                        if (islandMargins[i] <= 1f) loX[i] = scalesX[i];
                        else hiX[i] = scalesX[i];
                    }
                }
                for (int i = 0; i < n; i++) islands[i].scaleX = loX[i];

                // Y 轴二分。Y-axis bisection.
                for (int it = 0; it < Iterations; it++)
                {
                    ctx.CheckCancelled();
                    for (int i = 0; i < n; i++)
                    {
                        scalesX[i] = islands[i].scaleX;
                        scalesY[i] = (loY[i] + hiY[i]) * 0.5f;
                    }
                    RunAniso(cache, compactData, useStart, useCount, scalesX, scalesY, m, islandMargins, n);
                    for (int i = 0; i < n; i++)
                    {
                        if (islandMargins[i] <= 1f) loY[i] = scalesY[i];
                        else hiY[i] = scalesY[i];
                    }
                }
                for (int i = 0; i < n; i++) islands[i].scaleY = loY[i];
            }
            finally
            {
                useStart.Dispose();
                useCount.Dispose();
                compactData.Dispose();
                scalesX.Dispose();
                scalesY.Dispose();
                islandMargins.Dispose();
                loX.Dispose();
                hiX.Dispose();
                loY.Dispose();
                hiY.Dispose();
            }
        }

        private static void RunAniso(TextureCache cache, NativeArray<UseEvalData> compactData,
            NativeArray<int> useStart, NativeArray<int> useCount,
            NativeArray<float> scalesX, NativeArray<float> scalesY, BurstMetrics m,
            NativeArray<float> islandMargins, int n)
        {
            var job = new AnisoEvalJob
            {
                pool = cache.Pool,
                uses = compactData,
                useStart = useStart,
                useCount = useCount,
                scalesX = scalesX,
                scalesY = scalesY,
                m = m,
                margins = islandMargins
            };
            job.Schedule(n, 8).Complete();
        }

        private static void FillEvalData(List<EvalUse> evals, TextureCache cache, NativeArray<UseEvalData> data)
        {
            for (int i = 0; i < evals.Count; i++)
            {
                var eu = evals[i];
                var e = eu.island;
                var u = eu.use;
                var entry = u.texture;
                var info = cache.Get(entry);
                int w = entry.width, h = entry.height;
                int cx = Mathf.Clamp(Mathf.FloorToInt(e.uvMin.x * w), 0, w - 1);
                int cy = Mathf.Clamp(Mathf.FloorToInt(e.uvMin.y * h), 0, h - 1);
                int cw = Mathf.Clamp(Mathf.CeilToInt((e.uvMax.x - e.uvMin.x) * w) + 1, 1, w - cx);
                int ch = Mathf.Clamp(Mathf.CeilToInt((e.uvMax.y - e.uvMin.y) * h) + 1, 1, h - cy);

                var d = new UseEvalData
                {
                    texOffset = info.offset,
                    texW = info.width,
                    texH = info.height,
                    cropX = cx,
                    cropY = cy,
                    cropW = cw,
                    cropH = ch,
                    kind = (int)u.kind,
                    alphaMode = (int)u.alphaMode,
                    usedChannels = info.usedChannels,
                    dxt5nm = info.dxt5nm,
                    cutoffCount = 1
                };
                unsafe
                {
                    d.cutoffs[0] = u.cutoff;
                }
                data[i] = d;
            }
        }

        // 纯色检测：采样检查岛覆盖区内全部通道是否一致（含 alpha；半精度容差）。Pure-color detection on the island crop (all channels, incl. alpha).
        private static bool IsPureColor(TextureCache cache, Analysis.TextureEntry entry, IslandEntity e)
        {
            var info = cache.Get(entry);
            int w = entry.width, h = entry.height;
            int cx = Mathf.Clamp(Mathf.FloorToInt(e.uvMin.x * w), 0, w - 1);
            int cy = Mathf.Clamp(Mathf.FloorToInt(e.uvMin.y * h), 0, h - 1);
            int cw = Mathf.Clamp(Mathf.CeilToInt((e.uvMax.x - e.uvMin.x) * w) + 1, 1, w - cx);
            int ch = Mathf.Clamp(Mathf.CeilToInt((e.uvMax.y - e.uvMin.y) * h) + 1, 1, h - cy);

            int n = cw * ch;
            int step = Mathf.Max(1, n / 4096);
            half4 first = cache.Pool[info.offset + cy * info.width + cx];
            const float tol = 2f / 255f;
            for (int i = 0; i < n; i += step)
            {
                int px = cx + (i % cw);
                int py = cy + (i / cw);
                var p = cache.Pool[info.offset + py * info.width + px];
                if (math.abs((float)p.x - (float)first.x) > tol ||
                    math.abs((float)p.y - (float)first.y) > tol ||
                    math.abs((float)p.z - (float)first.z) > tol ||
                    math.abs((float)p.w - (float)first.w) > tol) return false;
            }
            return true;
        }

        private static bool NeedsPremultiply(Analysis.TextureEntry entry)
        {
            return entry.kind == Analysis.ATOTextureKind.Color
                && (entry.worstAlphaMode == Analysis.ATOAlphaMode.Cutout || entry.worstAlphaMode == Analysis.ATOAlphaMode.Blend);
        }

        private static bool IsNoAtlasTexture(Analysis.TextureEntry entry)
        {
            return entry.whitelistLevel == Analysis.ATOWhitelistLevel.NoAtlas;
        }

        // 去重链解析。Dedup chain resolution.
        private static Analysis.TextureEntry ResolveCanonical(Analysis.TextureEntry entry)
        {
            var cur = entry;
            int guard = 0;
            while (cur != null && cur.dedupTarget != null && guard++ < 32) cur = cur.dedupTarget;
            return cur;
        }

        // 该贴图类型的阈值是否全无损。Whether the thresholds are lossless for this kind.
        private static bool IsKindLossless(Analysis.ATOTextureKind kind, Analysis.ATOAlphaMode alphaMode, ATOMetricThresholds m)
        {
            switch (kind)
            {
                case Analysis.ATOTextureKind.NormalMap:
                    return m.normalAngleDegP95 <= 0f;
                case Analysis.ATOTextureKind.Grayscale:
                case Analysis.ATOTextureKind.Mask:
                    return m.grayRMSE <= 0f;
                default:
                    bool colorLossless = m.msSsim >= 1f && m.deltaE2000 <= 0f;
                    if (alphaMode == Analysis.ATOAlphaMode.Cutout) return colorLossless && m.alphaIoU >= 1f;
                    if (alphaMode == Analysis.ATOAlphaMode.Blend) return colorLossless && m.alphaRMSE <= 0f;
                    return colorLossless;
            }
        }

        private static BurstMetrics ToBurstMetrics(ATOMetricThresholds m)
        {
            return new BurstMetrics
            {
                msSsim = m.msSsim,
                deltaE = m.deltaE2000,
                alphaIoU = m.alphaIoU,
                alphaRMSE = m.alphaRMSE,
                normalAngle = m.normalAngleDegP95,
                grayRMSE = m.grayRMSE
            };
        }
    }
}
