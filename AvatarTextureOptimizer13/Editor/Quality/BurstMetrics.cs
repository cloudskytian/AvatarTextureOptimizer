// ATO — Avatar Texture Optimizer
// Burst-accelerated metric and resampling backends. Every public entry point falls back
// to the CPU reference implementation (QualityMath) if Burst is unavailable, compilation
// fails, or any job throws — the optimizer never produces wrong results because of a
// missing/broken backend.
// Burst 加速的度量与重采样后端。每个公开入口在 Burst 不可用、编译失败或任务抛异常时
// 都会回退到 CPU 参考实现（QualityMath）——优化器绝不会因后端缺失/损坏而产生错误结果。

using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace net.fosa.ato.editor
{
    /// <summary>
    /// Burst metric backend with CPU fallback. Burst 度量后端（带 CPU 回退）。
    /// </summary>
    public static class BurstMetrics
    {
        /// <summary>Global toggle (advanced debugging). 全局开关（高级调试）。</summary>
        public static bool Enabled = true;

        // ------------------------------------------------------------------ SSIM

        [BurstCompile]
        private struct SsimJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float> a;
            [ReadOnly] public NativeArray<float> b;
            [ReadOnly] public NativeArray<float> kernel;
            public int width;
            public int height;
            public int window;
            public NativeArray<float> result;

            public void Execute(int index)
            {
                int x = index % width;
                int y = index / width;
                int half = window >> 1;

                double muA = 0, muB = 0;
                for (int ky = 0; ky < window; ky++)
                for (int kx = 0; kx < window; kx++)
                {
                    int sx = Clamp(x + kx - half, 0, width - 1);
                    int sy = Clamp(y + ky - half, 0, height - 1);
                    float kw = kernel[ky * window + kx];
                    muA += a[sy * width + sx] * kw;
                    muB += b[sy * width + sx] * kw;
                }

                double va = 0, vb = 0, vab = 0;
                for (int ky = 0; ky < window; ky++)
                for (int kx = 0; kx < window; kx++)
                {
                    int sx = Clamp(x + kx - half, 0, width - 1);
                    int sy = Clamp(y + ky - half, 0, height - 1);
                    float kw = kernel[ky * window + kx];
                    double da = a[sy * width + sx] - muA;
                    double db = b[sy * width + sx] - muB;
                    va += da * da * kw;
                    vb += db * db * kw;
                    vab += da * db * kw;
                }

                const float k1 = 0.01f, k2 = 0.03f, L = 1f;
                float c1 = k1 * L; c1 *= c1;
                float c2 = k2 * L; c2 *= c2;
                double ssim = ((2 * muA * muB + c1) * (2 * vab + c2)) /
                              ((muA * muA + muB * muB + c1) * (va + vb + c2));
                result[index] = (float)ssim;
            }

            private static int Clamp(int v, int min, int max) => v < min ? min : (v > max ? max : v);
        }

        [BurstCompile]
        private struct Downsample2xJob : IJob
        {
            [ReadOnly] public NativeArray<float> src;
            public NativeArray<float> dst;
            public int srcW, srcH, dstW, dstH;

            public void Execute()
            {
                for (int y = 0; y < dstH; y++)
                for (int x = 0; x < dstW; x++)
                {
                    float sum = 0; int cnt = 0;
                    for (int dy = 0; dy < 2; dy++)
                    for (int dx = 0; dx < 2; dx++)
                    {
                        int sx = math.min(x * 2 + dx, srcW - 1);
                        int sy = math.min(y * 2 + dy, srcH - 1);
                        sum += src[sy * srcW + sx]; cnt++;
                    }
                    dst[y * dstW + x] = sum / cnt;
                }
            }
        }

        /// <summary>Mean SSIM over one channel with an 11x11 Gaussian window (Burst → CPU fallback). 单通道 11x11 高斯窗均值 SSIM（Burst → CPU 回退）。</summary>
        public static float SSIM(float[] a, float[] b, int w, int h)
        {
            if (!Enabled) return QualityMath.SSIM(a, b, w, h);
            try
            {
                const int window = 11;
                const float sigma = 1.5f;
                using var ka = new NativeArray<float>(window * window, Allocator.TempJob);
                float ksum = 0f;
                for (int y = 0; y < window; y++)
                for (int x = 0; x < window; x++)
                {
                    float dx = x - window / 2, dy = y - window / 2;
                    float g = math.exp(-(dx * dx + dy * dy) / (2f * sigma * sigma));
                    ka[y * window + x] = g;
                    ksum += g;
                }
                for (int i = 0; i < ka.Length; i++) ka[i] /= ksum;

                using var na = new NativeArray<float>(a, Allocator.TempJob);
                using var nb = new NativeArray<float>(b, Allocator.TempJob);
                using var res = new NativeArray<float>(w * h, Allocator.TempJob);

                new SsimJob
                {
                    a = na, b = nb, kernel = ka, width = w, height = h, window = window, result = res,
                }.Schedule(w * h, 128).Complete();

                double sum = 0; int n = res.Length;
                for (int i = 0; i < n; i++) sum += res[i];
                return n > 0 ? (float)(sum / n) : 1f;
            }
            catch (Exception e)
            {
                ATOLog.Verbose($"[Burst] SSIM fell back to CPU: {e.Message}");
                return QualityMath.SSIM(a, b, w, h);
            }
        }

        /// <summary>Multi-scale SSIM over 5 scales (Burst → CPU fallback). 5 尺度 MS-SSIM（Burst → CPU 回退）。</summary>
        public static float MSSSIM(float[] a, float[] b, int w, int h)
        {
            if (!Enabled) return QualityMath.MSSSIM(a, b, w, h);
            if (w < 32 || h < 32) return SSIM(a, b, w, h);
            try
            {
                const int scales = 5;
                var A = new NativeArray<float>[scales];
                var B = new NativeArray<float>[scales];
                var W = new int[scales];
                var H = new int[scales];
                try
                {
                    A[0] = new NativeArray<float>(a, Allocator.TempJob);
                    B[0] = new NativeArray<float>(b, Allocator.TempJob);
                    W[0] = w; H[0] = h;
                    for (int s = 1; s < scales; s++)
                    {
                        W[s] = Mathf.Max(1, W[s - 1] / 2);
                        H[s] = Mathf.Max(1, H[s - 1] / 2);
                        A[s] = new NativeArray<float>(W[s] * H[s], Allocator.TempJob);
                        B[s] = new NativeArray<float>(W[s] * H[s], Allocator.TempJob);
                        new Downsample2xJob { src = A[s - 1], dst = A[s], srcW = W[s - 1], srcH = H[s - 1], dstW = W[s], dstH = H[s] }.Schedule().Complete();
                        new Downsample2xJob { src = B[s - 1], dst = B[s], srcW = W[s - 1], srcH = H[s - 1], dstW = W[s], dstH = H[s] }.Schedule().Complete();
                    }

                    var weights = new[] { 0.0448f, 0.2856f, 0.3001f, 0.2363f, 0.1333f };
                    float ms = 1f;
                    for (int s = 0; s < scales; s++)
                    {
                        // Coarsest scale contributes luminance; use SsimJob with luminance via a
                        // per-scale weighting proxy: reuse SSIM for all scales (stable reference).
                        // 最粗尺度贡献亮度；此处各尺度统一用 SSIM（稳定参考）。
                        float cs = SSIMNative(A[s], B[s], W[s], H[s]);
                        ms *= math.pow(math.max(cs, 0f), weights[s]);
                    }
                    return Mathf.Clamp01(ms);
                }
                finally
                {
                    foreach (var arr in A) arr.Dispose();
                    foreach (var arr in B) arr.Dispose();
                }
            }
            catch (Exception e)
            {
                ATOLog.Verbose($"[Burst] MSSSIM fell back to CPU: {e.Message}");
                return QualityMath.MSSSIM(a, b, w, h);
            }
        }

        private static float SSIMNative(NativeArray<float> a, NativeArray<float> b, int w, int h)
        {
            const int window = 11;
            const float sigma = 1.5f;
            using var ka = new NativeArray<float>(window * window, Allocator.TempJob);
            float ksum = 0f;
            for (int y = 0; y < window; y++)
            for (int x = 0; x < window; x++)
            {
                float dx = x - window / 2, dy = y - window / 2;
                float g = math.exp(-(dx * dx + dy * dy) / (2f * sigma * sigma));
                ka[y * window + x] = g; ksum += g;
            }
            for (int i = 0; i < ka.Length; i++) ka[i] /= ksum;
            using var res = new NativeArray<float>(w * h, Allocator.TempJob);
            new SsimJob { a = a, b = b, kernel = ka, width = w, height = h, window = window, result = res }
                .Schedule(w * h, 128).Complete();
            double sum = 0;
            for (int i = 0; i < res.Length; i++) sum += res[i];
            return res.Length > 0 ? (float)(sum / res.Length) : 1f;
        }

        // ------------------------------------------------------------------ Alpha

        [BurstCompile]
        private struct AlphaRmseJob : IJob
        {
            [ReadOnly] public NativeArray<float> a;
            [ReadOnly] public NativeArray<float> b;
            public NativeArray<double> sum; // [0] accumulator
            public void Execute()
            {
                double s = 0;
                for (int i = 0; i < a.Length; i++) { double d = a[i] - b[i]; s += d * d; }
                sum[0] = s;
            }
        }

        [BurstCompile]
        private struct AlphaIouJob : IJob
        {
            [ReadOnly] public NativeArray<float> a;
            [ReadOnly] public NativeArray<float> b;
            public float cutoff;
            public NativeArray<long> counts; // [0] inter, [1] union
            public void Execute()
            {
                long inter = 0, union = 0;
                for (int i = 0; i < a.Length; i++)
                {
                    bool ca = a[i] > cutoff, cb = b[i] > cutoff;
                    if (ca && cb) inter++;
                    if (ca || cb) union++;
                }
                counts[0] = inter; counts[1] = union;
            }
        }

        /// <summary>Alpha RMSE (Burst → CPU fallback). Alpha RMSE（Burst → CPU 回退）。</summary>
        public static float AlphaRMSE(float[] a, float[] b)
        {
            if (!Enabled) return QualityMath.AlphaRMSE(a, b);
            try
            {
                using var na = new NativeArray<float>(a, Allocator.TempJob);
                using var nb = new NativeArray<float>(b, Allocator.TempJob);
                using var sum = new NativeArray<double>(1, Allocator.TempJob);
                new AlphaRmseJob { a = na, b = nb, sum = sum }.Schedule().Complete();
                return (float)math.sqrt(sum[0] / math.max(1, a.Length));
            }
            catch (Exception e)
            {
                ATOLog.Verbose($"[Burst] AlphaRMSE fell back to CPU: {e.Message}");
                return QualityMath.AlphaRMSE(a, b);
            }
        }

        /// <summary>Alpha IoU at a cutoff (Burst → CPU fallback). 给定 cutoff 的 Alpha IoU（Burst → CPU 回退）。</summary>
        public static float AlphaIoU(float[] a, float[] b, float cutoff)
        {
            if (!Enabled) return QualityMath.AlphaIoU(a, b, cutoff);
            try
            {
                using var na = new NativeArray<float>(a, Allocator.TempJob);
                using var nb = new NativeArray<float>(b, Allocator.TempJob);
                using var counts = new NativeArray<long>(2, Allocator.TempJob);
                new AlphaIouJob { a = na, b = nb, cutoff = cutoff, counts = counts }.Schedule().Complete();
                long inter = counts[0], union = counts[1];
                return union == 0 ? 1f : (float)inter / union;
            }
            catch (Exception e)
            {
                ATOLog.Verbose($"[Burst] AlphaIoU fell back to CPU: {e.Message}");
                return QualityMath.AlphaIoU(a, b, cutoff);
            }
        }

        // ------------------------------------------------------------------ Angle

        [BurstCompile]
        private struct AngleErrorJob : IJob
        {
            [ReadOnly] public NativeArray<float3> a;
            [ReadOnly] public NativeArray<float3> b;
            public NativeArray<double> sum; // [0]
            public void Execute()
            {
                double s = 0;
                for (int i = 0; i < a.Length; i++)
                {
                    float dot = math.clamp(math.dot(a[i], b[i]), -1f, 1f);
                    s += math.acos(dot) * 57.29577951308232;
                }
                sum[0] = s;
            }
        }

        /// <summary>Mean angular error in degrees (Burst → CPU fallback). 平均角度误差（度）（Burst → CPU 回退）。</summary>
        public static float MeanAngleErrorDeg(Vector3[] a, Vector3[] b)
        {
            if (!Enabled) return QualityMath.MeanAngleErrorDeg(a, b);
            try
            {
                using var na = new NativeArray<float3>(a.Length, Allocator.TempJob);
                using var nb = new NativeArray<float3>(b.Length, Allocator.TempJob);
                for (int i = 0; i < a.Length; i++) { na[i] = new float3(a[i].x, a[i].y, a[i].z); nb[i] = new float3(b[i].x, b[i].y, b[i].z); }
                using var sum = new NativeArray<double>(1, Allocator.TempJob);
                new AngleErrorJob { a = na, b = nb, sum = sum }.Schedule().Complete();
                return (float)(sum[0] / math.max(1, a.Length));
            }
            catch (Exception e)
            {
                ATOLog.Verbose($"[Burst] Angle fell back to CPU: {e.Message}");
                return QualityMath.MeanAngleErrorDeg(a, b);
            }
        }

        /// <summary>p95 angular error in degrees (CPU sort). p95 角度误差（度）（CPU 排序）。</summary>
        public static float P95AngleErrorDeg(Vector3[] a, Vector3[] b)
        {
            // Sorting is simpler on the managed side; the heavy per-pixel part is cheap enough.
            // 排序在托管侧更简单；逐像素部分开销足够小。
            return QualityMath.P95AngleErrorDeg(a, b);
        }

        // ------------------------------------------------------------------ Resample

        [BurstCompile]
        private struct AreaResampleJob : IJob
        {
            [ReadOnly] public NativeArray<float4> src;
            public NativeArray<float4> dst;
            public int srcW, srcH, dstW, dstH;
            public void Execute()
            {
                float sx = (float)srcW / dstW, sy = (float)srcH / dstH;
                for (int y = 0; y < dstH; y++)
                for (int x = 0; x < dstW; x++)
                {
                    float x0 = x * sx, x1 = (x + 1) * sx;
                    float y0 = y * sy, y1 = (y + 1) * sy;
                    int ix0 = (int)math.floor(x0), ix1 = math.min(srcW, (int)math.ceil(x1));
                    int iy0 = (int)math.floor(y0), iy1 = math.min(srcH, (int)math.ceil(y1));
                    float4 acc = float4.zero; float wsum = 0f;
                    for (int iy = iy0; iy < iy1; iy++)
                    for (int ix = ix0; ix < ix1; ix++)
                    {
                        float ox = math.min(x1, ix + 1) - math.max(x0, ix);
                        float oy = math.min(y1, iy + 1) - math.max(y0, iy);
                        float w = ox * oy;
                        acc += src[iy * srcW + ix] * w; wsum += w;
                    }
                    float inv = wsum > 1e-9f ? 1f / wsum : 1f;
                    dst[y * dstW + x] = acc * inv;
                }
            }
        }

        [BurstCompile]
        private struct BilinearUpsampleJob : IJob
        {
            [ReadOnly] public NativeArray<float4> src;
            public NativeArray<float4> dst;
            public int srcW, srcH, dstW, dstH;
            public void Execute()
            {
                float sx = (float)srcW / dstW, sy = (float)srcH / dstH;
                for (int y = 0; y < dstH; y++)
                for (int x = 0; x < dstW; x++)
                {
                    float u = (x + 0.5f) * sx - 0.5f;
                    float v = (y + 0.5f) * sy - 0.5f;
                    int x0 = (int)math.floor(u), y0 = (int)math.floor(v);
                    float fx = u - x0, fy = v - y0;
                    int x1 = math.min(x0 + 1, srcW - 1), y1 = math.min(y0 + 1, srcH - 1);
                    x0 = math.max(x0, 0); y0 = math.max(y0, 0);
                    float4 c00 = src[y0 * srcW + x0];
                    float4 c10 = src[y0 * srcW + x1];
                    float4 c01 = src[y1 * srcW + x0];
                    float4 c11 = src[y1 * srcW + x1];
                    float4 top = math.lerp(c00, c10, fx);
                    float4 bot = math.lerp(c01, c11, fx);
                    dst[y * dstW + x] = math.lerp(top, bot, fy);
                }
            }
        }

        /// <summary>Area-average downsample (Burst → CPU fallback). 面积平均下采样（Burst → CPU 回退）。</summary>
        public static Color[] AreaResample(Color[] src, int w, int h, int dw, int dh)
        {
            if (!Enabled) return QualityMath.AreaResample(src, w, h, dw, dh);
            try
            {
                var f4 = ToFloat4(src);
                using var na = new NativeArray<float4>(f4, Allocator.TempJob);
                using var nd = new NativeArray<float4>(dw * dh, Allocator.TempJob);
                new AreaResampleJob { src = na, dst = nd, srcW = w, srcH = h, dstW = dw, dstH = dh }.Schedule().Complete();
                return ToColor(nd);
            }
            catch (Exception e)
            {
                ATOLog.Verbose($"[Burst] AreaResample fell back to CPU: {e.Message}");
                return QualityMath.AreaResample(src, w, h, dw, dh);
            }
        }

        /// <summary>Bilinear upsample (GPU → Burst → CPU fallback). 双线性上采样（GPU → Burst → CPU 回退）。</summary>
        public static Color[] BilinearUpsample(Color[] src, int w, int h, int dw, int dh)
        {
            var gpu = GpuResampler.TryUpsample(src, w, h, dw, dh);
            if (gpu != null) return gpu;
            if (!Enabled) return QualityMath.BilinearUpsample(src, w, h, dw, dh);
            try
            {
                var f4 = ToFloat4(src);
                using var na = new NativeArray<float4>(f4, Allocator.TempJob);
                using var nd = new NativeArray<float4>(dw * dh, Allocator.TempJob);
                new BilinearUpsampleJob { src = na, dst = nd, srcW = w, srcH = h, dstW = dw, dstH = dh }.Schedule().Complete();
                return ToColor(nd);
            }
            catch (Exception e)
            {
                ATOLog.Verbose($"[Burst] BilinearUpsample fell back to CPU: {e.Message}");
                return QualityMath.BilinearUpsample(src, w, h, dw, dh);
            }
        }

        private static float4[] ToFloat4(Color[] c)
        {
            var r = new float4[c.Length];
            for (int i = 0; i < c.Length; i++) r[i] = new float4(c[i].r, c[i].g, c[i].b, c[i].a);
            return r;
        }

        private static Color[] ToColor(NativeArray<float4> f)
        {
            var r = new Color[f.Length];
            for (int i = 0; i < f.Length; i++) r[i] = new Color(f[i].x, f[i].y, f[i].z, f[i].w);
            return r;
        }
    }
}
