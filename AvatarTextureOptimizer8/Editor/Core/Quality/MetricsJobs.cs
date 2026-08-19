// MetricsJobs.cs
// Burst jobs for the perceptual quality pipeline.
// 感知质量管线 Burst 作业。
//  - AreaDownsampleJob: linear-space, premultiplied-alpha area-average downsample / 线性空间预乘alpha面积下采样
//  - BilinearUpsampleJob: GPU-like bilinear upsample / 与GPU一致的双线性上采样
//  - MsSsimJob: mask-aware SSIM / MS-SSIM (Wang 2003) / 带掩码的 SSIM/MS-SSIM
//  - DeltaE2000Job: mean CIEDE2000 / 平均 CIEDE2000
//  - AlphaMetricsJob: cutout IoU & blend RMSE / Cutout IoU 与 Blend RMSE
//  - NormalAngleJob: angular error mean & p95 / 法线角度误差(均值与p95)
//  - GrayRmseJob: per-used-channel linear RMSE, worst channel / 被使用通道线性RMSE取最差
//  - PureColorJob: pure-color island detection / 纯色岛检测
// References / 参考文献: Wang+ 2003/2004 (SSIM/MS-SSIM); Sharma/Wu/Dalal 2005 (CIEDE2000).
// Copyright (c) 2026 fosa. Licensed under the MIT License.

using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace net.fosa.ato
{
    // ================================================================== //
    // Resampling / 重采样
    // ================================================================== //

    /// <summary>Area-average downsample in LINEAR space with premultiplied alpha + coverage weights. / 线性空间+预乘alpha+覆盖加权的面积下采样。</summary>
    [BurstCompile]
    internal struct AreaDownsampleJob : IJob
    {
        [ReadOnly] public NativeArray<Color32> Source; // full source pixels / 源全图像素
        public int SrcW, SrcH;
        [ReadOnly] public NativeArray<byte> Coverage; // bbox coverage / bbox 覆盖
        public int CovW, CovH;
        public float4 Bbox; // x,y,w,h in source pixels / 源像素坐标 bbox
        public float ScaleX, ScaleY;
        public bool ToLinear;
        public int DstW, DstH;
        /// <summary>Target stride/offsets for writing into an atlas sub-rect. / 写入图集子矩形的跨距与偏移。</summary>
        public int DstStride;      // 0 = contiguous / 0=连续
        public int DstOffsetX, DstOffsetY;
        public bool Rotate;        // write transposed / 转置写入
        [WriteOnly] public NativeArray<Color32> Target;

        public void Execute()
        {
            float invSx = ScaleX > 1e-6f ? 1f / ScaleX : 1f;
            float invSy = ScaleY > 1e-6f ? 1f / ScaleY : 1f;

            for (int dy = 0; dy < DstH; dy++)
            {
                float sy0 = Bbox.y + dy * invSy, sy1 = Bbox.y + (dy + 1) * invSy;
                for (int dx = 0; dx < DstW; dx++)
                {
                    float sx0 = Bbox.x + dx * invSx, sx1 = Bbox.x + (dx + 1) * invSx;
                    int x0 = (int)math.floor(sx0), x1 = (int)math.ceil(sx1);
                    int y0 = (int)math.floor(sy0), y1 = (int)math.ceil(sy1);

                    float4 sum = 0; float wsum = 0;
                    for (int y = y0; y < y1; y++)
                    {
                        if (y < 0 || y >= SrcH) continue;
                        float fy = SpanWeight(y, sy0, sy1);
                        if (fy <= 0) continue;
                        for (int x = x0; x < x1; x++)
                        {
                            if (x < 0 || x >= SrcW) continue;
                            float fx = SpanWeight(x, sx0, sx1);
                            if (fx <= 0) continue;
                            float wgt = fx * fy;
                            var c = Source[y * SrcW + x];
                            float4 p = new float4(c.r, c.g, c.b, c.a) / 255f;
                            if (ToLinear) p.xyz = ColorSpace.SrgbToLinear(p.xyz);
                            float4 pm = new float4(p.xyz * p.w, p.w);
                            sum += pm * wgt;
                            wsum += wgt;
                        }
                    }

                    Color32 outC;
                    if (wsum <= 0) outC = new Color32(0, 0, 0, 0);
                    else
                    {
                        float4 pm = sum / wsum;
                        float a = pm.w;
                        float3 rgb = a > 1e-5f ? pm.xyz / a : 0;
                        if (ToLinear) rgb = ColorSpace.LinearToSrgb(rgb);
                        outC = new Color32(
                            (byte)math.clamp(rgb.x * 255f + 0.5f, 0, 255),
                            (byte)math.clamp(rgb.y * 255f + 0.5f, 0, 255),
                            (byte)math.clamp(rgb.z * 255f + 0.5f, 0, 255),
                            (byte)math.clamp(a * 255f + 0.5f, 0, 255));
                    }
                    if (Rotate)
                    {
                        int tx = DstOffsetX + dy, ty = DstOffsetY + dx;
                        int stride = DstStride > 0 ? DstStride : DstH; // rotated: width becomes DstH / 转置后宽为 DstH
                        Target[ty * stride + tx] = outC;
                    }
                    else if (DstStride > 0)
                    {
                        Target[(DstOffsetY + dy) * DstStride + DstOffsetX + dx] = outC;
                    }
                    else
                    {
                        Target[dy * DstW + dx] = outC;
                    }
                }
            }
        }

        private static float SpanWeight(int i, float lo, float hi)
        {
            float a = math.max(i, lo), b = math.min(i + 1, hi);
            return math.max(0, b - a);
        }
    }

    /// <summary>Bilinear upsample, straight alpha, matching GPU sampling. / 双线性上采样,非预乘,与 GPU 采样一致。</summary>
    [BurstCompile]
    internal struct BilinearUpsampleJob : IJob
    {
        [ReadOnly] public NativeArray<Color32> Small;
        public int SmallW, SmallH;
        public int DstW, DstH;
        [WriteOnly] public NativeArray<Color32> Dst;

        public void Execute()
        {
            for (int y = 0; y < DstH; y++)
            {
                float gy = (y + 0.5f) * SmallH / DstH - 0.5f;
                int y0 = (int)math.floor(gy);
                int y1 = math.min(y0 + 1, SmallH - 1);
                y0 = math.max(0, y0);
                float ty = math.saturate(gy - y0);
                for (int x = 0; x < DstW; x++)
                {
                    float gx = (x + 0.5f) * SmallW / DstW - 0.5f;
                    int x0 = (int)math.floor(gx);
                    int x1 = math.min(x0 + 1, SmallW - 1);
                    x0 = math.max(0, x0);
                    float tx = math.saturate(gx - x0);
                    var p0 = Lerp(Small[y0 * SmallW + x0], Small[y0 * SmallW + x1], tx);
                    var p1 = Lerp(Small[y1 * SmallW + x0], Small[y1 * SmallW + x1], tx);
                    Dst[y * DstW + x] = Lerp(p0, p1, ty);
                }
            }
        }

        private static Color32 Lerp(Color32 a, Color32 b, float t)
        {
            return new Color32(
                (byte)(a.r + (b.r - a.r) * t + 0.5f),
                (byte)(a.g + (b.g - a.g) * t + 0.5f),
                (byte)(a.b + (b.b - a.b) * t + 0.5f),
                (byte)(a.a + (b.a - a.a) * t + 0.5f));
        }
    }

    /// <summary>sRGB transfer helpers shared by jobs. / 作业共享的 sRGB 转换。</summary>
    internal static class ColorSpace
    {
        internal static float3 SrgbToLinear(float3 c)
        {
            return math.select(math.pow((c + 0.055f) / 1.055f, 2.4f), c / 12.92f, c <= 0.04045f);
        }

        internal static float3 LinearToSrgb(float3 c)
        {
            c = math.max(c, 0);
            return math.select(math.pow(c, 1f / 2.4f) * 1.055f - 0.055f, c * 12.92f, c <= 0.0031308f);
        }
    }

    // ================================================================== //
    // SSIM family / SSIM 系列
    // ================================================================== //

    /// <summary>
    /// Mask-aware SSIM/MS-SSIM. Luma in gamma domain; 11×11 Gaussian σ1.5 separable;
    /// MS-SSIM 5 scales (short edge ≥176), else single scale; short edge &lt;11 → ignored
    /// (Result=+∞). / 带掩码 SSIM/MS-SSIM:luma取gamma域;短边≥176用5尺度,否则单尺度;
    /// 短边<11忽略(Result=+∞)。
    /// </summary>
    [BurstCompile]
    internal struct MsSsimJob : IJob
    {
        [ReadOnly] public NativeArray<Color32> A;
        [ReadOnly] public NativeArray<Color32> B;
        [ReadOnly] public NativeArray<byte> Mask; // W*H coverage / 覆盖
        [ReadOnly] public NativeArray<float> Kernel; // 11 gaussian weights / 高斯核
        public int W, H;
        [WriteOnly] public NativeArray<float> Result;

        public void Execute()
        {
            int shortEdge = math.min(W, H);
            if (shortEdge < 11) { Result[0] = float.MaxValue; return; }

            int n = W * H;
            var la = new NativeArray<float>(n, Allocator.Temp);
            var lb = new NativeArray<float>(n, Allocator.Temp);
            try
            {
                for (int i = 0; i < n; i++)
                {
                    var a = A[i]; var b = B[i];
                    la[i] = 0.2126f * a.r + 0.7152f * a.g + 0.0722f * a.b;
                    lb[i] = 0.2126f * b.r + 0.7152f * b.g + 0.0722f * b.b;
                }

                bool multiScale = shortEdge >= 176;
                if (!multiScale)
                {
                    Result[0] = SsimPlane(la, lb, Mask, W, H);
                    return;
                }

                // MS-SSIM: 5 scales (Wang 2003 weights inlined) / 5 尺度(权重内联)
                int cw = W, ch = H;
                float total = 1f;
                int scalesUsed = 0;
                var curA = new NativeArray<float>(n, Allocator.Temp);
                var curB = new NativeArray<float>(n, Allocator.Temp);
                var curM = new NativeArray<byte>(n, Allocator.Temp);
                try
                {
                    curA.CopyFrom(la); curB.CopyFrom(lb); curM.CopyFrom(Mask);
                    for (int s = 0; s < 5; s++)
                    {
                        if (math.min(cw, ch) < 11) break;
                        float ssim = SsimPlane(curA, curB, curM, cw, ch);
                        if (ssim < -9000f) break;
                        float w = s == 0 ? 0.0448f : s == 1 ? 0.2856f : s == 2 ? 0.3001f : s == 3 ? 0.2363f : 0.1333f;
                        total *= math.pow(math.max(ssim, 1e-6f), w);
                        scalesUsed++;

                        int nw = cw / 2, nh = ch / 2;
                        if (nw < 1 || nh < 1 || s == 4) break;
                        Downsample2(curA, curA, cw, ch, nw, nh);
                        Downsample2(curB, curB, cw, ch, nw, nh);
                        DownsampleMask(curM, curM, cw, ch, nw, nh);
                        cw = nw; ch = nh;
                    }
                    if (scalesUsed == 0) { Result[0] = float.MaxValue; return; }
                }
                finally
                {
                    curA.Dispose(); curB.Dispose(); curM.Dispose();
                }
                Result[0] = total;
            }
            finally
            {
                la.Dispose(); lb.Dispose();
            }
        }

        private static void Downsample2(NativeArray<float> src, NativeArray<float> dst, int w, int h, int nw, int nh)
        {
            // box 2× downsample into temp then copy back if in-place / 就地降采样经暂存中转
            var tmp = new NativeArray<float>(nw * nh, Allocator.Temp);
            for (int y = 0; y < nh; y++)
            for (int x = 0; x < nw; x++)
            {
                tmp[y * nw + x] = 0.25f * (src[(2 * y) * w + 2 * x] + src[(2 * y) * w + 2 * x + 1] +
                                           src[(2 * y + 1) * w + 2 * x] + src[(2 * y + 1) * w + 2 * x + 1]);
            }
            for (int i = 0; i < nw * nh; i++) dst[i] = tmp[i];
            tmp.Dispose();
        }

        private static void DownsampleMask(NativeArray<byte> src, NativeArray<byte> dst, int w, int h, int nw, int nh)
        {
            var tmp = new NativeArray<byte>(nw * nh, Allocator.Temp);
            for (int y = 0; y < nh; y++)
            for (int x = 0; x < nw; x++)
            {
                byte v = (byte)(src[(2 * y) * w + 2 * x] != 0 || src[(2 * y) * w + 2 * x + 1] != 0 ||
                                src[(2 * y + 1) * w + 2 * x] != 0 || src[(2 * y + 1) * w + 2 * x + 1] != 0 ? 1 : 0);
                tmp[y * nw + x] = v;
            }
            for (int i = 0; i < nw * nh; i++) dst[i] = tmp[i];
            tmp.Dispose();
        }

        /// <summary>Masked SSIM over a plane with separable Gaussian moment filtering. / 可分离高斯矩滤波的带掩码 SSIM。</summary>
        private float SsimPlane(NativeArray<float> fa, NativeArray<float> fb, NativeArray<byte> mask, int w, int h)
        {
            const float C1 = (0.01f * 255f) * (0.01f * 255f);
            const float C2 = (0.03f * 255f) * (0.03f * 255f);
            int n = w * h;

            var muA = new NativeArray<float>(n, Allocator.Temp);
            var muB = new NativeArray<float>(n, Allocator.Temp);
            var aa = new NativeArray<float>(n, Allocator.Temp);
            var bb = new NativeArray<float>(n, Allocator.Temp);
            var ab = new NativeArray<float>(n, Allocator.Temp);
            var t1 = new NativeArray<float>(n, Allocator.Temp);
            var t2 = new NativeArray<float>(n, Allocator.Temp);
            try
            {
                SepFir(fa, muA, t1, w, h, Kernel);
                SepFir(fb, muB, t2, w, h, Kernel);
                for (int i = 0; i < n; i++) aa[i] = fa[i] * fa[i];
                for (int i = 0; i < n; i++) bb[i] = fb[i] * fb[i];
                for (int i = 0; i < n; i++) ab[i] = fa[i] * fb[i];
                SepFir(aa, aa, t1, w, h, Kernel);
                SepFir(bb, bb, t2, w, h, Kernel);
                SepFir(ab, ab, t1, w, h, Kernel);

                double sum = 0; long cnt = 0;
                for (int i = 0; i < n; i++)
                {
                    if (mask[i] == 0) continue;
                    float ma = muA[i], mb = muB[i];
                    float saa = math.max(aa[i] - ma * ma, 0);
                    float sbb = math.max(bb[i] - mb * mb, 0);
                    float sab = ab[i] - ma * mb;
                    float v = ((2 * ma * mb + C1) * (2 * sab + C2)) / ((ma * ma + mb * mb + C1) * (saa + sbb + C2));
                    sum += v; cnt++;
                }
                if (cnt == 0) return float.MaxValue;
                return (float)(sum / cnt);
            }
            finally
            {
                muA.Dispose(); muB.Dispose(); aa.Dispose(); bb.Dispose(); ab.Dispose(); t1.Dispose(); t2.Dispose();
            }
        }

        private static void SepFir(NativeArray<float> src, NativeArray<float> dst, NativeArray<float> tmp, int w, int h, NativeArray<float> k)
        {
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float acc = 0;
                for (int j = 0; j < 11; j++)
                {
                    int xx = math.clamp(x + j - 5, 0, w - 1);
                    acc += k[j] * src[y * w + xx];
                }
                tmp[y * w + x] = acc;
            }
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float acc = 0;
                for (int j = 0; j < 11; j++)
                {
                    int yy = math.clamp(y + j - 5, 0, h - 1);
                    acc += k[j] * tmp[yy * w + x];
                }
                dst[y * w + x] = acc;
            }
        }
    }

    // ================================================================== //
    // CIEDE2000 / CIEDE2000 色差
    // ================================================================== //

    /// <summary>Mean CIEDE2000 over masked pixels (sRGB input). / 掩码内平均 CIEDE2000(sRGB 输入)。</summary>
    [BurstCompile]
    internal struct DeltaE2000Job : IJob
    {
        [ReadOnly] public NativeArray<Color32> A;
        [ReadOnly] public NativeArray<Color32> B;
        [ReadOnly] public NativeArray<byte> Mask;
        public int W, H;
        [WriteOnly] public NativeArray<float> Result;

        public void Execute()
        {
            double sum = 0; long cnt = 0;
            int n = W * H;
            for (int i = 0; i < n; i++)
            {
                if (Mask[i] == 0) continue;
                float e = PixelDeltaE(A[i], B[i]);
                sum += e; cnt++;
            }
            Result[0] = cnt == 0 ? float.MaxValue : (float)(sum / cnt);
        }

        internal static float PixelDeltaE(Color32 ca, Color32 cb)
        {
            float3 labA = SrgbToLab(new float3(ca.r, ca.g, ca.b) / 255f);
            float3 labB = SrgbToLab(new float3(cb.r, cb.g, cb.b) / 255f);
            return Ciede2000(labA, labB);
        }

        internal static float3 SrgbToLab(float3 srgb)
        {
            float3 lin = ColorSpace.SrgbToLinear(srgb);
            // sRGB D65 → XYZ / 转XYZ
            float X = math.dot(lin, new float3(0.4124564f, 0.3575761f, 0.1804375f));
            float Y = math.dot(lin, new float3(0.2126729f, 0.7151522f, 0.0721750f));
            float Z = math.dot(lin, new float3(0.0193339f, 0.1191920f, 0.9503041f));
            const float xn = 0.95047f, yn = 1.0f, zn = 1.08883f;
            float fx = LabF(X / xn), fy = LabF(Y / yn), fz = LabF(Z / zn);
            return new float3(116f * fy - 16f, 500f * (fx - fy), 200f * (fy - fz));
        }

        private static float LabF(float t)
        {
            const float d = 6f / 29f;
            return t > d * d * d ? math.pow(t, 1f / 3f) : t / (3 * d * d) + 4f / 29f;
        }

        /// <summary>Full CIEDE2000 (Sharma et al. 2005 formulation). / 完整 CIEDE2000 公式。</summary>
        internal static float Ciede2000(float3 lab1, float3 lab2)
        {
            float L1 = lab1.x, a1 = lab1.y, b1 = lab1.z;
            float L2 = lab2.x, a2 = lab2.y, b2 = lab2.z;
            float kL = 1f, kC = 1f, kH = 1f;

            float C1 = math.sqrt(a1 * a1 + b1 * b1);
            float C2 = math.sqrt(a2 * a2 + b2 * b2);
            float Cb = 0.5f * (C1 + C2);
            float Cb7 = math.pow(Cb, 7f);
            float G = 0.5f * (1f - math.sqrt(Cb7 / (Cb7 + 6103515625f))); // 25^7
            float ap1 = (1f + G) * a1, ap2 = (1f + G) * a2;
            float Cp1 = math.sqrt(ap1 * ap1 + b1 * b1);
            float Cp2 = math.sqrt(ap2 * ap2 + b2 * b2);
            float hp1 = Cp1 < 1e-8f ? 0f : math.degrees(math.atan2(b1, ap1));
            float hp2 = Cp2 < 1e-8f ? 0f : math.degrees(math.atan2(b2, ap2));
            if (hp1 < 0) hp1 += 360f;
            if (hp2 < 0) hp2 += 360f;

            float dLp = L2 - L1;
            float dCp = Cp2 - Cp1;
            float dhp;
            if (Cp1 * Cp2 < 1e-8f) dhp = 0f;
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
            if (Cp1 * Cp2 < 1e-8f) hbp = hp1 + hp2;
            else
            {
                float sum = hp1 + hp2;
                float diff = math.abs(hp1 - hp2);
                if (diff <= 180f) hbp = 0.5f * sum;
                else if (sum < 360f) hbp = 0.5f * (sum + 360f);
                else hbp = 0.5f * (sum - 360f);
            }

            float T = 1f - 0.17f * math.cos(math.radians(hbp - 30f))
                        + 0.24f * math.cos(math.radians(2f * hbp))
                        + 0.32f * math.cos(math.radians(3f * hbp + 6f))
                        - 0.20f * math.cos(math.radians(4f * hbp - 63f));
            float dTheta = 30f * math.exp(-math.pow((hbp - 275f) / 25f, 2f));
            float Cbp7 = math.pow(Cbp, 7f);
            float Rc = 2f * math.sqrt(Cbp7 / (Cbp7 + 6103515625f));
            float Lm = (Lbp - 50f) * (Lbp - 50f);
            float Sl = 1f + 0.015f * Lm / math.sqrt(20f + Lm);
            float Sc = 1f + 0.045f * Cbp;
            float Sh = 1f + 0.015f * Cbp * T;
            float Rt = -math.sin(math.radians(2f * dTheta)) * Rc;

            float tL = dLp / (kL * Sl);
            float tC = dCp / (kC * Sc);
            float tH = dHp / (kH * Sh);
            return math.sqrt(tL * tL + tC * tC + tH * tH + Rt * tC * tH);
        }
    }

    // ================================================================== //
    // Alpha metrics / alpha 指标
    // ================================================================== //

    /// <summary>
    /// Cutout coverage IoU (per cutoff) and Blend linear alpha RMSE, strictest over cutoffs.
    /// / Cutout 覆盖 IoU(逐阈值)与 Blend 线性 alpha RMSE,取最严。
    /// </summary>
    [BurstCompile]
    internal struct AlphaMetricsJob : IJob
    {
        [ReadOnly] public NativeArray<Color32> A;
        [ReadOnly] public NativeArray<Color32> B;
        [ReadOnly] public NativeArray<byte> Mask;
        public int W, H;
        [ReadOnly] public NativeArray<float> Cutoffs; // evaluated cutoffs / 全部阈值
        public bool EvaluateIoU, EvaluateRmse;
        [WriteOnly] public NativeArray<float> Result; // [0]=worst IoU [1]=worst RMSE

        public void Execute()
        {
            float worstIoU = float.MaxValue;
            if (EvaluateIoU)
            {
                for (int ci = 0; ci < Cutoffs.Length; ci++)
                {
                    float cut = Cutoffs[ci];
                    long inter = 0, union = 0;
                    int n = W * H;
                    for (int i = 0; i < n; i++)
                    {
                        if (Mask[i] == 0) continue;
                        bool xa = A[i].a / 255f >= cut, xb = B[i].a / 255f >= cut;
                        if (xa && xb) inter++;
                        if (xa || xb) union++;
                    }
                    float iou = union == 0 ? 1f : (float)inter / union;
                    if (iou < worstIoU) worstIoU = iou;
                }
            }

            float rmse = 0; long cnt = 0;
            if (EvaluateRmse)
            {
                double sum = 0;
                int n = W * H;
                for (int i = 0; i < n; i++)
                {
                    if (Mask[i] == 0) continue;
                    float d = A[i].a / 255f - B[i].a / 255f;
                    sum += d * d; cnt++;
                }
                rmse = cnt == 0 ? 0f : (float)math.sqrt(sum / cnt);
            }
            Result[0] = worstIoU;
            Result[1] = rmse;
        }
    }

    // ================================================================== //
    // Normal / 法线
    // ================================================================== //

    /// <summary>Angular error mean & p95 between decoded unit normals. / 解码单位法线角度误差均值与p95。</summary>
    [BurstCompile]
    internal struct NormalAngleJob : IJob
    {
        [ReadOnly] public NativeArray<float3> A; // unit vectors / 单位向量
        [ReadOnly] public NativeArray<float3> B;
        [ReadOnly] public NativeArray<byte> Mask;
        public int W, H;
        [WriteOnly] public NativeArray<float> Result; // [0]=mean [1]=p95

        public void Execute()
        {
            int n = W * H;
            int cnt = 0;
            var angles = new NativeList<float>(n <= 0 ? 4 : n, Allocator.Temp);
            try
            {
                double sum = 0;
                for (int i = 0; i < n; i++)
                {
                    if (Mask[i] == 0) continue;
                    float d = math.clamp(math.dot(A[i], B[i]), -1f, 1f);
                    float ang = math.degrees(math.acos(d));
                    angles.Add(ang);
                    sum += ang; cnt++;
                }
                if (cnt == 0) { Result[0] = float.MaxValue; Result[1] = float.MaxValue; return; }

                angles.AsArray().Sort();
                // nearest-rank p95 / 近似秩 p95
                int idx = math.clamp((int)math.ceil(0.95 * cnt) - 1, 0, cnt - 1);
                Result[0] = (float)(sum / cnt);
                Result[1] = angles[idx];
            }
            finally { angles.Dispose(); }
        }
    }

    // ================================================================== //
    // Grayscale / 灰度
    // ================================================================== //

    /// <summary>Linear-space RMSE per used channel; worst channel reported. / 被使用通道线性RMSE;报告最差通道。</summary>
    [BurstCompile]
    internal struct GrayRmseJob : IJob
    {
        [ReadOnly] public NativeArray<Color32> A;
        [ReadOnly] public NativeArray<Color32> B;
        [ReadOnly] public NativeArray<byte> Mask;
        public int W, H;
        public byte UsedChannels; // R=1 G=2 B=4 A=8
        public bool LinearSource; // source imported linear / 源为线性导入
        [WriteOnly] public NativeArray<float> Result; // [0]=worst RMSE

        public void Execute()
        {
            int n = W * H;
            float worst = 0;
            for (int ch = 0; ch < 4; ch++)
            {
                if ((UsedChannels & (1 << ch)) == 0) continue;
                double sum = 0; long cnt = 0;
                for (int i = 0; i < n; i++)
                {
                    if (Mask[i] == 0) continue;
                    float va = Channel(A[i], ch) / 255f;
                    float vb = Channel(B[i], ch) / 255f;
                    if (!LinearSource)
                    {
                        va = math.select(math.pow((va + 0.055f) / 1.055f, 2.4f), va / 12.92f, va <= 0.04045f);
                        vb = math.select(math.pow((vb + 0.055f) / 1.055f, 2.4f), vb / 12.92f, vb <= 0.04045f);
                    }
                    float d = va - vb;
                    sum += d * d; cnt++;
                }
                float rmse = cnt == 0 ? 0f : (float)math.sqrt(sum / cnt);
                if (rmse > worst) worst = rmse;
            }
            Result[0] = worst;
        }

        private static byte Channel(Color32 c, int ch)
        {
            if (ch == 0) return c.r;
            if (ch == 1) return c.g;
            if (ch == 2) return c.b;
            return c.a;
        }
    }

    // ================================================================== //
    // Normal decode/encode / 法线解码编码
    // ================================================================== //

    /// <summary>Decode stored normal texels to unit vectors (RGorAG). / 将存储的法线纹理解码为单位向量(RG或AG布局)。</summary>
    [BurstCompile]
    internal struct DecodeNormalsJob : IJob
    {
        [ReadOnly] public NativeArray<Color32> Source;
        public int Count;
        [WriteOnly] public NativeArray<float3> Normals;

        public void Execute()
        {
            for (int i = 0; i < Count; i++)
            {
                var c = Source[i];
                float x = c.r / 255f * (c.a / 255f); // RGorAG trick: r*a picks AG layout when a carries x / RG或AG兼容
                float y = c.g / 255f;
                float2 xy = new float2(x * 2f - 1f, y * 2f - 1f);
                xy = math.normalize(xy);
                float z = math.sqrt(math.saturate(1f - math.dot(xy, xy)));
                Normals[i] = math.normalize(new float3(xy.x, xy.y, z));
            }
        }
    }

    /// <summary>Encode unit vectors to plain RGB. / 将单位向量编码为普通 RGB。</summary>
    [BurstCompile]
    internal struct EncodeNormalsJob : IJob
    {
        [ReadOnly] public NativeArray<float3> Normals;
        public int Count;
        [WriteOnly] public NativeArray<Color32> Target;

        public void Execute()
        {
            for (int i = 0; i < Count; i++)
            {
                float3 n = math.normalizesafe(Normals[i], new float3(0, 0, 1));
                Target[i] = new Color32(
                    (byte)math.round((n.x * 0.5f + 0.5f) * 255f),
                    (byte)math.round((n.y * 0.5f + 0.5f) * 255f),
                    (byte)math.round((n.z * 0.5f + 0.5f) * 255f),
                    255);
            }
        }
    }

    /// <summary>Vector area-average downsample then renormalize. / 向量面积平均下采样后重归一化。</summary>
    [BurstCompile]
    internal struct VectorDownsampleJob : IJob
    {
        [ReadOnly] public NativeArray<float3> Source;
        public int SrcW, SrcH;
        [ReadOnly] public NativeArray<byte> Coverage;
        public int DstW, DstH;
        [WriteOnly] public NativeArray<float3> Target;

        public void Execute()
        {
            float fx = (float)SrcW / DstW, fy = (float)SrcH / DstH;
            for (int dy = 0; dy < DstH; dy++)
            {
                int y0 = (int)math.floor(dy * fy), y1 = math.max(y0 + 1, (int)math.ceil((dy + 1) * fy));
                for (int dx = 0; dx < DstW; dx++)
                {
                    int x0 = (int)math.floor(dx * fx), x1 = math.max(x0 + 1, (int)math.ceil((dx + 1) * fx));
                    float3 sum = 0; int cnt = 0;
                    for (int y = y0; y < y1 && y < SrcH; y++)
                    for (int x = x0; x < x1 && x < SrcW; x++)
                    {
                        if (Coverage[y * SrcW + x] == 0) continue;
                        sum += Source[y * SrcW + x];
                        cnt++;
                    }
                    Target[dy * DstW + dx] = cnt == 0 ? new float3(0, 0, 1) : math.normalizesafe(sum / cnt, new float3(0, 0, 1));
                }
            }
        }
    }

    /// <summary>Vector bilinear upsample then renormalize. / 向量双线性上采样后重归一化。</summary>
    [BurstCompile]
    internal struct VectorUpsampleJob : IJob
    {
        [ReadOnly] public NativeArray<float3> Small;
        public int SmallW, SmallH;
        public int DstW, DstH;
        [WriteOnly] public NativeArray<float3> Dst;

        public void Execute()
        {
            for (int y = 0; y < DstH; y++)
            {
                float gy = (y + 0.5f) * SmallH / DstH - 0.5f;
                int y0 = math.max(0, (int)math.floor(gy));
                int y1 = math.min(y0 + 1, SmallH - 1);
                float ty = math.saturate(gy - y0);
                for (int x = 0; x < DstW; x++)
                {
                    float gx = (x + 0.5f) * SmallW / DstW - 0.5f;
                    int x0 = math.max(0, (int)math.floor(gx));
                    int x1 = math.min(x0 + 1, SmallW - 1);
                    float tx = math.saturate(gx - x0);
                    float3 p0 = math.lerp(Small[y0 * SmallW + x0], Small[y0 * SmallW + x1], tx);
                    float3 p1 = math.lerp(Small[y1 * SmallW + x0], Small[y1 * SmallW + x1], tx);
                    Dst[y * DstW + x] = math.normalizesafe(math.lerp(p0, p1, ty), new float3(0, 0, 1));
                }
            }
        }
    }

    // ================================================================== //
    // Pure color / 纯色
    // ================================================================== //

    /// <summary>Detects islands where every covered pixel is (near-)identical. / 检测覆盖像素几乎一致的纯色岛。</summary>
    [BurstCompile]
    internal struct PureColorJob : IJob
    {
        [ReadOnly] public NativeArray<Color32> Source;
        [ReadOnly] public NativeArray<byte> Mask;
        public int W, H;
        [WriteOnly] public NativeArray<int> Result; // [0]=1 pure,[0]=0 not;[1..4] packed color / 结果

        public void Execute()
        {
            int n = W * H;
            Color32 first = new Color32(0, 0, 0, 0);
            bool has = false;
            for (int i = 0; i < n; i++)
            {
                if (Mask[i] == 0) continue;
                if (!has) { first = Source[i]; has = true; continue; }
                var c = Source[i];
                if (math.abs(c.r - first.r) > 2 || math.abs(c.g - first.g) > 2 ||
                    math.abs(c.b - first.b) > 2 || math.abs(c.a - first.a) > 2)
                {
                    Result[0] = 0;
                    Result[1] = 0; Result[2] = 0; Result[3] = 0; Result[4] = 0;
                    return;
                }
            }
            Result[0] = 1;
            Result[1] = first.r; Result[2] = first.g; Result[3] = first.b; Result[4] = first.a;
        }
    }
}
