// ATO — Avatar Texture Optimizer
// Core quality math: exact sRGB↔linear, CIELAB / CIEDE2000, SSIM / MS-SSIM, alpha
// (IoU + RMSE), normal angle error, and area-average + bilinear resampling.
// 核心质量数学：精确 sRGB↔linear、CIELAB / CIEDE2000、SSIM / MS-SSIM、alpha（IoU+RMSE）、
// 法线角度误差，以及面积平均 + 双线性重采样。
//
// References 参考文献:
//  - Sharma, Wu, Dalal — The CIEDE2000 color-difference formula (2005)
//  - Wang et al. — Image quality assessment: from error visibility to structural similarity (2004)
//  - Wang, Simoncelli, Bovik — Multi-scale structural similarity (2003)
//
// This is the CPU reference implementation. It is structured so the heavy pixel loops can
// be swapped for Burst/GPU backends (see CLAUDE.md #34) without changing the call sites.
// 这是 CPU 参考实现。结构上允许将重像素循环替换为 Burst/GPU 后端（CLAUDE.md #34），无需改动调用点。

using System;
using UnityEngine;

namespace net.fosa.ato.editor
{
    /// <summary>
    /// Color-space and quality math. 色彩空间与质量数学。
    /// </summary>
    public static class QualityMath
    {
        // ---- sRGB / linear -------------------------------------------------

        public static float SRgbToLinear(float c)
        {
            if (c <= 0.04045f) return c / 12.92f;
            return Mathf.Pow((c + 0.055f) / 1.055f, 2.4f);
        }

        public static float LinearToSRgb(float c)
        {
            if (c <= 0.0031308f) return c * 12.92f;
            return 1.055f * Mathf.Pow(c, 1f / 2.4f) - 0.055f;
        }

        // ---- linear RGB → Lab (D65) ---------------------------------------

        public static void LinearRGBToLab(float r, float g, float b, out float L, out float a, out float bb)
        {
            // sRGB D65 matrix. sRGB D65 矩阵。
            float x = 0.4124564f * r + 0.3575761f * g + 0.1804375f * b;
            float y = 0.2126729f * r + 0.7151522f * g + 0.0721750f * b;
            float z = 0.0193339f * r + 0.1191920f * g + 0.9503041f * b;

            const float xn = 0.95047f, yn = 1.0f, zn = 1.08883f;
            float fx = F(x / xn), fy = F(y / yn), fz = F(z / zn);
            L = 116f * fy - 16f;
            a = 500f * (fx - fy);
            bb = 200f * (fy - fz);
        }

        private static float F(float t)
        {
            const float d = 6f / 29f;
            if (t > d * d * d) return Mathf.Pow(t, 1f / 3f);
            return t / (3f * d * d) + 4f / 29f;
        }

        // ---- CIEDE2000 ----------------------------------------------------

        /// <summary>CIEDE2000 delta-E between two Lab colors. 两个 Lab 颜色之间的 CIEDE2000 色差。</summary>
        public static float DeltaE2000(float L1, float a1, float b1, float L2, float a2, float b2)
        {
            const float pi = Mathf.PI;
            float C1 = Mathf.Sqrt(a1 * a1 + b1 * b1);
            float C2 = Mathf.Sqrt(a2 * a2 + b2 * b2);
            float Cbar = (C1 + C2) * 0.5f;
            float Cbar7 = Mathf.Pow(Cbar, 7f);
            float G = 0.5f * (1f - Mathf.Sqrt(Cbar7 / (Cbar7 + Mathf.Pow(25f, 7f))));
            float a1p = (1f + G) * a1;
            float a2p = (1f + G) * a2;
            float C1p = Mathf.Sqrt(a1p * a1p + b1 * b1);
            float C2p = Mathf.Sqrt(a2p * a2p + b2 * b2);
            float h1p = HueAngle(b1, a1p);
            float h2p = HueAngle(b2, a2p);

            float dLp = L2 - L1;
            float dCp = C2p - C1p;
            float dhp;
            if (C1p * C2p == 0f) dhp = 0f;
            else
            {
                float dh = h2p - h1p;
                if (dh > 180f) dh -= 360f;
                else if (dh < -180f) dh += 360f;
                dhp = 2f * Mathf.Sqrt(C1p * C2p) * Mathf.Sin(dh * pi / 360f);
            }

            float Lbarp = (L1 + L2) * 0.5f;
            float Cbarp = (C1p + C2p) * 0.5f;
            float hbarp;
            if (C1p * C2p == 0f) hbarp = h1p + h2p;
            else
            {
                float dh = Mathf.Abs(h1p - h2p);
                if (dh <= 180f) hbarp = (h1p + h2p) * 0.5f;
                else if (h1p + h2p < 360f) hbarp = (h1p + h2p + 360f) * 0.5f;
                else hbarp = (h1p + h2p - 360f) * 0.5f;
            }

            float T = 1f - 0.17f * Mathf.Cos((hbarp - 30f) * pi / 180f)
                        + 0.24f * Mathf.Cos((2f * hbarp) * pi / 180f)
                        + 0.32f * Mathf.Cos((3f * hbarp + 6f) * pi / 180f)
                        - 0.20f * Mathf.Cos((4f * hbarp - 63f) * pi / 180f);

            float dTheta = 30f * Mathf.Exp(-((hbarp - 275f) / 25f) * ((hbarp - 275f) / 25f));
            float Cbarp7 = Mathf.Pow(Cbarp, 7f);
            float Rc = 2f * Mathf.Sqrt(Cbarp7 / (Cbarp7 + Mathf.Pow(25f, 7f)));
            float Lbarp2 = Lbarp - 50f;
            float Sl = 1f + 0.015f * Lbarp2 * Lbarp2 / Mathf.Sqrt(20f + Lbarp2 * Lbarp2);
            float Sc = 1f + 0.045f * Cbarp;
            float Sh = 1f + 0.015f * Cbarp * T;
            float Rt = -Mathf.Sin(2f * dTheta * pi / 180f) * Rc;

            float t1 = dLp / Sl;
            float t2 = dCp / Sc;
            float t3 = dhp / Sh;
            return Mathf.Sqrt(t1 * t1 + t2 * t2 + t3 * t3 + Rt * t2 * t3);
        }

        private static float HueAngle(float b, float a)
        {
            if (a == 0f && b == 0f) return 0f;
            float h = Mathf.Atan2(b, a) * 180f / Mathf.PI;
            if (h < 0f) h += 360f;
            return h;
        }

        // ---- SSIM ---------------------------------------------------------

        private static readonly float[] SsimWeights5 = { 0.0448f, 0.2856f, 0.3001f, 0.2363f, 0.1333f };

        /// <summary>Mean SSIM over a single channel with an 11x11 Gaussian window. 11x11 高斯窗的单通道均值 SSIM。</summary>
        public static float SSIM(float[] a, float[] b, int w, int h)
        {
            const float k1 = 0.01f, k2 = 0.03f, L = 1f;
            float c1 = k1 * L; c1 *= c1;
            float c2 = k2 * L; c2 *= c2;

            // Build 11x11 Gaussian window (sigma=1.5). 构建 11x11 高斯窗（sigma=1.5）。
            const int win = 11;
            const float sigma = 1.5f;
            var kernel = new float[win * win];
            float ksum = 0f;
            for (int y = 0; y < win; y++)
            for (int x = 0; x < win; x++)
            {
                float dx = x - win / 2, dy = y - win / 2;
                float g = Mathf.Exp(-(dx * dx + dy * dy) / (2f * sigma * sigma));
                kernel[y * win + x] = g;
                ksum += g;
            }
            for (int i = 0; i < kernel.Length; i++) kernel[i] /= ksum;

            double sum = 0;
            int count = 0;
            int half = win / 2;
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double muA = 0, muB = 0;
                for (int ky = 0; ky < win; ky++)
                for (int kx = 0; kx < win; kx++)
                {
                    int sx = Clamp(x + kx - half, 0, w - 1);
                    int sy = Clamp(y + ky - half, 0, h - 1);
                    float kw = kernel[ky * win + kx];
                    muA += a[sy * w + sx] * kw;
                    muB += b[sy * w + sx] * kw;
                }
                double va = 0, vb = 0, vab = 0;
                for (int ky = 0; ky < win; ky++)
                for (int kx = 0; kx < win; kx++)
                {
                    int sx = Clamp(x + kx - half, 0, w - 1);
                    int sy = Clamp(y + ky - half, 0, h - 1);
                    float kw = kernel[ky * win + kx];
                    double da = a[sy * w + sx] - muA;
                    double db = b[sy * w + sx] - muB;
                    va += da * da * kw;
                    vb += db * db * kw;
                    vab += da * db * kw;
                }
                double ssim = ((2 * muA * muB + c1) * (2 * vab + c2)) /
                              ((muA * muA + muB * muB + c1) * (va + vb + c2));
                sum += ssim;
                count++;
            }
            return count > 0 ? (float)(sum / count) : 1f;
        }

        /// <summary>
        /// Multi-scale SSIM over 5 scales. Falls back to single-scale SSIM when the image is too small.
        /// 5 尺度的多尺度 SSIM；图像过小时回退到单尺度 SSIM。
        /// </summary>
        public static float MSSSIM(float[] a, float[] b, int w, int h)
        {
            if (w < 32 || h < 32) return SSIM(a, b, w, h);

            const int scales = 5;
            var A = new float[scales][];
            var B = new float[scales][];
            var W = new int[scales];
            var H = new int[scales];
            A[0] = a; B[0] = b; W[0] = w; H[0] = h;
            for (int s = 1; s < scales; s++)
            {
                W[s] = Mathf.Max(1, W[s - 1] / 2);
                H[s] = Mathf.Max(1, H[s - 1] / 2);
                A[s] = Down2(A[s - 1], W[s - 1], H[s - 1]);
                B[s] = Down2(B[s - 1], W[s - 1], H[s - 1]);
            }

            // Coarsest scale contributes luminance + contrast-structure; finer scales CS only.
            // 最粗尺度贡献亮度+对比结构；更细尺度仅 CS。
            float ms = 1f;
            for (int s = 0; s < scales; s++)
            {
                float cs = ContrastStructure(A[s], B[s], W[s], H[s], withLuminance: s == scales - 1);
                ms *= Mathf.Pow(Mathf.Max(cs, 0f), SsimWeights5[s]);
            }
            return Mathf.Clamp01(ms);
        }

        private static float[] Down2(float[] src, int w, int h)
        {
            int nw = Mathf.Max(1, w / 2), nh = Mathf.Max(1, h / 2);
            var dst = new float[nw * nh];
            for (int y = 0; y < nh; y++)
            for (int x = 0; x < nw; x++)
            {
                float sum = 0; int cnt = 0;
                for (int dy = 0; dy < 2; dy++)
                for (int dx = 0; dx < 2; dx++)
                {
                    int sx = Mathf.Min(x * 2 + dx, w - 1);
                    int sy = Mathf.Min(y * 2 + dy, h - 1);
                    sum += src[sy * w + sx]; cnt++;
                }
                dst[y * nw + x] = sum / cnt;
            }
            return dst;
        }

        private static float ContrastStructure(float[] a, float[] b, int w, int h, bool withLuminance)
        {
            const float k1 = 0.01f, k2 = 0.03f, L = 1f;
            float c1 = k1 * L; c1 *= c1;
            float c2 = k2 * L; c2 *= c2;
            // Simple 8x8 box-window statistics (adequate for the downsampled scales).
            // 简化 8x8 盒窗统计（对下采样后的尺度足够）。
            const int win = 8;
            int half = win / 2;
            double sum = 0; int count = 0;
            for (int y = 0; y < h; y += 4)
            for (int x = 0; x < w; x += 4)
            {
                double muA = 0, muB = 0; int n = 0;
                for (int ky = 0; ky < win; ky++)
                for (int kx = 0; kx < win; kx++)
                {
                    int sx = Clamp(x + kx - half, 0, w - 1);
                    int sy = Clamp(y + ky - half, 0, h - 1);
                    muA += a[sy * w + sx]; muB += b[sy * w + sx]; n++;
                }
                muA /= n; muB /= n;
                double va = 0, vb = 0, vab = 0;
                for (int ky = 0; ky < win; ky++)
                for (int kx = 0; kx < win; kx++)
                {
                    int sx = Clamp(x + kx - half, 0, w - 1);
                    int sy = Clamp(y + ky - half, 0, h - 1);
                    double da = a[sy * w + sx] - muA;
                    double db = b[sy * w + sx] - muB;
                    va += da * da; vb += db * db; vab += da * db;
                }
                va /= n; vb /= n; vab /= n;
                double cs = (2 * vab + c2) / (va + vb + c2);
                if (withLuminance)
                {
                    double lum = (2 * muA * muB + c1) / (muA * muA + muB * muB + c1);
                    cs *= lum;
                }
                sum += cs; count++;
            }
            return count > 0 ? (float)(sum / count) : 1f;
        }

        private static int Clamp(int v, int min, int max) => v < min ? min : (v > max ? max : v);

        // ---- Alpha metrics -------------------------------------------------

        /// <summary>Linear RMSE between two alpha arrays. 两个 alpha 数组的线性 RMSE。</summary>
        public static float AlphaRMSE(float[] a, float[] b)
        {
            double sum = 0;
            for (int i = 0; i < a.Length; i++)
            {
                double d = a[i] - b[i];
                sum += d * d;
            }
            return (float)Math.Sqrt(sum / Math.Max(1, a.Length));
        }

        /// <summary>
        /// Clipped-outline IoU between two alpha arrays at a cutoff. 给定 cutoff 下两个 alpha 数组的裁剪轮廓 IoU。
        /// </summary>
        public static float AlphaIoU(float[] a, float[] b, float cutoff)
        {
            long inter = 0, union = 0;
            for (int i = 0; i < a.Length; i++)
            {
                bool ca = a[i] > cutoff, cb = b[i] > cutoff;
                if (ca && cb) inter++;
                if (ca || cb) union++;
            }
            return union == 0 ? 1f : (float)inter / union;
        }

        // ---- Normal metrics ------------------------------------------------

        /// <summary>Mean angular error (degrees) between two unit normal arrays. 两个单位法线数组的平均角度误差（度）。</summary>
        public static float MeanAngleErrorDeg(Vector3[] a, Vector3[] b)
        {
            double sum = 0;
            for (int i = 0; i < a.Length; i++)
            {
                float dot = Vector3.Dot(a[i], b[i]);
                dot = Mathf.Clamp(dot, -1f, 1f);
                sum += Mathf.Acos(dot) * Mathf.Rad2Deg;
            }
            return (float)(sum / Math.Max(1, a.Length));
        }

        /// <summary>p95 angular error (degrees). p95 角度误差（度）。</summary>
        public static float P95AngleErrorDeg(Vector3[] a, Vector3[] b)
        {
            var errs = new float[a.Length];
            for (int i = 0; i < a.Length; i++)
            {
                float dot = Mathf.Clamp(Vector3.Dot(a[i], b[i]), -1f, 1f);
                errs[i] = Mathf.Acos(dot) * Mathf.Rad2Deg;
            }
            Array.Sort(errs);
            int idx = Mathf.Min(a.Length - 1, Mathf.FloorToInt(a.Length * 0.95f));
            return errs[idx];
        }

        // ---- Resampling ----------------------------------------------------

        /// <summary>
        /// Area-average downsample (linear space). For premultiplied-alpha textures the caller
        /// should pass premultiplied colors so that alpha blends correctly.
        /// 面积平均下采样（线性空间）。预乘 alpha 贴图应由调用方传入预乘颜色以正确混合。
        /// </summary>
        public static Color[] AreaResample(Color[] src, int srcW, int srcH, int dstW, int dstH)
        {
            dstW = Mathf.Max(1, dstW); dstH = Mathf.Max(1, dstH);
            var dst = new Color[dstW * dstH];
            float sx = (float)srcW / dstW, sy = (float)srcH / dstH;
            for (int y = 0; y < dstH; y++)
            for (int x = 0; x < dstW; x++)
            {
                float x0 = x * sx, x1 = (x + 1) * sx;
                float y0 = y * sy, y1 = (y + 1) * sy;
                int ix0 = Mathf.FloorToInt(x0), ix1 = Mathf.Min(srcW, Mathf.CeilToInt(x1));
                int iy0 = Mathf.FloorToInt(y0), iy1 = Mathf.Min(srcH, Mathf.CeilToInt(y1));
                float r = 0, g = 0, b = 0, a = 0, wsum = 0;
                for (int iy = iy0; iy < iy1; iy++)
                for (int ix = ix0; ix < ix1; ix++)
                {
                    float ox = Mathf.Min(x1, ix + 1) - Mathf.Max(x0, ix);
                    float oy = Mathf.Min(y1, iy + 1) - Mathf.Max(y0, iy);
                    float w = ox * oy;
                    var c = src[iy * srcW + ix];
                    r += c.r * w; g += c.g * w; b += c.b * w; a += c.a * w; wsum += w;
                }
                float inv = wsum > 1e-9f ? 1f / wsum : 1f;
                dst[y * dstW + x] = new Color(r * inv, g * inv, b * inv, a * inv);
            }
            return dst;
        }

        /// <summary>Bilinear upsample. 双线性上采样。</summary>
        public static Color[] BilinearUpsample(Color[] src, int srcW, int srcH, int dstW, int dstH)
        {
            var dst = new Color[dstW * dstH];
            float sx = (float)srcW / dstW, sy = (float)srcH / dstH;
            for (int y = 0; y < dstH; y++)
            for (int x = 0; x < dstW; x++)
            {
                float u = (x + 0.5f) * sx - 0.5f;
                float v = (y + 0.5f) * sy - 0.5f;
                int x0 = Mathf.FloorToInt(u), y0 = Mathf.FloorToInt(v);
                float fx = u - x0, fy = v - y0;
                int x1 = Mathf.Min(x0 + 1, srcW - 1), y1 = Mathf.Min(y0 + 1, srcH - 1);
                x0 = Mathf.Max(x0, 0); y0 = Mathf.Max(y0, 0);
                var c00 = src[y0 * srcW + x0];
                var c10 = src[y0 * srcW + x1];
                var c01 = src[y1 * srcW + x0];
                var c11 = src[y1 * srcW + x1];
                Color top = Color.Lerp(c00, c10, fx);
                Color bot = Color.Lerp(c01, c11, fx);
                dst[y * dstW + x] = Color.Lerp(top, bot, fy);
            }
            return dst;
        }

        /// <summary>Extract a region from a flat pixel array. 从扁平像素数组提取区域。</summary>
        public static Color[] ExtractRegion(Color[] src, int srcW, int srcH, int x, int y, int w, int h)
        {
            var dst = new Color[w * h];
            for (int iy = 0; iy < h; iy++)
            for (int ix = 0; ix < w; ix++)
            {
                int sx = Mathf.Clamp(x + ix, 0, srcW - 1);
                int sy = Mathf.Clamp(y + iy, 0, srcH - 1);
                dst[iy * w + ix] = src[sy * srcW + sx];
            }
            return dst;
        }
    }
}
