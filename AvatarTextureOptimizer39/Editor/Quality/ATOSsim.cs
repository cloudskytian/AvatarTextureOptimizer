// Avatar Texture Optimizer (ATO)
// Bilingual comments: English + Simplified Chinese. 双语注释：英文 + 简体中文。

using System;
using UnityEngine;

namespace AvatarTextureOptimizer.Editor.Quality
{
    /// <summary>
    /// Structural similarity (SSIM) and multi-scale SSIM (MS-SSIM) reference
    /// implementations. Reference: Wang et al. (2003) "Multi-scale structural similarity
    /// for image quality assessment".
    ///
    /// SSIM 与多尺度 SSIM（MS-SSIM）参考实现。参考 Wang et al. (2003)。
    /// Note: reference implementation is clarity-first; the Burst/GPU accelerated path
    /// (ATOSsimBurst) mirrors this math. 参考实现以清晰为先；Burst/GPU 加速路径复刻此算法。
    /// </summary>
    public static class ATOSsim
    {
        private const float K1 = 0.01f;
        private const float K2 = 0.03f;
        private const float L = 1.0f;
        private const float C1 = (K1 * L) * (K1 * L);
        private const float C2 = (K2 * L) * (K2 * L);

        private static readonly float[] MSWeights = { 0.0448f, 0.2856f, 0.3001f, 0.2363f, 0.1333f };

        /// <summary>
        /// Single-scale SSIM (per-window local statistics, mean over image).
        /// 单尺度 SSIM（逐窗口局部统计，全图取平均）。
        /// </summary>
        public static float Ssim(float[] refImg, float[] testImg, int w, int h)
        {
            float[] win = GaussianWindow(11, 1.5f);
            int r = 5;

            double sum = 0;
            int count = 0;

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float mu1 = 0, mu2 = 0;
                    for (int wy = -r; wy <= r; wy++)
                    {
                        int yy = Mathf.Clamp(y + wy, 0, h - 1);
                        int rowBase = yy * w;
                        for (int wx = -r; wx <= r; wx++)
                        {
                            int xx = Mathf.Clamp(x + wx, 0, w - 1);
                            float wv = win[wy + r] * win[wx + r];
                            int idx = rowBase + xx;
                            mu1 += wv * refImg[idx];
                            mu2 += wv * testImg[idx];
                        }
                    }

                    float s1 = 0, s2 = 0, s12 = 0;
                    for (int wy = -r; wy <= r; wy++)
                    {
                        int yy = Mathf.Clamp(y + wy, 0, h - 1);
                        int rowBase = yy * w;
                        for (int wx = -r; wx <= r; wx++)
                        {
                            int xx = Mathf.Clamp(x + wx, 0, w - 1);
                            float wv = win[wy + r] * win[wx + r];
                            int idx = rowBase + xx;
                            float d1 = refImg[idx] - mu1;
                            float d2 = testImg[idx] - mu2;
                            s1 += wv * d1 * d1;
                            s2 += wv * d2 * d2;
                            s12 += wv * d1 * d2;
                        }
                    }

                    sum += (2f * mu1 * mu2 + C1) * (2f * s12 + C2) /
                           ((mu1 * mu1 + mu2 * mu2 + C1) * (s1 + s2 + C2));
                    count++;
                }
            }

            return count == 0 ? 1f : Mathf.Clamp01((float)(sum / count));
        }

        /// <summary>
        /// Multi-scale SSIM over a single channel.
        /// 单通道多尺度 SSIM。
        /// </summary>
        public static float MsSsim(float[] refImg, float[] testImg, int w, int h)
        {
            int scales = 1, ww = w, hh = h;
            while (ww >= 16 && hh >= 16 && scales < 5) { ww /= 2; hh /= 2; scales++; }

            if (scales <= 1) return Ssim(refImg, testImg, w, h);

            float[] curRef = refImg, curTest = testImg;
            int cw = w, ch = h;
            float total = 0f;

            for (int s = 0; s < scales; s++)
            {
                if (s == scales - 1)
                {
                    total += MSWeights[s] * Ssim(curRef, curTest, cw, ch);
                }
                else
                {
                    // Contrast-structure term: SSIM without luminance.
                    // 对比度-结构项：不含亮度的 SSIM。
                    total += MSWeights[s] * Cs(curRef, curTest, cw, ch);
                    var (nr, nt) = Downsample(curRef, curTest, cw, ch);
                    curRef = nr; curTest = nt;
                    cw = Mathf.Max(1, cw / 2); ch = Mathf.Max(1, ch / 2);
                }
            }

            return Mathf.Clamp01(total);
        }

        /// <summary>
        /// Single-scale SSIM across RGB (per-channel average). RGB 逐通道平均单尺度 SSIM。
        /// </summary>
        public static float SsimRgb(Color[] refPix, Color[] testPix, int w, int h)
        {
            int n = w * h;
            var r1 = new float[n]; var g1 = new float[n]; var b1 = new float[n];
            var r2 = new float[n]; var g2 = new float[n]; var b2 = new float[n];
            for (int i = 0; i < n; i++)
            {
                r1[i] = refPix[i].r; g1[i] = refPix[i].g; b1[i] = refPix[i].b;
                r2[i] = testPix[i].r; g2[i] = testPix[i].g; b2[i] = testPix[i].b;
            }
            return (Ssim(r1, r2, w, h) + Ssim(g1, g2, w, h) + Ssim(b1, b2, w, h)) / 3f;
        }

        /// <summary>
        /// MS-SSIM across RGB (per-channel average). RGB 逐通道平均 MS-SSIM。
        /// </summary>
        public static float MsSsimRgb(Color[] refPix, Color[] testPix, int w, int h)
        {
            int n = w * h;
            var r1 = new float[n]; var g1 = new float[n]; var b1 = new float[n];
            var r2 = new float[n]; var g2 = new float[n]; var b2 = new float[n];
            for (int i = 0; i < n; i++)
            {
                r1[i] = refPix[i].r; g1[i] = refPix[i].g; b1[i] = refPix[i].b;
                r2[i] = testPix[i].r; g2[i] = testPix[i].g; b2[i] = testPix[i].b;
            }

            float sr = MsSsim(r1, r2, w, h);
            float sg = MsSsim(g1, g2, w, h);
            float sb = MsSsim(b1, b2, w, h);
            return (sr + sg + sb) / 3f;
        }

        /// <summary>Contrast-structure (no luminance) term, per-window mean. 对比度-结构项。</summary>
        private static float Cs(float[] a, float[] b, int w, int h)
        {
            float[] win = GaussianWindow(11, 1.5f);
            int r = 5;
            double sum = 0; int count = 0;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float mu1 = 0, mu2 = 0;
                    for (int wy = -r; wy <= r; wy++)
                    {
                        int yy = Mathf.Clamp(y + wy, 0, h - 1); int rowBase = yy * w;
                        for (int wx = -r; wx <= r; wx++)
                        {
                            int xx = Mathf.Clamp(x + wx, 0, w - 1);
                            float wv = win[wy + r] * win[wx + r];
                            int idx = rowBase + xx;
                            mu1 += wv * a[idx]; mu2 += wv * b[idx];
                        }
                    }
                    float s1 = 0, s2 = 0, s12 = 0;
                    for (int wy = -r; wy <= r; wy++)
                    {
                        int yy = Mathf.Clamp(y + wy, 0, h - 1); int rowBase = yy * w;
                        for (int wx = -r; wx <= r; wx++)
                        {
                            int xx = Mathf.Clamp(x + wx, 0, w - 1);
                            float wv = win[wy + r] * win[wx + r];
                            int idx = rowBase + xx;
                            float d1 = a[idx] - mu1, d2 = b[idx] - mu2;
                            s1 += wv * d1 * d1; s2 += wv * d2 * d2; s12 += wv * d1 * d2;
                        }
                    }
                    sum += (2f * s12 + C2) / (s1 + s2 + C2);
                    count++;
                }
            }
            return count == 0 ? 1f : Mathf.Clamp01((float)(sum / count));
        }

        private static (float[], float[]) Downsample(float[] a, float[] b, int w, int h)
        {
            int nw = Mathf.Max(1, w / 2), nh = Mathf.Max(1, h / 2);
            var na = new float[nw * nh]; var nb = new float[nw * nh];
            for (int y = 0; y < nh; y++)
            for (int x = 0; x < nw; x++)
            {
                int x0 = x * 2, y0 = y * 2;
                float sa = 0, sb = 0; int cnt = 0;
                for (int dy = 0; dy < 2; dy++)
                for (int dx = 0; dx < 2; dx++)
                {
                    int xx = Mathf.Min(x0 + dx, w - 1);
                    int yy = Mathf.Min(y0 + dy, h - 1);
                    int idx = yy * w + xx;
                    sa += a[idx]; sb += b[idx]; cnt++;
                }
                na[y * nw + x] = sa / cnt;
                nb[y * nw + x] = sb / cnt;
            }
            return (na, nb);
        }

        private static float[] GaussianWindow(int size, float sigma)
        {
            var w = new float[size];
            int r = size / 2; float sum = 0;
            for (int i = 0; i < size; i++)
            {
                float x = i - r;
                w[i] = Mathf.Exp(-(x * x) / (2f * sigma * sigma));
                sum += w[i];
            }
            for (int i = 0; i < size; i++) w[i] /= sum;
            return w;
        }
    }
}
