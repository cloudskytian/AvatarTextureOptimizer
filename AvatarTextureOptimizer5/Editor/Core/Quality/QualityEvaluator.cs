// Copyright (c) fosa. Licensed under the MIT License.
// Binary search over UV island scale: uniform shrink until the quality budget is exhausted,
// then independent per-axis refinement to exploit anisotropic islands.
// UV 岛缩放的二分搜索：先均匀缩小直至耗尽质量预算，再逐轴独立细化以利用各向异性岛。

using System;
using System.Collections.Generic;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// The strictest requirement gathered from every material that references a texture.
    /// 从引用某贴图的所有材质中收集到的最严苛要求。
    /// </summary>
    public sealed class EvaluationContext
    {
        /// <summary>Thresholds for the active quality tier. / 当前质量挡位的阈值。</summary>
        public QualityParameters Parameters;

        /// <summary>Texture category driving which metrics run. / 决定运行哪些指标的贴图分类。</summary>
        public TextureCategory Category;

        /// <summary>Strictest alpha mode across all references. / 所有引用中最严苛的 alpha 模式。</summary>
        public AlphaMode AlphaMode;

        /// <summary>All distinct cutoffs to evaluate. / 需要评估的所有不同 cutoff。</summary>
        public List<float> Cutoffs = new List<float>();

        /// <summary>Channels the shader actually reads. / 着色器实际读取的通道。</summary>
        public ChannelMask UsedChannels = ChannelMask.All;
    }

    /// <summary>
    /// Decides how far each UV island may shrink while still meeting the quality target.
    /// 决定每个 UV 岛在仍满足质量目标的前提下可以缩小到何种程度。
    /// </summary>
    public static class QualityEvaluator
    {
        /// <summary>
        /// Evaluates whether a candidate size still satisfies every enabled metric.
        /// The candidate is downsampled then bilinearly upsampled back to the original size and
        /// compared pixel for pixel, which is exactly how the GPU will sample it in game.
        /// 评估候选尺寸是否仍满足所有启用的指标。
        /// 候选先下采样再双线性上采样回原尺寸并逐像素比较，这与 GPU 在游戏中的采样方式一致。
        /// </summary>
        public static bool MeetsQuality(
            ImageBuffer original, int candidateW, int candidateH, EvaluationContext ctx)
        {
            var p = ctx.Parameters;

            // Near-lossless never resamples, so any size other than the original fails.
            // 近无损绝不重采样，因此任何非原始尺寸都不通过。
            if (p.IsLossless)
            {
                return candidateW >= original.Width && candidateH >= original.Height;
            }

            ImageBuffer shrunk;
            ImageBuffer restored;

            if (ctx.Category == TextureCategory.NormalMap)
            {
                shrunk = Resampler.ResampleNormalMap(original, candidateW, candidateH);
                restored = Resampler.UpsampleBilinear(shrunk, original.Width, original.Height);

                // Compare against the identically-encoded original so the metric measures
                // resampling loss only, not an encoding difference.
                // 与同样编码的原图比较，使指标只衡量重采样损失而非编码差异。
                var encodedOriginal = Resampler.EncodeNormals(original);

                ImageMetrics.NormalAngularError(
                    encodedOriginal, restored, out var meanDeg, out var p95Deg);

                if (meanDeg > p.normalAngleMeanDeg) return false;
                if (p95Deg > p.normalAngleP95Deg) return false;
                return true;
            }

            var premultiply = ctx.AlphaMode != AlphaMode.Opaque;
            shrunk = Resampler.Downsample(original, candidateW, candidateH, premultiply);
            restored = Resampler.UpsampleBilinear(shrunk, original.Width, original.Height);

            if (ctx.Category == TextureCategory.Grayscale)
            {
                var rmse = ImageMetrics.WorstChannelRmse255(original, restored, ctx.UsedChannels);
                return rmse <= p.grayscaleRmse255;
            }

            // Colour textures: structural similarity plus colour difference.
            // 颜色贴图：结构相似度 + 色差。
            var shortSide = original.ShortSide;

            if (shortSide >= QualityPresets.StructuralMetricIgnoreShortSide)
            {
                if (shortSide >= QualityPresets.MsSsimMinShortSide)
                {
                    var ms = ImageMetrics.MsSsim(original, restored);
                    if (ms < p.msSsimMin) return false;
                }
                else
                {
                    var ss = ImageMetrics.Ssim(original, restored);
                    if (ss < p.ssimMin) return false;
                }
            }

            ImageMetrics.DeltaE2000(original, restored, out var deMean, out var deP95);
            if (deMean > p.deltaE00Mean) return false;
            if (deP95 > p.deltaE00P95) return false;

            // Alpha fidelity, evaluated against every cutoff this texture is tested with.
            // alpha 保真度，针对该贴图被测试过的每一个 cutoff 逐一评估。
            switch (ctx.AlphaMode)
            {
                case AlphaMode.Cutout:
                    foreach (var cutoff in ctx.Cutoffs)
                    {
                        var iou = ImageMetrics.CutoutIoU(original, restored, cutoff);
                        if (iou < p.cutoutIoUMin) return false;
                    }

                    break;

                case AlphaMode.Blend:
                    var alphaRmse = ImageMetrics.AlphaRmse255(original, restored);
                    if (alphaRmse > p.blendAlphaRmse255) return false;
                    break;
            }

            return true;
        }

        /// <summary>
        /// Finds the smallest acceptable size for an island: a uniform binary search followed by
        /// independent per-axis refinement. Uniform-first avoids the common failure where an
        /// early aggressive shrink on one axis blocks progress on the other.
        /// 为岛寻找可接受的最小尺寸：先均匀二分搜索，再逐轴独立细化。
        /// 先均匀可避免常见的失败模式——过早在某一轴上激进缩小会阻碍另一轴的优化。
        /// </summary>
        public static Vector2Int FindOptimalSize(
            ImageBuffer original,
            EvaluationContext ctx,
            Vector2Int minSize,
            Vector2Int maxSize,
            Func<bool> cancellationCheck = null)
        {
            var p = ctx.Parameters;

            // Lossless: keep the original resolution untouched.
            // 无损：完全保持原始分辨率。
            if (p.IsLossless)
            {
                return new Vector2Int(original.Width, original.Height);
            }

            // Solid-colour short circuit: a uniform island carries no detail to preserve.
            // 纯色短路：统一颜色的岛没有需要保留的细节。
            if (Resampler.IsSolidColor(original, out _))
            {
                var side = Mathf.Min(4, Mathf.Min(original.Width, original.Height));
                return new Vector2Int(Mathf.Max(1, side), Mathf.Max(1, side));
            }

            maxSize = new Vector2Int(
                Mathf.Min(maxSize.x, original.Width),
                Mathf.Min(maxSize.y, original.Height));
            minSize = new Vector2Int(
                Mathf.Clamp(minSize.x, 1, maxSize.x),
                Mathf.Clamp(minSize.y, 1, maxSize.y));

            // Phase 1: uniform scale binary search on a normalised factor.
            // 阶段 1：在归一化因子上做均匀缩放二分搜索。
            var lo = 0f;
            var hi = 1f;
            var bestUniform = 1f;

            const int uniformIterations = 8;
            for (var i = 0; i < uniformIterations; i++)
            {
                if (cancellationCheck != null && cancellationCheck()) break;

                var mid = (lo + hi) * 0.5f;
                var w = ScaleAxis(maxSize.x, minSize.x, mid);
                var h = ScaleAxis(maxSize.y, minSize.y, mid);

                if (MeetsQuality(original, w, h, ctx))
                {
                    bestUniform = mid;
                    hi = mid;
                }
                else
                {
                    lo = mid;
                }
            }

            var curW = ScaleAxis(maxSize.x, minSize.x, bestUniform);
            var curH = ScaleAxis(maxSize.y, minSize.y, bestUniform);

            // Phase 2: refine each axis independently. Anisotropic islands, e.g. a long thin
            // strip, often tolerate far more shrinking along one axis than the other.
            // 阶段 2：逐轴独立细化。各向异性的岛（例如细长条带）
            // 往往在某一轴上能承受远大于另一轴的缩小幅度。
            curW = RefineAxis(original, ctx, curW, curH, minSize.x, true, cancellationCheck);
            curH = RefineAxis(original, ctx, curW, curH, minSize.y, false, cancellationCheck);

            return new Vector2Int(Mathf.Max(1, curW), Mathf.Max(1, curH));
        }

        private static int RefineAxis(
            ImageBuffer original,
            EvaluationContext ctx,
            int width,
            int height,
            int minValue,
            bool refineWidth,
            Func<bool> cancellationCheck)
        {
            var current = refineWidth ? width : height;
            var lo = minValue;
            var hi = current;
            var best = current;

            const int iterations = 6;
            for (var i = 0; i < iterations && lo < hi; i++)
            {
                if (cancellationCheck != null && cancellationCheck()) break;

                var mid = (lo + hi) / 2;
                if (mid < minValue) break;

                var w = refineWidth ? mid : width;
                var h = refineWidth ? height : mid;

                if (MeetsQuality(original, w, h, ctx))
                {
                    best = mid;
                    hi = mid;
                }
                else
                {
                    lo = mid + 1;
                }
            }

            return best;
        }

        private static int ScaleAxis(int maxValue, int minValue, float t)
        {
            var v = Mathf.RoundToInt(Mathf.Lerp(minValue, maxValue, t));
            return Mathf.Clamp(v, Mathf.Max(1, minValue), Mathf.Max(1, maxValue));
        }
    }
}
