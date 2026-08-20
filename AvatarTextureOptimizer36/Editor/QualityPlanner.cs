using System;
using System.Collections.Generic;
using UnityEngine;
using Fosa.AvatarTextureOptimizer;

namespace Fosa.AvatarTextureOptimizer.Editor
{
    internal readonly struct QualityThresholds
    {
        public readonly float Ssim;
        public readonly float DeltaE;
        public readonly float AlphaIoU;
        public readonly float AlphaRmse;
        public readonly float NormalMeanAngle;
        public readonly float NormalP95Angle;
        public readonly float GrayscaleRmse;

        public QualityThresholds(float ssim, float deltaE, float alphaIoU, float alphaRmse, float normalMeanAngle,
            float normalP95Angle, float grayscaleRmse)
        {
            Ssim = ssim;
            DeltaE = deltaE;
            AlphaIoU = alphaIoU;
            AlphaRmse = alphaRmse;
            NormalMeanAngle = normalMeanAngle;
            NormalP95Angle = normalP95Angle;
            GrayscaleRmse = grayscaleRmse;
        }

        public static QualityThresholds From(ATOQualityParameters parameters)
        {
            float ssim = Mathf.Lerp(0.92f, 0.9999f, Mathf.Clamp01(parameters.msSsimQuality));
            float deltaE = Mathf.Lerp(10f, 0.25f, Mathf.Clamp01(parameters.deltaEQuality));
            float alphaIoU = Mathf.Lerp(0.90f, 0.999f, Mathf.Clamp01(parameters.alphaQuality));
            float alphaRmse = Mathf.Lerp(0.05f, 0.001f, Mathf.Clamp01(parameters.alphaQuality));
            float normalMean = Mathf.Lerp(8f, 0.15f, Mathf.Clamp01(parameters.normalQuality));
            float normalP95 = Mathf.Lerp(15f, 0.8f, Mathf.Clamp01(parameters.normalQuality));
            float grayRmse = Mathf.Lerp(0.04f, 0.001f, Mathf.Clamp01(parameters.grayscaleQuality));
            return new QualityThresholds(ssim, deltaE, alphaIoU, alphaRmse, normalMean, normalP95, grayRmse);
        }
    }

    internal readonly struct QualityResult
    {
        public readonly bool Passed;
        public readonly float Ssim;
        public readonly float DeltaE;
        public readonly float AlphaIoU;
        public readonly float AlphaRmse;
        public readonly float NormalMeanAngle;
        public readonly float NormalP95Angle;
        public readonly float GrayscaleRmse;

        public QualityResult(bool passed, float ssim, float deltaE, float alphaIoU, float alphaRmse, float normalMeanAngle,
            float normalP95Angle, float grayscaleRmse)
        {
            Passed = passed;
            Ssim = ssim;
            DeltaE = deltaE;
            AlphaIoU = alphaIoU;
            AlphaRmse = alphaRmse;
            NormalMeanAngle = normalMeanAngle;
            NormalP95Angle = normalP95Angle;
            GrayscaleRmse = grayscaleRmse;
        }
    }

    /// <summary>
    /// Chooses texture density first, then quality-safe uniform and anisotropic scales. / 先按像素密度选择，再做质量安全的均匀/各向异性缩放。
    /// </summary>
    internal static class QualityPlanner
    {
        public static void Plan(BuildSnapshot snapshot, ATOQualityParameters parameters, int minPixelsPerMeter,
            int maxPixelsPerMeter, ATOLogger logger, ATOProgress progress, ATOBuildReport report)
        {
            minPixelsPerMeter = Mathf.Clamp(minPixelsPerMeter, 512, 8192);
            maxPixelsPerMeter = Mathf.Clamp(maxPixelsPerMeter, minPixelsPerMeter, 8192);
            QualityThresholds thresholds = QualityThresholds.From(parameters);
            bool nearLossless = parameters.targetQuality >= 0.999999f;
            for (int i = 0; i < snapshot.Islands.Count; i++)
            {
                IslandRecord island = snapshot.Islands[i];
                TextureAssetInfo primary = island.PrimaryTexture;
                if (primary == null || island.SkipQuality)
                {
                    island.UniformScale = 1f;
                    island.AxisScale = Vector2.one;
                    UpdateOutputSize(island, primary);
                    continue;
                }

                if (nearLossless)
                {
                    island.UniformScale = 1f;
                    island.AxisScale = Vector2.one;
                    UpdateOutputSize(island, primary);
                    continue;
                }

                float originalShort = Mathf.Max(1f, Mathf.Min(island.UVBounds.width * primary.Width,
                    island.UVBounds.height * primary.Height));
                if (island.PureColor)
                {
                    float targetShort = Mathf.Min(4f, originalShort);
                    island.UniformScale = Mathf.Clamp(targetShort / originalShort, 1f / Mathf.Max(1f, originalShort), 1f);
                    island.AxisScale = Vector2.one;
                    UpdateOutputSize(island, primary);
                    continue;
                }

                float densityScale = DensityScale(island, primary, minPixelsPerMeter, maxPixelsPerMeter);
                float maximumAllowedScale = Mathf.Clamp01(densityScale);
                if (maximumAllowedScale <= 0f) maximumAllowedScale = 1f;
                float uniform = FindSmallestPassingScale(snapshot, island, maximumAllowedScale, thresholds, logger);
                island.UniformScale = Mathf.Clamp(uniform, 1f / Mathf.Max(1f, originalShort), 1f);
                island.AxisScale = RefineAxes(snapshot, island, thresholds, logger);
                UpdateOutputSize(island, primary);

                progress.Step(0.02f + 0.93f * ((i + 1) / (float)Math.Max(1, snapshot.Islands.Count)),
                    "Quality island " + (i + 1) + "/" + snapshot.Islands.Count + " / 评估质量");
            }
            logger.Info("Quality planning completed for " + snapshot.Islands.Count + " island(s). / 质量规划完成。");
        }

        private static float DensityScale(IslandRecord island, TextureAssetInfo texture, int minPixelsPerMeter,
            int maxPixelsPerMeter)
        {
            if (island.SurfaceArea <= 1e-8f) return 1f;
            float pixelArea = Mathf.Max(1f, island.UVBounds.width * texture.Width * island.UVBounds.height * texture.Height);
            float currentDensity = Mathf.Sqrt(pixelArea / island.SurfaceArea);
            if (currentDensity <= maxPixelsPerMeter) return 1f;
            float targetDensity = Mathf.Clamp(maxPixelsPerMeter, minPixelsPerMeter, maxPixelsPerMeter);
            return Mathf.Clamp01(targetDensity / currentDensity);
        }

        private static float FindSmallestPassingScale(BuildSnapshot snapshot, IslandRecord island, float maximumScale,
            QualityThresholds thresholds, ATOLogger logger)
        {
            float minimumScale = 1f / Mathf.Max(1f,
                Mathf.Max(island.UVBounds.width * Mathf.Max(1, island.PrimaryTexture.Width),
                    island.UVBounds.height * Mathf.Max(1, island.PrimaryTexture.Height)));
            minimumScale = Mathf.Clamp(minimumScale, 0.0001f, maximumScale);
            QualityResult atMaximum = EvaluateIsland(snapshot, island, maximumScale, Vector2.one, thresholds, logger);
            if (!atMaximum.Passed)
            {
                // Never violate quality: the original resolution is the safe fallback. / 绝不牺牲质量，不达标时回退原分辨率。
                return 1f;
            }
            QualityResult atMinimum = EvaluateIsland(snapshot, island, minimumScale, Vector2.one, thresholds, logger);
            if (atMinimum.Passed) return minimumScale;

            float low = minimumScale;
            float high = maximumScale;
            float best = maximumScale;
            for (int iteration = 0; iteration < 14; iteration++)
            {
                float candidate = (low + high) * 0.5f;
                QualityResult result = EvaluateIsland(snapshot, island, candidate, Vector2.one, thresholds, logger);
                if (result.Passed)
                {
                    best = candidate;
                    high = candidate;
                }
                else
                {
                    low = candidate;
                }
            }
            return best;
        }

        private static Vector2 RefineAxes(BuildSnapshot snapshot, IslandRecord island, QualityThresholds thresholds,
            ATOLogger logger)
        {
            Vector2 axis = Vector2.one;
            for (int axisIndex = 0; axisIndex < 2; axisIndex++)
            {
                float low = 0.05f;
                float high = 1f;
                float best = 1f;
                for (int iteration = 0; iteration < 10; iteration++)
                {
                    float candidate = (low + high) * 0.5f;
                    Vector2 test = axis;
                    if (axisIndex == 0) test.x = candidate;
                    else test.y = candidate;
                    QualityResult result = EvaluateIsland(snapshot, island, island.UniformScale, test, thresholds, logger);
                    if (result.Passed)
                    {
                        best = candidate;
                        high = candidate;
                    }
                    else low = candidate;
                }
                if (axisIndex == 0) axis.x = best;
                else axis.y = best;
            }
            return axis;
        }

        private static QualityResult EvaluateIsland(BuildSnapshot snapshot, IslandRecord island, float uniform,
            Vector2 axis, QualityThresholds thresholds, ATOLogger logger)
        {
            bool passed = true;
            float worstSsim = 1f;
            float worstDeltaE = 0f;
            float worstIoU = 1f;
            float worstAlphaRmse = 0f;
            float worstNormalMean = 0f;
            float worstNormalP95 = 0f;
            float worstGray = 0f;
            MaterialUse material = island.Material;
            if (material == null || material.References.Count == 0) return new QualityResult(true, 1f, 0f, 1f, 0f, 0f, 0f, 0f);

            for (int i = 0; i < material.References.Count; i++)
            {
                TextureReference reference = material.References[i];
                if (reference == null || reference.Texture == null || reference.Texture.Source == null) continue;
                TexturePixelData pixels = snapshot.PixelCache.Get(reference.Texture.Source, logger);
                if (pixels == null)
                {
                    passed = false;
                    continue;
                }
                QualityResult result = QualityEvaluator.Evaluate(pixels, island.UVBounds, uniform, axis,
                    reference.Category, material.Cutout, material.Blend, material.Cutoff, thresholds);
                passed &= result.Passed;
                worstSsim = Mathf.Min(worstSsim, result.Ssim);
                worstDeltaE = Mathf.Max(worstDeltaE, result.DeltaE);
                worstIoU = Mathf.Min(worstIoU, result.AlphaIoU);
                worstAlphaRmse = Mathf.Max(worstAlphaRmse, result.AlphaRmse);
                worstNormalMean = Mathf.Max(worstNormalMean, result.NormalMeanAngle);
                worstNormalP95 = Mathf.Max(worstNormalP95, result.NormalP95Angle);
                worstGray = Mathf.Max(worstGray, result.GrayscaleRmse);
            }

            return new QualityResult(passed, worstSsim, worstDeltaE, worstIoU, worstAlphaRmse,
                worstNormalMean, worstNormalP95, worstGray);
        }

        private static void UpdateOutputSize(IslandRecord island, TextureAssetInfo texture)
        {
            if (texture == null)
            {
                island.OutputWidth = 1;
                island.OutputHeight = 1;
                return;
            }
            float scaleX = island.UniformScale * island.AxisScale.x;
            float scaleY = island.UniformScale * island.AxisScale.y;
            island.OutputWidth = Mathf.Clamp(Mathf.CeilToInt(island.UVBounds.width * texture.Width * scaleX), 1, texture.Width);
            island.OutputHeight = Mathf.Clamp(Mathf.CeilToInt(island.UVBounds.height * texture.Height * scaleY), 1, texture.Height);
        }
    }

    internal static class MaterialAlphaInspector
    {
        public static void Apply(Material material, MaterialUse use)
        {
            if (material == null || use == null) return;
            use.Cutout = material.IsKeywordEnabled("_ALPHATEST_ON") || material.IsKeywordEnabled("_ALPHACLIP_ON");
            use.Blend = material.IsKeywordEnabled("_ALPHABLEND_ON") || material.IsKeywordEnabled("_SURFACE_TYPE_TRANSPARENT") ||
                        material.renderQueue >= 3000;
            if (material.HasProperty("_AlphaToMask")) use.Cutout |= material.GetFloat("_AlphaToMask") > 0.5f;
            if (material.HasProperty("_Cutoff")) use.Cutoff = Mathf.Clamp01(material.GetFloat("_Cutoff"));
        }
    }

    /// <summary>
    /// CPU reference implementation of the requested perceptual metrics. / 所需感知指标的 CPU 参考实现。
    /// </summary>
    internal static class QualityEvaluator
    {
        public static QualityResult Evaluate(TexturePixelData source, Rect bounds, float uniform, Vector2 axis,
            ATOTextureCategory category, bool cutout, bool blend, float cutoff, QualityThresholds thresholds)
        {
            int sourceWidth = Mathf.Max(1, Mathf.CeilToInt(bounds.width * source.Width));
            int sourceHeight = Mathf.Max(1, Mathf.CeilToInt(bounds.height * source.Height));
            int sampleWidth = Mathf.Clamp(sourceWidth, 8, 64);
            int sampleHeight = Mathf.Clamp(sourceHeight, 8, 64);
            int count = sampleWidth * sampleHeight;
            float[] sourceLuma = new float[count];
            float[] candidateLuma = new float[count];
            float[] sourceAlpha = new float[count];
            float[] candidateAlpha = new float[count];
            Vector3[] sourceNormals = category == ATOTextureCategory.Normal ? new Vector3[count] : null;
            Vector3[] candidateNormals = category == ATOTextureCategory.Normal ? new Vector3[count] : null;
            Color[] sourceColors = category == ATOTextureCategory.Normal ? null : new Color[count];
            Color[] candidateColors = category == ATOTextureCategory.Normal ? null : new Color[count];

            int index = 0;
            for (int y = 0; y < sampleHeight; y++)
            {
                for (int x = 0; x < sampleWidth; x++, index++)
                {
                    float u = bounds.xMin + bounds.width * ((x + 0.5f) / sampleWidth);
                    float v = bounds.yMin + bounds.height * ((y + 0.5f) / sampleHeight);
                    Color sourceColor = Sample(source, u, v, 1f);
                    Color candidateColor = Sample(source, u, v, Mathf.Max(0.0001f, uniform * Mathf.Sqrt(axis.x * axis.y)));
                    sourceLuma[index] = Luma(sourceColor);
                    candidateLuma[index] = Luma(candidateColor);
                    sourceAlpha[index] = sourceColor.a;
                    candidateAlpha[index] = candidateColor.a;
                    if (sourceColors != null)
                    {
                        sourceColors[index] = sourceColor;
                        candidateColors[index] = candidateColor;
                    }
                    if (sourceNormals != null)
                    {
                        sourceNormals[index] = DecodeNormal(sourceColor);
                        candidateNormals[index] = DecodeNormal(candidateColor);
                    }
                }
            }

            float ssim = MultiScaleSsim(sourceLuma, candidateLuma, sampleWidth, sampleHeight);
            float deltaE = 0f;
            float grayRmse = 0f;
            if (category != ATOTextureCategory.Normal)
            {
                float totalDelta = 0f;
                for (int i = 0; i < count; i++)
                    totalDelta += Ciede2000(RgbToLab(sourceColors[i]), RgbToLab(candidateColors[i]));
                deltaE = totalDelta / count;
                grayRmse = category == ATOTextureCategory.Grayscale
                    ? Mathf.Sqrt(BurstQualityKernels.MeanSquaredError(sourceLuma, candidateLuma))
                    : 0f;
            }

            float alphaIoU = 1f;
            float alphaRmse = 0f;
            if (category == ATOTextureCategory.Transparent && (cutout || blend))
            {
                int intersection = 0;
                int union = 0;
                float alphaSquare = 0f;
                for (int i = 0; i < count; i++)
                {
                    bool sourceMask = sourceAlpha[i] >= cutoff;
                    bool candidateMask = candidateAlpha[i] >= cutoff;
                    if (sourceMask && candidateMask) intersection++;
                    if (sourceMask || candidateMask) union++;
                    alphaSquare += Mathf.Pow(sourceAlpha[i] - candidateAlpha[i], 2f);
                }
                alphaIoU = union == 0 ? 1f : intersection / (float)union;
                alphaRmse = Mathf.Sqrt(alphaSquare / count);
            }

            float normalMean = 0f;
            float normalP95 = 0f;
            if (sourceNormals != null)
            {
                float[] angles = new float[count];
                for (int i = 0; i < count; i++)
                {
                    float dot = Mathf.Clamp(Vector3.Dot(sourceNormals[i], candidateNormals[i]), -1f, 1f);
                    angles[i] = Mathf.Acos(dot) * Mathf.Rad2Deg;
                    normalMean += angles[i];
                }
                normalMean /= count;
                Array.Sort(angles);
                normalP95 = angles[Mathf.Clamp(Mathf.CeilToInt(count * 0.95f) - 1, 0, count - 1)];
            }

            bool passed = ssim >= thresholds.Ssim && deltaE <= thresholds.DeltaE &&
                          alphaIoU >= thresholds.AlphaIoU && alphaRmse <= thresholds.AlphaRmse &&
                          normalMean <= thresholds.NormalMeanAngle && normalP95 <= thresholds.NormalP95Angle &&
                          (category != ATOTextureCategory.Grayscale || grayRmse <= thresholds.GrayscaleRmse);
            return new QualityResult(passed, ssim, deltaE, alphaIoU, alphaRmse, normalMean, normalP95, grayRmse);
        }

        private static Color Sample(TexturePixelData data, float u, float v, float scale)
        {
            // Box filtering approximates linear-space prefiltered resampling without final compression loss. / 以盒式过滤近似线性空间预滤波，不引入最终压缩损失。
            float radius = scale < 0.999f ? 0.5f / Mathf.Max(1f, scale) : 0f;
            Color c0 = Bilinear(data, u - radius / data.Width, v - radius / data.Height);
            Color c1 = Bilinear(data, u + radius / data.Width, v - radius / data.Height);
            Color c2 = Bilinear(data, u - radius / data.Width, v + radius / data.Height);
            Color c3 = Bilinear(data, u + radius / data.Width, v + radius / data.Height);
            return (c0 + c1 + c2 + c3) * 0.25f;
        }

        private static Color Bilinear(TexturePixelData data, float u, float v)
        {
            float x = Mathf.Clamp01(u) * (data.Width - 1);
            float y = Mathf.Clamp01(v) * (data.Height - 1);
            int x0 = Mathf.FloorToInt(x);
            int y0 = Mathf.FloorToInt(y);
            int x1 = Mathf.Min(data.Width - 1, x0 + 1);
            int y1 = Mathf.Min(data.Height - 1, y0 + 1);
            float tx = x - x0;
            float ty = y - y0;
            Color a = Color32ToLinear(data.Get(x0, y0));
            Color b = Color32ToLinear(data.Get(x1, y0));
            Color c = Color32ToLinear(data.Get(x0, y1));
            Color d = Color32ToLinear(data.Get(x1, y1));
            return Color.Lerp(Color.Lerp(a, b, tx), Color.Lerp(c, d, tx), ty);
        }

        private static Color Color32ToLinear(Color32 color)
        {
            return new Color(Mathf.GammaToLinearSpace(color.r / 255f), Mathf.GammaToLinearSpace(color.g / 255f),
                Mathf.GammaToLinearSpace(color.b / 255f), color.a / 255f);
        }

        private static float Luma(Color color)
        {
            return color.r * 0.2126f + color.g * 0.7152f + color.b * 0.0722f;
        }

        private static Vector3 DecodeNormal(Color color)
        {
            Vector3 normal = new Vector3(color.r * 2f - 1f, color.g * 2f - 1f, color.b * 2f - 1f);
            return normal.sqrMagnitude < 1e-8f ? Vector3.forward : normal.normalized;
        }

        private static float MultiScaleSsim(float[] left, float[] right, int width, int height)
        {
            float score = Ssim(left, right);
            if (Mathf.Min(width, height) < 176) return score;
            float[] leftHalf = Downsample(left, width, height, out int halfWidth, out int halfHeight);
            float[] rightHalf = Downsample(right, width, height, out _, out _);
            score *= Mathf.Pow(Mathf.Max(0.0001f, Ssim(leftHalf, rightHalf)), 0.5f);
            if (Mathf.Min(halfWidth, halfHeight) >= 88)
            {
                float[] leftQuarter = Downsample(leftHalf, halfWidth, halfHeight, out _, out _);
                float[] rightQuarter = Downsample(rightHalf, halfWidth, halfHeight, out _, out _);
                score *= Mathf.Pow(Mathf.Max(0.0001f, Ssim(leftQuarter, rightQuarter)), 0.25f);
            }
            return score;
        }

        private static float[] Downsample(float[] source, int width, int height, out int newWidth, out int newHeight)
        {
            newWidth = Mathf.Max(1, width / 2);
            newHeight = Mathf.Max(1, height / 2);
            float[] result = new float[newWidth * newHeight];
            for (int y = 0; y < newHeight; y++)
                for (int x = 0; x < newWidth; x++)
                {
                    int sx = x * 2;
                    int sy = y * 2;
                    float total = source[sy * width + sx];
                    total += source[Mathf.Min(height - 1, sy + 1) * width + sx];
                    total += source[sy * width + Mathf.Min(width - 1, sx + 1)];
                    total += source[Mathf.Min(height - 1, sy + 1) * width + Mathf.Min(width - 1, sx + 1)];
                    result[y * newWidth + x] = total * 0.25f;
                }
            return result;
        }

        private static float Ssim(float[] left, float[] right)
        {
            if (left.Length == 0 || right.Length != left.Length) return 0f;
            float meanLeft = 0f;
            float meanRight = 0f;
            for (int i = 0; i < left.Length; i++) { meanLeft += left[i]; meanRight += right[i]; }
            meanLeft /= left.Length;
            meanRight /= right.Length;
            float varianceLeft = 0f;
            float varianceRight = 0f;
            float covariance = 0f;
            for (int i = 0; i < left.Length; i++)
            {
                float dl = left[i] - meanLeft;
                float dr = right[i] - meanRight;
                varianceLeft += dl * dl;
                varianceRight += dr * dr;
                covariance += dl * dr;
            }
            float divisor = Mathf.Max(1, left.Length - 1);
            varianceLeft /= divisor;
            varianceRight /= divisor;
            covariance /= divisor;
            const float c1 = 0.0001f;
            const float c2 = 0.0009f;
            return ((2f * meanLeft * meanRight + c1) * (2f * covariance + c2)) /
                   ((meanLeft * meanLeft + meanRight * meanRight + c1) *
                    (varianceLeft + varianceRight + c2));
        }

        private static Vector3 RgbToLab(Color color)
        {
            float r = color.r;
            float g = color.g;
            float b = color.b;
            float x = (r * 0.4124f + g * 0.3576f + b * 0.1805f) / 0.95047f;
            float y = (r * 0.2126f + g * 0.7152f + b * 0.0722f) / 1.00000f;
            float z = (r * 0.0193f + g * 0.1192f + b * 0.9505f) / 1.08883f;
            x = x > 0.008856f ? Mathf.Pow(x, 1f / 3f) : 7.787f * x + 16f / 116f;
            y = y > 0.008856f ? Mathf.Pow(y, 1f / 3f) : 7.787f * y + 16f / 116f;
            z = z > 0.008856f ? Mathf.Pow(z, 1f / 3f) : 7.787f * z + 16f / 116f;
            return new Vector3(116f * y - 16f, 500f * (x - y), 200f * (y - z));
        }

        private static float Ciede2000(Vector3 first, Vector3 second)
        {
            // CIEDE2000 implementation follows Sharma et al.; all math is deterministic and CPU-only. / CIEDE2000 按 Sharma 等人的公式实现，确定性 CPU 计算。
            float l1 = first.x, a1 = first.y, b1 = first.z;
            float l2 = second.x, a2 = second.y, b2 = second.z;
            float c1 = Mathf.Sqrt(a1 * a1 + b1 * b1);
            float c2 = Mathf.Sqrt(a2 * a2 + b2 * b2);
            float cBar = (c1 + c2) * 0.5f;
            float g = 0.5f * (1f - Mathf.Sqrt(Mathf.Pow(cBar, 7f) / (Mathf.Pow(cBar, 7f) + Mathf.Pow(25f, 7f))));
            float ap1 = (1f + g) * a1;
            float ap2 = (1f + g) * a2;
            float cp1 = Mathf.Sqrt(ap1 * ap1 + b1 * b1);
            float cp2 = Mathf.Sqrt(ap2 * ap2 + b2 * b2);
            float hp1 = Hue(ap1, b1);
            float hp2 = Hue(ap2, b2);
            float dL = l2 - l1;
            float dC = cp2 - cp1;
            float dh = hp2 - hp1;
            if (cp1 * cp2 == 0f) dh = 0f;
            else if (dh > 180f) dh -= 360f;
            else if (dh < -180f) dh += 360f;
            float dH = 2f * Mathf.Sqrt(cp1 * cp2) * Mathf.Sin(dh * Mathf.Deg2Rad * 0.5f);
            float lBar = (l1 + l2) * 0.5f;
            float cBarP = (cp1 + cp2) * 0.5f;
            float hBar = (cp1 * cp2 == 0f) ? hp1 + hp2 :
                (Mathf.Abs(hp1 - hp2) <= 180f ? (hp1 + hp2) * 0.5f :
                 (hp1 + hp2 < 360f ? (hp1 + hp2 + 360f) * 0.5f : (hp1 + hp2 - 360f) * 0.5f));
            float t = 1f - 0.17f * Mathf.Cos((hBar - 30f) * Mathf.Deg2Rad) +
                      0.24f * Mathf.Cos(2f * hBar * Mathf.Deg2Rad) +
                      0.32f * Mathf.Cos((3f * hBar + 6f) * Mathf.Deg2Rad) -
                      0.20f * Mathf.Cos((4f * hBar - 63f) * Mathf.Deg2Rad);
            float deltaTheta = 30f * Mathf.Exp(-Mathf.Pow((hBar - 275f) / 25f, 2f));
            float rc = 2f * Mathf.Sqrt(Mathf.Pow(cBarP, 7f) / (Mathf.Pow(cBarP, 7f) + Mathf.Pow(25f, 7f)));
            float sl = 1f + 0.015f * Mathf.Pow(lBar - 50f, 2f) / Mathf.Sqrt(20f + Mathf.Pow(lBar - 50f, 2f));
            float sc = 1f + 0.045f * cBarP;
            float sh = 1f + 0.015f * cBarP * t;
            float rt = -Mathf.Sin(2f * deltaTheta * Mathf.Deg2Rad) * rc;
            float lTerm = dL / sl;
            float cTerm = dC / sc;
            float hTerm = dH / sh;
            return Mathf.Sqrt(lTerm * lTerm + cTerm * cTerm + hTerm * hTerm + rt * cTerm * hTerm);
        }

        private static float Hue(float a, float b)
        {
            float hue = Mathf.Atan2(b, a) * Mathf.Rad2Deg;
            return hue < 0f ? hue + 360f : hue;
        }
    }
}
