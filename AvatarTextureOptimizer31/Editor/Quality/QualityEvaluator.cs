// QualityEvaluator.cs
// Implements the target quality algorithm:
// - Color (alpha): MS-SSIM + ΔE(CIEDE2000) + alpha (Cutout IoU / Blend RMSE)
// - Color (opaque): MS-SSIM + ΔE
// - Normal: Angular error + p95
// - Mask/grayscale: Per-channel linear RMSE (worst channel)
// All evaluated by upscaling the scaled region bilinearly back to original size.
// 目标质量算法实现。
//
// References:
//   Wang et al. (2003) "Multiscale structural similarity for image quality assessment"
//   Sharma et al. (2005) "The CIEDE2000 color-difference formula"
//
// Copyright (c) 2024 fosa. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Burst;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using Fosa.AvatarTextureOptimizer.Core;

namespace Fosa.AvatarTextureOptimizer.Quality
{
    /// <summary>
    /// Evaluates texture quality metrics for downscaling decisions.
    /// The binary search in QualityScaler calls these to determine if a scale
    /// factor meets the quality threshold.
    /// 评估贴图质量度量，用于缩放决策。
    /// </summary>
    internal sealed class QualityEvaluator
    {
        private readonly AdvancedSettings _settings;

        // MS-SSIM constants (from Wang et al. 2003)
        // C1 = (K1*L)^2, C2 = (K2*L)^2, L=255, K1=0.01, K2=0.03
        private const float C1 = 6.5025f;    // (0.01*255)^2
        private const float C2 = 58.5225f;   // (0.03*255)^2

        // MS-SSIM weights for 5 scales
        private static readonly float[] MSSSIMWeights = { 0.0448f, 0.2856f, 0.3001f, 0.2363f, 0.1333f };

        internal QualityEvaluator(AdvancedSettings settings)
        {
            _settings = settings;
        }

        /// <summary>
        /// Evaluates the quality of a downscaled-then-upscaled color texture vs. the original.
        /// Returns the worst metric ratio (1.0 = perfect). Binary search target: ratio >= threshold.
        /// 评估下采样后上采样的贴图与原图的质量差异。
        /// </summary>
        internal QualityResult EvaluateColor(
            NativeArray<Color32> original, NativeArray<Color32> scaled,
            int origW, int origH, int scaleW, int scaleH,
            AlphaMode alphaMode, float cutoff,
            bool hasAlpha)
        {
            var result = new QualityResult();

            int shortEdge = Mathf.Min(origW, origH);

            // MS-SSIM (reverts to single-scale SSIM for small islands)
            if (shortEdge >= _settings.ignoreIslandThreshold)
            {
                if (shortEdge < _settings.singleScaleSSIMThreshold)
                {
                    // Single-scale SSIM
                    result.MSSSIM = ComputeSSIM(original, scaled, origW, origH);
                }
                else
                {
                    // Full MS-SSIM
                    result.MSSSIM = ComputeMSSSIM(original, scaled, origW, origH);
                }
            }
            else
            {
                result.MSSSIM = 1.0f; // ignore for tiny islands
            }

            // ΔE (CIEDE2000) on color channels
            result.DeltaE = ComputeMaxDeltaE(original, scaled, origW, origH);

            // Alpha metrics
            if (hasAlpha)
            {
                if (alphaMode == AlphaMode.Cutout)
                {
                    result.AlphaIoU = ComputeAlphaIoU(original, scaled, origW, origH, cutoff);
                    result.AlphaMetric = result.AlphaIoU;
                    result.AlphaIsIoU = true;
                }
                else
                {
                    result.AlphaRMSE = ComputeAlphaRMSE(original, scaled, origW, origH);
                    result.AlphaMetric = result.AlphaRMSE;
                    result.AlphaIsIoU = false;
                }
            }

            // Compute worst pass ratio
            result.ComputePassRatio(_settings, hasAlpha, alphaMode);
            return result;
        }

        /// <summary>
        /// Evaluates quality of a normal map: angular error + p95.
        /// 评估法线贴图质量：角度误差 + p95。
        /// </summary>
        internal QualityResult EvaluateNormal(
            NativeArray<Color32> original, NativeArray<Color32> scaled,
            int origW, int origH)
        {
            var result = new QualityResult();

            // Decode both to tangent-space normals, compute angular error
            float maxAngle = 0;
            int count = 0;
            List<float> angles = new List<float>();

            for (int i = 0; i < original.Length; i++)
            {
                var o = DecodeNormal(original[i]);
                var s = DecodeNormal(scaled[i]);
                float dot = Mathf.Clamp(Vector3.Dot(o, s), -1f, 1f);
                float angle = Mathf.Acos(dot) * Mathf.Rad2Deg;
                angles.Add(angle);
                count++;
            }

            if (angles.Count > 0)
            {
                angles.Sort();
                // p95
                int p95Idx = Mathf.Clamp((int)(angles.Count * 0.95f), 0, angles.Count - 1);
                result.NormalP95Angle = angles[p95Idx];
                result.NormalMaxAngle = angles[angles.Count - 1];
            }

            result.PassRatio = _settings.normalAngleThreshold > 0
                ? Mathf.Clamp01(result.NormalP95Angle / _settings.normalAngleThreshold)
                : 0f;
            // Lower angle is better, so invert
            result.PassRatio = 1.0f - result.PassRatio;

            return result;
        }

        /// <summary>
        /// Evaluates grayscale mask quality: linear-space RMSE, worst channel.
        /// 评估灰度蒙版质量：线性空间 RMSE，最差通道。
        /// </summary>
        internal QualityResult EvaluateGrayscale(
            NativeArray<Color32> original, NativeArray<Color32> scaled,
            int origW, int origH, bool[] usedChannels)
        {
            var result = new QualityResult();
            float worstRMSE = 0;

            for (int ch = 0; ch < 4; ch++)
            {
                if (usedChannels == null || (ch < usedChannels.Length && !usedChannels[ch])) continue;

                double sumSqErr = 0;
                int count = 0;
                for (int i = 0; i < original.Length; i++)
                {
                    float o = GetChannel(original[i], ch) / 255f;
                    float s = GetChannel(scaled[i], ch) / 255f;
                    float diff = o - s;
                    sumSqErr += diff * diff;
                    count++;
                }
                float rmse = count > 0 ? Mathf.Sqrt((float)(sumSqErr / count)) : 0;
                worstRMSE = Mathf.Max(worstRMSE, rmse);
            }

            result.GrayscaleRMSE = worstRMSE;
            result.PassRatio = _settings.grayscaleRMSEThreshold > 0
                ? Mathf.Clamp01(1.0f - worstRMSE / _settings.grayscaleRMSEThreshold)
                : (worstRMSE <= 0.001f ? 1.0f : 0f);

            return result;
        }

        // ──────────────────────────────────────────────
        // SSIM / MS-SSIM
        // ──────────────────────────────────────────────

        private float ComputeSSIM(NativeArray<Color32> a, NativeArray<Color32> b, int w, int h)
        {
            // Convert to luminance
            float[] lumA = ToLuminance(a);
            float[] lumB = ToLuminance(b);

            // Compute SSIM with 8x8 window
            const int winSize = 8;
            float totalSSIM = 0;
            int winCount = 0;

            for (int y = 0; y <= h - winSize; y += winSize)
            {
                for (int x = 0; x <= w - winSize; x += winSize)
                {
                    float meanA = 0, meanB = 0;
                    int n = 0;
                    for (int dy = 0; dy < winSize; dy++)
                    {
                        for (int dx = 0; dx < winSize; dx++)
                        {
                            int idx = (y + dy) * w + (x + dx);
                            if (idx < lumA.Length)
                            {
                                meanA += lumA[idx];
                                meanB += lumB[idx];
                                n++;
                            }
                        }
                    }
                    if (n == 0) continue;
                    meanA /= n; meanB /= n;

                    float varA = 0, varB = 0, cov = 0;
                    for (int dy = 0; dy < winSize; dy++)
                    {
                        for (int dx = 0; dx < winSize; dx++)
                        {
                            int idx = (y + dy) * w + (x + dx);
                            if (idx < lumA.Length)
                            {
                                float da = lumA[idx] - meanA;
                                float db = lumB[idx] - meanB;
                                varA += da * da;
                                varB += db * db;
                                cov += da * db;
                            }
                        }
                    }
                    varA /= n; varB /= n; cov /= n;

                    float ssim = ((2 * meanA * meanB + C1) * (2 * cov + C2)) /
                                 ((meanA * meanA + meanB * meanB + C1) * (varA + varB + C2));
                    totalSSIM += ssim;
                    winCount++;
                }
            }

            return winCount > 0 ? totalSSIM / winCount : 1.0f;
        }

        private float ComputeMSSSIM(NativeArray<Color32> a, NativeArray<Color32> b, int w, int h)
        {
            // Multi-scale: downsample both by 2 at each scale, compute contrast+structure
            // Product of scales weighted by MSSSIMWeights
            float product = 1.0f;
            int curW = w, curH = h;
            var curA = ToLuminance(a);
            var curB = ToLuminance(b);

            for (int scale = 0; scale < 5; scale++)
            {
                if (curW < 8 || curH < 8) break;

                var cs = ComputeCS(curA, curB, curW, curH);
                float weight = scale < MSSSIMWeights.Length ? MSSSIMWeights[scale] : 0;

                // At the last scale, include luminance term
                if (scale == 4 || (curW <= 16 || curH <= 16))
                {
                    float ssim = ComputeSSIMFromArrays(curA, curB, curW, curH);
                    product *= Mathf.Pow(Mathf.Clamp01(ssim), weight);
                }
                else
                {
                    product *= Mathf.Pow(Mathf.Clamp01(cs), weight);
                }

                // Downsample
                int newW = curW / 2;
                int newH = curH / 2;
                if (newW < 1 || newH < 1) break;
                curA = Downsample(curA, curW, curH);
                curB = Downsample(curB, curW, curH);
                curW = newW;
                curH = newH;
            }

            return Mathf.Clamp01(product);
        }

        private float ComputeCS(float[] a, float[] b, int w, int h)
        {
            const int winSize = 8;
            float total = 0;
            int count = 0;
            for (int y = 0; y <= h - winSize; y += winSize)
            {
                for (int x = 0; x <= w - winSize; x += winSize)
                {
                    float meanA = WindowMean(a, x, y, w, winSize);
                    float meanB = WindowMean(b, x, y, w, winSize);
                    float varA = WindowVariance(a, x, y, w, winSize, meanA);
                    float varB = WindowVariance(b, x, y, w, winSize, meanB);
                    float cov = WindowCovariance(a, b, x, y, w, winSize, meanA, meanB);
                    float cs = (2 * cov + C2) / (varA + varB + C2);
                    total += cs;
                    count++;
                }
            }
            return count > 0 ? total / count : 1f;
        }

        private float ComputeSSIMFromArrays(float[] a, float[] b, int w, int h)
        {
            const int winSize = 8;
            float total = 0;
            int count = 0;
            for (int y = 0; y <= h - winSize; y += winSize)
            {
                for (int x = 0; x <= w - winSize; x += winSize)
                {
                    float meanA = WindowMean(a, x, y, w, winSize);
                    float meanB = WindowMean(b, x, y, w, winSize);
                    float varA = WindowVariance(a, x, y, w, winSize, meanA);
                    float varB = WindowVariance(b, x, y, w, winSize, meanB);
                    float cov = WindowCovariance(a, b, x, y, w, winSize, meanA, meanB);
                    float ssim = ((2 * meanA * meanB + C1) * (2 * cov + C2)) /
                                 ((meanA * meanA + meanB * meanB + C1) * (varA + varB + C2));
                    total += ssim;
                    count++;
                }
            }
            return count > 0 ? total / count : 1f;
        }

        // ──────────────────────────────────────────────
        // CIEDE2000
        // ──────────────────────────────────────────────

        private float ComputeMaxDeltaE(NativeArray<Color32> a, NativeArray<Color32> b, int w, int h)
        {
            float maxDeltaE = 0;
            int sampleStep = Mathf.Max(1, a.Length / 4096); // sample for performance

            for (int i = 0; i < a.Length; i += sampleStep)
            {
                var labA = RGBToLab(a[i]);
                var labB = RGBToLab(b[i]);
                float dE = CIEDE2000(labA, labB);
                if (dE > maxDeltaE) maxDeltaE = dE;
            }
            return maxDeltaE;
        }

        /// <summary>
        /// CIEDE2000 color difference formula (Sharma et al. 2005).
        /// CIEDE2000 颜色差异公式。
        /// </summary>
        internal static float CIEDE2000(Vector3 lab1, Vector3 lab2)
        {
            float L1 = lab1.x, a1 = lab1.y, b1 = lab1.z;
            float L2 = lab2.x, a2 = lab2.y, b2 = lab2.z;

            float avgL = (L1 + L2) / 2f;
            float C1 = Mathf.Sqrt(a1 * a1 + b1 * b1);
            float C2 = Mathf.Sqrt(a2 * a2 + b2 * b2);
            float avgC = (C1 + C2) / 2f;
            float G = 0.5f * (1 - Mathf.Sqrt(Mathf.Pow(avgC, 7) / (Mathf.Pow(avgC, 7) + Mathf.Pow(25f, 7))));

            float a1p = (1 + G) * a1;
            float a2p = (1 + G) * a2;
            float C1p = Mathf.Sqrt(a1p * a1p + b1 * b1);
            float C2p = Mathf.Sqrt(a2p * a2p + b2 * b2);
            float avgCp = (C1p + C2p) / 2f;

            float h1p = Mathf.Atan2(b1, a1p) * Mathf.Rad2Deg;
            float h2p = Mathf.Atan2(b2, a2p) * Mathf.Rad2Deg;
            if (h1p < 0) h1p += 360;
            if (h2p < 0) h2p += 360;

            float avgLp = avgL;
            float avgHp = (C1p * C2p == 0) ? (h1p + h2p) : DiffHue(h1p, h2p);

            float T = 1 - 0.17f * Mathf.Cos((avgHp - 30) * Mathf.Deg2Rad)
                        + 0.24f * Mathf.Cos((2 * avgHp) * Mathf.Deg2Rad)
                        + 0.32f * Mathf.Cos((3 * avgHp + 6) * Mathf.Deg2Rad)
                        - 0.20f * Mathf.Cos((4 * avgHp - 63) * Mathf.Deg2Rad);

            float dLp = L2 - L1;
            float dCp = C2p - C1p;

            float dhp = HueDiff(h1p, h2p, C1p, C2p);
            float dHp = 2 * Mathf.Sqrt(C1p * C2p) * Mathf.Sin(dhp / 2 * Mathf.Deg2Rad);

            float fAvgL = (avgLp - 50) * (avgLp - 50);
            float SL = 1 + 0.015f * fAvgL / Mathf.Sqrt(20 + fAvgL);
            float SC = 1 + 0.045f * avgCp;
            float SH = 1 + 0.015f * avgCp * T;

            float dTheta = 30 * Mathf.Exp(-Mathf.Pow((avgHp - 275) / 25, 2));
            float RC = 2 * Mathf.Sqrt(Mathf.Pow(avgCp, 7) / (Mathf.Pow(avgCp, 7) + Mathf.Pow(25f, 7)));
            float RT = -RC * Mathf.Sin(2 * dTheta * Mathf.Deg2Rad);

            float kL = 1, kC = 1, kH = 1;

            float term1 = dLp / (kL * SL);
            float term2 = dCp / (kC * SC);
            float term3 = dHp / (kH * SH);

            return Mathf.Sqrt(term1 * term1 + term2 * term2 + term3 * term3 + RT * term2 * term3);
        }

        private static float DiffHue(float h1p, float h2p)
        {
            float diff = (h1p + h2p) / 2f;
            if (Mathf.Abs(h1p - h2p) > 180)
            {
                if (diff < 180) diff += 180; else diff -= 180;
            }
            return diff;
        }

        private static float HueDiff(float h1p, float h2p, float C1p, float C2p)
        {
            if (C1p * C2p == 0) return 0;
            float diff = h2p - h1p;
            if (Mathf.Abs(diff) <= 180) return diff;
            if (diff > 180) return diff - 360;
            return diff + 360;
        }

        private static Vector3 RGBToLab(Color32 c)
        {
            // sRGB to linear
            float r = SRGBToLinear(c.r / 255f);
            float g = SRGBToLinear(c.g / 255f);
            float b = SRGBToLinear(c.b / 255f);

            // Linear RGB to XYZ (D65)
            float X = r * 0.4124564f + g * 0.3575761f + b * 0.1804375f;
            float Y = r * 0.2126729f + g * 0.7151522f + b * 0.0721750f;
            float Z = r * 0.0193339f + g * 0.1191920f + b * 0.9503041f;

            // Normalize by D65 white point
            X /= 0.95047f;
            Y /= 1.0f;
            Z /= 1.08883f;

            X = LabF(X); Y = LabF(Y); Z = LabF(Z);

            float L = 116 * Y - 16;
            float a = 500 * (X - Y);
            float bb = 200 * (Y - Z);
            return new Vector3(L, a, bb);
        }

        private static float SRGBToLinear(float v)
        {
            return v <= 0.04045f ? v / 12.92f : Mathf.Pow((v + 0.055f) / 1.055f, 2.4f);
        }

        private static float LabF(float t)
        {
            const float delta = 6f / 29f;
            return t > delta * delta * delta ? Mathf.Pow(t, 1f / 3f) : t / (3 * delta * delta) + 4f / 29f;
        }

        // ──────────────────────────────────────────────
        // Alpha metrics
        // ──────────────────────────────────────────────

        private float ComputeAlphaIoU(NativeArray<Color32> a, NativeArray<Color32> b, int w, int h, float cutoff)
        {
            float cutoffByte = cutoff * 255f;
            int intersection = 0, union = 0;

            for (int i = 0; i < a.Length; i++)
            {
                bool aOpaque = a[i].a >= cutoffByte;
                bool bOpaque = b[i].a >= cutoffByte;
                if (aOpaque && bOpaque) intersection++;
                if (aOpaque || bOpaque) union++;
            }
            return union > 0 ? (float)intersection / union : 1f;
        }

        private float ComputeAlphaRMSE(NativeArray<Color32> a, NativeArray<Color32> b, int w, int h)
        {
            // Premultiplied alpha RMSE for blend mode
            double sumSq = 0;
            for (int i = 0; i < a.Length; i++)
            {
                float aA = a[i].a / 255f;
                float bA = b[i].a / 255f;
                // Premultiplied RGB
                float aR = (a[i].r / 255f) * aA;
                float aG = (a[i].g / 255f) * aA;
                float aB = (a[i].b / 255f) * aA;
                float bR = (b[i].r / 255f) * bA;
                float bG = (b[i].g / 255f) * bA;
                float bB = (b[i].b / 255f) * bA;

                float dr = aR - bR;
                float dg = aG - bG;
                float db = aB - bB;
                float da = aA - bA;
                sumSq += (dr * dr + dg * dg + db * db + da * da) / 4.0;
            }
            return Mathf.Sqrt((float)(sumSq / a.Length));
        }

        // ──────────────────────────────────────────────
        // Helpers
        // ──────────────────────────────────────────────

        private static Vector3 DecodeNormal(Color32 c)
        {
            return new Vector3(
                c.r / 255f * 2f - 1f,
                c.g / 255f * 2f - 1f,
                c.b / 255f * 2f - 1f
            ).normalized;
        }

        private static float GetChannel(Color32 c, int ch)
        {
            return ch switch
            {
                0 => c.r,
                1 => c.g,
                2 => c.b,
                3 => c.a,
                _ => 0
            };
        }

        private static float[] ToLuminance(NativeArray<Color32> pixels)
        {
            var result = new float[pixels.Length];
            for (int i = 0; i < pixels.Length; i++)
            {
                // Rec. 709 luminance, scaled to 0-255
                result[i] = (pixels[i].r * 0.2126f + pixels[i].g * 0.7152f + pixels[i].b * 0.0722f);
            }
            return result;
        }

        private static float[] Downsample(float[] src, int w, int h)
        {
            int newW = w / 2;
            int newH = h / 2;
            var dst = new float[newW * newH];
            for (int y = 0; y < newH; y++)
            {
                for (int x = 0; x < newW; x++)
                {
                    int s00 = (y * 2) * w + (x * 2);
                    int s01 = (y * 2) * w + (x * 2 + 1);
                    int s10 = (y * 2 + 1) * w + (x * 2);
                    int s11 = (y * 2 + 1) * w + (x * 2 + 1);
                    dst[y * newW + x] = (src[s00] + src[s01] + src[s10] + src[s11]) / 4f;
                }
            }
            return dst;
        }

        private static float WindowMean(float[] arr, int x, int y, int w, int size)
        {
            float sum = 0;
            for (int dy = 0; dy < size; dy++)
                for (int dx = 0; dx < size; dx++)
                {
                    int idx = (y + dy) * w + (x + dx);
                    if (idx < arr.Length) sum += arr[idx];
                }
            return sum / (size * size);
        }

        private static float WindowVariance(float[] arr, int x, int y, int w, int size, float mean)
        {
            float sum = 0;
            for (int dy = 0; dy < size; dy++)
                for (int dx = 0; dx < size; dx++)
                {
                    int idx = (y + dy) * w + (x + dx);
                    if (idx < arr.Length)
                    {
                        float d = arr[idx] - mean;
                        sum += d * d;
                    }
                }
            return sum / (size * size);
        }

        private static float WindowCovariance(float[] a, float[] b, int x, int y, int w, int size, float meanA, float meanB)
        {
            float sum = 0;
            for (int dy = 0; dy < size; dy++)
                for (int dx = 0; dx < size; dx++)
                {
                    int idx = (y + dy) * w + (x + dx);
                    if (idx < a.Length) sum += (a[idx] - meanA) * (b[idx] - meanB);
                }
            return sum / (size * size);
        }
    }

    /// <summary>Result of a quality evaluation. / 质量评估结果。</summary>
    internal sealed class QualityResult
    {
        internal float MSSSIM;
        internal float DeltaE;
        internal float AlphaRMSE;
        internal float AlphaIoU;
        internal float AlphaMetric;
        internal bool AlphaIsIoU;
        internal float NormalP95Angle;
        internal float NormalMaxAngle;
        internal float GrayscaleRMSE;
        internal float PassRatio; // 0-1, how close to passing (1=pass)

        internal void ComputePassRatio(AdvancedSettings settings, bool hasAlpha, AlphaMode alphaMode)
        {
            // All thresholds must be satisfied (AND logic)
            // Pass ratio = min of all individual pass ratios
            float minRatio = 1.0f;

            // MS-SSIM: ratio = value/threshold (higher is better)
            if (settings.mSSSIMThreshold < 1.0f)
                minRatio = Mathf.Min(minRatio, MSSSIM / settings.mSSSIMThreshold);

            // ΔE: ratio = threshold/value (lower is better)
            if (settings.deltaEThreshold > 0)
                minRatio = Mathf.Min(minRatio, settings.deltaEThreshold / Mathf.Max(DeltaE, 0.001f));

            // Alpha
            if (hasAlpha)
            {
                if (AlphaIsIoU)
                {
                    if (settings.alphaIoUThreshold < 1.0f)
                        minRatio = Mathf.Min(minRatio, AlphaIoU / settings.alphaIoUThreshold);
                }
                else
                {
                    if (settings.alphaRMSEThreshold > 0)
                        minRatio = Mathf.Min(minRatio, settings.alphaRMSEThreshold / Mathf.Max(AlphaRMSE, 0.0001f));
                }
            }

            PassRatio = Mathf.Clamp01(minRatio);
        }

        internal bool Passes => PassRatio >= 0.999f;
    }
}
