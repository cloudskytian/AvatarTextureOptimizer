using System;
using System.Collections.Generic;
using System.Linq;
using Fosa.AvatarTextureOptimizer.Editor.Analysis;
using Fosa.AvatarTextureOptimizer.Editor.Pipeline;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Editor.Quality
{
    internal readonly struct QualityMetrics
    {
        public readonly float Ssim, MsSsim, DeltaE2000, CutoutIoU, BlendAlphaRmse;
        public readonly float NormalMeanDegrees, NormalP95Degrees, GrayscaleRmse;
        public QualityMetrics(float ssim, float msSsim, float deltaE, float iou, float alphaRmse,
            float normalMean, float normalP95, float grayscaleRmse)
        {
            Ssim = ssim; MsSsim = msSsim; DeltaE2000 = deltaE; CutoutIoU = iou; BlendAlphaRmse = alphaRmse;
            NormalMeanDegrees = normalMean; NormalP95Degrees = normalP95; GrayscaleRmse = grayscaleRmse;
        }

        public bool Passes(ATOQualitySettings settings, TextureBindingRecord binding)
        {
            // NaN comparisons are false in C#, so every metric must be validated before threshold checks.
            // C# 中 NaN 的大小比较均为 false，必须先显式拒绝非有限指标，避免异常数据被误判为通过。
            if (!Finite(Ssim) || !Finite(MsSsim) || !Finite(DeltaE2000) || !Finite(CutoutIoU) ||
                !Finite(BlendAlphaRmse) || !Finite(NormalMeanDegrees) || !Finite(NormalP95Degrees) ||
                !Finite(GrayscaleRmse)) return false;
            if (Ssim < settings.minSsim || MsSsim < settings.minMsSsim) return false;
            if ((binding.Kind == ATOTextureKind.ColorOpaque || binding.Kind == ATOTextureKind.ColorAlpha ||
                 binding.Kind == ATOTextureKind.ColorRgbaData) && DeltaE2000 > settings.maxDeltaE2000) return false;
            if (binding.EvaluateCutout && CutoutIoU < settings.minCutoutIoU) return false;
            if (binding.EvaluateBlend && BlendAlphaRmse > settings.maxBlendAlphaRmse) return false;
            if (binding.Kind == ATOTextureKind.Normal &&
                (NormalMeanDegrees > settings.maxNormalMeanDegrees || NormalP95Degrees > settings.maxNormalP95Degrees)) return false;
            return binding.Kind != ATOTextureKind.Grayscale && !binding.EvaluatePackedChannels ||
                   GrayscaleRmse <= settings.maxGrayscaleRmse;
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }

    internal static class QualityMetricEvaluator
    {
        private static readonly float[] MsWeights = { 0.0448f, 0.2856f, 0.3001f, 0.2363f, 0.1333f };

        public static QualityMetrics EvaluateForBinding(NativeArray<float4> reference, NativeArray<float4> candidate,
            NativeArray<byte> mask, int width, int height, TextureBindingRecord binding)
        {
            if (binding == null) throw new ArgumentNullException(nameof(binding));
            var cutoff = binding.Cutoff;
            if (binding.EvaluateCutout && binding.Cutoffs != null && binding.Cutoffs.Length > 0)
                cutoff = binding.Cutoffs[0];
            var metrics = Evaluate(reference, candidate, mask, width, height, binding, cutoff);
            if (!binding.EvaluateCutout || binding.Cutoffs == null || binding.Cutoffs.Length <= 1) return metrics;
            var minimum = binding.Cutoffs.Min(); var maximum = binding.Cutoffs.Max();
            var worstIoU = EvaluateWorstCutoutIoU(reference, candidate, mask, minimum, maximum);
            return new QualityMetrics(metrics.Ssim, metrics.MsSsim, metrics.DeltaE2000, worstIoU,
                metrics.BlendAlphaRmse, metrics.NormalMeanDegrees, metrics.NormalP95Degrees,
                metrics.GrayscaleRmse);
        }

        public static QualityMetrics Evaluate(NativeArray<float4> reference, NativeArray<float4> candidate,
            NativeArray<byte> mask, int width, int height, TextureBindingRecord binding, float cutoff)
        {
            if (binding == null) throw new ArgumentNullException(nameof(binding));
            if (!ValidInput(reference, candidate, mask, width, height) ||
                binding.EvaluateCutout && !Finite(cutoff)) return InvalidMetrics();
            var baseMetrics = EvaluateLevel(reference, candidate, mask, width, height, cutoff, true,
                binding.Kind == ATOTextureKind.Normal, binding.Kind == ATOTextureKind.ColorAlpha);
            var msSsim = math.pow(math.max(baseMetrics.ContrastStructure, 1e-6f), MsWeights[0]);
            var a = reference; var b = candidate; var currentMask = mask;
            var currentMetrics = baseMetrics;
            var owns = false;
            try
            {
                for (var level = 1; level < MsWeights.Length; level++)
                {
                    if (width <= 1 || height <= 1)
                    {
                        var terminal = level == MsWeights.Length - 1 ? currentMetrics.Ssim : currentMetrics.ContrastStructure;
                        msSsim *= math.pow(math.max(terminal, 1e-6f), MsWeights[level]); continue;
                    }
                    // Use ceil division so an odd final column/row (and its mask coverage) is not discarded.
                    var nextWidth = math.max(1, (width + 1) / 2); var nextHeight = math.max(1, (height + 1) / 2);
                    NativeArray<float4> nextA = default, nextB = default; NativeArray<byte> nextMask = default;
                    try
                    {
                        nextA = new NativeArray<float4>(nextWidth * nextHeight, Allocator.TempJob);
                        nextB = new NativeArray<float4>(nextWidth * nextHeight, Allocator.TempJob);
                        nextMask = new NativeArray<byte>((nextWidth * nextHeight + 3) / 4, Allocator.TempJob, NativeArrayOptions.ClearMemory);
                        const int cancellableDownsampleBatch = 65536;
                        var downsampleLength = nextWidth * nextHeight;
                        for (var start = 0; start < downsampleLength; start += cancellableDownsampleBatch)
                        {
                            ATOProgress.Checkpoint("Downsampling quality metric level " + level);
                            var count = math.min(cancellableDownsampleBatch, downsampleLength - start);
                            new DownsampleJob { SourceA = a, SourceB = b, SourceMask = currentMask, SourceWidth = width,
                                SourceHeight = height, DestinationA = nextA, DestinationB = nextB,
                                DestinationMask = nextMask, DestinationWidth = nextWidth, StartIndex = start }
                                .Schedule(count, 64).Complete();
                        }
                        ATOProgress.Checkpoint("Completed quality metric level " + level);
                    }
                    catch
                    {
                        if (nextA.IsCreated) nextA.Dispose(); if (nextB.IsCreated) nextB.Dispose();
                        if (nextMask.IsCreated) nextMask.Dispose(); throw;
                    }
                    if (owns) { a.Dispose(); b.Dispose(); currentMask.Dispose(); }
                    a = nextA; b = nextB; currentMask = nextMask; owns = true; width = nextWidth; height = nextHeight;
                    var levelMetrics = EvaluateLevel(a, b, currentMask, width, height, cutoff, false, false, false);
                    currentMetrics = levelMetrics;
                    var component = level == MsWeights.Length - 1 ? currentMetrics.Ssim : currentMetrics.ContrastStructure;
                    msSsim *= math.pow(math.max(component, 1e-6f), MsWeights[level]);
                }
            }
            finally { if (owns) { a.Dispose(); b.Dispose(); currentMask.Dispose(); } }
            return new QualityMetrics(baseMetrics.Ssim, msSsim, baseMetrics.DeltaE, baseMetrics.IoU,
                math.sqrt(baseMetrics.AlphaSquared / math.max(1, baseMetrics.PixelCount)),
                baseMetrics.NormalMean, baseMetrics.NormalP95,
                math.sqrt(SelectedChannelSquared(baseMetrics.ChannelSquared, binding.UsedChannels) /
                          math.max(1, baseMetrics.PixelCount)));
        }

        internal static float EvaluateWorstCutoutIoU(NativeArray<float4> reference,
            NativeArray<float4> candidate, NativeArray<byte> mask, float minimumCutoff, float maximumCutoff)
        {
            if (!reference.IsCreated || !candidate.IsCreated || !mask.IsCreated ||
                reference.Length != candidate.Length || mask.Length != (reference.Length + 3L) / 4L ||
                !Finite(minimumCutoff) || !Finite(maximumCutoff)) return float.NaN;
            if (minimumCutoff > maximumCutoff)
            {
                var swap = minimumCutoff; minimumCutoff = maximumCutoff; maximumCutoff = swap;
            }

            // For one covered pixel, intersection at threshold t means min(alphaA, alphaB) >= t, while union
            // means max(alphaA, alphaB) >= t. Sweeping the two sorted breakpoint lists therefore evaluates every
            // constant-IoU interval in the continuous animated cutoff range, not merely curve extrema.
            // 对每个覆盖像素，交集由较小 Alpha、并集由较大 Alpha 决定；扫描断点可精确覆盖连续 Cutoff 区间。
            var lower = new List<float>(); var upper = new List<float>();
            for (var index = 0; index < reference.Length; index++)
            {
                if ((index & 65535) == 0) ATOProgress.Checkpoint("Collecting animated-cutoff breakpoints");
                if (!IslandMaskRasterizer.IsSet(mask, index)) continue;
                var first = reference[index].w; var second = candidate[index].w;
                if (!Finite(first) || !Finite(second)) return float.NaN;
                lower.Add(math.min(first, second)); upper.Add(math.max(first, second));
            }
            ATOProgress.Checkpoint("Sorting animated-cutoff breakpoints");
            lower.Sort(); upper.Sort();
            var lowerIndex = FirstAtLeast(lower, minimumCutoff);
            var upperIndex = FirstAtLeast(upper, minimumCutoff);
            var lowerActive = lower.Count - lowerIndex; var upperActive = upper.Count - upperIndex;
            var worst = IoU(lowerActive, upperActive);
            while (lowerIndex < lower.Count || upperIndex < upper.Count)
            {
                ATOProgress.Checkpoint("Sweeping animated-cutoff breakpoints");
                var nextLower = lowerIndex < lower.Count ? lower[lowerIndex] : float.PositiveInfinity;
                var nextUpper = upperIndex < upper.Count ? upper[upperIndex] : float.PositiveInfinity;
                var breakpoint = math.min(nextLower, nextUpper);
                // At t == breakpoint values equal to t are still visible (alpha >= cutoff). Only the interval
                // immediately above a breakpoint changes, and it exists inside the requested range iff t < max.
                if (breakpoint >= maximumCutoff) break;
                while (lowerIndex < lower.Count && lower[lowerIndex] <= breakpoint) lowerIndex++;
                while (upperIndex < upper.Count && upper[upperIndex] <= breakpoint) upperIndex++;
                lowerActive = lower.Count - lowerIndex; upperActive = upper.Count - upperIndex;
                worst = math.min(worst, IoU(lowerActive, upperActive));
            }
            return worst;
        }

        private static int FirstAtLeast(List<float> values, float threshold)
        {
            var low = 0; var high = values.Count;
            while (low < high)
            {
                var middle = low + (high - low) / 2;
                if (values[middle] < threshold) low = middle + 1; else high = middle;
            }
            return low;
        }

        private static float IoU(int intersection, int union) => union == 0 ? 1f : (float)intersection / union;
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        private static bool ValidInput(NativeArray<float4> reference, NativeArray<float4> candidate,
            NativeArray<byte> mask, int width, int height)
        {
            if (!reference.IsCreated || !candidate.IsCreated || !mask.IsCreated || width <= 0 || height <= 0)
                return false;
            var pixels = (long)width * height;
            if (pixels > int.MaxValue || reference.Length != pixels || candidate.Length != pixels ||
                mask.Length != (pixels + 3L) / 4L) return false;
            var completeBytes = (int)(pixels / 4L);
            for (var index = 0; index < completeBytes; index++)
                if ((mask[index] & 0x0f) != 0) return true;
            var remainder = (int)(pixels & 3L);
            return remainder > 0 && (mask[completeBytes] & ((1 << remainder) - 1)) != 0;
        }

        private static QualityMetrics InvalidMetrics() => new QualityMetrics(float.NaN, float.NaN, float.NaN,
            float.NaN, float.NaN, float.NaN, float.NaN, float.NaN);

        internal static float DeltaE2000Lab(float3 first, float3 second) =>
            MetricBlockJob.DeltaE2000(first, second);

        private static float SelectedChannelSquared(float4 squared, ATOTextureChannels channels)
        {
            if (channels == ATOTextureChannels.None) channels = ATOTextureChannels.Rgba;
            var maximum = 0f;
            if ((channels & ATOTextureChannels.R) != 0) maximum = math.max(maximum, squared.x);
            if ((channels & ATOTextureChannels.G) != 0) maximum = math.max(maximum, squared.y);
            if ((channels & ATOTextureChannels.B) != 0) maximum = math.max(maximum, squared.z);
            if ((channels & ATOTextureChannels.A) != 0) maximum = math.max(maximum, squared.w);
            return maximum;
        }

        private static LevelMetrics EvaluateLevel(NativeArray<float4> reference, NativeArray<float4> candidate,
            NativeArray<byte> mask, int width, int height, float cutoff, bool detailed, bool calculateNormal,
            bool premultipliedColor)
        {
            const int blockSize = 8;
            var blocksX = (width + blockSize - 1) / blockSize; var blocksY = (height + blockSize - 1) / blockSize;
            NativeArray<BlockResult> blocks = default; NativeArray<float> angles = default;
            try
            {
                blocks = new NativeArray<BlockResult>(blocksX * blocksY, Allocator.TempJob);
                angles = new NativeArray<float>(detailed && calculateNormal ? width * height : 1, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
                const int cancellableBlockBatch = 1024;
                for (var start = 0; start < blocks.Length; start += cancellableBlockBatch)
                {
                    ATOProgress.Checkpoint("Computing texture quality metrics");
                    var count = math.min(cancellableBlockBatch, blocks.Length - start);
                    new MetricBlockJob { Reference = reference, Candidate = candidate, Mask = mask, Width = width, Height = height,
                        BlocksX = blocksX, Cutoff = cutoff, Detailed = detailed, CalculateNormal = calculateNormal,
                        PremultipliedColor = premultipliedColor, StartIndex = start, Results = blocks, NormalAngles = angles }
                        .Schedule(count, 16).Complete();
                }
                double ssim = 0, contrastStructure = 0, deltaE = 0, alpha = 0, channelR = 0, channelG = 0,
                    channelB = 0, channelA = 0, normal = 0;
                long pixels = 0, intersection = 0, union = 0, normalCount = 0;
                for (var i = 0; i < blocks.Length; i++)
                {
                    if ((i & 65535) == 0) ATOProgress.Checkpoint("Aggregating texture quality metrics");
                    var value = blocks[i];
                    // Edge blocks can contain far fewer covered pixels than interior blocks. Weighting every block equally
                    // would let a one-pixel sliver dominate an entire 8x8 region.
                    if (value.Count > 0)
                    {
                        ssim += value.Ssim * value.Count;
                        contrastStructure += value.ContrastStructure * value.Count;
                    }
                    pixels += value.Count; deltaE += value.DeltaE; alpha += value.AlphaSquared;
                    channelR += value.ChannelSquared.x; channelG += value.ChannelSquared.y;
                    channelB += value.ChannelSquared.z; channelA += value.ChannelSquared.w;
                    intersection += value.Intersection; union += value.Union;
                    normal += value.NormalDegrees; normalCount += value.NormalCount;
                }
                var p95 = 0f;
                if (detailed && calculateNormal && normalCount > 0)
                {
                    var values = new List<float>((int)normalCount);
                    for (var i = 0; i < angles.Length; i++)
                    {
                        if ((i & 65535) == 0) ATOProgress.Checkpoint("Collecting normal-angle statistics");
                        if (angles[i] >= 0f) values.Add(angles[i]);
                    }
                    ATOProgress.Checkpoint("Sorting normal-angle statistics");
                    values.Sort();
                    ATOProgress.Checkpoint("Completed normal-angle statistics");
                    p95 = values[Mathf.Clamp(Mathf.CeilToInt(values.Count * 0.95f) - 1, 0, values.Count - 1)];
                }
                return new LevelMetrics
                {
                    Ssim = pixels == 0 ? 1f : (float)(ssim / pixels),
                    ContrastStructure = pixels == 0 ? 1f : (float)(contrastStructure / pixels), PixelCount = pixels,
                    DeltaE = pixels == 0 ? 0f : (float)(deltaE / pixels), AlphaSquared = (float)alpha,
                    ChannelSquared = new float4((float)channelR, (float)channelG, (float)channelB, (float)channelA),
                    IoU = union == 0 ? 1f : (float)intersection / union,
                    NormalMean = normalCount == 0 ? 0f : (float)(normal / normalCount), NormalP95 = p95
                };
            }
            finally { if (blocks.IsCreated) blocks.Dispose(); if (angles.IsCreated) angles.Dispose(); }
        }

        private struct LevelMetrics
        {
            public float Ssim, ContrastStructure, DeltaE, AlphaSquared, IoU, NormalMean, NormalP95;
            public float4 ChannelSquared;
            public long PixelCount;
        }

        private struct BlockResult
        {
            public float Ssim, ContrastStructure, DeltaE, AlphaSquared, NormalDegrees;
            public float4 ChannelSquared;
            public int Count, Intersection, Union, NormalCount;
        }

        [BurstCompile]
        private struct DownsampleJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float4> SourceA, SourceB;
            [ReadOnly] public NativeArray<byte> SourceMask;
            public int SourceWidth, SourceHeight, DestinationWidth, StartIndex;
            [WriteOnly, NativeDisableParallelForRestriction] public NativeArray<float4> DestinationA, DestinationB;
            [NativeDisableParallelForRestriction] public NativeArray<byte> DestinationMask;
            public void Execute(int index)
            {
                index += StartIndex;
                var x = index % DestinationWidth; var y = index / DestinationWidth;
                var sumA = float4.zero; var sumB = float4.zero; var count = 0;
                for (var oy = 0; oy < 2; oy++) for (var ox = 0; ox < 2; ox++)
                {
                    var sx = x * 2 + ox; var sy = y * 2 + oy;
                    if (sx >= SourceWidth || sy >= SourceHeight) continue;
                    var sourceIndex = sy * SourceWidth + sx;
                    // Values outside the UV-island mask are unrelated texels and must not bleed into a covered
                    // coarse sample. The coarse mask is an OR, while its value is the mean of covered samples only.
                    if ((SourceMask[sourceIndex >> 2] & (1 << (sourceIndex & 3))) == 0) continue;
                    sumA += SourceA[sourceIndex]; sumB += SourceB[sourceIndex]; count++;
                }
                DestinationA[index] = count == 0 ? float4.zero : sumA / count;
                DestinationB[index] = count == 0 ? float4.zero : sumB / count;
                // Four adjacent output pixels share a byte and may run concurrently. Only lane zero writes the complete byte.
                if ((index & 3) == 0)
                {
                    var byteIndex = index >> 2; byte packed = 0; var total = DestinationA.Length;
                    for (var lane = 0; lane < 4 && index + lane < total; lane++)
                    {
                        var lx = (index + lane) % DestinationWidth; var ly = (index + lane) / DestinationWidth; var laneActive = false;
                        for (var loy = 0; loy < 2; loy++) for (var lox = 0; lox < 2; lox++)
                        {
                            var lsx = math.min(SourceWidth - 1, lx * 2 + lox); var lsy = math.min(SourceHeight - 1, ly * 2 + loy);
                            var si = lsy * SourceWidth + lsx;
                            laneActive |= (SourceMask[si >> 2] & (1 << (si & 3))) != 0;
                        }
                        if (laneActive) packed |= (byte)(1 << lane);
                    }
                    DestinationMask[byteIndex] = packed;
                }
            }
        }

        [BurstCompile]
        private struct MetricBlockJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float4> Reference, Candidate;
            [ReadOnly] public NativeArray<byte> Mask;
            public int Width, Height, BlocksX;
            public float Cutoff;
            public bool Detailed, CalculateNormal, PremultipliedColor;
            public int StartIndex;
            [WriteOnly, NativeDisableParallelForRestriction] public NativeArray<BlockResult> Results;
            [NativeDisableParallelForRestriction] public NativeArray<float> NormalAngles;

            public void Execute(int blockIndex)
            {
                blockIndex += StartIndex;
                var startX = blockIndex % BlocksX * 8; var startY = blockIndex / BlocksX * 8;
                var result = new BlockResult();
                var sumA = 0f; var sumB = 0f; var squareA = 0f; var squareB = 0f; var product = 0f;
                for (var y = startY; y < math.min(Height, startY + 8); y++)
                for (var x = startX; x < math.min(Width, startX + 8); x++)
                {
                    var index = y * Width + x;
                    if ((Mask[index >> 2] & (1 << (index & 3))) == 0)
                    { if (Detailed && CalculateNormal) NormalAngles[index] = -1f; continue; }
                    var a = Reference[index]; var b = Candidate[index];
                    var la = math.dot(a.xyz, new float3(0.2126f, 0.7152f, 0.0722f));
                    var lb = math.dot(b.xyz, new float3(0.2126f, 0.7152f, 0.0722f));
                    sumA += la; sumB += lb; squareA += la * la; squareB += lb * lb; product += la * lb; result.Count++;
                    if (Detailed)
                    {
                        var straightA = PremultipliedColor ? (a.w > 1e-7f ? a.xyz / a.w : float3.zero) : a.xyz;
                        var straightB = PremultipliedColor ? (b.w > 1e-7f ? b.xyz / b.w : float3.zero) : b.xyz;
                        result.DeltaE += DeltaE2000(LinearRgbToLab(straightA), LinearRgbToLab(straightB));
                        var difference = a - b; result.ChannelSquared += difference * difference;
                        var alphaDifference = a.w - b.w; result.AlphaSquared += alphaDifference * alphaDifference;
                        var cutA = a.w >= Cutoff; var cutB = b.w >= Cutoff;
                        if (cutA || cutB) result.Union++; if (cutA && cutB) result.Intersection++;
                        if (CalculateNormal)
                        {
                            var normalA = DecodeNormal(straightA); var normalB = DecodeNormal(straightB);
                            var degrees = math.degrees(math.acos(math.clamp(math.dot(normalA, normalB), -1f, 1f)));
                            NormalAngles[index] = degrees; result.NormalDegrees += degrees; result.NormalCount++;
                        }
                    }
                }
                if (result.Count > 0)
                {
                    var count = result.Count; var meanA = sumA / count; var meanB = sumB / count;
                    var varianceA = math.max(0f, squareA / count - meanA * meanA);
                    var varianceB = math.max(0f, squareB / count - meanB * meanB);
                    var covariance = product / count - meanA * meanB;
                    const float c1 = 0.0001f; const float c2 = 0.0009f;
                    result.ContrastStructure = (2f * covariance + c2) / (varianceA + varianceB + c2);
                    result.Ssim = ((2f * meanA * meanB + c1) * result.ContrastStructure) /
                                  (meanA * meanA + meanB * meanB + c1);
                }
                Results[blockIndex] = result;
            }

            private static float3 DecodeNormal(float3 encoded)
            {
                var value = encoded * 2f - 1f;
                if (math.abs(value.z) < 1e-5f) value.z = math.sqrt(math.saturate(1f - value.x * value.x - value.y * value.y));
                return math.normalizesafe(value, new float3(0f, 0f, 1f));
            }

            private static float3 LinearRgbToLab(float3 rgb)
            {
                rgb = math.max(rgb, 0f);
                var xyz = new float3(0.4124564f * rgb.x + 0.3575761f * rgb.y + 0.1804375f * rgb.z,
                    0.2126729f * rgb.x + 0.7151522f * rgb.y + 0.0721750f * rgb.z,
                    0.0193339f * rgb.x + 0.1191920f * rgb.y + 0.9503041f * rgb.z);
                xyz /= new float3(0.95047f, 1f, 1.08883f);
                xyz = new float3(LabCurve(xyz.x), LabCurve(xyz.y), LabCurve(xyz.z));
                return new float3(116f * xyz.y - 16f, 500f * (xyz.x - xyz.y), 200f * (xyz.y - xyz.z));
            }

            private static float LabCurve(float value) => value > 0.0088564517f ? math.pow(value, 1f / 3f) : 7.787037f * value + 16f / 116f;

            public static float DeltaE2000(float3 first, float3 second)
            {
                const float degrees = 57.2957795131f; const float radians = 0.01745329252f;
                var c1 = math.length(first.yz); var c2 = math.length(second.yz); var meanC = (c1 + c2) * 0.5f;
                var meanC7 = math.pow(meanC, 7f); var g = 0.5f * (1f - math.sqrt(meanC7 / (meanC7 + math.pow(25f, 7f))));
                var a1 = (1f + g) * first.y; var a2 = (1f + g) * second.y;
                var cp1 = math.sqrt(a1 * a1 + first.z * first.z); var cp2 = math.sqrt(a2 * a2 + second.z * second.z);
                var h1 = math.atan2(first.z, a1) * degrees; if (h1 < 0f) h1 += 360f;
                var h2 = math.atan2(second.z, a2) * degrees; if (h2 < 0f) h2 += 360f;
                var deltaL = second.x - first.x; var deltaC = cp2 - cp1; var deltaH = h2 - h1;
                if (cp1 * cp2 == 0f) deltaH = 0f; else if (deltaH > 180f) deltaH -= 360f; else if (deltaH < -180f) deltaH += 360f;
                var deltaBigH = 2f * math.sqrt(cp1 * cp2) * math.sin(deltaH * radians * 0.5f);
                var meanL = (first.x + second.x) * 0.5f; var meanCp = (cp1 + cp2) * 0.5f;
                float meanH;
                if (cp1 * cp2 == 0f) meanH = h1 + h2;
                else if (math.abs(h1 - h2) <= 180f) meanH = (h1 + h2) * 0.5f;
                else meanH = h1 + h2 < 360f ? (h1 + h2 + 360f) * 0.5f : (h1 + h2 - 360f) * 0.5f;
                var t = 1f - 0.17f * math.cos((meanH - 30f) * radians) + 0.24f * math.cos(2f * meanH * radians) +
                        0.32f * math.cos((3f * meanH + 6f) * radians) - 0.20f * math.cos((4f * meanH - 63f) * radians);
                var sl = 1f + 0.015f * (meanL - 50f) * (meanL - 50f) / math.sqrt(20f + (meanL - 50f) * (meanL - 50f));
                var sc = 1f + 0.045f * meanCp; var sh = 1f + 0.015f * meanCp * t;
                var deltaTheta = 30f * math.exp(-math.pow((meanH - 275f) / 25f, 2f));
                var meanCp7 = math.pow(meanCp, 7f);
                var rc = 2f * math.sqrt(meanCp7 / (meanCp7 + math.pow(25f, 7f)));
                var rt = -rc * math.sin(2f * deltaTheta * radians);
                var l = deltaL / sl; var c = deltaC / sc; var h = deltaBigH / sh;
                return math.sqrt(math.max(0f, l * l + c * c + h * h + rt * c * h));
            }
        }
    }
}
