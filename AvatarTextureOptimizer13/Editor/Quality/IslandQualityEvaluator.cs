// ATO — Avatar Texture Optimizer
// Island-level quality evaluation: resamples an island's source region to the candidate
// size (area-average in linear, premultiplied-alpha space), upsamples it back with bilinear
// filtering, and compares it against the original region with the kind-appropriate metrics.
// 岛级质量评估：把岛的源区域重采样到候选尺寸（线性空间、预乘 alpha 的面积平均），
// 再双线性上采样回原尺寸，与源区域用对应类别的指标比较。
//
// Metric selection (CLAUDE.md #34):
//  - Color/Emission/Other: MS-SSIM (SSIM fallback <176px; ignored <11px) + Delta-E2000 + alpha (IoU/RMSE).
//  - NormalMap: decoded-angle error (mean + p95).
//  - Mask/Grayscale: per-channel linear RMSE, worst channel.

using System;
using UnityEngine;
using net.fosa.ato;

namespace net.fosa.ato.editor
{
    /// <summary>
    /// Per-metric evaluation result. 单项指标评估结果。
    /// </summary>
    public struct ATOMetric
    {
        public string name;
        public float value;
        public float threshold;
        public bool higherIsBetter;
        public bool passed;
        public bool skipped;
    }

    /// <summary>
    /// Full island evaluation result. 岛的完整评估结果。
    /// </summary>
    public class ATOIslandEval
    {
        public readonly System.Collections.Generic.List<ATOMetric> Metrics = new System.Collections.Generic.List<ATOMetric>();
        public bool Passed { get; set; }
        public float ScaleX = 1f, ScaleY = 1f;

        /// <summary>
        /// Add a metric. <paramref name="higherIsBetter"/> true for similarity/IoU metrics (MS-SSIM, IoU),
        /// false for error metrics (ΔE, RMSE, angle). 添加指标；相似度/IoU 类指标（MS-SSIM、IoU）为 true，
        /// 误差类指标（ΔE、RMSE、角度）为 false。
        /// </summary>
        public void Add(string name, float value, float threshold, bool higherIsBetter, bool skipped)
        {
            bool passed = skipped || (higherIsBetter ? value >= threshold : value <= threshold);
            Metrics.Add(new ATOMetric { name = name, value = value, threshold = threshold, higherIsBetter = higherIsBetter, passed = passed, skipped = skipped });
        }
    }

    /// <summary>
    /// Evaluates the quality of a scaled island. 评估缩放后岛的质量。
    /// </summary>
    public static class IslandQualityEvaluator
    {
        private const float SsimBboxShortSideFallback = 176f; // below this, single-scale SSIM
        private const float SsimBboxShortSideIgnore = 11f;    // below this, ignore SSIM

        /// <summary>
        /// Evaluate an island at a given scale. Returns per-metric results and overall pass.
        /// 在给定缩放下评估岛，返回各指标结果与总体是否通过。
        /// </summary>
        public static ATOIslandEval Evaluate(
            Color[] sourcePixels, int srcW, int srcH,
            int regionX, int regionY, int regionW, int regionH,
            float scaleX, float scaleY,
            ATOTextureKind kind, ATOAlphaMode alphaMode, float cutoff,
            ATOQualityParameters qp)
        {
            var eval = new ATOIslandEval { ScaleX = scaleX, ScaleY = scaleY };

            // Clamp region to source. 将区域钳制到源内。
            regionX = Mathf.Clamp(regionX, 0, srcW - 1);
            regionY = Mathf.Clamp(regionY, 0, srcH - 1);
            regionW = Mathf.Clamp(regionW, 1, srcW - regionX);
            regionH = Mathf.Clamp(regionH, 1, srcH - regionY);

            var original = QualityMath.ExtractRegion(sourcePixels, srcW, srcH, regionX, regionY, regionW, regionH);

            int scaledW = Mathf.Max(1, Mathf.RoundToInt(regionW * scaleX));
            int scaledH = Mathf.Max(1, Mathf.RoundToInt(regionH * scaleY));

            if (kind == ATOTextureKind.NormalMap)
            {
                EvaluateNormal(eval, original, regionW, regionH, scaledW, scaledH, qp);
            }
            else
            {
                var resampled = BurstMetrics.AreaResample(original, regionW, regionH, scaledW, scaledH);
                var upsampled = BurstMetrics.BilinearUpsample(resampled, scaledW, scaledH, regionW, regionH);
                EvaluateColor(eval, original, upsampled, regionW, regionH, kind, alphaMode, cutoff, qp);
            }

            bool passed = true;
            foreach (var m in eval.Metrics) if (!m.passed) passed = false;
            eval.Passed = passed;
            return eval;
        }

        private static void EvaluateColor(ATOIslandEval eval, Color[] a, Color[] b, int w, int h,
            ATOTextureKind kind, ATOAlphaMode alphaMode, float cutoff, ATOQualityParameters qp)
        {
            int n = a.Length;
            var la = new float[n];
            var lb = new float[n];

            for (int i = 0; i < n; i++)
            {
                // Luma from premultiplied linear RGB. 由预乘线性 RGB 计算亮度。
                la[i] = 0.2126729f * a[i].r + 0.7151522f * a[i].g + 0.0721750f * a[i].b;
                lb[i] = 0.2126729f * b[i].r + 0.7151522f * b[i].g + 0.0721750f * b[i].b;
            }

            // MS-SSIM / SSIM with the short-side rules. 按短边规则选择 MS-SSIM / SSIM。
            float shortSide = Mathf.Min(w, h);
            if (shortSide >= SsimBboxShortSideIgnore)
            {
                float ssim = shortSide < SsimBboxShortSideFallback
                    ? BurstMetrics.SSIM(la, lb, w, h)
                    : BurstMetrics.MSSSIM(la, lb, w, h);
                eval.Add("MS-SSIM", ssim, qp.msSsim, higherIsBetter: true, skipped: false);
            }
            else
            {
                eval.Add("MS-SSIM", 1f, qp.msSsim, higherIsBetter: true, skipped: true); // ignored 忽略
            }

            // Delta-E 2000 (mean). ΔE2000（均值）。
            {
                double sum = 0; int cnt = 0;
                for (int i = 0; i < n; i++)
                {
                    QualityMath.LinearRGBToLab(a[i].r, a[i].g, a[i].b, out float L1, out float a1, out float b1);
                    QualityMath.LinearRGBToLab(b[i].r, b[i].g, b[i].b, out float L2, out float a2, out float b2);
                    sum += QualityMath.DeltaE2000(L1, a1, b1, L2, a2, b2);
                    cnt++;
                }
                eval.Add("DeltaE", (float)(sum / Math.Max(1, cnt)), qp.deltaE, higherIsBetter: false, skipped: false);
            }

            // Alpha metric. Alpha 指标。
            if (alphaMode != ATOAlphaMode.Opaque)
            {
                var aa = new float[n];
                var ab = new float[n];
                for (int i = 0; i < n; i++) { aa[i] = a[i].a; ab[i] = b[i].a; }
                if (alphaMode == ATOAlphaMode.Cutout)
                    eval.Add("AlphaIoU", BurstMetrics.AlphaIoU(aa, ab, cutoff), qp.alphaIou, higherIsBetter: true, skipped: false);
                else
                    eval.Add("AlphaRMSE", BurstMetrics.AlphaRMSE(aa, ab), qp.alphaRmse, higherIsBetter: false, skipped: false);
            }

            // Mask / grayscale: per-channel RMSE, worst. 蒙版/灰度：逐通道 RMSE 取最差。
            if (kind == ATOTextureKind.Mask || kind == ATOTextureKind.Grayscale)
            {
                float worst = 0f;
                for (int ch = 0; ch < 4; ch++)
                {
                    var ca = new float[n];
                    var cb = new float[n];
                    for (int i = 0; i < n; i++)
                    {
                        ca[i] = ch == 0 ? a[i].r : ch == 1 ? a[i].g : ch == 2 ? a[i].b : a[i].a;
                        cb[i] = ch == 0 ? b[i].r : ch == 1 ? b[i].g : ch == 2 ? b[i].b : b[i].a;
                    }
                    worst = Mathf.Max(worst, BurstMetrics.AlphaRMSE(ca, cb));
                }
                eval.Add("GrayRMSE", worst, qp.grayRmse, higherIsBetter: false, skipped: false);
            }
        }

        private static void EvaluateNormal(ATOIslandEval eval, Color[] a, int w, int h, int scaledW, int scaledH, ATOQualityParameters qp)
        {
            // Decode normals, resample in decoded space, renormalize, compare angles.
            // 解码法线，在解码空间重采样，重归一化，比较角度。
            int n = a.Length;
            var va = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                va[i] = ATOTextureIO.DecodeNormal(new Color32(
                    (byte)Mathf.RoundToInt(a[i].r * 255f),
                    (byte)Mathf.RoundToInt(a[i].g * 255f),
                    (byte)Mathf.RoundToInt(a[i].b * 255f),
                    (byte)Mathf.RoundToInt(a[i].a * 255f)));
            }

            var resampled = ResampleNormals(va, w, h, scaledW, scaledH);
            var upsampled = BurstMetrics.BilinearUpsample(
                ToColorArray(resampled, scaledW, scaledH), scaledW, scaledH, w, h);
            var vb = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                vb[i] = ATOTextureIO.DecodeNormal(new Color32(
                    (byte)Mathf.RoundToInt(upsampled[i].r * 255f),
                    (byte)Mathf.RoundToInt(upsampled[i].g * 255f),
                    (byte)Mathf.RoundToInt(upsampled[i].b * 255f),
                    (byte)Mathf.RoundToInt(upsampled[i].a * 255f)));
            }

            eval.Add("NormalAngle", BurstMetrics.MeanAngleErrorDeg(va, vb), qp.normalAngleDeg, higherIsBetter: false, skipped: false);
            eval.Add("NormalAngleP95", BurstMetrics.P95AngleErrorDeg(va, vb), qp.normalAngleP95Deg, higherIsBetter: false, skipped: false);
        }

        private static Vector3[] ResampleNormals(Vector3[] src, int w, int h, int dstW, int dstH)
        {
            dstW = Mathf.Max(1, dstW); dstH = Mathf.Max(1, dstH);
            var dst = new Vector3[dstW * dstH];
            float sx = (float)w / dstW, sy = (float)h / dstH;
            for (int y = 0; y < dstH; y++)
            for (int x = 0; x < dstW; x++)
            {
                float x0 = x * sx, x1 = (x + 1) * sx;
                float y0 = y * sy, y1 = (y + 1) * sy;
                int ix0 = Mathf.FloorToInt(x0), ix1 = Mathf.Min(w, Mathf.CeilToInt(x1));
                int iy0 = Mathf.FloorToInt(y0), iy1 = Mathf.Min(h, Mathf.CeilToInt(y1));
                var sum = Vector3.zero; float wsum = 0;
                for (int iy = iy0; iy < iy1; iy++)
                for (int ix = ix0; ix < ix1; ix++)
                {
                    float ox = Mathf.Min(x1, ix + 1) - Mathf.Max(x0, ix);
                    float oy = Mathf.Min(y1, iy + 1) - Mathf.Max(y0, iy);
                    float wt = ox * oy;
                    sum += src[iy * w + ix] * wt; wsum += wt;
                }
                dst[y * dstW + x] = wsum > 1e-9f ? (sum / wsum).normalized : Vector3.up;
            }
            return dst;
        }

        private static Color[] ToColorArray(Vector3[] n, int w, int h)
        {
            var c = new Color[w * h];
            for (int i = 0; i < n.Length; i++)
            {
                var enc = ATOTextureIO.EncodeNormal(n[i]);
                c[i] = new Color(enc.r / 255f, enc.g / 255f, enc.b / 255f, enc.a / 255f);
            }
            return c;
        }

        /// <summary>True when a pixel region is a solid color (within epsilon). 像素区域是否为纯色（容差内）。</summary>
        public static bool IsSolidColor(Color[] region)
        {
            if (region.Length == 0) return true;
            var first = region[0];
            const float eps = 2f / 255f;
            foreach (var c in region)
            {
                if (Mathf.Abs(c.r - first.r) > eps || Mathf.Abs(c.g - first.g) > eps ||
                    Mathf.Abs(c.b - first.b) > eps || Mathf.Abs(c.a - first.a) > eps)
                    return false;
            }
            return true;
        }
    }
}
