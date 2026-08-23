using System;
using System.Collections.Generic;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Resamples island regions and decides whether a candidate downscale passes all metrics.
    /// Color: GPU bilinear in linear space with premultiplied alpha (transparent); normals: CPU
    /// decode→filter→renormalize→encode; grayscale/mask: raw linear filtering.
    /// / 重采样岛区域并判定候选缩放是否通过全部指标。颜色贴图走 GPU 线性+预乘；法线走 CPU 解码重采样；
    /// 灰度/蒙版按原始线性值过滤。
    /// </summary>
    internal class QualityEvaluator : IDisposable
    {
        private Material _mat;
        private bool _disposed;

        internal QualityEvaluator()
        {
            var shader = Shader.Find("Hidden/ATO/Gfx");
            if (shader == null)
            {
                ATOLog.Error("shader Hidden/ATO/Gfx not found — quality scaling falls back to no-op / 着色器缺失，质量缩放退化为不缩放");
            }
            _mat = new Material(shader);
        }

        internal bool IsValid => _mat != null && _mat.shader != null;

        /// <summary>Shader missing ⇒ resampling becomes identity (search degenerates safely). / 着色器缺失时重采样退化为恒等，搜索安全退化。</summary>
        internal bool CanResample => IsValid;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_mat != null) UnityEngine.Object.DestroyImmediate(_mat);
        }

        // ------------------------------------------------------------------ resampling
        /// <summary>
        /// Downscale a region. Returns bytes appropriate for evaluation: color → linear premultiplied;
        /// gray/mask → raw filtered bytes; normal → raw packed bytes (CPU path).
        /// / 缩小区域并返回评估用字节（颜色=线性预乘；灰度/蒙版=原始线性；法线=CPU原始打包）。
        /// </summary>
        internal Color32[] Downsample(Color32[] srcRegion, int w, int h, int dw, int dh,
            TexCategory cat, bool srcSrgb)
        {
            if (dw == w && dh == h) return (Color32[])srcRegion.Clone();
            if (!CanResample && cat != TexCategory.Normal)
            {
                ATOLog.Warning("resample shader unavailable; skipping downscale this session / 重采样不可用，本会话跳过缩小");
                return (Color32[])srcRegion.Clone();
            }

            if (cat == TexCategory.Normal)
                return NormalResampler.Downsample(srcRegion, w, h, dw, dh);

            using var temp = new Gfx.TempTextureScope(srcRegion, w, h);
            bool color = cat == TexCategory.Color;
            return Gfx.ResampleRegion(temp.Texture, new RectInt(0, 0, w, h), dw, dh,
                linearize: color && srcSrgb, premultiply: color && HasAlphaUsage(cat), _mat);
        }

        private static bool HasAlphaUsage(TexCategory cat) => cat == TexCategory.Color; // only color textures evaluate alpha / 仅颜色贴图评估alpha

        /// <summary>Upscale a scaled buffer back to original size for comparison. / 放大回原尺寸用于比较。</summary>
        internal Color32[] Upsample(Color32[] scaled, int sw, int sh, int w, int h,
            TexCategory cat, bool hasAlphaUsage)
        {
            if (sw == w && sh == h) return (Color32[])scaled.Clone();

            if (cat == TexCategory.Normal)
                return NormalResampler.Upsample(scaled, sw, sh, w, h);

            return Gfx.UpsampleBuffer(scaled, sw, sh, w, h, unpremultiply: cat == TexCategory.Color && hasAlphaUsage, _mat);
        }

        /// <summary>
        /// Final atlas bytes for an instance: color → straight sRGB (encode premul back); others as
        /// produced. / 实例的图集字节：颜色=直通sRGB（预乘回编码），其他类别原样。
        /// </summary>
        internal Color32[] MakeAtlasBytes(Color32[] srcRegion, int w, int h, int dw, int dh,
            TexCategory cat, bool srcSrgb)
        {
            if (dw == w && dh == h) return (Color32[])srcRegion.Clone();

            if (cat == TexCategory.Normal)
                return NormalResampler.Downsample(srcRegion, w, h, dw, dh);

            bool premul = cat == TexCategory.Color;
            Color32[] prem;
            var tempTex = ToTemp(srcRegion, w, h);
            try
            {
                prem = Gfx.ResampleRegion(tempTex, new RectInt(0, 0, w, h), dw, dh,
                    linearize: srcSrgb, premultiply: premul, _mat);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(tempTex);
            }

            if (!premul) return prem;

            // encode back to straight sRGB for storage / 回编码为直通sRGB存储
            var result = new Color32[prem.Length];
            for (int i = 0; i < prem.Length; i++)
            {
                var c = prem[i];
                float a = c.a / 255f;
                if (a <= 0f)
                {
                    result[i] = new Color32(0, 0, 0, 0);
                    continue;
                }
                float inv = 1f / a;
                result[i] = new Color32(
                    Encode(EncodeChannel(c.r * inv)),
                    Encode(EncodeChannel(c.g * inv)),
                    Encode(EncodeChannel(c.b * inv)),
                    c.a);
            }
            return result;

            static float EncodeChannel(float lin) =>
                lin <= 0.0031308f ? lin * 12.92f : 1.055f * Mathf.Pow(Mathf.Clamp01(lin), 1f / 2.4f) - 0.055f;

            static byte Encode(float srgb01) => (byte)Mathf.Round(Mathf.Clamp01(srgb01) * 255f);
        }

        private static Texture2D ToTemp(Color32[] px, int w, int h) => Gfx.CreateTempTexture(w, h, px);

        // ------------------------------------------------------------------ evaluation
        /// <summary>
        /// Evaluate one texture's scaled candidate against the original region. `orig` holds raw
        /// source bytes; `test` the reconstruction at original size. Returns false with the failing
        /// metric name. / 评估单张贴图的候选缩放；返回是否通过与失败指标名。
        /// </summary>
        internal bool Evaluate(TexCategory cat, bool srcSrgb,
            Color32[] orig, Color32[] test, int w, int h,
            IReadOnlyCollection<(AlphaMode mode, float cutoff)> alphaCandidates,
            QualityParams p, bool textureHasAlpha, out string failedMetric)
        {
            failedMetric = null;

            switch (cat)
            {
                case TexCategory.Color:
                {
                    // MS-SSIM (skip &lt;11px short side) / MS-SSIM
                    float ssim = MetricJobs.Msssim(orig, test, w, h, xSrgb: srcSrgb, ySrgbBytes: false);
                    if (!float.IsNaN(ssim) && ssim < p.msSsim) { failedMetric = $"ms-ssim {ssim:F4}<{p.msSsim}"; return false; }

                    // ΔE2000 mean / 平均色差
                    float de = MetricJobs.DeltaE2000Mean(orig, srcSrgb, test, yHasAlpha: textureHasAlpha);
                    if (de > p.deltaE2000Mean) { failedMetric = $"dE2000 {de:F2}>{p.deltaE2000Mean}"; return false; }

                    // alpha: worst over all referencing-material combos / alpha：逐组合取最差
                    foreach (var (mode, cutoff) in alphaCandidates)
                    {
                        if (mode == AlphaMode.Opaque) continue;
                        if (!textureHasAlpha) continue;
                        MetricJobs.AlphaMetrics(orig, test, cutoff, out var iou, out var rmse);
                        if (mode == AlphaMode.Cutout && iou < p.alphaCutoutIoU)
                        {
                            failedMetric = $"alphaIoU {iou:F4}<{p.alphaCutoutIoU}@{cutoff:F2}"; return false;
                        }
                        if (mode == AlphaMode.Blend && rmse > p.alphaBlendRmse)
                        {
                            failedMetric = $"alphaRmse {rmse:F4}>{p.alphaBlendRmse}"; return false;
                        }
                    }
                    return true;
                }

                case TexCategory.Normal:
                {
                    MetricJobs.NormalAngleStats(orig, test, out var mean, out var p95);
                    if (mean > p.normalAngleMeanDeg) { failedMetric = $"normalMean {mean:F2}>{p.normalAngleMeanDeg}"; return false; }
                    if (p95 > p.normalAngleP95Deg) { failedMetric = $"normalP95 {p95:F2}>{p.normalAngleP95Deg}"; return false; }
                    return true;
                }

                case TexCategory.Mask:
                case TexCategory.Grayscale:
                {
                    // worst used channel; unknown usage ⇒ all channels (strictest) / 使用通道未知时取全部通道（最严）
                    MetricJobs.GrayChannelRmse(orig, test, _rmseBuf);
                    float worst = Mathf.Max(Mathf.Max(_rmseBuf[0], _rmseBuf[1]), Mathf.Max(_rmseBuf[2], _rmseBuf[3]));
                    if (worst > p.grayRmse) { failedMetric = $"grayRmse {worst:F4}>{p.grayRmse}"; return false; }
                    return true;
                }

                default:
                    return true;
            }
        }

        private readonly float[] _rmseBuf = new float[4];

        /// <summary>Detect whether any alpha candidate actually uses the alpha channel. / 是否存在真正使用alpha的组合。</summary>
        internal static bool UsesAlpha(IReadOnlyCollection<(AlphaMode mode, float cutoff)> candidates)
        {
            foreach (var (m, _) in candidates)
                if (m != AlphaMode.Opaque) return true;
            return false;
        }
    }
}
