// ATOIslandScaler.cs — UV 岛质量缩放求解器 / UV island quality-scale solver.
// 说明：对每份岛引用求解满足目标质量的最大缩放（二分搜索，全部达标才算通过）：
//  - 先均匀缩放（等比）二分至达标，再双轴独立二分细化（各向异性）
//  - 像素密度钳制：按岛世界面积（含实例/动画/形态键最大面积）× 用户密度（默认最小 2048px/m、最大 4096px/m），
//    且不超过源贴图物理像素（scale ≤ 1）
//  - 纯色岛（目标质量不为 1 时）短路缩到 min(4, 包围盒短边)；目标质量 = 1（近无损）时跳过缩放、原样拷贝
//  - UV 组木桶效应：岛的各角色/各轴尺寸取组内最大（最保守），由调用方聚合
// Note: solves the largest scale satisfying the target quality per island ref (binary search; ALL metrics must pass):
// uniform scale first, then per-axis anisotropic refinement; pixel-density clamps from the island's max world area
// (instances/animation/morphs) × user density (default 2048~4096 px/m), never exceeding the source texture pixels;
// solid-color islands short-circuit to min(4, bbox short side) unless near-lossless; near-lossless copies as-is.
// The UV-group barrel effect (largest = most conservative per role/axis) is aggregated by the caller.

using System;
using Unity.Mathematics;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer
{
    /// <summary>岛质量缩放求解器。/ Island quality-scale solver.</summary>
    internal static class ATOIslandScaler
    {
        private const int BinaryIterations = 14;

        /// <summary>
        /// 求解一份引用的缩放（结果写入 ref.solvedScaleU/V）。
        /// Solve the scale of one ref (writes ref.solvedScaleU/V).
        /// </summary>
        public static void SolveRef(ATOIslandRef r, ATOIsland island, ATOQualityParams thresholds,
            ATOQualityEvaluator evaluator, ATOConfig config)
        {
            r.solved = true;
            r.solvedScaleU = 1f;
            r.solvedScaleV = 1f;

            // 白名单：不处理（保持原样）/ whitelisted: untouched
            if (r.whitelisted) return;

            var nativeW = Mathf.Max(1, r.cropRect.width);
            var nativeH = Mathf.Max(1, r.cropRect.height);

            // 1×1 无优化空间 / no room to optimize 1×1 crops
            if (nativeW <= 1 && nativeH <= 1) return;

            // 近无损（质量=1）：跳过缩放（含纯色），原样拷贝 / near-lossless: skip scaling (incl. solid), plain copy
            if (thresholds.lossless)
            {
                r.losslessCopy = true;
                return;
            }

            // 纯色短路 / solid-color short-circuit
            if (evaluator.IsSolid(r))
            {
                var minSide = Mathf.Min(nativeW, nativeH);
                var target = Mathf.Min(4f, minSide);
                var s = target / minSide;
                r.solvedScaleU = s;
                r.solvedScaleV = s;
                r.pureColor = true;
                return;
            }

            // 像素密度钳制（世界面积 × 密度；受到源物理像素钳制）/ pixel-density clamps
            var aspect = nativeW / (float)nativeH;
            var texelMin = island.worldAreaMax * config.minPixelDensity * config.minPixelDensity;
            var texelMax = island.worldAreaMax * config.maxPixelDensity * config.maxPixelDensity;
            var loU = island.worldAreaMax > 1e-8f ? Mathf.Sqrt(Mathf.Max(texelMin, 1f) * aspect) / nativeW : 0f;
            var loV = island.worldAreaMax > 1e-8f ? Mathf.Sqrt(Mathf.Max(texelMin, 1f) / aspect) / nativeH : 0f;
            var hiU = island.worldAreaMax > 1e-8f ? Mathf.Sqrt(Mathf.Max(texelMax, 1f) * aspect) / nativeW : 1f;
            var hiV = island.worldAreaMax > 1e-8f ? Mathf.Sqrt(Mathf.Max(texelMax, 1f) / aspect) / nativeH : 1f;
            hiU = Mathf.Min(1f, hiU);
            hiV = Mathf.Min(1f, hiV);
            loU = Mathf.Min(loU, hiU);
            loV = Mathf.Min(loV, hiV);

            // 均匀缩放二分 / uniform binary search
            var sU = BinarySearch(r, nativeW, nativeH, thresholds, evaluator, hiU);
            var sV = BinarySearch(r, nativeW, nativeH, thresholds, evaluator, hiV);

            // 双轴独立细化：先固定 U 细化 V，再固定 V 细化 U / per-axis refinement: fix U → refine V; fix V → refine U
            if (sU > 0f)
            {
                var v = BinarySearchAxis(r, nativeW, nativeH, thresholds, evaluator, sU, hiV, true);
                sV = Mathf.Max(sV, v);
            }
            if (sV > 0f)
            {
                var u = BinarySearchAxis(r, nativeW, nativeH, thresholds, evaluator, sV, hiU, false);
                sU = Mathf.Max(sU, u);
            }

            // 密度钳制（防发糊下限优先于质量搜索）/ density clamps (blur-proof floor applies after the search)
            r.solvedScaleU = Mathf.Clamp(sU, loU, hiU);
            r.solvedScaleV = Mathf.Clamp(sV, loV, hiV);
            // 至少 1px / at least 1 px
            r.solvedScaleU = Mathf.Max(r.solvedScaleU, 1f / nativeW);
            r.solvedScaleV = Mathf.Max(r.solvedScaleV, 1f / nativeH);
        }

        /// <summary>均匀缩放二分：找最大 s 使 PASS(s×native)。/ Uniform binary search: largest s with PASS(s×native).</summary>
        private static float BinarySearch(ATOIslandRef r, int nativeW, int nativeH,
            ATOQualityParams thresholds, ATOQualityEvaluator evaluator, float hi)
        {
            if (hi <= 0f) return 0f;
            var lo = 0f;
            for (int i = 0; i < BinaryIterations; i++)
            {
                var mid = (lo + hi) * 0.5f;
                var w = Mathf.Max(1, Mathf.RoundToInt(nativeW * mid));
                var h = Mathf.Max(1, Mathf.RoundToInt(nativeH * mid));
                var res = evaluator.Evaluate(r, w, h, thresholds);
                if (res.pass) lo = mid; else hi = mid;
            }
            return lo;
        }

        /// <summary>单轴二分：固定一轴，细化另一轴。/ Single-axis binary search: one axis fixed, refine the other.</summary>
        private static float BinarySearchAxis(ATOIslandRef r, int nativeW, int nativeH,
            ATOQualityParams thresholds, ATOQualityEvaluator evaluator, float fixedScale, float hi, bool searchV)
        {
            if (hi <= fixedScale) return fixedScale;
            var lo = fixedScale;
            for (int i = 0; i < BinaryIterations; i++)
            {
                var mid = (lo + hi) * 0.5f;
                int w, h;
                if (searchV)
                {
                    w = Mathf.Max(1, Mathf.RoundToInt(nativeW * fixedScale));
                    h = Mathf.Max(1, Mathf.RoundToInt(nativeH * mid));
                }
                else
                {
                    w = Mathf.Max(1, Mathf.RoundToInt(nativeW * mid));
                    h = Mathf.Max(1, Mathf.RoundToInt(nativeH * fixedScale));
                }
                var res = evaluator.Evaluate(r, w, h, thresholds);
                if (res.pass) lo = mid; else hi = mid;
            }
            return lo;
        }
    }
}
