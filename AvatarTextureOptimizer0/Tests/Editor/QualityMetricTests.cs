using Fosa.AvatarTextureOptimizer.Editor.Analysis;
using Fosa.AvatarTextureOptimizer.Editor.Quality;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;

namespace Fosa.AvatarTextureOptimizer.Tests
{
    public sealed class QualityMetricTests
    {
        [Test]
        public void IdenticalPixelsProducePerfectMetrics()
        {
            var pixels = new NativeArray<float4>(64, Allocator.TempJob);
            var mask = new NativeArray<byte>(16, Allocator.TempJob);
            try
            {
                for (var i = 0; i < pixels.Length; i++) pixels[i] = new float4(0.2f, 0.4f, 0.1f, 1f);
                for (var i = 0; i < mask.Length; i++) mask[i] = 0x0f;
                var binding = new TextureBindingRecord { Kind = ATOTextureKind.ColorOpaque, Cutoff = 0.5f };
                var metrics = QualityMetricEvaluator.Evaluate(pixels, pixels, mask, 8, 8, binding, 0.5f);
                Assert.That(metrics.Ssim, Is.EqualTo(1f).Within(1e-5f));
                Assert.That(metrics.MsSsim, Is.EqualTo(1f).Within(1e-5f));
                Assert.That(metrics.DeltaE2000, Is.EqualTo(0f).Within(1e-5f));
            }
            finally { pixels.Dispose(); mask.Dispose(); }
        }

        [Test]
        public void MetricThresholdBoundariesAreInclusiveAndOneSideFails()
        {
            var settings = new ATOQualitySettings
            {
                minSsim = 0.9f, minMsSsim = 0.8f, maxDeltaE2000 = 2f,
                minCutoutIoU = 0.7f, maxBlendAlphaRmse = 0.1f,
                maxNormalMeanDegrees = 3f, maxNormalP95Degrees = 5f, maxGrayscaleRmse = 0.2f
            };
            var color = new TextureBindingRecord
                { Kind = ATOTextureKind.ColorAlpha, EvaluateCutout = true, EvaluateBlend = true };
            Assert.IsTrue(new QualityMetrics(0.9f, 0.8f, 2f, 0.7f, 0.1f, 0f, 0f, 0f)
                .Passes(settings, color));
            Assert.IsFalse(new QualityMetrics(0.899f, 0.8f, 2f, 0.7f, 0.1f, 0f, 0f, 0f)
                .Passes(settings, color));
            Assert.IsFalse(new QualityMetrics(0.9f, 0.8f, 2.001f, 0.7f, 0.1f, 0f, 0f, 0f)
                .Passes(settings, color));
            Assert.IsFalse(new QualityMetrics(0.9f, 0.8f, 2f, 0.699f, 0.1f, 0f, 0f, 0f)
                .Passes(settings, color));

            var normal = new TextureBindingRecord { Kind = ATOTextureKind.Normal };
            Assert.IsTrue(new QualityMetrics(1f, 1f, 0f, 1f, 0f, 3f, 5f, 0f)
                .Passes(settings, normal));
            Assert.IsFalse(new QualityMetrics(1f, 1f, 0f, 1f, 0f, 3.001f, 5f, 0f)
                .Passes(settings, normal));

            var gray = new TextureBindingRecord { Kind = ATOTextureKind.Grayscale };
            Assert.IsTrue(new QualityMetrics(1f, 1f, 0f, 1f, 0f, 0f, 0f, 0.2f)
                .Passes(settings, gray));
            Assert.IsFalse(new QualityMetrics(1f, 1f, 0f, 1f, 0f, 0f, 0f, 0.201f)
                .Passes(settings, gray));
        }

        [Test]
        public void NonFiniteMetricNeverPassesThresholds()
        {
            var metrics = new QualityMetrics(float.NaN, 1f, 0f, 1f, 0f, 0f, 0f, 0f);
            var settings = new ATOQualitySettings();
            var binding = new TextureBindingRecord { Kind = ATOTextureKind.ColorOpaque };
            Assert.IsFalse(metrics.Passes(settings, binding));
        }

        [Test]
        public void InvalidDimensionsMaskOrCutoffReturnFailClosedMetrics()
        {
            var pixels = new NativeArray<float4>(4, Allocator.TempJob, NativeArrayOptions.ClearMemory);
            var shortMask = new NativeArray<byte>(0, Allocator.TempJob);
            var validMask = new NativeArray<byte>(1, Allocator.TempJob, NativeArrayOptions.ClearMemory);
            var oversizedMask = new NativeArray<byte>(2, Allocator.TempJob, NativeArrayOptions.ClearMemory);
            try
            {
                var opaque = new TextureBindingRecord { Kind = ATOTextureKind.ColorOpaque };
                Assert.That(QualityMetricEvaluator.Evaluate(pixels, pixels, shortMask, 2, 2, opaque, 0.5f).Ssim,
                    Is.NaN);
                Assert.That(QualityMetricEvaluator.Evaluate(pixels, pixels, oversizedMask, 2, 2, opaque, 0.5f).Ssim,
                    Is.NaN);
                Assert.That(QualityMetricEvaluator.Evaluate(pixels, pixels, validMask, 3, 2, opaque, 0.5f).Ssim,
                    Is.NaN);
                Assert.That(QualityMetricEvaluator.Evaluate(pixels, pixels, validMask, 2, 2, opaque, 0.5f).Ssim,
                    Is.NaN, "a quality surface without any covered island pixel must fail closed");
                validMask[0] = 0x01;
                var cutout = new TextureBindingRecord
                    { Kind = ATOTextureKind.ColorAlpha, EvaluateCutout = true };
                Assert.That(QualityMetricEvaluator.Evaluate(pixels, pixels, validMask, 2, 2, cutout,
                    float.NaN).CutoutIoU, Is.NaN);
            }
            finally
            {
                pixels.Dispose(); shortMask.Dispose(); validMask.Dispose(); oversizedMask.Dispose();
            }
        }

        [Test]
        public void MaskWithOnlyUnusedHighBitsFailsClosed()
        {
            var pixels = new NativeArray<float4>(1, Allocator.TempJob, NativeArrayOptions.ClearMemory);
            var mask = new NativeArray<byte>(1, Allocator.TempJob, NativeArrayOptions.ClearMemory);
            try
            {
                mask[0] = 0x08; // One pixel uses only bit 0; bit 3 is padding and must not count as coverage.
                var binding = new TextureBindingRecord { Kind = ATOTextureKind.ColorOpaque };

                var metrics = QualityMetricEvaluator.Evaluate(pixels, pixels, mask, 1, 1, binding, 0.5f);

                Assert.That(metrics.Ssim, Is.NaN);
                Assert.That(metrics.MsSsim, Is.NaN);
                Assert.That(metrics.DeltaE2000, Is.NaN);
                Assert.That(metrics.CutoutIoU, Is.NaN);
                Assert.That(metrics.BlendAlphaRmse, Is.NaN);
                Assert.That(metrics.NormalMeanDegrees, Is.NaN);
                Assert.That(metrics.NormalP95Degrees, Is.NaN);
                Assert.That(metrics.GrayscaleRmse, Is.NaN);
            }
            finally
            {
                pixels.Dispose(); mask.Dispose();
            }
        }

        [Test]
        public void Ciede2000MatchesPublishedSharmaReferencePairs()
        {
            Assert.That(QualityMetricEvaluator.DeltaE2000Lab(
                    new float3(50f, 2.6772f, -79.7751f), new float3(50f, 0f, -82.7485f)),
                Is.EqualTo(2.0425f).Within(0.0001f));
            Assert.That(QualityMetricEvaluator.DeltaE2000Lab(
                    new float3(50f, 3.1571f, -77.2803f), new float3(50f, 0f, -82.7485f)),
                Is.EqualTo(2.8615f).Within(0.0001f));
            Assert.That(QualityMetricEvaluator.DeltaE2000Lab(
                    new float3(50f, 2.8361f, -74.0200f), new float3(50f, 0f, -82.7485f)),
                Is.EqualTo(3.4412f).Within(0.0001f));
        }

        [Test]
        public void PackedRgbaDataMeasuresAlphaAsAnIndependentChannel()
        {
            var reference = new NativeArray<float4>(4, Allocator.TempJob);
            var candidate = new NativeArray<float4>(4, Allocator.TempJob);
            var mask = new NativeArray<byte>(1, Allocator.TempJob);
            try
            {
                for (var i = 0; i < 4; i++)
                {
                    reference[i] = new float4(0.25f, 0.5f, 0.75f, 0.2f);
                    candidate[i] = new float4(0.25f, 0.5f, 0.75f, 0.3f);
                }
                mask[0] = 0x0f;
                var binding = new TextureBindingRecord
                    { Kind = ATOTextureKind.ColorRgbaData, EvaluatePackedChannels = true };
                var metrics = QualityMetricEvaluator.Evaluate(reference, candidate, mask, 2, 2, binding, 0.5f);
                Assert.That(metrics.GrayscaleRmse, Is.EqualTo(0.1f).Within(1e-4f));
                var settings = new ATOQualitySettings
                {
                    minSsim = 0f, minMsSsim = 0f, maxDeltaE2000 = 100f, maxGrayscaleRmse = 0.01f
                };
                Assert.That(metrics.Passes(settings, binding), Is.False);
            }
            finally { reference.Dispose(); candidate.Dispose(); mask.Dispose(); }
        }

        [Test]
        public void GrayscaleRmseUsesOnlyAuditedShaderChannels()
        {
            var reference = new NativeArray<float4>(4, Allocator.TempJob);
            var candidate = new NativeArray<float4>(4, Allocator.TempJob);
            var mask = new NativeArray<byte>(1, Allocator.TempJob);
            try
            {
                for (var i = 0; i < 4; i++)
                {
                    reference[i] = new float4(0.25f, 0.1f, 0.2f, 0.3f);
                    candidate[i] = new float4(0.25f, 0.9f, 0.8f, 1f);
                }
                mask[0] = 0x0f;
                var red = new TextureBindingRecord
                    { Kind = ATOTextureKind.Grayscale, UsedChannels = ATOTextureChannels.R };
                var redMetrics = QualityMetricEvaluator.Evaluate(reference, candidate, mask, 2, 2, red, 0.5f);
                Assert.That(redMetrics.GrayscaleRmse, Is.EqualTo(0f).Within(1e-6f));

                var green = new TextureBindingRecord
                    { Kind = ATOTextureKind.Grayscale, UsedChannels = ATOTextureChannels.G };
                var greenMetrics = QualityMetricEvaluator.Evaluate(reference, candidate, mask, 2, 2, green, 0.5f);
                Assert.That(greenMetrics.GrayscaleRmse, Is.EqualTo(0.8f).Within(1e-6f));
            }
            finally { reference.Dispose(); candidate.Dispose(); mask.Dispose(); }
        }

        [Test]
        public void CutoutAndBlendMetricsUseTheActualAlphaChannelAndCutoff()
        {
            var reference = new NativeArray<float4>(4, Allocator.TempJob);
            var candidate = new NativeArray<float4>(4, Allocator.TempJob);
            var mask = new NativeArray<byte>(1, Allocator.TempJob);
            try
            {
                reference[0] = new float4(0f, 0f, 0f, 0.6f);
                reference[1] = new float4(0f, 0f, 0f, 0.6f);
                candidate[0] = new float4(0f, 0f, 0f, 0.6f);
                candidate[1] = new float4(0f, 0f, 0f, 0.4f);
                reference[2] = candidate[2] = new float4(0f, 0f, 0f, 0.4f);
                reference[3] = candidate[3] = new float4(0f, 0f, 0f, 0.4f);
                mask[0] = 0x0f;
                var binding = new TextureBindingRecord
                    { Kind = ATOTextureKind.ColorAlpha, EvaluateCutout = true, EvaluateBlend = true };
                var metrics = QualityMetricEvaluator.Evaluate(reference, candidate, mask, 2, 2, binding, 0.5f);
                Assert.That(metrics.CutoutIoU, Is.EqualTo(0.5f).Within(1e-5f));
                Assert.That(metrics.BlendAlphaRmse, Is.EqualTo(0.1f).Within(1e-5f));
            }
            finally { reference.Dispose(); candidate.Dispose(); mask.Dispose(); }
        }

        [Test]
        public void AnimatedCutoffRangeChecksInteriorAlphaBreakpoints()
        {
            var reference = new NativeArray<float4>(1, Allocator.TempJob);
            var candidate = new NativeArray<float4>(1, Allocator.TempJob);
            var mask = new NativeArray<byte>(1, Allocator.TempJob);
            try
            {
                reference[0] = new float4(0f, 0f, 0f, 0.4f);
                candidate[0] = new float4(0f, 0f, 0f, 0.6f);
                mask[0] = 0x01;
                var binding = new TextureBindingRecord
                {
                    Kind = ATOTextureKind.ColorAlpha,
                    EvaluateCutout = true,
                    Cutoff = 0.2f,
                    Cutoffs = new[] { 0.2f, 0.8f }
                };

                Assert.That(QualityMetricEvaluator.Evaluate(reference, candidate, mask, 1, 1, binding, 0.2f).CutoutIoU,
                    Is.EqualTo(1f));
                Assert.That(QualityMetricEvaluator.Evaluate(reference, candidate, mask, 1, 1, binding, 0.8f).CutoutIoU,
                    Is.EqualTo(1f));
                Assert.That(QualityMetricEvaluator.EvaluateForBinding(reference, candidate, mask, 1, 1, binding).CutoutIoU,
                    Is.EqualTo(0f), "the continuously animated interval includes thresholds between the two alpha values");
            }
            finally { reference.Dispose(); candidate.Dispose(); mask.Dispose(); }
        }

        [Test]
        public void AnimatedCutoffMaximumEndpointUsesInclusiveAlphaComparison()
        {
            var reference = new NativeArray<float4>(1, Allocator.TempJob);
            var candidate = new NativeArray<float4>(1, Allocator.TempJob);
            var mask = new NativeArray<byte>(1, Allocator.TempJob);
            try
            {
                reference[0] = new float4(0f, 0f, 0f, 0.6f);
                candidate[0] = new float4(0f, 0f, 0f, 0.4f);
                mask[0] = 0x01;
                Assert.That(QualityMetricEvaluator.EvaluateWorstCutoutIoU(reference, candidate, mask, 0.2f, 0.6f),
                    Is.EqualTo(0f), "alpha equal to the maximum cutoff remains visible while the lower alpha is hidden");
            }
            finally { reference.Dispose(); candidate.Dispose(); mask.Dispose(); }
        }

        [Test]
        public void AnimatedCutoffEmptyUnionIsPerfectAgreement()
        {
            var reference = new NativeArray<float4>(1, Allocator.TempJob);
            var candidate = new NativeArray<float4>(1, Allocator.TempJob);
            var mask = new NativeArray<byte>(1, Allocator.TempJob);
            try
            {
                reference[0] = new float4(0f, 0f, 0f, 0.1f);
                candidate[0] = new float4(0f, 0f, 0f, 0.2f);
                mask[0] = 0x01;
                Assert.That(QualityMetricEvaluator.EvaluateWorstCutoutIoU(reference, candidate, mask, 0.8f, 1f),
                    Is.EqualTo(1f));
                mask[0] = 0;
                Assert.That(QualityMetricEvaluator.EvaluateWorstCutoutIoU(reference, candidate, mask, 0f, 1f),
                    Is.EqualTo(1f), "two empty silhouettes use the conventional IoU value of one");
            }
            finally { reference.Dispose(); candidate.Dispose(); mask.Dispose(); }
        }

        [Test]
        public void AnimatedCutoffRejectsNonFiniteCoveredAlphaAndRange()
        {
            var reference = new NativeArray<float4>(1, Allocator.TempJob);
            var candidate = new NativeArray<float4>(1, Allocator.TempJob);
            var mask = new NativeArray<byte>(1, Allocator.TempJob);
            try
            {
                reference[0] = new float4(0f, 0f, 0f, float.NaN);
                candidate[0] = new float4(0f, 0f, 0f, 0.5f);
                mask[0] = 0x01;
                Assert.That(QualityMetricEvaluator.EvaluateWorstCutoutIoU(reference, candidate, mask, 0f, 1f),
                    Is.NaN);
                reference[0] = new float4(0f, 0f, 0f, 0.5f);
                Assert.That(QualityMetricEvaluator.EvaluateWorstCutoutIoU(reference, candidate, mask,
                    float.NegativeInfinity, 1f), Is.NaN);
            }
            finally { reference.Dispose(); candidate.Dispose(); mask.Dispose(); }
        }

        [Test]
        public void NormalP95IncludesTheWorstFivePercentOfCoveredPixels()
        {
            var reference = new NativeArray<float4>(64, Allocator.TempJob);
            var candidate = new NativeArray<float4>(64, Allocator.TempJob);
            var mask = new NativeArray<byte>(16, Allocator.TempJob);
            try
            {
                var forward = new float4(0.5f, 0.5f, 1f, 1f);
                var tilted = new float4(0.5f + 0.5f * 0.8660254f, 0.5f, 0.75f, 1f);
                for (var i = 0; i < 64; i++)
                {
                    reference[i] = forward;
                    candidate[i] = i < 60 ? forward : tilted;
                }
                for (var i = 0; i < mask.Length; i++) mask[i] = 0x0f;
                var binding = new TextureBindingRecord { Kind = ATOTextureKind.Normal };
                var metrics = QualityMetricEvaluator.Evaluate(reference, candidate, mask, 8, 8, binding, 0.5f);
                Assert.That(metrics.NormalMeanDegrees, Is.EqualTo(3.75f).Within(0.05f));
                Assert.That(metrics.NormalP95Degrees, Is.EqualTo(60f).Within(0.05f));
            }
            finally { reference.Dispose(); candidate.Dispose(); mask.Dispose(); }
        }

        [Test]
        public void OddFinalRowAndColumnContributeToMsSsim()
        {
            var reference = new NativeArray<float4>(9, Allocator.TempJob);
            var candidate = new NativeArray<float4>(9, Allocator.TempJob);
            var mask = new NativeArray<byte>(3, Allocator.TempJob, NativeArrayOptions.ClearMemory);
            try
            {
                reference[8] = new float4(1f, 1f, 1f, 1f);
                candidate[8] = new float4(0f, 0f, 0f, 1f);
                mask[2] = 0x01; // Only pixel (2,2), which floor division used to discard.
                var binding = new TextureBindingRecord { Kind = ATOTextureKind.ColorOpaque };
                var metrics = QualityMetricEvaluator.Evaluate(reference, candidate, mask, 3, 3, binding, 0.5f);
                Assert.Less(metrics.MsSsim, 0.99f);
            }
            finally
            {
                reference.Dispose(); candidate.Dispose(); mask.Dispose();
            }
        }
    }
}
