using System;
using System.Collections.Generic;
using System.Linq;
using Net.Fosa.AvatarTextureOptimizer;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// CPU-side quality evaluator used to avoid over-shrinking in the current milestone.
    /// 当前里程碑用于防止过度缩小的 CPU 质量评估器。
    /// </summary>
    internal static class AtoQualityEvaluator
    {
        public static Vector2 EstimateMinimumTargetPixels(AtoUvGroupRecord uvGroup, AvatarTextureOptimizer component)
        {
            if (uvGroup == null || uvGroup.Usages.Count == 0)
            {
                return Vector2.zero;
            }

            var sourcePixels = AtoAtlasPlanning.EstimateSourcePixels(uvGroup);
            if (component.Quality.Parameters.GlobalTargetQuality >= 0.9999f)
            {
                return sourcePixels;
            }

            var sourceCache = new Dictionary<Texture2D, Texture2D>();
            var maxRequirement = new Vector2(4.0f, 4.0f);
            foreach (var usage in uvGroup.Usages.Where(u => u.Decision == AtoTextureDecision.Candidate))
            {
                if (usage.Texture is not Texture2D texture)
                {
                    continue;
                }

                var readable = GetReadableTexture(texture, sourceCache);
                var baseWidth = Mathf.Max(4, Mathf.CeilToInt(Mathf.Max(1.0f, texture.width * Mathf.Max(uvGroup.Span.x, 0.0f))));
                var baseHeight = Mathf.Max(4, Mathf.CeilToInt(Mathf.Max(1.0f, texture.height * Mathf.Max(uvGroup.Span.y, 0.0f))));
                var reference = AtoTextureRasterizer.RenderUvGroupPatch(readable, uvGroup, baseWidth, baseHeight, usage.Semantic);
                Vector2 requirement;
                if (usage.Semantic == AtoTextureSemantic.Color && IsApproximatelySolid(reference))
                {
                    requirement = new Vector2(Mathf.Min(4, baseWidth), Mathf.Min(4, baseHeight));
                }
                else
                {
                    requirement = BinarySearchMinimum(uvGroup, usage.Semantic, usage.FilterMode, readable, reference, baseWidth, baseHeight, component);
                }
                maxRequirement = Vector2.Max(maxRequirement, requirement);
            }

            return Vector2.Min(sourcePixels, maxRequirement);
        }

        private static Vector2 BinarySearchMinimum(AtoUvGroupRecord uvGroup, AtoTextureSemantic semantic, FilterMode filterMode, Texture2D readable, Color[] reference, int sourceWidth, int sourceHeight, AvatarTextureOptimizer component)
        {
            if (!Passes(uvGroup, semantic, readable, reference, sourceWidth, sourceHeight, sourceWidth, sourceHeight, component))
            {
                return new Vector2(sourceWidth, sourceHeight);
            }

            var isotropic = BinarySearchIsotropic(uvGroup, semantic, readable, reference, sourceWidth, sourceHeight, component);
            var refinedWidth = BinarySearchAxis(uvGroup, semantic, readable, reference, sourceWidth, sourceHeight, isotropic.x, isotropic.y, true, component);
            var refinedHeight = BinarySearchAxis(uvGroup, semantic, readable, reference, sourceWidth, sourceHeight, refinedWidth, isotropic.y, false, component);
            return new Vector2(refinedWidth, refinedHeight);
        }

        private static Vector2 BinarySearchIsotropic(AtoUvGroupRecord uvGroup, AtoTextureSemantic semantic, Texture2D readable, Color[] reference, int sourceWidth, int sourceHeight, AvatarTextureOptimizer component)
        {
            var lowScale = Mathf.Max(4.0f / sourceWidth, 4.0f / sourceHeight);
            var highScale = 1.0f;
            var bestScale = highScale;
            const int iterations = 7;

            for (var i = 0; i < iterations; i++)
            {
                var mid = (lowScale + highScale) * 0.5f;
                var testWidth = Mathf.Max(4, Mathf.CeilToInt(sourceWidth * mid));
                var testHeight = Mathf.Max(4, Mathf.CeilToInt(sourceHeight * mid));
                if (Passes(uvGroup, semantic, readable, reference, sourceWidth, sourceHeight, testWidth, testHeight, component))
                {
                    bestScale = mid;
                    highScale = mid;
                }
                else
                {
                    lowScale = mid;
                }
            }

            return new Vector2(
                Mathf.Max(4, Mathf.CeilToInt(sourceWidth * bestScale)),
                Mathf.Max(4, Mathf.CeilToInt(sourceHeight * bestScale)));
        }

        private static int BinarySearchAxis(AtoUvGroupRecord uvGroup, AtoTextureSemantic semantic, Texture2D readable, Color[] reference, int sourceWidth, int sourceHeight, float currentWidth, float currentHeight, bool refineWidth, AvatarTextureOptimizer component)
        {
            var low = 4;
            var high = refineWidth ? Mathf.Max(4, Mathf.RoundToInt(currentWidth)) : Mathf.Max(4, Mathf.RoundToInt(currentHeight));
            var best = high;
            const int iterations = 7;

            for (var i = 0; i < iterations; i++)
            {
                var mid = (low + high) / 2;
                var testWidth = refineWidth ? mid : Mathf.Max(4, Mathf.RoundToInt(currentWidth));
                var testHeight = refineWidth ? Mathf.Max(4, Mathf.RoundToInt(currentHeight)) : mid;
                if (Passes(uvGroup, semantic, readable, reference, sourceWidth, sourceHeight, testWidth, testHeight, component))
                {
                    best = mid;
                    high = mid;
                }
                else
                {
                    low = mid + 1;
                }
            }

            return Mathf.Max(4, best);
        }

        private static bool Passes(AtoUvGroupRecord uvGroup, AtoTextureSemantic semantic, Texture2D readable, Color[] reference, int sourceWidth, int sourceHeight, int testWidth, int testHeight, AvatarTextureOptimizer component)
        {
            var reduced = AtoTextureRasterizer.RenderUvGroupPatch(readable, uvGroup, testWidth, testHeight, semantic);
            var restored = UpscaleBilinear(reduced, testWidth, testHeight, sourceWidth, sourceHeight);
            return semantic switch
            {
                AtoTextureSemantic.Normal => EvaluateNormal(reference, restored, component.Quality.Parameters),
                AtoTextureSemantic.Grayscale => EvaluateGrayscale(reference, restored, component.Quality.Parameters),
                AtoTextureSemantic.Mask => EvaluateMask(reference, restored, component.Quality.Parameters),
                _ => EvaluateColor(reference, restored, component.Quality.Parameters),
            };
        }

        private static bool EvaluateColor(IReadOnlyList<Color> reference, IReadOnlyList<Color> restored, AvatarTextureOptimizerQualityParameters parameters)
        {
            var referenceLinear = reference.Select(c => c.linear).ToArray();
            var restoredLinear = restored.Select(c => c.linear).ToArray();
            var minDim = Mathf.Min(referenceLinear.Length > 0 ? Mathf.RoundToInt(Mathf.Sqrt(referenceLinear.Length)) : 0, referenceLinear.Length > 0 ? Mathf.RoundToInt(Mathf.Sqrt(referenceLinear.Length)) : 0);
            var structural = minDim < 11
                ? 1.0
                : minDim < 176
                    ? ComputeSsim(referenceLinear, restoredLinear)
                    : ComputeMsSsim(referenceLinear, restoredLinear);

            var deltaE = ComputeAverageDeltaE2000(referenceLinear, restoredLinear);
            var binaryLike = IsBinaryLikeAlpha(reference);
            if (binaryLike)
            {
                var iou = ComputeAlphaIou(reference, restored, 0.5f);
                return structural >= parameters.StructuralSimilarity
                       && deltaE <= parameters.MaxDeltaE2000
                       && iou >= parameters.AlphaEdgeIou;
            }

            var alphaRmse = ComputeAlphaRmse(reference, restored);
            return structural >= parameters.StructuralSimilarity
                   && deltaE <= parameters.MaxDeltaE2000
                   && alphaRmse <= parameters.AlphaBlendRmse;
        }

        private static bool EvaluateMask(IReadOnlyList<Color> reference, IReadOnlyList<Color> restored, AvatarTextureOptimizerQualityParameters parameters)
        {
            var alphaRmse = ComputeAlphaRmse(reference, restored);
            var worstGray = 0.0;
            for (var i = 0; i < reference.Count; i++)
            {
                worstGray = Math.Max(worstGray, Math.Abs(reference[i].grayscale - restored[i].grayscale));
            }

            return alphaRmse <= Math.Max(parameters.AlphaBlendRmse, 0.002)
                   && worstGray <= Math.Max(parameters.GrayscaleRmse * 2.0, 0.01);
        }

        private static bool EvaluateGrayscale(IReadOnlyList<Color> reference, IReadOnlyList<Color> restored, AvatarTextureOptimizerQualityParameters parameters)
        {
            var rmse = 0.0;
            for (var i = 0; i < reference.Count; i++)
            {
                rmse += Square(reference[i].grayscale - restored[i].grayscale);
            }

            rmse = Math.Sqrt(rmse / Math.Max(1, reference.Count));
            return rmse <= Math.Max(parameters.GrayscaleRmse, 0.001);
        }

        private static bool EvaluateNormal(IReadOnlyList<Color> reference, IReadOnlyList<Color> restored, AvatarTextureOptimizerQualityParameters parameters)
        {
            var angles = new List<double>(reference.Count);
            for (var i = 0; i < reference.Count; i++)
            {
                var a = DecodeNormal(reference[i]);
                var b = DecodeNormal(restored[i]);
                var dot = Mathf.Clamp(Vector3.Dot(a, b), -1.0f, 1.0f);
                angles.Add(Math.Acos(dot) * Mathf.Rad2Deg);
            }

            angles.Sort();
            var mean = angles.Average();
            var p95 = angles[(int)Mathf.Clamp(Mathf.FloorToInt((angles.Count - 1) * 0.95f), 0, angles.Count - 1)];
            return mean <= Math.Max(parameters.NormalAngularErrorDegrees, 0.5f)
                   && p95 <= Math.Max(parameters.NormalP95AngularErrorDegrees, 1.0f);
        }

        private static double ComputeSsim(IReadOnlyList<Color> reference, IReadOnlyList<Color> restored)
        {
            var n = Math.Min(reference.Count, restored.Count);
            if (n == 0)
            {
                return 1.0;
            }

            var muX = 0.0;
            var muY = 0.0;
            for (var i = 0; i < n; i++)
            {
                muX += Luminance(reference[i]);
                muY += Luminance(restored[i]);
            }
            muX /= n;
            muY /= n;

            var sigmaX = 0.0;
            var sigmaY = 0.0;
            var sigmaXY = 0.0;
            for (var i = 0; i < n; i++)
            {
                var x = Luminance(reference[i]) - muX;
                var y = Luminance(restored[i]) - muY;
                sigmaX += x * x;
                sigmaY += y * y;
                sigmaXY += x * y;
            }

            sigmaX /= Math.Max(1, n - 1);
            sigmaY /= Math.Max(1, n - 1);
            sigmaXY /= Math.Max(1, n - 1);

            const double c1 = 0.01 * 0.01;
            const double c2 = 0.03 * 0.03;
            var numerator = (2 * muX * muY + c1) * (2 * sigmaXY + c2);
            var denominator = (muX * muX + muY * muY + c1) * (sigmaX + sigmaY + c2);
            return denominator == 0 ? 1.0 : numerator / denominator;
        }

        private static double ComputeMsSsim(Color[] reference, Color[] restored)
        {
            var currentReference = reference;
            var currentRestored = restored;
            var weights = new[] { 0.0448, 0.2856, 0.3001, 0.2363, 0.1333 };
            var value = 1.0;
            for (var level = 0; level < weights.Length; level++)
            {
                var ssim = ComputeSsim(currentReference, currentRestored);
                value *= Math.Pow(Math.Max(0.0001, ssim), weights[level]);
                if (level == weights.Length - 1)
                {
                    break;
                }

                var size = InferSquareSize(currentReference.Length);
                if (size <= 1)
                {
                    break;
                }

                var nextSize = Math.Max(1, size / 2);
                currentReference = Downsample2x(currentReference, size, size, nextSize, nextSize);
                currentRestored = Downsample2x(currentRestored, size, size, nextSize, nextSize);
            }

            return value;
        }

        private static double ComputeAverageDeltaE2000(IReadOnlyList<Color> reference, IReadOnlyList<Color> restored)
        {
            var n = Math.Min(reference.Count, restored.Count);
            if (n == 0)
            {
                return 0.0;
            }

            var total = 0.0;
            for (var i = 0; i < n; i++)
            {
                total += ComputeDeltaE2000(reference[i], restored[i]);
            }

            return total / n;
        }

        private static double ComputeDeltaE2000(Color a, Color b)
        {
            var lab1 = RgbToLab(a);
            var lab2 = RgbToLab(b);

            var lBar = (lab1.x + lab2.x) * 0.5;
            var c1 = Math.Sqrt(lab1.y * lab1.y + lab1.z * lab1.z);
            var c2 = Math.Sqrt(lab2.y * lab2.y + lab2.z * lab2.z);
            var cBar = (c1 + c2) * 0.5;
            var g = 0.5 * (1 - Math.Sqrt(Math.Pow(cBar, 7) / (Math.Pow(cBar, 7) + Math.Pow(25.0, 7))));
            var a1Prime = lab1.y * (1 + g);
            var a2Prime = lab2.y * (1 + g);
            var c1Prime = Math.Sqrt(a1Prime * a1Prime + lab1.z * lab1.z);
            var c2Prime = Math.Sqrt(a2Prime * a2Prime + lab2.z * lab2.z);
            var h1Prime = ToHueAngle(Math.Atan2(lab1.z, a1Prime));
            var h2Prime = ToHueAngle(Math.Atan2(lab2.z, a2Prime));

            var deltaLPrime = lab2.x - lab1.x;
            var deltaCPrime = c2Prime - c1Prime;
            var deltahPrime = DeltaHue(h1Prime, h2Prime, c1Prime, c2Prime);
            var deltaHPrime = 2 * Math.Sqrt(c1Prime * c2Prime) * Math.Sin(DegToRad(deltahPrime / 2));

            var lPrimeBar = (lab1.x + lab2.x) * 0.5;
            var cPrimeBar = (c1Prime + c2Prime) * 0.5;
            var hPrimeBar = AverageHue(h1Prime, h2Prime, c1Prime, c2Prime);
            var t = 1 - 0.17 * Math.Cos(DegToRad(hPrimeBar - 30))
                      + 0.24 * Math.Cos(DegToRad(2 * hPrimeBar))
                      + 0.32 * Math.Cos(DegToRad(3 * hPrimeBar + 6))
                      - 0.20 * Math.Cos(DegToRad(4 * hPrimeBar - 63));
            var deltaTheta = 30 * Math.Exp(-Square((hPrimeBar - 275) / 25.0));
            var rC = 2 * Math.Sqrt(Math.Pow(cPrimeBar, 7) / (Math.Pow(cPrimeBar, 7) + Math.Pow(25.0, 7)));
            var sL = 1 + (0.015 * Square(lPrimeBar - 50)) / Math.Sqrt(20 + Square(lPrimeBar - 50));
            var sC = 1 + 0.045 * cPrimeBar;
            var sH = 1 + 0.015 * cPrimeBar * t;
            var rT = -Math.Sin(DegToRad(2 * deltaTheta)) * rC;

            var termL = deltaLPrime / sL;
            var termC = deltaCPrime / sC;
            var termH = deltaHPrime / sH;
            return Math.Sqrt(termL * termL + termC * termC + termH * termH + rT * termC * termH);
        }

        private static bool IsBinaryLikeAlpha(IReadOnlyList<Color> reference)
        {
            foreach (var color in reference)
            {
                if (color.a > 0.05f && color.a < 0.95f)
                {
                    return false;
                }
            }

            return true;
        }

        private static double ComputeAlphaIou(IReadOnlyList<Color> reference, IReadOnlyList<Color> restored, float threshold)
        {
            var intersection = 0;
            var union = 0;
            var n = Math.Min(reference.Count, restored.Count);
            for (var i = 0; i < n; i++)
            {
                var a = reference[i].a >= threshold;
                var b = restored[i].a >= threshold;
                if (a && b) intersection++;
                if (a || b) union++;
            }

            return union == 0 ? 1.0 : intersection / (double)union;
        }

        private static double ComputeAlphaRmse(IReadOnlyList<Color> reference, IReadOnlyList<Color> restored)
        {
            var total = 0.0;
            var n = Math.Min(reference.Count, restored.Count);
            for (var i = 0; i < n; i++)
            {
                total += Square(reference[i].a - restored[i].a);
            }

            return Math.Sqrt(total / Math.Max(1, n));
        }

        private static bool IsApproximatelySolid(IReadOnlyList<Color> pixels)
        {
            if (pixels.Count == 0)
            {
                return true;
            }

            var first = pixels[0].linear;
            var alpha = pixels[0].a;
            var maxDeviation = 0.0;
            for (var i = 1; i < pixels.Count; i++)
            {
                var current = pixels[i].linear;
                maxDeviation = Math.Max(maxDeviation, Math.Abs(current.r - first.r));
                maxDeviation = Math.Max(maxDeviation, Math.Abs(current.g - first.g));
                maxDeviation = Math.Max(maxDeviation, Math.Abs(current.b - first.b));
                maxDeviation = Math.Max(maxDeviation, Math.Abs(pixels[i].a - alpha));
                if (maxDeviation > 0.003)
                {
                    return false;
                }
            }

            return true;
        }

        private static Color[] Downsample2x(IReadOnlyList<Color> source, int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
        {
            var result = new Color[targetWidth * targetHeight];
            for (var y = 0; y < targetHeight; y++)
            {
                for (var x = 0; x < targetWidth; x++)
                {
                    var sx = x * 2;
                    var sy = y * 2;
                    var c00 = Sample(source, sourceWidth, sourceHeight, sx, sy);
                    var c10 = Sample(source, sourceWidth, sourceHeight, sx + 1, sy);
                    var c01 = Sample(source, sourceWidth, sourceHeight, sx, sy + 1);
                    var c11 = Sample(source, sourceWidth, sourceHeight, sx + 1, sy + 1);
                    result[y * targetWidth + x] = (c00 + c10 + c01 + c11) * 0.25f;
                }
            }

            return result;
        }

        private static Color Sample(IReadOnlyList<Color> source, int width, int height, int x, int y)
        {
            x = Mathf.Clamp(x, 0, width - 1);
            y = Mathf.Clamp(y, 0, height - 1);
            return source[y * width + x];
        }

        private static Color[] UpscaleBilinear(IReadOnlyList<Color> source, int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
        {
            var result = new Color[targetWidth * targetHeight];
            for (var y = 0; y < targetHeight; y++)
            {
                var v = (y + 0.5f) / targetHeight * sourceHeight - 0.5f;
                var y0 = Mathf.Clamp(Mathf.FloorToInt(v), 0, sourceHeight - 1);
                var y1 = Mathf.Clamp(y0 + 1, 0, sourceHeight - 1);
                var fy = Mathf.Clamp01(v - y0);
                for (var x = 0; x < targetWidth; x++)
                {
                    var u = (x + 0.5f) / targetWidth * sourceWidth - 0.5f;
                    var x0 = Mathf.Clamp(Mathf.FloorToInt(u), 0, sourceWidth - 1);
                    var x1 = Mathf.Clamp(x0 + 1, 0, sourceWidth - 1);
                    var fx = Mathf.Clamp01(u - x0);

                    var c00 = source[y0 * sourceWidth + x0];
                    var c10 = source[y0 * sourceWidth + x1];
                    var c01 = source[y1 * sourceWidth + x0];
                    var c11 = source[y1 * sourceWidth + x1];
                    result[y * targetWidth + x] = Color.Lerp(Color.Lerp(c00, c10, fx), Color.Lerp(c01, c11, fx), fy);
                }
            }

            return result;
        }

        private static Texture2D GetReadableTexture(Texture2D sourceTexture, IDictionary<Texture2D, Texture2D> sourceCache)
        {
            if (sourceCache.TryGetValue(sourceTexture, out var cached))
            {
                return cached;
            }

            var rt = RenderTexture.GetTemporary(sourceTexture.width, sourceTexture.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
            var previous = RenderTexture.active;
            Graphics.Blit(sourceTexture, rt);
            RenderTexture.active = rt;
            var readable = new Texture2D(sourceTexture.width, sourceTexture.height, TextureFormat.RGBA32, false, false);
            readable.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            readable.Apply(false, false);
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(rt);
            sourceCache[sourceTexture] = readable;
            return readable;
        }

        private static Vector3 DecodeNormal(Color color)
        {
            var vector = new Vector3(color.r * 2.0f - 1.0f, color.g * 2.0f - 1.0f, color.b * 2.0f - 1.0f);
            return vector.sqrMagnitude < 0.000001f ? Vector3.forward : vector.normalized;
        }

        private static Vector3 RgbToLab(Color color)
        {
            var linear = color.linear;
            var r = PivotRgb(linear.r);
            var g = PivotRgb(linear.g);
            var b = PivotRgb(linear.b);

            var x = r * 0.4124564 + g * 0.3575761 + b * 0.1804375;
            var y = r * 0.2126729 + g * 0.7151522 + b * 0.0721750;
            var z = r * 0.0193339 + g * 0.1191920 + b * 0.9503041;

            x /= 0.95047;
            z /= 1.08883;
            var fx = PivotXyz(x);
            var fy = PivotXyz(y);
            var fz = PivotXyz(z);

            return new Vector3(
                (float)(116 * fy - 16),
                (float)(500 * (fx - fy)),
                (float)(200 * (fy - fz)));
        }

        private static double PivotRgb(double value)
        {
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        private static double PivotXyz(double value)
        {
            const double epsilon = 216.0 / 24389.0;
            const double kappa = 24389.0 / 27.0;
            return value > epsilon ? Math.Pow(value, 1.0 / 3.0) : (kappa * value + 16.0) / 116.0;
        }

        private static double Luminance(Color color)
        {
            var linear = color.linear;
            return 0.2126 * linear.r + 0.7152 * linear.g + 0.0722 * linear.b;
        }

        private static int InferSquareSize(int count)
        {
            return Mathf.Max(1, Mathf.RoundToInt(Mathf.Sqrt(count)));
        }

        private static double ToHueAngle(double radians)
        {
            var degrees = radians * 180.0 / Math.PI;
            while (degrees < 0) degrees += 360.0;
            while (degrees >= 360.0) degrees -= 360.0;
            return degrees;
        }

        private static double DeltaHue(double h1, double h2, double c1, double c2)
        {
            if (c1 * c2 == 0) return 0;
            var delta = h2 - h1;
            if (Math.Abs(delta) <= 180) return delta;
            return delta > 180 ? delta - 360 : delta + 360;
        }

        private static double AverageHue(double h1, double h2, double c1, double c2)
        {
            if (c1 * c2 == 0) return h1 + h2;
            if (Math.Abs(h1 - h2) <= 180) return (h1 + h2) * 0.5;
            return (h1 + h2 + (h1 + h2 < 360 ? 360 : -360)) * 0.5;
        }

        private static double DegToRad(double degrees) => degrees * Math.PI / 180.0;
        private static double Square(double value) => value * value;
    }
}
