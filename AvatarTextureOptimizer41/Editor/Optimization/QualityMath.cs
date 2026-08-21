using System;

// Pure C# quality-metric math. NO Unity dependencies — compiles in Unity and in the dotnet test harness.
// 纯 C# 质量指标数学。不依赖 Unity —— 可在 Unity 与 dotnet 单测中编译。
//
// All pixel values are floats in [0,1] LINEAR space unless noted (the Unity pipeline decodes to linear RGBA32).
// 所有像素值为 [0,1] 线性空间浮点（Unity 管线解码为线性 RGBA32）——除非另有说明。

namespace Net.Fosa.AvatarTextureOptimizer.Pure
{
    public static class QualityMath
    {
        // ---------------- Color space / color difference. 色彩空间与色差。 ----------------

        public static float SrgbToLinear(float c) => c <= 0.04045f ? c / 12.92f : (float)Math.Pow((c + 0.055) / 1.055, 2.4);
        public static float LinearToSrgb(float c) => c <= 0.0031308f ? 12.92f * c : 1.055f * (float)Math.Pow(c, 1f / 2.4f) - 0.055f;

        /// <summary>sRGB-linear RGB (0..1) -> CIE Lab (D65). sRGB 线性 RGB → CIE Lab（D65）。</summary>
        public static void RgbToLab(float r, float g, float b, out float L, out float a, out float bb)
        {
            // Linear RGB -> XYZ (D65). 线性 RGB → XYZ（D65）。
            float x = 0.4124564f * r + 0.3575761f * g + 0.1804375f * b;
            float y = 0.2126729f * r + 0.7151522f * g + 0.0721750f * b;
            float z = 0.0193339f * r + 0.1191920f * g + 0.9503041f * b;
            const float xn = 0.95047f, yn = 1.0f, zn = 1.08883f;
            float fx = Fxyz(x / xn), fy = Fxyz(y / yn), fz = Fxyz(z / zn);
            L = 116f * fy - 16f;
            a = 500f * (fx - fy);
            bb = 200f * (fy - fz);
        }
        private static float Fxyz(float t) => t > 0.008856f ? (float)Math.Pow(t, 1f / 3f) : 7.787f * t + 16f / 116f;

        /// <summary>
        /// CIEDE2000 color difference (Sharma-Wu-Dalal 2005). 0..~100 scale; JND ~1.0-2.3.
        /// Verified against the official supplementary test data (2.0425 / 2.8615 / 3.4412 / ...).
        /// CIEDE2000 色差（Sharma-Wu-Dalal 2005）。JND≈1.0-2.3。已对照官方补充测试数据验证。
        /// </summary>
        public static double DeltaE2000(double L1, double a1, double b1, double L2, double a2, double b2)
        {
            const double kL = 1.0, kC = 1.0, kH = 1.0;
            const double deg360 = Math.PI * 2.0, deg180 = Math.PI;
            const double pow25To7 = 6103515625.0; // pow(25,7). 25 的 7 次方。

            // Step 1. 第一步。
            double c1 = Math.Sqrt(a1 * a1 + b1 * b1);
            double c2 = Math.Sqrt(a2 * a2 + b2 * b2);
            double barC = (c1 + c2) / 2.0;
            double g = 0.5 * (1 - Math.Sqrt(Math.Pow(barC, 7) / (Math.Pow(barC, 7) + pow25To7)));
            double a1p = (1 + g) * a1, a2p = (1 + g) * a2;
            double c1p = Math.Sqrt(a1p * a1p + b1 * b1);
            double c2p = Math.Sqrt(a2p * a2p + b2 * b2);
            double h1p = HueRad(b1, a1p), h2p = HueRad(b2, a2p);

            // Step 2. 第二步。
            double dLp = L2 - L1;
            double dCp = c2p - c1p;
            double dhp;
            double cpProd = c1p * c2p;
            if (cpProd == 0) dhp = 0;
            else
            {
                dhp = h2p - h1p;
                if (dhp < -deg180) dhp += deg360;
                else if (dhp > deg180) dhp -= deg360;
            }
            // Equation 11: ΔH′ = 2·sqrt(C1'C2')·sin(Δh'/2). 公式 11。
            double dHp = 2.0 * Math.Sqrt(cpProd) * Math.Sin(dhp / 2.0);

            // Step 3. 第三步。
            double barLp = (L1 + L2) / 2.0;
            double barCp = (c1p + c2p) / 2.0;
            double barhp, hSum = h1p + h2p;
            if (cpProd == 0) barhp = hSum;
            else if (Math.Abs(h1p - h2p) <= deg180) barhp = hSum / 2.0;
            else barhp = hSum < deg360 ? (hSum + deg360) / 2.0 : (hSum - deg360) / 2.0;

            double t = 1.0 - 0.17 * Math.Cos(barhp - Math.PI / 6.0)
                       + 0.24 * Math.Cos(2.0 * barhp)
                       + 0.32 * Math.Cos(3.0 * barhp + Math.PI / 30.0)
                       - 0.20 * Math.Cos(4.0 * barhp - 63.0 * Math.PI / 180.0);
            double deltaTheta = (Math.PI / 6.0) * Math.Exp(-Math.Pow((barhp - 275.0 * Math.PI / 180.0) / (25.0 * Math.PI / 180.0), 2.0));
            double rC = 2.0 * Math.Sqrt(Math.Pow(barCp, 7) / (Math.Pow(barCp, 7) + pow25To7));
            double sL = 1 + 0.015 * Math.Pow(barLp - 50.0, 2) / Math.Sqrt(20 + Math.Pow(barLp - 50.0, 2));
            double sC = 1 + 0.045 * barCp;
            double sH = 1 + 0.015 * barCp * t;
            double rT = -Math.Sin(2.0 * deltaTheta) * rC;

            double dL = dLp / (kL * sL), dC = dCp / (kC * sC), dH = dHp / (kH * sH);
            return Math.Sqrt(dL * dL + dC * dC + dH * dH + rT * dC * dH);
        }
        private static double HueRad(double b, double ap)
        {
            if (ap == 0 && b == 0) return 0;
            double h = Math.Atan2(b, ap);
            return h < 0 ? h + Math.PI * 2.0 : h;
        }

        // ---------------- SSIM / MS-SSIM. ----------------

        private static readonly float[] GaussianWindow = BuildGaussianWindow(11, 1.5f);

        private static float[] BuildGaussianWindow(int size, float sigma)
        {
            var w = new float[size * size];
            float s2 = 2 * sigma * sigma;
            float sum = 0;
            int c = size / 2;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float d2 = (x - c) * (x - c) + (y - c) * (y - c);
                    w[y * size + x] = (float)Math.Exp(-d2 / s2);
                    sum += w[y * size + x];
                }
            for (int i = 0; i < w.Length; i++) w[i] /= sum;
            return w;
        }

        /// <summary>Reflect-padded sample. 反射填充采样。</summary>
        private static float Sample(float[] a, int w, int h, int x, int y)
        {
            if (x < 0) x = -x; else if (x >= w) x = 2 * w - 2 - x;
            if (y < 0) y = -y; else if (y >= h) y = 2 * h - 2 - y;
            if (x < 0) x = 0; if (x >= w) x = w - 1;
            if (y < 0) y = 0; if (y >= h) y = h - 1;
            return a[y * w + x];
        }

        /// <summary>
        /// Single-scale luminance SSIM (Wang et al. 2004), 11x11 Gaussian window, K1=0.01, K2=0.03, L=1.0.
        /// Inputs are linear-luminance arrays of length w*h.
        /// 单尺度亮度 SSIM（11×11 高斯窗）。输入为线性亮度数组。
        /// </summary>
        public static double SSIM(float[] a, float[] b, int w, int h)
        {
            const double C1 = 0.01 * 0.01, C2 = 0.03 * 0.03;
            int ws = 11, c = ws / 2;
            double sum = 0; long count = 0;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    double mu1 = 0, mu2 = 0;
                    for (int wy = -c; wy <= c; wy++)
                        for (int wx = -c; wx <= c; wx++)
                        {
                            float gw = GaussianWindow[(wy + c) * ws + (wx + c)];
                            mu1 += gw * Sample(a, w, h, x + wx, y + wy);
                            mu2 += gw * Sample(b, w, h, x + wx, y + wy);
                        }
                    double s11 = 0, s22 = 0, s12 = 0;
                    for (int wy = -c; wy <= c; wy++)
                        for (int wx = -c; wx <= c; wx++)
                        {
                            float gw = GaussianWindow[(wy + c) * ws + (wx + c)];
                            double d1 = Sample(a, w, h, x + wx, y + wy) - mu1;
                            double d2 = Sample(b, w, h, x + wx, y + wy) - mu2;
                            s11 += gw * d1 * d1;
                            s22 += gw * d2 * d2;
                            s12 += gw * d1 * d2;
                        }
                    double ssim = ((2 * mu1 * mu2 + C1) * (2 * s12 + C2)) /
                                  ((mu1 * mu1 + mu2 * mu2 + C1) * (s11 + s22 + C2));
                    sum += ssim; count++;
                }
            }
            return count > 0 ? sum / count : 1.0;
        }

        /// <summary>2x2 block-average downsample (floor). 2×2 块平均降采样（向下取整）。</summary>
        public static void Downsample2x(float[] src, int w, int h, float[] dst, int dw, int dh)
        {
            for (int y = 0; y < dh; y++)
                for (int x = 0; x < dw; x++)
                {
                    float s = 0; int n = 0;
                    for (int dy = 0; dy < 2; dy++)
                        for (int dx = 0; dx < 2; dx++)
                        {
                            int sx = x * 2 + dx, sy = y * 2 + dy;
                            if (sx < w && sy < h) { s += src[sy * w + sx]; n++; }
                        }
                    dst[y * dw + x] = n > 0 ? s / n : 0f;
                }
        }

        /// <summary>
        /// MS-SSIM (Wang-Simoncelli-Bovik 2003), 5 scales, weights [0.0448,0.2856,0.3001,0.2363,0.1333].
        /// Short-edge &lt; 176px should use SSIM (single scale) instead; short-edge &lt; 11px should be skipped entirely.
        /// 5 尺度 MS-SSIM。短边<176px 应改用单尺度 SSIM；短边<11px 应整体跳过。
        /// </summary>
        public static double MSSSIM(float[] a, float[] b, int w, int h, int levels = 5)
        {
            double[] weights = { 0.0448, 0.2856, 0.3001, 0.2363, 0.1333 };
            const double C1 = 0.01 * 0.01, C2 = 0.03 * 0.03;
            int ws = 11, c = ws / 2;

            // Luminance term is folded into csProd at the coarsest scale. 亮度项在最粗尺度并入 csProd。
            double csProd = 1.0;

            var curA = a; var curB = b;
            int cw = w, ch = h;

            for (int level = 0; level < levels && cw >= ws && ch >= ws; level++)
            {
                double l = 0, cs = 0; long cnt = 0;
                for (int y = 0; y < ch; y++)
                {
                    for (int x = 0; x < cw; x++)
                    {
                        double mu1 = 0, mu2 = 0;
                        for (int wy = -c; wy <= c; wy++)
                            for (int wx = -c; wx <= c; wx++)
                            {
                                float gw = GaussianWindow[(wy + c) * ws + (wx + c)];
                                mu1 += gw * Sample(curA, cw, ch, x + wx, y + wy);
                                mu2 += gw * Sample(curB, cw, ch, x + wx, y + wy);
                            }
                        double s11 = 0, s22 = 0, s12 = 0;
                        for (int wy = -c; wy <= c; wy++)
                            for (int wx = -c; wx <= c; wx++)
                            {
                                float gw = GaussianWindow[(wy + c) * ws + (wx + c)];
                                double d1 = Sample(curA, cw, ch, x + wx, y + wy) - mu1;
                                double d2 = Sample(curB, cw, ch, x + wx, y + wy) - mu2;
                                s11 += gw * d1 * d1; s22 += gw * d2 * d2; s12 += gw * d1 * d2;
                            }
                        if (level == levels - 1 || cw < ws * 2 || ch < ws * 2)
                        {
                            // Luminance only measured at the final (coarsest) scale. 亮度项仅在最终（最粗）尺度测量。
                            l += (2 * mu1 * mu2 + C1) / (mu1 * mu1 + mu2 * mu2 + C1);
                        }
                        cs += (2 * s12 + C2) / (s11 + s22 + C2);
                        cnt++;
                    }
                }
                double avgL = l / Math.Max(1, cnt);
                double avgCs = cs / Math.Max(1, cnt);
                if (level == levels - 1 || cw < ws * 2 || ch < ws * 2)
                {
                    // Final scale: multiply luminance (weighted by last weight). 最终尺度：乘入亮度项。
                    csProd *= Math.Pow(avgL, weights[Math.Min(level, weights.Length - 1)]);
                }
                csProd *= Math.Pow(avgCs, weights[Math.Min(level, weights.Length - 1)]);

                // Downsample for next scale. 为下一尺度降采样。
                if (level < levels - 1)
                {
                    int nw = Math.Max(1, cw / 2), nh = Math.Max(1, ch / 2);
                    var na = new float[nw * nh]; var nb = new float[nw * nh];
                    Downsample2x(curA, cw, ch, na, nw, nh);
                    Downsample2x(curB, cw, ch, nb, nw, nh);
                    curA = na; curB = nb; cw = nw; ch = nh;
                }
            }
            return csProd;
        }

        // ---------------- Alpha / coverage / normal / gray. ----------------

        /// <summary>Cutout coverage IoU after clipping at cutoff: |A∩B|/|A∪B| on binary masks. Cutout 覆盖率 IoU（按 cutoff 裁剪后）。</summary>
        public static double CoverageIoU(float[] alphaA, float[] alphaB, int w, int h, float cutoff)
        {
            long inter = 0, union = 0;
            for (int i = 0; i < w * h; i++)
            {
                bool a = alphaA[i] > cutoff, b = alphaB[i] > cutoff;
                if (a && b) inter++;
                if (a || b) union++;
            }
            return union > 0 ? (double)inter / union : 1.0;
        }

        /// <summary>Linear alpha RMSE for Blend mode. Blend 模式线性 alpha RMSE。</summary>
        public static double AlphaRMSE(float[] alphaA, float[] alphaB, int n)
        {
            double s = 0;
            for (int i = 0; i < n; i++) { double d = alphaA[i] - alphaB[i]; s += d * d; }
            return Math.Sqrt(s / Math.Max(1, n));
        }

        /// <summary>
        /// p95 normal angle error in degrees. Inputs are unit-vector buffers (x,y,z per pixel).
        /// 法线角度误差 p95（度）。输入为单位向量缓冲（每像素 x,y,z）。
        /// </summary>
        public static double NormalAngleErrorP95(float[] na, float[] nb, int n)
        {
            var errs = new double[n];
            for (int i = 0; i < n; i++)
            {
                float ax = na[i * 3], ay = na[i * 3 + 1], az = na[i * 3 + 2];
                float bx = nb[i * 3], by = nb[i * 3 + 1], bz = nb[i * 3 + 2];
                double dot = ax * bx + ay * by + az * bz;
                dot = Math.Max(-1.0, Math.Min(1.0, dot));
                errs[i] = Math.Acos(dot) * 180.0 / Math.PI;
            }
            Array.Sort(errs);
            int idx = (int)Math.Ceiling(0.95 * n) - 1;
            if (idx < 0) idx = 0;
            return errs[idx];
        }

        /// <summary>RMSE over one channel pair. 单通道 RMSE。</summary>
        public static double ChannelRMSE(float[] a, float[] b, int n)
        {
            double s = 0;
            for (int i = 0; i < n; i++) { double d = a[i] - b[i]; s += d * d; }
            return Math.Sqrt(s / Math.Max(1, n));
        }

        /// <summary>Worst (max) RMSE across the given channels. 多通道取最差（最大）RMSE。</summary>
        public static double WorstChannelRMSE(float[] a, float[] b, int n, int channelCount)
        {
            double worst = 0;
            for (int c = 0; c < channelCount; c++)
            {
                double s = 0;
                for (int i = 0; i < n; i++) { double d = a[i * channelCount + c] - b[i * channelCount + c]; s += d * d; }
                double rmse = Math.Sqrt(s / Math.Max(1, n));
                if (rmse > worst) worst = rmse;
            }
            return worst;
        }

        /// <summary>True if every channel is (near-)uniform across the buffer. 各通道是否（近似）均匀。</summary>
        public static bool IsUniform(float[] rgba, int n, int channelCount, float epsilon = 1e-3f)
        {
            for (int c = 0; c < channelCount; c++)
            {
                float min = float.MaxValue, max = float.MinValue;
                for (int i = 0; i < n; i++)
                {
                    float v = rgba[i * channelCount + c];
                    if (v < min) min = v;
                    if (v > max) max = v;
                }
                if (max - min > epsilon) return false;
            }
            return true;
        }
    }
}
