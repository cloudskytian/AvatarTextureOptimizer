// QualityEvaluator.cs - Island-aware quality search: bisection on uniform scale, then independent
// axis refinement (anisotropy); density clamps by real-world size; pure-color short-circuit.
// 岛级质量搜索：均匀尺度二分，随后双轴独立细化（各向异性）；按真实大小做密度钳制；纯色短路。
// The final island size takes the MAX across every covering texture (barrel principle / 木桶效应取最大),
// never exceeding the largest original size in the group / 且不超过组内最大原始尺寸。
using System;
using System.Collections.Generic;
using System.Linq;
using Fosa.ATO.Editor.Analysis;
using Fosa.ATO.Editor.Core;
using Fosa.ATO.Runtime;
using UnityEngine;

namespace Fosa.ATO.Editor.Quality
{
    public static class QualityEvaluator
    {
        private const int MinIslandPx = 4;         // hard floor / 硬下限
        private const float BisectEps = 0.02f;     // 2% scale precision / 尺度精度2%

        public static void ProcessAll(UsageGraph g, ATOSettings st, GPUTexOps ops, ATOProgress progress)
        {
            using (ATOLog.Scope("QualityEval"))
            {
                bool lossless = IsLossless(st);
                // 1) pixel scans / 像素扫描
                int i = 0, total = g.textures.Count;
                foreach (var e in g.textures)
                {
                    progress?.Report(i++ / (float)Mathf.Max(1, total), "Pixel scans");
                    if (e.whitelisted) continue;
                    var (usesAlpha, isGray) = ops.ScanTexture(e.texture);
                    e.usesAlpha = usesAlpha;
                    e.usesColor_ = !isGray;
                }

                // 2) per group / 逐组
                int gi = 0;
                foreach (var grp in g.groups)
                {
                    progress?.Report(gi++ / (float)Mathf.Max(1, g.groups.Count), "Quality search");
                    if (!grp.Processable) { foreach (var isl in grp.islands) { isl.targetW = SrcW(grp, isl); isl.targetH = SrcH(grp, isl); } continue; }
                    ProcessGroup(g, grp, st, ops, lossless);
                }
            }
        }

        private static void ProcessGroup(UsageGraph g, UvGroup grp, ATOSettings st, GPUTexOps ops, bool lossless)
        {
            // source reference size: max covering texture dims mapped by island bbox / 参考尺寸=最大覆盖贴图
            foreach (var isl in grp.islands)
            {
                int bestW = 1, bestH = 1;
                foreach (var e in grp.textures)
                {
                    if (e.whitelisted) continue;
                    var (w, h) = IslandTarget(e, isl, st, ops, lossless);
                    bestW = Mathf.Max(bestW, w); bestH = Mathf.Max(bestH, h);
                }
                // clamp by largest original island size in group / 不超过组内最大原始尺寸
                int maxSrcW = 1, maxSrcH = 1;
                foreach (var e in grp.textures)
                {
                    int rw = Mathf.CeilToInt((isl.uvMax.x - isl.uvMin.x) * e.texture.width);
                    int rh = Mathf.CeilToInt((isl.uvMax.y - isl.uvMin.y) * e.texture.height);
                    maxSrcW = Mathf.Max(maxSrcW, rw); maxSrcH = Mathf.Max(maxSrcH, rh);
                }
                isl.targetW = Mathf.Clamp(bestW, MinIslandPx, maxSrcW);
                isl.targetH = Mathf.Clamp(bestH, MinIslandPx, maxSrcH);
            }
        }

        /// <summary>Target pixel size of an island for one texture (quality + density). / 单贴图视角的岛目标像素尺寸。</summary>
        private static (int, int) IslandTarget(TexEntry e, Island isl, ATOSettings st, GPUTexOps ops, bool lossless)
        {
            var tex = e.texture;
            var region = RegionOf(isl, tex);
            int srcW = Mathf.Max(1, region.width), srcH = Mathf.Max(1, region.height);

            if (lossless) return (srcW, srcH); // no resample / 原样拷贝

            // pure color short-circuit / 纯色短路
            if (!isl.pureColorChecked)
            {
                isl.pureColor = ops.IsPureColor(tex, region);
                isl.pureColorChecked = true;
            }
            if (isl.pureColor)
            {
                int mn = Mathf.Min(4, Mathf.Min(srcW, srcH));
                return (Mathf.Max(mn, MinIslandPx), Mathf.Max(mn, MinIslandPx));
            }

            // density bounds / 密度边界
            float meters = Mathf.Sqrt(Mathf.Max(1e-6f, isl.worldAreaM2));
            float dMin = (int)st.minDensity, dMax = (int)st.maxDensity;
            int pxMin = Mathf.CeilToInt(meters * dMin);
            int pxMax = Mathf.CeilToInt(meters * dMax);

            // uniform bisection / 均匀二分
            var (sw, sh) = BisectAxis(e, isl, region, st, ops, 1f, 1f, uniform: true);
            // anisotropic refine / 各向异性细化
            (sw, sh) = BisectAxis(e, isl, region, st, ops, sw, sh, uniform: false, refineX: true);
            (sw, sh) = BisectAxis(e, isl, region, st, ops, sw, sh, uniform: false, refineX: false);

            int w = Mathf.Clamp(Mathf.RoundToInt(srcW * sw), MinIslandPx, srcW);
            int h = Mathf.Clamp(Mathf.RoundToInt(srcH * sh), MinIslandPx, srcH);
            // density clamps (px per meter) / 密度钳制
            float pxPerM = Mathf.Max(w / meters, h / meters);
            if (pxPerM > dMax) { float f = dMax / pxPerM; w = Mathf.Max(MinIslandPx, Mathf.RoundToInt(w * f)); h = Mathf.Max(MinIslandPx, Mathf.RoundToInt(h * f)); }
            if (pxPerM < dMin) { float f = dMin / pxPerM; w = Mathf.Min(srcW, Mathf.RoundToInt(w * f)); h = Mathf.Min(srcH, Mathf.RoundToInt(h * f)); }
            return (w, h);
        }

        /// <summary>Bisect one scale factor while the other stays fixed. / 一轴固定时对另一轴二分。</summary>
        private static (float, float) BisectAxis(TexEntry e, Island isl, RectInt region, ATOSettings st, GPUTexOps ops, float sx, float sy, bool uniform, bool refineX = false)
        {
            float hi = uniform ? 1f : (refineX ? sx : sy); // known-pass bound / 已知通过的上界
            float lo = 1f / 64f;                            // fail bound / 失败下界
            if (Pass(e, isl, region, lo, lo, st, ops)) return (lo, lo); // already minimal / 已最小
            if (uniform)
            {
                // exponential probe down then bisect / 指数探测后二分
                float p = 1f;
                while (p > lo) { float next = Mathf.Max(lo, p * 0.5f); if (Pass(e, isl, region, next, next, st, ops)) { p = next; } else { lo = next; break; } if (next <= lo) break; }
                if (p <= lo + 1e-6f) return (hi, hi);
                float a = p, b = hi; // a passes, b fails / a通过b失败
                // standard: find boundary between passing p and failing 2p / 在通过与失败间二分
                float failB = Mathf.Min(1f, p * 2f);
                while (failB / p > 1f + BisectEps)
                {
                    float mid = Mathf.Sqrt(p * failB);
                    if (Pass(e, isl, region, mid, mid, st, ops)) p = mid; else failB = mid;
                }
                return (p, p);
            }
            else
            {
                float known = refineX ? sx : sy;
                float failB = known; // current known-pass; find smaller / 当前已通过，找更小
                float tryS = known * 0.5f;
                if (!Pass(e, isl, region, refineX ? tryS : known, refineX ? known : tryS, st, ops)) return (sx, sy); // cannot shrink this axis / 此轴无法缩小
                float passS = tryS;
                while (failB / passS > 1f + BisectEps)
                {
                    float mid = Mathf.Sqrt(passS * failB);
                    bool ok = Pass(e, isl, region, refineX ? mid : known, refineX ? known : mid, st, ops);
                    if (ok) passS = mid; else failB = mid;
                }
                return refineX ? (passS, sy) : (sx, passS);
            }
        }

        /// <summary>Does scale (sx,sy) pass every threshold for every usage? / 该尺度是否通过全部用途的全部阈值？</summary>
        private static bool Pass(TexEntry e, Island isl, RectInt region, float sx, float sy, ATOSettings st, GPUTexOps ops)
        {
            int dw = Mathf.Max(1, Mathf.RoundToInt(region.width * sx));
            int dh = Mathf.Max(1, Mathf.RoundToInt(region.height * sy));
            if (dw >= region.width && dh >= region.height) return true; // identity / 恒等
            var q = st.quality;
            var cat = e.Category();
            int shortSide = Mathf.Min(region.width, region.height);
            int scales = shortSide < 176 ? 1 : 5;               // MS-SSIM fallback / 回退
            int ssimOn = shortSide < 11 ? 0 : 1;                // window floor / 窗口下限
            var cutoffs = CollectCutoffs(e);
            bool anyBlend = e.usages.Any(u => u.alphaMode == ATOAlphaMode.Blend);
            bool anyCutout = e.usages.Any(u => u.alphaMode == ATOAlphaMode.Cutout);

            var task = new EvalTask
            {
                tex = e.texture, region = region, dstW = dw, dstH = dh,
                isNormal = cat == ATOTextureCategory.NormalMap,
                transparent = cat == ATOTextureCategory.Transparent,
                cutoff = anyCutout ? cutoffs.FirstOrDefault() : 0.5f,
                ssimScales = ssimOn == 1 ? scales : 1,
            };
            var m = ops.Evaluate(task, cat);

            if (ssimOn == 1 && m.ssim < q.msSsimMin) return false;
            if (cat == ATOTextureCategory.Opaque || cat == ATOTextureCategory.Transparent)
            {
                if (m.dEMean > q.deltaEMeanMax) return false;
                if (m.dEP95 > q.deltaEP95Max) return false;
            }
            if (cat == ATOTextureCategory.Transparent)
            {
                if (anyBlend && m.alphaRmse > q.alphaRmseMax) return false;
                if (anyCutout)
                {
                    foreach (var c in cutoffs)
                    {
                        var t2 = task; t2.cutoff = c;
                        var m2 = c == task.cutoff ? m : ops.Evaluate(t2, cat);
                        if (m2.alphaIou < q.alphaCutoutIouMin) return false;
                    }
                }
            }
            if (cat == ATOTextureCategory.NormalMap)
            {
                if (m.nMeanDeg > q.normalMeanDegMax) return false;
                if (m.nP95Deg > q.normalP95DegMax) return false;
            }
            if (cat == ATOTextureCategory.Grayscale)
            {
                float worst = Mathf.Max(m.grayRmse.x, Mathf.Max(m.grayRmse.y, m.grayRmse.z));
                if (worst > q.grayRmseMax) return false;
            }
            return true;
        }

        private static List<float> CollectCutoffs(TexEntry e)
        {
            var l = new List<float>();
            foreach (var u in e.usages) if (u.alphaMode == ATOAlphaMode.Cutout) l.Add(u.cutoff);
            if (l.Count == 0) l.Add(0.5f);
            return l.Distinct().ToList();
        }

        private static bool IsLossless(ATOSettings st)
        {
            var q = st.quality;
            return q.msSsimMin >= 1f && q.deltaEMeanMax <= 1.0001f && q.deltaEP95Max <= 2.0001f
                && q.alphaRmseMax <= 1f / 254f && q.alphaCutoutIouMin >= 0.9999f
                && q.normalMeanDegMax <= 1.0001f && q.normalP95DegMax <= 2.0001f && q.grayRmseMax <= 1f / 254f
                || st.qualityPreset == ATOQualityPreset.NearLossless;
        }

        /// <summary>Whole-texture scaling (no-atlas mode / skipped groups): quality bisection over the full image. / 整图缩放（无图集模式或被跳过的组）：全图质量二分。</summary>
        public static void ProcessWholeTextures(UsageGraph g, ATOSettings st, GPUTexOps ops, ATOProgress progress, Func<TexEntry, bool> filter)
        {
            bool lossless = IsLossless(st);
            int i = 0;
            var list = g.textures.Where(t => !t.whitelisted && filter(t)).ToList();
            foreach (var e in list)
            {
                progress?.Report(i++ / (float)Mathf.Max(1, list.Count), "Whole-texture scale");
                if (lossless) { e.wholeScale = 1f; continue; }
                var region = new RectInt(0, 0, e.texture.width, e.texture.height);
                var fake = new Island(); // host for pure-color flag / 纯色标记宿主
                if (ops.IsPureColor(e.texture, region)) { e.wholeScale = Mathf.Min(4f / Mathf.Max(1, Mathf.Min(region.width, region.height)), 1f); continue; }
                float lo = 1f / 64f, pass = 1f;
                if (Pass(e, fake, region, lo, lo, st, ops)) pass = lo;
                else
                {
                    float fail = 1f;
                    while (fail / pass > 1f + BisectEps)
                    {
                        float mid = Mathf.Sqrt(pass * fail);
                        if (Pass(e, fake, region, mid, mid, st, ops)) pass = mid; else fail = mid;
                    }
                }
                e.wholeScale = pass;
                ATOLog.Detail("whole-scale " + e.texture.name + ": " + pass.ToString("F3"));
            }
        }

        private static RectInt RegionOf(Island isl, Texture2D tex)
        {
            int x = Mathf.Clamp(Mathf.FloorToInt(isl.uvMin.x * tex.width), 0, tex.width - 1);
            int y = Mathf.Clamp(Mathf.FloorToInt(isl.uvMin.y * tex.height), 0, tex.height - 1);
            int w = Mathf.Clamp(Mathf.CeilToInt((isl.uvMax.x - isl.uvMin.x) * tex.width), 1, tex.width - x);
            int h = Mathf.Clamp(Mathf.CeilToInt((isl.uvMax.y - isl.uvMin.y) * tex.height), 1, tex.height - y);
            return new RectInt(x, y, w, h);
        }

        private static int SrcW(UvGroup grp, Island isl) { int m = 1; foreach (var t in grp.textures) m = Mathf.Max(m, Mathf.CeilToInt((isl.uvMax.x - isl.uvMin.x) * t.texture.width)); return m; }
        private static int SrcH(UvGroup grp, Island isl) { int m = 1; foreach (var t in grp.textures) m = Mathf.Max(m, Mathf.CeilToInt((isl.uvMax.y - isl.uvMin.y) * t.texture.height)); return m; }
    }

}
