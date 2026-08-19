// SPDX-License-Identifier: MIT
// AvatarTextureOptimizer (ATO) - Target-quality metrics.
// AvatarTextureOptimizer (ATO) - 目标质量算法的各项指标。

using System;
using System.Threading.Tasks;
using Unity.Mathematics;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor.Quality
{
    /// <summary>
    /// EN: A rectangular image in *linear* space. Colour textures are stored premultiplied by alpha when the
    ///     source has meaningful alpha, because that is the only mathematically correct way to downsample
    ///     transparent images (straight-alpha downsampling drags invisible RGB into visible pixels).
    /// ZH: 线性空间中的矩形图像。当源图存在有效 alpha 时，颜色以预乘 alpha 存储，
    ///     因为这是对透明图像做下采样唯一数学正确的方式（直通 alpha 下采样会把不可见的 RGB 拖进可见像素）。
    /// </summary>
    public sealed class LinearImage
    {
        public readonly int Width, Height;
        public readonly float4[] Pixels;
        public readonly bool Premultiplied;

        public LinearImage(int width, int height, bool premultiplied)
        {
            Width = Mathf.Max(1, width);
            Height = Mathf.Max(1, height);
            Premultiplied = premultiplied;
            Pixels = new float4[Width * Height];
        }

        public float4 this[int x, int y]
        {
            get => Pixels[y * Width + x];
            set => Pixels[y * Width + x] = value;
        }

        /// <summary>EN: Area-average (box) downsample. Correct for arbitrary, non-integer ratios.
        ///     ZH: 面积平均（box）下采样，对任意非整数比例都正确。</summary>
        public LinearImage Downsample(int w, int h)
        {
            w = Mathf.Max(1, w);
            h = Mathf.Max(1, h);
            var dst = new LinearImage(w, h, Premultiplied);
            if (w == Width && h == Height)
            {
                Array.Copy(Pixels, dst.Pixels, Pixels.Length);
                return dst;
            }

            // EN: GPU first; the CPU loop below is the reference implementation and the fallback.
            // ZH: 优先走 GPU；下方的 CPU 循环既是参考实现也是兜底路径。
            if (GpuImageOps.TryDownsample(this, w, h, out var gpu)) return gpu;

            float sx = (float)Width / w;
            float sy = (float)Height / h;

            Parallel.For(0, h, y =>
            {
                float y0 = y * sy, y1 = (y + 1) * sy;
                int iy0 = Mathf.FloorToInt(y0), iy1 = Mathf.Min(Height - 1, Mathf.CeilToInt(y1) - 1);
                for (int x = 0; x < w; x++)
                {
                    float x0 = x * sx, x1 = (x + 1) * sx;
                    int ix0 = Mathf.FloorToInt(x0), ix1 = Mathf.Min(Width - 1, Mathf.CeilToInt(x1) - 1);

                    float4 sum = 0f;
                    float weight = 0f;
                    for (int yy = iy0; yy <= iy1; yy++)
                    {
                        float wy = Mathf.Min(y1, yy + 1f) - Mathf.Max(y0, yy);
                        if (wy <= 0f) continue;
                        for (int xx = ix0; xx <= ix1; xx++)
                        {
                            float wx = Mathf.Min(x1, xx + 1f) - Mathf.Max(x0, xx);
                            if (wx <= 0f) continue;
                            float ww = wx * wy;
                            sum += Pixels[yy * Width + xx] * ww;
                            weight += ww;
                        }
                    }
                    dst.Pixels[y * w + x] = weight > 0f ? sum / weight : 0f;
                }
            });
            return dst;
        }

        /// <summary>EN: Bilinear upsample back to a reference resolution for comparison.
        ///     ZH: 双线性上采样回参考分辨率以便比较。</summary>
        public LinearImage UpsampleTo(int w, int h)
        {
            var dst = new LinearImage(w, h, Premultiplied);
            if (w == Width && h == Height)
            {
                Array.Copy(Pixels, dst.Pixels, Pixels.Length);
                return dst;
            }

            if (GpuImageOps.TryUpsample(this, w, h, out var gpu)) return gpu;

            float sx = (float)Width / w;
            float sy = (float)Height / h;

            Parallel.For(0, h, y =>
            {
                float fy = (y + 0.5f) * sy - 0.5f;
                int y0 = Mathf.Clamp(Mathf.FloorToInt(fy), 0, Height - 1);
                int y1 = Mathf.Clamp(y0 + 1, 0, Height - 1);
                float ty = Mathf.Clamp01(fy - y0);

                for (int x = 0; x < w; x++)
                {
                    float fx = (x + 0.5f) * sx - 0.5f;
                    int x0 = Mathf.Clamp(Mathf.FloorToInt(fx), 0, Width - 1);
                    int x1 = Mathf.Clamp(x0 + 1, 0, Width - 1);
                    float tx = Mathf.Clamp01(fx - x0);

                    var a = math.lerp(Pixels[y0 * Width + x0], Pixels[y0 * Width + x1], tx);
                    var b = math.lerp(Pixels[y1 * Width + x0], Pixels[y1 * Width + x1], tx);
                    dst.Pixels[y * w + x] = math.lerp(a, b, ty);
                }
            });
            return dst;
        }
    }

    /// <summary>
    /// EN: Result of comparing a rescaled island against its original.
    /// ZH: 缩放后的岛与原图比较的结果。
    /// </summary>
    public struct QualityResult
    {
        public float MsSsim;
        public float DeltaEMean;
        public float DeltaEP95;
        public float AlphaIoU;
        public float AlphaRmse;
        public float NormalAngleMeanDeg;
        public float NormalAngleP95Deg;
        public float GrayscaleRmse;

        public override string ToString() =>
            $"ssim={MsSsim:F4} dE={DeltaEMean:F2}/{DeltaEP95:F2} ioU={AlphaIoU:F4} aRMSE={AlphaRmse:F4} " +
            $"n={NormalAngleMeanDeg:F2}/{NormalAngleP95Deg:F2} gRMSE={GrayscaleRmse:F4}";
    }

    /// <summary>
    /// EN: All quality metrics required by the spec. Everything is evaluated in linear space on the
    ///     *upsampled* reconstruction versus the original, and the compression format's own loss is
    ///     deliberately NOT included (it is applied later and is orthogonal to UV scaling).
    /// ZH: 需求要求的全部质量指标。所有计算都在线性空间中、用“上采样回原尺寸的重建图”与原图比较，
    ///     并且刻意不包含最终压缩格式引入的损失（压缩在之后进行，与 UV 缩放正交）。
    /// </summary>
    public static class QualityMetrics
    {
        /// <summary>EN: MS-SSIM needs 11 * 2^4 = 176 px to run all five scales. ZH: MS-SSIM 需要 11 * 2^4 = 176px 才能跑完五个尺度。</summary>
        public const int MsSsimMinShortSide = 176;

        /// <summary>EN: Below this, structural metrics are meaningless. ZH: 低于此尺寸结构指标已无意义。</summary>
        public const int SsimMinShortSide = 11;

        private static readonly float[] MsSsimWeights = { 0.0448f, 0.2856f, 0.3001f, 0.2363f, 0.1333f };

        // ---- Entry point / 入口 -------------------------------------------------------------------

        public static QualityResult Evaluate(LinearImage original, LinearImage reconstructed,
            bool isNormalMap, bool isGrayscale, int grayChannelMask,
            ATOAlphaMode alphaMode, float[] cutoffs)
        {
            var r = new QualityResult
            {
                MsSsim = 1f,
                AlphaIoU = 1f,
            };

            if (isNormalMap)
            {
                EvaluateNormal(original, reconstructed, ref r);
                return r;
            }

            if (isGrayscale)
            {
                r.GrayscaleRmse = WorstChannelRmse(original, reconstructed, grayChannelMask);
                return r;
            }

            int shortSide = Mathf.Min(original.Width, original.Height);
            if (shortSide >= MsSsimMinShortSide) r.MsSsim = MultiScaleSsim(original, reconstructed);
            else if (shortSide >= SsimMinShortSide) r.MsSsim = SingleScaleSsim(original, reconstructed);
            else r.MsSsim = 1f; // EN: ignored. ZH: 忽略该参数。

            DeltaE2000(original, reconstructed, out r.DeltaEMean, out r.DeltaEP95);

            switch (alphaMode)
            {
                case ATOAlphaMode.Cutout:
                    r.AlphaIoU = WorstCutoutIoU(original, reconstructed, cutoffs);
                    break;
                case ATOAlphaMode.Blend:
                    r.AlphaRmse = AlphaRmse(original, reconstructed);
                    break;
            }

            return r;
        }

        /// <summary>EN: Does the result satisfy every threshold? ZH: 结果是否满足全部阈值？</summary>
        public static bool Passes(in QualityResult r, ATOQualityParams p, bool isNormalMap, bool isGrayscale,
            ATOAlphaMode alphaMode, int shortSide)
        {
            if (isNormalMap)
            {
                return r.NormalAngleMeanDeg <= p.normalAngleMeanMaxDeg &&
                       r.NormalAngleP95Deg <= p.normalAngleP95MaxDeg;
            }
            if (isGrayscale) return r.GrayscaleRmse <= p.grayscaleRmseMax;

            if (shortSide >= SsimMinShortSide && r.MsSsim < p.msSsimMin) return false;
            if (r.DeltaEMean > p.deltaE2000Mean) return false;
            if (r.DeltaEP95 > p.deltaE2000P95) return false;

            if (alphaMode == ATOAlphaMode.Cutout && r.AlphaIoU < p.alphaCutoutIoUMin) return false;
            if (alphaMode == ATOAlphaMode.Blend && r.AlphaRmse > p.alphaBlendRmseMax) return false;
            return true;
        }

        // ---- SSIM / MS-SSIM ------------------------------------------------------------------------

        /// <summary>EN: Relative luminance of a linear RGB pixel (Rec.709). ZH: 线性 RGB 像素的相对亮度（Rec.709）。</summary>
        private static float Luma(float4 c) => 0.2126f * c.x + 0.7152f * c.y + 0.0722f * c.z;

        private static float[] ToLuma(LinearImage img)
        {
            var l = new float[img.Width * img.Height];
            Parallel.For(0, img.Height, y =>
            {
                for (int x = 0; x < img.Width; x++) l[y * img.Width + x] = Luma(img.Pixels[y * img.Width + x]);
            });
            return l;
        }

        private static float[] Gaussian11()
        {
            // EN: 11-tap Gaussian, sigma = 1.5, as in Wang et al. 2004.
            // ZH: 11 抽头高斯核，sigma = 1.5，取自 Wang 等 2004 年的论文。
            var k = new float[11];
            float sum = 0f;
            for (int i = 0; i < 11; i++)
            {
                float d = i - 5f;
                k[i] = Mathf.Exp(-(d * d) / (2f * 1.5f * 1.5f));
                sum += k[i];
            }
            for (int i = 0; i < 11; i++) k[i] /= sum;
            return k;
        }

        private static readonly float[] Kernel = Gaussian11();

        private static float[] Blur(float[] src, int w, int h)
        {
            var tmp = new float[w * h];
            var dst = new float[w * h];

            Parallel.For(0, h, y =>
            {
                for (int x = 0; x < w; x++)
                {
                    float s = 0f;
                    for (int i = 0; i < 11; i++)
                    {
                        int xx = Mathf.Clamp(x + i - 5, 0, w - 1);
                        s += src[y * w + xx] * Kernel[i];
                    }
                    tmp[y * w + x] = s;
                }
            });

            Parallel.For(0, h, y =>
            {
                for (int x = 0; x < w; x++)
                {
                    float s = 0f;
                    for (int i = 0; i < 11; i++)
                    {
                        int yy = Mathf.Clamp(y + i - 5, 0, h - 1);
                        s += tmp[yy * w + x] * Kernel[i];
                    }
                    dst[y * w + x] = s;
                }
            });
            return dst;
        }

        private static void SsimComponents(float[] a, float[] b, int w, int h, out float meanSsim, out float meanCs)
        {
            // EN: The GPU path computes exactly the same quantities; see ATOImageOps.compute.
            // ZH: GPU 路径计算的是完全相同的量，见 ATOImageOps.compute。
            if (GpuImageOps.TrySsimScale(a, b, w, h, out meanSsim, out meanCs)) return;

            const float c1 = 0.01f * 0.01f;
            const float c2 = 0.03f * 0.03f;

            var muA = Blur(a, w, h);
            var muB = Blur(b, w, h);

            var aa = new float[a.Length];
            var bb = new float[a.Length];
            var ab = new float[a.Length];
            for (int i = 0; i < a.Length; i++)
            {
                aa[i] = a[i] * a[i];
                bb[i] = b[i] * b[i];
                ab[i] = a[i] * b[i];
            }

            var sAA = Blur(aa, w, h);
            var sBB = Blur(bb, w, h);
            var sAB = Blur(ab, w, h);

            double ssimSum = 0, csSum = 0;
            for (int i = 0; i < a.Length; i++)
            {
                float ma = muA[i], mb = muB[i];
                float va = Mathf.Max(0f, sAA[i] - ma * ma);
                float vb = Mathf.Max(0f, sBB[i] - mb * mb);
                float cab = sAB[i] - ma * mb;

                float l = (2f * ma * mb + c1) / (ma * ma + mb * mb + c1);
                float cs = (2f * cab + c2) / (va + vb + c2);
                ssimSum += l * cs;
                csSum += cs;
            }

            meanSsim = (float)(ssimSum / a.Length);
            meanCs = (float)(csSum / a.Length);
        }

        public static float SingleScaleSsim(LinearImage a, LinearImage b)
        {
            SsimComponents(ToLuma(a), ToLuma(b), a.Width, a.Height, out var ssim, out _);
            return Mathf.Clamp01(ssim);
        }

        public static float MultiScaleSsim(LinearImage a, LinearImage b)
        {
            var la = ToLuma(a);
            var lb = ToLuma(b);
            int w = a.Width, h = a.Height;

            double product = 1.0;
            double usedWeight = 0.0;
            for (int scale = 0; scale < 5; scale++)
            {
                SsimComponents(la, lb, w, h, out var ssim, out var cs);
                if (scale == 4)
                {
                    product *= Math.Pow(Math.Max(1e-6f, ssim), MsSsimWeights[scale]);
                    usedWeight += MsSsimWeights[scale];
                    break;
                }

                product *= Math.Pow(Math.Max(1e-6f, cs), MsSsimWeights[scale]);
                usedWeight += MsSsimWeights[scale];

                int nw = Mathf.Max(1, w / 2);
                int nh = Mathf.Max(1, h / 2);
                if (nw < 11 || nh < 11)
                {
                    // EN: Not enough resolution left; renormalise the weights we actually used.
                    // ZH: 剩余分辨率不足；对实际使用的权重做归一化。
                    product *= Math.Pow(Math.Max(1e-6f, ssim), 1.0 - usedWeight);
                    break;
                }

                la = Halve(la, w, h);
                lb = Halve(lb, w, h);
                w = nw;
                h = nh;
            }
            return Mathf.Clamp01((float)product);
        }

        private static float[] Halve(float[] src, int w, int h)
        {
            if (GpuImageOps.TryHalve(src, w, h, out var gpu, out _, out _)) return gpu;

            int nw = Mathf.Max(1, w / 2);
            int nh = Mathf.Max(1, h / 2);
            var dst = new float[nw * nh];
            for (int y = 0; y < nh; y++)
            for (int x = 0; x < nw; x++)
            {
                int x0 = Mathf.Min(w - 1, x * 2), x1 = Mathf.Min(w - 1, x * 2 + 1);
                int y0 = Mathf.Min(h - 1, y * 2), y1 = Mathf.Min(h - 1, y * 2 + 1);
                dst[y * nw + x] = 0.25f * (src[y0 * w + x0] + src[y0 * w + x1] + src[y1 * w + x0] + src[y1 * w + x1]);
            }
            return dst;
        }

        // ---- CIEDE2000 -----------------------------------------------------------------------------

        public static void DeltaE2000(LinearImage a, LinearImage b, out float mean, out float p95)
        {
            int n = a.Pixels.Length;
            float[] values;

            if (!GpuImageOps.TryDeltaEMap(a, b, out values))
            {
                values = new float[n];
                Parallel.For(0, n, i =>
                {
                    var ca = a.Pixels[i];
                    var cb = b.Pixels[i];
                    if (a.Premultiplied)
                    {
                        ca = Unpremultiply(ca);
                        cb = Unpremultiply(cb);
                    }
                    values[i] = Ciede2000(LinearRgbToLab(ca.xyz), LinearRgbToLab(cb.xyz));
                });
            }

            double sum = 0;
            for (int i = 0; i < n; i++) sum += values[i];
            mean = (float)(sum / Math.Max(1, n));

            var sorted = (float[])values.Clone();
            Array.Sort(sorted);
            p95 = sorted[Mathf.Clamp(Mathf.FloorToInt(0.95f * (n - 1)), 0, n - 1)];
        }

        private static float4 Unpremultiply(float4 c)
        {
            float a = math.max(c.w, 1e-4f);
            return new float4(c.x / a, c.y / a, c.z / a, c.w);
        }

        /// <summary>EN: Linear sRGB primaries -&gt; CIE XYZ (D65) -&gt; CIE L*a*b*. ZH: 线性 sRGB 基色 -&gt; CIE XYZ (D65) -&gt; CIE L*a*b*。</summary>
        public static float3 LinearRgbToLab(float3 rgb)
        {
            float x = 0.4124564f * rgb.x + 0.3575761f * rgb.y + 0.1804375f * rgb.z;
            float y = 0.2126729f * rgb.x + 0.7151522f * rgb.y + 0.0721750f * rgb.z;
            float z = 0.0193339f * rgb.x + 0.1191920f * rgb.y + 0.9503041f * rgb.z;

            const float xn = 0.95047f, yn = 1.0f, zn = 1.08883f;
            float fx = LabF(x / xn), fy = LabF(y / yn), fz = LabF(z / zn);
            return new float3(116f * fy - 16f, 500f * (fx - fy), 200f * (fy - fz));
        }

        private static float LabF(float t)
        {
            const float d = 6f / 29f;
            return t > d * d * d ? Mathf.Pow(t, 1f / 3f) : t / (3f * d * d) + 4f / 29f;
        }

        /// <summary>EN: Full CIEDE2000 implementation (Sharma, Wu &amp; Dalal 2005). ZH: 完整的 CIEDE2000 实现（Sharma、Wu 与 Dalal，2005）。</summary>
        public static float Ciede2000(float3 lab1, float3 lab2)
        {
            const float kL = 1f, kC = 1f, kH = 1f;

            float l1 = lab1.x, a1 = lab1.y, b1 = lab1.z;
            float l2 = lab2.x, a2 = lab2.y, b2 = lab2.z;

            float c1 = Mathf.Sqrt(a1 * a1 + b1 * b1);
            float c2 = Mathf.Sqrt(a2 * a2 + b2 * b2);
            float cBar = (c1 + c2) * 0.5f;

            float cBar7 = Mathf.Pow(cBar, 7f);
            float g = 0.5f * (1f - Mathf.Sqrt(cBar7 / (cBar7 + Mathf.Pow(25f, 7f))));

            float a1p = (1f + g) * a1;
            float a2p = (1f + g) * a2;
            float c1p = Mathf.Sqrt(a1p * a1p + b1 * b1);
            float c2p = Mathf.Sqrt(a2p * a2p + b2 * b2);

            float h1p = Hue(b1, a1p);
            float h2p = Hue(b2, a2p);

            float dLp = l2 - l1;
            float dCp = c2p - c1p;

            float dhp;
            if (c1p * c2p == 0f) dhp = 0f;
            else
            {
                dhp = h2p - h1p;
                if (dhp > 180f) dhp -= 360f;
                else if (dhp < -180f) dhp += 360f;
            }
            float dHp = 2f * Mathf.Sqrt(c1p * c2p) * Mathf.Sin(dhp * Mathf.Deg2Rad * 0.5f);

            float lBarP = (l1 + l2) * 0.5f;
            float cBarP = (c1p + c2p) * 0.5f;

            float hBarP;
            if (c1p * c2p == 0f) hBarP = h1p + h2p;
            else
            {
                float diff = Mathf.Abs(h1p - h2p);
                if (diff <= 180f) hBarP = (h1p + h2p) * 0.5f;
                else if (h1p + h2p < 360f) hBarP = (h1p + h2p + 360f) * 0.5f;
                else hBarP = (h1p + h2p - 360f) * 0.5f;
            }

            float t = 1f
                      - 0.17f * Mathf.Cos((hBarP - 30f) * Mathf.Deg2Rad)
                      + 0.24f * Mathf.Cos((2f * hBarP) * Mathf.Deg2Rad)
                      + 0.32f * Mathf.Cos((3f * hBarP + 6f) * Mathf.Deg2Rad)
                      - 0.20f * Mathf.Cos((4f * hBarP - 63f) * Mathf.Deg2Rad);

            float dTheta = 30f * Mathf.Exp(-Mathf.Pow((hBarP - 275f) / 25f, 2f));
            float cBarP7 = Mathf.Pow(cBarP, 7f);
            float rC = 2f * Mathf.Sqrt(cBarP7 / (cBarP7 + Mathf.Pow(25f, 7f)));
            float rT = -rC * Mathf.Sin(2f * dTheta * Mathf.Deg2Rad);

            float lBarP50 = (lBarP - 50f) * (lBarP - 50f);
            float sL = 1f + (0.015f * lBarP50) / Mathf.Sqrt(20f + lBarP50);
            float sC = 1f + 0.045f * cBarP;
            float sH = 1f + 0.015f * cBarP * t;

            float termL = dLp / (kL * sL);
            float termC = dCp / (kC * sC);
            float termH = dHp / (kH * sH);

            return Mathf.Sqrt(termL * termL + termC * termC + termH * termH + rT * termC * termH);
        }

        private static float Hue(float b, float ap)
        {
            if (b == 0f && ap == 0f) return 0f;
            float h = Mathf.Atan2(b, ap) * Mathf.Rad2Deg;
            return h < 0f ? h + 360f : h;
        }

        // ---- Alpha ---------------------------------------------------------------------------------

        public static float WorstCutoutIoU(LinearImage a, LinearImage b, float[] cutoffs)
        {
            if (cutoffs == null || cutoffs.Length == 0) cutoffs = new[] { 0.5f };
            float worst = 1f;
            foreach (var cutoff in cutoffs)
            {
                long inter = 0, union = 0;
                for (int i = 0; i < a.Pixels.Length; i++)
                {
                    bool pa = a.Pixels[i].w >= cutoff;
                    bool pb = b.Pixels[i].w >= cutoff;
                    if (pa && pb) inter++;
                    if (pa || pb) union++;
                }
                float iou = union == 0 ? 1f : (float)inter / union;
                worst = Mathf.Min(worst, iou);
            }
            return worst;
        }

        public static float AlphaRmse(LinearImage a, LinearImage b)
        {
            double sum = 0;
            for (int i = 0; i < a.Pixels.Length; i++)
            {
                float d = a.Pixels[i].w - b.Pixels[i].w;
                sum += d * d;
            }
            return Mathf.Sqrt((float)(sum / Math.Max(1, a.Pixels.Length)));
        }

        // ---- Grayscale / data maps -----------------------------------------------------------------

        /// <summary>EN: Per-channel linear RMSE, restricted to sampled channels, worst channel wins.
        ///     ZH: 逐通道线性 RMSE，仅统计被使用的通道，取最差通道。</summary>
        public static float WorstChannelRmse(LinearImage a, LinearImage b, int channelMask)
        {
            if (channelMask == 0) channelMask = 0xF;
            float worst = 0f;
            for (int ch = 0; ch < 4; ch++)
            {
                if ((channelMask & (1 << ch)) == 0) continue;
                double sum = 0;
                for (int i = 0; i < a.Pixels.Length; i++)
                {
                    float d = a.Pixels[i][ch] - b.Pixels[i][ch];
                    sum += d * d;
                }
                worst = Mathf.Max(worst, Mathf.Sqrt((float)(sum / Math.Max(1, a.Pixels.Length))));
            }
            return worst;
        }

        // ---- Normal maps ---------------------------------------------------------------------------

        /// <summary>
        /// EN: Angular error between the decoded, renormalised normals. The images passed in must already
        ///     hold decoded unit vectors in xyz (see <see cref="NormalCodec"/>).
        /// ZH: 解码并重归一化之后的法线之间的角度误差。传入的图像必须已在 xyz 中存放单位向量
        ///     （见 <see cref="NormalCodec"/>）。
        /// </summary>
        private static void EvaluateNormal(LinearImage a, LinearImage b, ref QualityResult r)
        {
            int n = a.Pixels.Length;
            float[] angles;

            if (!GpuImageOps.TryNormalAngleMap(a, b, out angles))
            {
                angles = new float[n];
                Parallel.For(0, n, i =>
                {
                    var na = math.normalizesafe(a.Pixels[i].xyz, new float3(0, 0, 1));
                    var nb = math.normalizesafe(b.Pixels[i].xyz, new float3(0, 0, 1));
                    angles[i] = math.degrees(math.acos(math.clamp(math.dot(na, nb), -1f, 1f)));
                });
            }

            double sum = 0;
            for (int i = 0; i < n; i++) sum += angles[i];
            r.NormalAngleMeanDeg = (float)(sum / Math.Max(1, n));

            var sorted = (float[])angles.Clone();
            Array.Sort(sorted);
            r.NormalAngleP95Deg = sorted[Mathf.Clamp(Mathf.FloorToInt(0.95f * (n - 1)), 0, n - 1)];
        }
    }
}
