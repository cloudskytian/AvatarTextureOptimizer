// AvatarTextureOptimizer - MetricsCpu
// EN: CPU implementations of the target quality metrics (MS-SSIM/SSIM, CIEDE2000, alpha IoU/RMSE, normal angle,
// grayscale RMSE). GPU path lives in MetricsGpu and self-tests against these.
// CN: 目标质量指标的 CPU 实现（MS-SSIM/SSIM、CIEDE2000、alpha IoU/RMSE、法线角度、灰度 RMSE）。
//     GPU 路径见 MetricsGpu，并会与这些实现做自检对比。
using System;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer
{
    /// <summary>EN: Floating RGBA image in linear space. / CN: 线性空间浮点 RGBA 图像。</summary>
    public sealed class LinearImage
    {
        public readonly int width, height;
        public readonly float[] rgba; // w*h*4

        public LinearImage(int w, int h)
        {
            width = w; height = h;
            rgba = new float[w * h * 4];
        }

        public float Luma(int i) =>
            0.2126f * rgba[i * 4] + 0.7152f * rgba[i * 4 + 1] + 0.0722f * rgba[i * 4 + 2];

        public float Alpha(int i) => rgba[i * 4 + 3];
    }

    /// <summary>
    /// EN: Bilinear resampler. Premultiplied alpha mode: RGB premultiplied before filtering, then unpremultiplied
    /// (spec: 透明贴图预乘 alpha 下采样).
    /// CN: 双线性重采样器。预乘 alpha 模式：滤波前预乘 RGB，滤波后反预乘（按需求）。
    /// </summary>
    public static class Resampler
    {
        public static LinearImage Bilinear(LinearImage src, int dstW, int dstH, bool premultiply)
        {
            var dst = new LinearImage(dstW, dstH);
            float sx = (float)src.width / dstW;
            float sy = (float)src.height / dstH;
            float[] s = src.rgba;
            float[] d = dst.rgba;

            for (int y = 0; y < dstH; y++)
            {
                float fy = (y + 0.5f) * sy - 0.5f;
                int y0 = Mathf.Clamp(Mathf.FloorToInt(fy), 0, src.height - 1);
                int y1 = Mathf.Clamp(y0 + 1, 0, src.height - 1);
                float ty = Mathf.Clamp(fy - y0, 0f, 1f);
                for (int x = 0; x < dstW; x++)
                {
                    float fx = (x + 0.5f) * sx - 0.5f;
                    int x0 = Mathf.Clamp(Mathf.FloorToInt(fx), 0, src.width - 1);
                    int x1 = Mathf.Clamp(x0 + 1, 0, src.width - 1);
                    float tx = Mathf.Clamp(fx - x0, 0f, 1f);

                    int di = (y * dstW + x) * 4;
                    for (int c = 0; c < 4; c++)
                    {
                        float v00 = s[(y0 * src.width + x0) * 4 + c];
                        float v01 = s[(y0 * src.width + x1) * 4 + c];
                        float v10 = s[(y1 * src.width + x0) * 4 + c];
                        float v11 = s[(y1 * src.width + x1) * 4 + c];
                        float top = v00 + (v01 - v00) * tx;
                        float bot = v10 + (v11 - v10) * tx;
                        float v = top + (bot - top) * ty;
                        // EN: Premultiplied filtering: RGB weighted by alpha before interpolation (spec).
                        // CN: 预乘过滤：插值前 RGB 按 alpha 加权（按需求）。
                        if (premultiply && c < 3)
                        {
                            float a00 = s[(y0 * src.width + x0) * 4 + 3];
                            float a01 = s[(y0 * src.width + x1) * 4 + 3];
                            float a10 = s[(y1 * src.width + x0) * 4 + 3];
                            float a11 = s[(y1 * src.width + x1) * 4 + 3];
                            float at = a00 + (a01 - a00) * tx;
                            float ab = a10 + (a11 - a10) * tx;
                            float a = at + (ab - at) * ty;
                            v *= a;
                        }
                        d[di + c] = v;
                    }
                }
            }
            return dst;
        }

        /// <summary>EN: Premultiplies an image in place. / CN: 原地预乘图像。</summary>
        public static void Premultiply(LinearImage img)
        {
            float[] a = img.rgba;
            for (int i = 0; i < a.Length; i += 4)
            {
                float al = a[i + 3];
                a[i] *= al; a[i + 1] *= al; a[i + 2] *= al;
            }
        }

        /// <summary>EN: Unpremultiplies in place (alpha==0 → RGB kept 0). / CN: 原地反预乘（alpha==0 时 RGB 保持 0）。</summary>
        public static void Unpremultiply(LinearImage img)
        {
            float[] a = img.rgba;
            for (int i = 0; i < a.Length; i += 4)
            {
                float al = a[i + 3];
                if (al > 1e-5f)
                {
                    a[i] /= al; a[i + 1] /= al; a[i + 2] /= al;
                }
            }
        }
    }

    /// <summary>
    /// EN: CPU quality metrics. All compare a reference island (original) with the round-tripped candidate,
    /// both at original resolution in linear space.
    ///
    /// 参考：Wang & Bovik 的结构相似度 (SSIM) 与多尺度 SSIM；Sharma et al. 的 CIEDE2000。
    /// </summary>
    public static class MetricsCpu
    {
        public const float MaxAnalysisResolution = 1024f; // CPU 回退路径的分析分辨率上限（GPU 路径全分辨率）

        // ------------------------------------------------------------- SSIM

        /// <summary>EN: Mean SSIM at a single scale with an 11x11 Gaussian window (σ=1.5). / CN: 单尺度平均 SSIM（11x11 高斯窗，σ=1.5）。</summary>
        public static float Ssim(LinearImage refImg, LinearImage candImg)
        {
            int w = Mathf.Min(refImg.width, candImg.width);
            int h = Mathf.Min(refImg.height, candImg.height);
            if (w < 4 || h < 4) return 1f;

            float[] r = new float[w * h], c = new float[w * h];
            for (int i = 0; i < w * h; i++)
            {
                r[i] = refImg.Luma(i);
                c[i] = candImg.Luma(i);
            }

            var gauss = BuildGaussian11();
            var muR = Convolve(r, w, h, gauss);
            var muC = Convolve(c, w, h, gauss);
            var muR2 = Mul(muR, muR);
            var muC2 = Mul(muC, muC);
            var muRC = Mul(muR, muC);

            var sR = new float[w * h]; var sC = new float[w * h]; var sRC = new float[w * h];
            for (int i = 0; i < w * h; i++)
            {
                sR[i] = r[i] * r[i];
                sC[i] = c[i] * c[i];
                sRC[i] = r[i] * c[i];
            }
            var sR2 = Convolve(sR, w, h, gauss);
            var sC2 = Convolve(sC, w, h, gauss);
            var sRC2 = Convolve(sRC, w, h, gauss);

            const float c1 = 0.01f * 0.01f;
            const float c2 = 0.03f * 0.03f;
            double sum = 0;
            for (int i = 0; i < w * h; i++)
            {
                float varr = sR2[i] - muR2[i];
                float varc = sC2[i] - muC2[i];
                float cov = sRC2[i] - muRC[i];
                float num = (2f * muRC[i] + c1) * (2f * cov + c2);
                float den = (muR2[i] + muC2[i] + c1) * (varr + varc + c2);
                sum += (num + 1e-6f) / (den + 1e-6f);
            }
            return (float)(sum / (w * h));
        }

        /// <summary>
        /// EN: Multi-scale SSIM (5 scales, standard weights). / CN: 多尺度 SSIM（5 尺度，标准权重）。
        /// </summary>
        public static float MsSsim(LinearImage refImg, LinearImage candImg)
        {
            // EN: Downsample chain for both images, compute SSIM per scale, combine with standard weights.
            // CN: 两图下采样链，逐尺度计算 SSIM，用标准权重组合。
            int w = Mathf.Min(refImg.width, candImg.width);
            int h = Mathf.Min(refImg.height, candImg.height);
            if (w < 8 || h < 8) return Ssim(refImg, candImg);

            float[] weights = { 0.0448f, 0.2856f, 0.3001f, 0.2363f, 0.1333f };
            double product = 1.0;
            int scales = 0;
            int cw = w, ch = h;
            var rCur = CropToMin(refImg, w, h);
            var cCur = CropToMin(candImg, w, h);

            while (scales < 5 && cw >= 8 && ch >= 8)
            {
                float ssim = Ssim(rCur, cCur);
                product *= Math.Pow(Math.Max(0, ssim), weights[scales]);
                scales++;
                if (cw / 2 < 8 || ch / 2 < 8) break;
                int nw = Mathf.Max(1, cw / 2), nh = Mathf.Max(1, ch / 2);
                var rDown = Resampler.Bilinear(rCur, nw, nh, false);
                var cDown = Resampler.Bilinear(cCur, nw, nh, false);
                if (rCur != refImg) { } // no-op (owned locally)
                rCur = rDown; cCur = cDown;
                cw = nw; ch = nh;
            }
            return (float)Math.Max(0, product);
        }

        private static LinearImage CropToMin(LinearImage img, int w, int h)
        {
            if (img.width == w && img.height == h) return img;
            return Resampler.Bilinear(img, w, h, false);
        }

        private static float[] BuildGaussian11()
        {
            var g = new float[11];
            float sigma = 1.5f;
            float sum = 0;
            for (int i = -5; i <= 5; i++)
            {
                float v = Mathf.Exp(-(i * i) / (2f * sigma * sigma));
                g[i + 5] = v;
                sum += v;
            }
            for (int i = 0; i < 11; i++) g[i] /= sum;
            return g;
        }

        /// <summary>EN: Separable 11-tap convolution with clamp-to-edge (rows processed in parallel). / CN: 可分离 11 抽头卷积（边缘钳制，按行并行）。</summary>
        private static float[] Convolve(float[] src, int w, int h, float[] kernel)
        {
            var tmp = new float[w * h];
            var dst = new float[w * h];
            int k = kernel.Length, kc = k / 2;

            if (w * h >= 4096)
            {
                Parallel.For(0, h, y =>
                {
                    int row = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        float acc = 0;
                        for (int t = 0; t < k; t++)
                        {
                            int xk = Mathf.Clamp(x + t - kc, 0, w - 1);
                            acc += src[row + xk] * kernel[t];
                        }
                        tmp[row + x] = acc;
                    }
                });
                Parallel.For(0, h, y =>
                {
                    for (int x = 0; x < w; x++)
                    {
                        float acc = 0;
                        for (int t = 0; t < k; t++)
                        {
                            int yk = Mathf.Clamp(y + t - kc, 0, h - 1);
                            acc += tmp[yk * w + x] * kernel[t];
                        }
                        dst[y * w + x] = acc;
                    }
                });
            }
            else
            {
                for (int y = 0; y < h; y++)
                {
                    int row = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        float acc = 0;
                        for (int t = 0; t < k; t++)
                        {
                            int xk = Mathf.Clamp(x + t - kc, 0, w - 1);
                            acc += src[row + xk] * kernel[t];
                        }
                        tmp[row + x] = acc;
                    }
                }
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        float acc = 0;
                        for (int t = 0; t < k; t++)
                        {
                            int yk = Mathf.Clamp(y + t - kc, 0, h - 1);
                            acc += tmp[yk * w + x] * kernel[t];
                        }
                        dst[y * w + x] = acc;
                    }
                }
            }
            return dst;
        }

        private static float[] Mul(float[] a, float[] b)
        {
            var r = new float[a.Length];
            for (int i = 0; i < a.Length; i++) r[i] = a[i] * b[i];
            return r;
        }

        // ------------------------------------------------------------- ΔE

        /// <summary>EN: Mean CIEDE2000 ΔE between two sRGB images (converted internally). / CN: 两幅 sRGB 图像的平均 CIEDE2000 ΔE。</summary>
        public static float DeltaE2000(LinearImage refSrgb, LinearImage candSrgb)
        {
            int n = refSrgb.width * refSrgb.height;
            if (n == 0) return 0f;
            double sum = 0;
            for (int i = 0; i < n; i++)
            {
                float r1 = refSrgb.rgba[i * 4], g1 = refSrgb.rgba[i * 4 + 1], b1 = refSrgb.rgba[i * 4 + 2];
                float r2 = candSrgb.rgba[i * 4], g2 = candSrgb.rgba[i * 4 + 1], b2 = candSrgb.rgba[i * 4 + 2];
                MetricMath.SrgbToLab(r1, g1, b1, out float L1, out float a1, out float bb1);
                MetricMath.SrgbToLab(r2, g2, b2, out float L2, out float a2, out float bb2);
                sum += MetricMath.Ciede2000(L1, a1, bb1, L2, a2, bb2);
            }
            return (float)(sum / n);
        }

        // ------------------------------------------------------------- Alpha

        /// <summary>EN: Linear RMSE on alpha. / CN: alpha 线性 RMSE。</summary>
        public static float AlphaBlendRmse(LinearImage refImg, LinearImage candImg)
        {
            int n = Mathf.Min(refImg.width * refImg.height, candImg.width * candImg.height);
            double sum = 0;
            for (int i = 0; i < n; i++)
            {
                float d = refImg.Alpha(i) - candImg.Alpha(i);
                sum += d * d;
            }
            return (float)Math.Sqrt(sum / Math.Max(1, n));
        }

        /// <summary>EN: Contour IoU of the alpha clip mask at a cutoff (1 = identical silhouettes). / CN: 指定 cutoff 的 alpha 裁剪轮廓 IoU（1 = 完全一致）。</summary>
        public static float AlphaCutoutIou(LinearImage refImg, LinearImage candImg, float cutoff)
        {
            int n = Mathf.Min(refImg.width * refImg.height, candImg.width * candImg.height);
            int inter = 0, uni = 0;
            for (int i = 0; i < n; i++)
            {
                bool a = refImg.Alpha(i) > cutoff;
                bool b = candImg.Alpha(i) > cutoff;
                if (a && b) inter++;
                if (a || b) uni++;
            }
            return uni == 0 ? 1f : (float)inter / uni;
        }

        // ------------------------------------------------------------- Normal

        /// <summary>EN: Decodes tangent-space normals (2x-1, renormalized) and returns (mean, p95) angle error in degrees. / CN: 解码切线空间法线（2x-1，重归一化），返回（均值, p95）角度误差（度）。</summary>
        public static (float mean, float p95) NormalAngleError(LinearImage refNormal, LinearImage candNormal)
        {
            int n = Mathf.Min(refNormal.width * refNormal.height, candNormal.width * candNormal.height);
            var errors = new float[n];
            for (int i = 0; i < n; i++)
            {
                Vector3 a = Decode(refNormal, i);
                Vector3 b = Decode(candNormal, i);
                float dot = Mathf.Clamp(Vector3.Dot(a, b), -1f, 1f);
                errors[i] = Mathf.Acos(dot) * Mathf.Rad2Deg;
            }
            Array.Sort(errors);
            float mean = 0;
            foreach (var e in errors) mean += e;
            mean /= Math.Max(1, n);
            float p95 = errors.Length > 0 ? errors[Mathf.Clamp((int)(errors.Length * 0.95f), 0, errors.Length - 1)] : 0f;
            return (mean, p95);
        }

        private static Vector3 Decode(LinearImage img, int i)
        {
            var v = new Vector3(
                img.rgba[i * 4] * 2f - 1f,
                img.rgba[i * 4 + 1] * 2f - 1f,
                img.rgba[i * 4 + 2] * 2f - 1f);
            if (v.sqrMagnitude < 1e-8f) return Vector3.forward;
            return v.normalized;
        }

        // ------------------------------------------------------------- Gray

        /// <summary>EN: Linear RMSE per channel, on used channels only (used = has variance in the reference); returns worst. / CN: 逐通道线性 RMSE（仅使用通道，使用 = 参考图中有方差）；返回最差。</summary>
        public static float GrayRmseUsedChannels(LinearImage refImg, LinearImage candImg)
        {
            int n = Mathf.Min(refImg.width * refImg.height, candImg.width * candImg.height);
            float worst = 0;
            for (int c = 0; c < 3; c++)
            {
                double sum = 0, sum2 = 0;
                for (int i = 0; i < n; i++)
                {
                    float v = refImg.rgba[i * 4 + c];
                    sum += v; sum2 += v * v;
                }
                double mean = sum / Math.Max(1, n);
                double variance = sum2 / Math.Max(1, n) - mean * mean;
                if (variance < 1e-6f) continue; // 未使用的通道
                double err = 0;
                for (int i = 0; i < n; i++)
                {
                    float d = refImg.rgba[i * 4 + c] - candImg.rgba[i * 4 + c];
                    err += d * d;
                }
                float rmse = (float)Math.Sqrt(err / Math.Max(1, n));
                if (rmse > worst) worst = rmse;
            }
            return worst;
        }

        // ------------------------------------------------------------- 纯色检测

        /// <summary>EN: True when all texels are (nearly) equal. / CN: 全部纹素（近似）相等时为真。</summary>
        public static bool IsPureColor(LinearImage img, float eps = 0.004f)
        {
            int n = img.width * img.height;
            if (n == 0) return true;
            float[] baseC = { img.rgba[0], img.rgba[1], img.rgba[2], img.rgba[3] };
            for (int i = 1; i < n; i++)
            {
                for (int c = 0; c < 4; c++)
                    if (Mathf.Abs(img.rgba[i * 4 + c] - baseC[c]) > eps) return false;
            }
            return true;
        }
    }
}

    // =====================================================================
    // 掩码（实际覆盖区）版本
    // EN: Masked variants: metrics are computed only on the island's actual coverage (mask==1).
    // CN: 掩码版本：指标仅在岛的实际覆盖区（mask==1）上计算。
    // =====================================================================

    /// <summary>EN: Mean SSIM restricted to the masked region (windowed stats are mask-weighted). / CN: 仅在掩码区域的平均 SSIM（窗口统计按掩码加权）。</summary>
    public static float SsimMasked(LinearImage refImg, LinearImage candImg, byte[] mask)
    {
        int w = Mathf.Min(refImg.width, candImg.width);
        int h = Mathf.Min(refImg.height, candImg.height);
        if (w < 4 || h < 4) return 1f;
        if (mask == null) return Ssim(refImg, candImg);

        float[] r = new float[w * h], c = new float[w * h];
        var m = new float[w * h];
        int cnt = 0;
        for (int i = 0; i < w * h; i++)
        {
            r[i] = refImg.Luma(i);
            c[i] = candImg.Luma(i);
            m[i] = mask[i];
            if (mask[i] > 0) cnt++;
        }
        if (cnt == 0) return 1f;

        var gauss = BuildGaussian11();
        // EN: Mask-weighted stats: E[x·m]/E[m].
        // CN: 掩码加权统计：E[x·m]/E[m]。
        var wR = Mul(r, m); var wC = Mul(c, m); var wRC = Mul(r, c); wRC = Mul(wRC, m);
        var wR2 = Mul(r, r); wR2 = Mul(wR2, m);
        var wC2 = Mul(c, c); wC2 = Mul(wC2, m);
        var muR = Div(Convolve(wR, w, h, gauss), Convolve(m, w, h, gauss));
        var muC = Div(Convolve(wC, w, h, gauss), Convolve(m, w, h, gauss));
        var muR2 = Mul(muR, muR);
        var muC2 = Mul(muC, muC);
        var muRC = Mul(muR, muC);
        var sR2 = Div(Convolve(wR2, w, h, gauss), Convolve(m, w, h, gauss));
        var sC2 = Div(Convolve(wC2, w, h, gauss), Convolve(m, w, h, gauss));
        var sRC2 = Div(Convolve(wRC, w, h, gauss), Convolve(m, w, h, gauss));

        const float c1 = 0.01f * 0.01f;
        const float c2 = 0.03f * 0.03f;
        double sum = 0;
        for (int i = 0; i < w * h; i++)
        {
            if (mask[i] == 0) continue;
            float varr = Mathf.Max(0, sR2[i] - muR2[i]);
            float varc = Mathf.Max(0, sC2[i] - muC2[i]);
            float cov = sRC2[i] - muRC[i];
            float num = (2f * muRC[i] + c1) * (2f * cov + c2);
            float den = (muR2[i] + muC2[i] + c1) * (varr + varc + c2);
            sum += (num + 1e-6f) / (den + 1e-6f);
        }
        return (float)(sum / cnt);
    }

    /// <summary>EN: Multi-scale SSIM restricted to the masked region. / CN: 仅在掩码区域的多尺度 SSIM。</summary>
    public static float MsSsimMasked(LinearImage refImg, LinearImage candImg, byte[] mask)
    {
        int w = Mathf.Min(refImg.width, candImg.width);
        int h = Mathf.Min(refImg.height, candImg.height);
        if (w < 8 || h < 8) return SsimMasked(refImg, candImg, mask);
        if (mask == null) return MsSsim(refImg, candImg);

        float[] weights = { 0.0448f, 0.2856f, 0.3001f, 0.2363f, 0.1333f };
        double product = 1.0;
        int scales = 0;
        int cw = w, ch = h;
        var rCur = CropToMin(refImg, w, h);
        var cCur = CropToMin(candImg, w, h);
        var mCur = mask;
        int mw = w, mh = h;

        while (scales < 5 && cw >= 8 && ch >= 8)
        {
            float ssim = SsimMasked(rCur, cCur, mCur);
            product *= Math.Pow(Math.Max(0, ssim), weights[scales]);
            scales++;
            if (cw / 2 < 8 || ch / 2 < 8) break;
            int nw = Mathf.Max(1, cw / 2), nh = Mathf.Max(1, ch / 2);
            rCur = Resampler.Bilinear(rCur, nw, nh, false);
            cCur = Resampler.Bilinear(cCur, nw, nh, false);
            mCur = DownscaleMaskPublic(mCur, mw, mh, nw, nh);
            mw = nw; mh = nh;
            cw = nw; ch = nh;
        }
        return (float)Math.Max(0, product);
    }

    public static byte[] DownscaleMaskPublic(byte[] src, int srcW, int srcH, int dstW, int dstH)
    {
        var dst = new byte[dstW * dstH];
        for (int y = 0; y < dstH; y++)
        {
            int y0 = Mathf.Clamp(y * srcH / dstH, 0, srcH - 1);
            int y1 = Mathf.Clamp((y + 1) * srcH / dstH - 1, 0, srcH - 1);
            for (int x = 0; x < dstW; x++)
            {
                int x0 = Mathf.Clamp(x * srcW / dstW, 0, srcW - 1);
                int x1 = Mathf.Clamp((x + 1) * srcW / dstW - 1, 0, srcW - 1);
                int cnt = 0;
                for (int yy = y0; yy <= y1; yy++)
                    for (int xx = x0; xx <= x1; xx++)
                        if (src[yy * srcW + xx] > 0) cnt++;
                dst[y * dstW + x] = (byte)(cnt > 0 ? 1 : 0);
            }
        }
        return dst;
    }

    private static float[] Div(float[] a, float[] b)
    {
        var r = new float[a.Length];
        for (int i = 0; i < a.Length; i++) r[i] = b[i] > 1e-6f ? a[i] / b[i] : 0f;
        return r;
    }

    /// <summary>EN: Masked mean CIEDE2000 ΔE. / CN: 掩码平均 CIEDE2000 ΔE。</summary>
    public static float DeltaE2000Masked(LinearImage refSrgb, LinearImage candSrgb, byte[] mask)
    {
        int n = Mathf.Min(refSrgb.width * refSrgb.height, candSrgb.width * candSrgb.height);
        if (n == 0) return 0f;
        double sum = 0; int cnt = 0;
        for (int i = 0; i < n; i++)
        {
            if (mask != null && mask[i] == 0) continue;
            MetricMath.SrgbToLab(refSrgb.rgba[i * 4], refSrgb.rgba[i * 4 + 1], refSrgb.rgba[i * 4 + 2],
                out float L1, out float a1, out float b1);
            MetricMath.SrgbToLab(candSrgb.rgba[i * 4], candSrgb.rgba[i * 4 + 1], candSrgb.rgba[i * 4 + 2],
                out float L2, out float a2, out float b2);
            sum += MetricMath.Ciede2000(L1, a1, b1, L2, a2, b2);
            cnt++;
        }
        return cnt == 0 ? 0f : (float)(sum / cnt);
    }

    /// <summary>EN: Masked alpha RMSE. / CN: 掩码 alpha RMSE。</summary>
    public static float AlphaBlendRmseMasked(LinearImage refImg, LinearImage candImg, byte[] mask)
    {
        int n = Mathf.Min(refImg.width * refImg.height, candImg.width * candImg.height);
        double sum = 0; int cnt = 0;
        for (int i = 0; i < n; i++)
        {
            if (mask != null && mask[i] == 0) continue;
            float d = refImg.Alpha(i) - candImg.Alpha(i);
            sum += d * d; cnt++;
        }
        return cnt == 0 ? 0f : (float)Math.Sqrt(sum / cnt);
    }

    /// <summary>EN: Masked cutout contour IoU. / CN: 掩码 Cutout 轮廓 IoU。</summary>
    public static float AlphaCutoutIouMasked(LinearImage refImg, LinearImage candImg, float cutoff, byte[] mask)
    {
        int n = Mathf.Min(refImg.width * refImg.height, candImg.width * candImg.height);
        int inter = 0, uni = 0;
        for (int i = 0; i < n; i++)
        {
            if (mask != null && mask[i] == 0) continue;
            bool a = refImg.Alpha(i) > cutoff;
            bool b = candImg.Alpha(i) > cutoff;
            if (a && b) inter++;
            if (a || b) uni++;
        }
        return uni == 0 ? 1f : (float)inter / uni;
    }

    /// <summary>EN: Masked normal angle error. / CN: 掩码法线角度误差。</summary>
    public static (float mean, float p95) NormalAngleErrorMasked(LinearImage refNormal, LinearImage candNormal, byte[] mask)
    {
        int n = Mathf.Min(refNormal.width * refNormal.height, candNormal.width * candNormal.height);
        var errors = new System.Collections.Generic.List<float>(n);
        for (int i = 0; i < n; i++)
        {
            if (mask != null && mask[i] == 0) continue;
            Vector3 a = Decode(refNormal, i);
            Vector3 b = Decode(candNormal, i);
            float dot = Mathf.Clamp(Vector3.Dot(a, b), -1f, 1f);
            errors.Add(Mathf.Acos(dot) * Mathf.Rad2Deg);
        }
        if (errors.Count == 0) return (0f, 0f);
        var arr = errors.ToArray();
        Array.Sort(arr);
        float mean = 0;
        foreach (var e in arr) mean += e;
        mean /= arr.Length;
        float p95 = arr[Mathf.Clamp((int)(arr.Length * 0.95f), 0, arr.Length - 1)];
        return (mean, p95);
    }

    /// <summary>EN: Masked grayscale RMSE on used channels. / CN: 掩码灰度 RMSE（仅使用通道）。</summary>
    public static float GrayRmseUsedChannelsMasked(LinearImage refImg, LinearImage candImg, byte[] mask)
    {
        int n = Mathf.Min(refImg.width * refImg.height, candImg.width * candImg.height);
        float worst = 0;
        for (int c = 0; c < 3; c++)
        {
            double sum = 0, sum2 = 0; int cnt = 0;
            for (int i = 0; i < n; i++)
            {
                if (mask != null && mask[i] == 0) continue;
                float v = refImg.rgba[i * 4 + c];
                sum += v; sum2 += v * v; cnt++;
            }
            if (cnt == 0) continue;
            double mean = sum / cnt;
            double variance = sum2 / cnt - mean * mean;
            if (variance < 1e-6f) continue;
            double err = 0;
            for (int i = 0; i < n; i++)
            {
                if (mask != null && mask[i] == 0) continue;
                float d = refImg.rgba[i * 4 + c] - candImg.rgba[i * 4 + c];
                err += d * d;
            }
            float rmse = (float)Math.Sqrt(err / cnt);
            if (rmse > worst) worst = rmse;
        }
        return worst;
    }

    /// <summary>EN: Masked pure-color detection. / CN: 掩码纯色检测。</summary>
    public static bool IsPureColorMasked(LinearImage img, byte[] mask, float eps = 0.004f)
    {
        int n = img.width * img.height;
        if (n == 0) return true;
        float[] baseC = null;
        for (int i = 0; i < n; i++)
        {
            if (mask != null && mask[i] == 0) continue;
            if (baseC == null)
                baseC = new[] { img.rgba[i * 4], img.rgba[i * 4 + 1], img.rgba[i * 4 + 2], img.rgba[i * 4 + 3] };
            else
            {
                for (int c = 0; c < 4; c++)
                    if (Mathf.Abs(img.rgba[i * 4 + c] - baseC[c]) > eps) return false;
            }
        }
        return baseC != null;
    }
