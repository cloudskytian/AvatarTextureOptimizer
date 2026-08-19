// ============================================================================
// QualityStage.cs — 阶段5：目标质量缩放 / Stage 5: quality-gated scaling
// (EN) For each UV island, binary-searches the largest downscale (uniform first,
//      then per-axis for anisotropy) that still meets all quality thresholds
//      across every referencing texture (barrel/cask effect: most restrictive
//      wins). Applies pixel-density clamping, pure-color short-circuit, and
//      skips entirely when target quality is ~1.
// (ZH) 对每个 UV 岛，二分搜索仍满足全部质量阈值的最大缩放（先均匀，再逐轴做
//      各向异性细化），并跨所有引用贴图取木桶效应（最严苛者胜）。应用像素密度
//      钳制、纯色短路，目标质量≈1 时整体跳过。
// ============================================================================

using System;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer
{
    public class QualityStage
    {
        private readonly ATOBuildContext _ctx;
        private readonly ATOIslandResult _islands;
        private ATOQualityThresholds _th;

        public QualityStage(ATOBuildContext ctx, ATOIslandResult islands)
        {
            _ctx = ctx;
            _islands = islands;
        }

        public void Run()
        {
            _th = _ctx.Quality.GetEffective();

            // 目标质量≈1（近无损）→ 全部跳过缩放 / near-lossless → skip all scaling
            if (_ctx.Quality.preset == ATOQualityPreset.Lossless)
            {
                foreach (var g in _islands.UvGroups)
                    foreach (var i in g.Islands)
                        i.SkipScaling = true;
                ATOLog.Info("[quality] Lossless preset: skipping all island scaling");
                return;
            }

            if (_ctx.Atlas.enableAtlas)
                ScalePerIsland();
            else
                ScaleWholeTextureAll();

            ATOLog.VerboseLog("[quality] scaling complete");
        }

        // ---------------------------------------------------------------------
        // 逐岛缩放（图集模式）/ per-island scaling (atlas mode)
        // ---------------------------------------------------------------------
        private void ScalePerIsland()
        {
            // 收集被不安全岛（白名单/ST变换/跨缝）引用的贴图
            // collect textures referenced by unsafe islands (whitelist/ST-transform/cross-seam)
            var unsafeTextures = new HashSet<ATOTextureRef>();
            foreach (var g in _islands.UvGroups)
                foreach (var i in g.Islands)
                    if (i.HasUnsafeReference || i.CrossesWrapSeam)
                        foreach (var t in i.ReferencingTextures)
                            unsafeTextures.Add(t);

            // 传播：若贴图不安全，其所有岛都跳过图集化（否则安全岛进图集、不安全岛不进的矛盾）
            // propagate: unsafe texture -> ALL its islands skip atlas
            foreach (var g in _islands.UvGroups)
                foreach (var i in g.Islands)
                    foreach (var t in i.ReferencingTextures)
                        if (unsafeTextures.Contains(t))
                            i.HasUnsafeReference = true;

            // 对不安全贴图计算整图缩放 / whole scaling for unsafe textures
            foreach (var tex in unsafeTextures)
            {
                if (tex.Whitelisted || tex.Texture == null) continue;
                tex.WholeScaleX = SearchWholeScale(tex, true);
                tex.WholeScaleY = SearchWholeScale(tex, false);
            }

            // 逐岛缩放（仅安全岛）/ per-island scaling (safe islands only)
            foreach (var group in _islands.UvGroups)
            {
                foreach (var island in group.Islands)
                {
                    if (island.HasUnsafeReference || island.CrossesWrapSeam) continue;
                    ComputeIslandScale(island, group);
                }
            }
        }

        private void ComputeIslandScale(ATOUVIsland island, ATOUVGroup group)
        {
            // 取岛引用的贴图（去重）/ distinct referencing textures
            var textures = new System.Collections.Generic.List<ATOTextureRef>();
            foreach (var t in island.ReferencingTextures)
                if (!textures.Contains(t)) textures.Add(t);
            if (textures.Count == 0) return;

            // 纯色短路：仅当所有引用贴图均为纯色才短路 / pure-color shortcut only if ALL textures are pure
            bool allPure = true;
            foreach (var tex in textures)
                if (!IsPureColor(island, tex)) { allPure = false; break; }
            if (allPure)
            {
                ApplyPureColorScale(island, textures[0]);
                return;
            }

            // 逐贴图二分，取木桶效应最大尺寸 / per-texture binary search, barrel effect
            float uniformScale = 0f; // max across textures
            foreach (var tex in textures)
            {
                float s = SearchUniformScale(island, tex);
                uniformScale = Mathf.Max(uniformScale, s);
            }

            // 各向异性细化 / anisotropic refinement
            float sx = SearchAxisScale(island, textures, uniformScale, true);
            float sy = SearchAxisScale(island, textures, sx, false);

            // 像素密度钳制 / pixel density clamp
            var (minScale, maxScale) = DensityClamp(island);
            sx = Mathf.Clamp(sx, minScale, maxScale);
            sy = Mathf.Clamp(sy, minScale, maxScale);

            island.ScaleX = sx;
            island.ScaleY = sy;
        }

        /// <summary>(EN) Binary search the minimum uniform scale that passes. (ZH) 二分搜索通过的最小均匀缩放。</summary>
        private float SearchUniformScale(ATOUVIsland island, ATOTextureRef tex)
        {
            float lo = 0f, hi = 1f;
            // 若 1.0 本身不通过（理论上不会），直接返回 1
            if (!Pass(island, tex, 1f, 1f)) return 1f;
            for (int i = 0; i < 8; i++)
            {
                float mid = (lo + hi) * 0.5f;
                if (Pass(island, tex, mid, mid)) hi = mid;
                else lo = mid;
            }
            return hi;
        }

        /// <summary>(EN) Binary search one axis while the other stays fixed. (ZH) 固定一轴，二分搜索另一轴。</summary>
        private float SearchAxisScale(ATOUVIsland island, System.Collections.Generic.List<ATOTextureRef> textures, float fixedScale, bool searchX)
        {
            float lo = 0f, hi = fixedScale;
            for (int i = 0; i < 6; i++)
            {
                float mid = (lo + hi) * 0.5f;
                float sx = searchX ? mid : fixedScale;
                float sy = searchX ? fixedScale : mid;
                bool allPass = true;
                foreach (var tex in textures)
                    if (!Pass(island, tex, sx, sy)) { allPass = false; break; }
                if (allPass) hi = mid; else lo = mid;
            }
            return hi;
        }

        // ---------------------------------------------------------------------
        // 质量判定 / quality pass check
        // ---------------------------------------------------------------------
        private bool Pass(ATOUVIsland island, ATOTextureRef tex, float sx, float sy)
        {
            int tw = tex.Texture.width, th = tex.Texture.height;

            // 裁剪岛区域 / crop island region
            int rx = Mathf.FloorToInt(island.Bounds.xMin * tw);
            int ry = Mathf.FloorToInt(island.Bounds.yMin * th);
            int rw = Mathf.Max(1, Mathf.CeilToInt(island.Bounds.width * tw));
            int rh = Mathf.Max(1, Mathf.CeilToInt(island.Bounds.height * th));
            rw = Mathf.Min(rw, tw - rx); rh = Mathf.Min(rh, th - ry);

            var orig = ATOTextureIO.ReadRegion(tex.Texture, rx, ry, rw, rh);
            int n = rw * rh;

            int dw = Mathf.Max(1, Mathf.RoundToInt(rw * sx));
            int dh = Mathf.Max(1, Mathf.RoundToInt(rh * sy));

            // 缩小后再放大回原尺寸 / downsample then upsample back
            var down = new Color[dw * dh];
            ATOQuality.ResampleRegion(orig, rw, rh, 0, 0, rw, rh, dw, dh,
                linearSpace: true, premultiplyAlpha: HasAlpha(tex), down);
            var up = new Color[n];
            ATOQuality.ResampleRegion(down, dw, dh, 0, 0, dw, dh, rw, rh,
                linearSpace: true, premultiplyAlpha: HasAlpha(tex), up);

            int shortSide = Mathf.Min(rw, rh);
            bool ignoreSsim = shortSide < _ctx.Quality.ignoreSsimShortSide;
            bool singleScale = shortSide < _ctx.Quality.ssImSingleScaleShortSide;

            switch (tex.Usage)
            {
                case ATOTextureUsage.NormalMap:
                    return ATOQuality.NormalP95Angle(orig, up, n, _th.normalP95) <= _th.normalAngleErrorDeg;

                case ATOTextureUsage.Mask:
                case ATOTextureUsage.Grayscale:
                    return ATOQuality.GrayRmse(orig, up, n) <= _th.grayRmse;

                default: // MainColor
                    return PassColor(orig, up, rw, rh, tex, ignoreSsim, singleScale);
            }
        }

        private bool PassColor(Color[] orig, Color[] up, int w, int h, ATOTextureRef tex, bool ignoreSsim, bool singleScale)
        {
            bool transparent = tex.Classification == ATOTextureClass.Transparent;

            // MS-SSIM / SSIM
            if (!ignoreSsim)
            {
                var lumA = new float[w * h];
                var lumB = new float[w * h];
                for (int i = 0; i < w * h; i++)
                {
                    lumA[i] = ATOQuality.Luminance(orig[i]);
                    lumB[i] = ATOQuality.Luminance(up[i]);
                }
                float ssim = singleScale ? ATOQuality.SSIM(lumA, lumB, w, h) : ATOQuality.MSSSIM(lumA, lumB, w, h);
                if (ssim < _th.msSsim) return false;
            }

            // ΔE2000（均值）/ mean CIEDE2000
            double deSum = 0; int n = w * h;
            for (int i = 0; i < n; i++)
                deSum += ATOQuality.DeltaE2000(orig[i].r, orig[i].g, orig[i].b, up[i].r, up[i].g, up[i].b);
            float meanDe = (float)(deSum / n);
            if (meanDe > _th.deltaE2000) return false;

            // alpha（透明贴图，Cutout IoU 与 Blend RMSE 均需通过，取最严苛）
            if (transparent)
            {
                if (ATOMaxMismatch(orig, up, n) > _th.alphaRmse) return false;
                if (ATOMaxMismatchIoU(orig, up, n, 0.5f) < _th.alphaIoU) return false;
            }

            return true;
        }

        private float ATOMaxMismatch(Color[] a, Color[] b, int n)
        {
            double sum = 0;
            for (int i = 0; i < n; i++) { double d = a[i].a - b[i].a; sum += d * d; }
            return (float)Math.Sqrt(sum / n);
        }

        private float ATOMaxMismatchIoU(Color[] a, Color[] b, int n, float cutoff)
        {
            long inter = 0, union = 0;
            for (int i = 0; i < n; i++)
            {
                bool ba = a[i].a >= cutoff, bb = b[i].a >= cutoff;
                if (ba && bb) inter++;
                if (ba || bb) union++;
            }
            return union == 0 ? 1f : (float)inter / union;
        }

        private static bool HasAlpha(ATOTextureRef tex) => tex.Classification == ATOTextureClass.Transparent;

        // ---------------------------------------------------------------------
        // 纯色短路 / pure-color short-circuit
        // ---------------------------------------------------------------------
        private bool IsPureColor(ATOUVIsland island, ATOTextureRef tex)
        {
            int tw = tex.Texture.width, th = tex.Texture.height;
            int rx = Mathf.FloorToInt(island.Bounds.xMin * tw);
            int ry = Mathf.FloorToInt(island.Bounds.yMin * th);
            int rw = Mathf.Max(1, Mathf.CeilToInt(island.Bounds.width * tw));
            int rh = Mathf.Max(1, Mathf.CeilToInt(island.Bounds.height * th));
            rw = Mathf.Min(rw, tw - rx); rh = Mathf.Min(rh, th - ry);
            if (rw * rh == 0) return true;

            var region = ATOTextureIO.ReadRegion(tex.Texture, rx, ry, rw, rh);
            var first = region[0];
            for (int i = 1; i < region.Length; i++)
                if (region[i] != first) return false;
            return true;
        }

        private void ApplyPureColorScale(ATOUVIsland island, ATOTextureRef tex)
        {
            // 短边取当前贴图分辨率下的岛短边 / short side at this texture's resolution
            int tw = tex.Texture.width, th = tex.Texture.height;
            int rw = Mathf.Max(1, Mathf.CeilToInt(island.Bounds.width * tw));
            int rh = Mathf.Max(1, Mathf.CeilToInt(island.Bounds.height * th));
            int shortSide = Mathf.Min(rw, rh);
            int target = Mathf.Min(4, shortSide);
            float s = (float)target / Mathf.Max(1, shortSide);
            island.ScaleX = Mathf.Min(island.ScaleX, s);
            island.ScaleY = Mathf.Min(island.ScaleY, s);
        }

        // ---------------------------------------------------------------------
        // 像素密度钳制 / pixel density clamp
        // ---------------------------------------------------------------------
        private (float min, float max) DensityClamp(ATOUVIsland island)
        {
            // 岛的世界空间面积（含动画缩放，取最大缩放保守估计）
            // island world area (incl. animation scale, conservative max)
            float worldArea = island.WorldArea * island.MaxAreaScale * island.MaxAreaScale;

            // 形态键最大面积已在 island.MaxBlendArea 计入（由 IslandStage 填充）
            worldArea = Mathf.Max(worldArea, island.MaxBlendArea);

            float targetMinPx = worldArea * _ctx.Quality.minPixelDensity * _ctx.Quality.minPixelDensity;
            float targetMaxPx = worldArea * _ctx.Quality.maxPixelDensity * _ctx.Quality.maxPixelDensity;

            float origPx = (float)island.PixelWidth * island.PixelHeight;
            if (origPx <= 0) return (0f, 1f);

            float maxScale = Mathf.Min(1f, Mathf.Sqrt(targetMaxPx / origPx));
            float minScale = Mathf.Min(1f, Mathf.Sqrt(targetMinPx / origPx));
            if (minScale > maxScale) minScale = maxScale;
            return (minScale, maxScale);
        }

        // ---------------------------------------------------------------------
        // 整图缩放（不生成图集）/ whole-texture scaling (no atlas)
        // ---------------------------------------------------------------------
        private void ScaleWholeTextureAll()
        {
            // 对每张唯一贴图做整图二分缩放 / whole-texture binary search per unique texture
            foreach (var tex in _ctx.Collect.Canonical.Values)
            {
                if (tex.Whitelisted || tex.Texture == null) continue;
                if (tex.Usage == ATOTextureUsage.Other) continue;

                tex.WholeScaleX = SearchWholeScale(tex, true);
                tex.WholeScaleY = SearchWholeScale(tex, false);
            }
        }

        private float SearchWholeScale(ATOTextureRef tex, bool searchX)
        {
            int tw = tex.Texture.width, th = tex.Texture.height;
            var src = ATOTextureIO.ReadRegion(tex.Texture, 0, 0, tw, th);

            float lo = 0f, hi = 1f;
            for (int i = 0; i < 8; i++)
            {
                float mid = (lo + hi) * 0.5f;
                float sx = searchX ? mid : 1f;
                float sy = searchX ? 1f : mid;
                if (PassWhole(src, tw, th, tex, sx, sy)) hi = mid;
                else lo = mid;
            }
            return hi;
        }

        private bool PassWhole(Color[] src, int tw, int th, ATOTextureRef tex, float sx, float sy)
        {
            int dw = Mathf.Max(1, Mathf.RoundToInt(tw * sx));
            int dh = Mathf.Max(1, Mathf.RoundToInt(th * sy));
            var down = new Color[dw * dh];
            ATOQuality.ResampleRegion(src, tw, th, 0, 0, tw, th, dw, dh,
                linearSpace: true, premultiplyAlpha: HasAlpha(tex), down);
            var up = new Color[tw * th];
            ATOQuality.ResampleRegion(down, dw, dh, 0, 0, dw, dh, tw, th,
                linearSpace: true, premultiplyAlpha: HasAlpha(tex), up);

            int shortSide = Mathf.Min(tw, th);
            bool ignoreSsim = shortSide < _ctx.Quality.ignoreSsimShortSide;
            bool singleScale = shortSide < _ctx.Quality.ssImSingleScaleShortSide;

            switch (tex.Usage)
            {
                case ATOTextureUsage.NormalMap:
                    return ATOQuality.NormalP95Angle(src, up, tw * th, _th.normalP95) <= _th.normalAngleErrorDeg;
                case ATOTextureUsage.Mask:
                case ATOTextureUsage.Grayscale:
                    return ATOQuality.GrayRmse(src, up, tw * th) <= _th.grayRmse;
                default:
                    return PassColor(src, up, tw, th, tex, ignoreSsim, singleScale);
            }
        }
    }
}
