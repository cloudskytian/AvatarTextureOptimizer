// ATOMetrics.cs — 质量度量核心（Burst CPU 实现）/ Quality metrics core (Burst CPU implementation).
// 说明：实现目标质量算法（CPU 参考实现，GPU 路径见 ATOGpuMetrics 与 ATOCompute.compute）：
//  - 线性空间重采样（透明贴图预乘 alpha 下采样）
//  - MS-SSIM（短边<176px 回退单尺度 SSIM；<11px 忽略该项）+ CIEDE2000 p95
//  - 法线贴图：解码 → 重采样 → 重归一化 → 角度误差 p95
//  - Cutout：clip 后轮廓 IoU；Blend：线性预乘 alpha RMSE；灰度：逐通道线性 RMSE 取最差
// Note: reference CPU implementation of the target quality algorithm (GPU path in ATOGpuMetrics + ATOCompute.compute):
// linear-space resampling (premultiplied alpha for transparent), MS-SSIM (single-scale fallback below 176px short
// side; skipped below 11px) + CIEDE2000 p95, normal maps decoded/resampled/renormalized with p95 angular error,
// Cutout silhouette IoU, Blend premultiplied-alpha RMSE, grayscale per-channel worst RMSE.

using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer
{
    /// <summary>质量阈值参数（由质量挡位提供）。/ Quality thresholds (from the quality tier).</summary>
    public struct ATOQualityParams
    {
        public float msSsim;            // MS-SSIM 阈值 / MS-SSIM threshold
        public float deltaEP95;         // ΔE p95 阈值 / ΔE p95 threshold
        public float normalAngleP95;    // 法线角度 p95（度）/ normal angle p95 (degrees)
        public float alphaIoU;          // Cutout IoU / cutout IoU
        public float alphaLinearRmse;   // Blend alpha RMSE / blend alpha RMSE
        public float grayLinearRmse;    // 灰度逐通道 RMSE / grayscale per-channel RMSE
        public bool lossless;           // 近无损（跳过缩放、原样拷贝）/ near-lossless (skip scaling, plain copy)
    }

    /// <summary>单次评估结果。/ Single evaluation result.</summary>
    public struct ATOEvalResult
    {
        public float msSsim;
        public float deltaEP95;
        public float normalAngleP95;
        public float normalAngleMean;
        public float alphaIoU;
        public float alphaRmse;
        public float grayRmse;
        public bool pass;
        public string failReason; // 未通过的原因 / failure reason
    }

    /// <summary>质量评估输入（已解码的线性贴图数据）。/ Quality evaluation input (decoded linear texture data).</summary>
    public struct ATOEvalInput
    {
        public NativeArray<float4> source;   // 原图裁剪（线性、按需预乘）/ original crop (linear, premultiplied if required)
        public int srcW;
        public int srcH;
        public int dstW;                     // 候选尺寸 / candidate size
        public int dstH;
        public bool premultiplied;           // 是否预乘 / premultiplied
        public bool normalMap;               // 法线贴图（需解码与角度评估）/ normal map (decode & angular eval)
        public bool grayEval;                // 灰度评估 / grayscale eval
        public ATOAlphaUsage alphaFlags;     // 透明度模式位标志 / alpha mode flags
        public NativeArray<float> cutoffs;   // Cutout 阈值采样 / cutout threshold samples
        public ATOQualityParams thresholds;  // 阈值 / thresholds
    }

    /// <summary>Burst 质量度量实现。/ Burst quality metrics implementation.</summary>
    [BurstCompile]
    internal static class ATOMetrics
    {
        private const float LumaR = 0.2126f;
        private const float LumaG = 0.7152f;
        private const float LumaB = 0.0722f;

        // MS-SSIM 权重（Wang et al. 2003）/ MS-SSIM weights
        private static readonly float4 MsSsimWeights = new float4(0.0448f, 0.2856f, 0.3001f, 0.2363f);
        private const float W5 = 0.1333f;
        private const float C1 = 1e-4f; // (0.01*L)^2, L=1
        private const float C2 = 9e-4f; // (0.03*L)^2, L=1

        // 高斯核（11-tap，σ=1.5，归一化）/ Gaussian kernel (11 taps, σ=1.5, normalized)
        private static readonly float[] GaussKernel =
        {
            0.000003f, 0.000229f, 0.005977f, 0.060598f,
            0.241732f, 0.382925f, 0.241732f, 0.060598f,
            0.005977f, 0.000229f, 0.000003f,
        };

        /// <summary>评估入口（CPU）。/ Evaluation entry (CPU).</summary>
        public static ATOEvalResult Evaluate(ref ATOEvalInput input, Allocator alloc)
        {
            var result = new ATOEvalResult { pass = true };

            // 1. 缩放到候选尺寸（迭代 2x 盒滤波 + 末级双线性）/ resize to candidate size (iterative 2x box + final bilinear)
            var scaled = Resize(input.source, input.srcW, input.srcH, input.dstW, input.dstH, alloc);

            // 2. 双线性上采样回原尺寸比较 / bilinear upsample back to original size for comparison
            var upsampled = Resize(scaled, input.dstW, input.dstH, input.srcW, input.srcH, alloc);

            try
            {
                // 3. 按角色评估 / per-role evaluation
                if (input.normalMap)
                {
                    // 法线：解码 → 角度误差 / normal: decode → angular error
                    EvalNormal(input.source, upsampled, input.srcW, input.srcH, alloc, out result.normalAngleP95, out result.normalAngleMean);
                    if (input.thresholds.lossless)
                    {
                        if (result.normalAngleP95 > 1e-4f) { result.pass = false; result.failReason = "normal angle"; }
                    }
                    else if (result.normalAngleP95 > input.thresholds.normalAngleP95)
                    {
                        result.pass = false;
                        result.failReason = "normal angle";
                    }
                }
                else if (input.grayEval)
                {
                    // 灰度：逐通道线性 RMSE 取最差 / grayscale: per-channel linear RMSE, worst channel
                    result.grayRmse = GrayRmse(input.source, upsampled, input.srcW, input.srcH);
                    var limit = input.thresholds.lossless ? 0f : input.thresholds.grayLinearRmse;
                    if (result.grayRmse > limit + 1e-6f)
                    {
                        result.pass = false;
                        result.failReason = "gray rmse";
                    }
                }
                else
                {
                    // 颜色类：MS-SSIM + ΔE / color: MS-SSIM + ΔE
                    if (input.srcW >= 11 && input.srcH >= 11)
                    {
                        var multi = input.srcW >= 176 && input.srcH >= 176;
                        result.msSsim = multi
                            ? MsSsim(input.source, upsampled, input.srcW, input.srcH, alloc)
                            : Ssim(input.source, upsampled, input.srcW, input.srcH, alloc);
                        if (input.thresholds.lossless)
                        {
                            if (result.msSsim < 1f - 1e-5f) { result.pass = false; result.failReason = "ms-ssim"; }
                        }
                        else if (result.msSsim < input.thresholds.msSsim)
                        {
                            result.pass = false;
                            result.failReason = "ms-ssim";
                        }
                    }
                    result.deltaEP95 = DeltaE2000P95(input.source, upsampled, input.srcW, input.srcH, alloc);
                    if (input.thresholds.lossless)
                    {
                        if (result.deltaEP95 > 1e-4f) { result.pass = false; result.failReason = "ΔE"; }
                    }
                    else if (result.deltaEP95 > input.thresholds.deltaEP95)
                    {
                        result.pass = false;
                        result.failReason = "ΔE";
                    }
                }

                // 4. 透明度评估 / alpha evaluation
                if ((input.alphaFlags & ATOAlphaUsage.Cutout) != 0 && input.cutoffs.Length > 0)
                {
                    result.alphaIoU = 1f;
                    for (int i = 0; i < input.cutoffs.Length; i++)
                    {
                        var iou = AlphaIoU(input.source, upsampled, input.srcW, input.srcH, input.cutoffs[i]);
                        if (iou < result.alphaIoU) result.alphaIoU = iou;
                    }
                    if (input.thresholds.lossless)
                    {
                        if (result.alphaIoU < 1f - 1e-5f) { result.pass = false; result.failReason = "alpha IoU"; }
                    }
                    else if (result.alphaIoU < input.thresholds.alphaIoU)
                    {
                        result.pass = false;
                        result.failReason = "alpha IoU";
                    }
                }
                if ((input.alphaFlags & ATOAlphaUsage.Blend) != 0)
                {
                    result.alphaRmse = AlphaLinearRmse(input.source, upsampled, input.srcW, input.srcH, input.premultiplied);
                    var limit = input.thresholds.lossless ? 0f : input.thresholds.alphaLinearRmse;
                    if (result.alphaRmse > limit + 1e-6f)
                    {
                        result.pass = false;
                        result.failReason = "alpha rmse";
                    }
                }
            }
            finally
            {
                if (scaled.IsCreated) scaled.Dispose();
                if (upsampled.IsCreated) upsampled.Dispose();
            }
            return result;
        }

        // ---------------- 重采样 / resampling ----------------

        /// <summary>迭代 2x 盒式降采样 + 末级双线性（线性空间、预乘安全）。/ Iterative 2x box downsample + final bilinear (linear space, premultiply-safe).</summary>
        public static NativeArray<float4> Resize(NativeArray<float4> src, int sw, int sh, int dw, int dh, Allocator alloc)
        {
            var cur = new NativeArray<float4>(src.Length, alloc);
            cur.CopyFrom(src);
            var cw = sw;
            var ch = sh;

            // 当目标小于当前一半以上时反复 2x 盒式降采样 / iterate 2x box while target is less than half
            while (cw / 2 >= dw && ch / 2 >= dh && cw > 2 && ch > 2)
            {
                var hw = cw / 2;
                var hh = ch / 2;
                var half = new NativeArray<float4>(hw * hh, alloc);
                var job = new BoxDownsample2xJob { src = cur, sw = cw, sh = ch, dst = half };
                job.Schedule(hw * hh, 256).Complete();
                cur.Dispose();
                cur = half;
                cw = hw;
                ch = hh;
            }

            // 末级双线性 / final bilinear
            if (cw != dw || ch != dh)
            {
                var dst = new NativeArray<float4>(dw * dh, alloc);
                var job = new BilinearJob { src = cur, sw = cw, sh = ch, dw = dw, dh = dh, dst = dst };
                job.Schedule(dw * dh, 256).Complete();
                cur.Dispose();
                return dst;
            }
            return cur;
        }

        [BurstCompile]
        private struct BoxDownsample2xJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float4> src;
            public int sw;
            public int sh;
            [WriteOnly] public NativeArray<float4> dst;

            public void Execute(int index)
            {
                var x = index % (sw / 2);
                var y = index / (sw / 2);
                var x0 = x * 2;
                var y0 = y * 2;
                var sum = src[y0 * sw + x0] + src[y0 * sw + x0 + 1] + src[(y0 + 1) * sw + x0] + src[(y0 + 1) * sw + x0 + 1];
                dst[index] = sum * 0.25f;
            }
        }

        [BurstCompile]
        private struct BilinearJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float4> src;
            public int sw;
            public int sh;
            public int dw;
            public int dh;
            [WriteOnly] public NativeArray<float4> dst;

            public void Execute(int index)
            {
                var x = index % dw;
                var y = index / dw;
                // 像素中心映射 / pixel-center mapping
                var fx = (x + 0.5f) * sw / dw - 0.5f;
                var fy = (y + 0.5f) * sh / dh - 0.5f;
                var x0 = math.clamp((int)math.floor(fx), 0, sw - 1);
                var y0 = math.clamp((int)math.floor(fy), 0, sh - 1);
                var x1 = math.clamp(x0 + 1, 0, sw - 1);
                var y1 = math.clamp(y0 + 1, 0, sh - 1);
                var tx = math.clamp(fx - x0, 0f, 1f);
                var ty = math.clamp(fy - y0, 0f, 1f);
                var a = math.lerp(src[y0 * sw + x0], src[y0 * sw + x1], tx);
                var b = math.lerp(src[y1 * sw + x0], src[y1 * sw + x1], tx);
                dst[index] = math.lerp(a, b, ty);
            }
        }

        // ---------------- SSIM / MS-SSIM ----------------

        private static void ToLuma(NativeArray<float4> src, int w, int h, NativeArray<float> luma, Allocator alloc)
        {
            var job = new ToLumaJob { src = src, luma = luma };
            job.Schedule(w * h, 512).Complete();
        }

        [BurstCompile]
        private struct ToLumaJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float4> src;
            [WriteOnly] public NativeArray<float> luma;

            public void Execute(int i)
            {
                var c = src[i];
                luma[i] = math.dot(c.xyz, new float3(LumaR, LumaG, LumaB));
            }
        }

        /// <summary>单尺度 SSIM（均值）。/ Single-scale SSIM (mean).</summary>
        public static float Ssim(NativeArray<float4> a, NativeArray<float4> b, int w, int h, Allocator alloc)
        {
            var la = new NativeArray<float>(w * h, alloc);
            var lb = new NativeArray<float>(w * h, alloc);
            var tmp = new NativeArray<float>(w * h, alloc);
            try
            {
                ToLuma(a, w, h, la, alloc);
                ToLuma(b, w, h, lb, alloc);
                return SsimOnLuma(la, lb, w, h, tmp, alloc);
            }
            finally
            {
                la.Dispose();
                lb.Dispose();
                tmp.Dispose();
            }
        }

        /// <summary>MS-SSIM（5 级金字塔，不足 5 级时重归一化权重）。/ MS-SSIM (5-level pyramid; weights renormalized when fewer levels).</summary>
        public static float MsSsim(NativeArray<float4> a, NativeArray<float4> b, int w, int h, Allocator alloc)
        {
            var levels = new NativeArray<float4>[5];
            var sum = 0f;
            var acc = 0f;
            int level = 0;
            var cw = w;
            var ch = h;
            var la = new NativeArray<float>(w * h, alloc);
            var lb = new NativeArray<float>(w * h, alloc);
            try
            {
                ToLuma(a, w, h, la, alloc);
                ToLuma(b, w, h, lb, alloc);
                while (level < 5)
                {
                    var tmp = new NativeArray<float>(cw * ch, alloc);
                    var s = SsimOnLuma(la, lb, cw, ch, tmp, alloc);
                    tmp.Dispose();
                    var weight = level < 4 ? MsSsimWeights[level] : W5;
                    acc += s * weight;
                    sum += weight;
                    level++;
                    if (level >= 5 || cw <= 16 || ch <= 16) break;
                    // 下一级：2x 盒式降采样（对 luma）/ next level: 2x box downsample (on luma)
                    var hw = cw / 2;
                    var hh = ch / 2;
                    var nla = new NativeArray<float>(hw * hh, alloc);
                    var nlb = new NativeArray<float>(hw * hh, alloc);
                    DownsampleLuma2x(la, cw, ch, nla, alloc);
                    DownsampleLuma2x(lb, cw, ch, nlb, alloc);
                    la.Dispose();
                    lb.Dispose();
                    la = nla;
                    lb = nlb;
                    cw = hw;
                    ch = hh;
                }
                return sum > 0f ? acc / sum : 1f;
            }
            finally
            {
                la.Dispose();
                lb.Dispose();
            }
        }

        /// <summary>在亮度图上的 SSIM（高斯 11-tap 局部统计）。/ SSIM on luma images (Gaussian 11-tap local statistics).</summary>
        private static float SsimOnLuma(NativeArray<float> la, NativeArray<float> lb, int w, int h, NativeArray<float> tmp, Allocator alloc)
        {
            var n = w * h;
            var ma = new NativeArray<float>(n, alloc);
            var mb = new NativeArray<float>(n, alloc);
            var va = new NativeArray<float>(n, alloc);
            var vb = new NativeArray<float>(n, alloc);
            var cov = new NativeArray<float>(n, alloc);
            var prod = new NativeArray<float>(n, alloc);
            try
            {
                GaussianBlur(la, w, h, ma, tmp, alloc);
                GaussianBlur(lb, w, h, mb, tmp, alloc);
                for (int i = 0; i < n; i++)
                {
                    var da = la[i] - ma[i];
                    var db = lb[i] - mb[i];
                    va[i] = da * da;
                    vb[i] = db * db;
                    prod[i] = da * db;
                }
                GaussianBlur(va, w, h, va, tmp, alloc); // 就地（先读后写冲突？使用独立缓冲）— 用 tmp 中转避免就地别名
                GaussianBlur(vb, w, h, vb, tmp, alloc);
                GaussianBlur(prod, w, h, cov, tmp, alloc);
                // 防止就地别名：va/vb 的模糊在写回前已读完 → 用独立输出缓冲
                double total = 0.0;
                for (int i = 0; i < n; i++)
                {
                    var l = (2f * ma[i] * mb[i] + C1) / (ma[i] * ma[i] + mb[i] * mb[i] + C1);
                    var cs = (2f * cov[i] + C2) / (va[i] + vb[i] + C2);
                    total += l * cs;
                }
                return (float)(total / n);
            }
            finally
            {
                ma.Dispose();
                mb.Dispose();
                va.Dispose();
                vb.Dispose();
                cov.Dispose();
                prod.Dispose();
            }
        }

        /// <summary>可分离高斯模糊（11-tap，就地写出用独立输出）。/ Separable Gaussian blur (11 taps; separate output to avoid in-place aliasing).</summary>
        private static void GaussianBlur(NativeArray<float> src, int w, int h, NativeArray<float> dst, NativeArray<float> tmp, Allocator alloc)
        {
            var n = w * h;
            var horiz = tmp;
            var jobH = new GaussianJob { src = src, dst = horiz, width = w, height = h, axis = 0 };
            jobH.Schedule(n, 512).Complete();
            var jobV = new GaussianJob { src = horiz, dst = dst, width = w, height = h, axis = 1 };
            jobV.Schedule(n, 512).Complete();
        }

        [BurstCompile]
        private struct GaussianJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float> src;
            [WriteOnly] public NativeArray<float> dst;
            public int width;
            public int height;
            public int axis; // 0=H, 1=V

            public void Execute(int index)
            {
                var x = axis == 0 ? index % width : index / width;
                var y = axis == 0 ? index / width : index % height;
                float acc = 0f;
                for (int k = -5; k <= 5; k++)
                {
                    var xx = axis == 0 ? math.clamp(x + k, 0, width - 1) : x;
                    var yy = axis == 1 ? math.clamp(y + k, 0, height - 1) : y;
                    acc += src[yy * width + xx] * GaussKernel[k + 5];
                }
                dst[index] = acc;
            }
        }

        private static void DownsampleLuma2x(NativeArray<float> src, int w, int h, NativeArray<float> dst, Allocator alloc)
        {
            var job = new DownLuma2xJob { src = src, sw = w, dst = dst };
            job.Schedule(dst.Length, 512).Complete();
        }

        [BurstCompile]
        private struct DownLuma2xJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float> src;
            public int sw;
            [WriteOnly] public NativeArray<float> dst;

            public void Execute(int i)
            {
                var hw = sw / 2;
                var x = i % hw;
                var y = i / hw;
                var x0 = x * 2;
                var y0 = y * 2;
                dst[i] = (src[y0 * sw + x0] + src[y0 * sw + x0 + 1] + src[(y0 + 1) * sw + x0] + src[(y0 + 1) * sw + x0 + 1]) * 0.25f;
            }
        }

        // ---------------- CIEDE2000 ----------------

        /// <summary>CIEDE2000 色差 p95。/ CIEDE2000 p95.</summary>
        public static float DeltaE2000P95(NativeArray<float4> a, NativeArray<float4> b, int w, int h, Allocator alloc)
        {
            var n = w * h;
            var de = new NativeArray<float>(n, alloc);
            try
            {
                var job = new DeltaEJob { a = a, b = b, dst = de };
                job.Schedule(n, 512).Complete();
                return Percentile95(de);
            }
            finally
            {
                de.Dispose();
            }
        }

        [BurstCompile]
        private struct DeltaEJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float4> a;
            [ReadOnly] public NativeArray<float4> b;
            [WriteOnly] public NativeArray<float> dst;

            public void Execute(int i)
            {
                dst[i] = Ciede2000(LinearToLab(a[i].xyz), LinearToLab(b[i].xyz));
            }
        }

        /// <summary>线性 RGB → CIE Lab（D65）。/ Linear RGB → CIE Lab (D65).</summary>
        public static float3 LinearToLab(float3 lin)
        {
            // 线性 RGB → XYZ / linear RGB → XYZ
            var x = 0.4124564f * lin.x + 0.3575761f * lin.y + 0.1804375f * lin.z;
            var y = 0.2126729f * lin.x + 0.7151522f * lin.y + 0.0721750f * lin.z;
            var z = 0.0193339f * lin.x + 0.1191920f * lin.y + 0.9503041f * lin.z;
            // 参考白点归一 / normalize by D65 white
            x /= 0.95047f;
            y /= 1.00000f;
            z /= 1.08883f;
            var fx = LabF(x);
            var fy = LabF(y);
            var fz = LabF(z);
            return new float3(116f * fy - 16f, 500f * (fx - fy), 200f * (fy - fz));
        }

        private static float LabF(float t) => t > 0.008856452f ? math.pow(t, 1f / 3f) : (7.787037f * t + 16f / 116f);

        /// <summary>CIEDE2000 色差公式（Sharma et al. 2005）。/ CIEDE2000 formula (Sharma et al. 2005).</summary>
        public static float Ciede2000(float3 lab1, float3 lab2)
        {
            var L1 = lab1.x; var a1 = lab1.y; var b1 = lab1.z;
            var L2 = lab2.x; var a2 = lab2.y; var b2 = lab2.z;

            var C1 = math.sqrt(a1 * a1 + b1 * b1);
            var C2 = math.sqrt(a2 * a2 + b2 * b2);
            var Cb = (C1 + C2) * 0.5f;
            var Cb7 = math.pow(Cb, 7f);
            var G = 0.5f * (1f - math.sqrt(Cb7 / (Cb7 + math.pow(25f, 7f))));
            var a1p = (1f + G) * a1;
            var a2p = (1f + G) * a2;
            var C1p = math.sqrt(a1p * a1p + b1 * b1);
            var C2p = math.sqrt(a2p * a2p + b2 * b2);
            var h1p = HueDeg(a1p, b1);
            var h2p = HueDeg(a2p, b2);

            var dL = L2 - L1;
            var dC = C2p - C1p;
            var dh = h2p - h1p;
            if (dh > 180f) dh -= 360f;
            else if (dh < -180f) dh += 360f;
            var dH = 2f * math.sqrt(C1p * C2p) * math.sin(math.radians(dh) * 0.5f);

            var Lb = (L1 + L2) * 0.5f;
            var Cpb = (C1p + C2p) * 0.5f;
            float hb;
            var hSum = h1p + h2p;
            if (math.abs(h1p - h2p) <= 180f) hb = hSum * 0.5f;
            else if (hSum < 360f) hb = (hSum + 360f) * 0.5f;
            else hb = (hSum - 360f) * 0.5f;

            var T = 1f - 0.17f * math.cos(math.radians(hb - 30f)) + 0.24f * math.cos(math.radians(2f * hb))
                    + 0.32f * math.cos(math.radians(3f * hb + 6f)) - 0.20f * math.cos(math.radians(4f * hb - 63f));

            var dTheta = 30f * math.exp(-math.pow((hb - 275f) / 25f, 2f));
            var Cpb7 = math.pow(Cpb, 7f);
            var Rc = 2f * math.sqrt(Cpb7 / (Cpb7 + math.pow(25f, 7f)));
            var Lb50 = (Lb - 50f) * (Lb - 50f);
            var Sl = 1f + 0.015f * Lb50 / math.sqrt(20f + Lb50);
            var Sc = 1f + 0.045f * Cpb;
            var Sh = 1f + 0.015f * Cpb * T;
            var Rt = -math.sin(math.radians(2f * dTheta)) * Rc;

            var dLk = dL / Sl;
            var dCk = dC / Sc;
            var dHk = dH / Sh;
            return math.sqrt(dLk * dLk + dCk * dCk + dHk * dHk + Rt * dCk * dHk);
        }

        private static float HueDeg(float a, float b)
        {
            if (math.abs(a) < 1e-8f && math.abs(b) < 1e-8f) return 0f;
            var h = math.degrees(math.atan2(b, a));
            return h < 0f ? h + 360f : h;
        }

        // ---------------- 法线 / normal ----------------

        private static void EvalNormal(NativeArray<float4> source, NativeArray<float4> upsampled, int w, int h,
            Allocator alloc, out float p95, out float mean)
        {
            var n = w * h;
            var angles = new NativeArray<float>(n, alloc);
            try
            {
                var job = new NormalAngleJob { src = source, up = upsampled, dst = angles };
                job.Schedule(n, 512).Complete();
                mean = Mean(angles);
                p95 = Percentile95(angles);
            }
            finally
            {
                angles.Dispose();
            }
        }

        [BurstCompile]
        private struct NormalAngleJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float4> src; // 原图（解码后单位法线存于 xyz）/ original (decoded unit normal in xyz)
            [ReadOnly] public NativeArray<float4> up;  // 上采样结果（解码后法线）/ upsampled (decoded normal)
            [WriteOnly] public NativeArray<float> dst;

            public void Execute(int i)
            {
                var n1 = math.normalize(src[i].xyz);
                var n2 = math.normalize(up[i].xyz);
                var dot = math.clamp(math.dot(n1, n2), -1f, 1f);
                dst[i] = math.degrees(math.acos(dot));
            }
        }

        // ---------------- 灰度 RMSE / grayscale RMSE ----------------

        private static float GrayRmse(NativeArray<float4> src, NativeArray<float4> up, int w, int h)
        {
            var n = w * h;
            double sumR = 0, sumG = 0, sumB = 0, sumA = 0;
            for (int i = 0; i < n; i++)
            {
                var d = src[i] - up[i];
                sumR += (double)d.x * d.x;
                sumG += (double)d.y * d.y;
                sumB += (double)d.z * d.z;
                sumA += (double)d.w * d.w;
            }
            var r = (float)math.sqrt(sumR / n);
            var g = (float)math.sqrt(sumG / n);
            var b = (float)math.sqrt(sumB / n);
            var a = (float)math.sqrt(sumA / n);
            return math.max(math.max(r, g), math.max(b, a));
        }

        // ---------------- Alpha / IoU ----------------

        private static float AlphaIoU(NativeArray<float4> src, NativeArray<float4> up, int w, int h, float cutoff)
        {
            var n = w * h;
            long inter = 0, union = 0;
            for (int i = 0; i < n; i++)
            {
                var a = src[i].w >= cutoff ? 1 : 0;
                var b = up[i].w >= cutoff ? 1 : 0;
                inter += (a & b);
                union += (a | b);
            }
            return union == 0 ? 1f : (float)inter / union;
        }

        private static float AlphaLinearRmse(NativeArray<float4> src, NativeArray<float4> up, int w, int h, bool premultiplied)
        {
            var n = w * h;
            double sum = 0;
            for (int i = 0; i < n; i++)
            {
                float d;
                if (premultiplied)
                {
                    // 预乘空间比较 RGB×A 与 A / compare RGB×A and A in premultiplied space
                    var sa = src[i];
                    var sb = up[i];
                    var da = sa.w - sb.w;
                    var dr = sa.x * sa.w - sb.x * sb.w;
                    var dg = sa.y * sa.w - sb.y * sb.w;
                    var db = sa.z * sa.w - sb.z * sb.w;
                    d = dr * dr + dg * dg + db * db + da * da;
                }
                else
                {
                    var da = src[i].w - up[i].w;
                    d = da * da;
                }
                sum += d;
            }
            return (float)math.sqrt(sum / n);
        }

        // ---------------- 统计 / statistics ----------------

        /// <summary>均值。/ Mean.</summary>
        public static float Mean(NativeArray<float> data)
        {
            double sum = 0;
            for (int i = 0; i < data.Length; i++) sum += data[i];
            return (float)(sum / data.Length);
        }

        /// <summary>第 95 百分位（排序法）。/ 95th percentile (by sorting).</summary>
        public static float Percentile95(NativeArray<float> data)
        {
            if (data.Length == 0) return 0f;
            var sorted = new NativeArray<float>(data, Allocator.Temp);
            try
            {
                NativeSortExtension.Sort(sorted);
                var idx = (int)math.ceil(sorted.Length * 0.95f) - 1;
                if (idx < 0) idx = 0;
                if (idx >= sorted.Length) idx = sorted.Length - 1;
                return sorted[idx];
            }
            finally
            {
                sorted.Dispose();
            }
        }

        // ---------------- 辅助 / helpers ----------------

        /// <summary>sRGB 字节 → 线性 float4（0~1）。/ sRGB bytes → linear float4 (0..1).</summary>
        [BurstCompile]
        public static float4 SrgbByteToLinear(int rgba)
        {
            float4 c = new float4(
                (rgba & 0xFF) / 255f,
                ((rgba >> 8) & 0xFF) / 255f,
                ((rgba >> 16) & 0xFF) / 255f,
                ((rgba >> 24) & 0xFF) / 255f);
            c.xyz = SrgbToLinear(c.xyz);
            return c;
        }

        /// <summary>sRGB → 线性。/ sRGB → linear.</summary>
        public static float3 SrgbToLinear(float3 c)
        {
            return new float3(SrgbToLinear(c.x), SrgbToLinear(c.y), SrgbToLinear(c.z));
        }

        public static float SrgbToLinear(float c) =>
            c <= 0.04045f ? c / 12.92f : math.pow((c + 0.055f) / 1.055f, 2.4f);

        /// <summary>线性 → sRGB 字节。/ Linear → sRGB byte.</summary>
        public static float LinearToSrgb(float c) =>
            c <= 0.0031308f ? c * 12.92f : 1.055f * math.pow(c, 1f / 2.4f) - 0.055f;

        /// <summary>检测纯色（全部像素相等）。/ Detect solid color (all pixels equal).</summary>
        public static bool IsSolid(NativeArray<float4> src)
        {
            if (src.Length == 0) return true;
            var first = src[0];
            for (int i = 1; i < src.Length; i++)
                if (math.any(src[i] != first)) return false;
            return true;
        }
    }
}
