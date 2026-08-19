using System;
using UnityEngine;

namespace AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Scales UV islands toward the target quality via binary search.
    /// 通过二分搜索将 UV 岛缩放到目标质量。
    /// Scaling is computed per UV group (worst-case "barrel" across member textures), then
    /// clamped by pixel-density bounds. Uniform first, then per-axis refinement for anisotropy.
    /// 缩放按 UV 组计算（组内贴图木桶取最严），再按像素密度钳制。先均匀缩放，后双轴各向异性细化。
    /// </summary>
    public static class IslandScaler
    {
        /// <summary>Result of scaling one texture over one island. / 单张贴图在单岛上的缩放结果。</summary>
        public struct SingleResult
        {
            public Vector2 scale;      // UV-space scale / UV 空间缩放
            public bool skipped;       // lossless target → copy as-is / 无损目标 → 原样拷贝
            public bool solid;         // solid color short-circuit / 纯色短路
        }

        /// <summary>
        /// Compute the unified UV-space scale for a whole UV group. / 计算整个 UV 组的统一 UV 缩放。
        /// </summary>
        /// <param name="group">The UV group. / UV 组。</param>
        /// <param name="q">Quality thresholds. / 质量阈值。</param>
        /// <param name="settings">Platform settings. / 平台设置。</param>
        /// <param name="animMaxScale">Animation-driven max scale factor (>=1). / 动画驱动的最大缩放（>=1）。</param>
        public static Vector2 ComputeGroupScale(UvGroup group, ATOQualityParameters q,
            ATOPlatformSettings settings, float animMaxScale)
        {
            var worst = Vector2.zero;
            bool allSkipped = true;

            foreach (var tex in group.textures)
            {
                if (tex == null || tex.texture == null) continue;
                var r = ScaleSingle(tex, group.island, q);
                worst = Vector2.Max(worst, r.scale);
                allSkipped &= r.skipped;
            }

            if (allSkipped || worst == Vector2.zero)
                return Vector2.one; // lossless target or nothing to scale / 无损目标或无需缩放

            // pixel density clamp / 像素密度钳制
            worst = ClampByPixelDensity(group, worst, settings, animMaxScale);
            return worst;
        }

        /// <summary>
        /// Scale one texture's island region toward quality. / 将单张贴图的岛区域缩放到目标质量。
        /// </summary>
        private static SingleResult ScaleSingle(TextureEntry tex, UvIsland island, ATOQualityParameters q)
        {
            var r = new SingleResult { scale = Vector2.one };
            var src = tex.readable ?? tex.texture;
            if (src == null) return r;

            int rw = Mathf.Max(1, Mathf.RoundToInt(island.bounds.width * tex.width));
            int rh = Mathf.Max(1, Mathf.RoundToInt(island.bounds.height * tex.height));
            var region = new Rect(island.bounds.x * tex.width, island.bounds.y * tex.height, rw, rh);

            // lossless target → skip / 无损目标 → 跳过
            if (IsLossless(q)) { r.skipped = true; return r; }

            // solid color short-circuit → min(4, bbox short edge) in pixel scale / 纯色短路
            if (TextureOps.IsSolidColor(src, region))
            {
                r.solid = true;
                int minPx = Mathf.Min(4, Mathf.Min(rw, rh));
                r.scale = new Vector2((float)minPx / rw, (float)minPx / rh);
                return r;
            }

            var cutoff = 0.5f;
            if (tex.references.Count > 0)
            {
                var (_, cutout, c) = ShaderAnalysis.GetRenderMode(tex.references[0].material);
                if (cutout) cutoff = c;
            }

            // uniform binary search / 均匀二分
            float lo = 0f, hi = 1f;
            for (int i = 0; i < 16; i++)
            {
                float mid = (lo + hi) * 0.5f;
                var cand = TextureOps.MakeScaledCandidate(src, region, mid, mid);
                var m = EvaluateFor(tex, cand, region, cutoff);
                UnityEngine.Object.DestroyImmediate(cand);
                if (Passes(m, q, tex.category)) lo = mid; else hi = mid;
            }
            float uniform = lo;

            // anisotropic refinement (skip for normal maps — tangent data must not be recomputed) / 各向异性细化（法线跳过——切线数据绝不重算）
            float sx = uniform, sy = uniform;
            if (tex.category != ATOTextureCategory.Normal)
            {
                float loX = uniform, hiX = 1f;
                for (int i = 0; i < 12; i++)
                {
                    float mid = (loX + hiX) * 0.5f;
                    var cand = TextureOps.MakeScaledCandidate(src, region, mid, uniform);
                    var m = EvaluateFor(tex, cand, region, cutoff);
                    UnityEngine.Object.DestroyImmediate(cand);
                    if (Passes(m, q, tex.category)) loX = mid; else hiX = mid;
                }
                float loY = uniform, hiY = 1f;
                for (int i = 0; i < 12; i++)
                {
                    float mid = (loY + hiY) * 0.5f;
                    var cand = TextureOps.MakeScaledCandidate(src, region, uniform, mid);
                    var m = EvaluateFor(tex, cand, region, cutoff);
                    UnityEngine.Object.DestroyImmediate(cand);
                    if (Passes(m, q, tex.category)) loY = mid; else hiY = mid;
                }
                sx = loX; sy = loY;
            }

            r.scale = new Vector2(sx, sy);
            return r;
        }

        /// <summary>
        /// Evaluate metrics for a candidate, applying the MS-SSIM short-edge rules
        /// (&lt;11px ignore, &lt;176px single-scale SSIM, else multi-scale SSIM).
        /// 计算候选的指标，应用 MS-SSIM 短边规则（&lt;11px 忽略，&lt;176px 单尺度 SSIM，否则多尺度 SSIM）。
        /// </summary>
        private static ATOMetrics EvaluateFor(TextureEntry tex, Texture2D cand, Rect region, float cutoff)
        {
            var src = tex.readable ?? tex.texture;
            var m = ATOQuality.Evaluate(src, cand, region, tex.category, cutoff,
                tex.normalEncoding, tex.grayChannelMask);
            int shortEdge = Mathf.Min(Mathf.RoundToInt(region.width), Mathf.RoundToInt(region.height));
            if (shortEdge < 11)
                m.msSsim = 1f; // metric ignored / 忽略该参数
            else if (shortEdge >= 176)
                m.msSsim = ATOQuality.EvaluateMSSsim(src, cand, region);
            return m;
        }

        /// <summary>
        /// Clamp the scale so the island's on-model pixel density stays within
        /// [minPixelDensity, maxPixelDensity] px/m. / 将缩放钳制到岛的模型像素密度落在
        /// [minPixelDensity, maxPixelDensity] px/m 内。
        /// </summary>
        private static Vector2 ClampByPixelDensity(UvGroup group, Vector2 scale,
            ATOPlatformSettings settings, float animMaxScale)
        {
            var island = group.island;
            if (island.localArea <= 0f) return scale; // no geometry → no density info / 无几何信息 → 不钳制

            // representative texture for pixel resolution (prefer a color texture) / 代表性贴图分辨率（优先主色）
            TextureEntry rep = null;
            foreach (var t in group.textures)
            {
                if (t.category.IsColor()) { rep = t; break; }
            }
            if (rep == null && group.textures.Count > 0) rep = group.textures[0];
            if (rep == null || rep.width <= 0 || rep.height <= 0) return scale;

            // world scale = lossyScale (bake-time) × animation max scale / 世界缩放 = lossyScale × 动画最大缩放
            var ls = group.renderer != null ? group.renderer.transform.lossyScale : Vector3.one;
            float worldScale = Mathf.Sqrt(Mathf.Abs(ls.x * ls.y)) * Mathf.Max(1f, animMaxScale);

            // island linear size in world units (m) and in source pixels / 岛世界线性尺寸（米）与源像素
            float worldLinear = Mathf.Sqrt(island.localArea) * worldScale;
            if (worldLinear <= 0f) return scale;

            float pixelLinear = Mathf.Sqrt(island.area * rep.width * rep.height);
            float currentDensity = pixelLinear / worldLinear; // px per meter / 每米像素

            float minD = settings.minPixelDensity;
            float maxD = settings.maxPixelDensity;
            if (minD <= 0f) minD = 1f;
            if (maxD <= 0f) maxD = minD;

            // density scale bounds / 密度缩放上下界
            float scaleMax = Mathf.Clamp(maxD / currentDensity, 0f, 1f);
            float scaleMin = Mathf.Clamp(minD / currentDensity, 0f, 1f);

            // apply: never exceed densityMax (waste), never drop below densityMin (blur).
            // 应用：不超过 densityMax（防浪费），不低于 densityMin（防发糊）。
            float finalX = Mathf.Clamp(scale.x, scaleMin, scaleMax);
            float finalY = Mathf.Clamp(scale.y, scaleMin, scaleMax);
            return new Vector2(finalX, finalY);
        }

        private static bool IsLossless(ATOQualityParameters q) =>
            q.msSsim >= 1f && q.deltaE <= 0f && q.alphaIoU >= 1f &&
            q.alphaRmse <= 0f && q.normalAngle <= 0f && q.grayRmse <= 0f;

        private static bool Passes(ATOMetrics m, ATOQualityParameters q, ATOTextureCategory cat)
        {
            if (m.msSsim < q.msSsim) return false;
            if (m.deltaEP95 > q.deltaE) return false;
            switch (cat)
            {
                case ATOTextureCategory.TransparentColor:
                    if (m.alphaIoU < q.alphaIoU) return false;
                    if (m.alphaRmse > q.alphaRmse) return false;
                    break;
                case ATOTextureCategory.Normal:
                    if (m.normalP95 > q.normalAngle) return false;
                    break;
                case ATOTextureCategory.Gray:
                    if (m.grayRmse > q.grayRmse) return false;
                    break;
            }
            return true;
        }

        /// <summary>Result of scaling a whole texture (fallback path). / 整张贴图缩放（回退路径）的结果。</summary>
        public struct WholeTextureResult
        {
            public bool skipped;   // lossless → copy as-is / 无损 → 原样
            public int newWidth;
            public int newHeight;
        }

        /// <summary>
        /// Scale an entire texture toward the target quality (used when atlas generation is off
        /// or a group falls back). / 将整张贴图缩放到目标质量（图集关闭或组回退时使用）。
        /// </summary>
        public static WholeTextureResult ScaleSingleForWholeTexture(TextureEntry tex, Rect region,
            ATOQualityParameters q, float cutoff)
        {
            var r = new WholeTextureResult { newWidth = tex.width, newHeight = tex.height };
            var src = tex.readable ?? tex.texture;
            if (src == null) return r;

            if (IsLossless(q)) { r.skipped = true; return r; }

            int rw = tex.width, rh = tex.height;
            if (TextureOps.IsSolidColor(src, region))
            {
                int minPx = Mathf.Min(4, Mathf.Min(rw, rh));
                r.newWidth = minPx; r.newHeight = minPx;
                return r;
            }

            // uniform binary search over whole texture / 整张贴图均匀二分
            float lo = 0f, hi = 1f;
            for (int i = 0; i < 16; i++)
            {
                float mid = (lo + hi) * 0.5f;
                var cand = TextureOps.MakeScaledCandidate(src, region, mid, mid);
                var m = EvaluateFor(tex, cand, region, cutoff);
                UnityEngine.Object.DestroyImmediate(cand);
                if (Passes(m, q, tex.category)) lo = mid; else hi = mid;
            }

            r.newWidth = Mathf.Max(1, Mathf.RoundToInt(rw * lo));
            r.newHeight = Mathf.Max(1, Mathf.RoundToInt(rh * lo));
            return r;
        }

        /// <summary>Resolve quality parameters for a preset. / 解析挡位对应的质量参数。</summary>
        public static ATOQualityParameters Resolve(ATOPlatformSettings s)
        {
            switch (s.qualityPreset)
            {
                case ATOQualityPreset.NearLossless:
                    return new ATOQualityParameters { msSsim = 1f, deltaE = 0f, alphaIoU = 1f, alphaRmse = 0f, normalAngle = 0f, grayRmse = 0f };
                case ATOQualityPreset.High:
                    return new ATOQualityParameters { msSsim = 0.995f, deltaE = 1.0f, alphaIoU = 0.95f, alphaRmse = 2f / 255f, normalAngle = 1.5f, grayRmse = 2f / 255f };
                case ATOQualityPreset.Balanced:
                    return new ATOQualityParameters { msSsim = 0.990f, deltaE = 2.0f, alphaIoU = 0.90f, alphaRmse = 4f / 255f, normalAngle = 3.0f, grayRmse = 4f / 255f };
                case ATOQualityPreset.Performance:
                    return new ATOQualityParameters { msSsim = 0.980f, deltaE = 3.0f, alphaIoU = 0.85f, alphaRmse = 8f / 255f, normalAngle = 5.0f, grayRmse = 8f / 255f };
                case ATOQualityPreset.Custom:
                default:
                    return s.customQuality;
            }
        }
    }
}
