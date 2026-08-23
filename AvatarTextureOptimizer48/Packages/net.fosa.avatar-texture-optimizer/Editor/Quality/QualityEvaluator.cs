// Shared quality evaluation used by island scaling and whole-texture scaling.
// / 岛缩放与整图缩放共用的质量评估。

using net.fosa.avatar_texture_optimizer.editor.analysis;

namespace net.fosa.avatar_texture_optimizer.editor.quality
{
    /// <summary>
    /// Evaluates a texture at a target size against the quality bar. / 在目标尺寸下评估贴图是否达到质量线。
    /// </summary>
    public static class QualityEvaluator
    {
        /// <summary>
        /// True if the resampled image (target size tw x th) passes all applicable metrics.
        /// / 重采样图像（目标尺寸 tw x th）是否通过全部适用指标。
        /// </summary>
        public static bool Passes(float[] refRgba, int rw, int rh, int tw, int th,
            TexRecord record, TextureRole role, QualityBar bar)
        {
            var small = TextureOps.ResizeBilinearPremultiplied(refRgba, rw, rh, tw, th);
            var test = TextureOps.ResizeBilinearPremultiplied(small, tw, th, rw, rh);

            int shortSide = rw < rh ? rw : rh;

            // SSIM family / SSIM 家族
            if (shortSide >= 11)
            {
                float ssim;
                var refRgb = TextureOps.RgbaToRgb(refRgba, rw, rh);
                var testRgb = TextureOps.RgbaToRgb(test, rw, rh);
                if (shortSide < 176)
                {
                    ssim = MetricMath.Ssim(refRgb, testRgb, rw, rh);
                }
                else
                {
                    ssim = MetricMath.MsSsim(refRgb, testRgb, rw, rh);
                }
                if (ssim < bar.Ssim) return false;
            }

            // CIEDE2000 / 色差
            {
                var refSrgb = TextureOps.LinearRgbToSrgb(TextureOps.RgbaToRgb(refRgba, rw, rh), rw, rh);
                var testSrgb = TextureOps.LinearRgbToSrgb(TextureOps.RgbaToRgb(test, rw, rh), rw, rh);
                float dE = MetricMath.DeltaE2000Images(refSrgb, testSrgb, rw, rh);
                if (dE > bar.DeltaE) return false;
            }

            // Role-specific metrics / 按用途的指标
            switch (role)
            {
                case TextureRole.Normal:
                {
                    var n1 = TextureOps.DecodeNormals(refRgba, rw, rh);
                    var n2 = TextureOps.DecodeNormals(test, rw, rh);
                    float angle = MetricMath.NormalAngleP95(n1, n2, rw * rh);
                    if (angle > bar.NormalAngle) return false;
                    break;
                }
                case TextureRole.Mask:
                {
                    float g = MetricMath.GrayRmsUsedChannels(refRgba, test, rw * rh);
                    if (g > bar.GrayRms) return false;
                    break;
                }
            }

            // Alpha metrics (strictest across usages) / alpha 指标（跨使用处取最严苛）
            bool needCutout = false, needBlend = false;
            float cutoff = 0.5f;
            if (record != null)
            {
                foreach (var b in record.Bindings)
                {
                    if (b.TransparentCutout) { needCutout = true; cutoff = cutoff < b.Cutoff ? cutoff : b.Cutoff; }
                    if (b.TransparentBlend) needBlend = true;
                }
            }
            if (needCutout || needBlend)
            {
                var ra = TextureOps.Alpha(refRgba, rw, rh);
                var ta = TextureOps.Alpha(test, rw, rh);
                if (needCutout)
                {
                    float iou = MetricMath.CutoutIoU(ra, ta, cutoff);
                    if (1f - iou > bar.Alpha) return false;
                }
                if (needBlend)
                {
                    float rmse = MetricMath.AlphaRmse(ra, ta);
                    if (rmse > bar.Alpha) return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Binary search the smallest whole-texture scale that passes. / 二分求通过质量的最小整图缩放。
        /// </summary>
        public static float FindWholeScale(float[] refRgba, int w, int h, TexRecord record, TextureRole role, QualityBar bar)
        {
            float lo = 0.01f, hi = 1f;
            for (int it = 0; it < 8; it++)
            {
                float mid = (lo + hi) * 0.5f;
                int tw = System.Math.Max(1, (int)System.Math.Round(w * mid));
                int th = System.Math.Max(1, (int)System.Math.Round(h * mid));
                if (Passes(refRgba, w, h, tw, th, record, role, bar)) hi = mid;
                else lo = mid;
            }
            return hi;
        }
    }
}
