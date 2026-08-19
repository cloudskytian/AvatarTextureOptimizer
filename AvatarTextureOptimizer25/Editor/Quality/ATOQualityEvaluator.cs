// Avatar Texture Optimizer / 头像贴图优化器
// Per-island quality evaluation: GPU-resampled candidates are compared against
// the original via role-specific metrics; a binary search finds the smallest
// passing scale, then two independent axis refinements. UV-group consistency is
// achieved by taking the worst (largest) requirement across all member
// textures (木桶效应). Pixel-density bands and original-size clamps are applied.
// 逐岛质量评估：GPU 重采样候选与原图按角色指标对比；二分搜索最小达标缩放，
// 再做双轴独立细化。UV 组一致性通过对组内所有贴图取最差（最大）需求实现
// （木桶效应）。应用像素密度带宽钳制与原尺寸钳制。

using System;
using System.Collections.Generic;
using nadena.dev.ndmf;
using UnityEngine;

namespace FOSA.AvatarTextureOptimizer.Editor
{
    /// <summary>Sizing decision for one island (in original-texture pixel ratios). / 单岛尺寸决策（按原贴图像素比例）。</summary>
    public sealed class ATOIslandDecision
    {
        public ATOIsland island;
        public float ratioU = 1f;
        public float ratioV = 1f;
        public bool pureColor;
        public bool skipped;
        public string note;
        public long evalCount;
    }

    /// <summary>Result of evaluating one texture within one UV group. / 组内单贴图的评估结果。</summary>
    public sealed class ATOTextureEvalResult
    {
        public ATOTextureEntry texture;
        public readonly Dictionary<ATOIsland, ATOIslandDecision> decisions = new Dictionary<ATOIsland, ATOIslandDecision>();
    }

    /// <summary>
    /// Evaluates all UV groups and produces per-island scale ratios.
    /// 评估所有 UV 组并产出逐岛缩放比例。
    /// </summary>
    public sealed class ATOQualityEvaluator : IDisposable
    {
        private readonly AvatarTextureOptimizer _settings;
        private readonly ATOQualitySettings _quality;
        private readonly ATOGpuPipeline _gpu;
        private readonly ATOProgress _progress;
        // Caches keyed by island/texture identity (islands are unique per group).
        // 缓存以岛/贴图对象为键（岛在组内唯一，组间无碰撞）。
        private readonly Dictionary<(ATOIsland, int, int), bool[]> _maskCache = new Dictionary<(ATOIsland, int, int), bool[]>();
        private readonly Dictionary<(ATOIsland, ATOTextureEntry, int, int, bool), bool> _candidateCache =
            new Dictionary<(ATOIsland, ATOTextureEntry, int, int, bool), bool>();
        private int _evalCounter;

        public ATOQualityEvaluator(AvatarTextureOptimizer settings, ATOGpuPipeline gpu, ATOProgress progress)
        {
            _settings = settings;
            _quality = settings.EffectiveQuality();
            _gpu = gpu;
            _progress = progress;
        }

        public void Dispose() { /* pipeline owned by caller / 管线由调用方持有 */ }

        /// <summary>
        /// Evaluate all UV groups; returns per-group per-island ratios. Reports
        /// absolute progress inside [progressFrom, progressTo] so the bar is monotonic.
        /// 评估全部 UV 组，返回每组每岛比例。按 [progressFrom, progressTo] 绝对区间
        /// 上报进度，保证进度条单调不回退。
        /// </summary>
        public Dictionary<ATOUVGroup, Dictionary<ATOIsland, Vector2>> EvaluateAll(
            List<ATOUVGroup> groups, float progressFrom = 0f, float progressTo = 1f)
        {
            var result = new Dictionary<ATOUVGroup, Dictionary<ATOIsland, Vector2>>();
            int done = 0;
            string stageName = ATOLoc.T("ato:stage.quality");
            foreach (var group in groups)
            {
                _progress.ThrowIfCancelled();
                float frac = (float)done / Mathf.Max(1, groups.Count);
                _progress.Report(stageName, progressFrom + (progressTo - progressFrom) * frac,
                    group.mesh != null ? group.mesh.name : "?");
                result[group] = EvaluateGroup(group);
                done++;
            }
            return result;
        }

        /// <summary>Evaluate one UV group (all member textures, worst requirement wins). / 评估一个 UV 组（全成员贴图，取最差需求）。</summary>
        public Dictionary<ATOIsland, Vector2> EvaluateGroup(ATOUVGroup group)
        {
            using (new ATOLog.Step($"quality-group:{group.mesh?.name}#sm{group.submesh}#uv{group.uvChannel}"))
            {
                // Default: ratio 1 (no scaling) for islands nobody evaluates.
                // 默认：无人评估的岛比例为 1（不缩放）。
                var finalRatios = new Dictionary<ATOIsland, Vector2>();
                foreach (var isl in group.islands) finalRatios[isl] = Vector2.one;

                // Near-lossless fast path: no UV scaling at all. / 近无损快速路径：完全不缩放。
                if (_quality.targetQuality >= 0.999f)
                {
                    foreach (var isl in group.islands) finalRatios[isl] = Vector2.one;
                    return finalRatios;
                }

                // 木桶 fold: the group ratio is the WORST (largest) requirement across
                // member textures. The fold must start from the FIRST candidate, not
                // from the 1.0 default — folding max into a 1.0-initialized map would
                // pin every island at 1.0 and silently disable all scaling (QA-1).
                // 木桶折叠：组比例取成员贴图中最差（最大）需求。折叠必须以首个候选
                // 为起点，而非 1.0 默认值——若向 1.0 初始化的字典折叠 max，所有岛
                // 会被钉死在 1.0，缩放被静默禁用（QA-1 发现）。
                var folded = new HashSet<ATOIsland>();

                foreach (var tex in group.OptimizableTextures())
                {
                    _progress.ThrowIfCancelled();
                    var session = OpenSession(tex);
                    if (session == null) continue;
                    try
                    {
                        EvaluateTextureInGroup(group, tex, session, finalRatios, folded);
                    }
                    finally
                    {
                        session.Dispose();
                    }
                }

                // Pixel density clamp (applies on top of quality decisions).
                // 像素密度钳制（在质量决策之上应用）。
                foreach (var isl in group.islands.ApplyDensityClamp(group, _settings, finalRatios)) { }

                return finalRatios;
            }
        }

        /// <summary>
        /// Fold one island candidate into the group ratio map (max-across-textures).
        /// 折叠一个岛的候选比例到组比例图（跨贴图取最大）。
        /// </summary>
        private static void FoldRatio(
            Dictionary<ATOIsland, Vector2> map, HashSet<ATOIsland> folded,
            ATOIsland isl, Vector2 candidate)
        {
            if (folded.Add(isl))
            {
                map[isl] = candidate;
            }
            else
            {
                var cur = map[isl];
                map[isl] = new Vector2(Mathf.Max(cur.x, candidate.x), Mathf.Max(cur.y, candidate.y));
            }
        }

        private ATOTextureSession OpenSession(ATOTextureEntry tex)
        {
            try
            {
                bool normalPath = tex.isNormalMap || tex.category == ATOTextureCategory.Normal;
                return _gpu.OpenSession(tex, normalPath);
            }
            catch (Exception e)
            {
                ATOLog.Warn($"gpu session failed for {tex.texture?.name}: {e.Message}");
                return null;
            }
        }

        private void EvaluateTextureInGroup(
            ATOUVGroup group, ATOTextureEntry tex, ATOTextureSession session,
            Dictionary<ATOIsland, Vector2> finalRatios, HashSet<ATOIsland> folded)
        {
            var usages = CollectUsages(group, tex);
            if (usages.Count == 0) return;
            bool normalPath = session.originalNormals != null;

            foreach (var isl in group.islands)
            {
                _progress.ThrowIfCancelled();
                var texW = tex.width;
                var texH = tex.height;
                int origW = Mathf.Max(1, Mathf.RoundToInt((isl.uvMax.x - isl.uvMin.x) * texW));
                int origH = Mathf.Max(1, Mathf.RoundToInt((isl.uvMax.y - isl.uvMin.y) * texH));
                var crop = IslandCropRect(isl, texW, texH);
                int shortEdge = Mathf.Min(crop.width, crop.height);

                // Tiny islands: skip SSIM entirely, keep original size (safe).
                // 超小岛：完全跳过 SSIM，保持原尺寸（安全）。
                if (shortEdge < ATOConsts.SsimIgnoreShortEdge)
                {
                    FoldRatio(finalRatios, folded, isl, Vector2.one);
                    continue;
                }

                // Pure color shortcut (quality < 1). / 纯色短路（质量<1）。
                if (IsPureColor(isl, session, crop, normalPath))
                {
                    int targetShort = Mathf.Min(ATOConsts.PureColorMinSize, shortEdge);
                    float pr = Mathf.Clamp((float)targetShort / shortEdge, 1f / Mathf.Max(origW, origH), 1f);
                    // keep aspect / 保持宽高
                    FoldRatio(finalRatios, folded, isl, new Vector2(pr, pr));
                    continue;
                }

                // Uniform binary search / 均匀二分
                float lo = MinimumRatio(origW, origH);
                float hi = 1f;
                if (!CandidatePasses(isl, tex, session, crop, origW, origH, 1f, 1f, usages, normalPath))
                {
                    // Even 1.0 fails: content cannot satisfy thresholds (e.g. already
                    // below MS-SSIM floor?) -> keep original size, note it.
                    // 1.0 也不达标：内容本身无法满足阈值 -> 保持原尺寸并记录。
                    ATOLog.Verbose($"quality: {tex.texture.name} island {isl.index} fails at 1.0, keep original");
                    FoldRatio(finalRatios, folded, isl, Vector2.one);
                    continue;
                }

                // 10 iterations ~ 1/1024 precision / 10 次迭代 ~ 1/1024 精度
                for (int it = 0; it < 10; it++)
                {
                    float mid = (lo + hi) * 0.5f;
                    if (CandidatePasses(isl, tex, session, crop, origW, origH, mid, mid, usages, normalPath))
                        hi = mid;
                    else
                        lo = mid;
                }
                float ru = hi, rv = hi;

                // Per-axis refinement (independent binary searches). / 双轴独立细化。
                float loU = MinimumRatio(origW, origH);
                for (int it = 0; it < 8; it++)
                {
                    float mid = (loU + ru) * 0.5f;
                    if (CandidatePasses(isl, tex, session, crop, origW, origH, mid, rv, usages, normalPath))
                        ru = mid;
                    else
                        loU = mid;
                }
                float loV = MinimumRatio(origW, origH);
                for (int it = 0; it < 8; it++)
                {
                    float mid = (loV + rv) * 0.5f;
                    if (CandidatePasses(isl, tex, session, crop, origW, origH, ru, mid, usages, normalPath))
                        rv = mid;
                    else
                        loV = mid;
                }

                FoldRatio(finalRatios, folded, isl, new Vector2(ru, rv));
            }
        }

        private static float MinimumRatio(int w, int h)
        {
            // Never propose a size below 1px on either axis. / 任何轴不低于 1px。
            return Mathf.Min(0.5f, Mathf.Max(1f / Mathf.Max(1, w), 1f / Mathf.Max(1, h)));
        }

        private List<ATOUsage> CollectUsages(ATOUVGroup group, ATOTextureEntry tex)
        {
            var list = new List<ATOUsage>();
            foreach (var u in group.usages)
                if (u.texture == tex && u.Optimizable) list.Add(u);
            return list;
        }

        /// <summary>Quantized pixel crop rect of the island in texture space (clamped, +1px pad). / 岛在贴图空间的量化像素裁剪（含 1px 内扩）。</summary>
        private static RectInt IslandCropRect(ATOIsland isl, int texW, int texH)
        {
            int x0 = Mathf.Clamp(Mathf.FloorToInt(isl.uvMin.x * texW), 0, texW - 1);
            int y0 = Mathf.Clamp(Mathf.FloorToInt(isl.uvMin.y * texH), 0, texH - 1);
            int x1 = Mathf.Clamp(Mathf.CeilToInt(isl.uvMax.x * texW), x0 + 1, texW);
            int y1 = Mathf.Clamp(Mathf.CeilToInt(isl.uvMax.y * texH), y0 + 1, texH);
            return new RectInt(x0, y0, x1 - x0, y1 - y0);
        }

        /// <summary>Coverage mask in crop space (dilated 1px), cached. / 裁剪空间覆盖掩码（外扩 1px），带缓存。</summary>
        private bool[] CoverageMask(ATOIsland isl, int texW, int texH, RectInt crop)
        {
            var k2 = (isl, texW, texH);
            if (_maskCache.TryGetValue(k2, out var cached)) return cached;

            var mask = new bool[crop.width * crop.height];
            for (int t = 0; t < isl.localTriangles.Length; t += 3)
            {
                Vector2 a = UvToCropPx(isl.bakedUVs[isl.localTriangles[t]], crop, texW, texH);
                Vector2 b = UvToCropPx(isl.bakedUVs[isl.localTriangles[t + 1]], crop, texW, texH);
                Vector2 c = UvToCropPx(isl.bakedUVs[isl.localTriangles[t + 2]], crop, texW, texH);
                ATORaster.RasterTrianglePx(a, b, c, mask, crop.width, crop.height);
            }
            // 1px dilation to cover bilinear taps at the island edge. / 外扩 1px 覆盖边缘双线性采样。
            var dilated = new bool[mask.Length];
            for (int y = 0; y < crop.height; y++)
            for (int x = 0; x < crop.width; x++)
            {
                if (!mask[y * crop.width + x]) continue;
                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= crop.width || ny >= crop.height) continue;
                    dilated[ny * crop.width + nx] = true;
                }
            }
            _maskCache[k2] = dilated;
            return dilated;
        }

        private static Vector2 UvToCropPx(Vector2 uv, RectInt crop, int texW, int texH)
        {
            return new Vector2(uv.x * texW - crop.x, uv.y * texH - crop.y);
        }

        private bool IsPureColor(ATOIsland isl, ATOTextureSession session, RectInt crop, bool normalPath)
        {
            if (normalPath)
            {
                var normals = session.originalNormals;
                if (normals == null) return false;
                var refs = normals[SampleIndex(crop, 0.5f, session.entry.width)];
                for (int y = 0; y < crop.height; y += 2)
                for (int x = 0; x < crop.width; x += 2)
                {
                    int idx = (crop.y + y) * session.entry.width + (crop.x + x);
                    var c = normals[idx];
                    if (Mathf.Abs(c.r - refs.r) > 0.004f || Mathf.Abs(c.g - refs.g) > 0.004f || Mathf.Abs(c.b - refs.b) > 0.004f)
                        return false;
                }
                return true;
            }
            var bytes = session.originalDisplayBytes;
            if (bytes == null) return false;
            var first = bytes[SampleIndex(crop, 0.5f, session.entry.width)];
            for (int y = 0; y < crop.height; y += 2)
            for (int x = 0; x < crop.width; x += 2)
            {
                int idx = (crop.y + y) * session.entry.width + (crop.x + x);
                var c = bytes[idx];
                if (Mathf.Abs(c.r - first.r) > 3 || Mathf.Abs(c.g - first.g) > 3 ||
                    Mathf.Abs(c.b - first.b) > 3 || Mathf.Abs(c.a - first.a) > 3)
                    return false;
            }
            return true;
        }

        /// <summary>Index of the crop-center pixel in a full-size row-major array. / 裁剪中心像素在整图行主序数组中的索引。</summary>
        private static int SampleIndex(RectInt crop, float pos, int texW)
        {
            int sx = Mathf.Clamp(crop.x + crop.width / 2, 0, texW - 1);
            int sy = crop.y + crop.height / 2;
            return sy * texW + sx;
        }

        /// <summary>
        /// Render one candidate (scaled island) and test all role metrics.
        /// 渲染一个候选（缩放岛）并测试全部角色指标。
        /// </summary>
        private bool CandidatePasses(
            ATOIsland isl, ATOTextureEntry tex, ATOTextureSession session,
            RectInt crop, int origW, int origH, float ru, float rv,
            List<ATOUsage> usages, bool normalPath)
        {
            int targetW = Mathf.Clamp(Mathf.RoundToInt(origW * ru), 1, crop.width);
            int targetH = Mathf.Clamp(Mathf.RoundToInt(origH * rv), 1, crop.height);
            int evalId = ++_evalCounter;
            var cacheKey = (isl, tex, targetW, targetH, normalPath);
            if (_candidateCache.TryGetValue(cacheKey, out var cached)) return cached;

            bool pass;
            try
            {
                pass = EvaluateCandidate(isl, tex, session, crop, targetW, targetH, usages, normalPath, evalId);
            }
            catch (Exception e)
            {
                ATOLog.Warn($"quality eval failed for {tex.texture.name} island {isl.index}: {e.Message}");
                pass = false;
            }
            _candidateCache[cacheKey] = pass;
            return pass;
        }

        private bool EvaluateCandidate(
            ATOIsland isl, ATOTextureEntry tex, ATOTextureSession session,
            RectInt crop, int targetW, int targetH,
            List<ATOUsage> usages, bool normalPath, int evalId)
        {
            using (new ATOLog.Step($"cand#{evalId} {tex.texture.name}.i{isl.index}->{targetW}x{targetH}"))
            {
                var mask = CoverageMask(isl, tex.width, tex.height, crop);
                var chain = _gpu.DownsampleCrop(session.fullLinearFloat, crop, targetW, targetH);
                var small = chain[chain.Count - 1];
                try
                {
                    // Upsample back to the original crop size for comparison.
                    // 上采样回原裁剪尺寸进行对比。
                    var back = _gpu.Upsample(small, crop.width, crop.height);
                    try
                    {
                        if (normalPath)
                        {
                            var renorm = _gpu.RunPass(back, ATOGpuPipeline.PassRenormalize, crop.width, crop.height);
                            var newNormals = _gpu.ReadbackRegionFloat(renorm, new RectInt(0, 0, crop.width, crop.height));
                            _gpu.Pool.Return(renorm);
                            var origNormals = CropNormals(session.originalNormals, session.entry.width, crop);
                            var (mean, p95) = ATOMetrics.NormalAngular(origNormals, newNormals, mask);
                            if (mean > _quality.normalMeanDegMax || p95 > _quality.normalP95DegMax) return false;
                            return true;
                        }

                        bool srgbPath = session.entry.sRGB;
                        int encodePass = srgbPath ? ATOGpuPipeline.PassUnpremultiplyEncodeSRGB : ATOGpuPipeline.PassLinearCopy;
                        var disp = _gpu.EncodeToDisplay(back, encodePass, crop.width, crop.height);
                        var newBytes = _gpu.ReadbackRegion32(disp, new RectInt(0, 0, crop.width, crop.height));
                        _gpu.Pool.Return(disp);
                        var origBytes = CropBytes(session.originalDisplayBytes, session.entry.width, crop);
                        var pair = new ATOCropPair { a = origBytes, b = newBytes, width = crop.width, height = crop.height, mask = mask };

                        var role = usages[0].role;
                        switch (role)
                        {
                            case ATORole.Normal:
                            {
                                // Should not happen (normal uses normalPath), safe fallback.
                                // 理论上不会发生（法线走 normalPath），安全兜底。
                                return true;
                            }
                            case ATORole.Mask:
                            {
                                int maskChannels = 0;
                                foreach (var u in usages) maskChannels |= u.usedChannels;
                                float rmse = ATOMetrics.GrayRmseWorstChannel(pair, maskChannels);
                                return rmse <= _quality.grayRmseMax;
                            }
                            default:
                            {
                                // Color roles: SSIM + DeltaE + mode-specific alpha metrics.
                                // 色彩角色：SSIM + ΔE + 按透明模式的 alpha 指标。
                                float ssim = ATOMetrics.ScoreSSIM(pair);
                                if (ssim < _quality.msSsimMin) return false;
                                float de = ATOMetrics.MeanDeltaE2000(pair);
                                if (de > _quality.deltaEMax) return false;

                                foreach (var u in usages)
                                {
                                    switch (u.renderMode)
                                    {
                                        case ATORenderMode.Cutout:
                                        {
                                            float iou = ATOMetrics.CutoutIoU(pair, u.cutoff);
                                            if (iou < _quality.cutoutIouMin) return false;
                                            break;
                                        }
                                        case ATORenderMode.Transparent:
                                        {
                                            float rmse = ATOMetrics.AlphaRmse(pair);
                                            if (rmse > _quality.alphaRmseMax) return false;
                                            break;
                                        }
                                    }
                                }
                                return true;
                            }
                        }
                    }
                    finally
                    {
                        _gpu.Pool.Return(back);
                    }
                }
                finally
                {
                    foreach (var rt in chain) _gpu.Pool.Return(rt);
                }
            }
        }

        private Color[] CropNormals(Color[] src, int srcW, RectInt crop)
        {
            var dst = new Color[crop.width * crop.height];
            for (int y = 0; y < crop.height; y++)
            {
                int srcIdx = (crop.y + y) * srcW + crop.x;
                Array.Copy(src, srcIdx, dst, y * crop.width, crop.width);
            }
            return dst;
        }

        private Color32[] CropBytes(Color32[] src, int srcW, RectInt crop)
        {
            var dst = new Color32[crop.width * crop.height];
            for (int y = 0; y < crop.height; y++)
            {
                int srcIdx = (crop.y + y) * srcW + crop.x;
                Array.Copy(src, srcIdx, dst, y * crop.width, crop.width);
            }
            return dst;
        }
    }

    /// <summary>Density clamp helpers. / 密度钳制辅助。</summary>
    public static class ATOIslandDensityClamp
    {
        /// <summary>
        /// Apply pixel-density clamps (min/max px/m) to final ratios of every
        /// island in the group, in-place on the dictionary.
        /// 对组内全部岛的最终比例应用像素密度钳制（最小/最大 px/m），就地修改字典。
        /// </summary>
        public static IEnumerable<ATOIsland> ApplyDensityClamp(
            this List<ATOIsland> islands, ATOUVGroup group, AvatarTextureOptimizer settings,
            Dictionary<ATOIsland, Vector2> ratios)
        {
            float dMin = Mathf.Max(1, settings.minPixelDensity);
            float dMax = Mathf.Max(dMin, settings.maxPixelDensity);
            foreach (var isl in islands)
            {
                if (!ratios.TryGetValue(isl, out var r)) continue;
                float worldArea = Mathf.Max(isl.worldArea * group.areaFactor, 1e-10f);

                // Island pixel budget in original-texture space, using the average
                // member texture size as reference (the group ratio is common).
                // 以组成员贴图平均尺寸估算原始像素预算（组比例统一）。
                float avgTexelW = 0, avgTexelH = 0;
                int c = 0;
                foreach (var tex in group.OptimizableTextures())
                {
                    avgTexelW += tex.width;
                    avgTexelH += tex.height;
                    c++;
                }
                if (c == 0) continue;
                avgTexelW /= c;
                avgTexelH /= c;

                float uvW = Mathf.Max(1e-6f, isl.uvMax.x - isl.uvMin.x);
                float uvH = Mathf.Max(1e-6f, isl.uvMax.y - isl.uvMin.y);
                // World-space extents along the island's principal axes are
                // approximated from the anisotropy lengths (PCA spans are
                // UV-proportional; world length ratios follow the same ratio).
                // 沿岛主轴的世界跨度由各向异性长度近似（PCA 跨度与 UV 成比例，
                // 世界长度比例保持一致）。
                float aspect = isl.lenMajor / Mathf.Max(1e-6f, isl.lenMinor);
                float worldMajor = Mathf.Sqrt(worldArea * aspect);
                float worldMinor = Mathf.Sqrt(worldArea / Mathf.Max(1e-6f, aspect));

                float pxMajorOrig = uvW * avgTexelW * (isl.lenMajor / Mathf.Max(1e-6f, uvW)) > 0
                    ? isl.lenMajor * avgTexelW
                    : uvW * avgTexelW;
                float pxMinorOrig = isl.lenMinor * avgTexelH;
                float dOrigMajor = pxMajorOrig / Mathf.Max(1e-6f, worldMajor);
                float dOrigMinor = pxMinorOrig / Mathf.Max(1e-6f, worldMinor);

                // Per-axis allowed ratio band: [min(1, dMin/d), min(1, dMax/d)].
                // 逐轴允许比例带：[min(1, dMin/d), min(1, dMax/d)]。
                float floorU = dOrigMajor > 1e-6f ? Mathf.Min(1f, dMin / dOrigMajor) : 0f;
                float capU = dOrigMajor > 1e-6f ? Mathf.Min(1f, dMax / dOrigMajor) : 1f;
                float floorV = dOrigMinor > 1e-6f ? Mathf.Min(1f, dMin / dOrigMinor) : 0f;
                float capV = dOrigMinor > 1e-6f ? Mathf.Min(1f, dMax / dOrigMinor) : 1f;

                float ru = Mathf.Clamp(r.x, floorU, capU);
                float rv = Mathf.Clamp(r.y, floorV, capV);
                ratios[isl] = new Vector2(Mathf.Clamp01(ru), Mathf.Clamp01(rv));
                yield return isl;
            }
        }
    }
}
