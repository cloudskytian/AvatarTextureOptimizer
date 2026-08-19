using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NetFosa.AvatarTextureOptimizer.Editor.Analysis;
using NetFosa.AvatarTextureOptimizer.Editor.Logging;
using NetFosa.AvatarTextureOptimizer.Editor.Utils;
using UnityEngine;
using NetFosa.AvatarTextureOptimizer;
using NetFosa.AvatarTextureOptimizer.Editor.UV;

namespace NetFosa.AvatarTextureOptimizer.Editor.Quality
{
    /// <summary>一次质量评估的结果。</summary>
    public struct QualityResult
    {
        public bool pass;
        public float msSsim;
        public float ssim;
        public float de2000;
        public float alphaIoU;
        public float alphaRmse;
        public float normalAngleP95;
        public float grayRmse;
        public float worstRatio; // 最差指标与阈值的比值（>1 失败）
    }

    /// <summary>
    /// 质量评估器：对"某贴图的某岛区域"在候选缩放 (su,sv) 下评估全部适用指标。
    /// 流程：区域线性化（≤1024 上限，防内存爆炸）→ 预乘 alpha 下采样 → 双线性上采样回原尺寸 → 逐指标比较。
    /// 指标：MS-SSIM（短边&lt;176px 回退单尺度 SSIM，&lt;11px 忽略）+ ΔE2000 + alpha（Cutout IoU / Blend RMSE，
    /// 逐引用材质逐 cutoff 取最严）+ 法线角度误差 p95 + 灰度逐通道 RMSE 取最差。
    /// </summary>
    public sealed class QualityEvaluator : IDisposable
    {
        /// <summary>比较分辨率上限（长边），超出则先双线性降采样，控制内存。</summary>
        public const int MaxCompareSize = 1024;

        private readonly TextureCache _cache;
        private readonly bool _useGpu;
        private readonly ATOLogger _logger;

        public QualityEvaluator(TextureCache cache, bool useGpu, ATOLogger logger)
        {
            _cache = cache;
            _useGpu = useGpu;
            _logger = logger;
        }

        // ---------- 区域缓存（每个 (texture, island) 一次） ----------
        private readonly Dictionary<(Texture, int), RegionData> _regionCache = new Dictionary<(Texture, int), RegionData>();

        public struct RegionData
        {
            public float[] linear; // 线性 RGBA 交错，已缩到 <= MaxCompareSize
            public int w;
            public int h;
            public bool hasAlpha;
        }

        /// <summary>取（或生成）岛区域在线性空间的缓存数据。</summary>
        public RegionData GetRegion(TextureInfo info, UvIsland island, int texW, int texH)
        {
            var key = (info.texture, island.id);
            if (_regionCache.TryGetValue(key, out var cached)) return cached;

            var bounds = island.uvBounds;
            int x = Mathf.Clamp(Mathf.RoundToInt(bounds.x * texW), 0, texW - 1);
            int y = Mathf.Clamp(Mathf.RoundToInt(bounds.y * texH), 0, texH - 1);
            int w = Mathf.Clamp(Mathf.RoundToInt(bounds.width * texW), 1, texW - x);
            int h = Mathf.Clamp(Mathf.RoundToInt(bounds.height * texH), 1, texH - y);

            var px = _cache.GetPixels(info.texture, out _, out _);
            bool srgb = info.colorSpace == ATOColorSpace.SRGB;

            // 先提取原区域（全分辨率线性，临时数组），若超上限则先线性化再降采样以省内存
            int maxSide = Mathf.Max(w, h);
            float downFactor = maxSide > MaxCompareSize ? (float)MaxCompareSize / maxSide : 1f;
            int rw = Mathf.Max(1, Mathf.RoundToInt(w * downFactor));
            int rh = Mathf.Max(1, Mathf.RoundToInt(h * downFactor));

            float[] linear;
            if (downFactor >= 1f)
            {
                linear = ImageOps.ExtractRegionLinear(px, texW, texH, x, y, w, h, srgb);
            }
            else
            {
                // 一次性：直接提取区域并按比例降采样（双线性，sRGB→线性）
                linear = ExtractAndDownscale(px, texW, texH, x, y, w, h, rw, rh, srgb);
            }

            var data = new RegionData { linear = linear, w = rw, h = rh, hasAlpha = info.hasAlpha };
            _regionCache[key] = data;
            return data;
        }

        private static float[] ExtractAndDownscale(Color32[] px, int texW, int texH, int x, int y, int w, int h,
            int dstW, int dstH, bool srgb)
        {
            var dst = new float[dstW * dstH * 4];
            Parallel.For(0, dstH, py =>
            {
                float syf = (py + 0.5f) * h / dstH - 0.5f;
                int sy0 = Mathf.Clamp(Mathf.FloorToInt(syf), 0, h - 1);
                int sy1 = Mathf.Min(sy0 + 1, h - 1);
                float fy = syf - sy0;
                for (int px_ = 0; px_ < dstW; px_++)
                {
                    float sxf = (px_ + 0.5f) * w / dstW - 0.5f;
                    int sx0 = Mathf.Clamp(Mathf.FloorToInt(sxf), 0, w - 1);
                    int sx1 = Mathf.Min(sx0 + 1, w - 1);
                    float fx = sxf - sx0;

                    float r = 0, g = 0, b = 0, a = 0;
                    SamplePx(px, texW, x + sx0, y + sy0, srgb, ref r, ref g, ref b, ref a);
                    float r1 = 0, g1 = 0, b1 = 0, a1 = 0;
                    SamplePx(px, texW, x + sx1, y + sy0, srgb, ref r1, ref g1, ref b1, ref a1);
                    float r2 = 0, g2 = 0, b2 = 0, a2 = 0;
                    SamplePx(px, texW, x + sx0, y + sy1, srgb, ref r2, ref g2, ref b2, ref a2);
                    float r3 = 0, g3 = 0, b3 = 0, a3 = 0;
                    SamplePx(px, texW, x + sx1, y + sy1, srgb, ref r3, ref g3, ref b3, ref a3);

                    float w00 = (1 - fx) * (1 - fy), w10 = fx * (1 - fy), w01 = (1 - fx) * fy, w11 = fx * fy;
                    int o = (py * dstW + px_) * 4;
                    dst[o] = r * w00 + r1 * w10 + r2 * w01 + r3 * w11;
                    dst[o + 1] = g * w00 + g1 * w10 + g2 * w01 + g3 * w11;
                    dst[o + 2] = b * w00 + b1 * w10 + b2 * w01 + b3 * w11;
                    dst[o + 3] = a * w00 + a1 * w10 + a2 * w01 + a3 * w11;
                }
            });
            return dst;
        }

        private static void SamplePx(Color32[] px, int texW, int sx, int sy, bool srgb,
            ref float r, ref float g, ref float b, ref float a)
        {
            sx = Mathf.Clamp(sx, 0, texW - 1);
            sy = Mathf.Clamp(sy, 0, px.Length / texW - 1);
            var c = px[sy * texW + sx];
            r = c.r / 255f; g = c.g / 255f; b = c.b / 255f; a = c.a / 255f;
            if (srgb)
            {
                r = ImageOps.SrgbToLinear(r);
                g = ImageOps.SrgbToLinear(g);
                b = ImageOps.SrgbToLinear(b);
            }
        }

        /// <summary>
        /// 评估某贴图在候选缩放 (scaleU, scaleV) 下是否全部指标达标。
        /// </summary>
        public QualityResult Evaluate(TextureInfo info, UvGroupTexture gt, UvIsland island, int texW, int texH,
            float scaleU, float scaleV, QualityThresholds thresholds)
        {
            var result = new QualityResult { pass = false, worstRatio = float.MaxValue };
            if (thresholds.IsNearLossless)
            {
                result.pass = true;
                return result;
            }

            var region = GetRegion(info, island, texW, texH);
            int rw = region.w, rh = region.h;
            if (rw <= 0 || rh <= 0)
            {
                result.pass = true; // 空区域视为通过
                return result;
            }

            int cropW = Math.Max(1, Mathf.RoundToInt(rw * scaleU));
            int cropH = Math.Max(1, Mathf.RoundToInt(rh * scaleV));
            cropW = Math.Min(cropW, rw);
            cropH = Math.Min(cropH, rh);

            // 下采样（透明贴图预乘 alpha）
            var crop = ImageOps.DownscaleWithAlpha(region.linear, rw, rh, cropW, cropH, region.hasAlpha);
            // 上采样回原尺寸
            var up = ImageOps.ResampleBilinear(crop, cropW, cropH, rw, rh, false);

            float worst = 1f;
            bool allPass = true;
            bool anyMetric = false;

            int shortSide = Mathf.Min(rw, rh);

            foreach (var req in gt.requirements)
            {
                switch (req.kind)
                {
                    case ATOUsageKind.Normal:
                    {
                        float angle = NormalMetrics.AngleErrorP95(region.linear, up);
                        result.normalAngleP95 = Mathf.Max(result.normalAngleP95, angle);
                        anyMetric = true;
                        float ratio = thresholds.normalAngleP95 <= 0 ? (angle > 0 ? float.MaxValue : 0f) : angle / thresholds.normalAngleP95;
                        if (ratio > worst) worst = ratio;
                        if (angle > thresholds.normalAngleP95) allPass = false;
                        break;
                    }
                    case ATOUsageKind.GrayMask:
                    {
                        float rmse = GrayMetrics.WorstChannelRmse(region.linear, up);
                        result.grayRmse = Mathf.Max(result.grayRmse, rmse);
                        anyMetric = true;
                        float ratio = thresholds.grayRmse <= 0 ? (rmse > 0 ? float.MaxValue : 0f) : rmse / thresholds.grayRmse;
                        if (ratio > worst) worst = ratio;
                        if (rmse > thresholds.grayRmse) allPass = false;
                        break;
                    }
                    default: // Main / MainAlpha / Other
                    {
                        if (shortSide >= 11)
                        {
                            float lumA = Luminance(region.linear, rw, rh);
                            float lumB = Luminance(up, rw, rh);
                            if (shortSide >= 176)
                            {
                                float ms = Ssim.CompareMs(lumA, lumB, rw, rh);
                                result.msSsim = Mathf.Max(result.msSsim, ms);
                                float ratio = thresholds.msSsim <= 0 ? 0f : (thresholds.msSsim - ms) / thresholds.msSsim + 1f;
                                if (ms < thresholds.msSsim) allPass = false;
                            }
                            else
                            {
                                float ss = Ssim.Compare(lumA, lumB, rw, rh);
                                result.ssim = Mathf.Max(result.ssim, ss);
                                if (ss < thresholds.ssim) allPass = false;
                            }
                        }

                        float de = Ciede2000.MeanDeltaE(region.linear, up);
                        result.de2000 = Mathf.Max(result.de2000, de);
                        anyMetric = true;
                        float deRatio = thresholds.deltaE2000 <= 0 ? (de > 0 ? float.MaxValue : 0f) : de / thresholds.deltaE2000;
                        if (deRatio > worst) worst = deRatio;
                        if (de > thresholds.deltaE2000) allPass = false;

                        // alpha
                        if (req.mode == RenderMode.Cutout)
                        {
                            float iou = AlphaMetrics.CutoutIoU(region.linear, up, req.cutoff);
                            result.alphaIoU = Mathf.Min(result.alphaIoU, iou);
                            anyMetric = true;
                            float ratio = thresholds.alphaCutoutIoU <= 0 ? (iou < 1 ? float.MaxValue : 0f) : (thresholds.alphaCutoutIoU - iou) / thresholds.alphaCutoutIoU + 1f;
                            if (ratio > worst) worst = ratio;
                            if (iou < thresholds.alphaCutoutIoU) allPass = false;
                        }
                        else if (req.mode == RenderMode.Blend)
                        {
                            float rmse = AlphaMetrics.BlendRmse(region.linear, up);
                            result.alphaRmse = Mathf.Max(result.alphaRmse, rmse);
                            anyMetric = true;
                            float ratio = thresholds.alphaBlendRmse <= 0 ? (rmse > 0 ? float.MaxValue : 0f) : rmse / thresholds.alphaBlendRmse;
                            if (ratio > worst) worst = ratio;
                            if (rmse > thresholds.alphaBlendRmse) allPass = false;
                        }
                        break;
                    }
                }
            }

            if (!anyMetric)
            {
                result.pass = true;
                return result;
            }
            result.pass = allPass;
            result.worstRatio = worst;
            return result;
        }

        private static float[] Luminance(float[] rgba, int w, int h)
        {
            var lum = new float[w * h];
            Parallel.For(0, w * h, i =>
            {
                lum[i] = ImageOps.Luminance(rgba[i * 4], rgba[i * 4 + 1], rgba[i * 4 + 2]);
            });
            return lum;
        }

        /// <summary>检测岛区域是否为纯色（采样若干像素）。</summary>
        public bool IsRegionUniform(TextureInfo info, UvIsland island, int texW, int texH)
        {
            var bounds = island.uvBounds;
            int x = Mathf.Clamp(Mathf.RoundToInt(bounds.x * texW), 0, texW - 1);
            int y = Mathf.Clamp(Mathf.RoundToInt(bounds.y * texH), 0, texH - 1);
            int w = Mathf.Clamp(Mathf.RoundToInt(bounds.width * texW), 1, texW - x);
            int h = Mathf.Clamp(Mathf.RoundToInt(bounds.height * texH), 1, texH - y);
            var px = _cache.GetPixels(info.texture, out _, out _);
            var first = px[y * texW + x];
            int stepX = Math.Max(1, w / 8);
            int stepY = Math.Max(1, h / 8);
            for (int yy = 0; yy < h; yy += stepY)
            {
                for (int xx = 0; xx < w; xx += stepX)
                {
                    var c = px[(y + yy) * texW + (x + xx)];
                    if (Math.Abs(c.r - first.r) > 2 || Math.Abs(c.g - first.g) > 2 ||
                        Math.Abs(c.b - first.b) > 2 || Math.Abs(c.a - first.a) > 2)
                        return false;
                }
            }
            return true;
        }

        public void Dispose()
        {
            _regionCache.Clear();
        }
    }
}
