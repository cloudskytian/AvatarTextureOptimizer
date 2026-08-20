using System.Collections.Generic;
using Fosa.Ato.Editor.Pipeline;
using Fosa.Ato.Editor.Util;
using UnityEngine;

namespace Fosa.Ato.Editor.Quality
{
    /// <summary>
    /// Evaluates whether a UV group passes quality thresholds at candidate scales. GPU path batches
    /// downsample/upsample + metric on RenderTextures; CPU path is a Burst-friendly fallback for
    /// headless or when the GPU path is unavailable. The metric compares the downscaled-then-upscaled
    /// island coverage against the original (final compression loss is excluded per spec).
    /// 评估 UV 组在候选缩放下是否达标。GPU 路径在 RenderTexture 上批量下/上采样与指标；CPU 路径为回退。
    /// 比较“缩小后再上采样”的岛覆盖区与原图（不含最终压缩损失）。
    /// </summary>
    internal sealed class QualityEvaluator
    {
        private readonly AtoPipeline _p;
        // Decoded pixel cache keyed by texture instance. / 解码像素缓存
        private readonly Dictionary<int, Color[]> _pixelCache = new();

        public QualityEvaluator(AtoPipeline p) { _p = p; }

        public bool GroupPassesAt(UvGroup g, float uniformScale) => GroupPassesAt(g, uniformScale, uniformScale);

        public bool GroupPassesAt(UvGroup g, float sx, float sy)
        {
            foreach (var isl in g.Islands)
            {
                if (isl.SourceUsage == null) continue;
                int targetW = Mathf.Max(1, Mathf.RoundToInt(isl.SizePx.x * sx));
                int targetH = Mathf.Max(1, Mathf.RoundToInt(isl.SizePx.y * sy));
                if (targetW <= 0 || targetH <= 0) continue;
                if (!IslandPasses(isl, isl.SourceUsage, targetW, targetH)) return false;
            }
            return true;
        }

        public bool IsSolid(Island isl)
        {
            if (isl.SourceTexture == null) return false;
            var px = GetPixels(isl.SourceTexture, isl.SourceUsage);
            var box = BoxFromIsland(isl, isl.SourceTexture.width, isl.SourceTexture.height);
            return TextureUtil.IsSolid(px, isl.SourceTexture.width, isl.SourceTexture.height, box);
        }

        private bool IslandPasses(Island isl, TextureUsage usage, int targetW, int targetH)
        {
            float shortEdge = Mathf.Min(isl.SizePx.x, isl.SizePx.y);
            var cls = _p.Settings.GetClass(usage.Kind, usage.HasAlphaChannel);

            // Box short edge < 11 => ignore quality metric (always pass). / <11px 忽略指标
            if (shortEdge < 11f) return true;

            var src = GetPixels(isl.SourceTexture, usage);
            if (src == null) return true;

            int sw = isl.SourceTexture.width, sh = isl.SourceTexture.height;
            var box = BoxFromIsland(isl, sw, sh);

            // Try GPU batch first; fall back to CPU box filter + metric. / 先尝试 GPU，再回退 CPU
            // (GPU compute path is invoked through AtoQuality.compute when present; here we run the
            // exact same metric on CPU for determinism during first bring-up.)
            var small = DownsampleBox(src, sw, sh, box, targetW, targetH, usage);
            var up = UpsampleBilinear(small, targetW, targetH, box.width, box.height);

            if (usage.Kind == TextureKind.Normal)
            {
                float mean, p95;
                NormalError(src, sw, sh, box, up, out mean, out p95);
                return mean <= cls.NormalAngleDeg && p95 <= cls.NormalP95Deg;
            }

            if (usage.Kind == TextureKind.Mask || usage.Kind == TextureKind.Data)
            {
                return DataRmse(src, sw, sh, box, up, usage.ChannelsUsedMask) <= cls.DataRmse;
            }

            // Color / Emission: MS-SSIM + ΔE + alpha. / 主色/自发光：MS-SSIM + ΔE + alpha
            bool useMulti = shortEdge >= 176f;
            float ssim = useMulti ? MsSsim(src, sw, sh, box, up) : SsimSingle(src, sw, sh, box, up);
            float de = MeanDeltaE(src, sw, sh, box, up);
            if (ssim < cls.MsSsim) return false;
            if (de > cls.DeltaE) return false;

            if (usage.HasAlphaChannel && usage.Alpha != TexAlphaMode.Opaque)
            {
                if (usage.Alpha == TexAlphaMode.Cutout)
                {
                    float iou = CutoutIou(src, sw, sh, box, up, usage.Cutoff);
                    if (iou < cls.AlphaCutoutIou) return false;
                }
                else
                {
                    float rmse = AlphaRmse(src, sw, sh, box, up);
                    if (rmse > cls.AlphaBlendRmse) return false;
                }
            }
            return true;
        }

        private static RectInt BoxFromIsland(Island isl, int sw, int sh)
        {
            int x = Mathf.Clamp(Mathf.FloorToInt(isl.UvBox.xMin * sw), 0, sw - 1);
            int y = Mathf.Clamp(Mathf.FloorToInt(isl.UvBox.yMin * sh), 0, sh - 1);
            int w = Mathf.Clamp(Mathf.CeilToInt(isl.UvBox.width * sw), 1, sw - x);
            int h = Mathf.Clamp(Mathf.CeilToInt(isl.UvBox.height * sh), 1, sh - y);
            return new RectInt(x, y, w, h);
        }

        private Color[] GetPixels(Texture2D t, TextureUsage usage)
        {
            if (t == null) return null;
            int id = t.GetInstanceID();
            if (_pixelCache.TryGetValue(id, out var px)) return px;
            px = TextureIO.ReadPixels(t, linear: !usage.SRGB);
            _pixelCache[id] = px;
            return px;
        }

        // ---- Resampling (premultiplied alpha for transparent) ----
        // 重采样（透明贴图预乘 alpha）
        private static Color[] DownsampleBox(Color[] src, int sw, int sh, RectInt box, int tw, int th, TextureUsage usage)
        {
            var dst = new Color[tw * th];
            bool premul = usage.HasAlphaChannel && usage.Alpha == TexAlphaMode.Blend;
            for (int y = 0; y < th; y++)
                for (int x = 0; x < tw; x++)
                {
                    float sx0 = box.x + (x * box.width) / (float)tw;
                    float sx1 = box.x + ((x + 1) * box.width) / (float)tw;
                    float sy0 = box.y + (y * box.height) / (float)th;
                    float sy1 = box.y + ((y + 1) * box.height) / (float)th;
                    int x0 = Mathf.Clamp(Mathf.FloorToInt(sx0), box.x, box.xMax - 1);
                    int x1 = Mathf.Clamp(Mathf.CeilToInt(sx1), x0 + 1, box.xMax);
                    int y0 = Mathf.Clamp(Mathf.FloorToInt(sy0), box.y, box.yMax - 1);
                    int y1 = Mathf.Clamp(Mathf.CeilToInt(sy1), y0 + 1, box.yMax);
                    float ar = 0, ag = 0, ab = 0, aa = 0, cnt = 0;
                    for (int yy = y0; yy < y1; yy++)
                        for (int xx = x0; xx < x1; xx++)
                        {
                            var c = src[yy * sw + xx];
                            float a = premul ? c.a : 1f;
                            ar += c.r * a; ag += c.g * a; ab += c.b * a; aa += c.a; cnt++;
                        }
                    if (cnt <= 0) continue;
                    float inv = 1f / cnt;
                    float outA = aa * inv;
                    float invA = outA > 1e-5f ? 1f / outA : 0f;
                    dst[y * tw + x] = premul
                        ? new Color(ar * inv * invA, ag * inv * invA, ab * inv * invA, outA)
                        : new Color(ar * inv, ag * inv, ab * inv, outA);
                }
            return dst;
        }

        private static Color[] UpsampleBilinear(Color[] small, int sw, int sh, int W, int H)
        {
            var dst = new Color[W * H];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    float fx = (x + 0.5f) * sw / W - 0.5f;
                    float fy = (y + 0.5f) * sh / H - 0.5f;
                    int x0 = Mathf.Clamp(Mathf.FloorToInt(fx), 0, sw - 1);
                    int y0 = Mathf.Clamp(Mathf.FloorToInt(fy), 0, sh - 1);
                    int x1 = Mathf.Min(x0 + 1, sw - 1);
                    int y1 = Mathf.Min(y0 + 1, sh - 1);
                    float tx = Mathf.Clamp01(fx - x0), ty = Mathf.Clamp01(fy - y0);
                    var a = small[y0 * sw + x0], b = small[y0 * sw + x1];
                    var c = small[y1 * sw + x0], d = small[y1 * sw + x1];
                    dst[y * W + x] = Color.Lerp(Color.Lerp(a, b, tx), Color.Lerp(c, d, tx), ty);
                }
            return dst;
        }

        // ---- Metrics / 指标 ----
        private static float SsimSingle(Color[] a, int sw, int sh, RectInt box, Color[] b)
        {
            double muA = 0, muB = 0; int n = box.width * box.height;
            for (int y = box.yMin; y < box.yMax; y++)
                for (int x = box.xMin; x < box.xMax; x++)
                {
                    var ca = a[y * sw + x];
                    float la = ColorMath.GammaToLinear((ca.r + ca.g + ca.b) / 3f);
                    float lb = ColorMath.GammaToLinear((b[(y - box.yMin) * box.width + (x - box.xMin)].r +
                                                        b[(y - box.yMin) * box.width + (x - box.xMin)].g +
                                                        b[(y - box.yMin) * box.width + (x - box.xMin)].b) / 3f);
                    muA += la; muB += lb;
                }
            muA /= n; muB /= n;
            double va = 0, vb = 0, cov = 0;
            for (int y = box.yMin; y < box.yMax; y++)
                for (int x = box.xMin; x < box.xMax; x++)
                {
                    var ca = a[y * sw + x];
                    float la = ColorMath.GammaToLinear((ca.r + ca.g + ca.b) / 3f);
                    float lb = ColorMath.GammaToLinear((b[(y - box.yMin) * box.width + (x - box.xMin)].r +
                                                        b[(y - box.yMin) * box.width + (x - box.xMin)].g +
                                                        b[(y - box.yMin) * box.width + (x - box.xMin)].b) / 3f);
                    double da = la - muA, db = lb - muB;
                    va += da * da; vb += db * db; cov += da * db;
                }
            return ColorMath.Ssim((float)muA, (float)muB, (float)(va / n), (float)(vb / n), (float)(cov / n));
        }

        private static float MsSsim(Color[] a, int sw, int sh, RectInt box, Color[] b)
        {
            // Multi-scale: average SSIM over 3 downscaled levels (simplified MS-SSIM).
            // 简化版 MS-SSIM：3 个尺度的 SSIM 平均
            float acc = 0; int levels = 0;
            var ca = a; var cb = b; int cw = box.width, ch = box.height;
            int casw = sw, cash = sh; RectInt cbox = box;
            for (int l = 0; l < 3 && cw >= 8 && ch >= 8; l++)
            {
                acc += SsimSingle(ca, casw, cash, cbox, cb); levels++;
                // halve / 半尺寸
                var nb = new Color[(cw / 2) * (ch / 2)];
                for (int y = 0; y < ch / 2; y++)
                    for (int x = 0; x < cw / 2; x++)
                    {
                        int sy = cbox.yMin + y * 2, sx = cbox.xMin + x * 2;
                        nb[y * (cw / 2) + x] = (ca[sy * casw + sx] + ca[(sy + 1) * casw + sx] +
                                                ca[sy * casw + sx + 1] + ca[(sy + 1) * casw + sx + 1]) * 0.25f;
                    }
                ca = nb; casw = cw / 2; cash = ch / 2; cw /= 2; ch /= 2;
                cbox = new RectInt(0, 0, cw, ch);
                cb = UpsampleBilinear(cb, box.width, box.height, cw, ch);
            }
            return levels > 0 ? acc / levels : SsimSingle(a, sw, sh, box, b);
        }

        private static float MeanDeltaE(Color[] a, int sw, int sh, RectInt box, Color[] b)
        {
            double sum = 0; int n = 0;
            for (int y = box.yMin; y < box.yMax; y++)
                for (int x = box.xMin; x < box.xMax; x++)
                {
                    var ca = a[y * sw + x];
                    var cb = b[(y - box.yMin) * box.width + (x - box.xMin)];
                    sum += ColorMath.DeltaE2000(ca, cb); n++;
                }
            return n > 0 ? (float)(sum / n) : 0f;
        }

        private static float CutoutIou(Color[] a, int sw, int sh, RectInt box, Color[] b, float cutoff)
        {
            double inter = 0, uni = 0;
            for (int y = box.yMin; y < box.yMax; y++)
                for (int x = box.xMin; x < box.xMax; x++)
                {
                    bool ma = a[y * sw + x].a >= cutoff;
                    bool mb = b[(y - box.yMin) * box.width + (x - box.xMin)].a >= cutoff;
                    if (ma && mb) inter++;
                    if (ma || mb) uni++;
                }
            return uni > 0 ? (float)(inter / uni) : 1f;
        }

        private static float AlphaRmse(Color[] a, int sw, int sh, RectInt box, Color[] b)
        {
            double s = 0; int n = 0;
            for (int y = box.yMin; y < box.yMax; y++)
                for (int x = box.xMin; x < box.xMax; x++)
                {
                    float d = a[y * sw + x].a - b[(y - box.yMin) * box.width + (x - box.xMin)].a;
                    s += d * d; n++;
                }
            return n > 0 ? Mathf.Sqrt((float)(s / n)) : 0f;
        }

        private static float DataRmse(Color[] a, int sw, int sh, RectInt box, Color[] b, int channelMask)
        {
            double worst = 0;
            for (int ch = 0; ch < 4; ch++)
            {
                if ((channelMask & (1 << ch)) == 0) continue;
                double s = 0; int n = 0;
                for (int y = box.yMin; y < box.yMax; y++)
                    for (int x = box.xMin; x < box.xMax; x++)
                    {
                        float va = Ch(a[y * sw + x], ch);
                        float vb = Ch(b[(y - box.yMin) * box.width + (x - box.xMin)], ch);
                        float d = va - vb; s += d * d; n++;
                    }
                    if (n > 0) worst = Mathf.Max(worst, s / n);
            }
            return Mathf.Sqrt((float)worst);
        }
        private static float Ch(Color c, int ch) => ch switch { 0 => c.r, 1 => c.g, 2 => c.b, _ => c.a };

        private static void NormalError(Color[] a, int sw, int sh, RectInt box, Color[] b, out float mean, out float p95)
        {
            var errs = new List<float>(box.width * box.height);
            for (int y = box.yMin; y < box.yMax; y++)
                for (int x = box.xMin; x < box.xMax; x++)
                {
                    var va = ColorMath.DecodeNormal(a[y * sw + x]);
                    var vb = ColorMath.DecodeNormal(b[(y - box.yMin) * box.width + (x - box.xMin)]);
                    errs.Add(ColorMath.AngleDeg(va, vb));
                }
            errs.Sort();
            mean = 0; for (int i = 0; i < errs.Count; i++) mean += errs[i];
            mean /= Mathf.Max(1, errs.Count);
            p95 = errs.Count > 0 ? errs[Mathf.Min(errs.Count - 1, (int)(errs.Count * 0.95f))] : 0;
        }
    }
}
