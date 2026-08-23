using System;
using Fosa.AvatarTextureOptimizer.Editor.Analysis;
using Fosa.AvatarTextureOptimizer.Editor.Pipeline;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Editor.Quality
{
    internal sealed class IslandQualityEvaluator : IDisposable
    {
        // One full 2048² island is a common Avatar source and must remain evaluable. At this hard cap the two CPU
        // float4 readbacks use 128 MiB total; the mask, block aggregates and sequential GPU work surfaces keep the
        // evaluator's deterministic working set bounded. Anything larger still falls back before allocation rather
        // than being judged through a lossy proxy. / 完整 2K 岛是常见输入；上限内两张 CPU float4 共 128 MiB，
        // 其余掩码、分块结果和顺序 GPU 表面仍受控；更大输入在分配前保守回退，不使用有损代理。
        internal const long MaximumResidentPixels = 4L * 1024L * 1024L;
        private readonly GpuLinearResampler _resampler = new GpuLinearResampler();
        public bool ResourceLimitExceeded { get; private set; }
        public void ResetResourceLimit() => ResourceLimitExceeded = false;

        public bool Passes(UvGroupRecord group, UvIsland island, Vector2Int candidateSize,
            ATOQualitySettings quality, out bool allPureColor)
        {
            allPureColor = true;
            foreach (var binding in group.Bindings)
            {
                ATOProgress.Checkpoint("Evaluating texture quality " +
                                       (binding.Texture == null ? "<null>" : binding.Texture.name));
                if (binding.Whitelisted) continue;
                var passes = EvaluateBinding(group, island, binding, candidateSize, quality, out var pure);
                allPureColor &= pure;
                if (!passes) return false;
            }
            return true;
        }

        private bool EvaluateBinding(UvGroupRecord group, UvIsland island, TextureBindingRecord binding,
            Vector2Int candidateSize, ATOQualitySettings quality, out bool pure)
        {
            var originalSize = new Vector2Int(
                Mathf.Max(1, Mathf.CeilToInt(island.UvBounds.width * binding.Texture.width)),
                Mathf.Max(1, Mathf.CeilToInt(island.UvBounds.height * binding.Texture.height)));
            if ((long)originalSize.x * originalSize.y > MaximumResidentPixels ||
                (long)candidateSize.x * candidateSize.y > MaximumResidentPixels)
            {
                // Component-wise group maxima can form a much larger cross-product than any single source.
                // 共享组逐轴最大值可能组合成远大于任一源贴图的面积，分配 GPU 资源前必须同样门禁。
                ResourceLimitExceeded = true;
                pure = false;
                return false;
            }
            var point = binding.Texture.filterMode == FilterMode.Point;
            RenderTexture referenceRt = null, downsampled = null, reconstructed = null;
            NativeArray<float4> reference = default, comparison = default;
            NativeArray<byte> mask = default;
            try
            {
                referenceRt = _resampler.Resample(binding.Texture, island.UvBounds, originalSize, point, false, false, binding.Kind);
                reference = _resampler.Readback(referenceRt, Allocator.TempJob);
                GpuLinearResampler.Release(referenceRt); referenceRt = null;
                mask = IslandMaskRasterizer.Rasterize(group, island, originalSize, Allocator.TempJob);
                pure = IsPure(reference, mask);
                // Masked source texels can be pure even when the rectangular UV bound contains different off-island
                // texels. Every candidate must therefore prove its actual reconstruction; CopyMasked cannot repair a
                // 1x1/low-resolution sample that was already taken outside the shape.
                // 源岛 mask 内可以是纯色，但矩形 UV bounds 的岛外 texel 仍可能异色；所有候选都必须验证实际重建，
                // CopyMasked 无法修复已从形状外采错的 1x1/低分辨率颜色。

                downsampled = _resampler.Resample(binding.Texture, island.UvBounds, candidateSize, point, false, false, binding.Kind);
                reconstructed = _resampler.Resample(downsampled, new Rect(0f, 0f, 1f, 1f), originalSize,
                    point, true, false, binding.Kind, binding.Kind == ATOTextureKind.Normal
                        ? ATONormalInputEncoding.EncodedRgb : ATONormalInputEncoding.Imported);
                comparison = _resampler.Readback(reconstructed, Allocator.TempJob);
                GpuLinearResampler.Release(downsampled); downsampled = null;
                GpuLinearResampler.Release(reconstructed); reconstructed = null;
                var metrics = QualityMetricEvaluator.EvaluateForBinding(reference, comparison, mask,
                    originalSize.x, originalSize.y, binding);
                return metrics.Passes(quality, binding);
            }
            finally
            {
                if (reference.IsCreated) reference.Dispose(); if (comparison.IsCreated) comparison.Dispose();
                if (mask.IsCreated) mask.Dispose();
                GpuLinearResampler.Release(referenceRt); GpuLinearResampler.Release(downsampled); GpuLinearResampler.Release(reconstructed);
            }
        }

        internal static bool IsPure(NativeArray<float4> colors, NativeArray<byte> mask)
        {
            var found = false; var first = float4.zero;
            for (var i = 0; i < colors.Length; i++)
            {
                if ((i & 65535) == 0) ATOProgress.Checkpoint("Testing UV-island pure-color shortcut");
                if (!IslandMaskRasterizer.IsSet(mask, i)) continue;
                var color = colors[i];
                // A NaN comparison is false, so checking only the tolerance would incorrectly classify a
                // non-finite floating-point texture as pure and bypass every fail-closed quality metric.
                // NaN 比较恒为 false；必须先拒绝非有限像素，避免“纯色”快捷路径绕过质量门禁。
                if (!Finite(color)) return false;
                if (!found) { first = color; found = true; continue; }
                if (math.cmax(math.abs(color - first)) > 1f / 1024f) return false;
            }
            // Empty coverage can occur for degenerate/sub-pixel geometry and must not trigger the 1x1 shortcut.
            // 退化或亚像素几何可能没有覆盖像素，不能据此触发纯色 1x1 快捷路径。
            return found;
        }

        private static bool Finite(float4 value) =>
            !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
            !float.IsNaN(value.y) && !float.IsInfinity(value.y) &&
            !float.IsNaN(value.z) && !float.IsInfinity(value.z) &&
            !float.IsNaN(value.w) && !float.IsInfinity(value.w);

        public void Dispose() => _resampler.Dispose();
    }
}
