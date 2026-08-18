// English: Quality metrics + anisotropic binary search. GPU path via RenderTexture; CPU fallback.
// 中文：质量指标 + 各向异性二分。GPU 走 RenderTexture，失败则 CPU。
using System;
using net.fosa.ato;
using UnityEngine;

namespace net.fosa.ato.editor
{
    public static class AtoQuality
    {
        public static void ScaleIsland(AtoIsland island, AtoDecoded[] sources, AtoUvBinding[] bindings,
            AtoQualityThresholds th, AtoQualityPreset preset, int minDensity, int maxDensity)
        {
            if (island == null || sources == null || sources.Length == 0) return;
            var src = sources[0];
            int tw = src.W, thh = src.H;
            var size = island.Max - island.Min;
            int bw = Mathf.Max(1, Mathf.CeilToInt(size.x * tw));
            int bh = Mathf.Max(1, Mathf.CeilToInt(size.y * thh));
            int shortSide = Mathf.Min(bw, bh);

            // Density clamp vs world area
            float world = Mathf.Max(island.WorldArea, 1e-8f);
            float minPx = minDensity * Mathf.Sqrt(world);
            float maxPx = maxDensity * Mathf.Sqrt(world);
            int maxAllowed = Mathf.Clamp(Mathf.RoundToInt(maxPx), 1, shortSide);
            int minAllowed = Mathf.Clamp(Mathf.RoundToInt(minPx), 1, maxAllowed);

            if (preset == AtoQualityPreset.Lossless || NearlyOne(th))
            {
                island.ScaleU = island.ScaleV = 1f;
                island.PixelRect = new RectInt(
                    Mathf.FloorToInt(island.Min.x * tw),
                    Mathf.FloorToInt(island.Min.y * thh), bw, bh);
                AtoLog.VerboseInfo($"island {island.IslandIndex} lossless copy {bw}x{bh}");
                return;
            }

            bool allSolid = true;
            foreach (var s in sources) if (s != null && !s.SolidColor) allSolid = false;
            if (allSolid)
            {
                int m = Mathf.Min(4, shortSide);
                island.ScaleU = island.ScaleV = shortSide <= 0 ? 1f : m / (float)shortSide;
                island.PixelRect = new RectInt(0, 0, m, m);
                island.SolidColor = true;
                AtoLog.VerboseInfo($"island {island.IslandIndex} solid-color short-circuit {m}px");
                return;
            }

            // Uniform binary search then anisotropic refine
            float lo = minAllowed / (float)Mathf.Max(shortSide, 1);
            float hi = 1f;
            float best = 1f;
            for (int it = 0; it < 10; it++)
            {
                float mid = 0.5f * (lo + hi);
                if (PassAll(island, sources, bindings, th, mid, mid, tw, thh))
                {
                    best = mid; hi = mid;
                }
                else lo = mid;
            }
            float su = best, sv = best;
            // Anisotropic: shrink U then V independently
            su = RefineAxis(island, sources, bindings, th, su, sv, tw, thh, true, minAllowed, shortSide);
            sv = RefineAxis(island, sources, bindings, th, su, sv, tw, thh, false, minAllowed, shortSide);
            island.ScaleU = su;
            island.ScaleV = sv;
            island.PixelRect = new RectInt(
                Mathf.FloorToInt(island.Min.x * tw),
                Mathf.FloorToInt(island.Min.y * thh),
                Mathf.Max(1, Mathf.CeilToInt(bw * su)),
                Mathf.Max(1, Mathf.CeilToInt(bh * sv)));
            AtoLog.VerboseInfo($"island {island.IslandIndex} scale u={su:0.000} v={sv:0.000} px={island.PixelRect.width}x{island.PixelRect.height}");
        }

        private static float RefineAxis(AtoIsland island, AtoDecoded[] sources, AtoUvBinding[] bindings,
            AtoQualityThresholds th, float su, float sv, int tw, int thh, bool axisU, int minAllowed, int shortSide)
        {
            float lo = minAllowed / (float)Mathf.Max(shortSide, 1);
            float hi = axisU ? su : sv;
            float best = hi;
            for (int it = 0; it < 8; it++)
            {
                float mid = 0.5f * (lo + hi);
                float u = axisU ? mid : su;
                float v = axisU ? sv : mid;
                if (PassAll(island, sources, bindings, th, u, v, tw, thh)) { best = mid; hi = mid; }
                else lo = mid;
            }
            return best;
        }

        private static bool NearlyOne(AtoQualityThresholds th) =>
            th.msSsim >= 0.9999f && th.ciede2000 <= 1e-4f && th.alphaRmse <= 1e-4f;

        private static bool PassAll(AtoIsland island, AtoDecoded[] sources, AtoUvBinding[] bindings,
            AtoQualityThresholds th, float su, float sv, int tw, int thh)
        {
            for (int i = 0; i < sources.Length; i++)
            {
                var src = sources[i];
                if (src == null) continue;
                var bind = bindings != null && i < bindings.Length ? bindings[i] : null;
                if (!PassOne(island, src, bind, th, su, sv)) return false;
            }
            return true;
        }

        private static bool PassOne(AtoIsland island, AtoDecoded src, AtoUvBinding bind, AtoQualityThresholds th,
            float su, float sv)
        {
            int x0 = Mathf.Clamp(Mathf.FloorToInt(island.Min.x * src.W), 0, src.W - 1);
            int y0 = Mathf.Clamp(Mathf.FloorToInt(island.Min.y * src.H), 0, src.H - 1);
            int x1 = Mathf.Clamp(Mathf.CeilToInt(island.Max.x * src.W), x0 + 1, src.W);
            int y1 = Mathf.Clamp(Mathf.CeilToInt(island.Max.y * src.H), y0 + 1, src.H);
            int bw = x1 - x0, bh = y1 - y0;
            int dw = Mathf.Max(1, Mathf.RoundToInt(bw * su));
            int dh = Mathf.Max(1, Mathf.RoundToInt(bh * sv));
            var orig = Crop(src.Pixels, src.W, src.H, x0, y0, bw, bh);
            var small = Downsample(orig, bw, bh, dw, dh, src, bind);
            var up = Upsample(small, dw, dh, bw, bh);

            int shortSide = Mathf.Min(bw, bh);
            var cls = bind != null ? bind.Class : src.ClassHint;

            if (cls == AtoTextureClass.Normal)
            {
                MeanP95Angle(orig, up, bw * bh, out var mean, out var p95);
                return mean <= th.normalAngleDeg && p95 <= th.normalP95Deg;
            }
            if (cls == AtoTextureClass.Gray || cls == AtoTextureClass.Mask)
            {
                float worst = WorstChannelRmse(orig, up, bw * bh);
                return worst <= th.grayRmse;
            }

            if (shortSide >= 11)
            {
                float ssim = shortSide < 176 ? SsimSingle(orig, up, bw, bh) : MsSsim(orig, up, bw, bh);
                if (ssim < th.msSsim) return false;
            }
            float de = MeanCiede2000(orig, up, bw * bh);
            if (de > th.ciede2000) return false;

            var mode = bind != null ? bind.AlphaMode : (src.HasAlpha ? AtoAlphaMode.Blend : AtoAlphaMode.Opaque);
            if (mode == AtoAlphaMode.Cutout)
            {
                float iou = CutoutIou(orig, up, bw * bh, bind != null ? bind.Cutoff : 0.5f);
                if (iou < th.cutoutIou) return false;
            }
            else if (mode == AtoAlphaMode.Blend && src.HasAlpha)
            {
                float rmse = AlphaRmse(orig, up, bw * bh);
                if (rmse > th.alphaRmse) return false;
            }
            return true;
        }

        public static Color32[] Crop(Color32[] src, int sw, int sh, int x, int y, int w, int h)
        {
            var d = new Color32[w * h];
            for (int j = 0; j < h; j++)
            {
                int sy = Mathf.Clamp(y + j, 0, sh - 1);
                for (int i = 0; i < w; i++)
                {
                    int sx = Mathf.Clamp(x + i, 0, sw - 1);
                    d[j * w + i] = src[sy * sw + sx];
                }
            }
            return d;
        }

        private static Color32[] Downsample(Color32[] src, int sw, int sh, int dw, int dh, AtoDecoded dec, AtoUvBinding bind)
        {
            bool premul = bind != null && bind.AlphaMode != AtoAlphaMode.Opaque;
            bool linear = dec == null || dec.Linear || (bind != null && bind.Class == AtoTextureClass.Normal);
            if (sw * sh > 64 * 64 && AtoGpuQuality.TryDownsampleGpu(src, sw, sh, dw, dh, linear, premul, out var gpu) && gpu != null)
            {
                if (bind != null && bind.Class == AtoTextureClass.Normal) Renormalize(gpu);
                return gpu;
            }
            var dst = new Color32[dw * dh];
            for (int y = 0; y < dh; y++)
            for (int x = 0; x < dw; x++)
            {
                float x0 = x / (float)dw * sw;
                float x1 = (x + 1) / (float)dw * sw;
                float y0 = y / (float)dh * sh;
                float y1 = (y + 1) / (float)dh * sh;
                dst[y * dw + x] = BoxSample(src, sw, sh, x0, y0, x1, y1, premul, linear, bind);
            }
            if (bind != null && bind.Class == AtoTextureClass.Normal)
                Renormalize(dst);
            return dst;
        }

        private static Color32 BoxSample(Color32[] src, int sw, int sh, float x0, float y0, float x1, float y1,
            bool premul, bool linear, AtoUvBinding bind)
        {
            int ix0 = Mathf.Clamp(Mathf.FloorToInt(x0), 0, sw - 1);
            int ix1 = Mathf.Clamp(Mathf.CeilToInt(x1) - 1, 0, sw - 1);
            int iy0 = Mathf.Clamp(Mathf.FloorToInt(y0), 0, sh - 1);
            int iy1 = Mathf.Clamp(Mathf.CeilToInt(y1) - 1, 0, sh - 1);
            float r = 0, g = 0, b = 0, a = 0, n = 0;
            for (int y = iy0; y <= iy1; y++)
            for (int x = ix0; x <= ix1; x++)
            {
                var p = src[y * sw + x];
                float pr = p.r / 255f, pg = p.g / 255f, pb = p.b / 255f, pa = p.a / 255f;
                if (!linear) { pr = Mathf.GammaToLinearSpace(pr); pg = Mathf.GammaToLinearSpace(pg); pb = Mathf.GammaToLinearSpace(pb); }
                if (premul) { pr *= pa; pg *= pa; pb *= pa; }
                r += pr; g += pg; b += pb; a += pa; n += 1f;
            }
            if (n < 1f) n = 1f;
            r /= n; g /= n; b /= n; a /= n;
            if (premul && a > 1e-6f) { r /= a; g /= a; b /= a; }
            if (!linear) { r = Mathf.LinearToGammaSpace(r); g = Mathf.LinearToGammaSpace(g); b = Mathf.LinearToGammaSpace(b); }
            return new Color32(
                (byte)Mathf.Clamp(Mathf.RoundToInt(r * 255f), 0, 255),
                (byte)Mathf.Clamp(Mathf.RoundToInt(g * 255f), 0, 255),
                (byte)Mathf.Clamp(Mathf.RoundToInt(b * 255f), 0, 255),
                (byte)Mathf.Clamp(Mathf.RoundToInt(a * 255f), 0, 255));
        }

        private static void Renormalize(Color32[] px)
        {
            for (int i = 0; i < px.Length; i++)
            {
                var n = new Vector3(px[i].r / 255f * 2f - 1f, px[i].g / 255f * 2f - 1f, px[i].b / 255f * 2f - 1f);
                if (n.sqrMagnitude < 1e-8f) n = Vector3.forward;
                n.Normalize();
                px[i] = new Color32(
                    (byte)Mathf.Clamp(Mathf.RoundToInt((n.x * 0.5f + 0.5f) * 255f), 0, 255),
                    (byte)Mathf.Clamp(Mathf.RoundToInt((n.y * 0.5f + 0.5f) * 255f), 0, 255),
                    (byte)Mathf.Clamp(Mathf.RoundToInt((n.z * 0.5f + 0.5f) * 255f), 0, 255),
                    px[i].a);
            }
        }

        private static Color32[] Upsample(Color32[] src, int sw, int sh, int dw, int dh)
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
            int x0 = Mathf.Clamp(Mathf.FloorToInt(u), 0, w - 1);
            int y0 = Mathf.Clamp(Mathf.FloorToInt(v), 0, h - 1);
            int x1 = Mathf.Min(x0 + 1, w - 1);
            int y1 = Mathf.Min(y0 + 1, h - 1);
            float tx = Mathf.Clamp01(u - x0), ty = Mathf.Clamp01(v - y0);
            Color c00 = src[y0 * w + x0], c10 = src[y0 * w + x1], c01 = src[y1 * w + x0], c11 = src[y1 * w + x1];
            var c = Color.Lerp(Color.Lerp(c00, c10, tx), Color.Lerp(c01, c11, tx), ty);
            return c;
        }

        private static float SsimSingle(Color32[] a, Color32[] b, int w, int h)
        {
            // Luma SSIM, 8x8 windows
            const float C1 = 0.01f * 0.01f, C2 = 0.03f * 0.03f;
            double sum = 0; int n = 0;
            int win = 8;
            for (int y = 0; y + win <= h; y += win)
            for (int x = 0; x + win <= w; x += win)
            {
                double ma = 0, mb = 0;
                for (int j = 0; j < win; j++)
                for (int i = 0; i < win; i++)
                {
                    ma += Luma(a[(y + j) * w + x + i]);
                    mb += Luma(b[(y + j) * w + x + i]);
                }
                ma /= win * win; mb /= win * win;
                double va = 0, vb = 0, cov = 0;
                for (int j = 0; j < win; j++)
                for (int i = 0; i < win; i++)
                {
                    double da = Luma(a[(y + j) * w + x + i]) - ma;
                    double db = Luma(b[(y + j) * w + x + i]) - mb;
                    va += da * da; vb += db * db; cov += da * db;
                }
                va /= win * win; vb /= win * win; cov /= win * win;
                sum += ((2 * ma * mb + C1) * (2 * cov + C2)) / ((ma * ma + mb * mb + C1) * (va + vb + C2) + 1e-12);
                n++;
            }
            return n == 0 ? 1f : (float)(sum / n);
        }

        private static float MsSsim(Color32[] a, Color32[] b, int w, int h)
        {
            // 3-scale approximation
            float s0 = SsimSingle(a, b, w, h);
            var a1 = Half(a, w, h, out var w1, out var h1);
            var b1 = Half(b, w, h, out _, out _);
            float s1 = SsimSingle(a1, b1, w1, h1);
            var a2 = Half(a1, w1, h1, out var w2, out var h2);
            var b2 = Half(b1, w1, h1, out _, out _);
            float s2 = SsimSingle(a2, b2, w2, h2);
            return Mathf.Pow(Mathf.Max(s0, 1e-6f), 0.2f) * Mathf.Pow(Mathf.Max(s1, 1e-6f), 0.3f) * Mathf.Pow(Mathf.Max(s2, 1e-6f), 0.5f);
        }

        private static Color32[] Half(Color32[] s, int w, int h, out int nw, out int nh)
        {
            nw = Mathf.Max(1, w / 2); nh = Mathf.Max(1, h / 2);
            return Downsample(s, w, h, nw, nh, new AtoDecoded { Linear = true }, null);
        }

        private static double Luma(Color32 c) => (0.2126 * c.r + 0.7152 * c.g + 0.0722 * c.b) / 255.0;

        private static float MeanCiede2000(Color32[] a, Color32[] b, int n)
        {
            double s = 0;
            int step = Math.Max(1, n / 4096);
            int k = 0;
            for (int i = 0; i < n; i += step)
            {
                s += Ciede2000(a[i], b[i]);
                k++;
            }
            return k == 0 ? 0 : (float)(s / k);
        }

        // Sharma et al. CIEDE2000
        private static double Ciede2000(Color32 ca, Color32 cb)
        {
            Rgb2Lab(ca, out var L1, out var a1, out var b1);
            Rgb2Lab(cb, out var L2, out var a2, out var b2);
            double avgL = (L1 + L2) / 2.0;
            double C1 = Math.Sqrt(a1 * a1 + b1 * b1);
            double C2 = Math.Sqrt(a2 * a2 + b2 * b2);
            double avgC = (C1 + C2) / 2.0;
            double G = 0.5 * (1 - Math.Sqrt(Math.Pow(avgC, 7) / (Math.Pow(avgC, 7) + Math.Pow(25.0, 7))));
            double a1p = (1 + G) * a1, a2p = (1 + G) * a2;
            double C1p = Math.Sqrt(a1p * a1p + b1 * b1);
            double C2p = Math.Sqrt(a2p * a2p + b2 * b2);
            double h1p = Atan2Deg(b1, a1p);
            double h2p = Atan2Deg(b2, a2p);
            double dLp = L2 - L1;
            double dCp = C2p - C1p;
            double dhp = 0;
            if (C1p * C2p != 0)
            {
                double dh = h2p - h1p;
                if (dh > 180) dh -= 360;
                if (dh < -180) dh += 360;
                dhp = dh;
            }
            double dHp = 2 * Math.Sqrt(C1p * C2p) * Math.Sin(dhp * Math.PI / 360.0);
            double avgLp = (L1 + L2) / 2.0;
            double avgCp = (C1p + C2p) / 2.0;
            double avghp = h1p + h2p;
            if (C1p * C2p != 0)
            {
                if (Math.Abs(h1p - h2p) > 180) avghp += (h1p + h2p < 360) ? 360 : -360;
                avghp /= 2.0;
            }
            double T = 1 - 0.17 * Math.Cos(Rad(avghp - 30)) + 0.24 * Math.Cos(Rad(2 * avghp))
                         + 0.32 * Math.Cos(Rad(3 * avghp + 6)) - 0.20 * Math.Cos(Rad(4 * avghp - 63));
            double sl = 1 + 0.015 * Math.Pow(avgLp - 50, 2) / Math.Sqrt(20 + Math.Pow(avgLp - 50, 2));
            double sc = 1 + 0.045 * avgCp;
            double sh = 1 + 0.015 * avgCp * T;
            double dtheta = 30 * Math.Exp(-Math.Pow((avghp - 275) / 25.0, 2));
            double Rc = 2 * Math.Sqrt(Math.Pow(avgCp, 7) / (Math.Pow(avgCp, 7) + Math.Pow(25.0, 7)));
            double Rt = -Rc * Math.Sin(Rad(2 * dtheta));
            double dE = Math.Sqrt(Math.Pow(dLp / sl, 2) + Math.Pow(dCp / sc, 2) + Math.Pow(dHp / sh, 2) + Rt * (dCp / sc) * (dHp / sh));
            return dE;
        }

        private static double Rad(double d) => d * Math.PI / 180.0;
        private static double Atan2Deg(double y, double x)
        {
            var h = Math.Atan2(y, x) * 180.0 / Math.PI;
            return h < 0 ? h + 360 : h;
        }
        private static void Rgb2Lab(Color32 c, out double L, out double a, out double b)
        {
            double r = PivotRgb(c.r / 255.0), g = PivotRgb(c.g / 255.0), bl = PivotRgb(c.b / 255.0);
            double x = r * 0.4124 + g * 0.3576 + bl * 0.1805;
            double y = r * 0.2126 + g * 0.7152 + bl * 0.0722;
            double z = r * 0.0193 + g * 0.1192 + bl * 0.9505;
            x /= 0.95047; z /= 1.08883;
            x = PivotXyz(x); y = PivotXyz(y); z = PivotXyz(z);
            L = 116 * y - 16; a = 500 * (x - y); b = 200 * (y - z);
        }
        private static double PivotRgb(double n) => n > 0.04045 ? Math.Pow((n + 0.055) / 1.055, 2.4) : n / 12.92;
        private static double PivotXyz(double n) => n > 0.008856 ? Math.Pow(n, 1.0 / 3.0) : 7.787 * n + 16.0 / 116.0;

        private static void MeanP95Angle(Color32[] a, Color32[] b, int n, out float mean, out float p95)
        {
            var ang = new float[n];
            double s = 0;
            for (int i = 0; i < n; i++)
            {
                var na = DecodeN(a[i]); var nb = DecodeN(b[i]);
                float d = Mathf.Clamp(Vector3.Dot(na, nb), -1f, 1f);
                ang[i] = Mathf.Acos(d) * Mathf.Rad2Deg;
                s += ang[i];
            }
            Array.Sort(ang);
            mean = n == 0 ? 0 : (float)(s / n);
            p95 = n == 0 ? 0 : ang[Mathf.Clamp(Mathf.FloorToInt(n * 0.95f), 0, n - 1)];
        }
        private static Vector3 DecodeN(Color32 c) =>
            new Vector3(c.r / 255f * 2f - 1f, c.g / 255f * 2f - 1f, c.b / 255f * 2f - 1f).normalized;

        private static float WorstChannelRmse(Color32[] a, Color32[] b, int n)
        {
            double r = 0, g = 0, bl = 0, al = 0;
            for (int i = 0; i < n; i++)
            {
                r += Sq(a[i].r - b[i].r); g += Sq(a[i].g - b[i].g);
                bl += Sq(a[i].b - b[i].b); al += Sq(a[i].a - b[i].a);
            }
            float rmse(double s) => (float)Math.Sqrt(s / Math.Max(1, n)) / 255f;
            return Mathf.Max(rmse(r), Mathf.Max(rmse(g), Mathf.Max(rmse(bl), rmse(al))));
        }
        private static double Sq(int x) { double d = x; return d * d; }

        private static float CutoutIou(Color32[] a, Color32[] b, int n, float cutoff)
        {
            int cut = Mathf.Clamp(Mathf.RoundToInt(cutoff * 255f), 0, 255);
            int inter = 0, uni = 0;
            for (int i = 0; i < n; i++)
            {
                bool aa = a[i].a >= cut, bb = b[i].a >= cut;
                if (aa && bb) inter++;
                if (aa || bb) uni++;
            }
            return uni == 0 ? 1f : inter / (float)uni;
        }

        private static float AlphaRmse(Color32[] a, Color32[] b, int n)
        {
            double s = 0;
            for (int i = 0; i < n; i++) s += Sq(a[i].a - b[i].a);
            return (float)Math.Sqrt(s / Math.Max(1, n)) / 255f;
        }
    }
}
