// Avatar Texture Optimizer (ATO)
// Metric gate: MS-SSIM/SSIM + CIEDE2000 + alpha (IoU/RMSE) for color, angle error for
// normals, per-channel RMSE for grayscale. All metrics must pass; the worst one is reported.
// 指标门控：颜色用 MS-SSIM/SSIM + CIEDE2000 + alpha（IoU/RMSE），法线用角度误差，
// 灰度用逐通道 RMSE。全部指标达标才算通过，报告最差者。

using System.Collections.Generic;
using UnityEngine;

namespace NetFosa.ATO
{
    /// <summary>
    /// Result of evaluating a scaled island against its original. / 缩放岛对照原图的评估结果。
    /// </summary>
    public sealed class ATOQualityResult
    {
        public bool pass;
        public string limiting;
        public float value;
        public float threshold;
        public float margin; // >=0 means pass / >=0 表示达标
    }

    /// <summary>
    /// Quality evaluator. / 质量评估器。
    /// </summary>
    public static class ATOQualityEvaluator
    {
        /// <summary>
        /// Evaluate one texture's scaled island against the original.
        /// orig/scaled are equal-size straight-sRGB buffers over the island bbox; mask is coverage.
        /// orig/scaled 为等尺寸的直通 sRGB 缓冲（覆盖岛包围盒）；mask 为覆盖掩码。
        /// </summary>
        public static ATOQualityResult Evaluate(ATOTextureRef tr, Color[] orig, Color[] scaled, byte[] mask,
            int w, int h, int bboxShort, ATOQualityThresholds thr)
        {
            var result = new ATOQualityResult { pass = true, margin = float.MaxValue };
            var gates = new List<(string name, float value, float threshold, bool greaterIsBetter)>();

            if (tr.IsNormal)
            {
                EvaluateNormal(orig, scaled, mask, thr.angleDegMax, gates);
            }
            else if (tr.Category == ATOTextureCategory.Grayscale || tr.Category == ATOTextureCategory.Mask)
            {
                EvaluateGrayscale(tr, orig, scaled, mask, thr.grayRmseMax, gates);
            }
            else
            {
                EvaluateColor(tr, orig, scaled, mask, w, h, bboxShort, thr, gates);
            }

            foreach (var g in gates)
            {
                float margin = g.greaterIsBetter ? (g.value - g.threshold) : (g.threshold - g.value);
                if (margin < result.margin)
                {
                    result.margin = margin;
                    result.limiting = g.name;
                    result.value = g.value;
                    result.threshold = g.threshold;
                }
                if (margin < 0f) result.pass = false;
            }

            if (gates.Count == 0)
            {
                result.pass = true;
                result.limiting = "none";
                result.margin = 1f;
            }
            return result;
        }

        private static void EvaluateColor(ATOTextureRef tr, Color[] orig, Color[] scaled, byte[] mask,
            int w, int h, int bboxShort, ATOQualityThresholds thr,
            List<(string, float, float, bool)> gates)
        {
            int n = w * h;
            var oL = new float[n]; var sL = new float[n];
            var oR = new float[n]; var oG = new float[n]; var oB = new float[n]; var oA = new float[n];
            var sR = new float[n]; var sG = new float[n]; var sB = new float[n]; var sA = new float[n];

            for (int i = 0; i < n; i++)
            {
                var o = ATOUtil.SrgbToLinear(orig[i]);
                var s = ATOUtil.SrgbToLinear(scaled[i]);
                oL[i] = ATOColorMath.Luma(o.r, o.g, o.b);
                sL[i] = ATOColorMath.Luma(s.r, s.g, s.b);
                oR[i] = o.r; oG[i] = o.g; oB[i] = o.b; oA[i] = o.a;
                sR[i] = s.r; sG[i] = s.g; sB[i] = s.b; sA[i] = s.a;
            }

            // Structural metric. / 结构指标。
            if (bboxShort >= ATOConstants.SsimFallbackShortSide)
            {
                float ms = ATOColorMath.MsSsim(oL, sL, mask, w, h);
                gates.Add(("MS-SSIM", ms, thr.msSsimMin, true));
            }
            else if (bboxShort >= ATOConstants.MsSsimIgnoreShortSide)
            {
                float ss = ATOColorMath.Ssim(oL, sL, mask, w, h);
                gates.Add(("SSIM", ss, thr.msSsimMin, true));
            }
            // else: island too small, skip structural metric. / 岛过小，跳过结构指标。

            // CIEDE2000 (p95). / CIEDE2000（p95）。
            var de = new float[n];
            int cnt = 0;
            for (int i = 0; i < n; i++)
            {
                if (mask != null && mask[i] == 0) continue;
                de[cnt++] = ATOColorMath.Ciede2000(oR[i], oG[i], oB[i], sR[i], sG[i], sB[i]);
            }
            if (cnt > 0)
            {
                float p95 = ATOColorMath.Percentile95(de, cnt);
                gates.Add(("ΔE2000 p95", p95, thr.deltaEMax, false));
            }

            // Alpha: strictest across all usages. / Alpha：所有使用中取最严。
            bool anyCutout = false, anyBlend = false;
            float worstIoU = 1f, worstRmse = 0f;
            foreach (var u in tr.usages)
            {
                if (u.alphaMode == ATOAlphaMode.Cutout)
                {
                    anyCutout = true;
                    var mo = new byte[n]; var ms2 = new byte[n];
                    for (int i = 0; i < n; i++)
                    {
                        mo[i] = (byte)(oA[i] >= u.cutoff ? 1 : 0);
                        ms2[i] = (byte)(sA[i] >= u.cutoff ? 1 : 0);
                    }
                    worstIoU = Mathf.Min(worstIoU, ATOColorMath.IoU(mo, ms2, mask));
                }
                else if (u.alphaMode == ATOAlphaMode.Blend)
                {
                    anyBlend = true;
                    worstRmse = Mathf.Max(worstRmse, ATOColorMath.Rmse(oA, sA, mask));
                }
            }
            if (anyCutout) gates.Add(("α IoU", worstIoU, thr.alphaIoUMin, true));
            if (anyBlend) gates.Add(("α RMSE", worstRmse, thr.alphaRmseMax, false));
        }

        private static void EvaluateNormal(Color[] orig, Color[] scaled, byte[] mask, float angleDegMax,
            List<(string, float, float, bool)> gates)
        {
            int n = orig.Length;
            var angles = new float[n];
            int cnt = 0;
            for (int i = 0; i < n; i++)
            {
                if (mask != null && mask[i] == 0) continue;
                ATOColorMath.DecodeNormal(orig[i].r, orig[i].g, orig[i].b, out var x1, out var y1, out var z1);
                ATOColorMath.DecodeNormal(scaled[i].r, scaled[i].g, scaled[i].b, out var x2, out var y2, out var z2);
                angles[cnt++] = ATOColorMath.AngleDeg(x1, y1, z1, x2, y2, z2);
            }
            if (cnt > 0)
            {
                float p95 = ATOColorMath.Percentile95(angles, cnt);
                gates.Add(("normal angle p95", p95, angleDegMax, false));
            }
        }

        private static void EvaluateGrayscale(ATOTextureRef tr, Color[] orig, Color[] scaled, byte[] mask, float grayRmseMax,
            List<(string, float, float, bool)> gates)
        {
            int n = orig.Length;
            float worst = 0f;
            // Compare R, G, B in linear space; take the worst channel. / 线性空间比较 R/G/B，取最差通道。
            for (int ch = 0; ch < 3; ch++)
            {
                var a = new float[n]; var b = new float[n];
                for (int i = 0; i < n; i++)
                {
                    var o = tr.isSRGB ? ATOUtil.SrgbToLinear(orig[i]) : orig[i];
                    var s = tr.isSRGB ? ATOUtil.SrgbToLinear(scaled[i]) : scaled[i];
                    a[i] = o[ch]; b[i] = s[ch];
                }
                worst = Mathf.Max(worst, ATOColorMath.Rmse(a, b, mask));
            }
            gates.Add(("gray RMSE", worst, grayRmseMax, false));
        }
    }
}
