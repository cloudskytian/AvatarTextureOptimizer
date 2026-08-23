using System;
using System.Collections.Generic;
using System.Linq;
using Fosa.AvatarTextureOptimizer.Editor.Core;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Fosa.AvatarTextureOptimizer.Editor.Quality
{
    /// <summary>
    /// EN: GPU linear-space resampling plus Burst-parallel quality measurement over actual island coverage.
    /// ZH: GPU 线性空间重采样，并以 Burst 并行测量 UV 岛实际覆盖区质量。
    /// </summary>
    internal sealed class GpuIslandQualityEvaluator : IDisposable
    {
        private const int StripeHeight = 128;
        private const int ChunkSize = 4096;
        private static readonly float[] MsWeights = { 0.0448f, 0.2856f, 0.3001f, 0.2363f, 0.1333f };
        private readonly Material _material;
        private readonly ComputeShader _qualityCompute;
        private bool _disposed;

        private sealed class LevelAccumulator
        {
            public MetricPartial Total;
            public readonly long[] NormalHistogram = new long[181];
            public readonly Dictionary<int, (long intersection, long union)> Cutout = new Dictionary<int, (long, long)>();
            public double SsimSum;
            public double ContrastStructureSum;
            public long SsimCount;
            public bool HasPixels;
        }

        public GpuIslandQualityEvaluator()
        {
            var shader = Shader.Find("Hidden/ATO/LinearResample");
            if (shader == null) throw new InvalidOperationException("Hidden/ATO/LinearResample shader was not found.");
            _qualityCompute = Resources.Load<ComputeShader>("ATO_Quality");
            if (_qualityCompute == null) throw new InvalidOperationException("ATO_Quality compute shader was not found.");
            _material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
        }

        public QualityResult Evaluate(TextureUsage usage, UvGroup group, UvIsland island, Vector2Int commonTarget)
        {
            var sourceWidth = Mathf.Max(1, Mathf.CeilToInt(island.UvBounds.width * usage.Texture.width));
            var sourceHeight = Mathf.Max(1, Mathf.CeilToInt(island.UvBounds.height * usage.Texture.height));
            var targetWidth = Mathf.Clamp(Mathf.RoundToInt(commonTarget.x * (float)sourceWidth / Mathf.Max(1, island.SourcePixelSize.x)), 1, sourceWidth);
            var targetHeight = Mathf.Clamp(Mathf.RoundToInt(commonTarget.y * (float)sourceHeight / Mathf.Max(1, island.SourcePixelSize.y)), 1, sourceHeight);
            var sourceRect = new Rect(
                island.UvBounds.x - group.IntegerTranslation.x,
                island.UvBounds.y - group.IntegerTranslation.y,
                island.UvBounds.width,
                island.UvBounds.height);
            using (var points = BuildTrianglePoints(group, island))
            {
                var candidate = CreateRenderTarget(targetWidth, targetHeight);
                try
                {
                    ConfigureMaterial(sourceRect, SourceMode(usage.Semantic), usage.Semantic == TextureSemantic.ColorAlpha,
                        targetWidth < sourceWidth || targetHeight < sourceHeight);
                    Graphics.Blit(usage.Texture, candidate, _material, 0);

                    var shortSide = Mathf.Min(sourceWidth, sourceHeight);
                    var levels = shortSide >= 176 ? Mathf.Min(5, 1 + Mathf.FloorToInt(Mathf.Log(shortSide / 11f, 2f))) : 1;
                    var levelResults = new List<LevelAccumulator>(levels);
                    for (var level = 0; level < levels; level++)
                    {
                        var width = Mathf.Max(1, sourceWidth >> level);
                        var height = Mathf.Max(1, sourceHeight >> level);
                        levelResults.Add(EvaluateLevel(usage, points, candidate, sourceRect, width, height));
                    }
                    return Finish(usage, shortSide, levelResults);
                }
                finally
                {
                    candidate.Release();
                    Object.DestroyImmediate(candidate);
                }
            }
        }

        private LevelAccumulator EvaluateLevel(TextureUsage usage, NativeArray<float2> trianglePoints,
            RenderTexture candidate, Rect sourceRect, int width, int height)
        {
            var result = new LevelAccumulator
            {
                Total = EmptyPartial(),
            };
            var cutoffs = usage.AlphaConstraints.Where(x => x.Mode == AlphaMode.Cutout)
                .Select(x => Mathf.Clamp(Mathf.RoundToInt(x.Cutoff * 10000f), 0, 10000)).Distinct().ToList();
            foreach (var cutoff in cutoffs) result.Cutout[cutoff] = (0, 0);

            for (var y = 0; y < height; y += StripeHeight)
            {
                var stripeHeight = Mathf.Min(StripeHeight, height - y);
                var dataStart = Mathf.Max(0, y - 5);
                var dataEnd = Mathf.Min(height, y + stripeHeight + 5);
                var dataHeight = dataEnd - dataStart;
                var centerOffset = y - dataStart;
                var originalRt = CreateRenderTarget(width, dataHeight);
                var candidateRt = CreateRenderTarget(width, dataHeight);
                var ssimRt = CreateComputeTarget(width, stripeHeight, RenderTextureFormat.RGFloat);
                Texture2D originalReadback = null;
                Texture2D candidateReadback = null;
                Texture2D ssimReadback = null;
                try
                {
                    var dataFractionY = (float)dataStart / height;
                    var dataFractionHeight = (float)dataHeight / height;
                    var stripeRect = new Rect(sourceRect.x, sourceRect.y + sourceRect.height * dataFractionY,
                        sourceRect.width, sourceRect.height * dataFractionHeight);
                    ConfigureMaterial(stripeRect, SourceMode(usage.Semantic), usage.Semantic == TextureSemantic.ColorAlpha, false);
                    Graphics.Blit(usage.Texture, originalRt, _material, 0);
                    ConfigureMaterial(new Rect(0f, dataFractionY, 1f, dataFractionHeight),
                        usage.Semantic == TextureSemantic.Normal ? 2 : 0, false, false);
                    Graphics.Blit(candidate, candidateRt, _material, 0);
                    DispatchSsim(originalRt, candidateRt, ssimRt, centerOffset, stripeHeight);

                    originalReadback = Readback(originalRt, TextureFormat.RGBAFloat);
                    candidateReadback = Readback(candidateRt, TextureFormat.RGBAFloat);
                    ssimReadback = Readback(ssimRt, TextureFormat.RGFloat);
                    var original = originalReadback.GetRawTextureData<float4>();
                    var compared = candidateReadback.GetRawTextureData<float4>();
                    var ssim = ssimReadback.GetRawTextureData<float2>();
                    var centerLength = width * stripeHeight;
                    using (var mask = new NativeArray<byte>(centerLength, Allocator.TempJob, NativeArrayOptions.UninitializedMemory))
                    using (var angles = new NativeArray<float>(centerLength, Allocator.TempJob, NativeArrayOptions.UninitializedMemory))
                    {
                        var chunks = Mathf.CeilToInt((float)centerLength / ChunkSize);
                        using (var parts = new NativeArray<MetricPartial>(chunks, Allocator.TempJob, NativeArrayOptions.UninitializedMemory))
                        {
                            var maskJob = new IslandMaskJob
                            {
                                TrianglePoints = trianglePoints,
                                Mask = mask,
                                Width = width,
                                Height = stripeHeight,
                                YOffset = y,
                                FullHeight = height,
                            }.Schedule(centerLength, 128);

                            var firstCutoff = cutoffs.Count > 0 ? cutoffs[0] : 5000;
                            var metric = CreateMetricJob(usage, original, compared, mask, parts, angles, firstCutoff,
                                cutoffs.Count > 0, width, centerOffset);
                            metric.Schedule(chunks, 1, maskJob).Complete();
                            MergePartials(result, parts, angles);
                            MergeSsim(result, ssim, mask);
                            if (cutoffs.Count > 0) MergeCutout(result, firstCutoff, parts);

                            for (var cutoffIndex = 1; cutoffIndex < cutoffs.Count; cutoffIndex++)
                            {
                                metric.Cutoff = cutoffs[cutoffIndex] / 10000f;
                                metric.EvaluateCutout = 1;
                                metric.Schedule(chunks, 1).Complete();
                                MergeCutout(result, cutoffs[cutoffIndex], parts);
                            }
                        }
                    }
                }
                finally
                {
                    if (originalReadback != null) Object.DestroyImmediate(originalReadback);
                    if (candidateReadback != null) Object.DestroyImmediate(candidateReadback);
                    if (ssimReadback != null) Object.DestroyImmediate(ssimReadback);
                    originalRt.Release(); candidateRt.Release(); ssimRt.Release();
                    Object.DestroyImmediate(originalRt); Object.DestroyImmediate(candidateRt); Object.DestroyImmediate(ssimRt);
                }
            }
            return result;
        }

        private static MetricChunkJob CreateMetricJob(TextureUsage usage, NativeArray<float4> original,
            NativeArray<float4> compared, NativeArray<byte> mask, NativeArray<MetricPartial> parts,
            NativeArray<float> angles, int cutoff, bool evaluateCutout, int dataWidth, int dataRowOffset)
        {
            return new MetricChunkJob
            {
                Original = original,
                Candidate = compared,
                Mask = mask,
                Partials = parts,
                NormalAngles = angles,
                ChunkSize = ChunkSize,
                DataWidth = dataWidth,
                DataRowOffset = dataRowOffset,
                Semantic = (int)usage.Semantic,
                UsedChannelMask = usage.UsedChannelMask,
                Cutoff = cutoff / 10000f,
                EvaluateCutout = evaluateCutout ? 1 : 0,
            };
        }

        private static QualityResult Finish(TextureUsage usage, int sourceShortSide, IReadOnlyList<LevelAccumulator> levels)
        {
            var output = new QualityResult();
            if (levels.Count == 0 || levels[0].Total.Count == 0) return output;
            var full = levels[0].Total;
            var count = Math.Max(1L, full.Count);
            if (sourceShortSide < 11) output.Structural = 1f;
            else if (sourceShortSide < 176 || levels.Count == 1) output.Structural = LocalSsim(levels[0]);
            else
            {
                // EN: Canonical MS-SSIM combines contrast-structure at early scales and SSIM at the coarsest scale.
                // ZH: 标准 MS-SSIM 在早期尺度组合对比度-结构项，并在最粗尺度使用 SSIM。
                var value = 1f;
                for (var i = 0; i < levels.Count - 1; i++)
                    value *= Mathf.Pow(Mathf.Clamp(LocalContrastStructure(levels[i]), 1e-6f, 1f), MsWeights[i]);
                var last = levels.Count - 1;
                value *= Mathf.Pow(Mathf.Clamp(LocalSsim(levels[last]), 1e-6f, 1f), MsWeights[last]);
                output.Structural = value;
            }

            output.DeltaE2000 = (float)(full.DeltaESum / count);
            output.AlphaRmse = Mathf.Sqrt((float)(full.AlphaSquaredError / count));
            output.NormalMeanDegrees = (float)(full.NormalAngleSum / count);
            output.NormalP95Degrees = Percentile95(levels[0].NormalHistogram);
            output.ChannelRmse = new Vector4(
                (usage.UsedChannelMask & 1) != 0 ? Mathf.Sqrt(full.ChannelSquaredError.x / count) : 0f,
                (usage.UsedChannelMask & 2) != 0 ? Mathf.Sqrt(full.ChannelSquaredError.y / count) : 0f,
                (usage.UsedChannelMask & 4) != 0 ? Mathf.Sqrt(full.ChannelSquaredError.z / count) : 0f,
                (usage.UsedChannelMask & 8) != 0 ? Mathf.Sqrt(full.ChannelSquaredError.w / count) : 0f);

            var cutoutConstraints = usage.AlphaConstraints.Where(x => x.Mode == AlphaMode.Cutout).ToList();
            output.CutoutIou = 1f;
            foreach (var constraint in cutoutConstraints)
            {
                var key = Mathf.Clamp(Mathf.RoundToInt(constraint.Cutoff * 10000f), 0, 10000);
                if (!levels[0].Cutout.TryGetValue(key, out var value)) continue;
                var iou = value.union == 0 ? 1f : (float)value.intersection / value.union;
                output.CutoutIou = Mathf.Min(output.CutoutIou, iou);
            }
            if (!usage.AlphaConstraints.Any(x => x.Mode == AlphaMode.Blend)) output.AlphaRmse = 0f;
            var range = full.Maximum - full.Minimum;
            output.IsPureColor = math.cmax(math.abs(range)) <= 1f / 65535f;
            return output;
        }

        private static float LocalSsim(LevelAccumulator value) => value.SsimCount == 0
            ? 1f : Mathf.Clamp01((float)(value.SsimSum / value.SsimCount));

        private static float LocalContrastStructure(LevelAccumulator value) => value.SsimCount == 0
            ? 1f : Mathf.Clamp01((float)(value.ContrastStructureSum / value.SsimCount));

        private static float Percentile95(IReadOnlyList<long> histogram)
        {
            var total = histogram.Sum();
            if (total == 0) return 0f;
            var target = (long)Math.Ceiling(total * 0.95d); long accumulated = 0;
            for (var i = 0; i < histogram.Count; i++) { accumulated += histogram[i]; if (accumulated >= target) return i; }
            return 180f;
        }

        private static void MergeSsim(LevelAccumulator target, NativeArray<float2> values, NativeArray<byte> mask)
        {
            for (var i = 0; i < values.Length; i++)
            {
                if (mask[i] == 0) continue;
                target.SsimSum += values[i].x;
                target.ContrastStructureSum += values[i].y;
                target.SsimCount++;
            }
        }

        private static void MergePartials(LevelAccumulator target, NativeArray<MetricPartial> parts,
            NativeArray<float> angles)
        {
            foreach (var part in parts)
            {
                target.Total = Add(target.Total, part);
                if (part.Count > 0) target.HasPixels = true;
            }
            for (var i = 0; i < angles.Length; i++)
                if (angles[i] >= 0f) target.NormalHistogram[Mathf.Clamp(Mathf.RoundToInt(angles[i]), 0, 180)]++;
        }

        private static void MergeCutout(LevelAccumulator target, int cutoff, NativeArray<MetricPartial> parts)
        {
            var current = target.Cutout[cutoff];
            foreach (var part in parts) { current.intersection += part.CutoutIntersection; current.union += part.CutoutUnion; }
            target.Cutout[cutoff] = current;
        }

        private static MetricPartial Add(MetricPartial a, MetricPartial b)
        {
            if (b.Count == 0) return a;
            if (a.Count == 0) { a.Minimum = b.Minimum; a.Maximum = b.Maximum; }
            else { a.Minimum = math.min(a.Minimum, b.Minimum); a.Maximum = math.max(a.Maximum, b.Maximum); }
            a.Count += b.Count; a.SumX += b.SumX; a.SumY += b.SumY; a.SumXX += b.SumXX; a.SumYY += b.SumYY;
            a.SumXY += b.SumXY; a.DeltaESum += b.DeltaESum; a.AlphaSquaredError += b.AlphaSquaredError;
            a.NormalAngleSum += b.NormalAngleSum; a.ChannelSquaredError += b.ChannelSquaredError;
            a.CutoutIntersection += b.CutoutIntersection; a.CutoutUnion += b.CutoutUnion;
            return a;
        }

        private static MetricPartial EmptyPartial()
        {
            return new MetricPartial { Minimum = new float4(float.PositiveInfinity), Maximum = new float4(float.NegativeInfinity) };
        }

        private static NativeArray<float2> BuildTrianglePoints(UvGroup group, UvIsland island)
        {
            var uvs = new List<Vector2>(); group.Renderer.SourceMesh.GetUVs(group.UvChannel, uvs);
            var points = new NativeArray<float2>(island.Triangles.Count * 3, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            var width = Mathf.Max(1e-10f, island.UvBounds.width); var height = Mathf.Max(1e-10f, island.UvBounds.height);
            var index = 0;
            foreach (var triangle in island.Triangles)
            {
                Add(triangle.A); Add(triangle.B); Add(triangle.C);
            }
            void Add(int vertex)
            {
                var uv = uvs[vertex] + group.IntegerTranslation;
                points[index++] = new float2((uv.x - island.UvBounds.x) / width, (uv.y - island.UvBounds.y) / height);
            }
            return points;
        }

        private static int SourceMode(TextureSemantic semantic) => semantic == TextureSemantic.Normal ? 1 : 0;
        private void ConfigureMaterial(Rect rect, int mode, bool premultiply, bool areaSample)
        {
            _material.SetVector("_UvRect", new Vector4(rect.x, rect.y, rect.width, rect.height));
            _material.SetInt("_Mode", mode);
            _material.SetInt("_Premultiply", premultiply ? 1 : 0);
            _material.SetInt("_AreaSample", areaSample ? 1 : 0);
        }

        private static RenderTexture CreateRenderTarget(int width, int height)
        {
            var texture = new RenderTexture(new RenderTextureDescriptor(width, height, RenderTextureFormat.ARGBFloat, 0)
            { sRGB = false, useMipMap = false, autoGenerateMips = false })
            { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
            texture.Create(); return texture;
        }

        private static RenderTexture CreateComputeTarget(int width, int height, RenderTextureFormat format)
        {
            var texture = new RenderTexture(new RenderTextureDescriptor(width, height, format, 0)
            { sRGB = false, useMipMap = false, autoGenerateMips = false, enableRandomWrite = true })
            { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
            texture.Create();
            return texture;
        }

        private void DispatchSsim(RenderTexture original, RenderTexture candidate, RenderTexture output,
            int centerOffset, int outputHeight)
        {
            var kernel = _qualityCompute.FindKernel("LocalSsim");
            _qualityCompute.SetInt("_Width", original.width);
            _qualityCompute.SetInt("_DataHeight", original.height);
            _qualityCompute.SetInt("_OutputHeight", outputHeight);
            _qualityCompute.SetInt("_CenterOffset", centerOffset);
            _qualityCompute.SetTexture(kernel, "_Original", original);
            _qualityCompute.SetTexture(kernel, "_Candidate", candidate);
            _qualityCompute.SetTexture(kernel, "_SsimOutput", output);
            _qualityCompute.Dispatch(kernel, Mathf.CeilToInt(original.width / 8f), Mathf.CeilToInt(outputHeight / 8f), 1);
        }

        private static Texture2D Readback(RenderTexture source, TextureFormat format)
        {
            var previous = RenderTexture.active;
            try
            {
                RenderTexture.active = source;
                var texture = new Texture2D(source.width, source.height, format, false, true)
                    { hideFlags = HideFlags.HideAndDontSave };
                texture.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0, false);
                texture.Apply(false, false);
                return texture;
            }
            finally { RenderTexture.active = previous; }
        }

        public void Dispose()
        {
            if (_disposed) return; _disposed = true;
            if (_material != null) Object.DestroyImmediate(_material);
        }
    }
}
