// ============================================================================
// AvatarTextureOptimizer (net.fosa.avatar-texture-optimizer)
// Textures/IslandScaler.cs — UV 岛质量缩放 / Quality-driven UV island scaling
//
// 需求:
//  - 目标质量算法: 线性空间重采样；透明预乘 alpha 下采样；MS-SSIM(+ΔE/alpha)；
//    法线角度误差 p95；灰度逐通道 RMSE 取最差；多材质引用逐一评估取最严苛。
//  - 缩小后的岛覆盖区双线性上采样回原尺寸后与原图比较。
//  - UV 缩放二分搜索；最差阈值全达标才算通过；UV 组木桶效应取最大尺寸(≤组内最大原尺寸)。
//  - 像素密度钳制(默认 2048~4096 px/m，受原贴图尺寸钳制)。
//  - 质量!=1 纯色岛短路缩到 min(4, 原岛短边)；质量==1 跳过缩放(不重采样原样拷贝)。
//  - 各向异性: 先均匀缩放至全部达标，再双轴独立二分细化。
// ============================================================================
using System;
using System.Collections.Generic;
using UnityEngine;
using api = net.fosa.avatar_texture_optimizer.editor.api;

namespace net.fosa.avatar_texture_optimizer.editor
{
    /// <summary>
    /// 缩放上下文 / Scaling context.
    /// </summary>
    public sealed class ScalerContext
    {
        public ATOComponent cfg;
        public TextureDecodeCache cache;
        public AnimationData anim;
        public MetricParams metricParams;

        public ScalerContext(ATOComponent cfg, TextureDecodeCache cache, AnimationData anim)
        {
            this.cfg = cfg;
            this.cache = cache;
            this.anim = anim;
            var q = cfg.EffectiveQuality();
            metricParams = new MetricParams
            {
                msSsim = q.msSsim,
                maxDeltaE = q.maxDeltaE,
                minCutoutIoU = q.minAlphaCutoutIoU,
                maxBlendRmse = q.maxAlphaBlendRmse,
                maxNormalAngle = q.maxNormalAngleDeg,
                maxGrayRmse = q.maxGrayRmse,
            };
        }
    }

    /// <summary>
    /// 岛缩放器 / Island scaler.
    /// </summary>
    public static class IslandScaler
    {
        private const float RegionEps = 1e-4f;

        /// <summary>
        /// 缩放 UV 组内全部岛的尺寸（按纹理逐个体评估，组级取木桶效应）/
        /// Scale all islands of a UV group (per-texture evaluation, group-level bucket effect).
        /// </summary>
        public static void ScaleGroup(UVGroup group, ScalerContext ctx)
        {
            bool nearLossless = ctx.metricParams.msSsim >= 0.9995f && ctx.metricParams.maxDeltaE <= 0.5f;
            group.needsScaling = !nearLossless;

            foreach (var tref in group.textures)
            {
                if (tref.whitelisted || tref.source == null) continue;

                int texW = tref.source.width, texH = tref.source.height;
                if (texW <= 0 || texH <= 0) continue;

                var copy = ctx.cache.GetCopy(tref.source, tref.sRGB);
                var raw = ctx.cache.GetRawPixels(tref.source, tref.sRGB);
                if (copy == null) continue;

                foreach (var island in group.islands)
                {
                    // 岛在贴图中的像素区域 / island pixel region in this texture
                    int x0 = Mathf.Clamp((int)Mathf.Floor(island.uvMin.x * texW), 0, texW - 1);
                    int y0 = Mathf.Clamp((int)Mathf.Floor(island.uvMin.y * texH), 0, texH - 1);
                    int x1 = Mathf.Clamp((int)Mathf.Ceil(island.uvMax.x * texW), x0 + 1, texW);
                    int y1 = Mathf.Clamp((int)Mathf.Ceil(island.uvMax.y * texH), y0 + 1, texH);
                    int w = x1 - x0;
                    int h = y1 - y0;

                    // 原始区域（线性空间 float RGBA）/ original region (linear float RGBA)
                    float[] origLin = new float[w * h * 4];
                    for (int y = 0; y < h; y++)
                    {
                        for (int x = 0; x < w; x++)
                        {
                            int src = (y0 + y) * texW + (x0 + x);
                            int dst = (y * w + x) * 4;
                            origLin[dst] = raw[src].r / 255f;
                            origLin[dst + 1] = raw[src].g / 255f;
                            origLin[dst + 2] = raw[src].b / 255f;
                            origLin[dst + 3] = raw[src].a / 255f;
                        }
                    }
                    if (tref.sRGB && tref.role != TextureRole.Normal)
                    {
                        ColorMetrics.SrgbToLinearInPlace(origLin);
                    }

                    // 纯色检测 / pure color detection
                    bool pureColor = IsPureColor(origLin, w, h);

                    var target = new TexTarget
                    {
                        w = w,
                        h = h,
                        nearLossless = nearLossless,
                        pureColor = pureColor,
                    };

                    if (!nearLossless && !pureColor)
                    {
                        target = ComputeTarget(tref, island, origLin, w, h, ctx, texW, texH);
                    }

                    island.texTargets[tref.source.GetInstanceID()] = target;
                }
            }

            // 组级木桶效应: 取最大尺寸（≤组内最大原尺寸） / group-level bucket: max size
            foreach (var island in group.islands)
            {
                int fw = 0, fh = 0;
                foreach (var kv in island.texTargets)
                {
                    fw = Mathf.Max(fw, kv.Value.w);
                    fh = Mathf.Max(fh, kv.Value.h);
                }
                fw = Mathf.Clamp(fw, 1, Mathf.Max(1, group.maxOriginalShortSide));
                fh = Mathf.Clamp(fh, 1, Mathf.Max(1, group.maxOriginalShortSide));
                island.finalW = fw;
                island.finalH = fh;
            }
        }

        /// <summary>
        /// 整图缩放（图集关闭/白名单组兜底路径）：返回目标尺寸；失败(近无损/无法评估)返回原尺寸。/
        /// Whole-texture scaling (atlasing-off / whitelisted-group fallback): returns target size.
        /// </summary>
        public static (int w, int h) ScaleWholeTexture(TextureRef tref, ScalerContext ctx)
        {
            int texW = tref.source.width, texH = tref.source.height;
            if (texW <= 0 || texH <= 0) return (texW, texH);

            var raw = ctx.cache.GetRawPixels(tref.source, tref.sRGB);
            var origLin = new float[texW * texH * 4];
            for (int i = 0, j = 0; i < texW * texH; i++, j += 4)
            {
                origLin[j] = raw[i].r / 255f;
                origLin[j + 1] = raw[i].g / 255f;
                origLin[j + 2] = raw[i].b / 255f;
                origLin[j + 3] = raw[i].a / 255f;
            }
            if (tref.sRGB && tref.role != TextureRole.Normal)
            {
                ColorMetrics.SrgbToLinearInPlace(origLin);
            }

            var target = ComputeTarget(tref, null, origLin, texW, texH, ctx, texW, texH);
            return (target.w, target.h);
        }

        /// <summary>
        /// 计算单个纹理对单个岛的目标尺寸 /
        /// Compute the target size for one texture on one island.
        /// </summary>
        private static TexTarget ComputeTarget(TextureRef tref, Island island, float[] origLin, int w, int h,
            ScalerContext ctx, int texW, int texH)
        {
            var p = ctx.metricParams;

            bool multiScale = Mathf.Min(w, h) >= 176;   // <176px → 单尺度 SSIM / single-scale
            bool useSsimDeltaE = Mathf.Min(w, h) >= 11; // <11px → 忽略质量参数 / ignore quality params

            // 候选评估: 生成 (cw×ch) 候选 → 上采样回 (w×h) → 指标 / candidate → upsample → metrics
            bool Evaluate(int cw, int ch, out MetricReport rep)
            {
                rep = new MetricReport { allPass = true };
                cw = Mathf.Max(1, cw);
                ch = Mathf.Max(1, ch);

                // GPU 重采样（线性空间；失败回退 CPU）/ GPU resample (linear; CPU fallback)
                float[] candLin = ResampleRegion(tref, origLin, w, h, cw, ch);

                rep = EvaluateAll(tref, island, origLin, candLin, w, h, cw, ch, ctx, multiScale, useSsimDeltaE);
                return rep.allPass;
            }

            // 1) 均匀二分搜索最大达标比例 / uniform binary search for max passing scale
            float lo = 0f, hi = 1f;
            float best = 1f;
            for (int iter = 0; iter < 12; iter++)
            {
                float mid = (lo + hi) * 0.5f;
                int cw = Mathf.Max(1, (int)Math.Round(w * mid));
                int ch = Mathf.Max(1, (int)Math.Round(h * mid));
                if (Evaluate(cw, ch, out _)) { best = mid; lo = mid; }
                else hi = mid;
            }

            // 2) 密度钳制（面积级；整图模式跳过密度）/ density clamp (area level; skipped for whole textures)
            float area = w * h * best * best;
            float minArea = island != null ? island.densityLo * island.densityLo : 0f;
            float maxArea = island != null ? island.densityHi * island.densityHi : float.MaxValue;
            float clampedArea = Mathf.Clamp(area, minArea, maxArea);
            if (Mathf.Abs(clampedArea - area) > 0.5f && clampedArea > 0)
            {
                best = Mathf.Sqrt(clampedArea / (w * h));
            }

            // 3) 各向异性双轴独立细化（先均匀达标后）/ anisotropic per-axis refinement
            float sx = best, sy = best;
            for (int axis = 0; axis < 2; axis++)
            {
                float aloEff = 0f;
                float abest = axis == 0 ? sx : sy;
                float ahiEff = 1f;
                for (int iter = 0; iter < 12; iter++)
                {
                    float amid = (aloEff + ahiEff) * 0.5f;
                    int cw = axis == 0 ? Mathf.Max(1, (int)Math.Round(w * amid)) : Mathf.Max(1, (int)Math.Round(w * sx));
                    int ch = axis == 0 ? Mathf.Max(1, (int)Math.Round(h * sy)) : Mathf.Max(1, (int)Math.Round(h * amid));
                    bool areaOk = true;
                    if (island != null)
                    {
                        areaOk = (long)cw * ch >= minArea - 0.5f && (long)cw * ch <= maxArea + 0.5f;
                    }
                    if (Evaluate(cw, ch, out _) && areaOk) { abest = amid; aloEff = amid; }
                    else ahiEff = amid;
                }
                if (axis == 0) sx = abest; else sy = abest;
            }

            int fw = Mathf.Clamp((int)Math.Round(w * sx), 1, w);
            int fh = Mathf.Clamp((int)Math.Round(h * sy), 1, h);

            return new TexTarget { w = fw, h = fh, nearLossless = false, pureColor = false };
        }

        /// <summary>
        /// 区域重采样（GPU 线性空间，失败回退 CPU）/
        /// Region resample (GPU linear space; CPU fallback on failure).
        /// </summary>
        private static float[] ResampleRegion(TextureRef tref, float[] origLin, int w, int h, int cw, int ch)
        {
            // 生成中间纹理供 GPU 采样（数据按 sRGB 编码存储，标记 sRGB；
            // GPU 采样时自动线性化 → 在线性空间过滤，符合"线性空间重采样"）/
            // build a temporary texture for GPU sampling (data stored sRGB-encoded, marked sRGB;
            // GPU linearizes on sample → filtering in linear space, per spec)
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false, false); // linear:false → sRGB 标记
            var colors = new Color32[w * h];
            for (int i = 0; i < colors.Length; i++)
            {
                int j = i * 4;
                colors[i] = new Color32(
                    (byte)Mathf.Clamp(ColorMetrics.LinearToSrgb(origLin[j]) * 255f, 0, 255),
                    (byte)Mathf.Clamp(ColorMetrics.LinearToSrgb(origLin[j + 1]) * 255f, 0, 255),
                    (byte)Mathf.Clamp(ColorMetrics.LinearToSrgb(origLin[j + 2]) * 255f, 0, 255),
                    (byte)Mathf.Clamp(origLin[j + 3] * 255f, 0, 255));
            }
            tex.SetPixels32(colors);
            tex.Apply(false, false);
            tex.hideFlags = HideFlags.HideAndDontSave;

            try
            {
                // GPU: 候选尺寸（线性输出）；若失败 → CPU 兜底 / GPU downscale; CPU fallback
                var down = GPUResampler.ResampleLinear(tex, cw, ch, srgb: true);
                float[] candDown = new float[cw * ch * 4];
                if (down != null)
                {
                    var px = down.GetPixels32();
                    for (int i = 0; i < px.Length; i++)
                    {
                        int j = i * 4;
                        candDown[j] = px[i].r / 255f;
                        candDown[j + 1] = px[i].g / 255f;
                        candDown[j + 2] = px[i].b / 255f;
                        candDown[j + 3] = px[i].a / 255f;
                    }
                    UnityEngine.Object.DestroyImmediate(down);
                }
                else
                {
                    CPUResample(origLin, w, h, candDown, cw, ch);
                }

                // 上采样回原尺寸 / upsample back to original size
                float[] candUp = new float[w * h * 4];
                var up = GPUResampler.ResampleLinear(down, w, h, srgb: true);
                if (up != null)
                {
                    var px = up.GetPixels32();
                    for (int i = 0; i < px.Length; i++)
                    {
                        int j = i * 4;
                        candUp[j] = px[i].r / 255f;
                        candUp[j + 1] = px[i].g / 255f;
                        candUp[j + 2] = px[i].b / 255f;
                        candUp[j + 3] = px[i].a / 255f;
                    }
                    UnityEngine.Object.DestroyImmediate(up);
                }
                else
                {
                    CPUResample(candDown, cw, ch, candUp, w, h);
                }

                return candUp;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(tex);
            }
        }

        private static void CPUResample(float[] src, int sw, int sh, float[] dst, int dw, int dh)
        {
            GPUResampler.ResampleLinearCPU(src, sw, sh, dst, dw, dh);
        }

        /// <summary>
        /// 全部指标评估（含多材质最严苛 alpha 评估）/
        /// Evaluate all metrics (incl. strictest alpha across referencing materials).
        /// </summary>
        private static MetricReport EvaluateAll(TextureRef tref, Island island, float[] orig, float[] cand,
            int w, int h, int cw, int ch, ScalerContext ctx, bool multiScale, bool useSsimDeltaE)
        {
            var p = ctx.metricParams;
            var rep = new MetricReport { allPass = true };
            int n = w * h;

            bool hasAlpha = ctx.cache.UsesAlpha(tref.source, tref.sRGB);

            switch (tref.role)
            {
                case TextureRole.Normal:
                {
                    var r = QualityMetrics.EvaluateNormal(orig, cand, n, out rep.normalAngleP95);
                    rep.allPass = rep.normalAngleP95 <= p.maxNormalAngle;
                    break;
                }
                case TextureRole.Mask:
                {
                    // 蒙版按灰度评估（仅被使用通道、逐通道取最差）/
                    // masks evaluated as grayscale (used channels only, worst channel)
                    rep.grayRmse = QualityMetrics.EvaluateGray(orig, cand, n, out _);
                    rep.allPass = rep.grayRmse <= p.maxGrayRmse;
                    break;
                }
                case TextureRole.Emission:
                case TextureRole.Other:
                case TextureRole.MainColor:
                default: // color roles (incl. emission/other)
                {
                    EvaluateColorMetrics(tref, island, orig, cand, w, h, ctx, multiScale, useSsimDeltaE, hasAlpha, ref rep);
                    break;
                }
            }

            // 第三方质量指标扩展（全部必须达标）/ third-party quality metric extensions (all must pass)
            foreach (var m in api.ATOPublicAPI.QualityMetrics)
            {
                if (!m.Evaluate(tref, orig, cand, w, h))
                {
                    rep.allPass = false;
                    break;
                }
            }

            return rep;
        }

        /// <summary>
        /// 颜色指标评估（含 alpha 预乘与多材质最严苛 alpha）/
        /// Color metric evaluation (with premultiplied alpha and strictest alpha across materials).
        /// </summary>
        private static void EvaluateColorMetrics(TextureRef tref, Island island, float[] orig, float[] cand,
            int w, int h, ScalerContext ctx, bool multiScale, bool useSsimDeltaE, bool hasAlpha, ref MetricReport rep)
        {
            var p = ctx.metricParams;
            int n = w * h;

            // 预乘 alpha（透明下采样语义）/ premultiply alpha
            float[] origPm = orig, candPm = cand;
            if (hasAlpha)
            {
                origPm = (float[])orig.Clone();
                candPm = (float[])cand.Clone();
                ColorMetrics.PremultiplyInPlace(origPm);
                ColorMetrics.PremultiplyInPlace(candPm);
            }

            var colorRep = QualityMetrics.EvaluateColor(origPm, candPm, w, h, p, useSsimDeltaE, multiScale);
            rep.ssim = colorRep.ssim;
            rep.deltaE = colorRep.deltaE;
            rep.allPass = colorRep.allPass;

            // alpha 评估: 每个引用材质逐一评估取最严苛 / strictest alpha across referencing materials
            rep.alphaScore = float.NaN;
            if (hasAlpha && useSsimDeltaE)
            {
                float worst = float.MinValue;
                bool alphaPass = true;
                foreach (var slot in tref.referencingSlots)
                {
                    if (!AlphaEval(orig, cand, n, slot.alphaMode, slot.cutoff, p, out float score))
                    {
                        alphaPass = false;
                    }
                    worst = Mathf.Max(worst, score);
                    foreach (var extra in slot.extraAlphaModes)
                    {
                        if (!AlphaEval(orig, cand, n, extra.mode, extra.cutoff, p, out float s2))
                        {
                            alphaPass = false;
                        }
                        worst = Mathf.Max(worst, s2);
                    }
                    // 动画中的 _Cutoff 范围 / animated cutoff range
                    if (ctx.anim.slotAnims.TryGetValue(slot.renderer, out var smap) &&
                        smap.TryGetValue(slot.slotIndex, out var sinfo) &&
                        sinfo.cutoffMin <= sinfo.cutoffMax)
                    {
                        if (slot.alphaMode == AlphaMode.Cutout)
                        {
                            if (!AlphaEval(orig, cand, n, AlphaMode.Cutout, sinfo.cutoffMin, p, out float s3)) alphaPass = false;
                            if (!AlphaEval(orig, cand, n, AlphaMode.Cutout, sinfo.cutoffMax, p, out float s4)) alphaPass = false;
                            worst = Mathf.Max(worst, Mathf.Max(s3, s4));
                        }
                    }
                }
                rep.alphaScore = worst;
                rep.allPass = rep.allPass && alphaPass;
            }
        }

        private static bool AlphaEval(float[] orig, float[] cand, int n, AlphaMode mode, float cutoff,
            MetricParams p, out float score)
        {
            var oa = new float[n];
            var ca = new float[n];
            for (int i = 0; i < n; i++)
            {
                oa[i] = orig[i * 4 + 3];
                ca[i] = cand[i * 4 + 3];
            }
            QualityMetrics.EvaluateAlpha(oa, ca, n, mode, cutoff, out score);
            if (mode == AlphaMode.Cutout) return score >= p.minCutoutIoU;
            return score <= p.maxBlendRmse;
        }

        private static bool IsPureColor(float[] rgba, int w, int h)
        {
            if (rgba.Length < 4) return true;
            float r = rgba[0], g = rgba[1], b = rgba[2], a = rgba[3];
            for (int i = 4; i < rgba.Length; i += 4)
            {
                if (Mathf.Abs(rgba[i] - r) > RegionEps || Mathf.Abs(rgba[i + 1] - g) > RegionEps ||
                    Mathf.Abs(rgba[i + 2] - b) > RegionEps || Mathf.Abs(rgba[i + 3] - a) > RegionEps)
                {
                    return false;
                }
            }
            return true;
        }
    }
}
