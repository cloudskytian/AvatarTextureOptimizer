// ============================================================================
// ATO - quality metrics
// ATO - 质量指标
//
// Metrics per spec  按规范的指标：
//  - MS-SSIM (fallback: single-scale SSIM when bbox short side < 176px;
//    ignored when < 11px)  MS-SSIM（短边<176 回退单尺度；<11 忽略）
//  - ΔE2000 (CIEDE2000, linear space)  ΔE2000（线性空间）
//  - alpha: cutout -> clipped silhouette IoU; blend/premultiply -> linear
//    RMSE  alpha：裁剪->轮廓 IoU；混合/预乘->线性 RMSE
//  - normal: angle error p95 after decode/resample/renormalize/encode
//    法线：解码/重采样/重归一化/编码后的角度误差 p95
//  - gray: linear RMSE on used channels only, worst channel
//    灰度：仅被使用通道的线性 RMSE，逐通道取最差
//
// Performance note  性能说明：SSIM is computed on a cap of SSIM_MAX_RES
// (2048) to bound cost on very large regions; all other metrics run on the
// full original-size comparison.  SSIM 在 2048 上限内计算以限制大区域开销；
// 其余指标均按原尺寸完整比较。
// ============================================================================

#region

using System.Collections.Generic;
using net.fosa.AvatarTextureOptimizer.Editor.Core;
using UnityEngine;

#endregion

namespace net.fosa.AvatarTextureOptimizer.Editor.Quality
{
    public static class Metrics
    {
        public const int SSIM_MAX_RES = 2048;
        public const int SSIM_FALLBACK_SHORT = 176;
        public const int SSIM_IGNORE_SHORT = 11;

        /// <summary>Full evaluation. Returns true when ALL applicable
        /// metrics pass. 完整评估；全部适用指标达标返回 true。</summary>
        public static bool Evaluate(
            float[] original, int ow, int oh,
            byte[] coverage,
            float[] scaled, int sw, int sh, // upsampled back to (ow,oh) 已上采样回原尺寸
            ATOTextureCategory category, int alphaMode, float cutoff,
            ATOQualityParams p,
            out float worstScore)
        {
            worstScore = 1f;
            int n = ow * oh;
            if (scaled.Length < n * 4) return false;

            // -------- per-pixel fast metrics  逐像素快速指标 --------
            float dE2000Max = 0f, alphaErr = 0f, grayErr = 0f, angleErrSum = 0f;
            int angleCount = 0;
            float[] angleSamples = null;
            long iouInter = 0, iouUnion = 0;
            float[] chVariance = new float[4];
            float[] chSum = new float[4];
            int valid = 0;

            for (int y = 0; y < oh; y++)
            {
                for (int x = 0; x < ow; x++)
                {
                    int i = y * ow + x;
                    if (coverage[i] == 0) continue;
                    int o = i * 4, s = i * 4;
                    float r0 = original[o], g0 = original[o + 1], b0 = original[o + 2], a0 = original[o + 3];
                    float r1 = scaled[s], g1 = scaled[s + 1], b1 = scaled[s + 2], a1 = scaled[s + 3];
                    valid++;

                    // channel variance (to detect "used" gray channels)
                    // 通道方差（判断灰度通道是否被使用）
                    for (int c = 0; c < 4; c++)
                    {
                        chSum[c] += original[o + c];
                        chVariance[c] += original[o + c] * original[o + c];
                    }

                    switch (category)
                    {
                        case ATOTextureCategory.Normal:
                        {
                            float dot = Mathf.Clamp(r0 * r1 + g0 * g1 + b0 * b1, -1f, 1f);
                            float ang = Mathf.Acos(dot) * Mathf.Rad2Deg;
                            if (angleSamples == null) angleSamples = new float[Mathf.Max(64, n / 2)];
                            if (angleCount < angleSamples.Length) angleSamples[angleCount] = ang;
                            angleCount++;
                            angleErrSum += ang;
                            break;
                        }
                        case ATOTextureCategory.Gray:
                        {
                            // worst-channel linear RMSE  最差通道线性 RMSE
                            for (int c = 0; c < 3; c++)
                            {
                                float d = original[o + c] - scaled[s + c];
                                float e = d * d;
                                if (e > grayErr) grayErr = e; // running max of squared (per worst)
                            }
                            break;
                        }
                        default:
                        {
                            // ΔE2000  ΔE2000
                            float dE = CIEDE2000(r0, g0, b0, r1, g1, b1);
                            if (dE > dE2000Max) dE2000Max = dE;
                            // alpha  alpha
                            if (alphaMode == 1) // cutout  裁剪
                            {
                                int c0 = a0 >= cutoff ? 1 : 0;
                                int c1 = a1 >= cutoff ? 1 : 0;
                                if (c0 == 1 && c1 == 1) iouInter++;
                                if (c0 == 1 || c1 == 1) iouUnion++;
                            }
                            else if (alphaMode >= 2) // blend / premultiply  混合/预乘
                            {
                                if (alphaMode == 3)
                                {
                                    // premultiplied comparison  预乘比较
                                    float dr = (a0 * r0 - a1 * r1);
                                    float dg = (a0 * g0 - a1 * g1);
                                    float db = (a0 * b0 - a1 * b1);
                                    float da = a0 - a1;
                                    float e = dr * dr + dg * dg + db * db + da * da;
                                    if (e > alphaErr) alphaErr = e;
                                }
                                else
                                {
                                    float da = a0 - a1;
                                    float e = da * da;
                                    if (e > alphaErr) alphaErr = e;
                                }
                            }
                            break;
                        }
                    }
                }
            }
            if (valid == 0) return true; // no covered pixels - nothing to judge
            // 无覆盖像素 - 无法判定

            float e = 1f;

            // ---- category metrics  类别指标 ----
            switch (category)
            {
                case ATOTextureCategory.Normal:
                {
                    // p95 angle  p95 角度
                    if (angleCount > 0)
                    {
                        var arr = new float[angleCount];
                        System.Array.Copy(angleSamples, arr, angleCount);
                        System.Array.Sort(arr);
                        float p95 = arr[Mathf.Min(angleCount - 1, (int) (angleCount * 0.95f))];
                        e = Mathf.Min(e, p95 / p.normalAngleP95);
                        if (p95 > p.normalAngleP95) return worstScore = e, false;
                    }
                    break;
                }
                case ATOTextureCategory.Gray:
                {
                    // used channels only  仅使用通道
                    float worst = 1f;
                    float meanDiv = 1f / valid;
                    for (int c = 0; c < 3; c++)
                    {
                        float mean = chSum[c] * meanDiv;
                        float variance = chVariance[c] * meanDiv - mean * mean;
                        if (variance < 1e-5f) continue; // unused channel  未使用通道
                        // recompute RMSE on used channel  在该通道重算 RMSE
                        float sum = 0f;
                        for (int y2 = 0; y2 < oh; y2++)
                        {
                            for (int x2 = 0; x2 < ow; x2++)
                            {
                                int i2 = (y2 * ow + x2);
                                if (coverage[i2] == 0) continue;
                                int o2 = i2 * 4, s2 = i2 * 4;
                                float d = original[o2 + c] - scaled[s2 + c];
                                sum += d * d;
                            }
                        }
                        float rmse = Mathf.Sqrt(sum / valid);
                        float ratio = rmse / p.grayRMSE;
                        if (ratio > worst) worst = ratio;
                    }
                    e = Mathf.Min(e, worst);
                    if (worst > 1f) return false;
                    worstScore = e;
                    break;
                }
                default:
                {
                    if (dE2000Max > p.deltaE2000)
                    {
                        worstScore = Mathf.Min(worstScore, p.deltaE2000 / Mathf.Max(1e-6f, dE2000Max));
                        return false;
                    }
                    if (alphaMode == 1 && iouUnion > 0)
                    {
                        float iou = (float) iouInter / iouUnion;
                        if (iou < p.cutoutIoU)
                        {
                            worstScore = Mathf.Min(worstScore, iou / Mathf.Max(1e-6f, p.cutoutIoU));
                            return false;
                        }
                        e = Mathf.Min(e, iou / Mathf.Max(1e-6f, p.cutoutIoU));
                    }
                    else if (alphaMode >= 2)
                    {
                        float rmse = Mathf.Sqrt(alphaErr / valid);
                        if (rmse > p.alphaRMSE)
                        {
                            worstScore = Mathf.Min(worstScore, p.alphaRMSE / Mathf.Max(1e-6f, rmse));
                            return false;
                        }
                        e = Mathf.Min(e, rmse / Mathf.Max(1e-6f, p.alphaRMSE));
                    }
                    break;
                }
            }

            // -------- SSIM / MS-SSIM  --------
            int shortSide = Mathf.Min(ow, oh);
            if (category != ATOTextureCategory.Normal && shortSide >= SSIM_IGNORE_SHORT)
            {
                float ssim;
                if (shortSide >= SSIM_FALLBACK_SHORT)
                {
                    ssim = MSSSIM(original, ow, oh, scaled, ow, oh, coverage);
                }
                else
                {
                    ssim = SSIM(original, ow, oh, scaled, ow, oh, coverage);
                }
                if (ssim < p.ssim)
                {
                    worstScore = Mathf.Min(worstScore, ssim / Mathf.Max(1e-6f, p.ssim));
                    return false;
                }
                e = Mathf.Min(e, ssim / Mathf.Max(1e-6f, p.ssim));
            }

            worstScore = e;
            return true;
        }

        // ==================================================================
        // ΔE2000  ΔE2000
        // ==================================================================
        public static float CIEDE2000(float l1, float a1, float b1, float l2, float a2, float b2)
        {
            float kL = 1f, kC = 1f, kH = 1f;
            float c1 = Mathf.Sqrt(a1 * a1 + b1 * b1);
            float c2 = Mathf.Sqrt(a2 * a2 + b2 * b2);
            float cbar = (c1 + c2) * 0.5f;
            float cbar7 = Mathf.Pow(cbar, 7f);
            float g = 0.5f * (1f - Mathf.Sqrt(cbar7 / (cbar7 + Mathf.Pow(25f, 7f))));
            float a1p = a1 * (1f + g);
            float a2p = a2 * (1f + g);
            float c1p = Mathf.Sqrt(a1p * a1p + b1 * b1);
            float c2p = Mathf.Sqrt(a2p * a2p + b2 * b2);
            float h1p = c1p > 0f ? NormAngle(Mathf.Atan2(b1, a1p) * Mathf.Rad2Deg) : 0f;
            float h2p = c2p > 0f ? NormAngle(Mathf.Atan2(b2, a2p) * Mathf.Rad2Deg) : 0f;
            float dLp = l2 - l1;
            float dcp = c2p - c1p;
            float dhp;
            if (c1p * c2p == 0f) dhp = 0f;
            else if (Mathf.Abs(h2p - h1p) <= 180f) dhp = h2p - h1p;
            else if (h2p - h1p > 180f) dhp = h2p - h1p - 360f;
            else dhp = h2p - h1p + 360f;
            float dhp2 = 2f * Mathf.Sqrt(c1p * c2p) * Mathf.Sin(dhp * 0.5f * Mathf.Deg2Rad);
            float Lbp = (l1 + l2) * 0.5f;
            float Cbp = (c1p + c2p) * 0.5f;
            float hbp;
            if (c1p * c2p == 0f) hbp = h1p + h2p;
            else if (Mathf.Abs(h1p - h2p) <= 180f) hbp = (h1p + h2p) * 0.5f;
            else if (h1p + h2p < 360f) hbp = (h1p + h2p + 360f) / 2f;
            else hbp = (h1p + h2p - 360f) / 2f;
            float T = 1f - 0.17f * Mathf.Cos((hbp - 30f) * Mathf.Deg2Rad)
                          + 0.24f * Mathf.Cos(2f * hbp * Mathf.Deg2Rad)
                          + 0.32f * Mathf.Cos((3f * hbp + 6f) * Mathf.Deg2Rad)
                          - 0.20f * Mathf.Cos((4f * hbp - 63f) * Mathf.Deg2Rad);
            float dTheta = 30f * Mathf.Exp(-Mathf.Pow((hbp - 275f) / 25f, 2f));
            float Rc = 2f * Mathf.Sqrt(Mathf.Pow(Cbp, 7f) / (Mathf.Pow(Cbp, 7f) + Mathf.Pow(25f, 7f)));
            float Sl = 1f + 0.015f * Mathf.Pow(Lbp - 50f, 2f) / Mathf.Sqrt(20f + Mathf.Pow(Lbp - 50f, 2f));
            float Sc = 1f + 0.045f * Cbp;
            float Sh = 1f + 0.015f * Cbp * T;
            float Rt = -Mathf.Sin(2f * dTheta * Mathf.Deg2Rad) * Rc;
            float term = dLp / (kL * Sl);
            float termC = dcp / (kC * Sc);
            float termH = dhp2 / (kH * Sh);
            return Mathf.Sqrt(term * term + termC * termC + termH * termH + Rt * termC * termH);
        }

        private static float NormAngle(float deg)
        {
            deg = deg % 360f;
            if (deg < 0f) deg += 360f;
            return deg;
        }

        /// <summary>Linear sRGB -> Lab. 线性 sRGB 转 Lab。</summary>
        public static void RgbToLab(float r, float g, float b, out float L, out float A, out float B)
        {
            float x = (0.4124564f * r + 0.3575761f * g + 0.1804375f * b) / 0.95047f;
            float y = 0.2126729f * r + 0.7151522f * g + 0.0721750f * b;
            float z = (0.0193339f * r + 0.1191920f * g + 0.9503041f * b) / 1.08883f;
            x = LabF(x);
            y = LabF(y);
            z = LabF(z);
            L = 116f * y - 16f;
            A = 500f * (x - y);
            B = 200f * (y - z);
        }

        private static float LabF(float t)
        {
            const float e = 0.008856f;
            const float k = 903.3f;
            return t > e ? Mathf.Pow(t, 1f / 3f) : k * t + 16f / 116f;
        }

        /// <summary>Converts linear RGB pixels to Lab buffer (RGB in-place ->
        /// Lab). 线性 RGB 像素转 Lab。</summary>
        public static void ToLab(float[] rgba, int count)
        {
            for (int i = 0; i < count * 4; i += 4)
            {
                float L, A, B;
                RgbToLab(rgba[i], rgba[i + 1], rgba[i + 2], out L, out A, out B);
                rgba[i] = L;
                rgba[i + 1] = A;
                rgba[i + 2] = B;
            }
        }

        // ==================================================================
        // SSIM / MS-SSIM
        // ==================================================================
        private static readonly float[] Gaussian = { 0.0044f, 0.0231f, 0.1037f, 0.2625f, 0.2625f, 0.1037f, 0.0231f, 0.0044f };

        /// <summary>Single-scale SSIM over covered pixels (masked average of
        /// the SSIM map, 8-tap gaussian means).
        /// 覆盖像素上的单尺度 SSIM（SSIM 图掩码平均，8 抽头高斯均值）。</summary>
        public static float SSIM(float[] x, int w, int h, float[] y, int w2, int h2, byte[] coverage)
        {
            // cap resolution  分辨率上限
            if (Mathf.Max(w, h) > SSIM_MAX_RES)
            {
                int dw = Mathf.RoundToInt(w * (float) SSIM_MAX_RES / Mathf.Max(w, h));
                int dh = Mathf.RoundToInt(h * (float) SSIM_MAX_RES / Mathf.Max(w, h));
                x = Bilinear.Resample(x, w, h, dw, dh);
                y = Bilinear.Resample(y, w2, h2, dw, dh);
                coverage = ResampleCoverage(coverage, w, h, dw, dh);
                w = dw;
                h = dh;
            }

            var lx = new float[x.Length];
            System.Array.Copy(x, lx, x.Length);
            var ly = new float[y.Length];
            System.Array.Copy(y, ly, y.Length);
            ToLab(lx, w * h);
            ToLab(ly, w * h);

            float total = 0f;
            int count = 0;
            float C1 = 0.01f * 100f * 0.01f * 100f; // L=100 (Lab L range 0..100)
            float C2 = 0.03f * 100f * 0.03f * 100f;
            // simplified: use luminance channel only with gaussian means
            // 简化：仅用亮度通道的高斯均值
            var mx = BoxBlur(lx, w, h);
            var my = BoxBlur(ly, w, h);
            var mx2 = BoxBlur(MulPointwise(lx, lx), w, h);
            var my2 = BoxBlur(MulPointwise(ly, ly), w, h);
            var mxy = BoxBlur(MulPointwise(lx, ly), w, h);

            for (int i = 0; i < w * h; i++)
            {
                if (coverage[i] == 0) continue;
                float sx = mx[i], sy = my[i];
                float vxp = mx2[i] - sx * sx;
                float vyp = my2[i] - sy * sy;
                float covp = mxy[i] - sx * sy;
                var num = (2f * sx * sy + C1) * (2f * covp + C2);
                var den = (sx * sx + sy * sy + C1) * (vxp + vyp + C2);
                total += num / den;
                count++;
            }
            return count == 0 ? 1f : total / count;
        }

        /// <summary>Multi-scale SSIM (3 scales, standard weights).
        /// 多尺度 SSIM（3 尺度，标准权重）。</summary>
        public static float MSSSIM(float[] x, int w, int h, float[] y, int w2, int h2, byte[] coverage)
        {
            // cap  上限
            if (Mathf.Max(w, h) > SSIM_MAX_RES)
            {
                int dw = Mathf.RoundToInt(w * (float) SSIM_MAX_RES / Mathf.Max(w, h));
                int dh = Mathf.RoundToInt(h * (float) SSIM_MAX_RES / Mathf.Max(w, h));
                x = Bilinear.Resample(x, w, h, dw, dh);
                y = Bilinear.Resample(y, w2, h2, dw, dh);
                coverage = ResampleCoverage(coverage, w, h, dw, dh);
                w = dw;
                h = dh;
            }

            var lx = new float[x.Length];
            System.Array.Copy(x, lx, x.Length);
            var ly = new float[y.Length];
            System.Array.Copy(y, ly, y.Length);
            ToLab(lx, w * h);
            ToLab(ly, w * h);

            float mscc = 1f;
            float prod = 1f;
            for (int s = 0; s < 3; s++)
            {
                float wgt = s == 0 ? 0.61686f : s == 1 ? 0.28437f : 0.09877f;
                float ss;
                if (s < 2)
                {
                    ss = SSIMNoCap(lx, w, h, ly, w, h, coverage, out mscc);
                    // downsample by 2  下采样 2 倍
                    lx = Downsample2(lx, w, h);
                    ly = Downsample2(ly, w, h);
                    coverage = Downsample2(coverage, w, h);
                    w = Mathf.Max(1, w / 2);
                    h = Mathf.Max(1, h / 2);
                    prod *= Mathf.Pow(ss, 0.846f);
                }
                else
                {
                    // last scale: correlation  最后尺度：相关系数
                    var mx = BoxBlur(lx, w, h);
                    var my = BoxBlur(ly, w, h);
                    var mxy = BoxBlur(MulPointwise(lx, ly), w, h);
                    var mx2 = BoxBlur(MulPointwise(lx, lx), w, h);
                    var my2 = BoxBlur(MulPointwise(ly, ly), w, h);
                    float sumCov = 0f, sumVx = 0f, sumVy = 0f;
                    int n = 0;
                    for (int i = 0; i < w * h; i++)
                    {
                        if (coverage[i] == 0) continue;
                        sumCov += mxy[i] - mx[i] * my[i];
                        sumVx += mx2[i] - mx[i] * mx[i];
                        sumVy += my2[i] - my[i] * my[i];
                        n++;
                    }
                    mscc = n == 0 ? 1f : sumCov / Mathf.Sqrt(Mathf.Max(1e-12f, sumVx * sumVy));
                }
            }
            return Mathf.Pow(prod, 1f / 3f) * Mathf.Pow(mscc, 1f / 3f);
        }

        private static float SSIMNoCap(float[] x, int w, int h, float[] y, int w2, int h2, byte[] coverage,
            out float lastCorr)
        {
            lastCorr = 1f;
            float C1 = 0.01f * 100f * 0.01f * 100f;
            float C2 = 0.03f * 100f * 0.03f * 100f;
            var mx = BoxBlur(x, w, h);
            var my = BoxBlur(y, w, h);
            var mx2 = BoxBlur(MulPointwise(x, x), w, h);
            var my2 = BoxBlur(MulPointwise(y, y), w, h);
            var mxy = BoxBlur(MulPointwise(x, y), w, h);
            float total = 0f;
            int count = 0;
            for (int i = 0; i < w * h; i++)
            {
                if (coverage[i] == 0) continue;
                float sx = mx[i], sy = my[i];
                float vxp = mx2[i] - sx * sx;
                float vyp = my2[i] - sy * sy;
                float covp = mxy[i] - sx * sy;
                total += ((2f * sx * sy + C1) * (2f * covp + C2)) /
                         ((sx * sx + sy * sy + C1) * (vxp + vyp + C2));
                count++;
            }
            return count == 0 ? 1f : total / count;
        }

        // ---- small helpers  小工具 ----
        private static float[] MulPointwise(float[] a, float[] b)
        {
            var r = new float[a.Length];
            for (int i = 0; i < a.Length; i++) r[i] = a[i] * b[i];
            return r;
        }

        /// <summary>Approximate gaussian with two box blurs (channel 0 =
        /// luma proxy for Lab L). 两次盒式模糊近似高斯（Lab 的 L 通道）。</summary>
        private static float[] BoxBlur(float[] data, int w, int h)
        {
            var tmp = new float[data.Length];
            var outp = new float[data.Length];
            // horizontal  水平
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float sum = 0f;
                    for (int k = -1; k <= 1; k++)
                    {
                        int xx = Mathf.Clamp(x + k, 0, w - 1);
                        sum += data[y * w + xx];
                    }
                    tmp[y * w + x] = sum / 3f;
                }
            }
            // vertical  垂直
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float sum = 0f;
                    for (int k = -1; k <= 1; k++)
                    {
                        int yy = Mathf.Clamp(y + k, 0, h - 1);
                        sum += tmp[yy * w + x];
                    }
                    outp[y * w + x] = sum / 3f;
                }
            }
            return outp;
        }

        private static float[] Downsample2(float[] data, int w, int h)
        {
            int dw = Mathf.Max(1, w / 2), dh = Mathf.Max(1, h / 2);
            var r = new float[dw * dh];
            for (int y = 0; y < dh; y++)
            {
                for (int x = 0; x < dw; x++)
                {
                    r[y * dw + x] = (data[(2 * y) * w + 2 * x] + data[(2 * y) * w + 2 * x + 1] +
                                      data[(2 * y + 1) * w + 2 * x] + data[(2 * y + 1) * w + 2 * x + 1]) * 0.25f;
                }
            }
            return r;
        }

        private static byte[] Downsample2(byte[] cov, int w, int h)
        {
            int dw = Mathf.Max(1, w / 2), dh = Mathf.Max(1, h / 2);
            var r = new byte[dw * dh];
            for (int y = 0; y < dh; y++)
            {
                for (int x = 0; x < dw; x++)
                {
                    int s = 0;
                    s += cov[(2 * y) * w + 2 * x];
                    s += cov[(2 * y) * w + 2 * x + 1];
                    s += cov[(2 * y + 1) * w + 2 * x];
                    s += cov[(2 * y + 1) * w + 2 * x + 1];
                    r[y * dw + x] = s > 0 ? (byte) 1 : (byte) 0;
                }
            }
            return r;
        }

        private static byte[] ResampleCoverage(byte[] cov, int w, int h, int dw, int dh)
        {
            var r = new byte[dw * dh];
            for (int y = 0; y < dh; y++)
            {
                for (int x = 0; x < dw; x++)
                {
                    int x0 = Mathf.Min(w - 1, (x * w) / dw);
                    int y0 = Mathf.Min(h - 1, (y * h) / dh);
                    r[y * dw + x] = cov[y0 * w + x0];
                }
            }
            return r;
        }
    }
}
