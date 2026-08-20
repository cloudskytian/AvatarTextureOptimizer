using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Mathematics;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor
{
    /// <summary>
    /// Island scaling: binary search for the smallest scale that passes ALL metrics (per texture),
    /// then per-axis anisotropic refinement; wooden-barrel across textures; density band clamps;
    /// solid-color shortcut; near-lossless (quality=1) skips scaling entirely. /
    /// 岛缩放：二分搜索每张贴图达标的最小缩放，再逐轴细化（各向异性）；跨贴图木桶效应；密度带钳制；
    /// 纯色短路；近无损（质量=1）完全跳过缩放。
    /// </summary>
    internal static class AtoIslandScaler
    {
        private const int UniformIterations = 22;
        private const int AnisotropicIterations = 16;

        /// <summary>
        /// Compute the per-texture scales and the final shared UV rect for one island. /
        /// 计算一个岛的逐贴图缩放与最终共享 UV 矩形。
        /// </summary>
        public static void ScaleIsland(AtoContext ctx, AtoUvGroup uvGroup, AtoIsland island,
            AtoQualityEvaluator evaluator, AtoQualityThresholds thresholds, bool nearLossless)
        {
            var settings = ctx.State.Settings;
            var distinctTextures = uvGroup.Slots.Select(s => s.Texture).Distinct().ToList();
            var evaluations = new Dictionary<Texture2D, AtoTextureEvaluation>();

            foreach (var texture in distinctTextures)
            {
                if (ctx.IsWhitelisted(texture))
                {
                    // Whitelisted textures skip all optimization (incl. scaling). / 白名单贴图跳过一切优化（含缩放）。
                    island.PerTextureScale[texture] = Vector2.one;
                    continue;
                }

                if (uvGroup.Whitelisted &&
                    (island.NormalizationTranslation == Vector2Int.zero &&
                     (island.UvMax.x - island.UvMin.x > 1f + 1e-4f ||
                      island.UvMax.y - island.UvMin.y > 1f + 1e-4f)))
                {
                    // Multi-tile islands in whitelisted groups cannot be evaluated reliably →
                    // conservative: no shrink for this texture. / 白名单组中的跨 tile 岛无法可靠评估 →
                    // 保守：该贴图不缩放。
                    island.PerTextureScale[texture] = Vector2.one;
                    continue;
                }

                var eval = evaluator.Prepare(texture, island, uvGroup);
                evaluations[texture] = eval;

                if (nearLossless)
                {
                    // Quality = 1: no scaling, copy as-is (no resampling). / 质量=1：不缩放、原样拷贝（不重采样）。
                    island.PerTextureScale[texture] = Vector2.one;
                    continue;
                }

                if (eval.IsSolid)
                {
                    // Solid color shortcut: min(4, bbox short side). / 纯色短路：min(4, 包围盒短边)。
                    var shortSide = Mathf.Min(eval.BboxWidth, eval.BboxHeight);
                    var target = Mathf.Max(1, Mathf.Min(4, shortSide));
                    var sx = target / (float)eval.BboxWidth;
                    var sy = target / (float)eval.BboxHeight;
                    island.PerTextureScale[texture] = new Vector2(sx, sy);
                    continue;
                }

                // ---- uniform binary search ----
                var low = 1f / 1024f;
                var high = 1f;
                for (var i = 0; i < UniformIterations; i++)
                {
                    var mid = (low + high) * 0.5f;
                    if (evaluator.Evaluate(eval, mid, mid, thresholds, out _)) high = mid;
                    else low = mid;
                }
                var uniform = high;

                // ---- anisotropic refinement: per axis, binary search upward from uniform ----
                var ax = uniform;
                var ay = uniform;
                // x axis: fix y at uniform. / x 轴：y 固定为 uniform。
                low = uniform;
                high = 1f;
                for (var i = 0; i < AnisotropicIterations; i++)
                {
                    var mid = (low + high) * 0.5f;
                    if (evaluator.Evaluate(eval, mid, uniform, thresholds, out _)) high = mid;
                    else low = mid;
                }
                ax = high;
                // y axis: fix x at ax. / y 轴：x 固定为 ax。
                low = uniform;
                high = 1f;
                for (var i = 0; i < AnisotropicIterations; i++)
                {
                    var mid = (low + high) * 0.5f;
                    if (evaluator.Evaluate(eval, ax, mid, thresholds, out _)) high = mid;
                    else low = mid;
                }
                ay = high;

                island.PerTextureScale[texture] = new Vector2(ax, ay);
            }

            // ---- wooden barrel across textures: the largest size (smallest scale) wins ----
            var sFinal = Vector2.one;
            foreach (var texture in distinctTextures)
            {
                if (ctx.IsWhitelisted(texture)) continue;
                var scale = island.PerTextureScale[texture];
                sFinal.x = Mathf.Min(sFinal.x, scale.x);
                sFinal.y = Mathf.Min(sFinal.y, scale.y);
            }

            // ---- pixel density band: never shrink below min density (prevent blur) ----
            var minDensity = (float)(int)settings.minPixelDensity;
            var maxDensity = (float)(int)settings.maxPixelDensity;
            var densityFloor = new Vector2(0f, 0f);
            foreach (var texture in distinctTextures)
            {
                if (ctx.IsWhitelisted(texture)) continue;
                // density = (uvSize × texSize) / worldSize, per axis. / 密度 =（uv 尺寸 × 贴图尺寸）/ 世界尺寸，逐轴。
                var uvSize = island.UvMax - island.UvMin;
                var d0x = uvSize.x * texture.width / island.WorldSize.x;
                var d0y = uvSize.y * texture.height / island.WorldSize.y;
                densityFloor.x = Mathf.Max(densityFloor.x, d0x > 1e-6f ? minDensity / d0x : 0f);
                densityFloor.y = Mathf.Max(densityFloor.y, d0y > 1e-6f ? minDensity / d0y : 0f);
            }
            sFinal.x = Mathf.Clamp(sFinal.x, Mathf.Min(densityFloor.x, 1f), 1f);
            sFinal.y = Mathf.Clamp(sFinal.y, Mathf.Min(densityFloor.y, 1f), 1f);

            // Warn when density still exceeds the max band. / 密度仍超上限时告警。
            foreach (var texture in distinctTextures)
            {
                if (ctx.IsWhitelisted(texture)) continue;
                var uvSize = island.UvMax - island.UvMin;
                var d0x = uvSize.x * texture.width / island.WorldSize.x;
                if (d0x * sFinal.x > maxDensity)
                {
                    ctx.State.Notes.Add(ctx.State.Tr("warn.densityExceeded",
                        $"{uvGroup.DisplayName} island {island.Index}", (int)maxDensity));
                }
            }

            // ---- final shared UV rect (shrink from the bbox min corner; packing moves it later) ----
            var t = island.NormalizationTranslation;
            var baseMin = island.UvMin + new Vector2(t.x, t.y);
            var baseSize = island.UvMax - island.UvMin;
            island.FinalUvMin = baseMin;
            island.FinalUvMax = baseMin + new Vector2(baseSize.x * sFinal.x, baseSize.y * sFinal.y);

            // Free per-island evaluation resources. / 释放每岛评估资源。
            foreach (var eval in evaluations.Values) eval.Dispose();
        }

        /// <summary>
        /// Resample the island content from a texture to the target pixel size (for atlas
        /// composition). Premultiplied resampling with unpremultiplied output; normal maps are
        /// decoded/resampled/renormalized/re-encoded. / 把岛内容从贴图重采样到目标像素尺寸（供图集合成）。
        /// 预乘重采样、输出反预乘；法线贴图按 解码→重采样→重归一化→编码 处理。
        /// </summary>
        public static Color32[] ResampleToPixels(AtoTextureEvaluation eval, int targetW, int targetH)
        {
            var size = targetW * targetH;
            var down = new Unity.Collections.NativeArray<Unity.Mathematics.float4>(size, Unity.Collections.Allocator.TempJob);
            var output = new Color32[size];
            try
            {
                if (eval.NeedNormal)
                {
                    // decode → resize → renormalize → encode. / 解码 → 缩放 → 重归一化 → 编码。
                    var decoded = new Unity.Collections.NativeArray<Unity.Mathematics.float4>(
                        eval.BboxWidth * eval.BboxHeight, Unity.Collections.Allocator.TempJob);
                    try
                    {
                        var decodeJob = new AtoBurstKernels.NormalDecodeJob
                        {
                            Source = eval.ReferenceStraight,
                            Mask = eval.Mask,
                            Output = decoded,
                        };
                        decodeJob.Run(eval.BboxWidth * eval.BboxHeight);
                        var resizeJob = new AtoBurstKernels.BilinearResizeJob
                        {
                            Source = decoded,
                            SourceWidth = eval.BboxWidth,
                            SourceHeight = eval.BboxHeight,
                            DestWidth = targetW,
                            DestHeight = targetH,
                            Output = down,
                        };
                        resizeJob.Run(size);
                        for (var i = 0; i < size; i++)
                        {
                            var v = down[i];
                            var len = math.length(new Unity.Mathematics.float3(v.x, v.y, v.z));
                            if (len > 1e-6f) v = new Unity.Mathematics.float4(v.x / len, v.y / len, v.z / len, v.w);
                            output[i] = new Color32(
                                (byte)Mathf.RoundToInt((v.x * 0.5f + 0.5f) * 255f),
                                (byte)Mathf.RoundToInt((v.y * 0.5f + 0.5f) * 255f),
                                255, 255);
                        }
                        return output;
                    }
                    finally
                    {
                        decoded.Dispose();
                    }
                }

                // color/gray path: premultiplied resize, then unpremultiply + encode. /
                // 颜色/灰度路径：预乘缩放，再反预乘+编码。
                var resize = new AtoBurstKernels.BilinearResizeJob
                {
                    Source = eval.Reference,
                    SourceWidth = eval.BboxWidth,
                    SourceHeight = eval.BboxHeight,
                    DestWidth = targetW,
                    DestHeight = targetH,
                    Output = down,
                };
                resize.Run(size);
                for (var i = 0; i < size; i++)
                {
                    var v = down[i];
                    var a = v.w;
                    if (a > 1e-4f)
                    {
                        v = new Unity.Mathematics.float4(v.x / a, v.y / a, v.z / a, a);
                        if (eval.Srgb)
                        {
                            v = new Unity.Mathematics.float4(
                                AtoBurstKernels.LinearToSrgb(math.clamp(v.x, 0f, 1f)),
                                AtoBurstKernels.LinearToSrgb(math.clamp(v.y, 0f, 1f)),
                                AtoBurstKernels.LinearToSrgb(math.clamp(v.z, 0f, 1f)),
                                v.w);
                        }
                    }
                    else
                    {
                        // alpha=0 pixels keep whatever RGB the dilation provides; alpha stays 0. /
                        // alpha=0 像素保留外扩的 RGB；alpha 保持 0。
                        v = new Unity.Mathematics.float4(0f, 0f, 0f, 0f);
                    }
                    output[i] = new Color32(
                        (byte)Mathf.RoundToInt(math.clamp(v.x, 0f, 1f) * 255f),
                        (byte)Mathf.RoundToInt(math.clamp(v.y, 0f, 1f) * 255f),
                        (byte)Mathf.RoundToInt(math.clamp(v.z, 0f, 1f) * 255f),
                        (byte)Mathf.RoundToInt(math.clamp(v.w, 0f, 1f) * 255f));
                }
                return output;
            }
            finally
            {
                down.Dispose();
            }
        }
    }
}
