// Burst metric jobs. All comparisons: island coverage region downsampled (area-average,
// linear space, premultiplied alpha for transparent) then bilinearly upsampled back to
// original size and compared against the source (spec).
// Burst 指标作业。比较方式：岛覆盖区下采样（面积平均、线性空间、透明预乘alpha）后
// 双线性上采样回原尺寸与原图比较（需求书）。
//
// Spaces: luma & SSIM on display-referred (sRGB) luma; ΔE in CIELAB (linearized);
// alpha in linear; normals decoded then angle; masks per-channel linear RMSE.

using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace net.fosa.ato.editor
{
    internal static class MetricFactory
    {
        // MS-SSIM standard weights (Wang et al. 2003). / 标准五尺度权重。
        internal static readonly float[] MsssimWeights = { 0.0448f, 0.2856f, 0.3001f, 0.2363f, 0.1333f };
        internal const float SsimK1 = 0.01f, SsimK2 = 0.03f;
        internal const int SsimWin = 11;         // 11x11 gaussian window / 高斯窗
        internal const float SsimSigma = 1.5f;

        internal static bool UseSingleScaleSsim(int shortSide) => shortSide < 176; // spec / 需求书
        internal static bool IgnoreSsim(int shortSide) => shortSide < 11;         // spec / 需求书
    }

    /// <summary>Area-average downsample of a source region (premultiply option).
    /// 源区域面积平均下采样（可选预乘）。</summary>
    [BurstCompile]
    internal struct DownsampleJob : IJob
    {
        [ReadOnly] internal NativeArray<Color32> src; // full texture / 整张贴图
        internal int srcW, srcH;
        internal int4 region;     // x,y,w,h in source pixels / 源像素区域
        internal bool premultiply;
        internal bool srgb;
        internal NativeArray<Color32> dst; // region-sized / 区域尺寸
        internal NativeArray<int2> dstSize;

        internal void Execute()
        {
            int dw = dstSize[0].x, dh = dstSize[0].y;
            float sx = (float)region.z / dw, sy = (float)region.w / dh;
            for (int y = 0; y < dh; y++)
            {
                int y0 = region.y + (int)(y * sy), y1 = region.y + (int)((y + 1) * sy);
                if (y1 <= y0) y1 = y0 + 1;
                for (int x = 0; x < dw; x++)
                {
                    int x0 = region.x + (int)(x * sx), x1 = region.x + (int)((x + 1) * sx);
                    if (x1 <= x0) x1 = x0 + 1;
                    float4 acc = 0;
                    int n = 0;
                    for (int yy = y0; yy < y1 && yy < srcH; yy++)
                        for (int xx = x0; xx < x1 && xx < srcW; xx++)
                        {
                            var c = src[yy * srcW + xx];
                            var f = new float4(c.r, c.g, c.b, c.a) / 255f;
                            if (srgb) f = SrgbToLinear(f);
                            if (premultiply) f *= f.w;
                            acc += f;
                            n++;
                        }

                    var v = acc / math.max(1, n);
                    if (premultiply && v.w > 1e-5f) v /= v.w;
                    if (srgb) v = LinearToSrgb(v);
                    dst[y * dw + x] = new Color32((byte)math.round(v.x * 255), (byte)math.round(v.y * 255),
                        (byte)math.round(v.z * 255), (byte)math.round(math.saturate(v.w) * 255));
                }
            }
        }

        internal static float4 SrgbToLinear(float4 c) =>
            new float4(SrgbToLinear1(c.x), SrgbToLinear1(c.y), SrgbToLinear1(c.z), c.w);

        internal static float4 LinearToSrgb(float4 c) =>
            new float4(LinearToSrgb1(c.x), LinearToSrgb1(c.y), LinearToSrgb1(c.z), math.saturate(c.w));

        internal static float SrgbToLinear1(float v) =>
            v <= 0.04045f ? v / 12.92f : math.pow((v + 0.055f) / 1.055f, 2.4f);

        internal static float LinearToSrgb1(float v) =>
            v <= 0.0031308f ? v * 12.92f : 1.055f * math.pow(v, 1f / 2.4f) - 0.055f;
    }

    /// <summary>Bilinear upsample back to region size. / 双线性上采样回区域尺寸。</summary>
    [BurstCompile]
    internal struct UpsampleJob : IJob
    {
        [ReadOnly] internal NativeArray<Color32> src;
        internal int2 srcSize;
        internal NativeArray<Color32> dst; // srcSize -> region size / 输出区域尺寸
        internal NativeArray<int2> dstSize;

        internal void Execute()
        {
            int dw = dstSize[0].x, dh = dstSize[0].y;
            for (int y = 0; y < dh; y++)
            {
                float fy = (y + 0.5f) * srcSize.y / dh - 0.5f;
                int y0 = (int)math.floor(fy);
                float ty = math.saturate(fy - y0);
                for (int x = 0; x < dw; x++)
                {
                    float fx = (x + 0.5f) * srcSize.x / dw - 0.5f;
                    int x0 = (int)math.floor(fx);
                    float tx = math.saturate(fx - x0);
                    var a = At(x0, y0);
                    var b = At(x0 + 1, y0);
                    var c = At(x0, y0 + 1);
                    var d = At(x0 + 1, y0 + 1);
                    float4 top = math.lerp(a, b, tx), bot = math.lerp(c, d, tx);
                    var v = math.lerp(top, bot, ty);
                    dst[y * dw + x] = new Color32((byte)math.round(v.x), (byte)math.round(v.y),
                        (byte)math.round(v.z), (byte)math.round(v.w));
                }
            }
        }

        private float4 At(int x, int y)
        {
            x = math.clamp(x, 0, srcSize.x - 1);
            y = math.clamp(y, 0, srcSize.y - 1);
            var c = src[y * srcSize.x + x];
            return new float4(c.r, c.g, c.b, c.a);
        }
    }

    /// <summary>Masked MS-SSIM / single-scale SSIM on display luma.
    /// 掩码加权 MS-SSIM / 单尺度 SSIM（显示亮度）。</summary>
    [BurstCompile]
    internal struct SsimJob : IJob
    {
        [ReadOnly] internal NativeArray<float> refLuma;  // region-size / 区域尺寸
        [ReadOnly] internal NativeArray<float> testLuma;
        [ReadOnly] internal NativeArray<float> mask;     // 0..1 coverage / 覆盖
        internal int width, height;
        internal bool singleScale;
        [WriteOnly] internal NativeArray<float> result; // [0] = score / 得分

        private static float Gauss(float d, float sigma) => math.exp(-d * d / (2 * sigma * sigma));

        internal void Execute()
        {
            int n = width * height;
            var bufA = new NativeArray<float>(n, Allocator.Temp);
            var bufB = new NativeArray<float>(n, Allocator.Temp);
            var curRef = new NativeArray<float>(n, Allocator.Temp);
            var curTest = new NativeArray<float>(n, Allocator.Temp);
            var curMask = new NativeArray<float>(n, Allocator.Temp);
            refLuma.CopyTo(curRef);
            testLuma.CopyTo(curTest);
            mask.CopyTo(curMask);

            int scales = singleScale ? 1 : 5;
            float score = 0, wsum = 0;
            int w = width, h = height;

            for (int s = 0; s < scales; s++)
            {
                if (w < SsimWin + 1 || h < SsimWin + 1) break;
                float ss = SsimAtScale(curRef, curTest, curMask, w, h, bufA, bufB);
                float wt = MetricFactory.MsssimWeights[Mathf.Min(s, 4)];
                score += wt * ss;
                wsum += wt;
                if (s == scales - 1) break;

                // 2x downsample / 隔行下采样
                int nw = w / 2, nh = h / 2;
                for (int y = 0; y < nh; y++)
                    for (int x = 0; x < nw; x++)
                    {
                        int i = (y * 2) * w + x * 2;
                        curRef[y * nw + x] = 0.25f * (curRef[i] + curRef[i + 1] + curRef[i + w] + curRef[i + w + 1]);
                        curTest[y * nw + x] = 0.25f * (curTest[i] + curTest[i + 1] + curTest[i + w] + curTest[i + w + 1]);
                        curMask[y * nw + x] = 0.25f * (curMask[i] + curMask[i + 1] + curMask[i + w] + curMask[i + w + 1]);
                    }

                w = nw;
                h = nh;
            }

            result[0] = wsum > 0 ? score / wsum : 1f;

            bufA.Dispose();
            bufB.Dispose();
            curRef.Dispose();
            curTest.Dispose();
            curMask.Dispose();
        }

        private float SsimAtScale(NativeArray<float> r, NativeArray<float> t, NativeArray<float> m,
            int w, int h, NativeArray<float> a, NativeArray<float> b)
        {
            // separable gaussian-weighted means of r, t, r², t², rt, m / 各统计量的分离高斯均值
            float c1 = MetricFactory.SsimK1 * MetricFactory.SsimK1;
            float c2 = MetricFactory.SsimK2 * MetricFactory.SsimK2;
            // six statistic planes (Burst-safe explicit buffers, no managed arrays)
            // 六个统计平面（显式缓冲，Burst 兼容）
            var s0 = new NativeArray<float>(w * h, Allocator.Temp);
            var s1 = new NativeArray<float>(w * h, Allocator.Temp);
            var s2 = new NativeArray<float>(w * h, Allocator.Temp);
            var s3 = new NativeArray<float>(w * h, Allocator.Temp);
            var s4 = new NativeArray<float>(w * h, Allocator.Temp);
            var s5 = new NativeArray<float>(w * h, Allocator.Temp);

            // We filter: r*m, t*m, r²*m, t²*m, r*t*m, m (then normalize by mu_m).
            FilterWeighted(r, m, w, h, a, b); CopyStat(s0, a);
            FilterWeighted(t, m, w, h, a, b); CopyStat(s1, a);
            FilterWeighted(r, m, w, h, a, b, r); // r² weighted / r² 加权
            CopyStat(s2, a);
            FilterWeighted(t, m, w, h, a, b, t); CopyStat(s3, a);
            FilterWeightedRt(r, t, m, w, h, a, b); CopyStat(s4, a);
            FilterPlain(m, w, h, a, b); CopyStat(s5, a);

            float sum = 0, wsum = 0;
            int half = SsimWin / 2;
            for (int y = half; y < h - half; y++)
                for (int x = half; x < w - half; x++)
                {
                    int i = y * w + x;
                    float wm = s5[i];
                    if (wm < 0.25f) continue; // outside coverage / 覆盖外
                    float mr = s0[i] / wm, mt = s1[i] / wm;
                    float rr = s2[i] / wm, tt = s3[i] / wm, rt = s4[i] / wm;
                    float vr = math.max(0, rr - mr * mr), vt = math.max(0, tt - mt * mt);
                    float cvt = rt - mr * mt;
                    float ss = ((2 * mr * mt + c1) * (2 * cvt + c2)) /
                               ((mr * mr + mt * mt + c1) * (vr + vt + c2));
                    // weight by mask / 掩码加权
                    sum += ss * wm;
                    wsum += wm;
                }

            s0.Dispose(); s1.Dispose(); s2.Dispose(); s3.Dispose(); s4.Dispose(); s5.Dispose();
            return wsum > 0 ? sum / wsum : 1f;
        }

        private static void CopyStat(NativeArray<float> dst, NativeArray<float> src)
        {
            for (int i = 0; i < dst.Length; i++) dst[i] = src[i];
        }

        private void FilterWeighted(NativeArray<float> v, NativeArray<float> m, int w, int h,
            NativeArray<float> a, NativeArray<float> b, NativeArray<float> square = null)
        {
            // horizontal pass into a / 水平 pass
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float acc = 0;
                    for (int k = -SsimWin / 2; k <= SsimWin / 2; k++)
                    {
                        float g = Gauss(k, SsimSigma);
                        int xx = math.clamp(x + k, 0, w - 1);
                        float val = square != null ? square[y * w + xx] : v[y * w + xx];
                        acc += g * val * m[y * w + xx];
                    }
                    a[y * w + x] = acc;
                }

            // vertical pass into b / 垂直 pass
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float acc = 0;
                    for (int k = -SsimWin / 2; k <= SsimWin / 2; k++)
                    {
                        float g = Gauss(k, SsimSigma);
                        int yy = math.clamp(y + k, 0, h - 1);
                        acc += g * a[yy * w + x];
                    }
                    b[y * w + x] = acc;
                }
        }

        private void FilterWeightedRt(NativeArray<float> r, NativeArray<float> t, NativeArray<float> m,
            int w, int h, NativeArray<float> a, NativeArray<float> b)
        {
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float acc = 0;
                    for (int k = -SsimWin / 2; k <= SsimWin / 2; k++)
                    {
                        float g = Gauss(k, SsimSigma);
                        int xx = math.clamp(x + k, 0, w - 1);
                        acc += g * r[y * w + xx] * t[y * w + xx] * m[y * w + xx];
                    }
                    a[y * w + x] = acc;
                }

            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float acc = 0;
                    for (int k = -SsimWin / 2; k <= SsimWin / 2; k++)
                    {
                        float g = Gauss(k, SsimSigma);
                        int yy = math.clamp(y + k, 0, h - 1);
                        acc += g * a[yy * w + x];
                    }
                    b[y * w + x] = acc;
                }
        }

        private void FilterPlain(NativeArray<float> m, int w, int h, NativeArray<float> a, NativeArray<float> b)
        {
            FilterWeighted(m, m, w, h, a, b); // = gaussian(m) since v==m / 即 gaussian(m)
        }
    }

    /// <summary>CIEDE2000 mean & p95 (Sharma 2005 formulation) over masked pixels.
    /// 掩码内 CIEDE2000 均值与 p95（Sharma 2005 公式）。</summary>
    [BurstCompile]
    internal struct DeltaEJob : IJob
    {
        [ReadOnly] internal NativeArray<Color32> refPx;   // region-size, sRGB stored / 区域尺寸
        [ReadOnly] internal NativeArray<Color32> testPx;
        [ReadOnly] internal NativeArray<float> mask;
        internal bool refIsSrgb, testIsSrgb;
        [WriteOnly] internal NativeArray<float2> result; // (mean, p95)

        internal void Execute()
        {
            double sum = 0;
            long count = 0;
            var hist = new NativeArray<int>(1024, Allocator.Temp); // 0..10 / 直方图
            int n = mask.Length;
            for (int i = 0; i < n; i++)
            {
                if (mask[i] < 0.5f) continue;
                float de = DeltaE(refPx[i], testPx[i]);
                sum += de;
                count++;
                int bin = math.clamp((int)(de * 102.4f), 0, 1023);
                hist[bin]++;
            }

            float mean = count > 0 ? (float)(sum / count) : 0f;
            // p95 from histogram / 直方图 p95
            long target = (long)(count * 0.95);
            long acc = 0;
            float p95 = 0;
            for (int b = 0; b < 1024; b++)
            {
                acc += hist[b];
                if (acc >= target) { p95 = (b + 1) / 102.4f; break; }
            }

            result[0] = new float2(mean, p95);
            hist.Dispose();
        }

        private float DeltaE(Color32 c1, Color32 c2)
        {
            var lab1 = ToLab(c1, refIsSrgb);
            var lab2 = ToLab(c2, testIsSrgb);
            return Ciede2000(lab1, lab2);
        }

        internal static float3 ToLab(Color32 c, bool srgb)
        {
            float4 f = new float4(c.r, c.g, c.b, c.a) / 255f;
            if (srgb) f = DownsampleJob.SrgbToLinear(f);
            // sRGB primaries -> XYZ (D65) / sRGB 到 XYZ
            float X = math.dot(f.xyz, new float3(0.4124564f, 0.3575761f, 0.1804375f));
            float Y = math.dot(f.xyz, new float3(0.2126729f, 0.7151522f, 0.0721750f));
            float Z = math.dot(f.xyz, new float3(0.0193339f, 0.1191920f, 0.9503041f));
            const float wx = 0.95047f, wy = 1f, wz = 1.08883f;
            float fx = LabF(X / wx), fy = LabF(Y / wy), fz = LabF(Z / wz);
            return new float3(116f * fy - 16f, 500f * (fx - fy), 200f * (fy - fz));
        }

        private static float LabF(float t) =>
            t > 0.008856f ? math.pow(t, 1f / 3f) : 7.787f * t + 16f / 116f;

        internal static float Ciede2000(float3 lab1, float3 lab2)
        {
            float L1 = lab1.x, a1 = lab1.y, b1 = lab1.z;
            float L2 = lab2.x, a2 = lab2.y, b2 = lab2.z;
            float C1 = math.sqrt(a1 * a1 + b1 * b1), C2 = math.sqrt(a2 * a2 + b2 * b2);
            float Cb = 0.5f * (C1 + C2);
            float Cb7 = math.pow(Cb, 7f);
            float G = 0.5f * (1f - math.sqrt(Cb7 / (Cb7 + 6103515625f))); // 25^7
            float ap1 = (1f + G) * a1, ap2 = (1f + G) * a2;
            float Cp1 = math.sqrt(ap1 * ap1 + b1 * b1), Cp2 = math.sqrt(ap2 * ap2 + b2 * b2);
            float hp1 = (ap1 == 0 && b1 == 0) ? 0 : math.degrees(math.atan2(b1, ap1));
            float hp2 = (ap2 == 0 && b2 == 0) ? 0 : math.degrees(math.atan2(b2, ap2));
            if (hp1 < 0) hp1 += 360f;
            if (hp2 < 0) hp2 += 360f;

            float dLp = L2 - L1;
            float dCp = Cp2 - Cp1;
            float dhp;
            if (Cp1 * Cp2 == 0) dhp = 0;
            else
            {
                dhp = hp2 - hp1;
                if (dhp > 180f) dhp -= 360f;
                else if (dhp < -180f) dhp += 360f;
            }

            float dHp = 2f * math.sqrt(Cp1 * Cp2) * math.sin(math.radians(dhp) * 0.5f);
            float Lbp = 0.5f * (L1 + L2);
            float Cbp = 0.5f * (Cp1 + Cp2);
            float hbp;
            if (Cp1 * Cp2 == 0) hbp = hp1 + hp2;
            else
            {
                float diff = math.abs(hp1 - hp2);
                if (diff <= 180f) hbp = 0.5f * (hp1 + hp2);
                else if (hp1 + hp2 < 360f) hbp = 0.5f * (hp1 + hp2 + 360f);
                else hbp = 0.5f * (hp1 + hp2 - 360f);
            }

            float T = 1f - 0.17f * math.cos(math.radians(hbp - 30f))
                      + 0.24f * math.cos(math.radians(2f * hbp))
                      + 0.32f * math.cos(math.radians(3f * hbp + 6f))
                      - 0.20f * math.cos(math.radians(4f * hbp - 63f));
            float dTheta = 30f * math.exp(-math.pow((hbp - 275f) / 25f, 2f));
            float Cbp7 = math.pow(Cbp, 7f);
            float Rc = 2f * math.sqrt(Cbp7 / (Cbp7 + 6103515625f));
            float Lm50sq = (Lbp - 50f) * (Lbp - 50f);
            float Sl = 1f + 0.015f * Lm50sq / math.sqrt(20f + Lm50sq);
            float Sc = 1f + 0.045f * Cbp;
            float Sh = 1f + 0.015f * Cbp * T;
            float Rt = -math.sin(math.radians(2f * dTheta)) * Rc;

            float tL = dLp / Sl, tC = dCp / Sc, tH = dHp / Sh;
            return math.sqrt(tL * tL + tC * tC + tH * tH + Rt * tC * tH);
        }
    }

    /// <summary>Alpha metrics: cutout IoU at cutoff & blend linear RMSE.
    /// Alpha 指标：Cutout 剪影 IoU 与 Blend 线性 RMSE。</summary>
    [BurstCompile]
    internal struct AlphaMetricsJob : IJob
    {
        [ReadOnly] internal NativeArray<Color32> refPx;
        [ReadOnly] internal NativeArray<Color32> testPx;
        [ReadOnly] internal NativeArray<float> mask;
        internal float cutoff;          // for IoU / IoU 阈值
        [WriteOnly] internal NativeArray<float2> result; // (iou, rmse)

        internal void Execute()
        {
            long inter = 0, union = 0;
            double sq = 0;
            long count = 0;
            for (int i = 0; i < mask.Length; i++)
            {
                if (mask[i] < 0.5f) continue;
                float a0 = refPx[i].a / 255f, a1 = testPx[i].a / 255f;
                bool b0 = a0 >= cutoff, b1 = a1 >= cutoff;
                if (b0 && b1) inter++;
                if (b0 || b1) union++;
                float d = a1 - a0;
                sq += d * d;
                count++;
            }

            float iou = union > 0 ? (float)inter / union : 1f;
            float rmse = count > 0 ? (float)math.sqrt(sq / count) : 0f;
            result[0] = new float2(iou, rmse);
        }
    }

    /// <summary>Normal map angle error (decode per layout, renormalize, deg mean/p95).
    /// 法线角度误差（按布局解码、重归一化、均值/p95）。</summary>
    [BurstCompile]
    internal struct NormalAngleJob : IJob
    {
        [ReadOnly] internal NativeArray<Color32> refPx;
        [ReadOnly] internal NativeArray<Color32> testPx;
        [ReadOnly] internal NativeArray<float> mask;
        internal int refLayout, testLayout; // 0 RG, 1 AG, 2 RGB / 布局
        [WriteOnly] internal NativeArray<float2> result;

        internal void Execute()
        {
            double sum = 0;
            long count = 0;
            var hist = new NativeArray<int>(900, Allocator.Temp); // 0..90deg / 直方图
            for (int i = 0; i < mask.Length; i++)
            {
                if (mask[i] < 0.5f) continue;
                var n0 = Decode(refPx[i], refLayout);
                var n1 = Decode(testPx[i], testLayout);
                float d = math.acos(math.clamp(math.dot(n0, n1), -1f, 1f)) * 57.29578f; // rad->deg / 弧度转角度
                sum += d;
                count++;
                hist[math.clamp((int)(d * 10f), 0, 899)]++;
            }

            float mean = count > 0 ? (float)(sum / count) : 0f;
            long target = (long)(count * 0.95), acc = 0;
            float p95 = 0;
            for (int b = 0; b < 900; b++)
            {
                acc += hist[b];
                if (acc >= target) { p95 = (b + 1) / 10f; break; }
            }

            result[0] = new float2(mean, p95);
            hist.Dispose();
        }

        internal static float3 Decode(Color32 c, int layout)
        {
            float x, y;
            switch (layout)
            {
                case 0: x = c.r / 255f; y = c.g / 255f; break;
                case 1: x = c.a / 255f; y = c.g / 255f; break;
                default: x = c.r / 255f; y = c.g / 255f; break;
            }

            float2 xy = new float2(x, y) * 2f - 1f;
            float z = math.sqrt(math.max(0f, 1f - math.dot(xy, xy)));
            return math.normalizesafe(new float3(xy.x, xy.y, z), new float3(0, 0, 1));
        }
    }

    /// <summary>Per-channel linear RMSE over used channels; result.x = worst.
    /// 使用通道线性 RMSE；result.x 为最差。</summary>
    [BurstCompile]
    internal struct GrayRmseJob : IJob
    {
        [ReadOnly] internal NativeArray<Color32> refPx;
        [ReadOnly] internal NativeArray<Color32> testPx;
        [ReadOnly] internal NativeArray<float> mask;
        internal bool4 usedChannels;
        [WriteOnly] internal NativeArray<float> result;

        internal void Execute()
        {
            double4 sq = 0;
            long count = 0;
            for (int i = 0; i < mask.Length; i++)
            {
                if (mask[i] < 0.5f) continue;
                var d0 = (testPx[i].r - refPx[i].r) / 255f;
                var d1 = (testPx[i].g - refPx[i].g) / 255f;
                var d2 = (testPx[i].b - refPx[i].b) / 255f;
                var d3 = (testPx[i].a - refPx[i].a) / 255f;
                sq += new double4(d0 * d0, d1 * d1, d2 * d2, d3 * d3);
                count++;
            }

            if (count == 0)
            {
                result[0] = 0f;
                return;
            }

            float4 rmse = math.sqrt((float4)(sq / count));
            float worst = math.max(math.max(rmse.x, rmse.y), math.max(rmse.z, rmse.w));
            // only used channels count / 仅统计使用的通道
            float used = 0f;
            if (usedChannels.x) used = math.max(used, rmse.x);
            if (usedChannels.y) used = math.max(used, rmse.y);
            if (usedChannels.z) used = math.max(used, rmse.z);
            if (usedChannels.w) used = math.max(used, rmse.w);
            result[0] = usedChannels.x || usedChannels.y || usedChannels.z || usedChannels.w ? used : worst;
        }
    }
}
