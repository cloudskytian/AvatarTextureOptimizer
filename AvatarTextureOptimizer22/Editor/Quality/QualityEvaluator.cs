// AvatarTextureOptimizer
// File: Editor/Quality/QualityEvaluator.cs
//
// Evaluates whether a candidate scaling of a region passes ALL quality
// thresholds for ALL textures referencing it (strictest requirement wins).
//   - the scaled island's actual covered region is bilinearly up-sampled back
//     to the original size and compared against the original
//   - all color work is in LINEAR space; alpha is premultiplied before color
//     comparison (spec)
//   - MainColor:   MS-SSIM (GPU) + CIEDE2000 (+ alpha RMSE / cutout IoU per
//                  render mode)
//   - NormalMap:   decode -> resample -> re-normalize -> encode; angular error
//                  (p95) comparison
//   - Mask:        worst-channel linear RMSE on used channels only
//   - a texture referenced by multiple materials is evaluated for every usage
//     (render mode / cutoff / type), taking the strictest requirement
//
// 评估区域的一个候选缩放是否通过引用它的所有贴图的全部质量阈值（取最严苛
// 要求）。
//   - 缩小后的岛的实际覆盖区被双线性上采样回原尺寸并与原图比较
//   - 所有颜色工作在线性空间；颜色比较前 alpha 预乘（规格）
//   - 主色：MS-SSIM（GPU）+ CIEDE2000（按渲染模式追加 alpha RMSE /
//     Cutout IoU）
//   - 法线：解码 -> 重采样 -> 重归一化 -> 编码；角度误差（p95）对比
//   - 蒙版：仅被使用通道上的最差通道线性 RMSE
//   - 被多个材质引用的贴图按每个引用（渲染模式 / cutoff / 类型）评估，
//     取最严苛要求

using System.Collections.Generic;
using net.fosa.avatar_texture_optimizer.editor.model;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor.quality
{
    /// <summary>Result of one quality evaluation. / 一次质量评估的结果。</summary>
    public readonly struct QualityResult
    {
        public readonly bool Pass;
        public readonly float MsSsim;
        public readonly float DeltaE;
        public readonly float AlphaRmse;
        public readonly float CutoutIoU;
        public readonly float NormalAngleP95;
        public readonly float GrayRmse;

        public QualityResult(bool pass, float msSsim, float deltaE, float alphaRmse, float cutoutIoU,
            float normalAngleP95, float grayRmse)
        {
            Pass = pass;
            MsSsim = msSsim;
            DeltaE = deltaE;
            AlphaRmse = alphaRmse;
            CutoutIoU = cutoutIoU;
            NormalAngleP95 = normalAngleP95;
            GrayRmse = grayRmse;
        }

        public static QualityResult PassAll => new QualityResult(true, 1f, 0f, 0f, 1f, 0f, 0f);
    }

    public static class QualityEvaluator
    {
        /// <summary>
        /// Evaluate the region of `source` scaled by (fx, fy), up-sampled back
        /// to the original region size, against the original. All usages of the
        /// texture are considered (strictest wins).
        /// 评估 source 的区域按 (fx, fy) 缩放、上采样回原区域尺寸后与原图的
        /// 比较结果。考虑该贴图的全部引用（最严苛者胜出）。
        /// </summary>
        public static QualityResult Evaluate(Texture2D source, RectInt region,
            List<TextureUsage> usages, QualityThresholds thresholds,
            float fx, float fy)
        {
            if (source == null || region.width < 1 || region.height < 1)
                return QualityResult.PassAll;

            int w = region.width, h = region.height;
            int cw = Mathf.Max(1, Mathf.RoundToInt(w * fx));
            int ch = Mathf.Max(1, Mathf.RoundToInt(h * fy));

            var original = GPUImageOps.LinearizeRegion(source, region, w, h);
            var candidateSmall = GPUImageOps.CreateRT(cw, ch);
            var candidate = GPUImageOps.CreateRT(w, h);

            try
            {
                GPUImageOps.DownsampleBilinear(original, cw, ch, candidateSmall);
                GPUImageOps.UpsampleBilinear(candidateSmall, w, h, candidate);

                var flags = CollectUsageFlags(usages);

                // MS-SSIM runs directly on the GPU RTs (no readback).
                // MS-SSIM 直接在 GPU RT 上运行（无读回）。
                float ssim = flags.AnyMain ? MS_SSIM.Evaluate(original, candidate) : 1f;

                // Color metrics: premultiplied linear-space readbacks.
                // 颜色指标：预乘线性空间读回。
                float deltaE = 0f, alphaRmse = 0f, cutoutIoU = 1f, normalP95 = 0f, grayRmse = 0f;
                if (flags.AnyAlpha || flags.AnyMain || flags.AnyMask || flags.AnyNormal)
                {
                    Color[] a;
                    Color[] b;
                    if (flags.AnyAlpha || flags.AnyMain)
                    {
                        // Premultiply for color metrics when alpha matters.
                        // 需要 alpha 时对颜色指标预乘。
                        var premulA = GPUImageOps.CreateRT(w, h);
                        var premulB = GPUImageOps.CreateRT(w, h);
                        GPUImageOps.Premultiply(original, premulA);
                        GPUImageOps.Premultiply(candidate, premulB);
                        a = GPUImageOps.Readback(premulA);
                        b = GPUImageOps.Readback(premulB);
                        premulA.Release();
                        premulB.Release();
                    }
                    else
                    {
                        a = GPUImageOps.Readback(original);
                        b = GPUImageOps.Readback(candidate);
                    }

                    if (flags.AnyMain)
                    {
                        deltaE = CIEDE2000.ComputeMean(a, b);
                        if (flags.AnyAlpha) alphaRmse = Metrics.AlphaBlendRMSE(a, b);
                        if (flags.AnyCutout) cutoutIoU = Metrics.CutoutIoU(a, b, flags.MinCutoff);
                    }
                    if (flags.AnyMask) grayRmse = Metrics.GrayChannelRMSE(a, b);
                    if (flags.AnyNormal)
                    {
                        var decodedA = DecodeNormals(a);
                        var decodedB = DecodeNormals(b);
                        normalP95 = Metrics.NormalAngularError(decodedA, decodedB).P95;
                    }
                }

                bool pass = true;
                if (flags.AnyMain && !float.IsNaN(ssim) && ssim < thresholds.MinMsSsim) pass = false;
                if (flags.AnyMain && deltaE > thresholds.MaxDeltaE) pass = false;
                if (flags.AnyAlpha && alphaRmse > thresholds.MaxAlphaRmse) pass = false;
                if (flags.AnyCutout && cutoutIoU < thresholds.MinCutoutIoU) pass = false;
                if (flags.AnyNormal && normalP95 > thresholds.MaxNormalAngleDeg) pass = false;
                if (flags.AnyMask && grayRmse > thresholds.MaxGrayRmse) pass = false;

                return new QualityResult(pass, ssim, deltaE, alphaRmse, cutoutIoU, normalP95, grayRmse);
            }
            finally
            {
                original.Release();
                candidateSmall.Release();
                candidate.Release();
            }
        }

        private readonly struct UsageFlags
        {
            public readonly bool AnyMain, AnyNormal, AnyMask, AnyAlpha, AnyCutout;
            public readonly float MinCutoff;
            public UsageFlags(bool anyMain, bool anyNormal, bool anyMask, bool anyAlpha, bool anyCutout, float minCutoff)
            {
                AnyMain = anyMain; AnyNormal = anyNormal; AnyMask = anyMask;
                AnyAlpha = anyAlpha; AnyCutout = anyCutout; MinCutoff = minCutoff;
            }
        }

        private static UsageFlags CollectUsageFlags(List<TextureUsage> usages)
        {
            bool anyNormal = false, anyMask = false, anyMain = false, anyAlpha = false, anyCutout = false;
            float minCutoff = float.MaxValue;
            foreach (var u in usages)
            {
                switch (u.Type)
                {
                    case TextureUsageType.NormalMap: anyNormal = true; break;
                    case TextureUsageType.Mask: anyMask = true; break;
                    default: anyMain = true; break;
                }
                var mode = u.RenderMode ?? "";
                if (mode == "Cutout") { anyCutout = true; minCutoff = Mathf.Min(minCutoff, u.Cutoff); }
                else if (mode == "Transparent" || mode == "Fade") anyAlpha = true;
            }
            return new UsageFlags(anyMain, anyNormal, anyMask, anyAlpha, anyCutout, minCutoff);
        }

        /// <summary>Decode [0,1] encoded normal colors to [-1,1] vectors (CPU path; the GPU kernels handle the RT path). / 将 [0,1] 编码的法线颜色解码为 [-1,1] 向量（CPU 路径；RT 路径由 GPU 内核处理）。</summary>
        private static Color[] DecodeNormals(Color[] src)
        {
            var dst = new Color[src.Length];
            for (int i = 0; i < src.Length; i++)
            {
                dst[i].r = src[i].r * 2f - 1f;
                dst[i].g = src[i].g * 2f - 1f;
                dst[i].b = src[i].b * 2f - 1f;
                dst[i].a = src[i].a;
            }
            return dst;
        }
    }
}
