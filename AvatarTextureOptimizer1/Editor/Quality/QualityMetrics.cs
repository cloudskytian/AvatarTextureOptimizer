// QualityMetrics.cs / QualityMetrics.cs
// Quality metric computations. We implement reference CPU versions; GPU/ComputeShader versions
// can be added later via the GPUUtility helper.
// 质量指标计算。这里实现参考CPU版本；GPU/ComputeShader版本后续可以通过GPUUtility添加。
//
// Metrics (all on linear-space data unless noted):
//   - MS-SSIM (multi-scale SSIM) on luma channel
//   - Single-scale SSIM fallback for small islands
//   - CIEDE2000 ΔE average worst-percentile
//   - Normal map angular error (decode, renormalize, encode, compare, p95)
//   - Alpha RMSE for Blend, contour IoU for Cutout
//   - Grayscale per-channel RMSE (worst channel)
//
// 指标（除非注明，全部在线性空间数据上计算）：
//   - 亮度通道上的MS-SSIM
//   - 小岛回退到单尺度SSIM
//   - CIEDE2000 ΔE平均/差百分位
//   - 法线贴图角度误差（解码、重归一化、编码、比较、p95）
//   - Blend模式alpha RMSE，Cutout轮廓IoU
//   - 灰度逐通道RMSE（最差通道）

using System;
using UnityEngine;
using net.fosa.avatar_texture_optimizer.Editor.Util;

namespace net.fosa.avatar_texture_optimizer.Editor.Quality
{
    public static class QualityMetrics
    {
        private const float K1 = 0.01f, K2 = 0.03f;

        /// <summary>
        /// Compute SSIM between two luma windows. Used by MS-SSIM.
        /// 计算两个亮度窗口之间的SSIM。被MS-SSIM使用。
        /// </summary>
        public static float SSIM(float[] mu1, float[] mu2, float[] sigma1_sq, float[] sigma2_sq, float[] sigma12, float L = 1f)
        {
            float c1 = (K1 * L) * (K1 * L);
            float c2 = (K2 * L) * (K2 * L);
            float num = (2 * mu1[0] * mu2[0] + c1) * (2 * sigma12[0] + c2);
            float den = (mu1[0] * mu1[0] + mu2[0] * mu2[0] + c1) * (sigma1_sq[0] + sigma2_sq[0] + c2);
            return num / den;
        }

        /// <summary>
        /// Compute single-scale SSIM between two equal-sized linear-color patches (float arrays, RGB as 3*N or RGBA).
        /// Uses luma only (Rec.709).
        /// 计算两个等大线性颜色块（float数组，RGB=3*N或RGBA）的单尺度SSIM。只使用亮度(Rec.709)。
        /// </summary>
        public static float SingleScaleSSIM(Color[] a, Color[] b)
        {
            int n = a.Length;
            if (n != b.Length || n < 1) return 1f;

            float meanA = 0, meanB = 0;
            for (int i = 0; i < n; i++) { meanA += Luma(a[i]); meanB += Luma(b[i]); }
            meanA /= n; meanB /= n;

            float vA = 0, vB = 0, cov = 0;
            for (int i = 0; i < n; i++)
            {
                float la = Luma(a[i]) - meanA;
                float lb = Luma(b[i]) - meanB;
                vA += la * la; vB += lb * lb; cov += la * lb;
            }
            vA /= (n - 1); vB /= (n - 1); cov /= (n - 1);

            float c1 = 0.01f * 0.01f;
            float c2 = 0.03f * 0.03f;
            float ssim = ((2 * meanA * meanB + c1) * (2 * cov + c2)) /
                         ((meanA * meanA + meanB * meanB + c1) * (vA + vB + c2));
            return Mathf.Clamp01(ssim);
        }

        /// <summary>
        /// Multi-scale SSIM, computing SSIM across 5 scales using iterated 2x2 box downsample.
        /// For patches with short side < 176px, falls back to single-scale SSIM; < 11px returns 1.
        /// 多尺度SSIM，使用2x2盒下采样跨5个尺度计算SSIM。
        /// 短边<176px回退单尺度；<11px直接返回1。
        /// </summary>
        public static float MSSSIM(Color[] src, Color[] cmp, int w, int h)
        {
            int shortSide = Mathf.Min(w, h);
            if (shortSide < 11) return 1f;
            if (shortSide < 176) return SingleScaleSSIM(src, cmp);

            const int scales = 5;
            float[] weights = { 0.0448f, 0.2856f, 0.3001f, 0.2363f, 0.1333f };
            float[] mssim = new float[scales];
            Color[] s = (Color[])src.Clone(), c = (Color[])cmp.Clone();
            int sw = w, sh = h;
            for (int i = 0; i < scales; i++)
            {
                mssim[i] = SingleScaleSSIM(s, c);
                if (i < scales - 1)
                {
                    Downsample2x(s, sw, sh, out s, out int nw, out int nh);
                    Downsample2x(c, sw, sh, out c, out _, out _);
                    sw = nw; sh = nh;
                }
            }
            float prod = 1f;
            for (int i = 0; i < scales; i++) prod *= Mathf.Pow(Mathf.Max(mssim[i], 1e-6f), weights[i]);
            return Mathf.Clamp01(prod);
        }

        private static float Luma(Color c)
        {
            // Rec.709 luma / Rec.709亮度
            return 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;
        }

        private static void Downsample2x(Color[] src, int w, int h, out Color[] dst, out int nw, out int nh)
        {
            nw = Mathf.Max(1, w / 2); nh = Mathf.Max(1, h / 2);
            dst = new Color[nw * nh];
            for (int y = 0; y < nh; y++)
            for (int x = 0; x < nw; x++)
            {
                int x0 = x * 2, y0 = y * 2;
                Color a = src[y0 * w + x0];
                Color b = (x0+1 < w) ? src[y0 * w + x0+1] : a;
                Color d = (y0+1 < h) ? src[(y0+1) * w + x0] : a;
                Color e = (x0+1 < w && y0+1 < h) ? src[(y0+1) * w + x0+1] : a;
                dst[y * nw + x] = (a + b + d + e) * 0.25f;
            }
        }

        /// <summary>
        /// Average CIEDE2000 ΔE between two linear-color patches.
        /// 两个线性颜色块之间的平均CIEDE2000 ΔE。
        /// </summary>
        public static float AvgDeltaE(Color[] a, Color[] b)
        {
            int n = Mathf.Min(a.Length, b.Length);
            if (n < 1) return 0;
            float sum = 0;
            for (int i = 0; i < n; i++)
            {
                MathUtility.LinearRGBToLab(a[i].r, a[i].g, a[i].b, out float L1, out float A1, out float B1);
                MathUtility.LinearRGBToLab(b[i].r, b[i].g, b[i].b, out float L2, out float A2, out float B2);
                sum += MathUtility.CIEDE2000(L1, A1, B1, L2, A2, B2);
            }
            return sum / n;
        }

        /// <summary>
        /// P95 angular error (degrees) between two normal maps (in texture space 0..1 encoded).
        /// 两个法线贴图之间的P95角度误差（度）（贴图空间0..1编码）。
        /// </summary>
        public static float P95NormalAngle(Color[] a, Color[] b, bool useDXTnm = false)
        {
            int n = Mathf.Min(a.Length, b.Length);
            if (n < 1) return 0;
            var angles = new float[n];
            for (int i = 0; i < n; i++)
            {
                var na = MathUtility.DecodeNormal(a[i], useDXTnm);
                var nb = MathUtility.DecodeNormal(b[i], useDXTnm);
                angles[i] = MathUtility.AngleDeg(na, nb);
            }
            Array.Sort(angles);
            int p95idx = Mathf.Min(n - 1, (int)(n * 0.95f));
            return angles[p95idx];
        }

        /// <summary>
        /// Linear-space RMSE on alpha channel (for Blend mode).
        /// Alpha通道线性空间RMSE（用于Blend模式）。
        /// </summary>
        public static float AlphaRMSE(Color[] a, Color[] b)
        {
            int n = Mathf.Min(a.Length, b.Length);
            if (n < 1) return 0;
            double s = 0;
            for (int i = 0; i < n; i++)
            {
                float d = a[i].a - b[i].a;
                s += d * d;
            }
            return Mathf.Sqrt((float)(s / n));
        }

        /// <summary>
        /// Intersection over Union of alpha>cutoff masks (for Cutout mode).
        /// alpha>cutoff掩码的IoU（用于Cutout模式）。
        /// </summary>
        public static float CutoutIoU(Color[] a, Color[] b, float cutoff)
        {
            int n = Mathf.Min(a.Length, b.Length);
            if (n < 1) return 1f;
            int inter = 0, uni = 0;
            for (int i = 0; i < n; i++)
            {
                bool ba = a[i].a > cutoff;
                bool bb = b[i].a > cutoff;
                if (ba || bb) uni++;
                if (ba && bb) inter++;
            }
            return uni == 0 ? 1f : (float)inter / uni;
        }

        /// <summary>
        /// Per-channel RMSE on grayscale maps; returns worst channel.
        /// 灰度图逐通道RMSE；返回最差通道。
        /// </summary>
        public static float GrayscaleWorstRMSE(Color[] a, Color[] b)
        {
            int n = Mathf.Min(a.Length, b.Length);
            if (n < 1) return 0;
            double rr = 0, gg = 0, bb = 0;
            for (int i = 0; i < n; i++)
            {
                float dr = a[i].r - b[i].r;
                float dg = a[i].g - b[i].g;
                float db = a[i].b - b[i].b;
                rr += dr * dr; gg += dg * dg; bb += db * db;
            }
            float mr = Mathf.Sqrt((float)(rr / n));
            float mg = Mathf.Sqrt((float)(gg / n));
            float mb = Mathf.Sqrt((float)(bb / n));
            return Mathf.Max(mr, mg, mb);
        }
    }
}
