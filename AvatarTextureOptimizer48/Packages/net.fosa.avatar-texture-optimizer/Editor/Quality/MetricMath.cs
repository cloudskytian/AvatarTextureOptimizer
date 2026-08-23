// Perceptual quality metrics used by the island scaler.
// / 岛缩放使用的感知质量指标。
// Implementations follow the standard literature:
//  - SSIM: Wang et al. 2004 (Gaussian 11x11 window, C1/C2 = 0.01/0.03)
//  - MS-SSIM: Wang et al. 2003 (5 scales, luminance on last scale)
//  - CIEDE2000: Sharma et al. 2005
//  - normal angle error, alpha IoU / RMSE, per-channel grayscale RMSE
// / 实现遵循标准文献：SSIM（Wang 2004，高斯 11x11 窗）、MS-SSIM（Wang 2003，5 尺度）、
// CIEDE2000（Sharma 2005）、法线角度误差、alpha IoU/RMSE、灰度逐通道 RMSE。

using System;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor.quality
{
    /// <summary>
    /// Pure math for perceptual metrics. All color inputs are float arrays in [0,1].
    /// / 感知指标的纯数学实现。颜色输入为 [0,1] 的浮点数组。
    /// </summary>
    public static class MetricMath
    {
        // ===== sRGB <-> linear helpers / sRGB 与线性互转 =====
        private static readonly float[] SrgbToLinearLut = new float[256];
        private static readonly float[] LinearToSrgbLut = new float[65536];

        static MetricMath()
        {
            for (int i = 0; i < 256; i++)
            {
                float c = i / 255f;
                SrgbToLinearLut[i] = c <= 0.04045f ? c / 12.92f : Mathf.Pow((c + 0.055f) / 1.055f, 2.4f);
            }
            for (int i = 0; i < 65536; i++)
            {
                float c = i / 65535f;
                LinearToSrgbLut[i] = c <= 0.0031308f ? c * 12.92f : 1.055f * Mathf.Pow(c, 1f / 2.4f) - 0.055f;
            }
        }

        public static float SrgbToLinear(float s) => s <= 0.04045f ? s / 12.92f : Mathf.Pow((s + 0.055f) / 1.055f, 2.4f);

        /// <summary>Convert an sRGB byte to linear float. / sRGB 字节转线性浮点。</summary>
        public static float SrgbByteToLinear(byte b) => SrgbToLinearLut[b];

        // ===== SSIM =====
        private static float[] _gaussWeights;
        private static readonly object GaussLock = new object();

        private static float[] GaussWeights(int size, float sigma)
        {
            if (_gaussWeights != null && _gaussWeights.Length == size) return _gaussWeights;
            lock (GaussLock)
            {
                if (_gaussWeights != null && _gaussWeights.Length == size) return _gaussWeights;
                var w = new float[size];
                float sum = 0;
                int half = size / 2;
                for (int i = 0; i < size; i++)
                {
                    float d = i - half;
                    w[i] = Mathf.Exp(-(d * d) / (2f * sigma * sigma));
                    sum += w[i];
                }
                for (int i = 0; i < size; i++) w[i] /= sum;
                _gaussWeights = w;
                return w;
            }
        }

        /// <summary>
        /// Compute SSIM on luma of two images. Images are RGB interleaved (r,g,b per pixel) in [0,1] linear.
        /// / 计算两张图像亮度的 SSIM。图像为 RGB 交错（每像素 r,g,b），线性空间 [0,1]。
        /// </summary>
        public static float Ssim(float[] refImg, float[] testImg, int w, int h)
        {
            const float C1 = 0.01f * 0.01f * 255f * 255f;
            const float C2 = 0.03f * 0.03f * 255f * 255f;
            const int win = 11;

            float[] lRef = new float[w * h];
            float[] lTest = new float[w * h];
            for (int i = 0; i < w * h; i++)
            {
                lRef[i] = 0.299f * refImg[i * 3] + 0.587f * refImg[i * 3 + 1] + 0.114f * refImg[i * 3 + 2];
                lTest[i] = 0.299f * testImg[i * 3] + 0.587f * testImg[i * 3 + 1] + 0.114f * testImg[i * 3 + 2];
            }

            // Scale luma to 0..255 domain for standard constants / 缩放到 0..255 以使用标准常数
            for (int i = 0; i < lRef.Length; i++) { lRef[i] *= 255f; lTest[i] *= 255f; }

            var g = GaussWeights(win, 1.5f);
            int half = win / 2;

            double sum = 0;
            int count = 0;
            int ww = w - win + 1;
            int hh = h - win + 1;
            if (ww <= 0 || hh <= 0) return 1f; // too small / 过小直接视为相同

            for (int y = 0; y < hh; y++)
            {
                for (int x = 0; x < ww; x++)
                {
                    double mu1 = 0, mu2 = 0;
                    double s11 = 0, s22 = 0, s12 = 0;
                    for (int wy = 0; wy < win; wy++)
                    {
                        for (int wx = 0; wx < win; wx++)
                        {
                            float gw = g[wy] * g[wx];
                            float v1 = lRef[(y + wy) * w + x + wx];
                            float v2 = lTest[(y + wy) * w + x + wx];
                            mu1 += gw * v1;
                            mu2 += gw * v2;
                            s11 += gw * v1 * v1;
                            s22 += gw * v2 * v2;
                            s12 += gw * v1 * v2;
                        }
                    }
                    double var1 = s11 - mu1 * mu1;
                    double var2 = s22 - mu2 * mu2;
                    double cov = s12 - mu1 * mu2;
                    double ssim = ((2 * mu1 * mu2 + C1) * (2 * cov + C2)) /
                                  ((mu1 * mu1 + mu2 * mu2 + C1) * (var1 + var2 + C2));
                    sum += ssim;
                    count++;
                }
            }
            return count == 0 ? 1f : (float)(sum / count);
        }

        /// <summary>
        /// MS-SSIM over 5 scales. Inputs RGB interleaved linear [0,1]. / 5 尺度 MS-SSIM。
        /// </summary>
        public static float MsSsim(float[] refImg, float[] testImg, int w, int h)
        {
            const int scales = 5;
            float[] cs = new float[scales];
            float[] l = new float[scales];

            var curRef = refImg;
            var curTest = testImg;
            int cw = w, ch = h;

            for (int s = 0; s < scales; s++)
            {
                // Compute contrast/structure (cs) and luma at this scale / 计算本尺度对比度/结构与亮度
                var (csVal, lum) = SsimCsAndLuma(curRef, curTest, cw, ch);
                cs[s] = csVal;
                l[s] = lum;
                if (s < scales - 1)
                {
                    curRef = Downsample2(curRef, cw, ch, out cw, out ch);
                    curTest = Downsample2(curTest, cw, ch, out cw, out ch);
                }
            }

            // MS-SSIM = prod(cs[0..3]) * l[4] / MS-SSIM = 前四尺度 cs 的乘积 × 末尺度亮度
            double product = l[scales - 1];
            for (int s = 0; s < scales - 1; s++) product *= cs[s];
            return (float)product;
        }

        private static (float, float) SsimCsAndLuma(float[] a, float[] b, int w, int h)
        {
            const double C1 = 0.01 * 0.01 * 255 * 255;
            const double C2 = 0.03 * 0.03 * 255 * 255;
            const int win = 11;
            var g = GaussWeights(win, 1.5f);
            int half = win / 2;
            int ww = w - win + 1, hh = h - win + 1;
            if (ww <= 0 || hh <= 0)
            {
                return (1f, 1f);
            }

            double sumCs = 0, sumL = 0;
            int count = 0;
            for (int y = 0; y < hh; y++)
            {
                for (int x = 0; x < ww; x++)
                {
                    double mu1 = 0, mu2 = 0, s11 = 0, s22 = 0, s12 = 0;
                    for (int wy = 0; wy < win; wy++)
                    {
                        for (int wx = 0; wx < win; wx++)
                        {
                            float gw = g[wy] * g[wx];
                            float v1 = LumaAt(a, w, x + wx, y + wy) * 255f;
                            float v2 = LumaAt(b, w, x + wx, y + wy) * 255f;
                            mu1 += gw * v1; mu2 += gw * v2;
                            s11 += gw * v1 * v1; s22 += gw * v2 * v2; s12 += gw * v1 * v2;
                        }
                    }
                    double var1 = s11 - mu1 * mu1;
                    double var2 = s22 - mu2 * mu2;
                    double cov = s12 - mu1 * mu2;
                    double cs = (2 * cov + C2) / (var1 + var2 + C2);
                    double lum = (2 * mu1 * mu2 + C1) / (mu1 * mu1 + mu2 * mu2 + C1);
                    sumCs += cs;
                    sumL += lum;
                    count++;
                }
            }
            return count == 0 ? (1f, 1f) : ((float)(sumCs / count), (float)(sumL / count));
        }

        private static float LumaAt(float[] img, int w, int x, int y)
        {
            int i = (y * w + x) * 3;
            return 0.299f * img[i] + 0.587f * img[i + 1] + 0.114f * img[i + 2];
        }

        /// <summary>2x box downsample. / 2x 盒式降采样。</summary>
        public static float[] Downsample2(float[] src, int w, int h, out int nw, out int nh)
        {
            nw = Mathf.Max(1, w / 2);
            nh = Mathf.Max(1, h / 2);
            var dst = new float[nw * nh * 3];
            for (int y = 0; y < nh; y++)
            {
                for (int x = 0; x < nw; x++)
                {
                    int x0 = Mathf.Min(x * 2, w - 1), y0 = Mathf.Min(y * 2, h - 1);
                    int x1 = Mathf.Min(x * 2 + 1, w - 1), y1 = Mathf.Min(y * 2 + 1, h - 1);
                    for (int c = 0; c < 3; c++)
                    {
                        float v = src[(y0 * w + x0) * 3 + c] + src[(y0 * w + x1) * 3 + c] +
                                  src[(y1 * w + x0) * 3 + c] + src[(y1 * w + x1) * 3 + c];
                        dst[(y * nw + x) * 3 + c] = v * 0.25f;
                    }
                }
            }
            return dst;
        }

        // ===== CIEDE2000 =====
        /// <summary>sRGB (0..1) to Lab (D65). / sRGB 转 Lab（D65）。</summary>
        public static void SrgbToLab(float r, float g, float b, out float L, out float a, out float bb)
        {
            float rl = SrgbToLinear(r), gl = SrgbToLinear(g), bl = SrgbToLinear(b);
            // linear RGB -> XYZ (D65) / 线性 RGB 转 XYZ
            double X = 0.4124564 * rl + 0.3575761 * gl + 0.1804375 * bl;
            double Y = 0.2126729 * rl + 0.7151522 * gl + 0.0721750 * bl;
            double Z = 0.0193339 * rl + 0.1191920 * gl + 0.9503041 * bl;
            const double Xn = 0.95047, Yn = 1.0, Zn = 1.08883;
            X /= Xn; Y /= Yn; Z /= Zn;

            double fx = Fxyz(X), fy = Fxyz(Y), fz = Fxyz(Z);
            L = (float)(116.0 * fy - 16.0);
            a = (float)(500.0 * (fx - fy));
            bb = (float)(200.0 * (fy - fz));
        }

        private static double Fxyz(double t)
        {
            const double eps = 216.0 / 24389.0;
            const double k = 24389.0 / 27.0;
            return t > eps ? Math.Pow(t, 1.0 / 3.0) : (k * t + 16.0) / 116.0;
        }

        /// <summary>CIEDE2000 color difference. / CIEDE2000 色差。</summary>
        public static float DeltaE2000(float L1, float a1, float b1, float L2, float a2, float b2)
        {
            const double deg2Rad = Math.PI / 180.0;
            const double rad2Deg = 180.0 / Math.PI;

            double C1 = Math.Sqrt(a1 * a1 + b1 * b1);
            double C2 = Math.Sqrt(a2 * a2 + b2 * b2);
            double Cbar = (C1 + C2) / 2.0;
            double Cbar7 = Math.Pow(Cbar, 7);
            double G = 0.5 * (1.0 - Math.Sqrt(Cbar7 / (Cbar7 + Math.Pow(25.0, 7))));
            double a1p = (1.0 + G) * a1;
            double a2p = (1.0 + G) * a2;
            double C1p = Math.Sqrt(a1p * a1p + b1 * b1);
            double C2p = Math.Sqrt(a2p * a2p + b2 * b2);
            double h1p = Hue(a1p, b1);
            double h2p = Hue(a2p, b2);

            double dLp = L2 - L1;
            double dCp = C2p - C1p;
            double dhp = 0;
            if (C1p * C2p != 0)
            {
                double diff = h2p - h1p;
                if (Math.Abs(diff) <= 180) dhp = diff;
                else if (diff > 180) dhp = diff - 360;
                else dhp = diff + 360;
            }
            double dHp = 2.0 * Math.Sqrt(C1p * C2p) * Math.Sin(dhp * deg2Rad / 2.0);

            double Lbarp = (L1 + L2) / 2.0;
            double Cbarp = (C1p + C2p) / 2.0;
            double hbarp = 0;
            if (C1p * C2p != 0)
            {
                double hsum = h1p + h2p;
                if (Math.Abs(h1p - h2p) <= 180) hbarp = hsum / 2.0;
                else if (hsum < 360) hbarp = (hsum + 360) / 2.0;
                else hbarp = (hsum - 360) / 2.0;
            }

            double T = 1.0
                       - 0.17 * Math.Cos((hbarp - 30) * deg2Rad)
                       + 0.24 * Math.Cos(2.0 * hbarp * deg2Rad)
                       + 0.32 * Math.Cos((3.0 * hbarp + 6) * deg2Rad)
                       - 0.20 * Math.Cos((4.0 * hbarp - 63) * deg2Rad);
            double dTheta = 30.0 * Math.Exp(-Math.Pow((hbarp - 275) / 25.0, 2));
            double Cbarp7 = Math.Pow(Cbarp, 7);
            double Rc = 2.0 * Math.Sqrt(Cbarp7 / (Cbarp7 + Math.Pow(25.0, 7)));
            double Sl = 1.0 + 0.015 * Math.Pow(Lbarp - 50.0, 2) / Math.Sqrt(20.0 + Math.Pow(Lbarp - 50.0, 2));
            double Sc = 1.0 + 0.045 * Cbarp;
            double Sh = 1.0 + 0.015 * Cbarp * T;
            double Rt = -Math.Sin(2.0 * dTheta * deg2Rad) * Rc;

            double dLpSl = dLp / Sl, dCpSc = dCp / Sc, dHpSh = dHp / Sh;
            return (float)Math.Sqrt(dLpSl * dLpSl + dCpSc * dCpSc + dHpSh * dHpSh + Rt * dCpSc * dHpSh);
        }

        private static double Hue(double a, double b)
        {
            if (a == 0 && b == 0) return 0;
            double h = Math.Atan2(b, a) * (180.0 / Math.PI);
            if (h < 0) h += 360.0;
            return h;
        }

        /// <summary>Mean absolute / RMSE in Lab space. / Lab 空间 RMSE。</summary>
        public static float DeltaE2000Images(float[] refImg, float[] testImg, int w, int h)
        {
            double sum = 0;
            int count = 0;
            for (int i = 0; i < w * h; i++)
            {
                int idx = i * 3;
                SrgbToLab(refImg[idx], refImg[idx + 1], refImg[idx + 2], out float L1, out float a1, out float b1);
                SrgbToLab(testImg[idx], testImg[idx + 1], testImg[idx + 2], out float L2, out float a2, out float b2);
                sum += DeltaE2000(L1, a1, b1, L2, a2, b2);
                count++;
            }
            return count == 0 ? 0f : (float)(sum / count);
        }

        // ===== Alpha metrics =====
        /// <summary>Cutout alpha IoU: binary alpha vs cutoff threshold. / Cutout alpha IoU：按阈值二值化后的 IoU。</summary>
        public static float CutoutIoU(float[] refAlpha, float[] testAlpha, float cutoff)
        {
            int tp = 0, fp = 0, fn = 0;
            for (int i = 0; i < refAlpha.Length; i++)
            {
                bool r = refAlpha[i] >= cutoff;
                bool t = testAlpha[i] >= cutoff;
                if (r && t) tp++;
                else if (t) fp++;
                else if (r) fn++;
            }
            int denom = tp + fp + fn;
            return denom == 0 ? 1f : tp / (float)denom;
        }

        /// <summary>Linear alpha RMSE. / 线性 alpha RMSE。</summary>
        public static float AlphaRmse(float[] refAlpha, float[] testAlpha)
        {
            double sum = 0;
            for (int i = 0; i < refAlpha.Length; i++)
            {
                double d = refAlpha[i] - testAlpha[i];
                sum += d * d;
            }
            return (float)Math.Sqrt(sum / Math.Max(1, refAlpha.Length));
        }

        // ===== Normal metrics =====
        /// <summary>p95 angle error between decoded normals (degrees). / 解码法线之间的 p95 角度误差（度）。</summary>
        public static float NormalAngleP95(float[] refNrm, float[] testNrm, int count)
        {
            var angles = new float[count];
            for (int i = 0; i < count; i++)
            {
                int idx = i * 3;
                var n1 = Normalize(new Vector3(refNrm[idx], refNrm[idx + 1], refNrm[idx + 2]));
                var n2 = Normalize(new Vector3(testNrm[idx], testNrm[idx + 1], testNrm[idx + 2]));
                float dot = Mathf.Clamp(Vector3.Dot(n1, n2), -1f, 1f);
                angles[i] = Mathf.Acos(dot) * Mathf.Rad2Deg;
            }
            Array.Sort(angles);
            int p95 = Mathf.Min(count - 1, (int)(count * 0.95f));
            return angles[Mathf.Max(0, p95)];
        }

        private static Vector3 Normalize(Vector3 v)
        {
            float m = v.magnitude;
            return m > 1e-6f ? v / m : Vector3.up;
        }

        // ===== Grayscale =====
        /// <summary>
        /// Per-channel linear RMSE on used channels (a channel is used if it has any variation); returns the worst.
        /// / 对被使用通道逐通道计算线性 RMSE（通道存在变化即视为使用），返回最差者。
        /// </summary>
        public static float GrayRmsUsedChannels(float[] refImg, float[] testImg, int count)
        {
            float worst = 0;
            for (int c = 0; c < 4; c++)
            {
                if (!ChannelUsed(refImg, count, c)) continue;
                double sum = 0;
                for (int i = 0; i < count; i++)
                {
                    double d = refImg[i * 4 + c] - testImg[i * 4 + c];
                    sum += d * d;
                }
                float rms = (float)Math.Sqrt(sum / count);
                worst = Mathf.Max(worst, rms);
            }
            return worst;
        }

        private static bool ChannelUsed(float[] img, int count, int c)
        {
            float min = float.MaxValue, max = float.MinValue;
            for (int i = 0; i < count; i++)
            {
                float v = img[i * 4 + c];
                if (v < min) min = v;
                if (v > max) max = v;
            }
            return max - min > 1f / 255f;
        }
    }
}
