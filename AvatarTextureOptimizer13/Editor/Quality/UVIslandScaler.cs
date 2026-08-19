// ATO — Avatar Texture Optimizer
// Binary-search island scaling: finds the smallest uniform scale at which every texture in
// the UV group still passes its quality thresholds, then refines each axis independently
// (anisotropy). Applies density clamping and solid-color / lossless shortcuts.
// 二分搜索岛缩放：找到 UV 组内所有贴图仍全部达标的最小均匀缩放，再逐轴独立细化（各向异性）。
// 应用密度钳制与纯色 / 近无损捷径。

using System;
using System.Collections.Generic;
using UnityEngine;
using net.fosa.ato;

namespace net.fosa.ato.editor
{
    /// <summary>
    /// Island scaling via binary search. 二分搜索岛缩放。
    /// </summary>
    public static class UVIslandScaler
    {
        private const int BinaryIterations = 12;
        private const float MinScale = 1f / 4096f;
        private const float SolidColorMinPixels = 4f;

        /// <summary>
        /// Compute the scaled UVs for an island (uniform scale about the bbox min corner).
        /// 计算岛缩放后的 UV（以包围盒最小角为锚点均匀缩放）。
        /// </summary>
        public static void ApplyUniform(ATOIsland island, float s)
        {
            island.uniformScale = s;
            island.scaleX = s;
            island.scaleY = s;
            island.scaledUV = ScaleUV(island.originalUV, island.bounds.min, s, s);
        }

        /// <summary>Compute the scaled UVs for an island with anisotropic factors. 按双轴因子计算缩放后 UV。</summary>
        public static void ApplyAnisotropic(ATOIsland island, float sx, float sy)
        {
            island.scaleX = sx;
            island.scaleY = sy;
            island.scaledUV = ScaleUV(island.originalUV, island.bounds.min, sx, sy);
        }

        private static Vector2[] ScaleUV(Vector2[] uv, Vector2 anchor, float sx, float sy)
        {
            var result = new Vector2[uv.Length];
            for (int i = 0; i < uv.Length; i++)
            {
                result[i] = new Vector2(anchor.x + (uv[i].x - anchor.x) * sx, anchor.y + (uv[i].y - anchor.y) * sy);
            }
            return result;
        }

        /// <summary>
        /// Whether the whole UV group passes all quality thresholds at (sx, sy) for the given island.
        /// 给定岛在 (sx, sy) 下 UV 组内全部贴图是否达标。
        /// </summary>
        public static bool GroupPasses(
            ATOBuildContext bc, ATOUVGroup group, ATOIsland island,
            Dictionary<Texture2D, ATOTextureRef> refs, float sx, float sy, ATOEffectiveSettings settings)
        {
            var seen = new HashSet<Texture2D>();
            foreach (var usage in group.usages)
            {
                if (usage.texture == null || !seen.Add(usage.texture)) continue;
                if (usage.whitelisted) continue;

                var texRef = refs.TryGetValue(usage.texture, out var tr) ? tr : null;
                var kind = usage.kind;
                var alphaMode = texRef != null ? texRef.alphaMode : ATOAlphaMode.Opaque;
                var cutoff = texRef != null ? texRef.cutoff : 0.5f;

                var eval = EvaluateTexture(bc, usage.texture, kind, island, alphaMode, cutoff, sx, sy, settings);
                if (!eval.Passed) return false;
            }
            return true;
        }

        private static ATOIslandEval EvaluateTexture(
            ATOBuildContext bc, Texture2D tex, ATOTextureKind kind, ATOIsland island,
            ATOAlphaMode alphaMode, float cutoff, float sx, float sy, ATOEffectiveSettings settings)
        {
            int w = tex.width, h = tex.height;
            int rx = Mathf.Clamp(Mathf.FloorToInt(island.bounds.xMin * w), 0, w - 1);
            int ry = Mathf.Clamp(Mathf.FloorToInt(island.bounds.yMin * h), 0, h - 1);
            int rw = Mathf.Clamp(Mathf.CeilToInt(island.bounds.width * w), 1, w - rx);
            int rh = Mathf.Clamp(Mathf.CeilToInt(island.bounds.height * h), 1, h - ry);

            var linear = GetLinearRegion(bc, tex, rx, ry, rw, rh);
            return IslandQualityEvaluator.Evaluate(linear, rw, rh, 0, 0, rw, rh, sx, sy, kind, alphaMode, cutoff, settings.parameters);
        }

        /// <summary>
        /// Get a linear, premultiplied-alpha region from the cached raw pixels.
        /// 从缓存的原始像素取线性、预乘 alpha 区域。
        /// </summary>
        public static Color[] GetLinearRegion(ATOBuildContext bc, Texture2D tex, int x, int y, int w, int h)
        {
            if (!bc.DecodedPixels.TryGetValue(tex, out var raw))
            {
                if (!ATOTextureIO.TryReadPixels(tex, out raw)) return Array.Empty<Color>();
                bc.DecodedPixels[tex] = raw;
            }
            int texW = tex.width;
            bool srgb = ATOTextureIO.IsSRGB(tex);
            var region = new Color[w * h];
            for (int iy = 0; iy < h; iy++)
            for (int ix = 0; ix < w; ix++)
            {
                int sx = Mathf.Clamp(x + ix, 0, texW - 1);
                int sy = Mathf.Clamp(y + iy, 0, tex.height - 1);
                region[iy * w + ix] = ATOTextureIO.ToLinearPremultiplied(raw[sy * texW + sx], srgb);
            }
            return region;
        }

        /// <summary>
        /// Scale one island to the target quality. Applies lossless / solid-color shortcuts,
        /// uniform binary search, per-axis refinement and density clamping.
        /// 将一个岛缩放到目标质量：近无损/纯色捷径、均匀二分、逐轴细化、密度钳制。
        /// </summary>
        public static void ScaleIsland(
            ATOBuildContext bc, ATOUVGroup group, ATOIsland island,
            Dictionary<Texture2D, ATOTextureRef> refs, ATOEffectiveSettings settings)
        {
            bc.ThrowIfCancelled();

            // Lossless (quality == 1): copy as-is. 近无损：原样拷贝。
            if (settings.parameters.IsLossless)
            {
                island.losslessSkip = true;
                ApplyUniform(island, 1f);
                return;
            }

            // Solid-color shortcut: shrink to min(4, bbox short side), using the smallest
            // short side across the group's textures (most conservative).
            // 纯色捷径：缩到 min(4, 包围盒短边)，取组内各贴图短边的最小值（最保守）。
            int shortSide = int.MaxValue;
            foreach (var usage in group.usages)
            {
                if (usage.texture == null || usage.whitelisted) continue;
                int w = usage.texture.width, h = usage.texture.height;
                int sw = Mathf.Clamp(Mathf.CeilToInt(island.bounds.width * w), 1, w);
                int sh = Mathf.Clamp(Mathf.CeilToInt(island.bounds.height * h), 1, h);
                shortSide = Mathf.Min(shortSide, Mathf.Min(sw, sh));
            }
            if (shortSide == int.MaxValue) shortSide = 1;

            if (IsSolidForAllTextures(bc, group, island))
            {
                island.solidColor = true;
                float target = Mathf.Min(SolidColorMinPixels, shortSide);
                float s = target / Mathf.Max(shortSide, 1);
                ApplyUniform(island, Mathf.Clamp(s, MinScale, 1f));
                return;
            }

            // Uniform binary search: smallest scale where all textures pass. 均匀二分：全部贴图达标的最小缩放。
            float lo = MinScale, hi = 1f;
            if (!GroupPasses(bc, group, island, refs, hi, hi, settings))
            {
                // Even at full size quality fails (should be ~impossible); keep 1.0.
                // 全尺寸仍不达标（几乎不可能）；保持 1.0。
                ApplyUniform(island, 1f);
                return;
            }
            for (int i = 0; i < BinaryIterations; i++)
            {
                float mid = (lo + hi) * 0.5f;
                if (GroupPasses(bc, group, island, refs, mid, mid, settings)) hi = mid;
                else lo = mid;
            }
            float uniform = hi;

            // Per-axis refinement (anisotropy): shrink X then Y further. 逐轴细化：进一步收缩 X 再 Y。
            float sx = uniform, sy = uniform;
            float lox = MinScale, hix = uniform;
            for (int i = 0; i < BinaryIterations; i++)
            {
                float mid = (lox + hix) * 0.5f;
                if (GroupPasses(bc, group, island, refs, mid, sy, settings)) hix = mid;
                else lox = mid;
            }
            sx = hix;
            float loy = MinScale, hiy = uniform;
            for (int i = 0; i < BinaryIterations; i++)
            {
                float mid = (loy + hiy) * 0.5f;
                if (GroupPasses(bc, group, island, refs, sx, mid, settings)) hiy = mid;
                else loy = mid;
            }
            sy = hiy;

            // Density clamping: never shrink below min density, never above max density.
            // 密度钳制：不低于最小密度，不高于最大密度。
            int refTexSize = ReferenceTextureSize(group);
            (sx, sy) = ClampDensity(island, sx, sy, settings, refTexSize);

            ApplyAnisotropic(island, sx, sy);
        }

        private static int ReferenceTextureSize(ATOUVGroup group)
        {
            // Use the main color texture's size as the density reference; fall back to the largest.
            // 以主色贴图尺寸作为密度参考；否则取最大。
            int size = 0;
            foreach (var u in group.usages)
            {
                if (u.texture == null) continue;
                if (u.isMainColor) return Mathf.Max(u.texture.width, u.texture.height);
                size = Mathf.Max(size, u.texture.width, u.texture.height);
            }
            return Mathf.Max(1, size);
        }

        /// <summary>
        /// Scale a whole texture (used when atlas generation is disabled): binary-search the
        /// uniform scale against the full image. 整图缩放（不生成图集时）：对整张图二分搜索均匀缩放。
        /// </summary>
        public static void ScaleWholeTexture(ATOBuildContext bc, ATOTextureRef texRef, ATOEffectiveSettings settings)
        {
            bc.ThrowIfCancelled();
            var tex = texRef.texture;
            if (tex == null) return;
            texRef.wholeTextureScale = 1f;

            if (settings.parameters.IsLossless) return;

            int w = tex.width, h = tex.height;
            var linear = GetLinearRegion(bc, tex, 0, 0, w, h);
            if (IslandQualityEvaluator.IsSolidColor(linear))
            {
                texRef.wholeTextureScale = Mathf.Max(MinScale, SolidColorMinPixels / Mathf.Max(Mathf.Min(w, h), 1));
                return;
            }

            var kind = texRef.usages.Count > 0 ? texRef.usages[0].kind : ATOTextureKind.Color;
            float lo = MinScale, hi = 1f;
            var eval = IslandQualityEvaluator.Evaluate(linear, w, h, 0, 0, w, h, hi, hi, kind, texRef.alphaMode, texRef.cutoff, settings.parameters);
            if (!eval.Passed) return; // full size fails → keep 1.0

            for (int i = 0; i < BinaryIterations; i++)
            {
                float mid = (lo + hi) * 0.5f;
                var e = IslandQualityEvaluator.Evaluate(linear, w, h, 0, 0, w, h, mid, mid, kind, texRef.alphaMode, texRef.cutoff, settings.parameters);
                if (e.Passed) hi = mid; else lo = mid;
            }
            texRef.wholeTextureScale = hi;
        }

        private static (float, float) ClampDensity(ATOIsland island, float sx, float sy, ATOEffectiveSettings settings, int refTexSize)
        {
            // Linear island size in world space (m) and in the reference texture (px).
            // 岛在世界空间的线性尺寸（m）与在参考贴图中的线性尺寸（px）。
            float worldLen = Mathf.Sqrt(Mathf.Max(island.worldArea, 1e-12f));
            float bboxLen = Mathf.Max(island.bounds.width, island.bounds.height);
            float origPx = bboxLen * refTexSize;

            float minPx = settings.minPixelDensity * worldLen; // floor: don't shrink below this
            float maxPx = settings.maxPixelDensity * worldLen; // ceiling: don't waste above this

            float minS = origPx > 1e-6f ? minPx / origPx : 0f;
            float maxS = origPx > 1e-6f ? maxPx / origPx : 1f;
            float lo = Mathf.Clamp(minS, MinScale, 1f);
            float hi = Mathf.Clamp(maxS, MinScale, 1f);
            float sx1 = Mathf.Clamp(sx, lo, hi);
            float sy1 = Mathf.Clamp(sy, lo, hi);
            return (sx1, sy1);
        }

        private static bool IsSolidForAllTextures(ATOBuildContext bc, ATOUVGroup group, ATOIsland island)
        {
            var seen = new HashSet<Texture2D>();
            foreach (var usage in group.usages)
            {
                if (usage.texture == null || !seen.Add(usage.texture)) continue;
                if (usage.whitelisted) continue;
                int w = usage.texture.width, h = usage.texture.height;
                int rx = Mathf.Clamp(Mathf.FloorToInt(island.bounds.xMin * w), 0, w - 1);
                int ry = Mathf.Clamp(Mathf.FloorToInt(island.bounds.yMin * h), 0, h - 1);
                int rw = Mathf.Clamp(Mathf.CeilToInt(island.bounds.width * w), 1, w - rx);
                int rh = Mathf.Clamp(Mathf.CeilToInt(island.bounds.height * h), 1, h - ry);
                var region = GetLinearRegion(bc, usage.texture, rx, ry, rw, rh);
                if (!IslandQualityEvaluator.IsSolidColor(region)) return false;
            }
            return true;
        }
    }
}
