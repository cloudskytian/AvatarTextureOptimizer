// SPDX-License-Identifier: MIT
// EN: The target quality algorithm. Determines, per UV island and per texture, the largest downscale that
//     still satisfies every configured perceptual threshold.
// ZH: 目标质量算法。为每个 UV 岛、每张贴图求出在满足全部感知阈值前提下可以做到的最大缩小比例。

using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// EN: Detailed outcome of one quality evaluation, kept for the report.
    /// ZH: 一次质量评估的详细结果，用于报告。
    /// </summary>
    public struct ATOQualityResult
    {
        public bool Passed;
        public float Ssim;
        public float DeltaEMean;
        public float DeltaEP95;
        public float AlphaIoU;
        public float AlphaRmse;
        public float NormalMeanDeg;
        public float NormalP95Deg;
        public float GrayRmse;

        public override string ToString() =>
            $"pass={Passed} ssim={Ssim:F4} dE={DeltaEMean:F2}/{DeltaEP95:F2} " +
            $"alpha={AlphaIoU:F4}/{AlphaRmse:F4} normal={NormalMeanDeg:F2}/{NormalP95Deg:F2} gray={GrayRmse:F4}";
    }

    /// <summary>
    /// EN: Evaluates islands. One instance per build; not thread safe (jobs are scheduled internally).
    /// ZH: 岛评估器。每次构建一个实例；非线程安全（内部会调度 Job）。
    /// </summary>
    public sealed class ATOQualityEvaluator : IDisposable
    {
        // EN: Standard MS-SSIM scale weights (Wang, Simoncelli & Bovik, 2003).
        // ZH: 标准 MS-SSIM 各尺度权重（Wang、Simoncelli 与 Bovik，2003）。
        private static readonly float[] MsSsimWeights = { 0.0448f, 0.2856f, 0.3001f, 0.2363f, 0.1333f };

        private const int MsSsimMinShortSide = 176;
        private const int SsimMinShortSide = 11;
        private const int BinarySearchIterations = 8;
        private const int AxisRefineIterations = 5;

        private readonly ATOLog _log;
        private readonly ATOTextureCache _cache;
        private readonly ATOQualityParameters _q;
        private readonly bool _lossless;

        public ATOQualityEvaluator(ATOLog log, ATOTextureCache cache, ATOQualityParameters quality, bool lossless)
        {
            _log = log;
            _cache = cache;
            _q = quality;
            _lossless = lossless;
        }

        /// <summary>
        /// EN: Reference data of one island inside one texture, reused across all binary search steps.
        /// ZH: 某贴图中某个岛的参考数据，在整个二分搜索过程中复用。
        /// </summary>
        private sealed class Reference : IDisposable
        {
            public RectInt Rect;
            public NativeArray<float4> Pixels;      // EN: premultiplied when transparent. ZH: 透明时为预乘。
            public NativeArray<float4> Straight;    // EN: non premultiplied reference. ZH: 未预乘的参考。
            public NativeArray<float4> Lab;
            public NativeArray<byte> Coverage;
            public int CoveredTexels;
            public bool FlatColor;

            public void Dispose()
            {
                if (Pixels.IsCreated) Pixels.Dispose();
                if (Straight.IsCreated) Straight.Dispose();
                if (Lab.IsCreated) Lab.Dispose();
                if (Coverage.IsCreated) Coverage.Dispose();
            }
        }

        /// <summary>
        /// EN: Finds the per axis scale for one island of one texture.
        /// ZH: 求出某贴图某个岛的双轴缩放系数。
        /// </summary>
        public Vector2 FindIslandScale(ATOTextureInfo texture, ATOIsland island, Vector2[] uv, int[] triangleIndices,
            out ATOQualityResult best)
        {
            best = default;
            best.Passed = true;

            if (_lossless) return Vector2.one;

            using var reference = BuildReference(texture, island, uv, triangleIndices);
            if (reference == null || reference.CoveredTexels == 0) return Vector2.one;

            var shortSide = Mathf.Min(reference.Rect.width, reference.Rect.height);

            // EN: Flat colour islands short circuit to the smallest sensible size.
            // ZH: 纯色岛直接短路缩到最小尺寸。
            if (reference.FlatColor)
            {
                var target = Mathf.Min(4, shortSide);
                var s = target / (float)Mathf.Max(1, shortSide);
                _log.Trace("quality", $"{texture} {island}: flat colour -> scale {s:F3}");
                return new Vector2(s, s);
            }

            var (lo, hi) = DensityBounds(island, reference);
            if (hi <= lo + 1e-4f) return new Vector2(hi, hi);

            // EN: Uniform binary search for the smallest passing scale. ZH: 先用均匀二分搜索找最小可行比例。
            var passing = hi;
            var low = lo;
            var high = hi;
            for (var i = 0; i < BinarySearchIterations; i++)
            {
                var mid = 0.5f * (low + high);
                var r = Evaluate(texture, reference, new Vector2(mid, mid));
                if (r.Passed)
                {
                    passing = mid;
                    best = r;
                    high = mid;
                }
                else
                {
                    low = mid;
                }

                if (high - low < 0.01f) break;
            }

            // EN: Anisotropic refinement: shrink each axis further while the other stays fixed.
            // ZH: 各向异性细化：固定一个轴，继续压缩另一个轴。
            var scale = new Vector2(passing, passing);
            for (var axis = 0; axis < 2; axis++)
            {
                var l = lo;
                var h = scale[axis];
                for (var i = 0; i < AxisRefineIterations; i++)
                {
                    var mid = 0.5f * (l + h);
                    var candidate = scale;
                    candidate[axis] = mid;
                    var r = Evaluate(texture, reference, candidate);
                    if (r.Passed)
                    {
                        scale[axis] = mid;
                        best = r;
                        h = mid;
                    }
                    else
                    {
                        l = mid;
                    }

                    if (h - l < 0.01f) break;
                }
            }

            _log.Trace("quality",
                $"{texture} island#{island.Index} {reference.Rect.width}x{reference.Rect.height} -> " +
                $"scale ({scale.x:F3}, {scale.y:F3}) {best}");
            return scale;
        }

        /// <summary>
        /// EN: Scale bounds implied by the texel density limits and by never upscaling.
        /// ZH: 由像素密度限制以及“绝不放大”原则推出的缩放上下界。
        /// </summary>
        private (float lo, float hi) DensityBounds(ATOIsland island, Reference reference)
        {
            var hi = 1f;
            var lo = 1f / 512f;

            if (island.WorldArea > 1e-8f && reference.CoveredTexels > 0)
            {
                var density = Mathf.Sqrt(reference.CoveredTexels / island.WorldArea); // px per meter
                if (_q.maxPixelDensity > 0)
                    hi = Mathf.Clamp(_q.maxPixelDensity / density, 1f / 512f, 1f);
                if (_q.minPixelDensity > 0)
                    lo = Mathf.Clamp(_q.minPixelDensity / density, 1f / 512f, 1f);

                if (lo > hi) lo = hi;
            }

            return (lo, hi);
        }

        // ------------------------------------------------------------------ reference construction

        private Reference BuildReference(ATOTextureInfo texture, ATOIsland island, Vector2[] uv,
            int[] triangleIndices)
        {
            var decoded = _cache.Get(texture.Source, texture.Role == ATOTextureRole.Normal);
            var rect = ATORaster.IslandPixelRect(island.Bounds, decoded.Width, decoded.Height);

            var coverage = ATORaster.RasterizeCoverage(uv, triangleIndices, island.Triangles, rect,
                decoded.Width, decoded.Height, Allocator.Persistent);

            var covered = ATORaster.CountCoverage(coverage);
            if (covered == 0)
            {
                coverage.Dispose();
                return null;
            }

            var count = rect.width * rect.height;
            var premultiply = texture.Role == ATOTextureRole.ColorTransparent;

            var pixels = new NativeArray<float4>(count, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            var straight = new NativeArray<float4>(count, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);

            new ATOExtractRegionJob
            {
                Source = decoded.Pixels,
                Destination = pixels,
                SourceWidth = decoded.Width,
                SourceHeight = decoded.Height,
                X0 = rect.x,
                Y0 = rect.y,
                Width = rect.width,
                Height = rect.height,
                PremultiplyAlpha = premultiply,
            }.Schedule(rect.height, 1).Complete();

            new ATOExtractRegionJob
            {
                Source = decoded.Pixels,
                Destination = straight,
                SourceWidth = decoded.Width,
                SourceHeight = decoded.Height,
                X0 = rect.x,
                Y0 = rect.y,
                Width = rect.width,
                Height = rect.height,
                PremultiplyAlpha = false,
            }.Schedule(rect.height, 1).Complete();

            var lab = default(NativeArray<float4>);
            if (texture.Role == ATOTextureRole.ColorOpaque || texture.Role == ATOTextureRole.ColorTransparent)
            {
                lab = new NativeArray<float4>(count, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                new ATOLinearToLabJob { Source = straight, Destination = lab }.Schedule(count, 512).Complete();
            }

            var flat = IsFlat(straight, coverage);

            island.SourcePixelSize = new Vector2Int(rect.width, rect.height);
            island.IsFlatColor = flat;

            return new Reference
            {
                Rect = rect,
                Pixels = pixels,
                Straight = straight,
                Lab = lab,
                Coverage = coverage,
                CoveredTexels = covered,
                FlatColor = flat,
            };
        }

        private static bool IsFlat(NativeArray<float4> pixels, NativeArray<byte> coverage)
        {
            var first = float4.zero;
            var haveFirst = false;
            for (var i = 0; i < pixels.Length; i++)
            {
                if (coverage[i] == 0) continue;
                if (!haveFirst)
                {
                    first = pixels[i];
                    haveFirst = true;
                    continue;
                }

                if (math.any(math.abs(pixels[i] - first) > 1.5f / 255f)) return false;
            }

            return haveFirst;
        }

        // ------------------------------------------------------------------ evaluation

        private ATOQualityResult Evaluate(ATOTextureInfo texture, Reference reference, Vector2 scale)
        {
            var w = reference.Rect.width;
            var h = reference.Rect.height;
            var dw = Mathf.Max(1, Mathf.RoundToInt(w * scale.x));
            var dh = Mathf.Max(1, Mathf.RoundToInt(h * scale.y));

            var result = new ATOQualityResult { Passed = true, Ssim = 1f, AlphaIoU = 1f };
            if (dw >= w && dh >= h) return result;

            var small = new NativeArray<float4>(dw * dh, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            var restored = new NativeArray<float4>(w * h, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);

            try
            {
                new ATODownsampleJob
                {
                    Source = reference.Pixels,
                    Destination = small,
                    SrcWidth = w,
                    SrcHeight = h,
                    DstWidth = dw,
                    DstHeight = dh,
                }.Schedule(dh, 1).Complete();

                if (texture.Role == ATOTextureRole.Normal) Renormalise(small);

                new ATOUpsampleJob
                {
                    Source = small,
                    Destination = restored,
                    SrcWidth = dw,
                    SrcHeight = dh,
                    DstWidth = w,
                    DstHeight = h,
                    UnpremultiplyAlpha = texture.Role == ATOTextureRole.ColorTransparent,
                }.Schedule(h, 1).Complete();

                if (texture.Role == ATOTextureRole.Normal) Renormalise(restored);

                switch (texture.Role)
                {
                    case ATOTextureRole.Normal:
                        EvaluateNormal(reference, restored, ref result);
                        break;
                    case ATOTextureRole.Grayscale:
                        EvaluateGrayscale(texture, reference, restored, ref result);
                        break;
                    default:
                        EvaluateColor(texture, reference, restored, ref result);
                        break;
                }
            }
            finally
            {
                small.Dispose();
                restored.Dispose();
            }

            return result;
        }

        private static void Renormalise(NativeArray<float4> data)
        {
            for (var i = 0; i < data.Length; i++)
            {
                var c = data[i];
                data[i] = new float4(math.normalizesafe(c.xyz, new float3(0, 0, 1)), c.w);
            }
        }

        private void EvaluateColor(ATOTextureInfo texture, Reference reference, NativeArray<float4> restored,
            ref ATOQualityResult result)
        {
            var w = reference.Rect.width;
            var h = reference.Rect.height;

            // ---- structural similarity
            var shortSide = Mathf.Min(w, h);
            if (shortSide >= SsimMinShortSide)
            {
                var scales = shortSide >= MsSsimMinShortSide ? MsSsimWeights.Length : 1;
                result.Ssim = ComputeMultiScaleSsim(reference.Straight, restored, reference.Coverage, w, h, scales);
                if (result.Ssim < _q.minStructuralSimilarity) result.Passed = false;
            }

            // ---- colour difference
            var restoredLab = new NativeArray<float4>(restored.Length, Allocator.TempJob,
                NativeArrayOptions.UninitializedMemory);
            try
            {
                new ATOLinearToLabJob { Source = restored, Destination = restoredLab }
                    .Schedule(restored.Length, 512).Complete();

                var sums = new NativeArray<double>(h, Allocator.TempJob);
                var counts = new NativeArray<int>(h, Allocator.TempJob);
                var hist = new NativeArray<int>(h * 1024, Allocator.TempJob, NativeArrayOptions.ClearMemory);
                try
                {
                    new ATODeltaE2000Job
                    {
                        LabA = reference.Lab,
                        LabB = restoredLab,
                        Coverage = reference.Coverage,
                        SumPerRow = sums,
                        CountPerRow = counts,
                        Histogram = hist,
                        Width = w,
                    }.Schedule(h, 1).Complete();

                    var total = 0;
                    double sum = 0;
                    for (var y = 0; y < h; y++)
                    {
                        total += counts[y];
                        sum += sums[y];
                    }

                    if (total > 0)
                    {
                        result.DeltaEMean = (float)(sum / total);
                        result.DeltaEP95 = Percentile(hist, h, total, 0.95f) / 10f;
                        if (result.DeltaEMean > _q.maxDeltaE2000Mean) result.Passed = false;
                        if (result.DeltaEP95 > _q.maxDeltaE2000P95) result.Passed = false;
                    }
                }
                finally
                {
                    sums.Dispose();
                    counts.Dispose();
                    hist.Dispose();
                }
            }
            finally
            {
                restoredLab.Dispose();
            }

            // ---- alpha
            if (texture.Role != ATOTextureRole.ColorTransparent) return;

            var cutoffs = new List<float>(texture.Cutoffs);
            if (cutoffs.Count == 0) cutoffs.Add(0.5f);

            var worstIoU = 1f;
            var worstRmse = 0f;

            foreach (var cutoff in cutoffs)
            {
                var inter = new NativeArray<int>(h, Allocator.TempJob);
                var uni = new NativeArray<int>(h, Allocator.TempJob);
                var sq = new NativeArray<double>(h, Allocator.TempJob);
                var cnt = new NativeArray<int>(h, Allocator.TempJob);
                try
                {
                    new ATOAlphaJob
                    {
                        A = reference.Straight,
                        B = restored,
                        Coverage = reference.Coverage,
                        IntersectionPerRow = inter,
                        UnionPerRow = uni,
                        SqErrPerRow = sq,
                        CountPerRow = cnt,
                        Width = w,
                        Cutoff = cutoff,
                    }.Schedule(h, 1).Complete();

                    long i0 = 0, u0 = 0, n0 = 0;
                    double s0 = 0;
                    for (var y = 0; y < h; y++)
                    {
                        i0 += inter[y];
                        u0 += uni[y];
                        s0 += sq[y];
                        n0 += cnt[y];
                    }

                    var iou = u0 > 0 ? (float)((double)i0 / u0) : 1f;
                    var rmse = n0 > 0 ? (float)Math.Sqrt(s0 / n0) : 0f;
                    worstIoU = Mathf.Min(worstIoU, iou);
                    worstRmse = Mathf.Max(worstRmse, rmse);
                }
                finally
                {
                    inter.Dispose();
                    uni.Dispose();
                    sq.Dispose();
                    cnt.Dispose();
                }
            }

            result.AlphaIoU = worstIoU;
            result.AlphaRmse = worstRmse;

            if (texture.AlphaMode == ATOAlphaMode.Cutout && worstIoU < _q.minAlphaIoU) result.Passed = false;
            if (texture.AlphaMode == ATOAlphaMode.Blend && worstRmse > _q.maxAlphaRmse) result.Passed = false;
        }

        private void EvaluateNormal(Reference reference, NativeArray<float4> restored, ref ATOQualityResult result)
        {
            var w = reference.Rect.width;
            var h = reference.Rect.height;

            var sums = new NativeArray<double>(h, Allocator.TempJob);
            var counts = new NativeArray<int>(h, Allocator.TempJob);
            var hist = new NativeArray<int>(h * 1024, Allocator.TempJob, NativeArrayOptions.ClearMemory);
            try
            {
                new ATONormalAngleJob
                {
                    A = reference.Straight,
                    B = restored,
                    Coverage = reference.Coverage,
                    SumPerRow = sums,
                    CountPerRow = counts,
                    Histogram = hist,
                    Width = w,
                }.Schedule(h, 1).Complete();

                var total = 0;
                double sum = 0;
                for (var y = 0; y < h; y++)
                {
                    total += counts[y];
                    sum += sums[y];
                }

                if (total == 0) return;

                result.NormalMeanDeg = (float)(sum / total);
                result.NormalP95Deg = Percentile(hist, h, total, 0.95f) / 5f;

                if (result.NormalMeanDeg > _q.maxNormalAngleMeanDeg) result.Passed = false;
                if (result.NormalP95Deg > _q.maxNormalAngleP95Deg) result.Passed = false;
            }
            finally
            {
                sums.Dispose();
                counts.Dispose();
                hist.Dispose();
            }
        }

        private void EvaluateGrayscale(ATOTextureInfo texture, Reference reference, NativeArray<float4> restored,
            ref ATOQualityResult result)
        {
            var w = reference.Rect.width;
            var h = reference.Rect.height;

            var sq = new NativeArray<double4>(h, Allocator.TempJob);
            var cnt = new NativeArray<int>(h, Allocator.TempJob);
            try
            {
                new ATOChannelRmseJob
                {
                    A = reference.Straight,
                    B = restored,
                    Coverage = reference.Coverage,
                    SqErrPerRow = sq,
                    CountPerRow = cnt,
                    Width = w,
                }.Schedule(h, 1).Complete();

                var total = 0;
                var sum = double4.zero;
                for (var y = 0; y < h; y++)
                {
                    total += cnt[y];
                    sum += sq[y];
                }

                if (total == 0) return;

                var worst = 0f;
                for (var c = 0; c < 4; c++)
                {
                    if (!texture.UsedChannels[c]) continue;
                    var rmse = (float)Math.Sqrt(sum[c] / total);
                    worst = Mathf.Max(worst, rmse);
                }

                result.GrayRmse = worst;
                if (worst > _q.maxGrayscaleRmse) result.Passed = false;
            }
            finally
            {
                sq.Dispose();
                cnt.Dispose();
            }
        }

        // ------------------------------------------------------------------ SSIM

        /// <summary>
        /// EN: MS-SSIM as defined by Wang, Simoncelli &amp; Bovik (2003): the contrast-structure term of every
        ///     scale, multiplied by the full SSIM of the coarsest scale, each raised to its scale weight.
        ///     Small islands fall back to single scale SSIM (see the class constants).
        /// ZH: 严格按 Wang、Simoncelli 与 Bovik (2003) 定义的 MS-SSIM：各尺度取对比度-结构项，
        ///     最粗尺度取完整 SSIM，分别按尺度权重加权相乘。小岛回退到单尺度 SSIM（见类常量）。
        /// </summary>
        private float ComputeMultiScaleSsim(NativeArray<float4> a, NativeArray<float4> b, NativeArray<byte> coverage,
            int width, int height, int scales)
        {
            var curA = a;
            var curB = b;
            var curCov = coverage;
            var ownA = false;
            var ownB = false;
            var ownCov = false;
            var w = width;
            var h = height;

            var product = 1f;
            var weightSum = 0f;

            try
            {
                for (var s = 0; s < scales; s++)
                {
                    var (ssim, cs) = ComputeSsim(curA, curB, curCov, w, h);
                    var weight = scales == 1 ? 1f : MsSsimWeights[s];

                    var nw = Mathf.Max(1, w / 2);
                    var nh = Mathf.Max(1, h / 2);
                    var isLastScale = s == scales - 1 || nw < 8 || nh < 8;

                    // EN: only the coarsest scale contributes the luminance term. ZH: 只有最粗尺度贡献亮度项。
                    var term = isLastScale ? ssim : cs;
                    product *= Mathf.Pow(Mathf.Clamp(term, 1e-4f, 1f), weight);
                    weightSum += weight;

                    if (isLastScale) break;

                    var nextA = Downsample(curA, w, h, nw, nh);
                    var nextB = Downsample(curB, w, h, nw, nh);
                    var nextCov = DownsampleCoverage(curCov, w, h, nw, nh);

                    if (ownA) curA.Dispose();
                    if (ownB) curB.Dispose();
                    if (ownCov) curCov.Dispose();

                    curA = nextA;
                    curB = nextB;
                    curCov = nextCov;
                    ownA = ownB = ownCov = true;
                    w = nw;
                    h = nh;
                }
            }
            finally
            {
                if (ownA) curA.Dispose();
                if (ownB) curB.Dispose();
                if (ownCov) curCov.Dispose();
            }

            return weightSum > 0f ? Mathf.Pow(product, 1f / weightSum) : 1f;
        }

        /// <summary>
        /// EN: Returns (mean SSIM, mean contrast-structure) over all sufficiently covered 8x8 windows.
        /// ZH: 返回所有覆盖足够的 8x8 窗口上的（SSIM 均值, 对比度-结构均值）。
        /// </summary>
        private static (float ssim, float cs) ComputeSsim(NativeArray<float4> a, NativeArray<float4> b,
            NativeArray<byte> coverage, int width, int height)
        {
            const int window = 8;
            var rows = Mathf.Max(1, height / window);

            var sums = new NativeArray<double>(rows, Allocator.TempJob);
            var cs = new NativeArray<double>(rows, Allocator.TempJob);
            var counts = new NativeArray<int>(rows, Allocator.TempJob);
            try
            {
                new ATOSsimJob
                {
                    A = a,
                    B = b,
                    Coverage = coverage,
                    SumPerRow = sums,
                    CsPerRow = cs,
                    CountPerRow = counts,
                    Width = width,
                    Height = height,
                    Window = window,
                }.Schedule(rows, 1).Complete();

                double sum = 0;
                double csSum = 0;
                var count = 0;
                for (var i = 0; i < rows; i++)
                {
                    sum += sums[i];
                    csSum += cs[i];
                    count += counts[i];
                }

                return count > 0 ? ((float)(sum / count), (float)(csSum / count)) : (1f, 1f);
            }
            finally
            {
                sums.Dispose();
                cs.Dispose();
                counts.Dispose();
            }
        }

        private static NativeArray<float4> Downsample(NativeArray<float4> src, int w, int h, int nw, int nh)
        {
            var dst = new NativeArray<float4>(nw * nh, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            new ATODownsampleJob
            {
                Source = src,
                Destination = dst,
                SrcWidth = w,
                SrcHeight = h,
                DstWidth = nw,
                DstHeight = nh,
            }.Schedule(nh, 1).Complete();
            return dst;
        }

        private static NativeArray<byte> DownsampleCoverage(NativeArray<byte> src, int w, int h, int nw, int nh)
        {
            var dst = new NativeArray<byte>(nw * nh, Allocator.TempJob, NativeArrayOptions.ClearMemory);
            for (var y = 0; y < nh; y++)
            for (var x = 0; x < nw; x++)
            {
                var sx = Mathf.Min(w - 1, x * w / nw);
                var sy = Mathf.Min(h - 1, y * h / nh);
                dst[y * nw + x] = src[sy * w + sx];
            }

            return dst;
        }

        private static float Percentile(NativeArray<int> histogram, int rows, int total, float percentile)
        {
            var target = (int)(total * percentile);
            var acc = 0;
            for (var bin = 0; bin < 1024; bin++)
            {
                for (var y = 0; y < rows; y++) acc += histogram[y * 1024 + bin];
                if (acc >= target) return bin;
            }

            return 1023;
        }

        public void Dispose()
        {
        }
    }
}
