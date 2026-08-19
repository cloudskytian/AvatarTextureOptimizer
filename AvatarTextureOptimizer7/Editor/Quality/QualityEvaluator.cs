using System;
using System.Collections.Generic;
using Fosa.AvatarTextureOptimizer;
using Fosa.AvatarTextureOptimizer.API;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Target-quality scaler. Linear resample, premultiplied-alpha downsample,
    /// MS-SSIM (single-scale under 176 px, skip under 11 px) + CIEDE2000 + alpha
    /// (Cutout IoU / Blend RMSE) / normal angle / gray RMSE.
    /// Compare by bilinear-upsampling the scaled island back to the original size.
    /// 目标质量缩放器。线性重采样，透明预乘下采样，MS-SSIM（短边&lt;176 单尺度，&lt;11 忽略）
    /// + CIEDE2000 + Alpha（Cutout IoU / Blend RMSE）/ 法线角度 / 灰度 RMSE。
    /// 将缩小后的岛双线性上采样回原尺寸再比较。
    /// </summary>
    public static class QualityEvaluator
    {
        const float SolidEps = 1e-4f;

        public static void ScaleGraph(AtoSession session, AtoGraph graph)
        {
            foreach (var ug in graph.UvGroups)
            {
                session.SetProgress("progress.quality", 0.45f, "UV group " + ug.Id);
                ScaleUvGroup(session, ug);
            }
        }

        static void ScaleUvGroup(AtoSession session, UvGroup ug)
        {
            if (ug.Islands.Count == 0) return;
            if (session.Lossless)
            {
                foreach (var isl in ug.Islands)
                {
                    isl.ScaledW = isl.OrigPixelW;
                    isl.ScaledH = isl.OrigPixelH;
                    isl.ScaleU = 1f;
                    isl.ScaleV = 1f;
                }

                session.Log.VerboseInfo("Lossless: skip UV scale for UV group " + ug.Id);
                return;
            }

            // Bucket: each island/texture pair votes a minimum size; take the max.
            // 木桶：每个岛/贴图组合投一票最小尺寸，取最大。
            foreach (var isl in ug.Islands)
            {
                float bestU = 0f, bestV = 0f;
                int bestW = 1, bestH = 1;
                foreach (var b in ug.Bindings)
                {
                    var tex = b.Slot?.Texture;
                    if (tex == null) continue;
                    if (session.WhitelistTextures.Contains(tex) && ReferenceEquals(tex, isl.SourceTexture) == false)
                    {
                        // Other textures on a whitelist UV still participate in whole-tex / import opt,
                        // but island scale is driven by non-whitelist members.
                    }

                    var vote = ScaleIsland(session, isl, b.Slot);
                    if (vote.w > bestW) bestW = vote.w;
                    if (vote.h > bestH) bestH = vote.h;
                    bestU = Mathf.Max(bestU, vote.su);
                    bestV = Mathf.Max(bestV, vote.sv);
                }

                var maxEdge = Mathf.Max(1, ug.MaxSourceEdge);
                bestW = Mathf.Clamp(bestW, 1, Mathf.Max(1, isl.OrigPixelW));
                bestH = Mathf.Clamp(bestH, 1, Mathf.Max(1, isl.OrigPixelH));
                // Cap by UV-group max original. / 不超过 UV 组内最大原尺寸。
                bestW = Mathf.Min(bestW, maxEdge);
                bestH = Mathf.Min(bestH, maxEdge);
                isl.ScaledW = Mathf.Max(1, bestW);
                isl.ScaledH = Mathf.Max(1, bestH);
                isl.ScaleU = isl.OrigPixelW > 0 ? (float)isl.ScaledW / isl.OrigPixelW : 1f;
                isl.ScaleV = isl.OrigPixelH > 0 ? (float)isl.ScaledH / isl.OrigPixelH : 1f;
            }
        }

        struct Vote
        {
            public int w, h;
            public float su, sv;
        }

        static Vote ScaleIsland(AtoSession session, UvIsland isl, AtoTextureSlot slot)
        {
            var tex = slot.Texture;
            var ow = Mathf.Max(1, isl.OrigPixelW);
            var oh = Mathf.Max(1, isl.OrigPixelH);
            var shortSide = Mathf.Min(ow, oh);

            var dec = session.DecodeCache.Get(tex, slot.Kind == AtoTextureKind.Normal);
            var crop = CropIsland(dec, isl, tex.width, tex.height);

            if (!session.Lossless && IsSolid(crop, ow, oh, slot.Kind))
            {
                isl.SolidColor = true;
                var s = Mathf.Max(1, Mathf.Min(4, shortSide));
                session.Log.VerboseInfo("Solid-color island " + tex.name + " -> " + s + "px");
                return new Vote { w = s, h = s, su = (float)s / ow, sv = (float)s / oh };
            }

            // Pixel-density clamp. / 像素密度钳制。
            var minScale = DensityMinScale(session, isl, ow, oh);
            var maxScale = 1f;
            // Also cannot exceed the physical island on the imported file. / 不能超过原文件上的物理岛尺寸。
            maxScale = Mathf.Min(maxScale, 1f);

            // Uniform binary search. / 均匀二分。
            float lo = minScale, hi = maxScale;
            float pass = maxScale;
            for (int i = 0; i < 10; i++)
            {
                var mid = 0.5f * (lo + hi);
                var tw = Mathf.Max(1, Mathf.RoundToInt(ow * mid));
                var th = Mathf.Max(1, Mathf.RoundToInt(oh * mid));
                if (Passes(session, crop, ow, oh, tw, th, slot))
                {
                    pass = mid;
                    hi = mid;
                }
                else lo = mid;
            }

            var uniW = Mathf.Max(1, Mathf.RoundToInt(ow * pass));
            var uniH = Mathf.Max(1, Mathf.RoundToInt(oh * pass));

            // Anisotropic refine: independent axis binary search after uniform pass.
            // 各向异性细化：均匀达标后再双轴独立二分。
            var su = pass;
            var sv = pass;
            lo = minScale;
            hi = su;
            for (int i = 0; i < 8; i++)
            {
                var mid = 0.5f * (lo + hi);
                var tw = Mathf.Max(1, Mathf.RoundToInt(ow * mid));
                if (Passes(session, crop, ow, oh, tw, uniH, slot)) { su = mid; hi = mid; }
                else lo = mid;
            }

            lo = minScale;
            hi = sv;
            for (int i = 0; i < 8; i++)
            {
                var mid = 0.5f * (lo + hi);
                var th = Mathf.Max(1, Mathf.RoundToInt(oh * mid));
                var tw = Mathf.Max(1, Mathf.RoundToInt(ow * su));
                if (Passes(session, crop, ow, oh, tw, th, slot)) { sv = mid; hi = mid; }
                else lo = mid;
            }

            return new Vote
            {
                w = Mathf.Max(1, Mathf.RoundToInt(ow * su)),
                h = Mathf.Max(1, Mathf.RoundToInt(oh * sv)),
                su = su,
                sv = sv
            };
        }

        static float DensityMinScale(AtoSession session, UvIsland isl, int ow, int oh)
        {
            if (isl.WorldArea <= 1e-10f) return 1f / Mathf.Max(ow, oh);
            // density = pixels / metre along short side ≈ shortPx / sqrt(area) for a square-ish island.
            // 密度 ≈ 短边像素 / sqrt(面积)。
            var shortPx = (float)Mathf.Min(ow, oh);
            var metres = Mathf.Sqrt(Mathf.Max(isl.WorldArea, 1e-10f));
            var cur = shortPx / metres;
            if (cur <= session.MinPxPerMeter) return 1f; // already at/under min, do not shrink
            var target = session.MaxPxPerMeter > 0 ? Mathf.Min(cur, session.MaxPxPerMeter) : cur;
            // We only shrink, so scale so density approaches max? Spec: clamp to [min,max] to avoid waste or blur.
            // 只缩小。若当前密度高于 max，缩到 max；不低于 min。
            if (cur > session.MaxPxPerMeter && session.MaxPxPerMeter > 0)
            {
                return Mathf.Clamp(session.MaxPxPerMeter / cur, 1f / shortPx, 1f);
            }

            return Mathf.Max(1f / shortPx, session.MinPxPerMeter / cur);
        }

        static bool Passes(AtoSession session, Color[] src, int ow, int oh, int tw, int th, AtoTextureSlot slot)
        {
            if (tw >= ow && th >= oh) return true;
            var down = Resample(src, ow, oh, tw, th, slot.AlphaMode != AtoAlphaMode.Opaque);
            var up = Resample(down, tw, th, ow, oh, false);
            var sample = Compare(src, up, ow, oh, slot);
            foreach (var hook in AtoExtensions.GetQualityHooks())
            {
                if (hook != null && !hook.Accept(slot.Texture, slot.Kind, sample, session.Quality))
                    return false;
            }

            return sample.Passes(session.Quality, slot.Kind, slot.AlphaMode);
        }

        public static Color[] CropIsland(TextureDecodeCache.Decoded dec, UvIsland isl, int texW, int texH)
        {
            var x0 = Mathf.Clamp(Mathf.FloorToInt(isl.MinUvNorm.x * texW), 0, texW - 1);
            var y0 = Mathf.Clamp(Mathf.FloorToInt(isl.MinUvNorm.y * texH), 0, texH - 1);
            var x1 = Mathf.Clamp(Mathf.CeilToInt(isl.MaxUvNorm.x * texW), x0 + 1, texW);
            var y1 = Mathf.Clamp(Mathf.CeilToInt(isl.MaxUvNorm.y * texH), y0 + 1, texH);
            var w = x1 - x0;
            var h = y1 - y0;
            isl.OrigPixelW = w;
            isl.OrigPixelH = h;
            var crop = new Color[w * h];
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var sx = Mathf.Clamp(x0 + x, 0, dec.Width - 1);
                var sy = Mathf.Clamp(y0 + y, 0, dec.Height - 1);
                crop[y * w + x] = dec.Linear[sy * dec.Width + sx];
            }

            return crop;
        }

        static bool IsSolid(Color[] px, int w, int h, AtoTextureKind kind)
        {
            if (px == null || px.Length == 0) return true;
            var c0 = px[0];
            for (int i = 1; i < px.Length; i++)
            {
                var d = px[i];
                if (Mathf.Abs(d.r - c0.r) > SolidEps || Mathf.Abs(d.g - c0.g) > SolidEps ||
                    Mathf.Abs(d.b - c0.b) > SolidEps || Mathf.Abs(d.a - c0.a) > SolidEps)
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Bilinear resample. Transparent sources use premultiplied alpha on downsample.
        /// 双线性重采样。透明源下采样时预乘 Alpha。
        /// </summary>
        public static Color[] Resample(Color[] src, int sw, int sh, int dw, int dh, bool premultiply)
        {
            var dst = new Color[dw * dh];
            if (sw <= 0 || sh <= 0 || dw <= 0 || dh <= 0) return dst;
            for (int y = 0; y < dh; y++)
            {
                var v = (y + 0.5f) * sh / dh - 0.5f;
                var y0 = Mathf.FloorToInt(v);
                var fy = v - y0;
                var y1 = y0 + 1;
                y0 = Mathf.Clamp(y0, 0, sh - 1);
                y1 = Mathf.Clamp(y1, 0, sh - 1);
                for (int x = 0; x < dw; x++)
                {
                    var u = (x + 0.5f) * sw / dw - 0.5f;
                    var x0 = Mathf.FloorToInt(u);
                    var fx = u - x0;
                    var x1 = x0 + 1;
                    x0 = Mathf.Clamp(x0, 0, sw - 1);
                    x1 = Mathf.Clamp(x1, 0, sw - 1);
                    var c00 = Sample(src, sw, x0, y0, premultiply);
                    var c10 = Sample(src, sw, x1, y0, premultiply);
                    var c01 = Sample(src, sw, x0, y1, premultiply);
                    var c11 = Sample(src, sw, x1, y1, premultiply);
                    var c0 = Color.LerpUnclamped(c00, c10, fx);
                    var c1 = Color.LerpUnclamped(c01, c11, fx);
                    var c = Color.LerpUnclamped(c0, c1, fy);
                    if (premultiply && c.a > 1e-6f)
                    {
                        c.r /= c.a;
                        c.g /= c.a;
                        c.b /= c.a;
                    }

                    dst[y * dw + x] = c;
                }
            }

            return dst;
        }

        static Color Sample(Color[] src, int w, int x, int y, bool premultiply)
        {
            var c = src[y * w + x];
            if (!premultiply) return c;
            return new Color(c.r * c.a, c.g * c.a, c.b * c.a, c.a);
        }

        public static AtoQualitySample Compare(Color[] a, Color[] b, int w, int h, AtoTextureSlot slot)
        {
            var sample = new AtoQualitySample { MsSsim = 1f, CutoutIou = 1f };
            var n = Mathf.Min(a.Length, b.Length);
            if (n == 0) return sample;
            var shortSide = Mathf.Min(w, h);

            if (slot.Kind == AtoTextureKind.Normal)
            {
                EvaluateNormal(a, b, n, out sample.NormalMeanDegrees, out sample.NormalP95Degrees);
                return sample;
            }

            if (slot.Kind == AtoTextureKind.Gray || slot.Kind == AtoTextureKind.Mask)
            {
                sample.GrayRmse = EvaluateGray(a, b, n, slot.UsedChannels);
                return sample;
            }

            if (shortSide < 11)
            {
                sample.SkippedSsimForTinyIsland = true;
            }
            else if (shortSide < 176)
            {
                sample.UsedSingleScaleSsim = true;
                sample.MsSsim = SsimLuma(a, b, w, h);
            }
            else
            {
                sample.MsSsim = MsSsimLuma(a, b, w, h);
            }

            sample.DeltaE = MeanCiede2000(a, b, n);

            if (slot.AlphaMode == AtoAlphaMode.Blend)
                sample.AlphaRmse = RmseAlpha(a, b, n);
            if (slot.AlphaMode == AtoAlphaMode.Cutout)
                sample.CutoutIou = CutoutIou(a, b, n, slot.Cutoff);

            return sample;
        }

        static void EvaluateNormal(Color[] a, Color[] b, int n, out float mean, out float p95)
        {
            var acc = 0.0;
            var angles = new float[n];
            for (int i = 0; i < n; i++)
            {
                var na = new Vector3(a[i].r, a[i].g, a[i].b).normalized;
                var nb = new Vector3(b[i].r, b[i].g, b[i].b).normalized;
                var d = Mathf.Clamp(Vector3.Dot(na, nb), -1f, 1f);
                var deg = Mathf.Acos(d) * Mathf.Rad2Deg;
                angles[i] = deg;
                acc += deg;
            }

            mean = (float)(acc / n);
            Array.Sort(angles);
            p95 = angles[Mathf.Clamp(Mathf.FloorToInt(n * 0.95f), 0, n - 1)];
        }

        static float EvaluateGray(Color[] a, Color[] b, int n, bool[] used)
        {
            float worst = 0f;
            for (int ch = 0; ch < 4; ch++)
            {
                if (used != null && ch < used.Length && !used[ch]) continue;
                double acc = 0;
                for (int i = 0; i < n; i++)
                {
                    var d = Chan(a[i], ch) - Chan(b[i], ch);
                    acc += d * d;
                }

                worst = Mathf.Max(worst, Mathf.Sqrt((float)(acc / n)));
            }

            return worst;
        }

        static float Chan(Color c, int i)
        {
            switch (i)
            {
                case 0: return c.r;
                case 1: return c.g;
                case 2: return c.b;
                default: return c.a;
            }
        }

        static float RmseAlpha(Color[] a, Color[] b, int n)
        {
            double acc = 0;
            for (int i = 0; i < n; i++)
            {
                var d = a[i].a - b[i].a;
                acc += d * d;
            }

            return Mathf.Sqrt((float)(acc / n));
        }

        static float CutoutIou(Color[] a, Color[] b, int n, float cutoff)
        {
            int inter = 0, uni = 0;
            for (int i = 0; i < n; i++)
            {
                var ka = a[i].a >= cutoff;
                var kb = b[i].a >= cutoff;
                if (ka && kb) inter++;
                if (ka || kb) uni++;
            }

            return uni == 0 ? 1f : (float)inter / uni;
        }

        static float MeanCiede2000(Color[] a, Color[] b, int n)
        {
            var step = n > 20000 ? n / 20000 : 1;
            var count = (n + step - 1) / step;
            if (count >= 256)
            {
                var na = new NativeArray<float4>(count, Allocator.TempJob);
                var nb = new NativeArray<float4>(count, Allocator.TempJob);
                var part = new NativeArray<float>(count, Allocator.TempJob);
                try
                {
                    int w = 0;
                    for (int i = 0; i < n; i += step, w++)
                    {
                        na[w] = new float4(a[i].r, a[i].g, a[i].b, a[i].a);
                        nb[w] = new float4(b[i].r, b[i].g, b[i].b, b[i].a);
                    }

                    new Ciede2000Job { A = na, B = nb, Partial = part }
                        .Schedule(count, 64)
                        .Complete();
                    double accJ = 0;
                    for (int i = 0; i < count; i++) accJ += part[i];
                    return (float)(accJ / count);
                }
                finally
                {
                    if (na.IsCreated) na.Dispose();
                    if (nb.IsCreated) nb.Dispose();
                    if (part.IsCreated) part.Dispose();
                }
            }

            double acc = 0;
            int used = 0;
            for (int i = 0; i < n; i += step)
            {
                ColorScience.RgbToLab(a[i].r, a[i].g, a[i].b, out var L1, out var a1, out var b1);
                ColorScience.RgbToLab(b[i].r, b[i].g, b[i].b, out var L2, out var a2, out var b2);
                acc += ColorScience.Ciede2000(L1, a1, b1, L2, a2, b2);
                used++;
            }

            return used == 0 ? 0f : (float)(acc / used);
        }

        static float MsSsimLuma(Color[] a, Color[] b, int w, int h)
        {
            // Wang et al. weights. / Wang 等人权重。
            float[] weights = { 0.0448f, 0.2856f, 0.3001f, 0.2363f, 0.1333f };
            float acc = 1f;
            var ca = a;
            var cb = b;
            var cw = w;
            var ch = h;
            for (int s = 0; s < weights.Length; s++)
            {
                if (cw < 11 || ch < 11) break;
                var ssim = SsimLuma(ca, cb, cw, ch);
                acc *= Mathf.Pow(Mathf.Max(ssim, 1e-6f), weights[s]);
                if (s == weights.Length - 1) break;
                var nw = Mathf.Max(1, cw / 2);
                var nh = Mathf.Max(1, ch / 2);
                ca = Resample(ca, cw, ch, nw, nh, false);
                cb = Resample(cb, cw, ch, nw, nh, false);
                cw = nw;
                ch = nh;
            }

            return acc;
        }

        static float SsimLuma(Color[] a, Color[] b, int w, int h)
        {
            // 11x11 uniform window (Gaussian would be nicer; uniform is stable and Burst-friendly).
            // 11×11 均匀窗（高斯更佳；均匀窗稳定且利于 Burst）。
            const int win = 11;
            const float k1 = 0.01f, k2 = 0.03f;
            const float L = 1f;
            var c1 = (k1 * L) * (k1 * L);
            var c2 = (k2 * L) * (k2 * L);
            if (w < win || h < win)
            {
                // Fallback global SSIM. / 回退全局 SSIM。
                return GlobalSsim(a, b, w * h, c1, c2);
            }

            double sum = 0;
            int count = 0;
            var r = win / 2;
            for (int y = r; y < h - r; y += 2)
            for (int x = r; x < w - r; x += 2)
            {
                double ma = 0, mb = 0;
                int n = 0;
                for (int j = -r; j <= r; j++)
                for (int i = -r; i <= r; i++)
                {
                    var idx = (y + j) * w + (x + i);
                    ma += ColorScience.LinearLuma(a[idx]);
                    mb += ColorScience.LinearLuma(b[idx]);
                    n++;
                }

                ma /= n;
                mb /= n;
                double va = 0, vb = 0, cv = 0;
                for (int j = -r; j <= r; j++)
                for (int i = -r; i <= r; i++)
                {
                    var idx = (y + j) * w + (x + i);
                    var la = ColorScience.LinearLuma(a[idx]) - ma;
                    var lb = ColorScience.LinearLuma(b[idx]) - mb;
                    va += la * la;
                    vb += lb * lb;
                    cv += la * lb;
                }

                va /= (n - 1);
                vb /= (n - 1);
                cv /= (n - 1);
                var ssim = ((2 * ma * mb + c1) * (2 * cv + c2)) /
                           ((ma * ma + mb * mb + c1) * (va + vb + c2) + 1e-12);
                sum += ssim;
                count++;
            }

            return count == 0 ? 1f : (float)(sum / count);
        }

        static float GlobalSsim(Color[] a, Color[] b, int n, float c1, float c2)
        {
            double ma = 0, mb = 0;
            for (int i = 0; i < n; i++)
            {
                ma += ColorScience.LinearLuma(a[i]);
                mb += ColorScience.LinearLuma(b[i]);
            }

            ma /= n;
            mb /= n;
            double va = 0, vb = 0, cv = 0;
            for (int i = 0; i < n; i++)
            {
                var la = ColorScience.LinearLuma(a[i]) - ma;
                var lb = ColorScience.LinearLuma(b[i]) - mb;
                va += la * la;
                vb += lb * lb;
                cv += la * lb;
            }

            va /= Math.Max(1, n - 1);
            vb /= Math.Max(1, n - 1);
            cv /= Math.Max(1, n - 1);
            return (float)(((2 * ma * mb + c1) * (2 * cv + c2)) /
                           ((ma * ma + mb * mb + c1) * (va + vb + c2) + 1e-12));
        }
    }
}
