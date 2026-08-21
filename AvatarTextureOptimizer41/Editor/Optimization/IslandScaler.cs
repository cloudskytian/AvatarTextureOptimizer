using System;
using System.Collections.Generic;
using Net.Fosa.AvatarTextureOptimizer.Pure;
using UnityEngine;

// Island scaling: per (use, island) quality-driven binary search (uniform, then per-axis refine),
// bounded by pixel density, pure-color shortcut, near-lossless shortcut.
// 岛缩放：按 (use, island) 做质量驱动的二分搜索（先均匀，再逐轴细化），受像素密度约束，
// 含纯色短路与近无损短路。

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    public static class IslandScaler
    {
        private const int SearchIterations = 14;

        /// <summary>
        /// Computes scaled pixel sizes for every (use, island) pair of every UV group and stores them in
        /// use.IslandScaleFactors (in the source texture's pixel space).
        /// 计算每个 UV 组每个 (use, island) 的缩放像素尺寸，存入 use.IslandScaleFactors（源贴图像素空间）。
        /// </summary>
        public static void ScaleAll(ATOBuildContext ctx, ATOSettingsData data, QualityTierSettings tier,
            TextureDecodeCache decode, RenderTexturePool rtPool, ATOCancellation cancel, ATOBuildReport report)
        {
            int groupIdx = 0;
            foreach (var group in ctx.UVGroups)
            {
                cancel.ThrowIfCancelled($"Scaling islands (group {groupIdx + 1}/{ctx.UVGroups.Count})", groupIdx / (float)Math.Max(1, ctx.UVGroups.Count));
                foreach (var use in group.Uses)
                {
                    if (use.Skip) continue;
                    foreach (var island in group.Islands)
                    {
                        var origPx = PixelSizeAtTexture(island, use.Texture);
                        Vector2 scaled = ComputeScaledSize(use, island, origPx, tier, data, decode, rtPool);
                        use.IslandScaleFactors[island] = scaled;
                        report.ScaledIslands++;
                    }
                }
                report.TotalIslands += group.Islands.Count;
                groupIdx++;
            }
        }

        public static Vector2Int PixelSizeAtTexture(UVIsland island, Texture2D tex)
        {
            if (tex == null) return new Vector2Int(1, 1);
            var s = island.SizeUV;
            return new Vector2Int(
                Mathf.Max(1, Mathf.RoundToInt(s.x * tex.width)),
                Mathf.Max(1, Mathf.RoundToInt(s.y * tex.height)));
        }

        private static Vector2 ComputeScaledSize(TextureUse use, UVIsland island, Vector2Int origPx,
            QualityTierSettings tier, ATOSettingsData data, TextureDecodeCache decode, RenderTexturePool rtPool)
        {
            // Near-lossless: copy at original size (no resampling). 近无损：原尺寸拷贝。
            if (tier.targetQuality >= 0.999f) return new Vector2(origPx.x, origPx.y);

            float shortEdge = Mathf.Min(origPx.x, origPx.y);

            // Pure color: short-circuit to min(4, shortEdge). 纯色：短路到 min(4, 短边)。
            if (IsRegionUniform(use, island, origPx, decode, rtPool))
            {
                float s = Mathf.Min(4f, shortEdge);
                return new Vector2(Mathf.Max(1, s), Mathf.Max(1, s));
            }

            var (minScale, maxScale) = DensityPlanner.ScaleBounds(island.WorldSizeMeters, origPx, data);
            if (minScale > maxScale) minScale = maxScale;

            // Tiny islands: metrics ignored; target = min density floor (as small as allowed).
            // 极小岛：忽略质量指标；目标 = 最小密度下限（允许范围内尽量小）。
            if (shortEdge < QualityEvaluator.IgnoreBelowShortEdge)
            {
                float s = Mathf.Max(minScale, 1f / Mathf.Max(1, Mathf.Max(origPx.x, origPx.y)));
                return new Vector2(Mathf.Max(1, origPx.x * s), Mathf.Max(1, origPx.y * s));
            }

            // ---- Uniform binary search. 均匀二分搜索。----
            float hi = maxScale;
            float lo = minScale;
            if (!Evaluate(use, island, origPx, new Vector2(hi, hi), tier, decode, rtPool).Pass && hi < 1f)
            {
                // Quality demands more pixels than the density maximum allows; extend up to original size.
                // 质量需求超过密度上限：扩展到原尺寸。
                hi = 1f;
                ATOLog.VerboseLog($"island {island} exceeds max density; quality priority (scale up to 1)");
            }
            if (!Evaluate(use, island, origPx, new Vector2(hi, hi), tier, decode, rtPool).Pass)
            {
                // Even the original fails (should not happen since scaled==orig passes trivially); keep original.
                // 原尺寸仍不达标（正常不会发生）；保持原尺寸。
                return new Vector2(origPx.x, origPx.y);
            }
            float s = BinarySearch(lo, hi, v => Evaluate(use, island, origPx, new Vector2(v, v), tier, decode, rtPool).Pass);

            // ---- Per-axis refinement (anisotropy). 逐轴细化（各向异性）。----
            float sx = BinarySearch(lo, s, v => Evaluate(use, island, origPx, new Vector2(v, s), tier, decode, rtPool).Pass);
            float sy = BinarySearch(lo, s, v => Evaluate(use, island, origPx, new Vector2(sx, v), tier, decode, rtPool).Pass);

            var result = new Vector2(
                Mathf.Max(1, Mathf.RoundToInt(origPx.x * sx)),
                Mathf.Max(1, Mathf.RoundToInt(origPx.y * sy)));
            return result;
        }

        /// <summary>Binary search for the smallest v in [lo, hi] where pred(v) is true. 在 [lo,hi] 内找 pred 为真的最小 v。</summary>
        private static float BinarySearch(float lo, float hi, Func<float, bool> pred)
        {
            if (lo >= hi) return hi;
            for (int i = 0; i < SearchIterations; i++)
            {
                float mid = (lo + hi) * 0.5f;
                if (pred(mid)) hi = mid;
                else lo = mid;
            }
            return hi;
        }

        // ---- Evaluation helpers. 评估辅助。----

        private static readonly Dictionary<(Texture2D, int, bool), float[]> OrigRegionCache = new Dictionary<(Texture2D, int, bool), float[]>();

        private static float[] GetOrigRegion(TextureUse use, UVIsland island, Vector2Int origPx, bool premultiply, TextureDecodeCache decode, RenderTexturePool rtPool)
        {
            var rect = IslandUVToPixelRect(island, use.Texture);
            var roundKey = (use.Texture, rect.GetHashCode(), premultiply);
            if (OrigRegionCache.TryGetValue(roundKey, out var buf) && buf.Length == origPx.x * origPx.y * 4) return buf;

            buf = TextureOps.SampleRegion(use.Texture, IslandUVRect(island), origPx.x, origPx.y, premultiply, rtPool);
            if (IsSRGB(use.Texture))
                for (int i = 0; i < buf.Length; i++) buf[i] = TextureDecodeCache.SrgbToLinear(buf[i]);
            // Keep cache bounded: clear when too large. 控制缓存规模。
            long total = 0;
            foreach (var kv in OrigRegionCache) total += kv.Value.Length;
            if (total > 384L * 1024 * 1024) OrigRegionCache.Clear();
            OrigRegionCache[roundKey] = buf;
            return buf;
        }

        public static Rect IslandUVRect(UVIsland island)
        {
            // Content rect in the texture: for out-of-bounds-but-translatable islands the content lives at
            // the wrapped position (repeat sampling), e.g. UV 1.2..1.4 -> content 0.2..0.4. Span <= 1 makes
            // the wrapped rect unambiguous. For in-bounds islands this is just the bbox itself.
            // 贴图内容矩形：越界但可平移的岛，内容位于取模后的位置（repeat 采样），如 UV 1.2..1.4 → 内容 0.2..0.4。
            // 跨度<=1 保证取模矩形无歧义；界内岛即包围盒本身。
            float u = Wrap01(island.BoundsMin.x);
            float v = Wrap01(island.BoundsMin.y);
            return new Rect(u, v, island.SizeUV.x, island.SizeUV.y);
        }

        private static float Wrap01(float x)
        {
            x = x % 1f;
            return x < 0f ? x + 1f : x;
        }

        private static RectInt IslandUVToPixelRect(UVIsland island, Texture2D tex)
        {
            var r = IslandUVRect(island);
            return new RectInt(
                Mathf.RoundToInt(r.x * tex.width), Mathf.RoundToInt(r.y * tex.height),
                Mathf.Max(1, Mathf.RoundToInt(r.width * tex.width)), Mathf.Max(1, Mathf.RoundToInt(r.height * tex.height)));
        }

        private static QualityMetricsResult Evaluate(TextureUse use, UVIsland island, Vector2Int origPx, Vector2 scale,
            QualityTierSettings tier, TextureDecodeCache decode, RenderTexturePool rtPool)
        {
            int sw = Mathf.Max(1, Mathf.RoundToInt(origPx.x * scale.x));
            int sh = Mathf.Max(1, Mathf.RoundToInt(origPx.y * scale.y));
            if (sw >= origPx.x && sh >= origPx.y)
                return new QualityMetricsResult { Pass = true }; // identity resample. 恒等重采样。
            // Transparent textures: downsample with premultiplied alpha (spec) to avoid dark fringes;
            // both sides are compared premultiplied, alpha metrics use the straight alpha.
            // 透明贴图：按规格用预乘 alpha 下采样避免暗边；两侧以预乘形式比较，alpha 指标使用直通 alpha。
            bool premult = use.Class == TextureClass.ColorAlpha || use.AlphaMode != AlphaMode.Opaque;
            var orig = GetOrigRegion(use, island, origPx, premult, decode, rtPool);
            var scaled = TextureOps.SampleRegion(use.Texture, IslandUVRect(island), sw, sh, premult, rtPool);
            if (IsSRGB(use.Texture))
                for (int i = 0; i < scaled.Length; i++) scaled[i] = TextureDecodeCache.SrgbToLinear(scaled[i]);
            return QualityEvaluator.Evaluate(orig, scaled, sw, sh, origPx.x, origPx.y, tier, use);
        }

        private static bool IsRegionUniform(TextureUse use, UVIsland island, Vector2Int origPx, TextureDecodeCache decode, RenderTexturePool rtPool)
        {
            int cw = Mathf.Clamp(origPx.x, 1, 64), ch = Mathf.Clamp(origPx.y, 1, 64);
            var buf = TextureOps.SampleRegion(use.Texture, IslandUVRect(island), cw, ch, premultiplyAlpha: false, rtPool);
            if (IsSRGB(use.Texture))
                for (int i = 0; i < buf.Length; i++) buf[i] = TextureDecodeCache.SrgbToLinear(buf[i]);
            return QualityMath.IsUniform(buf, cw * ch, 4);
        }

        private static readonly Dictionary<int, bool> SRGBCache = new Dictionary<int, bool>();
        private static bool IsSRGB(Texture2D tex)
        {
            int id = tex.GetInstanceID();
            if (SRGBCache.TryGetValue(id, out var v)) return v;
            v = TextureDecodeCache.IsSRGB(tex);
            SRGBCache[id] = v;
            return v;
        }
    }
}
