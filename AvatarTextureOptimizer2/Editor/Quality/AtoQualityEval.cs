using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using Net.Fosa.AvatarTextureOptimizer;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Quality metrics: linear resample, premul-alpha downsample, MS-SSIM / SSIM, CIEDE2000,
    /// cutout IoU / blend RMSE, normal angle, gray channel RMSE.
    /// 质量指标实现。评估不含最终压缩损失。
    /// </summary>
    public static class AtoQualityEval
    {
        public struct Score
        {
            public float Ssim;
            public float DeltaE;
            public float AlphaRmse;
            public float CutoutIou;
            public float NormalAngleMean;
            public float NormalAngleP95;
            public float GrayRmse;
            public bool Solid;
        }

        public static bool Passes(Score s, AtoQualityParameters p, AtoTextureRole role, AtoBlendMode blend, int shortSide)
        {
            if (p.targetQuality >= 0.999f) return true;
            if (shortSide < 11) return true;

            if (role == AtoTextureRole.Normal)
                return s.NormalAngleMean <= p.normalAngleDegMax && s.NormalAngleP95 <= p.normalP95AngleDegMax;
            if (role == AtoTextureRole.Gray)
                return s.GrayRmse <= p.grayRmseMax;

            if (s.Ssim < p.msSsimMin) return false;
            if (s.DeltaE > p.ciede2000Max) return false;
            if (blend == AtoBlendMode.Cutout && s.CutoutIou < p.cutoutIouMin) return false;
            if (blend == AtoBlendMode.Blend && s.AlphaRmse > p.alphaRmseMax) return false;
            return true;
        }

        public static Score Compare(
            Color32[] original, int ow, int oh, Rect origPx,
            Color32[] scaled, int sw, int sh,
            AtoTextureRole role, AtoBlendMode blend, float cutoff, bool srgb)
        {
            int x0 = Mathf.Clamp(Mathf.FloorToInt(origPx.xMin), 0, ow - 1);
            int y0 = Mathf.Clamp(Mathf.FloorToInt(origPx.yMin), 0, oh - 1);
            int x1 = Mathf.Clamp(Mathf.CeilToInt(origPx.xMax), 1, ow);
            int y1 = Mathf.Clamp(Mathf.CeilToInt(origPx.yMax), 1, oh);
            int w = Mathf.Max(1, x1 - x0);
            int h = Mathf.Max(1, y1 - y0);

            var up = BilinearUpsample(scaled, sw, sh, w, h, role == AtoTextureRole.Albedo && blend != AtoBlendMode.Opaque);
            var src = Crop(original, ow, oh, x0, y0, w, h);

            var score = new Score { Ssim = 1, CutoutIou = 1 };
            int shortSide = Mathf.Min(w, h);

            if (role == AtoTextureRole.Normal)
            {
                NormalAngles(src, up, w * h, out score.NormalAngleMean, out score.NormalAngleP95);
                return score;
            }
            if (role == AtoTextureRole.Gray)
            {
                score.GrayRmse = ChannelRmse(src, up, w * h);
                return score;
            }

            if (shortSide >= 11)
            {
                if (shortSide < 176)
                    score.Ssim = Ssim(src, up, w, h, srgb);
                else
                    score.Ssim = MsSsim(src, up, w, h, srgb);
                score.DeltaE = MeanCiede2000(src, up, w * h, srgb);
            }
            if (blend == AtoBlendMode.Cutout)
                score.CutoutIou = CutoutIou(src, up, w * h, cutoff);
            if (blend == AtoBlendMode.Blend)
                score.AlphaRmse = AlphaRmse(src, up, w * h);
            return score;
        }

        public static bool IsSolid(Color32[] px, int x0, int y0, int x1, int y1, int w, out Color32 c)
        {
            c = px[y0 * w + x0];
            for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                var p = px[y * w + x];
                if (p.r != c.r || p.g != c.g || p.b != c.b || p.a != c.a) return false;
            }
            return true;
        }

        static Color32[] Crop(Color32[] src, int tw, int th, int x0, int y0, int w, int h)
        {
            var o = new Color32[w * h];
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int sx = Mathf.Clamp(x0 + x, 0, tw - 1);
                int sy = Mathf.Clamp(y0 + y, 0, th - 1);
                o[y * w + x] = src[sy * tw + sx];
            }
            return o;
        }

        public static Color32[] BilinearUpsample(Color32[] src, int sw, int sh, int dw, int dh, bool premul)
        {
            var o = new Color32[dw * dh];
            for (int y = 0; y < dh; y++)
            for (int x = 0; x < dw; x++)
            {
                float u = (x + 0.5f) * sw / dw - 0.5f;
                float v = (y + 0.5f) * sh / dh - 0.5f;
                o[y * dw + x] = SampleBilinear(src, sw, sh, u, v, premul);
            }
            return o;
        }

        public static Color32[] BilinearDownsample(Color32[] src, int sw, int sh, int dw, int dh, bool premul)
        {
            return BilinearUpsample(src, sw, sh, dw, dh, premul);
        }

        static Color32 SampleBilinear(Color32[] src, int w, int h, float u, float v, bool premul)
        {
            int x0 = Mathf.FloorToInt(u), y0 = Mathf.FloorToInt(v);
            float fx = u - x0, fy = v - y0;
            Color acc = Color.clear;
            float wt = 0;
            for (int j = 0; j <= 1; j++)
            for (int i = 0; i <= 1; i++)
            {
                int x = Mathf.Clamp(x0 + i, 0, w - 1);
                int y = Mathf.Clamp(y0 + j, 0, h - 1);
                var c = (Color)src[y * w + x];
                float ww = (i == 0 ? 1 - fx : fx) * (j == 0 ? 1 - fy : fy);
                if (premul)
                {
                    acc.r += c.r * c.a * ww;
                    acc.g += c.g * c.a * ww;
                    acc.b += c.b * c.a * ww;
                    acc.a += c.a * ww;
                }
                else
                {
                    acc += c * ww;
                }
                wt += ww;
            }
            if (wt < 1e-8f) return default;
            acc /= wt;
            if (premul && acc.a > 1e-8f)
            {
                acc.r /= acc.a; acc.g /= acc.a; acc.b /= acc.a;
            }
            return acc;
        }

        static float Ssim(Color32[] a, Color32[] b, int w, int h, bool srgb)
        {
            // Single-scale SSIM on luma. / 单尺度亮度 SSIM。
            const float K1 = 0.01f, K2 = 0.03f;
            float C1 = K1 * K1, C2 = K2 * K2;
            double meanA = 0, meanB = 0;
            int n = w * h;
            var la = new float[n];
            var lb = new float[n];
            for (int i = 0; i < n; i++)
            {
                la[i] = Luma(a[i], srgb);
                lb[i] = Luma(b[i], srgb);
                meanA += la[i]; meanB += lb[i];
            }
            meanA /= n; meanB /= n;
            double varA = 0, varB = 0, cov = 0;
            for (int i = 0; i < n; i++)
            {
                double da = la[i] - meanA, db = lb[i] - meanB;
                varA += da * da; varB += db * db; cov += da * db;
            }
            varA /= n; varB /= n; cov /= n;
            return (float)((2 * meanA * meanB + C1) * (2 * cov + C2) /
                           ((meanA * meanA + meanB * meanB + C1) * (varA + varB + C2) + 1e-12));
        }

        static float MsSsim(Color32[] a, Color32[] b, int w, int h, bool srgb)
        {
            // 3-scale geometric mean. / 三尺度几何平均。
            float s = Ssim(a, b, w, h, srgb);
            var a2 = Half(a, w, h, out int w2, out int h2);
            var b2 = Half(b, w, h, out _, out _);
            float s2 = Ssim(a2, b2, w2, h2, srgb);
            var a3 = Half(a2, w2, h2, out int w3, out int h3);
            var b3 = Half(b2, w2, h2, out _, out _);
            float s3 = Ssim(a3, b3, w3, h3, srgb);
            return Mathf.Pow(Mathf.Max(s, 1e-6f), 0.5f) *
                   Mathf.Pow(Mathf.Max(s2, 1e-6f), 0.3f) *
                   Mathf.Pow(Mathf.Max(s3, 1e-6f), 0.2f);
        }

        static Color32[] Half(Color32[] s, int w, int h, out int nw, out int nh)
        {
            nw = Mathf.Max(1, w / 2); nh = Mathf.Max(1, h / 2);
            return BilinearDownsample(s, w, h, nw, nh, true);
        }

        static float Luma(Color32 c, bool srgb)
        {
            float r = c.r / 255f, g = c.g / 255f, b = c.b / 255f;
            if (srgb)
            {
                r = SrgbToLinear(r); g = SrgbToLinear(g); b = SrgbToLinear(b);
            }
            return 0.2126f * r + 0.7152f * g + 0.0722f * b;
        }

        static float SrgbToLinear(float c) =>
            c <= 0.04045f ? c / 12.92f : Mathf.Pow((c + 0.055f) / 1.055f, 2.4f);

        static float MeanCiede2000(Color32[] a, Color32[] b, int n, bool srgb)
        {
            double acc = 0;
            int step = Mathf.Max(1, n / 4096);
            int cnt = 0;
            for (int i = 0; i < n; i += step)
            {
                acc += Ciede2000(ToLab(a[i], srgb), ToLab(b[i], srgb));
                cnt++;
            }
            return (float)(acc / Mathf.Max(1, cnt));
        }

        static float3 ToLab(Color32 c, bool srgb)
        {
            float r = c.r / 255f, g = c.g / 255f, b = c.b / 255f;
            if (srgb) { r = SrgbToLinear(r); g = SrgbToLinear(g); b = SrgbToLinear(b); }
            float x = r * 0.4124f + g * 0.3576f + b * 0.1805f;
            float y = r * 0.2126f + g * 0.7152f + b * 0.0722f;
            float z = r * 0.0193f + g * 0.1192f + b * 0.9505f;
            x /= 0.95047f; z /= 1.08883f;
            x = Pivot(x); y = Pivot(y); z = Pivot(z);
            return new float3(116f * y - 16f, 500f * (x - y), 200f * (y - z));
        }

        static float Pivot(float t) => t > 0.008856f ? Mathf.Pow(t, 1f / 3f) : 7.787f * t + 16f / 116f;

        static float Ciede2000(float3 lab1, float3 lab2)
        {
            // Sharma et al. CIEDE2000. / CIEDE2000 实现。
            double L1 = lab1.x, a1 = lab1.y, b1 = lab1.z;
            double L2 = lab2.x, a2 = lab2.y, b2 = lab2.z;
            double C1 = Math.Sqrt(a1 * a1 + b1 * b1);
            double C2 = Math.Sqrt(a2 * a2 + b2 * b2);
            double Cab = (C1 + C2) / 2.0;
            double G = 0.5 * (1 - Math.Sqrt(Math.Pow(Cab, 7) / (Math.Pow(Cab, 7) + Math.Pow(25.0, 7))));
            double a1p = (1 + G) * a1, a2p = (1 + G) * a2;
            double C1p = Math.Sqrt(a1p * a1p + b1 * b1);
            double C2p = Math.Sqrt(a2p * a2p + b2 * b2);
            double h1p = Math.Atan2(b1, a1p); if (h1p < 0) h1p += 2 * Math.PI;
            double h2p = Math.Atan2(b2, a2p); if (h2p < 0) h2p += 2 * Math.PI;
            double dLp = L2 - L1;
            double dCp = C2p - C1p;
            double dhp = h2p - h1p;
            if (C1p * C2p == 0) dhp = 0;
            else if (dhp > Math.PI) dhp -= 2 * Math.PI;
            else if (dhp < -Math.PI) dhp += 2 * Math.PI;
            double dHp = 2 * Math.Sqrt(C1p * C2p) * Math.Sin(dhp / 2);
            double Lbar = (L1 + L2) / 2;
            double Cpbar = (C1p + C2p) / 2;
            double hpbar = (h1p + h2p) / 2;
            if (C1p * C2p != 0 && Math.Abs(h1p - h2p) > Math.PI)
                hpbar += (h1p + h2p < 2 * Math.PI) ? Math.PI : -Math.PI;
            double T = 1 - 0.17 * Math.Cos(hpbar - Math.PI / 6) + 0.24 * Math.Cos(2 * hpbar)
                       + 0.32 * Math.Cos(3 * hpbar + Math.PI / 30) - 0.20 * Math.Cos(4 * hpbar - 21 * Math.PI / 60);
            double Sl = 1 + 0.015 * Math.Pow(Lbar - 50, 2) / Math.Sqrt(20 + Math.Pow(Lbar - 50, 2));
            double Sc = 1 + 0.045 * Cpbar;
            double Sh = 1 + 0.015 * Cpbar * T;
            double dt = 30 * Math.PI / 180 * Math.Exp(-Math.Pow((hpbar * 180 / Math.PI - 275) / 25, 2));
            double Rc = 2 * Math.Sqrt(Math.Pow(Cpbar, 7) / (Math.Pow(Cpbar, 7) + Math.Pow(25.0, 7)));
            double Rt = -Math.Sin(2 * dt) * Rc;
            double dE = Math.Sqrt(Math.Pow(dLp / Sl, 2) + Math.Pow(dCp / Sc, 2) + Math.Pow(dHp / Sh, 2) + Rt * (dCp / Sc) * (dHp / Sh));
            return (float)dE;
        }

        static float CutoutIou(Color32[] a, Color32[] b, int n, float cutoff)
        {
            int thr = Mathf.Clamp(Mathf.RoundToInt(cutoff * 255f), 0, 255);
            int inter = 0, uni = 0;
            for (int i = 0; i < n; i++)
            {
                bool aa = a[i].a >= thr, bb = b[i].a >= thr;
                if (aa || bb) uni++;
                if (aa && bb) inter++;
            }
            return uni == 0 ? 1f : inter / (float)uni;
        }

        static float AlphaRmse(Color32[] a, Color32[] b, int n)
        {
            double acc = 0;
            for (int i = 0; i < n; i++)
            {
                double d = (a[i].a - b[i].a) / 255.0;
                acc += d * d;
            }
            return (float)Math.Sqrt(acc / n);
        }

        static float ChannelRmse(Color32[] a, Color32[] b, int n)
        {
            double wr = 0, wg = 0, wb = 0, wa = 0;
            for (int i = 0; i < n; i++)
            {
                wr += Sq((a[i].r - b[i].r) / 255.0);
                wg += Sq((a[i].g - b[i].g) / 255.0);
                wb += Sq((a[i].b - b[i].b) / 255.0);
                wa += Sq((a[i].a - b[i].a) / 255.0);
            }
            wr = Math.Sqrt(wr / n); wg = Math.Sqrt(wg / n);
            wb = Math.Sqrt(wb / n); wa = Math.Sqrt(wa / n);
            return (float)Math.Max(Math.Max(wr, wg), Math.Max(wb, wa));
        }

        static double Sq(double x) => x * x;

        static void NormalAngles(Color32[] a, Color32[] b, int n, out float mean, out float p95)
        {
            var ang = new float[n];
            double acc = 0;
            for (int i = 0; i < n; i++)
            {
                var na = DecodeNormal(a[i]);
                var nb = DecodeNormal(b[i]);
                float d = Mathf.Clamp(math.dot(na, nb), -1f, 1f);
                ang[i] = Mathf.Acos(d) * Mathf.Rad2Deg;
                acc += ang[i];
            }
            mean = (float)(acc / n);
            Array.Sort(ang);
            p95 = ang[Mathf.Clamp((int)(n * 0.95f), 0, n - 1)];
        }

        static Vector3 DecodeNormal(Color32 c)
        {
            var n = new Vector3(c.r / 255f * 2 - 1, c.g / 255f * 2 - 1, c.b / 255f * 2 - 1);
            if (n.sqrMagnitude < 1e-8f) n = Vector3.forward;
            return n.normalized;
        }

        static float math_dot(Vector3 a, Vector3 b) => a.x * b.x + a.y * b.y + a.z * b.z;
    }
}
