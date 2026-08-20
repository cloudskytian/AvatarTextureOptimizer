using System;
using UnityEngine;

namespace Fosa.ATO.Editor
{
    /// <summary>
    /// 质量算法：线性空间重采样、透明预乘 alpha 下采样。
    /// 指标：MS-SSIM（短边 &lt;176px 回退单尺度 SSIM，&lt;11px 忽略）、ΔE(CIEDE2000)、
    /// alpha（Cutout 用 clip 后轮廓 IoU / Blend 用线性 RMSE）、法线角度误差+p95、灰度逐通道最差 RMSE。
    ///
    /// Quality metrics: linear-space resampling, premultiplied alpha downsampling.
    /// MS-SSIM (fallback to single-scale SSIM when short side &lt;176px, ignored &lt;11px), ΔE2000,
    /// alpha (cutout IoU / blend RMSE), normal angle+p95, per-channel grayscale RMSE.
    /// </summary>
    public static class ATOQualityMetrics
    {
        public const float Eps = 1e-6f;

        // ===== 色彩空间 / color space =====
        public static float SRGBToLinear(float c) =>
            c <= 0.04045f ? c / 12.92f : Mathf.Pow((c + 0.055f) / 1.055f, 2.4f);

        public static float LinearToSRGB(float c) =>
            c <= 0.0031308f ? c * 12.92f : 1.055f * Mathf.Pow(c, 1f / 2.4f) - 0.055f;

        public static Vector3 SRGBToLinear(Vector3 c) =>
            new Vector3(SRGBToLinear(c.x), SRGBToLinear(c.y), SRGBToLinear(c.z));

        public static Vector3 LinearToSRGB(Vector3 c) =>
            new Vector3(LinearToSRGB(c.x), LinearToSRGB(c.y), LinearToSRGB(c.z));

        // ===== CIEDE2000 ΔE =====
        public static float DeltaE2000(Vector3 lab1, Vector3 lab2)
        {
            float L1 = lab1.x, a1 = lab1.y, b1 = lab1.z;
            float L2 = lab2.x, a2 = lab2.y, b2 = lab2.z;

            float C1 = Mathf.Sqrt(a1 * a1 + b1 * b1);
            float C2 = Mathf.Sqrt(a2 * a2 + b2 * b2);
            float Cbar = (C1 + C2) * 0.5f;
            float Cbar7 = Mathf.Pow(Cbar, 7);
            float G = 0.5f * (1f - Mathf.Sqrt(Cbar7 / (Cbar7 + Mathf.Pow(25, 7))));
            float a1p = (1 + G) * a1, a2p = (1 + G) * a2;
            float C1p = Mathf.Sqrt(a1p * a1p + b1 * b1);
            float C2p = Mathf.Sqrt(a2p * a2p + b2 * b2);

            float h1p = Mathf.Atan2(b1, a1p) * Mathf.Rad2Deg; if (h1p < 0) h1p += 360;
            float h2p = Mathf.Atan2(b2, a2p) * Mathf.Rad2Deg; if (h2p < 0) h2p += 360;

            float dLp = L2 - L1;
            float dCp = C2p - C1p;
            float dhp;
            if (C1p * C2p == 0) dhp = 0;
            else if (Mathf.Abs(h2p - h1p) <= 180) dhp = h2p - h1p;
            else if (h2p - h1p > 180) dhp = h2p - h1p - 360;
            else dhp = h2p - h1p + 360;
            float dHp = 2 * Mathf.Sqrt(C1p * C2p) * Mathf.Sin(dhp * 0.5f * Mathf.Deg2Rad);

            float Lbar = (L1 + L2) * 0.5f;
            float Cbarp = (C1p + C2p) * 0.5f;
            float hbarp;
            if (C1p * C2p == 0) hbarp = h1p + h2p;
            else if (Mathf.Abs(h1p - h2p) <= 180) hbarp = (h1p + h2p) * 0.5f;
            else if (h1p + h2p < 360) hbarp = (h1p + h2p + 360) * 0.5f;
            else hbarp = (h1p + h2p - 360) * 0.5f;

            float T = 1 - 0.17f * Mathf.Cos((hbarp - 30) * Mathf.Deg2Rad)
                        + 0.24f * Mathf.Cos((2 * hbarp) * Mathf.Deg2Rad)
                        + 0.32f * Mathf.Cos((3 * hbarp + 6) * Mathf.Deg2Rad)
                        - 0.20f * Mathf.Cos((4 * hbarp - 63) * Mathf.Deg2Rad);

            float dTheta = 30 * Mathf.Exp(-Mathf.Pow((hbarp - 275) / 25, 2));
            float Cbarp7 = Mathf.Pow(Cbarp, 7);
            float Rc = 2 * Mathf.Sqrt(Cbarp7 / (Cbarp7 + Mathf.Pow(25, 7)));

            float Lbar50 = (Lbar - 50) * (Lbar - 50);
            float Sl = 1 + 0.015f * Lbar50 / Mathf.Sqrt(20 + Lbar50);
            float Sc = 1 + 0.045f * Cbarp;
            float Sh = 1 + 0.015f * Cbarp * T;
            float Rt = -Mathf.Sin(2 * dTheta * Mathf.Deg2Rad) * Rc;

            float dL = dLp / Sl, dC = dCp / Sc, dH = dHp / Sh;
            return Mathf.Sqrt(dL * dL + dC * dC + dH * dH + Rt * dC * dH);
        }

        /// <summary>线性 RGB → CIELAB（D65）。Linear RGB -> CIELAB (D65).</summary>
        public static Vector3 LinearRGBToLab(Vector3 lin)
        {
            // sRGB D65 矩阵。
            float X = 0.4124564f * lin.x + 0.3575761f * lin.y + 0.1804375f * lin.z;
            float Y = 0.2126729f * lin.x + 0.7151522f * lin.y + 0.0721750f * lin.z;
            float Z = 0.0193339f * lin.x + 0.1191920f * lin.y + 0.9503041f * lin.z;

            float fx = F(X / 0.95047f), fy = F(Y), fz = F(Z / 1.08883f);
            return new Vector3(116 * fy - 16, 500 * (fx - fy), 200 * (fy - fz));
        }

        private static float F(float t) => t > 0.008856f ? Mathf.Pow(t, 1f / 3f) : (7.787f * t + 16f / 116f);

        // ===== SSIM / MS-SSIM =====
        private const float C1 = (0.01f * 255) * (0.01f * 255);
        private const float C2 = (0.03f * 255) * (0.03f * 255);

        /// <summary>单尺度 SSIM（亮度）。Single-scale SSIM on luminance.</summary>
        public static double SSIM(float[] a, float[] b, int w, int h)
        {
            if (a.Length != b.Length) return 0;
            int n = a.Length;
            double muA = 0, muB = 0;
            for (int i = 0; i < n; i++) { muA += a[i]; muB += b[i]; }
            muA /= n; muB /= n;
            double varA = 0, varB = 0, cov = 0;
            for (int i = 0; i < n; i++)
            {
                double da = a[i] - muA, db = b[i] - muB;
                varA += da * da; varB += db * db; cov += da * db;
            }
            varA /= n; varB /= n; cov /= n;
            return ((2 * muA * muB + C1) * (2 * cov + C2)) /
                   ((muA * muA + muB * muB + C1) * (varA + varB + C2));
        }

        /// <summary>
        /// MS-SSIM（5 尺度，2x 平均下采样）。返回整体值。
        /// MS-SSIM (5 scales). Returns the overall index.
        /// </summary>
        public static double MSSSIM(float[] a, float[] b, int w, int h)
        {
            if (w < 11 || h < 11) return SSIM(a, b, w, h); // 极小岛回退单尺度
            if (Mathf.Min(w, h) < 176) return SSIM(a, b, w, h); // 短边<176 回退单尺度

            double[] weights = { 0.0448, 0.2856, 0.3001, 0.2363, 0.1333 };
            double msssim = 1.0;
            float[] ca = a, cb = b;
            int cw = w, ch = h;
            for (int scale = 0; scale < 5; scale++)
            {
                double s = SSIM(ca, cb, cw, ch);
                if (scale == 4) { msssim *= Math.Pow(s, weights[scale]); break; }
                // 前四层只用对比度+结构项（简化：直接用完整 SSIM 幂乘）。
                msssim *= Math.Pow(s, weights[scale]);
                if (cw <= 8 || ch <= 8) break;
                var (na, nb, nw, nh) = Downsample(ca, cb, cw, ch);
                ca = na; cb = nb; cw = nw; ch = nh;
            }
            return msssim;
        }

        private static (float[], float[], int, int) Downsample(float[] a, float[] b, int w, int h)
        {
            int nw = w / 2, nh = h / 2;
            var na = new float[nw * nh];
            var nb = new float[nw * nh];
            for (int y = 0; y < nh; y++)
                for (int x = 0; x < nw; x++)
                {
                    int i00 = (y * 2) * w + (x * 2), i01 = i00 + 1, i10 = i00 + w, i11 = i10 + 1;
                    na[y * nw + x] = (a[i00] + a[i01] + a[i10] + a[i11]) * 0.25f;
                    nb[y * nw + x] = (b[i00] + b[i01] + b[i10] + b[i11]) * 0.25f;
                }
            return (na, nb, nw, nh);
        }

        // ===== 法线角度误差 / normal angle error =====
        public static float NormalAngleErrorDeg(Vector3 n1, Vector3 n2)
        {
            var d = Mathf.Clamp(Vector3.Dot(n1.normalized, n2.normalized), -1f, 1f);
            return Mathf.Acos(d) * Mathf.Rad2Deg;
        }

        public static float Percentile95(float[] values)
        {
            if (values.Length == 0) return 0;
            Array.Sort(values);
            int idx = Mathf.Clamp((int)(values.Length * 0.95f), 0, values.Length - 1);
            return values[idx];
        }

        // ===== 灰度逐通道 RMSE（线性空间） =====
        public static float ChannelRMSE(float[] a, float[] b)
        {
            if (a.Length != b.Length) return float.MaxValue;
            double sum = 0;
            for (int i = 0; i < a.Length; i++)
            {
                double d = a[i] - b[i];
                sum += d * d;
            }
            return (float)Math.Sqrt(sum / a.Length);
        }

        // ===== alpha：Cutout 轮廓 IoU / Blend 线性 RMSE =====
        public static float CutoutIoU(float[] a, float[] b, float cutoff)
        {
            int inter = 0, union = 0;
            for (int i = 0; i < a.Length; i++)
            {
                bool aa = a[i] > cutoff, bb = b[i] > cutoff;
                if (aa && bb) inter++;
                if (aa || bb) union++;
            }
            return union == 0 ? 1f : (float)inter / union;
        }
    }
}
