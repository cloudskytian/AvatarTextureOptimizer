// AvatarTextureOptimizer
// File: Editor/Quality/IslandScaler.cs
//
// Quality-driven UV island scaling.
//   - target quality == 1 -> skip scaling (copy without resampling)
//   - solid-color islands shortcut to min(4, original short side) when
//     target quality < 1
//   - binary search for the SMALLEST scale factor that still passes all
//     metrics (strictest requirement across all referencing textures)
//   - pixel density (px/m) clamping: final size must keep density within
//     [minPPM, maxPPM] and must never exceed the island's original size in
//     the texture file
//   - anisotropic refinement: uniform scale first, then per-axis bisection
//   - UV normalization (whole-box translation into [0,1]) recorded so the
//     applier can remap vertices
//   - whole-texture scaling path when no atlas is generated
//
// 质量驱动的 UV 岛缩放。
//   - 目标质量 == 1 -> 跳过缩放（不重采样直接拷贝）
//   - 目标质量 < 1 时纯色岛短路缩到 min(4, 原岛短边)
//   - 二分搜索【仍能通过全部指标】的最小缩放系数（取所有引用贴图的最严苛
//     要求）
//   - 像素密度（px/m）钳制：最终尺寸必须保持密度在 [minPPM, maxPPM] 内，
//     且绝不能超过岛在贴图文件中的原始尺寸
//   - 各向异性细化：先均匀缩放，再逐轴二分
//   - UV 归一化（整体平移到 [0,1]）被记录，供应用器重映射顶点
//   - 不生成图集时的整张贴图缩放路径

using System.Collections.Generic;
using System.Linq;
using net.fosa.avatar_texture_optimizer.editor.logging;
using net.fosa.avatar_texture_optimizer.editor.model;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor.quality
{
    public static class IslandScaler
    {
        private const float MinScale = 1f / 16f;   // binary search floor / 二分搜索下限
        private const float ScaleEps = 0.02f;      // bisection tolerance / 二分容差

        public static void Scale(ATOBuildState state)
        {
            var component = state.Component;
            if (component == null) return;

            var stopwatch = new ATOStopwatch("IslandScaler.Scale");
            int scaledIslands = 0;
            int skippedAtQuality1 = 0;
            int solidShortcuts = 0;
            var wholeScaleTextures = new HashSet<Texture2D>();

            // When no atlas is generated, ALL groups take the whole-texture
            // path. / 不生成图集时，所有组走整图路径。
            if (!component.GenerateAtlas)
            {
                foreach (var g in state.UVGroups)
                    if (!g.Whitelisted)
                        foreach (var u in g.Textures)
                            if (u.Texture != null) wholeScaleTextures.Add(u.Texture);
                ScaleWholeTextures(state, wholeScaleTextures);
                return;
            }

            foreach (var group in state.UVGroups)
            {
                if (group.Whitelisted) continue;
                if (group.SkippedAtlas)
                {
                    // Same-UV textures next to a whitelisted texture: skip
                    // atlasization; whole-texture scaling is applied to this
                    // group's textures (UVs stay unchanged).
                    // 与白名单贴图同 UV 的贴图：跳过图集化；对本组贴图应用
                    // 整图缩放（UV 保持不变）。
                    foreach (var u in group.Textures)
                        if (u.Texture != null) wholeScaleTextures.Add(u.Texture);
                    continue;
                }
                if (group.Textures.Count == 0) continue;

                stopwatch.Begin($"group {group.Space}");

                var thresholds = EffectiveThresholds(component, group);
                var usages = group.Textures;
                var texture = usages[0].Texture;
                if (texture == null) continue;

                // Bucket effect: the group takes the LARGEST required size
                // among its member texture types (least aggressive scaling).
                // 木桶效应：组取成员贴图类型中最大的所需尺寸（最不激进）。
                var maxOriginalSize = group.Textures.Max(u => u.Texture != null ? Mathf.Max(u.Texture.width, u.Texture.height) : 0);
                group.MaxOriginalTextureSize = maxOriginalSize;

                foreach (var island in group.Islands)
                {
                    if (!island.Normalizable)
                    {
                        // Crosses a wrap seam: cannot remap; the group was
                        // already flagged by the extractor.
                        // 跨 wrap 缝：无法重映射；提取器已标记该组。
                        continue;
                    }

                    // Normalize UVs into [0,1] via whole-box translation.
                    // 通过整体平移将 UV 归一化到 [0,1]。
                    var norm = new Vector2(Mathf.Floor(island.BoundsUV.xMin), Mathf.Floor(island.BoundsUV.yMin));
                    island.NormalizeOffset = norm;

                    var islandTexture = texture;
                    var islandRegion = island.PixelBounds;

                    // Target quality == 1: copy without resampling (skip).
                    // 目标质量 == 1：不重采样直接拷贝（跳过）。
                    if (thresholds.TargetQuality >= 1f)
                    {
                        island.ScaledRect = new RectInt(0, 0, islandRegion.width, islandRegion.height);
                        skippedAtQuality1++;
                        continue;
                    }

                    // Solid-color shortcut.
                    // 纯色短路。
                    if (thresholds.SolidColorShortcut && DetectSolid(islandTexture, islandRegion))
                    {
                        island.IsSolidColor = true;
                        int s = Mathf.Min(4, island.OriginalShortSide);
                        island.ScaledRect = new RectInt(0, 0, Mathf.Max(1, s), Mathf.Max(1, s));
                        solidShortcuts++;
                        continue;
                    }

                    // Uniform binary search for the smallest passing scale.
                    // 均匀二分搜索最小通过缩放。
                    float fUniform = BinarySearchUniform(islandTexture, islandRegion, usages, thresholds);
                    if (fUniform <= 0f) continue;

                    // Anisotropic refinement: per-axis bisection.
                    // 各向异性细化：逐轴二分。
                    float fx = fUniform, fy = fUniform;
                    fx = RefineAxis(islandTexture, islandRegion, usages, thresholds, fx, fy, true);
                    fy = RefineAxis(islandTexture, islandRegion, usages, thresholds, fx, fy, false);

                    // Density clamping (never exceed the island's original
                    // physical size; keep density within [minPPM, maxPPM]).
                    // 像素密度钳制（不超岛的原物理尺寸；密度保持在
                    // [minPPM, maxPPM]）。
                    ApplyDensityClamp(group, island, ref fx, ref fy, component);

                    int sw = Mathf.Max(1, Mathf.RoundToInt(islandRegion.width * fx));
                    int sh = Mathf.Max(1, Mathf.RoundToInt(islandRegion.height * fy));
                    island.ScaledRect = new RectInt(0, 0, sw, sh);
                    island.RasterAreaPixels = (long)sw * sh;
                    scaledIslands++;
                }

                stopwatch.End($"group {group.Space}");
            }

            ATOLog.Info($"[ATO] Scaled {scaledIslands} islands (skipped at quality 1: {skippedAtQuality1}, solid shortcuts: {solidShortcuts}). / 缩放 {scaledIslands} 个岛（质量 1 跳过：{skippedAtQuality1}，纯色短路：{solidShortcuts}）。");

            if (wholeScaleTextures.Count > 0)
                ScaleWholeTextures(state, wholeScaleTextures);
        }

        // ====================================================================
        // Whole-texture path (no atlas / skipped-atlas groups) / 整图路径
        // ====================================================================

        private static void ScaleWholeTextures(ATOBuildState state, HashSet<Texture2D> textures)
        {
            var component = state.Component;
            var stopwatch = new ATOStopwatch("IslandScaler.ScaleWholeTextures");

            foreach (var tex in textures)
            {
                // Find the group referencing this texture for thresholds.
                // 查找引用该贴图的组以获取阈值。
                var group = state.UVGroups.FirstOrDefault(g =>
                    g.Textures.Any(u => u.Texture == tex));
                if (group == null) continue;

                var thresholds = EffectiveThresholds(component, group);
                if (thresholds.TargetQuality >= 1f) continue; // copy as-is / 原样拷贝

                var region = new RectInt(0, 0, tex.width, tex.height);
                var usages = group.Textures.Where(u => u.Texture == tex).ToList();
                float f = BinarySearchUniform(tex, region, usages, thresholds);
                if (f <= 0f) continue;

                int sw = Mathf.Max(1, Mathf.RoundToInt(tex.width * f));
                int sh = Mathf.Max(1, Mathf.RoundToInt(tex.height * f));
                state.WholeTextureScale[tex] = new Vector2Int(sw, sh);
                ATOLog.Trace($"whole-texture scale {tex.name}: {tex.width}x{tex.height} -> {sw}x{sh}");
            }
        }

        // ====================================================================
        // Helpers / 辅助
        // ====================================================================

        private static QualityThresholds EffectiveThresholds(AvatarTextureOptimizer component, UVGroup group)
        {
            // The group's thresholds come from the component's quality config.
            // 组的阈值来自组件的质量配置。
            return component.Quality.Thresholds;
        }

        private static float BinarySearchUniform(Texture2D texture, RectInt region,
            List<TextureUsage> usages, QualityThresholds thresholds)
        {
            // Binary search for the smallest f in [MinScale, 1] that passes.
            // Invariant: f = 1 passes (scaling is near-identity); f shrinks
            // monotonically degrade quality.
            // 在 [MinScale, 1] 中二分搜索仍通过的最小 f。
            // 不变式：f = 1 通过（缩放近似恒等）；f 越小质量单调下降。
            float lo = MinScale, hi = 1f;
            if (!Passes(texture, region, usages, thresholds, hi, hi)) return -1f; // sanity / 健全性检查

            while (hi - lo > ScaleEps)
            {
                float mid = (lo + hi) * 0.5f;
                if (Passes(texture, region, usages, thresholds, mid, mid))
                    hi = mid;
                else
                    lo = mid;
            }
            return hi;
        }

        private static float RefineAxis(Texture2D texture, RectInt region,
            List<TextureUsage> usages, QualityThresholds thresholds,
            float fx, float fy, bool isX)
        {
            float lo = MinScale, hi = isX ? fx : fy;
            if (!Passes(texture, region, usages, thresholds, fx, fy)) return hi;
            while (hi - lo > ScaleEps)
            {
                float mid = (lo + hi) * 0.5f;
                bool pass = isX
                    ? Passes(texture, region, usages, thresholds, mid, fy)
                    : Passes(texture, region, usages, thresholds, fx, mid);
                if (pass) hi = mid; else lo = mid;
            }
            return hi;
        }

        private static bool Passes(Texture2D texture, RectInt region,
            List<TextureUsage> usages, QualityThresholds thresholds, float fx, float fy)
        {
            var result = QualityEvaluator.Evaluate(texture, region, usages, thresholds, fx, fy);
            return result.Pass;
        }

        private static void ApplyDensityClamp(UVGroup group, UVIsland island,
            ref float fx, ref float fy, AvatarTextureOptimizer component)
        {
            float origPPM = island.PixelDensityPPM;
            if (origPPM <= 0f) return;

            var q = component.Quality;
            float minPPM = q.MinPixelsPerMeter;
            float maxPPM = q.MaxPixelsPerMeter;

            // Desired scale to keep density in range; never upscale beyond 1
            // (island cannot exceed its original physical size).
            // 保持密度在范围内的期望缩放；绝不放大超过 1（岛不能超过其原始
            // 物理尺寸）。
            float fMaxDensity = Mathf.Min(1f, maxPPM / origPPM);
            float fMinDensity = minPPM / origPPM;

            // The final uniform scale must be within [fMinDensity, fMaxDensity].
            // 最终均匀缩放必须在 [fMinDensity, fMaxDensity] 内。
            float current = Mathf.Min(fx, fy);
            if (current < fMinDensity)
            {
                // Density says don't shrink below this; restore to the lower
                // density bound (quality already passed at the larger size).
                // 密度要求不要缩得低于此值；恢复到密度下限（更大的尺寸
                // 质量必然通过）。
                float ratio = fMinDensity / current;
                fx = Mathf.Min(1f, fx * ratio);
                fy = Mathf.Min(1f, fy * ratio);
            }
            else if (current > fMaxDensity)
            {
                // Density says we can shrink more; apply it.
                // 密度表示可以进一步缩小；应用它。
                float ratio = fMaxDensity / current;
                fx *= ratio;
                fy *= ratio;
            }

            // Clamp by the UV group's max original texture size.
            // 以 UV 组的最大原贴图尺寸钳制。
            float maxDimF = group.MaxOriginalTextureSize > 0
                ? group.MaxOriginalTextureSize / (float)Mathf.Max(island.PixelBounds.width, island.PixelBounds.height)
                : 1f;
            fx = Mathf.Min(fx, Mathf.Min(1f, maxDimF));
            fy = Mathf.Min(fy, Mathf.Min(1f, maxDimF));
            fx = Mathf.Max(fx, 1f / 1024f);
            fy = Mathf.Max(fy, 1f / 1024f);
        }

        private static bool DetectSolid(Texture2D texture, RectInt region)
        {
            if (texture == null) return false;
            try
            {
                if (!texture.isReadable) return false;
                // Sample a small grid of pixels inside the region.
                // 在区域内采样小网格像素。
                int samples = 9;
                var c0 = texture.GetPixel(
                    Mathf.Clamp(region.x + region.width / 2, 0, texture.width - 1),
                    Mathf.Clamp(region.y + region.height / 2, 0, texture.height - 1));
                for (int i = 0; i < samples; i++)
                {
                    int px = region.x + Mathf.Clamp(region.width * i / samples, 0, region.width - 1);
                    int py = region.y + Mathf.Clamp(region.height * (i * 7919 % samples) / samples, 0, region.height - 1);
                    var c = texture.GetPixel(px, py);
                    if (!NearlyEqual(c, c0)) return false;
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool NearlyEqual(Color a, Color b) =>
            Mathf.Abs(a.r - b.r) < 0.002f && Mathf.Abs(a.g - b.g) < 0.002f &&
            Mathf.Abs(a.b - b.b) < 0.002f && Mathf.Abs(a.a - b.a) < 0.002f;
    }
}
