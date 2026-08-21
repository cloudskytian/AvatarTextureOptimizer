using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using Fosa.ATO;

namespace Fosa.ATO.Editor
{
    /// <summary>
    /// Quality gates: linear resample, premul-alpha downsample, MS-SSIM, CIEDE2000, alpha IoU/RMSE, normal angle.
    /// Compared after bilinear-upsampling the scaled island coverage back to original size.
    /// 质量门限。缩放后的岛覆盖区上采样回原尺寸再比较。不含最终压缩损失。
    /// </summary>
    public static class AtoQuality
    {
        public struct Result
        {
            public float MsSsim;
            public float DeMean, DeP95;
            public float NormalMean, NormalP95;
            public float AlphaIou, AlphaRmse, GrayRmse;
            public bool Solid;
        }

        public static bool Passes(Result r, AtoQualitySettings q, AtoTextureClass cls, AtoAlphaMode alpha, int shortSide)
        {
            if (q.IsLossless) return true;
            if (cls == AtoTextureClass.Normal)
                return r.NormalMean <= q.normalAngleMeanDeg && r.NormalP95 <= q.normalAngleP95Deg;
            if (cls == AtoTextureClass.Gray)
                return r.GrayRmse <= q.grayRmse;

            bool ok = true;
            if (shortSide >= 11)
            {
                // short < 176 → single-scale SSIM (still stored in MsSsim). 短边 <176 已在计算时回退单尺度。
                ok &= r.MsSsim >= q.msSsim;
            }
            ok &= r.DeMean <= q.deltaE00Mean && r.DeP95 <= q.deltaE00P95;
            if (cls == AtoTextureClass.Transparent || alpha != AtoAlphaMode.Opaque)
            {
                if (alpha == AtoAlphaMode.Cutout) ok &= r.AlphaIou >= q.alphaIou;
                else ok &= r.AlphaRmse <= q.alphaRmse;
            }
            return ok;
        }

        public static Result Evaluate(
            Color[] orig, int ow, int oh,
            Color[] scaled, int sw, int sh,
            AtoTextureClass cls, bool origIsSrgb, AtoAlphaMode alpha, float cutoff)
        {
            var r = new Result();
            if (orig == null || orig.Length == 0 || ow < 1 || oh < 1)
            {
                r.MsSsim = 1; r.AlphaIou = 1; return r;
            }
            r.Solid = AtoTextureUtil.IsSolidColor(orig);

            bool premul = cls == AtoTextureClass.Transparent || alpha != AtoAlphaMode.Opaque;
            bool linearize = origIsSrgb && cls != AtoTextureClass.Normal;

            // Upsample scaled coverage back to original size. 将缩小结果上采样回原尺寸。
            var up = AtoTextureUtil.Resample(scaled, sw, sh, ow, oh, premul, linearize: false);

            int n = ow * oh;
            int shortSide = Math.Min(ow, oh);

            if (cls == AtoTextureClass.Normal)
            {
                EvalNormal(orig, up, n, out r.NormalMean, out r.NormalP95);
                return r;
            }
            if (cls == AtoTextureClass.Gray)
            {
                r.GrayRmse = EvalGrayRmse(orig, up, n);
                return r;
            }

            if (shortSide >= 11)
            {
                bool ms = shortSide >= 176;
                r.MsSsim = EvalSsim(orig, up, ow, oh, linearize, ms);
            }
            else r.MsSsim = 1f;

            EvalCiede(orig, up, n, linearize, out r.DeMean, out r.DeP95);

            if (alpha == AtoAlphaMode.Cutout)
                r.AlphaIou = EvalIou(orig, up, n, cutoff);
            else if (alpha == AtoAlphaMode.Blend || cls == AtoTextureClass.Transparent)
                r.AlphaRmse = EvalAlphaRmse(orig, up, n, linearize);

            return r;
        }

        /// <summary>
        /// Binary-search uniform scale then anisotropic refine. Returns scaleU, scaleV in (0,1].
        /// 先均匀二分至全部达标，再双轴独立细化。
        /// </summary>
        public static Vector2 SearchScale(
            Color[] orig, int ow, int oh, bool origIsSrgb,
            AtoTextureClass cls, AtoAlphaMode alpha, float cutoff,
            AtoQualitySettings q, float minScale, bool lossless, bool solid)
        {
            if (lossless) return Vector2.one;
            if (solid && !q.IsLossless)
            {
                float s = Math.Min(4f, Math.Min(ow, oh)) / Math.Max(1, Math.Min(ow, oh));
                return new Vector2(Mathf.Clamp01(s), Mathf.Clamp01(s));
            }
            minScale = Mathf.Clamp(minScale, 1f / Math.Max(ow, oh), 1f);

            bool Ok(float su, float sv)
            {
                int sw = Math.Max(1, Mathf.RoundToInt(ow * su));
                int sh = Math.Max(1, Mathf.RoundToInt(oh * sv));
                bool premul = cls == AtoTextureClass.Transparent || alpha != AtoAlphaMode.Opaque;
                Color[] down;
                if (cls == AtoTextureClass.Normal)
                    down = AtoTextureUtil.ResampleNormal(orig, ow, oh, sw, sh);
                else
                    down = AtoGpu.ResampleOrCpu(orig, ow, oh, sw, sh, premul, origIsSrgb && cls != AtoTextureClass.Normal);
                var r = Evaluate(orig, ow, oh, down, sw, sh, cls, origIsSrgb, alpha, cutoff);
                return Passes(r, q, cls, alpha, Math.Min(ow, oh));
            }

            float lo = minScale, hi = 1f, uni = 1f;
            for (int i = 0; i < 10; i++)
            {
                float mid = 0.5f * (lo + hi);
                if (Ok(mid, mid)) { uni = mid; hi = mid; }
                else lo = mid;
            }
            if (!Ok(uni, uni)) uni = 1f;

            float su = uni, sv = uni;
            lo = minScale; hi = uni;
            float bestU = uni;
            for (int i = 0; i < 8; i++)
            {
                float mid = 0.5f * (lo + hi);
                if (Ok(mid, sv)) { bestU = mid; hi = mid; }
                else lo = mid;
            }
            su = bestU;
            lo = minScale; hi = uni;
            float bestV = uni;
            for (int i = 0; i < 8; i++)
            {
                float mid = 0.5f * (lo + hi);
                if (Ok(su, mid)) { bestV = mid; hi = mid; }
                else lo = mid;
            }
            sv = bestV;
            return new Vector2(su, sv);
        }

        static float EvalSsim(Color[] a, Color[] b, int w, int h, bool linearize, bool multi)
        {
            // Luma SSIM / MS-SSIM (Wang et al. 2003). Weights: 0.0448, 0.2856, 0.3001, 0.2363, 0.1333
            float[] wa = { 0.0448f, 0.2856f, 0.3001f, 0.2363f, 0.1333f };
            int scales = multi ? 5 : 1;
            int cw = w, ch = h;
            var la = ToLuma(a, w, h, linearize);
            var lb = ToLuma(b, w, h, linearize);
            float csProd = 1f, lastSsim = 1f;
            for (int s = 0; s < scales; s++)
            {
                SsimComponents(la, lb, cw, ch, out float l, out float cs);
                lastSsim = l * cs;
                if (multi)
                {
                    float weight = wa[Math.Min(s, wa.Length - 1)];
                    if (s < scales - 1) csProd *= Mathf.Pow(Mathf.Max(cs, 1e-6f), weight);
                    else csProd *= Mathf.Pow(Mathf.Max(l * cs, 1e-6f), weight);
                }
                if (!multi) break;
                if (cw < 8 || ch < 8) break;
                la = Down2(la, cw, ch, out int nwa, out int nha);
                lb = Down2(lb, cw, ch, out _, out _);
                cw = nwa;
                ch = nha;
            }
            return multi ? csProd : lastSsim;
        }

        static float[] ToLuma(Color[] c, int w, int h, bool srgb)
        {
            var l = new float[w * h];
            for (int i = 0; i < l.Length && i < c.Length; i++)
            {
                float r = c[i].r, g = c[i].g, b = c[i].b;
                if (srgb)
                {
                    r = Mathf.GammaToLinearSpace(r);
                    g = Mathf.GammaToLinearSpace(g);
                    b = Mathf.GammaToLinearSpace(b);
                }
                l[i] = 0.2126f * r + 0.7152f * g + 0.0722f * b;
            }
            return l;
        }

        static void SsimComponents(float[] a, float[] b, int w, int h, out float l, out float cs)
        {
            const float C1 = 0.01f * 0.01f;
            const float C2 = 0.03f * 0.03f;
            int n = w * h;
            double ma = 0, mb = 0;
            for (int i = 0; i < n; i++) { ma += a[i]; mb += b[i]; }
            ma /= n; mb /= n;
            double va = 0, vb = 0, cov = 0;
            for (int i = 0; i < n; i++)
            {
                double da = a[i] - ma, db = b[i] - mb;
                va += da * da; vb += db * db; cov += da * db;
            }
            va /= n; vb /= n; cov /= n;
            l = (float)((2 * ma * mb + C1) / (ma * ma + mb * mb + C1));
            cs = (float)((2 * cov + C2) / (va + vb + C2));
        }

        static float[] Down2(float[] src, int w, int h, out int nw, out int nh)
        {
            nw = Math.Max(1, w / 2); nh = Math.Max(1, h / 2);
            var d = new float[nw * nh];
            for (int y = 0; y < nh; y++)
            for (int x = 0; x < nw; x++)
            {
                int x0 = x * 2, y0 = y * 2;
                int x1 = Math.Min(x0 + 1, w - 1), y1 = Math.Min(y0 + 1, h - 1);
                d[y * nw + x] = 0.25f * (src[y0 * w + x0] + src[y0 * w + x1] + src[y1 * w + x0] + src[y1 * w + x1]);
            }
            return d;
        }

        static void EvalCiede(Color[] a, Color[] b, int n, bool srgb, out float mean, out float p95)
        {
            var err = new float[n];
            double sum = 0;
            for (int i = 0; i < n; i++)
            {
                var la = ToLab(a[i], srgb);
                var lb = ToLab(b[i], srgb);
                err[i] = Ciede2000(la, lb);
                sum += err[i];
            }
            mean = (float)(sum / Math.Max(1, n));
            Array.Sort(err);
            p95 = err[Math.Min(n - 1, (int)(n * 0.95))];
        }

        static Vector3 ToLab(Color c, bool srgb)
        {
            float r = srgb ? Mathf.GammaToLinearSpace(c.r) : c.r;
            float g = srgb ? Mathf.GammaToLinearSpace(c.g) : c.g;
            float b = srgb ? Mathf.GammaToLinearSpace(c.b) : c.b;
            // sRGB D65 → XYZ → Lab
            float x = r * 0.4124564f + g * 0.3575761f + b * 0.1804375f;
            float y = r * 0.2126729f + g * 0.7151522f + b * 0.0721750f;
            float z = r * 0.0193339f + g * 0.1191920f + b * 0.9503041f;
            x /= 0.95047f; z /= 1.08883f;
            x = Pivot(x); y = Pivot(y); z = Pivot(z);
            return new Vector3(116f * y - 16f, 500f * (x - y), 200f * (y - z));
        }

        static float Pivot(float t)
        {
            const float e = 216f / 24389f, k = 24389f / 27f;
            return t > e ? Mathf.Pow(t, 1f / 3f) : (k * t + 16f) / 116f;
        }

        /// <summary>Sharma, Wu, Dalal CIEDE2000.</summary>
        public static float Ciede2000(Vector3 lab1, Vector3 lab2)
        {
            double L1 = lab1.x, a1 = lab1.y, b1 = lab1.z;
            double L2 = lab2.x, a2 = lab2.y, b2 = lab2.z;
            double C1 = Math.Sqrt(a1 * a1 + b1 * b1);
            double C2 = Math.Sqrt(a2 * a2 + b2 * b2);
            double Cm = 0.5 * (C1 + C2);
            double G = 0.5 * (1 - Math.Sqrt(Math.Pow(Cm, 7) / (Math.Pow(Cm, 7) + Math.Pow(25.0, 7))));
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
            double Lpm = 0.5 * (L1 + L2);
            double Cpm = 0.5 * (C1p + C2p);
            double hpm;
            if (C1p * C2p == 0) hpm = h1p + h2p;
            else
            {
                double dh = Math.Abs(h1p - h2p);
                if (dh <= 180) hpm = 0.5 * (h1p + h2p);
                else hpm = 0.5 * (h1p + h2p + (h1p + h2p < 360 ? 360 : -360));
            }
            double T = 1 - 0.17 * Math.Cos(Rad(hpm - 30)) + 0.24 * Math.Cos(Rad(2 * hpm))
                       + 0.32 * Math.Cos(Rad(3 * hpm + 6)) - 0.20 * Math.Cos(Rad(4 * hpm - 63));
            double dTheta = 30 * Math.Exp(-Math.Pow((hpm - 275) / 25, 2));
            double Rc = 2 * Math.Sqrt(Math.Pow(Cpm, 7) / (Math.Pow(Cpm, 7) + Math.Pow(25.0, 7)));
            double Sl = 1 + 0.015 * Math.Pow(Lpm - 50, 2) / Math.Sqrt(20 + Math.Pow(Lpm - 50, 2));
            double Sc = 1 + 0.045 * Cpm;
            double Sh = 1 + 0.015 * Cpm * T;
            double Rt = -Math.Sin(Rad(2 * dTheta)) * Rc;
            double dE = Math.Sqrt(Math.Pow(dLp / Sl, 2) + Math.Pow(dCp / Sc, 2) + Math.Pow(dHp / Sh, 2)
                                  + Rt * (dCp / Sc) * (dHp / Sh));
            return (float)dE;
        }

        static double Atan2Deg(double y, double x)
        {
            var d = Math.Atan2(y, x) * 180.0 / Math.PI;
            return d >= 0 ? d : d + 360;
        }
        static double Rad(double d) => d * Math.PI / 180.0;

        static void EvalNormal(Color[] a, Color[] b, int n, out float mean, out float p95)
        {
            var err = new float[n];
            double sum = 0;
            for (int i = 0; i < n; i++)
            {
                var na = DecodeNormal(a[i]);
                var nb = DecodeNormal(b[i]);
                // resample + renormalize. 重采样后重归一化。
                na.Normalize(); nb.Normalize();
                float dot = Mathf.Clamp(Vector3.Dot(na, nb), -1f, 1f);
                err[i] = Mathf.Acos(dot) * Mathf.Rad2Deg;
                sum += err[i];
            }
            mean = (float)(sum / Math.Max(1, n));
            Array.Sort(err);
            p95 = err[Math.Min(n - 1, (int)(n * 0.95))];
        }

        static Vector3 DecodeNormal(Color c)
        {
            // Unity DXT5nm: ag = xy, or standard rgb.
            float x = c.r * 2f - 1f;
            float y = c.g * 2f - 1f;
            float z = c.b * 2f - 1f;
            if (c.b < 0.01f && c.a > 0.01f)
            {
                // likely DXT5nm (ag)
                x = c.a * 2f - 1f;
                y = c.g * 2f - 1f;
                z = Mathf.Sqrt(Mathf.Max(0, 1 - x * x - y * y));
            }
            var v = new Vector3(x, y, z);
            if (v.sqrMagnitude < 1e-8f) v = Vector3.forward;
            return v;
        }

        static float EvalIou(Color[] a, Color[] b, int n, float cutoff)
        {
            int inter = 0, uni = 0;
            for (int i = 0; i < n; i++)
            {
                bool aa = a[i].a >= cutoff;
                bool bb = b[i].a >= cutoff;
                if (aa || bb) uni++;
                if (aa && bb) inter++;
            }
            return uni == 0 ? 1f : (float)inter / uni;
        }

        static float EvalAlphaRmse(Color[] a, Color[] b, int n, bool srgb)
        {
            double s = 0;
            for (int i = 0; i < n; i++)
            {
                float da = a[i].a - b[i].a;
                s += da * da;
            }
            return (float)Math.Sqrt(s / Math.Max(1, n));
        }

        static float EvalGrayRmse(Color[] a, Color[] b, int n)
        {
            // Worst used channel. Detect used = variance > eps on original.
            float worst = 0;
            for (int ch = 0; ch < 4; ch++)
            {
                double mean = 0;
                for (int i = 0; i < n; i++) mean += GetCh(a[i], ch);
                mean /= n;
                double var = 0, mse = 0;
                for (int i = 0; i < n; i++)
                {
                    float av = GetCh(a[i], ch), bv = GetCh(b[i], ch);
                    var += (av - mean) * (av - mean);
                    mse += (av - bv) * (av - bv);
                }
                var /= n;
                if (var < 1e-8) continue; // unused channel
                worst = Mathf.Max(worst, (float)Math.Sqrt(mse / n));
            }
            return worst;
        }

        static float GetCh(Color c, int i) => i == 0 ? c.r : i == 1 ? c.g : i == 2 ? c.b : c.a;
    }
}
