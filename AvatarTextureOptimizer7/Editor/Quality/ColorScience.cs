using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Linear RGB ↔ Lab and CIEDE2000. Burst-friendly statics.
    /// 线性 RGB ↔ Lab 与 CIEDE2000。可进 Burst。
    /// </summary>
    [BurstCompile]
    public static class ColorScience
    {
        public static float LinearLuma(Color c)
        {
            return 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;
        }

        public static void RgbToLab(float r, float g, float b, out float L, out float a, out float bb)
        {
            r = PivotXyz(r);
            g = PivotXyz(g);
            b = PivotXyz(b);
            // sRGB D65 linear → XYZ → Lab
            var x = r * 0.4124564f + g * 0.3575761f + b * 0.1804375f;
            var y = r * 0.2126729f + g * 0.7151522f + b * 0.0721750f;
            var z = r * 0.0193339f + g * 0.1191920f + b * 0.9503041f;
            x = PivotLab(x / 0.95047f);
            y = PivotLab(y / 1.00000f);
            z = PivotLab(z / 1.08883f);
            L = 116f * y - 16f;
            a = 500f * (x - y);
            bb = 200f * (y - z);
        }

        static float PivotXyz(float c)
        {
            // already linear
            return math.max(c, 0f);
        }

        static float PivotLab(float t)
        {
            const float e = 216f / 24389f;
            const float k = 24389f / 27f;
            return t > e ? math.pow(t, 1f / 3f) : (k * t + 16f) / 116f;
        }

        public static float Ciede2000(float L1, float a1, float b1, float L2, float a2, float b2)
        {
            const float kL = 1f, kC = 1f, kH = 1f;
            var C1 = math.sqrt(a1 * a1 + b1 * b1);
            var C2 = math.sqrt(a2 * a2 + b2 * b2);
            var Cab = 0.5f * (C1 + C2);
            var Cab7 = math.pow(Cab, 7f);
            var G = 0.5f * (1f - math.sqrt(Cab7 / (Cab7 + 6103515625f))); // 25^7
            var a1p = (1f + G) * a1;
            var a2p = (1f + G) * a2;
            var C1p = math.sqrt(a1p * a1p + b1 * b1);
            var C2p = math.sqrt(a2p * a2p + b2 * b2);
            var h1p = Atan2Deg(b1, a1p);
            var h2p = Atan2Deg(b2, a2p);
            var dLp = L2 - L1;
            var dCp = C2p - C1p;
            var dhp = 0f;
            if (C1p * C2p != 0f)
            {
                var dh = h2p - h1p;
                if (dh > 180f) dh -= 360f;
                else if (dh < -180f) dh += 360f;
                dhp = dh;
            }

            var dHp = 2f * math.sqrt(C1p * C2p) * math.sin(math.radians(dhp * 0.5f));
            var Lbar = 0.5f * (L1 + L2);
            var Cpbar = 0.5f * (C1p + C2p);
            var hbar = h1p + h2p;
            if (C1p * C2p != 0f)
            {
                var dh = math.abs(h1p - h2p);
                if (dh > 180f) hbar = (h1p + h2p + 360f) * 0.5f;
                else hbar = (h1p + h2p) * 0.5f;
            }

            var T = 1f
                    - 0.17f * math.cos(math.radians(hbar - 30f))
                    + 0.24f * math.cos(math.radians(2f * hbar))
                    + 0.32f * math.cos(math.radians(3f * hbar + 6f))
                    - 0.20f * math.cos(math.radians(4f * hbar - 63f));
            var dTh = 30f * math.exp(-math.pow((hbar - 275f) / 25f, 2f));
            var Rc = 2f * math.sqrt(math.pow(Cpbar, 7f) / (math.pow(Cpbar, 7f) + 6103515625f));
            var Sl = 1f + 0.015f * math.pow(Lbar - 50f, 2f) / math.sqrt(20f + math.pow(Lbar - 50f, 2f));
            var Sc = 1f + 0.045f * Cpbar;
            var Sh = 1f + 0.015f * Cpbar * T;
            var Rt = -math.sin(math.radians(2f * dTh)) * Rc;
            var dE = math.sqrt(
                math.pow(dLp / (kL * Sl), 2f) +
                math.pow(dCp / (kC * Sc), 2f) +
                math.pow(dHp / (kH * Sh), 2f) +
                Rt * (dCp / (kC * Sc)) * (dHp / (kH * Sh)));
            return dE;
        }

        static float Atan2Deg(float y, float x)
        {
            var h = math.degrees(math.atan2(y, x));
            if (h < 0f) h += 360f;
            return h;
        }
    }
}
