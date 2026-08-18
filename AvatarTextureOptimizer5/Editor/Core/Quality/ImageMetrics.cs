// Copyright (c) fosa. Licensed under the MIT License.
// Perceptual image metrics: MS-SSIM, single-scale SSIM, CIEDE2000, normal angular error and
// alpha metrics. All operate on linear-space RGBA. Implementations follow the original papers.
// 感知图像指标：MS-SSIM、单尺度 SSIM、CIEDE2000、法线角度误差与 alpha 指标。
// 全部基于线性空间 RGBA 计算，实现遵循原始论文。

using System;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// A plain linear-space RGBA image buffer used by the metric routines.
    /// 指标计算所使用的线性空间 RGBA 图像缓冲。
    /// </summary>
    public sealed class ImageBuffer
    {
        /// <summary>Width in pixels. / 宽度（像素）。</summary>
        public readonly int Width;

        /// <summary>Height in pixels. / 高度（像素）。</summary>
        public readonly int Height;

        /// <summary>Linear RGBA pixels, row-major. / 线性 RGBA 像素，行主序。</summary>
        public readonly Color[] Pixels;

        /// <summary>Allocates an empty buffer. / 分配空缓冲。</summary>
        public ImageBuffer(int width, int height)
        {
            Width = Mathf.Max(1, width);
            Height = Mathf.Max(1, height);
            Pixels = new Color[Width * Height];
        }

        /// <summary>Wraps an existing pixel array. / 包装已有的像素数组。</summary>
        public ImageBuffer(int width, int height, Color[] pixels)
        {
            Width = Mathf.Max(1, width);
            Height = Mathf.Max(1, height);
            Pixels = pixels;
        }

        /// <summary>Shorter of the two dimensions. / 两个维度中较短者。</summary>
        public int ShortSide => Mathf.Min(Width, Height);

        /// <summary>Reads a pixel with clamped coordinates. / 以钳制坐标读取像素。</summary>
        public Color At(int x, int y)
        {
            x = Mathf.Clamp(x, 0, Width - 1);
            y = Mathf.Clamp(y, 0, Height - 1);
            return Pixels[y * Width + x];
        }
    }

    /// <summary>
    /// Static perceptual metric implementations.
    /// 静态感知指标实现。
    /// </summary>
    public static class ImageMetrics
    {
        // Standard SSIM stabilisation constants for data in the [0,1] range (Wang et al. 2004).
        // 针对 [0,1] 范围数据的标准 SSIM 稳定常数（Wang 等，2004）。
        private const float C1 = 0.0001f;   // (K1*L)^2 with K1=0.01, L=1
        private const float C2 = 0.0009f;   // (K2*L)^2 with K2=0.03, L=1

        /// <summary>
        /// Per-scale weights from Wang, Simoncelli &amp; Bovik (2003), Table 1.
        /// 来自 Wang、Simoncelli 与 Bovik (2003) 表 1 的各尺度权重。
        /// </summary>
        private static readonly float[] MsSsimWeights =
            { 0.0448f, 0.2856f, 0.3001f, 0.2363f, 0.1333f };

        /// <summary>
        /// 11-tap Gaussian kernel with sigma = 1.5, the window used by the SSIM reference
        /// implementation. Normalised to sum to 1.
        /// sigma = 1.5 的 11 抽头高斯核，即 SSIM 参考实现所用窗口，已归一化使和为 1。
        /// </summary>
        private static readonly float[] GaussianKernel = BuildGaussian(11, 1.5f);

        private static float[] BuildGaussian(int size, float sigma)
        {
            var k = new float[size];
            var half = size / 2;
            var sum = 0f;
            for (var i = 0; i < size; i++)
            {
                var d = i - half;
                k[i] = Mathf.Exp(-(d * d) / (2f * sigma * sigma));
                sum += k[i];
            }

            for (var i = 0; i < size; i++) k[i] /= sum;
            return k;
        }

        /// <summary>
        /// Converts linear RGB to relative luminance using Rec.709 coefficients.
        /// 使用 Rec.709 系数将线性 RGB 转换为相对亮度。
        /// </summary>
        public static float[] ToLuminance(ImageBuffer img)
        {
            var lum = new float[img.Width * img.Height];
            for (var i = 0; i < lum.Length; i++)
            {
                var c = img.Pixels[i];
                lum[i] = 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;
            }

            return lum;
        }

        /// <summary>
        /// Single-scale SSIM over the luminance channel with an 11x11 Gaussian window.
        /// 使用 11x11 高斯窗在亮度通道上计算单尺度 SSIM。
        /// </summary>
        public static float Ssim(ImageBuffer a, ImageBuffer b)
        {
            if (a.Width != b.Width || a.Height != b.Height) return 0f;
            var la = ToLuminance(a);
            var lb = ToLuminance(b);
            return SsimOnPlane(la, lb, a.Width, a.Height);
        }

        /// <summary>
        /// SSIM over a single scalar plane.
        /// 在单个标量平面上计算 SSIM。
        /// </summary>
        public static float SsimOnPlane(float[] a, float[] b, int width, int height)
        {
            // Separable Gaussian: compute local means, variances and covariance.
            // 可分离高斯：计算局部均值、方差与协方差。
            var muA = GaussianBlur(a, width, height);
            var muB = GaussianBlur(b, width, height);

            var aa = new float[a.Length];
            var bb = new float[a.Length];
            var ab = new float[a.Length];
            for (var i = 0; i < a.Length; i++)
            {
                aa[i] = a[i] * a[i];
                bb[i] = b[i] * b[i];
                ab[i] = a[i] * b[i];
            }

            var sAA = GaussianBlur(aa, width, height);
            var sBB = GaussianBlur(bb, width, height);
            var sAB = GaussianBlur(ab, width, height);

            double total = 0;
            for (var i = 0; i < a.Length; i++)
            {
                var ma = muA[i];
                var mb = muB[i];
                var vA = sAA[i] - ma * ma;
                var vB = sBB[i] - mb * mb;
                var cov = sAB[i] - ma * mb;

                var num = (2f * ma * mb + C1) * (2f * cov + C2);
                var den = (ma * ma + mb * mb + C1) * (vA + vB + C2);
                total += den > 1e-12f ? num / den : 1.0;
            }

            return (float)(total / a.Length);
        }

        /// <summary>
        /// Contrast and structure terms only, used by all MS-SSIM scales except the coarsest.
        /// 仅计算对比度与结构项，用于 MS-SSIM 中除最粗尺度外的所有尺度。
        /// </summary>
        private static float ContrastStructure(float[] a, float[] b, int width, int height)
        {
            var muA = GaussianBlur(a, width, height);
            var muB = GaussianBlur(b, width, height);

            var aa = new float[a.Length];
            var bb = new float[a.Length];
            var ab = new float[a.Length];
            for (var i = 0; i < a.Length; i++)
            {
                aa[i] = a[i] * a[i];
                bb[i] = b[i] * b[i];
                ab[i] = a[i] * b[i];
            }

            var sAA = GaussianBlur(aa, width, height);
            var sBB = GaussianBlur(bb, width, height);
            var sAB = GaussianBlur(ab, width, height);

            double total = 0;
            for (var i = 0; i < a.Length; i++)
            {
                var vA = sAA[i] - muA[i] * muA[i];
                var vB = sBB[i] - muB[i] * muB[i];
                var cov = sAB[i] - muA[i] * muB[i];

                var num = 2f * cov + C2;
                var den = vA + vB + C2;
                total += den > 1e-12f ? num / den : 1.0;
            }

            return (float)(total / a.Length);
        }

        /// <summary>
        /// Multi-scale SSIM. Requires a short side of at least 176px so that five successive
        /// halvings still leave room for the 11-tap window (11 * 2^4 = 176).
        /// 多尺度 SSIM。要求短边至少 176px，使五次连续降采样后仍能容纳 11 抽头窗口（11 * 2^4 = 176）。
        /// </summary>
        public static float MsSsim(ImageBuffer a, ImageBuffer b)
        {
            if (a.Width != b.Width || a.Height != b.Height) return 0f;

            var la = ToLuminance(a);
            var lb = ToLuminance(b);
            var w = a.Width;
            var h = a.Height;

            double product = 1.0;
            for (var scale = 0; scale < MsSsimWeights.Length; scale++)
            {
                if (scale == MsSsimWeights.Length - 1)
                {
                    // Coarsest scale contributes the full SSIM including the luminance term.
                    // 最粗尺度贡献包含亮度项的完整 SSIM。
                    var full = SsimOnPlane(la, lb, w, h);
                    product *= Math.Pow(Mathf.Max(full, 1e-6f), MsSsimWeights[scale]);
                }
                else
                {
                    var cs = ContrastStructure(la, lb, w, h);
                    product *= Math.Pow(Mathf.Max(cs, 1e-6f), MsSsimWeights[scale]);

                    la = Downsample2x(la, w, h, out var nw, out var nh);
                    lb = Downsample2x(lb, w, h, out _, out _);
                    w = nw;
                    h = nh;
                    if (w < 11 || h < 11) break;
                }
            }

            return (float)product;
        }

        /// <summary>
        /// Separable Gaussian blur with clamped edges.
        /// 边缘钳制的可分离高斯模糊。
        /// </summary>
        private static float[] GaussianBlur(float[] src, int width, int height)
        {
            var k = GaussianKernel;
            var half = k.Length / 2;
            var tmp = new float[src.Length];
            var dst = new float[src.Length];

            for (var y = 0; y < height; y++)
            {
                var row = y * width;
                for (var x = 0; x < width; x++)
                {
                    var sum = 0f;
                    for (var i = 0; i < k.Length; i++)
                    {
                        var sx = Mathf.Clamp(x + i - half, 0, width - 1);
                        sum += src[row + sx] * k[i];
                    }

                    tmp[row + x] = sum;
                }
            }

            for (var x = 0; x < width; x++)
            {
                for (var y = 0; y < height; y++)
                {
                    var sum = 0f;
                    for (var i = 0; i < k.Length; i++)
                    {
                        var sy = Mathf.Clamp(y + i - half, 0, height - 1);
                        sum += tmp[sy * width + x] * k[i];
                    }

                    dst[y * width + x] = sum;
                }
            }

            return dst;
        }

        /// <summary>
        /// 2x box downsample, matching the MS-SSIM reference pipeline.
        /// 2 倍盒式降采样，与 MS-SSIM 参考流程一致。
        /// </summary>
        private static float[] Downsample2x(float[] src, int width, int height, out int nw, out int nh)
        {
            nw = Mathf.Max(1, width / 2);
            nh = Mathf.Max(1, height / 2);
            var dst = new float[nw * nh];
            for (var y = 0; y < nh; y++)
            {
                for (var x = 0; x < nw; x++)
                {
                    var x0 = Mathf.Min(x * 2, width - 1);
                    var y0 = Mathf.Min(y * 2, height - 1);
                    var x1 = Mathf.Min(x * 2 + 1, width - 1);
                    var y1 = Mathf.Min(y * 2 + 1, height - 1);
                    dst[y * nw + x] = 0.25f * (src[y0 * width + x0] + src[y0 * width + x1] +
                                               src[y1 * width + x0] + src[y1 * width + x1]);
                }
            }

            return dst;
        }

        /// <summary>
        /// CIEDE2000 colour difference statistics between two linear-RGB images.
        /// Returns the mean and the 95th percentile.
        /// 两幅线性 RGB 图像之间的 CIEDE2000 色差统计，返回均值与 95 分位数。
        /// </summary>
        public static void DeltaE2000(ImageBuffer a, ImageBuffer b, out float mean, out float p95)
        {
            var n = Mathf.Min(a.Pixels.Length, b.Pixels.Length);
            var values = new float[n];
            double sum = 0;

            for (var i = 0; i < n; i++)
            {
                LinearRgbToLab(a.Pixels[i], out var l1, out var a1, out var b1);
                LinearRgbToLab(b.Pixels[i], out var l2, out var a2, out var b2);
                var d = Ciede2000(l1, a1, b1, l2, a2, b2);
                values[i] = d;
                sum += d;
            }

            mean = n > 0 ? (float)(sum / n) : 0f;
            p95 = Percentile(values, 0.95f);
        }

        /// <summary>
        /// Converts linear RGB to CIE L*a*b* via XYZ with a D65 white point.
        /// 通过 XYZ（D65 白点）将线性 RGB 转换为 CIE L*a*b*。
        /// </summary>
        public static void LinearRgbToLab(Color c, out float l, out float a, out float b)
        {
            // Rec.709 linear RGB to XYZ, D65.
            // Rec.709 线性 RGB 到 XYZ，D65。
            var x = 0.4124564f * c.r + 0.3575761f * c.g + 0.1804375f * c.b;
            var y = 0.2126729f * c.r + 0.7151522f * c.g + 0.0721750f * c.b;
            var z = 0.0193339f * c.r + 0.1191920f * c.g + 0.9503041f * c.b;

            // D65 reference white.
            // D65 参考白。
            x /= 0.95047f;
            z /= 1.08883f;

            x = LabF(x);
            y = LabF(y);
            z = LabF(z);

            l = 116f * y - 16f;
            a = 500f * (x - y);
            b = 200f * (y - z);
        }

        private static float LabF(float t)
        {
            const float delta = 6f / 29f;
            return t > delta * delta * delta
                ? Mathf.Pow(t, 1f / 3f)
                : t / (3f * delta * delta) + 4f / 29f;
        }

        /// <summary>
        /// CIEDE2000 colour difference (Sharma, Wu &amp; Dalal 2005).
        /// CIEDE2000 色差（Sharma、Wu 与 Dalal，2005）。
        /// </summary>
        public static float Ciede2000(
            float l1, float a1, float b1, float l2, float a2, float b2)
        {
            // Computed in double precision throughout. The hue-average branch tests |h1'-h2'|
            // against exactly 180 degrees, and float rounding selects the wrong branch for
            // near-neutral opposing hues (reproduced by the Sharma et al. test pairs 13-15).
            // 全程使用双精度计算。色相平均分支需要将 |h1'-h2'| 与恰好 180 度比较，
            // 单精度舍入会在近中性对立色相处选错分支（Sharma 等的第 13-15 组测试向量即可复现）。
            const double kL = 1.0, kC = 1.0, kH = 1.0;
            const double pow25To7 = 6103515625.0; // 25^7
            const double deg2Rad = Math.PI / 180.0;
            const double rad2Deg = 180.0 / Math.PI;

            double dl1 = l1, da1 = a1, db1 = b1;
            double dl2 = l2, da2 = a2, db2 = b2;

            var c1 = Math.Sqrt(da1 * da1 + db1 * db1);
            var c2 = Math.Sqrt(da2 * da2 + db2 * db2);
            var cBar = (c1 + c2) * 0.5;

            var cBar7 = Math.Pow(cBar, 7.0);
            var g = 0.5 * (1.0 - Math.Sqrt(cBar7 / (cBar7 + pow25To7)));
            var a1p = (1.0 + g) * da1;
            var a2p = (1.0 + g) * da2;

            var c1p = Math.Sqrt(a1p * a1p + db1 * db1);
            var c2p = Math.Sqrt(a2p * a2p + db2 * db2);

            var h1p = HueAngle(db1, a1p, rad2Deg);
            var h2p = HueAngle(db2, a2p, rad2Deg);

            var dLp = dl2 - dl1;
            var dCp = c2p - c1p;

            double dhp;
            if (c1p * c2p == 0.0) dhp = 0.0;
            else
            {
                var diff = h2p - h1p;
                if (diff > 180.0) diff -= 360.0;
                else if (diff < -180.0) diff += 360.0;
                dhp = diff;
            }

            var dHp = 2.0 * Math.Sqrt(c1p * c2p) * Math.Sin(dhp * deg2Rad * 0.5);

            var lBarP = (dl1 + dl2) * 0.5;
            var cBarP = (c1p + c2p) * 0.5;

            double hBarP;
            if (c1p * c2p == 0.0) hBarP = h1p + h2p;
            else
            {
                var diff = Math.Abs(h1p - h2p);
                var sum = h1p + h2p;
                if (diff <= 180.0) hBarP = sum * 0.5;
                else if (sum < 360.0) hBarP = (sum + 360.0) * 0.5;
                else hBarP = (sum - 360.0) * 0.5;
            }

            var t = 1.0
                    - 0.17 * Math.Cos((hBarP - 30.0) * deg2Rad)
                    + 0.24 * Math.Cos(2.0 * hBarP * deg2Rad)
                    + 0.32 * Math.Cos((3.0 * hBarP + 6.0) * deg2Rad)
                    - 0.20 * Math.Cos((4.0 * hBarP - 63.0) * deg2Rad);

            var dThetaArg = (hBarP - 275.0) / 25.0;
            var dTheta = 30.0 * Math.Exp(-(dThetaArg * dThetaArg));
            var cBarP7 = Math.Pow(cBarP, 7.0);
            var rC = 2.0 * Math.Sqrt(cBarP7 / (cBarP7 + pow25To7));
            var rT = -rC * Math.Sin(2.0 * dTheta * deg2Rad);

            var lBarP50 = (lBarP - 50.0) * (lBarP - 50.0);
            var sL = 1.0 + 0.015 * lBarP50 / Math.Sqrt(20.0 + lBarP50);
            var sC = 1.0 + 0.045 * cBarP;
            var sH = 1.0 + 0.015 * cBarP * t;

            var termL = dLp / (kL * sL);
            var termC = dCp / (kC * sC);
            var termH = dHp / (kH * sH);

            return (float)Math.Sqrt(
                termL * termL + termC * termC + termH * termH + rT * termC * termH);
        }

        private static double HueAngle(double b, double ap, double rad2Deg)
        {
            if (ap == 0.0 && b == 0.0) return 0.0;
            var deg = Math.Atan2(b, ap) * rad2Deg;
            return deg < 0.0 ? deg + 360.0 : deg;
        }

        /// <summary>
        /// Normal-map angular error in degrees, reported as mean and 95th percentile.
        /// Both images must already be decoded, resampled and re-normalised.
        /// 法线贴图角度误差（度），返回均值与 95 分位数。两幅图像必须已完成解码、重采样与重归一化。
        /// </summary>
        public static void NormalAngularError(
            ImageBuffer a, ImageBuffer b, out float meanDeg, out float p95Deg)
        {
            var n = Mathf.Min(a.Pixels.Length, b.Pixels.Length);
            var values = new float[n];
            double sum = 0;

            for (var i = 0; i < n; i++)
            {
                var na = DecodeNormal(a.Pixels[i]);
                var nb = DecodeNormal(b.Pixels[i]);
                var dot = Mathf.Clamp(Vector3.Dot(na, nb), -1f, 1f);
                var deg = Mathf.Acos(dot) * Mathf.Rad2Deg;
                values[i] = deg;
                sum += deg;
            }

            meanDeg = n > 0 ? (float)(sum / n) : 0f;
            p95Deg = Percentile(values, 0.95f);
        }

        /// <summary>
        /// Decodes a tangent-space normal, handling both RGB and DXTnm/BC5 style two-channel
        /// encodings where the blue channel must be reconstructed.
        /// 解码切线空间法线，同时处理 RGB 编码与需要重建蓝通道的 DXTnm/BC5 双通道编码。
        /// </summary>
        public static Vector3 DecodeNormal(Color c)
        {
            // DXTnm stores X in alpha and Y in green. Detect it by a near-constant red channel.
            // DXTnm 将 X 存于 alpha、Y 存于 green。通过红通道近似恒定来检测。
            float x, y;
            if (c.r > 0.99f && c.a < 0.99f)
            {
                x = c.a * 2f - 1f;
                y = c.g * 2f - 1f;
            }
            else
            {
                x = c.r * 2f - 1f;
                y = c.g * 2f - 1f;
            }

            var zSq = 1f - x * x - y * y;
            var z = zSq > 0f ? Mathf.Sqrt(zSq) : 0f;
            var v = new Vector3(x, y, z);
            var m = v.magnitude;
            return m > 1e-6f ? v / m : new Vector3(0f, 0f, 1f);
        }

        /// <summary>
        /// Linear-space RMSE restricted to the given channels, expressed in 1/255 units.
        /// Each channel is evaluated separately and the worst result is returned, matching the
        /// specification's per-channel worst-case rule for grayscale data.
        /// 限定通道的线性空间 RMSE，单位为 1/255。逐通道分别评估并返回最差结果，
        /// 符合需求中灰度数据“逐通道取最差”的规定。
        /// </summary>
        public static float WorstChannelRmse255(ImageBuffer a, ImageBuffer b, ChannelMask channels)
        {
            var n = Mathf.Min(a.Pixels.Length, b.Pixels.Length);
            if (n == 0) return 0f;

            var worst = 0f;
            for (var ch = 0; ch < 4; ch++)
            {
                var mask = (ChannelMask)(1 << ch);
                if ((channels & mask) == 0) continue;

                double sum = 0;
                for (var i = 0; i < n; i++)
                {
                    var d = a.Pixels[i][ch] - b.Pixels[i][ch];
                    sum += (double)d * d;
                }

                var rmse = (float)Math.Sqrt(sum / n) * 255f;
                if (rmse > worst) worst = rmse;
            }

            return worst;
        }

        /// <summary>
        /// Alpha RMSE in 1/255 units, for Blend materials.
        /// Blend 材质使用的 alpha RMSE，单位 1/255。
        /// </summary>
        public static float AlphaRmse255(ImageBuffer a, ImageBuffer b)
        {
            var n = Mathf.Min(a.Pixels.Length, b.Pixels.Length);
            if (n == 0) return 0f;

            double sum = 0;
            for (var i = 0; i < n; i++)
            {
                var d = a.Pixels[i].a - b.Pixels[i].a;
                sum += (double)d * d;
            }

            return (float)Math.Sqrt(sum / n) * 255f;
        }

        /// <summary>
        /// Silhouette IoU after applying an alpha cutoff, for Cutout materials. This measures
        /// exactly what the player sees: whether a texel survives the clip test.
        /// Cutout 材质应用 alpha 裁剪后的轮廓 IoU。该指标衡量的正是玩家所见：texel 是否通过裁剪测试。
        /// </summary>
        public static float CutoutIoU(ImageBuffer a, ImageBuffer b, float cutoff)
        {
            var n = Mathf.Min(a.Pixels.Length, b.Pixels.Length);
            if (n == 0) return 1f;

            long intersection = 0;
            long union = 0;
            for (var i = 0; i < n; i++)
            {
                var ka = a.Pixels[i].a >= cutoff;
                var kb = b.Pixels[i].a >= cutoff;
                if (ka && kb) intersection++;
                if (ka || kb) union++;
            }

            return union == 0 ? 1f : (float)intersection / union;
        }

        /// <summary>
        /// Percentile of an unsorted array. The array is sorted in place.
        /// 未排序数组的分位数，会就地排序该数组。
        /// </summary>
        public static float Percentile(float[] values, float fraction)
        {
            if (values == null || values.Length == 0) return 0f;
            Array.Sort(values);
            var idx = Mathf.Clamp(
                Mathf.RoundToInt(fraction * (values.Length - 1)), 0, values.Length - 1);
            return values[idx];
        }
    }
}
