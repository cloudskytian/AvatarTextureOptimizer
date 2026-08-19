// ============================================================================
// ATOQuality.cs — 质量度量算法 / Quality metric algorithms
// (EN) Pure metric functions: bilinear resampling (linear-space, premultiplied
//      alpha), MS-SSIM / SSIM, CIEDE2000 ΔE, alpha IoU/RMSE, normal-map angle
//      error with p95, and grayscale linear RMSE. CPU reference implementation
//      (Burst/GPU acceleration to be layered on later).
// (ZH) 纯度量函数：双线性重采样（线性空间、预乘 alpha）、MS-SSIM/SSIM、
//      CIEDE2000 ΔE、alpha IoU/RMSE、法线贴图角度误差+p95、灰度线性 RMSE。
//      当前为 CPU 参考实现（后续叠加 Burst/GPU 加速）。
// ============================================================================

using System;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer
{
    public static class ATOQuality
    {
        // ---------------------------------------------------------------------
        // 色彩空间 / color space
        // ---------------------------------------------------------------------
        public static float SrgbToLinear(float c) =>
            c <= 0.04045f ? c / 12.92f : Mathf.Pow((c + 0.055f) / 1.055f, 2.4f);

        public static float LinearToSrgb(float c) =>
            c <= 0.0031308f ? c * 12.92f : 1.055f * Mathf.Pow(c, 1f / 2.4f) - 0.055f;

        public static Color SrgbToLinear(Color c) =>
            new Color(SrgbToLinear(c.r), SrgbToLinear(c.g), SrgbToLinear(c.b), c.a);

        public static Color LinearToSrgb(Color c) =>
            new Color(LinearToSrgb(c.r), LinearToSrgb(c.g), LinearToSrgb(c.b), c.a);

        // sRGB → CIE L*a*b* (D65)
        public static void SrgbToLab(float r, float g, float b, out float L, out float a, out float bb)
        {
            // 先转线性再转 XYZ / linear then XYZ
            float rl = SrgbToLinear(r), gl = SrgbToLinear(g), bl = SrgbToLinear(b);
            float X = rl * 0.4124f + gl * 0.3576f + bl * 0.1805f;
            float Y = rl * 0.2126f + gl * 0.7152f + bl * 0.0722f;
            float Z = rl * 0.0193f + gl * 0.1192f + bl * 0.9505f;
            // 归一化到 D65 白点 / normalize to D65
            float xr = X / 0.95047f, yr = Y / 1.00000f, zr = Z / 1.08883f;
            float fx = F(xr), fy = F(yr), fz = F(zr);
            L = 116f * fy - 16f;
            a = 500f * (fx - fy);
            bb = 200f * (fy - fz);
        }

        private static float F(float t) =>
            t > 0.008856f ? Mathf.Pow(t, 1f / 3f) : (7.787f * t + 16f / 116f);

        /// <summary>(EN) CIEDE2000 color difference. (ZH) CIEDE2000 色差。</summary>
        public static float DeltaE2000(float r1, float g1, float b1, float r2, float g2, float b2)
        {
            SrgbToLab(r1, g1, b1, out float L1, out float a1, out float b1_);
            SrgbToLab(r2, g2, b2, out float L2, out float a2, out float b2_);
            return DeltaE2000Lab(L1, a1, b1_, L2, a2, b2_);
        }

        public static float DeltaE2000Lab(float L1, float a1, float b1, float L2, float a2, float b2)
        {
            float C1 = Mathf.Sqrt(a1 * a1 + b1 * b1);
            float C2 = Mathf.Sqrt(a2 * a2 + b2 * b2);
            float Cbar = (C1 + C2) * 0.5f;
            float Cbar7 = Mathf.Pow(Cbar, 7f);
            float G = 0.5f * (1f - Mathf.Sqrt(Cbar7 / (Cbar7 + 6103515625f))); // 25^7
            float a1p = (1f + G) * a1, a2p = (1f + G) * a2;
            float C1p = Mathf.Sqrt(a1p * a1p + b1 * b1);
            float C2p = Mathf.Sqrt(a2p * a2p + b2 * b2);
            float h1p = HueAngle(b1, a1p), h2p = HueAngle(b2, a2p);

            float dLp = L2 - L1;
            float dCp = C2p - C1p;
            float dhp = HueDiff(C1p, C2p, h1p, h2p);
            float dHp = 2f * Mathf.Sqrt(C1p * C2p) * Mathf.Sin(dhp * Mathf.Deg2Rad * 0.5f);

            float Lbarp = (L1 + L2) * 0.5f;
            float Cbarp = (C1p + C2p) * 0.5f;
            float hbarp = HueMean(C1p, C2p, h1p, h2p);

            float T = 1f - 0.17f * Mathf.Cos((hbarp - 30f) * Mathf.Deg2Rad)
                        + 0.24f * Mathf.Cos((2f * hbarp) * Mathf.Deg2Rad)
                        + 0.32f * Mathf.Cos((3f * hbarp + 6f) * Mathf.Deg2Rad)
                        - 0.20f * Mathf.Cos((4f * hbarp - 63f) * Mathf.Deg2Rad);

            float dTheta = 30f * Mathf.Exp(-Mathf.Pow((hbarp - 275f) / 25f, 2f));
            float Cbarp7 = Mathf.Pow(Cbarp, 7f);
            float Rc = 2f * Mathf.Sqrt(Cbarp7 / (Cbarp7 + 6103515625f));

            float Lbarp50 = (Lbarp - 50f) * (Lbarp - 50f);
            float SL = 1f + 0.015f * Lbarp50 / Mathf.Sqrt(20f + Lbarp50);
            float SC = 1f + 0.045f * Cbarp;
            float SH = 1f + 0.015f * Cbarp * T;

            float RT = -Mathf.Sin(2f * dTheta * Mathf.Deg2Rad) * Rc;

            float dL = dLp / SL, dC = dCp / SC, dH = dHp / SH;
            return Mathf.Sqrt(dL * dL + dC * dC + dH * dH + RT * dC * dH);
        }

        private static float HueAngle(float b, float a)
        {
            if (a == 0f && b == 0f) return 0f;
            float h = Mathf.Atan2(b, a) * Mathf.Rad2Deg;
            return h < 0f ? h + 360f : h;
        }

        private static float HueDiff(float C1, float C2, float h1, float h2)
        {
            if (C1 * C2 == 0f) return 0f;
            float d = h2 - h1;
            if (Mathf.Abs(d) <= 180f) return d;
            if (d > 180f) return d - 360f;
            return d + 360f;
        }

        private static float HueMean(float C1, float C2, float h1, float h2)
        {
            if (C1 * C2 == 0f) return h1 + h2;
            float d = h2 - h1;
            if (Mathf.Abs(d) <= 180f) return (h1 + h2) * 0.5f;
            if (Mathf.Abs(d) > 180f && h1 + h2 < 360f) return (h1 + h2 + 360f) * 0.5f;
            return (h1 + h2 - 360f) * 0.5f;
        }

        // ---------------------------------------------------------------------
        // 双线性重采样 / bilinear resample (region crop → target size)
        // (EN) Crops a pixel region from src and resamples to dw×dh. If
        //      linearSpace, resamples in linear space; if premultiplyAlpha,
        //      premultiplies alpha before downsampling (and unpremultiplies after).
        // (ZH) 从 src 裁出像素区域并重采样到 dw×dh。linearSpace 时在线性空间重采样；
        //      premultiplyAlpha 时下采样前预乘 alpha（采样后反预乘）。
        // ---------------------------------------------------------------------
        public static void ResampleRegion(Color[] src, int sw, int sh,
            int rx, int ry, int rw, int rh, int dw, int dh,
            bool linearSpace, bool premultiplyAlpha, Color[] dst)
        {
            for (int y = 0; y < dh; y++)
            {
                // 目标像素中心对应的源坐标 / source coordinate of dst pixel center
                float sy = (y + 0.5f) * rh / dh - 0.5f + ry;
                int y0 = Mathf.FloorToInt(sy);
                float fy = sy - y0;

                for (int x = 0; x < dw; x++)
                {
                    float sx = (x + 0.5f) * rw / dw - 0.5f + rx;
                    int x0 = Mathf.FloorToInt(sx);
                    float fx = sx - x0;

                    Color c = BilinearSample(src, sw, sh, x0, y0, fx, fy, linearSpace, premultiplyAlpha);
                    dst[y * dw + x] = c;
                }
            }
        }

        private static Color BilinearSample(Color[] src, int sw, int sh,
            int x0, int y0, float fx, float fy, bool linear, bool premultiply)
        {
            int x1 = Mathf.Min(x0 + 1, sw - 1);
            int y1 = Mathf.Min(y0 + 1, sh - 1);
            x0 = Mathf.Clamp(x0, 0, sw - 1);
            y0 = Mathf.Clamp(y0, 0, sh - 1);

            Color c00 = src[y0 * sw + x0], c10 = src[y0 * sw + x1];
            Color c01 = src[y1 * sw + x0], c11 = src[y1 * sw + x1];

            if (linear)
            {
                c00 = SrgbToLinear(c00); c10 = SrgbToLinear(c10);
                c01 = SrgbToLinear(c01); c11 = SrgbToLinear(c11);
            }

            if (premultiply)
            {
                c00 = Premultiply(c00); c10 = Premultiply(c10);
                c01 = Premultiply(c01); c11 = Premultiply(c11);
            }

            Color top = Color.Lerp(c00, c10, fx);
            Color bot = Color.Lerp(c01, c11, fx);
            Color result = Color.Lerp(top, bot, fy);

            if (premultiply) result = Unpremultiply(result);
            if (linear) result = LinearToSrgb(result);
            result.a = Mathf.Clamp01(result.a);
            return result;
        }

        private static Color Premultiply(Color c) => new Color(c.r * c.a, c.g * c.a, c.b * c.a, c.a);
        private static Color Unpremultiply(Color c) =>
            c.a < 1e-6f ? new Color(0, 0, 0, 0) : new Color(c.r / c.a, c.g / c.a, c.b / c.a, c.a);

        // ---------------------------------------------------------------------
        // 灰度（亮度）/ luminance
        // ---------------------------------------------------------------------
        public static float Luminance(Color c) => 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;

        // ---------------------------------------------------------------------
        // SSIM（单尺度，8x8 窗口）/ SSIM (single-scale, 8x8 window)
        // ---------------------------------------------------------------------
        public static float SSIM(float[] a, float[] b, int w, int h)
        {
            const float C1 = 0.0001f; // (0.01)^2
            const float C2 = 0.0009f; // (0.03)^2
            const int win = 8;
            double sum = 0; int count = 0;

            for (int by = 0; by + win <= h; by += win)
            {
                for (int bx = 0; bx + win <= w; bx += win)
                {
                    double ma = 0, mb = 0;
                    for (int y = 0; y < win; y++)
                        for (int x = 0; x < win; x++)
                        {
                            int i = (by + y) * w + (bx + x);
                            ma += a[i]; mb += b[i];
                        }
                    ma /= win * win; mb /= win * win;

                    double va = 0, vb = 0, cov = 0;
                    for (int y = 0; y < win; y++)
                        for (int x = 0; x < win; x++)
                        {
                            int i = (by + y) * w + (bx + x);
                            double da = a[i] - ma, db = b[i] - mb;
                            va += da * da; vb += db * db; cov += da * db;
                        }
                    va /= win * win - 1; vb /= win * win - 1; cov /= win * win - 1;

                    double ssim = ((2 * ma * mb + C1) * (2 * cov + C2)) /
                                  ((ma * ma + mb * mb + C1) * (va + vb + C2));
                    sum += ssim; count++;
                }
            }

            return count == 0 ? 1f : (float)(sum / count);
        }

        // ---------------------------------------------------------------------
        // MS-SSIM（5 尺度）/ MS-SSIM (5 scales)
        // ---------------------------------------------------------------------
        private static readonly float[] MsSsimWeights = { 0.0448f, 0.2856f, 0.3001f, 0.2363f, 0.1333f };

        public static float MSSSIM(float[] a, float[] b, int w, int h)
        {
            float mssim = 0f;
            float[] curA = a, curB = b;
            int cw = w, ch = h;
            for (int level = 0; level < 5; level++)
            {
                if (cw < 8 || ch < 8)
                {
                    // 过小：剩余权重归入当前单尺度 SSIM / too small: fold remaining weights into current SSIM
                    float rem = 0f;
                    for (int i = level; i < 5; i++) rem += MsSsimWeights[i];
                    mssim += rem * SSIM(curA, curB, cw, ch);
                    break;
                }

                float ssim = SSIM(curA, curB, cw, ch);
                if (level == 4) { mssim += MsSsimWeights[level] * ssim; break; }
                mssim += MsSsimWeights[level] * ssim;

                // 2x 下采样 / 2x downsample
                int nw = cw / 2, nh = ch / 2;
                curA = Downsample2x(curA, cw, ch);
                curB = Downsample2x(curB, cw, ch);
                cw = nw; ch = nh;
            }
            return mssim;
        }

        private static float[] Downsample2x(float[] src, int w, int h)
        {
            int nw = w / 2, nh = h / 2;
            var dst = new float[nw * nh];
            for (int y = 0; y < nh; y++)
                for (int x = 0; x < nw; x++)
                {
                    int i0 = (y * 2) * w + x * 2;
                    dst[y * nw + x] = (src[i0] + src[i0 + 1] + src[i0 + w] + src[i0 + w + 1]) * 0.25f;
                }
            return dst;
        }

        // ---------------------------------------------------------------------
        // Alpha 度量 / alpha metrics
        // ---------------------------------------------------------------------
        /// <summary>(EN) IoU of cutout alpha silhouettes after clipping. (ZH) Cutout alpha 轮廓裁剪后的 IoU。</summary>
        public static float AlphaIoU(Color[] a, Color[] b, int count, float cutoff)
        {
            long inter = 0, union = 0;
            for (int i = 0; i < count; i++)
            {
                bool ba = a[i].a >= cutoff;
                bool bb = b[i].a >= cutoff;
                if (ba && bb) inter++;
                if (ba || bb) union++;
            }
            return union == 0 ? 1f : (float)inter / union;
        }

        /// <summary>(EN) Linear RMSE of alpha (Blend). (ZH) Blend 的 alpha 线性 RMSE。</summary>
        public static float AlphaRmse(Color[] a, Color[] b, int count)
        {
            double sum = 0;
            for (int i = 0; i < count; i++)
            {
                double d = a[i].a - b[i].a;
                sum += d * d;
            }
            return (float)Math.Sqrt(sum / Mathf.Max(1, count));
        }

        // ---------------------------------------------------------------------
        // 法线贴图 / normal maps
        // ---------------------------------------------------------------------
        /// <summary>(EN) Decode a normal map texel to a unit vector. (ZH) 解码法线贴图纹素为单位向量。</summary>
        public static Vector3 DecodeNormal(Color c)
        {
            var n = new Vector3(c.r * 2f - 1f, c.g * 2f - 1f, c.b * 2f - 1f);
            n.z = Mathf.Sqrt(Mathf.Max(0f, 1f - n.x * n.x - n.y * n.y));
            return n.normalized;
        }

        /// <summary>(EN) Angle error (degrees) between two normals. (ZH) 两个法线间的角度误差（度）。</summary>
        public static float NormalAngleDeg(Vector3 a, Vector3 b)
        {
            float dot = Mathf.Clamp(Vector3.Dot(a, b), -1f, 1f);
            return Mathf.Acos(dot) * Mathf.Rad2Deg;
        }

        /// <summary>(EN) p95 angle error between two normal maps. (ZH) 两张法线图间的 p95 角度误差。</summary>
        public static float NormalP95Angle(Color[] a, Color[] b, int count, float percentile)
        {
            var angles = new float[count];
            for (int i = 0; i < count; i++)
                angles[i] = NormalAngleDeg(DecodeNormal(a[i]), DecodeNormal(b[i]));
            System.Array.Sort(angles);
            int idx = Mathf.Clamp(Mathf.FloorToInt(count * percentile), 0, count - 1);
            return angles[idx];
        }

        // ---------------------------------------------------------------------
        // 灰度 RMSE（线性空间）/ grayscale RMSE (linear space)
        // ---------------------------------------------------------------------
        public static float GrayRmse(Color[] a, Color[] b, int count)
        {
            double sum = 0; int n = 0;
            for (int i = 0; i < count; i++)
            {
                // 逐通道取最差 / worst channel per pixel
                double dr = SrgbToLinear(a[i].r) - SrgbToLinear(b[i].r);
                double dg = SrgbToLinear(a[i].g) - SrgbToLinear(b[i].g);
                double db = SrgbToLinear(a[i].b) - SrgbToLinear(b[i].b);
                double worst = Math.Max(Math.Abs(dr), Math.Max(Math.Abs(dg), Math.Abs(db)));
                sum += worst * worst;
                n++;
            }
            return (float)Math.Sqrt(sum / Mathf.Max(1, n));
        }
    }
}
