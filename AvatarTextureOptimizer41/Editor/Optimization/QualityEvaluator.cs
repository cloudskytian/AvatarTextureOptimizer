using System;
using System.Threading.Tasks;
using Net.Fosa.AvatarTextureOptimizer.Pure;
using UnityEngine;

// Quality evaluation: compares the scaled island (bilinearly upsampled back to the original size)
// against the original region using MS-SSIM/SSIM + CIEDE2000 + alpha (IoU/RMSE) + normal angle + gray RMSE.
// All thresholds must pass ("wooden barrel": worst metric decides).
// 质量评估：将缩小后的岛双线性上采样回原尺寸后与原图比较，使用 MS-SSIM/SSIM+CIEDE2000+alpha(IoU/RMSE)
// +法线角度+灰度 RMSE；所有阈值必须达标（木桶效应：最差指标决定）。

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    public sealed class QualityMetricsResult
    {
        public bool Pass = true;
        public double SSIM = 1.0, DeltaE = 0.0, AlphaRMSE = 0.0, CutoutIoU = 1.0, NormalAngle = 0.0, GrayRMSE = 0.0;
        public string FailReason = "";
    }

    public static class QualityEvaluator
    {
        /// <summary>
        /// MS-SSIM short-edge cutoff: below this, fall back to single-scale SSIM. 短边低于此值回退单尺度 SSIM。
        /// </summary>
        public const float MSSSIMMinShortEdge = 176f;

        /// <summary>
        /// Islands whose short edge is below this are skipped entirely (metrics ignored). 短边低于此值整体忽略指标。
        /// </summary>
        public const float IgnoreBelowShortEdge = 11f;

        /// <summary>
        /// Evaluates a candidate scaled result against the original.
        /// orig: original region RGBA (linear, w*h*4). scaledSmall: scaled region at (sw,sh) in the SAME
        /// UV rect (bilinear downsampled), then upsampled to (w,h) internally.
        /// 评估候选缩放结果：orig 为原区域 RGBA（线性）；scaledSmall 为同一 UV 矩形下 (sw,sh) 的缩小采样，
        /// 内部双线性放大回 (w,h)。
        /// </summary>
        public static QualityMetricsResult Evaluate(
            float[] orig, float[] scaledSmall, int scaledW, int scaledH, int w, int h,
            QualityTierSettings tier, TextureUse use)
        {
            var result = new QualityMetricsResult();
            float shortEdge = Mathf.Min(w, h);
            if (shortEdge < IgnoreBelowShortEdge)
            {
                result.Pass = true;
                return result; // too tiny: ignore metrics. 太小：忽略指标。
            }

            // Bilinear upsample to original size. 双线性放大回原尺寸。
            var up = new float[w * h * 4];
            UpsampleBilinear(scaledSmall, scaledW, scaledH, w, h, up);

            int n = w * h;
            var lumaA = new float[n];
            var lumaB = new float[n];
            var alphaA = new float[n];
            var alphaB = new float[n];
            var normalA = use.Kind == TextureKind.Normal ? new float[n * 3] : null;
            var normalB = use.Kind == TextureKind.Normal ? new float[n * 3] : null;

            // Parallel conversion loop. 并行转换循环。
            Parallel.For(0, n, i =>
            {
                int o = i * 4;
                float ar = orig[o], ag = orig[o + 1], ab = orig[o + 2], aa = orig[o + 3];
                float br = up[o], bg = up[o + 1], bb = up[o + 2], ba = up[o + 3];
                // Linear luminance (Rec.709). 线性亮度（Rec.709）。
                lumaA[i] = 0.2126f * ar + 0.7152f * ag + 0.0722f * ab;
                lumaB[i] = 0.2126f * br + 0.7152f * bg + 0.0722f * bb;
                alphaA[i] = aa; alphaB[i] = ba;
                if (normalA != null)
                {
                    DecodeNormal(ar, ag, ab, out float nxA, out float nyA, out float nzA);
                    DecodeNormal(br, bg, bb, out float nxB, out float nyB, out float nzB);
                    normalA[i * 3] = nxA; normalA[i * 3 + 1] = nyA; normalA[i * 3 + 2] = nzA;
                    normalB[i * 3] = nxB; normalB[i * 3 + 1] = nyB; normalB[i * 3 + 2] = nzB;
                }
            });

            // MS-SSIM or single-scale SSIM by short edge. 按短边选择 MS-SSIM 或单尺度 SSIM。
            result.SSIM = shortEdge >= MSSSIMMinShortEdge ? QualityMath.MSSSIM(lumaA, lumaB, w, h) : QualityMath.SSIM(lumaA, lumaB, w, h);
            if (result.SSIM < tier.minSSIM) { result.Pass = false; result.FailReason = $"SSIM {result.SSIM:F4} < {tier.minSSIM:F4}"; }

            // Mean CIEDE2000 (parallel). 平均 CIEDE2000（并行）。
            double dEsum = 0;
            object dElock = new object();
            Parallel.For(0, n, i =>
            {
                int o = i * 4;
                QualityMath.RgbToLab(orig[o], orig[o + 1], orig[o + 2], out float L1, out float a1, out float b1);
                QualityMath.RgbToLab(up[o], up[o + 1], up[o + 2], out float L2, out float a2, out float b2);
                double d = QualityMath.DeltaE2000(L1, a1, b1, L2, a2, b2);
                lock (dElock) dEsum += d;
            });
            result.DeltaE = dEsum / n;
            if (result.DeltaE > tier.maxDeltaE) { result.Pass = false; result.FailReason = $"ΔE {result.DeltaE:F3} > {tier.maxDeltaE:F3}"; }

            // Alpha per mode. 按模式评估 alpha。
            if (use.AlphaMode == AlphaMode.Cutout)
            {
                result.CutoutIoU = QualityMath.CoverageIoU(alphaA, alphaB, w, h, Mathf.Clamp01(use.Cutoff));
                if (result.CutoutIoU < tier.minCutoutIoU) { result.Pass = false; result.FailReason = $"cutout IoU {result.CutoutIoU:F4} < {tier.minCutoutIoU:F4}"; }
            }
            else if (use.AlphaMode == AlphaMode.Blend)
            {
                result.AlphaRMSE = QualityMath.AlphaRMSE(alphaA, alphaB, n);
                if (result.AlphaRMSE > tier.maxAlphaRMSE) { result.Pass = false; result.FailReason = $"alpha RMSE {result.AlphaRMSE:F4} > {tier.maxAlphaRMSE:F4}"; }
            }

            // Normal angle error p95. 法线角度误差 p95。
            if (normalA != null)
            {
                result.NormalAngle = QualityMath.NormalAngleErrorP95(normalA, normalB, n);
                if (result.NormalAngle > tier.maxNormalAngleDeg) { result.Pass = false; result.FailReason = $"normal angle {result.NormalAngle:F2}° > {tier.maxNormalAngleDeg:F2}°"; }
            }

            // Grayscale: worst channel RMSE on the channels in use. 灰度：被使用通道的最差 RMSE。
            if (use.Class == TextureClass.Mask || use.Kind == TextureKind.Mask)
            {
                result.GrayRMSE = QualityMath.WorstChannelRMSE(orig, up, n, 3);
                if (result.GrayRMSE > tier.maxGrayRMSE) { result.Pass = false; result.FailReason = $"gray RMSE {result.GrayRMSE:F4} > {tier.maxGrayRMSE:F4}"; }
            }

            return result;
        }

        private static void DecodeNormal(float r, float g, float b, out float nx, out float ny, out float nz)
        {
            // RG encoding (DXT-style); B may hold z. RG 编码（DXT 风格）；B 可能存 z。
            nx = r * 2f - 1f;
            ny = g * 2f - 1f;
            float z2 = 1f - nx * nx - ny * ny;
            nz = z2 > 0f ? Mathf.Sqrt(z2) : 0f;
            // Re-normalize. 重归一化。
            float len = Mathf.Sqrt(nx * nx + ny * ny + nz * nz);
            if (len > 1e-6f) { nx /= len; ny /= len; nz /= len; }
        }

        /// <summary>
        /// Bilinear upsample of an RGBA buffer from (sw,sh) to (w,h).
        /// 将 RGBA 缓冲从 (sw,sh) 双线性放大到 (w,h)。
        /// </summary>
        public static void UpsampleBilinear(float[] small, int sw, int sh, int w, int h, float[] dst)
        {
            if (sw == w && sh == h) { Array.Copy(small, dst, Math.Min(small.Length, dst.Length)); return; }

            float sx = (float)sw / w, sy = (float)sh / h;
            Parallel.For(0, h, y =>
            {
                float fy = (y + 0.5f) * sy - 0.5f;
                int y0 = Mathf.Clamp((int)Mathf.Floor(fy), 0, sh - 1);
                int y1 = Mathf.Clamp(y0 + 1, 0, sh - 1);
                float ty = fy - y0;
                for (int x = 0; x < w; x++)
                {
                    float fx = (x + 0.5f) * sx - 0.5f;
                    int x0 = Mathf.Clamp((int)Mathf.Floor(fx), 0, sw - 1);
                    int x1 = Mathf.Clamp(x0 + 1, 0, sw - 1);
                    float tx = fx - x0;
                    int o = (y * w + x) * 4;
                    int o00 = (y0 * sw + x0) * 4, o10 = (y0 * sw + x1) * 4, o01 = (y1 * sw + x0) * 4, o11 = (y1 * sw + x1) * 4;
                    for (int c = 0; c < 4; c++)
                    {
                        float v00 = small[o00 + c], v10 = small[o10 + c], v01 = small[o01 + c], v11 = small[o11 + c];
                        float top = v00 + (v10 - v00) * tx;
                        float bot = v01 + (v11 - v01) * tx;
                        dst[o + c] = top + (bot - top) * ty;
                    }
                }
            });
        }
    }
}
