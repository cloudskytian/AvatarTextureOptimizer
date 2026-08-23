// -----------------------------------------------------------------------------
// ATOQuality.cs — island scale decision engine (binary search + density clamps).
// ATOQuality.cs — 岛缩放决策引擎（二分搜索 + 密度钳制）。
//
// Strategy per spec: uniform-scale binary search until all metrics pass, then
// per-axis refinement (anisotropy). Barrel rule: an island's decided size is the
// MAX over every texture it samples (never larger than the largest original).
// 按规格：先均匀缩放二分至全部达标，再双轴独立细化（各向异性）。木桶规则：
// 岛的最终尺寸取其采样所有贴图所需的最大值（不超过组内最大原尺寸）。
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace net.fosa.ato.editor
{
    internal static class ATOQuality
    {
        /// <summary>Decide the scaled size for one island (mutates island.scaledSize etc).
        /// 决定一个岛的缩放尺寸（写入 island.scaledSize 等）。</summary>
        public static void DecideIslandScale(IslandInfo isl, ATOBuildState st)
        {
            var q = st.settings.quality;

            // Reference size = largest texture dims mapped through uvBounds.
            // 参考尺寸 = 最大贴图尺寸经 uvBounds 映射。
            int refW = 0, refH = 0;
            foreach (var (tex, role) in isl.sampledTextures)
            {
                if (tex.SkipOptimization) continue;
                refW = Mathf.Max(refW, Mathf.CeilToInt(isl.uvBounds.width * tex.Width));
                refH = Mathf.Max(refH, Mathf.CeilToInt(isl.uvBounds.height * tex.Height));
            }

            isl.origSize = new Vector2Int(Mathf.Max(1, refW), Mathf.Max(1, refH));

            // ---- pure color short-circuit / 纯色短路 ----
            if (!q.IsLossless && IsPureColor(isl, st))
            {
                int shortSide = Mathf.Min(4, Mathf.Min(isl.origSize.x, isl.origSize.y));
                isl.pureColor = true;
                isl.scaledSize = new Vector2Int(
                    Mathf.Max(1, Mathf.RoundToInt(isl.origSize.x * (float)shortSide / Mathf.Min(isl.origSize.x, isl.origSize.y))),
                    Mathf.Max(1, Mathf.RoundToInt(isl.origSize.y * (float)shortSide / Mathf.Min(isl.origSize.x, isl.origSize.y))));
                isl.scaledSize = new Vector2Int(
                    Mathf.Min(isl.scaledSize.x, isl.origSize.x),
                    Mathf.Min(isl.scaledSize.y, isl.origSize.y));
                return;
            }

            // ---- lossless copy / 无损拷贝 ----
            if (q.IsLossless)
            {
                isl.losslessCopy = true;
                isl.scaledSize = isl.origSize;
                return;
            }

            // ---- density bounds / 密度边界 ----
            float d0 = isl.DensityPxPerMeter; // density at scale 1 / 缩放1时的密度
            float sHi = 1f;
            if (d0 > st.settings.maxDensity) sHi = Mathf.Clamp(d0 > 0 ? st.settings.maxDensity / d0 : 1f, 0.05f, 1f);
            float sLo = d0 > 0 && d0 < st.settings.minDensity
                ? Mathf.Clamp(st.settings.minDensity / d0, 1f, 4f) // may exceed 1 → capped to 1 below
                : 0.05f;
            sLo = Mathf.Min(sLo, sHi);

            // ---- uniform binary search / 均匀二分 ----
            float sMax = sHi;
            if (!Evaluate(isl, Vector2.one * sMax, st))
            {
                // Even the largest allowed scale fails → keep sMax (size bound wins; logged).
                // 最大允许缩放仍不达标 → 取 sMax（尺寸约束优先，已记录日志）。
                ATOLog.Debug($"island {isl.id}: quality unmet at s={sMax:F3}; keeping bound");
                isl.scaledSize = ScaleTo(isl.origSize, Vector2.one * sMax);
                return;
            }

            float lo = sLo, hi = sMax; // hi passes / hi 达标
            for (int it = 0; it < 7; it++)
            {
                float mid = 0.5f * (lo + hi);
                if (Evaluate(isl, Vector2.one * mid, st)) hi = mid;
                else lo = mid;
            }

            Vector2 s = Vector2.one * hi;

            // ---- per-axis refinement / 双轴独立细化 ----
            for (int axis = 0; axis < 2; axis++)
            {
                float alo = Mathf.Max(0.25f * s[axis], sLo * 0.5f);
                float ahi = s[axis];
                if (ahi - alo < 0.02f) continue;
                if (!Evaluate(isl, WithAxis(s, axis, alo), st)) continue; // floor fails → skip
                for (int it = 0; it < 5; it++)
                {
                    float mid = 0.5f * (alo + ahi);
                    if (Evaluate(isl, WithAxis(s, axis, mid), st)) ahi = mid;
                    else alo = mid;
                }

                s = WithAxis(s, axis, ahi);
            }

            isl.scaledSize = ScaleTo(isl.origSize, s);
        }

        private static Vector2 WithAxis(Vector2 v, int axis, float val)
        {
            if (axis == 0) return new Vector2(val, v.y);
            return new Vector2(v.x, val);
        }

        private static Vector2Int ScaleTo(Vector2Int orig, Vector2 s)
        {
            // Never upscale, never below 2px / 不放大、不低于2px
            int w = Mathf.Clamp(Mathf.RoundToInt(orig.x * s.x), 2, orig.x);
            int h = Mathf.Clamp(Mathf.RoundToInt(orig.y * s.y), 2, orig.y);
            return new Vector2Int(w, h);
        }

        // ================================================================= //
        // Pure-color / 纯色
        // ================================================================= //

        private static bool IsPureColor(IslandInfo isl, ATOBuildState st)
        {
            foreach (var (tex, role) in isl.sampledTextures)
            {
                if (tex.SkipOptimization) continue;
                var buf = GetBuffer(tex, st);
                if (buf == null) return false;
                var r = IslandRect(isl, tex);
                using var src = new NativeArray<Color32>(
                    CopyRect(buf, r), Allocator.TempJob);
                using var outp = new NativeArray<float>(1, Allocator.TempJob);
                new ATOQualityJobs.PureColorJob { src = src, n = src.Length, result = outp }
                    .Run();
                if (outp[0] < 0.5f) return false;
            }

            return true;
        }

        // ================================================================= //
        // Full evaluation of one candidate scale / 候选缩放全量评估
        // ================================================================= //

        /// <summary>Evaluate all metrics at a scale for every sampled texture.
        /// 对每个被采样贴图按该缩放评估全部指标。</summary>
        public static bool Evaluate(IslandInfo isl, Vector2 scale, ATOBuildState st)
        {
            var q = st.settings.quality;
            foreach (var (tex, role) in isl.sampledTextures)
            {
                if (tex.SkipOptimization) continue;
                if (!EvaluateTexture(isl, tex, scale, q, st)) return false;
            }

            return true;
        }

        private static bool EvaluateTexture(IslandInfo isl, TexInfo tex, Vector2 scale,
            ATOQualityParams q, ATOBuildState st)
        {
            var buf = GetBuffer(tex, st);
            if (buf == null) return true; // unreadable → assume pass (fallback-safe)
            var r = IslandRect(isl, tex);
            int dw = Mathf.Max(1, Mathf.RoundToInt(r.width * scale.x));
            int dh = Mathf.Max(1, Mathf.RoundToInt(r.height * scale.y));

            var orig = CopyRect(buf, r);

            // Normals: decode → average/renormalize resample → encode → compare angles.
            // 法线：解码→均值重归一化重采样→编码→角度对比。
            if (tex.texClass == TexClass.NormalMap)
                return EvaluateNormal(orig, r.width, r.height, dw, dh, tex, q);

            // Color / gray path / 颜色与灰度路径
            bool alpha = tex.texClass == TexClass.AlbedoAlpha;
            var small = new Color32[dw * dh];
            var up = new Color32[orig.Length];

            using (var srcNa = new NativeArray<Color32>(orig, Allocator.TempJob))
            using (var dstNa = new NativeArray<Color32>(small, Allocator.TempJob))
            {
                new ATOQualityJobs.DownsampleJob
                {
                    src = srcNa, srcW = r.width, srcH = r.height,
                    srcX = 0, srcY = 0, srcWd = r.width, srcHt = r.height,
                    dstW = dw, dstH = dh, premultiply = alpha, dst = dstNa,
                }.Schedule(dstNa.Length, 64).Complete();
                dstNa.CopyTo(small);
            }

            using (var srcNa = new NativeArray<Color32>(small, Allocator.TempJob))
            using (var dstNa = new NativeArray<Color32>(up, Allocator.TempJob))
            {
                new ATOQualityJobs.UpsampleJob
                {
                    src = srcNa, srcW = dw, srcH = dh, dstW = r.width, dstH = r.height, dst = dstNa,
                }.Schedule(dstNa.Length, 64).Complete();
                dstNa.CopyTo(up);
            }

            int n = orig.Length;

            if (tex.texClass == TexClass.GrayMask)
            {
                using var aNa = new NativeArray<Color32>(orig, Allocator.TempJob);
                using var bNa = new NativeArray<Color32>(up, Allocator.TempJob);
                using var outNa = new NativeArray<float>(1, Allocator.TempJob);
                new ATOQualityJobs.GrayJob
                {
                    a = aNa, b = bNa, n = n,
                    usedChannels = GrayUsedChannels(tex),
                    result = outNa,
                }.Run();
                return outNa[0] <= q.grayRmse;
            }

            // ---- color metrics (premultiplied for alpha textures) ----
            // ---- 颜色指标（alpha 贴图按预乘比较，稳定且感知合理） ----
            if (alpha)
            {
                PremultInPlace(orig);
                PremultInPlace(up);
            }

            // MS-SSIM (with small-island fallbacks handled inside) / MS-SSIM（内部处理小岛回退）
            using (var aNa = new NativeArray<Color32>(orig, Allocator.TempJob))
            using (var bNa = new NativeArray<Color32>(up, Allocator.TempJob))
            using (var outNa = new NativeArray<float>(1, Allocator.TempJob))
            {
                new ATOQualityJobs.MsSsimJob
                {
                    a = aNa, b = bNa, width = r.width, height = r.height, result = outNa,
                }.Run();
                if (outNa[0] < q.msSsim) return false;
            }

            // ΔE00 / 色差
            using (var aNa = new NativeArray<Color32>(orig, Allocator.TempJob))
            using (var bNa = new NativeArray<Color32>(up, Allocator.TempJob))
            using (var outNa = new NativeArray<float>(1, Allocator.TempJob))
            {
                new ATOQualityJobs.DeltaEJob { a = aNa, b = bNa, n = n, result = outNa }.Run();
                if (outNa[0] > q.deltaE) return false;
            }

            // Alpha / 透明度（逐材质最严：cutout→每个cutoff的IoU；blend→RMSE）
            // Alpha metrics: alpha channel is untouched by premult, so evaluate directly.
            // Strictest rule: IoU at every cutoff seen (cutout), plus RMSE (blend & cutout).
            // alpha 指标：预乘不改动 alpha，可直接评估。最严规则：cutout 对每个 cutoff 的
            // IoU，叠加 blend/cutout 的 RMSE。
            if (alpha)
            {
                foreach (var (mat, (mode, cutoffs)) in tex.alphaUsage)
                {
                    if (mode == AlphaMode.Cutout || (cutoffs != null && cutoffs.Count > 0))
                    {
                        var list = cutoffs != null && cutoffs.Count > 0 ? cutoffs : new List<float> { 0.5f };
                        foreach (var c in list)
                            if (!EvalAlpha(orig, up, n, c, q, cutout: true)) return false;
                    }

                    if (mode == AlphaMode.Blend || mode == AlphaMode.Cutout)
                        if (!EvalAlpha(orig, up, n, 0f, q, cutout: false)) return false;
                }
            }

            return true;
        }

        private static bool EvalAlpha(Color32[] a, Color32[] b, int n, float cutoff,
            ATOQualityParams q, bool cutout)
        {
            using var aNa = new NativeArray<Color32>(a, Allocator.TempJob);
            using var bNa = new NativeArray<Color32>(b, Allocator.TempJob);
            using var outNa = new NativeArray<float>(2, Allocator.TempJob);
            new ATOQualityJobs.AlphaJob
            {
                a = aNa, b = bNa, n = n, cutoff = cutoff, result = outNa,
            }.Run();
            return cutout ? outNa[0] >= q.alphaIou : outNa[1] <= q.alphaRmse;
        }

        private static void PremultInPlace(Color32[] px)
        {
            for (int i = 0; i < px.Length; i++)
            {
                var c = px[i];
                px[i] = new Color32(
                    (byte)(c.r * c.a / 255), (byte)(c.g * c.a / 255), (byte)(c.b * c.a / 255), c.a);
            }
        }

        // ================================================================= //
        // Normal evaluation / 法线评估
        // ================================================================= //

        private static bool EvaluateNormal(Color32[] orig, int w, int h, int dw, int dh,
            TexInfo tex, ATOQualityParams q)
        {
            var srcFormat = tex.source != null ? tex.source.format : TextureFormat.RGBA32;
            var decoded = new Color32[orig.Length];
            for (int i = 0; i < orig.Length; i++) decoded[i] = ATONormalCodec.EncodeRgb(ATONormalCodec.Decode(orig[i], srcFormat));

            // Downsample by vector averaging + renormalization (CPU; islands are small).
            // 向量均值+重归一化降采样（CPU；岛较小）。
            var small = new Color32[dw * dh];
            for (int y = 0; y < dh; y++)
            for (int x = 0; x < dw; x++)
            {
                // 2x2-area average approximation / 近似 2x2 均值
                var acc = Vector3.zero;
                int cnt = 0;
                for (int sy = 0; sy < 2; sy++)
                for (int sx = 0; sx < 2; sx++)
                {
                    int ox = Mathf.Min(w - 1, x * w / dw + sx);
                    int oy = Mathf.Min(h - 1, y * h / dh + sy);
                    var c = decoded[oy * w + ox];
                    var v = ATONormalCodec.Decode(c, TextureFormat.RGBA32);
                    if (v.sqrMagnitude > 0.1f) { acc += v; cnt++; }
                }

                small[y * dw + x] = cnt > 0
                    ? ATONormalCodec.EncodeRgb(acc.normalized)
                    : new Color32(128, 128, 255, 255);
            }

            // bilinear upsample on decoded vectors / 解码向量双线性上采样
            var up = new Color32[orig.Length];
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float fx = (x + 0.5f) * dw / w - 0.5f;
                float fy = (y + 0.5f) * dh / h - 0.5f;
                int x0 = Mathf.Clamp((int)Mathf.Floor(fx), 0, dw - 1);
                int y0 = Mathf.Clamp((int)Mathf.Floor(fy), 0, dh - 1);
                int x1 = Mathf.Min(x0 + 1, dw - 1), y1 = Mathf.Min(y0 + 1, dh - 1);
                float tx = Mathf.Clamp01(fx - x0), ty = Mathf.Clamp01(fy - y0);
                var a = ATONormalCodec.Decode(small[y0 * dw + x0], TextureFormat.RGBA32);
                var b = ATONormalCodec.Decode(small[y0 * dw + x1], TextureFormat.RGBA32);
                var c2 = ATONormalCodec.Decode(small[y1 * dw + x0], TextureFormat.RGBA32);
                var d = ATONormalCodec.Decode(small[y1 * dw + x1], TextureFormat.RGBA32);
                var v = Vector3.Lerp(Vector3.Lerp(a, b, tx), Vector3.Lerp(c2, d, tx), ty);
                up[y * w + x] = ATONormalCodec.EncodeRgb(v.sqrMagnitude > 1e-4f ? v.normalized : Vector3.forward);
            }

            using var aNa = new NativeArray<Color32>(decoded, Allocator.TempJob);
            using var bNa = new NativeArray<Color32>(up, Allocator.TempJob);
            using var outNa = new NativeArray<float>(2, Allocator.TempJob);
            new ATOQualityJobs.NormalJob { a = aNa, b = bNa, n = orig.Length, result = outNa }.Run();
            return outNa[0] <= q.normalAngleMean && outNa[1] <= q.normalAngleP95;
        }

        // ================================================================= //
        // helpers
        // ================================================================= //

        /// <summary>Island pixel rect on a texture / 岛在某贴图上的像素矩形。</summary>
        internal static RectInt IslandRect(IslandInfo isl, TexInfo tex)
        {
            int x = Mathf.Clamp(Mathf.FloorToInt(isl.uvBounds.xMin * tex.Width), 0, tex.Width - 1);
            int y = Mathf.Clamp(Mathf.FloorToInt(isl.uvBounds.yMin * tex.Height), 0, tex.Height - 1);
            int xm = Mathf.Clamp(Mathf.CeilToInt(isl.uvBounds.xMax * tex.Width), x + 1, tex.Width);
            int ym = Mathf.Clamp(Mathf.CeilToInt(isl.uvBounds.yMax * tex.Height), y + 1, tex.Height);
            return new RectInt(x, y, xm - x, ym - y);
        }

        private static bool4 GrayUsedChannels(TexInfo tex)
        {
            // Masks conventionally use R; include channels with actual variance.
            // 蒙版惯例使用R；同时纳入有实际变化的通道。
            var buf = tex.buffer;
            if (buf == null) return new bool4(true, false, false, false);
            byte r0 = 0, g0 = 0, b0 = 0, a0 = 0;
            bool gr = false, gg = false, gb = false, ga = false;
            int step = Mathf.Max(1, buf.pixels.Length / 4096);
            for (int i = 0; i < buf.pixels.Length; i += step)
            {
                var c = buf.pixels[i];
                if (i == 0) { r0 = c.r; g0 = c.g; b0 = c.b; a0 = c.a; continue; }
                if (c.r != r0) gr = true;
                if (c.g != g0) gg = true;
                if (c.b != b0) gb = true;
                if (c.a != a0) ga = true;
            }

            return new bool4(true, gg, gb, ga) | new bool4(gr, false, false, false);
        }

        private static Color32[] CopyRect(PixelBuffer buf, RectInt r)
        {
            var outp = new Color32[r.width * r.height];
            for (int y = 0; y < r.height; y++)
            {
                int srcRow = (r.y + y) * buf.width + r.x;
                for (int x = 0; x < r.width; x++) outp[y * r.width + x] = buf.pixels[srcRow + x];
            }

            return outp;
        }

        /// <summary>Lazy readable linear buffer / 懒加载可读线性缓冲。</summary>
        internal static PixelBuffer GetBuffer(TexInfo tex, ATOBuildState st)
        {
            if (tex.buffer != null) return tex.buffer;
            if (tex.source == null) return null;
            var raw = ATOGpu.ReadPixelsRaw(tex.source, st.gpu);
            if (raw == null || raw.Length == 0)
            {
                tex.buffer = null;
                return null;
            }

            tex.buffer = ATOGpu.ToLinearBuffer(raw, tex.Width, tex.Height, tex.IsSRGB);
            return tex.buffer;
        }

        /// <summary>Free cached pixels when memory pressure matters (called between stages).
        /// 阶段之间释放像素缓存以控制内存。</summary>
        internal static void ReleaseBuffers(ATOBuildState st)
        {
            foreach (var t in st.textures)
                if (t.buffer != null && !t.wholeScaled && t.atlasified)
                    t.buffer = null;
        }
    }

    /// <summary>Normal map channel codec (source-format aware decode, target-format aware encode).
    /// 法线通道编解码（按源格式解码、按目标格式编码）。</summary>
    internal static class ATONormalCodec
    {
        /// <summary>Decode a stored normal to a unit vector per source format layout.
        /// 按源格式布局解码为单位向量。</summary>
        public static Vector3 Decode(Color32 c, TextureFormat fmt)
        {
            float x, y;
            switch (fmt)
            {
                case TextureFormat.DXT5:
                case TextureFormat.DXT5Crunched:
                case TextureFormat.BC7:
                    x = c.a / 127.5f - 1f;  // DXTnm: X in A / X 存于 A
                    y = c.g / 127.5f - 1f;
                    break;
                case TextureFormat.BC5:
                    x = c.r / 127.5f - 1f;  // BC5: RG / RG 布局
                    y = c.g / 127.5f - 1f;
                    break;
                default:
                    x = c.r / 127.5f - 1f;  // plain RGB / 普通 RGB
                    y = c.g / 127.5f - 1f;
                    break;
            }

            float z = Mathf.Sqrt(Mathf.Clamp01(1f - x * x - y * y));
            var v = new Vector3(x, y, z);
            return v.sqrMagnitude > 1e-4f ? v.normalized : Vector3.forward;
        }

        /// <summary>Encode as plain RGB (internal working representation).
        /// 编码为普通 RGB（内部工作表示）。</summary>
        public static Color32 EncodeRgb(Vector3 n)
        {
            n = n.normalized;
            return new Color32(
                (byte)Mathf.RoundToInt((n.x * 0.5f + 0.5f) * 255f),
                (byte)Mathf.RoundToInt((n.y * 0.5f + 0.5f) * 255f),
                (byte)Mathf.RoundToInt((n.z * 0.5f + 0.5f) * 255f),
                255);
        }

        /// <summary>Encode for a target compressed format & platform (AG for PC DXTnm,
        /// RG otherwise). / 按目标压缩格式与平台编码（PC DXTnm 为 AG，否则 RG）。</summary>
        public static Color32 EncodeFor(Color32 rgb, TextureFormat target, bool pcDxtnm)
        {
            if (pcDxtnm && (target == TextureFormat.DXT5 || target == TextureFormat.DXT5Crunched ||
                            target == TextureFormat.BC7))
                return new Color32(255, rgb.g, 255, rgb.r); // AG: X→A, Y→G / AG 布局
            return new Color32(rgb.r, rgb.g, 255, 255);     // RG layout (BC5/ASTC/uncompressed)
        }
    }
}
