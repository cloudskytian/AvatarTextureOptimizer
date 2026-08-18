// MathUtility.cs / MathUtility.cs
// Common math helpers for UV processing, colour spaces, etc.
// UV处理、色彩空间等通用数学工具。

using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.Editor.Util
{
    public static class MathUtility
    {
        /// <summary>
        /// Linear-space sRGB -> Linear conversion for a single channel.
        /// 单通道sRGB->Linear转换（线性空间）。
        /// </summary>
        public static float SRGBToLinear(float c)
        {
            if (c <= 0.04045f) return c / 12.92f;
            return Mathf.Pow((c + 0.055f) / 1.055f, 2.4f);
        }

        /// <summary>
        /// Linear -> sRGB conversion for a single channel.
        /// 单通道Linear->sRGB转换。
        /// </summary>
        public static float LinearToSRGB(float c)
        {
            if (c <= 0.0031308f) return 12.92f * c;
            return 1.055f * Mathf.Pow(c, 1f / 2.4f) - 0.055f;
        }

        /// <summary>
        /// CIEDE2000 colour difference between two Lab colours. Returns ΔE.
        /// 两个Lab色彩之间的CIEDE2000色差。返回ΔE。
        /// Implementation based on the standard CIEDE2000 formula.
        /// </summary>
        public static float CIEDE2000(float L1, float a1, float b1, float L2, float a2, float b2)
        {
            float L_ = (L1 + L2) * 0.5f;
            float C1 = Mathf.Sqrt(a1 * a1 + b1 * b1);
            float C2 = Mathf.Sqrt(a2 * a2 + b2 * b2);
            float C_ = (C1 + C2) * 0.5f;
            float C7 = C_ * C_ * C_ * C_ * C_ * C_ * C_;
            float G = 0.5f * (1f - Mathf.Sqrt(C7 / (C7 + Mathf.Pow(25f, 7))));
            float a1p = a1 * (1f + G);
            float a2p = a2 * (1f + G);
            float C1p = Mathf.Sqrt(a1p * a1p + b1 * b1);
            float C2p = Mathf.Sqrt(a2p * a2p + b2 * b2);
            float C_p = (C1p + C2p) * 0.5f;
            float h1p = Mathf.Atan2(b1, a1p) * Mathf.Rad2Deg; if (h1p < 0) h1p += 360;
            float h2p = Mathf.Atan2(b2, a2p) * Mathf.Rad2Deg; if (h2p < 0) h2p += 360;

            float hp;
            if (Mathf.Abs(C1p * C2p) < 1e-6f) hp = h1p + h2p;
            else if (Mathf.Abs(h1p - h2p) <= 180) hp = (h1p + h2p) * 0.5f;
            else if (h1p + h2p < 360) hp = (h1p + h2p + 360) * 0.5f;
            else hp = (h1p + h2p - 360) * 0.5f;

            float T = 1f - 0.17f * Mathf.Cos((hp - 30f) * Mathf.Deg2Rad)
                         + 0.24f * Mathf.Cos(2f * hp * Mathf.Deg2Rad)
                         + 0.32f * Mathf.Cos((3f * hp + 6f) * Mathf.Deg2Rad)
                         - 0.20f * Mathf.Cos((4f * hp - 63f) * Mathf.Deg2Rad);

            float dhp;
            if (Mathf.Abs(C1p * C2p) < 1e-6f) dhp = 0f;
            else if (Mathf.Abs(h1p - h2p) <= 180) dhp = h2p - h1p;
            else if (h2p - h1p > 180) dhp = h2p - h1p - 360;
            else dhp = h2p - h1p + 360;

            float dLp = L2 - L1;
            float dCp = C2p - C1p;
            float dHp = 2f * Mathf.Sqrt(C1p * C2p) * Mathf.Sin(0.5f * dhp * Mathf.Deg2Rad);

            float L_p = (L1 + L2) * 0.5f;
            float L_pm50sq = (L_p - 50f) * (L_p - 50f);
            float SL = 1f + 0.015f * L_pm50sq / Mathf.Sqrt(20f + L_pm50sq);
            float SC = 1f + 0.045f * C_p;
            float SH = 1f + 0.015f * C_p * T;

            float C_p7 = C_p * C_p * C_p * C_p * C_p * C_p * C_p;
            float RC = 2f * Mathf.Sqrt(C_p7 / (C_p7 + Mathf.Pow(25f, 7)));
            float dtheta = 30f * Mathf.Exp(-((hp - 275f) / 25f) * ((hp - 275f) / 25f));
            float RT = -RC * Mathf.Sin(2f * dtheta * Mathf.Deg2Rad);

            float dE = Mathf.Sqrt(
                (dLp / (SL)) * (dLp / (SL)) +
                (dCp / (SC)) * (dCp / (SC)) +
                (dHp / (SH)) * (dHp / (SH)) +
                RT * (dCp / (SC)) * (dHp / (SH))
            );
            return dE;
        }

        /// <summary>
        /// Convert linear RGB to CIE Lab. Inputs are linear in [0,1], uses D65 white point.
        /// 线性RGB转CIE Lab。输入线性[0,1]，使用D65白点。
        /// </summary>
        public static void LinearRGBToLab(float r, float g, float b, out float L, out float a, out float bl)
        {
            // sRGB/Linear -> XYZ (D65)
            float x = 0.4124564f * r + 0.3575761f * g + 0.1804375f * b;
            float y = 0.2126729f * r + 0.7151522f * g + 0.0721750f * b;
            float z = 0.0193339f * r + 0.1191920f * g + 0.9503041f * b;

            // D65 reference white / D65参考白点
            const float xn = 0.95047f, yn = 1.00000f, zn = 1.08883f;
            float xr = x / xn, yr = y / yn, zr = z / zn;
            const float eps = 216f / 24389f;
            const float kappa = 24389f / 27f;
            float fx = xr > eps ? Mathf.Pow(xr, 1f / 3f) : (kappa * xr + 16f) / 116f;
            float fy = yr > eps ? Mathf.Pow(yr, 1f / 3f) : (kappa * yr + 16f) / 116f;
            float fz = zr > eps ? Mathf.Pow(zr, 1f / 3f) : (kappa * zr + 16f) / 116f;
            L = 116f * fy - 16f;
            a = 500f * (fx - fy);
            bl = 200f * (fy - fz);
        }

        /// <summary>
        /// Decode a normal-map texel (DXTnm/standard-style stored in [0,1]) to a unit vector in tangent space.
        /// 解码法线贴图纹素（[0,1]存储）到切线空间单位向量。
        /// </summary>
        public static Vector3 DecodeNormal(Color c, bool useDXTnm = false)
        {
            float x = c.r * 2f - 1f;
            float y;
            if (useDXTnm)
            {
                // DXTnm stores normal.x in alpha and derives z; standard derivation
                // DXTnm在alpha存normal.x并推导z
                x = c.a * 2f - 1f;
                y = c.g * 2f - 1f;
            }
            else
            {
                y = c.g * 2f - 1f;
            }
            float zsq = 1f - x * x - y * y;
            float z = zsq > 0 ? Mathf.Sqrt(zsq) : 0;
            return new Vector3(x, y, z).normalized;
        }

        /// <summary>
        /// Angle between two unit vectors in degrees.
        /// 两个单位向量间的角度（度）。
        /// </summary>
        public static float AngleDeg(Vector3 a, Vector3 b)
        {
            float d = Vector3.Dot(a, b);
            d = Mathf.Clamp(d, -1f, 1f);
            return Mathf.Acos(d) * Mathf.Rad2Deg;
        }

        /// <summary>
        /// Find tight axis-aligned bounding box of a set of UV points.
        /// 计算一组UV点的轴对齐包围盒。
        /// </summary>
        public static Rect BoundingBox(Vector2[] uvs)
        {
            if (uvs == null || uvs.Length == 0) return new Rect(0, 0, 0, 0);
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            foreach (var uv in uvs)
            {
                if (uv.x < minX) minX = uv.x;
                if (uv.y < minY) minY = uv.y;
                if (uv.x > maxX) maxX = uv.x;
                if (uv.y > maxY) maxY = uv.y;
            }
            return new Rect(minX, minY, maxX - minX, maxY - minY);
        }

        /// <summary>
        /// Returns true if the UV set can be translated into [0,1] without crossing wrap seams.
        /// 判断UV是否可以整体平移归一到[0,1]而不跨越wrap缝。
        /// </summary>
        public static bool CanNormalizeUVs(Vector2[] uvs, out Vector2 offset)
        {
            offset = Vector2.zero;
            var bb = BoundingBox(uvs);
            if (bb.width <= 1f && bb.height <= 1f)
            {
                float shiftX = Mathf.Floor(bb.xMin);
                float shiftY = Mathf.Floor(bb.yMin);
                offset = new Vector2(-shiftX, -shiftY);
                return true;
            }
            return false;
        }
    }
}
