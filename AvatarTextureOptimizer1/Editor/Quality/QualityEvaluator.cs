// QualityEvaluator.cs / QualityEvaluator.cs
// Full quality evaluation: downsamples island pixels to a candidate size using bilinear (linear space,
// premultiplied alpha for transparent), upsamples bilinearly back to original pixel dimensions, then
// compares against source using the set of metrics applicable to each texture type. Returns true
// if every metric is within the target thresholds.
// 完整质量评估：使用双线性在线性空间把岛像素下采样到候选尺寸（透明贴图预乘alpha），再双线性上采样回原像素尺寸，
// 然后用每个贴图类型适用的指标与源比较。若所有指标都在目标阈值内返回true。
//
// Implemented on CPU for robustness (GPU batches can be layered later without changing the interface).
// 为稳定性在CPU上实现（GPU批量路径可在不改变接口的情况下后续分层加入）。

using System;
using UnityEngine;
using net.fosa.avatar_texture_optimizer.Editor.Groups;
using net.fosa.avatar_texture_optimizer.Editor.Util;

namespace net.fosa.avatar_texture_optimizer.Editor.Quality
{
    public static class QualityEvaluator
    {
        /// <summary>
        /// Evaluate whether an island (given by srcPixels in original pixel space, layout srcW x srcH, region at srcRect within it)
        /// passes quality metrics when resampled to (targetW, targetH).
        /// 评估一个岛（原像素空间中的srcPixels，布局srcW×srcH，其中区域srcRect）被重采样到(targetW,targetH)时是否通过质量指标。
        /// </summary>
        public static bool PassesQuality(Color[] srcPixels, int srcW, int srcH, RectInt srcRect,
            int targetW, int targetH, bool isNormal, bool isGrayscale, bool isAlpha, bool isCutout, float cutoff,
            bool premultiplyAlpha, QualityTarget target)
        {
            if (target.IsNearLossless) return true;
            if (targetW >= srcRect.width && targetH >= srcRect.height) return true; // upscaling never loses quality (won't happen by design)
            int shortSide = Mathf.Min(targetW, targetH);
            if (shortSide < 11) return true; // per spec: islands < 11px skip metric

            // Extract the source region and convert to linear
            // 提取源区域并转为线性
            int rw = srcRect.width, rh = srcRect.height;
            Color[] src = new Color[rw * rh];
            for (int y = 0; y < rh; y++)
                for (int x = 0; x < rw; x++)
                {
                    int sx = srcRect.x + x;
                    int sy = srcRect.y + y;
                    if (sx < 0 || sy < 0 || sx >= srcW || sy >= srcH) { src[y * rw + x] = new Color(0, 0, 0, 0); continue; }
                    src[y * rw + x] = ToLinear(srcPixels[sy * srcW + sx], premultiplyAlpha);
                }

            // Downsample to targetW x targetH using bilinear / 用双线性下采样到targetW×targetH
            Color[] scaled = BilinearResample(src, rw, rh, targetW, targetH);
            // Upsample back to rw x rh using bilinear / 用双线性上采样回rw×rh
            Color[] reconstructed = BilinearResample(scaled, targetW, targetH, rw, rh);

            // Undo premultiplication / 撤销预乘
            if (premultiplyAlpha)
            {
                for (int i = 0; i < reconstructed.Length; i++)
                {
                    float a = Mathf.Max(0.0001f, reconstructed[i].a);
                    reconstructed[i] = new Color(
                        Mathf.Clamp01(reconstructed[i].r / a),
                        Mathf.Clamp01(reconstructed[i].g / a),
                        Mathf.Clamp01(reconstructed[i].b / a),
                        reconstructed[i].a);
                }
            }

            // Compute metrics / 计算指标
            if (isNormal)
            {
                float p95 = QualityMetrics.P95NormalAngle(src, reconstructed, false);
                if (p95 > target.NormalAngleDeg) return false;
            }
            else if (isGrayscale)
            {
                float rmse = QualityMetrics.GrayscaleWorstRMSE(src, reconstructed);
                if (rmse > target.GrayscaleRMSE) return false;
            }
            else
            {
                // Color + alpha / 颜色+alpha
                float ssim = shortSide < 176
                    ? QualityMetrics.SingleScaleSSIM(src, reconstructed)
                    : QualityMetrics.MSSSIM(src, reconstructed, rw, rh);
                if (ssim < target.MsSSIM) return false;
                float de = QualityMetrics.AvgDeltaE(src, reconstructed);
                if (de > target.DeltaE) return false;
                if (isAlpha)
                {
                    if (isCutout)
                    {
                        float iou = QualityMetrics.CutoutIoU(src, reconstructed, cutoff);
                        if (iou < target.CutoutIoU) return false;
                    }
                    else
                    {
                        float armse = QualityMetrics.AlphaRMSE(src, reconstructed);
                        if (armse > target.AlphaRMSE) return false;
                    }
                }
            }
            return true;
        }

        /// <summary>
        /// Detect if a region is solid color (all pixels equal within a tiny tolerance).
        /// 检测一个区域是否纯色（所有像素在极小容差内相等）。
        /// </summary>
        public static bool IsSolidColor(Color[] srcPixels, int srcW, int srcH, RectInt srcRect)
        {
            int rw = srcRect.width, rh = srcRect.height;
            if (rw <= 1 || rh <= 1) return true;
            Color refc = srcPixels[(srcRect.y) * srcW + srcRect.x];
            float rtol = 1f / 255f;
            for (int y = 0; y < rh; y++)
                for (int x = 0; x < rw; x++)
                {
                    int sx = srcRect.x + x, sy = srcRect.y + y;
                    if (sx < 0 || sy < 0 || sx >= srcW || sy >= srcH) return false;
                    var c = srcPixels[sy * srcW + sx];
                    if (Mathf.Abs(c.r - refc.r) > rtol || Mathf.Abs(c.g - refc.g) > rtol
                        || Mathf.Abs(c.b - refc.b) > rtol || Mathf.Abs(c.a - refc.a) > rtol) return false;
                }
            return true;
        }

        private static Color ToLinear(Color c, bool premultiply)
        {
            float r = MathUtility.SRGBToLinear(Mathf.Clamp01(c.r));
            float g = MathUtility.SRGBToLinear(Mathf.Clamp01(c.g));
            float b = MathUtility.SRGBToLinear(Mathf.Clamp01(c.b));
            float a = Mathf.Clamp01(c.a);
            if (premultiply) { r *= a; g *= a; b *= a; }
            return new Color(r, g, b, a);
        }

        /// <summary>
        /// Bilinear resample from src to dst of given sizes.
        /// 从src按给定尺寸双线性重采样到dst。
        /// </summary>
        private static Color[] BilinearResample(Color[] src, int sw, int sh, int dw, int dh)
        {
            Color[] dst = new Color[dw * dh];
            if (sw <= 0 || sh <= 0 || dw <= 0 || dh <= 0) return dst;
            float xFactor = (float)sw / dw;
            float yFactor = (float)sh / dh;
            for (int y = 0; y < dh; y++)
            {
                float sy = (y + 0.5f) * yFactor - 0.5f;
                int sy0 = Mathf.FloorToInt(sy);
                int sy1 = Mathf.Min(sh - 1, sy0 + 1);
                sy0 = Mathf.Clamp(sy0, 0, sh - 1);
                float fy = sy - sy0;
                for (int x = 0; x < dw; x++)
                {
                    float sx = (x + 0.5f) * xFactor - 0.5f;
                    int sx0 = Mathf.FloorToInt(sx);
                    int sx1 = Mathf.Min(sw - 1, sx0 + 1);
                    sx0 = Mathf.Clamp(sx0, 0, sw - 1);
                    float fx = sx - sx0;
                    Color c00 = src[sy0 * sw + sx0];
                    Color c10 = src[sy0 * sw + sx1];
                    Color c01 = src[sy1 * sw + sx0];
                    Color c11 = src[sy1 * sw + sx1];
                    Color c0 = Color.Lerp(c00, c10, fx);
                    Color c1 = Color.Lerp(c01, c11, fx);
                    dst[y * dw + x] = Color.Lerp(c0, c1, fy);
                }
            }
            return dst;
        }
    }
}
