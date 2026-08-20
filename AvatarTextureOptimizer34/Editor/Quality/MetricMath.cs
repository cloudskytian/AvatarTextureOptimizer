// AvatarTextureOptimizer - MetricMath
// EN: Color math used by the quality metrics: sRGB<->linear, premultiply, Lab, CIEDE2000.
// CN: 质量指标使用的色彩数学：sRGB<->线性、预乘、Lab、CIEDE2000。
using System;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer
{
    /// <summary>
    /// EN: Pure color math. All metrics compare in linear space (spec: 线性空间重采样).
    /// CN: 纯色彩数学。所有指标在线性空间比较（按需求：线性空间重采样）。
    /// </summary>
    public static class MetricMath
    {
        public static float SrgbToLinear(float c)
        {
            if (c <= 0.04045f) return c / 12.92f;
            return Mathf.Pow((c + 0.055f) / 1.055f, 2.4f);
        }

        public static float LinearToSrgb(float c)
        {
            if (c <= 0.0031308f) return c * 12.92f;
            return 1.055f * Mathf.Pow(c, 1f / 2.4f) - 0.055f;
        }

        /// <summary>EN: sRGB byte → linear float. / CN: sRGB 字节转线性浮点。</summary>
        public static float SrgbToLinear(byte c) => SrgbToLinear(c / 255f);

        // ------------------------------------------------------------- CIEDE2000

        // EN: sRGB (0..1) → CIELAB (D65). / CN: sRGB（0..1）→ CIELAB（D65）。
        public static void SrgbToLab(float r, float g, float b, out float L, out float a, out float b2)
        {
            float lr = SrgbToLinear(r), lg = SrgbToLinear(g), lb = SrgbToLinear(b);
            float X = 0.4124564f * lr + 0.3575761f * lg + 0.1804375f * lb;
            float Y = 0.2126729f * lr + 0.7151522f * lg + 0.0721750f * lb;
            float Z = 0.0193339f * lr + 0.1191920f * lg + 0.9503041f * lb;
            X /= 0.95047f; Y /= 1.00000f; Z /= 1.08883f;

            float F(float t) => t > 0.008856f ? Mathf.Pow(t, 1f / 3f) : (7.787f * t + 16f / 116f);
            float fx = F(X), fy = F(Y), fz = F(Z);
            L = 116f * fy - 16f;
            a = 500f * (fx - fy);
            b2 = 200f * (fy - fz);
        }

        /// <summary>EN: CIEDE2000 color difference (Sharma et al. 2005). / CN: CIEDE2000 色差（Sharma 等 2005）。</summary>
        public static float Ciede2000(float L1, float a1, float b1, float L2, float a2, float b2)
        {
            float C1 = Mathf.Sqrt(a1 * a1 + b1 * b1);
            float C2 = Mathf.Sqrt(a2 * a2 + b2 * b2);
            float Cbar = (C1 + C2) * 0.5f;
            float Cbar7 = Cbar * Cbar * Cbar * Cbar * Cbar * Cbar * Cbar;
            float G = 0.5f * (1f - Mathf.Sqrt(Cbar7 / (Cbar7 + 6103515625f))); // 25^7
            float a1p = (1f + G) * a1;
            float a2p = (1f + G) * a2;
            float C1p = Mathf.Sqrt(a1p * a1p + b1 * b1);
            float C2p = Mathf.Sqrt(a2p * a2p + b2 * b2);
            float h1p = Hue(a1p, b1);
            float h2p = Hue(a2p, b2);
            float dLp = L2 - L1;
            float dCp = C2p - C1p;

            float dh = h2p - h1p;
            if (C1p * C2p == 0f) dh = 0f;
            else if (Mathf.Abs(h2p - h1p) <= 180f) dh = h2p - h1p;
            else if (h2p - h1p > 180f) dh = h2p - h1p - 360f;
            else dh = h2p - h1p + 360f;
            float dHp = 2f * Mathf.Sqrt(C1p * C2p) * Mathf.Sin(dh * Mathf.Deg2Rad * 0.5f);

            float Lbp = (L1 + L2) * 0.5f;
            float Cbp = (C1p + C2p) * 0.5f;
            float hbp = 0f;
            if (C1p * C2p != 0f)
            {
                if (Mathf.Abs(h1p - h2p) <= 180f) hbp = (h1p + h2p) * 0.5f;
                else if (Mathf.Abs(h1p - h2p) > 180f && h1p + h2p < 360f) hbp = (h1p + h2p + 360f) * 0.5f;
                else hbp = (h1p + h2p - 360f) * 0.5f;
            }

            float T = 1f - 0.17f * Mathf.Cos((hbp - 30f) * Mathf.Deg2Rad)
                        + 0.24f * Mathf.Cos(2f * hbp * Mathf.Deg2Rad)
                        + 0.32f * Mathf.Cos((3f * hbp + 6f) * Mathf.Deg2Rad)
                        - 0.20f * Mathf.Cos((4f * hbp - 63f) * Mathf.Deg2Rad);
            float dTheta = 30f * Mathf.Exp(-((hbp - 275f) / 25f) * ((hbp - 275f) / 25f));
            float Cbp7 = Cbp * Cbp * Cbp * Cbp * Cbp * Cbp * Cbp;
            float Rc = 2f * Mathf.Sqrt(Cbp7 / (Cbp7 + 6103515625f));
            float Sl = 1f + 0.015f * (Lbp - 50f) * (Lbp - 50f) / Mathf.Sqrt(20f + (Lbp - 50f) * (Lbp - 50f));
            float Sc = 1f + 0.045f * Cbp;
            float Sh = 1f + 0.015f * Cbp * T;
            float Rt = -Mathf.Sin(2f * dTheta * Mathf.Deg2Rad) * Rc;

            float dLpSl = dLp / Sl;
            float dCpSc = dCp / Sc;
            float dHpSh = dHp / Sh;
            return Mathf.Sqrt(dLpSl * dLpSl + dCpSc * dCpSc + dHpSh * dHpSh + Rt * dCpSc * dHpSh);
        }

        private static float Hue(float a, float b)
        {
            if (a == 0f && b == 0f) return 0f;
            float h = Mathf.Atan2(b, a) * Mathf.Rad2Deg;
            if (h < 0f) h += 360f;
            return h;
        }
    }
}
