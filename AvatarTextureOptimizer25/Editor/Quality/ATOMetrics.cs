// Avatar Texture Optimizer / 头像贴图优化器
// Managed orchestration of the Burst metrics: single-scale SSIM, MS-SSIM
// (with fallbacks driven by island bbox short edge), DeltaE2000, alpha RMSE,
// cutout IoU, normal angular mean/p95, gray per-channel RMSE.
// Burst 指标的托管编排：单尺度 SSIM、MS-SSIM（按岛包围盒短边回退）、ΔE2000、
// alpha RMSE、Cutout IoU、法线角度均值/p95、灰度分通道 RMSE。

using System;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace FOSA.AvatarTextureOptimizer.Editor
{
    /// <summary>Image pair helper holding crop arrays. / 承载裁剪数组的图像对。</summary>
    public struct ATOCropPair
    {
        public Color32[] a;   // original / 原图
        public Color32[] b;   // processed / 处理后
        public int width, height;
        /// <summary>Coverage mask (null = full). / 覆盖掩码（null=全覆盖）。</summary>
        public bool[] mask;
    }

    /// <summary>Static metric entry points. / 静态指标入口。</summary>
    public static class ATOMetrics
    {
        private static readonly float[] Gaussian11 = BuildGauss11();
        private static readonly float[] MsSsimWeights = { 0.0448f, 0.2856f, 0.3001f, 0.2363f, 0.1333f };

        private static float[] BuildGauss11()
        {
            var k = new float[11];
            float sum = 0;
            for (int i = 0; i < 11; i++)
            {
                float d = i - 5;
                k[i] = Mathf.Exp(-d * d / (2f * 1.5f * 1.5f));
                sum += k[i];
            }
            for (int i = 0; i < 11; i++) k[i] /= sum;
            return k;
        }

        // --------------------------------------------------------------
        // SSIM / MS-SSIM / SSIM 与 MS-SSIM
        // --------------------------------------------------------------

        /// <summary>
        /// Single-scale or multi-scale SSIM chosen by bbox short edge. The mean
        /// is taken over the island coverage mask only (spec: compare the actual
        /// coverage region).
        /// 按包围盒短边选择单尺度或多尺度 SSIM。均值只在岛覆盖掩码内取
        ///（需求：比较实际覆盖区）。
        /// </summary>
        public static float ScoreSSIM(ATOCropPair pair)
        {
            int n = pair.width * pair.height;
            if (n == 0) return 1f;
            int shortEdge = Mathf.Min(pair.width, pair.height);
            if (shortEdge < ATOConsts.SsimIgnoreShortEdge) return 1f; // ignored / 忽略

            var lumaA = new NativeArray<float>(n, Allocator.TempJob);
            var lumaB = new NativeArray<float>(n, Allocator.TempJob);
            NativeArray<byte> mask = ToNativeMask(pair);
            try
            {
                ComputeLuma(pair, lumaA, lumaB);
                if (shortEdge < ATOConsts.MsSsimMinShortEdge)
                {
                    return SsimAtScale(lumaA, lumaB, pair.width, pair.height, mask);
                }
                return MsSsim(lumaA, lumaB, pair.width, pair.height, mask);
            }
            finally
            {
                if (lumaA.IsCreated) lumaA.Dispose();
                if (lumaB.IsCreated) lumaB.Dispose();
                if (mask.IsCreated) mask.Dispose();
            }
        }

        /// <summary>bool[] coverage -&gt; NativeArray&lt;byte&gt; (null = everything covered). / bool[] 覆盖转 NativeArray&lt;byte&gt;（null=全覆盖）。</summary>
        private static NativeArray<byte> ToNativeMask(ATOCropPair pair)
        {
            if (pair.mask == null) return default(NativeArray<byte>);
            int n = pair.width * pair.height;
            var m = new NativeArray<byte>(n, Allocator.TempJob);
            for (int i = 0; i < n; i++) m[i] = pair.mask[i] ? (byte)1 : (byte)0;
            return m;
        }

        private static void ComputeLuma(ATOCropPair pair, NativeArray<float> lumaA, NativeArray<float> lumaB)
        {
            var a = new NativeArray<Color32>(pair.a, Allocator.TempJob);
            var b = new NativeArray<Color32>(pair.b, Allocator.TempJob);
            try
            {
                JobHandle.ScheduleBatchedJobs();
                var j1 = new BytesToLumaJob { src = a, luma = lumaA }.Schedule(a.Length, 64);
                var j2 = new BytesToLumaJob { src = b, luma = lumaB }.Schedule(b.Length, 64);
                JobHandle.CombineDependencies(j1, j2).Complete();
            }
            finally
            {
                a.Dispose();
                b.Dispose();
            }
        }

        private static float SsimAtScale(NativeArray<float> x, NativeArray<float> y, int w, int h, NativeArray<byte> mask)
        {
            int n = w * h;
            var stats = new NativeArray<float>(n * 5, Allocator.TempJob);
            var tmp = new NativeArray<float>(n * 5, Allocator.TempJob);
            var map = new NativeArray<float>(n, Allocator.TempJob);
            var kernel = new NativeArray<float>(Gaussian11, Allocator.TempJob);
            try
            {
                var h0 = new SsimStatMapsJob { x = x, y = y, stats = stats }.Schedule(n, 64);
                var h1 = new GaussBlurHJob { src = stats, dst = tmp, width = w, height = h, channels = 5, kernel = kernel }
                    .Schedule(h, 1, h0);
                var h2 = new GaussBlurVJob { src = tmp, dst = stats, width = w, height = h, channels = 5, kernel = kernel }
                    .Schedule(w, 1, h1);
                var h3 = new SsimCombineJob { stats = stats, map = map, contrastStructureOnly = false }.Schedule(n, 64, h2);
                h3.Complete();
                double sum = 0;
                int count = 0;
                for (int i = 0; i < n; i++)
                {
                    if (mask.IsCreated && mask[i] == 0) continue;
                    sum += map[i];
                    count++;
                }
                return count > 0 ? Mathf.Clamp((float)(sum / count), 0f, 1f) : 1f;
            }
            finally
            {
                stats.Dispose();
                tmp.Dispose();
                map.Dispose();
                kernel.Dispose();
            }
        }

        private static float MsSsim(NativeArray<float> x0, NativeArray<float> y0, int w, int h, NativeArray<byte> mask0)
        {
            // Multi-scale SSIM (Wang 2003) with graceful early stop: each executed
            // scale contributes its own weight; the deepest executed scale replaces
            // the final scale's SSIM term (renormalizing the remaining weight mass).
            // 多尺度 SSIM（Wang 2003），不可行层提前终止：每个执行层贡献自身权重，
            // 最深执行层替代末层 SSIM 项（剩余权重质量归一化）。
            var x = new NativeArray<float>(x0, Allocator.TempJob);
            var y = new NativeArray<float>(y0, Allocator.TempJob);
            // Coverage mask pyramid mirrors the luma pyramid (2x2 OR rule).
            // 覆盖掩码金字塔与亮度金字塔同步（2x2 或规则）。
            NativeArray<byte> mask = mask0.IsCreated
                ? new NativeArray<byte>(mask0, Allocator.TempJob)
                : default(NativeArray<byte>);
            int cw = w, ch = h;
            double product = 1.0;
            int lastLevelExecuted = -1;
            try
            {
                for (int level = 0; level < MsSsimWeights.Length; level++)
                {
                    bool feasible = Mathf.Min(cw, ch) >= 11;
                    if (!feasible) break;

                    // Is this the last feasible/allowed level? / 是否最后一个可行层？
                    bool isLastAllowed = level == MsSsimWeights.Length - 1;
                    int nw = Mathf.Max(1, cw / 2), nh = Mathf.Max(1, ch / 2);
                    bool nextFeasible = !isLastAllowed && Mathf.Min(nw, nh) >= 11 && cw > 1 && ch > 1;

                    if (!nextFeasible)
                    {
                        // Deepest executed level: SSIM term with this level's weight
                        // plus the folded-in weight of skipped deeper levels.
                        // 最深执行层：SSIM 项携带本层与被跳过的更深层的权重。
                        float ssim = SsimAtScale(x, y, cw, ch, mask);
                        float wSum = 0f;
                        for (int k = level; k < MsSsimWeights.Length; k++) wSum += MsSsimWeights[k];
                        product *= Math.Pow(Math.Max(0.0001, ssim), wSum);
                        lastLevelExecuted = level;
                        break;
                    }
                    else
                    {
                        float cs = CsAtScale(x, y, cw, ch, mask);
                        product *= Math.Pow(Math.Max(0.0001, cs), MsSsimWeights[level]);
                        lastLevelExecuted = level;
                        int nn = nw * nh;
                        var nx = new NativeArray<float>(nn, Allocator.TempJob);
                        var ny = new NativeArray<float>(nn, Allocator.TempJob);
                        var j1 = new DownsampleHalfJob { src = x, dst = nx, srcW = cw, srcH = ch }.Schedule(nn, 64);
                        var j2 = new DownsampleHalfJob { src = y, dst = ny, srcW = cw, srcH = ch }.Schedule(nn, 64);
                        JobHandle maskHandle = default(JobHandle);
                        NativeArray<byte> nmask = default(NativeArray<byte>);
                        if (mask.IsCreated)
                        {
                            nmask = new NativeArray<byte>(nn, Allocator.TempJob);
                            maskHandle = new MaskDownsampleJob { src = mask, dst = nmask, srcW = cw, srcH = ch }
                                .Schedule(nn, 64);
                        }
                        JobHandle.CombineDependencies(JobHandle.CombineDependencies(j1, j2), maskHandle).Complete();
                        x.Dispose(); y.Dispose();
                        if (mask.IsCreated) mask.Dispose();
                        mask = nmask;
                        x = nx; y = ny;
                        cw = nw; ch = nh;
                    }
                }
            }
            finally
            {
                if (x.IsCreated) x.Dispose();
                if (y.IsCreated) y.Dispose();
                if (mask.IsCreated) mask.Dispose();
            }
            if (lastLevelExecuted < 0) return 1f;
            return Mathf.Clamp((float)product, 0f, 1f);
        }

        private static float CsAtScale(NativeArray<float> x, NativeArray<float> y, int w, int h, NativeArray<byte> mask)
        {
            int n = w * h;
            var stats = new NativeArray<float>(n * 5, Allocator.TempJob);
            var tmp = new NativeArray<float>(n * 5, Allocator.TempJob);
            var map = new NativeArray<float>(n, Allocator.TempJob);
            var kernel = new NativeArray<float>(Gaussian11, Allocator.TempJob);
            try
            {
                var h0 = new SsimStatMapsJob { x = x, y = y, stats = stats }.Schedule(n, 64);
                var h1 = new GaussBlurHJob { src = stats, dst = tmp, width = w, height = h, channels = 5, kernel = kernel }
                    .Schedule(h, 1, h0);
                var h2 = new GaussBlurVJob { src = tmp, dst = stats, width = w, height = h, channels = 5, kernel = kernel }
                    .Schedule(w, 1, h1);
                var h3 = new SsimCombineJob { stats = stats, map = map, contrastStructureOnly = true }.Schedule(n, 64, h2);
                h3.Complete();
                double sum = 0;
                int count = 0;
                for (int i = 0; i < n; i++)
                {
                    if (mask.IsCreated && mask[i] == 0) continue;
                    sum += map[i];
                    count++;
                }
                return count > 0 ? Mathf.Clamp((float)(sum / count), -1f, 1f) : 1f;
            }
            finally
            {
                stats.Dispose();
                tmp.Dispose();
                map.Dispose();
                kernel.Dispose();
            }
        }

        // --------------------------------------------------------------
        // DeltaE2000 / 色差
        // --------------------------------------------------------------

        /// <summary>Mean DeltaE2000 over coverage. / 覆盖区 ΔE2000 均值。</summary>
        public static float MeanDeltaE2000(ATOCropPair pair)
        {
            int n = pair.width * pair.height;
            if (n == 0) return 0f;
            var a = new NativeArray<Color32>(pair.a, Allocator.TempJob);
            var b = new NativeArray<Color32>(pair.b, Allocator.TempJob);
            var labA = new NativeArray<float>(n * 3, Allocator.TempJob);
            var labB = new NativeArray<float>(n * 3, Allocator.TempJob);
            var de = new NativeArray<float>(n, Allocator.TempJob);
            try
            {
                var j1 = new BytesToLabJob { src = a, lab = labA }.Schedule(n, 32);
                var j2 = new BytesToLabJob { src = b, lab = labB }.Schedule(n, 32);
                var j3 = new DeltaE2000Job { labA = labA, labB = labB, de = de }
                    .Schedule(n, 64, JobHandle.CombineDependencies(j1, j2));
                j3.Complete();
                double sum = 0;
                int count = 0;
                for (int i = 0; i < n; i++)
                {
                    if (pair.mask != null && !pair.mask[i]) continue;
                    sum += de[i];
                    count++;
                }
                return count > 0 ? (float)(sum / count) : 0f;
            }
            finally
            {
                a.Dispose(); b.Dispose();
                labA.Dispose(); labB.Dispose();
                de.Dispose();
            }
        }

        // --------------------------------------------------------------
        // Alpha / 透明
        // --------------------------------------------------------------

        /// <summary>Linear RMSE of alpha over coverage. / 覆盖区 alpha 线性 RMSE。</summary>
        public static float AlphaRmse(ATOCropPair pair)
        {
            int n = pair.width * pair.height;
            if (n == 0) return 0f;
            var a = new NativeArray<Color32>(pair.a, Allocator.TempJob);
            var b = new NativeArray<Color32>(pair.b, Allocator.TempJob);
            var diff = new NativeArray<float>(n, Allocator.TempJob);
            try
            {
                new AlphaDiffJob { a = a, b = b, diff = diff }.Schedule(n, 64).Complete();
                double sum = 0;
                int count = 0;
                for (int i = 0; i < n; i++)
                {
                    if (pair.mask != null && !pair.mask[i]) continue;
                    sum += diff[i];
                    count++;
                }
                return count > 0 ? Mathf.Sqrt((float)(sum / count)) : 0f;
            }
            finally
            {
                a.Dispose(); b.Dispose();
                diff.Dispose();
            }
        }

        /// <summary>IoU of clipped silhouettes at a cutoff. / 指定 cutoff 下轮廓 IoU。</summary>
        public static float CutoutIoU(ATOCropPair pair, float cutoff)
        {
            int n = pair.width * pair.height;
            if (n == 0) return 1f;
            var a = new NativeArray<Color32>(pair.a, Allocator.TempJob);
            var b = new NativeArray<Color32>(pair.b, Allocator.TempJob);
            var ma = new NativeArray<byte>(n, Allocator.TempJob);
            var mb = new NativeArray<byte>(n, Allocator.TempJob);
            try
            {
                var j1 = new CutoutMaskJob { src = a, mask = ma, cutoff = cutoff }.Schedule(n, 64);
                var j2 = new CutoutMaskJob { src = b, mask = mb, cutoff = cutoff }.Schedule(n, 64);
                JobHandle.CombineDependencies(j1, j2).Complete();
                int inter = 0, union = 0;
                for (int i = 0; i < n; i++)
                {
                    if (pair.mask != null && !pair.mask[i]) continue;
                    int xa = ma[i], xb = mb[i];
                    if (xa == 1 || xb == 1) union++;
                    if (xa == 1 && xb == 1) inter++;
                }
                if (union == 0) return 1f;
                return (float)inter / union;
            }
            finally
            {
                a.Dispose(); b.Dispose();
                ma.Dispose(); mb.Dispose();
            }
        }

        // --------------------------------------------------------------
        // Normals / 法线
        // --------------------------------------------------------------

        /// <summary>Normal angular error: mean and p95 (degrees) over coverage. / 法线角度误差：覆盖区均值与 p95（度）。</summary>
        public static (float mean, float p95) NormalAngular(Color[] a, Color[] b, bool[] mask)
        {
            int n = a.Length;
            if (n == 0 || b.Length != n) return (0f, 0f);
            var na = new NativeArray<Color>(a, Allocator.TempJob);
            var nb = new NativeArray<Color>(b, Allocator.TempJob);
            var angle = new NativeArray<float>(n, Allocator.TempJob);
            try
            {
                new NormalAngleJob { a = na, b = nb, angle = angle }.Schedule(n, 64).Complete();
                const int bins = 4096;
                var hist = new int[bins];
                double sum = 0;
                int count = 0;
                for (int i = 0; i < n; i++)
                {
                    if (mask != null && !mask[i]) continue;
                    float v = Mathf.Clamp(angle[i], 0f, 180f);
                    sum += v;
                    count++;
                    int bin = Mathf.Clamp((int)(v / 180f * (bins - 1)), 0, bins - 1);
                    hist[bin]++;
                }
                if (count == 0) return (0f, 0f);
                int target = (int)Math.Ceiling(count * 0.95);
                int acc = 0;
                float p95 = 0f;
                for (int i = 0; i < bins; i++)
                {
                    acc += hist[i];
                    if (acc >= target)
                    {
                        p95 = i / (float)(bins - 1) * 180f;
                        break;
                    }
                }
                return ((float)(sum / count), p95);
            }
            finally
            {
                if (na.IsCreated) na.Dispose();
                if (nb.IsCreated) nb.Dispose();
                if (angle.IsCreated) angle.Dispose();
            }
        }

        // --------------------------------------------------------------
        // Grayscale / 灰度
        // --------------------------------------------------------------

        /// <summary>Worst per-used-channel linear RMSE. / 被使用通道中最差的线性 RMSE。</summary>
        public static float GrayRmseWorstChannel(ATOCropPair pair, int usedChannelMask)
        {
            int n = pair.width * pair.height;
            if (n == 0) return 0f;
            float worst = 0f;
            var a = new NativeArray<Color32>(pair.a, Allocator.TempJob);
            var b = new NativeArray<Color32>(pair.b, Allocator.TempJob);
            var diff = new NativeArray<float>(n, Allocator.TempJob);
            try
            {
                for (int ch = 0; ch < 4; ch++)
                {
                    if ((usedChannelMask & (1 << ch)) == 0) continue;
                    new GrayDiffJob { a = a, b = b, diff = diff, channel = ch }.Schedule(n, 64).Complete();
                    double sum = 0;
                    int count = 0;
                    for (int i = 0; i < n; i++)
                    {
                        if (pair.mask != null && !pair.mask[i]) continue;
                        sum += diff[i];
                        count++;
                    }
                    if (count > 0) worst = Mathf.Max(worst, Mathf.Sqrt((float)(sum / count)));
                }
                return worst;
            }
            finally
            {
                a.Dispose(); b.Dispose();
                diff.Dispose();
            }
        }
    }
}
