// SPDX-License-Identifier: MIT
// EN: Chooses the smallest island size that still satisfies every quality threshold.
// ZH: 为每个岛选出仍能满足全部质量阈值的最小尺寸。

using System;
using System.Collections.Generic;
using Net.Fosa.AvatarTextureOptimizer.Editor.Core;
using Net.Fosa.AvatarTextureOptimizer.Editor.Model;
using Net.Fosa.AvatarTextureOptimizer.Editor.Textures;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor.Quality
{
    /// <summary>
    /// EN: Everything the solver needs to know about one texture participating in an island.
    /// ZH: 求解器针对参与某个岛的单张贴图所需要知道的一切。
    /// </summary>
    public sealed class SolverTexture
    {
        /// <summary>EN: The entry being evaluated. ZH: 被评估的贴图条目。</summary>
        public TextureEntry Entry;
        /// <summary>EN: Full resolution linear copy, owned by the caller. ZH: 全分辨率线性副本，所有权归调用方。</summary>
        public RenderTexture LinearSource;
        /// <summary>EN: Strictest alpha mode across every reference. ZH: 所有引用中最严格的 alpha 模式。</summary>
        public AtoAlphaMode AlphaMode;
        /// <summary>EN: Strictest cutoff across every reference. ZH: 所有引用中最严格的裁剪阈值。</summary>
        public float Cutoff;
        /// <summary>EN: Detected normal encoding. ZH: 检测到的法线编码。</summary>
        public NormalEncoding NormalEncoding;
    }

    /// <summary>
    /// EN: Binary search based island scaler. The search is uniform first (fast, and guarantees the
    ///     aspect ratio never distorts more than necessary), then each axis is refined independently to
    ///     exploit anisotropic islands.
    /// ZH: 基于二分搜索的岛缩放器。先做均匀搜索（快速，且保证长宽比失真不超过必要程度），
    ///     再对每个轴独立细化，以利用各向异性的岛。
    /// </summary>
    public static class IslandQualitySolver
    {
        private const string Stage = "Quality";
        private const int MinIslandSide = 4;
        private const int UniformIterations = 8;
        private const int AxisIterations = 6;

        /// <summary>
        /// EN: Solves the scale for one island across every texture of its UV group. The木桶 (weakest
        ///     link) rule applies: the island keeps the largest size any member texture demands.
        /// ZH: 为一个岛在其 UV 组的所有贴图上求解缩放。适用木桶效应：
        ///     岛保留任一成员贴图所要求的最大尺寸。
        /// </summary>
        public static void Solve(UvIsland island, IReadOnlyList<SolverTexture> textures,
            AtoQualityParameters quality, Vector2Int referenceSize, AtoProgress progress)
        {
            if (quality.IsLossless)
            {
                island.Scale = Vector2.one;
                island.ScaledSize = new Vector2Int(island.Bounds.width, island.Bounds.height);
                return;
            }

            // EN: A flat coloured island carries no detail, so shrink it to the minimum immediately.
            // ZH: 纯色岛不携带任何细节，直接缩到最小。
            if (island.SolidColor)
            {
                int side = Mathf.Min(MinIslandSide, Mathf.Min(island.Bounds.width, island.Bounds.height));
                island.ScaledSize = new Vector2Int(Mathf.Max(1, side), Mathf.Max(1, side));
                island.Scale = new Vector2(
                    island.ScaledSize.x / (float)island.Bounds.width,
                    island.ScaledSize.y / (float)island.Bounds.height);
                AtoLog.Trace(Stage, $"island {island.Index} is solid; shortcut to {island.ScaledSize}");
                return;
            }

            var densityLimits = ComputeDensityLimits(island, quality, referenceSize);

            // EN: Phase 1 - uniform binary search on a single scale factor.
            // ZH: 阶段 1 —— 对单一缩放系数做均匀二分搜索。
            float lo = densityLimits.min, hi = 1f;
            float best = 1f;
            for (int i = 0; i < UniformIterations && hi - lo > 1f / Mathf.Max(island.Bounds.width, island.Bounds.height); i++)
            {
                float mid = (lo + hi) * 0.5f;
                if (EvaluateScale(island, textures, quality, new Vector2(mid, mid)))
                {
                    best = mid;
                    hi = mid;
                }
                else
                {
                    lo = mid;
                }
                progress?.Step(0f);
            }
            // EN: If even the largest allowed scale fails, keep the original size.
            // ZH: 若连允许的最大缩放都不通过，则保留原尺寸。
            if (!EvaluateScale(island, textures, quality, new Vector2(best, best))) best = 1f;

            var scale = new Vector2(best, best);

            // EN: Phase 2 - refine each axis independently so a long thin island does not waste texels
            //     on its short axis.
            // ZH: 阶段 2 —— 独立细化每个轴，使细长的岛不会在短轴上浪费像素。
            for (int axis = 0; axis < 2; axis++)
            {
                float axisLo = densityLimits.min, axisHi = scale[axis];
                float axisBest = scale[axis];
                for (int i = 0; i < AxisIterations && axisHi - axisLo > 1f / island.Bounds.size[axis == 0 ? 0 : 1]; i++)
                {
                    float mid = (axisLo + axisHi) * 0.5f;
                    var candidate = scale;
                    candidate[axis] = mid;
                    if (EvaluateScale(island, textures, quality, candidate))
                    {
                        axisBest = mid;
                        axisHi = mid;
                    }
                    else
                    {
                        axisLo = mid;
                    }
                    progress?.Step(0f);
                }
                scale[axis] = axisBest;
            }

            scale = new Vector2(
                Mathf.Clamp(scale.x, densityLimits.min, densityLimits.max),
                Mathf.Clamp(scale.y, densityLimits.min, densityLimits.max));

            // EN: The two axes were refined one after the other, so the combination has never been
            //     evaluated as a whole. Verify it, and if it fails walk back towards the uniform
            //     solution that is known to pass. This makes the anisotropic step strictly safe.
            // ZH: 两个轴是先后细化的，因此这个组合从未被整体评估过。
            //     在此验证；若不通过则朝已知可通过的均匀解回退。这使各向异性步骤严格安全。
            if (!EvaluateScale(island, textures, quality, scale))
            {
                var uniform = new Vector2(best, best);
                var lo2 = scale;
                var hi2 = uniform;
                var accepted = uniform;
                for (int i = 0; i < AxisIterations; i++)
                {
                    var mid = Vector2.Lerp(lo2, hi2, 0.5f);
                    if (EvaluateScale(island, textures, quality, mid))
                    {
                        accepted = mid;
                        hi2 = mid;
                    }
                    else
                    {
                        lo2 = mid;
                    }
                    progress?.Step(0f);
                }
                AtoLog.Trace(Stage,
                    $"island {island.Index}: anisotropic combination failed verification, backed off to " +
                    $"({accepted.x:F3},{accepted.y:F3})");
                scale = accepted;
            }

            island.Scale = scale;
            island.ScaledSize = ToSize(island, island.Scale);

            AtoLog.Trace(Stage,
                $"island {island.Index}: {island.Bounds.width}x{island.Bounds.height} -> {island.ScaledSize.x}x{island.ScaledSize.y} " +
                $"(scale {island.Scale.x:F3},{island.Scale.y:F3})");
        }

        /// <summary>
        /// EN: Converts a scale factor into an integer size, never going below the minimum side and never
        ///     exceeding the original.
        /// ZH: 将缩放系数转换为整数尺寸，不低于最小边长且不超过原尺寸。
        /// </summary>
        private static Vector2Int ToSize(UvIsland island, Vector2 scale)
        {
            return new Vector2Int(
                Mathf.Clamp(Mathf.RoundToInt(island.Bounds.width * scale.x), Mathf.Min(MinIslandSide, island.Bounds.width), island.Bounds.width),
                Mathf.Clamp(Mathf.RoundToInt(island.Bounds.height * scale.y), Mathf.Min(MinIslandSide, island.Bounds.height), island.Bounds.height));
        }

        /// <summary>
        /// EN: Turns the texel density preferences into scale bounds. Density is measured against the
        ///     island's world space area, and is additionally clamped by the physical resolution the
        ///     source texture actually has - we can never invent detail that is not in the file.
        /// ZH: 将像素密度偏好转换为缩放范围。密度以岛的世界空间面积衡量，
        ///     并额外受源贴图实际物理分辨率的钳制——我们无法凭空造出文件里没有的细节。
        /// </summary>
        private static (float min, float max) ComputeDensityLimits(UvIsland island, AtoQualityParameters q, Vector2Int referenceSize)
        {
            if (island.WorldAreaM2 <= 1e-9f) return (1f / Mathf.Max(island.Bounds.width, island.Bounds.height), 1f);

            float sourceTexels = Mathf.Max(1f, island.CoveredCells * UvIslandBuilderCellArea);
            float currentDensity = Mathf.Sqrt(sourceTexels / island.WorldAreaM2); // texels per meter
            float minDensity = (float)q.minPixelDensity;
            float maxDensity = (float)q.maxPixelDensity;

            // EN: scale factor is linear in density, so the ratio of densities is the ratio of scales.
            // ZH: 缩放系数与密度成线性关系，因此密度之比即为缩放之比。
            float minScale = Mathf.Clamp(minDensity / Mathf.Max(currentDensity, 1e-6f), 0f, 1f);
            float maxScale = Mathf.Clamp(maxDensity / Mathf.Max(currentDensity, 1e-6f), 0f, 1f);
            if (maxScale < minScale) maxScale = minScale;

            float absoluteMin = Mathf.Min(MinIslandSide, Mathf.Min(island.Bounds.width, island.Bounds.height))
                                / (float)Mathf.Max(island.Bounds.width, island.Bounds.height);
            minScale = Mathf.Max(minScale, absoluteMin);

            return (minScale, Mathf.Max(maxScale, minScale));
        }

        private const float UvIslandBuilderCellArea = Meshes.UvIslandBuilder.CellSize * Meshes.UvIslandBuilder.CellSize;

        /// <summary>
        /// EN: Evaluates a candidate scale against every texture of the group and returns true only when
        ///     all of them pass. Each texture is downscaled with premultiplied alpha where relevant, then
        ///     bilinearly upsampled back to the original size before comparison, exactly as specified.
        /// ZH: 针对组内每张贴图评估候选缩放，只有全部通过才返回 true。
        ///     每张贴图在需要时以预乘 alpha 降采样，再双线性上采样回原尺寸后比较，与规格完全一致。
        /// </summary>
        private static bool EvaluateScale(UvIsland island, IReadOnlyList<SolverTexture> textures,
            AtoQualityParameters quality, Vector2 scale)
        {
            var size = ToSize(island, scale);
            if (size.x >= island.Bounds.width && size.y >= island.Bounds.height) return true;

            foreach (var st in textures)
            {
                if (st.LinearSource == null) continue;

                var region = ScaleRegion(island.Bounds, st.LinearSource);
                if (region.width <= 0 || region.height <= 0) continue;

                var targetSize = new Vector2Int(
                    Mathf.Max(1, Mathf.RoundToInt(region.width * scale.x)),
                    Mathf.Max(1, Mathf.RoundToInt(region.height * scale.y)));

                RenderTexture small = null, restored = null, originalCrop = null;
                LinearImage origImg = null, candImg = null;
                try
                {
                    bool premultiply = st.Entry.HasAlpha &&
                                       (st.Entry.Kind == AtoTextureKind.ColorAlpha || st.AlphaMode != AtoAlphaMode.Opaque);

                    small = GpuTextureUtil.Downsample(st.LinearSource, region, targetSize, premultiply);
                    restored = GpuTextureUtil.BilinearUpsample(small, new Vector2Int(region.width, region.height));
                    originalCrop = GpuTextureUtil.Downsample(st.LinearSource, region, new Vector2Int(region.width, region.height), false);

                    origImg = GpuTextureUtil.Readback(originalCrop, new RectInt(0, 0, region.width, region.height));
                    candImg = GpuTextureUtil.Readback(restored, new RectInt(0, 0, region.width, region.height));

                    var scores = QualityMetrics.Evaluate(origImg, candImg, st.Entry.Kind,
                        st.AlphaMode, st.Cutoff, st.Entry.UsedChannelMask, st.NormalEncoding);

                    if (!QualityMetrics.Passes(scores, quality, st.Entry.Kind, st.AlphaMode))
                        return false;
                }
                catch (Exception e)
                {
                    AtoLog.Warning(Stage, $"metric evaluation failed for '{st.Entry.Texture.name}': {e.Message}; treating as failed.");
                    return false;
                }
                finally
                {
                    origImg?.Dispose();
                    candImg?.Dispose();
                    GpuTextureUtil.Release(small);
                    GpuTextureUtil.Release(restored);
                    GpuTextureUtil.Release(originalCrop);
                }
            }

            return true;
        }

        /// <summary>
        /// EN: Maps an island rectangle expressed in the group's reference resolution onto a specific
        ///     texture. The pipeline resamples every member of a UV group into the group's reference
        ///     resolution before solving, so the mapping is the identity; the helper stays as an explicit
        ///     seam for extensions that want per-texture resolutions.
        /// ZH: 将以组参考分辨率表示的岛矩形映射到某张具体贴图上。管线在求解前已把 UV 组的每个成员
        ///     重采样到该组的参考分辨率，因此这里是恒等映射；保留该方法是为扩展留出显式接缝，
        ///     以便将来支持逐贴图分辨率。
        /// </summary>
        public static RectInt ScaleRegion(RectInt referenceRect, RenderTexture target)
        {
            if (target == null) return referenceRect;
            return new RectInt(
                Mathf.Clamp(referenceRect.x, 0, Mathf.Max(0, target.width - 1)),
                Mathf.Clamp(referenceRect.y, 0, Mathf.Max(0, target.height - 1)),
                Mathf.Clamp(referenceRect.width, 0, target.width - Mathf.Clamp(referenceRect.x, 0, target.width)),
                Mathf.Clamp(referenceRect.height, 0, target.height - Mathf.Clamp(referenceRect.y, 0, target.height)));
        }
    }
}
