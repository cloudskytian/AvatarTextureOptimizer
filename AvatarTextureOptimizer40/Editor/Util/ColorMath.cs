using UnityEngine;

namespace Fosa.Ato.Editor
{
    /// <summary>
    /// Color science helpers (sRGB<->linear, CIEDE2000, SSIM/MS-SSIM, normal angular error).
    /// CPU reference implementations (Burst-friendly struct methods). GPU batch path lives in the
    /// compute shader; these are used for fallback and verification.
    /// 色彩科学工具（sRGB/线性、CIEDE2000、SSIM/MS-SSIM、法线角度误差）的 CPU 参考实现，
    /// 供回退与校验使用；GPU 批量路径在 compute shader 内。
    /// </summary>
    internal static class ColorMath
    {
        // ---- Gamma / linear ----
        public static float GammaToLinear(float c) =>
            c <= 0.04045f ? c / 12.92f : Mathf.Pow((c + 0.055f) / 1.055f, 2.4f);
        public static float LinearToGamma(float c) =>
            c <= 0.0031308f ? c * 12.92f : 1.055f * Mathf.Pow(c, 1f / 2.4f) - 0.055f;

        public static Vector3 RgbToXyzLinear(Vector3 rgb)
        {
            // sRGB D65 / sRGB D65
            return new Vector3(
                rgb.x * 0.4124564f + rgb.y * 0.3575761f + rgb.z * 0.1804375f,
                rgb.x * 0.2126729f + rgb.y * 0.7151522f + rgb.z * 0.0721750f,
                rgb.x * 0.0193339f + rgb.y * 0.1191920f + rgb.z * 0.9503041f);
        }

        // Reference white D65 / D65 参考白
        private const float Xn = 0.95047f, Yn = 1.00000f, Zn = 1.08883f;

        public static void XyzToLab(Vector3 xyz, out float l, out float a, out float b)
        {
            float F(float t) => t > 0.008856f ? Mathf.Cbrt(t) : 7.787f * t + 16f / 116f;
            float fx = F(xyz.x / Xn), fy = F(xyz.y / Yn), fz = F(xyz.z / Zn);
            l = 116f * fy - 16f; a = 500f * (fx - fy); b = 200f * (fy - fz);
        }

        /// <summary>CIEDE2000 color difference between two sRGB (gamma) colors. / 两个 sRGB 颜色的 CIEDE2000 色差。</summary>
        public static float DeltaE2000(Color c1, Color c2)
        {
            Vector3 lin1 = new(GammaToLinear(c1.r), GammaToLinear(c1.g), GammaToLinear(c1.b));
            Vector3 lin2 = new(GammaToLinear(c2.r), GammaToLinear(c2.g), GammaToLinear(c2.b));
            XyzToLab(RgbToXyzLinear(lin1), out float L1, out float a1, out float b1);
            XyzToLab(RgbToXyzLinear(lin2), out float L2, out float a2, out float b2);
            return DeltaE2000Lab(L1, a1, b1, L2, a2, b2);
        }

        // Sharma et al. CIEDE2000 implementation / Sharma 等人的 CIEDE2000 实现
        public static float DeltaE2000Lab(float L1, float a1, float b1, float L2, float a2, float b2)
        {
            float avgL = (L1 + L2) / 2f;
            float C1 = Mathf.Sqrt(a1 * a1 + b1 * b1);
            float C2 = Mathf.Sqrt(a2 * a2 + b2 * b2);
            float avgC = (C1 + C2) / 2f;
            float G = 0.5f * (1f - Mathf.Sqrt(Mathf.Pow(avgC, 7f) / (Mathf.Pow(avgC, 7f) + Mathf.Pow(25f, 7f))));
            float a1p = (1f + G) * a1, a2p = (1f + G) * a2;
            float C1p = Mathf.Sqrt(a1p * a1p + b1 * b1), C2p = Mathf.Sqrt(a2p * a2p + b2 * b2);
            float avgCp = (C1p + C2p) / 2f;
            float h1p = Mathf.Atan2(b1, a1p).RadToDeg(); if (h1p < 0) h1p += 360f;
            float h2p = Mathf.Atan2(b2, a2p).RadToDeg(); if (h2p < 0) h2p += 360f;
            float dLp = L2 - L1;
            float dCp = C2p - C1p;
            float dhp = 0f;
            if (Mathf.Abs(C1p) > 1e-9f && Mathf.Abs(C2p) > 1e-9f)
            {
                dhp = h2p - h1p;
                if (dhp > 180f) dhp -= 360f;
                else if (dhp < -180f) dhp += 360f;
            }
            float dHp = 2f * Mathf.Sqrt(C1p * C2p) * Mathf.Sin((dhp / 2f) * Mathf.Deg2Rad);
            float avgLp = (L1 + L2) / 2f;
            float avgHp = Mathf.Abs(h1p - h2p) > 180f ? (h1p + h2p + 360f) / 2f : (h1p + h2p) / 2f;
            float T = 1f - 0.17f * Mathf.Cos((avgHp - 30f) * Mathf.Deg2Rad)
                        + 0.24f * Mathf.Cos((2f * avgHp) * Mathf.Deg2Rad)
                        + 0.32f * Mathf.Cos((3f * avgHp + 6f) * Mathf.Deg2Rad)
                        - 0.20f * Mathf.Cos((4f * avgHp - 63f) * Mathf.Deg2Rad);
            float SL = 1f + 0.015f * (avgLp - 50f) * (avgLp - 50f) / Mathf.Sqrt(20f + (avgLp - 50f) * (avgLp - 50f));
            float SC = 1f + 0.045f * avgCp;
            float SH = 1f + 0.015f * avgCp * T;
            float dTheta = 30f * Mathf.Exp(-((avgHp - 275f) / 25f) * ((avgHp - 275f) / 25f));
            float RC = 2f * Mathf.Sqrt(Mathf.Pow(avgCp, 7f) / (Mathf.Pow(avgCp, 7f) + Mathf.Pow(25f, 7f)));
            float RT = -2f * RC * Mathf.Sin(2f * dTheta * Mathf.Deg2Rad);
            float de = Mathf.Sqrt(
                (dLp / SL) * (dLp / SL) +
                (dCp / SC) * (dCp / SC) +
                (dHp / SH) * (dHp / SH) +
                RT * (dCp / SC) * (dHp / SH));
            return float.IsNaN(de) ? 0f : de;
        }

        private static float RadToDeg(this float r) => r * Mathf.Rad2Deg;

        /// <summary>
        /// Structural similarity for a window of precomputed means/variances. K1=0.01 K2=0.03 (8-bit).
        /// 给定窗口均值/方差的 SSIM。
        /// </summary>
        public static float Ssim(float muX, float muY, float sigmaX2, float sigmaY2, float sigmaXY, float L = 1f)
        {
            float K1 = 0.01f, K2 = 0.03f;
            float C1 = K1 * L * (K1 * L), C2 = K2 * L * (K2 * L);
            float num = (2f * muX * muY + C1) * (2f * sigmaXY + C2);
            float den = (muX * muX + muY * muY + C1) * (sigmaX2 + sigmaY2 + C2);
            return num / den;
        }

        /// <summary>Decode a tangent-space normal map texel (DXT5nm/RGB) to a unit vector in [-1,1]. / 解码法线贴图纹素。</summary>
        public static Vector3 DecodeNormal(Color c)
        {
            // Unity-style: supports both RGB and AG (DXT5nm) / 同时支持 RGB 与 AG(DXT5nm)
            float x, y, z;
            if (Mathf.Abs(c.a - c.b) > 0.02f || c.r < 0.99f || c.g < 0.99f)
            {
                x = c.r * 2f - 1f; y = c.g * 2f - 1f;
                z = Mathf.Sqrt(Mathf.Max(0f, 1f - x * x - y * y));
                return new Vector3(x, y, z).normalized;
            }
            // DXT5nm: red stored in alpha, green in green / DXT5nm：红存在 alpha，绿存在 green
            x = c.a * 2f - 1f; y = c.g * 2f - 1f;
            z = Mathf.Sqrt(Mathf.Max(0f, 1f - x * x - y * y));
            return new Vector3(x, y, z).normalized;
        }

        public static float AngleDeg(Vector3 a, Vector3 b)
        {
            float d = Vector3.Dot(a.normalized, b.normalized);
            d = Mathf.Clamp(d, -1f, 1f);
            return Mathf.Acos(d) * Mathf.Rad2Deg;
        }
    }
}
