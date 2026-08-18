// Avatar Texture Optimizer (ATO)
// Per-island quality scaling: binary search for the smallest uniform scale that passes
// all metrics, then per-axis refinement. Pure-color shortcut; pixel-density clamp.
// Original island rasterizations are cached per texture so binary search never re-rasterizes.
// 逐岛质量缩放：二分搜索能通过全部指标的最小均匀缩放，再双轴细化。纯色短路；像素密度钳制。
// 每贴图的原图光栅化被缓存，二分过程中绝不重复光栅化。

using System.Collections.Generic;
using UnityEngine;

namespace NetFosa.ATO
{
    /// <summary>
    /// Stage 5: scale every island (or mark whole-texture scaling) according to target quality.
    /// 阶段 5：按目标质量缩放每个岛（或标记整图缩放）。
    /// </summary>
    public static class ATOIslandScaler
    {
        private sealed class OrigCache
        {
            public readonly Dictionary<ATOTextureRef, (Color[] px, byte[] mask)> cache
                = new Dictionary<ATOTextureRef, (Color[], byte[])>();
        }

        public static void ScaleAll(ATOBuildContext build, ATOProgress progress)
        {
            var thr = ATOQualityModel.Resolve(build);
            bool lossless = ATOQualityModel.IsLossless(thr);

            // No-atlas mode: no island scaling, no re-UV; whole textures are resized instead.
            // 无图集模式：不缩放岛、不重排 UV；改为整图缩放。
            if (!build.profile.generateAtlas)
            {
                foreach (var t in build.textures)
                    if (!t.skipAllOptimization) t.wholeTextureScale = true;
                ATOLogger.Info("Atlas generation disabled; using whole-texture scaling. / 已关闭图集生成，改用整图缩放。");
                return;
            }
            int work = 0;
            foreach (var s in build.uvSpaces) work += s.islands.Count;
            progress.Begin(work);

            foreach (var space in build.uvSpaces)
            {
                if (IsSpacePinned(space))
                {
                    // UV-mates of whitelisted textures: no UV change; whole-texture scaling only.
                    // 白名单贴图的同 UV 贴图：不改 UV，仅整图缩放。
                    foreach (var t in space.textures)
                        if (!t.skipAllOptimization) t.wholeTextureScale = true;
                    progress.Advance(space.islands.Count, "pinned space");
                    continue;
                }

                foreach (var isl in space.islands)
                {
                    ScaleIsland(build, space, isl, thr, lossless);
                    progress.Advance(1);
                    progress.ThrowIfCancelled();
                }
            }

            // In no-atlas mode every non-skipped texture gets whole-texture scaling. / 无图集模式下所有未跳过贴图整图缩放。
            if (!build.profile.generateAtlas)
                foreach (var t in build.textures)
                    if (!t.skipAllOptimization) t.wholeTextureScale = true;
        }

        private static bool IsSpacePinned(ATOUvSpace space)
        {
            foreach (var t in space.textures)
                if (t.skipAllOptimization) return true;
            return false;
        }

        private static void ScaleIsland(ATOBuildContext build, ATOUvSpace space, ATOIsland isl,
            ATOQualityThresholds thr, bool lossless)
        {
            var primary = PickPrimaryTexture(space);
            if (primary == null || primary.width <= 0 || primary.height <= 0) return;

            int bboxW = Mathf.Max(1, Mathf.RoundToInt(isl.Size.x * primary.width));
            int bboxH = Mathf.Max(1, Mathf.RoundToInt(isl.Size.y * primary.height));
            int bboxShort = Mathf.Min(bboxW, bboxH);

            if (lossless)
            {
                isl.scalingSkipped = true;
                isl.uniformScale = 1f;
                isl.anisotropicScale = Vector2.one;
                return;
            }

            // Rasterize every texture's original island once (reused across all iterations).
            // 每张贴图的原图岛只光栅化一次（所有迭代复用）。
            var orig = new OrigCache();
            foreach (var t in space.textures)
            {
                if (t.skipAllOptimization || t.texture == null) continue;
                ATOTextureSampler.Rasterize(t.texture, isl, bboxW, bboxH, out var px, out var mask);
                orig.cache[t] = (px, mask);
            }

            // Pure-color shortcut (primary texture). / 纯色短路（主贴图）。
            var (pPx, pMask) = orig.cache[primary];
            if (IsPureColor(pPx, pMask))
            {
                isl.pureColor = true;
                isl.pureColorValue = pPx[0];
                float targetShort = Mathf.Min(4, bboxShort);
                isl.uniformScale = targetShort / bboxShort;
                isl.anisotropicScale = Vector2.one;
                ATOLogger.Verbose($"Island {isl.islandId} is pure color; scaled to {targetShort}px short side.");
                return;
            }

            // Density clamp bounds. / 密度钳制边界。
            float sMin = 1f, sMax = 1f;
            var rr = FindRenderer(build, isl.meshId);
            if (rr != null)
            {
                float worldArea = ATOPixelDensity.WorldAreaMeters(build, rr, isl);
                float tpmAtFull = ATOPixelDensity.TexelsPerMeter(primary.width, isl.areaUv, worldArea);
                if (float.IsFinite(tpmAtFull) && tpmAtFull > 0f)
                {
                    sMin = Mathf.Clamp(build.profile.pixelDensityMin / tpmAtFull, 0.02f, 1f);
                    sMax = Mathf.Clamp(build.profile.pixelDensityMax / tpmAtFull, 0.02f, 1f);
                }
            }
            if (sMin > sMax) sMin = sMax;

            // Uniform binary search. / 均匀二分搜索。
            float s = BinarySearch(build, space, isl, primary, orig, bboxW, bboxH, bboxShort, thr, sMin, sMax);
            isl.uniformScale = s;

            // Anisotropic refinement. / 各向异性细化。
            float sx = s >= 1f ? s : RefineAxis(build, space, isl, primary, orig, bboxW, bboxH, bboxShort, thr, s, true);
            float sy = s >= 1f ? s : RefineAxis(build, space, isl, primary, orig, bboxW, bboxH, bboxShort, thr, s, false);
            isl.anisotropicScale = new Vector2(sx / s, sy / s);

            var qr = EvaluateAt(build, space, isl, orig, bboxW, bboxH, bboxShort, thr, sx, sy);
            if (qr != null)
                build.report.islandQuality.Add(new ATOIslandQualityResult
                {
                    islandId = isl.islandId,
                    worstMetric = qr.value,
                    limitingMetric = qr.limiting,
                    originalTexels = bboxW * bboxH,
                    scaledTexels = Mathf.Max(1, Mathf.RoundToInt(bboxW * sx)) * Mathf.Max(1, Mathf.RoundToInt(bboxH * sy)),
                });

            ATOLogger.Verbose($"Island {isl.islandId}: uniform {s:F3}, anisotropic ({isl.anisotropicScale.x:F3},{isl.anisotropicScale.y:F3})");
        }

        private static ATOTextureRef PickPrimaryTexture(ATOUvSpace space)
        {
            ATOTextureRef fallback = null;
            foreach (var t in space.textures)
            {
                if (t.skipAllOptimization) continue;
                if (t.Category == ATOTextureCategory.MainColor) return t;
                if (fallback == null) fallback = t;
            }
            return fallback;
        }

        private static ATORendererRef FindRenderer(ATOBuildContext build, int meshId)
        {
            foreach (var r in build.renderers) if (r.rendererId == meshId) return r;
            return null;
        }

        private static bool IsPureColor(Color[] px, byte[] mask)
        {
            Color? first = null;
            for (int i = 0; i < px.Length; i++)
            {
                if (mask != null && mask[i] == 0) continue;
                if (first == null) first = px[i];
                else if (!NearlyEqual(first.Value, px[i])) return false;
            }
            return first != null;
        }

        private static bool NearlyEqual(Color a, Color b)
        {
            return Mathf.Abs(a.r - b.r) < 1f / 255f && Mathf.Abs(a.g - b.g) < 1f / 255f
                && Mathf.Abs(a.b - b.b) < 1f / 255f && Mathf.Abs(a.a - b.a) < 1f / 255f;
        }

        private static float BinarySearch(ATOBuildContext build, ATOUvSpace space, ATOIsland isl, ATOTextureRef primary,
            OrigCache orig, int bboxW, int bboxH, int bboxShort, ATOQualityThresholds thr, float sMin, float sMax)
        {
            // Verify sMax passes; if not, no scaling is allowed. / 校验 sMax 是否达标；否则不允许缩放。
            var atMax = EvaluateAt(build, space, isl, orig, bboxW, bboxH, bboxShort, thr, sMax, sMax);
            if (atMax == null || !atMax.pass) return 1f;

            float lo = sMin, hi = sMax;
            for (int i = 0; i < 14; i++)
            {
                float mid = (lo + hi) * 0.5f;
                var r = EvaluateAt(build, space, isl, orig, bboxW, bboxH, bboxShort, thr, mid, mid);
                if (r != null && r.pass) hi = mid; else lo = mid;
            }
            return hi;
        }

        private static float RefineAxis(ATOBuildContext build, ATOUvSpace space, ATOIsland isl, ATOTextureRef primary,
            OrigCache orig, int bboxW, int bboxH, int bboxShort, ATOQualityThresholds thr, float s, bool isX)
        {
            float lo = s * 0.25f, hi = s;
            for (int i = 0; i < 10; i++)
            {
                float mid = (lo + hi) * 0.5f;
                var r = EvaluateAt(build, space, isl, orig, bboxW, bboxH, bboxShort, thr,
                    isX ? mid : s, isX ? s : mid);
                if (r != null && r.pass) hi = mid; else lo = mid;
            }
            return hi;
        }

        private static ATOQualityResult EvaluateAt(ATOBuildContext build, ATOUvSpace space, ATOIsland isl,
            OrigCache orig, int bboxW, int bboxH, int bboxShort, ATOQualityThresholds thr, float sx, float sy)
        {
            int sw = Mathf.Max(1, Mathf.RoundToInt(bboxW * sx));
            int sh = Mathf.Max(1, Mathf.RoundToInt(bboxH * sy));
            if (sw >= bboxW && sh >= bboxH) return null; // no downscale / 无降采样

            var worst = new ATOQualityResult { pass = true, margin = float.MaxValue };
            foreach (var kvp in orig.cache)
            {
                var t = kvp.Key;
                var (origPx, origMask) = kvp.Value;
                int tw = Mathf.Max(1, Mathf.RoundToInt(isl.Size.x * t.width * sx));
                int th = Mathf.Max(1, Mathf.RoundToInt(isl.Size.y * t.height * sy));
                ATOTextureSampler.Rasterize(t.texture, isl, tw, th, out var small, out _);
                var up = new Color[bboxW * bboxH];
                ATOTextureSampler.BilinearUpsample(small, tw, th, up, bboxW, bboxH);

                var r = ATOQualityEvaluator.Evaluate(t, origPx, up, origMask, bboxW, bboxH, bboxShort, thr);
                if (r == null) continue;
                if (!r.pass || r.margin < worst.margin)
                {
                    worst.pass = r.pass;
                    worst.margin = r.margin;
                    worst.limiting = r.limiting;
                    worst.value = r.value;
                    worst.threshold = r.threshold;
                    if (!r.pass) return worst;
                }
            }
            return worst;
        }
    }
}
