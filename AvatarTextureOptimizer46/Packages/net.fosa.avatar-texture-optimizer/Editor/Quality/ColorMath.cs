// SPDX-License-Identifier: MIT
// EN: Colour space conversions and the CIEDE2000 difference formula.
// ZH: 色彩空间转换与 CIEDE2000 色差公式。

using Unity.Burst;
using Unity.Mathematics;

namespace Net.Fosa.AvatarTextureOptimizer.Editor.Quality
{
    /// <summary>
    /// EN: Burst friendly colour maths. All inputs are linear RGB in [0,1].
    /// ZH: 适配 Burst 的色彩数学。所有输入均为 [0,1] 的线性 RGB。
    /// </summary>
    [BurstCompile]
    public static class ColorMath
    {
        /// <summary>EN: Rec.709 relative luminance of a linear colour. ZH: 线性颜色的 Rec.709 相对亮度。</summary>
        public static float Luminance(float3 linearRgb)
            => 0.2126f * linearRgb.x + 0.7152f * linearRgb.y + 0.0722f * linearRgb.z;

        /// <summary>EN: Linear RGB to CIE XYZ under D65. ZH: D65 下线性 RGB 转 CIE XYZ。</summary>
        public static float3 LinearToXyz(float3 c)
        {
            return new float3(
                0.4124564f * c.x + 0.3575761f * c.y + 0.1804375f * c.z,
                0.2126729f * c.x + 0.7151522f * c.y + 0.0721750f * c.z,
                0.0193339f * c.x + 0.1191920f * c.y + 0.9503041f * c.z);
        }

        /// <summary>EN: CIE XYZ to CIE L*a*b* under the D65 white point. ZH: D65 白点下 CIE XYZ 转 CIE L*a*b*。</summary>
        public static float3 XyzToLab(float3 xyz)
        {
            const float xn = 0.95047f, yn = 1.0f, zn = 1.08883f;
            float fx = LabF(xyz.x / xn);
            float fy = LabF(xyz.y / yn);
            float fz = LabF(xyz.z / zn);
            return new float3(116f * fy - 16f, 500f * (fx - fy), 200f * (fy - fz));
        }

        private static float LabF(float t)
        {
            const float d = 6f / 29f;
            return t > d * d * d ? math.pow(t, 1f / 3f) : t / (3f * d * d) + 4f / 29f;
        }

        /// <summary>EN: Convenience: linear RGB straight to L*a*b*. ZH: 便捷方法：线性 RGB 直接转 L*a*b*。</summary>
        public static float3 LinearToLab(float3 c) => XyzToLab(LinearToXyz(math.max(c, 0f)));

        /// <summary>
        /// EN: CIEDE2000 colour difference (Sharma, Wu and Dalal 2005). Inputs are L*a*b* triples.
        /// ZH: CIEDE2000 色差（Sharma/Wu/Dalal, 2005）。输入为 L*a*b* 三元组。
        /// </summary>
        public static float DeltaE2000(float3 lab1, float3 lab2)
        {
            float l1 = lab1.x, a1 = lab1.y, b1 = lab1.z;
            float l2 = lab2.x, a2 = lab2.y, b2 = lab2.z;

            float c1 = math.sqrt(a1 * a1 + b1 * b1);
            float c2 = math.sqrt(a2 * a2 + b2 * b2);
            float cBar = (c1 + c2) * 0.5f;

            float cBar7 = math.pow(cBar, 7f);
            float g = 0.5f * (1f - math.sqrt(cBar7 / (cBar7 + 6103515625f))); // 25^7

            float a1p = (1f + g) * a1;
            float a2p = (1f + g) * a2;
            float c1p = math.sqrt(a1p * a1p + b1 * b1);
            float c2p = math.sqrt(a2p * a2p + b2 * b2);

            float h1p = HueAngle(b1, a1p);
            float h2p = HueAngle(b2, a2p);

            float dLp = l2 - l1;
            float dCp = c2p - c1p;

            float dhp;
            if (c1p * c2p == 0f) dhp = 0f;
            else
            {
                dhp = h2p - h1p;
                if (dhp > 180f) dhp -= 360f;
                else if (dhp < -180f) dhp += 360f;
            }
            float dHp = 2f * math.sqrt(c1p * c2p) * math.sin(math.radians(dhp * 0.5f));

            float lBarP = (l1 + l2) * 0.5f;
            float cBarP = (c1p + c2p) * 0.5f;

            float hBarP;
            if (c1p * c2p == 0f) hBarP = h1p + h2p;
            else
            {
                float diff = math.abs(h1p - h2p);
                if (diff <= 180f) hBarP = (h1p + h2p) * 0.5f;
                else if (h1p + h2p < 360f) hBarP = (h1p + h2p + 360f) * 0.5f;
                else hBarP = (h1p + h2p - 360f) * 0.5f;
            }

            float t = 1f
                      - 0.17f * math.cos(math.radians(hBarP - 30f))
                      + 0.24f * math.cos(math.radians(2f * hBarP))
                      + 0.32f * math.cos(math.radians(3f * hBarP + 6f))
                      - 0.20f * math.cos(math.radians(4f * hBarP - 63f));

            float dTheta = 30f * math.exp(-((hBarP - 275f) / 25f) * ((hBarP - 275f) / 25f));
            float cBarP7 = math.pow(cBarP, 7f);
            float rc = 2f * math.sqrt(cBarP7 / (cBarP7 + 6103515625f));
            float lBarP50 = (lBarP - 50f) * (lBarP - 50f);
            float sl = 1f + 0.015f * lBarP50 / math.sqrt(20f + lBarP50);
            float sc = 1f + 0.045f * cBarP;
            float sh = 1f + 0.015f * cBarP * t;
            float rt = -math.sin(math.radians(2f * dTheta)) * rc;

            float term1 = dLp / sl;
            float term2 = dCp / sc;
            float term3 = dHp / sh;
            return math.sqrt(term1 * term1 + term2 * term2 + term3 * term3 + rt * term2 * term3);
        }

        private static float HueAngle(float b, float ap)
        {
            if (b == 0f && ap == 0f) return 0f;
            float h = math.degrees(math.atan2(b, ap));
            return h < 0f ? h + 360f : h;
        }
    }
}
