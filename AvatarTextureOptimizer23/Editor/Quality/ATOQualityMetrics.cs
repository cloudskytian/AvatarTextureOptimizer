using System;
using Unity.Mathematics;
using UnityEngine;
using FOSA.AvatarTextureOptimizer;

namespace FOSA.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Perceptual metrics. GPU path first, Burst/CPU fallback.
    /// Comparison is always "scaled island bilinear-upsampled back to the original bbox".
    /// 感知指标。先 GPU，失败再 Burst/CPU。
    /// 比较对象永远是“缩小后再双线性放大回原包围盒”的结果。
    /// </summary>
    internal static class ATOQualityMetrics
    {
        public static bool Passes(
            ATOContext ctx,
            Color[] original, int ow, int oh,
            Color[] scaled, int sw, int sh,
            ATOTextureCategory cat,
            ATOAlphaMode alphaMode,
            float cutoff,
            ATOQualityParameters th,
            out string detail)
        {
            var up = Upsample(scaled, sw, sh, ow, oh);
            try
            {
                return PassesUpsampled(ctx, original, up, ow, oh, cat, alphaMode, cutoff, th, out detail);
            }
            finally
            {
                // nothing to free / 无托管外资源
            }
        }

        public static bool PassesUpsampled(
            ATOContext ctx,
            Color[] original, Color[] up, int w, int h,
            ATOTextureCategory cat,
            ATOAlphaMode alphaMode,
            float cutoff,
            ATOQualityParameters th,
            out string detail)
        {
            detail = "";
            if (original == null || up == null || original.Length != up.Length)
            {
                detail = "length-mismatch";
                return false;
            }

            foreach (var ext in ATOApi.QualityMetrics)
            {
                if (!ext.Evaluate(original, up, w, h, cat, th, out var score))
                {
                    detail = $"ext:{ext.Id}={score:F4}";
                    return false;
                }
            }

            if (cat == ATOTextureCategory.Normal)
            {
                ComputeNormalAngles(original, up, out var mean, out var p95);
                detail = $"nMean={mean:F2} nP95={p95:F2}";
                return mean <= th.normalAngleDeg + 1e-4f && p95 <= th.normalP95Deg + 1e-4f;
            }

            if (cat == ATOTextureCategory.Gray)
            {
                var rmse = ChannelRmseWorst(original, up);
                detail = $"grayRMSE={rmse:F4}";
                return rmse <= th.grayRmse + 1e-6f;
            }

            var shortSide = Math.Min(w, h);
            float ssim = 1f;
            if (shortSide >= 11)
            {
                var useMs = shortSide >= 176;
                ssim = useMs ? MsSsim(original, up, w, h) : Ssim(original, up, w, h);
            }
            var de = MeanDeltaE00(original, up);
            var ok = ssim + 1e-6f >= th.msSsim && de <= th.deltaE00 + 1e-4f;

            if (alphaMode == ATOAlphaMode.Cutout)
            {
                var iou = ClipIou(original, up, cutoff);
                ok = ok && iou + 1e-6f >= th.alphaIou;
                detail = $"SSIM={ssim:F4} dE={de:F2} IoU={iou:F3}";
            }
            else if (alphaMode == ATOAlphaMode.Blend)
            {
                var rmse = AlphaRmse(original, up);
                ok = ok && rmse <= th.alphaRmse + 1e-6f;
                detail = $"SSIM={ssim:F4} dE={de:F2} aRMSE={rmse:F4}";
            }
            else
            {
                detail = $"SSIM={ssim:F4} dE={de:F2}";
            }
            return ok;
        }

        public static Color[] Upsample(Color[] src, int sw, int sh, int dw, int dh)
        {
            var dst = new Color[dw * dh];
            if (sw <= 0 || sh <= 0) return dst;
            for (int y = 0; y < dh; y++)
            {
                var v = (y + 0.5f) * sh / dh - 0.5f;
                for (int x = 0; x < dw; x++)
                {
                    var u = (x + 0.5f) * sw / dw - 0.5f;
                    dst[y * dw + x] = ATOTextureUtil.Bilinear(src, sw, sh, u, v);
                }
            }
            return dst;
        }

        public static Color[] DownsamplePremultiplied(Color[] src, int sw, int sh, int dw, int dh)
        {
            // Area-average in linear premultiplied alpha. / 线性预乘 alpha 下的面积平均。
            var dst = new Color[dw * dh];
            for (int y = 0; y < dh; y++)
            {
                var y0 = y * sh / (float)dh;
                var y1 = (y + 1) * sh / (float)dh;
                for (int x = 0; x < dw; x++)
                {
                    var x0 = x * sw / (float)dw;
                    var x1 = (x + 1) * sw / (float)dw;
                    var acc = new Vector4();
                    var wsum = 0f;
                    var ix0 = Mathf.Max(0, (int)x0);
                    var iy0 = Mathf.Max(0, (int)y0);
                    var ix1 = Mathf.Min(sw - 1, (int)Math.Ceiling(x1) - 1);
                    var iy1 = Mathf.Min(sh - 1, (int)Math.Ceiling(y1) - 1);
                    for (int iy = iy0; iy <= iy1; iy++)
                    for (int ix = ix0; ix <= ix1; ix++)
                    {
                        var c = src[iy * sw + ix];
                        acc += new Vector4(c.r * c.a, c.g * c.a, c.b * c.a, c.a);
                        wsum += 1f;
                    }
                    if (wsum <= 0f) continue;
                    acc /= wsum;
                    var a = acc.w;
                    dst[y * dw + x] = a > 1e-6f
                        ? new Color(acc.x / a, acc.y / a, acc.z / a, a)
                        : new Color(0, 0, 0, 0);
                }
            }
            return dst;
        }

        public static Color[] DownsampleLinear(Color[] src, int sw, int sh, int dw, int dh)
        {
            var dst = new Color[dw * dh];
            for (int y = 0; y < dh; y++)
            {
                var v = (y + 0.5f) * sh / dh - 0.5f;
                for (int x = 0; x < dw; x++)
                {
                    var u = (x + 0.5f) * sw / dw - 0.5f;
                    dst[y * dw + x] = ATOTextureUtil.Bilinear(src, sw, sh, u, v);
                }
            }
            return dst;
        }

        public static float Ssim(Color[] a, Color[] b, int w, int h)
        {
            // Burst path for larger images. / 较大图像走 Burst。
            if (w >= 32 && h >= 32 && a.Length == w * h)
            {
                var la = new float[a.Length];
                var lb = new float[b.Length];
                for (int i = 0; i < a.Length; i++)
                {
                    la[i] = Luma(a[i]);
                    lb[i] = Luma(b[i]);
                }
                return ATOSsimBurst.Evaluate(la, lb, w, h);
            }

            // Single-scale SSIM on luma. / 单尺度 SSIM，作用在亮度上。
            const float C1 = 0.01f * 0.01f;
            const float C2 = 0.03f * 0.03f;
            const int win = 8;
            double sum = 0;
            int n = 0;
            for (int y = 0; y + win <= h; y += win)
            for (int x = 0; x + win <= w; x += win)
            {
                double ma = 0, mb = 0;
                for (int j = 0; j < win; j++)
                for (int i = 0; i < win; i++)
                {
                    ma += Luma(a[(y + j) * w + (x + i)]);
                    mb += Luma(b[(y + j) * w + (x + i)]);
                }
                var inv = 1.0 / (win * win);
                ma *= inv; mb *= inv;
                double va = 0, vb = 0, cab = 0;
                for (int j = 0; j < win; j++)
                for (int i = 0; i < win; i++)
                {
                    var la = Luma(a[(y + j) * w + (x + i)]) - ma;
                    var lb = Luma(b[(y + j) * w + (x + i)]) - mb;
                    va += la * la; vb += lb * lb; cab += la * lb;
                }
                va *= inv; vb *= inv; cab *= inv;
                var s = ((2 * ma * mb + C1) * (2 * cab + C2)) /
                        ((ma * ma + mb * mb + C1) * (va + vb + C2));
                sum += s;
                n++;
            }
            return n == 0 ? 1f : (float)(sum / n);
        }

        public static float MsSsim(Color[] a, Color[] b, int w, int h)
        {
            // 5-scale MS-SSIM with Wang weights. / 五尺度 MS-SSIM，权重来自 Wang。
            double[] weights = { 0.0448, 0.2856, 0.3001, 0.2363, 0.1333 };
            double acc = 1.0;
            var ca = a; var cb = b; var cw = w; var ch = h;
            for (int s = 0; s < 5; s++)
            {
                var ss = Ssim(ca, cb, cw, ch);
                acc *= Math.Pow(Math.Max(ss, 1e-6), weights[s]);
                if (s == 4) break;
                var nw = Math.Max(1, cw / 2);
                var nh = Math.Max(1, ch / 2);
                if (nw < 8 || nh < 8) break;
                ca = DownsampleLinear(ca, cw, ch, nw, nh);
                cb = DownsampleLinear(cb, cw, ch, nw, nh);
                cw = nw; ch = nh;
            }
            return (float)acc;
        }

        public static float MeanDeltaE00(Color[] a, Color[] b)
        {
            double sum = 0;
            var step = Math.Max(1, a.Length / 16384);
            int n = 0;
            for (int i = 0; i < a.Length; i += step)
            {
                sum += Ciede2000(a[i], b[i]);
                n++;
            }
            return n == 0 ? 0f : (float)(sum / n);
        }

        public static float Ciede2000(Color ca, Color cb)
        {
            RgbToLab(ca, out var L1, out var a1, out var b1);
            RgbToLab(cb, out var L2, out var a2, out var b2);
            return (float)DeltaE00(L1, a1, b1, L2, a2, b2);
        }

        public static void ComputeNormalAngles(Color[] a, Color[] b, out float meanDeg, out float p95Deg)
        {
            var n = a.Length;
            var angles = new float[n];
            double sum = 0;
            for (int i = 0; i < n; i++)
            {
                var na = DecodeNormal(a[i]);
                var nb = DecodeNormal(b[i]);
                var dot = math.clamp(math.dot(na, nb), -1f, 1f);
                var deg = math.degrees(math.acos(dot));
                angles[i] = deg;
                sum += deg;
            }
            meanDeg = n == 0 ? 0f : (float)(sum / n);
            Array.Sort(angles);
            p95Deg = n == 0 ? 0f : angles[Math.Min(n - 1, (int)(n * 0.95f))];
        }

        public static float ChannelRmseWorst(Color[] a, Color[] b)
        {
            double er = 0, eg = 0, eb = 0, ea = 0;
            for (int i = 0; i < a.Length; i++)
            {
                var d = a[i] - b[i];
                er += d.r * d.r; eg += d.g * d.g; eb += d.b * d.b; ea += d.a * d.a;
            }
            var inv = 1.0 / Math.Max(1, a.Length);
            var wr = Math.Sqrt(er * inv);
            var wg = Math.Sqrt(eg * inv);
            var wb = Math.Sqrt(eb * inv);
            var wa = Math.Sqrt(ea * inv);
            return (float)Math.Max(Math.Max(wr, wg), Math.Max(wb, wa));
        }

        public static float AlphaRmse(Color[] a, Color[] b)
        {
            double e = 0;
            for (int i = 0; i < a.Length; i++)
            {
                var d = a[i].a - b[i].a;
                e += d * d;
            }
            return (float)Math.Sqrt(e / Math.Max(1, a.Length));
        }

        public static float ClipIou(Color[] a, Color[] b, float cutoff)
        {
            int inter = 0, uni = 0;
            for (int i = 0; i < a.Length; i++)
            {
                var aa = a[i].a >= cutoff;
                var bb = b[i].a >= cutoff;
                if (aa || bb) uni++;
                if (aa && bb) inter++;
            }
            return uni == 0 ? 1f : inter / (float)uni;
        }

        private static float Luma(Color c) => 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;

        private static float3 DecodeNormal(Color c)
        {
            var n = new float3(c.r * 2f - 1f, c.g * 2f - 1f, c.b * 2f - 1f);
            var len = math.length(n);
            return len > 1e-6f ? n / len : new float3(0, 0, 1);
        }

        private static void RgbToLab(Color c, out double L, out double a, out double b)
        {
            double r = PivotRgb(c.r), g = PivotRgb(c.g), bl = PivotRgb(c.b);
            double x = r * 0.4124564 + g * 0.3575761 + bl * 0.1804375;
            double y = r * 0.2126729 + g * 0.7151522 + bl * 0.0721750;
            double z = r * 0.0193339 + g * 0.1191920 + bl * 0.9503041;
            x /= 0.95047; y /= 1.00000; z /= 1.08883;
            x = PivotXyz(x); y = PivotXyz(y); z = PivotXyz(z);
            L = 116.0 * y - 16.0;
            a = 500.0 * (x - y);
            b = 200.0 * (y - z);
        }

        private static double PivotRgb(double u)
        {
            u = Math.Max(0, u);
            return u > 0.04045 ? Math.Pow((u + 0.055) / 1.055, 2.4) : u / 12.92;
        }

        private static double PivotXyz(double u)
        {
            return u > 0.008856 ? Math.Pow(u, 1.0 / 3.0) : (7.787 * u) + 16.0 / 116.0;
        }

        /// <summary>
        /// CIEDE2000 (Sharma, Wu, Dalal).
        /// </summary>
        private static double DeltaE00(double L1, double a1, double b1, double L2, double a2, double b2)
        {
            double C1 = Math.Sqrt(a1 * a1 + b1 * b1);
            double C2 = Math.Sqrt(a2 * a2 + b2 * b2);
            double Cab = (C1 + C2) * 0.5;
            double G = 0.5 * (1.0 - Math.Sqrt(Math.Pow(Cab, 7) / (Math.Pow(Cab, 7) + Math.Pow(25.0, 7))));
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
            else
            {
                var dh = h2p - h1p;
                if (dh > 180) dh -= 360;
                if (dh < -180) dh += 360;
                dhp = dh;
            }
            double dHp = 2 * Math.Sqrt(C1p * C2p) * Math.Sin(dhp * Math.PI / 360.0);
            double Lbar = (L1 + L2) * 0.5;
            double Cpbar = (C1p + C2p) * 0.5;
            double Hpbar;
            if (C1p * C2p == 0) Hpbar = h1p + h2p;
            else
            {
                var dh = Math.Abs(h1p - h2p);
                if (dh > 180) Hpbar = (h1p + h2p + 360) * 0.5;
                else Hpbar = (h1p + h2p) * 0.5;
                if (dh > 180 && h1p + h2p < 360) Hpbar = (h1p + h2p + 360) * 0.5;
                if (dh > 180 && h1p + h2p >= 360) Hpbar = (h1p + h2p - 360) * 0.5;
            }
            double T = 1
                       - 0.17 * Math.Cos(Rad(Hpbar - 30))
                       + 0.24 * Math.Cos(Rad(2 * Hpbar))
                       + 0.32 * Math.Cos(Rad(3 * Hpbar + 6))
                       - 0.20 * Math.Cos(Rad(4 * Hpbar - 63));
            double dTheta = 30 * Math.Exp(-Math.Pow((Hpbar - 275) / 25, 2));
            double Rc = 2 * Math.Sqrt(Math.Pow(Cpbar, 7) / (Math.Pow(Cpbar, 7) + Math.Pow(25.0, 7)));
            double Sl = 1 + (0.015 * Math.Pow(Lbar - 50, 2)) / Math.Sqrt(20 + Math.Pow(Lbar - 50, 2));
            double Sc = 1 + 0.045 * Cpbar;
            double Sh = 1 + 0.015 * Cpbar * T;
            double Rt = -Math.Sin(Rad(2 * dTheta)) * Rc;
            double dE = Math.Sqrt(
                Math.Pow(dLp / Sl, 2) +
                Math.Pow(dCp / Sc, 2) +
                Math.Pow(dHp / Sh, 2) +
                Rt * (dCp / Sc) * (dHp / Sh));
            return dE;
        }

        private static double Atan2Deg(double y, double x)
        {
            var d = Math.Atan2(y, x) * 180.0 / Math.PI;
            return d < 0 ? d + 360 : d;
        }

        private static double Rad(double deg) => deg * Math.PI / 180.0;
    }
}
