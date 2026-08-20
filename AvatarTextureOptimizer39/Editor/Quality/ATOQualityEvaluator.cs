// Avatar Texture Optimizer (ATO)
// Bilingual comments: English + Simplified Chinese. 双语注释：英文 + 简体中文。

using System;
using System.Collections.Generic;
using AvatarTextureOptimizer.Editor.Core;
using UnityEngine;

namespace AvatarTextureOptimizer.Editor.Quality
{
    /// <summary>
    /// Describes how a texture is used by one referencing material.
    /// 描述一个引用材质对贴图的使用方式。
    /// </summary>
    public struct ATOTextureUsage
    {
        public bool opaque;      // no alpha used. 不使用 alpha。
        public bool cutout;      // alpha test. alpha 测试（Cutout）。
        public float cutoff;     // cutoff threshold [0..1]. 阈值。
        public bool blend;       // alpha blend. alpha 混合。
        public bool isNormal;    // normal map. 法线贴图。
        public bool isGrayscale; // only some channels used. 仅使用部分通道。
        public int grayChannels; // bitmask of used channels (1=R,2=G,4=B,8=A). 使用通道位掩码。
    }

    /// <summary>
    /// Result of evaluating one island scale candidate.
    /// 评估一次岛缩放候选的结果。
    /// </summary>
    public struct ATOQualityResult
    {
        public bool Passed;
        public float WorstScore;      // normalized score (>=1 pass). 归一化得分（≥1 通过）。
        public string FailedMetric;   // which metric failed ("" if passed). 未通过的指标名。

        public float MsSsim;
        public float DeltaE;
        public float AlphaMetric;
        public float NormalAngle;
        public float GrayRmse;
    }

    /// <summary>
    /// Top-level quality evaluator implementing the target-quality algorithm:
    ///  - linear-space resampling, premultiplied-alpha downsample for transparent textures;
    ///  - MS-SSIM (fallback to SSIM below 176px short edge, ignored below 11px) + ΔE2000;
    ///  - alpha: cutout clip-IoU / blend linear RMSE (evaluate every referencing usage, keep strictest);
    ///  - normal maps: angular error + p95 after decode/resample/renormalize/encode;
    ///  - grayscale: per-used-channel linear RMSE, worst channel.
    /// All comparisons upsample the shrunk island bilinearly back to original size.
    ///
    /// 顶层质量评估器，实现目标质量算法（详见类注释英文）。
    /// </summary>
    public static class ATOQualityEvaluator
    {
        /// <summary>
        /// Evaluate a scale candidate for an island.
        /// 评估一个岛的缩放候选。
        /// </summary>
        /// <param name="original">Island pixels cropped from the source texture (linear space). 源贴图裁剪出的岛像素（线性空间）。</param>
        /// <param name="origW/origH">Island size in texels. 岛的 texel 尺寸。</param>
        /// <param name="scaledW/scaledH">Candidate scaled size. 候选缩放尺寸。</param>
        /// <param name="usages">All referencing usages (strictest wins). 所有引用用法（取最严）。</param>
        public static ATOQualityResult Evaluate(
            ATOQualityThresholds t,
            Color[] original, int origW, int origH,
            int scaledW, int scaledH,
            List<ATOTextureUsage> usages)
        {
            var result = new ATOQualityResult { Passed = true, WorstScore = float.MaxValue };

            if (scaledW == origW && scaledH == origH)
            {
                // No scaling → trivially passes. 未缩放 → 直接通过。
                result.MsSsim = 1f; result.DeltaE = 0f;
                result.AlphaMetric = 0f; result.NormalAngle = 0f; result.GrayRmse = 0f;
                return result;
            }

            bool hasNormal = false, hasGrayscale = false, hasAlpha = false, opaqueOnly = true;
            bool anyCutout = false, anyBlend = false;
            float maxCutoff = 0f;
            int grayChannels = 0;

            foreach (var u in usages)
            {
                hasNormal |= u.isNormal;
                hasGrayscale |= u.isGrayscale;
                if (!u.opaque) { hasAlpha = true; opaqueOnly = false; }
                anyCutout |= u.cutout;
                anyBlend |= u.blend;
                if (u.cutoff > maxCutoff) maxCutoff = u.cutoff;
                grayChannels |= u.grayChannels;
            }

            bool premultiply = hasAlpha && !hasNormal && !hasGrayscale;

            // Downsample then upsample back (Burst-accelerated when available). 先下采样再上采样回原尺寸。
            var scaled = ATOCompute.Downsample(original, origW, origH, scaledW, scaledH, premultiply);
            if (hasNormal) RenormalizeNormals(scaled);
            var restored = ATOCompute.Upsample(scaled, scaledW, scaledH, origW, origH, premultiply);

            int n = origW * origH;
            int shortEdge = Mathf.Min(origW, origH);

            // 1) MS-SSIM (or SSIM fallback / skip). 结构相似度。
            bool skipSsim = shortEdge < t.ssIgnoreBelowPx;
            bool singleScale = shortEdge < t.ssFallbackBelowPx;

            if (!skipSsim)
            {
                result.MsSsim = singleScale
                    ? ATOSsim.SsimRgb(original, restored, origW, origH)
                    : ATOSsim.MsSsimRgb(original, restored, origW, origH);
                Check(ref result, result.MsSsim / t.msSsim, "MS-SSIM");
            }
            else
            {
                result.MsSsim = 1f;
            }

            // 2) ΔE2000 (mean). 色差。
            if (!hasNormal && !hasGrayscale)
            {
                double sum = 0;
                for (int i = 0; i < n; i++)
                {
                    var a = original[i]; var b = restored[i];
                    sum += ATOCieLab.DeltaE2000Rgb(a.r, a.g, a.b, b.r, b.g, b.b);
                }
                result.DeltaE = (float)(sum / n);
                Check(ref result, t.deltaE / result.DeltaE, "ΔE");
            }

            // 3) Alpha (cutout IoU / blend RMSE). alpha 指标。
            if (hasAlpha)
            {
                float worstAlphaScore = float.MaxValue;
                if (anyCutout)
                {
                    float iou = AlphaIoU(original, restored, n, maxCutoff);
                    result.AlphaMetric = iou;
                    worstAlphaScore = Mathf.Min(worstAlphaScore, iou / t.alphaIoU);
                }
                if (anyBlend)
                {
                    float rmse = AlphaRmse(original, restored, n);
                    result.AlphaMetric = Mathf.Min(result.AlphaMetric, rmse);
                    float score = t.alphaRmse <= 0f ? 1f : t.alphaRmse / rmse;
                    worstAlphaScore = Mathf.Min(worstAlphaScore, score);
                }
                Check(ref result, worstAlphaScore, "Alpha");
            }

            // 4) Normal map angular error + p95. 法线角度误差。
            if (hasNormal)
            {
                result.NormalAngle = NormalAngularP95(original, restored, n);
                float score = t.normalAngleDegrees <= 0f ? 1f : t.normalAngleDegrees / result.NormalAngle;
                Check(ref result, score, "Normal");
            }

            // 5) Grayscale per-channel RMSE, worst channel. 灰度逐通道 RMSE。
            if (hasGrayscale)
            {
                result.GrayRmse = GrayWorstRmse(original, restored, n, grayChannels);
                float score = t.grayRmse <= 0f ? 1f : t.grayRmse / result.GrayRmse;
                Check(ref result, score, "Gray");
            }

            return result;
        }

        private static void Check(ref ATOQualityResult r, float score, string metric)
        {
            if (score < r.WorstScore)
            {
                r.WorstScore = score;
                if (score < 1f)
                {
                    r.Passed = false;
                    r.FailedMetric = metric;
                }
            }
        }

        /// <summary>Renormalize normal-map pixels in place (decode → normalize → encode). 原位重归一化法线像素。</summary>
        private static void RenormalizeNormals(Color[] px)
        {
            for (int i = 0; i < px.Length; i++)
            {
                var v = new Vector3(px[i].r * 2f - 1f, px[i].g * 2f - 1f, px[i].b * 2f - 1f);
                if (v.sqrMagnitude < 1e-6f) v = Vector3.forward;
                else v.Normalize();
                px[i] = new Color(v.x * 0.5f + 0.5f, v.y * 0.5f + 0.5f, v.z * 0.5f + 0.5f, px[i].a);
            }
        }

        private static float AlphaIoU(Color[] a, Color[] b, int n, float cutoff)
        {
            int inter = 0, union = 0;
            for (int i = 0; i < n; i++)
            {
                bool aa = a[i].a >= cutoff;
                bool bb = b[i].a >= cutoff;
                if (aa && bb) inter++;
                if (aa || bb) union++;
            }
            return union == 0 ? 1f : (float)inter / union;
        }

        private static float AlphaRmse(Color[] a, Color[] b, int n)
        {
            double sum = 0;
            for (int i = 0; i < n; i++)
            {
                float d = a[i].a - b[i].a;
                sum += d * d;
            }
            return (float)Math.Sqrt(sum / n);
        }

        private static float NormalAngularP95(Color[] a, Color[] b, int n)
        {
            var angles = new float[n];
            for (int i = 0; i < n; i++)
            {
                var na = DecodeNormal(a[i]);
                var nb = DecodeNormal(b[i]);
                float dot = Mathf.Clamp(Vector3.Dot(na, nb), -1f, 1f);
                angles[i] = Mathf.Acos(dot) * Mathf.Rad2Deg;
            }
            System.Array.Sort(angles);
            return angles[Mathf.Min(n - 1, (int)(n * 0.95f))];
        }

        private static Vector3 DecodeNormal(Color c)
        {
            var v = new Vector3(c.r * 2f - 1f, c.g * 2f - 1f, c.b * 2f - 1f);
            return v.sqrMagnitude < 1e-6f ? Vector3.forward : v.normalized;
        }

        private static float GrayWorstRmse(Color[] a, Color[] b, int n, int channels)
        {
            float worst = 0f;
            for (int ch = 0; ch < 4; ch++)
            {
                if ((channels & (1 << ch)) == 0) continue;
                double sum = 0;
                for (int i = 0; i < n; i++)
                {
                    float va = ch == 0 ? a[i].r : ch == 1 ? a[i].g : ch == 2 ? a[i].b : a[i].a;
                    float vb = ch == 0 ? b[i].r : ch == 1 ? b[i].g : ch == 2 ? b[i].b : b[i].a;
                    float d = va - vb;
                    sum += d * d;
                }
                float rmse = (float)Math.Sqrt(sum / n);
                if (rmse > worst) worst = rmse;
            }
            return worst;
        }
    }
}
