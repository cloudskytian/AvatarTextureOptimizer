// -----------------------------------------------------------------------------
// ATOQualityJobs.cs — Burst-compiled quality metrics.
// ATOQualityJobs.cs — Burst 编译的质量指标。
//
// References / 参考文献:
//  - MS-SSIM: Z. Wang, E. P. Simoncelli, A. C. Bovik, "Multi-scale structural
//    similarity for image quality assessment," Asilomar 2003.
//  - CIEDE2000: G. Sharma, W. Wu, E. N. Dalal, "The CIEDE2000 color-difference
//    formula: implementation notes," Color Research & Application 30(1), 2005.
//  - SSIM single-scale fallback per spec (<176px short side → SSIM; <11px skip).
//    单尺度回退与跳过阈值按规格（<176px→SSIM；<11px→忽略）。
// All metrics operate in linear space on premultiplied-ready data; worst-wins.
// 全部指标在线性空间计算；木桶最差者主导。
// -----------------------------------------------------------------------------

using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace net.fosa.ato.editor
{
    /// <summary>Shared metric outputs / 共享指标输出。</summary>
    internal struct MetricOut
    {
        public float ssim;        // 1 = identical
        public float meanDeltaE;  // CIEDE2000 mean
        public float alphaIou;    // cutout silhouette IoU
        public float alphaRmse;   // 0..255
        public float normalMean;  // degrees
        public float normalP95;   // degrees
        public float grayRmse;    // 0..255, worst used channel
    }

    internal static class ATOQualityJobs
    {
        // ================================================================= //
        // Downsample (premultiplied, area-weighted) & upsample (bilinear)
        // 降采样（预乘、面积加权）与上采样（双线性）
        // ================================================================= //

        /// <summary>
        /// Downsample an RGBA32 (linear) rectangle to (dw, dh) with premultiplied alpha,
        /// area-weighted (box) filtering — the reference resampler used both by quality
        /// evaluation and the final atlas copy, so metrics match the artifact.
        /// 将 RGBA32（线性）矩形区域预乘 alpha、面积加权（box）降采样到 (dw, dh)。
        /// 质量评估与最终图集拷贝共用此重采样器，保证指标与产物一致。
        /// </summary>
        [BurstCompile]
        public struct DownsampleJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<Color32> src;
            public int srcW, srcH;
            public int srcX, srcY, srcWd, srcHt;   // rect / 区域
            public int dstW, dstH;
            public bool premultiply;

            [WriteOnly] public NativeArray<Color32> dst;

            public void Execute(int i)
            {
                int dx = i % dstW, dy = i / dstW;
                float x0 = srcX + (float)dx * srcWd / dstW;
                float x1 = srcX + (float)(dx + 1) * srcWd / dstW;
                float y0 = srcY + (float)dy * srcHt / dstH;
                float y1 = srcY + (float)(dy + 1) * srcHt / dstH;

                float4 acc = 0;
                float wsum = 0;
                for (int sy = (int)math.floor(y0); sy < (int)math.ceil(y1) && sy < srcY + srcHt; sy++)
                {
                    float wy = math.min(y1, sy + 1) - math.max(y0, sy);
                    if (wy <= 0) continue;
                    for (int sx = (int)math.floor(x0); sx < (int)math.ceil(x1) && sx < srcX + srcWd; sx++)
                    {
                        float wx = math.min(x1, sx + 1) - math.max(x0, sx);
                        if (wx <= 0) continue;
                        var c = ToF4(src[sy * srcW + sx]);
                        float w = wx * wy;
                        if (premultiply) c = new float4(c.rgb * c.a, c.a) * w;
                        else c *= w;
                        acc += c;
                        wsum += w;
                    }
                }

                if (wsum <= 0) { dst[i] = new Color32(0, 0, 0, 0); return; }
                acc /= wsum;
                if (premultiply && acc.a > 1e-6f) acc = new float4(acc.rgb / acc.a, acc.a);
                dst[i] = FromF4(acc);
            }

            internal static float4 ToF4(Color32 c) => new float4(c.r, c.g, c.b, c.a) / 255f;
            internal static Color32 FromF4(float4 f) => new Color32(
                (byte)math.clamp((int)math.round(f.x * 255f), 0, 255),
                (byte)math.clamp((int)math.round(f.y * 255f), 0, 255),
                (byte)math.clamp((int)math.round(f.z * 255f), 0, 255),
                (byte)math.clamp((int)math.round(f.w * 255f), 0, 255));
        }

        /// <summary>Bilinear upsample (used to compare against the original).
        /// 双线性上采样（用于与原图比较）。</summary>
        [BurstCompile]
        public struct UpsampleJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<Color32> src;
            public int srcW, srcH;
            public int dstW, dstH;

            [WriteOnly] public NativeArray<Color32> dst;

            public void Execute(int i)
            {
                int ux = i % dstW, uy = i / dstW;
                float fx = (ux + 0.5f) * srcW / dstW - 0.5f;
                float fy = (uy + 0.5f) * srcH / dstH - 0.5f;
                int x0 = (int)math.floor(fx), y0 = (int)math.floor(fy);
                float tx = fx - x0, ty = fy - y0;
                x0 = math.clamp(x0, 0, srcW - 1);
                y0 = math.clamp(y0, 0, srcH - 1);
                int x1 = math.min(x0 + 1, srcW - 1), y1 = math.min(y0 + 1, srcH - 1);
                var a = DownsampleJob.ToF4(src[y0 * srcW + x0]);
                var b = DownsampleJob.ToF4(src[y0 * srcW + x1]);
                var c = DownsampleJob.ToF4(src[y1 * srcW + x0]);
                var d = DownsampleJob.ToF4(src[y1 * srcW + x1]);
                var v = math.lerp(math.lerp(a, b, tx), math.lerp(c, d, tx), ty);
                dst[i] = DownsampleJob.FromF4(v);
            }
        }

        // ================================================================= //
        // MS-SSIM / SSIM (luma)
        // ================================================================= //

        /// <summary>
        /// MS-SSIM with 5 scales (weights 0.0448...0.1333 per Wang 2003). Falls back to
        /// single-scale SSIM when shortSide &lt; 176 (i.e. < 11px at scale 5); skipped
        /// (returns 1) when shortSide &lt; 11.
        /// 5 尺度 MS-SSIM（Wang 2003 权重）。短边 <176（第5尺度不足11px）回退单尺度 SSIM；
        /// 短边 <11 直接返回 1（忽略）。
        /// </summary>
        [BurstCompile]
        public struct MsSsimJob : IJob
        {
            [ReadOnly] public NativeArray<Color32> a;   // original / 原图
            [ReadOnly] public NativeArray<Color32> b;   // degraded / 缩放后
            public int width, height;

            public NativeArray<float> result;   // [0] = msssim

            public void Execute()
            {
                int shortSide = math.min(width, height);
                if (shortSide < 11) { result[0] = 1f; return; }

                // Work in luma (linear → perceptual approx with simple sRGB-like gamma).
                // 以亮度计算（线性→近似感知伽马）。
                int n = width * height;
                var la = new NativeArray<float>(n, Allocator.Temp);
                var lb = new NativeArray<float>(n, Allocator.Temp);
                for (int i = 0; i < n; i++)
                {
                    la[i] = Luma(DownsampleJob.ToF4(a[i]));
                    lb[i] = Luma(DownsampleJob.ToF4(b[i]));
                }

                bool multi = shortSide >= 176;
                float[] weights = multi
                    ? new float[] { 0.0448f, 0.2856f, 0.3001f, 0.2363f, 0.1333f }
                    : new float[] { 1f };

                float msssim = 1f;
                int cw = width, ch = height;
                var ca = new NativeArray<float>(n, Allocator.Temp);
                var cb = new NativeArray<float>(n, Allocator.Temp);
                la.CopyTo(ca);
                lb.CopyTo(cb);

                for (int s = 0; s < weights.Length; s++)
                {
                    bool last = s == weights.Length - 1;
                    float cs = Ssim(ca, cb, cw, ch, !last);
                    msssim *= math.pow(math.max(cs, 1e-6f), weights[s]);
                    if (last) break;

                    // 2x2 average down / 2x2 均值下采样
                    int nw = math.max(1, cw / 2), nh = math.max(1, ch / 2);
                    Downscale(ref ca, cw, ch, nw, nh);
                    Downscale(ref cb, cw, ch, nw, nh);
                    cw = nw; ch = nh;
                }

                result[0] = msssim;

                ca.Dispose();
                cb.Dispose();
                la.Dispose();
                lb.Dispose();
            }

            private static float Luma(float4 c) =>
                0.2126f * srgb(c.x) + 0.7152f * srgb(c.y) + 0.0722f * srgb(c.z);

            private static float srgb(float lin) =>
                lin <= 0.0031308f ? lin * 12.92f : 1.055f * math.pow(lin, 1f / 2.4f) - 0.055f;

            private static void Downscale(ref NativeArray<float> img, int w, int h, int nw, int nh)
            {
                var tmp = new NativeArray<float>(nw * nh, Allocator.Temp);
                for (int y = 0; y < nh; y++)
                for (int x = 0; x < nw; x++)
                {
                    int x0 = math.min(x * 2, w - 1), x1 = math.min(x * 2 + 1, w - 1);
                    int y0 = math.min(y * 2, h - 1), y1 = math.min(y * 2 + 1, h - 1);
                    tmp[y * nw + x] = 0.25f * (img[y0 * w + x0] + img[y0 * w + x1] +
                                               img[y1 * w + x0] + img[y1 * w + x1]);
                }

                for (int i = 0; i < tmp.Length; i++) img[i] = tmp[i];
                tmp.Dispose();
            }

            /// <summary>SSIM with C1/C2 per Wang; contrast-term-only when csOnly (MS-SSIM internal).
            /// 标准 SSIM；csOnly 时仅返回对比项（MS-SSIM 内部尺度用）。</summary>
            private static float Ssim(NativeArray<float> x, NativeArray<float> y, int w, int h, bool csOnly)
            {
                const float K1 = 0.01f, K2 = 0.03f, L = 1f;
                float C1 = K1 * K1 * L * L, C2 = K2 * K2 * L * L;

                // 8x8-block mean like the reference implementation (fast & close enough).
                // 与参考实现类似的 8x8 块均值（快且足够接近）。
                const int B = 8;
                double mssim = 0; int count = 0;
                for (int by = 0; by < h; by += B)
                for (int bx = 0; bx < w; bx += B)
                {
                    int bw = math.min(B, w - bx), bh = math.min(B, h - by);
                    double mx = 0, my = 0;
                    for (int y = 0; y < bh; y++)
                    for (int x = 0; x < bw; x++)
                    {
                        mx += x[(by + y) * w + bx + x];
                        my += y[(by + y) * w + bx + x];
                    }

                    double inv = 1.0 / (bw * bh);
                    mx *= inv; my *= inv;
                    double sxx = 0, syy = 0, sxy = 0;
                    for (int y = 0; y < bh; y++)
                    for (int x = 0; x < bw; x++)
                    {
                        float vx = x[(by + y) * w + bx + x] - (float)mx;
                        float vy = y[(by + y) * w + bx + x] - (float)my;
                        sxx += vx * vx; syy += vy * vy; sxy += vx * vy;
                    }

                    sxx *= inv; syy *= inv; sxy *= inv;
                    float lum = (float)((2 * mx * my + C1) / (mx * mx + my * my + C1));
                    float con = (float)((2 * sxy + C2) / (sxx + syy + C2));
                    msssim += csOnly ? con : lum * con;
                    count++;
                }

                return (float)(mssim / math.max(1, count));
            }
        }

        // ================================================================= //
        // CIEDE2000 mean / ΔE00 均值
        // ================================================================= //

        /// <summary>Mean CIEDE2000 over the rectangle, linear RGB → Lab per pixel.
        /// 区域内 CIEDE2000 均值，逐像素 线性RGB→Lab。</summary>
        [BurstCompile]
        public struct DeltaEJob : IJob
        {
            [ReadOnly] public NativeArray<Color32> a;
            [ReadOnly] public NativeArray<Color32> b;
            public int n;

            public NativeArray<float> result;   // [0] = mean ΔE00

            public void Execute()
            {
                double sum = 0;
                for (int i = 0; i < n; i++)
                {
                    var lab1 = ToLab(DownsampleJob.ToF4(a[i]));
                    var lab2 = ToLab(DownsampleJob.ToF4(b[i]));
                    sum += Ciede2000(lab1, lab2);
                }

                result[0] = (float)(sum / math.max(1, n));
            }

            private static float3 ToLab(float4 rgb)
            {
                // linear sRGB (D65) → XYZ → Lab / 线性sRGB(D65)→XYZ→Lab
                float X = 0.4124564f * rgb.x + 0.3575761f * rgb.y + 0.1804375f * rgb.z;
                float Y = 0.2126729f * rgb.x + 0.7151522f * rgb.y + 0.0721750f * rgb.z;
                float Z = 0.0193339f * rgb.x + 0.1191920f * rgb.y + 0.9503041f * rgb.z;

                const float wx = 0.95047f, wy = 1.0f, wz = 1.08883f;
                float fx = LabF(X / wx), fy = LabF(Y / wy), fz = LabF(Z / wz);
                return new float3(116f * fy - 16f, 500f * (fx - fy), 200f * (fy - fz));
            }

            private static float LabF(float t) =>
                t > 0.008856f ? math.pow(t, 1f / 3f) : (7.787f * t + 16f / 116f);

            internal static float Ciede2000(float3 lab1, float3 lab2)
            {
                float L1 = lab1.x, a1 = lab1.y, b1 = lab1.z;
                float L2 = lab2.x, a2 = lab2.y, b2 = lab2.z;

                float C1 = math.sqrt(a1 * a1 + b1 * b1);
                float C2 = math.sqrt(a2 * a2 + b2 * b2);
                float Cb = 0.5f * (C1 + C2);
                float Cb7 = math.pow(Cb, 7f);
                float G = 0.5f * (1f - math.sqrt(Cb7 / (Cb7 + 6103515625f))); // 25^7

                float ap1 = (1f + G) * a1, ap2 = (1f + G) * a2;
                float Cp1 = math.sqrt(ap1 * ap1 + b1 * b1);
                float Cp2 = math.sqrt(ap2 * ap2 + b2 * b2);

                float hp1 = (ap1 == 0 && b1 == 0) ? 0 : math.degrees(math.atan2(b1, ap1));
                if (hp1 < 0) hp1 += 360f;
                float hp2 = (ap2 == 0 && b2 == 0) ? 0 : math.degrees(math.atan2(b2, ap2));
                if (hp2 < 0) hp2 += 360f;

                float dL = L2 - L1;
                float dC = Cp2 - Cp1;

                float dhp;
                if (Cp1 * Cp2 == 0) dhp = 0;
                else
                {
                    dhp = hp2 - hp1;
                    if (dhp > 180f) dhp -= 360f;
                    else if (dhp < -180f) dhp += 360f;
                }

                float dH = 2f * math.sqrt(Cp1 * Cp2) * math.sin(math.radians(dhp) * 0.5f);

                float Lbp = 0.5f * (L1 + L2);
                float Cbp = 0.5f * (Cp1 + Cp2);

                float hbp;
                if (Cp1 * Cp2 == 0) hbp = hp1 + hp2;
                else
                {
                    float sum = hp1 + hp2;
                    if (math.abs(hp1 - hp2) > 180f) hbp = sum < 360f ? sum + 360f : sum - 360f;
                    else hbp = sum;
                    hbp *= 0.5f;
                }

                float T = 1f - 0.17f * math.cos(math.radians(hbp - 30f))
                            + 0.24f * math.cos(math.radians(2f * hbp))
                            + 0.32f * math.cos(math.radians(3f * hbp + 6f))
                            - 0.20f * math.cos(math.radians(4f * hbp - 63f));

                float dTheta = 30f * math.exp(-math.pow((hbp - 275f) / 25f, 2f));
                float Cbp7 = math.pow(Cbp, 7f);
                float Rc = 2f * math.sqrt(Cbp7 / (Cbp7 + 6103515625f));

                float Ld = (Lbp - 50f) * (Lbp - 50f);
                float Sl = 1f + 0.015f * Ld / math.sqrt(20f + Ld);
                float Sc = 1f + 0.045f * Cbp;
                float Sh = 1f + 0.015f * Cbp * T;
                float Rt = -math.sin(math.radians(2f * dTheta)) * Rc;

                float tl = dL / Sl, tc = dC / Sc, th = dH / Sh;
                return math.sqrt(tl * tl + tc * tc + th * th + Rt * tc * th);
            }
        }

        // ================================================================= //
        // Alpha: cutout IoU & blend RMSE
        // ================================================================= //

        [BurstCompile]
        public struct AlphaJob : IJob
        {
            [ReadOnly] public NativeArray<Color32> a;
            [ReadOnly] public NativeArray<Color32> b;
            public int n;
            public float cutoff;

            public NativeArray<float> result;   // [0]=IoU [1]=RMSE(0..255)

            public void Execute()
            {
                int inter = 0, union = 0;
                double se = 0;
                for (int i = 0; i < n; i++)
                {
                    bool ca = a[i].a / 255f >= cutoff;
                    bool cb = b[i].a / 255f >= cutoff;
                    if (ca && cb) inter++;
                    if (ca || cb) union++;
                    float d = a[i].a - b[i].a;
                    se += d * d;
                }

                result[0] = union == 0 ? 1f : (float)inter / union;
                result[1] = (float)math.sqrt(se / math.max(1, n));
            }
        }

        // ================================================================= //
        // Normals: angular error mean & p95 / 法线角度误差均值与p95
        // ================================================================= //

        /// <summary>Input textures are DXTnm-agnostic decoded XY+Z-reconstructed unit vectors in RGB(A).
        /// 输入为已解码并重建Z的单位法线（存于RGB(A)）。</summary>
        [BurstCompile]
        public struct NormalJob : IJob
        {
            [ReadOnly] public NativeArray<Color32> a;
            [ReadOnly] public NativeArray<Color32> b;
            public int n;

            public NativeArray<float> result;   // [0]=mean° [1]=p95°

            public void Execute()
            {
                var angles = new NativeArray<float>(n, Allocator.Temp);
                double sum = 0;
                for (int i = 0; i < n; i++)
                {
                    var va = Decode(a[i]);
                    var vb = Decode(b[i]);
                    float dot = math.clamp(math.dot(va, vb), -1f, 1f);
                    float deg = math.degrees(math.acos(dot));
                    angles[i] = deg;
                    sum += deg;
                }

                angles.Sort();
                int p95i = math.clamp((int)math.floor(0.95f * (n - 1)), 0, n - 1);
                result[0] = (float)(sum / math.max(1, n));
                result[1] = angles[p95i];
                angles.Dispose();
            }

            private static float3 Decode(Color32 c)
            {
                var v = new float3(c.r / 255f * 2f - 1f, c.g / 255f * 2f - 1f, c.b / 255f * 2f - 1f);
                float len = math.length(v);
                return len > 1e-4f ? v / len : new float3(0, 0, 1);
            }
        }

        // ================================================================= //
        // Gray: per-channel RMSE (worst used channel) / 灰度逐通道RMSE（最差使用通道）
        // ================================================================= //

        [BurstCompile]
        public struct GrayJob : IJob
        {
            [ReadOnly] public NativeArray<Color32> a;
            [ReadOnly] public NativeArray<Color32> b;
            public int n;
            public bool4 usedChannels;

            public NativeArray<float> result;   // [0] = worst RMSE 0..255

            public void Execute()
            {
                float worst = 0;
                for (int ch = 0; ch < 4; ch++)
                {
                    if (!usedChannels[ch]) continue;
                    double se = 0;
                    for (int i = 0; i < n; i++)
                    {
                        float va = Ch(a[i], ch), vb = Ch(b[i], ch);
                        float d = va - vb;
                        se += d * d;
                    }

                    worst = math.max(worst, (float)math.sqrt(se / math.max(1, n)));
                }

                result[0] = worst;
            }

            private static float Ch(Color32 c, int i) => i == 0 ? c.r : i == 1 ? c.g : i == 2 ? c.b : c.a;
        }

        // ================================================================= //
        // Pure-color detection / 纯色检测
        // ================================================================= //

        [BurstCompile]
        public struct PureColorJob : IJob
        {
            [ReadOnly] public NativeArray<Color32> src;
            public int n;

            public NativeArray<float> result;   // [0] = 1 if pure / 纯色为1

            public void Execute()
            {
                var first = src[0];
                for (int i = 1; i < n; i++)
                {
                    var c = src[i];
                    if (c.r != first.r || c.g != first.g || c.b != first.b || c.a != first.a)
                    {
                        result[0] = 0;
                        return;
                    }
                }

                result[0] = 1;
            }
        }
    }
}
