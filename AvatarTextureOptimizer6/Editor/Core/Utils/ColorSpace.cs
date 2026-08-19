using System;
using UnityEngine;

namespace NetFosa.AvatarTextureOptimizer.Editor.Utils
{
    /// <summary>
    /// 色彩空间与感知颜色数学：sRGB↔线性、sRGB→XYZ→Lab、CIEDE2000。
    /// Color math: sRGB/linear conversion, XYZ/Lab and CIEDE2000.
    /// </summary>
    public static class ColorSpace
    {
        // ---------- sRGB <-> linear ----------
        public static float SrgbToLinear(float c)
        {
            return c <= 0.04045f ? c / 12.92f : Mathf.Pow((c + 0.055f) / 1.055f, 2.4f);
        }

        public static float LinearToSrgb(float c)
        {
            return c <= 0.0031308f ? c * 12.92f : 1.055f * Mathf.Pow(c, 1f / 2.4f) - 0.055f;
        }

        public static Vector3 SrgbToLinear(Vector3 c) => new Vector3(SrgbToLinear(c.x), SrgbToLinear(c.y), SrgbToLinear(c.z));
        public static Vector3 LinearToSrgb(Vector3 c) => new Vector3(LinearToSrgb(c.x), LinearToSrgb(c.y), LinearToSrgb(c.z));

        // ---------- sRGB -> XYZ (D65) ----------
        public static Vector3 SrgbToXyz(Vector3 s)
        {
            var l = SrgbToLinear(s);
            return new Vector3(
                l.x * 0.4124564f + l.y * 0.3575761f + l.z * 0.1804375f,
                l.x * 0.2126729f + l.y * 0.7151522f + l.z * 0.0721750f,
                l.x * 0.0193339f + l.y * 0.1191920f + l.z * 0.9503041f);
        }

        // ---------- XYZ -> Lab (D65) ----------
        private static float Pivot(float t)
        {
            const float epsilon = 216f / 24389f;
            const float kappa = 24389f / 27f;
            return t > epsilon ? Mathf.Pow(t, 1f / 3f) : (kappa * t + 16f) / 116f;
        }

        public static Vector3 XyzToLab(Vector3 xyz)
        {
            const float xn = 0.95047f, yn = 1.0f, zn = 1.08883f;
            var fx = Pivot(xyz.x / xn);
            var fy = Pivot(xyz.y / yn);
            var fz = Pivot(xyz.z / zn);
            return new Vector3(116f * fy - 16f, 500f * (fx - fy), 200f * (fy - fz));
        }

        public static Vector3 SrgbToLab(Vector3 s) => XyzToLab(SrgbToXyz(s));

        // ---------- CIEDE2000 ----------
        public static float Ciede2000(Vector3 lab1, Vector3 lab2)
        {
            const float deg2rad = Mathf.Deg2Rad;
            const float rad2deg = Mathf.Rad2Deg;

            float l1 = lab1.x, a1 = lab1.y, b1 = lab1.z;
            float l2 = lab2.x, a2 = lab2.y, b2 = lab2.z;

            float c1 = Mathf.Sqrt(a1 * a1 + b1 * b1);
            float c2 = Mathf.Sqrt(a2 * a2 + b2 * b2);
            float cbar = (c1 + c2) * 0.5f;
            float cbar7 = cbar * cbar * cbar * cbar * cbar * cbar * cbar;

            float g = 0.5f * (1f - Mathf.Sqrt(cbar7 / (cbar7 + 6103515625f))); // 25^7
            float a1p = (1f + g) * a1;
            float a2p = (1f + g) * a2;

            float c1p = Mathf.Sqrt(a1p * a1p + b1 * b1);
            float c2p = Mathf.Sqrt(a2p * a2p + b2 * b2);

            float h1p = HueAngle(a1p, b1);
            float h2p = HueAngle(a2p, b2);

            float dl = l2 - l1;
            float dc = c2p - c1p;

            float dh;
            if (c1p * c2p == 0f)
            {
                dh = 0f;
            }
            else
            {
                float diff = h2p - h1p;
                if (Mathf.Abs(diff) <= 180f) dh = diff;
                else if (diff > 180f) dh = diff - 360f;
                else dh = diff + 360f;
            }

            float dhp = 2f * Mathf.Sqrt(c1p * c2p) * Mathf.Sin(dh * 0.5f * deg2rad);

            float lp = (l1 + l2) * 0.5f;
            float cp = (c1p + c2p) * 0.5f;

            float hp = 0f;
            if (c1p * c2p != 0f)
            {
                float hsum = h1p + h2p;
                float hdiff = Mathf.Abs(h1p - h2p);
                if (hdiff <= 180f) hp = hsum * 0.5f;
                else if (hsum < 360f) hp = (hsum + 360f) * 0.5f;
                else hp = (hsum - 360f) * 0.5f;
            }

            float t = 1f
                      - 0.17f * Mathf.Cos((hp - 30f) * deg2rad)
                      + 0.24f * Mathf.Cos(2f * hp * deg2rad)
                      + 0.32f * Mathf.Cos((3f * hp + 6f) * deg2rad)
                      - 0.20f * Mathf.Cos((4f * hp - 63f) * deg2rad);

            float dtheta = 30f * Mathf.Exp(-((hp - 275f) / 25f) * ((hp - 275f) / 25f));
            float cp7 = cp * cp * cp * cp * cp * cp * cp;
            float rc = 2f * Mathf.Sqrt(cp7 / (cp7 + 6103515625f));
            float sl = 1f + 0.015f * (lp - 50f) * (lp - 50f) / Mathf.Sqrt(20f + (lp - 50f) * (lp - 50f));
            float sc = 1f + 0.045f * cp;
            float sh = 1f + 0.015f * cp * t;
            float rt = -Mathf.Sin(2f * dtheta * deg2rad) * rc;

            float dlp = dl / sl;
            float dcp = dc / sc;
            float dhp2 = dhp / sh;

            return Mathf.Sqrt(dlp * dlp + dcp * dcp + dhp2 * dhp2 + rt * dcp * dhp2);
        }

        private static float HueAngle(float a, float b)
        {
            if (a == 0f && b == 0f) return 0f;
            float h = Mathf.Atan2(b, a) * Mathf.Rad2Deg;
            if (h < 0f) h += 360f;
            return h;
        }
    }
}
