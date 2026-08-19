using System;
using System.Collections.Generic;
using UnityEngine;
using FOSA.AvatarTextureOptimizer;

namespace FOSA.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Scales each island (or whole texture if atlas is off) by binary search.
    /// Uniform first, then independent X/Y. UV-group barrel: take the max required size.
    /// 用二分搜索缩放每个岛（关闭图集时则缩放整图）。
    /// 先均匀，再独立 X/Y。UV 组木桶效应：取所需最大尺寸。
    /// </summary>
    internal static class ATOQualityScaler
    {
        public static void Run(ATOContext ctx)
        {
            // Attach pixel-size hints to every island from every texture that uses it.
            // 用所有引用该岛的贴图给岛写上像素尺寸提示。
            foreach (var ri in ctx.Renderers)
            {
                foreach (var island in ri.Islands)
                {
                    SizeIslandOnTextures(ctx, island);
                }
            }

            var skip = ctx.Settings.quality.SkipUvScale ||
                       ctx.Settings.qualityPreset == ATOQualityPreset.Lossless;
            if (skip)
            {
                ctx.Log.Info("Quality=lossless, skip UV scale (copy as-is).");
                foreach (var ri in ctx.Renderers)
                foreach (var island in ri.Islands)
                {
                    island.ScaledW = Math.Max(1, island.OriginalPixelW);
                    island.ScaledH = Math.Max(1, island.OriginalPixelH);
                }
                return;
            }

            foreach (var ri in ctx.Renderers)
            {
                ctx.Progress.ThrowIfCanceled();
                foreach (var island in ri.Islands)
                {
                    ScaleIsland(ctx, island);
                }
            }
        }

        private static void SizeIslandOnTextures(ATOContext ctx, ATOIsland island)
        {
            var maxW = 1;
            var maxH = 1;
            var anySolid = true;
            Color solid = default;
            var first = true;
            foreach (var use in ctx.Uses)
            {
                if (use.Renderer != island.Renderer) continue;
                if (use.Slot.submeshIndex != island.Submesh) continue;
                if (use.Slot.uvChannel != island.UvChannel) continue;
                if (use.Slot.texture == null) continue;
                if (ctx.WhitelistedTextures.Contains(use.Slot.texture)) continue;

                var tex = use.Slot.texture;
                var w = Math.Max(1, Mathf.CeilToInt(island.UvSize.x * tex.width));
                var h = Math.Max(1, Mathf.CeilToInt(island.UvSize.y * tex.height));
                maxW = Math.Max(maxW, w);
                maxH = Math.Max(maxH, h);

                var dec = ATOTextureUtil.Decode(ctx, tex);
                var crop = Crop(dec, island);
                if (first)
                {
                    anySolid = ATOTextureUtil.IsSolidColor(crop, out solid);
                    first = false;
                }
                else if (anySolid)
                {
                    if (!ATOTextureUtil.IsSolidColor(crop, out var s2) || !Approx(s2, solid))
                        anySolid = false;
                }
            }
            island.OriginalPixelW = maxW;
            island.OriginalPixelH = maxH;
            island.ScaledW = maxW;
            island.ScaledH = maxH;
            island.SolidColor = !first && anySolid;
            island.Solid = solid;
        }

        private static void ScaleIsland(ATOContext ctx, ATOIsland island)
        {
            if (island.OriginalPixelW <= 0 || island.OriginalPixelH <= 0) return;

            if (island.SolidColor)
            {
                var s = Math.Min(4, Math.Min(island.OriginalPixelW, island.OriginalPixelH));
                island.ScaledW = Math.Max(1, s);
                island.ScaledH = Math.Max(1, s);
                ctx.Log.Detail($"Island {island.Id} solid → {island.ScaledW}x{island.ScaledH}");
                return;
            }

            DensityClamp(ctx, island, out var minW, out var minH, out var maxW, out var maxH);
            // Uniform binary search on the short side, keep aspect. / 短边均匀二分，保持宽高比。
            var aspect = island.OriginalPixelW / (float)Math.Max(1, island.OriginalPixelH);
            int lo = Math.Max(1, Math.Min(minW, minH));
            int hi = Math.Max(lo, Math.Min(maxW, maxH));
            int best = hi;
            while (lo <= hi)
            {
                ctx.Progress.ThrowIfCanceled();
                var mid = (lo + hi) / 2;
                var tw = aspect >= 1f ? Math.Max(mid, Mathf.RoundToInt(mid * aspect)) : mid;
                var th = aspect >= 1f ? mid : Math.Max(mid, Mathf.RoundToInt(mid / Math.Max(1e-4f, aspect)));
                tw = Mathf.Clamp(tw, 1, island.OriginalPixelW);
                th = Mathf.Clamp(th, 1, island.OriginalPixelH);
                if (EvaluateAll(ctx, island, tw, th, out _))
                {
                    best = mid;
                    hi = mid - 1;
                }
                else lo = mid + 1;
            }

            var uniW = aspect >= 1f ? Math.Max(best, Mathf.RoundToInt(best * aspect)) : best;
            var uniH = aspect >= 1f ? best : Math.Max(best, Mathf.RoundToInt(best / Math.Max(1e-4f, aspect)));
            uniW = Mathf.Clamp(uniW, 1, island.OriginalPixelW);
            uniH = Mathf.Clamp(uniH, 1, island.OriginalPixelH);

            // Independent axis refine. / 双轴独立细化。
            uniW = RefineAxis(ctx, island, uniW, uniH, true, minW, maxW);
            uniH = RefineAxis(ctx, island, uniW, uniH, false, minH, maxH);

            island.ScaledW = Math.Max(1, uniW);
            island.ScaledH = Math.Max(1, uniH);
            ctx.Log.Detail($"Island {island.Id} {island.OriginalPixelW}x{island.OriginalPixelH} → {island.ScaledW}x{island.ScaledH} world={island.WorldShortSide:F4}m");
        }

        private static int RefineAxis(ATOContext ctx, ATOIsland island, int w, int h, bool axisX, int min, int max)
        {
            int lo = Math.Max(1, min);
            int hi = axisX ? w : h;
            int best = hi;
            while (lo <= hi)
            {
                var mid = (lo + hi) / 2;
                var tw = axisX ? mid : w;
                var th = axisX ? h : mid;
                if (EvaluateAll(ctx, island, tw, th, out _))
                {
                    best = mid;
                    hi = mid - 1;
                }
                else lo = mid + 1;
            }
            return best;
        }

        private static void DensityClamp(ATOContext ctx, ATOIsland island, out int minW, out int minH, out int maxW, out int maxH)
        {
            // linear density ≈ island_pixel_short / world_short
            // 线性密度 ≈ 岛像素短边 / 世界短边
            var world = Math.Max(1e-6f, island.WorldShortSide);
            var minPx = ctx.Settings.minPixelDensity * world;
            var maxPx = ctx.Settings.maxPixelDensity * world;
            var origS = Math.Max(1, Math.Min(island.OriginalPixelW, island.OriginalPixelH));
            minPx = Math.Min(minPx, origS);
            maxPx = Math.Min(maxPx, origS);
            if (maxPx < minPx) maxPx = minPx;

            var aspect = island.OriginalPixelW / (float)Math.Max(1, island.OriginalPixelH);
            if (aspect >= 1f)
            {
                minH = Math.Max(1, Mathf.RoundToInt(minPx));
                maxH = Math.Max(minH, Mathf.RoundToInt(maxPx));
                minW = Math.Max(1, Mathf.RoundToInt(minH * aspect));
                maxW = Math.Max(minW, Mathf.RoundToInt(maxH * aspect));
            }
            else
            {
                minW = Math.Max(1, Mathf.RoundToInt(minPx));
                maxW = Math.Max(minW, Mathf.RoundToInt(maxPx));
                minH = Math.Max(1, Mathf.RoundToInt(minW / Math.Max(1e-4f, aspect)));
                maxH = Math.Max(minH, Mathf.RoundToInt(maxW / Math.Max(1e-4f, aspect)));
            }
            minW = Mathf.Clamp(minW, 1, island.OriginalPixelW);
            maxW = Mathf.Clamp(maxW, minW, island.OriginalPixelW);
            minH = Mathf.Clamp(minH, 1, island.OriginalPixelH);
            maxH = Mathf.Clamp(maxH, minH, island.OriginalPixelH);
        }

        private static bool EvaluateAll(ATOContext ctx, ATOIsland island, int tw, int th, out string detail)
        {
            detail = "";
            foreach (var use in ctx.Uses)
            {
                if (use.Renderer != island.Renderer) continue;
                if (use.Slot.submeshIndex != island.Submesh) continue;
                if (use.Slot.uvChannel != island.UvChannel) continue;
                if (use.Slot.texture == null) continue;
                if (ctx.WhitelistedTextures.Contains(use.Slot.texture)) continue;

                var dec = ATOTextureUtil.Decode(ctx, use.Slot.texture);
                var crop = Crop(dec, island);
                var cw = Math.Max(1, Mathf.RoundToInt(island.UvSize.x * dec.Width));
                var ch = Math.Max(1, Mathf.RoundToInt(island.UvSize.y * dec.Height));
                if (crop.Length != cw * ch)
                {
                    cw = island.OriginalPixelW;
                    ch = island.OriginalPixelH;
                }

                Color[] scaled;
                if (use.Slot.alphaMode == ATOAlphaMode.Blend ||
                    use.Slot.category == ATOTextureCategory.TransparentAlbedo)
                    scaled = ATOQualityMetrics.DownsamplePremultiplied(crop, cw, ch, tw, th);
                else if (use.Slot.category == ATOTextureCategory.Normal)
                    scaled = DownsampleNormal(crop, cw, ch, tw, th);
                else
                    scaled = ATOQualityMetrics.DownsampleLinear(crop, cw, ch, tw, th);

                var cat = use.Slot.category;
                foreach (var ext in ATOApi.TextureClassifiers)
                {
                    if (ext.TryClassify(use.Slot.texture, new[] { use.Slot }, out var c))
                    {
                        cat = c;
                        break;
                    }
                }

                if (!ATOQualityMetrics.Passes(ctx, crop, cw, ch, scaled, tw, th, cat, use.Slot.alphaMode, use.Slot.cutoff, ctx.Settings.quality, out detail))
                    return false;
            }
            return true;
        }

        internal static Color[] Crop(ATODecodedTexture dec, ATOIsland island)
        {
            var x0 = Mathf.Clamp(Mathf.FloorToInt(island.UvMin.x * dec.Width), 0, dec.Width - 1);
            var y0 = Mathf.Clamp(Mathf.FloorToInt(island.UvMin.y * dec.Height), 0, dec.Height - 1);
            var x1 = Mathf.Clamp(Mathf.CeilToInt(island.UvMax.x * dec.Width), x0 + 1, dec.Width);
            var y1 = Mathf.Clamp(Mathf.CeilToInt(island.UvMax.y * dec.Height), y0 + 1, dec.Height);
            var w = x1 - x0;
            var h = y1 - y0;
            var dst = new Color[w * h];
            for (int y = 0; y < h; y++)
            {
                Array.Copy(dec.Pixels, (y0 + y) * dec.Width + x0, dst, y * w, w);
            }
            return dst;
        }

        private static Color[] DownsampleNormal(Color[] src, int sw, int sh, int dw, int dh)
        {
            var tmp = ATOQualityMetrics.DownsampleLinear(src, sw, sh, dw, dh);
            for (int i = 0; i < tmp.Length; i++)
            {
                var n = new Vector3(tmp[i].r * 2f - 1f, tmp[i].g * 2f - 1f, tmp[i].b * 2f - 1f);
                if (n.sqrMagnitude < 1e-8f) n = new Vector3(0, 0, 1);
                n.Normalize();
                tmp[i] = new Color(n.x * 0.5f + 0.5f, n.y * 0.5f + 0.5f, n.z * 0.5f + 0.5f, tmp[i].a);
            }
            return tmp;
        }

        private static bool Approx(Color a, Color b)
        {
            return Mathf.Abs(a.r - b.r) < 1e-3f && Mathf.Abs(a.g - b.g) < 1e-3f &&
                   Mathf.Abs(a.b - b.b) < 1e-3f && Mathf.Abs(a.a - b.a) < 1e-3f;
        }
    }
}
