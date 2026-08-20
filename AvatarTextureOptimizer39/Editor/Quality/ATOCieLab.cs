// Avatar Texture Optimizer (ATO)
// Bilingual comments: English + Simplified Chinese. 双语注释：英文 + 简体中文。

using UnityEngine;

namespace AvatarTextureOptimizer.Editor.Quality
{
    /// <summary>
    /// Color science: linear RGB → CIELAB and ΔE(CIEDE2000).
    /// Reference: Sharma, Wu, Dalal (2005) "The CIEDE2000 Color-Difference Formula".
    ///
    /// 色彩科学：线性 RGB → CIELAB 与 ΔE(CIEDE2000)。参考 Sharma et al. (2005)。
    /// </summary>
    public static class ATOCieLab
    {
        // D65 reference white. D65 参考白。
        private const float Xn = 0.95047f;
        private const float Yn = 1.00000f;
        private const float Zn = 1.08883f;

        /// <summary>Linear sRGB → XYZ (D65). 线性 sRGB → XYZ（D65）。</summary>
        public static void RgbToXyz(float r, float g, float b, out float x, out float y, out float z)
        {
            x = 0.4124564f * r + 0.3575761f * g + 0.1804375f * b;
            y = 0.2126729f * r + 0.7151522f * g + 0.0721750f * b;
            z = 0.0193339f * r + 0.1191920f * g + 0.9503041f * b;
        }

        private static float F(float t)
        {
            const float delta = 6f / 29f;
            if (t > delta * delta * delta)
                return Mathf.Pow(t, 1f / 3f);
            return t / (3f * delta * delta) + 4f / 29f;
        }

        /// <summary>Linear RGB → CIELAB. 线性 RGB → CIELAB。</summary>
        public static void RgbToLab(float r, float g, float b, out float L, out float a, out float bb)
        {
            RgbToXyz(r, g, b, out float x, out float y, out float z);
            float fx = F(x / Xn), fy = F(y / Yn), fz = F(z / Zn);
            L = 116f * fy - 16f;
            a = 500f * (fx - fy);
            bb = 200f * (fy - fz);
        }

        /// <summary>
        /// ΔE CIEDE2000 between two colors given in linear RGB. Inputs are clamped to [0,1].
        /// 两个线性 RGB 颜色之间的 ΔE(CIEDE2000)。输入钳制到 [0,1]。
        /// </summary>
        public static float DeltaE2000Rgb(float r1, float g1, float b1, float r2, float g2, float b2)
        {
            RgbToLab(Mathf.Clamp01(r1), Mathf.Clamp01(g1), Mathf.Clamp01(b1), out float L1, out float a1, out float bb1);
            RgbToLab(Mathf.Clamp01(r2), Mathf.Clamp01(g2), Mathf.Clamp01(b2), out float L2, out float a2, out float bb2);
            return DeltaE2000Lab(L1, a1, bb1, L2, a2, bb2);
        }

        /// <summary>ΔE CIEDE2000 between two CIELAB colors. 两个 CIELAB 颜色间的 ΔE2000。</summary>
        public static float DeltaE2000Lab(float L1, float a1, float b1, float L2, float a2, float b2)
        {
            float C1 = Mathf.Sqrt(a1 * a1 + b1 * b1);
            float C2 = Mathf.Sqrt(a2 * a2 + b2 * b2);
            float Cbar = (C1 + C2) * 0.5f;

            float G = 0.5f * (1f - Mathf.Sqrt(Mathf.Pow(Cbar, 7f) / (Mathf.Pow(Cbar, 7f) + Mathf.Pow(25f, 7f))));

            float a1p = (1f + G) * a1;
            float a2p = (1f + G) * a2;

            float C1p = Mathf.Sqrt(a1p * a1p + b1 * b1);
            float C2p = Mathf.Sqrt(a2p * a2p + b2 * b2);

            float h1p = HueDeg(a1p, b1);
            float h2p = HueDeg(a2p, b2);

            float dLp = L2 - L1;
            float dCp = C2p - C1p;

            float dhp;
            if (C1p * C2p == 0f) dhp = 0f;
            else
            {
                float dh = h2p - h1p;
                if (dh > 180f) dh -= 360f;
                else if (dh < -180f) dh += 360f;
                dhp = 2f * Mathf.Sqrt(C1p * C2p) * Mathf.Sin(Deg2Rad(dh) * 0.5f);
            }

            float Lbarp = (L1 + L2) * 0.5f;
            float Cbarp = (C1p + C2p) * 0.5f;

            float hbarp;
            if (C1p * C2p == 0f) hbarp = h1p + h2p;
            else
            {
                float hsum = h1p + h2p;
                if (Mathf.Abs(h1p - h2p) <= 180f) hbarp = hsum * 0.5f;
                else if (hsum < 360f) hbarp = (hsum + 360f) * 0.5f;
                else hbarp = (hsum - 360f) * 0.5f;
            }

            float T = 1f
                      - 0.17f * Mathf.Cos(Deg2Rad(hbarp - 30f))
                      + 0.24f * Mathf.Cos(Deg2Rad(2f * hbarp))
                      + 0.32f * Mathf.Cos(Deg2Rad(3f * hbarp + 6f))
                      - 0.20f * Mathf.Cos(Deg2Rad(4f * hbarp - 63f));

            float dTheta = 30f * Mathf.Exp(-Mathf.Pow((hbarp - 275f) / 25f, 2f));

            float Rc = 2f * Mathf.Sqrt(Mathf.Pow(Cbarp, 7f) / (Mathf.Pow(Cbarp, 7f) + Mathf.Pow(25f, 7f)));

            float SL = 1f + 0.015f * Mathf.Pow(Lbarp - 50f, 2f) /
                Mathf.Sqrt(20f + Mathf.Pow(Lbarp - 50f, 2f));
            float SC = 1f + 0.045f * Cbarp;
            float SH = 1f + 0.015f * Cbarp * T;

            float RT = -Mathf.Sin(Deg2Rad(2f * dTheta)) * Rc;

            float dE = Mathf.Sqrt(
                Mathf.Pow(dLp / SL, 2f) +
                Mathf.Pow(dCp / SC, 2f) +
                Mathf.Pow(dhp / SH, 2f) +
                RT * (dCp / SC) * (dhp / SH));

            return dE;
        }

        private static float HueDeg(float a, float b)
        {
            if (a == 0f && b == 0f) return 0f;
            float h = Rad2Deg(Mathf.Atan2(b, a));
            if (h < 0f) h += 360f;
            return h;
        }

        private static float Deg2Rad(float d) => d * Mathf.Deg2Rad;
        private static float Rad2Deg(float r) => r * Mathf.Rad2Deg;
    }
}
