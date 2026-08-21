using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Profiling;

namespace net.fosa.ato
{
    // ============================================================================
    // 批量二分搜索调度器 / Batch bisection search scheduler.
    //
    // 全部岛的搜索被组织为"调度回合": 每个回合对全部活跃岛并行评估当前中点,
    // 然后统一收紧二分边界, 直到收敛. 评估作业(重采样/高斯矩/汇总)均为 Burst 行并行.
    // All island searches run as scheduled "rounds": every round evaluates the current midpoint
    // of every active island in parallel (Burst row-parallel jobs), then tightens the bisection
    // bounds, until convergence.
    //
    // GPU 路径: 岛的全分辨率短边 > 512 且 GPU 可用时, 重采样改由 compute shader 执行,
    // 指标仍由 Burst 作业计算(确定性).
    // GPU path: when an island's full-res short side exceeds 512 and a GPU is available, the
    // resampling runs in a compute shader; metrics still run in deterministic Burst jobs.
    // ============================================================================

    internal struct ATOIslandSearchData
    {
        public ATOIsland island;
        public ATOTextureInfo tex;
        public ATOIslandTexture it;
        public ATOEvalContext ctx;
        public int cropW, cropH;          // 裁剪像素尺寸 / crop pixel dims
        public Color32[] crop;            // 裁剪像素(托管) / crop pixels (managed)
        public int w, h;                  // 比较分辨率 / comparison resolution
        public bool useGpu;               // GPU全分辨率重采样路径 / GPU full-resolution resample path
        // GPU 路径资源 / GPU path resources (owned by the scheduler)
        public RenderTexture srcRT;
        public RenderTexture alphaRT;
        public RenderTexture normalRT;
    }

    internal static class ATOBatchSearch
    {
        private const float MinScale = 1f / 64f;

        public static void Run(ATOBuildState state, List<ATOIslandSearchData> searches)
        {
            if (searches.Count == 0) return;
            Profiler.BeginSample("ATO.BatchSearch");
            var timer = new ATOLog.StageTimer();
            timer.Start();

            var cfg = state.config;
            var q = cfg.quality;

        // ---------------------------------------------------------------
        // 1. 准备(逐岛): 构建预乘线性/alpha/sRGB/法线/掩码缓冲 + 纯色检测
        // ---------------------------------------------------------------
        timer.BeginStep("prep");
        // GPU 路径: 原生分辨率短边 > 512 且 GPU 可用 -> 比较分辨率上限 1024
        // GPU path: native short side > 512 and GPU available -> comparison res cap 1024
        foreach (var s in searches)
        {
            if (Mathf.Min(s.cropW, s.cropH) > 512 && ATOGpu.ResampleAvailable && !s.ctx.normalMap)
            {
                s.useGpu = true;
                UploadGpuIsland(s);
            }
        }
            var items = new List<ATOEvalItem>();
            var srcBuffer = new List<NativeArray<float>>();
            var alphaBuffer = new List<NativeArray<float>>();
            var srgbBuffer = new List<NativeArray<float>>();
            var normalBuffer = new List<NativeArray<float>>();
            var maskBuffer = new List<NativeArray<byte>>();
            var upBuffer = new List<NativeArray<float>>();
            var upAlphaBuffer = new List<NativeArray<float>>();
            var upNormalBuffer = new List<NativeArray<float>>();
            var hMoments = new List<NativeArray<float>>();
            var moments = new List<NativeArray<float>>();

            // 收集有效搜索(非纯色) / collect valid searches (non-solid)
            var valid = new List<ATOIslandSearchData>();
            foreach (var s in searches)
            {
                bool solid = DetectSolidNative(s, out var solidColor);
                if (solid)
                {
                    // 纯色短路 / solid shortcut
                    float shortSide = Mathf.Min(s.cropW, s.cropH);
                    float sc = Mathf.Clamp(Mathf.Min(4f, shortSide) / Mathf.Max(1f, shortSide), MinScale, 1f);
                    ApplyScale(s.it, new Vector2(sc, sc));
                    s.it.solidColor = true;
                    s.it.solid = solidColor;
                    ATOLog.InfoVerbose($"纯色岛短路 / solid island shortcut: {s.tex.source.name} -> {sc:F3}");
                    continue;
                }

                valid.Add(s);
            }

            if (valid.Count == 0)
            {
                timer.End("质量搜索 Quality Search (纯色短路)");
                Profiler.EndSample();
                return;
            }

            // 构建评估项 / build eval items
            // 所有偏移统一为像素单位 / all offsets are in PIXEL units
            var evalItems = new NativeArray<ATOEvalItem>(valid.Count, Allocator.TempJob);
            int srcOff = 0, upOff = 0, momentsOff = 0;
            var perEvalW = new int[valid.Count];
            var perEvalH = new int[valid.Count];
            var srcOffs = new int[valid.Count];
            for (int i = 0; i < valid.Count; i++)
            {
                var s = valid[i];
                PrepareSample(s, out var srcNA, out var alphaNA, out var srgbNA, out var normalNA, out var maskNA);
                srcBuffer.Add(srcNA);
                alphaBuffer.Add(alphaNA);
                srgbBuffer.Add(srgbNA);
                normalBuffer.Add(normalNA);
                maskBuffer.Add(maskNA);

                int n = s.w * s.h;
                perEvalW[i] = s.w;
                perEvalH[i] = s.h;
                srcOffs[i] = srcOff;

                evalItems[i] = new ATOEvalItem
                {
                    srcOffset = srcOff,
                    upOffset = upOff,
                    momentsOffset = momentsOff,
                    w = s.w,
                    h = s.h,
                    category = (int)s.ctx.category,
                    hasAlpha = s.ctx.hasAlpha ? 1 : 0,
                    cutout = s.ctx.cutout ? 1 : 0,
                    blend = s.ctx.blend ? 1 : 0,
                    renderModeAnimated = s.ctx.renderModeAnimated ? 1 : 0,
                    usedChannels = s.ctx.usedChannels,
                    sx = 1f,
                    sy = 1f,
                    gpu = s.useGpu ? 1 : 0
                };

                srcOff += n;
                upOff += n;
                momentsOff += n;
            }

            // 汇总缓冲(像素单位偏移: src=4float/px, normal=3float/px, moments=5float/px) / aggregate buffers
            var upNA = new NativeArray<float>(upOff * 4, Allocator.TempJob);
            var upAlphaNA = new NativeArray<float>(upOff, Allocator.TempJob);
            var upNormalNA = new NativeArray<float>(upOff * 3, Allocator.TempJob);
            var hMomNA = new NativeArray<float>(momentsOff * 5, Allocator.TempJob);
            var momNA = new NativeArray<float>(momentsOff * 5, Allocator.TempJob);
            var srcAll = new NativeArray<float>(srcOff * 4, Allocator.TempJob);
            var alphaAll = new NativeArray<float>(srcOff, Allocator.TempJob);
            var srgbAll = new NativeArray<float>(srcOff * 4, Allocator.TempJob);
            var normalAll = new NativeArray<float>(srcOff * 3, Allocator.TempJob);
            var maskAll = new NativeArray<byte>(srcOff, Allocator.TempJob);

            int srcBase = 0;
            for (int i = 0; i < valid.Count; i++)
            {
                int n = perEvalW[i] * perEvalH[i];
                srcAll.Slice(srcBase * 4, n * 4).CopyFrom(srcBuffer[i]);
                alphaAll.Slice(srcBase, n).CopyFrom(alphaBuffer[i]);
                srgbAll.Slice(srcBase * 4, n * 4).CopyFrom(srgbBuffer[i]);
                maskAll.Slice(srcBase, n).CopyFrom(maskBuffer[i]);
                if (normalBuffer[i].Length > 0)
                {
                    normalAll.Slice(srcBase * 3, n * 3).CopyFrom(normalBuffer[i]);
                }

                srcBase += n;
            }

            // 各指标逐行部分和(避免多行写同一累加器的竞争) / per-row partial sums (race-free)
            int maxH = 1;
            for (int i = 0; i < valid.Count; i++) maxH = Mathf.Max(maxH, perEvalH[i]);
            var deSums = new NativeArray<float>(valid.Count * maxH * 2, Allocator.TempJob);
            var iouInter = new NativeArray<float>(valid.Count * maxH * 16 * 2, Allocator.TempJob);
            var alphaSum = new NativeArray<float>(valid.Count * maxH * 2, Allocator.TempJob);
            var normalSum = new NativeArray<float>(valid.Count * maxH * 2, Allocator.TempJob);
            var graySum = new NativeArray<float>(valid.Count * maxH * 4 * 2, Allocator.TempJob);
            var results = new NativeArray<float>(valid.Count * ATOQResultLayout.PerIsland, Allocator.TempJob);
            var cutoffs = new NativeArray<float>(valid.Count * 16, Allocator.TempJob);
            // 每评估项 cutoff 列表(下标0=最小, -1=未使用) / per-eval cutoff lists (index 0 = min, -1 = unused)
            for (int i = 0; i < cutoffs.Length; i++) cutoffs[i] = -1f;
            for (int i = 0; i < valid.Count; i++)
            {
                var cs = valid[i].ctx.cutoffs;
                if (cs == null || cs.Length == 0) continue;
                var sorted = new List<float>(cs);
                sorted.Sort();
                for (int c = 0; c < sorted.Count && c < 16; c++) cutoffs[i * 16 + c] = sorted[c];
            }

            // 二分状态 / bisection state
            var loX = new float[valid.Count];
            var hiX = new float[valid.Count];
            var loY = new float[valid.Count];
            var hiY = new float[valid.Count];
            var phase = new int[valid.Count]; // 0=uniform, 1=axisX, 2=axisY, 3=done

            for (int i = 0; i < valid.Count; i++)
            {
                loX[i] = MinScale;
                hiX[i] = 1f;
                loY[i] = MinScale;
                hiY[i] = 1f;
                phase[i] = 0;
            }

            try
            {
                timer.EndStep();

                // -----------------------------------------------------------
                // 2. 二分回合 / bisection rounds
                // -----------------------------------------------------------
                int maxRounds = 14 + 12 + 12;
                var itemsNA = new NativeArray<ATOEvalItem>(valid.Count, Allocator.TempJob);

                for (int round = 0; round < maxRounds; round++)
                {
                    bool anyActive = false;
                    for (int i = 0; i < valid.Count; i++)
                    {
                        var item = evalItems[i];
                        if (phase[i] == 0)
                        {
                            float mid = (loX[i] + hiX[i]) * 0.5f;
                            item.sx = mid;
                            item.sy = mid;
                            anyActive = true;
                        }
                        else if (phase[i] == 1)
                        {
                            float mid = (loX[i] + hiX[i]) * 0.5f;
                            item.sx = mid;
                            item.sy = hiY[i];
                            anyActive = true;
                        }
                        else if (phase[i] == 2)
                        {
                            float mid = (loY[i] + hiY[i]) * 0.5f;
                            item.sx = hiX[i];
                            item.sy = mid;
                            anyActive = true;
                        }
                        else
                        {
                            item.sx = item.sy = -1f; // 跳过 / skip
                        }

                        itemsNA[i] = item;
                    }

                    if (!anyActive) break;

                    // 清空部分和 / clear partial sums
                    for (int i = 0; i < deSums.Length; i++) deSums[i] = 0;
                    for (int i = 0; i < iouInter.Length; i++) iouInter[i] = 0;
                    for (int i = 0; i < alphaSum.Length; i++) alphaSum[i] = 0;
                    for (int i = 0; i < normalSum.Length; i++) normalSum[i] = 0;
                    for (int i = 0; i < graySum.Length; i++) graySum[i] = 0;
                    for (int i = 0; i < results.Length; i++) results[i] = 0;

                    // GPU 路径: 原生分辨率重采样 + 回读降采样到比较分辨率 / GPU path: native-res resample + downscale readback
                    for (int i = 0; i < valid.Count; i++)
                    {
                        if (!valid[i].useGpu || phase[i] >= 3) continue;
                        var item = itemsNA[i];
                        if (item.sx < 0) continue;
                        GpuRound(valid[i], item, srcAll, alphaAll, srcOffs[i], upNA, upAlphaNA);
                    }

                    // 阶段1: 重采样(CPU路径; GPU项跳过) / stage 1: resample (CPU path; GPU items skip)
                    var rJob = new ATOEvalResampleJob
                    {
                        items = itemsNA,
                        src = srcAll,
                        srcAlpha = alphaAll,
                        srcSrgb = srgbAll,
                        srcNormal = normalAll,
                        mask = maskAll,
                        up = upNA,
                        upAlpha = upAlphaNA,
                        upNormal = upNormalNA
                    };
                    var rHandle = rJob.Schedule(valid.Count, 4);

                    // 阶段2a: 水平高斯 / stage 2a: horizontal Gaussian
                    // 线性索引解码: eval = idx / maxH, row = idx % maxH (越界行跳过)
                    var hJob = new ATOGaussHJob
                    {
                        items = itemsNA,
                        src = srcAll,
                        up = upNA,
                        mask = maskAll,
                        hMoments = hMomNA,
                        maxH = maxH
                    };
                    var hHandle = hJob.Schedule(valid.Count * maxH, 64, rHandle);

                    // 阶段2b: 垂直 + 部分和 / stage 2b: vertical + partial sums
                    var vJob = new ATOGaussVJob
                    {
                        items = itemsNA,
                        hMoments = hMomNA,
                        moments = momNA,
                        mask = maskAll,
                        src = srcAll,
                        up = upNA,
                        srcSrgb = srgbAll,
                        srcAlpha = alphaAll,
                        upAlpha = upAlphaNA,
                        srcNormal = normalAll,
                        upNormal = upNormalNA,
                        cutoffs = cutoffs,
                        cutoffCount = 16,
                        maxH = maxH,
                        deSums = deSums,
                        iouInter = iouInter,
                        alphaSum = alphaSum,
                        normalSum = normalSum,
                        graySum = graySum
                    };
                    var vHandle = vJob.Schedule(valid.Count * maxH, 64, hHandle);

                    // 阶段3: 汇总 / stage 3: reduce
                    var redJob = new ATOEvalReduceJob
                    {
                        items = itemsNA,
                        moments = momNA,
                        mask = maskAll,
                        deSums = deSums,
                        iouInter = iouInter,
                        cutoffCount = 16,
                        maxH = maxH,
                        alphaSum = alphaSum,
                        normalSum = normalSum,
                        graySum = graySum,
                        p95Work = results,
                        results = results
                    };
                    var redHandle = redJob.Schedule(valid.Count, 4, vHandle);

                    // 阶段4: p95 / stage 4: p95
                    var p95Job = new ATOP95Job
                    {
                        items = itemsNA,
                        srcNormal = normalAll,
                        upNormal = upNormalNA,
                        mask = maskAll,
                        results = results
                    };
                    var p95Handle = p95Job.Schedule(valid.Count, 4, redHandle);

                    p95Handle.Complete();

                    // 更新二分边界 / update bisection bounds
                    for (int i = 0; i < valid.Count; i++)
                    {
                        if (phase[i] >= 3) continue;
                        float worst = WorstRatioFor(valid[i], results, i, q);
                        bool pass = worst <= 1f;

                        if (phase[i] == 0)
                        {
                            if (pass) { loX[i] = loY[i] = (loX[i] + hiX[i]) * 0.5f; }
                            else { hiX[i] = hiY[i] = (loX[i] + hiX[i]) * 0.5f; }

                            bool uniformDone = (hiX[i] - loX[i]) < 0.002f;
                            if (uniformDone)
                            {
                                // 各向异性细化(法线贴图除外) / anisotropic refinement (not for normals)
                                if (valid[i].ctx.category != ATOTextureCategory.Normal)
                                {
                                    phase[i] = 1;
                                    loX[i] = MinScale;
                                    hiX[i] = hiY[i];
                                }
                                else
                                {
                                    phase[i] = 3;
                                }
                            }
                        }
                        else if (phase[i] == 1)
                        {
                            if (pass) loX[i] = (loX[i] + hiX[i]) * 0.5f;
                            else hiX[i] = (loX[i] + hiX[i]) * 0.5f;
                            if ((hiX[i] - loX[i]) < 0.002f)
                            {
                                phase[i] = 2;
                                loY[i] = MinScale;
                            }
                        }
                        else if (phase[i] == 2)
                        {
                            if (pass) loY[i] = (loY[i] + hiY[i]) * 0.5f;
                            else hiY[i] = (loY[i] + hiY[i]) * 0.5f;
                            if ((hiY[i] - loY[i]) < 0.002f) phase[i] = 3;
                        }
                    }
                }

                itemsNA.Dispose();

                // -----------------------------------------------------------
                // 3. 写回结果(密度钳制由调用方执行) / write back (density clamp by the caller)
                // -----------------------------------------------------------
                for (int i = 0; i < valid.Count; i++)
                {
                    var s = valid[i];
                    float sx = hiX[i], sy = hiY[i];
                    if (phase[i] == 0) { sx = sy = (loX[i] + hiX[i]) * 0.5f; }
                    ApplyScale(s.it, new Vector2(sx, sy));
                    ATOLog.InfoVerbose($"岛缩放 / island scale: {s.tex.source.name} {sx:F3}x{sy:F3} ({s.cropW}x{s.cropH} -> {Mathf.RoundToInt(s.cropW * sx)}x{Mathf.RoundToInt(s.cropH * sy)})");
                }

                timer.End("质量搜索 Quality Search (Burst批量二分)");
            }
            finally
            {
                // 释放GPU资源 / release GPU resources
                foreach (var s in searches)
                {
                    if (s.srcRT != null) RenderTexture.ReleaseTemporary(s.srcRT);
                    if (s.alphaRT != null) RenderTexture.ReleaseTemporary(s.alphaRT);
                    if (s.normalRT != null) RenderTexture.ReleaseTemporary(s.normalRT);
                    s.srcRT = null;
                    s.alphaRT = null;
                    s.normalRT = null;
                }

                cutoffs.Dispose();
                results.Dispose();
                graySum.Dispose();
                normalSum.Dispose();
                alphaSum.Dispose();
                iouInter.Dispose();
                deSums.Dispose();
                maskAll.Dispose();
                normalAll.Dispose();
                srgbAll.Dispose();
                alphaAll.Dispose();
                srcAll.Dispose();
                momNA.Dispose();
                hMomNA.Dispose();
                upNormalNA.Dispose();
                upAlphaNA.Dispose();
                upNA.Dispose();
                foreach (var b in srcBuffer) b.Dispose();
                foreach (var b in alphaBuffer) b.Dispose();
                foreach (var b in srgbBuffer) b.Dispose();
                foreach (var b in normalBuffer) b.Dispose();
                foreach (var b in maskBuffer) b.Dispose();
                evalItems.Dispose();
            }

            Profiler.EndSample();
        }

        private static void ApplyScale(ATOIslandTexture it, Vector2 scale)
        {
            it.scale = scale;
            it.targetWidth = Mathf.Max(1, Mathf.RoundToInt(it.pixelRect.width * scale.x));
            it.targetHeight = Mathf.Max(1, Mathf.RoundToInt(it.pixelRect.height * scale.y));
        }

        private static float WorstRatioFor(ATOIslandSearchData s, NativeArray<float> results, int idx, ATOQualityParameters q)
        {
            int off = idx * ATOQResultLayout.PerIsland;
            float worst = 0;
            float ssim = results[off + ATOQResultLayout.Ssim];
            if (ssim >= 0 && q.msSsim > 0) worst = Mathf.Max(worst, ssim / q.msSsim);
            if (q.deltaE2000 > 0) worst = Mathf.Max(worst, results[off + ATOQResultLayout.De2000] / q.deltaE2000);
            float iou = results[off + ATOQResultLayout.IoU];
            if (q.alphaIoU > 0) worst = Mathf.Max(worst, (1f - iou) / Mathf.Max(1f - q.alphaIoU, 1e-6f));
            if (q.alphaRmse > 0) worst = Mathf.Max(worst, results[off + ATOQResultLayout.AlphaRmse] / q.alphaRmse);
            if (q.normalAngleMean > 0) worst = Mathf.Max(worst, results[off + ATOQResultLayout.NormalMean] / q.normalAngleMean);
            if (q.normalAngleP95 > 0) worst = Mathf.Max(worst, results[off + ATOQResultLayout.NormalP95] / q.normalAngleP95);
            if (q.grayscaleRmse > 0) worst = Mathf.Max(worst, results[off + ATOQResultLayout.GrayRmse] / q.grayscaleRmse);
            return worst;
        }

        private static bool DetectSolidNative(ATOIslandSearchData s, out Color32 solid)
        {
            solid = default;
            if (s.crop == null || s.crop.Length == 0) return false;
            var first = s.crop[0];
            for (int i = 1; i < s.crop.Length; i++)
            {
                var c = s.crop[i];
                if (c.r != first.r || c.g != first.g || c.b != first.b || c.a != first.a) return false;
            }

            solid = first;
            return true;
        }

        /// <summary>
        /// 准备岛采样数据(CPU): 读取裁剪像素并构建预乘线性/alpha/sRGB/法线缓冲与覆盖掩码.
        /// Prepares island sample data (CPU): reads the crop and builds premultiplied-linear/alpha/sRGB/normal
        /// buffers and the coverage mask. 比较分辨率短边上限 512px.
        /// </summary>
        private static void PrepareSample(ATOIslandSearchData s,
            out NativeArray<float> src, out NativeArray<float> alpha, out NativeArray<float> srgb,
            out NativeArray<float> normal, out NativeArray<byte> mask)
        {
            src = default;
            alpha = default;
            srgb = default;
            normal = default;
            mask = default;

            var crop = s.crop;
            int cw = s.cropW, ch = s.cropH;
            if (crop == null || crop.Length != cw * ch) return;

            // GPU路径比较分辨率上限1024, CPU路径512 / comparison res cap: 1024 (GPU), 512 (CPU)
            int cap = s.useGpu ? 1024 : 512;
            int scaleDown = 1;
            while (Mathf.Min(cw, ch) / scaleDown > cap) scaleDown *= 2;
            int w = Mathf.Max(1, cw / scaleDown);
            int h = Mathf.Max(1, ch / scaleDown);
            s.w = w;
            s.h = h;
            int n = w * h;

            src = new NativeArray<float>(n * 4, Allocator.TempJob);
            alpha = new NativeArray<float>(n, Allocator.TempJob);
            srgb = new NativeArray<float>(n * 4, Allocator.TempJob);
            mask = new NativeArray<byte>(n, Allocator.TempJob);
            normal = s.ctx.normalMap ? new NativeArray<float>(n * 3, Allocator.TempJob) : default;

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float sr = 0, sg = 0, sb = 0, sa = 0;
                    int cnt = 0;
                    for (int dy = 0; dy < scaleDown; dy++)
                    {
                        for (int dx = 0; dx < scaleDown; dx++)
                        {
                            int px = x * scaleDown + dx, py = y * scaleDown + dy;
                            if (px >= cw || py >= ch) continue;
                            var c = crop[py * cw + px];
                            sr += c.r;
                            sg += c.g;
                            sb += c.b;
                            sa += c.a;
                            cnt++;
                        }
                    }

                    if (cnt == 0) continue;
                    sr /= cnt * 255f;
                    sg /= cnt * 255f;
                    sb /= cnt * 255f;
                    sa /= cnt * 255f;

                    int idx = y * w + x;
                    alpha[idx] = sa;
                    srgb[idx * 4] = sr;
                    srgb[idx * 4 + 1] = sg;
                    srgb[idx * 4 + 2] = sb;
                    srgb[idx * 4 + 3] = sa;

                    float lr = ATOColorMath.SRGBToLinear(sr);
                    float lg = ATOColorMath.SRGBToLinear(sg);
                    float lb = ATOColorMath.SRGBToLinear(sb);
                    src[idx * 4] = lr * sa;
                    src[idx * 4 + 1] = lg * sa;
                    src[idx * 4 + 2] = lb * sa;
                    src[idx * 4 + 3] = sa;

                    if (s.ctx.normalMap)
                    {
                        float nx = lr * 2f - 1f, ny = lg * 2f - 1f;
                        float nz = Mathf.Sqrt(Mathf.Max(0f, 1f - nx * nx - ny * ny));
                        float len = Mathf.Sqrt(nx * nx + ny * ny + nz * nz);
                        if (len < 1e-6f) len = 1f;
                        normal[idx * 3] = nx / len;
                        normal[idx * 3 + 1] = ny / len;
                        normal[idx * 3 + 2] = nz / len;
                    }
                }
            }

            RasterizeMaskCPU(s, w, h, mask);
        }

        private static void RasterizeMaskCPU(ATOIslandSearchData s, int w, int h, NativeArray<byte> mask)
        {
            var island = s.island;
            var tex = s.tex;
            var uvList = island.owner.newUVs[island.channel];
            int[] tris = island.owner.mesh.triangles;
            var b = s.it.pixelRect;
            float stepX = b.width / (tex.width * w);
            float stepY = b.height / (tex.height * h);

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float u0 = b.x / tex.width + x * stepX;
                    float v0 = b.y / tex.height + y * stepY;
                    float u1 = u0 + stepX;
                    float v1 = v0 + stepY;

                    bool covered = false;
                    foreach (var t in island.triangles)
                    {
                        Vector2 a = uvList[tris[t * 3]];
                        Vector2 bb = uvList[tris[t * 3 + 1]];
                        Vector2 c = uvList[tris[t * 3 + 2]];
                        if (Mathf.Max(a.x, bb.x, c.x) < u0 || Mathf.Min(a.x, bb.x, c.x) > u1
                            || Mathf.Max(a.y, bb.y, c.y) < v0 || Mathf.Min(a.y, bb.y, c.y) > v1) continue;
                        if (PointInTri(new Vector2(u0, v0), a, bb, c) || PointInTri(new Vector2(u1, v0), a, bb, c)
                            || PointInTri(new Vector2(u0, v1), a, bb, c) || PointInTri(new Vector2(u1, v1), a, bb, c))
                        {
                            covered = true;
                            break;
                        }
                    }

                    if (covered) mask[y * w + x] = 1;
                }
            }
        }

        private static bool PointInTri(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float d1 = (p.x - c.x) * (b.y - c.y) - (b.x - c.x) * (p.y - c.y);
            float d2 = (p.x - a.x) * (c.y - a.y) - (c.x - a.x) * (p.y - a.y);
            float d3 = (p.x - b.x) * (a.y - b.y) - (a.x - b.x) * (p.y - b.y);
            bool neg = d1 < 0 || d2 < 0 || d3 < 0;
            bool pos = d1 > 0 || d2 > 0 || d3 > 0;
            return !(neg && pos);
        }

        // ------------------------------------------------------------------
        // GPU 路径 / GPU path
        // ------------------------------------------------------------------
        /// <summary>上传裁剪像素到 RenderTexture(预乘线性RGBA + alpha) / Uploads the crop pixels to RenderTextures.</summary>
        private static void UploadGpuIsland(ATOIslandSearchData s)
        {
            var desc = new RenderTextureDescriptor(s.cropW, s.cropH, RenderTextureFormat.ARGBFloat, 0);
            desc.enableRandomWrite = true;
            s.srcRT = RenderTexture.GetTemporary(desc);
            var alphaDesc = new RenderTextureDescriptor(s.cropW, s.cropH, RenderTextureFormat.RFloat, 0);
            alphaDesc.enableRandomWrite = true;
            s.alphaRT = RenderTexture.GetTemporary(alphaDesc);

            var n = s.cropW * s.cropH;
            var premul = new Color[s.cropW * s.cropH];
            var alpha = new float[s.cropW * s.cropH];
            for (int i = 0; i < n; i++)
            {
                var c = s.crop[i];
                float r = c.r / 255f, g = c.g / 255f, b = c.b / 255f, a = c.a / 255f;
                float lr = ATOColorMath.SRGBToLinear(r);
                float lg = ATOColorMath.SRGBToLinear(g);
                float lb = ATOColorMath.SRGBToLinear(b);
                premul[i] = new Color(lr * a, lg * a, lb * a, a);
                alpha[i] = a;
            }

            var cpu = new Texture2D(s.cropW, s.cropH, TextureFormat.RGBAFloat, false, true);
            cpu.SetPixels(premul);
            cpu.Apply(false, false);
            var prev = RenderTexture.active;
            Graphics.Blit(cpu, s.srcRT);
            UnityEngine.Object.DestroyImmediate(cpu);

            var cpuA = new Texture2D(s.cropW, s.cropH, TextureFormat.RFloat, false, true);
            cpuA.SetPixelData(alpha, 0);
            cpuA.Apply(false, false);
            Graphics.Blit(cpuA, s.alphaRT);
            UnityEngine.Object.DestroyImmediate(cpuA);
            RenderTexture.active = prev;
        }

        /// <summary>
        /// GPU 回合: 原生分辨率重采样(下采样+上采样) -> 回读 -> 降采样到比较分辨率, 写入共享 up 缓冲.
        /// GPU round: native-res resample (down+up) -> readback -> downscale to the comparison res into the shared up buffers.
        /// </summary>
        private static void GpuRound(ATOIslandSearchData s, ATOEvalItem item,
            NativeArray<float> srcAll, NativeArray<float> alphaAll, int srcOffset, NativeArray<float> upNA, NativeArray<float> upAlphaNA)
        {
            if (ATOGpu.ResampleIsland(s.srcRT, s.alphaRT, null, null, s.cropW, s.cropH, item.sx, item.sy,
                    out var upBuf, out var upABuf, out _))
            {
                int w = item.w, h = item.h;
                var upSlice = upNA.Slice(item.upOffset * 4, w * h * 4);
                var upASlice = upAlphaNA.Slice(item.upOffset, w * h);

                // 降采样到比较分辨率 / downscale to the comparison resolution
                int scaleDown = s.cropW / w;
                if (scaleDown < 1) scaleDown = 1;
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        float sr = 0, sg = 0, sb = 0, sa = 0;
                        int cnt = 0;
                        for (int dy = 0; dy < scaleDown; dy++)
                        {
                            for (int dx = 0; dx < scaleDown; dx++)
                            {
                                int px = x * scaleDown + dx, py = y * scaleDown + dy;
                                if (px >= s.cropW || py >= s.cropH) continue;
                                int i = py * s.cropW + px;
                                sr += upBuf[i * 4];
                                sg += upBuf[i * 4 + 1];
                                sb += upBuf[i * 4 + 2];
                                sa += upABuf[i];
                                cnt++;
                            }
                        }

                        if (cnt == 0) continue;
                        int oi = y * w + x;
                        upSlice[oi * 4] = sr / cnt;
                        upSlice[oi * 4 + 1] = sg / cnt;
                        upSlice[oi * 4 + 2] = sb / cnt;
                        upSlice[oi * 4 + 3] = sa / cnt;
                        upASlice[oi] = sa / cnt;
                    }
                }

                upBuf.Dispose();
                upABuf.Dispose();
            }
            else
            {
                // GPU 失败 -> 按1:1复制(保守: 不缩放) / GPU failure -> copy 1:1 (conservative: no resizing)
                int w = item.w, h = item.h;
                var upSlice = upNA.Slice(item.upOffset * 4, w * h * 4);
                var upASlice = upAlphaNA.Slice(item.upOffset, w * h);
                upSlice.CopyFrom(srcAll.Slice(srcOffset * 4, w * h * 4));
                upASlice.CopyFrom(alphaAll.Slice(srcOffset, w * h));
            }
        }
    }
}
