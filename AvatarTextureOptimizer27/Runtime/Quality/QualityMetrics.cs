using System;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer
{
    /// <summary>
    /// CPU fallback of the target-quality algorithm.
    /// GPU/Burst paths live in Editor/Quality/GpuQualityBatch.
    /// 目标质量算法的 CPU 回退；GPU/Burst 在编辑器程序集。
    /// </summary>
    public static class QualityMetrics
    {
        public const int MsSsimMinShortEdge = 176;
        public const int IgnoreSsimShortEdge = 11;

        public static float Ciede2000(Color a, Color b)
        {
            RgbToLab(a, out double L1, out double a1, out double b1);
            RgbToLab(b, out double L2, out double a2, out double b2);
            return (float)DeltaE2000(L1, a1, b1, L2, a2, b2);
        }

        public static float MsSsim(Color[] orig, Color[] recon, int w, int h)
        {
            int shortEdge = Math.Min(w, h);
            if (shortEdge < IgnoreSsimShortEdge) return 1f;
            if (shortEdge < MsSsimMinShortEdge)
                return Ssim(orig, recon, w, h);
            // Multi-scale: average SSIM on successively halved images (Wang et al.).
            float acc = 0f;
            int levels = 0;
            Color[] a = orig;
            Color[] b = recon;
            int cw = w, ch = h;
            while (Math.Min(cw, ch) >= 11 && levels < 5)
            {
                acc += Ssim(a, b, cw, ch);
                levels++;
                if (cw < 22 || ch < 22) break;
                a = Downsample2(a, cw, ch, out int nw, out int nh);
                b = Downsample2(b, cw, ch, out nw, out nh);
                cw = nw;
                ch = nh;
            }
            return levels == 0 ? 1f : acc / levels;
        }

        public static float Ssim(Color[] a, Color[] b, int w, int h)
        {
            const float C1 = 0.01f * 0.01f;
            const float C2 = 0.03f * 0.03f;
            double meanA = 0, meanB = 0;
            int n = w * h;
            if (n == 0) return 1f;
            for (int i = 0; i < n; i++)
            {
                meanA += Luma(a[i]);
                meanB += Luma(b[i]);
            }
            meanA /= n;
            meanB /= n;
            double varA = 0, varB = 0, cov = 0;
            for (int i = 0; i < n; i++)
            {
                double da = Luma(a[i]) - meanA;
                double db = Luma(b[i]) - meanB;
                varA += da * da;
                varB += db * db;
                cov += da * db;
            }
            varA /= n;
            varB /= n;
            cov /= n;
            double num = (2 * meanA * meanB + C1) * (2 * cov + C2);
            double den = (meanA * meanA + meanB * meanB + C1) * (varA + varB + C2);
            return den <= 0 ? 1f : (float)(num / den);
        }

        public static float AlphaRmse(Color[] a, Color[] b)
        {
            if (a.Length == 0) return 0f;
            double s = 0;
            for (int i = 0; i < a.Length; i++)
            {
                double d = a[i].a - b[i].a;
                s += d * d;
            }
            return (float)Math.Sqrt(s / a.Length);
        }

        public static float CutoutIou(Color[] a, Color[] b, float cutoff)
        {
            int inter = 0, uni = 0;
            for (int i = 0; i < a.Length; i++)
            {
                bool aa = a[i].a >= cutoff;
                bool bb = b[i].a >= cutoff;
                if (aa && bb) inter++;
                if (aa || bb) uni++;
            }
            return uni == 0 ? 1f : inter / (float)uni;
        }

        public static void DecodeNormal(Color c, out Vector3 n)
        {
            n = new Vector3(c.r * 2f - 1f, c.g * 2f - 1f, c.b * 2f - 1f);
            if (n.sqrMagnitude < 1e-8f) n = Vector3.forward;
            else n.Normalize();
        }

        public static float NormalAngleMeanP95(Color[] a, Color[] b, out float p95)
        {
            int n = a.Length;
            if (n == 0) { p95 = 0; return 0; }
            var angles = new float[n];
            double sum = 0;
            for (int i = 0; i < n; i++)
            {
                DecodeNormal(a[i], out var na);
                DecodeNormal(b[i], out var nb);
                float d = Mathf.Clamp(Vector3.Dot(na, nb), -1f, 1f);
                float ang = Mathf.Acos(d) * Mathf.Rad2Deg;
                angles[i] = ang;
                sum += ang;
            }
            Array.Sort(angles);
            p95 = angles[Math.Min(n - 1, (int)(n * 0.95f))];
            return (float)(sum / n);
        }

        public static float ChannelRmse(Color[] a, Color[] b, bool r, bool g, bool bl, bool al)
        {
            double worst = 0;
            void Acc(Func<Color, float> ch)
            {
                double s = 0;
                for (int i = 0; i < a.Length; i++)
                {
                    double d = ch(a[i]) - ch(b[i]);
                    s += d * d;
                }
                worst = Math.Max(worst, Math.Sqrt(s / Math.Max(1, a.Length)));
            }
            if (r) Acc(c => c.r);
            if (g) Acc(c => c.g);
            if (bl) Acc(c => c.b);
            if (al) Acc(c => c.a);
            return (float)worst;
        }

        public static Color[] BilinearUpsample(Color[] src, int sw, int sh, int dw, int dh)
        {
            var dst = new Color[dw * dh];
            for (int y = 0; y < dh; y++)
            for (int x = 0; x < dw; x++)
            {
                float u = (x + 0.5f) * sw / (float)dw - 0.5f;
                float v = (y + 0.5f) * sh / (float)dh - 0.5f;
                dst[y * dw + x] = SampleBilinear(src, sw, sh, u, v);
            }
            return dst;
        }

        public static Color[] PremultipliedDownsample(Color[] src, int sw, int sh, int dw, int dh)
        {
            var dst = new Color[dw * dh];
            float sx = sw / (float)dw;
            float sy = sh / (float)dh;
            for (int y = 0; y < dh; y++)
            for (int x = 0; x < dw; x++)
            {
                int x0 = Mathf.FloorToInt(x * sx);
                int y0 = Mathf.FloorToInt(y * sy);
                int x1 = Mathf.Min(sw, Mathf.CeilToInt((x + 1) * sx));
                int y1 = Mathf.Min(sh, Mathf.CeilToInt((y + 1) * sy));
                Color acc = default;
                int c = 0;
                for (int yy = y0; yy < y1; yy++)
                for (int xx = x0; xx < x1; xx++)
                {
                    var p = src[yy * sw + xx];
                    acc += new Color(p.r * p.a, p.g * p.a, p.b * p.a, p.a);
                    c++;
                }
                if (c == 0) continue;
                acc /= c;
                if (acc.a > 1e-6f)
                    acc = new Color(acc.r / acc.a, acc.g / acc.a, acc.b / acc.a, acc.a);
                dst[y * dw + x] = acc;
            }
            return dst;
        }

        public static bool IsSolid(Color[] px, float eps = 1e-3f)
        {
            if (px == null || px.Length == 0) return true;
            var r = px[0];
            for (int i = 1; i < px.Length; i++)
            {
                var d = px[i] - r;
                if (Mathf.Abs(d.r) > eps || Mathf.Abs(d.g) > eps || Mathf.Abs(d.b) > eps || Mathf.Abs(d.a) > eps)
                    return false;
            }
            return true;
        }

        static float Luma(Color c) => 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;

        static Color SampleBilinear(Color[] src, int w, int h, float u, float v)
        {
            u = Mathf.Clamp(u, 0, w - 1.001f);
            v = Mathf.Clamp(v, 0, h - 1.001f);
            int x0 = (int)u, y0 = (int)v;
            int x1 = Math.Min(w - 1, x0 + 1);
            int y1 = Math.Min(h - 1, y0 + 1);
            float tx = u - x0, ty = v - y0;
            var a = src[y0 * w + x0];
            var b = src[y0 * w + x1];
            var c = src[y1 * w + x0];
            var d = src[y1 * w + x1];
            return Color.Lerp(Color.Lerp(a, b, tx), Color.Lerp(c, d, tx), ty);
        }

        static Color[] Downsample2(Color[] src, int w, int h, out int nw, out int nh)
        {
            nw = Math.Max(1, w / 2);
            nh = Math.Max(1, h / 2);
            return PremultipliedDownsample(src, w, h, nw, nh);
        }

        static void RgbToLab(Color c, out double L, out double a, out double b)
        {
            double r = PivotRgb(c.r), g = PivotRgb(c.g), bl = PivotRgb(c.b);
            double x = r * 0.4124 + g * 0.3576 + bl * 0.1805;
            double y = r * 0.2126 + g * 0.7152 + bl * 0.0722;
            double z = r * 0.0193 + g * 0.1192 + bl * 0.9505;
            x /= 0.95047; z /= 1.08883;
            x = PivotXyz(x); y = PivotXyz(y); z = PivotXyz(z);
            L = 116 * y - 16;
            a = 500 * (x - y);
            b = 200 * (y - z);
        }

        static double PivotRgb(double n)
        {
            n = Math.Max(0, n);
            return n > 0.04045 ? Math.Pow((n + 0.055) / 1.055, 2.4) : n / 12.92;
        }

        static double PivotXyz(double n) => n > 0.008856 ? Math.Pow(n, 1.0 / 3.0) : 7.787 * n + 16.0 / 116.0;

        static double DeltaE2000(double L1, double a1, double b1, double L2, double a2, double b2)
        {
            double avgLp = (L1 + L2) / 2.0;
            double C1 = Math.Sqrt(a1 * a1 + b1 * b1);
            double C2 = Math.Sqrt(a2 * a2 + b2 * b2);
            double avgC = (C1 + C2) / 2.0;
            double G = 0.5 * (1 - Math.Sqrt(Math.Pow(avgC, 7) / (Math.Pow(avgC, 7) + Math.Pow(25.0, 7))));
            double a1p = (1 + G) * a1;
            double a2p = (1 + G) * a2;
            double C1p = Math.Sqrt(a1p * a1p + b1 * b1);
            double C2p = Math.Sqrt(a2p * a2p + b2 * b2);
            double h1p = Atan2Deg(b1, a1p);
            double h2p = Atan2Deg(b2, a2p);
            double dLp = L2 - L1;
            double dCp = C2p - C1p;
            double dhp = 0;
            if (C1p * C2p != 0)
            {
                dhp = h2p - h1p;
                if (dhp > 180) dhp -= 360;
                if (dhp < -180) dhp += 360;
            }
            double dHp = 2 * Math.Sqrt(C1p * C2p) * Math.Sin(dhp * Math.PI / 360.0);
            double avgLp2 = (L1 + L2) / 2.0;
            double avgCp = (C1p + C2p) / 2.0;
            double avghp = h1p + h2p;
            if (C1p * C2p != 0)
            {
                if (Math.Abs(h1p - h2p) > 180)
                    avghp += (h1p + h2p < 360) ? 360 : -360;
                avghp /= 2.0;
            }
            double T = 1 - 0.17 * Math.Cos(Rad(avghp - 30)) + 0.24 * Math.Cos(Rad(2 * avghp))
                       + 0.32 * Math.Cos(Rad(3 * avghp + 6)) - 0.20 * Math.Cos(Rad(4 * avghp - 63));
            double sl = 1 + 0.015 * Math.Pow(avgLp2 - 50, 2) / Math.Sqrt(20 + Math.Pow(avgLp2 - 50, 2));
            double sc = 1 + 0.045 * avgCp;
            double sh = 1 + 0.015 * avgCp * T;
            double dTheta = 30 * Math.Exp(-Math.Pow((avghp - 275) / 25, 2));
            double Rc = 2 * Math.Sqrt(Math.Pow(avgCp, 7) / (Math.Pow(avgCp, 7) + Math.Pow(25.0, 7)));
            double Rt = -Math.Sin(Rad(2 * dTheta)) * Rc;
            double dE = Math.Sqrt(Math.Pow(dLp / sl, 2) + Math.Pow(dCp / sc, 2) + Math.Pow(dHp / sh, 2) + Rt * (dCp / sc) * (dHp / sh));
            return dE;
        }

        static double Atan2Deg(double y, double x)
        {
            double d = Math.Atan2(y, x) * 180.0 / Math.PI;
            return d < 0 ? d + 360 : d;
        }

        static double Rad(double d) => d * Math.PI / 180.0;
    }
}
