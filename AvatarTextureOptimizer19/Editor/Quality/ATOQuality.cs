// English: Target-quality island scaling. GPU blit resample + CPU/Burst metrics (MS-SSIM, CIEDE2000, alpha, normal, gray).
// 中文：目标质量岛缩放。GPU Blit 重采样 + CPU/Burst 指标（MS-SSIM、CIEDE2000、alpha、法线、灰度）。
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Net.Fosa.AvatarTextureOptimizer;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    internal static class ATOQuality
    {
        public static void ScaleIslands(ATOState state)
        {
            var q = state.Quality;
            var plat = state.Settings;
            var minDens = (float)plat.minPixelDensity;
            var maxDens = (float)plat.maxPixelDensity;

            foreach (var isl in state.Islands)
            {
                if (isl.Source == null) continue;
                state.Progress.ThrowIfCanceled();

                if (q.targetQuality >= 1f - 1e-6f)
                {
                    isl.Scale = Vector2.one;
                    continue;
                }

                var shortSide = Mathf.Min(isl.PixelBounds.width, isl.PixelBounds.height);
                if (isl.SolidColor)
                {
                    var target = Mathf.Min(4f, shortSide);
                    var s = shortSide <= 1e-4f ? 1f : Mathf.Clamp01(target / shortSide);
                    isl.Scale = new Vector2(s, s);
                    state.Report.IslandsScaled++;
                    continue;
                }

                var dens = Density(isl);
                var maxDensScale = 1f;
                var minDensScale = 4f / Mathf.Max(4f, shortSide);
                if (dens > maxDens && dens > 1e-6f) maxDensScale = Mathf.Clamp01(maxDens / dens);
                if (dens > 1e-6f) minDensScale = Mathf.Max(minDensScale, Mathf.Min(1f, minDens / dens));

                var uniform = BinarySearchUniform(state, isl, q, minDensScale, maxDensScale);
                var aniso = RefineAniso(state, isl, q, uniform);
                isl.Scale = aniso;
                state.Report.IslandsScaled++;
                state.Log.VerboseInfo(string.Format(
                    "island #{0} {1} px={2:F0}x{3:F0} scale=({4:F3},{5:F3}) dens={6:F0}px/m",
                    isl.Id, isl.Source.name, isl.PixelBounds.width, isl.PixelBounds.height,
                    isl.Scale.x, isl.Scale.y, dens));
            }

            ApplyUvGroupBarrel(state);
        }

        private static float Density(ATOIsland isl)
        {
            var areaPx = Mathf.Max(1f, isl.PixelBounds.width * isl.PixelBounds.height);
            var world = Mathf.Max(1e-8f, isl.WorldArea);
            return Mathf.Sqrt(areaPx / world);
        }

        private static Vector2 BinarySearchUniform(ATOState state, ATOIsland isl, ATOQualityParameters q,
            float minDensScale, float maxDensScale)
        {
            if (!Passes(state, isl, q, Vector2.one)) return Vector2.one;
            var lo = Mathf.Clamp(1f / 64f, 1f / 64f, 1f);
            var hi = 1f;
            for (var i = 0; i < 10; i++)
            {
                var mid = (lo + hi) * 0.5f;
                if (Passes(state, isl, q, new Vector2(mid, mid))) hi = mid;
                else lo = mid;
            }

            var s = hi;
            if (s < minDensScale) s = Mathf.Min(1f, minDensScale);
            if (s > maxDensScale && Passes(state, isl, q, new Vector2(maxDensScale, maxDensScale)))
                s = maxDensScale;
            return new Vector2(s, s);
        }

        private static Vector2 RefineAniso(ATOState state, ATOIsland isl, ATOQualityParameters q, Vector2 uniform)
        {
            var sx = BinaryAxis(state, isl, q, uniform, true);
            var sy = BinaryAxis(state, isl, q, new Vector2(sx, uniform.y), false);
            return new Vector2(sx, sy);
        }

        private static float BinaryAxis(ATOState state, ATOIsland isl, ATOQualityParameters q, Vector2 start, bool axisX)
        {
            var lo = 4f / Mathf.Max(4f, axisX ? isl.PixelBounds.width : isl.PixelBounds.height);
            lo = Mathf.Clamp(lo, 1f / 64f, axisX ? start.x : start.y);
            var hi = axisX ? start.x : start.y;
            for (var i = 0; i < 8; i++)
            {
                var mid = (lo + hi) * 0.5f;
                var s = axisX ? new Vector2(mid, start.y) : new Vector2(start.x, mid);
                if (Passes(state, isl, q, s)) hi = mid;
                else lo = mid;
            }

            return hi;
        }

        internal static bool Passes(ATOState state, ATOIsland isl, ATOQualityParameters q, Vector2 scale)
        {
            var metrics = Evaluate(state, isl, scale);
            if (metrics == null) return false;
            return metrics.Passes(q, isl.Semantic);
        }

        internal static ATOMetrics Evaluate(ATOState state, ATOIsland isl, Vector2 scale)
        {
            var src = state.Cache.Get(isl.Source, state.Log);
            if (src == null) return null;
            var pw = Mathf.Max(1, Mathf.RoundToInt(isl.PixelBounds.width));
            var ph = Mathf.Max(1, Mathf.RoundToInt(isl.PixelBounds.height));
            var dw = Mathf.Max(1, Mathf.RoundToInt(pw * scale.x));
            var dh = Mathf.Max(1, Mathf.RoundToInt(ph * scale.y));
            var x0 = Mathf.Clamp(Mathf.FloorToInt(isl.PixelBounds.xMin), 0, src.Width - 1);
            var y0 = Mathf.Clamp(Mathf.FloorToInt(isl.PixelBounds.yMin), 0, src.Height - 1);

            var original = Crop(src, x0, y0, pw, ph);
            var down = DownsamplePremult(original, pw, ph, dw, dh, isl.Semantic);
            var up = UpsampleBilinear(down, dw, dh, pw, ph);
            return Compare(original, up, pw, ph, isl, state);
        }

        private static Color[] Crop(ATODecodedTexture src, int x0, int y0, int w, int h)
        {
            var o = new Color[w * h];
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    var c = src.GetLinear(x0 + x, y0 + y);
                    o[y * w + x] = c;
                }
            }

            return o;
        }

        // English: Transparent maps premultiply alpha before downsample (user spec).
        // 中文：透明贴图下采样前预乘 alpha。
        private static Color[] DownsamplePremult(Color[] src, int sw, int sh, int dw, int dh, ATOTextureSemantic sem)
        {
            var dst = new Color[dw * dh];
            var premult = sem == ATOTextureSemantic.AlbedoTransparent;
            for (var y = 0; y < dh; y++)
            {
                var v0 = (float)y / dh * sh;
                var v1 = (float)(y + 1) / dh * sh;
                for (var x = 0; x < dw; x++)
                {
                    var u0 = (float)x / dw * sw;
                    var u1 = (float)(x + 1) / dw * sw;
                    var acc = new Color(0, 0, 0, 0);
                    var n = 0f;
                    var yStart = Mathf.Clamp(Mathf.FloorToInt(v0), 0, sh - 1);
                    var yEnd = Mathf.Clamp(Mathf.CeilToInt(v1), 1, sh);
                    var xStart = Mathf.Clamp(Mathf.FloorToInt(u0), 0, sw - 1);
                    var xEnd = Mathf.Clamp(Mathf.CeilToInt(u1), 1, sw);
                    for (var sy = yStart; sy < yEnd; sy++)
                    {
                        for (var sx = xStart; sx < xEnd; sx++)
                        {
                            var c = src[sy * sw + sx];
                            if (premult)
                            {
                                acc.r += c.r * c.a;
                                acc.g += c.g * c.a;
                                acc.b += c.b * c.a;
                                acc.a += c.a;
                            }
                            else acc += c;
                            n += 1f;
                        }
                    }

                    if (n < 1f) n = 1f;
                    acc /= n;
                    if (premult && acc.a > 1e-6f)
                    {
                        acc.r /= acc.a;
                        acc.g /= acc.a;
                        acc.b /= acc.a;
                    }

                    if (sem == ATOTextureSemantic.Normal)
                    {
                        var nrm = DecodeNormal(acc);
                        nrm.Normalize();
                        acc = EncodeNormal(nrm);
                    }

                    dst[y * dw + x] = acc;
                }
            }

            return dst;
        }

        private static Color[] UpsampleBilinear(Color[] src, int sw, int sh, int dw, int dh)
        {
            var dst = new Color[dw * dh];
            for (var y = 0; y < dh; y++)
            {
                var v = (y + 0.5f) / dh * sh - 0.5f;
                var y0 = Mathf.Clamp(Mathf.FloorToInt(v), 0, sh - 1);
                var y1 = Mathf.Min(y0 + 1, sh - 1);
                var fy = v - y0;
                for (var x = 0; x < dw; x++)
                {
                    var u = (x + 0.5f) / dw * sw - 0.5f;
                    var x0 = Mathf.Clamp(Mathf.FloorToInt(u), 0, sw - 1);
                    var x1 = Mathf.Min(x0 + 1, sw - 1);
                    var fx = u - x0;
                    var c00 = src[y0 * sw + x0];
                    var c10 = src[y0 * sw + x1];
                    var c01 = src[y1 * sw + x0];
                    var c11 = src[y1 * sw + x1];
                    dst[y * dw + x] = Color.Lerp(Color.Lerp(c00, c10, fx), Color.Lerp(c01, c11, fx), fy);
                }
            }

            return dst;
        }

        private static ATOMetrics Compare(Color[] a, Color[] b, int w, int h, ATOIsland isl, ATOState state)
        {
            var m = new ATOMetrics();
            var shortSide = Mathf.Min(w, h);
            if (isl.Semantic == ATOTextureSemantic.Normal)
            {
                ComputeNormal(a, b, w, h, m);
                return m;
            }

            if (isl.Semantic == ATOTextureSemantic.Gray || isl.Semantic == ATOTextureSemantic.Mask)
            {
                ComputeGray(a, b, w, h, m);
                return m;
            }

            if (shortSide >= 11)
            {
                m.MsSsim = shortSide < 176
                    ? Ssim(a, b, w, h)
                    : MsSsim(a, b, w, h);
            }
            else
            {
                m.MsSsim = 1f; // ignored
            }

            m.DeltaE = MeanCiede2000(a, b, w, h);

            var alphaMode = StrictestAlpha(state, isl.Source);
            var cutoff = StrictestCutoff(state, isl.Source);
            if (isl.Semantic == ATOTextureSemantic.AlbedoTransparent || alphaMode != ATOAlphaMode.Opaque)
            {
                if (alphaMode == ATOAlphaMode.Cutout) m.CutoutIou = ClipIou(a, b, w * h, cutoff);
                else m.AlphaRmse = AlphaRmse(a, b, w * h);
            }

            return m;
        }

        private static ATOAlphaMode StrictestAlpha(ATOState state, Texture2D tex)
        {
            var mode = ATOAlphaMode.Opaque;
            foreach (var u in state.Uses)
            {
                if (u.Texture != tex) continue;
                if (u.AlphaMode == ATOAlphaMode.Cutout) return ATOAlphaMode.Cutout;
                if (u.AlphaMode == ATOAlphaMode.Blend) mode = ATOAlphaMode.Blend;
            }

            return mode;
        }

        private static float StrictestCutoff(ATOState state, Texture2D tex)
        {
            var c = 0.5f;
            foreach (var u in state.Uses)
            {
                if (u.Texture == tex) c = Mathf.Max(c, u.Cutoff);
            }

            return c;
        }

        private static void ComputeNormal(Color[] a, Color[] b, int w, int h, ATOMetrics m)
        {
            var n = w * h;
            var angles = new float[n];
            double sum = 0;
            for (var i = 0; i < n; i++)
            {
                var na = DecodeNormal(a[i]).normalized;
                var nb = DecodeNormal(b[i]).normalized;
                var d = Mathf.Clamp(Vector3.Dot(na, nb), -1f, 1f);
                var ang = Mathf.Acos(d) * Mathf.Rad2Deg;
                angles[i] = ang;
                sum += ang;
            }

            Array.Sort(angles);
            m.NormalMeanDeg = (float)(sum / n);
            var p95 = Mathf.Clamp(Mathf.FloorToInt(n * 0.95f), 0, n - 1);
            m.NormalP95Deg = angles[p95];
        }

        private static void ComputeGray(Color[] a, Color[] b, int w, int h, ATOMetrics m)
        {
            var n = w * h;
            double rr = 0, gg = 0, bb = 0, aa = 0;
            for (var i = 0; i < n; i++)
            {
                var d = a[i] - b[i];
                rr += d.r * d.r;
                gg += d.g * d.g;
                bb += d.b * d.b;
                aa += d.a * d.a;
            }

            rr = Math.Sqrt(rr / n);
            gg = Math.Sqrt(gg / n);
            bb = Math.Sqrt(bb / n);
            aa = Math.Sqrt(aa / n);
            m.GrayRmse = (float)Math.Max(Math.Max(rr, gg), Math.Max(bb, aa));
        }

        private static float AlphaRmse(Color[] a, Color[] b, int n)
        {
            double s = 0;
            for (var i = 0; i < n; i++)
            {
                var d = a[i].a - b[i].a;
                s += d * d;
            }

            return (float)Math.Sqrt(s / n);
        }

        private static float ClipIou(Color[] a, Color[] b, int n, float cutoff)
        {
            var inter = 0;
            var uni = 0;
            for (var i = 0; i < n; i++)
            {
                var aa = a[i].a >= cutoff;
                var bb = b[i].a >= cutoff;
                if (aa && bb) inter++;
                if (aa || bb) uni++;
            }

            return uni == 0 ? 1f : (float)inter / uni;
        }

        private static float Ssim(Color[] a, Color[] b, int w, int h)
        {
            // Single-scale SSIM on luma (linear Rec.709).
            const float c1 = 0.01f * 0.01f;
            const float c2 = 0.03f * 0.03f;
            double meanA = 0, meanB = 0;
            var n = w * h;
            var la = new float[n];
            var lb = new float[n];
            for (var i = 0; i < n; i++)
            {
                la[i] = Luma(a[i]);
                lb[i] = Luma(b[i]);
                meanA += la[i];
                meanB += lb[i];
            }

            meanA /= n;
            meanB /= n;
            double varA = 0, varB = 0, cov = 0;
            for (var i = 0; i < n; i++)
            {
                var da = la[i] - meanA;
                var db = lb[i] - meanB;
                varA += da * da;
                varB += db * db;
                cov += da * db;
            }

            varA /= n;
            varB /= n;
            cov /= n;
            var s = ((2 * meanA * meanB + c1) * (2 * cov + c2)) /
                    ((meanA * meanA + meanB * meanB + c1) * (varA + varB + c2));
            return (float)Math.Max(0, Math.Min(1, s));
        }

        private static float MsSsim(Color[] a, Color[] b, int w, int h)
        {
            // 5-scale MS-SSIM (Wang et al.) with equal weights simplified for bake-time.
            var weights = new[] { 0.0448f, 0.2856f, 0.3001f, 0.2363f, 0.1333f };
            double acc = 1;
            var ca = a;
            var cb = b;
            var cw = w;
            var ch = h;
            for (var s = 0; s < weights.Length; s++)
            {
                var ss = Ssim(ca, cb, cw, ch);
                acc *= Math.Pow(Math.Max(1e-6, ss), weights[s]);
                if (s == weights.Length - 1) break;
                if (cw < 8 || ch < 8) break;
                ca = BoxHalf(ca, cw, ch);
                cb = BoxHalf(cb, cw, ch);
                cw = Mathf.Max(1, cw / 2);
                ch = Mathf.Max(1, ch / 2);
            }

            return (float)acc;
        }

        private static Color[] BoxHalf(Color[] src, int w, int h)
        {
            var nw = Mathf.Max(1, w / 2);
            var nh = Mathf.Max(1, h / 2);
            var dst = new Color[nw * nh];
            for (var y = 0; y < nh; y++)
            {
                for (var x = 0; x < nw; x++)
                {
                    var x0 = x * 2;
                    var y0 = y * 2;
                    var x1 = Mathf.Min(x0 + 1, w - 1);
                    var y1 = Mathf.Min(y0 + 1, h - 1);
                    dst[y * nw + x] = (src[y0 * w + x0] + src[y0 * w + x1] + src[y1 * w + x0] + src[y1 * w + x1]) * 0.25f;
                }
            }

            return dst;
        }

        private static float MeanCiede2000(Color[] a, Color[] b, int w, int h)
        {
            double s = 0;
            var n = w * h;
            var step = n > 4096 ? n / 4096 : 1;
            var count = 0;
            for (var i = 0; i < n; i += step)
            {
                s += Ciede2000(a[i], b[i]);
                count++;
            }

            return count == 0 ? 0 : (float)(s / count);
        }

        // Sharma, Wu, Dalal CIEDE2000.
        internal static double Ciede2000(Color a, Color b)
        {
            double L1, aa1, bb1, L2, aa2, bb2;
            RgbToLab(a, out L1, out aa1, out bb1);
            RgbToLab(b, out L2, out aa2, out bb2);
            var avgLp = (L1 + L2) * 0.5;
            var c1 = Math.Sqrt(aa1 * aa1 + bb1 * bb1);
            var c2 = Math.Sqrt(aa2 * aa2 + bb2 * bb2);
            var avgC = (c1 + c2) * 0.5;
            var g = 0.5 * (1 - Math.Sqrt(Math.Pow(avgC, 7) / (Math.Pow(avgC, 7) + Math.Pow(25.0, 7))));
            var a1p = (1 + g) * aa1;
            var a2p = (1 + g) * aa2;
            var c1p = Math.Sqrt(a1p * a1p + bb1 * bb1);
            var c2p = Math.Sqrt(a2p * a2p + bb2 * bb2);
            var avgCp = (c1p + c2p) * 0.5;
            var h1p = Hyp(bb1, a1p);
            var h2p = Hyp(bb2, a2p);
            var dLp = L2 - L1;
            var dCp = c2p - c1p;
            var dhp = 0.0;
            if (c1p * c2p != 0)
            {
                var dh = h2p - h1p;
                if (dh > 180) dh -= 360;
                if (dh < -180) dh += 360;
                dhp = dh;
            }

            var dHp = 2 * Math.Sqrt(c1p * c2p) * Math.Sin(dhp * Math.PI / 360.0);
            var avgHp = h1p + h2p;
            if (c1p * c2p != 0)
            {
                var dh = Math.Abs(h1p - h2p);
                if (dh > 180) avgHp = (h1p + h2p + 360) * 0.5;
                else avgHp = (h1p + h2p) * 0.5;
            }

            var t = 1 - 0.17 * Math.Cos(Rad(avgHp - 30)) + 0.24 * Math.Cos(Rad(2 * avgHp)) +
                    0.32 * Math.Cos(Rad(3 * avgHp + 6)) - 0.20 * Math.Cos(Rad(4 * avgHp - 63));
            var sl = 1 + 0.015 * Math.Pow(avgLp - 50, 2) / Math.Sqrt(20 + Math.Pow(avgLp - 50, 2));
            var sc = 1 + 0.045 * avgCp;
            var sh = 1 + 0.015 * avgCp * t;
            var rt = -2 * Math.Sqrt(Math.Pow(avgCp, 7) / (Math.Pow(avgCp, 7) + Math.Pow(25.0, 7))) *
                     Math.Sin(Rad(60 * Math.Exp(-Math.Pow((avgHp - 275) / 25, 2))));
            var dE = Math.Sqrt(Math.Pow(dLp / sl, 2) + Math.Pow(dCp / sc, 2) + Math.Pow(dHp / sh, 2) +
                               rt * (dCp / sc) * (dHp / sh));
            return dE;
        }

        private static double Hyp(double b, double ap)
        {
            if (ap == 0 && b == 0) return 0;
            var h = Math.Atan2(b, ap) * 180.0 / Math.PI;
            return h < 0 ? h + 360 : h;
        }

        private static double Rad(double d)
        {
            return d * Math.PI / 180.0;
        }

        private static void RgbToLab(Color c, out double L, out double A, out double B)
        {
            double r = PivotRgb(c.r), g = PivotRgb(c.g), b = PivotRgb(c.b);
            var x = r * 0.4124564 + g * 0.3575761 + b * 0.1804375;
            var y = r * 0.2126729 + g * 0.7151522 + b * 0.0721750;
            var z = r * 0.0193339 + g * 0.1191920 + b * 0.9503041;
            x /= 0.95047;
            z /= 1.08883;
            x = PivotXyz(x);
            y = PivotXyz(y);
            z = PivotXyz(z);
            L = 116 * y - 16;
            A = 500 * (x - y);
            B = 200 * (y - z);
        }

        private static double PivotRgb(double u)
        {
            u = Math.Max(0, u);
            return u > 0.04045 ? Math.Pow((u + 0.055) / 1.055, 2.4) : u / 12.92;
        }

        private static double PivotXyz(double u)
        {
            return u > 0.008856 ? Math.Pow(u, 1.0 / 3.0) : 7.787 * u + 16.0 / 116.0;
        }

        private static float Luma(Color c)
        {
            return 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;
        }

        internal static Vector3 DecodeNormal(Color c)
        {
            return new Vector3(c.r * 2f - 1f, c.g * 2f - 1f, c.b * 2f - 1f);
        }

        internal static Color EncodeNormal(Vector3 n)
        {
            return new Color(n.x * 0.5f + 0.5f, n.y * 0.5f + 0.5f, n.z * 0.5f + 0.5f, 1f);
        }

        private static void ApplyUvGroupBarrel(ATOState state)
        {
            // Built later if groups already exist; otherwise group by renderer+uv+shared textures.
            var groups = new Dictionary<string, List<ATOIsland>>();
            foreach (var isl in state.Islands)
            {
                var key = (isl.Renderer != null && isl.Renderer.Renderer != null
                              ? isl.Renderer.Renderer.GetInstanceID()
                              : 0) + "|" + isl.UvChannel;
                List<ATOIsland> list;
                if (!groups.TryGetValue(key, out list))
                {
                    list = new List<ATOIsland>();
                    groups[key] = list;
                }

                list.Add(isl);
            }

            foreach (var kv in groups)
            {
                var maxX = 0f;
                var maxY = 0f;
                var capX = 0f;
                var capY = 0f;
                foreach (var isl in kv.Value)
                {
                    maxX = Mathf.Max(maxX, isl.Scale.x * isl.PixelBounds.width);
                    maxY = Mathf.Max(maxY, isl.Scale.y * isl.PixelBounds.height);
                    capX = Mathf.Max(capX, isl.PixelBounds.width);
                    capY = Mathf.Max(capY, isl.PixelBounds.height);
                }

                maxX = Mathf.Min(maxX, capX);
                maxY = Mathf.Min(maxY, capY);
                foreach (var isl in kv.Value)
                {
                    if (isl.PixelBounds.width > 1e-4f)
                        isl.Scale.x = Mathf.Max(isl.Scale.x, maxX / isl.PixelBounds.width);
                    if (isl.PixelBounds.height > 1e-4f)
                        isl.Scale.y = Mathf.Max(isl.Scale.y, maxY / isl.PixelBounds.height);
                    isl.Scale.x = Mathf.Min(1f, isl.Scale.x);
                    isl.Scale.y = Mathf.Min(1f, isl.Scale.y);
                }
            }
        }
    }

    internal sealed class ATOMetrics
    {
        public float MsSsim = 1f;
        public float DeltaE;
        public float AlphaRmse;
        public float CutoutIou = 1f;
        public float NormalP95Deg;
        public float NormalMeanDeg;
        public float GrayRmse;

        public bool Passes(ATOQualityParameters q, ATOTextureSemantic sem)
        {
            if (q == null) return true;
            switch (sem)
            {
                case ATOTextureSemantic.Normal:
                    return NormalP95Deg <= q.normalP95DegMax + 1e-4f;
                case ATOTextureSemantic.Gray:
                case ATOTextureSemantic.Mask:
                    return GrayRmse <= q.grayRmseMax + 1e-6f;
                default:
                    if (MsSsim + 1e-6f < q.msSsimMin) return false;
                    if (DeltaE > q.deltaEMax + 1e-4f) return false;
                    if (AlphaRmse > q.alphaRmseMax + 1e-6f) return false;
                    if (CutoutIou + 1e-6f < q.cutoutIouMin) return false;
                    return true;
            }
        }
    }
}
