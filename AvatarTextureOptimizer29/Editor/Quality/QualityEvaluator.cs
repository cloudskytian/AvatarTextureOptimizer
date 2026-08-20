// Quality-driven island scale search: density clamps (px/m with blendshape & animated
// scale factors, capped by original texture size) -> uniform binary search -> per-axis
// refinement; pure-color short-circuit; quality==1 skips scaling (spec).
// 质量驱动的岛缩放搜索：像素密度钳制（含形态键/动画缩放因子，且不超原尺寸）→
// 均匀二分 → 双轴独立细化；纯色短路；质量为1跳过缩放（需求书）。

using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace net.fosa.ato.editor
{
    internal static class QualityEvaluator
    {
        /// <summary>Whole-image scale for non-atlas textures. / 非图集贴图的整图缩放。</summary>
        internal static readonly Dictionary<Texture2D, float> WholeScale = new Dictionary<Texture2D, float>();

        internal static void Evaluate(AtoSession s)
        {
            using var _ = ATOLog.Scope("QualityScale");
            WholeScale.Clear();

            if (s.qualityIsOne)
            {
                ATOLog.Info("quality == 1: skipping UV scaling entirely (copy as-is)");
                foreach (var isl in s.islands)
                foreach (var t in isl.textures)
                    isl.scaledSize[t] = new Vector2Int(
                        Mathf.Max(1, Mathf.Ceil(isl.uvBounds.width * t.width)),
                        Mathf.Max(1, Mathf.Ceil(isl.uvBounds.height * t.height)));
                foreach (var kv in s.texInfos)
                    if (!kv.Value.whitelisted)
                        WholeScale[kv.Key] = 1f;
                return;
            }

            int done = 0, total = s.islands.Count;
            var perTextureScale = new Dictionary<(UvIsland, Texture2D), float2>();

            foreach (var isl in s.islands)
            {
                Progress.Report("quality", done / (float)Mathf.Max(1, total), $"island {done + 1}/{total}");
                done++;

                if (isl.textures.Count == 0) continue;

                foreach (var tex in isl.textures)
                {
                    if (!s.texInfos.TryGetValue(tex, out var ti) || ti.whitelisted) continue;

                    var cp = TexturePixels.Get(tex, ti.category == AtoTexCategory.Normal);
                    if (cp == null) { isl.scaledSize[tex] = new Vector2Int(4, 4); continue; }

                    var (sx, sy) = SearchIslandScale(s, isl, tex, ti, cp);
                    perTextureScale[(isl, tex)] = new float2(sx, sy);
                }

                // barrel effect: per-axis max across textures, capped at 1 / 木桶效应逐轴取最大
                float nx = 0f, ny = 0f;
                foreach (var tex in isl.textures)
                    if (perTextureScale.TryGetValue((isl, tex), out var sc))
                    {
                        nx = Mathf.Max(nx, sc.x);
                        ny = Mathf.Max(ny, sc.y);
                    }

                nx = Mathf.Clamp01(nx);
                ny = Mathf.Clamp01(ny);
                if (nx <= 0f || ny <= 0f) { nx = ny = 1f; } // no data -> keep / 无数据保持

                foreach (var tex in isl.textures)
                    isl.scaledSize[tex] = new Vector2Int(
                        Mathf.Max(1, Mathf.Ceil(isl.uvBounds.width * nx * tex.width)),
                        Mathf.Max(1, Mathf.Ceil(isl.uvBounds.height * ny * tex.height)));

                isl.pureColor = false; // per-texture handled in search / 纯色已在搜索中处理
            }

            // whole-image scaling for non-atlas textures / 非图集贴图整图缩放
            foreach (var kv in s.texInfos)
            {
                var tex = kv.Key;
                var ti = kv.Value;
                if (ti.whitelisted) continue;
                bool atlasPath = s.component.generateAtlas && !ti.forceNoAtlas && ti.eligibleForAtlas;
                if (atlasPath) continue;

                var cp = TexturePixels.Get(tex, ti.category == AtoTexCategory.Normal);
                if (cp == null) { WholeScale[tex] = 1f; continue; }

                float sLo = DensityLo(s, tex, cp.width, cp.height);
                float s = SearchWholeScale(s, tex, ti, cp, sLo);
                WholeScale[tex] = s;
                ATOLog.DebugL($"whole-scale {tex.name}: {s:F3}");
            }
        }

        // ------------------------------------------------------------------
        private static (float sx, float sy) SearchIslandScale(AtoSession s, UvIsland isl,
            Texture2D tex, TexInfo ti, CachedPixels cp)
        {
            // island bbox in this texture's pixels / 该贴图像素下的岛包围盒
            int bx = Mathf.Max(1, Mathf.RoundToInt(isl.uvBounds.width * cp.width));
            int by = Mathf.Max(1, Mathf.RoundToInt(isl.uvBounds.height * cp.height));
            int rx = Mathf.Clamp(Mathf.RoundToInt(isl.uvBounds.xMin * cp.width), 0, cp.width - 1);
            int ry = Mathf.Clamp(Mathf.RoundToInt(isl.uvBounds.yMin * cp.height), 0, cp.height - 1);
            bx = Mathf.Min(bx, cp.width - rx);
            by = Mathf.Min(by, cp.height - ry);

            // pure color short-circuit / 纯色短路
            if (TexturePixels.IsPureColor(cp, out _))
            {
                int target = Mathf.Min(4, Mathf.Min(bx, by));
                float s = (float)target / Mathf.Max(1, Mathf.Min(bx, by));
                return (s, s);
            }

            // density bounds / 密度边界
            float bboxArea = Mathf.Max(isl.uvBounds.width * isl.uvBounds.height, 1e-9f);
            float pxArea = bboxArea * cp.width * cp.height; // island bbox pixel area at s=1
            float worldA = Mathf.Max(isl.worldArea, 1e-9f);
            float sLo = Mathf.Min(1f, s.settings.minDensity * Mathf.Sqrt(worldA / pxArea));
            float sHi = Mathf.Min(1f, s.settings.maxDensity * Mathf.Sqrt(worldA / pxArea));
            if (sLo > sHi) sHi = sLo;

            EvalContext ctx = BuildContext(ti, cp, rx, ry, bx, by, isl);

            // uniform binary search / 均匀二分
            float s = BinarySearch(v => Passes(s, ctx, ti, v, v), sLo, sHi);

            // per-axis refinement / 双轴细化
            float fx = BinarySearch(v => Passes(s, ctx, ti, v * s, s), 0.5f, 1f, 2);
            float fy = BinarySearch(v => Passes(s, ctx, ti, s, v * s * fx), 0.5f, 1f, 2);
            // note: axis refinement keeps the other axis at the uniform result / 另一轴保持在均匀结果

            float finalX = Mathf.Clamp(s * fx, 0.02f, 1f), finalY = Mathf.Clamp(s * fy, 0.02f, 1f);
            return (finalX, finalY);
        }

        private static float SearchWholeScale(AtoSession s, Texture2D tex, TexInfo ti, CachedPixels cp)
        {
            if (TexturePixels.IsPureColor(cp, out _))
                return 4f / Mathf.Max(1, Mathf.Min(cp.width, cp.height)); // short-circuit to 4px / 短路到4px

            float sLo = DensityLo(s, tex, cp.width, cp.height);
            var ctx = BuildContext(ti, cp, 0, 0, cp.width, cp.height, null);
            return BinarySearch(v => Passes(s, ctx, ti, v, v), sLo, 1f);
        }

        private static float DensityLo(AtoSession s, Texture2D tex, int w, int h)
        {
            // whole texture: world area unknown per-texture; use conservative 1 m² per
            // texture default is wrong; instead use min scale that keeps 4px minimums.
            // For whole-image mode density uses the largest island world area referencing it.
            float worldA = 0f;
            foreach (var isl in s.islands)
                if (isl.textures.Contains(tex))
                    worldA = Mathf.Max(worldA, isl.worldArea);
            float pxArea = (float)w * h;
            if (worldA <= 1e-6f) return 0.02f;
            return Mathf.Min(1f, s.settings.minDensity * Mathf.Sqrt(worldA / pxArea));
        }

        private static float BinarySearch(Func<float, bool> pass, float lo, float hi, int iters = 7)
        {
            // find minimal scale in [lo,hi] that passes / 找最小通过缩放
            if (pass(lo)) return lo;
            if (lo >= hi) return hi;
            float best = hi;
            for (int i = 0; i < iters; i++)
            {
                float mid = 0.5f * (lo + hi);
                if (pass(mid)) { best = mid; hi = mid; }
                else lo = mid;
            }
            return best;
        }

        // ------------------------------------------------------------------
        private class EvalContext
        {
            internal CachedPixels cp;
            internal int rx, ry, rw, rh;      // eval region (after eval-scale cap) / 评估区域
            internal float evalScale;         // region cap factor / 区域降采样因子
            internal NativeArray<Color32> refPx;
            internal NativeArray<float> mask;
            internal float[] refLuma, testLumaBuffer;

            internal void Dispose()
            {
                if (refPx.IsCreated) refPx.Dispose();
                if (mask.IsCreated) mask.Dispose();
            }
        }

        private static EvalContext BuildContext(TexInfo ti, CachedPixels cp, int rx, int ry, int bw, int bh,
            UvIsland isl)
        {
            const int cap = 2048; // eval region cap / 评估区域上限
            float evalScale = Mathf.Min(1f, (float)cap / Mathf.Max(bw, bh));
            int rw = Mathf.Max(2, Mathf.RoundToInt(bw * evalScale));
            int rh = Mathf.Max(2, Mathf.RoundToInt(bh * evalScale));

            var ctx = new EvalContext
            {
                cp = cp, rx = rx, ry = ry, rw = rw, rh = rh, evalScale = evalScale,
                refPx = new NativeArray<Color32>(rw * rh, Allocator.Persistent),
                mask = new NativeArray<float>(rw * rh, Allocator.Persistent),
            };

            // reference region (area-averaged when capped) / 参考区域
            DownsampleNative(cp.pixels, cp.width, cp.height, rx, ry, bw, bh, rw, rh, evalScale == 1f,
                ti.category != AtoTexCategory.Normal, ctx.refPx);

            // coverage mask / 覆盖掩码
            if (isl != null) BuildMask(isl, ctx, rw, rh);
            else FillMask(ctx.mask, 1f);

            return ctx;
        }

        private static void FillMask(NativeArray<float> mask, float v)
        {
            for (int i = 0; i < mask.Length; i++) mask[i] = v;
        }

        private static void BuildMask(UvIsland isl, EvalContext ctx, int rw, int rh)
        {
            FillMask(ctx.mask, 0f);
            foreach (var g in isl.groups)
            {
                var md = g.ri.mesh;
                var uvList = new System.Collections.Generic.List<Vector2>();
                md.GetUVs(g.channel, uvList);
                if (uvList.Count == 0) continue;
                var uv = uvList.ToArray();
                var tris = md.triangles;

                // triangle raster into mask grid / 三角形光栅化进掩码
                foreach (var t in g.triangles)
                {
                    Vector2 a = ToMask(uv[tris[t * 3]], isl, ctx, rw, rh);
                    Vector2 b = ToMask(uv[tris[t * 3 + 1]], isl, ctx, rw, rh);
                    Vector2 c = ToMask(uv[tris[t * 3 + 2]], isl, ctx, rw, rh);
                    RasterTri(ctx.mask, rw, rh, a, b, c);
                }
            }

            // 1px dilation for edge coverage / 边缘一圈膨胀
            Dilate(ctx.mask, rw, rh);
        }

        private static Vector2 ToMask(Vector2 uvPos, UvIsland isl, EvalContext ctx, int rw, int rh)
        {
            float x = (uvPos.x - isl.uvBounds.xMin) / Mathf.Max(1e-9f, isl.uvBounds.width) * (rw - 1);
            float y = (uvPos.y - isl.uvBounds.yMin) / Mathf.Max(1e-9f, isl.uvBounds.height) * (rh - 1);
            return new Vector2(x, y);
        }

        private static void RasterTri(NativeArray<float> mask, int w, int h, Vector2 a, Vector2 b, Vector2 c)
        {
            int minX = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(a.x, Mathf.Min(b.x, c.x))), 0, w - 1);
            int maxX = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(a.x, Mathf.Max(b.x, c.x))), 0, w - 1);
            int minY = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(a.y, Mathf.Min(b.y, c.y))), 0, h - 1);
            int maxY = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(a.y, Mathf.Max(b.y, c.y))), 0, h - 1);
            for (int y = minY; y <= maxY; y++)
                for (int x = minX; x <= maxX; x++)
                {
                    var p = new Vector2(x + 0.5f, y + 0.5f);
                    if (PointInTri(p, a, b, c)) mask[y * w + x] = 1f;
                }
        }

        private static bool PointInTri(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float d1 = Cross(p, a, b), d2 = Cross(p, b, c), d3 = Cross(p, c, a);
            bool neg = d1 < 0 || d2 < 0 || d3 < 0;
            bool pos = d1 > 0 || d2 > 0 || d3 > 0;
            return !(neg && pos);
        }

        private static float Cross(Vector2 p, Vector2 a, Vector2 b) =>
            (b.x - p.x) * (a.y - p.y) - (b.y - p.y) * (a.x - p.x);

        private static void Dilate(NativeArray<float> mask, int w, int h)
        {
            var copy = new float[mask.Length];
            for (int i = 0; i < mask.Length; i++) copy[i] = mask[i];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    if (copy[y * w + x] > 0.5f) continue;
                    bool near = (x > 0 && copy[y * w + x - 1] > 0.5f) || (x < w - 1 && copy[y * w + x + 1] > 0.5f)
                        || (y > 0 && copy[(y - 1) * w + x] > 0.5f) || (y < h - 1 && copy[(y + 1) * w + x] > 0.5f);
                    if (near) mask[y * w + x] = 1f;
                }
        }

        /// <summary>Downsample a source region into native buffer (CPU twin of DownsampleJob
        /// used for building the reference; premultiply for color). Build the reference area-average
        /// when evalScale<1 (linear, premultiplied for alpha textures).
        /// 生成参考区域（区域降采样时做面积平均）。</summary>
        private static void DownsampleNative(Color32[] src, int srcW, int srcH, int rx, int ry,
            int bw, int bh, int dw, int dh, bool direct, bool premultiply, NativeArray<Color32> dst)
        {
            if (direct)
            {
                for (int y = 0; y < dh; y++)
                    for (int x = 0; x < dw; x++)
                        dst[y * dw + x] = src[(ry + y) * srcW + rx + x];
                return;
            }

            var job = new DownsampleJob
            {
                src = new NativeArray<Color32>(src, Allocator.TempJob),
                srcW = srcW, srcH = srcH,
                region = new int4(rx, ry, bw, bh),
                premultiply = premultiply,
                srgb = false, // reference kept raw; comparisons decode per-metric
                dst = new NativeArray<Color32>(dw * dh, Allocator.TempJob),
                dstSize = new NativeArray<int2>(1, Allocator.TempJob),
            };
            job.dstSize[0] = new int2(dw, dh);
            job.Schedule().Complete();
            job.dst.CopyTo(dst);
            job.src.Dispose();
            job.dst.Dispose();
            job.dstSize.Dispose();
        }

        // ------------------------------------------------------------------
        private static bool Passes(AtoSession s, EvalContext ctx, TexInfo ti, float sx, float sy)
        {
            int dw = Mathf.Max(1, Mathf.RoundToInt(ctx.rw * sx));
            int dh = Mathf.Max(1, Mathf.RoundToInt(ctx.rh * sy));

            using var down = new NativeArray<Color32>(dw * dh, Allocator.TempJob);
            using var up = new NativeArray<Color32>(ctx.rw * ctx.rh, Allocator.TempJob);
            using var dsSize = new NativeArray<int2>(1, Allocator.TempJob);
            using var upSize = new NativeArray<int2>(1, Allocator.TempJob);
            dsSize[0] = new int2(dw, dh);
            upSize[0] = new int2(ctx.rw, ctx.rh);

            bool premult = ti.hasAlphaContent;
            var dj = new DownsampleJob
            {
                src = ctx.refPx, srcW = ctx.rw, srcH = ctx.rh,
                region = new int4(0, 0, ctx.rw, ctx.rh),
                premultiply = premult, srgb = ctx.cp.srgb && ti.category != AtoTexCategory.Normal,
                dst = down, dstSize = dsSize,
            };
            dj.Schedule().Complete();

            var uj = new UpsampleJob { src = down, srcSize = new int2(dw, dh), dst = up, dstSize = upSize };
            uj.Schedule().Complete();

            return MetricsPass(s, ctx, ti, up);
        }

        private static bool MetricsPass(AtoSession s, EvalContext ctx, TexInfo ti, NativeArray<Color32> test)
        {
            var q = s.quality;
            var cat = ti.category;

            if (cat == AtoTexCategory.Normal)
            {
                using var res = new NativeArray<float2>(1, Allocator.TempJob);
                int layout = (int)ctx.cp.normalLayout;
                var j = new NormalAngleJob
                {
                    refPx = ctx.refPx, testPx = test, mask = ctx.mask,
                    refLayout = layout, testLayout = layout, result = res,
                };
                j.Schedule().Complete();
                return res[0].x <= q.normalAngleMeanMax && res[0].y <= q.normalAngleP95Max;
            }

            if (cat == AtoTexCategory.Gray)
            {
                using var res = new NativeArray<float>(1, Allocator.TempJob);
                bool4 used = new bool4(
                    ti.usedChannels.Contains(0), ti.usedChannels.Contains(1),
                    ti.usedChannels.Contains(2), ti.usedChannels.Contains(3));
                var j = new GrayRmseJob { refPx = ctx.refPx, testPx = test, mask = ctx.mask, usedChannels = used, result = res };
                j.Schedule().Complete();
                return res[0] <= q.grayRmseMax;
            }

            // color: MS-SSIM + dE (+ alpha) / 主色：MS-SSIM + ΔE（+ alpha）
            bool ok = true;
            bool ssimEligible = !MetricFactory.IgnoreSsim(Mathf.Min(ctx.rw, ctx.rh));
            if (ssimEligible)
            {
                int n = ctx.rw * ctx.rh;
                using var refL = new NativeArray<float>(n, Allocator.TempJob);
                using var testL = new NativeArray<float>(n, Allocator.TempJob);
                using var res = new NativeArray<float>(1, Allocator.TempJob);
                for (int i = 0; i < n; i++)
                {
                    var c0 = ctx.refPx[i];
                    var c1 = test[i];
                    refL[i] = Luma(c0);
                    testL[i] = Luma(c1);
                }

                var j = new SsimJob
                {
                    refLuma = refL, testLuma = testL, mask = ctx.mask,
                    width = ctx.rw, height = ctx.rh,
                    singleScale = MetricFactory.UseSingleScaleSsim(Mathf.Min(ctx.rw, ctx.rh)),
                    result = res,
                };
                j.Schedule().Complete();
                ok &= res[0] >= q.msssimMin;
            }

            using var de = new NativeArray<float2>(1, Allocator.TempJob);
            var dj2 = new DeltaEJob
            {
                refPx = ctx.refPx, testPx = test, mask = ctx.mask,
                refIsSrgb = ctx.cp.srgb, testIsSrgb = ctx.cp.srgb, result = de,
            };
            dj2.Schedule().Complete();
            ok &= de[0].x <= q.deltaEMeanMax && de[0].y <= q.deltaEP95Max;

            if (ti.hasAlphaContent)
            {
                // strictest across every referencing material / 逐引用材质取最严苛
                foreach (var mode in AlphaModesOf(ti))
                {
                    using var ar = new NativeArray<float2>(1, Allocator.TempJob);
                    var aj = new AlphaMetricsJob
                    {
                        refPx = ctx.refPx, testPx = test, mask = ctx.mask,
                        cutoff = mode.cutoff, result = ar,
                    };
                    aj.Schedule().Complete();
                    if (mode.mode == AlphaMode.Cutout) ok &= ar[0].x >= q.alphaCutoutIoUMin;
                    else if (mode.mode == AlphaMode.Blend) ok &= ar[0].y <= q.alphaBlendRmseMax;
                }
            }

            return ok;
        }

        private static float Luma(Color32 c) =>
            (0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b) / 255f;

        private static IEnumerable<(AlphaMode mode, float cutoff)> AlphaModesOf(TexInfo ti)
        {
            var seen = new HashSet<(AlphaMode, float)>();
            foreach (var u in ti.uses)
                if (u.kind == TexKind.Color)
                    seen.Add((u.alpha, u.cutoff));
            return seen;
        }

        internal static void DisposeTemp() { }
    }
}
