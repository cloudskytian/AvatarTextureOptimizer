// ============================================================================
// AvatarTextureOptimizer (net.fosa.avatar-texture-optimizer)
// Textures/QualityMetrics.cs — 目标质量算法 / Target quality metrics
//
// 需求:
//  - 线性空间重采样；透明贴图预乘 alpha 下采样。
//  - 不透明: MS-SSIM + ΔE(CIEDE2000)；<176px 岛回退单尺度 SSIM；<11px 忽略质量参数。
//  - 透明: 同上 + Cutout 轮廓 IoU(clip 后) / Blend alpha 线性 RMSE；多材质取最严苛。
//  - 法线: 正确解码→重采样→重归一化编码→角度误差 + p95。
//  - 灰度: 仅被使用通道、线性空间 RMSE，逐通道取最差。
// 实现: 高斯卷积与降采样用 Burst 作业；CIEDE2000/法线/灰度用托管并行（数学重、GPU 无优势）。
// ============================================================================
using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor
{
    /// <summary>
    /// 质量评估参数 / Quality evaluation parameters.
    /// </summary>
    public struct MetricParams
    {
        public float msSsim;          // 目标 MS-SSIM / target MS-SSIM
        public float maxDeltaE;       // 最大 CIEDE2000 / max CIEDE2000
        public float minCutoutIoU;    // 最小 Cutout 轮廓 IoU / min cutout IoU
        public float maxBlendRmse;    // 最大 Blend alpha RMSE / max blend alpha RMSE
        public float maxNormalAngle;  // 最大法线角度误差(度, p95) / max normal angle (deg, p95)
        public float maxGrayRmse;     // 最大灰度 RMSE / max grayscale RMSE
    }

    /// <summary>
    /// 单次评估结果 / Single evaluation report.
    /// </summary>
    public struct MetricReport
    {
        public bool allPass;
        public float ssim;
        public float deltaE;
        public float alphaScore;      // Cutout: IoU（越大越好）；Blend: RMSE（越小越好）
        public float normalAngleP95;
        public float grayRmse;
    }

    /// <summary>
    /// 色彩空间与颜色度量工具 / Color space and color metric utilities.
    /// </summary>
    public static class ColorMetrics
    {
        public static float SrgbToLinear(float c)
        {
            return c <= 0.04045f ? c / 12.92f : Mathf.Pow((c + 0.055f) / 1.055f, 2.4f);
        }

        public static float LinearToSrgb(float c)
        {
            return c <= 0.0031308f ? c * 12.92f : 1.055f * Mathf.Pow(c, 1f / 2.4f) - 0.055f;
        }

        /// <summary>sRGB→线性（RGBA float 数组就地转换） / sRGB→linear in place</summary>
        public static void SrgbToLinearInPlace(float[] rgba)
        {
            for (int i = 0; i < rgba.Length; i += 4)
            {
                rgba[i] = SrgbToLinear(rgba[i]);
                rgba[i + 1] = SrgbToLinear(rgba[i + 1]);
                rgba[i + 2] = SrgbToLinear(rgba[i + 2]);
            }
        }

        /// <summary>预乘 alpha（就地） / Premultiply alpha in place</summary>
        public static void PremultiplyInPlace(float[] rgba)
        {
            for (int i = 0; i < rgba.Length; i += 4)
            {
                float a = rgba[i + 3];
                rgba[i] *= a;
                rgba[i + 1] *= a;
                rgba[i + 2] *= a;
            }
        }

        /// <summary>线性 RGB → CIE Lab (D65) / Linear RGB → CIE Lab (D65)</summary>
        public static Vector3 RgbToLab(float r, float g, float b)
        {
            // 线性 RGB → XYZ (D65) / linear RGB → XYZ (D65)
            float x = r * 0.4124564f + g * 0.3575761f + b * 0.1804375f;
            float y = r * 0.2126729f + g * 0.7151522f + b * 0.0721750f;
            float z = r * 0.0193339f + g * 0.1191920f + b * 0.9503041f;

            const float xn = 0.95047f, yn = 1f, zn = 1.08883f;
            x /= xn; y /= yn; z /= zn;

            float F(float t) => t > 0.008856f ? Mathf.Pow(t, 1f / 3f) : (7.787f * t + 16f / 116f);
            float fx = F(x), fy = F(y), fz = F(z);

            return new Vector3(116f * fy - 16f, 500f * (fx - fy), 200f * (fy - fz));
        }

        /// <summary>CIEDE2000 色差 / CIEDE2000 color difference</summary>
        public static float DeltaE2000(Vector3 lab1, Vector3 lab2)
        {
            float L1 = lab1.x, a1 = lab1.y, b1 = lab1.z;
            float L2 = lab2.x, a2 = lab2.y, b2 = lab2.z;

            float C1 = Mathf.Sqrt(a1 * a1 + b1 * b1);
            float C2 = Mathf.Sqrt(a2 * a2 + b2 * b2);
            float Cbar = (C1 + C2) * 0.5f;

            float Cbar7 = Cbar * Cbar * Cbar * Cbar * Cbar * Cbar * Cbar;
            float G = 0.5f * (1f - Mathf.Sqrt(Cbar7 / (Cbar7 + 6103515625f))); // 25^7
            float a1p = (1f + G) * a1;
            float a2p = (1f + G) * a2;
            float C1p = Mathf.Sqrt(a1p * a1p + b1 * b1);
            float C2p = Mathf.Sqrt(a2p * a2p + b2 * b2);

            float h1p = Mathf.Atan2(b1, a1p) * Mathf.Rad2Deg; if (h1p < 0) h1p += 360f;
            float h2p = Mathf.Atan2(b2, a2p) * Mathf.Rad2Deg; if (h2p < 0) h2p += 360f;

            float dLp = L2 - L1;
            float dCp = C2p - C1p;

            float dhp;
            if (C1p * C2p == 0f) dhp = 0f;
            else if (Mathf.Abs(h2p - h1p) <= 180f) dhp = h2p - h1p;
            else if (h2p - h1p > 180f) dhp = h2p - h1p - 360f;
            else dhp = h2p - h1p + 360f;

            float dHp = 2f * Mathf.Sqrt(C1p * C2p) * Mathf.Sin(dhp * 0.5f * Mathf.Deg2Rad);

            float Lbar = (L1 + L2) * 0.5f;
            float Cbarp = (C1p + C2p) * 0.5f;

            float hbarp;
            if (C1p * C2p == 0f) hbarp = h1p + h2p;
            else if (Mathf.Abs(h1p - h2p) <= 180f) hbarp = (h1p + h2p) * 0.5f;
            else if (h1p + h2p < 360f) hbarp = (h1p + h2p + 360f) * 0.5f;
            else hbarp = (h1p + h2p - 360f) * 0.5f;

            float T = 1f
                - 0.17f * Mathf.Cos((hbarp - 30f) * Mathf.Deg2Rad)
                + 0.24f * Mathf.Cos(2f * hbarp * Mathf.Deg2Rad)
                + 0.32f * Mathf.Cos((3f * hbarp + 6f) * Mathf.Deg2Rad)
                - 0.20f * Mathf.Cos((4f * hbarp - 63f) * Mathf.Deg2Rad);

            float dtheta = 30f * Mathf.Exp(-Mathf.Pow((hbarp - 275f) / 25f, 2f));
            float Cbarp7 = Cbarp * Cbarp * Cbarp * Cbarp * Cbarp * Cbarp * Cbarp;
            float Rc = 2f * Mathf.Sqrt(Cbarp7 / (Cbarp7 + 6103515625f));
            float Sl = 1f + 0.015f * (Lbar - 50f) * (Lbar - 50f) / Mathf.Sqrt(20f + (Lbar - 50f) * (Lbar - 50f));
            float Sc = 1f + 0.045f * Cbarp;
            float Sh = 1f + 0.015f * Cbarp * T;
            float Rt = -Mathf.Sin(2f * dtheta * Mathf.Deg2Rad) * Rc;

            float t1 = dLp / Sl;
            float t2 = dCp / Sc;
            float t3 = dHp / Sh;
            return Mathf.Sqrt(t1 * t1 + t2 * t2 + t3 * t3 + Rt * t2 * t3);
        }
    }

    /// <summary>
    /// Burst 高斯卷积（可分离，逐行） / Burst separable Gaussian convolution (per row).
    /// </summary>
    [BurstCompile]
    internal struct GaussianRowJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float> input;
        public NativeArray<float> temp;   // 中间结果（行卷积后）/ intermediate
        public NativeArray<float> output;
        public int width, height;
        [ReadOnly] public NativeArray<float> kernel; // 11 taps
        public int radius;

        public void Execute(int y)
        {
            for (int x = 0; x < width; x++)
            {
                float acc = 0f;
                for (int k = -radius; k <= radius; k++)
                {
                    int xx = Mathf.Clamp(x + k, 0, width - 1);
                    acc += input[y * width + xx] * kernel[k + radius];
                }
                temp[y * width + x] = acc;
            }
        }
    }

    /// <summary>
    /// Burst 高斯卷积（列方向）/ Burst Gaussian convolution (column pass).
    /// </summary>
    [BurstCompile]
    internal struct GaussianColumnJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float> temp;
        public NativeArray<float> output;
        public int width, height;
        [ReadOnly] public NativeArray<float> kernel;
        public int radius;

        public void Execute(int x)
        {
            for (int y = 0; y < height; y++)
            {
                float acc = 0f;
                for (int k = -radius; k <= radius; k++)
                {
                    int yy = Mathf.Clamp(y + k, 0, height - 1);
                    acc += temp[yy * width + x] * kernel[k + radius];
                }
                output[y * width + x] = acc;
            }
        }
    }

    /// <summary>
    /// Burst 2x2 平均降采样 / Burst 2x2 average downsampling.
    /// </summary>
    [BurstCompile]
    internal struct DownsampleJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float> input;
        public NativeArray<float> output;
        public int inW, inH, outW;

        public void Execute(int oy)
        {
            for (int ox = 0; ox < outW; ox++)
            {
                int iy0 = Mathf.Min(oy * 2, inH - 1);
                int ix0 = Mathf.Min(ox * 2, inW - 1);
                int iy1 = Mathf.Min(iy0 + 1, inH - 1);
                int ix1 = Mathf.Min(ix0 + 1, inW - 1);
                float v = (input[iy0 * inW + ix0] + input[iy0 * inW + ix1] +
                           input[iy1 * inW + ix0] + input[iy1 * inW + ix1]) * 0.25f;
                output[oy * outW + ox] = v;
            }
        }
    }

    /// <summary>
    /// 质量指标计算 / Quality metric computations.
    /// </summary>
    public static class QualityMetrics
    {
        private static readonly float[] GaussianKernel;
        private const int Radius = 5;

        static QualityMetrics()
        {
            // 11x11 高斯 σ=1.5 (separable) / 11x11 Gaussian σ=1.5 (separable)
            GaussianKernel = new float[11];
            float sigma = 1.5f;
            float sum = 0f;
            for (int i = -5; i <= 5; i++)
            {
                GaussianKernel[i + 5] = Mathf.Exp(-(i * i) / (2f * sigma * sigma));
                sum += GaussianKernel[i + 5];
            }
            for (int i = 0; i < 11; i++) GaussianKernel[i] /= sum;
        }

        /// <summary>
        /// 单尺度 SSIM / Single-scale SSIM (0..1, higher is better).
        /// </summary>
        public static float ComputeSSIM(float[] a, float[] b, int w, int h)
        {
            int n = w * h;
            if (n == 0) return 1f;

            using var na = new NativeArray<float>(a, Allocator.TempJob);
            using var nb = new NativeArray<float>(b, Allocator.TempJob);
            return ComputeSSIMNative(na, nb, w, h, out _, out _);
        }

        /// <summary>
        /// 多尺度 SSIM / Multi-scale SSIM (0..1, higher is better).
        /// </summary>
        public static float ComputeMS_SSIM(float[] a, float[] b, int w, int h)
        {
            int n = w * h;
            if (n == 0) return 1f;

            const float C1 = 0.01f * 0.01f;
            const float C2 = 0.03f * 0.03f;
            const int M = 5;
            float[] csWeights = { 0.0448f, 0.2856f, 0.3001f, 0.2363f };
            float lumaWeight = 0.1333f;

            // 显式所有权管理，避免 double-dispose / explicit ownership to avoid double-dispose
            var curA = new NativeArray<float>(a, Allocator.TempJob);
            var curB = new NativeArray<float>(b, Allocator.TempJob);
            bool ownsCur = true;
            int cw = w, ch = h;
            float ms = 1f;
            try
            {
                for (int level = 0; level < M; level++)
                {
                    float luma = 1f, cs = 1f;
                    ComputeSSIMNative(curA, curB, cw, ch, out luma, out cs, C1, C2);
                    if (level == M - 1)
                    {
                        ms *= Mathf.Pow(Mathf.Max(luma, 0f), lumaWeight);
                    }
                    else
                    {
                        ms *= Mathf.Pow(Mathf.Max(cs, 0f), csWeights[level]);
                    }

                    if (level < M - 1)
                    {
                        int nw = Mathf.Max(1, cw / 2), nh = Mathf.Max(1, ch / 2);
                        var da = Downsample(curA, cw, ch, nw, nh);
                        var db = Downsample(curB, cw, ch, nw, nh);
                        if (ownsCur) { curA.Dispose(); curB.Dispose(); }
                        curA = da;
                        curB = db;
                        ownsCur = true;
                        cw = nw; ch = nh;
                    }
                }
            }
            finally
            {
                if (ownsCur) { curA.Dispose(); curB.Dispose(); }
            }

            return Mathf.Clamp01(ms);
        }

        private static NativeArray<float> Downsample(NativeArray<float> input, int inW, int inH, int outW, int outH)
        {
            var output = new NativeArray<float>(outW * outH, Allocator.TempJob);
            var job = new DownsampleJob
            {
                input = input,
                output = output,
                inW = inW,
                inH = inH,
                outW = outW,
            };
            job.Schedule(outH, 16).Complete();
            return output;
        }

        private static float ComputeSSIMNative(NativeArray<float> a, NativeArray<float> b, int w, int h,
            out float luma, out float cs, float C1 = 0.01f * 0.01f, float C2 = 0.03f * 0.03f)
        {
            int n = w * h;
            var kernel = new NativeArray<float>(GaussianKernel, Allocator.TempJob);
            var muA = new NativeArray<float>(n, Allocator.TempJob);
            var muB = new NativeArray<float>(n, Allocator.TempJob);
            var a2 = new NativeArray<float>(n, Allocator.TempJob);
            var b2 = new NativeArray<float>(n, Allocator.TempJob);
            var a2c = new NativeArray<float>(n, Allocator.TempJob);
            var b2c = new NativeArray<float>(n, Allocator.TempJob);
            var abc = new NativeArray<float>(n, Allocator.TempJob);
            var scratchA = new NativeArray<float>(n, Allocator.TempJob);
            var scratchB = new NativeArray<float>(n, Allocator.TempJob);

            try
            {
                // 元素级平方/乘积 / element-wise squares and product
                for (int i = 0; i < n; i++)
                {
                    float av = a[i], bv = b[i];
                    a2[i] = av * av;
                    b2[i] = bv * bv;
                    abc[i] = av * bv;
                }

                // 可分离高斯卷积（行→列），scratchA/scratchB 复用 /
                // separable Gaussian (row → column), scratchA/scratchB reused
                Convolve(a, muA, scratchA, scratchB, w, h, kernel);
                Convolve(b, muB, scratchA, scratchB, w, h, kernel);
                Convolve(a2, a2c, scratchA, scratchB, w, h, kernel);
                Convolve(b2, b2c, scratchA, scratchB, w, h, kernel);
                Convolve(abc, abc, scratchA, scratchB, w, h, kernel);

                // 聚合 SSIM / aggregate SSIM
                double sumLuma = 0, sumCs = 0;
                for (int i = 0; i < n; i++)
                {
                    float ma = muA[i], mb = muB[i];
                    float varA = Mathf.Max(0f, a2c[i] - ma * ma);
                    float varB = Mathf.Max(0f, b2c[i] - mb * mb);
                    float covAB = abc[i] - ma * mb;

                    sumLuma += (2f * ma * mb + C1) / (ma * ma + mb * mb + C1);
                    sumCs += (2f * covAB + C2) / (varA + varB + C2);
                }
                luma = (float)(sumLuma / n);
                cs = (float)(sumCs / n);
                return luma * cs;
            }
            finally
            {
                kernel.Dispose(); muA.Dispose(); muB.Dispose();
                a2.Dispose(); b2.Dispose(); a2c.Dispose(); b2c.Dispose();
                abc.Dispose(); scratchA.Dispose(); scratchB.Dispose();
            }
        }

        /// <summary>
        /// 行卷积 → 列卷积 完整高斯滤波 / Full Gaussian filter: row pass then column pass.
        /// </summary>
        private static void Convolve(NativeArray<float> input, NativeArray<float> output,
            NativeArray<float> scratchA, NativeArray<float> scratchB, int w, int h, NativeArray<float> kernel)
        {
            // 行作业: input → scratchB（temp）/ row job: input → scratchB (temp)
            var row = new GaussianRowJob { input = input, temp = scratchB, output = scratchA, width = w, height = h, kernel = kernel, radius = Radius };
            row.Schedule(h, 16).Complete();
            // 列作业: scratchB → output / column job: scratchB → output
            var col = new GaussianColumnJob { temp = scratchB, output = output, width = w, height = h, kernel = kernel, radius = Radius };
            col.Schedule(w, 16).Complete();
        }

        /// <summary>
        /// 评估不透明/透明颜色质量 / Evaluate opaque/transparent color quality.
        /// </summary>
        /// <param name="orig">原图线性 RGBA / original linear RGBA</param>
        /// <param name="cand">候选线性 RGBA / candidate linear RGBA</param>
        /// <param name="w">宽度 / width</param>
        /// <param name="h">高度 / height</param>
        /// <param name="p">参数 / params</param>
        /// <param name="useSsimDeltaE">是否评估 SSIM+ΔE（&lt;11px 岛忽略）/ whether to evaluate SSIM+ΔE</param>
        /// <param name="multiScale">多尺度还是单尺度 / multi-scale vs single-scale</param>
        public static MetricReport EvaluateColor(float[] orig, float[] cand, int w, int h, MetricParams p,
            bool useSsimDeltaE, bool multiScale)
        {
            var rep = new MetricReport { allPass = true, ssim = 1f, deltaE = 0f };
            if (!useSsimDeltaE || w <= 0 || h <= 0) return rep;

            int n = w * h;
            var lumA = new float[n];
            var lumB = new float[n];
            for (int i = 0, j = 0; i < n; i++, j += 4)
            {
                lumA[i] = 0.2126729f * orig[j] + 0.7151522f * orig[j + 1] + 0.0721750f * orig[j + 2];
                lumB[i] = 0.2126729f * cand[j] + 0.7151522f * cand[j + 1] + 0.0721750f * cand[j + 2];
            }

            rep.ssim = multiScale ? ComputeMS_SSIM(lumA, lumB, w, h) : ComputeSSIM(lumA, lumB, w, h);

            // ΔE: 采样 ≤4096 个像素求均值（性能）/ ΔE: average over ≤4096 sampled pixels
            int step = Mathf.Max(1, n / 4096);
            double sumDE = 0; int count = 0;
            for (int i = 0; i < n; i += step)
            {
                int j = i * 4;
                var lab1 = ColorMetrics.RgbToLab(orig[j], orig[j + 1], orig[j + 2]);
                var lab2 = ColorMetrics.RgbToLab(cand[j], cand[j + 1], cand[j + 2]);
                sumDE += ColorMetrics.DeltaE2000(lab1, lab2);
                count++;
            }
            rep.deltaE = count > 0 ? (float)(sumDE / count) : 0f;

            rep.allPass = rep.ssim >= p.msSsim && rep.deltaE <= p.maxDeltaE;
            return rep;
        }

        /// <summary>
        /// 评估 alpha 质量 / Evaluate alpha quality.
        /// </summary>
        /// <param name="mode">Cutout → IoU；Blend → RMSE / Cutout → IoU; Blend → RMSE</param>
        /// <param name="cutoff">裁剪阈值 / clip threshold</param>
        public static float EvaluateAlpha(float[] origAlpha, float[] candAlpha, int n, AlphaMode mode, float cutoff,
            out float score)
        {
            if (mode == AlphaMode.Cutout)
            {
                int inter = 0, union = 0;
                for (int i = 0; i < n; i++)
                {
                    bool a = origAlpha[i] > cutoff;
                    bool b = candAlpha[i] > cutoff;
                    if (a && b) inter++;
                    if (a || b) union++;
                }
                score = union > 0 ? (float)inter / union : 1f;
                return score;
            }
            else // Blend
            {
                double sum = 0;
                for (int i = 0; i < n; i++)
                {
                    float d = origAlpha[i] - candAlpha[i];
                    sum += d * d;
                }
                score = n > 0 ? (float)Math.Sqrt(sum / n) : 0f;
                return score;
            }
        }

        /// <summary>
        /// 评估法线质量（角度误差 p95，度）/ Evaluate normal map quality (angle error p95 in degrees).
        /// </summary>
        public static float EvaluateNormal(float[] origRaw, float[] candRaw, int n, out float p95)
        {
            // 解码: xy = 2*raw-1; z = sqrt(1-x²-y²); 重归一化 / decode & renormalize
            int count = 0;
            var errors = new float[n];
            for (int i = 0, j = 0; i < n; i++, j += 4)
            {
                float ox = origRaw[j] * 2f - 1f;
                float oy = origRaw[j + 1] * 2f - 1f;
                float oz = Mathf.Sqrt(Mathf.Max(0f, 1f - ox * ox - oy * oy));
                float ol = Mathf.Sqrt(ox * ox + oy * oy + oz * oz);
                if (ol > 1e-6f) { ox /= ol; oy /= ol; oz /= ol; }

                float cx = candRaw[j] * 2f - 1f;
                float cy = candRaw[j + 1] * 2f - 1f;
                float cz = Mathf.Sqrt(Mathf.Max(0f, 1f - cx * cx - cy * cy));
                float cl = Mathf.Sqrt(cx * cx + cy * cy + cz * cz);
                if (cl > 1e-6f) { cx /= cl; cy /= cl; cz /= cl; }

                float dot = Mathf.Clamp(ox * cx + oy * cy + oz * cz, -1f, 1f);
                errors[i] = Mathf.Acos(dot) * Mathf.Rad2Deg;
                count++;
            }
            if (count == 0) { p95 = 0f; return 0f; }

            Array.Sort(errors, 0, count);
            p95 = errors[(int)(count * 0.95f)];
            return p95;
        }

        /// <summary>
        /// 评估灰度质量（仅被使用通道、线性 RMSE、逐通道取最差）/
        /// Evaluate grayscale quality (used channels only, linear RMSE, worst channel).
        /// </summary>
        public static float EvaluateGray(float[] orig, float[] cand, int n, out float worst)
        {
            worst = 0f;
            for (int c = 0; c < 4; c++)
            {
                // 检查通道是否被使用（原图非常量即使用）/ channel used if non-constant in original
                bool used = false;
                float first = orig[c];
                for (int i = 0; i < n; i++)
                {
                    if (Mathf.Abs(orig[i * 4 + c] - first) > 1e-4f) { used = true; break; }
                }
                if (!used) continue;

                double sum = 0;
                for (int i = 0; i < n; i++)
                {
                    float d = orig[i * 4 + c] - cand[i * 4 + c];
                    sum += d * d;
                }
                float rmse = n > 0 ? (float)Math.Sqrt(sum / n) : 0f;
                worst = Mathf.Max(worst, rmse);
            }
            return worst;
        }
    }
}
