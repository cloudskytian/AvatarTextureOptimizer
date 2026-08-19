// Stage 5: per-island target-quality binary search scaling (uniform then per-axis),
// with solid-color short-circuit, density clamps and UV-group barrel effect.
// 阶段5：逐岛目标质量二分缩放（先均匀后双轴）、纯色短路、密度钳制与UV组木桶效应。
using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace net.fosa.ato.editor
{
    public static class QualityStage
    {
        private const int MinSsimSide = 11;      // ignore SSIM below / 低于则忽略SSIM
        private const int SingleScaleSide = 176; // fallback to single-scale SSIM below / 低于则单尺度

        public static void Run(AtoContext ctx)
        {
            using (AtoLog.Time("QualityStage", (l, ms) => ctx.Stats.StageTimes.Add((l, ms))))
            {
                AtoProgress.BeginStage(AtoL10n.Tr("stage.quality"));
                bool lossless = ctx.Quality.IsLossless;

                int total = ctx.Islands.Sum(kv => kv.Value.Count), done = 0;
                foreach (var kv in ctx.Islands)
                {
                    var textures = ctx.MappingTextures[kv.Key].Where(t => !t.Whitelisted).ToList();
                    if (textures.Count == 0) continue;

                    foreach (var isl in kv.Value)
                    {
                        AtoProgress.Step(done++ / (float)Math.Max(1, total), kv.Key.ToString());
                        EvaluateIsland(ctx, kv.Key, isl, textures, lossless);
                    }
                }
                ComputeGroupScales(ctx);
            }
        }

        private static void EvaluateIsland(AtoContext ctx, MappingKey key, Island isl,
            List<TexInfo> textures, bool lossless)
        {
            // reference texture = largest / 参考贴图取最大者
            var refTex = textures.OrderByDescending(t => (long)t.Tex.width * t.Tex.height).First().Tex;
            var rectRef = IslandPixelRect(isl, refTex.width, refTex.height);
            isl.SrcPixelMin = new Vector2Int(rectRef.x, rectRef.y);
            isl.SrcPixelSize = new Vector2Int(rectRef.width, rectRef.height);

            if (lossless)
            {
                // quality==1: no rescale, straight copy / 质量1：不重采样原样拷贝
                foreach (var t in textures) isl.Scale[t] = Vector2.one;
                return;
            }

            // density clamps / 密度钳制
            float worldSize = Mathf.Sqrt(Mathf.Max(isl.WorldAreaMax, 1e-12f));
            float srcPx = Mathf.Sqrt(Mathf.Max(1f, (float)rectRef.width * rectRef.height * // approx via bbox+uv fill
                Mathf.Clamp01(isl.UvArea / Mathf.Max(1e-9f, (isl.BBoxMax.x - isl.BBoxMin.x) * (isl.BBoxMax.y - isl.BBoxMin.y)))));
            float minD = (float)(int)ResolveDensity(ctx, true);
            float maxD = (float)(int)ResolveDensity(ctx, false);
            float scaleLo = worldSize > 1e-9f ? Mathf.Clamp01(minD * worldSize / Mathf.Max(srcPx, 1f)) : 0.01f;
            float scaleHi = worldSize > 1e-9f ? Mathf.Clamp01(maxD * worldSize / Mathf.Max(srcPx, 1f)) : 1f;
            if (scaleHi <= 0f) scaleHi = 1f;
            if (scaleLo > scaleHi) scaleLo = scaleHi;
            scaleLo = Mathf.Max(scaleLo, 2f / Mathf.Max(rectRef.width, rectRef.height)); // never below 2px

            foreach (var ti in textures)
            {
                var rect = IslandPixelRect(isl, ti.Tex.width, ti.Tex.height);
                if (rect.width < 2 || rect.height < 2) { isl.Scale[ti] = Vector2.one; continue; }

                var pixels = ctx.Pixels.Get(ti.Tex, ti.Role == TexRole.Normal, out var tw, out var th);
                DetectAlphaContent(ti, pixels, tw, th);

                var mask = BuildMask(isl, rect, ti.Tex.width, ti.Tex.height, key);

                // solid color short-circuit / 纯色短路
                if (IsSolid(pixels, tw, rect, mask))
                {
                    isl.IsSolid = true;
                    float shortSide = Mathf.Min(rect.width, rect.height);
                    float s = Mathf.Min(4f, shortSide) / shortSide;
                    isl.Scale[ti] = new Vector2(s, s);
                    mask.Dispose();
                    continue;
                }

                var orig = CropPixels(pixels, tw, rect);
                var inputs = BuildInputs(ti, rect);

                // uniform binary search / 均匀二分
                float pass = 1f, lo2 = scaleLo, hi2 = scaleHi;
                if (Check(ctx, ti, isl, rect, orig, mask, inputs, hi2)) // try density cap first / 先试密度上限
                {
                    pass = hi2;
                    for (int it = 0; it < 7 && hi2 - lo2 > 1f / 128f; it++)
                    {
                        float mid = (lo2 + hi2) * 0.5f;
                        if (Check(ctx, ti, isl, rect, orig, mask, inputs, mid)) { pass = mid; hi2 = mid; }
                        else lo2 = mid;
                    }
                }
                // density cap fails quality: cap wins (anti-waste guard), record cap
                // 密度上限即未达标：以上限为准（防浪费护栏）
                var scale = new Vector2(pass, pass);

                // per-axis refinement / 双轴独立细化
                scale = RefineAxis(ctx, ti, isl, rect, orig, mask, inputs, scale, true, scaleLo);
                scale = RefineAxis(ctx, ti, isl, rect, orig, mask, inputs, scale, false, scaleLo);

                isl.Scale[ti] = scale;
                orig.Dispose();
                mask.Dispose();
            }
        }

        private static AtoDensityStep ResolveDensity(AtoContext ctx, bool min)
        {
            var po = ctx.PlatformOverride;
            if (po != null && po.overrideEnabled) return min ? po.minDensity : po.maxDensity;
            return min ? ctx.Settings.minDensity : ctx.Settings.maxDensity;
        }

        private static Vector2 RefineAxis(AtoContext ctx, TexInfo ti, Island isl, RectInt rect,
            NativeArray<float4> orig, NativeArray<byte> mask, MetricInputs inputs,
            Vector2 current, bool xAxis, float lo)
        {
            float hi = xAxis ? current.x : current.y;
            if (hi - lo <= 1f / 128f) return current;
            float best = hi;
            for (int it = 0; it < 5 && hi - lo > 1f / 128f; it++)
            {
                float mid = (lo + hi) * 0.5f;
                var test = xAxis ? new Vector2(mid, current.y) : new Vector2(current.x, mid);
                if (Check2(ctx, ti, isl, rect, orig, mask, inputs, test)) { best = mid; hi = mid; }
                else lo = mid;
            }
            return xAxis ? new Vector2(best, current.y) : new Vector2(current.x, best);
        }

        private static bool Check(AtoContext ctx, TexInfo ti, Island isl, RectInt rect,
            NativeArray<float4> orig, NativeArray<byte> mask, MetricInputs inputs, float scale)
            => Check2(ctx, ti, isl, rect, orig, mask, inputs, new Vector2(scale, scale));

        private static bool Check2(AtoContext ctx, TexInfo ti, Island isl, RectInt rect,
            NativeArray<float4> orig, NativeArray<byte> mask, MetricInputs inputs, Vector2 scale)
        {
            var small = new Vector2Int(
                Mathf.Max(1, Mathf.CeilToInt(rect.width * scale.x)),
                Mathf.Max(1, Mathf.CeilToInt(rect.height * scale.y)));
            if (small.x >= rect.width && small.y >= rect.height) return true;

            bool premult = ti.HasAlphaContent && ti.Role == TexRole.Color;
            var degraded = Resampler.RoundTrip(ti.Tex, rect, small, premult, ti.Role == TexRole.Normal);
            try
            {
                var output = new NativeArray<float>(6, Allocator.TempJob);
                var job = new MetricsJob { Original = orig, Degraded = degraded, Mask = mask, In = inputs, Out = output };
                job.Run();
                var q = ctx.Quality;
                bool ok = output[0] >= q.minMsSsim - 1e-6f
                          && output[1] <= q.maxDeltaE00P95 + 1e-6f
                          && output[2] >= q.minAlphaCutoutIoU - 1e-6f
                          && output[3] <= q.maxAlphaBlendRmse + 1e-6f
                          && output[4] <= q.maxNormalAngleP95Deg + 1e-6f
                          && output[5] <= q.maxGrayRmse + 1e-6f;
                output.Dispose();
                return ok;
            }
            finally { degraded.Dispose(); }
        }

        private static MetricInputs BuildInputs(TexInfo ti, RectInt rect)
        {
            int shortSide = Mathf.Min(rect.width, rect.height);
            var cutoffs = ti.Cutoffs.Distinct().OrderBy(x => x).ToList();
            var inputs = new MetricInputs
            {
                Width = rect.width, Height = rect.height,
                Role = (int)ti.Role, UsedChannels = ti.UsedChannels == 0 ? (byte)0xF : ti.UsedChannels,
                EvalCutout = ti.AnyCutout && ti.HasAlphaContent,
                EvalBlend = ti.AnyBlend && ti.HasAlphaContent,
                SkipSsim = shortSide < MinSsimSide,
                SingleScaleSsim = shortSide < SingleScaleSide,
                CutoffCount = Mathf.Min(4, cutoffs.Count),
            };
            if (cutoffs.Count > 0) inputs.Cutoff1 = cutoffs[0];
            if (cutoffs.Count > 1) inputs.Cutoff2 = cutoffs[1];
            if (cutoffs.Count > 2) inputs.Cutoff3 = cutoffs[2];
            if (cutoffs.Count > 3) inputs.Cutoff4 = cutoffs[cutoffs.Count - 1];
            if (inputs.EvalCutout && inputs.CutoffCount == 0) { inputs.Cutoff1 = 0.5f; inputs.CutoffCount = 1; }
            return inputs;
        }

        internal static RectInt IslandPixelRect(Island isl, int w, int h)
        {
            int x0 = Mathf.Clamp(Mathf.FloorToInt(isl.BBoxMin.x * w) - 1, 0, w - 1);
            int y0 = Mathf.Clamp(Mathf.FloorToInt(isl.BBoxMin.y * h) - 1, 0, h - 1);
            int x1 = Mathf.Clamp(Mathf.CeilToInt(isl.BBoxMax.x * w) + 1, x0 + 1, w);
            int y1 = Mathf.Clamp(Mathf.CeilToInt(isl.BBoxMax.y * h) + 1, y0 + 1, h);
            return new RectInt(x0, y0, x1 - x0, y1 - y0);
        }

        private static NativeArray<byte> BuildMask(Island isl, RectInt rect, int texW, int texH, MappingKey key)
        {
            var data = IslandStage.UvCache[key];
            var grid = new BitGrid(rect.width, rect.height);
            foreach (var t0 in isl.Triangles)
            {
                Vector2 A = (data.Uv[data.Indices[t0]] + isl.Shift);
                Vector2 B = (data.Uv[data.Indices[t0 + 1]] + isl.Shift);
                Vector2 C = (data.Uv[data.Indices[t0 + 2]] + isl.Shift);
                A = new Vector2(A.x * texW - rect.x, A.y * texH - rect.y);
                B = new Vector2(B.x * texW - rect.x, B.y * texH - rect.y);
                C = new Vector2(C.x * texW - rect.x, C.y * texH - rect.y);
                Raster.FillTriangle(grid, A, B, C);
            }
            var mask = new NativeArray<byte>(rect.width * rect.height, Allocator.Persistent);
            for (int y = 0; y < rect.height; y++)
                for (int x = 0; x < rect.width; x++)
                    mask[y * rect.width + x] = grid.Get(x, y) ? (byte)1 : (byte)0;
            return mask;
        }

        private static NativeArray<float4> CropPixels(NativeArray<Color> pixels, int texW, RectInt rect)
        {
            var crop = new NativeArray<float4>(rect.width * rect.height, Allocator.Persistent);
            for (int y = 0; y < rect.height; y++)
                for (int x = 0; x < rect.width; x++)
                {
                    var c = pixels[(rect.y + y) * texW + rect.x + x];
                    crop[y * rect.width + x] = new float4(c.r, c.g, c.b, c.a);
                }
            return crop;
        }

        private static void DetectAlphaContent(TexInfo ti, NativeArray<Color> pixels, int w, int h)
        {
            if (ti.HasAlphaContent) return;
            int step = Mathf.Max(1, (w * h) / 65536); // sampled scan / 采样扫描
            for (int i = 0; i < w * h; i += step)
                if (pixels[i].a < 0.996f) { ti.HasAlphaContent = true; return; }
        }

        private static bool IsSolid(NativeArray<Color> pixels, int texW, RectInt rect, NativeArray<byte> mask)
        {
            Color? first = null;
            for (int y = 0; y < rect.height; y++)
                for (int x = 0; x < rect.width; x++)
                {
                    if (mask[y * rect.width + x] == 0) continue;
                    var c = pixels[(rect.y + y) * texW + rect.x + x];
                    if (first == null) { first = c; continue; }
                    var f = first.Value;
                    if (Mathf.Abs(c.r - f.r) > 0.004f || Mathf.Abs(c.g - f.g) > 0.004f ||
                        Mathf.Abs(c.b - f.b) > 0.004f || Mathf.Abs(c.a - f.a) > 0.004f) return false;
                }
            return first != null;
        }

        /// <summary>Barrel effect: island final scale = max need across its UV-group textures.
        /// 木桶效应：岛的最终缩放取UV组内所有贴图的最大需求。</summary>
        private static void ComputeGroupScales(AtoContext ctx)
        {
            foreach (var kv in ctx.Islands)
                foreach (var isl in kv.Value)
                {
                    var s = Vector2.zero;
                    foreach (var pair in isl.Scale) s = Vector2.Max(s, pair.Value);
                    if (s == Vector2.zero) s = Vector2.one;
                    isl.GroupScale = Vector2.Min(s, Vector2.one);
                    isl.RasterSize = new Vector2Int(
                        Mathf.Max(1, Mathf.CeilToInt(isl.SrcPixelSize.x * isl.GroupScale.x)),
                        Mathf.Max(1, Mathf.CeilToInt(isl.SrcPixelSize.y * isl.GroupScale.y)));
                }
        }
    }
}
