// Quality Evaluator - Complete implementation with all metrics
// 质量评估器 - 包含所有指标的完整实现
//
// Implements:
// - Anisotropic scaling (uniform → per-axis binary refinement)
// - Alpha premultiplication before downsampling for transparent textures
// - Upsample back to original size for comparison
// - MS-SSIM / SSIM / CIEDE2000 / Alpha IoU / Alpha RMSE / Normal angle / Grayscale RMSE
// - Cache management to avoid redundant decoding
// - Pure color short-circuit
// 实现：
// - 各向异性缩放（均匀→逐轴二分细化）
// - 透明贴图下采样前预乘alpha
// - 上采样回原尺寸进行比较比较
// - MS-SSIM / SSIM / CIEDE2000 / Alpha IoU / Alpha RMSE / 法线角度 / 灰度RMSE
// - 缓存管理避免冗余解码
// - 纯色短路

using System;
using System.Collections.Generic;
using System.Linq;
using net.fosa.avatar_texture_optimizer.Editor.Core;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.Editor.Quality
{
    public static class QualityEvaluator
    {
        /// <summary>
        /// Evaluate quality with anisotropic scaling.
        /// Step 1: Uniform binary search until all metrics pass.
        /// Step 2: Per-axis independent binary refinement for finer control.
        /// 使用各向异性缩放评估质量。
        /// 步骤1：均匀二分搜索直到所有指标通过。
        /// 步骤2：逐轴独立二分细化以获得更精细控制。
        /// </summary>
        public static IslandQualityResult EvaluateIsland(
            UVIsland island, ATOBuildContext atoCtx,
            QualityParameters qp, bool isNearLossless)
        {
            var result = new IslandQualityResult { IslandId = island.Id };

            if (island.IsWhitelisted || island.SkipAtlasOnly)
            {
                result.SkippedQualityCheck = true;
                result.ScaleFactor = 1f;
                result.AnisotropicScale = Vector2.one;
                return result;
            }

            if (isNearLossless)
            {
                // Quality = 1 (near-lossless): skip UV scaling, copy original
                // 质量=1（近无损）：跳过UV缩放，原样拷贝
                result.SkippedQualityCheck = true;
                result.ScaleFactor = 1f;
                result.AnisotropicScale = Vector2.one;
                return result;
            }

            // Pure color short-circuit: min(4, bounding box short side in pixels)
            // 纯色短路：min(4, 包围盒短边像素数)
            if (island.IsPureColor)
            {
                float bbShortSide = Mathf.Min(
                    island.BoundsMax.x - island.BoundsMin.x,
                    island.BoundsMax.y - island.BoundsMin.y);
                int texIdx = island.SourceTextureIndex;
                int texSize = (texIdx >= 0 && texIdx < atoCtx.AllTextures.Count)
                    ? atoCtx.AllTextures[texIdx].Width : 1024;
                float shortSidePx = bbShortSide * texSize;
                float minPx = Mathf.Min(4f, shortSidePx);
                float scale = shortSidePx > 0 ? minPx / shortSidePx : 1f;
                result.ScaleFactor = scale;
                result.AnisotropicScale = new Vector2(scale, scale);
                return result;
            }

            // Get cached texture pixels
            Color[] origPixels = GetCachedPixels(island, atoCtx);
            if (origPixels == null)
            {
                result.ScaleFactor = 1f;
                result.AnisotropicScale = Vector2.one;
                return result;
            }

            int texW = atoCtx.AllTextures[island.SourceTextureIndex].Width;
            int texH = atoCtx.AllTextures[island.SourceTextureIndex].Height;
            bool hasAlpha = atoCtx.AllTextures[island.SourceTextureIndex].HasAlpha;
            bool isNormal = atoCtx.AllTextures[island.SourceTextureIndex].IsNormalMap;
            bool isGrayscale = atoCtx.AllTextures[island.SourceTextureIndex].IsGrayscale;

            // Extract island region from original texture
            var region = ExtractIslandRegion(origPixels, texW, texH, island);
            if (region == null || region.Pixels.Length == 0)
            {
                result.ScaleFactor = 1f;
                result.AnisotropicScale = Vector2.one;
                return result;
            }

            int regionW = region.Width;
            int regionH = region.Height;

            // Determine metric selection based on bounding box short side
            // 根据包围盒短边确定指标选择
            float bbShortSideNorm = Mathf.Min(
                island.BoundsMax.x - island.BoundsMin.x,
                island.BoundsMax.y - island.BoundsMin.y);
            float bbShortSidePx = bbShortSideNorm * Mathf.Min(texW, texH);
            bool ignoreSSIM = bbShortSidePx < 11f;
            bool useSingleScaleSSIM = bbShortSidePx < 176f;

            // Get transparency modes for this island
            var transModes = GetTransparencyModes(island, atoCtx);
            float maxCutoff = GetMaxCutoff(island, atoCtx);

            // Step 1: Uniform binary search
            // 步骤1：均匀二分搜索
            float lo = 0.01f, hi = 1f;
            float bestUniform = 1f;

            for (int iter = 0; iter < 12; iter++)
            {
                float mid = (lo + hi) * 0.5f;
                bool passed = EvaluateAtScale(region, regionW, regionH,
                    mid, mid, qp, hasAlpha, isNormal, isGrayscale,
                    ignoreSSIM, useSingleScaleSSIM, transModes, maxCutoff);

                if (passed)
                {
                    bestUniform = mid;
                    hi = mid;
                }
                else
                {
                    lo = mid;
                }
            }

            // Step 2: Per-axis anisotropic refinement
            // 步骤2：逐轴各向异性细化
            float scaleX = bestUniform;
            float scaleY = bestUniform;

            // Refine X axis independently (keep Y at bestUniform)
            lo = 0.01f; hi = bestUniform;
            for (int iter = 0; iter < 8; iter++)
            {
                float mid = (lo + hi) * 0.5f;
                bool passed = EvaluateAtScale(region, regionW, regionH,
                    mid, scaleY, qp, hasAlpha, isNormal, isGrayscale,
                    ignoreSSIM, useSingleScaleSSIM, transModes, maxCutoff);
                if (passed) { scaleX = mid; hi = mid; }
                else { lo = mid; }
            }

            // Refine Y axis independently (keep X at scaleX)
            lo = 0.01f; hi = bestUniform;
            for (int iter = 0; iter < 8; iter++)
            {
                float mid = (lo + hi) * 0.5f;
                bool passed = EvaluateAtScale(region, regionW, regionH,
                    scaleX, mid, qp, hasAlpha, isNormal, isGrayscale,
                    ignoreSSIM, useSingleScaleSSIM, transModes, maxCutoff);
                if (passed) { scaleY = mid; hi = mid; }
                else { lo = mid; }
            }

            result.ScaleFactor = Mathf.Max(scaleX, scaleY); // Worst case
            result.AnisotropicScale = new Vector2(scaleX, scaleY);

            return result;
        }

        /// <summary>
        /// Evaluate quality at a specific anisotropic scale.
        /// Downsample → upsample back to original → compare.
        /// For transparent textures: premultiply alpha before downsampling.
        /// 在特定各向异性缩放下评估质量。
        /// 降采样→升采样回原尺寸→比较。
        /// 对透明贴图：下采样前预乘alpha。
        /// </summary>
        private static bool EvaluateAtScale(
            IslandRegion region, int regionW, int regionH,
            float sx, float sy,
            QualityParameters qp,
            bool hasAlpha, bool isNormal, bool isGrayscale,
            bool ignoreSSIM, bool useSingleScaleSSIM,
            List<TransparencyMode> transModes, float cutoff)
        {
            int scaledW = Mathf.Max(1, Mathf.RoundToInt(regionW * sx));
            int scaledH = Mathf.Max(1, Mathf.RoundToInt(regionH * sy));

            Color[] origPixels = region.Pixels;

            // Prepare source pixels (premultiply alpha for transparent textures)
            // 准备源像素（对透明贴图预乘alpha）
            Color[] srcPixels = origPixels;
            bool premultiplied = false;
            if (hasAlpha && !isNormal)
            {
                srcPixels = new Color[origPixels.Length];
                for (int i = 0; i < origPixels.Length; i++)
                {
                    var c = origPixels[i];
                    srcPixels[i] = new Color(c.r * c.a, c.g * c.a, c.b * c.a, c.a);
                }
                premultiplied = true;
            }

            // Downsample (bilinear)
            Color[] downscaled = BilinearResize(srcPixels, regionW, regionH, scaledW, scaledH);

            // Upsample back to original size (bilinear)
            Color[] reconstructed = BilinearResize(downscaled, scaledW, scaledH, regionW, regionH);

            // Un-premultiply for comparison
            if (premultiplied)
            {
                for (int i = 0; i < reconstructed.Length; i++)
                {
                    var c = reconstructed[i];
                    float a = Mathf.Max(c.a, 0.001f);
                    reconstructed[i] = new Color(c.r / a, c.g / a, c.b / a, c.a);
                }
            }

            // Compare reconstructed vs original
            return CompareQuality(origPixels, reconstructed, regionW * regionH,
                qp, hasAlpha, isNormal, isGrayscale,
                ignoreSSIM, useSingleScaleSSIM, transModes, cutoff);
        }

        private static bool CompareQuality(
            Color[] original, Color[] comparison, int count,
            QualityParameters qp,
            bool hasAlpha, bool isNormal, bool isGrayscale,
            bool ignoreSSIM, bool useSingleScaleSSIM,
            List<TransparencyMode> transModes, float cutoff)
        {
            bool allPassed = true;
            string bottleneck = "";
            float worstMetric = float.MaxValue;

            // 1. MS-SSIM or SSIM (for color textures)
            if (!ignoreSSIM && !isNormal && !isGrayscale)
            {
                float ssimVal;
                if (useSingleScaleSSIM)
                {
                    ssimVal = SSIMCalculator.CalculateSSIM(original, comparison, count);
                    if (ssimVal < qp.ssimThreshold)
                    {
                        allPassed = false;
                        bottleneck = $"SSIM={ssimVal:F4}<{qp.ssimThreshold}";
                    }
                }
                else
                {
                    ssimVal = SSIMCalculator.CalculateMSSSIM(original, comparison, count);
                    if (ssimVal < qp.msSsimThreshold)
                    {
                        allPassed = false;
                        bottleneck = $"MS-SSIM={ssimVal:F4}<{qp.msSsimThreshold}";
                    }
                }
                worstMetric = Mathf.Min(worstMetric, ssimVal);
            }

            // 2. CIEDE2000 ΔE (for color textures, not normal maps)
            if (!isNormal && !isGrayscale)
            {
                float deltaE = DeltaECalculator.CalculateMaxCIEDE2000(original, comparison, count);
                if (deltaE > qp.deltaEThreshold)
                {
                    allPassed = false;
                    bottleneck = $"ΔE={deltaE:F2}>{qp.deltaEThreshold}";
                }
                worstMetric = Mathf.Min(worstMetric, qp.deltaEThreshold / Mathf.Max(deltaE, 0.001f));
            }

            // 3. Alpha metrics
            if (hasAlpha && !isNormal)
            {
                foreach (var mode in transModes)
                {
                    switch (mode)
                    {
                        case TransparencyMode.Cutout:
                            float iou = AlphaMetrics.CalculateClipIoU(original, comparison, count, cutoff);
                            if (iou < qp.alphaIoUThreshold)
                            {
                                allPassed = false;
                                bottleneck = $"AlphaIoU={iou:F4}<{qp.alphaIoUThreshold}";
                            }
                            worstMetric = Mathf.Min(worstMetric, iou);
                            break;

                        case TransparencyMode.Blend:
                        case TransparencyMode.Premultiply:
                        case TransparencyMode.Additive:
                            float rmse = AlphaMetrics.CalculateAlphaRMSE(original, comparison, count);
                            if (rmse > qp.alphaRMSEThreshold)
                            {
                                allPassed = false;
                                bottleneck = $"AlphaRMSE={rmse:F4}>{qp.alphaRMSEThreshold}";
                            }
                            worstMetric = Mathf.Min(worstMetric, qp.alphaRMSEThreshold / Mathf.Max(rmse, 0.001f));
                            break;
                    }
                }
            }

            // 4. Normal map: angle error + P95
            if (isNormal)
            {
                var (avgErr, p95Err) = NormalMapMetrics.CalculateAngleErrors(original, comparison, count);
                if (avgErr > qp.normalAngleErrorThreshold)
                {
                    allPassed = false;
                    bottleneck = $"NormalAvg={avgErr:F2}°>{qp.normalAngleErrorThreshold}°";
                }
                if (p95Err > qp.normalP95AngleErrorThreshold)
                {
                    allPassed = false;
                    bottleneck = $"NormalP95={p95Err:F2}°>{qp.normalP95AngleErrorThreshold}°";
                }
            }

            // 5. Grayscale: per-used-channel RMSE in linear space, take worst
            if (isGrayscale)
            {
                float maxRmse = GrayscaleMetrics.CalculateUsedChannelRMSE(original, comparison, count);
                if (maxRmse > qp.grayscaleRMSEThreshold)
                {
                    allPassed = false;
                    bottleneck = $"GrayRMSE={maxRmse:F4}>{qp.grayscaleRMSEThreshold}";
                }
            }

            return allPassed;
        }

        // === Helper Methods / 辅助方法 ===

        private static Color[] GetCachedPixels(UVIsland island, ATOBuildContext atoCtx)
        {
            if (island.SourceTextureIndex < 0 || island.SourceTextureIndex >= atoCtx.AllTextures.Count)
                return null;

            var texInfo = atoCtx.AllTextures[island.SourceTextureIndex];
            if (texInfo.Texture == null) return null;

            int id = texInfo.InstanceId;
            if (atoCtx.TexturePixelCache.TryGetValue(id, out var cached))
                return cached;

            var pixels = TextureHelper.ReadPixels(texInfo.Texture);
            if (pixels != null)
                atoCtx.TexturePixelCache[id] = pixels;

            return pixels;
        }

        private static IslandRegion ExtractIslandRegion(Color[] fullPixels, int texW, int texH, UVIsland island)
        {
            int x0 = Mathf.Clamp(Mathf.FloorToInt(island.BoundsMin.x * texW), 0, texW - 1);
            int y0 = Mathf.Clamp(Mathf.FloorToInt(island.BoundsMin.y * texH), 0, texH - 1);
            int x1 = Mathf.Clamp(Mathf.CeilToInt(island.BoundsMax.x * texW), 1, texW);
            int y1 = Mathf.Clamp(Mathf.CeilToInt(island.BoundsMax.y * texH), 1, texH);

            int w = x1 - x0;
            int h = y1 - y0;
            if (w <= 0 || h <= 0) return null;

            var region = new Color[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    region[y * w + x] = fullPixels[(y0 + y) * texW + (x0 + x)];

            return new IslandRegion { Pixels = region, Width = w, Height = h };
        }

        /// <summary>
        /// Bilinear resize (downsample or upsample).
        /// 双线性缩放（降采样或升采样）。
        /// </summary>
        public static Color[] BilinearResize(Color[] src, int srcW, int srcH, int dstW, int dstH)
        {
            dstW = Mathf.Max(1, dstW);
            dstH = Mathf.Max(1, dstH);
            srcW = Mathf.Max(1, srcW);
            srcH = Mathf.Max(1, srcH);

            var dst = new Color[dstW * dstH];
            float xRatio = (float)srcW / dstW;
            float yRatio = (float)srcH / dstH;

            for (int y = 0; y < dstH; y++)
            {
                float srcY = (y + 0.5f) * yRatio - 0.5f;
                int y0 = Mathf.Clamp(Mathf.FloorToInt(srcY), 0, srcH - 1);
                int y1 = Mathf.Min(y0 + 1, srcH - 1);
                float fy = srcY - y0;

                for (int x = 0; x < dstW; x++)
                {
                    float srcX = (x + 0.5f) * xRatio - 0.5f;
                    int x0 = Mathf.Clamp(Mathf.FloorToInt(srcX), 0, srcW - 1);
                    int x1 = Mathf.Min(x0 + 1, srcW - 1);
                    float fx = srcX - x0;

                    Color c00 = src[y0 * srcW + x0];
                    Color c10 = src[y0 * srcW + x1];
                    Color c01 = src[y1 * srcW + x0];
                    Color c11 = src[y1 * srcW + x1];

                    dst[y * dstW + x] = Color.Lerp(
                        Color.Lerp(c00, c10, fx),
                        Color.Lerp(c01, c11, fx), fy);
                }
            }
            return dst;
        }

        private static List<TransparencyMode> GetTransparencyModes(UVIsland island, ATOBuildContext atoCtx)
        {
            var modes = new HashSet<TransparencyMode>();
            foreach (var kvp in atoCtx.UVTextureMap)
            {
                foreach (var usage in kvp.Value.TextureUsages)
                {
                    if (usage.Texture != null && island.SourceTextureIndex >= 0 &&
                        island.SourceTextureIndex < atoCtx.AllTextures.Count)
                    {
                        var texInfo = atoCtx.AllTextures[island.SourceTextureIndex];
                        if (usage.Texture == texInfo.Texture || usage.Texture == texInfo.OriginalTexture)
                            modes.Add(usage.TransparencyMode);
                    }
                }
            }
            return modes.ToList();
        }

        private static float GetMaxCutoff(UVIsland island, ATOBuildContext atoCtx)
        {
            float maxCutoff = 0.5f;
            foreach (var kvp in atoCtx.UVTextureMap)
            {
                foreach (var usage in kvp.Value.TextureUsages)
                {
                    if (usage.Texture != null && island.SourceTextureIndex >= 0 &&
                        island.SourceTextureIndex < atoCtx.AllTextures.Count)
                    {
                        var texInfo = atoCtx.AllTextures[island.SourceTextureIndex];
                        if (usage.Texture == texInfo.Texture || usage.Texture == texInfo.OriginalTexture)
                            maxCutoff = Mathf.Max(maxCutoff, usage.Cutoff);
                    }
                }
            }
            return maxCutoff;
        }

        public class IslandRegion
        {
            public Color[] Pixels;
            public int Width, Height;
        }
    }

    // === SSIM Calculator / SSIM计算器 ===
    public static class SSIMCalculator
    {
        private const float C1 = 0.01f * 0.01f;
        private const float C2 = 0.03f * 0.03f;

        public static float CalculateSSIM(Color[] a, Color[] b, int count)
        {
            int n = Mathf.Min(a.Length, b.Length, count);
            if (n == 0) return 1f;

            float meanX = 0, meanY = 0;
            for (int i = 0; i < n; i++)
            {
                meanX += Luminance(a[i]);
                meanY += Luminance(b[i]);
            }
            meanX /= n; meanY /= n;

            float varX = 0, varY = 0, covXY = 0;
            for (int i = 0; i < n; i++)
            {
                float x = Luminance(a[i]) - meanX;
                float y = Luminance(b[i]) - meanY;
                varX += x * x; varY += y * y; covXY += x * y;
            }
            varX /= n; varY /= n; covXY /= n;

            return ((2 * meanX * meanY + C1) * (2 * covXY + C2)) /
                   ((meanX * meanX + meanY * meanY + C1) * (varX + varY + C2));
        }

        public static float CalculateMSSSIM(Color[] a, Color[] b, int count)
        {
            // Weights from Wang et al. 2003
            float[] w = { 0.0448f, 0.2856f, 0.3001f, 0.2363f, 0.1333f };
            float result = 1f;
            var ca = a; var cb = b;
            int n = count;

            for (int scale = 0; scale < 5; scale++)
            {
                if (n < 16) break;
                float ssim = CalculateSSIM(ca, cb, n);
                result *= Mathf.Pow(Mathf.Max(ssim, 0.0001f), w[scale]);

                // Downsample 2x for next scale
                int halfN = n / 4;
                if (halfN < 4) break;
                int side = Mathf.RoundToInt(Mathf.Sqrt(n));
                int newSide = side / 2;
                var na = new Color[newSide * newSide];
                var nb = new Color[newSide * newSide];
                for (int y = 0; y < newSide; y++)
                {
                    for (int x = 0; x < newSide; x++)
                    {
                        int sx = x * 2, sy = y * 2;
                        if (sx + 1 < side && sy + 1 < side)
                        {
                            na[y * newSide + x] = (ca[sy * side + sx] + ca[sy * side + sx + 1] +
                                ca[(sy + 1) * side + sx] + ca[(sy + 1) * side + sx + 1]) * 0.25f;
                            nb[y * newSide + x] = (cb[sy * side + sx] + cb[sy * side + sx + 1] +
                                cb[(sy + 1) * side + sx] + cb[(sy + 1) * side + sx + 1]) * 0.25f;
                        }
                    }
                }
                ca = na; cb = nb; n = newSide * newSide;
            }
            return Mathf.Clamp01(result);
        }

        private static float Luminance(Color c) => 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;
    }

    // === ΔE (CIEDE2000) / ΔE（CIEDE2000） ===
    public static class DeltaECalculator
    {
        public static float CalculateMaxCIEDE2000(Color[] a, Color[] b, int count)
        {
            int n = Mathf.Min(a.Length, b.Length, count);
            float maxDE = 0;
            for (int i = 0; i < n; i++)
            {
                var lab1 = RGBToLab(a[i]);
                var lab2 = RGBToLab(b[i]);
                float de = CIEDE2000(lab1, lab2);
                if (de > maxDE) maxDE = de;
            }
            return maxDE;
        }

        private static Vector3 RGBToLab(Color c)
        {
            float r = c.r <= 0.04045f ? c.r / 12.92f : Mathf.Pow((c.r + 0.055f) / 1.055f, 2.4f);
            float g = c.g <= 0.04045f ? c.g / 12.92f : Mathf.Pow((c.g + 0.055f) / 1.055f, 2.4f);
            float b = c.b <= 0.04045f ? c.b / 12.92f : Mathf.Pow((c.b + 0.055f) / 1.055f, 2.4f);

            float x = (0.4124564f * r + 0.3575761f * g + 0.1804375f * b) / 0.95047f;
            float y = (0.2126729f * r + 0.7151522f * g + 0.0721750f * b) / 1.00000f;
            float z = (0.0193339f * r + 0.1191920f * g + 0.9503041f * b) / 1.08883f;

            x = LabF(x); y = LabF(y); z = LabF(z);
            return new Vector3(116 * y - 16, 500 * (x - y), 200 * (y - z));
        }

        private static float LabF(float t)
        {
            float d = 6f / 29f;
            return t > d * d * d ? Mathf.Pow(t, 1f / 3f) : t / (3 * d * d) + 4f / 29f;
        }

        private static float CIEDE2000(Vector3 lab1, Vector3 lab2)
        {
            float L1 = lab1.x, a1 = lab1.y, b1 = lab1.z;
            float L2 = lab2.x, a2 = lab2.y, b2 = lab2.z;
            float C1 = Mathf.Sqrt(a1 * a1 + b1 * b1);
            float C2 = Mathf.Sqrt(a2 * a2 + b2 * b2);
            float Cab = (C1 + C2) / 2;
            float Cab7 = Mathf.Pow(Cab, 7);
            float G = 0.5f * (1 - Mathf.Sqrt(Cab7 / (Cab7 + 6103515625f))); // 25^7
            float a1p = a1 * (1 + G), a2p = a2 * (1 + G);
            float C1p = Mathf.Sqrt(a1p * a1p + b1 * b1);
            float C2p = Mathf.Sqrt(a2p * a2p + b2 * b2);
            float h1p = Mathf.Atan2(b1, a1p) * 180f / Mathf.PI; if (h1p < 0) h1p += 360;
            float h2p = Mathf.Atan2(b2, a2p) * 180f / Mathf.PI; if (h2p < 0) h2p += 360;
            float dLp = L2 - L1, dCp = C2p - C1p;
            float dhp;
            if (C1p * C2p == 0) dhp = 0;
            else if (Mathf.Abs(h2p - h1p) <= 180) dhp = h2p - h1p;
            else if (h2p - h1p > 180) dhp = h2p - h1p - 360;
            else dhp = h2p - h1p + 360;
            float dHp = 2 * Mathf.Sqrt(C1p * C2p) * Mathf.Sin(dhp * Mathf.PI / 360f);
            float Lp = (L1 + L2) / 2, Cp = (C1p + C2p) / 2;
            float hp;
            if (C1p * C2p == 0) hp = h1p + h2p;
            else if (Mathf.Abs(h1p - h2p) <= 180) hp = (h1p + h2p) / 2;
            else if (h1p + h2p < 360) hp = (h1p + h2p + 360) / 2;
            else hp = (h1p + h2p - 360) / 2;
            float T = 1 - 0.17f * Mathf.Cos((hp - 30) * Mathf.PI / 180)
                + 0.24f * Mathf.Cos(2 * hp * Mathf.PI / 180)
                + 0.32f * Mathf.Cos((3 * hp + 6) * Mathf.PI / 180)
                - 0.20f * Mathf.Cos((4 * hp - 63) * Mathf.PI / 180);
            float SL = 1 + 0.015f * (Lp - 50) * (Lp - 50) / Mathf.Sqrt(20 + (Lp - 50) * (Lp - 50));
            float SC = 1 + 0.045f * Cp;
            float SH = 1 + 0.015f * Cp * T;
            float Cp7 = Mathf.Pow(Cp, 7);
            float RT = -2 * Mathf.Sqrt(Cp7 / (Cp7 + 6103515625f))
                * Mathf.Sin(60 * Mathf.Exp(-((hp - 275) / 25) * ((hp - 275) / 25)) * Mathf.PI / 180);
            return Mathf.Sqrt(
                (dLp / SL) * (dLp / SL) + (dCp / SC) * (dCp / SC) + (dHp / SH) * (dHp / SH)
                + RT * (dCp / SC) * (dHp / SH));
        }
    }

    public static class AlphaMetrics
    {
        public static float CalculateClipIoU(Color[] a, Color[] b, int count, float cutoff = 0.5f)
        {
            int n = Mathf.Min(a.Length, b.Length, count);
            int inter = 0, union = 0;
            for (int i = 0; i < n; i++)
            {
                bool ao = a[i].a >= cutoff, bo = b[i].a >= cutoff;
                if (ao && bo) inter++;
                if (ao || bo) union++;
            }
            return union == 0 ? 1f : (float)inter / union;
        }

        public static float CalculateAlphaRMSE(Color[] a, Color[] b, int count)
        {
            int n = Mathf.Min(a.Length, b.Length, count);
            if (n == 0) return 0;
            float sum = 0;
            for (int i = 0; i < n; i++)
            {
                float d = a[i].a - b[i].a;
                sum += d * d;
            }
            return Mathf.Sqrt(sum / n);
        }
    }

    public static class NormalMapMetrics
    {
        public static (float avg, float p95) CalculateAngleErrors(Color[] a, Color[] b, int count)
        {
            int n = Mathf.Min(a.Length, b.Length, count);
            if (n == 0) return (0, 0);
            var errors = new float[n];
            for (int i = 0; i < n; i++)
            {
                var n1 = DecodeNormal(a[i]);
                var n2 = DecodeNormal(b[i]);
                float dot = Mathf.Clamp(Vector3.Dot(n1, n2), -1f, 1f);
                errors[i] = Mathf.Acos(dot) * Mathf.Rad2Deg;
            }
            Array.Sort(errors);
            float avg = 0;
            for (int i = 0; i < n; i++) avg += errors[i];
            avg /= n;
            int p95i = Mathf.Min((int)(n * 0.95f), n - 1);
            return (avg, errors[p95i]);
        }

        private static Vector3 DecodeNormal(Color c)
        {
            float x = c.r * 2 - 1, y = c.g * 2 - 1;
            float z = Mathf.Sqrt(Mathf.Max(0, 1 - x * x - y * y));
            return new Vector3(x, y, z).normalized;
        }
    }

    public static class GrayscaleMetrics
    {
        /// <summary>
        /// RMSE only on used channels in linear space, take worst channel.
        /// 仅在被使用的通道上线性空间RMSE，逐通道取最差。
        /// </summary>
        public static float CalculateUsedChannelRMSE(Color[] a, Color[] b, int count)
        {
            int n = Mathf.Min(a.Length, b.Length, count);
            if (n == 0) return 0;

            // Detect which channels are actually used (have non-zero variance)
            bool useR = false, useG = false, useB = false, useA = false;
            float maxR = 0, maxG = 0, maxB = 0, maxA = 0;
            for (int i = 0; i < n; i++)
            {
                maxR = Mathf.Max(maxR, a[i].r); maxG = Mathf.Max(maxG, a[i].g);
                maxB = Mathf.Max(maxB, a[i].b); maxA = Mathf.Max(maxA, a[i].a);
            }
            useR = maxR > 0.001f; useG = maxG > 0.001f;
            useB = maxB > 0.001f; useA = maxA > 0.001f && maxA < 0.999f;

            float sumR = 0, sumG = 0, sumB = 0, sumA = 0;
            for (int i = 0; i < n; i++)
            {
                // Linear space values (grayscale textures are typically linear)
                float dr = a[i].r - b[i].r; sumR += dr * dr;
                float dg = a[i].g - b[i].g; sumG += dg * dg;
                float db = a[i].b - b[i].b; sumB += db * db;
                float da = a[i].a - b[i].a; sumA += da * da;
            }

            float worst = 0;
            if (useR) worst = Mathf.Max(worst, Mathf.Sqrt(sumR / n));
            if (useG) worst = Mathf.Max(worst, Mathf.Sqrt(sumG / n));
            if (useB) worst = Mathf.Max(worst, Mathf.Sqrt(sumB / n));
            if (useA) worst = Mathf.Max(worst, Mathf.Sqrt(sumA / n));
            return worst;
        }
    }
}
