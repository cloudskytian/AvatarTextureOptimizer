// AvatarTextureOptimizer - QualityEvaluator
// EN: Core quality stage: per (island, texture) binary search of the UV scale factor (uniform, then dual-axis),
// with pixel-density clamps, pure-color shortcut, near-lossless skip, and UV-group bucket sizing.
// Metrics are computed on the island's ACTUAL coverage (triangle raster mask), not the bbox.
// CN: 核心质量阶段：每个 (岛, 贴图) 二分搜索 UV 缩放系数（先均匀后双轴），
//     含像素密度钳制、纯色短路、近无损跳过、UV 组木桶取最大。指标在岛的实际覆盖区（三角形栅格掩码）上计算。
using System;
using System.Collections.Generic;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer
{
    public static class QualityEvaluator
    {
        public const float MinScale = 1f / 64f;   // 二分下界
        public const int SearchIterations = 9;    // 二分迭代次数

        /// <summary>EN: Set by the build pass after the GPU self-test. / CN: 由构建流程在 GPU 自检后设置。</summary>
        public static bool GpuEnabled;

        /// <summary>EN: Runs the quality stage for all textures/islands. / CN: 对所有贴图/岛执行质量阶段。</summary>
        public static void Evaluate(AtoBuildState state)
        {
            var qp = state.Profile.EffectiveQuality();

            foreach (var tref in state.Textures)
            {
                if (tref.whitelisted || tref.specialUv) continue;
                foreach (var g in tref.uvGroups)
                {
                    foreach (var island in g.islands)
                    {
                        island.scales[tref] = EvaluateIsland(state, tref, island, qp);
                    }
                }
            }

            // EN: UV-group bucket: template size = max across textures of the group (spec: 木桶效应取最大).
            // CN: UV 组木桶：模板尺寸 = 组内各贴图目标尺寸的最大值。
            foreach (var g in state.UvGroups)
            {
                foreach (var island in g.islands)
                {
                    float tw = 0, th = 0;
                    foreach (var kv in island.scales)
                    {
                        var s = kv.Value;
                        if (s.skip || kv.Key.whitelisted) continue;
                        tw = Mathf.Max(tw, s.targetW);
                        th = Mathf.Max(th, s.targetH);
                    }
                    island.templateW = tw;
                    island.templateH = th;
                }
            }

            // EN: Per-texture uniform type scale per UV group (normal/mask atlases may shrink as a whole).
            // CN: 每贴图每 UV 组的均匀类型缩放（法线/蒙版图集可整体缩小）。
            int minPadding = Mathf.Max(4, state.Profile.padding);
            foreach (var tref in state.Textures)
            {
                if (tref.whitelisted || tref.specialUv) continue;
                tref.typeScale.Clear();
                foreach (var g in tref.uvGroups)
                {
                    float sT = 1f;
                    bool any = false;
                    foreach (var island in g.islands)
                    {
                        if (!island.scales.TryGetValue(tref, out var s)) continue;
                        if (s.skip) continue;
                        any = true;
                        float rw = island.templateW > 0 ? s.targetW / (float)island.templateW : 1f;
                        float rh = island.templateH > 0 ? s.targetH / (float)island.templateH : 1f;
                        sT = Mathf.Min(sT, Mathf.Min(rw, rh));
                    }
                    if (!any) { tref.typeScale[g] = 1f; continue; }
                    // EN: Clamp so padding stays >= min padding (spec: 满足最小 padding 的前提下可缩放).
                    // CN: 钳制以保证 padding 不小于最小 padding。
                    float padRatio = minPadding / (float)Mathf.Max(4, state.Profile.padding);
                    sT = Mathf.Clamp(sT, padRatio, 1f);
                    tref.typeScale[g] = sT;
                }
            }

            // EN: Whole-texture scale for non-atlas textures (atlas off, or skipAtlas).
            // CN: 非图集贴图的整图缩放（图集关闭，或跳图集贴图）。
            bool atlasOff = !state.Component.generateAtlases;
            foreach (var tref in state.Textures)
            {
                if (tref.whitelisted || tref.specialUv) continue;
                if (!atlasOff && !tref.skipAtlas) continue;
                float ws = 1f;
                foreach (var g in tref.uvGroups)
                {
                    foreach (var island in g.islands)
                    {
                        if (!island.scales.TryGetValue(tref, out var s)) continue;
                        if (s.skip) { ws = 1f; break; }
                        ws = Mathf.Min(ws, Mathf.Min(s.scaleX, s.scaleY));
                    }
                }
                tref.wholeScale = ws;
            }

            // EN: Aggregate per-(type group, usage) uniform scale: the MINIMUM across all member textures
            // (spec: 类型组内某一贴图类型所有岛的质量需求整体低于主色，则对应图集可缩放).
            // CN: 聚合 (类型组, 用途) 统一缩放：取全部成员贴图中的最小值（按需求）。
            foreach (var tg in state.TypeGroups)
            {
                tg.usageScale.Clear();
                var usages = new List<TextureUsage>();
                foreach (var t in tg.textures)
                    if (!usages.Contains(t.usage)) usages.Add(t.usage);
                foreach (var u in usages)
                {
                    float agg = 1f;
                    bool any = false;
                    foreach (var t in tg.textures)
                    {
                        if (t.usage != u || t.whitelisted || t.specialUv) continue;
                        foreach (var g in t.uvGroups)
                        {
                            if (!t.typeScale.TryGetValue(g, out float st)) continue;
                            any = true;
                            agg = Mathf.Min(agg, st);
                        }
                    }
                    tg.usageScale[u] = any ? agg : 1f;
                }
            }

            AtoLog.Detail("Quality evaluation done");
        }

        /// <summary>EN: Evaluates one (island, texture) pair: target size via binary search. / CN: 评估一个 (岛, 贴图) 对的目标尺寸。</summary>
        public static IslandScale EvaluateIsland(AtoBuildState state, TextureRef tref, Island island, QualityParams qp)
        {
            var result = new IslandScale();
            float rectW = Mathf.Max(1, island.fracRect.width * tref.width);
            float rectH = Mathf.Max(1, island.fracRect.height * tref.height);
            int shortSide = Mathf.Max(1, Mathf.RoundToInt(Mathf.Min(rectW, rectH)));
            result.shortSidePx = shortSide;

            // EN: Near-lossless: skip scaling entirely, copy as-is (spec).
            // CN: 近无损：完全跳过缩放，原样拷贝。
            if (qp.IsNearLossless)
            {
                result.skip = true;
                result.targetW = Mathf.RoundToInt(rectW);
                result.targetH = Mathf.RoundToInt(rectH);
                return result;
            }

            var pixelRect = new Rect(
                island.fracRect.x * tref.width,
                island.fracRect.y * tref.height,
                rectW, rectH);

            var source = TextureSampler.Sample(state, tref, pixelRect, out bool ok);
            if (!ok || source == null)
            {
                result.skip = true;
                return result;
            }

            // EN: Coverage mask at analysis resolution (actual triangle coverage, not bbox).
            // CN: 分析分辨率下的覆盖率掩码（实际三角形覆盖区，非包围盒）。
            int aw = Mathf.Max(2, Mathf.RoundToInt(Mathf.Min(source.width, MetricsCpu.MaxAnalysisResolution)));
            int ah = Mathf.Max(2, Mathf.RoundToInt(Mathf.Min(source.height, MetricsCpu.MaxAnalysisResolution)));
            byte[] mask = CoverageMask.Build(island, aw, ah);

            // EN: Pure color shortcut (spec: 目标质量不为 1 时纯色岛直接缩到 min(4, 短边)).
            // CN: 纯色短路（目标质量不为 1 时纯色岛直接缩到 min(4, 短边)）。
            if (MetricsCpu.IsPureColorMasked(source, null, 0.004f) || MetricsCpu.IsPureColorMasked(source, mask, 0.004f))
            {
                int target = Mathf.Min(4, shortSide);
                result.pureColorShortcut = true;
                float s = target / Mathf.Max(1f, shortSide);
                result.scaleX = result.scaleY = s;
                result.targetW = Mathf.Max(1, Mathf.RoundToInt(rectW * s));
                result.targetH = Mathf.Max(1, Mathf.RoundToInt(rectH * s));
                return result;
            }

            // EN: Pixel density (texels per meter) & clamps: don't shrink below minDensity, don't exceed
            // maxDensity or the source texel size (spec: 最小/最大像素密度 + 原图真实大小钳制).
            // CN: 像素密度（px/m）与钳制：不低于 minDensity，不超过 maxDensity 与源纹素大小。
            float density = Mathf.Sqrt((rectW * rectH) / Mathf.Max(1e-6f, island.worldAreaM2));
            float sMin = Mathf.Clamp(state.Profile.minPixelDensity / density, MinScale, 1f);
            float sMax = Mathf.Clamp(state.Profile.maxPixelDensity / density, MinScale, 1f);

            // EN: Binary search for the minimal passing scale (uniform), then dual-axis refinement.
            // CN: 二分搜索最小达标缩放（均匀），再做双轴细化。
            float s = BinarySearchUniform(state, tref, island, source, mask, qp, sMin, sMax);
            (float sx, float sy) = RefineAxes(state, tref, island, source, mask, qp, sMin, sMax, s);

            result.scaleX = sx;
            result.scaleY = sy;
            result.targetW = Mathf.Max(1, Mathf.RoundToInt(rectW * sx));
            result.targetH = Mathf.Max(1, Mathf.RoundToInt(rectH * sy));
            return result;
        }

        private static float BinarySearchUniform(AtoBuildState state, TextureRef tref, Island island,
            LinearImage source, byte[] mask, QualityParams qp, float sMin, float sMax)
        {
            // EN: If even the smallest allowed scale passes, take it; if the largest fails, keep 1 (identity).
            // CN: 若最小允许缩放即达标则取最小；若最大允许失败则保持原样。
            if (Passes(state, tref, island, source, mask, qp, sMin, sMin)) return sMin;
            if (!Passes(state, tref, island, source, mask, qp, sMax, sMax)) return 1f;

            float lo = sMin, hi = sMax;
            for (int i = 0; i < SearchIterations; i++)
            {
                float mid = (lo + hi) * 0.5f;
                if (Passes(state, tref, island, source, mask, qp, mid, mid)) hi = mid;
                else lo = mid;
            }
            return hi;
        }

        private static (float, float) RefineAxes(AtoBuildState state, TextureRef tref, Island island,
            LinearImage source, byte[] mask, QualityParams qp, float sMin, float sMax, float s)
        {
            float sx = s, sy = s;
            // EN: Refine X with Y fixed.
            // CN: 固定 Y 细化 X。
            if (Passes(state, tref, island, source, mask, qp, sMin, sy))
            {
                float lo = sMin, hi = sx;
                for (int i = 0; i < SearchIterations; i++)
                {
                    float mid = (lo + hi) * 0.5f;
                    if (Passes(state, tref, island, source, mask, qp, mid, sy)) hi = mid;
                    else lo = mid;
                }
                sx = hi;
            }
            // EN: Refine Y with X fixed.
            // CN: 固定 X 细化 Y。
            if (Passes(state, tref, island, source, mask, qp, sx, sMin))
            {
                float lo = sMin, hi = sy;
                for (int i = 0; i < SearchIterations; i++)
                {
                    float mid = (lo + hi) * 0.5f;
                    if (Passes(state, tref, island, source, mask, qp, sx, mid)) hi = mid;
                    else lo = mid;
                }
                sy = hi;
            }
            return (sx, sy);
        }

        /// <summary>
        /// EN: Round-trips the island to the candidate size and back, then compares all metrics (masked) against
        /// the reference. Linear space; premultiplied alpha for blend materials.
        /// CN: 岛往返候选尺寸后比较全部指标（带掩码）。线性空间；Blend 材质预乘 alpha。
        /// </summary>
        private static bool Passes(AtoBuildState state, TextureRef tref, Island island, LinearImage source,
            byte[] mask, QualityParams qp, float sx, float sy)
        {
            if (sx >= 1f && sy >= 1f) return true; // identity

            int origW = source.width, origH = source.height;
            int tw = Mathf.Max(1, Mathf.RoundToInt(origW * sx));
            int th = Mathf.Max(1, Mathf.RoundToInt(origH * sy));
            bool premul = tref.usage == TextureUsage.Albedo &&
                           tref.materials.Exists(m => m.StrictestMode == RenderMode.Blend);

            // EN: Reference & candidate at analysis resolution (CPU fallback; GPU path is full-res).
            // CN: 参考与候选在分析分辨率（CPU 回退；GPU 路径全分辨率）。
            float analysisScale = MetricsCpu.MaxAnalysisResolution / Mathf.Max(origW, origH);
            var refImg = analysisScale < 1f
                ? Resampler.Bilinear(source, Mathf.Max(1, Mathf.RoundToInt(origW * analysisScale)),
                    Mathf.Max(1, Mathf.RoundToInt(origH * analysisScale)), false)
                : source;
            var maskRef = analysisScale < 1f
                ? DownscaleMaskLinear(mask, source.width, source.height, refImg.width, refImg.height)
                : mask;

            // EN: Downsample with premultiplied filtering (spec: 透明贴图预乘 alpha 下采样); the upscale back to
            // analysis resolution interpolates the ALREADY-premultiplied values linearly (no second weighting).
            // CN: 预乘过滤下采样（按需求）；上采样回分析分辨率时对已预乘值做线性插值（不二次加权）。
            var work = Resampler.Bilinear(source, tw, th, premul);
            var cand = Resampler.Bilinear(work, refImg.width, refImg.height, false);
            if (premul)
            {
                // EN: Blend comparison: premultiply BOTH sides so RGB and alpha are evaluated consistently.
                // CN: Blend 比较：两侧统一预乘，RGB 与 alpha 一致评估。
                Resampler.Premultiply(refImg);
                Resampler.Premultiply(cand);
            }

            // EN: A texture may be referenced with different usages by different materials — the strictest
            // requirement wins (spec: 取质量最高要求最严苛的).
            // CN: 贴图可能被不同材质以不同用途引用——取最严苛要求（按需求）。
            bool anyFail = false;
            foreach (var u in DistinctUsages(tref))
            {
                switch (u)
                {
                    case TextureUsage.Albedo:
                        if (!PassesAlbedo(refImg, cand, maskRef, tref, qp)) anyFail = true;
                        break;
                    case TextureUsage.Normal:
                        var (mean, p95) = MetricsCpu.NormalAngleErrorMasked(refImg, cand, maskRef);
                        if (mean > qp.normalAngleMean || p95 > qp.normalAngleP95) anyFail = true;
                        break;
                    case TextureUsage.GrayMask:
                        if (MetricsCpu.GrayRmseUsedChannelsMasked(refImg, cand, maskRef) > qp.grayRmse) anyFail = true;
                        break;
                }
                if (anyFail) break;
            }
            return !anyFail;
        }

        private static System.Collections.Generic.List<TextureUsage> DistinctUsages(TextureRef tref)
        {
            var list = new System.Collections.Generic.List<TextureUsage> { tref.usage };
            foreach (var kv in tref.usageByMaterial)
            {
                if (!list.Contains(kv.Value)) list.Add(kv.Value);
            }
            return list;
        }

        private static bool PassesAlbedo(LinearImage refImg, LinearImage cand, byte[] mask, TextureRef tref,
            QualityParams qp)
        {
            int shortSide = Mathf.Min(refImg.width, refImg.height);

            // EN: MS-SSIM (single-scale SSIM below 176px; ignored below 11px) — spec thresholds.
            // GPU path when enabled & self-tested; CPU otherwise.
            // CN: MS-SSIM（短边 <176px 用单尺度 SSIM；<11px 忽略）——GPU 可用时用 GPU，否则 CPU。
            if (shortSide >= 11)
            {
                float sim;
                if (GpuEnabled)
                {
                    var shader = MetricsGpu.FindShader();
                    sim = shortSide < 176
                        ? MetricsGpu.SsimGpu(shader, refImg, cand, mask)
                        : MetricsGpu.MsSsimGpu(shader, refImg, cand, mask);
                    if (sim < 0) sim = shortSide < 176
                        ? MetricsCpu.SsimMasked(refImg, cand, mask)
                        : MetricsCpu.MsSsimMasked(refImg, cand, mask);
                }
                else
                {
                    sim = shortSide < 176
                        ? MetricsCpu.SsimMasked(refImg, cand, mask)
                        : MetricsCpu.MsSsimMasked(refImg, cand, mask);
                }
                if (sim < qp.ssim) return false;
            }

            if (MetricsCpu.DeltaE2000Masked(refImg, cand, mask) > qp.deltaE) return false;

            // EN: Alpha: strictest across all referencing materials (Cutout IoU at max cutoff / Blend RMSE).
            // CN: alpha：跨引用材质取最严苛（Cutout 最大 cutoff 的 IoU / Blend RMSE）。
            foreach (var mu in tref.materials)
            {
                if (mu.modes.Contains(RenderMode.Cutout))
                {
                    float cutoff = 0.5f;
                    foreach (var c in mu.cutoffs) cutoff = Mathf.Max(cutoff, c);
                    if (MetricsCpu.AlphaCutoutIouMasked(refImg, cand, cutoff, mask) < qp.alphaIou) return false;
                }
                else if (mu.modes.Contains(RenderMode.Blend))
                {
                    if (MetricsCpu.AlphaBlendRmseMasked(refImg, cand, mask) > qp.alphaRmse) return false;
                }
            }
            return true;
        }

        private static byte[] DownscaleMaskLinear(byte[] src, int srcW, int srcH, int dstW, int dstH)
        {
            var dst = new byte[dstW * dstH];
            for (int y = 0; y < dstH; y++)
            {
                int y0 = Mathf.Clamp(y * srcH / dstH, 0, srcH - 1);
                int y1 = Mathf.Clamp((y + 1) * srcH / dstH - 1, 0, srcH - 1);
                for (int x = 0; x < dstW; x++)
                {
                    int x0 = Mathf.Clamp(x * srcW / dstW, 0, srcW - 1);
                    int x1 = Mathf.Clamp((x + 1) * srcW / dstW - 1, 0, srcW - 1);
                    int cnt = 0;
                    for (int yy = y0; yy <= y1; yy++)
                        for (int xx = x0; xx <= x1; xx++)
                            if (src[yy * srcW + xx] > 0) cnt++;
                    dst[y * dstW + x] = (byte)(cnt > 0 ? 1 : 0);
                }
            }
            return dst;
        }
    }

    /// <summary>
    /// EN: Rasterizes an island's triangle coverage into a mask at the given resolution (edge functions, both
    /// windings, bias to seal shared edges).
    /// CN: 把岛的三角形覆盖区光栅化为指定分辨率的掩码（边函数、双绕向、偏差密封共享边）。
    /// </summary>
    public static class CoverageMask
    {
        public static byte[] Build(Island island, int w, int h)
        {
            var mask = new byte[w * h];
            var data = island.owner;
            if (data == null) return mask;
            var uvs = data.uvs;
            var allTris = data.allTriangles;
            if (allTris == null) return mask;

            float rw = Mathf.Max(1e-6f, island.fracRect.width);
            float rh = Mathf.Max(1e-6f, island.fracRect.height);

            foreach (var t in island.triangles)
            {
                int i0 = allTris[t * 3], i1 = allTris[t * 3 + 1], i2 = allTris[t * 3 + 2];
                Vector2 p0 = ToPixel(island, w, h, rw, rh, uvs, i0);
                Vector2 p1 = ToPixel(island, w, h, rw, rh, uvs, i1);
                Vector2 p2 = ToPixel(island, w, h, rw, rh, uvs, i2);
                RasterTriangle(mask, w, h, p0, p1, p2);
            }
            return mask;
        }

        private static Vector2 ToPixel(Island island, int w, int h, float rw, float rh, Vector2[] uvs, int idx)
        {
            Vector2 raw = uvs[idx];
            Vector2 local = new Vector2(raw.x - island.tile.x, raw.y - island.tile.y);
            float nx = (local.x - island.fracRect.x) / rw;
            float ny = (local.y - island.fracRect.y) / rh;
            return new Vector2(Mathf.Clamp01(nx) * w, Mathf.Clamp01(ny) * h);
        }

        private static void RasterTriangle(byte[] mask, int w, int h, Vector2 a, Vector2 b, Vector2 c)
        {
            float minX = Mathf.Max(0, Mathf.Min(a.x, Mathf.Min(b.x, c.x)) - 1);
            float maxX = Mathf.Min(w - 1, Mathf.Max(a.x, Mathf.Max(b.x, c.x)) + 1);
            float minY = Mathf.Max(0, Mathf.Min(a.y, Mathf.Min(b.y, c.y)) - 1);
            float maxY = Mathf.Min(h - 1, Mathf.Max(a.y, Mathf.Max(b.y, c.y)) + 1);
            if (maxX < minX || maxY < minY) return;

            float bias = 0.5f;
            for (int y = (int)minY; y <= (int)maxY; y++)
            {
                for (int x = (int)minX; x <= (int)maxX; x++)
                {
                    Vector2 p = new Vector2(x + 0.5f, y + 0.5f);
                    float e1 = (b.x - a.x) * (p.y - a.y) - (b.y - a.y) * (p.x - a.x);
                    float e2 = (c.x - b.x) * (p.y - b.y) - (c.y - b.y) * (p.x - b.x);
                    float e3 = (a.x - c.x) * (p.y - c.y) - (a.y - c.y) * (p.x - a.x);
                    bool inside = (e1 >= -bias && e2 >= -bias && e3 >= -bias) ||
                                  (e1 <= bias && e2 <= bias && e3 <= bias);
                    if (inside) mask[y * w + x] = 1;
                }
            }
        }
    }

    /// <summary>
    /// EN: Samples an island rect from a texture as a linear-space LinearImage (with sRGB decode as appropriate).
    /// CN: 从贴图采样岛矩形为线性空间 LinearImage（按需 sRGB 解码）。
    /// </summary>
    public static class TextureSampler
    {
        public static LinearImage Sample(AtoBuildState state, TextureRef tref, Rect pixelRect, out bool ok)
        {
            ok = false;
            var tex = tref.texture;
            if (tex == null) return null;
            var decoded = state.Decoder != null ? state.Decoder.Decode(tex) : null;
            if (decoded == null) return null;

            int x0 = Mathf.Clamp(Mathf.FloorToInt(pixelRect.xMin), 0, decoded.width - 1);
            int y0 = Mathf.Clamp(Mathf.FloorToInt(pixelRect.yMin), 0, decoded.height - 1);
            int x1 = Mathf.Clamp(Mathf.CeilToInt(pixelRect.xMax), x0 + 1, decoded.width);
            int y1 = Mathf.Clamp(Mathf.CeilToInt(pixelRect.yMax), y0 + 1, decoded.height);
            int w = x1 - x0, h = y1 - y0;
            if (w <= 0 || h <= 0) return null;

            var img = new LinearImage(w, h);
            var data = decoded.GetRawTextureData<Color32>();
            bool srgb = tref.sRGB;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    var c = data[(y0 + y) * decoded.width + (x0 + x)];
                    int i = (y * w + x) * 4;
                    img.rgba[i] = srgb ? MetricMath.SrgbToLinear(c.r) : c.r / 255f;
                    img.rgba[i + 1] = srgb ? MetricMath.SrgbToLinear(c.g) : c.g / 255f;
                    img.rgba[i + 2] = srgb ? MetricMath.SrgbToLinear(c.b) : c.b / 255f;
                    img.rgba[i + 3] = c.a / 255f;
                }
            }
            ok = true;
            return img;
        }
    }
}
