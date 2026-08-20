using System;
using Unity.Mathematics;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Target-quality evaluation. Linear resample; transparent uses premultiplied-alpha downsample.
    /// 目标质量评估。线性空间重采样；透明贴图预乘 alpha 下采样。
    /// Compare by bilinear-upsampling the scaled island back to the original bbox.
    /// 将缩小后的岛双线性上采样回原包围盒再比较。
    /// Burst CPU always; GPU compute used when available for batch SSIM/ΔE.
    /// 始终有 Burst CPU；有 GPU 时批量跑 SSIM/ΔE。
    /// </summary>
    public static class QualityEval
    {
        public struct Metrics
        {
            public float MsSsim;
            public float Ciede2000;
            public float CutoutIou;
            public float BlendRmse;
            public float NormalMeanDeg;
            public float NormalP95Deg;
            public float GrayRmse;
        }

        public static bool Passes(Metrics m, QualityParameters q, TextureUsageKind usage, AlphaEvalMode alpha, int shortEdge)
        {
            if (usage == TextureUsageKind.Normal)
                return m.NormalMeanDeg <= q.normalMeanAngleDegMax && m.NormalP95Deg <= q.normalP95AngleDegMax;
            if (usage == TextureUsageKind.Gray || usage == TextureUsageKind.Mask)
                return m.GrayRmse <= q.grayRmseMax;

            bool ok = true;
            if (shortEdge >= 11)
            {
                ok &= m.MsSsim >= q.msSsimMin;
                ok &= m.Ciede2000 <= q.ciede2000Max;
            }
            if (alpha == AlphaEvalMode.Cutout) ok &= m.CutoutIou >= q.cutoutIouMin;
            if (alpha == AlphaEvalMode.Blend) ok &= m.BlendRmse <= q.blendAlphaRmseMax;
            return ok;
        }

        /// <summary>
        /// Binary search uniform scale then anisotropic refine. Returns scale in (0,1] relative to original island px.
        /// 先均匀二分再双轴细化。返回相对原岛像素的 (0,1] 缩放。
        /// </summary>
        public static Vector2 FindScale(Color32[] orig, int ow, int oh, QualityParameters q,
            TextureUsageKind usage, AlphaEvalMode alpha, float cutoff, bool nearLossless,
            int minPx, int densityMinPx, int densityMaxPx)
        {
            int shortOrig = Math.Max(1, Math.Min(ow, oh));
            if (nearLossless || q.IsNearLossless)
                return Vector2.one;

            int loBound = Math.Max(1, minPx);
            int hiW = Math.Min(ow, densityMaxPx > 0 ? Math.Max(densityMaxPx, 1) : ow);
            int hiH = Math.Min(oh, densityMaxPx > 0 ? Math.Max(densityMaxPx, 1) : oh);
            if (densityMinPx > 0)
            {
                hiW = Math.Max(hiW, Math.Min(ow, densityMinPx));
                hiH = Math.Max(hiH, Math.Min(oh, densityMinPx));
            }

            // Uniform search on short edge. / 短边均匀搜索。
            int lo = loBound, hi = shortOrig, best = shortOrig;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                float s = mid / (float)shortOrig;
                int nw = Math.Max(1, Mathf.RoundToInt(ow * s));
                int nh = Math.Max(1, Mathf.RoundToInt(oh * s));
                var m = Evaluate(orig, ow, oh, nw, nh, usage, alpha, cutoff);
                if (Passes(m, q, usage, alpha, shortOrig))
                {
                    best = mid;
                    hi = mid - 1;
                }
                else lo = mid + 1;
            }

            float us = best / (float)shortOrig;
            int bw = Math.Max(1, Mathf.RoundToInt(ow * us));
            int bh = Math.Max(1, Mathf.RoundToInt(oh * us));

            // Anisotropic refine independently. / 双轴独立细化。
            int bestW = bw;
            lo = loBound; hi = bw;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                var m = Evaluate(orig, ow, oh, mid, bh, usage, alpha, cutoff);
                if (Passes(m, q, usage, alpha, shortOrig)) { bestW = mid; hi = mid - 1; }
                else lo = mid + 1;
            }
            int bestH = bh;
            lo = loBound; hi = bh;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                var m = Evaluate(orig, ow, oh, bestW, mid, usage, alpha, cutoff);
                if (Passes(m, q, usage, alpha, shortOrig)) { bestH = mid; hi = mid - 1; }
                else lo = mid + 1;
            }

            return new Vector2(bestW / (float)Math.Max(1, ow), bestH / (float)Math.Max(1, oh));
        }

        public static Metrics Evaluate(Color32[] orig, int ow, int oh, int nw, int nh,
            TextureUsageKind usage, AlphaEvalMode alpha, float cutoff)
        {
            var down = Downsample(orig, ow, oh, nw, nh, alpha != AlphaEvalMode.Opaque);
            var up = Upsample(down, nw, nh, ow, oh);
            return Compare(orig, up, ow, oh, usage, alpha, cutoff);
        }

        public static Color32[] Downsample(Color32[] src, int sw, int sh, int dw, int dh, bool premult)
        {
            var dst = new Color32[dw * dh];
            for (int y = 0; y < dh; y++)
            for (int x = 0; x < dw; x++)
            {
                float x0 = x / (float)dw * sw;
                float x1 = (x + 1) / (float)dw * sw;
                float y0 = y / (float)dh * sh;
                float y1 = (y + 1) / (float)dh * sh;
                float r = 0, g = 0, b = 0, a = 0, wsum = 0;
                int ix0 = Mathf.Clamp((int)x0, 0, sw - 1);
                int ix1 = Mathf.Clamp((int)Math.Ceiling(x1) - 1, 0, sw - 1);
                int iy0 = Mathf.Clamp((int)y0, 0, sh - 1);
                int iy1 = Mathf.Clamp((int)Math.Ceiling(y1) - 1, 0, sh - 1);
                for (int iy = iy0; iy <= iy1; iy++)
                for (int ix = ix0; ix <= ix1; ix++)
                {
                    var c = src[iy * sw + ix];
                    float rf = SrgbToLinear(c.r / 255f);
                    float gf = SrgbToLinear(c.g / 255f);
                    float bf = SrgbToLinear(c.b / 255f);
                    float af = c.a / 255f;
                    if (premult) { rf *= af; gf *= af; bf *= af; }
                    r += rf; g += gf; b += bf; a += af; wsum += 1f;
                }
                if (wsum < 1e-8f) wsum = 1f;
                r /= wsum; g /= wsum; b /= wsum; a /= wsum;
                if (premult && a > 1e-6f) { r /= a; g /= a; b /= a; }
                dst[y * dw + x] = new Color32(
                    (byte)Mathf.Clamp(LinearToSrgb(r) * 255f, 0, 255),
                    (byte)Mathf.Clamp(LinearToSrgb(g) * 255f, 0, 255),
                    (byte)Mathf.Clamp(LinearToSrgb(b) * 255f, 0, 255),
                    (byte)Mathf.Clamp(a * 255f, 0, 255));
            }
            return dst;
        }

        public static Color32[] Upsample(Color32[] src, int sw, int sh, int dw, int dh)
        {
            var dst = new Color32[dw * dh];
            for (int y = 0; y < dh; y++)
            for (int x = 0; x < dw; x++)
            {
                float u = (x + 0.5f) / dw * sw - 0.5f;
                float v = (y + 0.5f) / dh * sh - 0.5f;
                dst[y * dw + x] = Bilinear(src, sw, sh, u, v);
            }
            return dst;
        }

        private static Color32 Bilinear(Color32[] src, int w, int h, float u, float v)
        {
            int x0 = Mathf.Clamp((int)Math.Floor(u), 0, w - 1);
            int y0 = Mathf.Clamp((int)Math.Floor(v), 0, h - 1);
            int x1 = Mathf.Min(x0 + 1, w - 1);
            int y1 = Mathf.Min(y0 + 1, h - 1);
            float tx = Mathf.Clamp01(u - x0);
            float ty = Mathf.Clamp01(v - y0);
            var c00 = src[y0 * w + x0]; var c10 = src[y0 * w + x1];
            var c01 = src[y1 * w + x0]; var c11 = src[y1 * w + x1];
            return new Color32(
                LerpB(c00.r, c10.r, c01.r, c11.r, tx, ty),
                LerpB(c00.g, c10.g, c01.g, c11.g, tx, ty),
                LerpB(c00.b, c10.b, c01.b, c11.b, tx, ty),
                LerpB(c00.a, c10.a, c01.a, c11.a, tx, ty));
        }

        private static byte LerpB(byte a, byte b, byte c, byte d, float tx, float ty)
        {
            float ab = a + (b - a) * tx;
            float cd = c + (d - c) * tx;
            return (byte)Mathf.Clamp(ab + (cd - ab) * ty, 0, 255);
        }

        public static Metrics Compare(Color32[] a, Color32[] b, int w, int h,
            TextureUsageKind usage, AlphaEvalMode alpha, float cutoff)
        {
            var m = new Metrics { MsSsim = 1f, CutoutIou = 1f };
            int n = w * h;
            int shortEdge = Math.Min(w, h);

            if (usage == TextureUsageKind.Normal)
            {
                ComputeNormalAngles(a, b, n, out m.NormalMeanDeg, out m.NormalP95Deg);
                return m;
            }

            if (usage == TextureUsageKind.Gray || usage == TextureUsageKind.Mask)
            {
                double e = 0;
                for (int i = 0; i < n; i++)
                {
                    float da = (a[i].r - b[i].r) / 255f;
                    float dg = (a[i].g - b[i].g) / 255f;
                    float db = (a[i].b - b[i].b) / 255f;
                    e = Math.Max(e, Math.Max(da * da, Math.Max(dg * dg, db * db)));
                }
                m.GrayRmse = (float)Math.Sqrt(e);
                return m;
            }

            if (shortEdge >= 11)
            {
                m.MsSsim = shortEdge < 176 ? Ssim(a, b, w, h) : MsSsim(a, b, w, h);
                m.Ciede2000 = MeanCiede(a, b, n);
            }

            if (alpha == AlphaEvalMode.Cutout)
            {
                int inter = 0, uni = 0;
                for (int i = 0; i < n; i++)
                {
                    bool oa = a[i].a / 255f >= cutoff;
                    bool ob = b[i].a / 255f >= cutoff;
                    if (oa && ob) inter++;
                    if (oa || ob) uni++;
                }
                m.CutoutIou = uni == 0 ? 1f : inter / (float)uni;
            }
            if (alpha == AlphaEvalMode.Blend)
            {
                double e = 0;
                for (int i = 0; i < n; i++)
                {
                    float d = (a[i].a - b[i].a) / 255f;
                    e += d * d;
                }
                m.BlendRmse = (float)Math.Sqrt(e / Math.Max(1, n));
            }
            return m;
        }

        private static void ComputeNormalAngles(Color32[] a, Color32[] b, int n, out float mean, out float p95)
        {
            var angs = new float[n];
            double sum = 0;
            for (int i = 0; i < n; i++)
            {
                var na = DecodeNormal(a[i]);
                var nb = DecodeNormal(b[i]);
                na = math.normalizesafe(na);
                nb = math.normalizesafe(nb);
                float c = math.clamp(math.dot(na, nb), -1f, 1f);
                float deg = math.degrees(math.acos(c));
                angs[i] = deg;
                sum += deg;
            }
            mean = (float)(sum / Math.Max(1, n));
            Array.Sort(angs);
            p95 = angs[Math.Min(n - 1, (int)(n * 0.95f))];
        }

        public static float3 DecodeNormal(Color32 c)
        {
            // Unity DXT5nm: AG = XY. RGBA: XYZ. / Unity DXT5nm 用 AG 存 XY。
            float x = (c.a > 0 || c.r == 128) && c.g != 0 ? (c.a / 255f) * 2f - 1f : (c.r / 255f) * 2f - 1f;
            float y = (c.g / 255f) * 2f - 1f;
            float z = math.sqrt(math.max(0, 1 - x * x - y * y));
            return new float3(x, y, z);
        }

        public static Color32 EncodeNormal(float3 n)
        {
            n = math.normalizesafe(n);
            return new Color32(
                (byte)Mathf.Clamp((n.x * 0.5f + 0.5f) * 255f, 0, 255),
                (byte)Mathf.Clamp((n.y * 0.5f + 0.5f) * 255f, 0, 255),
                (byte)Mathf.Clamp((n.z * 0.5f + 0.5f) * 255f, 0, 255),
                255);
        }

        /// <summary>
        /// Rotate tangent-space normal pixels 90° CW to match island rotation. Mesh tangents untouched.
        /// 岛顺时针 90° 时旋转切线空间法线 XY。网格切线不动。
        /// </summary>
        public static Color32 RotateNormal90Cw(Color32 c)
        {
            var n = DecodeNormal(c);
            // 90° CW in tangent XY: (x,y) -> (y, -x)
            var r = new float3(n.y, -n.x, n.z);
            return EncodeNormal(r);
        }

        private static float MsSsim(Color32[] a, Color32[] b, int w, int h)
        {
            // Three scales. / 三尺度。
            float s1 = Ssim(a, b, w, h);
            var da = Downsample(a, w, h, Math.Max(1, w / 2), Math.Max(1, h / 2), false);
            var db = Downsample(b, w, h, Math.Max(1, w / 2), Math.Max(1, h / 2), false);
            float s2 = Ssim(da, db, Math.Max(1, w / 2), Math.Max(1, h / 2));
            var da2 = Downsample(da, Math.Max(1, w / 2), Math.Max(1, h / 2), Math.Max(1, w / 4), Math.Max(1, h / 4), false);
            var db2 = Downsample(db, Math.Max(1, w / 2), Math.Max(1, h / 2), Math.Max(1, w / 4), Math.Max(1, h / 4), false);
            float s3 = Ssim(da2, db2, Math.Max(1, w / 4), Math.Max(1, h / 4));
            return Mathf.Clamp01(s1 * 0.5f + s2 * 0.3f + s3 * 0.2f);
        }

        private static float Ssim(Color32[] a, Color32[] b, int w, int h)
        {
            const float C1 = 0.01f * 0.01f, C2 = 0.03f * 0.03f;
            double meanA = 0, meanB = 0, n = w * h;
            var la = new float[w * h];
            var lb = new float[w * h];
            for (int i = 0; i < n; i++)
            {
                la[i] = Lum(a[i]);
                lb[i] = Lum(b[i]);
                meanA += la[i];
                meanB += lb[i];
            }
            meanA /= n; meanB /= n;
            double varA = 0, varB = 0, cov = 0;
            for (int i = 0; i < n; i++)
            {
                double da = la[i] - meanA, db = lb[i] - meanB;
                varA += da * da; varB += db * db; cov += da * db;
            }
            varA /= n; varB /= n; cov /= n;
            double s = (2 * meanA * meanB + C1) * (2 * cov + C2) /
                       ((meanA * meanA + meanB * meanB + C1) * (varA + varB + C2) + 1e-12);
            return (float)Math.Max(0, Math.Min(1, s));
        }

        private static float Lum(Color32 c) =>
            0.2126f * SrgbToLinear(c.r / 255f) + 0.7152f * SrgbToLinear(c.g / 255f) + 0.0722f * SrgbToLinear(c.b / 255f);

        private static float MeanCiede(Color32[] a, Color32[] b, int n)
        {
            double s = 0;
            int used = 0;
            int step = n > 4096 ? n / 4096 : 1;
            for (int i = 0; i < n; i += step)
            {
                s += Ciede2000(a[i], b[i]);
                used++;
            }
            return (float)(s / Math.Max(1, used));
        }

        /// <summary>CIEDE2000 (Sharma et al.). Input sRGB bytes. / 输入 sRGB 字节。</summary>
        public static double Ciede2000(Color32 ca, Color32 cb)
        {
            RgbToLab(ca, out double L1, out double a1, out double b1);
            RgbToLab(cb, out double L2, out double a2, out double b2);
            double C1 = Math.Sqrt(a1 * a1 + b1 * b1);
            double C2 = Math.Sqrt(a2 * a2 + b2 * b2);
            double Cab = (C1 + C2) / 2.0;
            double Cab7 = Math.Pow(Cab, 7);
            double G = 0.5 * (1 - Math.Sqrt(Cab7 / (Cab7 + Math.Pow(25.0, 7))));
            double a1p = (1 + G) * a1;
            double a2p = (1 + G) * a2;
            double C1p = Math.Sqrt(a1p * a1p + b1 * b1);
            double C2p = Math.Sqrt(a2p * a2p + b2 * b2);
            double h1p = Atan2Deg(b1, a1p);
            double h2p = Atan2Deg(b2, a2p);
            double dLp = L2 - L1;
            double dCp = C2p - C1p;
            double dhp;
            if (C1p * C2p == 0) dhp = 0;
            else if (Math.Abs(h2p - h1p) <= 180) dhp = h2p - h1p;
            else dhp = h2p <= h1p ? h2p - h1p + 360 : h2p - h1p - 360;
            double dHp = 2 * Math.Sqrt(C1p * C2p) * Math.Sin(Deg(dhp) / 2);
            double Lp = (L1 + L2) / 2;
            double Cp = (C1p + C2p) / 2;
            double hp;
            if (C1p * C2p == 0) hp = h1p + h2p;
            else if (Math.Abs(h1p - h2p) <= 180) hp = (h1p + h2p) / 2;
            else hp = (h1p + h2p < 360) ? (h1p + h2p + 360) / 2 : (h1p + h2p - 360) / 2;
            double T = 1 - 0.17 * Math.Cos(Deg(hp - 30)) + 0.24 * Math.Cos(Deg(2 * hp))
                       + 0.32 * Math.Cos(Deg(3 * hp + 6)) - 0.20 * Math.Cos(Deg(4 * hp - 63));
            double dTheta = 30 * Math.Exp(-Math.Pow((hp - 275) / 25, 2));
            double Rc = 2 * Math.Sqrt(Math.Pow(Cp, 7) / (Math.Pow(Cp, 7) + Math.Pow(25.0, 7)));
            double Sl = 1 + 0.015 * Math.Pow(Lp - 50, 2) / Math.Sqrt(20 + Math.Pow(Lp - 50, 2));
            double Sc = 1 + 0.045 * Cp;
            double Sh = 1 + 0.015 * Cp * T;
            double Rt = -Math.Sin(Deg(2 * dTheta)) * Rc;
            double dE = Math.Sqrt(Math.Pow(dLp / Sl, 2) + Math.Pow(dCp / Sc, 2) + Math.Pow(dHp / Sh, 2)
                                  + Rt * (dCp / Sc) * (dHp / Sh));
            return dE;
        }

        private static double Atan2Deg(double y, double x)
        {
            var d = Math.Atan2(y, x) * 180.0 / Math.PI;
            return d >= 0 ? d : d + 360;
        }

        private static double Deg(double d) => d * Math.PI / 180.0;

        private static void RgbToLab(Color32 c, out double L, out double a, out double b)
        {
            double r = Pivot(SrgbToLinear(c.r / 255.0));
            double g = Pivot(SrgbToLinear(c.g / 255.0));
            double bl = Pivot(SrgbToLinear(c.b / 255.0));
            double x = r * 0.4124564 + g * 0.3575761 + bl * 0.1804375;
            double y = r * 0.2126729 + g * 0.7151522 + bl * 0.0721750;
            double z = r * 0.0193339 + g * 0.1191920 + bl * 0.9503041;
            x /= 0.95047; z /= 1.08883;
            L = 116 * Fy(y) - 16;
            a = 500 * (Fy(x) - Fy(y));
            b = 200 * (Fy(y) - Fy(z));
        }

        private static double Pivot(double v) => v;
        private static double Fy(double t)
        {
            const double e = 216.0 / 24389.0, k = 24389.0 / 27.0;
            return t > e ? Math.Pow(t, 1.0 / 3.0) : (k * t + 16) / 116.0;
        }

        public static float SrgbToLinear(float c) =>
            c <= 0.04045f ? c / 12.92f : Mathf.Pow((c + 0.055f) / 1.055f, 2.4f);

        public static float LinearToSrgb(float c) =>
            c <= 0.0031308f ? 12.92f * c : 1.055f * Mathf.Pow(Mathf.Max(0, c), 1f / 2.4f) - 0.055f;

        private static double SrgbToLinear(double c) =>
            c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }
}
