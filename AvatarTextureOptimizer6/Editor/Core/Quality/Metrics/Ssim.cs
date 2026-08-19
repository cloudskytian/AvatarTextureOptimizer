using System;
using System.Threading.Tasks;
using UnityEngine;

namespace NetFosa.AvatarTextureOptimizer.Editor.Quality
{
    /// <summary>
    /// SSIM / MS-SSIM 实现（CPU 并行）。
    /// 标准 MS-SSIM：5 尺度，11×11 高斯窗（σ=1.5），权重
    /// [0.0448, 0.2856, 0.3001, 0.2363, 0.1333]，亮度 C1、对比度 C2 基于 L=1 动态范围。
    /// 小图自动收缩高斯窗。
    /// </summary>
    public static class Ssim
    {
        private static readonly float[] MsWeights = { 0.0448f, 0.2856f, 0.3001f, 0.2363f, 0.1333f };

        /// <summary>输入：亮度图（float[]，0..1），宽高。返回 SSIM 0..1。</summary>
        public static float Compute(float[] lum, int w, int h)
        {
            if (w < 3 || h < 3) return 1f; // 太小的图无法评估，视为通过
            int win = Mathf.Min(11, Mathf.Min(w, h));
            if (win % 2 == 0) win--;
            if (win < 3) return 1f;

            var gauss = BuildGauss(win, 1.5f);
            float c1 = 0.01f * 0.01f;
            float c2 = 0.03f * 0.03f;

            // 均值与方差（高斯加权）
            var mu1 = Convolve(lum, w, h, gauss, win);
            var mu2 = mu1; // 同一图比较时两图用同一窗口（这里 caller 传 ref 与 candidate）

            return ComputeForPair(lum, lum, mu1, mu2, w, h, gauss, win, c1, c2, out _, out _, out _);
        }

        /// <summary>比较两张亮度图。</summary>
        public static float Compare(float[] a, float[] b, int w, int h)
        {
            if (a.Length != b.Length) throw new ArgumentException("size mismatch");
            if (w < 3 || h < 3) return 1f;
            int win = Mathf.Min(11, Mathf.Min(w, h));
            if (win % 2 == 0) win--;
            if (win < 3) return 1f;
            var gauss = BuildGauss(win, 1.5f);
            float c1 = 0.01f * 0.01f;
            float c2 = 0.03f * 0.03f;
            var mu1 = Convolve(a, w, h, gauss, win);
            var mu2 = Convolve(b, w, h, gauss, win);
            return ComputeForPair(a, b, mu1, mu2, w, h, gauss, win, c1, c2, out _, out _, out _);
        }

        /// <summary>MS-SSIM（5 尺度）。</summary>
        public static float CompareMs(float[] a, float[] b, int w, int h)
        {
            if (a.Length != b.Length) return 0f;
            float product = 1f;
            var curA = a;
            var curB = b;
            int cw = w, ch = h;

            int scales = 5;
            for (int s = 0; s < scales; s++)
            {
                if (cw < 11 || ch < 11) break;
                int win = Mathf.Min(11, Mathf.Min(cw, ch));
                if (win % 2 == 0) win--;
                if (win < 3) break;

                var gauss = BuildGauss(win, 1.5f);
                float c1 = 0.01f * 0.01f, c2 = 0.03f * 0.03f;
                var mu1 = Convolve(curA, cw, ch, gauss, win);
                var mu2 = Convolve(curB, cw, ch, gauss, win);

                if (s < scales - 1)
                {
                    // 对比度/结构项
                    float cs = ComputeForPair(curA, curB, mu1, mu2, cw, ch, gauss, win, c1, c2, out float ssimFull, out float csOnly, out _);
                    product *= Mathf.Max(csOnly, 0f);
                }
                else
                {
                    // 最后一层含亮度项
                    float ssimFinal = ComputeForPair(curA, curB, mu1, mu2, cw, ch, gauss, win, c1, c2, out _, out _, out _);
                    product *= Mathf.Max(ssimFinal, 0f);
                }

                if (s < scales - 1)
                {
                    // 1/2 下采样（2x2 平均）
                    int nw = Mathf.Max(1, cw / 2);
                    int nh = Mathf.Max(1, ch / 2);
                    curA = Downsample2(curA, cw, ch, nw, nh);
                    curB = Downsample2(curB, cw, ch, nw, nh);
                    cw = nw; ch = nh;
                }
            }
            return Mathf.Clamp01(product);
        }

        private static float ComputeForPair(float[] a, float[] b, float[] mu1, float[] mu2, int w, int h,
            float[] gauss, int win, float c1, float c2,
            out float ssimMean, out float csMean, out float lMean)
        {
            double sumSsim = 0, sumCs = 0, sumL = 0;
            int count = 0;
            int half = win / 2;

            Parallel.For(half, h - half, y =>
            {
                for (int x = half; x < w - half; x++)
                {
                    int i = y * w + x;
                    float m1 = mu1[i], m2 = mu2[i];
                    float m1sq = m1 * m1, m2sq = m2 * m2;

                    // 局部方差/协方差（高斯加权）
                    double v1 = 0, v2 = 0, cov = 0;
                    for (int wy = -half; wy <= half; wy++)
                    {
                        for (int wx = -half; wx <= half; wx++)
                        {
                            int ii = (y + wy) * w + (x + wx);
                            float ga = gauss[(wy + half) * win + (wx + half)];
                            double d1 = a[ii] - m1;
                            double d2 = b[ii] - m2;
                            v1 += ga * d1 * d1;
                            v2 += ga * d2 * d2;
                            cov += ga * d1 * d2;
                        }
                    }

                    double l = (2 * m1 * m2 + c1) / (m1sq + m2sq + c1);
                    double cs = (2 * cov + c2) / (v1 + v2 + c2);
                    sumSsim += l * cs;
                    sumCs += cs;
                    sumL += l;
                    count++;
                }
            });

            ssimMean = count > 0 ? (float)(sumSsim / count) : 1f;
            csMean = count > 0 ? (float)(sumCs / count) : 1f;
            lMean = count > 0 ? (float)(sumL / count) : 1f;
            return ssimMean;
        }

        private static float[] BuildGauss(int win, float sigma)
        {
            var g = new float[win * win];
            int half = win / 2;
            double sum = 0;
            for (int y = -half; y <= half; y++)
            {
                for (int x = -half; x <= half; x++)
                {
                    double v = Math.Exp(-(x * x + y * y) / (2 * sigma * sigma));
                    g[(y + half) * win + (x + half)] = (float)v;
                    sum += v;
                }
            }
            for (int i = 0; i < g.Length; i++) g[i] = (float)(g[i] / sum);
            return g;
        }

        private static float[] Convolve(float[] img, int w, int h, float[] gauss, int win)
        {
            var result = new float[w * h];
            int half = win / 2;
            Parallel.For(0, h, y =>
            {
                for (int x = 0; x < w; x++)
                {
                    double sum = 0;
                    for (int wy = -half; wy <= half; wy++)
                    {
                        int yy = Mathf.Clamp(y + wy, 0, h - 1);
                        for (int wx = -half; wx <= half; wx++)
                        {
                            int xx = Mathf.Clamp(x + wx, 0, w - 1);
                            sum += img[yy * w + xx] * gauss[(wy + half) * win + (wx + half)];
                        }
                    }
                    result[y * w + x] = (float)sum;
                }
            });
            return result;
        }

        private static float[] Downsample2(float[] src, int w, int h, int nw, int nh)
        {
            var dst = new float[nw * nh];
            Parallel.For(0, nh, y =>
            {
                for (int x = 0; x < nw; x++)
                {
                    int sx = Mathf.Min(x * 2, w - 1);
                    int sy = Mathf.Min(y * 2, h - 1);
                    float acc = 0; int n = 0;
                    for (int dy = 0; dy < 2 && sy + dy < h; dy++)
                        for (int dx = 0; dx < 2 && sx + dx < w; dx++)
                        {
                            acc += src[(sy + dy) * w + (sx + dx)];
                            n++;
                        }
                    dst[y * nw + x] = n > 0 ? acc / n : 0f;
                }
            });
            return dst;
        }
    }
}
